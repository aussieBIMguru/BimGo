using System.Numerics;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// A spatially coherent run of elements inside one batch, culled as a unit.
    /// </summary>
    internal struct RenderChunk
    {
        /// <summary>Bounds of the chunk's elements.</summary>
        public Aabb Bounds;

        /// <summary>First index in the final index buffer.</summary>
        public int IndexStart;

        /// <summary>Number of indices.</summary>
        public int IndexCount;
    }

    /// <summary>
    /// All chunks of one category of one model (host or link) in one pass (opaque or transparent): drawn with a single
    /// multi-draw call.
    /// </summary>
    internal struct RenderBatch
    {
        /// <summary>Catalog index of the category.</summary>
        public int CategoryIndex;

        /// <summary>
        /// The visibility group (<see cref="SceneBatches.GroupOf"/>): category and model together, so category and
        /// link toggles share one lookup.
        /// </summary>
        public int Group;

        /// <summary>True for the transparent pass.</summary>
        public bool Transparent;

        /// <summary>First chunk.</summary>
        public int ChunkStart;

        /// <summary>Chunk count.</summary>
        public int ChunkCount;
    }

    /// <summary>
    /// An element's triangle ranges in the final (re-ordered) index buffer, used for highlighting.
    /// </summary>
    internal struct ElementRange
    {
        /// <summary>Opaque start.</summary>
        public int OpaqueStart;

        /// <summary>Opaque index count.</summary>
        public int OpaqueCount;

        /// <summary>Transparent start.</summary>
        public int TransparentStart;

        /// <summary>Transparent index count.</summary>
        public int TransparentCount;
    }

    /// <summary>
    /// Re-orders the snapshot's indices into per-model, per-category, per-pass batches of spatial chunks.
    /// Runs once on the game thread at startup.
    /// </summary>
    internal sealed class SceneBatches
    {
        /// <summary>
        /// The visibility group of an element: <c>link × categories + category</c> (the host is link 0). Index the
        /// session's group visibility array with it (see <see cref="GroupCount"/>).
        /// </summary>
        public static int GroupOf(ElementRecord record) => record.Link * CategoryCatalog.All.Count + record.CategoryIndex;

        /// <summary>The number of visibility groups of a scene (categories × (links + 1)).</summary>
        public static int GroupCount(SceneData scene) => CategoryCatalog.All.Count * (scene.Links.Length + 1);

        private const int MAX_ELEMENTS_PER_CHUNK = 48;
        private const int MAX_INDICES_PER_CHUNK = 96_000;
        private const float MAX_CHUNK_EXTENT = 14f;

        /// <summary>The re-ordered index buffer.</summary>
        public uint[] Indices { get; }

        /// <summary>All chunks.</summary>
        public RenderChunk[] Chunks { get; }

        /// <summary>All batches (opaque first, then transparent).</summary>
        public RenderBatch[] Batches { get; }

        /// <summary>Per element ranges in <see cref="Indices"/>.</summary>
        public ElementRange[] Ranges { get; }

        /// <summary>
        /// Builds the batches.
        /// </summary>
        /// <param name="scene">The snapshot.</param>
        public SceneBatches(SceneData scene)
        {
            ElementRecord[] elements = scene.Elements;
            uint[] source = scene.Indices;
            var indices = new uint[source.Length];
            var chunks = new List<RenderChunk>();
            var batches = new List<RenderBatch>();
            Ranges = new ElementRange[elements.Length];
            int write = 0;

            // Scene-wide quantisation for sort keys
            Aabb bounds = scene.Bounds;
            Vector3 size = Vector3.Max(bounds.Size, new Vector3(1f));

            int categoryCount = CategoryCatalog.All.Count;
            int groupCount = GroupCount(scene);
            var buckets = new List<int>[groupCount];

            for (int pass = 0; pass < 2; pass++)
            {
                bool transparent = pass == 1;

                // Bucket the pass's elements by group once (host groups first, then each link's)
                foreach (List<int> bucket in buckets) { bucket?.Clear(); }
                for (int e = 0; e < elements.Length; e++)
                {
                    ElementRecord record = elements[e];
                    if ((transparent ? record.TransparentCount : record.OpaqueCount) == 0) { continue; }
                    if (record.IsLibraryTemplate) { continue; } // never drawn statically (see below)
                    int g = GroupOf(record);
                    if ((uint)g >= (uint)groupCount) { continue; }
                    (buckets[g] ??= new List<int>()).Add(e);
                }

                for (int group = 0; group < groupCount; group++)
                {
                    List<int> members = buckets[group];
                    if (members == null || members.Count == 0) { continue; }
                    int category = group % categoryCount;

                    // Sort by Morton order of the element centres (z in coarse slabs)
                    ulong[] keys = new ulong[members.Count];
                    int[] order = members.ToArray();
                    for (int i = 0; i < order.Length; i++)
                    {
                        Vector3 c = (elements[order[i]].Bounds.Center - bounds.Min) / size;
                        uint qx = (uint)Math.Clamp(c.X * 1023f, 0f, 1023f);
                        uint qy = (uint)Math.Clamp(c.Y * 1023f, 0f, 1023f);
                        uint qz = (uint)Math.Clamp((elements[order[i]].Bounds.Center.Z - bounds.Min.Z) / 3f, 0f, 1023f);
                        keys[i] = ((ulong)qz << 20) | Morton2D(qx, qy);
                    }
                    Array.Sort(keys, order);

                    var batch = new RenderBatch { CategoryIndex = category, Group = group, Transparent = transparent, ChunkStart = chunks.Count };
                    RenderChunk chunk = default;
                    int chunkElements = 0;

                    foreach (int e in order)
                    {
                        ElementRecord record = elements[e];
                        int start = transparent ? record.TransparentStart : record.OpaqueStart;
                        int count = transparent ? record.TransparentCount : record.OpaqueCount;

                        // Close the current chunk if this element would make it too big or too spread out
                        if (chunkElements > 0)
                        {
                            Aabb grown = chunk.Bounds;
                            grown.Include(record.Bounds);
                            Vector3 extent = grown.Size;
                            if (chunkElements >= MAX_ELEMENTS_PER_CHUNK
                                || chunk.IndexCount + count > MAX_INDICES_PER_CHUNK
                                || MathF.Max(extent.X, extent.Y) > MAX_CHUNK_EXTENT)
                            {
                                chunks.Add(chunk);
                                chunkElements = 0;
                            }
                        }

                        if (chunkElements == 0)
                        {
                            chunk = new RenderChunk { Bounds = Aabb.Empty, IndexStart = write, IndexCount = 0 };
                        }

                        Array.Copy(source, start, indices, write, count);
                        ElementRange range = Ranges[e];
                        if (transparent) { range.TransparentStart = write; range.TransparentCount = count; }
                        else { range.OpaqueStart = write; range.OpaqueCount = count; }
                        Ranges[e] = range;

                        write += count;
                        chunk.IndexCount += count;
                        chunk.Bounds.Include(record.Bounds);
                        chunkElements++;
                    }
                    if (chunkElements > 0) { chunks.Add(chunk); }

                    batch.ChunkCount = chunks.Count - batch.ChunkStart;
                    batches.Add(batch);
                }
            }

            // Family library templates: their indices go after every batch, in no chunk, so they are never drawn as
            // part of the scene (shadows, minimap and probe captures included) but clones of them can be (the
            // dynamic draw copies an element's range from here)
            for (int e = 0; e < elements.Length; e++)
            {
                ElementRecord record = elements[e];
                if (!record.IsLibraryTemplate) { continue; }
                ElementRange range = default;
                range.OpaqueStart = write;
                range.OpaqueCount = record.OpaqueCount;
                Array.Copy(source, record.OpaqueStart, indices, write, record.OpaqueCount);
                write += record.OpaqueCount;
                range.TransparentStart = write;
                range.TransparentCount = record.TransparentCount;
                Array.Copy(source, record.TransparentStart, indices, write, record.TransparentCount);
                write += record.TransparentCount;
                Ranges[e] = range;
            }

            Indices = indices;
            Chunks = chunks.ToArray();
            Batches = batches.ToArray();
        }

        /// <summary>
        /// Interleaves the low 10 bits of two values.
        /// </summary>
        private static ulong Morton2D(uint x, uint y)
        {
            ulong result = 0;
            for (int bit = 0; bit < 10; bit++)
            {
                result |= ((ulong)((x >> bit) & 1) << (2 * bit)) | ((ulong)((y >> bit) & 1) << (2 * bit + 1));
            }
            return result;
        }
    }
}
