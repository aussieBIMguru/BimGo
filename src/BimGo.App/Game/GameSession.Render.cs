using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Game.Guns;
using BimGo.Rendering;
using BimGo.Scene;
using Gl = BimGo.Native.Gl;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Rendering: 3D passes, minimap and HUD.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private readonly Vector4[] _mapPlanes = new Vector4[6];
        private readonly List<Highlight> _highlights = new();

        // Help panel column widths (measured from the text; recomputed when the UI scale changes)
        private float _helpScale = -1f, _helpKeyWidth, _helpActionWidth;

        private static readonly (string Key, string Action)[] HELP_ROWS =
        {
            ("WASD", "Move"),
            ("SPACE / CTRL", "Jump / Crouch"),
            ("SHIFT", "Run"),
            ("V", "Fly / walk (no-clip)"),
            ("PGUP / PGDN", "Level up / down"),
            ("H · SHIFT+H", "Go home · Set home here"),
            ("1–9 · WHEEL", "Select gun (9 Place: family library)"),
            ("F", "Gizmo / Clone / Place: drop onto the surface below"),
            ("I · SHIFT+I", "Scan gun: hide target · isolate its category"),
            ("X", "Clear this gun's markers"),
            ("B · CTRL+1–9", "Bookmark this view · Go to bookmark"),
            ("L", "Coordinate readout"),
            ("K", "Artificial lights: off / glow / light"),
            ("O · SHIFT+O", "Shadows on/off · Sun panel"),
            ("J", "Sun / daylight study (click surfaces, RMB-drag looks)"),
            ("P · SHIFT+P · CTRL+P", "Section box · cut at aimed surface · clear"),
            ("[ ]", "Sun time −/+ 5 min (Shift: 1 min)"),
            ("CTRL+S · Z · Y", "Save · Undo · Redo (files)"),
            ("CTRL+F", "Find a room and go there"),
            ("F5", "Refresh from Revit (live sessions)"),
            ("TAB · ESC", "Minimap · Pause menu"),
            ("U", "Hide the UI (Esc or U shows it)"),
            ("M", "Photo mode: hi-res stills and 360° panoramas"),
            ("F11 · F12", "Fullscreen · Screenshot"),
            ("F1", "Hide help · BimGo " + Program.Version)
        };

        #endregion

        /// <summary>
        /// Renders one frame (scene into the off-screen target, then minimap and UI on the window).
        /// </summary>
        private void Render()
        {
            int width = _window.Width, height = _window.Height;

            // ---- Photo mode: a pending shot renders off-screen first (camera and targets restored afterwards)
            if (_photoShotIn > 0 && --_photoShotIn == 0) { TakePhoto(); }

            RenderScene(width, height, _target, _msaa, photo: false);

            _target.BlitToWindow();
            if (ThumbnailDue) { CaptureThumbnail(width, height); }
            if (_screenshotRequested) { CaptureScreenshot(width, height); }
            if (_sunShotRequested)
            {
                // The study's screenshot: the 3D view plus the legend (drawn and flushed first), no other UI
                _sunShotRequested = false;
                Gl.Viewport(0, 0, width, height);
                BuildSunLegend(_ui.Atlas, S(20), height - S(20) - S(78));
                _ui.Flush(width, height);
                CaptureScreenshot(width, height, " sun hours");
            }

            // ---- Window pass: minimap 3D, then all 2D UI in one batch
            Gl.Viewport(0, 0, width, height);
            float mapX = width - S(20) - S(220), mapY = S(20);
            if (_showMap && !_paused && !_uiHidden && !_photoOpen) { DrawMinimapPlan(mapX + S(8), mapY + S(30), S(204), S(170)); }

            if (_paused)
            {
                // A text box opened from a panel (reply, assignee) sits over the menu and takes the clicks
                if (IsEditingComment) { _window.Input.ConsumeClicks(); }
                BuildPauseMenu();
                if (IsEditingComment) { BuildCommentEditor(); }
            }
            else if (_photoOpen)
            {
                // Photo mode: a clean frame, the grid and the photo panel only
                BuildPhotoOverlay(_ui.Atlas, _window.Input, width, height);
            }
            else
            {
                // A text box (comment, bookmark or study name) takes the clicks too: panels behind it stay still
                if (IsEditingComment) { _window.Input.ConsumeClicks(); }
                if (_uiHidden) { BuildHiddenHud(width); }
                else { BuildHud(mapX, mapY); }
                if (IsEditingComment) { BuildCommentEditor(); }
            }
            _ui.Flush(width, height);
        }

        /// <summary>
        /// The 3D passes into a target: shadows, lights, AO and glow, the scene, caps, ground, highlights, glass, bloom,
        /// photo exposure, then (not for photos) markers and the section gizmo. Uses <see cref="Camera"/> as it stands.
        /// </summary>
        /// <param name="width">Target width (pixels).</param>
        /// <param name="height">Target height.</param>
        /// <param name="target">The target (sized here).</param>
        /// <param name="samples">MSAA samples.</param>
        /// <param name="photo">A photo: no probe baking, no highlights, markers or gizmos.</param>
        private void RenderScene(int width, int height, RenderTarget target, int samples, bool photo)
        {
            // ---- Sun lighting and shadow maps (only changed cascades re-render)
            _renderer.Lighting = CurrentLighting();
            string shadowError = _renderer.UpdateShadows(Camera, Scene.Bounds, ShadowSceneKey(), _groupVisible, Dynamics, _whitecard,
                ShadowMaps.PresetFor(_shadowQuality));
            if (shadowError != null)
            {
                OnShadowFailure(shadowError);
                _renderer.Lighting = CurrentLighting();
            }

            // ---- Artificial lights for this frame (after the sun: daylight dims them), and their cached shadow maps
            UpdateArtificialLights();
            string lightShadowError = _renderer.UpdateLightShadows(_groupVisible, Dynamics, ShadowSceneKey());
            if (lightShadowError != null) { Toast(lightShadowError, 6f, important: true); }

            // ---- Ambient occlusion and glow: half-resolution geometry pre-pass, AO + blur, bloom source + blur
            bool glow = _renderer.Artificial.Bloom > 0f;
            string effectsError = _renderer.UpdateScreenEffects(Camera, width, height, _groupVisible, Dynamics, _groundZ, _ambientOcclusion, glow);
            if (effectsError != null) { OnScreenEffectsFailure(effectsError); }

            // ---- Reflection probes: a couple of faces per frame until baked, re-baked a moment after things change
            if (!photo) { UpdateReflectionProbes(); }

            // ---- 3D scene into the (optionally multisampled) target
            target.Ensure(width, height, samples);
            target.Bind();
            Vector3 fog = _renderer.FogColour;
            Gl.ClearColor(fog.X, fog.Y, fog.Z, 1f);
            Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.Disable(Gl.CULL_FACE);
            Gl.Disable(Gl.BLEND);

            _renderer.DrawSky(Camera);

            var p = new SceneDrawParams
            {
                ViewProjection = Camera.ViewProjection,
                Planes = Camera.Planes,
                Eye = Camera.Position,
                Whitecard = _whitecard,
                Realistic = _realistic,
                Reflections = _reflections,
                ReflectThreshold = _reflectThreshold / 100f,
                ReflectGain = _reflectStrength,
                ReflectDebug = _reflectDebug,
                Probes = _reflectProbes,
                Time = _clock,
                Tint = _tintMode,
                Plan = false,
                ClipZ = new Vector2(-1e7f, 1e7f),
                FogDensity = 0.0022f,
                Sun = true,
                Section = true
            };

            _renderer.DrawStatic(p, _groupVisible, transparent: false);
            _renderer.DrawDynamic(p, Dynamics, transparent: false);
            if (_sectionCount > 0)
            {
                // Fill the cut solids (stencil caps) before the ground
                float extent = Scene.Bounds.Size.Length() + 50f;
                _renderer.DrawSectionCaps(Camera.ViewProjection, Camera.Planes, Camera.Position, _groupVisible, Dynamics, _capColour, extent);
            }
            _renderer.DrawGround(Camera, _groundZ);

            // Gun highlights (scan target, primed demolitions, gizmo target…)
            Gun active = _guns[_activeGun];
            _highlights.Clear();
            // Hidden UI: no tints either, unless a gun is in the middle of something (Gizmo / Clone holding an element)
            if (!photo && !_photoOpen && !_paused && (!_uiHidden || active.CapturesInput)) { active.CollectHighlights(_highlights); }
            if (_highlights.Count > 0)
            {
                Gl.Enable(Gl.BLEND);
                Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
                Gl.Enable(Gl.POLYGON_OFFSET_FILL);
                Gl.PolygonOffset(-1f, -2f);
                foreach (Highlight highlight in _highlights) { DrawHighlight(p, highlight); }
                Gl.Disable(Gl.POLYGON_OFFSET_FILL);
                Gl.Disable(Gl.BLEND);
            }

            // Transparent pass (glass etc.)
            Gl.Enable(Gl.BLEND);
            Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
            Gl.DepthMask(false);
            _renderer.DrawStatic(p, _groupVisible, transparent: true);
            _renderer.DrawDynamic(p, Dynamics, transparent: true);
            Gl.DepthMask(true);
            Gl.Disable(Gl.BLEND);

            // Bloom from glowing surfaces over everything (glass included)
            _renderer.CompositeGlow();

            // Photo exposure (the preview and the photo alike)
            if (_photoOpen) { _renderer.ApplyExposure(_photoExposure); }

            // Markers: depth-tested, then a faint x-ray copy so markers behind walls stay discoverable (none while the
            // UI is hidden or in photo mode: clean views)
            if (!_uiHidden && !photo && !_photoOpen)
            {
                _overlay.Begin(Camera);
                for (int i = 0; i < _guns.Length; i++) { _guns[i].DrawWorld(_overlay, i == _activeGun); }
                _overlay.Draw(Camera, depthTest: true, alpha: 1f, additive: false);
                _overlay.Draw(Camera, depthTest: false, alpha: 0.16f, additive: false);
            }

            // Sun hours grid (study results are content, so they show with the UI hidden too)
            DrawSunHoursCells();

            // Section box frame and handles (editor only)
            if (!photo) { DrawSectionGizmo(); }
        }

        /// <summary>
        /// AO and glow could not be set up on this GPU: switch AO off (the setting is saved off too) and the bloom off
        /// for this session (surfaces still glow, lights still light), and say why.
        /// </summary>
        private void OnScreenEffectsFailure(string reason)
        {
            _ambientOcclusion = false;
            _bloomFailed = true;
            _renderer.Artificial.Bloom = 0f;
            _renderer.DisableScreenEffects();
            Sound.Play(SoundId.Error);
            Toast(reason, 6f, important: true);
        }

        /// <summary>
        /// Tints one static element or dynamic instance.
        /// </summary>
        private void DrawHighlight(in SceneDrawParams p, Highlight highlight)
        {
            Vector4 colour = Rgba.ToVector(highlight.Colour);
            colour.W = highlight.Strength;

            if (highlight.DynamicId > 0)
            {
                Physics.DynamicInstance instance = Dynamics.Find(highlight.DynamicId);
                if (instance != null && Dynamics.IsActive(instance)) { _renderer.DrawDynamicHighlight(p, instance, colour); }
            }
            else if (highlight.Element >= 0 && _pickMask[highlight.Element])
            {
                _renderer.DrawElementHighlight(p, highlight.Element, colour);
            }
        }

        #region Minimap

        /// <summary>
        /// Draws the plan view (cut at 1.2 m above the current level) into a scissored viewport.
        /// </summary>
        private void DrawMinimapPlan(float x, float y, float w, float h)
        {
            int height = _window.Height;
            int vx = (int)x, vy = height - (int)(y + h), vw = Math.Max(1, (int)w), vh = Math.Max(1, (int)h);

            Gl.Enable(Gl.SCISSOR_TEST);
            Gl.Scissor(vx, vy, vw, vh);
            Gl.Viewport(vx, vy, vw, vh);
            Vector4 background = Rgba.ToVector(UiTheme.MAP_BACKGROUND);
            Gl.ClearColor(background.X, background.Y, background.Z, 1f);
            Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);

            float elevation = Scene.Levels.Length > 0 ? Scene.Levels[LevelIndexAt(_player.Feet.Z)].Elevation : _player.Feet.Z;
            float metresAcross = MapMetresAcross;
            float metresHigh = metresAcross * h / w;

            var eye = new Vector3(_player.Feet.X, _player.Feet.Y, elevation + 60f);
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye - Vector3.UnitZ, Vector3.UnitY);
            Matrix4x4 projection = FpsCamera.Orthographic(metresAcross, metresHigh, 1f, 200f);
            Matrix4x4 viewProjection = view * projection;
            FpsCamera.ExtractPlanes(viewProjection, _mapPlanes);

            var p = new SceneDrawParams
            {
                ViewProjection = viewProjection,
                Planes = _mapPlanes,
                Eye = eye,
                Whitecard = _whitecard,
                Realistic = _realistic,
                Tint = _tintMode,
                Plan = true,
                ClipZ = new Vector2(elevation - 0.3f, elevation + 1.2f),
                FogDensity = 0f
            };
            Gl.Enable(Gl.DEPTH_TEST);
            _renderer.DrawStatic(p, _groupVisible, transparent: false);
            _renderer.DrawDynamic(p, Dynamics, transparent: false);

            Gl.Disable(Gl.SCISSOR_TEST);
            Gl.Viewport(0, 0, _window.Width, _window.Height);
        }

        private const float MapMetresAcross = 34f;

        /// <summary>
        /// Draws the minimap frame, header and markers (UI layer).
        /// </summary>
        private void DrawMinimapOverlay(float x, float y)
        {
            FontAtlas f = _ui.Atlas;
            float w = S(220), h = S(208);
            float mapX = x + S(8), mapY = y + S(30), mapW = S(204), mapH = S(170);

            // Frame: header strip and border only (the plan itself was drawn in 3D)
            _ui.Rect(x, y, w, S(30), UiTheme.PANEL);
            _ui.Rect(x, y + S(30), S(8), h - S(30), UiTheme.PANEL);
            _ui.Rect(x + w - S(8), y + S(30), S(8), h - S(30), UiTheme.PANEL);
            _ui.Rect(x + S(8), y + h - S(8), w - S(16), S(8), UiTheme.PANEL);
            _ui.Outline(x, y, w, h, MathF.Max(1f, MathF.Round(UiScale)), UiTheme.PANEL_BORDER);

            Text.Clear().Append("MAP · ").Append(_levelNamesUpper.Length > 0 ? _levelNamesUpper[LevelIndexAt(_player.Feet.Z)] : "\u2014");
            _ui.Text(f.Small, x + S(8), y + S(9), Text.Span, UiTheme.TEXT_MUTED, S(1f));
            _ui.TextRight(f.Small, x + w - S(8), y + S(9), "TAB", UiTheme.TEXT_MUTED, S(1f));

            float metresPerPixel = MapMetresAcross / mapW;
            float cx = mapX + mapW * 0.5f, cy = mapY + mapH * 0.5f;
            float levelZ = _player.Feet.Z;
            int levelIndex = LevelIndexAt(levelZ);

            // Portals and comments on this level
            for (int i = 0; i < 2; i++)
            {
                if (!_portalGun.IsActive(i)) { continue; }
                Vector3 c = _portalGun.CentreOf(i);
                if (LevelIndexAt(c.Z - 1f) != levelIndex) { continue; }
                MapDot(c, PortalGun.ColourOf(i), S(4));
            }
            foreach (CommentRecord record in Comments.Comments)
            {
                if (LevelIndexAt(record.Local.Z - 0.5f) != levelIndex) { continue; }
                MapDot(record.Local, UiTheme.COMMENT, S(3.5f));
            }
            foreach (BookmarkRecord bookmark in Bookmarks.Bookmarks)
            {
                if (LevelIndexAt(bookmark.Local.Z) != levelIndex) { continue; }
                MapDot(bookmark.Local, UiTheme.BOOKMARK, S(3f));
            }

            // View cone and player arrow (north up, screen Y down)
            float angle = -_player.Yaw;
            float halfFov = _fov * MathF.PI / 360f;
            _ui.Wedge(cx, cy, S(56), angle - halfFov, angle + halfFov, Rgba.WithAlpha(UiTheme.ACCENT, 0.13f));
            var forward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var side = new Vector2(-forward.Y, forward.X);
            Vector2 tip = new Vector2(cx, cy) + forward * S(10);
            Vector2 left = new Vector2(cx, cy) - forward * S(6) + side * S(7);
            Vector2 right = new Vector2(cx, cy) - forward * S(6) - side * S(7);
            Vector2 notch = new Vector2(cx, cy) - forward * S(2);
            _ui.Triangle(tip.X, tip.Y, left.X, left.Y, notch.X, notch.Y, UiTheme.ACCENT);
            _ui.Triangle(tip.X, tip.Y, notch.X, notch.Y, right.X, right.Y, UiTheme.ACCENT);

            void MapDot(Vector3 world, uint colour, float radius)
            {
                float mx = cx + (world.X - _player.Feet.X) / metresPerPixel;
                float my = cy - (world.Y - _player.Feet.Y) / metresPerPixel;
                if (mx < mapX + radius || mx > mapX + mapW - radius || my < mapY + radius || my > mapY + mapH - radius) { return; }
                _ui.Circle(mx, my, radius, colour, 12);
            }
        }

        #endregion

        #region HUD

        /// <summary>
        /// Builds the in-game HUD.
        /// </summary>
        private void BuildHud(float mapX, float mapY)
        {
            FontAtlas f = _ui.Atlas;
            int width = _window.Width, height = _window.Height;
            Gun active = _guns[_activeGun];

            // Screen flash (portals)
            if (_clock < _flashUntil)
            {
                float t = (_flashUntil - _clock) / MathF.Max(_flashLength, 0.01f);
                _ui.Rect(0, 0, width, height, Rgba.WithAlpha(_flashColour, 0.35f * t));
            }

            // World-anchored labels
            for (int i = 0; i < _guns.Length; i++) { _guns[i].DrawLabels(_ui, i == _activeGun); }

            // Crosshair
            float cx = MathF.Round(width * 0.5f), cy = MathF.Round(height * 0.5f);
            float t1 = S(2f), gap = S(5), arm = S(9);
            _ui.Rect(cx - t1 * 0.5f, cy - gap - arm, t1, arm, UiTheme.TEXT);
            _ui.Rect(cx - t1 * 0.5f, cy + gap, t1, arm, UiTheme.TEXT);
            _ui.Rect(cx - gap - arm, cy - t1 * 0.5f, arm, t1, UiTheme.TEXT);
            _ui.Rect(cx + gap, cy - t1 * 0.5f, arm, t1, UiTheme.TEXT);
            _ui.Circle(cx, cy, S(1.8f), active.Colour, 10);

            if (!_window.IsCaptured && !IsEditingComment && !_sunPanelOpen && !_sunHoursOpen)
            {
                const string hint = "Click to look around";
                float hintWidth = UiBatch.Measure(f.Body, hint) + S(24);
                _ui.Panel(cx - hintWidth * 0.5f, cy + S(28), hintWidth, S(28), UiTheme.PANEL, UiTheme.PANEL_BORDER);
                _ui.TextCentred(f.Body, cx, cy + S(34), hint, UiTheme.TEXT);
            }

            BuildStatusPanel(f);
            BuildCoordinatePanel(f, S(20), S(20) + S(146) + S(10));

            // Minimap and the gun's context panel beneath it
            if (_showMap) { DrawMinimapOverlay(mapX, mapY); }
            // (hidden while the sun panel is open: the two would overlap on smaller screens)
            if (!_sunPanelOpen && !_sunHoursOpen && !_sectionOpen)
            {
                float panelTop = _showMap ? mapY + S(208) + S(12) : S(20);
                float panelWidth = S(260), panelX = width - S(20) - panelWidth;
                float panelHeight = S(active.PanelHeight) + S(24);
                _ui.Panel(panelX, panelTop, panelWidth, panelHeight, UiTheme.PANEL, UiTheme.PANEL_BORDER);
                active.DrawPanel(_ui, panelX + S(14), panelTop + S(12), panelWidth - S(28));
            }

            BuildSunIcon(f, _window.Input);
            if (_sunPanelOpen) { BuildSunPanel(f, _window.Input); }
            if (_sunHoursOpen) { BuildSunHoursPanel(f, _window.Input); }
            BuildSectionPanel(f, _window.Input);
            BuildSunLegend(f, S(20), height - S(52) - S(78));

            BuildHelp(f, height);
            BuildGunBar(f, width, height, active);
            BuildRoomBanner(f, width);
            BuildLiveBanner(f, width, height);
            BuildToast(f, width);
        }

        /// <summary>
        /// The HUD in hide-UI mode (U): only the portal flash and important toasts (errors). The comment text box is
        /// drawn by the caller as usual.
        /// </summary>
        private void BuildHiddenHud(int width)
        {
            if (_clock < _flashUntil)
            {
                float t = (_flashUntil - _clock) / MathF.Max(_flashLength, 0.01f);
                _ui.Rect(0, 0, width, _window.Height, Rgba.WithAlpha(_flashColour, 0.35f * t));
            }
            if (_toastImportant) { BuildToast(_ui.Atlas, width); }
        }

        /// <summary>
        /// Top-left status: title, FPS, mode, level, ground, view.
        /// </summary>
        private void BuildStatusPanel(FontAtlas f)
        {
            float x = S(20), y = S(20), w = S(250), h = S(146);
            _ui.Panel(x, y, w, h, UiTheme.PANEL, UiTheme.PANEL_BORDER);
            float titleWidth = _ui.Text(f.Bold, x + S(14), y + S(11), "BIMGO", UiTheme.TEXT, S(2f));

            // Where edits go: LIVE (Revit session; OFFLINE when contact is lost) or FILE (standalone, * when unsaved)
            string badge;
            uint badgeColour;
            if (Source.IsRevit)
            {
                bool connected = _live == null || _live.Connected;
                badge = connected ? "LIVE" : "OFFLINE";
                badgeColour = connected ? UiTheme.ACCENT : UiTheme.DANGER;
            }
            else
            {
                badge = IsDirty ? "FILE*" : "FILE";
                badgeColour = IsDirty ? UiTheme.MEASURE_LABEL : UiTheme.GOOD;
            }
            _ui.Text(f.Small, x + S(14) + titleWidth + S(8), y + S(15), badge, badgeColour, S(1f));

            if (_showFps)
            {
                Text.Clear().Append(_fps, 0).Append(" fps · ").Append(_frameMs, 1).Append(" ms");
                _ui.TextRight(f.Mono, x + w - S(14), y + S(14), Text.Span, UiTheme.GOOD);
            }

            float labelX = x + S(14), valueX = x + S(14) + S(70);
            float rowY = y + S(40);
            float row = S(20);

            _ui.Text(f.Body, labelX, rowY, "MODE", UiTheme.TEXT_MUTED);
            _ui.Text(f.Body, valueX, rowY, _player.Flying ? "FLY" : _player.Controller.Crouching ? "CROUCH" : "WALK", UiTheme.TEXT);
            rowY += row;

            _ui.Text(f.Body, labelX, rowY, "LEVEL", UiTheme.TEXT_MUTED);
            if (Scene.Levels.Length > 0)
            {
                LevelInfo level = Scene.Levels[LevelIndexAt(_player.Feet.Z)];
                float used = _ui.Text(f.Body, valueX, rowY, level.Name, UiTheme.TEXT);
                Text.Clear().Append(level.Elevation, 3, plusSign: true);
                _ui.Text(f.Mono, valueX + used + S(6), rowY + S(1), Text.Span, UiTheme.TEXT_SOFT);
            }
            else
            {
                _ui.Text(f.Body, valueX, rowY, "—", UiTheme.TEXT);
            }
            rowY += row;

            _ui.Text(f.Body, labelX, rowY, "ROOM", UiTheme.TEXT_MUTED);
            if (CurrentRoom is RoomInfo room)
            {
                float used = _ui.Text(f.Mono, valueX, rowY + S(1), room.Number, UiTheme.ACCENT);
                _ui.TextWrapped(f.Body, valueX + used + S(8), rowY, w - (valueX - x) - used - S(22), room.Name, UiTheme.TEXT, maxLines: 1);
            }
            else
            {
                _ui.Text(f.Body, valueX, rowY, Scene.Rooms.Length == 0 ? "No rooms in this model" : "—", UiTheme.TEXT_MUTED);
            }
            rowY += row;

            _ui.Text(f.Body, labelX, rowY, "GROUND", UiTheme.TEXT_MUTED);
            Text.Clear().Append(_groundZ, 3).Append(" m");
            _ui.Text(f.Mono, valueX, rowY + S(1), Text.Span, UiTheme.TEXT);
            rowY += row;

            _ui.Text(f.Body, labelX, rowY, "VIEW", UiTheme.TEXT_MUTED);
            _ui.Text(f.Body, valueX, rowY, ColourModeLabel(), UiTheme.TEXT);
        }

        /// <summary>
        /// Bottom-left controls hint (F1 toggles).
        /// </summary>
        private void BuildHelp(FontAtlas f, int height)
        {
            float x = S(20);
            if (!_showHelp)
            {
                _ui.Text(f.Mono, x, height - S(36), "F1  Help", UiTheme.TEXT_MUTED);
                return;
            }

            // Columns sized to the widest key and action (measured once per UI scale)
            if (_helpScale != UiScale)
            {
                _helpScale = UiScale;
                _helpKeyWidth = 0f;
                _helpActionWidth = 0f;
                foreach ((string key, string action) in HELP_ROWS)
                {
                    _helpKeyWidth = MathF.Max(_helpKeyWidth, UiBatch.Measure(f.Mono, key));
                    _helpActionWidth = MathF.Max(_helpActionWidth, UiBatch.Measure(f.Body, action));
                }
            }

            float row = S(17);
            float h = HELP_ROWS.Length * row + S(20);
            float y = height - S(20) - h;
            float keyWidth = _helpKeyWidth + S(16);
            _ui.Panel(x, y, S(12) + keyWidth + _helpActionWidth + S(14), h, Rgba.Hex(0x0C0E12, 0.66f), Rgba.Hex(0xFFFFFF, 0.1f));

            float rowY = y + S(10);
            for (int i = 0; i < HELP_ROWS.Length; i++)
            {
                bool last = i == HELP_ROWS.Length - 1;
                _ui.Text(f.Mono, x + S(12), rowY, HELP_ROWS[i].Key, last ? UiTheme.TEXT_MUTED : UiTheme.TEXT);
                _ui.Text(f.Body, x + S(12) + keyWidth, rowY - S(1), HELP_ROWS[i].Action, last ? UiTheme.TEXT_MUTED : UiTheme.TEXT_SOFT);
                rowY += row;
            }
        }

        /// <summary>
        /// Bottom-centre gun bar: square symbol slots with the key number in the corner. The selected gun's
        /// name sits above the bar beside the LMB / RMB hints, so the bar stays compact as guns are added.
        /// </summary>
        private void BuildGunBar(FontAtlas f, int width, int height, Gun active)
        {
            float slot = S(52), gap = S(6), iconSize = S(30);
            float total = _guns.Length * slot + (_guns.Length - 1) * gap;
            float x = MathF.Round(width * 0.5f - total * 0.5f);
            float y = height - S(20) - slot;

            for (int i = 0; i < _guns.Length; i++)
            {
                Gun gun = _guns[i];
                bool selected = i == _activeGun;
                float bx = x + i * (slot + gap);
                _ui.Rect(bx, y, slot, slot, Rgba.Hex(0x0C0E12, selected ? 0.9f : 0.6f));
                _ui.Outline(bx, y, slot, slot, S(2), selected ? gun.Colour : Rgba.Hex(0xFFFFFF, 0.14f));
                gun.DrawIcon(_ui, bx + slot * 0.5f, y + slot * 0.5f + S(2), iconSize, selected ? gun.Colour : UiTheme.TEXT_MUTED);
                _ui.Text(f.Small, bx + S(5), y + S(3), gun.Key, selected ? gun.Colour : UiTheme.TEXT_FAINT);
            }

            // Selected gun name, then the hints, on one row above the bar
            float rowY = y - S(8) - S(24);
            float nameWidth = UiBatch.Measure(f.Bold, active.Name, S(1f)) + S(20);
            float lmbWidth = HintWidth(f, active.HintPrimary);
            float rmbWidth = HintWidth(f, active.HintSecondary);
            float rowWidth = nameWidth + S(10) + lmbWidth + S(10) + rmbWidth;
            float rowX = MathF.Round(width * 0.5f - rowWidth * 0.5f);

            _ui.Rect(rowX, rowY, nameWidth, S(24), Rgba.WithAlpha(active.Colour, 0.9f));
            _ui.Text(f.Bold, rowX + S(10), rowY + S(4), active.Name, UiTheme.SCAN_TAG_TEXT, S(1f));
            rowX += nameWidth + S(10);
            Hint(f, rowX, rowY, "LMB", active.HintPrimary, active.Colour);
            Hint(f, rowX + lmbWidth + S(10), rowY, "RMB", active.HintSecondary, active.Colour);

            // Revit write-back activity
            if (EditsPending > 0)
            {
                Text.Clear().Append("REVIT · ").Append(EditsPending).Append(" pending");
                float pendingWidth = UiBatch.Measure(f.Small, Text.Span) + S(16);
                _ui.Rect(width * 0.5f - pendingWidth * 0.5f, rowY - S(26), pendingWidth, S(20), Rgba.Hex(0x0C0E12, 0.7f));
                _ui.TextCentred(f.Small, width * 0.5f, rowY - S(22), Text.Span, UiTheme.MEASURE_LABEL);
            }
        }

        private float HintWidth(FontAtlas f, string text) =>
            UiBatch.Measure(f.Mono, "LMB") + S(6) + UiBatch.Measure(f.Body, text) + S(16);

        private void Hint(FontAtlas f, float x, float y, string button, string text, uint colour)
        {
            _ui.Rect(x, y, HintWidth(f, text), S(24), Rgba.Hex(0x0C0E12, 0.7f));
            float used = _ui.Text(f.Mono, x + S(8), y + S(5), button, colour);
            _ui.Text(f.Body, x + S(8) + used + S(6), y + S(4), text, Rgba.Hex(0xE5E7EB));
        }

        /// <summary>
        /// Top-centre banner when the player walks into a different room.
        /// </summary>
        private void BuildRoomBanner(FontAtlas f, int width)
        {
            if (CurrentRoom is not RoomInfo room || _clock >= _roomBannerUntil) { return; }
            float remaining = _roomBannerUntil - _clock;
            float alpha = Math.Clamp(remaining / 0.5f, 0f, 1f) * Math.Clamp((2.4f - remaining) / 0.2f, 0f, 1f);

            float numberWidth = UiBatch.Measure(f.Mono, room.Number) + S(20);
            float nameWidth = UiBatch.Measure(f.Bold, room.Name, S(1f)) + S(24);
            float x = MathF.Round(width * 0.5f - (numberWidth + nameWidth) * 0.5f), y = S(62), h = S(32);
            _ui.Rect(x, y, numberWidth, h, Rgba.WithAlpha(UiTheme.ACCENT, 0.92f * alpha));
            _ui.Text(f.Mono, x + S(10), y + S(8), room.Number, Rgba.WithAlpha(UiTheme.SCAN_TAG_TEXT, alpha));
            _ui.Rect(x + numberWidth, y, nameWidth, h, Rgba.WithAlpha(UiTheme.PANEL_STRONG, 0.88f * alpha));
            _ui.Text(f.Bold, x + numberWidth + S(12), y + S(7), room.Name, Rgba.WithAlpha(UiTheme.TEXT, alpha), S(1f));
        }

        /// <summary>
        /// Above the gun bar: Revit changes waiting for a refresh, or a refresh in progress (live sessions).
        /// </summary>
        private void BuildLiveBanner(FontAtlas f, int width, int height)
        {
            if (_live == null) { return; }

            uint colour;
            if (_live.Refreshing)
            {
                Text.Clear().Append("REVIT · EXTRACTING A FRESH SNAPSHOT…");
                colour = UiTheme.ACCENT;
            }
            else if (_live.ModelChanges > 0 && _live.Connected)
            {
                Text.Clear().Append("MODEL CHANGED IN REVIT (").Append(_live.ModelChanges).Append(_live.ModelChanges == 1 ? " CHANGE" : " CHANGES").Append(") · F5 REFRESH");
                colour = UiTheme.MEASURE_LABEL;
            }
            else
            {
                return;
            }

            ReadOnlySpan<char> text = Text.Span;
            float textWidth = UiBatch.Measure(f.Small, text, S(1f)) + S(24);
            float x = width * 0.5f - textWidth * 0.5f;
            float y = height - S(20) - S(52) - S(8) - S(24) - S(60);
            _ui.Rect(x, y, textWidth, S(24), Rgba.Hex(0x0C0E12, 0.8f));
            _ui.Outline(x, y, textWidth, S(24), MathF.Max(1f, UiScale), Rgba.WithAlpha(colour, 0.6f));
            _ui.TextCentred(f.Small, width * 0.5f, y + S(6), text, colour, S(1f));
        }

        /// <summary>
        /// Top-centre transient message.
        /// </summary>
        private void BuildToast(FontAtlas f, int width)
        {
            if (_toast == null || _clock >= _toastUntil) { return; }
            float remaining = _toastUntil - _clock;
            float alpha = Math.Clamp(remaining / 0.4f, 0f, 1f);
            float textWidth = UiBatch.Measure(f.Body, _toast);
            float w = textWidth + S(32), h = S(32);
            float x = width * 0.5f - w * 0.5f, y = S(20);
            _ui.Panel(x, y, w, h, Rgba.WithAlpha(UiTheme.PANEL_STRONG, 0.88f * alpha), Rgba.WithAlpha(UiTheme.ACCENT, 0.6f * alpha));
            _ui.Text(f.Body, x + S(16), y + S(7), _toast, Rgba.WithAlpha(UiTheme.TEXT, alpha));
        }

        #endregion
    }
}
