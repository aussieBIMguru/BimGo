using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// BCF round: IFC GUIDs, BCF 2.1 write / read, comment ↔ topic mapping and merging, viewpoint coordinates, and the
    /// comment pictures kept for BCF snapshots (.bimgo entries and model-folder files).
    /// </summary>
    [TestClass]
    public sealed class BcfTests
    {
        private static readonly byte[] JPEG = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 0xFF, 0xD9 };

        #region IFC GUID

        [TestMethod]
        public void IfcGuid_ExtremesAndRoundTrip()
        {
            Assert.AreEqual("0000000000000000000000", IfcGuid.Encode(Guid.Empty));
            Assert.AreEqual("3$$$$$$$$$$$$$$$$$$$$$", IfcGuid.Encode(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff")));

            for (int i = 0; i < 50; i++)
            {
                Guid guid = Guid.NewGuid();
                string text = IfcGuid.Encode(guid);
                Assert.AreEqual(22, text.Length);
                Assert.IsTrue(IfcGuid.IsValid(text));
                Assert.IsTrue(IfcGuid.TryDecode(text, out Guid back));
                Assert.AreEqual(guid, back);
            }
        }

        [TestMethod]
        public void IfcGuid_UsesTextOrderMostSignificantFirst()
        {
            // Only the last hex digit set: the value 1 lands in the last character
            Assert.AreEqual("0000000000000000000001", IfcGuid.Encode(Guid.Parse("00000000-0000-0000-0000-000000000001")));
            // Only the top bit set: the first character holds the top 2 bits (2 = binary 10)
            Assert.AreEqual("2000000000000000000000", IfcGuid.Encode(Guid.Parse("80000000-0000-0000-0000-000000000000")));
        }

        [TestMethod]
        public void IfcGuid_RejectsBadText()
        {
            Assert.IsFalse(IfcGuid.IsValid(null));
            Assert.IsFalse(IfcGuid.IsValid("short"));
            Assert.IsFalse(IfcGuid.IsValid("4000000000000000000000"), "the first character only holds 2 bits");
            Assert.IsFalse(IfcGuid.IsValid("000000000000000000000!"));
            Assert.IsFalse(IfcGuid.TryDecode("nope", out _));
        }

        #endregion

        #region Mapping

        [TestMethod]
        public void Status_And_Priority_MapBothWays()
        {
            foreach (string status in CommentStatus.ALL)
            {
                Assert.AreEqual(status, BcfMapping.StatusFromBcf(BcfMapping.StatusToBcf(status)));
            }
            foreach (string priority in CommentPriority.ALL)
            {
                Assert.AreEqual(priority, BcfMapping.PriorityFromBcf(BcfMapping.PriorityToBcf(priority)));
            }

            Assert.AreEqual(CommentStatus.CLOSED, BcfMapping.StatusFromBcf("Resolved"));
            Assert.AreEqual(CommentStatus.CLOSED, BcfMapping.StatusFromBcf("done"));
            Assert.AreEqual(CommentStatus.IN_PROGRESS, BcfMapping.StatusFromBcf("Active"));
            Assert.AreEqual(CommentStatus.OPEN, BcfMapping.StatusFromBcf("ReOpened"));
            Assert.AreEqual(CommentStatus.OPEN, BcfMapping.StatusFromBcf(null));
            Assert.AreEqual(CommentPriority.HIGH, BcfMapping.PriorityFromBcf("Critical"));
            Assert.AreEqual(CommentPriority.LOW, BcfMapping.PriorityFromBcf("Minor"));
            Assert.AreEqual(CommentPriority.NORMAL, BcfMapping.PriorityFromBcf("On hold"));
        }

        [TestMethod]
        public void TitleOf_TakesTheFirstLineAndCutsLongOnes()
        {
            Assert.AreEqual("Door swing", BcfMapping.TitleOf("Door swing\nclashes with the bench"));
            string title = BcfMapping.TitleOf(new string('x', 200));
            Assert.AreEqual(BcfMapping.TITLE_LENGTH, title.Length);
            Assert.IsTrue(title.EndsWith("…"));
        }

        [TestMethod]
        public void TextOf_CombinesTitleAndDescription_OrUsesTheFirstComment()
        {
            var topic = new BcfTopic { Title = "Clash", Description = "Duct hits beam" };
            Assert.AreEqual("Clash — Duct hits beam", BcfMapping.TextOf(topic, out _));

            topic = new BcfTopic { Title = "Duct hits…", Description = "Duct hits beam at grid C" };
            Assert.AreEqual("Duct hits beam at grid C", BcfMapping.TextOf(topic, out _), "BimGo's own cut title isn't repeated");

            topic = new BcfTopic { Title = "Clash", Comments = { new BcfComment { Text = "See the beam" } } };
            Assert.AreEqual("Clash — See the beam", BcfMapping.TextOf(topic, out BcfComment used));
            Assert.IsNotNull(used);

            Assert.AreEqual("(untitled issue)", BcfMapping.TextOf(new BcfTopic(), out _));
        }

        [TestMethod]
        public void ToComment_ThenMerge_UpdatesFieldsAndAddsOnlyNewReplies()
        {
            Guid topicGuid = Guid.NewGuid();
            var reply = new BcfComment { Author = "sam", Text = "On it", Date = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(10)) };
            var topic = new BcfTopic
            {
                Guid = topicGuid, Title = "Door swing", Description = "Door swing clashes", Status = "Open", Priority = "High",
                AssignedTo = "sam", CreationAuthor = "gavin", Comments = { reply }
            };

            CommentRecord record = BcfMapping.ToComment(topic);
            Assert.AreEqual(topicGuid.ToString("N"), record.Id);
            Assert.AreEqual("Door swing clashes", record.Text);
            Assert.AreEqual(CommentPriority.HIGH, record.Priority);
            Assert.AreEqual("sam", record.AssignedTo);
            Assert.AreEqual(1, record.ReplyCount);

            // The same topic again changes nothing
            Assert.AreEqual(0, BcfMapping.Merge(record, topic, out bool changed));
            Assert.IsFalse(changed);

            // Closed elsewhere with a new reply
            topic.Status = "Closed";
            topic.ModifiedAuthor = "sam";
            topic.Comments.Add(new BcfComment { Author = "sam", Text = "Fixed in Revit", Date = reply.Date.AddHours(1) });
            Assert.AreEqual(1, BcfMapping.Merge(record, topic, out changed));
            Assert.IsTrue(changed);
            Assert.AreEqual(CommentStatus.CLOSED, record.Status);
            Assert.AreEqual("sam", record.UpdatedBy);
            Assert.AreEqual(2, record.ReplyCount);
            Assert.AreEqual("Fixed in Revit", record.Replies[^1].Text);
        }

        [TestMethod]
        public void Merge_SkipsADescriptionRepeatedAsAComment()
        {
            var record = new CommentRecord { Text = "Check this door swing" };
            var topic = new BcfTopic { Comments = { new BcfComment { Text = "Check this door swing" } } };
            Assert.AreEqual(0, BcfMapping.Merge(record, topic, out _));
            Assert.AreEqual(0, record.ReplyCount);
        }

        [TestMethod]
        public void ToTopic_KeepsIdsFieldsAndReplies()
        {
            var record = new CommentRecord
            {
                Text = "Check this door swing\nsecond line", Author = "gavin", Status = CommentStatus.IN_PROGRESS,
                Priority = CommentPriority.LOW, AssignedTo = "sam",
                Replies = new List<CommentReply> { new() { Author = "sam", Text = "Looking" } }
            };
            BcfTopic topic = BcfMapping.ToTopic(record);
            Assert.AreEqual(record.Id, topic.Guid.ToString("N"));
            Assert.AreEqual("Check this door swing", topic.Title);
            Assert.AreEqual(record.Text, topic.Description);
            Assert.AreEqual("In Progress", topic.Status);
            Assert.AreEqual("Low", topic.Priority);
            Assert.AreEqual(1, topic.Comments.Count);
            Assert.AreEqual(record.Replies[0].Id, topic.Comments[0].Guid.ToString("N"));

            // A non-GUID id (older files) still gets a stable GUID
            Assert.AreEqual(BcfMapping.ToTopic(new CommentRecord { Id = "c1", Text = "a" }).Guid, BcfMapping.ToTopic(new CommentRecord { Id = "c1", Text = "b" }).Guid);
        }

        #endregion

        #region Coordinates

        private static SiteInfo Site() => new()
        {
            HasSharedTransform = true, SharedEast = 280000.123456, SharedNorth = 6130000.654321, SharedElevation = 50.5, SharedAngle = 0.3,
            ProjectBasePoint = new SitePoint { Position = new System.Numerics.Vector3(5, 6, 1) }
        };

        [TestMethod]
        public void Frames_RoundTripPointsAndDirections()
        {
            foreach (BcfCoordinates kind in Enum.GetValues<BcfCoordinates>())
            {
                BcfFrame frame = BcfFrame.Resolve(Site(), kind, out bool fellBack);
                Assert.IsFalse(fellBack, kind.ToString());
                Assert.AreEqual(kind, frame.Kind);

                BcfVector p = frame.PointToBcf(12.345, -6.789, 3.21);
                frame.PointFromBcf(p, out double x, out double y, out double z);
                Assert.AreEqual(12.345, x, 1e-6);
                Assert.AreEqual(-6.789, y, 1e-6);
                Assert.AreEqual(3.21, z, 1e-6);

                BcfVector d = frame.DirectionToBcf(0.6, 0.8, 0);
                frame.DirectionFromBcf(d, out x, out y, out z);
                Assert.AreEqual(0.6, x, 1e-9);
                Assert.AreEqual(0.8, y, 1e-9);
            }

            BcfFrame shared = BcfFrame.Resolve(Site(), BcfCoordinates.Shared, out _);
            BcfVector origin = shared.PointToBcf(0, 0, 0);
            Assert.AreEqual(280000.123456, origin.X, 1e-6, "the internal origin lands on its shared position");
            Assert.AreEqual(50.5, origin.Z, 1e-9);
        }

        [TestMethod]
        public void Frames_FallBackToInternalWithoutASite()
        {
            BcfFrame frame = BcfFrame.Resolve(new SiteInfo(), BcfCoordinates.Shared, out bool fellBack);
            Assert.IsTrue(fellBack);
            Assert.AreEqual(BcfCoordinates.Internal, frame.Kind);
        }

        [TestMethod]
        public void Viewpoint_RoundTripsTheView()
        {
            var view = new CommentView { X = 3.5, Y = -2.25, Z = 1.0, Yaw = 2.0f, Pitch = -0.3f };
            BcfFrame frame = BcfFrame.Resolve(Site(), BcfCoordinates.Shared, out _);
            BcfViewpoint viewpoint = BcfMapping.ToViewpoint(view, 1.6, frame, 90);
            Assert.AreEqual(58.716, viewpoint.FieldOfView, 0.01, "90° across a 16:9 picture is ~58.7° up and down");
            Assert.AreEqual(1.0, viewpoint.Direction.Length, 1e-9);
            Assert.AreEqual(1.0, viewpoint.Up.Length, 1e-9);

            Assert.IsTrue(BcfMapping.TryToView(viewpoint, 1.6, frame, out CommentView back));
            Assert.AreEqual(view.X, back.X, 1e-3);
            Assert.AreEqual(view.Y, back.Y, 1e-3);
            Assert.AreEqual(view.Z, back.Z, 1e-3);
            Assert.AreEqual(view.Yaw, back.Yaw, 1e-4);
            Assert.AreEqual(view.Pitch, back.Pitch, 1e-4);

            Assert.AreEqual(BcfMapping.MAX_FOV, BcfMapping.ToViewpoint(view, 1.6, frame, 120).FieldOfView, "clamped to BCF 2.1's range");
        }

        #endregion

        #region Files

        private static BcfTopic SampleTopic()
        {
            var topic = BcfMapping.ToTopic(new CommentRecord
            {
                Text = "Duct clashes with beam", Author = "gavin", Priority = CommentPriority.HIGH, AssignedTo = "sam",
                Replies = new List<CommentReply> { new() { Author = "sam", Text = "Will reroute" } }
            });
            topic.Viewpoint = new BcfViewpoint
            {
                Position = new BcfVector(280012.5, 6130003.25, 53.1), Direction = new BcfVector(0, 1, 0), Up = new BcfVector(0, 0, 1),
                FieldOfView = 55, Selection = { new BcfComponent { IfcGuid = IfcGuid.Encode(Guid.NewGuid()), AuthoringToolId = "202", OriginatingSystem = "Autodesk Revit 2026" } }
            };
            topic.Snapshot = JPEG;
            topic.SnapshotExtension = ".png";
            return topic;
        }

        [TestMethod]
        public void Write_ThenRead_GivesTheTopicsBack()
        {
            using var folder = new TempFolder();
            string path = folder.File("issues" + BcfFile.EXTENSION);
            Assert.AreEqual(".bcf", BcfFile.EXTENSION);
            BcfTopic topic = SampleTopic();
            Assert.IsTrue(BcfFile.Write(path, new BcfProject { ProjectId = "p1", Name = "Test" }, new[] { topic }, out string error), error);

            List<string> names = TestData.EntryNames(path);
            string folderName = topic.Guid.ToString("D");
            CollectionAssert.Contains(names, "bcf.version");
            CollectionAssert.Contains(names, "project.bcfp");
            CollectionAssert.Contains(names, BcfFile.EXTENSIONS_FILE, "viewers expect the extension schema project.bcfp names");
            using (ZipArchive zip = ZipFile.OpenRead(path))
            {
                using var reader = new StreamReader(zip.GetEntry("project.bcfp").Open());
                StringAssert.Contains(reader.ReadToEnd(), "<ExtensionSchema>extensions.xsd</ExtensionSchema>");
                using var schema = new StreamReader(zip.GetEntry(BcfFile.EXTENSIONS_FILE).Open());
                string xsd = schema.ReadToEnd();
                StringAssert.Contains(xsd, "In Progress");
                StringAssert.Contains(xsd, "markup.xsd");
            }
            CollectionAssert.Contains(names, folderName + "/markup.bcf");
            CollectionAssert.Contains(names, folderName + "/viewpoint.bcfv");
            CollectionAssert.Contains(names, folderName + "/snapshot.png");

            BcfReadResult result = BcfFile.Read(path, out error);
            Assert.IsNotNull(result, error);
            Assert.AreEqual("2.1", result.Version);
            Assert.AreEqual("Test", result.Project.Name);
            Assert.AreEqual(1, result.Topics.Count);

            BcfTopic back = result.Topics[0];
            Assert.AreEqual(topic.Guid, back.Guid);
            Assert.AreEqual(topic.Title, back.Title);
            Assert.AreEqual(topic.Description, back.Description);
            Assert.AreEqual("Open", back.Status);
            Assert.AreEqual("High", back.Priority);
            Assert.AreEqual("sam", back.AssignedTo);
            Assert.AreEqual(1, back.Comments.Count);
            Assert.AreEqual(topic.Comments[0].Guid, back.Comments[0].Guid);
            Assert.AreEqual("Will reroute", back.Comments[0].Text);
            Assert.AreEqual(topic.CreationDate.ToUnixTimeSeconds(), back.CreationDate.ToUnixTimeSeconds());

            Assert.IsNotNull(back.Viewpoint);
            Assert.AreEqual(280012.5, back.Viewpoint.Position.X, 1e-6);
            Assert.AreEqual(6130003.25, back.Viewpoint.Position.Y, 1e-6);
            Assert.AreEqual(55, back.Viewpoint.FieldOfView, 1e-9);
            Assert.AreEqual(1, back.Viewpoint.Selection.Count);
            Assert.AreEqual(topic.Viewpoint.Selection[0].IfcGuid, back.Viewpoint.Selection[0].IfcGuid);
            Assert.AreEqual("202", back.Viewpoint.Selection[0].AuthoringToolId);
            CollectionAssert.AreEqual(JPEG, back.Snapshot);
        }

        [TestMethod]
        public void Read_Bcf30Layout_CommentsAndViewpointsUnderTheTopic()
        {
            using var folder = new TempFolder();
            string path = folder.File("v3.bcfzip");
            Guid guid = Guid.NewGuid();
            string markup = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<Markup xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">
  <Topic Guid=""{guid}"" TopicType=""Clash"" TopicStatus=""Active"">
    <Title>Beam clash</Title>
    <Priority>Critical</Priority>
    <CreationDate>2026-10-01T10:00:00Z</CreationDate>
    <CreationAuthor>bob@example.com</CreationAuthor>
    <Comments>
      <Comment Guid=""{Guid.NewGuid()}""><Date>2026-10-01T11:00:00Z</Date><Author>bob@example.com</Author><Comment>Please check</Comment></Comment>
    </Comments>
    <Viewpoints>
      <ViewPoint Guid=""{Guid.NewGuid()}""><Viewpoint>vp1.bcfv</Viewpoint><Snapshot>snap1.png</Snapshot></ViewPoint>
    </Viewpoints>
  </Topic>
</Markup>";
            string viewpoint = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<VisualizationInfo Guid=""" + Guid.NewGuid() + @""">
  <PerspectiveCamera>
    <CameraViewPoint><X>1</X><Y>2</Y><Z>3</Z></CameraViewPoint>
    <CameraDirection><X>0</X><Y>1</Y><Z>0</Z></CameraDirection>
    <CameraUpVector><X>0</X><Y>0</Y><Z>1</Z></CameraUpVector>
    <FieldOfView>50</FieldOfView>
  </PerspectiveCamera>
</VisualizationInfo>";
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(zip, "bcf.version", @"<?xml version=""1.0""?><Version VersionId=""3.0""/>");
                Add(zip, guid + "/markup.bcf", markup);
                Add(zip, guid + "/vp1.bcfv", viewpoint);
                ZipArchiveEntry image = zip.CreateEntry(guid + "/snap1.png");
                using (Stream stream = image.Open()) { stream.Write(JPEG); }
            }

            BcfReadResult result = BcfFile.Read(path, out string error);
            Assert.IsNotNull(result, error);
            Assert.AreEqual("3.0", result.Version);
            BcfTopic topic = result.Topics.Single();
            Assert.AreEqual(guid, topic.Guid);
            Assert.AreEqual("Active", topic.Status);
            Assert.AreEqual(1, topic.Comments.Count);
            Assert.AreEqual("Please check", topic.Comments[0].Text);
            Assert.IsNotNull(topic.Viewpoint);
            Assert.AreEqual(3, topic.Viewpoint.Position.Z, 1e-9);
            Assert.AreEqual(".png", topic.SnapshotExtension);

            CommentRecord record = BcfMapping.ToComment(topic);
            Assert.AreEqual(CommentStatus.IN_PROGRESS, record.Status);
            Assert.AreEqual(CommentPriority.HIGH, record.Priority);
            Assert.AreEqual("Beam clash — Please check", record.Text);
            Assert.AreEqual(0, record.ReplyCount, "the comment that became the text isn't repeated as a reply");
        }

        [TestMethod]
        public void Read_SkipsADamagedTopicAndRefusesNonZips()
        {
            using var folder = new TempFolder();
            string path = folder.File("mixed.bcfzip");
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                Add(zip, "a/markup.bcf", "<Markup><Topic Guid=\"" + Guid.NewGuid() + "\"><Title>Fine</Title></Topic></Markup>");
                Add(zip, "b/markup.bcf", "<Markup><Topic"); // broken XML
            }
            BcfReadResult result = BcfFile.Read(path, out string error);
            Assert.IsNotNull(result, error);
            Assert.AreEqual(1, result.Topics.Count);
            Assert.AreEqual(1, result.Skipped);

            string text = folder.File("not.bcfzip");
            File.WriteAllText(text, "hello");
            Assert.IsNull(BcfFile.Read(text, out error));
            Assert.IsNotNull(error);
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream stream = entry.Open();
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        #endregion

        #region Comment pictures

        [TestMethod]
        public void SnapshotNames_AreSafe()
        {
            Assert.AreEqual("comments/abc123.jpg", CommentSnapshots.NameFor("abc123"));
            Assert.AreEqual("comments/a_b.jpg", CommentSnapshots.NameFor("a/b"));
            Assert.IsTrue(CommentSnapshots.IsValidName("comments/abc.jpg"));
            Assert.IsFalse(CommentSnapshots.IsValidName("comments/../x.jpg"));
            Assert.IsFalse(CommentSnapshots.IsValidName("textures/abc.jpg"));
            Assert.IsFalse(CommentSnapshots.IsValidName(null));
        }

        [TestMethod]
        public void Bimgo_KeepsCommentPicturesAndElementIds()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument();
            CommentRecord comment = document.Comments.Comments[0];
            comment.Snapshot = CommentSnapshots.NameFor(comment.Id);
            comment.SnapshotData = JPEG;
            comment.ElementUniqueId = "door-uid";

            string path = folder.File("pictures.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            CollectionAssert.Contains(TestData.EntryNames(path), comment.Snapshot);

            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            CommentRecord back = read.Comments.Comments.Single();
            Assert.AreEqual(comment.Snapshot, back.Snapshot);
            CollectionAssert.AreEqual(JPEG, back.SnapshotData);
            Assert.AreEqual("door-uid", back.ElementUniqueId);
        }

        [TestMethod]
        public void ModelFolder_WritesPicturesBesideCommentsAndRemovesUnusedOnes()
        {
            using var folder = new TempFolder();
            string sidecar = folder.File(ModelFolders.COMMENTS_FILE);
            var keep = new CommentRecord { Text = "keep", SnapshotData = JPEG, SnapshotDirty = true };
            keep.Snapshot = CommentSnapshots.NameFor(keep.Id);
            var document = new CommentDocument { Comments = new List<CommentRecord> { keep } };

            string stale = Path.Combine(folder.Path, "comments", "old.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(stale));
            File.WriteAllBytes(stale, JPEG);

            Assert.IsTrue(CommentFiles.Write(sidecar, document, out string error), error);
            string written = Path.Combine(folder.Path, "comments", keep.Id + ".jpg");
            Assert.IsTrue(File.Exists(written));
            Assert.IsFalse(File.Exists(stale), "pictures no comment uses are removed");
            Assert.IsFalse(keep.SnapshotDirty);

            CommentDocument read = CommentFiles.Read(sidecar, out error);
            Assert.IsNotNull(read, error);
            CollectionAssert.AreEqual(JPEG, read.Comments.Single().SnapshotData);
        }

        #endregion
    }
}
