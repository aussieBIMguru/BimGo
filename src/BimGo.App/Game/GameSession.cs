using System.Diagnostics;
using System.Numerics;
using BimGo.Audio;
using BimGo.Edits;
using BimGo.Sources;
using BimGo.Game.Guns;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Vk = BimGo.Native.Win32;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// One walkthrough session: owns the game-thread systems and runs the loop
    /// (variable-rate rendering, fixed 120 Hz physics). Never touches the Revit API.
    ///
    /// Split across partial files: this file (setup, loop, update), GameSession.Render.cs (3D passes, HUD, minimap),
    /// GameSession.Menu.cs (pause menu, comment editor), GameSession.Edits.cs (runtime element edits, the model
    /// source pump and the room readout) and GameSession.Document.cs (journal replay, undo, save, dirty state).
    ///
    /// The same session runs inside Revit (edits go to Revit) and in the standalone app on a .bimgo file
    /// (edits go to the file's journal); the difference is the <see cref="IModelSource"/>.
    /// </summary>
    internal sealed partial class GameSession : IDisposable
    {
        #region Constants

        private const float TICK = 1f / 120f;
        private const float PICK_DISTANCE = 250f;

        /// <summary>How far the default ground plane sits below the lowest level (clear of slab faces at that level).</summary>
        private const float GROUND_BELOW_LOWEST_LEVEL = 0.1f;

        #endregion

        #region Systems

        private readonly GameWindow _window;
        private SceneBatches _batches;
        private SceneRenderer _renderer;
        private readonly RenderTarget _target = new();
        private readonly Overlay3D _overlay = new();
        private UiBatch _ui;
        private Bvh _bvh;
        private Player _player;
        private Gun[] _guns;
        private PortalGun _portalGun;
        private CommentGun _commentGun;
        private readonly SessionOptions _options;

        /// <summary>Where the model lives and where edits go.</summary>
        public IModelSource Source { get; }

        /// <summary>The snapshot.</summary>
        public SceneData Scene { get; }

        /// <summary>The camera.</summary>
        public FpsCamera Camera { get; } = new();

        /// <summary>Sound effects.</summary>
        public SoundSystem Sound { get; } = new();

        /// <summary>Persisted comments.</summary>
        public CommentStore Comments { get; private set; }

        /// <summary>Shared scratch text buffer for HUD formatting.</summary>
        public TextBuffer Text { get; } = new();

        /// <summary>UI scale (DPI / 96).</summary>
        public float UiScale { get; private set; } = 1f;

        /// <summary>Back-buffer width.</summary>
        public int ScreenWidth => _window.Width;

        /// <summary>Back-buffer height.</summary>
        public int ScreenHeight => _window.Height;

        #endregion

        #region Runtime settings and state

        private readonly bool[] _categoryVisible;
        private readonly bool[] _linkVisible;
        private readonly bool[] _groupVisible;
        private readonly string[] _levelNamesUpper;
        private readonly bool[] _pickMask;
        private readonly bool[] _collisionMask;
        private int _doorCategory = -1;

        private bool _whitecard;
        private int _msaa;
        private bool _ambientOcclusion;

        // Realistic colour mode (render colours and textures; falls back to material colours when the snapshot has
        // no materials, but the choice is kept) and reflections (glass, mirrors, shiny surfaces, water)
        private bool _realistic;
        private bool _reflections;

        // Reflections: lowest tier that reflects (25 or 50 %), strength multiplier, and the tier debug colours (not saved)
        private int _reflectThreshold = 50;
        private float _reflectStrength = 1f;

        // Reflection probes (else the sky), probe resolution (256 = HQ), and the debug colours (0 off, 1 tiers,
        // 2 probe cells; not saved)
        private bool _reflectProbes = true;
        private bool _probeHigh;
        private int _reflectDebug;

        // Realistic mode: how Revit's tint is drawn, and CC0 proxies for missing images (files made before proxies)
        private TintMode _tintMode;
        private bool _proxyMissing;
        private bool _proxyMaterialColour;
        private float _fov;
        private float _sensitivity;
        private bool _invertY;
        private bool _vsync;
        private bool _showFps;
        private bool _showHelp = true;
        private bool _showMap = true;
        private float _groundZ;
        private float _groundDefault;

        private bool _paused;
        private int _activeGun;
        private AimInfo _aim;
        private bool _endRequested;
        private bool _startedAtSavedHome;
        private float _homeSetUntil = -1f;
        private SessionEndReason _endReason = SessionEndReason.EndedByUser;

        // Timing
        private float _frameTime;
        private float _fpsAccumulator;
        private int _fpsFrames;
        private float _fps, _frameMs;

        // Feedback
        private string _toast;
        private float _toastUntil;
        private bool _toastImportant;
        private float _clock;

        // Hide-UI mode (U): HUD, minimap, crosshair, markers and ordinary toasts hidden; every control still works.
        // Esc or U shows the UI again. Not saved: each session starts with the UI shown.
        private bool _uiHidden;
        private uint _flashColour;
        private float _flashUntil, _flashLength;

        #endregion

        /// <summary>
        /// Creates the session (window and context already exist).
        /// </summary>
        public GameSession(GameWindow window, SceneData scene, SessionOptions options)
        {
            _window = window;
            Scene = scene;
            _options = options ?? throw new ArgumentNullException(nameof(options));
            Source = options.Source ?? throw new ArgumentException("A model source is required.", nameof(options));
            _live = Source as BimGo.Live.ILiveLink;
            _journal = options.Document?.Journal ?? new EditJournal();
            DocumentPath = options.Document?.Path;

            int categories = CategoryCatalog.All.Count;
            _categoryVisible = new bool[categories];
            for (int i = 0; i < categories; i++) { _categoryVisible[i] = scene.CategoryLoaded[i]; }
            _linkVisible = new bool[scene.Links.Length + 1];
            Array.Fill(_linkVisible, true);
            _groupVisible = new bool[SceneBatches.GroupCount(scene)];
            UpdateGroupVisibility();
            _pickMask = new bool[scene.Elements.Length];
            _collisionMask = new bool[scene.Elements.Length];
            _hidden = new bool[scene.Elements.Length];
            _userHidden = new bool[scene.Elements.Length];
            _elementIndexById = new Dictionary<long, int>(scene.Elements.Length);
            for (int e = 0; e < scene.Elements.Length; e++)
            {
                // Only host elements are looked up by id: linked models have their own id namespaces and are read-only
                ElementRecord record = scene.Elements[e];

                // Family library templates are hidden for good (never drawn, picked or collided as themselves; the
                // Place gun clones them) and have no Revit identity to look up
                if (record.IsLibraryTemplate)
                {
                    _hidden[e] = true;
                    continue;
                }
                if (record.IsLinked) { continue; }
                _elementIndexById.TryAdd(record.ElementId, e);
                if (!string.IsNullOrEmpty(record.UniqueId)) { _elementIndexByUniqueId.TryAdd(record.UniqueId, e); }
                if (record.HostId > 0)
                {
                    if (!_hostedBy.TryGetValue(record.HostId, out List<long> hosted)) { _hostedBy[record.HostId] = hosted = new List<long>(); }
                    hosted.Add(record.ElementId);
                }
            }
            _doorCategory = CategoryCatalog.Find(CategoryCatalog.KEY_DOORS)?.Index ?? -1;
            _levelNamesUpper = scene.Levels.Select(l => l.Name.ToUpperInvariant()).ToArray();

            LaunchSettings settings = scene.Settings;
            _whitecard = settings.Colour == ColourMode.Whitecard;
            _realistic = settings.Colour == ColourMode.Realistic;
            _reflections = settings.Reflections;
            _reflectThreshold = settings.ReflectionThreshold <= 37 ? 25 : 50;
            _reflectStrength = float.IsFinite(settings.ReflectionStrength) ? Math.Clamp(settings.ReflectionStrength, 0.5f, 2f) : 1f;
            _reflectProbes = settings.ReflectionProbes;
            _probeHigh = settings.ProbeResolution >= 192;
            _tintMode = settings.RevitTint == TintMode.Off ? TintMode.Off : TintMode.Multiply;
            _proxyMissing = settings.ProxyMissingTextures;
            _proxyMaterialColour = settings.ProxyMaterialColour;
            _msaa = settings.Msaa;
            _ambientOcclusion = settings.AmbientOcclusion;
            _fov = settings.FieldOfView;
            _sensitivity = settings.MouseSensitivity;
            _invertY = settings.InvertY;
            _vsync = settings.VSync;
            _showFps = settings.ShowFps;
            GizmoSnap = settings.GizmoSnap;
            SnapMoveMm = LaunchSettings.NearestStep(LaunchSettings.SNAP_MOVE_STEPS_MM, settings.SnapMoveMm);
            SnapAngleDeg = LaunchSettings.NearestStep(LaunchSettings.SNAP_ANGLE_STEPS_DEG, settings.SnapAngleDeg);
            InitialiseCoordinates(settings.CoordinateReadout);
            _bcfCoordinates = settings.BcfCoordinates;
            if (LaunchSettings.TryParseColour(settings.SectionCapColour, out uint capRgb)) { SetCapColour(capRgb); }
            _sectionNotice = null;
            InitialiseLights(settings);
        }

        #region Setup

        /// <summary>
        /// Builds everything that needs the GL context, showing a loading frame first.
        /// </summary>
        private void Initialise()
        {
            UiScale = _window.DpiScale;
            _ui = new UiBatch();
            _ui.Initialise(UiScale);
            // Sorting and the collision tree on a worker thread with a progress bar (Esc cancels back to the home screen)
            var stopwatch = Stopwatch.StartNew();
            var progress = new Utilities.OperationProgress();
            progress.Begin("Sorting the geometry for drawing", 0.0, 0.45);
            ProgressScreen.Run(_window, _ui, $"Preparing {DocumentName}", progress, () =>
            {
                _batches = new SceneBatches(Scene);
                progress.ThrowIfCancelled();
                progress.Begin("Building collision and picking", 0.45, 1.0);
                _bvh = new Bvh(Scene);
                progress.Step(1.0);
                progress.ThrowIfCancelled();
                return true;
            });
            Utilities.Log_Utils.Write($"Batches {_batches.Batches.Length} / chunks {_batches.Chunks.Length}, BVH nodes {_bvh.NodeCount} in {stopwatch.ElapsedMilliseconds} ms. GL {_window.GlVersion}: {Native.Gl.GetString(Native.Gl.RENDERER)}");

            DrawLoadingFrame("Uploading geometry…");
            _renderer = new SceneRenderer { AutoProxy = _proxyMissing, ProxyMaterialColour = _proxyMaterialColour };
            _renderer.Initialise(Scene, _batches);
            _renderer.Probes.SetOccluders(_bvh, ProbeOccluderMask());
            InitialiseTextures();
            if (_renderer.MaterialWarning != null) { Toast(_renderer.MaterialWarning, 6f); }
            else if (_realistic && !_renderer.HasMaterials)
            {
                Toast("Realistic needs textures: this snapshot shows material colours. Tick “Extract materials and textures” at Go.", 6f);
            }
            _overlay.Initialise();
            _target.Ensure(_window.Width, _window.Height, _msaa);

            // Systems
            Dynamics = new DynamicSet(_bvh, Scene.Elements, _groupVisible);
            var controller = new CharacterController(_bvh)
            {
                StepHeight = Scene.Settings.MaxStepHeightMm / 1000f,
                CollisionMask = _collisionMask,
                Dynamics = Dynamics
            };
            _player = new Player(controller);
            RefreshMasks();

            // Ground: just below the lowest level (or the model)
            _groundDefault = (Scene.Levels.Length > 0 ? Scene.Levels[0].Elevation : Scene.Bounds.Min.Z) - GROUND_BELOW_LOWEST_LEVEL;
            _groundZ = _groundDefault;
            controller.GroundZ = _groundZ;

            Sound.Initialise();

            // Comments: inside the file (standalone) or in a sidecar beside the Revit model
            if (IsFileMode)
            {
                Comments = new CommentStore(null, Scene.ModelTitle, Scene.OriginOffset);
                Comments.LoadFrom(_options.Document?.Comments);
            }
            else
            {
                Comments = new CommentStore(Scene.CommentsPath, Scene.ModelTitle, Scene.OriginOffset);
                if (Scene.Settings.LoadComments) { Comments.Load(); }
            }
            Comments.Changed += UpdateTitle;
            InitialiseBookmarks();
            InitialiseSun();
            InitialiseVisibility();

            _portalGun = new PortalGun(this);
            _commentGun = new CommentGun(this);
            _placeGun = new PlaceGun(this);
            _guns = new Gun[]
            {
                new ScanGun(this), new MeasureGun(this), _portalGun, _commentGun,
                new TeleportGun(this), new HammerGun(this), new GizmoGun(this), new CloneGun(this), _placeGun
            };
            for (int i = 0; i < _guns.Length; i++) { _guns[i].Key = (i + 1).ToString(); }

            // Re-apply the file's edits (demolitions, moves, clones) on top of the untouched geometry
            int replayFailures = ReplayJournal();
            MarkSaved();

            Spawn();
            Native.Wgl.SetSwapInterval(_vsync);
            UpdateTitle();

            if (Comments.LastError != null) { Toast(Comments.LastError); }
            else if (Bookmarks.LastError != null) { Toast(Bookmarks.LastError); }
            else if (!string.IsNullOrEmpty(Scene.PhaseNote)) { Toast(Scene.PhaseNote, 6f); }
            else if (replayFailures > 0) { Toast($"{replayFailures} saved edit{(replayFailures == 1 ? " refers" : "s refer")} to elements not in this file and {(replayFailures == 1 ? "was" : "were")} skipped.", 5f); }
            else
            {
                string edits = _journal.Count > 0 ? $" · {_journal.Count} edit{(_journal.Count == 1 ? string.Empty : "s")}" : string.Empty;
                string links = Scene.Links.Length > 0 ? $" · {Scene.Links.Length} linked model{(Scene.Links.Length == 1 ? string.Empty : "s")}" : string.Empty;
                string start = _startedAtSavedHome ? " Starting at your saved home (Shift+H sets it)." : string.Empty;
                Toast($"{Scene.Elements.Length:N0} elements · {Scene.TriangleCount:N0} triangles{links}{edits}.{start} Click to look around.", _startedAtSavedHome ? 4f : 2.6f);
            }
        }

        /// <summary>
        /// Clears to the menu colour and draws a status line (used while building).
        /// </summary>
        private void DrawLoadingFrame(string message)
        {
            Native.Gl.BindFramebuffer(Native.Gl.FRAMEBUFFER, 0);
            Native.Gl.Viewport(0, 0, _window.Width, _window.Height);
            Native.Gl.ClearColor(0.063f, 0.075f, 0.094f, 1f);
            Native.Gl.Clear(Native.Gl.COLOR_BUFFER_BIT | Native.Gl.DEPTH_BUFFER_BIT);
            FontAtlas f = _ui.Atlas;
            float cx = _window.Width * 0.5f, cy = _window.Height * 0.5f;
            _ui.TextCentred(f.Title, cx, cy - S(40), "BIMGO", UiTheme.TEXT, S(4));
            _ui.TextCentred(f.Body, cx, cy + S(16), message, UiTheme.TEXT_MUTED);
            _ui.Flush(_window.Width, _window.Height);
            _window.Swap();
            _window.PumpMessages();
        }

        /// <summary>
        /// Places the player: where they stood before a reload, else the saved home (Shift+H / SET HOME HERE, kept with
        /// the model), else the active 3D view eye, else a random valid point on the ground.
        /// </summary>
        private void Spawn()
        {
            if (_options.Pose is SessionPose pose)
            {
                ApplyPose(pose);
                return;
            }

            if (Bookmarks?.Home is Format.BookmarkRecord home)
            {
                if (home.Flying != _player.Flying) { _player.ToggleFly(); }
                _player.TeleportTo(home.Local, home.Yaw, Math.Clamp(home.Pitch, -1.5f, 1.5f));
                _player.SetHome();
                _startedAtSavedHome = true;
                return;
            }

            if (Scene.Spawn is SpawnInfo spawn)
            {
                var feet = spawn.Eye - new Vector3(0f, 0f, CharacterController.STAND_EYE);
                _player.TeleportTo(feet, spawn.Yaw, spawn.Pitch);

                // Far above everything (e.g. an aerial perspective)? Start flying so nobody falls 100 m.
                if (feet.Z > Scene.Bounds.Max.Z + 2f) { _player.ToggleFly(); }
            }
            else
            {
                _player.TeleportTo(FindRandomSpawn(out float yaw), yaw, 0f);
            }
            _player.SetHome();
        }

        /// <summary>
        /// Tries random points on the lowest level (or the ground plane outside) with headroom.
        /// </summary>
        private Vector3 FindRandomSpawn(out float yaw)
        {
            var random = new Random();
            Aabb bounds = Scene.Bounds;
            float baseZ = Scene.Levels.Length > 0 ? Scene.Levels[0].Elevation : bounds.Min.Z;
            Vector3 centre = bounds.Center;

            for (int attempt = 0; attempt < 60; attempt++)
            {
                var xy = new Vector2(
                    bounds.Min.X + (float)random.NextDouble() * bounds.Size.X,
                    bounds.Min.Y + (float)random.NextDouble() * bounds.Size.Y);
                var top = new Vector3(xy.X, xy.Y, baseZ + 1.7f);

                Vector3 feet;
                if (Pick(top, -Vector3.UnitZ, 2.6f, out RayHit hit))
                {
                    if (hit.Normal.Z < 0.7f) { continue; }
                    feet = hit.Point + new Vector3(0f, 0f, 0.02f);
                }
                else
                {
                    feet = new Vector3(xy.X, xy.Y, _groundZ);
                }

                if (!_player.Controller.Overlaps(feet + new Vector3(0f, 0f, 0.01f), CharacterController.STAND_HEIGHT))
                {
                    yaw = MathF.Atan2(centre.Y - feet.Y, centre.X - feet.X);
                    return feet;
                }
            }

            // Fallback: outside the model's south side, facing it
            yaw = MathF.PI * 0.5f;
            return new Vector3(centre.X, bounds.Min.Y - 5f, _groundZ);
        }

        /// <summary>
        /// Elements that close a room boundary for reflection-probe blending (walls, glazing, columns…): everything
        /// static except doors (always open to walk through) and movable furniture (it shouldn't decide whether two
        /// rooms connect). Built once per snapshot; probe blending is decided when the probes are placed.
        /// </summary>
        private bool[] ProbeOccluderMask()
        {
            ElementRecord[] elements = Scene.Elements;
            var mask = new bool[elements.Length];
            for (int e = 0; e < elements.Length; e++)
            {
                mask[e] = !elements[e].Movable && elements[e].CategoryIndex != _doorCategory;
            }
            return mask;
        }

        /// <summary>
        /// Rebuilds the per-element pick and collision masks from category and link visibility.
        /// </summary>
        private void RefreshMasks()
        {
            _sceneRevision++;
            UpdateGroupVisibility();
            ElementRecord[] elements = Scene.Elements;
            for (int e = 0; e < elements.Length; e++)
            {
                bool visible = _groupVisible[SceneBatches.GroupOf(elements[e])] && !_hidden[e] && !_userHidden[e];
                _pickMask[e] = visible;
                // Doors render as modelled but are always no-clip, so openings stay walkable
                _collisionMask[e] = visible && elements[e].CategoryIndex != _doorCategory;
            }
        }

        /// <summary>
        /// Combines the category and link toggles into the per-group array the renderer and dynamics read
        /// (the array is shared, so it is filled in place).
        /// </summary>
        private void UpdateGroupVisibility()
        {
            int categories = _categoryVisible.Length;
            for (int g = 0; g < _groupVisible.Length; g++)
            {
                _groupVisible[g] = _categoryVisible[g % categories] && _linkVisible[g / categories];
            }
        }

        #endregion

        #region Loop

        /// <summary>
        /// Runs until the window closes or the session is ended. Unsaved standalone changes are confirmed first
        /// (Cancel keeps the session running).
        /// </summary>
        /// <returns>Why the session ended.</returns>
        public SessionEndReason Run()
        {
            try
            {
                Initialise();
            }
            catch (OperationCanceledException)
            {
                Utilities.Log_Utils.Write("Walkthrough cancelled while the scene was being prepared.");
                return SessionEndReason.EndedByUser;
            }

            var clock = Stopwatch.StartNew();
            double previous = clock.Elapsed.TotalSeconds;
            float accumulator = 0f;

            while (true)
            {
                bool open = _window.PumpMessages();
                if (!open || _endRequested)
                {
                    // The window still exists (close was only requested): unsaved changes may cancel it
                    if (_window.Handle != 0 && !ConfirmDiscardOrSave())
                    {
                        _endRequested = false;
                        _endReason = SessionEndReason.EndedByUser;
                        _window.CancelClose();
                        previous = clock.Elapsed.TotalSeconds;
                        continue;
                    }
                    return open ? _endReason : SessionEndReason.WindowClosed;
                }

                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - previous, 0.1);
                previous = now;
                _clock += dt;
                _frameTime = dt;

                if (_window.IsMinimised)
                {
                    _window.Input.EndFrame();
                    Thread.Sleep(30);
                    continue;
                }

                UpdateFrame(dt);
                PollHost(dt);

                if (!_paused)
                {
                    accumulator += dt;
                    int ticks = 0;
                    while (accumulator >= TICK && ticks < 12)
                    {
                        FixedUpdate(TICK);
                        accumulator -= TICK;
                        ticks++;
                    }
                    if (ticks == 12) { accumulator = 0f; }
                }

                UpdateCamera(_paused ? 1f : accumulator / TICK, dt);
                UpdateAim();
                UpdateGuns(dt);

                Render();
                _window.Swap();
                _window.Input.EndFrame();
                UpdateFps(dt);
            }
        }

        /// <summary>
        /// Variable-rate input handling: window state, global keys, mouse look.
        /// </summary>
        private void UpdateFrame(float dt)
        {
            InputState input = _window.Input;

            if (_window.Resized)
            {
                _window.Resized = false;
                _target.Ensure(_window.Width, _window.Height, _msaa);
            }

            if (_window.FocusLost)
            {
                _window.FocusLost = false;
                if (!_paused && !IsEditingComment) { SetPaused(true); }
            }

            if (IsEditingComment)
            {
                UpdateCommentEditor(input);
                return;
            }

            // The sun hours study has the cursor: the player stands still, RMB-drag looks, clicks pick surfaces
            if (_sunHoursOpen && !_paused)
            {
                UpdateSunHoursMode(input);
                return;
            }

            // Photo mode has the cursor: RMB-drag looks, Enter shoots
            if (_photoOpen && !_paused)
            {
                UpdatePhotoMode(input);
                return;
            }

            // The section box editor has the cursor: RMB-drag looks, handles drag the cut
            if (_sectionOpen && !_paused)
            {
                UpdateSectionEditor(input);
                return;
            }

            // The sun panel has the cursor: the player stands still and its keys take over
            if (_sunPanelOpen && !_paused)
            {
                if (input.IsPressed(Vk.VK_F11)) { _window.ToggleFullscreen(); }
                UpdateSunPanelKeys(input);
                return;
            }

            // A gun that has taken over the movement keys (Gizmo / Clone): Esc cancels it instead of pausing,
            // and the player's own keys are ignored until it lets go.
            Gun current = _guns[_activeGun];
            bool captured = !_paused && current.CapturesInput;

            // Global keys. While the UI is hidden, Esc only brings it back (even when a gun has the keys); the next
            // Esc cancels the gun or pauses as usual.
            bool escape = input.IsPressed(Vk.VK_ESCAPE);
            if (escape && _uiHidden)
            {
                ShowUi();
                escape = false;
            }
            if (escape)
            {
                if (captured) { current.OnCancel(); }
                else if (_paused && (ClosePush() || CloseComments() || CloseBookmarks() || CloseTextures() || CloseLibrary() || CloseRooms())) { /* back to the pause menu */ }
                else { SetPaused(!_paused); }
            }
            if (input.IsPressed(Vk.VK_F1)) { _showHelp = !_showHelp; }
            if (input.IsPressed(Vk.VK_F5) && !captured) { RefreshFromRevit(); }
            if (input.IsPressed(Vk.VK_F11)) { _window.ToggleFullscreen(); }
            if (input.IsPressed(Vk.VK_F12)) { RequestScreenshot(); }

            // Document shortcuts (Ctrl+S save, Ctrl+Shift+S save as, Ctrl+Z undo, Ctrl+Y / Ctrl+Shift+Z redo in files)
            if (input.IsDown(Vk.VK_CONTROL) && !captured)
            {
                if (input.IsPressed('S'))
                {
                    Save(saveAs: input.IsDown(Vk.VK_SHIFT) || !IsFileMode);
                    return;
                }
                if (input.IsPressed('Z'))
                {
                    if (input.IsDown(Vk.VK_SHIFT)) { Redo(); }
                    else { Undo(); }
                    return;
                }
                if (input.IsPressed('Y'))
                {
                    Redo();
                    return;
                }
                if (input.IsPressed('F') && !_paused)
                {
                    OpenRooms();
                    return;
                }
                if (input.IsPressed('P') && !_paused)
                {
                    ClearSection();
                    return;
                }

                // Ctrl+1..9: jump to a bookmark (instead of selecting a gun)
                if (!_paused)
                {
                    for (int i = 0; i < 9; i++)
                    {
                        if (input.IsPressed('1' + i))
                        {
                            GoToBookmarkAt(i);
                            return;
                        }
                    }
                }
            }
            if (_paused) { return; }

            // Hide-UI mode (works while a gun has the movement keys too: none of them uses U)
            if (input.IsPressed('U')) { ToggleUiHidden(); }

            UpdateRoom();

            if (captured)
            {
                current.OnKeys(input);
                if (input.IsPressed(Vk.VK_TAB)) { _showMap = !_showMap; }
                UpdateMouseLook(input);
                return;
            }

            if (input.IsPressed(Vk.VK_TAB)) { _showMap = !_showMap; }
            if (input.IsPressed('V'))
            {
                _player.ToggleFly();
                Toast(_player.Flying ? "Fly mode (no-clip)" : "Walk mode");
            }
            if (input.IsPressed('H'))
            {
                if (input.IsDown(Vk.VK_SHIFT)) { SetHomeHere(); }
                else
                {
                    _player.GoHome();
                }
            }
            if (input.IsPressed(Vk.VK_PRIOR)) { TeleportLevel(+1); }
            if (input.IsPressed(Vk.VK_NEXT)) { TeleportLevel(-1); }
            if (input.IsPressed('X')) { _guns[_activeGun].ClearMarkers(); }
            if (input.IsPressed('B'))
            {
                AddBookmarkHere();
                return;
            }
            if (input.IsPressed('L')) { CycleCoordinateReadout(); }
            if (input.IsPressed('J'))
            {
                OpenSunHours();
                return;
            }
            if (input.IsPressed('M'))
            {
                OpenPhotoMode();
                return;
            }
            if (input.IsPressed('P'))
            {
                if (input.IsDown(Vk.VK_SHIFT)) { QuickSectionPlane(); }
                else { OpenSectionEditor(); }
                return;
            }
            if (input.IsPressed('K')) { CycleLightMode(); }
            if (input.IsPressed('O'))
            {
                if (input.IsDown(Vk.VK_SHIFT)) { OpenSunPanel(); }
                else { ToggleShadows(); }
                return;
            }
            if (ShadowsOn && input.IsPressedOrRepeated(Vk.VK_OEM_4)) { StepSunTime(input.IsDown(Vk.VK_SHIFT) ? -1 : -TIME_STEP); }
            if (ShadowsOn && input.IsPressedOrRepeated(Vk.VK_OEM_6)) { StepSunTime(input.IsDown(Vk.VK_SHIFT) ? 1 : TIME_STEP); }
            if (input.IsPressed(Vk.VK_SPACE) && !_player.Flying) { _player.QueueJump(); }

            for (int i = 0; i < _guns.Length; i++)
            {
                if (input.IsPressed('1' + i)) { SelectGun(i); }
            }
            if (input.Wheel != 0)
            {
                int next = ((_activeGun - Math.Sign(input.Wheel)) % _guns.Length + _guns.Length) % _guns.Length;
                SelectGun(next);
            }

            _guns[_activeGun].OnKeys(input);
            UpdateMouseLook(input);
        }

        /// <summary>
        /// Mouse capture (click to look) and mouse look.
        /// </summary>
        private void UpdateMouseLook(InputState input)
        {
            if (!_window.IsCaptured && input.LeftPressed && _window.IsActive)
            {
                // A click on the sun icon (cursor free) opens the sun panel instead of capturing the mouse
                if (HoverSunIcon(input))
                {
                    input.ConsumeClicks();
                    OpenSunPanel();
                    return;
                }
                _window.SetCaptured(true);
                input.ConsumeClicks();
            }
            if (_window.IsCaptured)
            {
                _player.Look(input.MouseDeltaX, input.MouseDeltaY, _sensitivity, _invertY);
            }
        }

        /// <summary>
        /// One fixed physics tick.
        /// </summary>
        private void FixedUpdate(float dt)
        {
            _player.Controller.GroundZ = _groundZ;
            bool frozen = IsEditingComment || _sunPanelOpen || _sunHoursOpen || !_window.IsActive || _guns[_activeGun].CapturesInput;
            _player.FixedUpdate(dt, _window.Input, inputEnabled: !frozen);
            _portalGun.CheckTeleport(_player, dt);
        }

        /// <summary>
        /// Positions the camera at the smoothed eye.
        /// </summary>
        private void UpdateCamera(float alpha, float dt)
        {
            Camera.Position = _player.GetEye(Math.Clamp(alpha, 0f, 1f), dt);
            Camera.Yaw = _player.Yaw;
            Camera.Pitch = _player.Pitch;
            Camera.HorizontalFovDegrees = _fov;
            Camera.ViewportWidth = _window.Width;
            Camera.ViewportHeight = _window.Height;
            Camera.Aspect = (float)_window.Width / Math.Max(_window.Height, 1);
            Camera.Update();
        }

        /// <summary>
        /// Casts the crosshair ray.
        /// </summary>
        private void UpdateAim()
        {
            _aim.Origin = Camera.Position;
            _aim.Direction = Camera.Forward;
            _aim.HasHit = Pick(Camera.Position, Camera.Forward, PICK_DISTANCE, out _aim.Hit);
        }

        /// <summary>
        /// Gun updates and fire input.
        /// </summary>
        private void UpdateGuns(float dt)
        {
            PumpSource(dt);
            foreach (Gun gun in _guns) { gun.Tick(dt); }
            if (_paused || IsEditingComment) { return; }

            Gun active = _guns[_activeGun];
            active.Update(dt, _aim);

            InputState input = _window.Input;
            if (_window.IsCaptured)
            {
                if (input.LeftPressed) { active.OnPrimary(_aim); }
                if (input.RightPressed) { active.OnSecondary(_aim); }
            }
        }

        private void UpdateFps(float dt)
        {
            _fpsAccumulator += dt;
            _fpsFrames++;
            if (_fpsAccumulator >= 0.5f)
            {
                _fps = _fpsFrames / _fpsAccumulator;
                _frameMs = _fpsAccumulator * 1000f / _fpsFrames;
                _fpsAccumulator = 0f;
                _fpsFrames = 0;
            }
        }

        #endregion

        #region Actions

        private void SelectGun(int index)
        {
            if (index == _activeGun || _guns[_activeGun].CapturesInput) { return; }
            _guns[_activeGun].OnDeselect();
            _activeGun = index;
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>
        /// Pauses (releases the mouse) or resumes.
        /// </summary>
        private void SetPaused(bool paused)
        {
            _paused = paused;
            if (paused) { ShowUi(); } // e.g. focus lost while hidden: come back to a normal HUD
            _window.SetCaptured(!paused && _window.IsActive && !_sunPanelOpen && !_sunHoursOpen);
            _window.Input.ReleaseAll();
        }

        /// <summary>
        /// Shift+H / SET HOME HERE: home becomes this viewpoint for H and for the next walkthrough of this model (saved
        /// with the bookmarks: in the .bimgo, or beside the Revit model). Acknowledged with a sound, a flash and a
        /// note (the pause menu button shows HOME SAVED for a moment).
        /// </summary>
        private void SetHomeHere()
        {
            _player.SetHome();
            Bookmarks?.SetHome(_player.Feet, _player.Yaw, _player.Pitch, _player.Flying, CurrentLevelName);
            _homeSetUntil = _clock + 2f;
            Sound.Play(SoundId.Commit);
            Flash(UiTheme.BOOKMARK, 0.2f);
            if (Bookmarks?.LastError != null) { Toast(Bookmarks.LastError, 4f, important: true); }
            else if (IsFileMode) { Toast("Home set here: H returns here, and this file opens here once saved (Ctrl+S)", 3.5f); }
            else { Toast("Home set here: H returns here, and this model opens here next time", 3.5f); }
        }

        /// <summary>
        /// Page Up / Down: jump to the next level, landing on the floor below the eye if there is one.
        /// </summary>
        private void TeleportLevel(int direction)
        {
            if (Scene.Levels.Length == 0) { return; }

            int current = LevelIndexAt(_player.Feet.Z);
            int target = Math.Clamp(current + direction, 0, Scene.Levels.Length - 1);
            if (target == current && !(current == 0 && direction > 0 && _player.Feet.Z < Scene.Levels[0].Elevation - 0.5f))
            {
                Toast(direction > 0 ? "Already on the top level" : "Already on the lowest level");
                return;
            }

            LevelInfo level = Scene.Levels[target];
            Vector3 feet = new(_player.Feet.X, _player.Feet.Y, level.Elevation + 0.02f);
            if (!_player.Flying && Pick(new Vector3(feet.X, feet.Y, level.Elevation + 1.7f), -Vector3.UnitZ, 2.4f, out RayHit hit) && hit.Normal.Z > 0.7f)
            {
                feet.Z = hit.Point.Z + 0.02f;
            }
            _player.TeleportTo(feet);
            Toast(level.Name);
        }

        /// <summary>
        /// Index of the level the given elevation is on (highest level at or below z + 0.4 m).
        /// </summary>
        public int LevelIndexAt(float z)
        {
            int index = 0;
            for (int i = 0; i < Scene.Levels.Length; i++)
            {
                if (Scene.Levels[i].Elevation <= z + 0.4f) { index = i; }
            }
            return index;
        }

        /// <summary>
        /// Level name at an elevation.
        /// </summary>
        public string LevelNameAt(float z) => Scene.Levels.Length == 0 ? "—" : Scene.Levels[LevelIndexAt(z)].Name;

        /// <summary>
        /// The player's current level name.
        /// </summary>
        public string CurrentLevelName => LevelNameAt(_player.Feet.Z);

        /// <summary>
        /// Shows a short message at the top of the screen.
        /// </summary>
        /// <param name="message">The text.</param>
        /// <param name="seconds">How long it stays.</param>
        /// <param name="important">True for errors and failures: shown even while the UI is hidden (U).</param>
        public void Toast(string message, float seconds = 2.6f, bool important = false)
        {
            // While the UI is hidden an ordinary message is dropped, so it can't replace an error still showing
            if (_uiHidden && !important) { return; }
            _toast = message;
            _toastUntil = _clock + seconds;
            _toastImportant = important;
        }

        /// <summary>True while hide-UI mode is on (U).</summary>
        public bool IsUiHidden => _uiHidden;

        /// <summary>
        /// U: hides or shows the UI. Entering says how to get it back.
        /// </summary>
        private void ToggleUiHidden()
        {
            if (_uiHidden)
            {
                ShowUi();
                return;
            }
            _uiHidden = true;
            Sound.Play(SoundId.UiClick);
            Toast("UI hidden · Esc or U to show it", 1.8f, important: true);
        }

        /// <summary>
        /// Leaves hide-UI mode (Esc, U, the pause menu, the sun panel).
        /// </summary>
        private void ShowUi()
        {
            if (!_uiHidden) { return; }
            _uiHidden = false;
            _toast = null;
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>
        /// Flashes the screen briefly (portal FX).
        /// </summary>
        public void Flash(uint colour, float seconds)
        {
            _flashColour = colour;
            _flashLength = seconds;
            _flashUntil = _clock + seconds;
        }

        /// <summary>
        /// Picks against visible geometry: the static scene and moved / cloned elements. Geometry the section cut
        /// removes is passed through (tools ignore it; collision doesn't).
        /// </summary>
        public bool Pick(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit) =>
            PickExcluding(origin, direction, maxDistance, null, out hit);

        /// <summary>
        /// Picks like <see cref="Pick"/> but ignores one moved / cloned element (drop to floor casts from inside the
        /// element's own box). A moved original's static copy is already hidden, so only the instance needs leaving out.
        /// Hits in geometry the section cut removes are skipped (the ray carries on past them).
        /// </summary>
        public bool PickExcluding(Vector3 origin, Vector3 direction, float maxDistance, DynamicInstance exclude, out RayHit hit)
        {
            if (_sectionCount == 0) { return PickOnce(origin, direction, maxDistance, exclude, out hit); }

            float travelled = 0f;
            for (int attempt = 0; attempt < 16 && travelled < maxDistance; attempt++)
            {
                if (!PickOnce(origin + direction * travelled, direction, maxDistance - travelled, exclude, out hit)) { return false; }
                if (!SectionCut.IsCut(_sectionPlanes, _sectionCount, hit.Point))
                {
                    hit.Distance += travelled;
                    return true;
                }
                travelled += hit.Distance + 0.002f;
            }
            hit = default;
            return false;
        }

        /// <summary>One pick against the static scene and the moved / placed elements (the cut not considered).</summary>
        private bool PickOnce(Vector3 origin, Vector3 direction, float maxDistance, DynamicInstance exclude, out RayHit hit)
        {
            bool hitStatic = _bvh.Raycast(origin, direction, maxDistance, _pickMask, out hit);
            float limit = hitStatic ? hit.Distance : maxDistance;
            if (Dynamics != null && Dynamics.Raycast(origin, direction, limit, out RayHit dynamicHit, exclude))
            {
                hit = dynamicHit;
                return true;
            }
            return hitStatic;
        }

        /// <summary>
        /// The player (for guns that move it).
        /// </summary>
        public Player Player => _player;

        /// <summary>
        /// This frame's input (for guns that read held keys, e.g. the gizmo).
        /// </summary>
        public InputState Input => _window.Input;

        #endregion

        #region Shutdown

        /// <summary>
        /// Persists in-session display settings for next time.
        /// </summary>
        private void SaveSettings()
        {
            LaunchSettings settings = LaunchSettings.LoadOrDefault();
            settings.Colour = _whitecard ? ColourMode.Whitecard : _realistic ? ColourMode.Realistic : ColourMode.Material;
            settings.Reflections = _reflections;
            settings.ReflectionThreshold = _reflectThreshold;
            settings.ReflectionStrength = _reflectStrength;
            settings.ReflectionProbes = _reflectProbes;
            settings.ProbeResolution = _probeHigh ? 256 : 128;
            settings.RevitTint = _tintMode;
            settings.ProxyMissingTextures = _proxyMissing;
            settings.ProxyMaterialColour = _proxyMaterialColour;
            settings.Msaa = _msaa;
            settings.AmbientOcclusion = _ambientOcclusion;
            settings.ArtificialLights = _lightMode;
            settings.ArtificialLightIntensity = _lightIntensity;
            settings.BloomIntensity = _bloomIntensity;
            settings.FieldOfView = _fov;
            settings.MouseSensitivity = _sensitivity;
            settings.InvertY = _invertY;
            settings.VSync = _vsync;
            settings.ShowFps = _showFps;
            settings.GizmoSnap = GizmoSnap;
            settings.SnapMoveMm = SnapMoveMm;
            settings.SnapAngleDeg = SnapAngleDeg;
            settings.CoordinateReadout = _coordinateReadout;
            settings.BcfCoordinates = _bcfCoordinates;
            settings.SectionCapColour = $"#{_capRgb:X6}";
            settings.ShadowQuality = _shadowQuality;
            settings.Save();
        }

        /// <summary>
        /// Releases GL and audio resources (context still current).
        /// </summary>
        public void Dispose()
        {
            try { SaveSettings(); } catch { /* logged inside */ }
            try { FlushSunSidecar(); } catch { /* logged inside */ }
            try { FlushVisibilitySidecar(); } catch { /* logged inside */ }
            _window.SetCaptured(false);
            _push?.Dispose();
            try { ReleaseThumbnails(); } catch (Exception ex) { Utilities.Log_Utils.Write($"Thumbnail cleanup failed: {ex.Message}"); }
            try { ReleaseLibraryPreviews(); } catch (Exception ex) { Utilities.Log_Utils.Write($"Library preview cleanup failed: {ex.Message}"); }
            Sound.Dispose();
            _renderer?.Dispose();
            _overlay.Dispose();
            _sunOverlay?.Dispose();
            _sectionOverlay?.Dispose();
            _photoTarget?.Dispose();
            _photoResolve?.Dispose();
            _target.Dispose();
            _ui?.Dispose();
        }

        #endregion

        /// <summary>
        /// Scales a value by the UI scale.
        /// </summary>
        private float S(float value) => value * UiScale;
    }
}
