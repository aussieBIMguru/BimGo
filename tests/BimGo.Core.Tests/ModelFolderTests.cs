using System;
using System.Collections.Generic;
using System.IO;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// BimGo's per-model folders (UX round build B): naming and keys, sidecar names inside a folder, the one-time copy
    /// of older sidecars, optional sharing beside the model, and texture overrides moving in. Temp folders only.
    /// </summary>
    [TestClass]
    public sealed class ModelFolderTests
    {
        [TestMethod]
        public void Key_PathsIgnoreCaseAndSlashes_CloudKeysDont()
        {
            Assert.AreEqual(ModelFolders.HashOf(@"local:C:\Models\Tower.rvt"), ModelFolders.HashOf("local:c:/models/TOWER.rvt"));
            Assert.AreEqual(ModelFolders.HashOf(@"central:\\server\jobs\Tower.rvt"), ModelFolders.HashOf(@"central:\\SERVER\Jobs\tower.rvt "));
            Assert.AreNotEqual(ModelFolders.HashOf(@"local:C:\Models\Tower.rvt"), ModelFolders.HashOf(@"local:D:\Copy\Tower.rvt"),
                "Two local copies are two folders");
            Assert.AreEqual(8, ModelFolders.HashOf("cloud:a/b").Length);
        }

        [TestMethod]
        public void FolderName_IsSafeTitlePlusHash()
        {
            string hash = ModelFolders.HashOf("local:c:\\a.rvt");
            Assert.AreEqual("Tower_" + hash, ModelFolders.FolderNameFor("Tower.rvt", "local:c:\\a.rvt"));
            Assert.AreEqual("A_B_C_" + hash, ModelFolders.FolderNameFor("A:B/C", "local:c:\\a.rvt"));
            Assert.AreEqual("Model_" + hash, ModelFolders.FolderNameFor("  ", "local:c:\\a.rvt"));
            Assert.AreEqual(60 + 1 + 8, ModelFolders.FolderNameFor(new string('x', 200), "local:c:\\a.rvt").Length);
        }

        [TestMethod]
        public void SidecarNames_InAModelFolder_AreFixed()
        {
            string folder = Path.Combine("C:", "Data", "Models", "Tower_12345678");
            string comments = Path.Combine(folder, ModelFolders.COMMENTS_FILE);
            Assert.AreEqual(folder, ModelFolders.FolderOf(comments));
            Assert.AreEqual(Path.Combine(folder, "bookmarks.json"), BookmarkFiles.SidecarFor(comments));
            Assert.AreEqual(Path.Combine(folder, "sun.json"), SunFiles.SidecarFor(comments));
            Assert.AreEqual(Path.Combine(folder, "visibility.json"), VisibilityFiles.SidecarFor(comments));

            // Older snapshots still point beside the model
            Assert.IsNull(ModelFolders.FolderOf(Path.Combine("C:", "Models", "Tower.bimgo-comments.json")));
        }

        [TestMethod]
        public void Prepare_CopiesOldSidecarsOnce_AndLeavesTheOriginals()
        {
            using var temp = new TempFolder();
            string modelDir = Path.Combine(temp.Path, "Project");
            Directory.CreateDirectory(modelDir);
            string beside = Path.Combine(modelDir, "Tower" + BimGoFormat.SIDECAR_SUFFIX);
            File.WriteAllText(beside, "{\"comments\":[{\"id\":\"c1\",\"text\":\"Beside\"}]}");
            File.WriteAllText(Path.Combine(modelDir, "Tower" + BimGoFormat.SUN_SIDECAR_SUFFIX), "{\"enabled\":true}");

            // The old Comments folder has bookmarks the model folder doesn't
            string oldDir = Path.Combine(temp.Path, "Comments");
            Directory.CreateDirectory(oldDir);
            string old = Path.Combine(oldDir, "Tower" + BimGoFormat.SIDECAR_SUFFIX);
            File.WriteAllText(old, "{\"comments\":[{\"id\":\"c2\",\"text\":\"Old\"}]}");
            File.WriteAllText(Path.Combine(oldDir, "Tower" + BimGoFormat.BOOKMARK_SIDECAR_SUFFIX), "{\"bookmarks\":[]}");

            string folder = Path.Combine(temp.Path, "Models", "Tower_abc");
            var info = new ModelFolderInfo { Title = "Tower", Key = "local:x" };
            string comments = ModelFolders.Prepare(folder, info, new[] { beside, old, null });

            Assert.AreEqual(Path.Combine(folder, ModelFolders.COMMENTS_FILE), comments);
            Assert.AreEqual("Beside", CommentFiles.Read(comments, out _).Comments[0].Text, "The beside-model copy wins");
            Assert.IsTrue(File.Exists(Path.Combine(folder, "sun.json")));
            Assert.IsTrue(File.Exists(Path.Combine(folder, "bookmarks.json")), "Taken from the next candidate");
            Assert.IsFalse(File.Exists(Path.Combine(folder, "visibility.json")));
            Assert.IsTrue(File.Exists(beside), "Originals stay as a backup");
            Assert.AreEqual("Tower", ModelFolders.ReadInfo(folder).Title);

            // A second Go never overwrites the folder's own files
            File.WriteAllText(beside, "{\"comments\":[{\"id\":\"c3\",\"text\":\"Changed beside\"}]}");
            ModelFolders.Prepare(folder, info, new[] { beside });
            Assert.AreEqual("Beside", CommentFiles.Read(comments, out _).Comments[0].Text);
        }

        [TestMethod]
        public void Prepare_MigratesAnRvtGoCommentsFile()
        {
            using var temp = new TempFolder();
            string beside = temp.File("Tower" + BimGoFormat.SIDECAR_SUFFIX);
            File.WriteAllText(temp.File("Tower" + BimGoFormat.LEGACY_SIDECAR_SUFFIX), "{\"Version\":1,\"Comments\":[{\"Id\":\"old\",\"Text\":\"From RvtGo\"}]}");

            string comments = ModelFolders.Prepare(Path.Combine(temp.Path, "Models", "T_1"), new ModelFolderInfo(), new[] { beside });
            Assert.AreEqual("From RvtGo", CommentFiles.Read(comments, out _).Comments[0].Text);
        }

        [TestMethod]
        public void Sharing_MirrorsWrites_AndTakesNewerCopies()
        {
            using var temp = new TempFolder();
            string modelDir = Path.Combine(temp.Path, "Shared");
            Directory.CreateDirectory(modelDir);
            string beside = Path.Combine(modelDir, "Tower" + BimGoFormat.SIDECAR_SUFFIX);
            string folder = Path.Combine(temp.Path, "Models", "Tower_abc");
            var info = new ModelFolderInfo { Title = "Tower", ShareBesideModel = true, BesideCommentsPath = beside };
            string comments = ModelFolders.Prepare(folder, info, new[] { beside });

            // Writes go beside the model too
            var document = new CommentDocument { Comments = new List<CommentRecord> { new() { Id = "a", Text = "Mine" } } };
            Assert.IsTrue(CommentFiles.Write(comments, document, out string error), error);
            Assert.AreEqual("Mine", CommentFiles.Read(beside, out _).Comments[0].Text);
            Assert.IsTrue(SunFiles.Write(SunFiles.SidecarFor(comments), new SunSettings(), out error), error);
            Assert.IsTrue(File.Exists(Path.Combine(modelDir, "Tower" + BimGoFormat.SUN_SIDECAR_SUFFIX)));

            // A colleague's newer file is taken at the next Go
            File.WriteAllText(beside, "{\"comments\":[{\"id\":\"b\",\"text\":\"Theirs\"}]}");
            File.SetLastWriteTimeUtc(comments, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(beside, DateTime.UtcNow);
            ModelFolders.Prepare(folder, info, new[] { beside });
            Assert.AreEqual("Theirs", CommentFiles.Read(comments, out _).Comments[0].Text);
        }

        [TestMethod]
        public void NoSharing_WritesStayInTheFolder()
        {
            using var temp = new TempFolder();
            string beside = temp.File("Tower" + BimGoFormat.SIDECAR_SUFFIX);
            string folder = Path.Combine(temp.Path, "Models", "Tower_abc");
            var info = new ModelFolderInfo { Title = "Tower", ShareBesideModel = false, BesideCommentsPath = beside };
            string comments = ModelFolders.Prepare(folder, info, new[] { beside });

            var document = new CommentDocument { Comments = new List<CommentRecord> { new() { Id = "a", Text = "Private" } } };
            Assert.IsTrue(CommentFiles.Write(comments, document, out string error), error);
            Assert.IsFalse(File.Exists(beside));
        }

        [TestMethod]
        public void TextureOverrides_MoveIntoTheModelFolder()
        {
            using var temp = new TempFolder();
            string legacyDir = Path.Combine(temp.Path, "texture-overrides");
            TextureOverrideSet legacy = TextureOverrideSet.Load("uid-123", legacyDir);
            legacy.Set(TextureOverrideSet.HOST, "uid-carpet", "Carpet", TextureOverride.ForProxy("carpet"));
            Assert.IsTrue(legacy.Save());

            string folder = Path.Combine(temp.Path, "Models", "Tower_abc");
            TextureOverrideSet moved = TextureOverrideSet.LoadFromModelFolder(folder, "uid-123", legacyDir);
            Assert.AreEqual("carpet", moved.Find(TextureOverrideSet.HOST, "uid-carpet", "Carpet")?.Proxy);
            Assert.IsTrue(File.Exists(Path.Combine(folder, ModelFolders.TEXTURE_OVERRIDES_FILE)));
            Assert.IsTrue(File.Exists(TextureOverrideSet.PathFor("uid-123", legacyDir)), "The old file stays as a backup");

            // Saves go to the folder
            moved.Set(TextureOverrideSet.HOST, "uid-brick", "Brick", TextureOverride.ForProxy("brick"));
            Assert.IsTrue(moved.Save());
            TextureOverrideSet again = TextureOverrideSet.LoadFromModelFolder(folder, "uid-123", legacyDir);
            Assert.AreEqual("brick", again.Find(TextureOverrideSet.HOST, "uid-brick", "Brick")?.Proxy);
            Assert.IsNull(TextureOverrideSet.Load("uid-123", legacyDir).Find(TextureOverrideSet.HOST, "uid-brick", "Brick"));
        }

        [TestMethod]
        public void TextureOverrides_WithoutAFolder_UseTheOlderFile()
        {
            using var temp = new TempFolder();
            TextureOverrideSet set = TextureOverrideSet.LoadFromModelFolder(null, "uid-9", temp.Path);
            set.Set(TextureOverrideSet.HOST, "m", "M", TextureOverride.ForColourOnly());
            Assert.IsTrue(set.Save());
            Assert.IsTrue(File.Exists(TextureOverrideSet.PathFor("uid-9", temp.Path)));
        }
    }
}
