using System.Numerics;
using BimGo.Scene;

// The class belongs to the Physics namespace
namespace BimGo.Physics
{
    /// <summary>
    /// An element drawn, picked and collided with a rigid transform instead of from the static scene:
    /// a moved original (its static copy is hidden) or a clone. The geometry is always the source element's
    /// static triangles, transformed by <see cref="Model"/> (rotation about +Z through the base pivot, then translation).
    /// </summary>
    internal sealed class DynamicInstance
    {
        /// <summary>Stable id (&gt; 0). <see cref="RayHit.DynamicId"/> refers to it.</summary>
        public int Id { get; init; }

        /// <summary>The source element (index into SceneData.Elements): geometry and metadata.</summary>
        public int Element { get; init; }

        /// <summary>The source element's pivot, where its static geometry sits (scene-local).</summary>
        public Vector3 BasePivot { get; init; }

        /// <summary>True for a copy (false for a moved original).</summary>
        public bool IsClone { get; init; }

        /// <summary>For clones: the key the Revit side registers the copy under (0 for originals).</summary>
        public int CloneKey { get; init; }

        /// <summary>The Revit ElementId value; 0 while a clone is still being created in Revit.</summary>
        public long RevitId { get; set; }

        /// <summary>True once a clone has been committed (sent to Revit); uncommitted clones vanish on cancel.</summary>
        public bool Committed { get; set; }

        /// <summary>Hidden (e.g. demolished, awaiting Revit's answer). Hidden instances are not drawn, picked or collided.</summary>
        public bool Hidden { get; set; }

        /// <summary>Translation of the pivot from its base position.</summary>
        public Vector3 Offset { get; private set; }

        /// <summary>Rotation about +Z (radians, CCW).</summary>
        public float Angle { get; private set; }

        /// <summary>Base-to-current transform (row-vector convention: p' = p * Model).</summary>
        public Matrix4x4 Model { get; private set; } = Matrix4x4.Identity;

        /// <summary>Inverse of <see cref="Model"/>.</summary>
        public Matrix4x4 InverseModel { get; private set; } = Matrix4x4.Identity;

        /// <summary>World bounds of the transformed geometry.</summary>
        public Aabb WorldBounds { get; private set; }

        /// <summary>The current pivot (scene-local).</summary>
        public Vector3 Pivot => BasePivot + Offset;

        /// <summary>
        /// Sets the transform and refreshes the matrices and bounds.
        /// </summary>
        /// <param name="offset">Pivot translation.</param>
        /// <param name="angle">Rotation about +Z (radians).</param>
        /// <param name="sourceBounds">The source element's static bounds.</param>
        public void SetTransform(Vector3 offset, float angle, in Aabb sourceBounds)
        {
            Offset = offset;
            Angle = angle;
            Model = Matrix4x4.CreateTranslation(-BasePivot) * Matrix4x4.CreateRotationZ(angle) * Matrix4x4.CreateTranslation(BasePivot + offset);
            InverseModel = Matrix4x4.Invert(Model, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
            WorldBounds = TransformBounds(sourceBounds, Model);
        }

        /// <summary>
        /// The axis-aligned bounds of a transformed box.
        /// </summary>
        public static Aabb TransformBounds(in Aabb box, in Matrix4x4 m)
        {
            Aabb result = Aabb.Empty;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? box.Min.X : box.Max.X,
                    (i & 2) == 0 ? box.Min.Y : box.Max.Y,
                    (i & 4) == 0 ? box.Min.Z : box.Max.Z);
                result.Include(Vector3.Transform(corner, m));
            }
            return result;
        }
    }

    /// <summary>
    /// All dynamic instances of a session, with picking and collision against their transformed triangles.
    /// Queries reuse the static BVH: the ray or box is moved into the source element's space and the BVH is
    /// searched with a one-element mask, so no per-instance acceleration structure is needed.
    /// </summary>
    internal sealed class DynamicSet
    {
        private readonly Bvh _bvh;
        private readonly ElementRecord[] _elements;
        private readonly bool[] _groupVisible;
        private readonly bool[] _solo;
        private int[] _query = new int[256];
        private int _nextId = 1;

        /// <summary>All instances (iterate; don't modify).</summary>
        public List<DynamicInstance> Instances { get; } = new();

        /// <summary>
        /// Creates the set.
        /// </summary>
        /// <param name="bvh">The static BVH (source geometry).</param>
        /// <param name="elements">The scene's elements.</param>
        /// <param name="groupVisible">Per visibility group (category × model; shared with the session).</param>
        public DynamicSet(Bvh bvh, ElementRecord[] elements, bool[] groupVisible)
        {
            _bvh = bvh;
            _elements = elements;
            _groupVisible = groupVisible;
            _solo = new bool[elements.Length];
        }

        #region Management

        /// <summary>
        /// Adds an instance of a source element.
        /// </summary>
        public DynamicInstance Create(int element, Vector3 offset, float angle, long revitId, bool isClone, int cloneKey)
        {
            var instance = new DynamicInstance
            {
                Id = _nextId++,
                Element = element,
                BasePivot = _elements[element].Pivot,
                IsClone = isClone,
                CloneKey = cloneKey,
                RevitId = revitId,
                Committed = !isClone
            };
            instance.SetTransform(offset, angle, _elements[element].Bounds);
            Instances.Add(instance);
            return instance;
        }

        /// <summary>
        /// Updates an instance's transform.
        /// </summary>
        public void SetTransform(DynamicInstance instance, Vector3 offset, float angle)
        {
            instance.SetTransform(offset, angle, _elements[instance.Element].Bounds);
        }

        /// <summary>
        /// Removes an instance.
        /// </summary>
        public void Remove(DynamicInstance instance) => Instances.Remove(instance);

        /// <summary>
        /// Finds an instance by id (null if gone).
        /// </summary>
        public DynamicInstance Find(int id)
        {
            if (id <= 0) { return null; }
            foreach (DynamicInstance instance in Instances)
            {
                if (instance.Id == id) { return instance; }
            }
            return null;
        }

        /// <summary>
        /// The moved original of a static element, if it has one.
        /// </summary>
        public DynamicInstance FindOriginal(int element)
        {
            foreach (DynamicInstance instance in Instances)
            {
                if (!instance.IsClone && instance.Element == element) { return instance; }
            }
            return null;
        }

        /// <summary>
        /// True if the instance should be drawn / picked / collided.
        /// </summary>
        public bool IsActive(DynamicInstance instance) => !instance.Hidden && _groupVisible[Rendering.SceneBatches.GroupOf(_elements[instance.Element])];

        #endregion

        #region Queries

        /// <summary>
        /// Closest hit against the active instances.
        /// </summary>
        /// <param name="origin">Ray origin.</param>
        /// <param name="direction">Unit direction.</param>
        /// <param name="maxDistance">Maximum distance.</param>
        /// <param name="hit">The hit (Element = source element, DynamicId = instance).</param>
        /// <param name="exclude">An instance to ignore (drop to floor: the element being dropped), or null.</param>
        /// <param name="opaqueOnly">True to pass through glass (sun hours).</param>
        /// <returns>True on a hit.</returns>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, DynamicInstance exclude = null, bool opaqueOnly = false)
        {
            hit = default;
            if (Instances.Count == 0) { return false; }

            Vector3 invDir = GeoMath.Reciprocal(direction);
            float best = maxDistance;
            bool found = false;

            for (int i = 0; i < Instances.Count; i++)
            {
                DynamicInstance instance = Instances[i];
                if (!IsActive(instance) || ReferenceEquals(instance, exclude)) { continue; }
                Aabb bounds = instance.WorldBounds;
                if (!GeoMath.RayAabb(origin, invDir, bounds.Min, bounds.Max, best, out _)) { continue; }

                // Into source space (rigid transform: distances are preserved)
                Vector3 localOrigin = Vector3.Transform(origin, instance.InverseModel);
                Vector3 localDirection = Vector3.TransformNormal(direction, instance.InverseModel);

                _solo[instance.Element] = true;
                bool hitLocal = _bvh.Raycast(localOrigin, localDirection, best, _solo, out RayHit local, opaqueOnly);
                _solo[instance.Element] = false;

                if (hitLocal && local.Distance < best)
                {
                    best = local.Distance;
                    found = true;
                    hit = new RayHit
                    {
                        Distance = local.Distance,
                        Point = Vector3.Transform(local.Point, instance.Model),
                        Normal = Vector3.Normalize(Vector3.TransformNormal(local.Normal, instance.Model)),
                        Element = instance.Element,
                        DynamicId = instance.Id
                    };
                }
            }
            return found;
        }

        /// <summary>
        /// Collects world-space triangles of active instances near a box (for the capsule controller).
        /// </summary>
        /// <param name="min">Box minimum.</param>
        /// <param name="max">Box maximum.</param>
        /// <param name="buffer">Output buffer (grown if needed).</param>
        /// <returns>The number of triangles written.</returns>
        public int CollectTriangles(Vector3 min, Vector3 max, ref BvhTriangle[] buffer)
        {
            int count = 0;
            var query = new Aabb(min, max);

            for (int i = 0; i < Instances.Count; i++)
            {
                DynamicInstance instance = Instances[i];
                if (!IsActive(instance) || !instance.WorldBounds.Overlaps(query)) { continue; }

                Aabb local = DynamicInstance.TransformBounds(query, instance.InverseModel);
                _solo[instance.Element] = true;
                int found = _bvh.Query(local.Min, local.Max, ref _query, _solo);
                _solo[instance.Element] = false;

                Matrix4x4 model = instance.Model;
                for (int t = 0; t < found; t++)
                {
                    ref BvhTriangle source = ref _bvh.Triangles[_query[t]];
                    if (count == buffer.Length) { Array.Resize(ref buffer, buffer.Length * 2); }
                    buffer[count++] = new BvhTriangle
                    {
                        A = Vector3.Transform(source.A, model),
                        B = Vector3.Transform(source.B, model),
                        C = Vector3.Transform(source.C, model),
                        Element = source.Element
                    };
                }
            }
            return count;
        }

        #endregion
    }
}
