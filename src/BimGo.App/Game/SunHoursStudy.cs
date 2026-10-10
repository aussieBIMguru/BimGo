using System.Diagnostics;
using System.Numerics;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// One surface of a sun hours study: coplanar triangles of one element, the room it is clipped to (if any) and
    /// how its normal is chosen.
    /// </summary>
    internal sealed class SunHoursFace
    {
        /// <summary>The element (index into SceneData.Elements).</summary>
        public int Element;

        /// <summary>
        /// The plane normal. For <see cref="BothSides"/> faces its sign is arbitrary and each cell takes the side that
        /// faces <see cref="Room"/>; otherwise it is the side tested.
        /// </summary>
        public Vector3 Normal;

        /// <summary>The plane offset (dot(normal, point)).</summary>
        public float Offset;

        /// <summary>The face's triangles (world, scene-local).</summary>
        public readonly List<(Vector3 A, Vector3 B, Vector3 C)> Triangles = new();

        /// <summary>The room the cells are clipped to, or -1 (the whole face).</summary>
        public int Room = -1;

        /// <summary>True for walls found from a room: each cell faces whichever side is inside the room.</summary>
        public bool BothSides;

        /// <summary>True when picked by clicking (not part of the room selection).</summary>
        public bool Picked;

        /// <summary>True for floors and other upward / downward faces (they take the floor offset).</summary>
        public bool Horizontal => MathF.Abs(Normal.Z) > 0.7f;

        /// <summary>True when another face lies in the same plane of the same element (either normal sign).</summary>
        public bool SamePlane(int element, Vector3 normal, float offset) =>
            element == Element && MathF.Abs(Vector3.Dot(normal, Normal)) > 0.995f &&
            MathF.Abs(offset * MathF.Sign(Vector3.Dot(normal, Normal)) - Offset) < 0.02f;
    }

    /// <summary>Where a daylight ray ends (daylight round).</summary>
    internal enum RayOutcome
    {
        /// <summary>Reaches the sky (above the horizon).</summary>
        Sky,

        /// <summary>Reaches the ground outside (below the horizon, nothing in the way).</summary>
        Ground,

        /// <summary>Hits a surface outside the cell's room (lit by the sky: one external bounce).</summary>
        External,

        /// <summary>Hits the cell's own room (its light is the internally reflected component).</summary>
        Internal
    }

    /// <summary>
    /// Follows a daylight ray from a test point: where it ends, and the transmittance of glass it passed (1 = none).
    /// </summary>
    internal delegate RayOutcome RayClassifier(Vector3 origin, Vector3 direction, int room, out float transmit);

    /// <summary>
    /// A room's daylight figures (daylight round): its surface area, reflectances, glazing and the split-flux
    /// internally reflected component.
    /// </summary>
    internal struct RoomLight
    {
        public bool Valid;
        public float TotalArea, WindowArea;
        public float AverageReflectance, LowerReflectance, UpperReflectance;
        public float CeilingReflectance, WallReflectance, FloorReflectance;

        /// <summary>The internally reflected component under the overcast sky (% of the outdoor horizontal).</summary>
        public float IrcPercent;
    }

    /// <summary>What a daylight run needs, prepared once at RUN (skies per time sample, rooms, rays).</summary>
    internal sealed class DaylightInputs
    {
        public StudyMode Mode;
        public RayClassifier Classify;

        /// <summary>Cosine-weighted ray directions around +Z (rotated into each cell's frame).</summary>
        public Vector3[] Rays;

        /// <summary>Daylight factor: overcast patch luminances scaled to a unit horizontal illuminance.</summary>
        public float[] Overcast;

        /// <summary>Illuminance: per time sample (sun above the horizon), the sun direction, clear-sky patches,
        /// diffuse horizontal and direct normal illuminance (lux).</summary>
        public Vector3[] Suns;
        public float[][] Clear;
        public float[] Diffuse, DirectNormal;
        public int TotalSamples;
        public bool DirectSun;
        public float LuxTarget;

        /// <summary>Per room index (Scene.Rooms), its daylight figures (invalid = no room data).</summary>
        public RoomLight[] Rooms;

        /// <summary>A cell's area (m²): the grid size squared.</summary>
        public float CellArea;
    }

    /// <summary>
    /// A direct sun hours study (sun hours round): a pixelated test grid over the chosen faces, and per cell the hours
    /// of direct sun over the study's time range. The grid is built at once; the ray casting runs on the game thread
    /// a few milliseconds per frame (<see cref="Step"/>), so the BVH needs no locking and moved / placed elements
    /// block the sun too. Never throws.
    /// </summary>
    internal sealed class SunHoursStudy
    {
        /// <summary>Most cells in one study (a larger grid is suggested beyond this).</summary>
        public const int MAX_CELLS = 80_000;

        /// <summary>Test points sit this far off their surface on top of the offset (m), clear of it for the rays.</summary>
        private const float LIFT = 0.02f;

        /// <summary>A sun this close to grazing a surface (cosine) gives it no sun.</summary>
        private const float GRAZING = 0.02f;

        /// <summary>Rays stop at this distance (m): anything further can't shade a room.</summary>
        private const float MAX_DISTANCE = 1500f;

        /// <summary>
        /// A room's own wall face may lie this far inside the room (m): rooms bounded at wall centres put the face half
        /// a wall's thickness in (0.4 m covers walls up to 800 mm).
        /// </summary>
        private const float WALL_INSIDE = 0.4f;

        /// <summary>
        /// ... or this far outside it (m): rooms bounded at wall finishes put the face on the boundary; the far face of
        /// a partition (at least its thickness out) is left out.
        /// </summary>
        private const float WALL_OUTSIDE = 0.03f;

        /// <summary>How far either side of a wall face is probed to find the side that leads into the room (m).</summary>
        private const float WALL_PROBE = 0.45f;

        // Cells
        private readonly List<Vector3> _points = new();
        private readonly List<Vector3> _normals = new();
        private readonly List<Vector3> _u = new(), _v = new();
        private readonly List<int> _faceOf = new();

        /// <summary>The settings the grid was built with (cell size, offsets).</summary>
        public SunHoursSettings Settings { get; private set; }

        /// <summary>The faces the grid covers.</summary>
        public IReadOnlyList<SunHoursFace> Faces { get; private set; } = Array.Empty<SunHoursFace>();

        /// <summary>Number of cells.</summary>
        public int CellCount => _points.Count;

        /// <summary>True when the faces gave more than <see cref="MAX_CELLS"/> cells (the grid stops there).</summary>
        public bool Truncated { get; private set; }

        /// <summary>
        /// The value per cell (valid up to <see cref="Done"/>), or null before a run: hours of direct sun, daylight
        /// factor (%) or average illuminance (lux), by <see cref="Mode"/>.
        /// </summary>
        public float[] Hours { get; private set; }

        /// <summary>Illuminance runs: per cell, the share of time samples at or above the lux target (else null).</summary>
        public float[] Shares { get; private set; }

        /// <summary>What the current results are.</summary>
        public StudyMode Mode { get; private set; }

        /// <summary>Cells computed so far in the current run.</summary>
        public int Done { get; private set; }

        /// <summary>True while a run is in progress.</summary>
        public bool Running { get; private set; }

        /// <summary>True when a run finished (results complete).</summary>
        public bool Finished { get; private set; }

        /// <summary>Changes whenever cells or results change (the overlay rebuilds on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>The run's sun samples above the horizon, and all samples in the range.</summary>
        public int SunSamples { get; private set; }

        /// <inheritdoc cref="SunSamples"/>
        public int TotalSamples { get; private set; }

        /// <summary>The run's settings (date, times, glass), for the summary and the export.</summary>
        public SunHoursSettings RunSettings { get; private set; }

        /// <summary>Run time.</summary>
        public TimeSpan Elapsed { get; private set; }

        private List<Vector3> _directions = new();
        private readonly Stopwatch _clock = new();

        #region Grid

        /// <summary>
        /// Lays the test grid over the faces: square cells of the grid size in each face's plane (U horizontal on walls,
        /// along X on floors; snapped to the plane's own origin so neighbouring faces line up), kept where the cell
        /// centre lies on a triangle and, for faces clipped to a room, where its tested side faces into the room.
        /// Clears any results.
        /// </summary>
        public void Build(IReadOnlyList<SunHoursFace> faces, SunHoursSettings settings, RoomInfo[] rooms)
        {
            Cancel();
            Settings = settings;
            Faces = faces ?? Array.Empty<SunHoursFace>();
            _points.Clear();
            _normals.Clear();
            _u.Clear();
            _v.Clear();
            _faceOf.Clear();
            Hours = null;
            Shares = null;
            _daylight = null;
            Finished = false;
            Done = 0;
            Truncated = false;
            Revision++;

            float g = settings.GridSize;
            for (int f = 0; f < Faces.Count && !Truncated; f++)
            {
                SunHoursFace face = Faces[f];
                if (face.Triangles.Count == 0) { continue; }
                Vector3 n = face.Normal;
                Vector3 u = MathF.Abs(n.Z) > 0.7f
                    ? Vector3.Normalize(Vector3.UnitX - n * Vector3.Dot(n, Vector3.UnitX))
                    : Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n));
                Vector3 v = Vector3.Cross(n, u);
                Vector3 origin = n * face.Offset; // the plane's point nearest the scene origin: shared by coplanar faces

                // The face in plane coordinates
                var flat = new (Vector2 A, Vector2 B, Vector2 C)[face.Triangles.Count];
                var min = new Vector2(float.MaxValue);
                var max = new Vector2(float.MinValue);
                for (int t = 0; t < flat.Length; t++)
                {
                    (Vector3 a, Vector3 b, Vector3 c) = face.Triangles[t];
                    flat[t] = (Project(a - origin, u, v), Project(b - origin, u, v), Project(c - origin, u, v));
                    min = Vector2.Min(min, Vector2.Min(flat[t].A, Vector2.Min(flat[t].B, flat[t].C)));
                    max = Vector2.Max(max, Vector2.Max(flat[t].A, Vector2.Max(flat[t].B, flat[t].C)));
                }

                RoomInfo room = face.Room >= 0 && face.Room < rooms.Length ? rooms[face.Room] : null;
                float offset = (face.Horizontal ? settings.HorizontalOffset : settings.WallOffset) + LIFT;
                int i0 = (int)MathF.Floor(min.X / g), i1 = (int)MathF.Floor(max.X / g);
                int j0 = (int)MathF.Floor(min.Y / g), j1 = (int)MathF.Floor(max.Y / g);
                for (int j = j0; j <= j1 && !Truncated; j++)
                {
                    for (int i = i0; i <= i1; i++)
                    {
                        var centre = new Vector2((i + 0.5f) * g, (j + 0.5f) * g);
                        if (!OnFace(flat, centre)) { continue; }
                        Vector3 p = origin + u * centre.X + v * centre.Y;
                        if (!SideOf(face, room, p, out Vector3 normal)) { continue; }

                        if (_points.Count >= MAX_CELLS)
                        {
                            Truncated = true;
                            break;
                        }
                        _points.Add(p + normal * offset);
                        _normals.Add(normal);
                        _u.Add(u);
                        _v.Add(v);
                        _faceOf.Add(f);
                    }
                }
            }
        }

        /// <summary>
        /// The side a cell is tested on (false when the cell is dropped). Floors: inside the room's plan near its
        /// height. Walls found from a room: the face must be the room's own (between <see cref="WALL_INSIDE"/> inside
        /// and <see cref="WALL_OUTSIDE"/> outside its boundary, so rooms bounded at wall finishes and at wall centres
        /// both work, and the far face of a partition isn't taken), tested on the side that leads deeper into the room.
        /// Picked faces: their own side, which must face into the room they were clipped to.
        /// </summary>
        private static bool SideOf(SunHoursFace face, RoomInfo room, Vector3 p, out Vector3 normal)
        {
            normal = face.Normal;
            if (room == null) { return true; }

            if (face.Horizontal)
            {
                // Floors (and other flat faces) in the room's plan, near its height range
                return room.Contains(new Vector2(p.X, p.Y)) && p.Z > room.BottomZ - 0.3f && p.Z < room.TopZ + 0.5f;
            }

            if (p.Z < room.BottomZ - 0.05f || p.Z > room.TopZ + 0.5f) { return false; }
            var plan = new Vector2(p.X, p.Y);
            var across = new Vector2(face.Normal.X, face.Normal.Y);
            if (across.LengthSquared() > 1e-8f) { across = Vector2.Normalize(across); }

            if (face.BothSides)
            {
                float depth = SignedDepth(room, plan);
                if (depth > WALL_INSIDE || depth < -WALL_OUTSIDE) { return false; }
                float front = SignedDepth(room, plan + across * WALL_PROBE);
                float back = SignedDepth(room, plan - across * WALL_PROBE);
                if (MathF.Max(front, back) <= 0f) { return false; }
                normal = front >= back ? face.Normal : -face.Normal;
                return true;
            }

            return room.Contains(plan + across * 0.1f);
        }

        /// <summary>Distance to the room's boundary: positive inside, negative outside.</summary>
        private static float SignedDepth(RoomInfo room, Vector2 plan)
        {
            float distance = room.DistanceToBoundary(plan);
            return room.Contains(plan) ? distance : -distance;
        }

        private static Vector2 Project(Vector3 p, Vector3 u, Vector3 v) => new(Vector3.Dot(p, u), Vector3.Dot(p, v));

        /// <summary>True when a plane point lies on any of the face's triangles (1 mm tolerance).</summary>
        private static bool OnFace((Vector2 A, Vector2 B, Vector2 C)[] triangles, Vector2 p)
        {
            foreach ((Vector2 a, Vector2 b, Vector2 c) in triangles)
            {
                float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
                bool negative = d1 < -1e-6f || d2 < -1e-6f || d3 < -1e-6f;
                bool positive = d1 > 1e-6f || d2 > 1e-6f || d3 > 1e-6f;
                if (!(negative && positive)) { return true; }
            }
            return false;
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        #endregion

        #region Run

        /// <summary>
        /// Starts (or restarts) the run with these sun directions (scene axes, sun above the horizon).
        /// </summary>
        public void Start(List<Vector3> directions, int totalSamples, SunHoursSettings settings)
        {
            _directions = directions ?? new List<Vector3>();
            SunSamples = _directions.Count;
            TotalSamples = totalSamples;
            RunSettings = settings;
            Mode = StudyMode.SunHours;
            _daylight = null;
            Hours = new float[_points.Count];
            Shares = null;
            Done = 0;
            Finished = false;
            Running = _points.Count > 0;
            Elapsed = TimeSpan.Zero;
            Revision++;
        }

        /// <summary>Most cells × time samples an illuminance run keeps in memory (floats).</summary>
        public const int MAX_LUX_VALUES = 12_000_000;

        private DaylightInputs _daylight;
        private readonly float[] _patchWeights = new float[Daylight.PATCHES];
        private float[] _luxSamples;     // illuminance: cells × samples (lux), sun bounce added at the end
        private float[][] _roomSunFlux;  // illuminance: per room, per sample, direct sun landing on its floor cells (lm)

        /// <summary>
        /// Starts a daylight factor or illuminance run.
        /// </summary>
        /// <returns>False when an illuminance run would need too much memory (cells × samples).</returns>
        public bool StartDaylight(DaylightInputs inputs, SunHoursSettings settings)
        {
            int samples = inputs.Suns?.Length ?? 0;
            if (inputs.Mode == StudyMode.Illuminance && (long)_points.Count * Math.Max(1, samples) > MAX_LUX_VALUES) { return false; }

            _daylight = inputs;
            Mode = inputs.Mode;
            RunSettings = settings;
            SunSamples = samples;
            TotalSamples = inputs.Mode == StudyMode.Illuminance ? inputs.TotalSamples : 0;
            Hours = new float[_points.Count];
            Shares = inputs.Mode == StudyMode.Illuminance ? new float[_points.Count] : null;
            _luxSamples = inputs.Mode == StudyMode.Illuminance ? new float[_points.Count * Math.Max(1, samples)] : null;
            _roomSunFlux = null;
            if (inputs.Mode == StudyMode.Illuminance && inputs.Rooms != null)
            {
                _roomSunFlux = new float[inputs.Rooms.Length][];
                for (int r = 0; r < inputs.Rooms.Length; r++) { if (inputs.Rooms[r].Valid) { _roomSunFlux[r] = new float[Math.Max(1, samples)]; } }
            }
            Done = 0;
            Finished = false;
            Running = _points.Count > 0;
            Elapsed = TimeSpan.Zero;
            Revision++;
            return true;
        }

        /// <summary>Stops a run (cells computed so far keep their hours, the rest show as not run).</summary>
        public void Cancel()
        {
            if (!Running) { return; }
            Running = false;
            Revision++;
        }

        /// <summary>
        /// Computes cells for up to <paramref name="budgetMs"/> milliseconds: per cell, every sun sample in front of
        /// its surface whose ray reaches the sky adds one step of sun.
        /// </summary>
        /// <param name="blocked">True when a ray from a point towards a direction hits something.</param>
        /// <param name="budgetMs">Time allowed this frame.</param>
        public void Step(Func<Vector3, Vector3, float, bool> blocked, double budgetMs)
        {
            if (!Running) { return; }
            if (_daylight != null)
            {
                StepDaylight(budgetMs);
                return;
            }
            _clock.Restart();
            float hoursPerSample = RunSettings.StepMinutes / 60f;
            int start = Done;
            while (Done < _points.Count)
            {
                Vector3 p = _points[Done], n = _normals[Done];
                int sunny = 0;
                foreach (Vector3 direction in _directions)
                {
                    if (Vector3.Dot(direction, n) <= GRAZING) { continue; }
                    if (!blocked(p, direction, MAX_DISTANCE)) { sunny++; }
                }
                Hours[Done] = sunny * hoursPerSample;
                Done++;
                if (((Done - start) & 7) == 0 && _clock.Elapsed.TotalMilliseconds >= budgetMs) { break; }
            }
            Elapsed += _clock.Elapsed;
            Revision++;
            if (Done >= _points.Count)
            {
                Running = false;
                Finished = true;
            }
        }

        /// <summary>
        /// Daylight cells for up to <paramref name="budgetMs"/>: per cell, rays over its hemisphere binned into sky
        /// patches (sky, ground, outside surfaces; rays into its own room are left to the internally reflected
        /// component), then the daylight factor, or per time sample the clear-sky + sun illuminance.
        /// </summary>
        private void StepDaylight(double budgetMs)
        {
            _clock.Restart();
            DaylightInputs d = _daylight;
            int start = Done;
            float weight = MathF.PI / d.Rays.Length;
            while (Done < _points.Count)
            {
                int i = Done;
                Vector3 p = _points[i], n = _normals[i], u = _u[i], v = _v[i];
                int room = Faces[_faceOf[i]].Room;
                RoomLight light = d.Rooms != null && room >= 0 && room < d.Rooms.Length ? d.Rooms[room] : default;

                Array.Clear(_patchWeights);
                float ground = 0f, outside = 0f;
                foreach (Vector3 r in d.Rays)
                {
                    Vector3 direction = Vector3.Normalize(u * r.X + v * r.Y + n * r.Z);
                    switch (d.Classify(p, direction, room, out float transmit))
                    {
                        case RayOutcome.Sky:
                            int patch = Daylight.PatchOf(direction);
                            if (patch >= 0) { _patchWeights[patch] += weight * transmit; }
                            else { ground += weight * transmit; }
                            break;
                        case RayOutcome.Ground:
                            ground += weight * transmit;
                            break;
                        case RayOutcome.External:
                            outside += weight * transmit;
                            break;
                    }
                }

                if (d.Mode == StudyMode.DaylightFactor)
                {
                    // Sky + ground + outside surfaces (relative to a unit outdoor horizontal illuminance) + the room's IRC
                    float sky = 0f;
                    for (int k = 0; k < Daylight.PATCHES; k++) { sky += _patchWeights[k] * d.Overcast[k]; }
                    float external = ground * Daylight.GROUND_REFLECTANCE / MathF.PI + outside * Daylight.OBSTRUCTION_REFLECTANCE / (2f * MathF.PI);
                    Hours[i] = (sky + external) * 100f + (light.Valid ? light.IrcPercent : 0f);
                }
                else
                {
                    StepLuxCell(i, p, n, room, light, ground, outside);
                }

                Done++;
                if (((Done - start) & 3) == 0 && _clock.Elapsed.TotalMilliseconds >= budgetMs) { break; }
            }
            Elapsed += _clock.Elapsed;
            Revision++;
            if (Done >= _points.Count)
            {
                if (d.Mode == StudyMode.Illuminance) { FinishLux(); }
                Running = false;
                Finished = true;
            }
        }

        /// <summary>
        /// One illuminance cell: per time sample, clear sky + ground + outside surfaces + the room's sky IRC + direct
        /// sun (when on and it reaches the cell); direct sun on horizontal cells is added to the room's floor flux for
        /// the sun bounce added at the end.
        /// </summary>
        private void StepLuxCell(int i, Vector3 p, Vector3 n, int room, RoomLight light, float ground, float outside)
        {
            DaylightInputs d = _daylight;
            int samples = d.Suns.Length;
            bool floorCell = n.Z > 0.7f && _roomSunFlux != null && room >= 0 && room < _roomSunFlux.Length && _roomSunFlux[room] != null;
            for (int s = 0; s < samples; s++)
            {
                float[] sky = d.Clear[s];
                float e = 0f;
                for (int k = 0; k < Daylight.PATCHES; k++) { e += _patchWeights[k] * sky[k]; }

                Vector3 sun = d.Suns[s];
                float direct = d.DirectSun ? d.DirectNormal[s] : 0f;
                float globalHorizontal = d.Diffuse[s] + direct * MathF.Max(0f, sun.Z);
                e += ground * Daylight.GROUND_REFLECTANCE * globalHorizontal / MathF.PI;
                e += outside * Daylight.OBSTRUCTION_REFLECTANCE * globalHorizontal / (2f * MathF.PI);
                if (light.Valid) { e += light.IrcPercent * 0.01f * d.Diffuse[s]; }

                float cos = Vector3.Dot(sun, n);
                if (direct > 0f && cos > GRAZING && d.Classify(p, sun, room, out float transmit) == RayOutcome.Sky)
                {
                    float sunLux = direct * cos * transmit;
                    e += sunLux;
                    if (floorCell) { _roomSunFlux[room][s] += sunLux * d.CellArea; }
                }
                _luxSamples[i * samples + s] = e;
            }
            Hours[i] = 0f; // the average is set once the sun bounce is known (FinishLux)
        }

        /// <summary>
        /// Adds each room's sun bounce (direct sun on its floor cells, reflected by the split-flux rule) and works out
        /// every cell's average illuminance and the share of samples at or above the lux target.
        /// </summary>
        private void FinishLux()
        {
            DaylightInputs d = _daylight;
            int samples = d.Suns.Length;
            for (int i = 0; i < _points.Count; i++)
            {
                if (samples == 0)
                {
                    Hours[i] = 0f;
                    Shares[i] = 0f;
                    continue;
                }
                int room = Faces[_faceOf[i]].Room;
                float[] flux = _roomSunFlux != null && room >= 0 && room < _roomSunFlux.Length ? _roomSunFlux[room] : null;
                RoomLight light = flux != null ? d.Rooms[room] : default;
                double sum = 0;
                int reached = 0;
                for (int s = 0; s < samples; s++)
                {
                    float e = _luxSamples[i * samples + s];
                    if (flux != null) { e += Daylight.FloorBounceLux(flux[s], light.TotalArea, light.AverageReflectance, light.LowerReflectance); }
                    sum += e;
                    if (e >= d.LuxTarget) { reached++; }
                }
                Hours[i] = (float)(sum / samples);
                Shares[i] = (float)reached / samples;
            }
            _luxSamples = null;
            _roomSunFlux = null;
        }

        #endregion

        #region Results

        /// <summary>
        /// Cell i: its test point, normal, in-plane axes and face index.
        /// </summary>
        public void Cell(int i, out Vector3 point, out Vector3 normal, out Vector3 u, out Vector3 v, out int face)
        {
            point = _points[i];
            normal = _normals[i];
            u = _u[i];
            v = _v[i];
            face = _faceOf[i];
        }

        /// <summary>
        /// Shows saved results on the grid just built (a loaded study whose cells still match the model): hours per
        /// cell, the run's settings and sample counts. False when the cell count doesn't match.
        /// </summary>
        public bool SetResults(float[] hours, SunHoursSettings runSettings, int sunSamples, int totalSamples, float[] shares = null)
        {
            if (hours == null || hours.Length != _points.Count) { return false; }
            if (runSettings.Mode == StudyMode.Illuminance && (shares == null || shares.Length != hours.Length)) { return false; }
            Running = false;
            _daylight = null;
            Mode = runSettings.Mode;
            Shares = runSettings.Mode == StudyMode.Illuminance ? (float[])shares.Clone() : null;
            Hours = (float[])hours.Clone();
            Done = Hours.Length;
            Finished = true;
            RunSettings = runSettings;
            SunSamples = sunSamples;
            TotalSamples = totalSamples;
            Elapsed = TimeSpan.Zero;
            Revision++;
            return true;
        }

        /// <summary>
        /// True when cell i passes the settings' test: sun hours ≥ the hours target, daylight factor ≥ the % target, or
        /// (illuminance) the lux target reached for at least the share of samples. Lux runs use the target they ran
        /// with (the share is per cell; changing the lux needs a new run, changing the share doesn't).
        /// </summary>
        public bool Passes(int i, SunHoursSettings settings) => Mode switch
        {
            StudyMode.DaylightFactor => Hours[i] >= settings.FactorTarget - 1e-4f,
            StudyMode.Illuminance => Shares != null && Shares[i] >= settings.LuxShare - 1e-4f,
            _ => SunHours.Passes(Hours[i], settings.TargetHours)
        };

        /// <summary>The share of computed cells that pass the settings' test (0–1).</summary>
        public float PassShare(SunHoursSettings settings)
        {
            if (Hours == null || Done == 0 || Running) { return 0f; }
            int pass = 0;
            for (int i = 0; i < Done; i++)
            {
                if (Passes(i, settings)) { pass++; }
            }
            return (float)pass / Done;
        }

        /// <summary>
        /// Statistics of the computed cells: average, minimum, maximum hours and the share of cells with at least 2 h
        /// and 3 h (cells are equal areas, so these are area shares).
        /// </summary>
        public (float Average, float Min, float Max, float AtLeast2, float AtLeast3) Statistics()
        {
            if (Hours == null || Done == 0) { return (0f, 0f, 0f, 0f, 0f); }
            double sum = 0;
            float min = float.MaxValue, max = 0f;
            int two = 0, three = 0;
            for (int i = 0; i < Done; i++)
            {
                float h = Hours[i];
                sum += h;
                min = MathF.Min(min, h);
                max = MathF.Max(max, h);
                if (h >= 2f - 1e-4f) { two++; }
                if (h >= 3f - 1e-4f) { three++; }
            }
            return ((float)(sum / Done), min, max, (float)two / Done, (float)three / Done);
        }

        #endregion
    }
}
