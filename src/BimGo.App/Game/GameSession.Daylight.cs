using System.Numerics;
using BimGo.Audio;
using BimGo.Physics;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// Daylight modes of the study panel (daylight round): <b>daylight factor</b> (CIE overcast sky) and
    /// <b>illuminance</b> (CIE clear sky + direct sun over a day's times). RUN prepares the skies and each room's
    /// figures (surface area, glazing, reflectances, split-flux internally reflected component), then the study casts
    /// rays from every cell a few milliseconds per frame (<see cref="SunHoursStudy"/>). An early design indicator, not
    /// a compliance simulation.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Run

        /// <summary>
        /// RUN in a daylight mode: rays, skies (overcast, or clear per time sample), the rooms' figures, then the
        /// time-sliced study.
        /// </summary>
        private void RunDaylightStudy(SunHoursSettings settings)
        {
            var inputs = new DaylightInputs
            {
                Mode = settings.Mode,
                Classify = ClassifyDaylightRay,
                Rays = Daylight.CosineDirections(settings.Rays),
                DirectSun = settings.DirectSun,
                LuxTarget = settings.LuxTarget,
                CellArea = _sunStudy.Settings.GridSize * _sunStudy.Settings.GridSize
            };

            bool known = true;
            if (settings.Mode == StudyMode.DaylightFactor)
            {
                inputs.Overcast = new float[Daylight.PATCHES];
                Daylight.OvercastPatches(inputs.Overcast);
                inputs.Suns = Array.Empty<Vector3>();
            }
            else
            {
                List<Vector3> suns = SunHours.SunDirections(Scene.Site, DateTime.Today.Year, settings, out int total, out known);
                inputs.TotalSamples = total;
                inputs.Suns = suns.ToArray();
                inputs.Clear = new float[suns.Count][];
                inputs.Diffuse = new float[suns.Count];
                inputs.DirectNormal = new float[suns.Count];
                for (int s = 0; s < suns.Count; s++)
                {
                    inputs.Clear[s] = new float[Daylight.PATCHES];
                    inputs.Diffuse[s] = (float)Daylight.ClearPatches(suns[s], inputs.Clear[s]);
                    inputs.DirectNormal[s] = (float)Daylight.DirectNormalClear(suns[s].Z);
                }
            }

            // Each room a cell is clipped to
            inputs.Rooms = new RoomLight[Scene.Rooms.Length];
            int rooms = 0;
            foreach (SunHoursFace face in _sunStudy.Faces)
            {
                if (face.Room < 0 || face.Room >= Scene.Rooms.Length || inputs.Rooms[face.Room].Valid) { continue; }
                inputs.Rooms[face.Room] = ComputeRoomLight(face.Room, settings.StandardReflectance);
                rooms++;
            }
            _sunRoomLight = inputs.Rooms;
            _sunRoomNote = RoomNote(inputs.Rooms, rooms);

            if (!_sunStudy.StartDaylight(inputs, settings))
            {
                _sunNotice = $"Too many cells × time samples for one illuminance run: pick a larger grid or a longer step (at most {SunHoursStudy.MAX_LUX_VALUES / 1_000_000} million)";
                Sound.Play(SoundId.Error);
                return;
            }
            _sunSummary = null;
            _sunPassLabel = _sunFailLabel = null;
            _sunStale = false;
            _sunLegendTitle = SunLegendTitle(settings);
            _sunNotice = settings.Mode == StudyMode.Illuminance && inputs.Suns.Length == 0
                ? "The sun is below the horizon for the whole range: every cell gets 0 lux"
                : !known ? "No site location in this model: Sydney assumed (set Revit's Location)"
                : rooms == 0 ? "No room under these surfaces: no internally reflected light is added (values are low)"
                : null;
            Utilities.Log_Utils.Write($"{ModeName(settings.Mode)} run: {_sunStudy.CellCount:N0} cells, {settings.Rays} rays, {inputs.Suns.Length} time samples, {rooms} rooms. {_sunRoomNote}");
            Sound.Play(SoundId.UiClick);
        }

        /// <summary>"glazing 4.2 m² · R 0.46 · IRC 1.3 %" for one room, or the number of rooms.</summary>
        private static string RoomNote(RoomLight[] rooms, int count)
        {
            if (count != 1) { return count == 0 ? null : $"{count} rooms"; }
            foreach (RoomLight light in rooms)
            {
                if (light.Valid) { return $"glazing {light.WindowArea:0.0} m² · R {light.AverageReflectance:0.00} · IRC {light.IrcPercent:0.0} %"; }
            }
            return null;
        }

        #endregion

        #region Rays

        /// <summary>
        /// Follows a daylight ray: the first opaque surface (static scene, then moved / placed elements) decides
        /// between the cell's own room (internal: left to the IRC) and outside surfaces; nothing in the way means sky
        /// (above the horizon) or ground. Glass in front of where it ends multiplies by the glass transmittance.
        /// </summary>
        private RayOutcome ClassifyDaylightRay(Vector3 origin, Vector3 direction, int room, out float transmit)
        {
            const float MAX = 1500f;
            transmit = 1f;
            bool opaque = _bvh.Raycast(origin, direction, MAX, _pickMask, out RayHit hit, opaqueOnly: true);
            float end = opaque ? hit.Distance : MAX;
            if (Dynamics != null && Dynamics.Raycast(origin, direction, end, out RayHit moved, null, opaqueOnly: true))
            {
                opaque = true;
                hit = moved;
                end = moved.Distance;
            }

            // Anything hit before the opaque surface (or the sky) is see-through: glass
            if (end > 0.002f && _bvh.Raycast(origin, direction, end - 0.001f, _pickMask, out RayHit _, opaqueOnly: false)) { transmit = Daylight.GLASS_VLT; }

            if (opaque) { return IsInsideRoom(room, hit.Point) ? RayOutcome.Internal : RayOutcome.External; }
            return direction.Z > 0f ? RayOutcome.Sky : RayOutcome.Ground;
        }

        /// <summary>
        /// True when a point belongs to a room's own surfaces: in its plan (or within 0.45 m of its boundary, for walls
        /// of rooms bounded at wall centres or finishes) and between its floor and a little above its top.
        /// </summary>
        private bool IsInsideRoom(int room, Vector3 point)
        {
            if (room < 0 || room >= Scene.Rooms.Length) { return false; }
            RoomInfo r = Scene.Rooms[room];
            if (point.Z < r.BottomZ - 0.3f || point.Z > r.TopZ + 1.0f) { return false; }
            var plan = new Vector2(point.X, point.Y);
            return r.Contains(plan) || r.DistanceToBoundary(plan) < 0.45f;
        }

        #endregion

        #region Rooms

        /// <summary>
        /// A room's daylight figures: floor area and perimeter from its boundary, its height, the surface total; the
        /// glazed area (see-through triangles in its walls and over it, halved as panes have two faces); reflectances
        /// from the colours of its floor, walls and ceiling (or standard ones, and standard where nothing was found);
        /// and the split-flux IRC.
        /// </summary>
        private RoomLight ComputeRoomLight(int roomIndex, bool standard)
        {
            RoomInfo room = Scene.Rooms[roomIndex];
            double floorArea = 0, perimeter = 0;
            foreach (Vector2[] loop in room.Loops ?? Array.Empty<Vector2[]>())
            {
                if (loop == null || loop.Length < 3) { continue; }
                double signed = 0;
                for (int i = 0; i < loop.Length; i++)
                {
                    Vector2 a = loop[i], b = loop[(i + 1) % loop.Length];
                    signed += (double)a.X * b.Y - (double)b.X * a.Y;
                    perimeter += Vector2.Distance(a, b);
                }
                floorArea += signed * 0.5;
            }
            floorArea = Math.Abs(floorArea);
            float height = room.TopZ - room.BottomZ;
            if (!float.IsFinite(height) || height < 2f || height > 8f) { height = 2.7f; }
            if (floorArea < 0.5) { return default; }
            double wallArea = perimeter * height;

            int walls = CategoryCatalog.Find("walls")?.Index ?? -1;
            int floors = CategoryCatalog.Find("floors")?.Index ?? -1;
            var box = new Aabb(new Vector3(room.Min - new Vector2(0.6f), room.BottomZ - 0.5f), new Vector3(room.Max + new Vector2(0.6f), room.TopZ + 1.5f));
            double glass = 0;
            double floorSum = 0, floorWeight = 0, wallSum = 0, wallWeight = 0, ceilingSum = 0, ceilingWeight = 0;
            ElementRecord[] elements = Scene.Elements;
            SceneVertex[] vertices = Scene.Vertices;
            uint[] indices = Scene.Indices;

            for (int e = 0; e < elements.Length; e++)
            {
                ElementRecord record = elements[e];
                if (!_pickMask[e] || record.IsLibraryTemplate || !record.Bounds.Overlaps(box)) { continue; }

                // Glazing: see-through triangles in the room's walls or over it
                for (int i = record.TransparentStart; i + 2 < record.TransparentStart + record.TransparentCount; i += 3)
                {
                    Triangle(vertices, indices, i, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 normal, out float area);
                    Vector3 centre = (a + b + c) / 3f;
                    if (centre.Z < room.BottomZ - 0.1f || centre.Z > room.TopZ + 1.5f) { continue; }
                    var plan = new Vector2(centre.X, centre.Y);
                    bool vertical = MathF.Abs(normal.Z) < 0.5f;
                    if ((vertical && (room.Contains(plan) || room.DistanceToBoundary(plan) < 0.5f)) || (!vertical && room.Contains(plan) && centre.Z > room.BottomZ + 1.5f))
                    {
                        glass += area;
                    }
                }

                if (standard) { continue; }

                // Reflectances: area-weighted colours of the floor, the walls and whatever is overhead
                bool isWall = record.CategoryIndex == walls, isFloor = record.CategoryIndex == floors;
                for (int i = record.OpaqueStart; i + 2 < record.OpaqueStart + record.OpaqueCount; i += 3)
                {
                    Triangle(vertices, indices, i, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 normal, out float area);
                    Vector3 centre = (a + b + c) / 3f;
                    var plan = new Vector2(centre.X, centre.Y);
                    uint colour = vertices[indices[i]].Colour;
                    float reflectance = Daylight.ReflectanceOf((byte)colour, (byte)(colour >> 8), (byte)(colour >> 16));

                    if (isFloor && normal.Z > 0.7f && MathF.Abs(centre.Z - room.BottomZ) < 0.15f && room.Contains(plan))
                    {
                        floorSum += reflectance * area;
                        floorWeight += area;
                    }
                    else if (isWall && MathF.Abs(normal.Z) < 0.3f && centre.Z > room.BottomZ - 0.1f && centre.Z < room.TopZ + 0.3f && room.DistanceToBoundary(plan) < 0.45f)
                    {
                        wallSum += reflectance * area;
                        wallWeight += area;
                    }
                    else if (normal.Z < -0.7f && centre.Z > room.BottomZ + 1.8f && centre.Z < room.TopZ + 1.5f && room.Contains(plan))
                    {
                        ceilingSum += reflectance * area;
                        ceilingWeight += area;
                    }
                }
            }

            var light = new RoomLight
            {
                Valid = true,
                WindowArea = (float)(glass * 0.5),
                FloorReflectance = floorWeight > 0 ? (float)(floorSum / floorWeight) : Daylight.FLOOR_REFLECTANCE,
                WallReflectance = wallWeight > 0 ? (float)(wallSum / wallWeight) : Daylight.WALL_REFLECTANCE,
                CeilingReflectance = ceilingWeight > 0 ? (float)(ceilingSum / ceilingWeight) : Daylight.CEILING_REFLECTANCE
            };
            double windows = Math.Min(light.WindowArea, wallArea * 0.9);
            double total = 2.0 * floorArea + wallArea;
            light.TotalArea = (float)total;
            light.AverageReflectance = (float)((light.FloorReflectance * floorArea + light.CeilingReflectance * floorArea
                + light.WallReflectance * (wallArea - windows) + 0.1 * windows) / total);
            double halfWall = wallArea * 0.5;
            light.LowerReflectance = (float)((light.FloorReflectance * floorArea + light.WallReflectance * halfWall) / (floorArea + halfWall));
            light.UpperReflectance = (float)((light.CeilingReflectance * floorArea + light.WallReflectance * halfWall) / (floorArea + halfWall));
            light.IrcPercent = Daylight.InternalReflectedPercent(windows, total, light.AverageReflectance, light.LowerReflectance, light.UpperReflectance);
            return light;
        }

        /// <summary>A triangle's corners, unit normal and area.</summary>
        private static void Triangle(SceneVertex[] vertices, uint[] indices, int i, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 normal, out float area)
        {
            a = vertices[indices[i]].Position;
            b = vertices[indices[i + 1]].Position;
            c = vertices[indices[i + 2]].Position;
            Vector3 cross = Vector3.Cross(b - a, c - a);
            float length = cross.Length();
            area = length * 0.5f;
            normal = length > 1e-9f ? cross / length : Vector3.UnitZ;
        }

        #endregion
    }
}
