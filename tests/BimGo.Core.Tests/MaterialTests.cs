using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// materials.json, material.bin and textures/ (Realistic mode): round-trip, absent, damaged, deduplicated images,
    /// and the texture settings.
    /// </summary>
    [TestClass]
    public sealed class MaterialTests
    {
        private static readonly byte[] JPEG_A = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
        private static readonly byte[] JPEG_B = { 0xFF, 0xD8, 0xFF, 0xE0, 4, 5, 6, 0xFF, 0xD9 };

        /// <summary>Three materials over the nine test vertices; two share one image.</summary>
        private static MaterialData Sample() => new()
        {
            TextureMaxSize = 1024,
            Materials = new[]
            {
                new SceneMaterial
                {
                    Name = "Brick", Schema = "GenericSchema", Colour = new Vector3(0.6f, 0.3f, 0.2f), Texture = "textures/a.jpg",
                    TextureState = TextureState.Embedded, TextureSource = "1/Mats/brick.jpg", Autodesk = true,
                    ScaleU = 0.6f, ScaleV = 0.4f, OffsetU = 0.1f, Angle = 90f, Fade = 0.75f, Tint = new Vector3(1f, 0.9f, 0.8f)
                },
                new SceneMaterial { Name = "Brick 2", Texture = "textures/a.jpg", TextureState = TextureState.Embedded, Link = 1 },
                new SceneMaterial { Name = "Glass", Colour = new Vector3(0.7f, 0.8f, 0.8f), Reflectivity = 0.08f },
                new SceneMaterial { Name = "Carpet", Texture = "textures/b.jpg", TextureState = TextureState.Embedded }
            },
            VertexMaterial = new ushort[] { 0, 0, 0, 1, 1, 1, 2, 2, MaterialData.NONE },
            VertexUv = Enumerable.Range(0, 9).Select(i => new Vector2(i * 0.5f, -i)).ToArray(),
            Textures = new Dictionary<string, byte[]> { ["textures/a.jpg"] = JPEG_A, ["textures/b.jpg"] = JPEG_B }
        };

        private static BimGoDocument WriteAndRead(BimGoDocument document, TempFolder folder, string name = "mat.bimgo")
        {
            string path = folder.File(name);
            Assert.IsTrue(BimGoWriter.Write(path, document, TestData.Writer, FileKinds.SAVE, out string error), error);
            BimGoDocument read = BimGoReader.Read(path, new LaunchSettings(), out error);
            Assert.IsNotNull(read, error);
            return read;
        }

        [TestMethod]
        public void Materials_RoundTrip()
        {
            using var folder = new TempFolder();
            MaterialData sample = Sample();
            MaterialData got = WriteAndRead(TestData.BuildDocument(materials: sample), folder).Scene.Materials;

            Assert.IsFalse(got.IsEmpty);
            Assert.AreEqual(1024, got.TextureMaxSize);
            Assert.AreEqual(sample.Materials.Length, got.Materials.Length);
            SceneMaterial brick = got.Materials[0];
            Assert.AreEqual("Brick", brick.Name);
            Assert.AreEqual(new Vector3(0.6f, 0.3f, 0.2f), brick.Colour);
            Assert.AreEqual("textures/a.jpg", brick.Texture);
            Assert.AreEqual(TextureState.Embedded, brick.TextureState);
            Assert.AreEqual("1/Mats/brick.jpg", brick.TextureSource);
            Assert.IsTrue(brick.Autodesk);
            Assert.AreEqual(0.6f, brick.ScaleU);
            Assert.AreEqual(0.4f, brick.ScaleV);
            Assert.AreEqual(0.1f, brick.OffsetU);
            Assert.AreEqual(90f, brick.Angle);
            Assert.AreEqual(0.75f, brick.Fade);
            Assert.AreEqual(new Vector3(1f, 0.9f, 0.8f), brick.Tint);
            Assert.AreEqual(1, got.Materials[1].Link);
            Assert.AreEqual(0.08f, got.Materials[2].Reflectivity);
            CollectionAssert.AreEqual(sample.VertexMaterial, got.VertexMaterial);
            CollectionAssert.AreEqual(sample.VertexUv, got.VertexUv);
            CollectionAssert.AreEqual(JPEG_A, got.Textures["textures/a.jpg"]);
            CollectionAssert.AreEqual(JPEG_B, got.Textures["textures/b.jpg"]);
        }

        [TestMethod]
        public void Materials_SharedImageIsStoredOnce()
        {
            using var folder = new TempFolder();
            WriteAndRead(TestData.BuildDocument(materials: Sample()), folder);
            using ZipArchive zip = ZipFile.OpenRead(folder.File("mat.bimgo"));
            Assert.AreEqual(2, zip.Entries.Count(e => e.FullName.StartsWith(BimGoFormat.TEXTURE_FOLDER)));
        }

        [TestMethod]
        public void Materials_AbsentIsEmptyAndNotWritten()
        {
            using var folder = new TempFolder();
            BimGoDocument read = WriteAndRead(TestData.BuildDocument(), folder);
            Assert.IsTrue(read.Scene.Materials.IsEmpty);

            using ZipArchive zip = ZipFile.OpenRead(folder.File("mat.bimgo"));
            Assert.IsNull(zip.GetEntry("materials.json"));
            Assert.IsNull(zip.GetEntry("material.bin"));
            Assert.IsFalse(zip.Entries.Any(e => e.FullName.StartsWith(BimGoFormat.TEXTURE_FOLDER)));
        }

        [TestMethod]
        public void Materials_WrongVertexCountIsNotWritten()
        {
            using var folder = new TempFolder();
            MaterialData sample = Sample();
            var shortStreams = new MaterialData
            {
                Materials = sample.Materials,
                VertexMaterial = new ushort[] { 0, 0, 0 },
                Textures = sample.Textures
            };
            Assert.IsTrue(WriteAndRead(TestData.BuildDocument(materials: shortStreams), folder).Scene.Materials.IsEmpty);
        }

        [TestMethod]
        public void Materials_DamagedPartsAreRepairedOrDropped()
        {
            using var folder = new TempFolder();
            string path = folder.File("damaged.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(materials: Sample()), TestData.Writer, FileKinds.SAVE, out string error), error);

            // Remove one image and replace material.bin with an index past the table and a NaN coordinate
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("textures/b.jpg").Delete();
                zip.GetEntry("material.bin").Delete();
                using var writer = new BinaryWriter(zip.CreateEntry("material.bin").Open());
                writer.Write(0x54414D42u);
                writer.Write(1);
                writer.Write(9);
                writer.Write(1);
                for (int i = 0; i < 9; i++) { writer.Write((ushort)(i == 4 ? 77 : 0)); }
                for (int i = 0; i < 9; i++) { writer.Write(i == 2 ? float.NaN : 1f); writer.Write(2f); }
            }

            MaterialData got = BimGoReader.Read(path, null, out error)?.Scene.Materials;
            Assert.IsNotNull(got, error);
            Assert.AreEqual(MaterialData.NONE, got.VertexMaterial[4], "index past the table reads as no material");
            Assert.AreEqual(Vector2.Zero, got.VertexUv[2], "non-finite coordinate is zeroed");
            Assert.IsNull(got.Materials[3].Texture, "missing image entry clears the reference");
            Assert.AreEqual(TextureState.Missing, got.Materials[3].TextureState);
            Assert.IsNotNull(got.Materials[0].Texture);
        }

        [TestMethod]
        public void Materials_MismatchedStreamsAreIgnoredButTheFileLoads()
        {
            using var folder = new TempFolder();
            string path = folder.File("mismatch.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(materials: Sample()), TestData.Writer, FileKinds.SAVE, out string error), error);
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("material.bin").Delete();
                using var writer = new BinaryWriter(zip.CreateEntry("material.bin").Open());
                writer.Write(0x54414D42u);
                writer.Write(1);
                writer.Write(5); // the geometry has 9 vertices
                writer.Write(0);
                for (int i = 0; i < 5; i++) { writer.Write((ushort)0); }
            }

            BimGoDocument read = BimGoReader.Read(path, null, out error);
            Assert.IsNotNull(read, error);
            Assert.IsTrue(read.Scene.Materials.IsEmpty);
            Assert.AreEqual(9, read.Scene.Vertices.Length);
        }

        [TestMethod]
        public void SceneMaterial_CleanClampsValues()
        {
            SceneMaterial clean = new SceneMaterial
            {
                Name = null, ScaleU = 0f, ScaleV = float.NaN, Fade = 3f, Reflectivity = -1f,
                Colour = new Vector3(2f, -1f, 0.5f), Tint = new Vector3(float.NaN), TextureState = (TextureState)42, Angle = 450f
            }.Clean();

            Assert.AreEqual(string.Empty, clean.Name);
            Assert.AreEqual(1f, clean.ScaleU);
            Assert.AreEqual(1f, clean.ScaleV);
            Assert.AreEqual(1f, clean.Fade);
            Assert.AreEqual(0f, clean.Reflectivity);
            Assert.AreEqual(new Vector3(1f, 0f, 0.5f), clean.Colour);
            Assert.AreEqual(Vector3.One, clean.Tint);
            Assert.AreEqual(TextureState.None, clean.TextureState);
            Assert.AreEqual(90f, clean.Angle);
        }

        [TestMethod]
        public void Settings_TextureDefaultsAndSanitise()
        {
            var settings = new LaunchSettings();
            Assert.IsFalse(settings.ExtractTextures, "textures are opt-in");
            Assert.AreEqual(512, settings.TextureMaxSize);
            Assert.IsTrue(settings.Reflections);

            settings.TextureMaxSize = 900;
            settings.Colour = (ColourMode)9;
            settings.Sanitise();
            Assert.AreEqual(1024, settings.TextureMaxSize);
            Assert.AreEqual(ColourMode.Material, settings.Colour);
        }

        [TestMethod]
        public void Materials_BuildBFieldsRoundTrip()
        {
            using var folder = new TempFolder();
            MaterialData sample = Sample();
            sample.Materials[0].UniqueId = "uid-brick";
            sample.Materials[0].Invert = true;
            sample.Materials[0].AssetTint = new Vector3(0.5f, 0.6f, 0.7f);
            sample.Materials[0].TextureOrigin = TextureOrigins.OVERRIDE;
            sample.Materials[2].RenderColour = new Vector3(1f, 1f, 1f);
            sample.Materials[2].Proxy = "Carpet";
            sample.Materials[2].TextureOrigin = "PROXY";

            MaterialData got = WriteAndRead(TestData.BuildDocument(materials: sample), folder).Scene.Materials;
            Assert.AreEqual("uid-brick", got.Materials[0].UniqueId);
            Assert.IsTrue(got.Materials[0].Invert);
            Assert.AreEqual(new Vector3(0.5f, 0.6f, 0.7f), got.Materials[0].AssetTint);
            Assert.AreEqual(TextureOrigins.OVERRIDE, got.Materials[0].TextureOrigin);
            Assert.AreEqual(new Vector3(1f, 1f, 1f), got.Materials[2].RenderColour);
            Assert.AreEqual("carpet", got.Materials[2].Proxy, "keywords are stored lower case");
            Assert.AreEqual(TextureOrigins.PROXY, got.Materials[2].TextureOrigin);

            // Unset optional fields stay unset (and are not written)
            Assert.IsNull(got.Materials[1].RenderColour);
            Assert.IsNull(got.Materials[1].AssetTint);
            Assert.IsNull(got.Materials[1].Proxy);
            Assert.IsFalse(got.Materials[1].Invert);
            using ZipArchive zip = ZipFile.OpenRead(folder.File("mat.bimgo"));
            using var reader = new StreamReader(zip.GetEntry("materials.json").Open());
            string json = reader.ReadToEnd();
            Assert.AreEqual(1, CountOf(json, "\"invert\""), "invert is only written when true");
            Assert.AreEqual(1, CountOf(json, "\"renderColour\""));

            static int CountOf(string text, string part) => (text.Length - text.Replace(part, string.Empty).Length) / part.Length;
        }

        [TestMethod]
        public void Materials_ReflectionFieldsRoundTrip()
        {
            using var folder = new TempFolder();
            MaterialData sample = Sample();
            sample.Materials[0].Shine = 0.85f;
            sample.Materials[0].Roughness = 0.05f;
            sample.Materials[0].Metallic = true;
            sample.Materials[0].ReflectSource = "metal_finish=polished";
            sample.Materials[3].Water = true;
            sample.Materials[3].WaterBump = 0.1f;
            sample.Materials[3].Shine = 0.6f;

            MaterialData got = WriteAndRead(TestData.BuildDocument(materials: sample), folder).Scene.Materials;
            Assert.AreEqual(0.85f, got.Materials[0].Shine);
            Assert.AreEqual(0.05f, got.Materials[0].Roughness);
            Assert.IsTrue(got.Materials[0].Metallic);
            Assert.AreEqual("metal_finish=polished", got.Materials[0].ReflectSource);
            Assert.IsTrue(got.Materials[3].Water);
            Assert.AreEqual(0.1f, got.Materials[3].WaterBump);

            // Unset: nothing written, defaults read back (older files behave the same)
            Assert.AreEqual(0f, got.Materials[1].Shine);
            Assert.IsNull(got.Materials[1].Roughness);
            Assert.IsFalse(got.Materials[1].Metallic);
            Assert.IsFalse(got.Materials[1].Water);
            using ZipArchive zip = ZipFile.OpenRead(folder.File("mat.bimgo"));
            using var reader = new StreamReader(zip.GetEntry("materials.json").Open());
            string json = reader.ReadToEnd();
            Assert.AreEqual(2, CountOf(json, "\"shine\""), "shine is only written when set");
            Assert.AreEqual(1, CountOf(json, "\"metallic\""));
            Assert.AreEqual(1, CountOf(json, "\"water\""));

            static int CountOf(string text, string part) => (text.Length - text.Replace(part, string.Empty).Length) / part.Length;
        }

        [TestMethod]
        public void SceneMaterial_CleanClampsReflectionFields()
        {
            SceneMaterial clean = new SceneMaterial { Shine = 3f, Roughness = float.NaN, WaterBump = -1f, ReflectSource = "  " }.Clean();
            Assert.AreEqual(1f, clean.Shine);
            Assert.IsNull(clean.Roughness);
            Assert.AreEqual(0f, clean.WaterBump);
            Assert.IsNull(clean.ReflectSource);

            clean = new SceneMaterial { Shine = 0.5f, Roughness = 2f, Metallic = true, Water = true }.Clean();
            Assert.AreEqual(0.5f, clean.Shine);
            Assert.AreEqual(1f, clean.Roughness);
            Assert.IsTrue(clean.Metallic);
            Assert.IsTrue(clean.Water);
        }

        [TestMethod]
        public void Settings_ReflectionDefaultsAndSanitise()
        {
            var settings = new LaunchSettings();
            Assert.AreEqual(50, settings.ReflectionThreshold);
            Assert.AreEqual(1f, settings.ReflectionStrength);
            Assert.IsTrue(settings.ReflectionProbes, "probes are on by default");
            Assert.AreEqual(128, settings.ProbeResolution);

            settings.ProbeResolution = 300;
            settings.Sanitise();
            Assert.AreEqual(256, settings.ProbeResolution);
            settings.ProbeResolution = 7;
            settings.Sanitise();
            Assert.AreEqual(128, settings.ProbeResolution);

            settings.ReflectionThreshold = 10;
            settings.ReflectionStrength = 9f;
            settings.Sanitise();
            Assert.AreEqual(25, settings.ReflectionThreshold);
            Assert.AreEqual(2f, settings.ReflectionStrength);

            settings.ReflectionThreshold = 75;
            settings.ReflectionStrength = float.NaN;
            settings.Sanitise();
            Assert.AreEqual(50, settings.ReflectionThreshold);
            Assert.AreEqual(1f, settings.ReflectionStrength);
        }

        [TestMethod]
        public void Materials_BuildAFileLoadsAndUnknownFieldsAreIgnored()
        {
            using var folder = new TempFolder();
            string path = folder.File("builda.bimgo");
            Assert.IsTrue(BimGoWriter.Write(path, TestData.BuildDocument(materials: Sample()), TestData.Writer, FileKinds.SAVE, out string error), error);

            // A build A table (no build B fields) plus a field from some later build
            using (ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                zip.GetEntry("materials.json").Delete();
                using var writer = new StreamWriter(zip.CreateEntry("materials.json").Open());
                writer.Write(@"{ ""textureMaxSize"": 512, ""materials"": [
                    { ""name"": ""Brick"", ""colour"": [0.6, 0.3, 0.2], ""texture"": ""textures/a.jpg"", ""textureState"": ""embedded"", ""futureThing"": { ""x"": 1 } },
                    { ""name"": ""B2"" }, { ""name"": ""Glass"" }, { ""name"": ""Carpet"", ""texture"": ""textures/b.jpg"", ""textureState"": ""embedded"" } ] }");
            }

            MaterialData got = BimGoReader.Read(path, null, out error)?.Scene.Materials;
            Assert.IsNotNull(got, error);
            Assert.IsFalse(got.IsEmpty);
            Assert.AreEqual("textures/a.jpg", got.Materials[0].Texture);
            Assert.IsNull(got.Materials[0].UniqueId);
            Assert.IsNull(got.Materials[0].TextureOrigin);
            Assert.IsFalse(got.Materials[0].Invert);
        }

        [TestMethod]
        public void MaterialData_WithSharesStreamsMergesAndPrunesImages()
        {
            MaterialData sample = Sample();
            SceneMaterial[] table = sample.Materials.Select(m => m.Clean()).ToArray();
            table[3].Texture = "textures/c.jpg"; // carpet now uses a new image; b.jpg is no longer referenced
            byte[] jpegC = { 0xFF, 0xD8, 7, 0xFF, 0xD9 };

            MaterialData changed = sample.With(table, new Dictionary<string, byte[]> { ["textures/c.jpg"] = jpegC, ["textures/unused.jpg"] = jpegC });
            Assert.AreSame(sample.VertexMaterial, changed.VertexMaterial);
            Assert.AreSame(sample.VertexUv, changed.VertexUv);
            Assert.AreEqual(sample.TextureMaxSize, changed.TextureMaxSize);
            CollectionAssert.AreEquivalent(new[] { "textures/a.jpg", "textures/c.jpg" }, changed.Textures.Keys.ToList());
            Assert.AreEqual("textures/b.jpg", sample.Materials[3].Texture, "the original is untouched");
            Assert.AreEqual(2, sample.Textures.Count);
            Assert.ThrowsExactly<System.ArgumentException>(() => sample.With(table.Take(2).ToArray()));
        }

        [TestMethod]
        public void Document_MaterialsOverrideIsWritten()
        {
            using var folder = new TempFolder();
            BimGoDocument document = TestData.BuildDocument(materials: Sample());
            SceneMaterial[] table = document.Scene.Materials.Materials.Select(m => m.Clean()).ToArray();
            table[2].Proxy = "brick";
            document.Materials = document.Scene.Materials.With(table);

            MaterialData got = WriteAndRead(document, folder).Scene.Materials;
            Assert.AreEqual("brick", got.Materials[2].Proxy);
            Assert.IsNull(document.Scene.Materials.Materials[2].Proxy, "the scene snapshot is untouched");
        }

        [TestMethod]
        public void Settings_BuildBDefaultsAndSanitise()
        {
            var settings = new LaunchSettings();
            Assert.AreEqual(TintMode.Multiply, settings.RevitTint);
            Assert.IsTrue(settings.ProxyMissingTextures);
            Assert.IsTrue(settings.ProxyMaterialColour);
            Assert.AreEqual(0, settings.TextureSearchFolders.Count);

            Assert.IsTrue(settings.AddTextureSearchFolder(@"D:\Maps\"));
            Assert.IsFalse(settings.AddTextureSearchFolder(@"d:\maps"), "same folder, already last");
            for (int i = 0; i < 25; i++) { settings.AddTextureSearchFolder($@"E:\F{i}"); }
            Assert.AreEqual(LaunchSettings.MAX_TEXTURE_SEARCH_FOLDERS, settings.TextureSearchFolders.Count);
            Assert.AreEqual(@"E:\F24", settings.TextureSearchFolders[^1]);

            settings.TextureSearchFolders = new List<string> { " ", @"A:\x\", @"a:\X", null, @"B:\y" };
            settings.RevitTint = (TintMode)7;
            settings.Sanitise();
            CollectionAssert.AreEqual(new[] { @"a:\X", @"B:\y" }, settings.TextureSearchFolders);
            Assert.AreEqual(TintMode.Multiply, settings.RevitTint);

            settings.RevitTint = TintMode.KeepLightness;
            settings.Sanitise();
            Assert.AreEqual(TintMode.Multiply, settings.RevitTint, "the retired trial mode reads as multiply");

            settings.RevitTint = TintMode.Off;
            settings.ProxyMissingTextures = false;
            LaunchSettings copy = settings.Clone();
            Assert.AreNotSame(settings.TextureSearchFolders, copy.TextureSearchFolders);
            CollectionAssert.AreEqual(settings.TextureSearchFolders, copy.TextureSearchFolders);
            Assert.AreEqual(TintMode.Off, copy.RevitTint);
            Assert.IsFalse(copy.ProxyMissingTextures);
        }
    }
}
