using System.Numerics;
using BimGo.Scene;

// The class belongs to the Rendering namespace
namespace BimGo.Rendering
{
    /// <summary>
    /// A Z-up first-person camera with GL-style projection and a cached view frustum.
    /// Yaw 0 looks along +X, increasing counter-clockwise; pitch positive looks up.
    /// </summary>
    internal sealed class FpsCamera
    {
        /// <summary>Near clip distance (m).</summary>
        public const float NEAR = 0.06f;

        /// <summary>Far clip distance (m).</summary>
        public const float FAR = 4000f;

        /// <summary>Eye position.</summary>
        public Vector3 Position;

        /// <summary>Yaw in radians.</summary>
        public float Yaw;

        /// <summary>Pitch in radians.</summary>
        public float Pitch;

        /// <summary>Horizontal field of view in degrees.</summary>
        public float HorizontalFovDegrees = 90f;

        /// <summary>Viewport aspect ratio (width / height).</summary>
        public float Aspect = 16f / 9f;

        /// <summary>Viewport height in pixels.</summary>
        public float ViewportHeight = 720f;

        /// <summary>Viewport width in pixels.</summary>
        public float ViewportWidth = 1280f;

        /// <summary>Forward direction (includes pitch).</summary>
        public Vector3 Forward { get; private set; } = Vector3.UnitX;

        /// <summary>Right direction (horizontal).</summary>
        public Vector3 Right { get; private set; } = -Vector3.UnitY;

        /// <summary>Horizontal forward direction.</summary>
        public Vector3 FlatForward { get; private set; } = Vector3.UnitX;

        /// <summary>The view matrix.</summary>
        public Matrix4x4 View { get; private set; }

        /// <summary>The projection matrix.</summary>
        public Matrix4x4 Projection { get; private set; }

        /// <summary>View * projection (row-vector convention).</summary>
        public Matrix4x4 ViewProjection { get; private set; }

        /// <summary>Inverse of <see cref="ViewProjection"/>.</summary>
        public Matrix4x4 InverseViewProjection { get; private set; }

        /// <summary>Vertical field of view in radians.</summary>
        public float FovY { get; private set; }

        /// <summary>World units per pixel at one metre distance.</summary>
        public float PixelScale { get; private set; }

        /// <summary>The frustum planes (a, b, c, d with inside >= 0).</summary>
        public readonly Vector4[] Planes = new Vector4[6];

        // Photo mode: a view direction / up given outright (360 panorama faces, straight up and down)
        private bool _customView;
        private Vector3 _customForward, _customUp;

        /// <summary>
        /// Looks along a direction with a given up vector until <see cref="ClearCustomView"/> (yaw / pitch are ignored
        /// meanwhile). Used for panorama faces, including straight up and down.
        /// </summary>
        public void SetCustomView(Vector3 forward, Vector3 up)
        {
            _customView = true;
            _customForward = Vector3.Normalize(forward);
            _customUp = Vector3.Normalize(up);
        }

        /// <summary>Back to yaw / pitch.</summary>
        public void ClearCustomView() => _customView = false;

        /// <summary>
        /// Recomputes directions, matrices and planes.
        /// </summary>
        public void Update()
        {
            Pitch = Math.Clamp(Pitch, -1.553f, 1.553f);
            float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);

            Forward = new Vector3(cp * cy, cp * sy, sp);
            FlatForward = new Vector3(cy, sy, 0f);
            Right = new Vector3(sy, -cy, 0f);
            Vector3 up = Vector3.UnitZ;
            if (_customView)
            {
                Forward = _customForward;
                up = _customUp;
                Right = Vector3.Normalize(Vector3.Cross(_customForward, _customUp));
                var flat = new Vector3(_customForward.X, _customForward.Y, 0f);
                FlatForward = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : new Vector3(-_customUp.X, -_customUp.Y, 0f);
            }

            float hfov = HorizontalFovDegrees * MathF.PI / 180f;
            FovY = 2f * MathF.Atan(MathF.Tan(hfov * 0.5f) / MathF.Max(Aspect, 0.1f));
            PixelScale = 2f * MathF.Tan(FovY * 0.5f) / MathF.Max(ViewportHeight, 1f);

            View = Matrix4x4.CreateLookAt(Position, Position + Forward, up);
            Projection = Perspective(FovY, Aspect, NEAR, FAR);
            ViewProjection = View * Projection;
            InverseViewProjection = Matrix4x4.Invert(ViewProjection, out Matrix4x4 inverse) ? inverse : Matrix4x4.Identity;
            ExtractPlanes(ViewProjection, Planes);
        }

        /// <summary>
        /// A GL (-1..1 depth) right-handed perspective matrix in System.Numerics layout.
        /// </summary>
        public static Matrix4x4 Perspective(float fovY, float aspect, float near, float far)
        {
            float f = 1f / MathF.Tan(fovY * 0.5f);
            return new Matrix4x4(
                f / aspect, 0f, 0f, 0f,
                0f, f, 0f, 0f,
                0f, 0f, (far + near) / (near - far), -1f,
                0f, 0f, 2f * far * near / (near - far), 0f);
        }

        /// <summary>
        /// A GL (-1..1 depth) orthographic matrix centred on the view axis.
        /// </summary>
        public static Matrix4x4 Orthographic(float width, float height, float near, float far)
        {
            return new Matrix4x4(
                2f / width, 0f, 0f, 0f,
                0f, 2f / height, 0f, 0f,
                0f, 0f, -2f / (far - near), 0f,
                0f, 0f, -(far + near) / (far - near), 1f);
        }

        /// <summary>
        /// Extracts frustum planes from a view-projection matrix (row-vector convention, GL clip space).
        /// </summary>
        public static void ExtractPlanes(in Matrix4x4 m, Vector4[] planes)
        {
            var c1 = new Vector4(m.M11, m.M21, m.M31, m.M41);
            var c2 = new Vector4(m.M12, m.M22, m.M32, m.M42);
            var c3 = new Vector4(m.M13, m.M23, m.M33, m.M43);
            var c4 = new Vector4(m.M14, m.M24, m.M34, m.M44);

            planes[0] = c4 + c1;
            planes[1] = c4 - c1;
            planes[2] = c4 + c2;
            planes[3] = c4 - c2;
            planes[4] = c4 + c3;
            planes[5] = c4 - c3;
        }

        /// <summary>
        /// True if the box is at least partly inside the planes.
        /// </summary>
        public static bool IsVisible(Vector4[] planes, in Aabb box)
        {
            for (int i = 0; i < 6; i++)
            {
                Vector4 p = planes[i];
                float x = p.X >= 0f ? box.Max.X : box.Min.X;
                float y = p.Y >= 0f ? box.Max.Y : box.Min.Y;
                float z = p.Z >= 0f ? box.Max.Z : box.Min.Z;
                if (p.X * x + p.Y * y + p.Z * z + p.W < 0f) { return false; }
            }
            return true;
        }

        /// <summary>
        /// Projects a world point to screen pixels (origin top-left).
        /// </summary>
        /// <returns>False if the point is behind the camera.</returns>
        public bool WorldToScreen(Vector3 world, out Vector2 screen)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), ViewProjection);
            if (clip.W <= 0.01f)
            {
                screen = default;
                return false;
            }
            float x = clip.X / clip.W, y = clip.Y / clip.W;
            screen = new Vector2((x * 0.5f + 0.5f) * ViewportWidth, (0.5f - y * 0.5f) * ViewportHeight);
            return true;
        }
    }
}
