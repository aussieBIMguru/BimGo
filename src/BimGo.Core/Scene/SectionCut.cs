using System.Numerics;
using System.Text.Json.Serialization;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// A section cut (section box round): an axis-aligned section box and / or one free clipping plane. Coordinates are
    /// Revit internal metres (like comments and bookmarks), so a cut survives re-extraction. Geometry outside the box,
    /// and on the plane's <see cref="PlaneNormal"/> side, is cut away. Saved with the model (visibility.json), in
    /// bookmarks and comment views, and as BCF clipping planes.
    /// </summary>
    public sealed class SectionCut
    {
        /// <summary>Most clipping planes a cut makes (6 box faces + the free plane).</summary>
        public const int MAX_PLANES = 7;

        /// <summary>Smallest box size along any axis (m).</summary>
        public const float MIN_SIZE = 0.2f;

        /// <summary>True when the section box cuts.</summary>
        public bool BoxOn { get; set; }

        /// <summary>The box's minimum corner (internal metres).</summary>
        public float MinX { get; set; }
        public float MinY { get; set; }
        public float MinZ { get; set; }

        /// <summary>The box's maximum corner (internal metres).</summary>
        public float MaxX { get; set; }
        public float MaxY { get; set; }
        public float MaxZ { get; set; }

        /// <summary>True when the free plane cuts.</summary>
        public bool PlaneOn { get; set; }

        /// <summary>A point on the free plane (internal metres).</summary>
        public float PlaneX { get; set; }
        public float PlaneY { get; set; }
        public float PlaneZ { get; set; }

        /// <summary>The free plane's unit normal, pointing into the side that is cut away (as BCF's direction).</summary>
        public float NormalX { get; set; }
        public float NormalY { get; set; }
        public float NormalZ { get; set; } = 1f;

        /// <summary>True when anything cuts.</summary>
        [JsonIgnore]
        public bool IsActive => BoxOn || PlaneOn;

        /// <summary>The box minimum.</summary>
        [JsonIgnore]
        public Vector3 BoxMin
        {
            get => new(MinX, MinY, MinZ);
            set { MinX = value.X; MinY = value.Y; MinZ = value.Z; }
        }

        /// <summary>The box maximum.</summary>
        [JsonIgnore]
        public Vector3 BoxMax
        {
            get => new(MaxX, MaxY, MaxZ);
            set { MaxX = value.X; MaxY = value.Y; MaxZ = value.Z; }
        }

        /// <summary>The free plane's point.</summary>
        [JsonIgnore]
        public Vector3 PlanePoint
        {
            get => new(PlaneX, PlaneY, PlaneZ);
            set { PlaneX = value.X; PlaneY = value.Y; PlaneZ = value.Z; }
        }

        /// <summary>The free plane's normal (towards the cut-away side).</summary>
        [JsonIgnore]
        public Vector3 PlaneNormal
        {
            get => new(NormalX, NormalY, NormalZ);
            set { NormalX = value.X; NormalY = value.Y; NormalZ = value.Z; }
        }

        /// <summary>A copy.</summary>
        public SectionCut Clone() => (SectionCut)MemberwiseClone();

        /// <summary>
        /// Makes the cut sane: finite numbers, box corners ordered and at least <see cref="MIN_SIZE"/> apart, a unit
        /// plane normal (a zero normal switches the plane off).
        /// </summary>
        /// <returns>This instance.</returns>
        public SectionCut Clean()
        {
            Vector3 a = Finite(BoxMin), b = Finite(BoxMax);
            Vector3 min = Vector3.Min(a, b), max = Vector3.Max(a, b);
            for (int axis = 0; axis < 3; axis++)
            {
                if (max[axis] - min[axis] < MIN_SIZE) { max[axis] = min[axis] + MIN_SIZE; }
            }
            BoxMin = min;
            BoxMax = max;

            PlanePoint = Finite(PlanePoint);
            Vector3 n = Finite(PlaneNormal);
            if (n.LengthSquared() < 1e-8f)
            {
                PlaneOn = false;
                n = Vector3.UnitZ;
            }
            PlaneNormal = Vector3.Normalize(n);
            return this;

            static Vector3 Finite(Vector3 v) => new(float.IsFinite(v.X) ? v.X : 0f, float.IsFinite(v.Y) ? v.Y : 0f, float.IsFinite(v.Z) ? v.Z : 0f);
        }

        /// <summary>
        /// The cut's planes in scene-local coordinates for the shaders: (n, d) with points where n·p > d cut away.
        /// Box faces first (+X, −X, +Y, −Y, +Z, −Z), then the free plane.
        /// </summary>
        /// <param name="origin">The scene origin offset (internal = local + origin).</param>
        /// <param name="planes">Filled from index 0 (length ≥ <see cref="MAX_PLANES"/>).</param>
        /// <returns>The number of planes written.</returns>
        public int LocalPlanes(Vector3 origin, Vector4[] planes)
        {
            int count = 0;
            if (BoxOn)
            {
                Vector3 min = BoxMin - origin, max = BoxMax - origin;
                planes[count++] = new Vector4(1, 0, 0, max.X);
                planes[count++] = new Vector4(-1, 0, 0, -min.X);
                planes[count++] = new Vector4(0, 1, 0, max.Y);
                planes[count++] = new Vector4(0, -1, 0, -min.Y);
                planes[count++] = new Vector4(0, 0, 1, max.Z);
                planes[count++] = new Vector4(0, 0, -1, -min.Z);
            }
            if (PlaneOn)
            {
                Vector3 n = PlaneNormal;
                planes[count++] = new Vector4(n, Vector3.Dot(n, PlanePoint - origin));
            }
            return count;
        }

        /// <summary>True when a scene-local point is cut away by these planes.</summary>
        public static bool IsCut(Vector4[] planes, int count, Vector3 point)
        {
            for (int i = 0; i < count; i++)
            {
                Vector4 p = planes[i];
                if (p.X * point.X + p.Y * point.Y + p.Z * point.Z > p.W) { return true; }
            }
            return false;
        }

        /// <summary>
        /// A cut rebuilt from clipping planes given as (point, direction towards the cut side), internal metres: six
        /// axis-aligned planes facing ±X, ±Y and ±Z (one each) make a box; any other plane becomes the free plane (the
        /// first one; further odd planes are dropped). Null when there are none.
        /// </summary>
        /// <param name="planes">The planes.</param>
        /// <param name="dropped">Out: planes that couldn't be represented.</param>
        public static SectionCut FromPlanes(IReadOnlyList<(Vector3 Point, Vector3 Direction)> planes, out int dropped)
        {
            dropped = 0;
            if (planes == null || planes.Count == 0) { return null; }
            var cut = new SectionCut();
            float?[] faces = new float?[6]; // +X −X +Y −Y +Z −Z
            var others = new List<(Vector3 Point, Vector3 Direction)>();
            foreach ((Vector3 point, Vector3 direction) in planes)
            {
                if (direction.LengthSquared() < 1e-8f) { dropped++; continue; }
                Vector3 n = Vector3.Normalize(direction);
                int face = AxisFace(n);
                if (face >= 0 && faces[face] == null) { faces[face] = point[face / 2]; }
                else { others.Add((point, n)); }
            }

            if (faces.All(f => f.HasValue))
            {
                cut.BoxOn = true;
                cut.BoxMin = new Vector3(faces[1].Value, faces[3].Value, faces[5].Value);
                cut.BoxMax = new Vector3(faces[0].Value, faces[2].Value, faces[4].Value);
            }
            else
            {
                // An incomplete box: its planes are free planes too
                for (int f = 0; f < 6; f++)
                {
                    if (!faces[f].HasValue) { continue; }
                    var n = new Vector3(f / 2 == 0 ? 1 : 0, f / 2 == 1 ? 1 : 0, f / 2 == 2 ? 1 : 0) * (f % 2 == 0 ? 1f : -1f);
                    Vector3 p = Vector3.Zero;
                    p[f / 2] = faces[f].Value;
                    others.Insert(0, (p, n));
                }
            }

            if (others.Count > 0)
            {
                cut.PlaneOn = true;
                cut.PlanePoint = others[0].Point;
                cut.PlaneNormal = others[0].Direction;
                dropped += others.Count - 1;
            }
            return cut.IsActive ? cut.Clean() : null;

            static int AxisFace(Vector3 n)
            {
                for (int axis = 0; axis < 3; axis++)
                {
                    if (MathF.Abs(n[axis]) > 0.9999f) { return axis * 2 + (n[axis] > 0 ? 0 : 1); }
                }
                return -1;
            }
        }

        /// <summary>
        /// The cut as clipping planes (point, direction towards the cut side), internal metres: the box's six faces, then
        /// the free plane.
        /// </summary>
        public List<(Vector3 Point, Vector3 Direction)> ToPlanes()
        {
            var planes = new List<(Vector3, Vector3)>();
            if (BoxOn)
            {
                Vector3 min = BoxMin, max = BoxMax;
                planes.Add((new Vector3(max.X, min.Y, min.Z), Vector3.UnitX));
                planes.Add((min, -Vector3.UnitX));
                planes.Add((new Vector3(min.X, max.Y, min.Z), Vector3.UnitY));
                planes.Add((min, -Vector3.UnitY));
                planes.Add((new Vector3(min.X, min.Y, max.Z), Vector3.UnitZ));
                planes.Add((min, -Vector3.UnitZ));
            }
            if (PlaneOn) { planes.Add((PlanePoint, PlaneNormal)); }
            return planes;
        }
    }
}
