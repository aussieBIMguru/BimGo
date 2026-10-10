using System.Numerics;
using BimGo.Scene;

// The class belongs to the Physics namespace
namespace BimGo.Physics
{
    /// <summary>
    /// A triangle stored in the BVH.
    /// </summary>
    internal struct BvhTriangle
    {
        public Vector3 A, B, C;

        /// <summary>Index into SceneData.Elements.</summary>
        public int Element;
    }

    /// <summary>
    /// A BVH node: leaf when Count &gt; 0 (triangles First..First+Count), else children at Left and Left+1.
    /// </summary>
    internal struct BvhNode
    {
        public Vector3 Min;
        public int LeftOrFirst;
        public Vector3 Max;
        public int Count;
    }

    /// <summary>
    /// The result of a ray cast.
    /// </summary>
    internal struct RayHit
    {
        /// <summary>Distance along the (unit) ray.</summary>
        public float Distance;

        /// <summary>Hit point.</summary>
        public Vector3 Point;

        /// <summary>Surface normal facing the ray origin.</summary>
        public Vector3 Normal;

        /// <summary>Element index (SceneData.Elements). For dynamic hits, the instance's source element.</summary>
        public int Element;

        /// <summary>The <see cref="DynamicInstance"/> hit, or 0 for the static scene.</summary>
        public int DynamicId;
    }

    /// <summary>
    /// A static triangle BVH over the whole scene, shared by collision and picking.
    /// Built once on the game thread with median splits (quickselect), queried without allocation.
    /// </summary>
    internal sealed class Bvh
    {
        private const int LEAF_SIZE = 4;

        /// <summary>Triangles (re-ordered so leaves are contiguous).</summary>
        public BvhTriangle[] Triangles { get; }

        /// <summary>
        /// Per triangle (same order as <see cref="Triangles"/>): true when it is glass (the element's transparent
        /// range). Ray casts with <c>opaqueOnly</c> pass through these (sun hours: sun through glazing).
        /// </summary>
        public bool[] Transparent { get; }

        private readonly BvhNode[] _nodes;
        private readonly int[] _stack = new int[256];

        /// <summary>Number of nodes used.</summary>
        public int NodeCount { get; }

        /// <summary>
        /// Builds the BVH from every static triangle in the scene.
        /// </summary>
        public Bvh(SceneData scene)
        {
            SceneVertex[] vertices = scene.Vertices;
            uint[] indices = scene.Indices;
            ElementRecord[] elements = scene.Elements;

            // Gather triangles element by element so each knows its owner
            var triangles = new BvhTriangle[indices.Length / 3];
            var transparent = new bool[triangles.Length];
            int count = 0;
            for (int e = 0; e < elements.Length; e++)
            {
                ElementRecord record = elements[e];
                AddRange(record.OpaqueStart, record.OpaqueCount, e, false);
                AddRange(record.TransparentStart, record.TransparentCount, e, true);
            }

            void AddRange(int start, int length, int element, bool glass)
            {
                for (int i = start; i + 2 < start + length; i += 3)
                {
                    transparent[count] = glass;
                    triangles[count++] = new BvhTriangle
                    {
                        A = vertices[indices[i]].Position,
                        B = vertices[indices[i + 1]].Position,
                        C = vertices[indices[i + 2]].Position,
                        Element = element
                    };
                }
            }

            if (count < triangles.Length) { Array.Resize(ref triangles, count); }

            // Build
            var centroids = new Vector3[count];
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                centroids[i] = (triangles[i].A + triangles[i].B + triangles[i].C) * (1f / 3f);
                order[i] = i;
            }

            _nodes = new BvhNode[Math.Max(1, 2 * count)];
            int nodeCount = 1;
            _nodes[0] = new BvhNode { LeftOrFirst = 0, Count = count };

            var work = new Stack<int>();
            work.Push(0);
            while (work.Count > 0)
            {
                int nodeIndex = work.Pop();
                ref BvhNode node = ref _nodes[nodeIndex];
                int first = node.LeftOrFirst, n = node.Count;

                // Bounds
                Vector3 min = new(float.MaxValue), max = new(float.MinValue);
                Vector3 cmin = new(float.MaxValue), cmax = new(float.MinValue);
                for (int i = first; i < first + n; i++)
                {
                    ref BvhTriangle t = ref triangles[order[i]];
                    min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
                    max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
                    cmin = Vector3.Min(cmin, centroids[order[i]]);
                    cmax = Vector3.Max(cmax, centroids[order[i]]);
                }
                if (n == 0) { min = max = Vector3.Zero; }
                node.Min = min;
                node.Max = max;

                if (n <= LEAF_SIZE) { continue; }

                Vector3 extent = cmax - cmin;
                int axis = extent.X > extent.Y ? (extent.X > extent.Z ? 0 : 2) : (extent.Y > extent.Z ? 1 : 2);
                if (Component(extent, axis) < 1e-6f) { continue; }

                int mid = first + n / 2;
                NthElement(order, centroids, axis, first, first + n - 1, mid);

                int left = nodeCount;
                nodeCount += 2;
                _nodes[left] = new BvhNode { LeftOrFirst = first, Count = mid - first };
                _nodes[left + 1] = new BvhNode { LeftOrFirst = mid, Count = first + n - mid };

                // Re-fetch: _nodes is not resized, the ref is still valid, but be explicit
                _nodes[nodeIndex].LeftOrFirst = left;
                _nodes[nodeIndex].Count = 0;

                work.Push(left);
                work.Push(left + 1);
            }
            NodeCount = nodeCount;

            // Re-order triangles to match leaf ranges
            var sorted = new BvhTriangle[count];
            var sortedTransparent = new bool[count];
            for (int i = 0; i < count; i++)
            {
                sorted[i] = triangles[order[i]];
                sortedTransparent[i] = transparent[order[i]];
            }
            Triangles = sorted;
            Transparent = sortedTransparent;
        }

        private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        /// <summary>
        /// Partially sorts so the nth element is in place (Hoare quickselect on centroid axis).
        /// </summary>
        private static void NthElement(int[] order, Vector3[] centroids, int axis, int left, int right, int nth)
        {
            while (right > left)
            {
                float pivot = Component(centroids[order[(left + right) >> 1]], axis);
                int i = left, j = right;
                while (i <= j)
                {
                    while (Component(centroids[order[i]], axis) < pivot) { i++; }
                    while (Component(centroids[order[j]], axis) > pivot) { j--; }
                    if (i <= j)
                    {
                        (order[i], order[j]) = (order[j], order[i]);
                        i++;
                        j--;
                    }
                }

                if (nth <= j) { right = j; }
                else if (nth >= i) { left = i; }
                else { return; }
            }
        }

        #region Queries

        /// <summary>
        /// Collects triangle indices whose node boxes overlap the query box.
        /// </summary>
        /// <param name="min">Query minimum.</param>
        /// <param name="max">Query maximum.</param>
        /// <param name="results">Output buffer (grown if needed).</param>
        /// <param name="mask">Per element: include? (null = all).</param>
        /// <returns>The number of triangle indices written.</returns>
        public int Query(Vector3 min, Vector3 max, ref int[] results, bool[] mask)
        {
            if (Triangles.Length == 0) { return 0; }

            int found = 0, top = 0;
            _stack[top++] = 0;
            while (top > 0)
            {
                ref BvhNode node = ref _nodes[_stack[--top]];
                if (node.Max.X < min.X || node.Min.X > max.X || node.Max.Y < min.Y || node.Min.Y > max.Y || node.Max.Z < min.Z || node.Min.Z > max.Z)
                {
                    continue;
                }

                if (node.Count > 0)
                {
                    for (int i = node.LeftOrFirst; i < node.LeftOrFirst + node.Count; i++)
                    {
                        if (mask != null && !mask[Triangles[i].Element]) { continue; }
                        if (found == results.Length) { Array.Resize(ref results, results.Length * 2); }
                        results[found++] = i;
                    }
                }
                else if (top + 2 <= _stack.Length)
                {
                    _stack[top++] = node.LeftOrFirst;
                    _stack[top++] = node.LeftOrFirst + 1;
                }
            }
            return found;
        }

        /// <summary>
        /// Closest hit along a ray.
        /// </summary>
        /// <param name="origin">Ray origin.</param>
        /// <param name="direction">Unit direction.</param>
        /// <param name="maxDistance">Maximum distance.</param>
        /// <param name="mask">Per element: include? (null = all).</param>
        /// <param name="hit">The hit.</param>
        /// <param name="opaqueOnly">True to pass through glass (transparent triangles).</param>
        /// <returns>True on a hit.</returns>
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, bool[] mask, out RayHit hit, bool opaqueOnly = false)
        {
            hit = default;
            if (Triangles.Length == 0) { return false; }

            Vector3 invDir = GeoMath.Reciprocal(direction);
            float best = maxDistance;
            int bestTriangle = -1;

            int top = 0;
            _stack[top++] = 0;
            while (top > 0)
            {
                ref BvhNode node = ref _nodes[_stack[--top]];
                if (!GeoMath.RayAabb(origin, invDir, node.Min, node.Max, best, out _)) { continue; }

                if (node.Count > 0)
                {
                    for (int i = node.LeftOrFirst; i < node.LeftOrFirst + node.Count; i++)
                    {
                        ref BvhTriangle t = ref Triangles[i];
                        if (mask != null && !mask[t.Element]) { continue; }
                        if (opaqueOnly && Transparent[i]) { continue; }
                        if (GeoMath.RayTriangle(origin, direction, t.A, t.B, t.C, best, out float distance) && distance > 1e-4f)
                        {
                            best = distance;
                            bestTriangle = i;
                        }
                    }
                }
                else if (top + 2 <= _stack.Length)
                {
                    // Visit the nearer child first
                    int left = node.LeftOrFirst, right = left + 1;
                    bool hitLeft = GeoMath.RayAabb(origin, invDir, _nodes[left].Min, _nodes[left].Max, best, out float tl);
                    bool hitRight = GeoMath.RayAabb(origin, invDir, _nodes[right].Min, _nodes[right].Max, best, out float tr);
                    if (hitLeft && hitRight)
                    {
                        if (tl <= tr) { _stack[top++] = right; _stack[top++] = left; }
                        else { _stack[top++] = left; _stack[top++] = right; }
                    }
                    else if (hitLeft) { _stack[top++] = left; }
                    else if (hitRight) { _stack[top++] = right; }
                }
            }

            if (bestTriangle < 0) { return false; }

            ref BvhTriangle tri = ref Triangles[bestTriangle];
            Vector3 normal = Vector3.Normalize(Vector3.Cross(tri.B - tri.A, tri.C - tri.A));
            if (Vector3.Dot(normal, direction) > 0f) { normal = -normal; }

            hit = new RayHit
            {
                Distance = best,
                Point = origin + direction * best,
                Normal = normal,
                Element = tri.Element
            };
            return true;
        }

        #endregion
    }
}
