using System.Globalization;
using System.Numerics;
using System.Text;

// The class belongs to the Scene namespace
namespace BimGo.Scene
{
    /// <summary>
    /// 360° panoramas (photo round): the equirectangular mapping and the Google Photo Sphere (GPano) XMP that makes
    /// phones, Facebook and panorama viewers open a JPEG as a 360 photo. Pure functions.
    /// </summary>
    public static class Panorama
    {
        /// <summary>
        /// The view direction (scene axes, Z up) of an equirectangular pixel's centre. The image's middle column looks
        /// along <paramref name="headingYaw"/> (radians, as the player's yaw); columns to the right turn right; the
        /// top row looks straight up.
        /// </summary>
        public static Vector3 Direction(int column, int row, int width, int height, double headingYaw)
        {
            double longitude = ((column + 0.5) / width - 0.5) * 2.0 * Math.PI; // + = right of the heading
            double latitude = (0.5 - (row + 0.5) / height) * Math.PI;
            double yaw = headingYaw - longitude;
            double c = Math.Cos(latitude);
            return new Vector3((float)(c * Math.Cos(yaw)), (float)(c * Math.Sin(yaw)), (float)Math.Sin(latitude));
        }

        /// <summary>
        /// The square face size (pixels) that matches an equirectangular width at the face centre for a face field of
        /// view (degrees): 2·tan(fov / 2) · width / 2π.
        /// </summary>
        public static int FaceSize(int panoramaWidth, double faceFovDegrees)
        {
            double half = faceFovDegrees * Math.PI / 360.0;
            return Math.Max(16, (int)Math.Ceiling(2.0 * Math.Tan(half) * panoramaWidth / (2.0 * Math.PI)));
        }

        /// <summary>
        /// A JPEG with a GPano XMP segment (APP1) added after the JFIF header: equirectangular, full panorama, the
        /// given heading. Returns the input unchanged when it isn't a JPEG.
        /// </summary>
        /// <param name="jpeg">The JPEG bytes.</param>
        /// <param name="width">Image width (pixels; twice the height).</param>
        /// <param name="height">Image height.</param>
        /// <param name="headingDegrees">Compass heading of the image centre (0–360), or null.</param>
        public static byte[] AddPhotoSphereXmp(byte[] jpeg, int width, int height, double? headingDegrees = null)
        {
            if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) { return jpeg; }

            CultureInfo inv = CultureInfo.InvariantCulture;
            string heading = headingDegrees.HasValue
                ? $"<GPano:PoseHeadingDegrees>{((headingDegrees.Value % 360 + 360) % 360).ToString("0.0", inv)}</GPano:PoseHeadingDegrees>"
                : string.Empty;
            string xmp =
                "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\" xmlns:GPano=\"http://ns.google.com/photos/1.0/panorama/\">" +
                "<GPano:ProjectionType>equirectangular</GPano:ProjectionType>" +
                "<GPano:UsePanoramaViewer>True</GPano:UsePanoramaViewer>" +
                $"<GPano:CroppedAreaImageWidthPixels>{width}</GPano:CroppedAreaImageWidthPixels>" +
                $"<GPano:CroppedAreaImageHeightPixels>{height}</GPano:CroppedAreaImageHeightPixels>" +
                $"<GPano:FullPanoWidthPixels>{width}</GPano:FullPanoWidthPixels>" +
                $"<GPano:FullPanoHeightPixels>{height}</GPano:FullPanoHeightPixels>" +
                "<GPano:CroppedAreaLeftPixels>0</GPano:CroppedAreaLeftPixels>" +
                "<GPano:CroppedAreaTopPixels>0</GPano:CroppedAreaTopPixels>" +
                heading +
                "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

            byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
            byte[] body = Encoding.UTF8.GetBytes(xmp);
            int length = 2 + header.Length + body.Length;
            if (length > 0xFFFF) { return jpeg; }

            // After SOI, and after a JFIF APP0 segment when there is one (some readers want JFIF first)
            int insertAt = 2;
            if (jpeg.Length > 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
            {
                int app0 = (jpeg[4] << 8) | jpeg[5];
                if (4 + app0 <= jpeg.Length) { insertAt = 4 + app0; }
            }

            var output = new byte[jpeg.Length + 2 + length];
            Buffer.BlockCopy(jpeg, 0, output, 0, insertAt);
            int o = insertAt;
            output[o++] = 0xFF;
            output[o++] = 0xE1;
            output[o++] = (byte)(length >> 8);
            output[o++] = (byte)length;
            Buffer.BlockCopy(header, 0, output, o, header.Length);
            o += header.Length;
            Buffer.BlockCopy(body, 0, output, o, body.Length);
            o += body.Length;
            Buffer.BlockCopy(jpeg, insertAt, output, o, jpeg.Length - insertAt);
            return output;
        }
    }
}
