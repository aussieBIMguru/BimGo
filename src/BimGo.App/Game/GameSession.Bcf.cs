using System.Numerics;
using BimGo.Audio;
using BimGo.Format;
using BimGo.Physics;
using BimGo.Platform;
using BimGo.Scene;

// The class belongs to the Game namespace
namespace BimGo.Game
{
    /// <summary>
    /// BCF export and import of comments (BCF round), from the COMMENTS panel. Plain BCF 2.1 out; 2.0 / 2.1 / 3.0 in.
    /// <list type="bullet">
    /// <item><b>Export</b> (the comments the panel shows: pick All / all levels for every comment): one topic per comment with its status, priority,
    /// assignee, replies, a perspective viewpoint from its saved view (in shared, project or internal coordinates),
    /// the commented element (IFC GUID + Revit ElementId) and its picture.</item>
    /// <item><b>Import</b>: a topic whose GUID matches a comment is merged (status, priority, assignee, new replies;
    /// nothing deleted); any other topic becomes a comment with its view, a marker where the view's centre ray meets
    /// the model (else on the named element, else 2 m ahead) and its picture.</item>
    /// </list>
    /// IO runs only on a click; nothing here runs per frame.
    /// </summary>
    internal sealed partial class GameSession
    {
        #region Fields

        /// <summary>Button labels for the coordinate choice (cached: no per-frame strings).</summary>
        private static readonly string[] BCF_COORDINATE_LABELS = { "BCF COORDS: SHARED", "BCF COORDS: PROJECT", "BCF COORDS: INTERNAL" };

        /// <summary>The coordinates BCF viewpoints are written in and read with (remembered in settings).</summary>
        private BcfCoordinates _bcfCoordinates = BcfCoordinates.Shared;

        #endregion

        #region Coordinates

        /// <summary>The label of the coordinate button.</summary>
        private string BcfCoordinateLabel => BCF_COORDINATE_LABELS[Math.Clamp((int)_bcfCoordinates, 0, BCF_COORDINATE_LABELS.Length - 1)];

        /// <summary>
        /// Steps the BCF coordinates (shared → project → internal) and says which are used, warning when the model
        /// can't supply them.
        /// </summary>
        private void CycleBcfCoordinates()
        {
            _bcfCoordinates = (BcfCoordinates)(((int)_bcfCoordinates + 1) % 3);
            BcfFrame.Resolve(Scene.Site, _bcfCoordinates, out bool fellBack);
            string name = CoordinateName(_bcfCoordinates);
            _commentsNotice = fellBack
                ? $"BCF viewpoints: {name} coordinates are not available in this model, internal will be used"
                : $"BCF viewpoints use {name} coordinates (match your IFC export's setting)";
        }

        private static string CoordinateName(BcfCoordinates coordinates) => coordinates switch
        {
            BcfCoordinates.Project => "project",
            BcfCoordinates.Internal => "internal",
            _ => "shared"
        };

        #endregion

        #region Export

        /// <summary>
        /// Writes the comments the panel shows (its level and status filters applied) to a BCF 2.1 file.
        /// </summary>
        private void ExportBcf()
        {
            var records = new List<CommentRecord>();
            foreach (CommentRecord record in Comments.Comments)
            {
                if (MatchesCommentFilter(record)) { records.Add(record); }
            }
            bool all = records.Count == Comments.Comments.Count;
            if (records.Count == 0)
            {
                _commentsNotice = "No comments to export (check the filters)";
                Sound.Play(SoundId.Error);
                return;
            }

            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            string name = Path.GetFileNameWithoutExtension(DocumentName) + " issues" + BcfFile.EXTENSION;
            string path = FileDialogs.ShowSave(_window.Handle, all ? "Export comments to BCF" : $"Export the {records.Count} shown comments to BCF",
                BcfFile.DIALOG_FILTER, SuggestedFolder(), name, BcfFile.EXTENSION);
            if (path == null) { return; }

            try
            {
                BcfFrame frame = BcfFrame.Resolve(Scene.Site, _bcfCoordinates, out bool fellBack);
                string system = string.IsNullOrWhiteSpace(Scene.Provenance?.RevitVersion) ? "Autodesk Revit" : "Autodesk Revit " + Scene.Provenance.RevitVersion;
                var topics = new List<BcfTopic>(records.Count);
                foreach (CommentRecord record in records)
                {
                    BcfTopic topic = BcfMapping.ToTopic(record);
                    topic.Viewpoint = BcfMapping.ToViewpoint(ViewForBcf(record), CharacterController.STAND_EYE, frame, _fov);
                    if (record.View?.Section != null) { topic.Viewpoint.ClippingPlanes.AddRange(BcfMapping.ToClippingPlanes(record.View.Section, frame)); }
                    BcfComponent component = ComponentFor(record, system);
                    if (component != null) { topic.Viewpoint.Selection.Add(component); }
                    topic.Snapshot = ToPng(record.SnapshotData ?? ThumbnailBytes(record));
                    topic.SnapshotExtension = ".png";
                    topics.Add(topic);
                }

                string key = string.IsNullOrWhiteSpace(Scene.Provenance?.ModelKey) ? Scene.ModelTitle : Scene.Provenance.ModelKey;
                var project = new BcfProject { ProjectId = BcfMapping.DeterministicGuid("bimgo-model:" + key).ToString("D"), Name = Scene.ModelTitle ?? string.Empty };
                if (!BcfFile.Write(path, project, topics, out string error))
                {
                    _commentsNotice = "BCF export failed: " + error;
                    Sound.Play(SoundId.Error);
                    return;
                }

                int withoutPicture = topics.Count(t => t.Snapshot == null);
                _commentsNotice = $"Exported {topics.Count} {(topics.Count == 1 ? "issue" : "issues")} to {Path.GetFileName(path)} ({CoordinateName(frame.Kind)} coordinates" +
                    (fellBack ? ", as the chosen ones aren't in this model" : string.Empty) +
                    (withoutPicture > 0 ? $"; {withoutPicture} without a picture" : string.Empty) + ")";
                Utilities.Log_Utils.Write($"BCF export: {topics.Count} topics to {path}, {CoordinateName(frame.Kind)} coordinates.");
                Sound.Play(SoundId.Commit);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"BCF export failed: {ex}");
                _commentsNotice = "BCF export failed: " + ex.Message;
                Sound.Play(SoundId.Error);
            }
        }

        /// <summary>
        /// The view a comment is exported with: its saved view, or (older comments) a standing spot looking at the
        /// marker. Revit internal metres.
        /// </summary>
        private CommentView ViewForBcf(CommentRecord record)
        {
            if (record.View != null) { return record.View; }
            ApproachMarker(record.Local, out Vector3 feet, out float yaw, out float pitch);
            Vector3 world = ToRevit(feet);
            return new CommentView { X = world.X, Y = world.Y, Z = world.Z, Yaw = yaw, Pitch = pitch };
        }

        /// <summary>
        /// The commented element as a BCF component (IFC GUID when the snapshot has one, plus the ElementId), or null.
        /// </summary>
        private BcfComponent ComponentFor(CommentRecord record, string system)
        {
            int index = -1;
            if (record.ElementUniqueId != null && _elementIndexByUniqueId.TryGetValue(record.ElementUniqueId, out int byUnique)) { index = byUnique; }
            else if (record.ElementId > 0 && _elementIndexById.TryGetValue(record.ElementId, out int byId)) { index = byId; }

            if (index < 0)
            {
                return record.ElementId > 0
                    ? new BcfComponent { AuthoringToolId = record.ElementId.ToString(System.Globalization.CultureInfo.InvariantCulture), OriginatingSystem = system }
                    : null;
            }

            ElementRecord element = Scene.Elements[index];
            return new BcfComponent
            {
                IfcGuid = string.IsNullOrEmpty(element.IfcGuid) ? null : element.IfcGuid,
                AuthoringToolId = element.ElementId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                OriginatingSystem = system
            };
        }

        /// <summary>
        /// A picture as PNG bytes (BCF viewers expect snapshot.png), or null when it can't be read.
        /// </summary>
        private static byte[] ToPng(byte[] image)
        {
            if (image == null || image.Length == 0) { return null; }
            try
            {
                using var input = new MemoryStream(image);
                using var bitmap = new System.Drawing.Bitmap(input);
                using var output = new MemoryStream();
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                return output.ToArray();
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"Comment picture not converted for BCF: {ex.Message}");
                return null;
            }
        }

        /// <summary>The thumbnail's JPEG bytes (older comments without a larger picture), or null.</summary>
        private static byte[] ThumbnailBytes(CommentRecord record)
        {
            if (string.IsNullOrEmpty(record.Thumbnail)) { return null; }
            try { return Convert.FromBase64String(record.Thumbnail); }
            catch (FormatException) { return null; }
        }

        #endregion

        #region Import

        /// <summary>
        /// Reads a BCF file and merges or adds its topics as comments, saving once.
        /// </summary>
        private void ImportBcf()
        {
            _window.SetCaptured(false);
            _window.Input.ReleaseAll();
            string path = FileDialogs.ShowOpen(_window.Handle, "Import BCF", BcfFile.DIALOG_FILTER, SuggestedFolder());
            if (path == null) { return; }

            BcfReadResult result = BcfFile.Read(path, out string error);
            if (result == null)
            {
                _commentsNotice = "BCF import failed: " + error;
                Sound.Play(SoundId.Error);
                return;
            }

            try
            {
                BcfFrame frame = BcfFrame.Resolve(Scene.Site, _bcfCoordinates, out bool fellBack);
                var byGuid = new Dictionary<Guid, CommentRecord>();
                foreach (CommentRecord record in Comments.Comments)
                {
                    if (Guid.TryParse(record.Id, out Guid guid)) { byGuid[guid] = record; }
                }
                Dictionary<string, int> byIfcGuid = null; // built on first need

                var added = new List<CommentRecord>();
                var merged = new List<CommentRecord>();
                int replies = 0, unchanged = 0;
                foreach (BcfTopic topic in result.Topics)
                {
                    if (byGuid.TryGetValue(topic.Guid, out CommentRecord existing))
                    {
                        int count = BcfMapping.Merge(existing, topic, out bool changed);
                        replies += count;
                        if (changed || count > 0) { merged.Add(existing); }
                        else { unchanged++; }
                        continue;
                    }

                    CommentRecord record = BcfMapping.ToComment(topic);
                    PlaceImported(record, topic, frame, ref byIfcGuid);
                    ImportPictures(record, topic);
                    added.Add(record);
                    byGuid[topic.Guid] = record; // a file listing a topic twice merges the second
                }

                Comments.ApplyImport(added, merged);
                _commentsNotice = Comments.LastError ?? $"BCF {result.Version}: {added.Count} new, {merged.Count} updated" +
                    (replies > 0 ? $" ({replies} {(replies == 1 ? "reply" : "replies")} added)" : string.Empty) +
                    (unchanged > 0 ? $", {unchanged} unchanged" : string.Empty) +
                    (result.Skipped > 0 ? $", {result.Skipped} unreadable skipped" : string.Empty) +
                    (fellBack ? " · views read as internal coordinates" : string.Empty);
                Utilities.Log_Utils.Write($"BCF import from {path}: {added.Count} new, {merged.Count} updated, {replies} replies, {unchanged} unchanged, {result.Skipped} skipped ({CoordinateName(frame.Kind)} coordinates).");
                Sound.Play(SoundId.Commit);
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"BCF import failed: {ex}");
                _commentsNotice = "BCF import failed: " + ex.Message;
                Sound.Play(SoundId.Error);
            }
        }

        /// <summary>
        /// Gives an imported comment its view, marker, element and level: the view from the viewpoint (walking when
        /// there is a floor just under the feet, else flying); the marker where the view's centre ray first meets the
        /// model, else at the named element's centre, else 2 m ahead of the camera.
        /// </summary>
        private void PlaceImported(CommentRecord record, BcfTopic topic, BcfFrame frame, ref Dictionary<string, int> byIfcGuid)
        {
            Vector3 origin = Scene.OriginOffset;
            int element = FindBcfElement(topic.Viewpoint, ref byIfcGuid);

            Vector3 marker;
            if (BcfMapping.TryToView(topic.Viewpoint, CharacterController.STAND_EYE, frame, out CommentView view))
            {
                var feet = new Vector3((float)(view.X - origin.X), (float)(view.Y - origin.Y), (float)(view.Z - origin.Z));
                view.Flying = !(Pick(feet + new Vector3(0f, 0f, 0.5f), -Vector3.UnitZ, 0.9f, out RayHit floor) && floor.Normal.Z > 0.7f);
                view.Section = BcfMapping.ToSection(topic.Viewpoint.ClippingPlanes, frame, out _) ?? new SectionCut();
                record.View = view;

                Vector3 eye = feet + new Vector3(0f, 0f, CharacterController.STAND_EYE);
                float cp = MathF.Cos(view.Pitch);
                var look = new Vector3(cp * MathF.Cos(view.Yaw), cp * MathF.Sin(view.Yaw), MathF.Sin(view.Pitch));
                if (Pick(eye, look, 300f, out RayHit hit))
                {
                    marker = hit.Point + hit.Normal * 0.06f;
                    if (element < 0) { element = hit.Element; }
                }
                else if (element >= 0) { marker = Scene.Elements[element].Bounds.Center; }
                else { marker = eye + look * 2f; }
            }
            else if (element >= 0)
            {
                marker = Scene.Elements[element].Bounds.Center;
            }
            else
            {
                // No camera and no element: at the player, so it can be found and moved on
                marker = _player.Feet + new Vector3(0f, 0f, 1.2f);
            }

            Comments.SetMarker(record, marker);
            record.Level = LevelNameAt(marker.Z);
            if (element >= 0 && element < Scene.Elements.Length)
            {
                ElementRecord found = Scene.Elements[element];
                record.ElementId = found.IsLinked || found.IsLibraryTemplate ? -1 : found.ElementId;
                record.ElementUniqueId = found.IsLinked || found.IsLibraryTemplate || string.IsNullOrEmpty(found.UniqueId) ? null : found.UniqueId;
            }
        }

        /// <summary>
        /// The element a viewpoint selects: by IFC GUID (any model), else by the authoring tool's id (host ElementId),
        /// else -1.
        /// </summary>
        private int FindBcfElement(BcfViewpoint viewpoint, ref Dictionary<string, int> byIfcGuid)
        {
            if (viewpoint?.Selection == null || viewpoint.Selection.Count == 0) { return -1; }
            foreach (BcfComponent component in viewpoint.Selection)
            {
                if (component.IfcGuid != null)
                {
                    if (byIfcGuid == null)
                    {
                        byIfcGuid = new Dictionary<string, int>(StringComparer.Ordinal);
                        for (int e = 0; e < Scene.Elements.Length; e++)
                        {
                            string guid = Scene.Elements[e].IfcGuid;
                            if (!string.IsNullOrEmpty(guid) && !Scene.Elements[e].IsLibraryTemplate) { byIfcGuid.TryAdd(guid, e); }
                        }
                    }
                    if (byIfcGuid.TryGetValue(component.IfcGuid, out int index)) { return index; }
                }
                if (long.TryParse(component.AuthoringToolId, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long id)
                    && _elementIndexById.TryGetValue(id, out int byId))
                {
                    return byId;
                }
            }
            return -1;
        }

        /// <summary>
        /// The topic's snapshot as the comment's thumbnail (192 × 108, centre-cropped) and BCF picture (JPEG, at most
        /// 1280 px wide). Unreadable images leave the comment without pictures.
        /// </summary>
        private static void ImportPictures(CommentRecord record, BcfTopic topic)
        {
            if (topic.Snapshot == null || topic.Snapshot.Length == 0) { return; }
            try
            {
                using var stream = new MemoryStream(topic.Snapshot);
                using var image = new System.Drawing.Bitmap(stream);
                record.Thumbnail = Convert.ToBase64String(EncodeScaled(image, THUMB_WIDTH, THUMB_HEIGHT, crop: true, THUMB_JPEG_QUALITY));

                int width = Math.Min(SNAPSHOT_MAX_WIDTH, image.Width);
                int height = Math.Max(1, (int)Math.Round((double)image.Height * width / Math.Max(1, image.Width)));
                record.SnapshotData = EncodeScaled(image, width, height, crop: false, SNAPSHOT_JPEG_QUALITY);
                record.Snapshot = CommentSnapshots.NameFor(record.Id);
                record.SnapshotDirty = true;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"BCF snapshot of {topic.Guid} unreadable: {ex.Message}");
            }
        }

        /// <summary>
        /// Draws an image into a new size (high-quality resampling; crop: centre part of the target's aspect) and
        /// encodes it as a JPEG.
        /// </summary>
        private static byte[] EncodeScaled(System.Drawing.Image image, int width, int height, bool crop, long quality)
        {
            var source = new System.Drawing.Rectangle(0, 0, image.Width, image.Height);
            if (crop)
            {
                float aspect = (float)width / height;
                if ((float)image.Width / image.Height > aspect)
                {
                    int w = (int)(image.Height * aspect);
                    source = new System.Drawing.Rectangle((image.Width - w) / 2, 0, w, image.Height);
                }
                else
                {
                    int h = (int)(image.Width / aspect);
                    source = new System.Drawing.Rectangle(0, (image.Height - h) / 2, image.Width, h);
                }
            }

            using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.Clear(System.Drawing.Color.Black);
                graphics.DrawImage(image, new System.Drawing.Rectangle(0, 0, width, height), source, System.Drawing.GraphicsUnit.Pixel);
            }

            System.Drawing.Imaging.ImageCodecInfo jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
            using var output = new MemoryStream();
            if (jpeg != null)
            {
                using var parameters = new System.Drawing.Imaging.EncoderParameters(1);
                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bitmap.Save(output, jpeg, parameters);
            }
            else
            {
                bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            }
            return output.ToArray();
        }

        #endregion
    }
}
