using System.Numerics;
using System.Runtime.InteropServices;
using BimGo.Native;
using BimGo.Physics;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// Per-pass parameters for the scene shader.
    /// </summary>
    internal struct SceneDrawParams
    {
        /// <summary>View-projection matrix.</summary>
        public Matrix4x4 ViewProjection;

        /// <summary>Frustum planes for chunk culling.</summary>
        public Vector4[] Planes;

        /// <summary>Eye position (fog, lighting).</summary>
        public Vector3 Eye;

        /// <summary>Greyscale colours.</summary>
        public bool Whitecard;

        /// <summary>Top-down plan shading (minimap).</summary>
        public bool Plan;

        /// <summary>Visible Z range (minimap cut); use a huge range otherwise.</summary>
        public Vector2 ClipZ;

        /// <summary>Fog density (0 = none).</summary>
        public float FogDensity;

        /// <summary>Use the renderer's sun lighting (when it is on); false for the plan minimap.</summary>
        public bool Sun;

        /// <summary>Realistic colours: render colours and textures (when the snapshot has materials).</summary>
        public bool Realistic;

        /// <summary>Reflections: glass, mirrors and shiny surfaces (Realistic mode only).</summary>
        public bool Reflections;

        /// <summary>Lowest reflection tier that reflects, 0.25 or 0.5 (glass and water always reflect).</summary>
        public float ReflectThreshold;

        /// <summary>Reflection strength multiplier (0.5–2).</summary>
        public float ReflectGain;

        /// <summary>Debug colours instead of the material: 0 off, 1 reflection tiers, 2 reflection probe cells.</summary>
        public int ReflectDebug;

        /// <summary>Reflect the reflection probes where they exist (else the sky).</summary>
        public bool Probes;

        /// <summary>Seconds since the session started (water ripples).</summary>
        public float Time;

        /// <summary>How Revit's tint is drawn (Realistic mode only).</summary>
        public TintMode Tint;
    }

    /// <summary>
    /// Owns the static scene buffers and draws sky, batches, ground and highlights, and (when the sun is on) the
    /// cascaded shadow maps. Lighting comes from <see cref="Lighting"/>: off = the classic fixed light.
    /// Ambient occlusion and the glow (when on) share a half-resolution geometry pre-pass drawn here and processed by
    /// <see cref="ScreenEffects"/>. Artificial lights (<see cref="ArtificialLighting"/>) and emissive surfaces are lit
    /// in the scene and ground shaders.
    /// </summary>
    internal sealed unsafe class SceneRenderer : IDisposable
    {
        #region Fields

        /// <summary>Sky / fog colour.</summary>
        public static readonly Vector3 FOG_COLOUR = new(0.80f, 0.85f, 0.89f);

        private static readonly Vector3 LIGHT_DIR = Vector3.Normalize(new Vector3(0.35f, 0.22f, 0.91f));

        private ShaderProgram _sceneProgram, _skyProgram, _groundProgram, _shadowDepthProgram, _shadowTransmitProgram;
        private ShaderProgram _geometryProgram, _groundGeometryProgram;
        private SceneUniforms _sceneUniforms;
        private LightUniforms _sceneLight, _groundLight;
        private AoUniforms _sceneAo, _groundAo;
        private ArtificialUniforms _sceneLights, _groundLights;
        private GeometryUniforms _geometryUniforms, _groundGeometryUniforms;
        private int _groundGeometryCenter, _groundGeometryHalf, _geometryGlow;
        private int _skyInvViewProj, _skyEye, _groundViewProj, _groundCenter, _groundHalf, _groundEye, _groundFog;
        private int _skySun, _skySunDir, _skyZenith, _skyHorizon, _skyDisc;
        private int _depthViewProj, _depthModel, _transmitViewProj, _transmitModel, _transmitGlass, _transmitWhitecard;

        // Sun and shadows
        private readonly ShadowMaps _shadows = new();
        private readonly bool[] _cascadeDirty = new bool[ShadowMaps.MAX_CASCADES];
        private Vector3 _cameraForward = Vector3.UnitX;
        private bool _shadowsActive;
        private bool _hasTransparent;

        // Ambient occlusion and glow
        private readonly ScreenEffects _effects = new();
        private bool _aoActive, _glowActive;
        private Vector3 _aoForward = Vector3.UnitX;

        // Glowing surfaces: a per-vertex RGBA8 stream (attribute 3), only when the model has any
        private uint _emissiveVbo;

        // Realistic mode: per-vertex material index (attribute 4) and surface coordinates (attribute 5), the material
        // table and texture arrays; only when the snapshot was extracted with materials
        private uint _materialVbo, _uvVbo;
        private readonly MaterialTextures _materials = new();

        /// <summary>True if the snapshot has materials and they loaded (the Realistic mode can be shown).</summary>
        public bool HasMaterials => _materialVbo != 0 && _materials.Ready;

        /// <summary>Why some or all textures couldn't be shown (once, after <see cref="Initialise"/>), or null.</summary>
        public string MaterialWarning { get; private set; }

        /// <summary>The material textures (counts and memory for the HUD and log).</summary>
        public MaterialTextures Materials => _materials;

        // Artificial-light shadow maps (cached per light)
        private readonly LightShadows _lightShadows = new();
        private readonly int[] _lightSlot = new int[ArtificialLighting.MAX_LIGHTS];
        private bool _lightShadowsReported;
        private readonly List<int> _lightsToRender = new(LightShadows.LIGHTS_PER_FRAME);

        private uint _vao, _vbo, _ibo, _emptyVao;

        // Reflection probes (Realistic mode, reflections set to probes)
        private readonly ReflectionProbes _probes = new();
        private readonly List<(int Probe, int Face)> _probeFaces = new(ReflectionProbes.FACES_PER_FRAME);
        private SceneData _scene;
        private bool _probesActive, _probeErrorReported;

        /// <summary>The reflection probes (counts, memory, last error).</summary>
        public ReflectionProbes Probes => _probes;

        /// <summary>This frame's artificial lights and glow strength (set by the session before drawing).</summary>
        public ArtificialLighting Artificial { get; } = new();

        /// <summary>True if the model has glowing surfaces.</summary>
        public bool HasEmissive => _emissiveVbo != 0;

        private SceneBatches _batches;
        private int[] _drawCounts = Array.Empty<int>();
        private nint[] _drawOffsets = Array.Empty<nint>();

        // Hidden elements: their index ranges are overwritten with degenerate triangles (scratch reused)
        private uint[] _degenerate = Array.Empty<uint>();

        // Dynamic (moved / cloned) geometry: copies of source index ranges drawn with a model matrix
        private uint _dynamicVao, _dynamicIbo;
        private readonly List<uint> _dynamicIndices = new();
        private ElementRange[] _dynamicRanges = Array.Empty<ElementRange>();
        private bool[] _hasDynamicRange = Array.Empty<bool>();
        private bool _dynamicDirty;

        /// <summary>Chunks drawn last frame (stats).</summary>
        public int ChunksDrawn { get; private set; }

        /// <summary>This frame's sun and sky (Enabled = false: the classic fixed light, no shadows).</summary>
        public SunLighting Lighting;

        /// <summary>The fog / clear colour: the sky's horizon when the sun is on.</summary>
        public Vector3 FogColour => Lighting.Enabled ? Lighting.Horizon : FOG_COLOUR;

        /// <summary>The shadow maps (state, presets, last error).</summary>
        public ShadowMaps Shadows => _shadows;

        /// <summary>The ambient occlusion / glow targets (state, last error).</summary>
        public ScreenEffects Effects => _effects;

        /// <summary>Half-size of the ground plane quad (m).</summary>
        private const float GROUND_HALF = 2500f;

        #endregion

        /// <summary>
        /// Uniform locations of the sun / shadow block shared by the scene and ground shaders.
        /// </summary>
        private struct LightUniforms
        {
            public int Sun, SunDir, SunColor, SkyColor, ShadowStrength, ShadowsOn, TransmitOn, CamForward;
            public int CascadeCount, CascadeFar, NormalOffset, ShadowTexel, Pcf, ShadowFar;
            public int ShadowMat0, ShadowMat1, ShadowMat2, ShadowMat3;

            public static LightUniforms From(ShaderProgram p)
            {
                // The samplers read fixed texture units (set once)
                p.Use();
                Gl.Uniform1(p.Uniform("uShadowMap"), ShadowMaps.DEPTH_UNIT);
                Gl.Uniform1(p.Uniform("uTransmit"), ShadowMaps.TRANSMIT_UNIT);
                return new LightUniforms
                {
                    Sun = p.Uniform("uSun"),
                    SunDir = p.Uniform("uSunDir"),
                    SunColor = p.Uniform("uSunColor"),
                    SkyColor = p.Uniform("uSkyColor"),
                    ShadowStrength = p.Uniform("uShadowStrength"),
                    ShadowsOn = p.Uniform("uShadowsOn"),
                    TransmitOn = p.Uniform("uTransmitOn"),
                    CamForward = p.Uniform("uCamForward"),
                    CascadeCount = p.Uniform("uCascadeCount"),
                    CascadeFar = p.Uniform("uCascadeFar"),
                    NormalOffset = p.Uniform("uNormalOffset"),
                    ShadowTexel = p.Uniform("uShadowTexel"),
                    Pcf = p.Uniform("uPcf"),
                    ShadowFar = p.Uniform("uShadowFar"),
                    ShadowMat0 = p.Uniform("uShadowMat[0]"),
                    ShadowMat1 = p.Uniform("uShadowMat[1]"),
                    ShadowMat2 = p.Uniform("uShadowMat[2]"),
                    ShadowMat3 = p.Uniform("uShadowMat[3]")
                };
            }
        }

        /// <summary>
        /// Uniform locations of the ambient occlusion block shared by the scene and ground shaders.
        /// </summary>
        private struct AoUniforms
        {
            public int On, Forward, Scale;

            public static AoUniforms From(ShaderProgram p)
            {
                // The sampler reads a fixed texture unit (set once)
                p.Use();
                Gl.Uniform1(p.Uniform("uAoMap"), ScreenEffects.AO_UNIT);
                return new AoUniforms
                {
                    On = p.Uniform("uAoOn"),
                    Forward = p.Uniform("uAoForward"),
                    Scale = p.Uniform("uAoScale")
                };
            }
        }

        /// <summary>
        /// Uniform locations of the artificial-light block shared by the scene and ground shaders.
        /// </summary>
        private struct ArtificialUniforms
        {
            public int Count, Pos, Color, Shadow, Emissive, Shoulder;

            public static ArtificialUniforms From(ShaderProgram p)
            {
                // The shadow sampler reads a fixed texture unit and the texel size never changes (set once)
                p.Use();
                Gl.Uniform1(p.Uniform("uLightShadowMap"), LightShadows.UNIT);
                Gl.Uniform1(p.Uniform("uLightShadowTexel"), LightShadows.Texel);
                return new ArtificialUniforms
                {
                    Count = p.Uniform("uLightCount"),
                    Pos = p.Uniform("uLightPos[0]"),
                    Color = p.Uniform("uLightColor[0]"),
                    Shadow = p.Uniform("uLightShadow[0]"),
                    Emissive = p.Uniform("uEmissive"),
                    Shoulder = p.Uniform("uShoulder")
                };
            }
        }

        /// <summary>
        /// Uniform locations of a geometry pre-pass program.
        /// </summary>
        private struct GeometryUniforms
        {
            public int ViewProj, Model, Eye, Right, Up, Forward;

            public static GeometryUniforms From(ShaderProgram p) => new()
            {
                ViewProj = p.Uniform("uViewProj"),
                Model = p.Uniform("uModel"),
                Eye = p.Uniform("uEye"),
                Right = p.Uniform("uRight"),
                Up = p.Uniform("uUp"),
                Forward = p.Uniform("uForward")
            };
        }

        /// <summary>
        /// Uniform locations of a scene-shaded program.
        /// </summary>
        private struct SceneUniforms
        {
            public int ViewProj, Model, Eye, LightDir, FogColor, FogDensity, Whitecard, Plan, ClipZ, Override;
            public int Realistic, Reflections, SkyZenith, SkyHorizon, TintMode;
            public int ReflectThreshold, ReflectGain, ReflectDebug, Time;
            public int ProbesOn, ProbeGridOrigin, ProbeGridCell, ProbeGridSize, ProbeMaxLod;

            public static SceneUniforms From(ShaderProgram p)
            {
                // The material samplers read fixed texture units (set once, even without materials: every sampler
                // of the program needs a unit of its own type)
                p.Use();
                Gl.Uniform1(p.Uniform("uMaterialTable"), MaterialTextures.TABLE_UNIT);
                for (int b = 0; b < MaterialTextures.BUCKETS.Length; b++)
                {
                    Gl.Uniform1(p.Uniform($"uTex{b}"), MaterialTextures.BUCKET_UNIT + b);
                }
                Gl.Uniform1(p.Uniform("uProbeArray"), ReflectionProbes.ARRAY_UNIT);
                Gl.Uniform1(p.Uniform("uProbeGrid"), ReflectionProbes.GRID_UNIT);
                Gl.Uniform1(p.Uniform("uProbeData"), ReflectionProbes.DATA_UNIT);
                return Locations(p);
            }

            private static SceneUniforms Locations(ShaderProgram p) => new()
            {
                ViewProj = p.Uniform("uViewProj"),
                Model = p.Uniform("uModel"),
                Eye = p.Uniform("uEye"),
                LightDir = p.Uniform("uLightDir"),
                FogColor = p.Uniform("uFogColor"),
                FogDensity = p.Uniform("uFogDensity"),
                Whitecard = p.Uniform("uWhitecard"),
                Plan = p.Uniform("uPlan"),
                ClipZ = p.Uniform("uClipZ"),
                Override = p.Uniform("uOverride"),
                Realistic = p.Uniform("uRealistic"),
                Reflections = p.Uniform("uReflections"),
                SkyZenith = p.Uniform("uSkyZenith"),
                SkyHorizon = p.Uniform("uSkyHorizon"),
                TintMode = p.Uniform("uTintMode"),
                ReflectThreshold = p.Uniform("uReflectThreshold"),
                ReflectGain = p.Uniform("uReflectGain"),
                ReflectDebug = p.Uniform("uReflectDebug"),
                Time = p.Uniform("uTime"),
                ProbesOn = p.Uniform("uProbesOn"),
                ProbeGridOrigin = p.Uniform("uProbeGridOrigin"),
                ProbeGridCell = p.Uniform("uProbeGridCell"),
                ProbeGridSize = p.Uniform("uProbeGridSize"),
                ProbeMaxLod = p.Uniform("uProbeMaxLod")
            };
        }

        #region Setup

        /// <summary>
        /// Creates programs and uploads the static scene.
        /// </summary>
        /// <param name="scene">The snapshot.</param>
        /// <param name="batches">The re-ordered batches.</param>
        public void Initialise(SceneData scene, SceneBatches batches)
        {
            _batches = batches;
            _scene = scene;

            _sceneProgram = ShaderProgram.Create("scene", Shaders.SCENE_VS, Shaders.SCENE_FS);
            _skyProgram = ShaderProgram.Create("sky", Shaders.FULLSCREEN_VS, Shaders.SKY_FS);
            _groundProgram = ShaderProgram.Create("ground", Shaders.GROUND_VS, Shaders.GROUND_FS);
            _shadowDepthProgram = ShaderProgram.Create("shadow depth", Shaders.SHADOW_VS, Shaders.SHADOW_DEPTH_FS);
            _shadowTransmitProgram = ShaderProgram.Create("shadow glass", Shaders.SHADOW_VS, Shaders.SHADOW_TRANSMIT_FS);
            _geometryProgram = ShaderProgram.Create("ao geometry", Shaders.SCENE_VS, Shaders.GEOMETRY_FS);
            _groundGeometryProgram = ShaderProgram.Create("ao ground geometry", Shaders.GROUND_VS, Shaders.GEOMETRY_GROUND_FS);

            _sceneUniforms = SceneUniforms.From(_sceneProgram);
            _sceneLight = LightUniforms.From(_sceneProgram);
            _groundLight = LightUniforms.From(_groundProgram);
            _sceneAo = AoUniforms.From(_sceneProgram);
            _groundAo = AoUniforms.From(_groundProgram);
            _sceneLights = ArtificialUniforms.From(_sceneProgram);
            _groundLights = ArtificialUniforms.From(_groundProgram);
            _geometryGlow = _geometryProgram.Uniform("uGlow");
            _geometryUniforms = GeometryUniforms.From(_geometryProgram);
            _groundGeometryUniforms = GeometryUniforms.From(_groundGeometryProgram);
            _groundGeometryCenter = _groundGeometryProgram.Uniform("uCenter");
            _groundGeometryHalf = _groundGeometryProgram.Uniform("uHalf");
            _skyInvViewProj = _skyProgram.Uniform("uInvViewProj");
            _skyEye = _skyProgram.Uniform("uEye");
            _skySun = _skyProgram.Uniform("uSun");
            _skySunDir = _skyProgram.Uniform("uSunDir");
            _skyZenith = _skyProgram.Uniform("uZenith");
            _skyHorizon = _skyProgram.Uniform("uHorizon");
            _skyDisc = _skyProgram.Uniform("uSunDisc");
            _depthViewProj = _shadowDepthProgram.Uniform("uViewProj");
            _depthModel = _shadowDepthProgram.Uniform("uModel");
            _transmitViewProj = _shadowTransmitProgram.Uniform("uViewProj");
            _transmitModel = _shadowTransmitProgram.Uniform("uModel");
            _transmitGlass = _shadowTransmitProgram.Uniform("uGlass");
            _transmitWhitecard = _shadowTransmitProgram.Uniform("uWhitecard");
            Gl.UseProgram(0);
            _groundViewProj = _groundProgram.Uniform("uViewProj");
            _groundCenter = _groundProgram.Uniform("uCenter");
            _groundHalf = _groundProgram.Uniform("uHalf");
            _groundEye = _groundProgram.Uniform("uEye");
            _groundFog = _groundProgram.Uniform("uFogColor");

            // Static geometry
            _vao = Gl.GenVertexArray();
            Gl.BindVertexArray(_vao);

            _vbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            fixed (SceneVertex* v = scene.Vertices)
            {
                Gl.BufferData(Gl.ARRAY_BUFFER, (nint)scene.Vertices.Length * SceneVertex.SIZE, v, Gl.STATIC_DRAW);
            }

            _ibo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _ibo);
            fixed (uint* i = batches.Indices)
            {
                Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (nint)batches.Indices.Length * sizeof(uint), i, Gl.STATIC_DRAW);
            }

            SetVertexLayout();
            Gl.BindVertexArray(0);

            // Dynamic geometry shares the vertex buffer, with its own (small) index buffer
            _dynamicVao = Gl.GenVertexArray();
            Gl.BindVertexArray(_dynamicVao);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _vbo);
            _dynamicIbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ELEMENT_ARRAY_BUFFER, _dynamicIbo);
            SetVertexLayout();
            Gl.BindVertexArray(0);

            // Glowing surfaces: a second vertex stream on both VAOs (left disabled when there are none: reads as no glow)
            UploadEmissive(scene);
            UploadMaterials(scene);
            _dynamicRanges = new ElementRange[scene.Elements.Length];
            _hasDynamicRange = new bool[scene.Elements.Length];

            int maxChunks = 1;
            foreach (RenderBatch batch in batches.Batches)
            {
                maxChunks = Math.Max(maxChunks, batch.ChunkCount);
                if (batch.Transparent) { _hasTransparent = true; }
            }
            _drawCounts = new int[maxChunks];
            _drawOffsets = new nint[maxChunks];

            _emptyVao = Gl.GenVertexArray();
            _shadows.Initialise();
            _effects.Initialise();
        }

        /// <summary>
        /// Expands the model's emissive runs to one RGBA8 per vertex and binds it as attribute 3 of both VAOs.
        /// </summary>
        private void UploadEmissive(SceneData scene)
        {
            EmissiveRun[] runs = scene.Lighting?.Emissive ?? Array.Empty<EmissiveRun>();
            if (runs.Length == 0) { return; }

            uint[] perVertex = new uint[scene.Vertices.Length];
            int glowing = 0;
            foreach (EmissiveRun run in runs)
            {
                Array.Fill(perVertex, run.Emissive, run.Start, run.Count);
                glowing += run.Count;
            }

            _emissiveVbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _emissiveVbo);
            fixed (uint* data = perVertex)
            {
                Gl.BufferData(Gl.ARRAY_BUFFER, (nint)perVertex.Length * sizeof(uint), data, Gl.STATIC_DRAW);
            }
            foreach (uint vao in new[] { _vao, _dynamicVao })
            {
                Gl.BindVertexArray(vao);
                Gl.BindBuffer(Gl.ARRAY_BUFFER, _emissiveVbo);
                Gl.EnableVertexAttribArray(3);
                Gl.VertexAttribPointer(3, 4, Gl.UNSIGNED_BYTE, true, sizeof(uint), 0);
            }
            Gl.BindVertexArray(0);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);
            Utilities.Log_Utils.Write($"Glowing surfaces: {glowing:N0} vertices in {runs.Length:N0} runs.");
        }

        /// <summary>
        /// Realistic mode: the per-vertex material index (ushort → float, attribute 4) and surface coordinates (float2,
        /// attribute 5) on both VAOs, then the material table and texture arrays. Skipped when the snapshot has none.
        /// </summary>
        private void UploadMaterials(SceneData scene)
        {
            MaterialData materials = scene.Materials;
            if (materials == null || materials.IsEmpty || materials.VertexMaterial.Length != scene.Vertices.Length) { return; }

            _materialVbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _materialVbo);
            fixed (ushort* data = materials.VertexMaterial)
            {
                Gl.BufferData(Gl.ARRAY_BUFFER, (nint)materials.VertexMaterial.Length * sizeof(ushort), data, Gl.STATIC_DRAW);
            }
            Vector2[] uv = materials.VertexUv.Length == scene.Vertices.Length ? materials.VertexUv : new Vector2[scene.Vertices.Length];
            _uvVbo = Gl.GenBuffer();
            Gl.BindBuffer(Gl.ARRAY_BUFFER, _uvVbo);
            fixed (Vector2* data = uv)
            {
                Gl.BufferData(Gl.ARRAY_BUFFER, (nint)uv.Length * sizeof(Vector2), data, Gl.STATIC_DRAW);
            }

            foreach (uint vao in new[] { _vao, _dynamicVao })
            {
                Gl.BindVertexArray(vao);
                Gl.BindBuffer(Gl.ARRAY_BUFFER, _materialVbo);
                Gl.EnableVertexAttribArray(4);
                Gl.VertexAttribPointer(4, 1, Gl.UNSIGNED_SHORT, false, sizeof(ushort), 0);
                Gl.BindBuffer(Gl.ARRAY_BUFFER, _uvVbo);
                Gl.EnableVertexAttribArray(5);
                Gl.VertexAttribPointer(5, 2, Gl.FLOAT, false, sizeof(Vector2), 0);
            }
            Gl.BindVertexArray(0);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, 0);

            MaterialWarning = _materials.Initialise(materials, ProxyPack.Shared, AutoProxy);
        }

        /// <summary>
        /// Proxy suggestions for missing images in snapshots made before proxies existed (the "Proxy textures for
        /// missing images" setting). Set before <see cref="Initialise"/>.
        /// </summary>
        public bool AutoProxy { get; set; } = true;

        /// <summary>Proxies take the material's colour (see <see cref="MaterialTextures.ProxyMaterialColour"/>).</summary>
        public bool ProxyMaterialColour
        {
            get => _materials.ProxyMaterialColour;
            set => _materials.ProxyMaterialColour = value;
        }

        /// <summary>
        /// Rebuilds the material table and texture arrays from a changed material set (the Textures panel). The
        /// per-vertex streams are unchanged: surface coordinates don't depend on the material. GL thread.
        /// </summary>
        /// <returns>Null, or a short reason the textures couldn't all be shown.</returns>
        public string ReloadMaterials(MaterialData materials)
        {
            if (_materialVbo == 0 || materials == null || materials.IsEmpty) { return null; }
            MaterialWarning = _materials.Initialise(materials, ProxyPack.Shared, AutoProxy);
            return MaterialWarning;
        }

        /// <summary>
        /// Vertex attributes of <see cref="SceneVertex"/> for the bound VAO / VBO.
        /// </summary>
        private static void SetVertexLayout()
        {
            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(0, 3, Gl.FLOAT, false, SceneVertex.SIZE, 0);
            Gl.EnableVertexAttribArray(1);
            Gl.VertexAttribPointer(1, 3, Gl.FLOAT, false, SceneVertex.SIZE, 12);
            Gl.EnableVertexAttribArray(2);
            Gl.VertexAttribPointer(2, 4, Gl.UNSIGNED_BYTE, true, SceneVertex.SIZE, 24);
        }

        #endregion

        #region Element visibility and dynamic geometry

        /// <summary>
        /// Hides or restores an element in the static batches. Hiding overwrites its index ranges with
        /// degenerate triangles (zero raster cost) so the batch / chunk draw lists never change.
        /// </summary>
        /// <param name="element">Element index.</param>
        /// <param name="hidden">True to hide, false to restore.</param>
        public void SetElementHidden(int element, bool hidden)
        {
            ElementRange range = _batches.Ranges[element];
            // The element buffer binding is VAO state (core profile): bind the VAO, never unbind the buffer from it
            Gl.BindVertexArray(_vao);
            Upload(range.OpaqueStart, range.OpaqueCount);
            Upload(range.TransparentStart, range.TransparentCount);
            Gl.BindVertexArray(0);

            void Upload(int start, int count)
            {
                if (count <= 0) { return; }
                if (hidden)
                {
                    if (_degenerate.Length < count) { _degenerate = new uint[Math.Max(count, _degenerate.Length * 2)]; }
                    Array.Fill(_degenerate, _batches.Indices[start], 0, count);
                    fixed (uint* data = _degenerate)
                    {
                        Gl.BufferSubData(Gl.ELEMENT_ARRAY_BUFFER, (nint)start * sizeof(uint), (nint)count * sizeof(uint), data);
                    }
                }
                else
                {
                    fixed (uint* data = &_batches.Indices[start])
                    {
                        Gl.BufferSubData(Gl.ELEMENT_ARRAY_BUFFER, (nint)start * sizeof(uint), (nint)count * sizeof(uint), data);
                    }
                }
            }
        }

        /// <summary>
        /// Makes sure an element's triangles are available to the dynamic pass (copied once per source element;
        /// clones of the same element share them).
        /// </summary>
        public void EnsureDynamicGeometry(int element)
        {
            if (_hasDynamicRange[element]) { return; }

            ElementRange source = _batches.Ranges[element];
            var range = new ElementRange
            {
                OpaqueStart = _dynamicIndices.Count,
                OpaqueCount = source.OpaqueCount
            };
            for (int i = 0; i < source.OpaqueCount; i++) { _dynamicIndices.Add(_batches.Indices[source.OpaqueStart + i]); }
            range.TransparentStart = _dynamicIndices.Count;
            range.TransparentCount = source.TransparentCount;
            for (int i = 0; i < source.TransparentCount; i++) { _dynamicIndices.Add(_batches.Indices[source.TransparentStart + i]); }

            _dynamicRanges[element] = range;
            _hasDynamicRange[element] = true;
            _dynamicDirty = true;
        }

        /// <summary>
        /// Uploads the dynamic index buffer if it changed.
        /// </summary>
        private void FlushDynamic()
        {
            if (!_dynamicDirty) { return; }
            _dynamicDirty = false;
            uint[] data = _dynamicIndices.ToArray();
            Gl.BindVertexArray(_dynamicVao);
            fixed (uint* pointer = data)
            {
                Gl.BufferData(Gl.ELEMENT_ARRAY_BUFFER, (nint)data.Length * sizeof(uint), pointer, Gl.DYNAMIC_DRAW);
            }
            Gl.BindVertexArray(0);
        }

        /// <summary>
        /// Draws the active dynamic instances of one pass.
        /// </summary>
        public void DrawDynamic(in SceneDrawParams p, DynamicSet set, bool transparent)
        {
            if (set.Instances.Count == 0) { return; }
            FlushDynamic();

            _sceneProgram.Use();
            ApplyUniforms(_sceneUniforms, p, Vector4.Zero, transmit: !transparent);
            DrawDynamicInstances(set, p.Planes, transparent, _sceneUniforms.Model);
        }

        /// <summary>
        /// Draws one dynamic instance with a colour override (highlights).
        /// </summary>
        public void DrawDynamicHighlight(in SceneDrawParams p, DynamicInstance instance, Vector4 colour)
        {
            if (!_hasDynamicRange[instance.Element]) { return; }
            FlushDynamic();

            _sceneProgram.Use();
            ApplyUniforms(_sceneUniforms, p, colour, transmit: true);
            Gl.BindVertexArray(_dynamicVao);
            DrawDynamicRange(instance, transparent: false, _sceneUniforms.Model);
            DrawDynamicRange(instance, transparent: true, _sceneUniforms.Model);
            Gl.UniformMatrix4(_sceneUniforms.Model, Matrix4x4.Identity);
            Gl.BindVertexArray(0);
        }

        /// <summary>
        /// Draws the active, visible dynamic instances of one pass with the current program.
        /// </summary>
        /// <param name="set">The instances.</param>
        /// <param name="planes">Culling planes.</param>
        /// <param name="transparent">Which ranges.</param>
        /// <param name="modelUniform">The current program's uModel location.</param>
        private void DrawDynamicInstances(DynamicSet set, Vector4[] planes, bool transparent, int modelUniform)
        {
            Gl.BindVertexArray(_dynamicVao);
            foreach (DynamicInstance instance in set.Instances)
            {
                if (!set.IsActive(instance) || !_hasDynamicRange[instance.Element]) { continue; }
                if (!FpsCamera.IsVisible(planes, instance.WorldBounds)) { continue; }
                DrawDynamicRange(instance, transparent, modelUniform);
            }
            Gl.UniformMatrix4(modelUniform, Matrix4x4.Identity);
            Gl.BindVertexArray(0);
        }

        private void DrawDynamicRange(DynamicInstance instance, bool transparent, int modelUniform)
        {
            ElementRange range = _dynamicRanges[instance.Element];
            int start = transparent ? range.TransparentStart : range.OpaqueStart;
            int count = transparent ? range.TransparentCount : range.OpaqueCount;
            if (count <= 0) { return; }
            Gl.UniformMatrix4(modelUniform, instance.Model);
            Gl.DrawElements(Gl.TRIANGLES, count, Gl.UNSIGNED_INT, (nint)start * sizeof(uint));
        }

        #endregion

        #region Sun and shadows

        /// <summary>
        /// Brings the shadow maps up to date for this frame (call before binding the scene target; <see cref="Lighting"/>
        /// must already be set). Only cascades whose fit, the sun or the scene changed are re-rendered. With the sun off
        /// the maps are freed and nothing is drawn.
        /// </summary>
        /// <param name="camera">The player camera (updated).</param>
        /// <param name="sceneBounds">The scene bounds (casters).</param>
        /// <param name="sceneKey">Changes whenever casters change (hidden / moved elements, category and link toggles).</param>
        /// <param name="groupVisible">Per visibility group (category × model, <see cref="SceneBatches.GroupOf"/>).</param>
        /// <param name="dynamics">Moved and cloned elements.</param>
        /// <param name="whitecard">Whitecard colours (glass casts untinted light).</param>
        /// <param name="preset">The quality preset.</param>
        /// <returns>Null, or a reason shadows could not be shown (the caller switches them off).</returns>
        public string UpdateShadows(FpsCamera camera, in Aabb sceneBounds, long sceneKey, bool[] groupVisible, DynamicSet dynamics,
            bool whitecard, ShadowMaps.Preset preset)
        {
            _cameraForward = camera.Forward;
            _shadowsActive = false;

            if (!Lighting.Enabled)
            {
                _shadows.Release();
                return null;
            }

            // Sun below the horizon: no direct light, so nothing to shadow (keep the maps for when it rises)
            if (Lighting.AltitudeDegrees < -1f) { return null; }

            if (!_shadows.Ensure(preset, _hasTransparent)) { return _shadows.LastError; }

            FlushDynamic();
            _shadows.Fit(camera, Lighting.SunDirection, sceneBounds, sceneKey, Lighting.Glass, _cascadeDirty);

            int rendered = 0;
            for (int c = 0; c < preset.Cascades; c++)
            {
                if (!_cascadeDirty[c]) { continue; }
                RenderCascade(c, groupVisible, dynamics, whitecard);
                rendered++;
            }
            _shadows.EndRender(rendered);

            _shadowsActive = true;
            _shadows.Bind();
            return null;
        }

        /// <summary>
        /// Renders one cascade: opaque casters into depth, then glass multiplied into the transmittance layer (only glass
        /// in front of the nearest opaque surface counts, so it never tints what lies behind a lit receiver).
        /// </summary>
        private void RenderCascade(int cascade, bool[] groupVisible, DynamicSet dynamics, bool whitecard)
        {
            _shadows.BeginCascade(cascade);
            Matrix4x4 matrix = _shadows.Matrices[cascade];
            Vector4[] planes = _shadows.PlanesFor(cascade);

            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.Disable(Gl.BLEND);
            Gl.Disable(Gl.CULL_FACE);

            // Opaque casters: depth only, pushed back a little against acne
            Gl.ColorMask(false, false, false, false);
            Gl.Enable(Gl.POLYGON_OFFSET_FILL);
            Gl.PolygonOffset(1.5f, 3f);
            _shadowDepthProgram.Use();
            Gl.UniformMatrix4(_depthViewProj, matrix);
            Gl.UniformMatrix4(_depthModel, Matrix4x4.Identity);
            DrawBatches(planes, groupVisible, transparent: false, countStats: false);
            if (dynamics != null && dynamics.Instances.Count > 0) { DrawDynamicInstances(dynamics, planes, transparent: false, _depthModel); }
            Gl.Disable(Gl.POLYGON_OFFSET_FILL);
            Gl.ColorMask(true, true, true, true);

            // Glass: multiply the light that gets through (dst = dst × src)
            if (_shadows.HasTransmit)
            {
                Gl.DepthMask(false);
                Gl.DepthFunc(Gl.LESS);
                Gl.Enable(Gl.BLEND);
                Gl.BlendFunc(Gl.ZERO, Gl.SRC_COLOR);
                _shadowTransmitProgram.Use();
                Gl.UniformMatrix4(_transmitViewProj, matrix);
                Gl.UniformMatrix4(_transmitModel, Matrix4x4.Identity);
                Gl.Uniform1(_transmitGlass, Lighting.Glass);
                Gl.Uniform1(_transmitWhitecard, whitecard ? 1 : 0);
                DrawBatches(planes, groupVisible, transparent: true, countStats: false);
                if (dynamics != null && dynamics.Instances.Count > 0) { DrawDynamicInstances(dynamics, planes, transparent: true, _transmitModel); }
                Gl.Disable(Gl.BLEND);
                Gl.DepthFunc(Gl.LEQUAL);
                Gl.DepthMask(true);
            }

            _shadows.MarkRendered(cascade);
        }

        /// <summary>
        /// Sets the sun / shadow block of the scene or ground program for one draw.
        /// </summary>
        /// <param name="u">The program's locations.</param>
        /// <param name="sun">False for passes that keep the classic light (plan minimap).</param>
        /// <param name="transmit">False for the transparent pass (glass shouldn't tint itself).</param>
        private void ApplyLight(in LightUniforms u, bool sun, bool transmit)
        {
            SunLighting l = Lighting;
            bool on = sun && l.Enabled;
            Gl.Uniform1(u.Sun, on ? 1 : 0);
            if (!on) { return; }

            Gl.Uniform3(u.SunDir, l.SunDirection.X, l.SunDirection.Y, l.SunDirection.Z);
            Gl.Uniform3(u.SunColor, l.SunColour.X, l.SunColour.Y, l.SunColour.Z);
            Gl.Uniform3(u.SkyColor, l.SkyColour.X, l.SkyColour.Y, l.SkyColour.Z);
            Gl.Uniform1(u.ShadowStrength, l.ShadowStrength);
            Gl.Uniform1(u.ShadowsOn, _shadowsActive ? 1 : 0);
            if (!_shadowsActive) { return; }

            ShadowMaps.Preset preset = _shadows.Current;
            float[] far = _shadows.CascadeFar, offset = _shadows.NormalOffset;
            Matrix4x4[] matrices = _shadows.RenderedMatrices;
            Gl.Uniform1(u.TransmitOn, transmit && _shadows.HasTransmit ? 1 : 0);
            Gl.Uniform3(u.CamForward, _cameraForward.X, _cameraForward.Y, _cameraForward.Z);
            Gl.Uniform1(u.CascadeCount, preset.Cascades);
            Gl.Uniform4(u.CascadeFar, far[0], far[1], far[2], far[3]);
            Gl.Uniform4(u.NormalOffset, offset[0], offset[1], offset[2], offset[3]);
            Gl.Uniform1(u.ShadowTexel, _shadows.Texel);
            Gl.Uniform1(u.Pcf, preset.PcfRadius);
            Gl.Uniform1(u.ShadowFar, preset.Distance);
            Gl.UniformMatrix4(u.ShadowMat0, matrices[0]);
            Gl.UniformMatrix4(u.ShadowMat1, matrices[1]);
            Gl.UniformMatrix4(u.ShadowMat2, matrices[2]);
            Gl.UniformMatrix4(u.ShadowMat3, matrices[3]);
        }

        #endregion

        #region Ambient occlusion and glow

        /// <summary>
        /// Renders this frame's screen effects (call after <see cref="UpdateShadows"/> and before binding the scene
        /// target): the opaque batches, moved / cloned elements and the ground into the half-resolution pre-pass with
        /// the player camera's culling, then the AO and / or bloom passes. With both off the targets are freed.
        /// </summary>
        /// <param name="camera">The player camera (updated).</param>
        /// <param name="width">Scene target width in pixels.</param>
        /// <param name="height">Scene target height in pixels.</param>
        /// <param name="groupVisible">Per visibility group (category × model, <see cref="SceneBatches.GroupOf"/>).</param>
        /// <param name="dynamics">Moved and cloned elements.</param>
        /// <param name="groundZ">Ground plane elevation.</param>
        /// <param name="ao">Ambient occlusion wanted.</param>
        /// <param name="glow">Bloom wanted (ignored when the model has no glowing surfaces).</param>
        /// <returns>Null, or a reason the effects could not be shown (the caller switches them off).</returns>
        public string UpdateScreenEffects(FpsCamera camera, int width, int height, bool[] groupVisible, DynamicSet dynamics, float groundZ,
            bool ao, bool glow)
        {
            glow &= HasEmissive;
            _aoActive = _glowActive = false;
            if (!ao && !glow)
            {
                _effects.Release();
                return null;
            }
            if (!_effects.Ensure(width, height))
            {
                _effects.Bind();
                return _effects.LastError;
            }

            FlushDynamic();
            Vector3 eye = camera.Position, forward = camera.Forward, right = camera.Right;
            Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));

            _effects.BeginGeometry(glow);

            _geometryProgram.Use();
            ApplyGeometry(_geometryUniforms, camera.ViewProjection, eye, right, up, forward);
            Gl.Uniform1(_geometryGlow, glow ? 1f : 0f);
            DrawBatches(camera.Planes, groupVisible, transparent: false, countStats: false);
            if (dynamics != null && dynamics.Instances.Count > 0) { DrawDynamicInstances(dynamics, camera.Planes, transparent: false, _geometryUniforms.Model); }

            _groundGeometryProgram.Use();
            ApplyGeometry(_groundGeometryUniforms, camera.ViewProjection, eye, right, up, forward);
            Gl.Uniform3(_groundGeometryCenter, eye.X, eye.Y, groundZ);
            Gl.Uniform1(_groundGeometryHalf, GROUND_HALF);
            Gl.BindVertexArray(_emptyVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
            Gl.BindVertexArray(0);

            _effects.Compute(camera, ao, glow);
            _aoForward = forward;
            _aoActive = ao;
            _glowActive = glow;
            return null;

            static void ApplyGeometry(in GeometryUniforms u, in Matrix4x4 viewProjection, Vector3 eye, Vector3 right, Vector3 up, Vector3 forward)
            {
                Gl.UniformMatrix4(u.ViewProj, viewProjection);
                Gl.UniformMatrix4(u.Model, Matrix4x4.Identity);
                Gl.Uniform3(u.Eye, eye.X, eye.Y, eye.Z);
                Gl.Uniform3(u.Right, right.X, right.Y, right.Z);
                Gl.Uniform3(u.Up, up.X, up.Y, up.Z);
                Gl.Uniform3(u.Forward, forward.X, forward.Y, forward.Z);
            }
        }

        /// <summary>
        /// Switches AO and glow off for this frame and frees their targets (after a failure).
        /// </summary>
        public void DisableScreenEffects()
        {
            _aoActive = _glowActive = false;
            _effects.Release();
        }

        /// <summary>
        /// Gives this frame's picked lights their shadow maps (call after the session filled <see cref="Artificial"/>,
        /// before binding the scene target): renders the few that are new, moved or out of date, then keeps only the
        /// lights that have a map (a new light joins, fading in, once its map exists). With no lights the maps are freed.
        /// If the GPU can't make the maps, lights are drawn without shadows.
        /// </summary>
        /// <param name="groupVisible">Per visibility group (category × model, <see cref="SceneBatches.GroupOf"/>).</param>
        /// <param name="dynamics">Moved and cloned elements.</param>
        /// <param name="sceneKey">Changes whenever shadow casters change.</param>
        /// <returns>Null, or (once) why light shadows are unavailable.</returns>
        public string UpdateLightShadows(bool[] groupVisible, DynamicSet dynamics, long sceneKey)
        {
            ArtificialLighting a = Artificial;
            if (a.Count == 0)
            {
                if (_lightShadows.Ready) { _lightShadows.Release(); }
                return null;
            }

            string error = null;
            if (!_lightShadows.Ensure())
            {
                // No maps: light without shadows (said once)
                error = _lightShadows.LastError;
                if (_lightShadowsReported) { error = null; }
                _lightShadowsReported = true;
                for (int k = 0; k < a.Count; k++) { a.Shadow[k].X = -1f; }
                return error;
            }

            _lightShadows.Assign(a, sceneKey, _lightSlot, _lightsToRender);
            if (_lightsToRender.Count > 0)
            {
                FlushDynamic();
                Gl.Enable(Gl.DEPTH_TEST);
                Gl.DepthFunc(Gl.LEQUAL);
                Gl.Disable(Gl.BLEND);
                Gl.Disable(Gl.CULL_FACE);
                Gl.ColorMask(false, false, false, false);
                Gl.Enable(Gl.POLYGON_OFFSET_FILL);
                Gl.PolygonOffset(1.5f, 3f);
                _shadowDepthProgram.Use();
                foreach (int k in _lightsToRender)
                {
                    Vector4 light = a.Position[k];
                    var position = new Vector3(light.X, light.Y, light.Z);
                    for (int face = 0; face < 6; face++)
                    {
                        Matrix4x4 matrix = LightShadows.FaceMatrix(position, light.W, face);
                        Vector4[] planes = _lightShadows.BeginFace(_lightSlot[k], face, matrix);
                        Gl.UniformMatrix4(_depthViewProj, matrix);
                        Gl.UniformMatrix4(_depthModel, Matrix4x4.Identity);
                        DrawBatches(planes, groupVisible, transparent: false, countStats: false);
                        if (dynamics != null && dynamics.Instances.Count > 0) { DrawDynamicInstances(dynamics, planes, transparent: false, _depthModel); }
                    }
                    _lightShadows.MarkRendered(_lightSlot[k], light, sceneKey);
                }
                Gl.Disable(Gl.POLYGON_OFFSET_FILL);
                Gl.ColorMask(true, true, true, true);
                _lightShadows.EndRender(_lightsToRender.Count);
            }

            // Keep the lights that have a map (order kept: nearest first)
            for (int k = 0; k < a.Count; k++)
            {
                if (_lightShadows.HasMap(_lightSlot[k], out float fadeIn))
                {
                    a.Shadow[k].X = _lightSlot[k] * 6;
                    a.Shadow[k].Y *= fadeIn;
                    continue;
                }
                a.RemoveAt(k);
                Array.Copy(_lightSlot, k + 1, _lightSlot, k, a.Count - k);
                k--;
            }
            _lightShadows.Bind();
            return null;
        }

        /// <summary>
        /// Reflection probes for this frame (call after the shadow maps, light maps and screen effects, before binding
        /// the scene target): places them and allocates their textures the first time, then captures this frame's few
        /// faces (nearest unbaked probe first, then stale ones). A capture is the normal scene draw (sky, opaque, moved
        /// elements, ground, glass) from the probe, without AO, reflections or fog-of-war differences. With probes not
        /// wanted the textures are freed.
        /// </summary>
        /// <param name="wanted">Reflections set to probes (or the probe debug colours): keeps the probes allocated.</param>
        /// <param name="capture">They are shown now (Realistic mode): bake this frame's faces.</param>
        /// <param name="size">Face size (128 or 256 px).</param>
        /// <param name="eye">The player's eye (bake order, provisional refresh).</param>
        /// <param name="template">This frame's scene parameters (colour mode, tint, time); view fields are replaced.</param>
        /// <param name="groupVisible">Per visibility group (category × model).</param>
        /// <param name="dynamics">Moved and cloned elements.</param>
        /// <param name="groundZ">Ground plane elevation.</param>
        /// <returns>Null, or (once) why probes can't be shown (the caller switches to sky reflections).</returns>
        public string UpdateReflectionProbes(bool wanted, bool capture, int size, Vector3 eye, in SceneDrawParams template, bool[] groupVisible,
            DynamicSet dynamics, float groundZ)
        {
            _probesActive = false;
            if (!wanted || !HasMaterials || _scene == null)
            {
                if (_probes.Ready) { _probes.Release(); }
                return null;
            }

            if (!_probes.Ensure(_scene, size))
            {
                string error = _probes.LastError;
                if (error == null || _probeErrorReported) { return null; }
                _probeErrorReported = true;
                return error;
            }
            _probeErrorReported = false;

            if (!capture) { return null; }
            _probes.NextFaces(eye, _probeFaces);
            if (_probeFaces.Count > 0) { CaptureProbeFaces(template, groupVisible, dynamics, groundZ); }
            _probes.Bind();
            _probesActive = _probes.BakedCount > 0;
            return null;
        }

        /// <summary>
        /// Marks every probe stale (the session calls this a moment after the sun, lights, colour mode or the model
        /// changed); they re-bake progressively and keep their old capture until then.
        /// </summary>
        public void InvalidateProbes() => _probes.Invalidate();

        /// <summary>
        /// Lets probes try again after a failure (the user switched them on again).
        /// </summary>
        public void RetryProbes()
        {
            _probes.ClearError();
            _probeErrorReported = false;
        }

        /// <summary>
        /// Captures this frame's probe faces with the scene program.
        /// </summary>
        private void CaptureProbeFaces(in SceneDrawParams template, bool[] groupVisible, DynamicSet dynamics, float groundZ)
        {
            FlushDynamic();
            bool aoWas = _aoActive;
            _aoActive = false;

            SceneDrawParams p = template;
            p.Reflections = false;
            p.Probes = false;
            p.ReflectDebug = 0;
            p.Plan = false;
            p.Sun = true;
            p.ClipZ = new Vector2(-1e7f, 1e7f);
            Vector3 clear = FogColour;
            bool hasDynamics = dynamics != null && dynamics.Instances.Count > 0;

            foreach ((int probe, int face) in _probeFaces)
            {
                Matrix4x4 matrix = _probes.FaceMatrix(probe, face, out Vector3 eye, out Vector4[] planes);
                _probes.BeginFace(probe, face, clear);
                Matrix4x4.Invert(matrix, out Matrix4x4 inverse);
                DrawSky(inverse, eye);

                p.ViewProjection = matrix;
                p.Planes = planes;
                p.Eye = eye;
                Gl.Enable(Gl.DEPTH_TEST);
                Gl.DepthFunc(Gl.LEQUAL);
                Gl.Disable(Gl.BLEND);
                Gl.Disable(Gl.CULL_FACE);
                Gl.DepthMask(true);

                _sceneProgram.Use();
                ApplyUniforms(_sceneUniforms, p, Vector4.Zero, transmit: true);
                DrawBatches(planes, groupVisible, transparent: false, countStats: false);
                if (hasDynamics) { DrawDynamicInstances(dynamics, planes, transparent: false, _sceneUniforms.Model); }
                DrawGround(matrix, eye, groundZ);

                Gl.Enable(Gl.BLEND);
                Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
                Gl.DepthMask(false);
                _sceneProgram.Use();
                ApplyUniforms(_sceneUniforms, p, Vector4.Zero, transmit: false);
                DrawBatches(planes, groupVisible, transparent: true, countStats: false);
                if (hasDynamics) { DrawDynamicInstances(dynamics, planes, transparent: true, _sceneUniforms.Model); }
                Gl.DepthMask(true);
                Gl.Disable(Gl.BLEND);

                _probes.EndFace(probe, face);
            }

            _aoActive = aoWas;
            _probes.EndCapture();
        }

        /// <summary>
        /// Adds this frame's bloom over the scene target (call after the transparent pass, target bound).
        /// </summary>
        public void CompositeGlow()
        {
            if (_glowActive) { _effects.CompositeGlow(Artificial.Bloom); }
        }

        /// <summary>
        /// Sets the AO block of the scene or ground program for one draw.
        /// </summary>
        /// <param name="u">The program's locations.</param>
        /// <param name="on">False for passes that never get AO (plan minimap, glass).</param>
        private void ApplyAo(in AoUniforms u, bool on)
        {
            on &= _aoActive;
            Gl.Uniform1(u.On, on ? 1 : 0);
            if (!on) { return; }
            Vector3 forward = _aoForward;
            Vector2 scale = _effects.Scale;
            Gl.Uniform3(u.Forward, forward.X, forward.Y, forward.Z);
            Gl.Uniform2(u.Scale, scale.X, scale.Y);
        }

        /// <summary>
        /// Sets the artificial-light block of the scene or ground program for one draw.
        /// </summary>
        /// <param name="u">The program's locations.</param>
        /// <param name="on">False for the plan minimap (no lights, no glow).</param>
        private void ApplyArtificial(in ArtificialUniforms u, bool on)
        {
            ArtificialLighting a = Artificial;
            int count = on ? a.Count : 0;
            float emissive = on ? a.Emissive : 0f;
            Gl.Uniform1(u.Count, count);
            Gl.Uniform1(u.Emissive, emissive);
            Gl.Uniform1(u.Shoulder, count > 0 || emissive > 0f ? 1 : 0);
            if (count == 0) { return; }
            Gl.Uniform4(u.Pos, count, a.Position);
            Gl.Uniform4(u.Color, count, a.Colour);
            Gl.Uniform4(u.Shadow, count, a.Shadow);
        }

        #endregion

        #region Drawing

        /// <summary>
        /// Draws the gradient sky (no depth).
        /// </summary>
        public void DrawSky(FpsCamera camera) => DrawSky(camera.InverseViewProjection, camera.Position);

        /// <summary>
        /// Draws the gradient sky (no depth) for any view (the camera, or a probe face).
        /// </summary>
        private void DrawSky(in Matrix4x4 inverseViewProjection, Vector3 eye)
        {
            Gl.Disable(Gl.DEPTH_TEST);
            Gl.DepthMask(false);
            _skyProgram.Use();
            Gl.UniformMatrix4(_skyInvViewProj, inverseViewProjection);
            Gl.Uniform3(_skyEye, eye.X, eye.Y, eye.Z);
            SunLighting l = Lighting;
            Gl.Uniform1(_skySun, l.Enabled ? 1 : 0);
            Gl.Uniform3(_skySunDir, l.SunDirection.X, l.SunDirection.Y, l.SunDirection.Z);
            Gl.Uniform3(_skyZenith, l.Zenith.X, l.Zenith.Y, l.Zenith.Z);
            Gl.Uniform3(_skyHorizon, l.Horizon.X, l.Horizon.Y, l.Horizon.Z);
            Gl.Uniform3(_skyDisc, l.SunDisc.X, l.SunDisc.Y, l.SunDisc.Z);
            Gl.BindVertexArray(_emptyVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.DepthMask(true);
            Gl.Enable(Gl.DEPTH_TEST);
        }

        /// <summary>
        /// Draws the infinite-looking ground plane.
        /// </summary>
        public void DrawGround(FpsCamera camera, float groundZ) => DrawGround(camera.ViewProjection, camera.Position, groundZ);

        /// <summary>
        /// Draws the ground plane for any view (the camera, or a probe face).
        /// </summary>
        private void DrawGround(in Matrix4x4 viewProjection, Vector3 eye, float groundZ)
        {
            _groundProgram.Use();
            Gl.UniformMatrix4(_groundViewProj, viewProjection);
            Gl.Uniform3(_groundCenter, eye.X, eye.Y, groundZ);
            Gl.Uniform1(_groundHalf, GROUND_HALF);
            Gl.Uniform3(_groundEye, eye.X, eye.Y, eye.Z);
            Vector3 fog = FogColour;
            Gl.Uniform3(_groundFog, fog.X, fog.Y, fog.Z);
            ApplyLight(_groundLight, sun: true, transmit: true);
            ApplyAo(_groundAo, on: true);
            ApplyArtificial(_groundLights, on: true);
            Gl.BindVertexArray(_emptyVao);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 6);
        }

        /// <summary>
        /// Draws all visible batches of one pass with frustum-culled chunks (one multi-draw per batch).
        /// </summary>
        /// <param name="p">Draw parameters.</param>
        /// <param name="groupVisible">Per visibility group (category × model, <see cref="SceneBatches.GroupOf"/>).</param>
        /// <param name="transparent">Which pass.</param>
        public void DrawStatic(in SceneDrawParams p, bool[] groupVisible, bool transparent)
        {
            if (!transparent) { ChunksDrawn = 0; }

            _sceneProgram.Use();
            ApplyUniforms(_sceneUniforms, p, Vector4.Zero, transmit: !transparent);
            DrawBatches(p.Planes, groupVisible, transparent, countStats: true);
        }

        /// <summary>
        /// Multi-draws the visible chunks of every batch of one pass with the current program.
        /// </summary>
        private void DrawBatches(Vector4[] planes, bool[] groupVisible, bool transparent, bool countStats)
        {
            Gl.BindVertexArray(_vao);

            RenderBatch[] batches = _batches.Batches;
            RenderChunk[] chunks = _batches.Chunks;

            fixed (int* counts = _drawCounts)
            fixed (nint* offsets = _drawOffsets)
            {
                for (int b = 0; b < batches.Length; b++)
                {
                    RenderBatch batch = batches[b];
                    if (batch.Transparent != transparent || !groupVisible[batch.Group]) { continue; }

                    int drawCount = 0;
                    int end = batch.ChunkStart + batch.ChunkCount;
                    for (int c = batch.ChunkStart; c < end; c++)
                    {
                        if (!FpsCamera.IsVisible(planes, chunks[c].Bounds)) { continue; }
                        counts[drawCount] = chunks[c].IndexCount;
                        offsets[drawCount] = (nint)chunks[c].IndexStart * sizeof(uint);
                        drawCount++;
                    }

                    if (drawCount > 0)
                    {
                        Gl.MultiDrawElements(Gl.TRIANGLES, counts, Gl.UNSIGNED_INT, (void**)offsets, drawCount);
                        if (countStats) { ChunksDrawn += drawCount; }
                    }
                }
            }
            Gl.BindVertexArray(0);
        }

        /// <summary>
        /// Draws an element again with a colour override (scan highlight).
        /// </summary>
        public void DrawElementHighlight(in SceneDrawParams p, int elementIndex, Vector4 colour)
        {
            ElementRange range = _batches.Ranges[elementIndex];
            _sceneProgram.Use();
            ApplyUniforms(_sceneUniforms, p, colour, transmit: true);
            Gl.BindVertexArray(_vao);
            if (range.OpaqueCount > 0) { Gl.DrawElements(Gl.TRIANGLES, range.OpaqueCount, Gl.UNSIGNED_INT, (nint)range.OpaqueStart * sizeof(uint)); }
            if (range.TransparentCount > 0) { Gl.DrawElements(Gl.TRIANGLES, range.TransparentCount, Gl.UNSIGNED_INT, (nint)range.TransparentStart * sizeof(uint)); }
            Gl.BindVertexArray(0);
        }

        private void ApplyUniforms(in SceneUniforms u, in SceneDrawParams p, Vector4 overrideColour, bool transmit)
        {
            Gl.UniformMatrix4(u.ViewProj, p.ViewProjection);
            Gl.UniformMatrix4(u.Model, Matrix4x4.Identity);
            Gl.Uniform3(u.Eye, p.Eye.X, p.Eye.Y, p.Eye.Z);
            Gl.Uniform3(u.LightDir, LIGHT_DIR.X, LIGHT_DIR.Y, LIGHT_DIR.Z);
            Vector3 fog = p.Sun ? FogColour : FOG_COLOUR;
            Gl.Uniform3(u.FogColor, fog.X, fog.Y, fog.Z);
            ApplyLight(_sceneLight, p.Sun, transmit);
            // AO follows "transmit": on for opaque surfaces and highlights, off for glass (it isn't in the pre-pass)
            ApplyAo(_sceneAo, transmit && !p.Plan);
            ApplyArtificial(_sceneLights, !p.Plan);
            Gl.Uniform1(u.FogDensity, p.FogDensity);
            Gl.Uniform1(u.Whitecard, p.Whitecard ? 1 : 0);
            Gl.Uniform1(u.Plan, p.Plan ? 1 : 0);
            Gl.Uniform2(u.ClipZ, p.ClipZ.X, p.ClipZ.Y);
            Gl.Uniform4(u.Override, overrideColour.X, overrideColour.Y, overrideColour.Z, overrideColour.W);

            bool realistic = p.Realistic && !p.Whitecard && HasMaterials;
            Gl.Uniform1(u.Realistic, realistic ? 1 : 0);
            if (!realistic) { return; }
            _materials.Bind();
            Gl.Uniform1(u.Reflections, p.Reflections ? 1 : 0);
            Gl.Uniform1(u.ReflectThreshold, p.ReflectThreshold > 0f ? p.ReflectThreshold : 0.5f);
            Gl.Uniform1(u.ReflectGain, p.ReflectGain > 0f ? p.ReflectGain : 1f);
            Gl.Uniform1(u.ReflectDebug, p.ReflectDebug);
            Gl.Uniform1(u.Time, p.Time);
            bool probes = (p.Probes || p.ReflectDebug == 2) && _probesActive;
            Gl.Uniform1(u.ProbesOn, probes ? 1 : 0);
            if (probes)
            {
                Vector3 origin = _probes.GridOrigin, cell = _probes.GridCell, size = _probes.GridSize;
                Gl.Uniform3(u.ProbeGridOrigin, origin.X, origin.Y, origin.Z);
                Gl.Uniform3(u.ProbeGridCell, cell.X, cell.Y, cell.Z);
                Gl.Uniform3(u.ProbeGridSize, size.X, size.Y, size.Z);
                Gl.Uniform1(u.ProbeMaxLod, _probes.MaxLod);
            }
            Gl.Uniform1(u.TintMode, (int)p.Tint);
            SunLighting l = Lighting;
            Vector3 zenith = l.Enabled ? l.Zenith : SKY_ZENITH, horizon = l.Enabled ? l.Horizon : FOG_COLOUR;
            Gl.Uniform3(u.SkyZenith, zenith.X, zenith.Y, zenith.Z);
            Gl.Uniform3(u.SkyHorizon, horizon.X, horizon.Y, horizon.Z);
        }

        /// <summary>The classic sky's zenith (matches SKY_FS with the sun off).</summary>
        private static readonly Vector3 SKY_ZENITH = new(0.34f, 0.50f, 0.70f);

        #endregion

        /// <summary>
        /// Releases GL resources.
        /// </summary>
        public void Dispose()
        {
            _sceneProgram?.Dispose();
            _skyProgram?.Dispose();
            _groundProgram?.Dispose();
            _shadowDepthProgram?.Dispose();
            _shadowTransmitProgram?.Dispose();
            _geometryProgram?.Dispose();
            _groundGeometryProgram?.Dispose();
            _shadows.Dispose();
            _effects.Dispose();
            _lightShadows.Dispose();
            _probes.Dispose();
            _materials.Dispose();
            Gl.DeleteBuffer(_materialVbo);
            Gl.DeleteBuffer(_uvVbo);
            Gl.DeleteBuffer(_emissiveVbo);
            Gl.DeleteBuffer(_vbo);
            Gl.DeleteBuffer(_ibo);
            Gl.DeleteBuffer(_dynamicIbo);
            Gl.DeleteVertexArray(_vao);
            Gl.DeleteVertexArray(_dynamicVao);
            Gl.DeleteVertexArray(_emptyVao);
        }
    }
}
