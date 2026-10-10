using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

// The class belongs to the Format namespace
namespace BimGo.Format
{
    /// <summary>
    /// Reads and writes BCF files (<c>.bcf</c>; <c>.bcfzip</c> is read too): plain BCF 2.1 out, and 2.0 / 2.1 / 3.0 in, as far as BimGo's
    /// comments need (topic fields, the comment thread, the first viewpoint's camera and selection, its snapshot).
    /// <code>
    /// bcf.version                 VersionId 2.1
    /// project.bcfp                project id and name
    /// &lt;topic guid&gt;/markup.bcf      topic, comments, viewpoint list
    /// &lt;topic guid&gt;/viewpoint.bcfv  camera, selected components
    /// &lt;topic guid&gt;/snapshot.png    picture of the viewpoint (PNG: the name and format most viewers expect)
    /// </code>
    /// Reading is namespace-agnostic and tolerant: unknown elements are ignored, a damaged topic is skipped (counted)
    /// rather than failing the file, DTDs are refused and entry sizes are capped. Never throws.
    /// </summary>
    public static class BcfFile
    {
        /// <summary>The file extension (with the dot).</summary>
        public const string EXTENSION = ".bcf";

        /// <summary>The file dialog filter (BCF 2.x files are .bcf; older tools wrote .bcfzip).</summary>
        public const string DIALOG_FILTER = "BCF file (*.bcf; *.bcfzip)|*.bcf;*.bcfzip|All files (*.*)|*.*";

        // The XML Schema instance namespaces declared on each root, as buildingSMART's sample files do
        private static readonly XNamespace XSI = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace XSD = "http://www.w3.org/2001/XMLSchema";

        /// <summary>The BCF version written.</summary>
        public const string VERSION = "2.1";

        // Caps for reading (a BCF file is small; anything bigger is damaged or hostile)
        private const long MAX_XML_BYTES = 8L * 1024 * 1024;
        private const long MAX_IMAGE_BYTES = 32L * 1024 * 1024;
        private const int MAX_TOPICS = 20_000;

        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private static readonly UTF8Encoding UTF8 = new(encoderShouldEmitUTF8Identifier: false);

        #region Write

        /// <summary>
        /// Writes topics to a BCF 2.1 file (temp file, then replace).
        /// </summary>
        /// <param name="path">The .bcfzip path.</param>
        /// <param name="project">The project (id and name).</param>
        /// <param name="topics">The topics.</param>
        /// <param name="error">A reason on failure.</param>
        /// <returns>True on success.</returns>
        public static bool Write(string path, BcfProject project, IReadOnlyList<BcfTopic> topics, out string error)
        {
            error = null;
            string temp = path + ".tmp";
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder)) { Directory.CreateDirectory(folder); }
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteXml(zip, "bcf.version", new XElement("Version",
                        new XAttribute("VersionId", VERSION),
                        new XElement("DetailedVersion", VERSION)));

                    project ??= new BcfProject();
                    WriteXml(zip, "project.bcfp", new XElement("ProjectExtension",
                        new XElement("Project", new XAttribute("ProjectId", project.ProjectId ?? Guid.NewGuid().ToString("D")),
                            new XElement("Name", project.Name ?? string.Empty)),
                        new XElement("ExtensionSchema", EXTENSIONS_FILE)));
                    WriteExtensions(zip, topics);

                    foreach (BcfTopic topic in topics ?? Array.Empty<BcfTopic>())
                    {
                        if (topic == null) { continue; }
                        string folderName = topic.Guid.ToString("D");
                        string snapshotName = topic.Snapshot is { Length: > 0 } ? "snapshot" + NormaliseExtension(topic.SnapshotExtension) : null;
                        WriteXml(zip, folderName + "/markup.bcf", BuildMarkup(topic, snapshotName));
                        if (topic.Viewpoint != null) { WriteXml(zip, folderName + "/viewpoint.bcfv", BuildViewpoint(topic.Viewpoint)); }
                        if (snapshotName != null)
                        {
                            ZipArchiveEntry entry = zip.CreateEntry(folderName + "/" + snapshotName, CompressionLevel.NoCompression);
                            using Stream image = entry.Open();
                            image.Write(topic.Snapshot, 0, topic.Snapshot.Length);
                        }
                    }
                }
                File.Move(temp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Utilities.Log_Utils.Write($"BCF export failed: {ex}");
                try { if (File.Exists(temp)) { File.Delete(temp); } } catch { /* best effort */ }
                return false;
            }
        }

        /// <summary>markup.bcf: the topic, its comments and the viewpoint list (BCF 2.1 element order).</summary>
        private static XElement BuildMarkup(BcfTopic topic, string snapshotName)
        {
            var topicElement = new XElement("Topic",
                new XAttribute("Guid", topic.Guid.ToString("D")),
                Optional("TopicType", topic.TopicType, attribute: true),
                Optional("TopicStatus", topic.Status, attribute: true),
                new XElement("Title", Truncate(topic.Title, 200)),
                Optional("Priority", topic.Priority),
                new XElement("CreationDate", Date(topic.CreationDate)),
                new XElement("CreationAuthor", topic.CreationAuthor ?? string.Empty),
                topic.ModifiedDate.HasValue ? new XElement("ModifiedDate", Date(topic.ModifiedDate.Value)) : null,
                Optional("ModifiedAuthor", topic.ModifiedAuthor),
                Optional("AssignedTo", topic.AssignedTo),
                Optional("Description", topic.Description));

            var markup = new XElement("Markup", topicElement);
            foreach (BcfComment comment in topic.Comments ?? new List<BcfComment>())
            {
                if (comment == null || string.IsNullOrWhiteSpace(comment.Text)) { continue; }
                markup.Add(new XElement("Comment",
                    new XAttribute("Guid", comment.Guid.ToString("D")),
                    new XElement("Date", Date(comment.Date)),
                    new XElement("Author", comment.Author ?? string.Empty),
                    new XElement("Comment", comment.Text),
                    comment.ModifiedDate.HasValue ? new XElement("ModifiedDate", Date(comment.ModifiedDate.Value)) : null,
                    Optional("ModifiedAuthor", comment.ModifiedAuthor)));
            }

            if (topic.Viewpoint != null)
            {
                markup.Add(new XElement("Viewpoints",
                    new XAttribute("Guid", topic.Viewpoint.Guid.ToString("D")),
                    new XElement("Viewpoint", "viewpoint.bcfv"),
                    snapshotName != null ? new XElement("Snapshot", snapshotName) : null));
            }
            return markup;
        }

        /// <summary>viewpoint.bcfv: selected components, default visibility and the perspective camera.</summary>
        private static XElement BuildViewpoint(BcfViewpoint viewpoint)
        {
            var components = new XElement("Components");
            if (viewpoint.Selection is { Count: > 0 })
            {
                var selection = new XElement("Selection");
                foreach (BcfComponent component in viewpoint.Selection)
                {
                    if (component == null || (string.IsNullOrEmpty(component.IfcGuid) && string.IsNullOrEmpty(component.AuthoringToolId))) { continue; }
                    selection.Add(new XElement("Component",
                        string.IsNullOrEmpty(component.IfcGuid) ? null : new XAttribute("IfcGuid", component.IfcGuid),
                        Optional("OriginatingSystem", component.OriginatingSystem),
                        Optional("AuthoringToolId", component.AuthoringToolId)));
                }
                if (selection.HasElements) { components.Add(selection); }
            }
            components.Add(new XElement("Visibility", new XAttribute("DefaultVisibility", "true")));

            return new XElement("VisualizationInfo",
                new XAttribute("Guid", viewpoint.Guid.ToString("D")),
                components,
                new XElement("PerspectiveCamera",
                    Vector("CameraViewPoint", viewpoint.Position),
                    Vector("CameraDirection", viewpoint.Direction),
                    Vector("CameraUpVector", viewpoint.Up),
                    new XElement("FieldOfView", Number(viewpoint.FieldOfView))),
                viewpoint.ClippingPlanes is { Count: > 0 }
                    ? new XElement("ClippingPlanes", viewpoint.ClippingPlanes.Select(c => new XElement("ClippingPlane",
                        Vector("Location", c.Location), Vector("Direction", c.Direction))))
                    : null);
        }

        /// <summary>The extension schema written beside project.bcfp.</summary>
        public const string EXTENSIONS_FILE = "extensions.xsd";

        /// <summary>
        /// extensions.xsd: the topic types, statuses and priorities this file uses (BimGo's three of each plus any
        /// other value a topic carries), as an XML Schema redefine of markup.xsd like buildingSMART's samples. Viewers
        /// read it for their drop-downs; an empty ExtensionSchema reference made some refuse the file.
        /// </summary>
        private static void WriteExtensions(ZipArchive zip, IReadOnlyList<BcfTopic> topics)
        {
            var types = new List<string> { "Issue" };
            var statuses = new List<string> { "Open", "In Progress", "Closed" };
            var priorities = new List<string> { "Low", "Normal", "High" };
            foreach (BcfTopic topic in topics ?? Array.Empty<BcfTopic>())
            {
                AddValue(types, topic?.TopicType);
                AddValue(statuses, topic?.Status);
                AddValue(priorities, topic?.Priority);
            }

            XNamespace xs = XSD;
            XElement Restriction(string name, IEnumerable<string> values) => new(xs + "simpleType", new XAttribute("name", name),
                new XElement(xs + "restriction", new XAttribute("base", name),
                    values.Select(v => new XElement(xs + "enumeration", new XAttribute("value", v)))));

            var schema = new XElement(xs + "schema",
                new XElement(xs + "redefine", new XAttribute("schemaLocation", "markup.xsd"),
                    Restriction("TopicType", types),
                    Restriction("TopicStatus", statuses),
                    Restriction("TopicLabel", Array.Empty<string>()),
                    Restriction("SnippetType", Array.Empty<string>()),
                    Restriction("Priority", priorities),
                    Restriction("UserIdType", Array.Empty<string>()),
                    Restriction("Stage", Array.Empty<string>())));

            ZipArchiveEntry entry = zip.CreateEntry(EXTENSIONS_FILE, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            using XmlWriter writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = UTF8, Indent = true });
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), schema).Save(writer);

            static void AddValue(List<string> list, string value)
            {
                if (string.IsNullOrWhiteSpace(value)) { return; }
                value = value.Trim();
                if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) { list.Add(value); }
            }
        }

        private static void WriteXml(ZipArchive zip, string name, XElement root)
        {
            root.Add(new XAttribute(XNamespace.Xmlns + "xsi", XSI.NamespaceName), new XAttribute(XNamespace.Xmlns + "xsd", XSD.NamespaceName));
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using Stream stream = entry.Open();
            var settings = new XmlWriterSettings { Encoding = UTF8, Indent = true };
            using XmlWriter writer = XmlWriter.Create(stream, settings);
            new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
        }

        private static XElement Vector(string name, BcfVector v) => new(name,
            new XElement("X", Number(v.X)), new XElement("Y", Number(v.Y)), new XElement("Z", Number(v.Z)));

        private static string Number(double value) => double.IsFinite(value) ? value.ToString("0.#######", INV) : "0";

        private static string Date(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", INV);

        private static XObject Optional(string name, string value, bool attribute = false)
        {
            if (string.IsNullOrWhiteSpace(value)) { return null; }
            return attribute ? new XAttribute(name, value.Trim()) : new XElement(name, value.Trim());
        }

        private static string Truncate(string text, int max)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= max ? text : text[..(max - 1)] + "…";
        }

        private static string NormaliseExtension(string extension) =>
            string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png";

        #endregion

        #region Read

        /// <summary>
        /// Reads a BCF file.
        /// </summary>
        /// <param name="path">The .bcfzip (or .bcf) path.</param>
        /// <param name="error">A reason when nothing could be read.</param>
        /// <returns>The topics, or null on failure.</returns>
        public static BcfReadResult Read(string path, out string error)
        {
            error = null;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
                var result = new BcfReadResult();

                // Entries by normalised name (forward slashes, case-insensitive)
                var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/').TrimStart('/');
                    if (name.Length > 0 && !name.EndsWith('/')) { entries[name] = entry; }
                }

                XElement version = LoadXml(entries, "bcf.version");
                if (version != null)
                {
                    result.Version = version.Attribute("VersionId")?.Value?.Trim() ?? Child(version, "DetailedVersion")?.Value?.Trim() ?? "?";
                }

                XElement project = LoadXml(entries, "project.bcfp");
                XElement projectNode = project == null ? null : Descendant(project, "Project");
                if (projectNode != null)
                {
                    result.Project = new BcfProject
                    {
                        ProjectId = projectNode.Attribute("ProjectId")?.Value ?? string.Empty,
                        Name = Child(projectNode, "Name")?.Value ?? string.Empty
                    };
                }

                foreach (string name in entries.Keys.Where(n => n.EndsWith("/markup.bcf", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.Ordinal).ToList())
                {
                    if (result.Topics.Count >= MAX_TOPICS) { result.Skipped++; continue; }
                    string folder = name[..^"/markup.bcf".Length];
                    BcfTopic topic = ReadTopic(entries, folder);
                    if (topic == null) { result.Skipped++; }
                    else { result.Topics.Add(topic); }
                }

                if (result.Topics.Count == 0 && result.Skipped == 0)
                {
                    error = "No topics found (is this a BCF file?)";
                    return null;
                }
                return result;
            }
            catch (Exception ex)
            {
                error = ex is InvalidDataException ? "Not a BCF file (not a ZIP archive)" : ex.Message;
                Utilities.Log_Utils.Write($"BCF import failed: {ex}");
                return null;
            }
        }

        /// <summary>One topic folder, or null when its markup can't be read.</summary>
        private static BcfTopic ReadTopic(Dictionary<string, ZipArchiveEntry> entries, string folder)
        {
            try
            {
                XElement markup = LoadXml(entries, folder + "/markup.bcf");
                XElement topicNode = markup == null ? null : (markup.Name.LocalName == "Topic" ? markup : Descendant(markup, "Topic"));
                if (topicNode == null) { return null; }

                var topic = new BcfTopic
                {
                    Guid = ParseGuid(topicNode.Attribute("Guid")?.Value) ?? ParseGuid(LastSegment(folder)) ?? Guid.NewGuid(),
                    TopicType = Attr(topicNode, "TopicType") ?? Text(topicNode, "TopicType"),
                    Status = Attr(topicNode, "TopicStatus") ?? Text(topicNode, "TopicStatus"),
                    Title = Text(topicNode, "Title") ?? string.Empty,
                    Priority = Text(topicNode, "Priority"),
                    CreationDate = ParseDate(Text(topicNode, "CreationDate")) ?? DateTimeOffset.Now,
                    CreationAuthor = Text(topicNode, "CreationAuthor") ?? string.Empty,
                    ModifiedDate = ParseDate(Text(topicNode, "ModifiedDate")),
                    ModifiedAuthor = Text(topicNode, "ModifiedAuthor"),
                    AssignedTo = Text(topicNode, "AssignedTo"),
                    Description = Text(topicNode, "Description")
                };

                // Comments: BCF 2.x keeps them under Markup, 3.0 under Topic/Comments; either way an element named
                // Comment holding a child named Comment (the text)
                foreach (XElement node in markup.Descendants().Where(e => e.Name.LocalName == "Comment" && Child(e, "Comment") != null))
                {
                    string text = Child(node, "Comment")?.Value;
                    if (string.IsNullOrWhiteSpace(text)) { continue; }
                    topic.Comments.Add(new BcfComment
                    {
                        Guid = ParseGuid(node.Attribute("Guid")?.Value) ?? Guid.NewGuid(),
                        Date = ParseDate(Text(node, "Date")) ?? topic.CreationDate,
                        Author = Text(node, "Author") ?? string.Empty,
                        Text = text.Trim(),
                        ModifiedDate = ParseDate(Text(node, "ModifiedDate")),
                        ModifiedAuthor = Text(node, "ModifiedAuthor")
                    });
                }
                topic.Comments.Sort((a, b) => a.Date.CompareTo(b.Date));

                // The first viewpoint: 2.x Markup/Viewpoints, 3.0 Topic/Viewpoints/ViewPoint (both hold Viewpoint + Snapshot)
                XElement reference = markup.Descendants().FirstOrDefault(e =>
                    (e.Name.LocalName == "Viewpoints" || e.Name.LocalName == "ViewPoint") && Child(e, "Viewpoint") != null);
                string viewpointFile = reference != null ? Text(reference, "Viewpoint") : null;
                string snapshotFile = reference != null ? Text(reference, "Snapshot") : null;
                viewpointFile ??= entries.ContainsKey(folder + "/viewpoint.bcfv") ? "viewpoint.bcfv" : null;
                snapshotFile ??= entries.ContainsKey(folder + "/snapshot.png") ? "snapshot.png" : entries.ContainsKey(folder + "/snapshot.jpg") ? "snapshot.jpg" : null;

                if (viewpointFile != null)
                {
                    topic.Viewpoint = ReadViewpoint(LoadXml(entries, folder + "/" + viewpointFile));
                    if (topic.Viewpoint != null && reference != null && ParseGuid(reference.Attribute("Guid")?.Value) is Guid guid) { topic.Viewpoint.Guid = guid; }
                }
                if (snapshotFile != null)
                {
                    topic.Snapshot = ReadBytes(entries, folder + "/" + snapshotFile, MAX_IMAGE_BYTES);
                    topic.SnapshotExtension = snapshotFile.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
                }
                return topic;
            }
            catch (Exception ex)
            {
                Utilities.Log_Utils.Write($"BCF topic {folder} skipped: {ex.Message}");
                return null;
            }
        }

        /// <summary>A viewpoint's camera and selection, or null when it has no usable camera.</summary>
        private static BcfViewpoint ReadViewpoint(XElement root)
        {
            if (root == null) { return null; }
            var viewpoint = new BcfViewpoint { Guid = ParseGuid(root.Attribute("Guid")?.Value) ?? Guid.NewGuid() };

            XElement camera = Descendant(root, "PerspectiveCamera");
            if (camera == null)
            {
                camera = Descendant(root, "OrthogonalCamera");
                viewpoint.Orthogonal = camera != null;
            }
            if (camera == null) { return null; }

            BcfVector? position = ReadVector(Child(camera, "CameraViewPoint"));
            BcfVector? direction = ReadVector(Child(camera, "CameraDirection"));
            if (position == null || direction == null || direction.Value.Length < 1e-9) { return null; }
            viewpoint.Position = position.Value;
            viewpoint.Direction = direction.Value;
            viewpoint.Up = ReadVector(Child(camera, "CameraUpVector")) ?? new BcfVector(0, 0, 1);
            string fov = Text(camera, viewpoint.Orthogonal ? "ViewToWorldScale" : "FieldOfView");
            if (double.TryParse(fov, NumberStyles.Float, INV, out double value) && double.IsFinite(value)) { viewpoint.FieldOfView = value; }

            XElement clipping = Descendant(root, "ClippingPlanes");
            if (clipping != null)
            {
                foreach (XElement node in clipping.Elements().Where(e => e.Name.LocalName == "ClippingPlane"))
                {
                    BcfVector? location = ReadVector(Child(node, "Location"));
                    BcfVector? clipDirection = ReadVector(Child(node, "Direction"));
                    if (location != null && clipDirection != null && clipDirection.Value.Length > 1e-9) { viewpoint.ClippingPlanes.Add(new BcfClippingPlane(location.Value, clipDirection.Value)); }
                }
            }

            XElement selection = Descendant(root, "Selection");
            if (selection != null)
            {
                foreach (XElement node in selection.Elements().Where(e => e.Name.LocalName == "Component"))
                {
                    var component = new BcfComponent
                    {
                        IfcGuid = Attr(node, "IfcGuid"),
                        AuthoringToolId = Text(node, "AuthoringToolId"),
                        OriginatingSystem = Text(node, "OriginatingSystem")
                    };
                    if (component.IfcGuid != null || component.AuthoringToolId != null) { viewpoint.Selection.Add(component); }
                }
            }
            return viewpoint;
        }

        private static BcfVector? ReadVector(XElement node)
        {
            if (node == null) { return null; }
            if (!TryNumber(Text(node, "X"), out double x) || !TryNumber(Text(node, "Y"), out double y) || !TryNumber(Text(node, "Z"), out double z)) { return null; }
            var vector = new BcfVector(x, y, z);
            return vector.IsFinite ? vector : null;
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text, NumberStyles.Float, INV, out value);

        private static XElement LoadXml(Dictionary<string, ZipArchiveEntry> entries, string name)
        {
            byte[] bytes = ReadBytes(entries, name, MAX_XML_BYTES);
            if (bytes == null) { return null; }
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var stream = new MemoryStream(bytes);
            using XmlReader reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader).Root;
        }

        private static byte[] ReadBytes(Dictionary<string, ZipArchiveEntry> entries, string name, long max)
        {
            name = name.Replace('\\', '/');
            if (!entries.TryGetValue(name, out ZipArchiveEntry entry) || entry.Length > max) { return null; }
            using Stream stream = entry.Open();
            using var memory = new MemoryStream((int)Math.Min(entry.Length, max));
            stream.CopyTo(memory);
            return memory.Length > max ? null : memory.ToArray();
        }

        private static XElement Child(XElement parent, string localName) =>
            parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

        private static XElement Descendant(XElement parent, string localName) =>
            parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

        private static string Text(XElement parent, string localName)
        {
            string value = Child(parent, localName)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string Attr(XElement node, string name)
        {
            string value = node?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static Guid? ParseGuid(string text) => Guid.TryParse(text?.Trim(), out Guid guid) ? guid : null;

        private static DateTimeOffset? ParseDate(string text) =>
            DateTimeOffset.TryParse(text, INV, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? date : null;

        private static string LastSegment(string folder)
        {
            int slash = folder.LastIndexOf('/');
            return slash >= 0 ? folder[(slash + 1)..] : folder;
        }

        #endregion
    }
}
