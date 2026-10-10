using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BimGo.Format;
using BimGo.Scene;
using Microsoft.VisualStudio.TestTools.UnitTesting;

// The class belongs to the Tests namespace
namespace BimGo.Tests
{
    /// <summary>
    /// Sun hours round: the study's sun samples and legend, the comment issue fields (status, priority, assignee,
    /// replies, view, thumbnail) and the room helpers used by Find room and the study.
    /// </summary>
    [TestClass]
    public sealed class SunHoursAndCommentsTests
    {
        private static SiteInfo Sydney() => new() { HasLocation = true, Latitude = -33.8688, Longitude = 151.2093, TimeZone = 10, PlaceName = "Sydney" };

        [TestMethod]
        public void SunDirections_MidWinterNineToThree_AreAllDaytime()
        {
            var settings = new SunHoursSettings(); // 21 June, 9:00–15:00, 5 min
            List<Vector3> directions = SunHours.SunDirections(Sydney(), 2026, settings, out int total, out bool known);
            Assert.IsTrue(known);
            Assert.AreEqual(72, total, "6 h at 5 min = 72 samples");
            Assert.AreEqual(72, directions.Count, "the sun is up in Sydney from 9 to 3 in June");
            Assert.IsTrue(directions.All(d => d.Z > 0f && MathF.Abs(d.Length() - 1f) < 1e-4f));

            // Southern hemisphere winter: the sun is in the north (+Y with no rotation) at noon
            List<Vector3> noon = SunHours.SunDirections(Sydney(), 2026, new SunHoursSettings { StartMinutes = 715, EndMinutes = 725, StepMinutes = 10 }, out _, out _);
            Assert.AreEqual(1, noon.Count);
            Assert.IsTrue(noon[0].Y > 0.6f, $"noon sun should be north: {noon[0]}");
        }

        [TestMethod]
        public void SunDirections_NightIsEmptyAndTheFallbackIsReported()
        {
            var night = new SunHoursSettings { StartMinutes = 0, EndMinutes = 4 * 60, StepMinutes = 15 };
            List<Vector3> directions = SunHours.SunDirections(new SiteInfo(), 2026, night, out int total, out bool known);
            Assert.IsFalse(known, "no site: Sydney is assumed and reported");
            Assert.AreEqual(16, total);
            Assert.AreEqual(0, directions.Count);
        }

        [TestMethod]
        public void SunHoursSettings_CleanClamps()
        {
            SunHoursSettings clean = new SunHoursSettings
            {
                Month = 2, Day = 31, StartMinutes = 900, EndMinutes = 600, StepMinutes = 7, GridSize = 0.3f,
                FloorOffset = 9f, WallOffset = float.NaN
            }.Clean(2026);
            Assert.AreEqual(28, clean.Day);
            Assert.AreEqual(905, clean.EndMinutes, "end moves after start");
            Assert.AreEqual(5, clean.StepMinutes);
            Assert.AreEqual(0.25f, clean.GridSize);
            Assert.AreEqual(2f, clean.FloorOffset);
            Assert.AreEqual(0f, clean.WallOffset);
        }

        [TestMethod]
        public void LegendColour_MatchesLadybugEnds()
        {
            Vector3 low = SunHours.LegendColour(0f), high = SunHours.LegendColour(SunHours.LEGEND_MAX), over = SunHours.LegendColour(20f);
            Assert.AreEqual(75 / 255f, low.X, 1e-4f);
            Assert.AreEqual(169 / 255f, low.Z, 1e-4f);
            Assert.AreEqual(234 / 255f, high.X, 1e-4f);
            Assert.AreEqual(38 / 255f, high.Y, 1e-4f);
            Assert.AreEqual(high, over);
            Assert.AreEqual(low, SunHours.LegendColour(float.NaN));
        }

        [TestMethod]
        public void Comments_IssueFieldsRoundTripAndOlderFilesReadAsOpen()
        {
            using var folder = new TempFolder();
            string path = folder.File("Tower.bimgo-comments.json");
            var record = new CommentRecord
            {
                Text = "Door swing clashes with the bench", Status = CommentStatus.IN_PROGRESS, Priority = CommentPriority.HIGH,
                AssignedTo = " Sam ", Replies = new List<CommentReply> { new() { Text = "Checking", Author = "sam" }, new() { Text = "  " } },
                View = new CommentView { X = 10, Y = 20, Z = 30, Yaw = 1.5f, Pitch = -0.2f }, Thumbnail = "AAAA"
            };
            Assert.IsTrue(CommentFiles.Write(path, new CommentDocument { Comments = new List<CommentRecord> { record } }, out string error), error);

            CommentRecord read = CommentFiles.Read(path, out error).Comments.Single().Clean();
            Assert.AreEqual(CommentStatus.IN_PROGRESS, read.Status);
            Assert.AreEqual(CommentPriority.HIGH, read.Priority);
            Assert.AreEqual("Sam", read.AssignedTo);
            Assert.AreEqual(1, read.ReplyCount, "blank replies are dropped");
            Assert.AreEqual("Checking", read.Replies[0].Text);
            Assert.AreEqual(1.5f, read.View.Yaw);
            Assert.AreEqual("AAAA", read.Thumbnail);

            // A comment from before the upgrade
            File.WriteAllText(path, "{ \"comments\": [ { \"text\": \"Old one\", \"x\": 1, \"y\": 2, \"z\": 3 } ] }");
            CommentRecord old = CommentFiles.Read(path, out error).Comments.Single().Clean();
            Assert.AreEqual(CommentStatus.OPEN, old.Status);
            Assert.AreEqual(CommentPriority.NORMAL, old.Priority);
            Assert.IsNull(old.AssignedTo);
            Assert.AreEqual(0, old.ReplyCount);
            Assert.IsNull(old.View);
        }

        [TestMethod]
        public void Comments_CleanNormalisesUnknownValues()
        {
            CommentRecord record = new CommentRecord
            {
                Status = "CLOSED", Priority = "urgent", AssignedTo = "   ", Replies = new List<CommentReply>(),
                View = new CommentView { X = double.NaN }, Thumbnail = " "
            }.Clean();
            Assert.AreEqual(CommentStatus.CLOSED, record.Status);
            Assert.AreEqual(CommentPriority.NORMAL, record.Priority);
            Assert.IsNull(record.AssignedTo);
            Assert.IsNull(record.Replies);
            Assert.IsNull(record.View);
            Assert.IsNull(record.Thumbnail);
            Assert.AreEqual("In progress", CommentStatus.Label(CommentStatus.IN_PROGRESS));
            Assert.AreEqual("High", CommentPriority.Label("HIGH"));
        }

        [TestMethod]
        public void Room_ContainsAndDistanceToBoundary()
        {
            // A 4 × 3 room with a 1 × 1 hole (a shaft) in it
            var room = new RoomInfo
            {
                Loops = new[]
                {
                    new[] { new Vector2(0, 0), new Vector2(4, 0), new Vector2(4, 3), new Vector2(0, 3) },
                    new[] { new Vector2(1, 1), new Vector2(2, 1), new Vector2(2, 2), new Vector2(1, 2) }
                },
                Min = new Vector2(0, 0), Max = new Vector2(4, 3), BottomZ = 0, TopZ = 3
            };
            Assert.IsTrue(room.Contains(new Vector2(3, 2)));
            Assert.IsFalse(room.Contains(new Vector2(1.5f, 1.5f)), "the hole is outside");
            Assert.IsFalse(room.Contains(new Vector2(5, 1)));
            Assert.AreEqual(0.5f, room.DistanceToBoundary(new Vector2(3.5f, 2f)), 1e-5f);
            Assert.AreEqual(0.5f, room.DistanceToBoundary(new Vector2(1.5f, 2.5f)), 1e-5f, "the hole's edge counts");
        }
    }
}
