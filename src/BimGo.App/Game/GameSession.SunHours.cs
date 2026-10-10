using System.Globalization;
using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Rendering;
using BimGo.Scene;
using Vk = BimGo.Native.Win32;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// The study panel (sun hours round; daylight factor and illuminance modes since the daylight round). J (or pause
    /// menu → SUN HOURS) opens the study panel: the player
    /// stands still, the cursor is free, RMB-drag looks around. The walls and floors of the room you stand in are
    /// selected; clicking a surface adds or removes it. RUN casts a ray towards the sun from every grid cell every
    /// <see cref="SunHoursSettings.StepMinutes"/> minutes over the chosen time range and colours the cells on
    /// Ladybug's 0–7 h legend. Results stay (also with the panel closed) until CLEAR or the session ends; EXPORT CSV
    /// and SCREENSHOT (with the legend) keep them.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        private static readonly string[] GRID_OPTIONS = { "0.1", "0.25", "0.5", "1 m" };
        private static readonly string[] STEP_OPTIONS = { "5 min", "10 min", "15 min" };
        private static readonly string[] MODE_OPTIONS = { "Sun hours", "Daylight %", "Lux" };
        private static readonly string[] RAY_OPTIONS = { "128", "256", "512" };
        private static readonly string[] REFLECTANCE_OPTIONS = { "From colours", "Standard" };

        /// <summary>Ray casting time per frame while a study runs (ms).</summary>
        private const double SUN_BUDGET_MS = 10.0;

        private bool _sunHoursOpen;
        private SunHoursSettings _sunHours = new();
        private readonly SunHoursStudy _sunStudy = new();
        private readonly List<SunHoursFace> _sunFaces = new();
        private int _sunFacesRoom = -1;
        private bool _sunGridDirty = true;
        private Overlay3D _sunOverlay;
        private int _sunOverlayRevision = -1;
        private bool _sunOpaqueOnly = true;
        private Func<Vector3, Vector3, float, bool> _sunBlocked;
        private Vector4 _sunPanelRect;
        private bool _sunShotRequested;
        private string _sunNotice;
        private string _sunSummary;
        private string _sunLegendTitle;
        private bool _sunStale;

        // Daylight: the rooms' figures from the last run, for the summary and the CSV
        private RoomLight[] _sunRoomLight;
        private string _sunRoomNote;

        // Pass / fail (sun hours round 2): legend labels cached when results or the target change
        private string _sunPassLabel, _sunFailLabel;
        private int _sunOverlayColourKey = -1;

        // Saved studies (sun hours round 2): the list view, its rows and the armed DELETE
        private bool _sunStudiesView;
        private List<SunStudyInfo> _sunStudyList = new();
        private List<string> _sunStudyDates = new();
        private string _sunStudyDeleteArmed;
        private float _sunStudyDeleteArmedUntil;
        private int _sunStudyScroll;

        /// <summary>True while the text box asks for a study name.</summary>
        private bool _editSunStudy;

        #endregion

        #region Open / close

        /// <summary>True while the study panel is open (cursor free, player still).</summary>
        private bool IsSunHoursOpen => _sunHoursOpen;

        /// <summary>
        /// Opens the study panel. The first time (or when the selection is empty) it selects the room you stand in.
        /// </summary>
        private void OpenSunHours()
        {
            if (_sunHoursOpen) { return; }
            if (_paused) { SetPaused(false); }
            CloseSunPanel();
            CloseSectionEditor();
            ClosePhotoMode();
            ShowUi();
            _sunHoursOpen = true;
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            Sound.Play(SoundId.UiClick);
            _sunBlocked ??= SunRayBlocked;
            _sunHours.DaylightSaving = _sun?.Time?.DaylightSaving ?? _sunHours.DaylightSaving;
            if (_sunFaces.Count == 0) { SelectRoomFaces(_roomIndex); }
        }

        /// <summary>
        /// Closes the panel (a finished study stays on screen with its legend; a running one keeps running).
        /// </summary>
        private void CloseSunHours()
        {
            if (!_sunHoursOpen) { return; }
            _sunHoursOpen = false;
            _window.Input.ReleaseAll();
            if (!_paused && _window.IsActive) { _window.SetCaptured(true); }
        }

        /// <summary>
        /// Keys and mouse while the panel is open: Esc / J close, RMB-drag looks, a click on the model (outside the
        /// panel) adds or removes that surface.
        /// </summary>
        private void UpdateSunHoursMode(InputState input)
        {
            if (input.IsPressed(Vk.VK_ESCAPE) || input.IsPressed('J'))
            {
                CloseSunHours();
                return;
            }
            if (input.IsPressed(Vk.VK_F11)) { _window.ToggleFullscreen(); }
            if (input.IsPressed(Vk.VK_F12)) { RequestScreenshot(); }
            if (input.RightDown) { _player.Look(input.MouseDeltaX, input.MouseDeltaY, _sensitivity, _invertY); }

            Vector2 m = input.MousePosition;
            bool overPanel = m.X >= _sunPanelRect.X && m.X < _sunPanelRect.X + _sunPanelRect.Z && m.Y >= _sunPanelRect.Y && m.Y < _sunPanelRect.Y + _sunPanelRect.W;
            if (input.LeftPressed && !overPanel && !_sunStudy.Running)
            {
                input.ConsumeClicks();
                PickSunFace(m);
            }
        }

        /// <summary>
        /// Per frame: rebuilds the grid after a change, runs the study a few milliseconds, reports the end.
        /// </summary>
        private void UpdateSunStudy()
        {
            if (_sunGridDirty)
            {
                _sunGridDirty = false;
                _sunStudy.Build(_sunFaces, _sunHours.Clean(DateTime.Today.Year), Scene.Rooms);
                _sunSummary = null;
                _sunStale = false;
                if (_sunStudy.Truncated) { _sunNotice = $"Over {SunHoursStudy.MAX_CELLS:N0} cells: pick a larger grid or fewer surfaces"; }
            }
            if (!_sunStudy.Running) { return; }

            _sunStudy.Step(_sunBlocked, SUN_BUDGET_MS);
            if (_sunStudy.Finished)
            {
                RefreshSunSummary();
                Utilities.Log_Utils.Write($"{ModeName(_sunStudy.Mode)}: {_sunStudy.CellCount:N0} cells, {_sunStudy.SunSamples} time samples, {_sunStudy.Elapsed.TotalSeconds:0.0} s. {_sunSummary}.");
                Sound.Play(SoundId.Commit);
                Toast(ModeName(_sunStudy.Mode) + " study done: " + _sunSummary, 4f);
            }
        }

        /// <summary>
        /// The results' summary and the pass / fail legend labels (after a run, a load, or a target change; never per
        /// frame).
        /// </summary>
        private void RefreshSunSummary()
        {
            _sunPassLabel = _sunFailLabel = null;
            if (_sunStudy.Hours == null || _sunStudy.Done == 0 || _sunStudy.Running)
            {
                _sunSummary = null;
                return;
            }

            (float average, float min, float max, float two, float three) = _sunStudy.Statistics();
            CultureInfo inv = CultureInfo.InvariantCulture;
            string target;
            switch (_sunStudy.Mode)
            {
                case StudyMode.DaylightFactor:
                    _sunSummary = $"Average DF {average:0.0} % · min {min:0.0} · max {max:0.0}" + (_sunRoomNote == null ? string.Empty : " · " + _sunRoomNote);
                    target = $"DF ≥ {_sunHours.FactorTarget.ToString("0.#", inv)} %";
                    break;
                case StudyMode.Illuminance:
                    _sunSummary = $"Average {average:N0} lux · min {min:N0} · max {max:N0}" + (_sunRoomNote == null ? string.Empty : " · " + _sunRoomNote);
                    target = $"≥ {(_sunStudy.RunSettings?.LuxTarget ?? _sunHours.LuxTarget).ToString("0", inv)} lux for {_sunHours.LuxShare:P0} of the time";
                    break;
                default:
                    _sunSummary = $"Average {average:0.0} h · min {min:0.0} · max {max:0.0} · {two:P0} ≥ 2 h · {three:P0} ≥ 3 h";
                    target = $"≥ {_sunHours.TargetHours.ToString("0.##", inv)} h";
                    break;
            }
            if (_sunHours.PassFail)
            {
                float pass = _sunStudy.PassShare(_sunHours);
                _sunSummary += $" · pass ({target}): {pass:P0} of the area";
                _sunPassLabel = $"PASS {target} · {pass:P0}";
                _sunFailLabel = $"FAIL · {1f - pass:P0}";
            }
        }

        /// <summary>"Sun hours", "Daylight factor", "Illuminance".</summary>
        private static string ModeName(StudyMode mode) => mode switch
        {
            StudyMode.DaylightFactor => "Daylight factor",
            StudyMode.Illuminance => "Illuminance",
            _ => "Sun hours"
        };

        /// <summary>PASS / FAIL TEST: on shows the mode's test (green / red), off the values on their legend.</summary>
        private void TogglePassFail()
        {
            _sunHours.Target = _sunHours.PassFail ? SunTarget.Off : SunTarget.On;
            RefreshSunSummary();
            _sunNotice = _sunHours.PassFail ? "Pass / fail test on: cells pass (green) or fail (red) the target" : "Pass / fail test off: cells show their values";
        }

        /// <summary>
        /// Mode selector: switches what the study computes. Results go; the room is selected again for the mode
        /// (daylight modes test its floor at the work plane; sun hours its walls and floors).
        /// </summary>
        private void SetStudyMode(StudyMode mode)
        {
            if (mode == _sunHours.Mode) { return; }
            _sunStudy.Cancel();
            _sunHours.Mode = mode;
            SelectRoomFaces(_sunFacesRoom >= 0 ? _sunFacesRoom : _roomIndex);
            _sunSummary = null;
            _sunPassLabel = _sunFailLabel = null;
            _sunLegendTitle = null;
            _sunNotice = mode switch
            {
                StudyMode.DaylightFactor => "Daylight factor: overcast sky, no date or time. An early design indicator, not a compliance tool",
                StudyMode.Illuminance => "Illuminance: clear sky (+ sun) over the day's times. An early design indicator, not a compliance tool",
                _ => null
            };
        }

        /// <summary>The legend title for a run ("DIRECT SUN HOURS · 21 Jun 09:00–15:00 · 5 min…").</summary>
        private string SunLegendTitle(SunHoursSettings settings, string studyName = null)
        {
            string name = string.IsNullOrEmpty(studyName) ? string.Empty : " · " + studyName.ToUpperInvariant();
            string times = $"{settings.Day} {MONTHS[settings.Month - 1]} {Clock(settings.StartMinutes)}–{Clock(settings.EndMinutes)} · {settings.StepMinutes} min";
            return settings.Mode switch
            {
                StudyMode.DaylightFactor => $"DAYLIGHT FACTOR · CIE OVERCAST SKY · WORK PLANE {settings.WorkPlane:0.00} m{name}",
                StudyMode.Illuminance => $"ILLUMINANCE · CLEAR SKY{(settings.DirectSun ? " + SUN" : string.Empty)} · {times}{name}",
                _ => $"DIRECT SUN HOURS · {times}{(settings.GlassBlocks ? " · glass blocks" : string.Empty)}{name}"
            };
        }

        /// <summary>
        /// True when a ray from a test point towards the sun hits visible geometry (glass passes unless it blocks).
        /// </summary>
        private bool SunRayBlocked(Vector3 origin, Vector3 direction, float distance)
        {
            if (_bvh.Raycast(origin, direction, distance, _pickMask, out RayHit _, _sunOpaqueOnly)) { return true; }
            return Dynamics != null && Dynamics.Raycast(origin, direction, distance, out RayHit _, null, _sunOpaqueOnly);
        }

        #endregion

        #region Targets

        /// <summary>
        /// Selects the walls and floors of a room (replacing the selection): every opaque wall or floor triangle near
        /// the room, grouped into faces by element and plane; the grid keeps only the cells inside the room.
        /// </summary>
        private void SelectRoomFaces(int roomIndex)
        {
            _sunFaces.Clear();
            _sunFacesRoom = roomIndex;
            _sunGridDirty = true;
            if (roomIndex < 0 || roomIndex >= Scene.Rooms.Length)
            {
                _sunNotice = "You are not in a room: click walls or floors to test them";
                return;
            }

            RoomInfo room = Scene.Rooms[roomIndex];
            int walls = CategoryCatalog.Find("walls")?.Index ?? -1, floors = CategoryCatalog.Find("floors")?.Index ?? -1;
            var box = new Aabb(new Vector3(room.Min - new Vector2(0.6f), room.BottomZ - 0.5f), new Vector3(room.Max + new Vector2(0.6f), room.TopZ + 0.5f));
            var byKey = new Dictionary<(int, int, int, int, int), SunHoursFace>();
            ElementRecord[] elements = Scene.Elements;
            SceneVertex[] vertices = Scene.Vertices;
            uint[] indices = Scene.Indices;

            for (int e = 0; e < elements.Length; e++)
            {
                ElementRecord record = elements[e];
                bool isWall = record.CategoryIndex == walls && _sunHours.Mode == StudyMode.SunHours; // daylight: the floor only
                bool isFloor = record.CategoryIndex == floors;
                if ((!isWall && !isFloor) || !_pickMask[e] || !record.Bounds.Overlaps(box)) { continue; }

                for (int i = record.OpaqueStart; i + 2 < record.OpaqueStart + record.OpaqueCount; i += 3)
                {
                    Vector3 a = vertices[indices[i]].Position, b = vertices[indices[i + 1]].Position, c = vertices[indices[i + 2]].Position;
                    Vector3 cross = Vector3.Cross(b - a, c - a);
                    if (cross.LengthSquared() < 1e-10f) { continue; }
                    Vector3 n = Vector3.Normalize(cross);
                    Vector3 centre = (a + b + c) / 3f;

                    bool horizontal = MathF.Abs(n.Z) > 0.9f;
                    if (horizontal)
                    {
                        // Floor tops at the room's floor (the slab's underside belongs to the room below)
                        if (MathF.Abs(centre.Z - room.BottomZ) > 0.15f) { continue; }
                        n = Vector3.UnitZ;
                    }
                    else if (MathF.Abs(n.Z) < 0.2f)
                    {
                        // Walls: one sign per plane (each cell then faces into the room)
                        if (n.X < -1e-4f || (MathF.Abs(n.X) <= 1e-4f && n.Y < 0f)) { n = -n; }
                    }
                    else { continue; }

                    float offset = Vector3.Dot(n, centre);
                    var key = (e, (int)MathF.Round(n.X * 50f), (int)MathF.Round(n.Y * 50f), (int)MathF.Round(n.Z * 50f), (int)MathF.Round(offset / 0.02f));
                    if (!byKey.TryGetValue(key, out SunHoursFace face))
                    {
                        face = new SunHoursFace { Element = e, Normal = n, Offset = offset, Room = roomIndex, BothSides = !horizontal };
                        byKey[key] = face;
                        _sunFaces.Add(face);
                    }
                    face.Triangles.Add((a, b, c));
                }
            }
            _sunNotice = _sunFaces.Count == 0 ? "No walls or floors found around this room: click surfaces to test them" : null;
        }

        /// <summary>
        /// A click on the model: removes the surface under the cursor when it is selected, else adds it (the element's
        /// triangles in that plane, tested on the side facing you, clipped to the room on that side if there is one).
        /// </summary>
        private void PickSunFace(Vector2 mouse)
        {
            ScreenRay(mouse, out Vector3 origin, out Vector3 direction);
            bool hitStatic = PickStaticVisible(origin, direction, 300f, out RayHit hit);
            if (Dynamics != null && Dynamics.Raycast(origin, direction, hitStatic ? hit.Distance : 300f, out RayHit _))
            {
                Sound.Play(SoundId.Error);
                _sunNotice = "Moved or placed elements can't be tested (they still cast shade)";
                return;
            }
            if (!hitStatic)
            {
                Sound.Play(SoundId.Error);
                return;
            }

            Vector3 n = hit.Normal;
            float offset = Vector3.Dot(n, hit.Point);
            for (int i = 0; i < _sunFaces.Count; i++)
            {
                if (_sunFaces[i].SamePlane(hit.Element, n, offset))
                {
                    _sunFaces.RemoveAt(i);
                    _sunGridDirty = true;
                    Sound.Play(SoundId.Remove);
                    _sunNotice = "Surface removed";
                    return;
                }
            }

            // The element's triangles in the clicked plane
            ElementRecord record = Scene.Elements[hit.Element];
            var face = new SunHoursFace { Element = hit.Element, Normal = n, Offset = offset, Picked = true };
            SceneVertex[] vertices = Scene.Vertices;
            uint[] indices = Scene.Indices;
            for (int i = record.OpaqueStart; i + 2 < record.OpaqueStart + record.OpaqueCount; i += 3)
            {
                Vector3 a = vertices[indices[i]].Position, b = vertices[indices[i + 1]].Position, c = vertices[indices[i + 2]].Position;
                if (MathF.Abs(Vector3.Dot(n, a) - offset) > 0.02f || MathF.Abs(Vector3.Dot(n, b) - offset) > 0.02f || MathF.Abs(Vector3.Dot(n, c) - offset) > 0.02f) { continue; }
                face.Triangles.Add((a, b, c));
            }
            if (face.Triangles.Count == 0)
            {
                Sound.Play(SoundId.Error);
                _sunNotice = "That surface isn't flat enough to test (pick a wall, floor or bench top)";
                return;
            }

            // Clipped to the room it faces (a slab or a long wall otherwise covers the whole level)
            face.Room = FindRoom(hit.Point + n * 0.15f);
            _sunFaces.Add(face);
            _sunGridDirty = true;
            Sound.Play(SoundId.Click);
            _sunNotice = $"Added: {record.Name}{(face.Room >= 0 ? " (in " + RoomLabel(face.Room) + ")" : string.Empty)}";
        }

        /// <summary>The world ray under a window pixel.</summary>
        private void ScreenRay(Vector2 pixel, out Vector3 origin, out Vector3 direction)
        {
            float x = pixel.X / Math.Max(1, _window.Width) * 2f - 1f;
            float y = 1f - pixel.Y / Math.Max(1, _window.Height) * 2f;
            Matrix4x4 inverse = Camera.InverseViewProjection;
            Vector4 near = Vector4.Transform(new Vector4(x, y, -1f, 1f), inverse);
            Vector4 far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
            Vector3 a = new Vector3(near.X, near.Y, near.Z) / near.W;
            Vector3 b = new Vector3(far.X, far.Y, far.Z) / far.W;
            origin = Camera.Position;
            direction = Vector3.Normalize(b - a);
        }

        /// <summary>"2.05 Kitchen" (number and name).</summary>
        private string RoomLabel(int room)
        {
            if (room < 0 || room >= Scene.Rooms.Length) { return "no room"; }
            RoomInfo r = Scene.Rooms[room];
            return string.IsNullOrWhiteSpace(r.Number) || r.Number == "—" ? r.Name : $"{r.Number} {r.Name}";
        }

        #endregion

        #region Run, clear, export

        /// <summary>
        /// RUN: the sun positions over the range (above the horizon), then the time-sliced ray casting.
        /// </summary>
        private void RunSunStudy()
        {
            if (_sunGridDirty) { UpdateSunStudy(); }
            if (_sunStudy.CellCount == 0)
            {
                Sound.Play(SoundId.Error);
                _sunNotice = "Nothing to test: select a room (THIS ROOM) or click surfaces";
                return;
            }

            SunHoursSettings settings = _sunHours.Clean(DateTime.Today.Year);
            if (settings.Mode != StudyMode.SunHours)
            {
                RunDaylightStudy(settings);
                return;
            }
            _sunRoomNote = null;
            _sunRoomLight = null;
            List<Vector3> directions = SunHours.SunDirections(Scene.Site, DateTime.Today.Year, settings, out int total, out bool known);
            _sunOpaqueOnly = !settings.GlassBlocks;
            _sunStudy.Start(directions, total, settings);
            _sunSummary = null;
            _sunStale = false;
            _sunLegendTitle = SunLegendTitle(settings);
            _sunNotice = directions.Count == 0 ? "The sun is below the horizon for the whole range: every cell gets 0 h"
                : known ? null : "No site location in this model: Sydney assumed (set Revit's Location)";
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>CLEAR RESULTS: the grid stays selected, the colours go.</summary>
        private void ClearSunResults()
        {
            _sunStudy.Cancel();
            _sunGridDirty = true;
            _sunSummary = null;
            _sunPassLabel = _sunFailLabel = null;
            _sunNotice = "Results cleared";
        }

        /// <summary>
        /// Writes the study to a CSV file: the settings, then one row per cell (surface, element, point and normal in
        /// Revit internal metres, hours).
        /// </summary>
        private void ExportSunHours()
        {
            if (_sunStudy.Hours == null || _sunStudy.Done == 0) { return; }
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            string name = Path.GetFileNameWithoutExtension(DocumentName) + " " + ModeName(_sunStudy.Mode).ToLowerInvariant() + ".csv";
            string path = FileDialogs.ShowSave(_window.Handle, "Export " + ModeName(_sunStudy.Mode).ToLowerInvariant(), "CSV file (*.csv)|*.csv|All files (*.*)|*.*", SuggestedFolder(), name, ".csv");
            if (path == null) { return; }

            try
            {
                SunHoursSettings s = _sunStudy.RunSettings;
                CultureInfo inv = CultureInfo.InvariantCulture;
                StudyMode mode = _sunStudy.Mode;
                bool test = _sunHours.PassFail;
                var lines = new List<string> { "BimGo " + ModeName(mode).ToLowerInvariant(), $"Model,{Csv(Scene.ModelTitle)}" };
                if (mode != StudyMode.DaylightFactor)
                {
                    lines.Add($"Date,{s.Day} {MONTHS[s.Month - 1]}");
                    lines.Add($"From,{Clock(s.StartMinutes)}");
                    lines.Add($"To,{Clock(s.EndMinutes)}");
                    lines.Add($"Step (min),{s.StepMinutes}");
                    lines.Add($"Daylight saving,{(s.DaylightSaving ? "yes" : "no")}");
                    lines.Add($"Time samples (sun up),{_sunStudy.SunSamples} of {_sunStudy.TotalSamples}");
                }
                if (mode == StudyMode.SunHours)
                {
                    lines.Add($"Glass,{(s.GlassBlocks ? "blocks sun" : "lets sun through")}");
                    lines.Add($"Floor offset (m),{_sunStudy.Settings.FloorOffset.ToString(inv)}");
                    lines.Add($"Pass / fail test,{(test ? "at least " + _sunHours.TargetHours.ToString("0.##", inv) + " h" : "off")}");
                }
                else
                {
                    lines.Add($"Sky,{(mode == StudyMode.DaylightFactor ? "CIE overcast" : "CIE clear" + (s.DirectSun ? " + direct sun" : " (no direct sun)"))}");
                    lines.Add($"Work plane (m),{_sunStudy.Settings.WorkPlane.ToString(inv)}");
                    lines.Add($"Rays per cell,{s.Rays}");
                    lines.Add($"Glass visible transmittance,{Daylight.GLASS_VLT.ToString(inv)}");
                    lines.Add($"Reflectances,{(s.StandardReflectance ? "standard" : "from colours")}");
                    lines.Add(mode == StudyMode.DaylightFactor
                        ? $"Pass / fail test,{(test ? "DF at least " + _sunHours.FactorTarget.ToString("0.#", inv) + " %" : "off")}"
                        : $"Pass / fail test,{(test ? "at least " + s.LuxTarget.ToString("0", inv) + " lux for " + _sunHours.LuxShare.ToString("P0", inv) + " of the time" : "off")}");
                    lines.Add("Note,Early design indicator: sky and sun components by ray tracing; one external bounce; internal reflection by the BRE split-flux formula per room. Not a compliance simulation.");
                    if (_sunRoomLight != null)
                    {
                        for (int r = 0; r < _sunRoomLight.Length; r++)
                        {
                            RoomLight l = _sunRoomLight[r];
                            if (!l.Valid) { continue; }
                            lines.Add($"Room {Csv(RoomLabel(r))},glazing {l.WindowArea.ToString("0.0", inv)} m2; surfaces {l.TotalArea.ToString("0.0", inv)} m2; reflectance ceiling {l.CeilingReflectance.ToString("0.00", inv)} walls {l.WallReflectance.ToString("0.00", inv)} floor {l.FloorReflectance.ToString("0.00", inv)} average {l.AverageReflectance.ToString("0.00", inv)}; IRC {l.IrcPercent.ToString("0.00", inv)} %");
                        }
                    }
                }
                lines.Add($"Grid (m),{_sunStudy.Settings.GridSize.ToString(inv)}");
                lines.Add($"Wall offset (m),{_sunStudy.Settings.WallOffset.ToString(inv)}");
                lines.Add($"Summary,{Csv(_sunSummary ?? "incomplete")}");
                lines.Add(string.Empty);
                string valueColumn = mode switch { StudyMode.DaylightFactor => "Daylight factor (%)", StudyMode.Illuminance => "Average illuminance (lux),Time at or above target", _ => "Sun hours" };
                lines.Add("Cell,Surface,Element id,Element,Room,X (m),Y (m),Z (m),Normal X,Normal Y,Normal Z," + valueColumn + (test ? ",Pass" : string.Empty));
                for (int i = 0; i < _sunStudy.Done; i++)
                {
                    _sunStudy.Cell(i, out Vector3 p, out Vector3 n, out _, out _, out int f);
                    SunHoursFace face = _sunStudy.Faces[f];
                    ElementRecord record = Scene.Elements[face.Element];
                    Vector3 world = ToRevit(p);
                    string value = _sunStudy.Hours[i].ToString(mode == StudyMode.Illuminance ? "0" : "0.###", inv);
                    if (mode == StudyMode.Illuminance) { value += "," + (_sunStudy.Shares?[i] ?? 0f).ToString("0.###", inv); }
                    lines.Add(string.Join(",",
                        (i + 1).ToString(inv),
                        face.Horizontal ? "Floor" : "Wall",
                        record.ElementId.ToString(inv),
                        Csv(record.Name),
                        Csv(face.Room >= 0 ? RoomLabel(face.Room) : string.Empty),
                        world.X.ToString("0.###", inv), world.Y.ToString("0.###", inv), world.Z.ToString("0.###", inv),
                        n.X.ToString("0.###", inv), n.Y.ToString("0.###", inv), n.Z.ToString("0.###", inv),
                        value) +
                        (test ? (_sunStudy.Passes(i, _sunHours) ? ",yes" : ",no") : string.Empty));
                }
                File.WriteAllLines(path, lines, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _sunNotice = $"Exported {_sunStudy.Done:N0} cells to {Path.GetFileName(path)}";
                Sound.Play(SoundId.Commit);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Sun hours export failed: {ex}");
                _sunNotice = "Export failed: " + ex.Message;
                Sound.Play(SoundId.Error);
            }

            static string Csv(string value)
            {
                value ??= string.Empty;
                return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
            }
        }

        /// <summary>"09:00".</summary>
        private static string Clock(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

        #endregion

        #region Saved studies

        /// <summary>
        /// Where this model's studies are saved: the live session's model folder, else (files) the folder the model's
        /// provenance points to (<see cref="ModelFolders.KeySourceFor"/>).
        /// </summary>
        private string SunStudyFolder()
        {
            string folder = ModelFolders.FolderOf(Scene.CommentsPath);
            return folder ?? ModelFolders.FolderFor(Scene.ModelTitle, ModelFolders.KeySourceFor(Scene.Provenance, DocumentPath));
        }

        /// <summary>SAVE STUDY…: the text box asks for a name (suggested: room, day and times).</summary>
        private void BeginSunStudyName()
        {
            SunHoursSettings run = _sunStudy.RunSettings ?? _sunHours;
            string when = run.Mode == StudyMode.DaylightFactor ? "daylight factor"
                : $"{(run.Mode == StudyMode.Illuminance ? "lux " : string.Empty)}{run.Day} {MONTHS[run.Month - 1]} {Clock(run.StartMinutes).Replace(':', '.')}-{Clock(run.EndMinutes).Replace(':', '.')}";
            string suggestion = (_sunFacesRoom >= 0 ? RoomLabel(_sunFacesRoom) + " " : string.Empty) + when;
            suggestion = SunStudyFiles.SafeName(suggestion);

            _editing = true;
            _editRecord = null;
            _editBookmark = null;
            _editReplyFor = _editAssigneeFor = null;
            _editSunStudy = true;
            _editMax = SunStudyFiles.MAX_NAME;
            _editLevel = null;
            _editLength = Math.Min(suggestion.Length, _editMax);
            suggestion.CopyTo(0, _editChars, 0, _editLength);
            _window.Input.ReleaseAll();
        }

        /// <summary>
        /// Saves the finished study under a name (a study of the same name is replaced): settings, surfaces, every
        /// cell's point (Revit internal metres) and hours.
        /// </summary>
        private void SaveSunStudy(string name)
        {
            if (!_sunStudy.Finished || _sunStudy.Hours == null)
            {
                _sunNotice = "Run the study first (only complete results can be saved)";
                Sound.Play(SoundId.Error);
                return;
            }

            try
            {
                int year = DateTime.Today.Year;
                SunHoursSettings run = _sunStudy.RunSettings.Clean(year);
                run.Target = _sunHours.Target;
                run.TargetHours = _sunHours.TargetHours;
                run.FactorTarget = _sunHours.FactorTarget;
                run.LuxShare = _sunHours.LuxShare;

                Vector3 origin = Scene.OriginOffset;
                var document = new SunStudyDocument
                {
                    Name = string.IsNullOrWhiteSpace(name) ? "Study" : name,
                    Model = Scene.ModelTitle ?? string.Empty,
                    Grid = _sunStudy.Settings.Clean(year),
                    Run = run,
                    SunSamples = _sunStudy.SunSamples,
                    TotalSamples = _sunStudy.TotalSamples
                };
                foreach (SunHoursFace face in _sunStudy.Faces)
                {
                    ElementRecord record = Scene.Elements[face.Element];
                    double offset = face.Offset + (double)face.Normal.X * origin.X + (double)face.Normal.Y * origin.Y + (double)face.Normal.Z * origin.Z;
                    document.Faces.Add(new SunStudyFace
                    {
                        UniqueId = string.IsNullOrEmpty(record.UniqueId) ? null : record.UniqueId,
                        ElementId = record.ElementId,
                        Link = record.Link,
                        ElementName = record.Name ?? string.Empty,
                        Nx = face.Normal.X, Ny = face.Normal.Y, Nz = face.Normal.Z,
                        Offset = Math.Round(offset, 5),
                        BothSides = face.BothSides,
                        Picked = face.Picked,
                        RoomKey = RoomKey(face.Room),
                        RoomLabel = face.Room >= 0 ? RoomLabel(face.Room) : string.Empty
                    });
                }

                int cells = _sunStudy.CellCount;
                document.CellFaces = new int[cells];
                document.Points = new float[cells * 3];
                document.Hours = new float[cells];
                document.Shares = _sunStudy.Shares != null ? (float[])_sunStudy.Shares.Clone() : null;
                for (int i = 0; i < cells; i++)
                {
                    _sunStudy.Cell(i, out Vector3 p, out _, out _, out _, out int f);
                    Vector3 world = ToRevit(p);
                    document.CellFaces[i] = f;
                    document.Points[i * 3] = world.X;
                    document.Points[i * 3 + 1] = world.Y;
                    document.Points[i * 3 + 2] = world.Z;
                    document.Hours[i] = _sunStudy.Hours[i];
                }

                if (!SunStudyFiles.Write(SunStudyFolder(), document, out string path, out string error))
                {
                    _sunNotice = "Study not saved: " + error;
                    Sound.Play(SoundId.Error);
                    return;
                }
                _sunLegendTitle = SunLegendTitle(run, document.Name);
                _sunNotice = $"Saved “{document.Name}” ({cells:N0} cells) in BimGo's folder for this model";
                Utilities.Log_Utils.Write($"Sun study saved: {path}");
                Sound.Play(SoundId.Commit);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Sun study save failed: {ex}");
                _sunNotice = "Study not saved: " + ex.Message;
                Sound.Play(SoundId.Error);
            }
        }

        /// <summary>SAVED STUDIES…: lists this model's studies.</summary>
        private void OpenSunStudies()
        {
            RefreshSunStudyList();
            _sunStudiesView = true;
            _sunStudyScroll = 0;
            _sunStudyDeleteArmed = null;
        }

        /// <summary>Reads the list of saved studies and their date labels (on open and after a delete).</summary>
        private void RefreshSunStudyList()
        {
            _sunStudyList = SunStudyFiles.List(SunStudyFolder());
            _sunStudyDates = _sunStudyList.Select(i => i.Saved.ToString("dd MMM yyyy HH:mm", CultureInfo.CurrentCulture)).ToList();
        }

        /// <summary>
        /// The saved studies list (in place of the study controls): name and date, LOAD and DELETE (twice), BACK.
        /// </summary>
        private void BuildSunStudiesList(FontAtlas f, InputState input, float x, float y, float w, float bottom)
        {
            _ui.TextWrapped(f.Small, x, y, w, "Saved in BimGo's folder for this model. LOAD shows the results again (the grid is laid again from the same surfaces).", UiTheme.TEXT_MUTED, maxLines: 3);
            y += S(48);

            float listBottom = bottom - S(100);
            float rowH = S(54);
            int visible = Math.Max(1, (int)((listBottom - y) / rowH));
            if (_sunStudyList.Count == 0)
            {
                _ui.TextWrapped(f.Body, x, y, w, "No saved studies yet: RUN a study, then SAVE STUDY…", UiTheme.TEXT_MUTED, maxLines: 2);
            }
            else
            {
                int maxScroll = Math.Max(0, _sunStudyList.Count - visible);
                if (input.Wheel != 0 && Hover(input, x, y, w, listBottom - y)) { _sunStudyScroll -= input.Wheel; }
                _sunStudyScroll = Math.Clamp(_sunStudyScroll, 0, maxScroll);
                if (_sunStudyDeleteArmed != null && _clock > _sunStudyDeleteArmedUntil) { _sunStudyDeleteArmed = null; }

                SunStudyInfo? load = null, delete = null;
                int last = Math.Min(_sunStudyList.Count, _sunStudyScroll + visible);
                for (int i = _sunStudyScroll; i < last; i++)
                {
                    SunStudyInfo info = _sunStudyList[i];
                    float ry = y + (i - _sunStudyScroll) * rowH;
                    _ui.TextWrapped(f.Body, x, ry, w - S(150), info.Name, UiTheme.TEXT, maxLines: 1);
                    _ui.TextWrapped(f.Small, x, ry + S(22), w - S(150), _sunStudyDates[i], UiTheme.TEXT_FAINT, maxLines: 1);
                    if (SmallButton(f, input, x + w - S(142), ry + S(4), S(66), S(30), "LOAD")) { load = info; }
                    bool armed = string.Equals(_sunStudyDeleteArmed, info.Path, StringComparison.OrdinalIgnoreCase);
                    if (SmallButton(f, input, x + w - S(70), ry + S(4), S(70), S(30), armed ? "SURE?" : "DELETE", danger: true)) { delete = info; }
                }

                if (load.HasValue) { LoadSunStudy(load.Value); }
                else if (delete.HasValue)
                {
                    if (string.Equals(_sunStudyDeleteArmed, delete.Value.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        _sunNotice = SunStudyFiles.Delete(delete.Value.Path) ? $"Deleted “{delete.Value.Name}”" : "The study could not be deleted (see the log)";
                        _sunStudyDeleteArmed = null;
                        RefreshSunStudyList();
                        Sound.Play(SoundId.Remove);
                    }
                    else
                    {
                        _sunStudyDeleteArmed = delete.Value.Path;
                        _sunStudyDeleteArmedUntil = _clock + 3f;
                    }
                }
            }

            if (_sunNotice != null) { _ui.TextWrapped(f.Small, x, bottom - S(92), w, _sunNotice, UiTheme.MEASURE_TEXT, maxLines: 2); }
            if (SmallButton(f, input, x, bottom - S(44), w, S(30), "BACK")) { _sunStudiesView = false; }
        }

        /// <summary>
        /// Loads a saved study: its settings, its surfaces (found again by element and plane) and, when the grid comes
        /// out the same, its results. Otherwise the surfaces stay selected and the study asks for a new RUN.
        /// </summary>
        private void LoadSunStudy(SunStudyInfo info)
        {
            SunStudyDocument document = SunStudyFiles.Read(info.Path, out string error);
            if (document == null)
            {
                _sunNotice = error;
                Sound.Play(SoundId.Error);
                return;
            }

            _sunStudy.Cancel();
            _sunStudiesView = false;

            // Settings: the run's day, times and target, the grid's cell size and offsets
            SunHoursSettings settings = document.Run.Clean(DateTime.Today.Year);
            settings.GridSize = document.Grid.GridSize;
            settings.FloorOffset = document.Grid.FloorOffset;
            settings.WallOffset = document.Grid.WallOffset;
            settings.WorkPlane = document.Grid.WorkPlane;
            _sunHours = settings;
            _sunRoomLight = null; // room figures aren't saved: the summary and CSV of a loaded study leave them out
            _sunRoomNote = null;

            // Surfaces
            _sunFaces.Clear();
            int missing = 0;
            foreach (SunStudyFace saved in document.Faces)
            {
                SunHoursFace face = RebuildFace(saved);
                if (face == null) { missing++; }
                else { _sunFaces.Add(face); }
            }
            _sunFacesRoom = _sunFaces.Count > 0 ? _sunFaces[0].Room : -1;
            _sunGridDirty = false;
            _sunStudy.Build(_sunFaces, _sunHours.Clean(DateTime.Today.Year), Scene.Rooms);
            _sunStale = false;
            _sunSummary = null;
            _sunPassLabel = _sunFailLabel = null;

            // Results, when the grid is the one that was saved
            bool same = missing == 0 && CellsMatch(document);
            if (same && _sunStudy.SetResults(document.Hours, document.Run, document.SunSamples, document.TotalSamples, document.Shares))
            {
                _sunLegendTitle = SunLegendTitle(document.Run, document.Name);
                RefreshSunSummary();
                _sunNotice = $"Loaded “{document.Name}” (saved {document.Saved.ToLocalTime():dd MMM HH:mm} by {document.SavedBy})";
                Sound.Play(SoundId.Commit);
            }
            else
            {
                _sunNotice = missing > 0
                    ? $"Loaded “{document.Name}”: {missing} of {document.Faces.Count} surfaces are no longer in the model. RUN again for new results"
                    : $"Loaded “{document.Name}”: the model changed since it was saved. RUN again for new results";
                Sound.Play(SoundId.Error);
            }
            Utilities.Log_Utils.Write($"Sun study loaded: {info.Path} ({_sunStudy.CellCount} cells, results {(same ? "shown" : "need a new run")}).");
        }

        /// <summary>True when the rebuilt grid has the saved cells (same count, each within 2 cm).</summary>
        private bool CellsMatch(SunStudyDocument document)
        {
            if (_sunStudy.CellCount != document.Hours.Length) { return false; }
            for (int i = 0; i < _sunStudy.CellCount; i++)
            {
                _sunStudy.Cell(i, out Vector3 p, out _, out _, out _, out int face);
                Vector3 world = ToRevit(p);
                var saved = new Vector3(document.Points[i * 3], document.Points[i * 3 + 1], document.Points[i * 3 + 2]);
                if (face != document.CellFaces[i] || Vector3.DistanceSquared(world, saved) > 0.02f * 0.02f) { return false; }
            }
            return true;
        }

        /// <summary>
        /// A saved surface found again: the element (UniqueId, else host ElementId), its triangles in the saved plane
        /// and the room it was clipped to. Null when the element or the plane is gone.
        /// </summary>
        private SunHoursFace RebuildFace(SunStudyFace saved)
        {
            int element = FindStudyElement(saved);
            if (element < 0) { return null; }

            var normal = new Vector3(saved.Nx, saved.Ny, saved.Nz);
            if (!float.IsFinite(normal.X) || normal.LengthSquared() < 1e-6f) { return null; }
            normal = Vector3.Normalize(normal);
            Vector3 origin = Scene.OriginOffset;
            float offset = (float)(saved.Offset - ((double)normal.X * origin.X + (double)normal.Y * origin.Y + (double)normal.Z * origin.Z));

            var face = new SunHoursFace
            {
                Element = element, Normal = normal, Offset = offset, BothSides = saved.BothSides, Picked = saved.Picked,
                Room = FindRoomByKey(saved.RoomKey)
            };
            ElementRecord record = Scene.Elements[element];
            SceneVertex[] vertices = Scene.Vertices;
            uint[] indices = Scene.Indices;
            for (int i = record.OpaqueStart; i + 2 < record.OpaqueStart + record.OpaqueCount; i += 3)
            {
                Vector3 a = vertices[indices[i]].Position, b = vertices[indices[i + 1]].Position, c = vertices[indices[i + 2]].Position;
                if (MathF.Abs(Vector3.Dot(normal, a) - offset) > 0.02f || MathF.Abs(Vector3.Dot(normal, b) - offset) > 0.02f || MathF.Abs(Vector3.Dot(normal, c) - offset) > 0.02f) { continue; }
                if (Vector3.Cross(b - a, c - a).LengthSquared() < 1e-10f) { continue; }
                face.Triangles.Add((a, b, c));
            }
            return face.Triangles.Count > 0 ? face : null;
        }

        /// <summary>The element a saved surface belongs to (host: UniqueId, else ElementId; links: UniqueId in that link), or -1.</summary>
        private int FindStudyElement(SunStudyFace saved)
        {
            if (saved.Link == 0)
            {
                if (saved.UniqueId != null && _elementIndexByUniqueId.TryGetValue(saved.UniqueId, out int byUnique)) { return byUnique; }
                return _elementIndexById.TryGetValue(saved.ElementId, out int byId) ? byId : -1;
            }
            for (int e = 0; e < Scene.Elements.Length; e++)
            {
                ElementRecord record = Scene.Elements[e];
                if (record.Link == saved.Link && string.Equals(record.UniqueId, saved.UniqueId, StringComparison.Ordinal)) { return e; }
            }
            return -1;
        }

        /// <summary>"number|name|link|floor height" (internal metres, 0.1 m): a room's identity across snapshots.</summary>
        private string RoomKey(int room)
        {
            if (room < 0 || room >= Scene.Rooms.Length) { return string.Empty; }
            RoomInfo r = Scene.Rooms[room];
            double z = Math.Round(r.BottomZ + (double)Scene.OriginOffset.Z, 1);
            return $"{r.Number}|{r.Name}|{r.Link}|{z.ToString("0.0", CultureInfo.InvariantCulture)}";
        }

        /// <summary>The room with a saved key, or -1.</summary>
        private int FindRoomByKey(string key)
        {
            if (string.IsNullOrEmpty(key)) { return -1; }
            for (int i = 0; i < Scene.Rooms.Length; i++)
            {
                if (string.Equals(RoomKey(i), key, StringComparison.Ordinal)) { return i; }
            }
            return -1;
        }

        #endregion

        #region Drawing

        /// <summary>
        /// The grid in the 3D pass: computed cells in their legend colour (or pass / fail), the rest grey (a preview
        /// while the panel is open, "not yet" while running; illuminance cells stay grey until the run ends, when the sun
        /// bounce is added). Rebuilt only when the study or the colouring changes. Nothing when closed without results.
        /// </summary>
        private void DrawSunHoursCells()
        {
            if (_sunStudy.CellCount == 0 || (!_sunHoursOpen && _sunStudy.Hours == null)) { return; }
            if (_sunOverlay == null)
            {
                _sunOverlay = new Overlay3D();
                _sunOverlay.Initialise();
            }

            bool passFail = _sunHours.PassFail;
            int colourKey = passFail
                ? 1 + (int)MathF.Round(_sunHours.TargetHours * 4f) + 100 * (int)MathF.Round(_sunHours.FactorTarget * 2f) + 10_000 * (int)MathF.Round(_sunHours.LuxShare * 10f)
                : 0;
            if (_sunOverlayRevision != _sunStudy.Revision || _sunOverlayColourKey != colourKey)
            {
                _sunOverlayRevision = _sunStudy.Revision;
                _sunOverlayColourKey = colourKey;
                _sunOverlay.Begin(Camera);
                float half = _sunStudy.Settings.GridSize * 0.47f;
                uint pending = Rgba.Hex(0xE5E7EB, _sunStudy.Running ? 0.25f : 0.45f);
                bool valuesReady = !(_sunStudy.Mode == StudyMode.Illuminance && _sunStudy.Running);
                for (int i = 0; i < _sunStudy.CellCount; i++)
                {
                    _sunStudy.Cell(i, out Vector3 p, out _, out Vector3 u, out Vector3 v, out _);
                    uint colour = pending;
                    if (_sunStudy.Hours != null && i < _sunStudy.Done && valuesReady)
                    {
                        float value = _sunStudy.Hours[i];
                        Vector3 c = passFail ? (_sunStudy.Passes(i, _sunHours) ? SunHours.PASS : SunHours.FAIL)
                            : _sunStudy.Mode switch
                            {
                                StudyMode.DaylightFactor => Daylight.FactorColour(value),
                                StudyMode.Illuminance => Daylight.LuxColour(value),
                                _ => SunHours.LegendColour(value)
                            };
                        colour = Rgba.FromFloat(c.X, c.Y, c.Z, 0.92f);
                    }
                    Vector3 du = u * half, dv = v * half;
                    _sunOverlay.Quad(p - du - dv, p + du - dv, p + du + dv, p - du + dv, colour);
                }
            }
            _sunOverlay.Draw(Camera, depthTest: true, alpha: 1f, additive: false);
        }

        /// <summary>
        /// The legend (bottom left, also in the study screenshot): Ladybug's colours over the mode's range (0–7 h,
        /// 0–5 %, 0–2000 lux) with ticks, or the pass / fail swatches, and the study's title.
        /// </summary>
        private void BuildSunLegend(FontAtlas f, float x, float y)
        {
            if (_sunStudy.Hours == null || _sunLegendTitle == null) { return; }
            float w = S(380), h = S(78);
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.PANEL_BORDER);
            _ui.Text(f.Small, x + S(12), y + S(8), _sunLegendTitle, UiTheme.SUN, S(0.5f));

            float barX = x + S(12), barY = y + S(30), barW = w - S(24), barH = S(14);
            if (_sunHours.PassFail && _sunPassLabel != null)
            {
                // Pass / fail: two swatches with the area shares
                float half = (barW - S(12)) * 0.5f;
                _ui.Rect(barX, barY, S(18), barH, Rgba.FromFloat(SunHours.PASS.X, SunHours.PASS.Y, SunHours.PASS.Z, 1f));
                _ui.TextWrapped(f.Small, barX + S(24), barY, half - S(24), _sunPassLabel, UiTheme.TEXT, maxLines: 2);
                _ui.Rect(barX + half + S(12), barY, S(18), barH, Rgba.FromFloat(SunHours.FAIL.X, SunHours.FAIL.Y, SunHours.FAIL.Z, 1f));
                _ui.Text(f.Small, barX + half + S(36), barY, _sunFailLabel, UiTheme.TEXT, S(0.4f));
                return;
            }

            const int STEPS = 56;
            for (int i = 0; i < STEPS; i++)
            {
                Vector3 c = SunHours.LegendColour((i + 0.5f) / STEPS * SunHours.LEGEND_MAX);
                _ui.Rect(barX + barW * i / STEPS, barY, barW / STEPS + 0.5f, barH, Rgba.FromFloat(c.X, c.Y, c.Z, 1f));
            }

            // Ticks: whole hours (0–7), whole % (0–5) or every 500 lux (0–2000)
            StudyMode mode = _sunStudy.Mode;
            int ticks = mode switch { StudyMode.DaylightFactor => (int)Daylight.DF_LEGEND_MAX, StudyMode.Illuminance => 4, _ => (int)SunHours.LEGEND_MAX };
            for (int t = 0; t <= ticks; t++)
            {
                float tx = barX + barW * t / ticks;
                _ui.Rect(tx - S(0.5f), barY + barH, S(1), S(4), UiTheme.TEXT_SOFT);
                Text.Clear().Append(mode == StudyMode.Illuminance ? t * 500 : t);
                if (t == ticks) { Text.Append(mode switch { StudyMode.DaylightFactor => "+ %", StudyMode.Illuminance => "+ lx", _ => "+ h" }); }
                _ui.TextCentred(f.Small, tx, barY + barH + S(6), Text.Span, UiTheme.TEXT_SOFT);
            }
        }

        /// <summary>
        /// The study panel (right side): mode, then the mode's settings (date, times and step for sun hours and
        /// illuminance; sky options, rays and reflectances for daylight), the pass / fail test, grid and offsets, the
        /// surfaces, RUN / progress, results, exports and saved studies.
        /// </summary>
        private void BuildSunHoursPanel(FontAtlas f, InputState input)
        {
            float w = S(380), x = _window.Width - S(20) - w, y = S(20);
            float h = MathF.Min(_window.Height - S(40), S(820));
            _sunPanelRect = new Vector4(x, y, w, h);
            _ui.Panel(x, y, w, h, UiTheme.PANEL_STRONG, UiTheme.SUN);
            float ix = x + S(16), iw = w - S(32), cy = y + S(14);
            _ui.Text(f.Small, ix, cy, "SUN AND DAYLIGHT STUDY", UiTheme.SUN, S(1.4f));
            cy += S(28);
            if (_sunStudiesView)
            {
                BuildSunStudiesList(f, input, ix, cy, iw, y + h);
                return;
            }

            bool shift = input.IsDown(Vk.VK_SHIFT);
            bool locked = _sunStudy.Running;
            SunHoursSettings s = _sunHours;
            int year = DateTime.Today.Year;

            // Mode
            int modeIndex = Math.Clamp((int)s.Mode, 0, MODE_OPTIONS.Length - 1);
            int newMode = Segmented(f, input, ix, cy, iw, MODE_OPTIONS, modeIndex);
            if (newMode != modeIndex && !locked) { SetStudyMode((StudyMode)newMode); }
            cy += S(42);

            if (s.Mode != StudyMode.DaylightFactor)
            {
                // Date: « month » ‹ day ›
                _ui.Text(f.Body, ix, cy + S(7), "Date", UiTheme.TEXT_SOFT);
                float fx = ix + S(110);
                if (SmallButton(f, input, fx, cy, S(30), S(30), "«") && !locked) { s.Month = (s.Month + 10) % 12 + 1; SunSettingChanged(false); }
                if (SmallButton(f, input, fx + S(34), cy, S(30), S(30), "‹") && !locked) { StepDay(s, -1, year); SunSettingChanged(false); }
                Text.Clear().Append(Math.Min(s.Day, DateTime.DaysInMonth(year, s.Month))).Append(' ').Append(MONTHS[s.Month - 1]);
                _ui.TextCentred(f.Bold, fx + S(68) + S(50), cy + S(6), Text.Span, UiTheme.TEXT);
                if (SmallButton(f, input, fx + S(172), cy, S(30), S(30), "›") && !locked) { StepDay(s, +1, year); SunSettingChanged(false); }
                if (SmallButton(f, input, fx + S(206), cy, S(30), S(30), "»") && !locked) { s.Month = s.Month % 12 + 1; SunSettingChanged(false); }
                cy += S(38);

                // From / to (15 min steps; Shift: 1 h)
                int stepMinutes = shift ? 60 : 15;
                cy = TimeRow(f, input, ix, cy, "From", s, true, stepMinutes, locked);
                cy = TimeRow(f, input, ix, cy, "To", s, false, stepMinutes, locked);

                _ui.Text(f.Body, ix, cy + S(7), "Sample every", UiTheme.TEXT_SOFT);
                int stepIndex = Math.Max(0, Array.IndexOf(SunHoursSettings.STEPS, s.StepMinutes));
                int newStep = Segmented(f, input, ix + S(110), cy, iw - S(110), STEP_OPTIONS, stepIndex);
                if (newStep != stepIndex && !locked) { s.StepMinutes = SunHoursSettings.STEPS[newStep]; SunSettingChanged(false); }
                cy += S(40);

                bool dst = Checkbox(f, input, ix, cy + S(4), iw, "Daylight saving (+1 h)", s.DaylightSaving);
                if (dst != s.DaylightSaving && !locked) { s.DaylightSaving = dst; SunSettingChanged(false); }
                cy += S(28);
            }

            if (s.Mode == StudyMode.SunHours)
            {
                bool glass = Checkbox(f, input, ix, cy + S(4), iw, "Glass blocks sun (off: sun passes through)", s.GlassBlocks);
                if (glass != s.GlassBlocks && !locked) { s.GlassBlocks = glass; SunSettingChanged(false); }
                cy += S(34);
            }
            else
            {
                if (s.Mode == StudyMode.Illuminance)
                {
                    bool sun = Checkbox(f, input, ix, cy + S(4), iw, "Direct sun (off: the clear sky only)", s.DirectSun);
                    if (sun != s.DirectSun && !locked) { s.DirectSun = sun; SunSettingChanged(false); }
                    cy += S(34);
                }
                _ui.Text(f.Body, ix, cy + S(7), "Rays per cell", UiTheme.TEXT_SOFT);
                int rayIndex = Math.Max(0, Array.IndexOf(SunHoursSettings.RAY_COUNTS, s.Rays));
                int newRays = Segmented(f, input, ix + S(110), cy, iw - S(110), RAY_OPTIONS, rayIndex);
                if (newRays != rayIndex && !locked) { s.Rays = SunHoursSettings.RAY_COUNTS[newRays]; SunSettingChanged(false); }
                cy += S(40);
                _ui.Text(f.Body, ix, cy + S(7), "Reflectance", UiTheme.TEXT_SOFT);
                int reflectIndex = s.StandardReflectance ? 1 : 0;
                int newReflect = Segmented(f, input, ix + S(110), cy, iw - S(110), REFLECTANCE_OPTIONS, reflectIndex);
                if (newReflect != reflectIndex && !locked) { s.StandardReflectance = newReflect == 1; SunSettingChanged(false); }
                cy += S(40);
            }

            // Pass / fail test: a toggle, then the mode's target
            if (SmallButton(f, input, ix, cy, S(150), S(30), s.PassFail ? "PASS / FAIL: ON" : "PASS / FAIL: OFF") && !locked) { TogglePassFail(); }
            if (s.PassFail)
            {
                float tx = ix + S(160);
                switch (s.Mode)
                {
                    case StudyMode.DaylightFactor:
                        float factor = StepperValue(f, input, tx, cy, s.FactorTarget, 0.5f, 0.5f, 10f, 1, " %");
                        if (factor != s.FactorTarget) { s.FactorTarget = factor; RefreshSunSummary(); }
                        break;
                    case StudyMode.Illuminance:
                        float lux = StepperValue(f, input, tx, cy, s.LuxTarget, shift ? 10f : 50f, 50f, 5000f, 0, " lx");
                        if (lux != s.LuxTarget && !locked) { s.LuxTarget = lux; SunSettingChanged(false); }
                        cy += S(36);
                        _ui.Text(f.Body, ix, cy + S(7), "for at least", UiTheme.TEXT_SOFT);
                        float share = StepperValue(f, input, tx, cy, s.LuxShare * 100f, 10f, 10f, 100f, 0, " % of time") / 100f;
                        if (MathF.Abs(share - s.LuxShare) > 1e-4f) { s.LuxShare = share; RefreshSunSummary(); }
                        break;
                    default:
                        float hours = StepperValue(f, input, tx, cy, s.TargetHours, 0.5f, 0.5f, 12f, 1, " h");
                        if (hours != s.TargetHours) { s.TargetHours = hours; RefreshSunSummary(); }
                        break;
                }
            }
            cy += S(40);

            // Grid and offsets (these rebuild the grid)
            _ui.Text(f.Body, ix, cy + S(7), "Grid", UiTheme.TEXT_SOFT);
            int gridIndex = Math.Max(0, Array.IndexOf(SunHoursSettings.GRID_SIZES, s.GridSize));
            int newGrid = Segmented(f, input, ix + S(110), cy, iw - S(110), GRID_OPTIONS, gridIndex);
            if (newGrid != gridIndex && !locked) { s.GridSize = SunHoursSettings.GRID_SIZES[newGrid]; SunSettingChanged(true); }
            cy += S(40);
            bool daylight = s.Mode != StudyMode.SunHours;
            float flat = OffsetRow(f, input, ix, cy, daylight ? "Work plane" : "Floor offset", daylight ? s.WorkPlane : s.FloorOffset, 2f);
            cy += S(38);
            float wallOffset = OffsetRow(f, input, ix, cy, "Wall offset", s.WallOffset, 1f);
            cy += S(38);
            if (!locked && (flat != (daylight ? s.WorkPlane : s.FloorOffset) || wallOffset != s.WallOffset))
            {
                if (daylight) { s.WorkPlane = flat; }
                else { s.FloorOffset = flat; }
                s.WallOffset = wallOffset;
                SunSettingChanged(true);
            }

            // Surfaces
            cy += S(4);
            _ui.Rect(ix, cy, iw, S(1), UiTheme.PANEL_BORDER);
            cy += S(10);
            Text.Clear().AppendGrouped(_sunFaces.Count).Append(_sunFaces.Count == 1 ? " surface · " : " surfaces · ").AppendGrouped(_sunStudy.CellCount).Append(" cells");
            _ui.Text(f.Body, ix, cy, Text.Span, UiTheme.TEXT);
            cy += S(22);
            _ui.TextWrapped(f.Small, ix, cy, iw, "Click a wall or floor to add / remove it · RMB-drag to look", UiTheme.TEXT_MUTED, maxLines: 1);
            cy += S(22);
            if (SmallButton(f, input, ix, cy, S(170), S(30), "THIS ROOM") && !locked)
            {
                SelectRoomFaces(_roomIndex);
                if (_roomIndex >= 0) { _sunNotice = "Selected: " + RoomLabel(_roomIndex); }
            }
            if (SmallButton(f, input, ix + S(178), cy, S(170), S(30), "CLEAR SURFACES") && !locked)
            {
                _sunFaces.Clear();
                _sunGridDirty = true;
            }
            cy += S(42);

            // Run / progress
            if (_sunStudy.Running)
            {
                float fraction = _sunStudy.CellCount == 0 ? 1f : (float)_sunStudy.Done / _sunStudy.CellCount;
                _ui.Rect(ix, cy + S(8), iw - S(110), S(12), UiTheme.CONTROL);
                _ui.Rect(ix, cy + S(8), (iw - S(110)) * fraction, S(12), UiTheme.SUN);
                if (SmallButton(f, input, ix + iw - S(100), cy, S(100), S(30), "CANCEL"))
                {
                    _sunStudy.Cancel();
                    _sunNotice = _sunStudy.Mode == StudyMode.Illuminance ? "Cancelled (illuminance needs a complete run)" : "Cancelled: the cells done so far keep their colour";
                }
            }
            else if (MenuButton(f, ix, cy, iw, _sunStale ? "RUN (settings changed)" : "RUN", true, false, _sunStudy.CellCount > 0, S(40)))
            {
                RunSunStudy();
            }
            cy += S(50);

            // Results and exports
            if (_sunSummary != null) { cy += S(2) + _ui.TextWrapped(f.Small, ix, cy, iw, _sunSummary, UiTheme.TEXT, maxLines: 3) + S(6); }
            bool results = _sunStudy.Finished && _sunStudy.Hours != null;
            float third = (iw - S(16)) / 3f;
            if (SmallButton(f, input, ix, cy, third, S(30), "EXPORT CSV") && results) { ExportSunHours(); }
            if (SmallButton(f, input, ix + third + S(8), cy, third, S(30), "SCREENSHOT") && results) { _sunShotRequested = true; }
            if (SmallButton(f, input, ix + (third + S(8)) * 2, cy, third, S(30), "CLEAR") && _sunStudy.Hours != null) { ClearSunResults(); }
            cy += S(38);
            float halfWidth = (iw - S(8)) * 0.5f;
            if (SmallButton(f, input, ix, cy, halfWidth, S(30), "SAVE STUDY…") && !locked)
            {
                if (_sunStudy.Finished) { BeginSunStudyName(); }
                else { _sunNotice = "Run the study first (only complete results can be saved)"; }
            }
            if (SmallButton(f, input, ix + halfWidth + S(8), cy, halfWidth, S(30), "SAVED STUDIES…") && !locked) { OpenSunStudies(); }
            cy += S(40);

            if (_sunNotice != null) { _ui.TextWrapped(f.Small, ix, cy, iw, _sunNotice, UiTheme.MEASURE_TEXT, maxLines: 2); }
            if (SmallButton(f, input, ix, y + h - S(44), iw, S(30), "CLOSE (J / ESC) · results stay")) { CloseSunHours(); }
        }

        /// <summary>
        /// A compact [−] value [+] stepper (pass / fail targets).
        /// </summary>
        /// <returns>The value after this frame's clicks (clamped, rounded to the step).</returns>
        private float StepperValue(FontAtlas f, InputState input, float x, float y, float value, float step, float min, float max, int decimals, string unit)
        {
            float changed = value;
            if (SmallButton(f, input, x, y, S(30), S(30), "−")) { changed = value - step; }
            Text.Clear().Append(value, decimals).Append(unit);
            _ui.TextCentred(f.Bold, x + S(34) + S(60), y + S(6), Text.Span, UiTheme.TEXT);
            if (SmallButton(f, input, x + S(158), y, S(30), S(30), "+")) { changed = value + step; }
            return changed == value ? value : MathF.Round(Math.Clamp(changed, min, max) / step) * step;
        }

        /// <summary>A "From" / "To" row with − / + buttons.</summary>
        private float TimeRow(FontAtlas f, InputState input, float x, float y, string label, SunHoursSettings s, bool start, int step, bool locked)
        {
            _ui.Text(f.Body, x, y + S(7), label, UiTheme.TEXT_SOFT);
            float fx = x + S(110);
            int value = start ? s.StartMinutes : s.EndMinutes;
            int changed = value;
            if (SmallButton(f, input, fx, y, S(40), S(30), "−")) { changed = value - step; }
            Text.Clear().Append(value / 60).Append(':');
            if (value % 60 < 10) { Text.Append('0'); }
            Text.Append(value % 60);
            _ui.TextCentred(f.Bold, fx + S(44) + S(55), y + S(6), Text.Span, UiTheme.TEXT);
            if (SmallButton(f, input, fx + S(158), y, S(40), S(30), "+")) { changed = value + step; }
            if (changed != value && !locked)
            {
                if (start) { s.StartMinutes = Math.Clamp(changed, 0, s.EndMinutes - 5); }
                else { s.EndMinutes = Math.Clamp(changed, s.StartMinutes + 5, 24 * 60); }
                SunSettingChanged(false);
            }
            return y + S(38);
        }

        /// <summary>An offset row (0.05 m steps) with − / +.</summary>
        /// <returns>The value after this frame's clicks.</returns>
        private float OffsetRow(FontAtlas f, InputState input, float x, float y, string label, float value, float max)
        {
            _ui.Text(f.Body, x, y + S(7), label, UiTheme.TEXT_SOFT);
            float fx = x + S(110);
            float changed = value;
            if (SmallButton(f, input, fx, y, S(40), S(30), "−")) { changed = value - 0.05f; }
            Text.Clear().Append(value, 2).Append(" m");
            _ui.TextCentred(f.Bold, fx + S(44) + S(55), y + S(6), Text.Span, UiTheme.TEXT);
            if (SmallButton(f, input, fx + S(158), y, S(40), S(30), "+")) { changed = value + 0.05f; }
            return changed == value ? value : MathF.Round(Math.Clamp(changed, 0f, max) * 20f) / 20f;
        }

        /// <summary>
        /// A setting changed: grid settings rebuild the grid (results go); time settings only mark results stale.
        /// </summary>
        private void SunSettingChanged(bool rebuildGrid)
        {
            if (rebuildGrid) { _sunGridDirty = true; }
            else if (_sunStudy.Hours != null) { _sunStale = true; }

        }

        private static void StepDay(SunHoursSettings s, int delta, int year)
        {
            var date = new DateTime(year, s.Month, Math.Clamp(s.Day, 1, DateTime.DaysInMonth(year, s.Month))).AddDays(delta);
            s.Month = date.Month;
            s.Day = date.Day;
        }

        #endregion
    }
}
