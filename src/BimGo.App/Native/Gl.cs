using System.Runtime.InteropServices;
using GLEnum = Silk.NET.OpenGL.GLEnum;
using SilkGL = Silk.NET.OpenGL.GL;

// The class belongs to the Native namespace
namespace BimGo.Native
{
    /// <summary>
    /// BimGo's OpenGL facade. Renderer and UI code call <c>Gl.Xxx(...)</c> with the <c>uint</c> constants below;
    /// each wrapper forwards to Silk.NET.OpenGL (MIT, pinned in BimGo.App.csproj), casting the constants to
    /// <see cref="GLEnum"/>. Entry points resolve through <see cref="GetProc"/> (wglGetProcAddress, then the
    /// opengl32.dll export for GL 1.1), lazily and once each; forwarding does not allocate.
    /// <see cref="Load"/> must be called on the game thread with a current context.
    /// To use a new GL function: add a wrapper here (and its GL name to <see cref="RequiredEntryPoints"/>), then call it
    /// through the renderer. Context creation stays in <see cref="Wgl"/>.
    /// </summary>
    internal static unsafe class Gl
    {
        #region Constants

        public const uint DEPTH_BUFFER_BIT = 0x00000100, COLOR_BUFFER_BIT = 0x00004000;
        public const uint TRIANGLES = 0x0004;
        public const uint NEVER = 0x0200, LESS = 0x0201, LEQUAL = 0x0203, ALWAYS = 0x0207;
        public const uint SRC_COLOR = 0x0300, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, ONE = 1, ZERO = 0;
        public const uint FRONT = 0x0404, BACK = 0x0405, CCW = 0x0901;
        public const uint CULL_FACE = 0x0B44, DEPTH_TEST = 0x0B71, BLEND = 0x0BE2, SCISSOR_TEST = 0x0C11;
        public const uint POLYGON_OFFSET_FILL = 0x8037, MULTISAMPLE = 0x809D, FRAMEBUFFER_SRGB = 0x8DB9;
        public const uint UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05, MAX_SAMPLES = 0x8D57;
        public const uint TEXTURE_2D = 0x0DE1;
        public const uint UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405, FLOAT = 0x1406;
        public const uint RGBA = 0x1908, RGBA8 = 0x8058, BGRA = 0x80E1;
        public const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02;
        public const uint NEAREST = 0x2600, LINEAR = 0x2601;
        public const uint TEXTURE_MAG_FILTER = 0x2800, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
        public const uint CLAMP_TO_EDGE = 0x812F;
        public const uint TEXTURE0 = 0x84C0;
        public const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893;
        public const uint STREAM_DRAW = 0x88E0, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8;
        public const uint FRAGMENT_SHADER = 0x8B30, VERTEX_SHADER = 0x8B31;
        public const uint COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, INFO_LOG_LENGTH = 0x8B84;
        public const uint FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41;
        public const uint COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, DEPTH_COMPONENT24 = 0x81A6;
        public const uint FRAMEBUFFER_COMPLETE = 0x8CD5;
        public const uint NO_ERROR = 0;

        // Shadow maps (texture arrays, depth comparison)
        public const uint TEXTURE_2D_ARRAY = 0x8C1A;
        public const uint DEPTH_COMPONENT = 0x1902;
        public const uint TEXTURE_COMPARE_MODE = 0x884C, TEXTURE_COMPARE_FUNC = 0x884D, COMPARE_REF_TO_TEXTURE = 0x884E;
        public const uint POLYGON_OFFSET_LINE = 0x2A02;
        public const uint OUT_OF_MEMORY = 0x0505;
        public const uint NONE = 0;

        // Ambient occlusion (float colour targets, read with texelFetch)
        public const uint RGBA32F = 0x8814, RG16F = 0x822F, RG = 0x8227, HALF_FLOAT = 0x140B;

        // Artificial lights (glow target as a second colour attachment)
        public const uint RGBA16F = 0x881A, COLOR_ATTACHMENT1 = 0x8CE1;
        public const uint DEPTH_COMPONENT16 = 0x81A5, UNSIGNED_SHORT = 0x1403;

        // Material textures (mipmapped colour arrays, wrap, anisotropy via GL_EXT_texture_filter_anisotropic / GL 4.6)
        public const uint REPEAT = 0x2901, LINEAR_MIPMAP_LINEAR = 0x2703, TEXTURE_MAX_LEVEL = 0x813D;
        public const uint TEXTURE_MAX_ANISOTROPY = 0x84FE, MAX_TEXTURE_MAX_ANISOTROPY = 0x84FF;
        public const uint MAX_ARRAY_TEXTURE_LAYERS = 0x88FF, MAX_TEXTURE_SIZE = 0x0D33;

        // Section box caps (stencil in the scene target)
        public const uint STENCIL_BUFFER_BIT = 0x00000400, STENCIL_TEST = 0x0B90;
        public const uint KEEP = 0x1E00, INVERT = 0x150A, EQUAL = 0x0202;
        public const uint DEPTH24_STENCIL8 = 0x88F0, DEPTH_STENCIL_ATTACHMENT = 0x821A;

        // Photo mode (large off-screen targets, exposure blend)
        public const uint MAX_RENDERBUFFER_SIZE = 0x84E8, DST_COLOR = 0x0306;

        #endregion

        #region Loading

        /// <summary>
        /// Every GL entry point BimGo calls. Checked once in <see cref="Load"/> so a missing one fails at startup
        /// with a readable message instead of a Silk.NET <c>SymbolLoadingException</c> mid-frame.
        /// Add the GL name here when a new wrapper is added below.
        /// </summary>
        private static readonly string[] RequiredEntryPoints =
        {
            // GL 1.1 (opengl32.dll exports)
            "glClear", "glClearColor", "glClearDepth", "glViewport", "glScissor", "glEnable", "glDisable",
            "glDepthFunc", "glDepthMask", "glBlendFunc", "glCullFace", "glPolygonOffset", "glGetString",
            "glGetIntegerv", "glGetError", "glBindTexture", "glGenTextures", "glDeleteTextures", "glTexImage2D",
            "glTexParameteri", "glPixelStorei", "glDrawElements", "glDrawArrays", "glColorMask", "glDrawBuffer",
            "glReadBuffer", "glReadPixels", "glStencilFunc", "glStencilOp", "glStencilMask", "glClearStencil",

            // GL 1.3+
            "glActiveTexture", "glMultiDrawElements", "glBlendFuncSeparate", "glTexImage3D", "glTexSubImage3D", "glGenerateMipmap",

            // Buffers and vertex arrays
            "glGenBuffers", "glDeleteBuffers", "glBindBuffer", "glBufferData", "glBufferSubData",
            "glGenVertexArrays", "glDeleteVertexArrays", "glBindVertexArray", "glEnableVertexAttribArray",
            "glVertexAttribPointer", "glVertexAttribDivisor", "glDrawElementsInstanced",

            // Shaders
            "glCreateShader", "glShaderSource", "glCompileShader", "glGetShaderiv", "glGetShaderInfoLog",
            "glDeleteShader", "glCreateProgram", "glAttachShader", "glLinkProgram", "glGetProgramiv",
            "glGetProgramInfoLog", "glDeleteProgram", "glUseProgram", "glGetUniformLocation", "glUniform1i",
            "glUniform1f", "glUniform2f", "glUniform3f", "glUniform4f", "glUniformMatrix4fv", "glUniform4fv",

            // Framebuffers
            "glGenFramebuffers", "glDeleteFramebuffers", "glBindFramebuffer", "glFramebufferRenderbuffer",
            "glCheckFramebufferStatus", "glGenRenderbuffers", "glDeleteRenderbuffers", "glBindRenderbuffer",
            "glRenderbufferStorageMultisample", "glBlitFramebuffer", "glFramebufferTextureLayer", "glFramebufferTexture2D", "glDrawBuffers",
        };

        [DllImport("opengl32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern nint wglGetProcAddress(string name);

        private static nint _opengl32;

        /// <summary>The Silk.NET API object every wrapper forwards to. Created once by <see cref="Load"/>.</summary>
        private static SilkGL _gl;

        /// <summary>
        /// Resolves one entry point (also used by <see cref="Wgl"/> for the WGL extensions).
        /// </summary>
        /// <param name="name">The GL / WGL function name.</param>
        /// <param name="required">Throw if missing.</param>
        /// <returns>The function address (0 if missing and optional).</returns>
        public static nint GetProc(string name, bool required = true)
        {
            long address = wglGetProcAddress(name);

            // wglGetProcAddress returns 0..3 or -1 for failures (and for GL 1.1 functions)
            if (address >= -1 && address <= 3)
            {
                if (_opengl32 == 0) { _opengl32 = NativeLibrary.Load("opengl32.dll"); }
                address = NativeLibrary.TryGetExport(_opengl32, name, out nint export) ? export : 0;
            }

            if (address == 0 && required)
            {
                throw new InvalidOperationException($"OpenGL function '{name}' is not available. Update the graphics driver.");
            }
            return (nint)address;
        }

        /// <summary>
        /// The loader handed to Silk.NET: same lookup as <see cref="GetProc"/>, never throws (Silk.NET resolves lazily,
        /// once per entry point, and the required ones were already checked).
        /// </summary>
        private static nint LoadProc(string name) => GetProc(name, required: false);

        /// <summary>
        /// Checks every entry point BimGo uses and creates the Silk.NET API.
        /// Must be called on the game thread with the context current.
        /// </summary>
        public static void Load()
        {
            foreach (string name in RequiredEntryPoints)
            {
                GetProc(name);
            }
            _gl = SilkGL.GetApi(LoadProc);
        }

        #endregion

        #region Wrappers: state

        public static void Clear(uint mask) => _gl.Clear(mask);
        public static void ClearColor(float r, float g, float b, float a) => _gl.ClearColor(r, g, b, a);
        public static void ClearDepth(double depth) => _gl.ClearDepth(depth);
        public static void Viewport(int x, int y, int w, int h) => _gl.Viewport(x, y, (uint)w, (uint)h);
        public static void Scissor(int x, int y, int w, int h) => _gl.Scissor(x, y, (uint)w, (uint)h);
        public static void Enable(uint cap) => _gl.Enable((GLEnum)cap);
        public static void Disable(uint cap) => _gl.Disable((GLEnum)cap);
        public static void DepthFunc(uint func) => _gl.DepthFunc((GLEnum)func);
        public static void DepthMask(bool write) => _gl.DepthMask(write);
        public static void BlendFunc(uint src, uint dst) => _gl.BlendFunc((GLEnum)src, (GLEnum)dst);
        public static void BlendFuncSeparate(uint srcRgb, uint dstRgb, uint srcA, uint dstA) => _gl.BlendFuncSeparate((GLEnum)srcRgb, (GLEnum)dstRgb, (GLEnum)srcA, (GLEnum)dstA);
        public static void CullFace(uint mode) => _gl.CullFace((GLEnum)mode);
        public static void PolygonOffset(float factor, float units) => _gl.PolygonOffset(factor, units);
        public static uint GetError() => (uint)_gl.GetError();
        public static void PixelStore(uint name, int value) => _gl.PixelStore((GLEnum)name, value);
        public static void ColorMask(bool r, bool g, bool b, bool a) => _gl.ColorMask(r, g, b, a);
        public static void StencilFunc(uint func, int reference, uint mask) => _gl.StencilFunc((GLEnum)func, reference, mask);
        public static void StencilOp(uint stencilFail, uint depthFail, uint depthPass) => _gl.StencilOp((GLEnum)stencilFail, (GLEnum)depthFail, (GLEnum)depthPass);
        public static void StencilMask(uint mask) => _gl.StencilMask(mask);
        public static void ClearStencil(int value) => _gl.ClearStencil(value);

        public static int GetInteger(uint name)
        {
            int value = 0;
            _gl.GetInteger((GLEnum)name, &value);
            return value;
        }

        public static string GetString(uint name)
        {
            byte* text = _gl.GetString((GLEnum)name);
            return text == null ? string.Empty : Marshal.PtrToStringAnsi((nint)text) ?? string.Empty;
        }

        #endregion

        #region Wrappers: textures

        public static uint GenTexture()
        {
            uint id = 0;
            _gl.GenTextures(1, &id);
            return id;
        }

        public static void DeleteTexture(uint id)
        {
            if (id != 0) { _gl.DeleteTextures(1, &id); }
        }

        public static void ActiveTexture(uint unit) => _gl.ActiveTexture((GLEnum)unit);
        public static void BindTexture(uint target, uint id) => _gl.BindTexture((GLEnum)target, id);
        public static void TexParameter(uint target, uint name, int value) => _gl.TexParameter((GLEnum)target, (GLEnum)name, value);

        public static void TexImage2D(uint target, int level, uint internalFormat, int width, int height, uint format, uint type, void* pixels)
            => _gl.TexImage2D((GLEnum)target, level, (int)internalFormat, (uint)width, (uint)height, 0, (GLEnum)format, (GLEnum)type, pixels);

        public static void TexImage3D(uint target, int level, uint internalFormat, int width, int height, int depth, uint format, uint type, void* pixels)
            => _gl.TexImage3D((GLEnum)target, level, (int)internalFormat, (uint)width, (uint)height, (uint)depth, 0, (GLEnum)format, (GLEnum)type, pixels);

        public static void TexSubImage3D(uint target, int level, int x, int y, int z, int width, int height, int depth, uint format, uint type, void* pixels)
            => _gl.TexSubImage3D((GLEnum)target, level, x, y, z, (uint)width, (uint)height, (uint)depth, (GLEnum)format, (GLEnum)type, pixels);

        public static void GenerateMipmap(uint target) => _gl.GenerateMipmap((GLEnum)target);

        public static void TexParameter(uint target, uint name, float value) => _gl.TexParameter((GLEnum)target, (GLEnum)name, value);

        #endregion

        #region Wrappers: buffers and arrays

        public static uint GenBuffer()
        {
            uint id = 0;
            _gl.GenBuffers(1, &id);
            return id;
        }

        public static void DeleteBuffer(uint id)
        {
            if (id != 0) { _gl.DeleteBuffers(1, &id); }
        }

        public static void BindBuffer(uint target, uint id) => _gl.BindBuffer((GLEnum)target, id);
        public static void BufferData(uint target, nint size, void* data, uint usage) => _gl.BufferData((GLEnum)target, (nuint)size, data, (GLEnum)usage);
        public static void BufferSubData(uint target, nint offset, nint size, void* data) => _gl.BufferSubData((GLEnum)target, offset, (nuint)size, data);

        public static uint GenVertexArray()
        {
            uint id = 0;
            _gl.GenVertexArrays(1, &id);
            return id;
        }

        public static void DeleteVertexArray(uint id)
        {
            if (id != 0) { _gl.DeleteVertexArrays(1, &id); }
        }

        public static void BindVertexArray(uint id) => _gl.BindVertexArray(id);
        public static void EnableVertexAttribArray(uint index) => _gl.EnableVertexAttribArray(index);

        public static void VertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, nint offset)
            => _gl.VertexAttribPointer(index, size, (GLEnum)type, normalized, (uint)stride, (void*)offset);

        public static void VertexAttribDivisor(uint index, uint divisor) => _gl.VertexAttribDivisor(index, divisor);

        public static void DrawElements(uint mode, int count, uint type, nint byteOffset) => _gl.DrawElements((GLEnum)mode, (uint)count, (GLEnum)type, (void*)byteOffset);
        public static void DrawArrays(uint mode, int first, int count) => _gl.DrawArrays((GLEnum)mode, first, (uint)count);

        public static void MultiDrawElements(uint mode, int* counts, uint type, void** offsets, int drawCount)
            => _gl.MultiDrawElements((GLEnum)mode, (uint*)counts, (GLEnum)type, offsets, (uint)drawCount);

        public static void DrawElementsInstanced(uint mode, int count, uint type, nint byteOffset, int instances)
            => _gl.DrawElementsInstanced((GLEnum)mode, (uint)count, (GLEnum)type, (void*)byteOffset, (uint)instances);

        #endregion

        #region Wrappers: shaders

        public static uint CreateShader(uint type) => _gl.CreateShader((GLEnum)type);

        public static void ShaderSource(uint shader, string source)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(source);
            fixed (byte* p = bytes)
            {
                byte* pp = p;
                int length = bytes.Length;
                _gl.ShaderSource(shader, 1, &pp, &length);
            }
        }

        public static void CompileShader(uint shader) => _gl.CompileShader(shader);

        public static int GetShader(uint shader, uint name)
        {
            int value = 0;
            _gl.GetShader(shader, (GLEnum)name, &value);
            return value;
        }

        public static string GetShaderInfoLog(uint shader)
        {
            int length = GetShader(shader, INFO_LOG_LENGTH);
            if (length <= 1) { return string.Empty; }
            byte[] buffer = new byte[length];
            fixed (byte* p = buffer) { _gl.GetShaderInfoLog(shader, (uint)length, (uint*)null, p); }
            return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }

        public static void DeleteShader(uint shader) => _gl.DeleteShader(shader);
        public static uint CreateProgram() => _gl.CreateProgram();
        public static void AttachShader(uint program, uint shader) => _gl.AttachShader(program, shader);
        public static void LinkProgram(uint program) => _gl.LinkProgram(program);

        public static int GetProgram(uint program, uint name)
        {
            int value = 0;
            _gl.GetProgram(program, (GLEnum)name, &value);
            return value;
        }

        public static string GetProgramInfoLog(uint program)
        {
            int length = GetProgram(program, INFO_LOG_LENGTH);
            if (length <= 1) { return string.Empty; }
            byte[] buffer = new byte[length];
            fixed (byte* p = buffer) { _gl.GetProgramInfoLog(program, (uint)length, (uint*)null, p); }
            return System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0');
        }

        public static void DeleteProgram(uint program)
        {
            if (program != 0) { _gl.DeleteProgram(program); }
        }

        public static void UseProgram(uint program) => _gl.UseProgram(program);

        public static int GetUniformLocation(uint program, string name)
        {
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = bytes) { return _gl.GetUniformLocation(program, p); }
        }

        public static void Uniform1(int location, int value) => _gl.Uniform1(location, value);
        public static void Uniform1(int location, float value) => _gl.Uniform1(location, value);
        public static void Uniform2(int location, float x, float y) => _gl.Uniform2(location, x, y);
        public static void Uniform3(int location, float x, float y, float z) => _gl.Uniform3(location, x, y, z);
        public static void Uniform4(int location, float x, float y, float z, float w) => _gl.Uniform4(location, x, y, z, w);

        /// <summary>
        /// Uploads a vec4 array (count elements, starting at the array's location).
        /// </summary>
        public static void Uniform4(int location, int count, System.Numerics.Vector4[] values)
        {
            if (count <= 0) { return; }
            fixed (System.Numerics.Vector4* p = values) { _gl.Uniform4(location, (uint)count, (float*)p); }
        }

        /// <summary>
        /// Uploads a System.Numerics matrix as-is (row-vector convention reads as the GL column-vector transpose).
        /// </summary>
        public static void UniformMatrix4(int location, in System.Numerics.Matrix4x4 matrix)
        {
            System.Numerics.Matrix4x4 copy = matrix;
            _gl.UniformMatrix4(location, 1, false, (float*)&copy);
        }

        #endregion

        #region Wrappers: framebuffers

        public static uint GenFramebuffer()
        {
            uint id = 0;
            _gl.GenFramebuffers(1, &id);
            return id;
        }

        public static void DeleteFramebuffer(uint id)
        {
            if (id != 0) { _gl.DeleteFramebuffers(1, &id); }
        }

        public static void BindFramebuffer(uint target, uint id) => _gl.BindFramebuffer((GLEnum)target, id);
        public static void FramebufferRenderbuffer(uint target, uint attachment, uint rbTarget, uint renderbuffer) => _gl.FramebufferRenderbuffer((GLEnum)target, (GLEnum)attachment, (GLEnum)rbTarget, renderbuffer);
        public static uint CheckFramebufferStatus(uint target) => (uint)_gl.CheckFramebufferStatus((GLEnum)target);

        public static void DrawBuffer(uint buffer) => _gl.DrawBuffer((GLEnum)buffer);

        /// <summary>
        /// Sets the draw buffers of the bound framebuffer (multiple render targets).
        /// </summary>
        public static void DrawBuffers(int count, uint* buffers) => _gl.DrawBuffers((uint)count, (GLEnum*)buffers);
        public static void ReadBuffer(uint buffer) => _gl.ReadBuffer((GLEnum)buffer);
        public static void ReadPixels(int x, int y, int width, int height, uint format, uint type, void* pixels) => _gl.ReadPixels(x, y, (uint)width, (uint)height, (GLEnum)format, (GLEnum)type, pixels);

        public static void FramebufferTextureLayer(uint target, uint attachment, uint texture, int level, int layer)
            => _gl.FramebufferTextureLayer((GLEnum)target, (GLEnum)attachment, texture, level, layer);

        public static void FramebufferTexture2D(uint target, uint attachment, uint textureTarget, uint texture, int level)
            => _gl.FramebufferTexture2D((GLEnum)target, (GLEnum)attachment, (GLEnum)textureTarget, texture, level);

        public static uint GenRenderbuffer()
        {
            uint id = 0;
            _gl.GenRenderbuffers(1, &id);
            return id;
        }

        public static void DeleteRenderbuffer(uint id)
        {
            if (id != 0) { _gl.DeleteRenderbuffers(1, &id); }
        }

        public static void BindRenderbuffer(uint target, uint id) => _gl.BindRenderbuffer((GLEnum)target, id);
        public static void RenderbufferStorageMultisample(uint target, int samples, uint format, int width, int height)
            => _gl.RenderbufferStorageMultisample((GLEnum)target, (uint)samples, (GLEnum)format, (uint)width, (uint)height);

        public static void BlitFramebuffer(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter)
            => _gl.BlitFramebuffer(sx0, sy0, sx1, sy1, dx0, dy0, dx1, dy1, mask, (GLEnum)filter);

        #endregion
    }
}
