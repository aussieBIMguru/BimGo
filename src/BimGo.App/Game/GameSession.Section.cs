using System.Numerics;
using BimGo.Audio;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Vk = BimGo.Native.Win32;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Section box and quick plane (section box round).
    /// <list type="bullet">
    /// <item><b>P</b> opens the box editor (free cursor, player still, RMB-drag looks): the first time it fits the box to
    /// the level you stand on (model footprint, the level to just under the next one). Drag a face's handle along its
    /// axis (0.05 m steps; Shift: free). The panel switches the box and plane on / off, fits it again (level, room,
    /// model), sets the caps' flat colour (Windows colour picker; RESET = dark grey) and clears. P / Esc closes; the cut stays.</item>
    /// <item><b>Shift+P</b> cuts at the aimed surface: a plane parallel to it, 5 cm behind it, removing your side, so the
    /// room behind a wall (or the storey below a floor) opens up. Drag it in the editor.</item>
    /// <item><b>Ctrl+P</b> clears every cut.</item>
    /// </list>
    /// The cut is drawn by the scene shaders (and capped, <see cref="SceneRenderer.DrawSectionCaps"/>); picking and the
    /// guns skip what it removes, collision and shadows don't. Saved with the model (visibility), in bookmarks and
    /// comment views, and exchanged as BCF clipping planes.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>The cap colour (0xRRGGBB; remembered in settings) and its RGB 0–1 form.</summary>
        private uint _capRgb = 0x3D4045;
        private Vector3 _capColour = new(0x3D / 255f, 0x40 / 255f, 0x45 / 255f);

        /// <summary>A quick plane sits this far behind the aimed surface (m).</summary>
        private const float QUICK_PLANE_DEPTH = 0.05f;

        /// <summary>Handle drags snap to this step (m) unless Shift is held.</summary>
        private const float SECTION_SNAP = 0.05f;

        private SectionCut _section = new();
        private readonly Vector4[] _sectionPlanes = new Vector4[SectionCut.MAX_PLANES];
        private int _sectionCount;
        private bool _sectionOpen;
        private Overlay3D _sectionOverlay;
        private Vector4 _sectionPanelRect;
        private string _sectionNotice;

        // Handles: 0–5 box faces (+X, −X, +Y, −Y, +Z, −Z), 6 the free plane; -1 none
        private int _sectionHover = -1, _sectionDrag = -1;
        private Vector3 _dragAxis, _dragOrigin;
        private float _dragStartParameter, _dragStartValue;

        #endregion

        #region Cut

        /// <summary>
        /// Makes a cut current (copied): the shaders' planes, picking, and the saved visibility.
        /// </summary>
        /// <param name="cut">The cut (an inactive one clears).</param>
        /// <param name="announce">Say what changed (off for bookmarks and comment views).</param>
        private void ApplySection(SectionCut cut, bool announce = true)
        {
            _section = (cut ?? new SectionCut()).Clone().Clean();
            SectionChanged();
            if (announce) { Toast(_section.IsActive ? "Section cut on (P edits, Ctrl+P clears)" : "Section cut off", 3f); }
        }

        /// <summary>Recomputes the planes after any change and records it for saving.</summary>
        private void SectionChanged()
        {
            _sectionCount = _section.LocalPlanes(Scene.OriginOffset, _sectionPlanes);
            _renderer?.SetSection(_sectionPlanes, _sectionCount, _section.BoxOn);
            VisibilityChanged();
        }

        /// <summary>Restores the cut saved with the model (no save is triggered).</summary>
        private void RestoreSection(SectionCut saved)
        {
            if (saved == null || !saved.IsActive) { return; }
            _section = saved.Clone().Clean();
            _sectionCount = _section.LocalPlanes(Scene.OriginOffset, _sectionPlanes);
            _renderer?.SetSection(_sectionPlanes, _sectionCount, _section.BoxOn);
        }

        /// <summary>Ctrl+P / CLEAR ALL: no box, no plane.</summary>
        private void ClearSection()
        {
            if (!_section.IsActive)
            {
                Toast("Nothing is cut", 2f);
                return;
            }
            _section.BoxOn = false;
            _section.PlaneOn = false;
            SectionChanged();
            Sound.Play(SoundId.Remove);
            Toast("Section cut cleared", 2f);
        }

        /// <summary>
        /// Shift+P: a plane parallel to the aimed surface, a little behind it, cutting away the side you're on.
        /// </summary>
        private void QuickSectionPlane()
        {
            if (!_aim.HasHit)
            {
                Sound.Play(SoundId.Error);
                Toast("Aim at a surface to cut there (Shift+P)", 3f);
                return;
            }
            // The hit normal faces the eye: the cut-away side is the eye's. Follow the view ray through the aimed element
            // (a wall's thickness, a slab's depth) so the plane sits just behind it and the space beyond opens up.
            Vector3 normal = Vector3.Normalize(_aim.Hit.Normal);
            Vector3 exit = _aim.Hit.Point;
            for (int i = 0; i < 8; i++)
            {
                if (!_bvh.Raycast(exit + _aim.Direction * 0.002f, _aim.Direction, 1.5f, _pickMask, out RayHit next) || next.Element != _aim.Hit.Element) { break; }
                exit = next.Point;
            }
            float thickness = Math.Clamp(Vector3.Dot(_aim.Hit.Point - exit, normal), 0f, 1.5f);
            Vector3 point = _aim.Hit.Point - normal * (thickness + QUICK_PLANE_DEPTH);
            _section.PlaneOn = true;
            _section.PlanePoint = ToRevit(point);
            _section.PlaneNormal = normal;
            _section.Clean();
            SectionChanged();
            Sound.Play(SoundId.Click);
            Toast("Plane cut at the aimed surface (P to drag it, Ctrl+P clears)", 3f);
        }

        /// <summary>Fits the box to the level the player stands on: model footprint, level to just under the next.</summary>
        private void FitSectionToLevel()
        {
            Aabb bounds = Scene.Bounds;
            Vector3 min = bounds.Min - new Vector3(1f), max = bounds.Max + new Vector3(1f);
            if (Scene.Levels.Length > 0)
            {
                int level = LevelIndexAt(_player.Feet.Z);
                min.Z = Scene.Levels[level].Elevation - 1f;
                max.Z = level + 1 < Scene.Levels.Length ? Scene.Levels[level + 1].Elevation - 0.4f : bounds.Max.Z + 1f;
            }
            SetBoxLocal(min, max);
            _sectionNotice = Scene.Levels.Length > 0 ? "Box fitted to " + LevelNameAt(_player.Feet.Z) : "Box fitted to the model";
        }

        /// <summary>Fits the box to the room the player stands in (floor to top, plus a margin).</summary>
        private void FitSectionToRoom()
        {
            if (_roomIndex < 0 || _roomIndex >= Scene.Rooms.Length)
            {
                _sectionNotice = "You are not in a room";
                Sound.Play(SoundId.Error);
                return;
            }
            RoomInfo room = Scene.Rooms[_roomIndex];
            SetBoxLocal(new Vector3(room.Min.X - 0.5f, room.Min.Y - 0.5f, room.BottomZ - 0.5f), new Vector3(room.Max.X + 0.5f, room.Max.Y + 0.5f, room.TopZ + 0.2f));
            _sectionNotice = "Box fitted to " + RoomLabel(_roomIndex);
        }

        /// <summary>Fits the box to the whole model (nothing cut until a face moves).</summary>
        private void FitSectionToModel()
        {
            SetBoxLocal(Scene.Bounds.Min - new Vector3(1f), Scene.Bounds.Max + new Vector3(1f));
            _sectionNotice = "Box fitted to the model";
        }

        private void SetBoxLocal(Vector3 min, Vector3 max)
        {
            _section.BoxOn = true;
            _section.BoxMin = ToRevit(min);
            _section.BoxMax = ToRevit(max);
            _section.Clean();
            SectionChanged();
        }

        /// <summary>
        /// Like <see cref="Pick"/> against the static scene only, skipping cut geometry (the sun study's surface clicks).
        /// </summary>
        private bool PickStaticVisible(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit)
        {
            float travelled = 0f;
            for (int attempt = 0; attempt < 16 && travelled < maxDistance; attempt++)
            {
                if (!_bvh.Raycast(origin + direction * travelled, direction, maxDistance - travelled, _pickMask, out hit)) { return false; }
                if (_sectionCount == 0 || !SectionCut.IsCut(_sectionPlanes, _sectionCount, hit.Point))
                {
                    hit.Distance += travelled;
                    return true;
                }
                travelled += hit.Distance + 0.002f;
            }
            hit = default;
            return false;
        }

        #endregion

        #region Editor

        /// <summary>P: opens the box editor (fitting a box to the current level the first time).</summary>
        private void OpenSectionEditor()
        {
            if (_sectionOpen) { return; }
            if (_paused) { SetPaused(false); }
            CloseSunPanel();
            CloseSunHours();
            ClosePhotoMode();
            ShowUi();
            if (!_section.BoxOn && !_section.PlaneOn) { FitSectionToLevel(); }
            _sectionOpen = true;
            _sectionDrag = _sectionHover = -1;
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>Closes the editor (the cut stays).</summary>
        private void CloseSectionEditor()
        {
            if (!_sectionOpen) { return; }
            _sectionOpen = false;
            _sectionDrag = -1;
            _window.Input.ReleaseAll();
            if (!_paused && _window.IsActive) { _window.SetCaptured(true); }
        }

        /// <summary>
        /// Keys and mouse while the editor is open: P / Esc close, RMB-drag looks, LMB drags a handle along its axis.
        /// </summary>
        private void UpdateSectionEditor(InputState input)
        {
            if (input.IsPressed(Vk.VK_ESCAPE) || (input.IsPressed('P') && !input.IsDown(Vk.VK_CONTROL)))
            {
                CloseSectionEditor();
                return;
            }
            if (input.IsPressed(Vk.VK_F11)) { _window.ToggleFullscreen(); }
            if (input.IsPressed(Vk.VK_F12)) { RequestScreenshot(); }
            if (input.RightDown) { _player.Look(input.MouseDeltaX, input.MouseDeltaY, _sensitivity, _invertY); }

            Vector2 mouse = input.MousePosition;
            bool overPanel = mouse.X >= _sectionPanelRect.X && mouse.X < _sectionPanelRect.X + _sectionPanelRect.Z
                && mouse.Y >= _sectionPanelRect.Y && mouse.Y < _sectionPanelRect.Y + _sectionPanelRect.W;

            if (_sectionDrag >= 0)
            {
                if (!input.LeftDown)
                {
                    _sectionDrag = -1;
                    Sound.Play(SoundId.Click);
                    return;
                }
                DragHandle(mouse, input.IsDown(Vk.VK_SHIFT));
                return;
            }

            _sectionHover = overPanel ? -1 : HandleUnder(mouse);
            if (input.LeftPressed && _sectionHover >= 0)
            {
                input.ConsumeClicks();
                BeginDrag(_sectionHover, mouse);
            }
        }

        /// <summary>The handle nearest the mouse (within 18 px), or -1.</summary>
        private int HandleUnder(Vector2 mouse)
        {
            int best = -1;
            float bestDistance = S(18);
            for (int h = 0; h < 7; h++)
            {
                if (!HandlePosition(h, out Vector3 position, out _)) { continue; }
                if (!Camera.WorldToScreen(position, out Vector2 screen)) { continue; }
                float distance = Vector2.Distance(screen, mouse);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = h;
                }
            }
            return best;
        }

        /// <summary>A handle's position (scene-local) and its axis (outward for box faces, the plane's normal).</summary>
        private bool HandlePosition(int handle, out Vector3 position, out Vector3 axis)
        {
            position = axis = Vector3.Zero;
            Vector3 origin = Scene.OriginOffset;
            if (handle < 6)
            {
                if (!_section.BoxOn) { return false; }
                Vector3 min = _section.BoxMin - origin, max = _section.BoxMax - origin;
                Vector3 centre = (min + max) * 0.5f;
                int a = handle / 2;
                bool positive = handle % 2 == 0;
                position = centre;
                position[a] = positive ? max[a] : min[a];
                axis = Vector3.Zero;
                axis[a] = positive ? 1f : -1f;
                return true;
            }
            if (!_section.PlaneOn) { return false; }
            position = _section.PlanePoint - origin;
            axis = _section.PlaneNormal;
            return true;
        }

        private void BeginDrag(int handle, Vector2 mouse)
        {
            if (!HandlePosition(handle, out Vector3 position, out Vector3 axis)) { return; }
            ScreenRay(mouse, out Vector3 rayOrigin, out Vector3 rayDirection);
            if (!ClosestOnAxis(position, axis, rayOrigin, rayDirection, out float parameter)) { return; }
            _sectionDrag = handle;
            _dragAxis = axis;
            _dragOrigin = position;
            _dragStartParameter = parameter;
            _dragStartValue = 0f;
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>
        /// Moves the dragged face (or plane) to where the mouse ray passes closest to its axis, snapped unless free.
        /// </summary>
        private void DragHandle(Vector2 mouse, bool free)
        {
            ScreenRay(mouse, out Vector3 rayOrigin, out Vector3 rayDirection);
            if (!ClosestOnAxis(_dragOrigin, _dragAxis, rayOrigin, rayDirection, out float parameter)) { return; }
            float delta = parameter - _dragStartParameter;
            if (!free) { delta = MathF.Round(delta / SECTION_SNAP) * SECTION_SNAP; }
            if (MathF.Abs(delta - _dragStartValue) < 1e-5f) { return; }
            _dragStartValue = delta;

            Vector3 moved = _dragOrigin + _dragAxis * delta;
            Vector3 world = ToRevit(moved);
            if (_sectionDrag < 6)
            {
                int a = _sectionDrag / 2;
                Vector3 min = _section.BoxMin, max = _section.BoxMax;
                if (_sectionDrag % 2 == 0) { max[a] = MathF.Max(world[a], min[a] + SectionCut.MIN_SIZE); }
                else { min[a] = MathF.Min(world[a], max[a] - SectionCut.MIN_SIZE); }
                _section.BoxMin = min;
                _section.BoxMax = max;
            }
            else
            {
                _section.PlanePoint = world;
            }
            SectionChanged();
        }

        /// <summary>
        /// The parameter along an axis (point + s · axis) where a ray passes closest; false when they are near parallel.
        /// </summary>
        private static bool ClosestOnAxis(Vector3 point, Vector3 axis, Vector3 rayOrigin, Vector3 rayDirection, out float parameter)
        {
            parameter = 0f;
            Vector3 w = point - rayOrigin;
            float b = Vector3.Dot(axis, rayDirection);
            float d = Vector3.Dot(axis, w), e = Vector3.Dot(rayDirection, w);
            float denominator = 1f - b * b;
            if (denominator < 1e-4f) { return false; }
            parameter = (b * e - d) / denominator;
            return float.IsFinite(parameter);
        }

        /// <summary>CHANGE…: the Windows colour picker for the caps.</summary>
        private void PickCapColour()
        {
            _window.Input.ReleaseAll();
            uint? picked = FileDialogs.PickColour(_window.Handle, _capRgb);
            _window.Input.ReleaseAll();
            if (picked.HasValue) { SetCapColour(picked.Value); }
        }

        /// <summary>Sets the cap colour (0xRRGGBB); saved with the settings.</summary>
        private void SetCapColour(uint rgb)
        {
            _capRgb = rgb & 0xFFFFFF;
            _capColour = new Vector3(((_capRgb >> 16) & 0xFF) / 255f, ((_capRgb >> 8) & 0xFF) / 255f, (_capRgb & 0xFF) / 255f);
            _sectionNotice = $"Cap colour #{_capRgb:X6}";
        }

        #endregion

        #region Drawing

        /// <summary>
        /// The cut's frame and handles while the editor is open (3D pass, after the scene): box edges, face handles
        /// (the hovered or dragged one highlighted) and the plane's outline and handle.
        /// </summary>
        private void DrawSectionGizmo()
        {
            if (!_sectionOpen || !_section.IsActive) { return; }
            if (_sectionOverlay == null)
            {
                _sectionOverlay = new Overlay3D();
                _sectionOverlay.Initialise();
            }
            _sectionOverlay.Begin(Camera);
            Vector3 origin = Scene.OriginOffset;
            uint edge = Rgba.Hex(0x22D3EE, 0.9f);
            if (_section.BoxOn)
            {
                Vector3 min = _section.BoxMin - origin, max = _section.BoxMax - origin;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 a = Corner(min, max, i, false), b = Corner(min, max, (i + 1) % 4, false);
                    Vector3 c = Corner(min, max, i, true), d = Corner(min, max, (i + 1) % 4, true);
                    _sectionOverlay.Line(a, b, 2f, edge);
                    _sectionOverlay.Line(c, d, 2f, edge);
                    _sectionOverlay.Line(a, c, 2f, edge);
                }
            }
            if (_section.PlaneOn)
            {
                Vector3 p = _section.PlanePoint - origin, n = _section.PlaneNormal;
                Vector3 t = MathF.Abs(n.Z) < 0.9f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n)) : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, n));
                Vector3 s = Vector3.Cross(n, t);
                _sectionOverlay.Ring(p, t, s, 1.5f, 1.5f, 0.03f, Rgba.Hex(0xFBBF24, 0.9f));
                _sectionOverlay.Line(p, p + n * 0.8f, 3f, Rgba.Hex(0xFBBF24, 0.9f));
            }
            for (int h = 0; h < 7; h++)
            {
                if (!HandlePosition(h, out Vector3 position, out _)) { continue; }
                bool active = h == _sectionDrag || (h == _sectionHover && _sectionDrag < 0);
                uint colour = active ? Rgba.Hex(0xFFFFFF, 1f) : h == 6 ? Rgba.Hex(0xFBBF24, 1f) : Rgba.Hex(0x22D3EE, 1f);
                _sectionOverlay.Dot(position, active ? 11f : 8f, colour);
            }
            _sectionOverlay.Draw(Camera, depthTest: false, alpha: 1f, additive: false);

            static Vector3 Corner(Vector3 min, Vector3 max, int i, bool top) => new(
                i == 1 || i == 2 ? max.X : min.X,
                i >= 2 ? max.Y : min.Y,
                top ? max.Z : min.Z);
        }

        /// <summary>
        /// The editor's panel (top right): what's cut, fit buttons, box / plane switches, cap colour, clear, close.
        /// </summary>
        private void BuildSectionPanel(FontAtlas f, InputState input)
        {
            if (!_sectionOpen) { return; }
            float w = S(300), x = _window.Width - S(20) - w, y = S(20), h = S(372);
            _sectionPanelRect = new Vector4(x, y, w, h);
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.ACCENT);
            float ix = x + S(16), iw = w - S(32), cy = y + S(14);
            _ui.Text(f.Small, ix, cy, "SECTION BOX", UiTheme.ACCENT, S(1.4f));
            cy += S(26);
            _ui.TextWrapped(f.Small, ix, cy, iw, "Drag a handle along its arrow (Shift: no snap) · RMB-drag to look", UiTheme.TEXT_MUTED, maxLines: 2);
            cy += S(38);

            float half = (iw - S(8)) * 0.5f;
            if (SmallButton(f, input, ix, cy, half, S(30), _section.BoxOn ? "BOX: ON" : "BOX: OFF"))
            {
                if (_section.BoxOn) { _section.BoxOn = false; SectionChanged(); }
                else { FitSectionToLevel(); }
            }
            if (SmallButton(f, input, ix + half + S(8), cy, half, S(30), _section.PlaneOn ? "PLANE: ON" : "PLANE: OFF"))
            {
                if (_section.PlaneOn) { _section.PlaneOn = false; SectionChanged(); }
                else { _sectionNotice = "Aim at a surface and press Shift+P for a plane"; }
            }
            cy += S(40);

            _ui.Text(f.Small, ix, cy, "FIT THE BOX TO", UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(20);
            float third = (iw - S(16)) / 3f;
            if (SmallButton(f, input, ix, cy, third, S(30), "LEVEL")) { FitSectionToLevel(); }
            if (SmallButton(f, input, ix + third + S(8), cy, third, S(30), "ROOM")) { FitSectionToRoom(); }
            if (SmallButton(f, input, ix + (third + S(8)) * 2, cy, third, S(30), "MODEL")) { FitSectionToModel(); }
            cy += S(42);

            _ui.Text(f.Small, ix, cy, "CAPS", UiTheme.TEXT_MUTED, S(0.6f));
            cy += S(20);
            // Swatch, then CHANGE… (the Windows colour picker) and RESET (dark grey)
            _ui.Rect(ix, cy, S(44), S(30), Rgba.FromFloat(_capColour.X, _capColour.Y, _capColour.Z, 1f));
            _ui.Outline(ix, cy, S(44), S(30), MathF.Max(1f, UiScale), UiTheme.CONTROL_BORDER);
            float buttonW = (iw - S(60) - S(8)) * 0.5f;
            if (SmallButton(f, input, ix + S(52), cy, buttonW, S(30), "CHANGE…")) { PickCapColour(); }
            if (SmallButton(f, input, ix + S(52) + buttonW + S(8), cy, buttonW, S(30), "RESET")) { SetCapColour(0x3D4045); }
            cy += S(42);

            if (SmallButton(f, input, ix, cy, iw, S(30), "CLEAR ALL CUTS (Ctrl+P)", danger: true)) { ClearSection(); }
            cy += S(40);
            if (_sectionNotice != null) { _ui.TextWrapped(f.Small, ix, cy, iw, _sectionNotice, UiTheme.MEASURE_TEXT, maxLines: 2); }
            if (SmallButton(f, input, ix, y + h - S(44), iw, S(30), "CLOSE (P / ESC) · cut stays")) { CloseSectionEditor(); }
        }

        #endregion
    }
}
