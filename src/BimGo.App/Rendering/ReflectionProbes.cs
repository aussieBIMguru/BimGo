using System.Numerics;
using BimGo.Native;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Reflection probes (reflection probes round, build B): static captures of the model around a point, read by the
    /// scene shader for reflective surfaces instead of the sky.
    /// <list type="bullet">
    /// <item><b>Where:</b> one probe per Revit room that holds a reflective surface (shine tier 25 % + or water), a grid
    /// of probes in large rooms (longest side over <see cref="LARGE_ROOM"/>), and fallback probes for reflective
    /// surfaces outside rooms (outdoor water, unroomed areas), one per <see cref="FALLBACK_CELL"/> cell. Rooms without
    /// reflective surfaces get none, so a model without shine bakes nothing.</item>
    /// <item><b>Storage:</b> six faces per probe (90° each, the light-shadow face table) in one mipmapped RGBA8 texture
    /// array (GL 3.3 has no cube-map arrays). Mips are the blur: rough surfaces read a smaller level.</item>
    /// <item><b>Lookup:</b> a plan grid (<see cref="GRID_CELL"/> plan cells × <see cref="GRID_BAND"/> height bands, a
    /// texture array) holds up to two probes per cell and a blend weight, so a fragment finds its probes with one
    /// fetch, and room boundaries blend over about <see cref="BLEND_RADIUS"/>. Each probe's position and room box
    /// (for box projection) sit in a small float texture.</item>
    /// <item><b>Baking:</b> progressive, <see cref="FACES_PER_FRAME"/> faces per frame, nearest unbaked probe first,
    /// then stale ones (sun, lights, colour mode, edits). Until a probe is baked its cells show the sky. A probe baked
    /// while the player was far away (lights and sun shadows are fitted around the player) is refreshed once when the
    /// player comes near.</item>
    /// </list>
    /// Never throws: GL failures set <see cref="LastError"/> (the renderer then reflects the sky).
    /// </summary>
    internal sealed unsafe class ReflectionProbes : IDisposable
    {
        #region Constants

        /// <summary>Texture unit of the probe faces (1–10 are shadows, AO, glow, lights, materials).</summary>
        public const int ARRAY_UNIT = 11;

        /// <summary>Texture unit of the lookup grid.</summary>
        public const int GRID_UNIT = 12;

        /// <summary>Texture unit of the per-probe data (position, box).</summary>
        public const int DATA_UNIT = 13;

        /// <summary>Most probes, whatever the memory allows.</summary>
        public const int MAX_PROBES = 64;

        /// <summary>GPU memory the probe faces may use (bytes).</summary>
        public const long MEMORY_CAP = 64L * 1024 * 1024;

        /// <summary>Faces captured per frame (each is one scene draw at probe resolution, culled to the probe's reach).</summary>
        public const int FACES_PER_FRAME = 2;

        /// <summary>Near plane of a capture (m).</summary>
        private const float NEAR = 0.05f;

        /// <summary>Faces cover a little more than 90° (the shader uses the same pad), as for light shadows.</summary>
        public const float PAD = LightShadows.PAD;

        /// <summary>Smallest mip level kept (px): blurrier levels add nothing but seams.</summary>
        private const int SMALLEST_LEVEL = 4;

        /// <summary>
        /// Probe height above the room's floor (m): above table and benchtop height, so less of the floor near the walls
        /// is hidden behind furniture (hidden floor is what smears at the foot of glass).
        /// </summary>
        private const float EYE_HEIGHT = 1.7f;

        /// <summary>A room whose longest side is over this (m) gets a grid of probes.</summary>
        private const float LARGE_ROOM = 12f;

        /// <summary>Probe spacing in large rooms (m).</summary>
        private const float ROOM_SPACING = 8f;

        /// <summary>Plan size of a fallback cell outside rooms (m); its height is <see cref="FALLBACK_HEIGHT"/>.</summary>
        private const float FALLBACK_CELL = 8f, FALLBACK_HEIGHT = 6f;

        /// <summary>Reflective surfaces are gathered on this voxel (m) before placement (keeps the planning fast).</summary>
        private const float VOXEL = 0.5f;

        /// <summary>Lookup grid: plan cell and height band (m), grown when the grid would exceed <see cref="MAX_GRID_TEXELS"/>.</summary>
        private const float GRID_CELL = 0.5f, GRID_BAND = 1f;

        /// <summary>Most lookup cells.</summary>
        private const int MAX_GRID_TEXELS = 4_000_000;

        /// <summary>Probes blend across room boundaries over about this distance (m) on each side.</summary>
        private const float BLEND_RADIUS = 1f;

        /// <summary>A probe baked with the player further than this (m) is provisional, refreshed once within <see cref="REFRESH_NEAR"/>.</summary>
        private const float REFRESH_FAR = 20f, REFRESH_NEAR = 10f;

        /// <summary>Shine at or above this counts as reflective for placement (the 25 % tier, rounded).</summary>
        private const float PLACEMENT_SHINE = 0.125f;

        #endregion

        #region Types

        /// <summary>One probe.</summary>
        private sealed class Probe
        {
            public Vector3 Position;
            public Aabb Box;
            public float Far;
            public int Room = -1;
            public int Weight;
            public bool Baked, Dirty, Provisional;
            public int NextFace;
        }

        /// <summary>A reflective voxel and the room it lies in (-1 none).</summary>
        private readonly record struct ShinyPoint(Vector3 Position, int Room);

        #endregion

        #region Fields

        private SceneData _scene;
        private List<ShinyPoint> _shiny;
        private readonly List<Probe> _probes = new();
        private readonly Vector4[] _planes = new Vector4[6];

        // Lookup grid
        private Vector3 _gridOrigin, _gridCell;
        private int _nx, _ny, _nz;
        private byte[] _gridData = Array.Empty<byte>();

        // GL
        private uint _array, _grid, _data, _fbo, _readFbo, _depth;
        private int _size, _levels;
        private bool _dataDirty, _nothingToBake;
        private int _baking = -1;

        #endregion

        #region State

        /// <summary>True once the probes are placed and their textures allocated.</summary>
        public bool Ready { get; private set; }

        /// <summary>The last failure, or null (the renderer reflects the sky).</summary>
        public string LastError { get; private set; }

        /// <summary>Probes placed.</summary>
        public int Count => _probes.Count;

        /// <summary>Probes baked at least once.</summary>
        public int BakedCount { get; private set; }

        /// <summary>Probes waiting for a (re)bake.</summary>
        public int PendingCount { get; private set; }

        /// <summary>Face resolution in px (0 before allocation).</summary>
        public int Size => _size;

        /// <summary>GPU memory of the faces and grid (bytes).</summary>
        public long GpuBytes { get; private set; }

        /// <summary>Highest mip level the shader may read (the blur of a fully rough surface).</summary>
        public float MaxLod => Math.Max(0, _levels - 1);

        /// <summary>Lookup grid origin, cell size (x, y plan; z band) and size in cells, for the shader.</summary>
        public Vector3 GridOrigin => _gridOrigin;

        /// <inheritdoc cref="GridOrigin"/>
        public Vector3 GridCell => _gridCell;

        /// <inheritdoc cref="GridOrigin"/>
        public Vector3 GridSize => new(_nx, _ny, _nz);

        #endregion

        #region Setup

        /// <summary>
        /// Places the probes and allocates their textures for a face size (128 or 256). Re-places when the size changes
        /// (the memory cap allows fewer probes at 256). Does nothing when already set up at that size or after a
        /// failure. GL thread.
        /// </summary>
        /// <param name="scene">The snapshot (vertices, materials, rooms).</param>
        /// <param name="size">Face size in px.</param>
        /// <returns>False when probes can't be shown (see <see cref="LastError"/>; null error = nothing reflective).</returns>
        public bool Ensure(SceneData scene, int size)
        {
            size = size >= 256 ? 256 : 128;
            if (Ready && _size == size && ReferenceEquals(scene, _scene)) { return true; }
            if (LastError != null) { return false; }
            if (_nothingToBake && ReferenceEquals(scene, _scene)) { return false; }

            Release();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!ReferenceEquals(scene, _scene) || _shiny == null)
                {
                    _scene = scene;
                    _nothingToBake = false;
                    _shiny = GatherShiny(scene);
                }

                long perProbe = BytesPerProbe(size);
                int maxLayers = Math.Max(6, Gl.GetInteger(Gl.MAX_ARRAY_TEXTURE_LAYERS));
                int budget = (int)Math.Min(Math.Min(MAX_PROBES, MEMORY_CAP / perProbe), maxLayers / 6);
                PlaceProbes(scene, budget);
                if (_probes.Count == 0)
                {
                    _nothingToBake = true;
                    Utilities.Log_Utils.Write("Reflection probes: no reflective surfaces (tier 25 % + or water): nothing to bake.");
                    return false;
                }
                BuildGrid(scene);
            }
            catch (Exception ex)
            {
                LastError = "Reflection probes could not be placed: reflections show the sky";
                Utilities.Log_Utils.Write($"{LastError}: {ex}");
                Release();
                return false;
            }
            double planMs = clock.Elapsed.TotalMilliseconds;

            if (!Allocate(size)) { return false; }
            Ready = true;
            PendingCount = _probes.Count;
            Utilities.Log_Utils.Write($"Reflection probes: {_probes.Count} placed ({_probes.Count(p => p.Room >= 0)} in rooms, " +
                $"{_probes.Count(p => p.Room < 0)} fallback) from {_shiny.Count:N0} reflective voxels in {planMs:0} ms; {size} px faces, " +
                $"grid {_nx}×{_ny}×{_nz} at {_gridCell.X:0.##} m, ≈ {GpuBytes / (1024 * 1024)} MB.");
            return true;
        }

        /// <summary>Bytes of one probe's six faces with their mips (RGBA8).</summary>
        private static long BytesPerProbe(int size)
        {
            long texels = 0;
            for (int s = size; s >= SMALLEST_LEVEL; s >>= 1) { texels += (long)s * s; }
            return texels * 4 * 6;
        }

        /// <summary>
        /// Allocates the face array (all mip levels), the capture framebuffers, the grid and the data texture.
        /// </summary>
        private bool Allocate(int size)
        {
            while (Gl.GetError() != Gl.NO_ERROR) { /* clear stale errors */ }
            _size = size;
            _levels = 0;
            for (int s = size; s >= SMALLEST_LEVEL; s >>= 1) { _levels++; }
            int layers = _probes.Count * 6;

            _array = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0 + ARRAY_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _array);
            for (int level = 0; level < _levels; level++)
            {
                int s = size >> level;
                Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, level, Gl.RGBA8, s, s, layers, Gl.RGBA, Gl.UNSIGNED_BYTE, null);
            }
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, (int)Gl.LINEAR_MIPMAP_LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, (int)Gl.LINEAR);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_S, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_WRAP_T, (int)Gl.CLAMP_TO_EDGE);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAX_LEVEL, _levels - 1);

            // Lookup grid: RGBA8 (first probe + 1, second probe + 1, second's weight), read with texelFetch
            _grid = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0 + GRID_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _grid);
            Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 1);
            fixed (byte* p = _gridData)
            {
                Gl.TexImage3D(Gl.TEXTURE_2D_ARRAY, 0, Gl.RGBA8, _nx, _ny, _nz, Gl.RGBA, Gl.UNSIGNED_BYTE, p);
            }
            Gl.PixelStore(Gl.UNPACK_ALIGNMENT, 4);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MIN_FILTER, (int)Gl.NEAREST);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAG_FILTER, (int)Gl.NEAREST);
            Gl.TexParameter(Gl.TEXTURE_2D_ARRAY, Gl.TEXTURE_MAX_LEVEL, 0);

            // Per-probe data: three RGBA32F texels per probe
            _data = Gl.GenTexture();
            Gl.ActiveTexture(Gl.TEXTURE0 + DATA_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D, _data);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MIN_FILTER, (int)Gl.NEAREST);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAG_FILTER, (int)Gl.NEAREST);
            Gl.TexParameter(Gl.TEXTURE_2D, Gl.TEXTURE_MAX_LEVEL, 0);
            _dataDirty = true;
            UploadData();
            Gl.ActiveTexture(Gl.TEXTURE0);

            // Capture: colour = one layer of the array, depth = a renderbuffer; a second framebuffer reads for mip blits
            _depth = Gl.GenRenderbuffer();
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, _depth);
            Gl.RenderbufferStorageMultisample(Gl.RENDERBUFFER, 0, Gl.DEPTH_COMPONENT24, size, size);
            Gl.BindRenderbuffer(Gl.RENDERBUFFER, 0);
            _fbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _array, 0, 0);
            Gl.FramebufferRenderbuffer(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.RENDERBUFFER, _depth);
            Gl.DrawBuffer(Gl.COLOR_ATTACHMENT0);
            uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
            _readFbo = Gl.GenFramebuffer();
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);

            uint error = Gl.GetError();
            if (error != Gl.NO_ERROR || status != Gl.FRAMEBUFFER_COMPLETE)
            {
                LastError = error == Gl.OUT_OF_MEMORY
                    ? "Not enough graphics memory for reflection probes: reflections show the sky"
                    : $"Reflection probes are not supported on this graphics driver (0x{(error != Gl.NO_ERROR ? error : status):X}): reflections show the sky";
                Utilities.Log_Utils.Write(LastError);
                Release();
                return false;
            }

            GpuBytes = BytesPerProbe(size) * _probes.Count + _gridData.LongLength;
            return true;
        }

        /// <summary>
        /// Frees the GL resources and forgets the placement (kept: the gathered reflective voxels). Clears an error,
        /// so a later <see cref="Ensure"/> tries again.
        /// </summary>
        public void Release()
        {
            Gl.DeleteTexture(_array);
            Gl.DeleteTexture(_grid);
            Gl.DeleteTexture(_data);
            Gl.DeleteFramebuffer(_fbo);
            Gl.DeleteFramebuffer(_readFbo);
            Gl.DeleteRenderbuffer(_depth);
            _array = _grid = _data = _fbo = _readFbo = _depth = 0;
            _probes.Clear();
            _size = _levels = 0;
            _baking = -1;
            BakedCount = PendingCount = 0;
            GpuBytes = 0;
            Ready = false;
        }

        /// <summary>Forgets a failure so the next <see cref="Ensure"/> tries again (the user switched probes on again).</summary>
        public void ClearError() => LastError = null;

        #endregion

        #region Placement

        /// <summary>
        /// The reflective surfaces (shine tier 25 % + or water), as voxel centres with the room they lie in.
        /// </summary>
        private static List<ShinyPoint> GatherShiny(SceneData scene)
        {
            var points = new List<ShinyPoint>();
            MaterialData materials = scene.Materials;
            if (materials == null || materials.IsEmpty || materials.VertexMaterial.Length != scene.Vertices.Length) { return points; }

            var shinyMaterial = new bool[materials.Materials.Length];
            bool any = false;
            for (int m = 0; m < shinyMaterial.Length; m++)
            {
                SceneMaterial material = materials.Materials[m];
                shinyMaterial[m] = material.Shine >= PLACEMENT_SHINE || material.Water;
                any |= shinyMaterial[m];
            }
            if (!any) { return points; }

            // Voxelise (dedupes dense meshes: a tiled floor is a few thousand voxels, not a million vertices)
            var voxels = new Dictionary<(int, int, int), (Vector3 Sum, int Count)>();
            ushort[] index = materials.VertexMaterial;
            SceneVertex[] vertices = scene.Vertices;
            for (int v = 0; v < vertices.Length; v++)
            {
                int m = index[v];
                if (m >= shinyMaterial.Length || !shinyMaterial[m]) { continue; }
                Vector3 p = vertices[v].Position;
                var key = ((int)MathF.Floor(p.X / VOXEL), (int)MathF.Floor(p.Y / VOXEL), (int)MathF.Floor(p.Z / VOXEL));
                voxels[key] = voxels.TryGetValue(key, out (Vector3 Sum, int Count) acc) ? (acc.Sum + p, acc.Count + 1) : (p, 1);
            }

            var rooms = new RoomIndex(scene.Rooms);
            foreach ((Vector3 sum, int count) in voxels.Values)
            {
                Vector3 p = sum / count;
                points.Add(new ShinyPoint(p, rooms.Find(p)));
            }
            return points;
        }

        /// <summary>
        /// Places probes: rooms with reflective surfaces (a grid in large rooms), then fallback cells for what lies
        /// outside rooms. Keeps the <paramref name="budget"/> probes with the most reflective surface.
        /// </summary>
        private void PlaceProbes(SceneData scene, int budget)
        {
            _probes.Clear();
            RoomInfo[] rooms = scene.Rooms;
            var candidates = new List<Probe>();

            // Rooms
            foreach (IGrouping<int, ShinyPoint> group in _shiny.Where(s => s.Room >= 0).GroupBy(s => s.Room))
            {
                RoomInfo room = rooms[group.Key];
                List<ShinyPoint> points = group.ToList();
                var box = new Aabb(new Vector3(room.Min, room.BottomZ), new Vector3(room.Max, room.TopZ));
                float z = MathF.Min(room.BottomZ + EYE_HEIGHT, (room.BottomZ + room.TopZ) * 0.5f);
                float far = Math.Clamp(box.Size.Length() + 15f, 25f, 80f);
                Vector2 size = room.Max - room.Min;

                var spots = new List<Vector2>();
                if (MathF.Max(size.X, size.Y) <= LARGE_ROOM)
                {
                    spots.Add(InsideSpot(room, points));
                }
                else
                {
                    // A grid, kept where it is inside the room and near a reflective surface
                    int cx = Math.Max(1, (int)MathF.Ceiling(size.X / ROOM_SPACING)), cy = Math.Max(1, (int)MathF.Ceiling(size.Y / ROOM_SPACING));
                    for (int i = 0; i < cx; i++)
                    {
                        for (int j = 0; j < cy; j++)
                        {
                            var spot = new Vector2(room.Min.X + (i + 0.5f) * size.X / cx, room.Min.Y + (j + 0.5f) * size.Y / cy);
                            if (!RoomIndex.Inside(room, spot)) { continue; }
                            if (points.Any(s => Vector2.DistanceSquared(new Vector2(s.Position.X, s.Position.Y), spot) < ROOM_SPACING * ROOM_SPACING)) { spots.Add(spot); }
                        }
                    }
                    if (spots.Count == 0) { spots.Add(InsideSpot(room, points)); }
                }

                // Each reflective voxel counts for its nearest probe in the room
                int first = candidates.Count;
                foreach (Vector2 spot in spots)
                {
                    candidates.Add(new Probe { Position = new Vector3(spot, z), Box = box, Far = far, Room = group.Key });
                }
                foreach (ShinyPoint s in points)
                {
                    int best = first;
                    for (int k = first + 1; k < candidates.Count; k++)
                    {
                        if (Vector3.DistanceSquared(candidates[k].Position, s.Position) < Vector3.DistanceSquared(candidates[best].Position, s.Position)) { best = k; }
                    }
                    candidates[best].Weight++;
                }
            }

            // Outside rooms: one probe per fallback cell, above the surfaces' average
            foreach (IGrouping<(int, int, int), ShinyPoint> cell in _shiny.Where(s => s.Room < 0)
                .GroupBy(s => ((int)MathF.Floor(s.Position.X / FALLBACK_CELL), (int)MathF.Floor(s.Position.Y / FALLBACK_CELL), (int)MathF.Floor(s.Position.Z / FALLBACK_HEIGHT))))
            {
                Vector3 average = Vector3.Zero;
                int count = 0;
                foreach (ShinyPoint s in cell) { average += s.Position; count++; }
                average /= count;
                Vector3 position = average + new Vector3(0f, 0f, EYE_HEIGHT);
                var box = new Aabb(average - new Vector3(12f, 12f, 1f), average + new Vector3(12f, 12f, 10f));
                candidates.Add(new Probe { Position = position, Box = box, Far = 80f, Weight = count });
            }

            _probes.AddRange(candidates.OrderByDescending(p => p.Weight).Take(Math.Max(0, budget)));
            foreach (Probe probe in _probes) { probe.Dirty = true; }
        }

        /// <summary>A plan point inside the room: its box centre if inside, else the reflective surfaces' average, else one of them.</summary>
        private static Vector2 InsideSpot(RoomInfo room, List<ShinyPoint> points)
        {
            Vector2 centre = (room.Min + room.Max) * 0.5f;
            if (RoomIndex.Inside(room, centre)) { return centre; }
            Vector2 average = Vector2.Zero;
            foreach (ShinyPoint s in points) { average += new Vector2(s.Position.X, s.Position.Y); }
            average /= Math.Max(1, points.Count);
            if (RoomIndex.Inside(room, average)) { return average; }
            foreach (ShinyPoint s in points)
            {
                var p = new Vector2(s.Position.X, s.Position.Y);
                if (RoomIndex.Inside(room, p)) { return p; }
            }
            return centre;
        }

        /// <summary>
        /// The lookup grid over the probes' boxes: each cell's probe (a room probe by containment, else the nearest
        /// fallback probe whose box holds it), then a second probe and weight near boundaries for blending.
        /// </summary>
        private void BuildGrid(SceneData scene)
        {
            var bounds = Aabb.Empty;
            foreach (Probe probe in _probes) { bounds.Include(probe.Box); }
            if (scene.Bounds.IsValid)
            {
                bounds = new Aabb(Vector3.Max(bounds.Min, scene.Bounds.Min - new Vector3(1f)), Vector3.Min(bounds.Max, scene.Bounds.Max + new Vector3(1f)));
            }

            float cell = GRID_CELL, band = GRID_BAND;
            Vector3 extent = Vector3.Max(bounds.Size, new Vector3(0.1f));
            while (true)
            {
                _nx = Math.Max(1, (int)MathF.Ceiling(extent.X / cell));
                _ny = Math.Max(1, (int)MathF.Ceiling(extent.Y / cell));
                _nz = Math.Max(1, (int)MathF.Ceiling(extent.Z / band));
                if ((long)_nx * _ny * _nz <= MAX_GRID_TEXELS && _nx <= 2048 && _ny <= 2048 && _nz <= 256) { break; }
                cell *= 1.25f;
                band *= 1.25f;
            }
            _gridOrigin = bounds.Min;
            _gridCell = new Vector3(cell, cell, band);

            int total = _nx * _ny * _nz;
            var primary = new short[total];
            Array.Fill(primary, (short)-1);
            RoomInfo[] rooms = scene.Rooms;

            // Room probes: cells whose centre is inside the room's plan and whose band overlaps its height
            for (int i = 0; i < _probes.Count; i++)
            {
                Probe probe = _probes[i];
                (int x0, int y0, int z0, int x1, int y1, int z1) = CellRange(probe.Box);
                for (int z = z0; z <= z1; z++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            int c = (z * _ny + y) * _nx + x;
                            Vector3 centre = CellCentre(x, y, z);
                            if (probe.Room >= 0)
                            {
                                RoomInfo room = rooms[probe.Room];
                                float bandLow = _gridOrigin.Z + z * _gridCell.Z, bandHigh = bandLow + _gridCell.Z;
                                if (bandHigh < room.BottomZ - 0.3f || bandLow > room.TopZ + 0.3f) { continue; }
                                if (!RoomIndex.Inside(room, new Vector2(centre.X, centre.Y))) { continue; }
                                int current = primary[c];
                                // Same room (large rooms): the nearest of its probes; another room's cell is kept
                                if (current >= 0 && (_probes[current].Room != probe.Room ||
                                    PlanDistance(_probes[current].Position, centre) <= PlanDistance(probe.Position, centre))) { continue; }
                                primary[c] = (short)i;
                            }
                            else
                            {
                                int current = primary[c];
                                if (current >= 0 && (_probes[current].Room >= 0 ||
                                    Vector3.DistanceSquared(_probes[current].Position, centre) <= Vector3.DistanceSquared(probe.Position, centre))) { continue; }
                                primary[c] = (short)i;
                            }
                        }
                    }
                }
            }

            // Blending: from every boundary cell, spread the neighbour's probe into cells of this probe within the radius
            var secondary = new short[total];
            var distance = new float[total];
            Array.Fill(secondary, (short)-1);
            Array.Fill(distance, float.MaxValue);
            int reach = Math.Max(1, (int)MathF.Ceiling(BLEND_RADIUS / cell));
            float radius = (reach + 0.5f) * cell;
            for (int z = 0; z < _nz; z++)
            {
                for (int y = 0; y < _ny; y++)
                {
                    for (int x = 0; x < _nx; x++)
                    {
                        int c = (z * _ny + y) * _nx + x;
                        int p = primary[c];
                        if (p < 0) { continue; }
                        int q = Neighbour(primary, x + 1, y, z, p);
                        if (q < 0) { q = Neighbour(primary, x - 1, y, z, p); }
                        if (q < 0) { q = Neighbour(primary, x, y + 1, z, p); }
                        if (q < 0) { q = Neighbour(primary, x, y - 1, z, p); }
                        if (q < 0) { continue; }

                        for (int dy = -reach; dy <= reach; dy++)
                        {
                            for (int dx = -reach; dx <= reach; dx++)
                            {
                                int tx = x + dx, ty = y + dy;
                                if (tx < 0 || ty < 0 || tx >= _nx || ty >= _ny) { continue; }
                                int t = (z * _ny + ty) * _nx + tx;
                                if (primary[t] != p) { continue; }
                                float d = MathF.Sqrt(dx * dx + dy * dy) * cell + 0.5f * cell;
                                if (d < distance[t] && d < radius)
                                {
                                    distance[t] = d;
                                    secondary[t] = (short)q;
                                }
                            }
                        }
                    }
                }
            }

            _gridData = new byte[total * 4];
            for (int c = 0; c < total; c++)
            {
                int o = c * 4;
                _gridData[o] = (byte)(primary[c] + 1);
                if (secondary[c] >= 0)
                {
                    _gridData[o + 1] = (byte)(secondary[c] + 1);
                    float weight = 0.5f * (1f - distance[c] / radius);
                    _gridData[o + 2] = (byte)Math.Clamp((int)MathF.Round(weight * 255f), 0, 255);
                }
                _gridData[o + 3] = 255;
            }
        }

        private int Neighbour(short[] primary, int x, int y, int z, int self)
        {
            if (x < 0 || y < 0 || x >= _nx || y >= _ny) { return -1; }
            int q = primary[(z * _ny + y) * _nx + x];
            return q >= 0 && q != self ? q : -1;
        }

        private (int, int, int, int, int, int) CellRange(in Aabb box)
        {
            Vector3 lo = (box.Min - _gridOrigin) / _gridCell, hi = (box.Max - _gridOrigin) / _gridCell;
            return (Math.Clamp((int)MathF.Floor(lo.X), 0, _nx - 1), Math.Clamp((int)MathF.Floor(lo.Y), 0, _ny - 1), Math.Clamp((int)MathF.Floor(lo.Z), 0, _nz - 1),
                    Math.Clamp((int)MathF.Floor(hi.X), 0, _nx - 1), Math.Clamp((int)MathF.Floor(hi.Y), 0, _ny - 1), Math.Clamp((int)MathF.Floor(hi.Z), 0, _nz - 1));
        }

        private Vector3 CellCentre(int x, int y, int z) => _gridOrigin + new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * _gridCell;

        private static float PlanDistance(Vector3 a, Vector3 b) => Vector2.DistanceSquared(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y));

        #endregion

        #region Baking

        /// <summary>
        /// Marks every probe stale (sun, lights, colour mode or the model changed). Stale probes keep showing their old
        /// capture until re-baked, nearest first.
        /// </summary>
        public void Invalidate()
        {
            foreach (Probe probe in _probes) { probe.Dirty = true; }
            PendingCount = _probes.Count;
        }

        /// <summary>
        /// Picks this frame's faces: the probe being baked continues; otherwise the nearest unbaked, then the nearest
        /// stale, then a provisional one the player came near.
        /// </summary>
        /// <param name="eye">The player's eye.</param>
        /// <param name="faces">Out: (probe, face) pairs to capture now (cleared first).</param>
        public void NextFaces(Vector3 eye, List<(int Probe, int Face)> faces)
        {
            faces.Clear();
            if (!Ready) { return; }
            while (faces.Count < FACES_PER_FRAME)
            {
                if (_baking < 0)
                {
                    _baking = Pick(eye);
                    if (_baking < 0) { break; }
                    _probes[_baking].NextFace = 0;
                    _probes[_baking].Provisional = Vector3.Distance(eye, _probes[_baking].Position) > REFRESH_FAR;
                }
                Probe probe = _probes[_baking];
                faces.Add((_baking, probe.NextFace));
                probe.NextFace++;
                if (probe.NextFace >= 6)
                {
                    // Finished after this frame's capture (mips and data in FinishProbe)
                    _baking = -1;
                }
            }
        }

        private int Pick(Vector3 eye)
        {
            int best = -1;
            int bestRank = int.MaxValue;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _probes.Count; i++)
            {
                Probe p = _probes[i];
                float d = Vector3.DistanceSquared(eye, p.Position);
                int rank = !p.Baked ? 0 : p.Dirty ? 1 : p.Provisional && d < REFRESH_NEAR * REFRESH_NEAR ? 2 : int.MaxValue;
                if (rank == int.MaxValue) { continue; }
                if (rank < bestRank || (rank == bestRank && d < bestDistance))
                {
                    best = i;
                    bestRank = rank;
                    bestDistance = d;
                }
            }
            return best;
        }

        /// <summary>The view-projection, eye and culling planes of one probe face.</summary>
        public Matrix4x4 FaceMatrix(int probe, int face, out Vector3 eye, out Vector4[] planes)
        {
            Probe p = _probes[probe];
            eye = p.Position;
            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + LightShadows.FaceForward[face], LightShadows.FaceUp[face]);
            Matrix4x4 projection = FpsCamera.Perspective(2f * MathF.Atan(PAD), 1f, NEAR, p.Far);
            Matrix4x4 matrix = view * projection;
            FpsCamera.ExtractPlanes(matrix, _planes);
            planes = _planes;
            return matrix;
        }

        /// <summary>
        /// Binds the capture framebuffer to one face's layer, clears it and sets the viewport. The face array is
        /// unbound from its sampling unit while capturing (no feedback loop).
        /// </summary>
        public void BeginFace(int probe, int face, Vector3 clear)
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + ARRAY_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
            Gl.ActiveTexture(Gl.TEXTURE0);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _array, 0, probe * 6 + face);
            Gl.Viewport(0, 0, _size, _size);
            Gl.ColorMask(true, true, true, true);
            Gl.DepthMask(true);
            Gl.ClearColor(clear.X, clear.Y, clear.Z, 1f);
            Gl.ClearDepth(1.0);
            Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        }

        /// <summary>
        /// After a face's capture: builds its mip chain (2× box-filter blits). When it was the sixth face the probe is
        /// baked and its data row updated.
        /// </summary>
        public void EndFace(int probe, int face)
        {
            int layer = probe * 6 + face;
            for (int level = 1; level < _levels; level++)
            {
                int from = _size >> (level - 1), to = _size >> level;
                Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _readFbo);
                Gl.FramebufferTextureLayer(Gl.READ_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _array, level - 1, layer);
                Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
                Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _fbo);
                Gl.FramebufferTextureLayer(Gl.DRAW_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _array, level, layer);
                Gl.BlitFramebuffer(0, 0, from, from, 0, 0, to, to, Gl.COLOR_BUFFER_BIT, Gl.LINEAR);
            }
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, _fbo);
            Gl.FramebufferTextureLayer(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, _array, 0, layer);

            if (face == 5)
            {
                Probe p = _probes[probe];
                if (!p.Baked) { BakedCount++; }
                p.Baked = true;
                p.Dirty = false;
                _dataDirty = true;
                PendingCount = _probes.Count(x => !x.Baked || x.Dirty);
            }
        }

        /// <summary>
        /// Restores the default framebuffer, uploads changed probe data and binds the probe textures to their units.
        /// </summary>
        public void EndCapture()
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
            UploadData();
            Bind();
        }

        /// <summary>
        /// Writes the per-probe data texture: (position, first layer), (box min, baked), (box max, 0).
        /// </summary>
        private void UploadData()
        {
            if (!_dataDirty || _data == 0) { return; }
            _dataDirty = false;
            int count = Math.Max(1, _probes.Count);
            float[] data = new float[count * 3 * 4];
            for (int i = 0; i < _probes.Count; i++)
            {
                Probe p = _probes[i];
                int o = i * 12;
                data[o] = p.Position.X; data[o + 1] = p.Position.Y; data[o + 2] = p.Position.Z; data[o + 3] = i * 6;
                data[o + 4] = p.Box.Min.X; data[o + 5] = p.Box.Min.Y; data[o + 6] = p.Box.Min.Z; data[o + 7] = p.Baked ? 1f : 0f;
                data[o + 8] = p.Box.Max.X; data[o + 9] = p.Box.Max.Y; data[o + 10] = p.Box.Max.Z; data[o + 11] = 0f;
            }
            Gl.ActiveTexture(Gl.TEXTURE0 + DATA_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D, _data);
            fixed (float* p = data)
            {
                // Rows are probes, columns the three texels (texelFetch(ivec2(column, probe)))
                Gl.TexImage2D(Gl.TEXTURE_2D, 0, Gl.RGBA32F, 3, count, Gl.RGBA, Gl.FLOAT, p);
            }
            Gl.ActiveTexture(Gl.TEXTURE0);
        }

        /// <summary>
        /// Binds the faces, grid and data to their units (leaves unit 0 active).
        /// </summary>
        public void Bind()
        {
            Gl.ActiveTexture(Gl.TEXTURE0 + ARRAY_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _array);
            Gl.ActiveTexture(Gl.TEXTURE0 + GRID_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, _grid);
            Gl.ActiveTexture(Gl.TEXTURE0 + DATA_UNIT);
            Gl.BindTexture(Gl.TEXTURE_2D, _data);
            Gl.ActiveTexture(Gl.TEXTURE0);
        }

        #endregion

        /// <summary>
        /// Releases GL resources.
        /// </summary>
        public void Dispose() => Release();

        /// <summary>
        /// Finds the room a point lies in: rooms bucketed on a 4 m plan grid, then the even-odd test over their loops
        /// and their height (with 0.3 m of tolerance so floor and ceiling surfaces count). Host rooms come first.
        /// </summary>
        private sealed class RoomIndex
        {
            private const float BUCKET = 4f;
            private readonly RoomInfo[] _rooms;
            private readonly Dictionary<(int, int), List<int>> _buckets = new();

            public RoomIndex(RoomInfo[] rooms)
            {
                _rooms = rooms ?? Array.Empty<RoomInfo>();
                // Host rooms (link 0) first, so they win where a linked room overlaps
                foreach (int r in Enumerable.Range(0, _rooms.Length).OrderBy(i => _rooms[i].Link))
                {
                    RoomInfo room = _rooms[r];
                    if (room.Loops == null || room.Loops.Length == 0) { continue; }
                    for (int x = (int)MathF.Floor(room.Min.X / BUCKET); x <= (int)MathF.Floor(room.Max.X / BUCKET); x++)
                    {
                        for (int y = (int)MathF.Floor(room.Min.Y / BUCKET); y <= (int)MathF.Floor(room.Max.Y / BUCKET); y++)
                        {
                            if (!_buckets.TryGetValue((x, y), out List<int> list)) { _buckets[(x, y)] = list = new List<int>(); }
                            list.Add(r);
                        }
                    }
                }
            }

            public int Find(Vector3 p)
            {
                if (!_buckets.TryGetValue(((int)MathF.Floor(p.X / BUCKET), (int)MathF.Floor(p.Y / BUCKET)), out List<int> list)) { return -1; }
                var plan = new Vector2(p.X, p.Y);
                foreach (int r in list)
                {
                    RoomInfo room = _rooms[r];
                    if (p.Z < room.BottomZ - 0.3f || p.Z > room.TopZ + 0.3f) { continue; }
                    if (plan.X < room.Min.X || plan.Y < room.Min.Y || plan.X > room.Max.X || plan.Y > room.Max.Y) { continue; }
                    if (Inside(room, plan)) { return r; }
                }
                return -1;
            }

            /// <summary>Even-odd test over all the room's loops (islands are holes).</summary>
            public static bool Inside(RoomInfo room, Vector2 p)
            {
                if (room.Loops == null) { return false; }
                bool inside = false;
                foreach (Vector2[] loop in room.Loops)
                {
                    if (loop == null) { continue; }
                    for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
                    {
                        Vector2 a = loop[i], b = loop[j];
                        if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X) { inside = !inside; }
                    }
                }
                return inside;
            }
        }
    }
}
