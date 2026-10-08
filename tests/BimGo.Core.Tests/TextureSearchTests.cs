using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BimGo.Scene;
using BimGo.Utilities;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// The deep scan's file-name stages (exact → extension → loose), ambiguity, the bump / cutout filter, the depth and
    /// file caps, and cancellation.
    /// </summary>
    [TestClass]
    public sealed class TextureSearchTests
    {
        private static string Touch(TempFolder folder, string relative)
        {
            string path = Path.Combine(folder.Path, relative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, new byte[] { 1 });
            return path;
        }

        [TestMethod]
        public void FileNames_SplitAlternativesAndBothSlashes()
        {
            CollectionAssert.AreEqual(new[] { "x.jpg" }, TextureSearch.FileNamesOf("1/Mats/x.jpg|2/Mats/x.jpg|3\\mats\\X.JPG").ToList());
            CollectionAssert.AreEqual(new[] { "BG_Carpet_Plain1.jpg" }, TextureSearch.FileNamesOf("BG_Carpet_Plain1.jpg").ToList());
            CollectionAssert.AreEqual(new[] { "a.png" }, TextureSearch.FileNamesOf(@"""C:\Maps\a.png""").ToList());
            Assert.AreEqual(0, TextureSearch.FileNamesOf("").Count);
            Assert.AreEqual(0, TextureSearch.FileNamesOf(null).Count);
        }

        [TestMethod]
        public void LooseStem_IgnoresCaseSeparatorsAndColourSuffix()
        {
            Assert.AreEqual("bgcarpetplain1", TextureSearch.LooseStem("BG_Carpet_Plain1.jpg"));
            Assert.AreEqual("bgcarpetplain1", TextureSearch.LooseStem("bg-carpet-plain-1.JPEG"));
            Assert.AreEqual("bgcarpetplain1", TextureSearch.LooseStem("BG Carpet Plain1_Color.png"));
            Assert.AreEqual("oak", TextureSearch.LooseStem("Oak_diffuse.tif"));
            Assert.AreEqual("oak", TextureSearch.LooseStem("oak-albedo.jpg"));
        }

        [TestMethod]
        public void AuxiliaryMaps_AreRecognised()
        {
            Assert.IsTrue(TextureSearch.IsAuxiliaryMap("BG_Carpet_Plain1_Bump.jpg"));
            Assert.IsTrue(TextureSearch.IsAuxiliaryMap("leaf_cutout.png"));
            Assert.IsTrue(TextureSearch.IsAuxiliaryMap("Brick_refl.jpg"));
            Assert.IsTrue(TextureSearch.IsAuxiliaryMap("Wood_NormalGL.jpg"));
            Assert.IsFalse(TextureSearch.IsAuxiliaryMap("BG_Carpet_Plain1.jpg"));
            Assert.IsFalse(TextureSearch.IsAuxiliaryMap("Brushed_Metal.jpg"));
        }

        [TestMethod]
        public void Index_OnlyImagesAreIndexed()
        {
            using var folder = new TempFolder();
            Touch(folder, "a.jpg");
            Touch(folder, "b.PNG");
            Touch(folder, "notes.txt");
            Touch(folder, "model.rvt");
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);
            Assert.AreEqual(2, index.FileCount);
            Assert.IsFalse(index.Truncated);
        }

        [TestMethod]
        public void Exact_IsCaseInsensitiveAndRecursive()
        {
            using var folder = new TempFolder();
            string hit = Touch(folder, @"Maps\Floors\bg_carpet_plain1.JPG");
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);

            TextureSearchResult result = TextureSearch.RunStage(index, new[] { "BG_Carpet_Plain1.jpg" }, TextureMatchStage.Exact).Single();
            Assert.AreEqual(TextureMatchStage.Exact, result.Stage);
            Assert.AreEqual(hit, result.Chosen);
            Assert.IsTrue(result.PreTicked);
        }

        [TestMethod]
        public void Exact_UsesEveryAlternative()
        {
            using var folder = new TempFolder();
            string hit = Touch(folder, "Brick.jpg");
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);
            TextureSearchResult result = TextureSearch.RunStage(index, new[] { "1/Mats/Brick.jpg|2/Mats/Brick.jpg" }, TextureMatchStage.Exact).Single();
            Assert.AreEqual(hit, result.Chosen);
        }

        [TestMethod]
        public void Extension_SwapsTheExtensionButNeverOffersBumpMaps()
        {
            using var folder = new TempFolder();
            string hit = Touch(folder, "brick.png");
            Touch(folder, "carpet_bump.png");
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);

            Assert.AreEqual(TextureMatchStage.None, TextureSearch.RunStage(index, new[] { "brick.jpg" }, TextureMatchStage.Exact).Single().Stage);
            TextureSearchResult result = TextureSearch.RunStage(index, new[] { "brick.jpg" }, TextureMatchStage.Extension).Single();
            Assert.AreEqual(TextureMatchStage.Extension, result.Stage);
            Assert.AreEqual(hit, result.Chosen);
            Assert.IsTrue(result.PreTicked);

            Assert.AreEqual(TextureMatchStage.None, TextureSearch.RunStage(index, new[] { "carpet_bump.jpg" }, TextureMatchStage.Extension).Single().Stage);
        }

        [TestMethod]
        public void Loose_ProposedOnlyWhenUniqueAndNeverPreTicked()
        {
            using var folder = new TempFolder();
            string hit = Touch(folder, "bg-carpet-plain-1.JPEG");
            Touch(folder, "bg_carpet_plain1_bump.jpg");
            Touch(folder, "tile-a.jpg");
            Touch(folder, @"other\TILE_A.png");
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);

            TextureSearchResult carpet = TextureSearch.RunStage(index, new[] { "BG_Carpet_Plain1.jpg" }, TextureMatchStage.Loose).Single();
            Assert.AreEqual(TextureMatchStage.Loose, carpet.Stage);
            Assert.AreEqual(hit, carpet.Chosen, "the bump map is not a loose candidate");
            Assert.IsFalse(carpet.PreTicked, "loose hits are proposals");

            TextureSearchResult tile = TextureSearch.RunStage(index, new[] { "Tile A.tif" }, TextureMatchStage.Loose).Single();
            Assert.AreEqual(TextureMatchStage.None, tile.Stage, "two loose candidates: no proposal");
        }

        [TestMethod]
        public void Ambiguity_ReportsAllCandidatesAndPicksNone()
        {
            using var folder = new TempFolder();
            string a = Touch(folder, @"A\wood.jpg");
            string b = Touch(folder, @"B\wood.jpg");
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);

            TextureSearchResult result = TextureSearch.RunStage(index, new[] { "wood.jpg" }, TextureMatchStage.Exact).Single();
            Assert.AreEqual(TextureMatchStage.Exact, result.Stage);
            Assert.IsTrue(result.IsAmbiguous);
            Assert.IsNull(result.Chosen);
            Assert.IsFalse(result.PreTicked);
            CollectionAssert.AreEquivalent(new[] { a, b }, result.Candidates.ToList());
        }

        [TestMethod]
        public void RunAll_EachStageSeesOnlyWhatIsLeft()
        {
            using var folder = new TempFolder();
            string exact = Touch(folder, "one.jpg");
            string ext = Touch(folder, "two.png");
            string loose = Touch(folder, "three-a.jpg");
            Touch(folder, "one.png"); // would also match "one" by extension, but exact already found it
            TextureFolderIndex index = TextureFolderIndex.Build(folder.Path);

            IReadOnlyList<TextureSearchResult> results = TextureSearch.RunAll(index, new[] { "one.jpg", "two.jpg", "Three_A.jpg", "four.jpg", "", "one.jpg" });
            Assert.AreEqual(4, results.Count, "blank and duplicate paths are skipped");
            Assert.AreEqual(exact, results[0].Chosen);
            Assert.AreEqual(TextureMatchStage.Exact, results[0].Stage);
            Assert.AreEqual(ext, results[1].Chosen);
            Assert.AreEqual(TextureMatchStage.Extension, results[1].Stage);
            Assert.AreEqual(loose, results[2].Chosen);
            Assert.AreEqual(TextureMatchStage.Loose, results[2].Stage);
            Assert.AreEqual(TextureMatchStage.None, results[3].Stage);

            IReadOnlyList<TextureSearchResult> exactOnly = TextureSearch.RunAll(index, new[] { "two.jpg" }, TextureMatchStage.Exact);
            Assert.AreEqual(TextureMatchStage.None, exactOnly[0].Stage, "search folders use exact names only");
        }

        [TestMethod]
        public void Caps_DepthAndFileCountTruncate()
        {
            using var folder = new TempFolder();
            Touch(folder, "a.jpg");
            Touch(folder, @"1\2\3\deep.jpg");
            TextureFolderIndex shallow = TextureFolderIndex.Build(folder.Path, maxDepth: 1);
            Assert.AreEqual(1, shallow.FileCount);
            Assert.IsTrue(shallow.Truncated);

            for (int i = 0; i < 5; i++) { Touch(folder, $"f{i}.jpg"); }
            TextureFolderIndex capped = TextureFolderIndex.Build(folder.Path, maxFiles: 3);
            Assert.AreEqual(3, capped.FileCount);
            Assert.IsTrue(capped.Truncated);
        }

        [TestMethod]
        public void Cancellation_StopsTheWalk()
        {
            using var folder = new TempFolder();
            Touch(folder, "a.jpg");
            var progress = new OperationProgress();
            progress.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => TextureFolderIndex.Build(folder.Path, progress));
        }

        [TestMethod]
        public void MissingFolder_IsAnEmptyIndex()
        {
            TextureFolderIndex index = TextureFolderIndex.Build(Path.Combine(Path.GetTempPath(), "BimGo.Tests", "does-not-exist-" + Guid.NewGuid().ToString("N")));
            Assert.AreEqual(0, index.FileCount);
            Assert.IsNull(TextureFolderIndex.GetCached(null));
        }
    }

    /// <summary>
    /// Proxy keyword suggestions by material name and schema.
    /// </summary>
    [TestClass]
    public sealed class ProxyCatalogTests
    {
        [DataTestMethod]
        [DataRow("BG_Carpet_Plain1", null, "carpet")]
        [DataRow("Concrete Block 200", null, "blockwork")]
        [DataRow("Masonry - Brick", null, "brick")]
        [DataRow("Concrete, Board Formed", null, "concrete-board")]
        [DataRow("Concrete - Cast-in-Place", null, "concrete")]
        [DataRow("Timber Floor - Spotted Gum", null, "timber-floor")]
        [DataRow("Oak veneer", null, "timber-panel")]
        [DataRow("Plywood", null, "plywood")]
        [DataRow("Tile 600 x 600 Porcelain", null, "tile-600")]
        [DataRow("Ceramic Tile White", null, "tile-300")]
        [DataRow("Colorbond Roof Sheet", null, "metal-galvanised")]
        [DataRow("Stainless Steel", null, "metal-brushed")]
        [DataRow("Sandstone", null, "stone")]
        [DataRow("Glass - Clear", null, null)]
        [DataRow("Acoustic Ceiling Tile 600x600", null, null)]
        [DataRow("Cloak Room Paint", null, null)]
        [DataRow("Default Wall", "MasonryCMUSchema", "blockwork")]
        [DataRow("Default Wall", "MasonrySchema", "brick")]
        [DataRow("Some floor", "HardwoodSchema", "timber-floor")]
        [DataRow("Paint - White", "WallPaintSchema", null)]
        [DataRow("Thing", "GenericSchema", null)]
        public void Suggest_ByNameThenSchema(string name, string schema, string expected)
        {
            Assert.AreEqual(expected, ProxyCatalog.Suggest(name, schema));
        }

        [TestMethod]
        public void Keywords_AreUniqueLowerCaseAndSized()
        {
            Assert.AreEqual(ProxyCatalog.ALL.Count, ProxyCatalog.ALL.Select(k => k.Keyword).Distinct().Count());
            foreach (ProxyKeyword keyword in ProxyCatalog.ALL)
            {
                Assert.AreEqual(keyword.Keyword.ToLowerInvariant(), keyword.Keyword);
                Assert.IsTrue(keyword.SizeU > 0 && keyword.SizeV > 0);
                Assert.AreSame(keyword, ProxyCatalog.Find(keyword.Keyword.ToUpperInvariant()));
            }
            Assert.IsNull(ProxyCatalog.Find("nope"));
            Assert.AreEqual("brick", ProxyCatalog.Normalise("  Brick "));
            Assert.IsNull(ProxyCatalog.Normalise(" "));
        }
    }

    /// <summary>
    /// The per-model texture override file.
    /// </summary>
    [TestClass]
    public sealed class TextureOverrideTests
    {
        [TestMethod]
        public void Overrides_RoundTripByUniqueIdThenName()
        {
            using var folder = new TempFolder();
            TextureOverrideSet set = TextureOverrideSet.Load("model-key-1", folder.Path);
            Assert.IsTrue(set.IsEmpty);

            set.Set(TextureOverrideSet.HOST, "uid-carpet", "BG Carpet", TextureOverride.ForImage(@"D:\Maps\BG_Carpet_Plain1.jpg"));
            set.Set(TextureOverrideSet.HOST, null, "Old Brick", TextureOverride.ForProxy("Brick"));
            set.Set("link-model-key", "uid-paint", "Paint", TextureOverride.ForColourOnly());
            Assert.IsTrue(set.Save());

            TextureOverrideSet read = TextureOverrideSet.Load("model-key-1", folder.Path);
            Assert.AreEqual(@"D:\Maps\BG_Carpet_Plain1.jpg", read.Find(TextureOverrideSet.HOST, "uid-carpet", "renamed")?.Image);
            Assert.AreEqual("brick", read.Find(TextureOverrideSet.HOST, "uid-unknown", "Old Brick")?.Proxy, "name fallback");
            Assert.IsTrue(read.Find("link-model-key", "uid-paint", "Paint").ColourOnly);
            Assert.IsNull(read.Find("link-model-key", "uid-carpet", "BG Carpet"), "documents are separate");
            Assert.IsNull(read.Find("nope", null, "x"));
        }

        [TestMethod]
        public void Overrides_ClearRemovesAndEmptyFileIsDeleted()
        {
            using var folder = new TempFolder();
            TextureOverrideSet set = TextureOverrideSet.Load("title:My Model", folder.Path);
            set.Set(TextureOverrideSet.HOST, "uid", "Mat", TextureOverride.ForProxy("carpet"));
            Assert.IsTrue(set.Save());
            string path = TextureOverrideSet.PathFor("title:My Model", folder.Path);
            Assert.IsTrue(File.Exists(path));
            Assert.IsFalse(Path.GetFileName(path).Contains(':'), "unsafe characters replaced");

            set.Set(TextureOverrideSet.HOST, "uid", "Mat", null);
            Assert.IsTrue(set.IsEmpty);
            Assert.IsTrue(set.Save());
            Assert.IsFalse(File.Exists(path));
        }

        [TestMethod]
        public void Overrides_DamagedFileIsIgnored()
        {
            using var folder = new TempFolder();
            File.WriteAllText(TextureOverrideSet.PathFor("k", folder.Path), "{ not json");
            Assert.IsTrue(TextureOverrideSet.Load("k", folder.Path).IsEmpty);

            File.WriteAllText(TextureOverrideSet.PathFor("k2", folder.Path),
                @"{ ""documents"": { ""host"": { ""a"": { ""image"": ""x.jpg"", ""proxy"": ""brick"" }, ""b"": { } } } }");
            TextureOverrideSet read = TextureOverrideSet.Load("k2", folder.Path);
            TextureOverride a = read.Find("host", "a", null);
            Assert.AreEqual("x.jpg", a.Image);
            Assert.IsNull(a.Proxy, "one meaning only: image wins");
            Assert.IsNull(read.Find("host", "b", null), "an empty override is dropped");
        }

        [TestMethod]
        public void DocumentKey_HostOrLinkModelKey()
        {
            var links = new[] { new LinkInfo { ModelKey = "link-key" }, new LinkInfo() };
            Assert.AreEqual("host", TextureOverrideSet.DocumentKeyOf(new SceneMaterial(), links));
            Assert.AreEqual("link-key", TextureOverrideSet.DocumentKeyOf(new SceneMaterial { Link = 1 }, links));
            Assert.AreEqual("link:2", TextureOverrideSet.DocumentKeyOf(new SceneMaterial { Link = 2 }, links));
        }
    }
}
