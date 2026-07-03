using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    /// <summary>
    /// Covers the disconnect/crash resilience story: a producer may stall and resume (Case 1), and a
    /// checkpointed export survives a crash and can be resumed into a new part (Case 2).
    /// </summary>
    public class ResilienceTests
    {
        private static int MessageCount(byte[] file)
        {
            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();
            return reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage);
        }

        // Case 1: a producer can pause mid-export (e.g. a source disconnect) for any length of time and
        // resume — there is no idle timeout and nothing is lost.
        [Fact]
        public void ProducerMayPauseAndResume_NothingLost()
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            using (var session = PstExportSession.Create(os, "Paused"))
            {
                session.AddMessage("Inbox", new MessageItem { Subject = "before the stall" });
                Thread.Sleep(400);   // simulate a disconnect: the session just waits, no timeout
                session.AddMessage("Inbox", new MessageItem { Subject = "after reconnecting" });
                session.Complete();
            }

            Assert.Equal(2, MessageCount(ms.ToArray()));
        }

        // Case 2: Checkpoint() writes a durable part; a crash (Dispose without Complete) keeps every
        // checkpointed part; Resume() continues at the next slot, overwriting the partial trailing file.
        [Fact]
        public void Checkpoint_IsDurable_AndResumeContinuesAfterCrash()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstbuilder-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "export.pst");
            string part2 = Path.Combine(dir, "export-002.pst");
            try
            {
                // ---- first run: two items, checkpoint, then a "crash" (abandon without Complete) ----
                using (var session = PstExportSession.CreateResumable(path))
                {
                    session.AddMessage("Inbox", new MessageItem { Subject = "a1" });
                    session.AddMessage("Inbox", new MessageItem { Subject = "a2" });

                    var sealed1 = session.Checkpoint();          // part 1 is now durable on disk
                    Assert.NotNull(sealed1);
                    Assert.Equal(path, sealed1!.Name);

                    session.AddMessage("Inbox", new MessageItem { Subject = "lost (never checkpointed)" });
                    // fall out of the using WITHOUT Complete() → simulates a crash
                }

                Assert.True(File.Exists(path));
                Assert.Equal(2, MessageCount(File.ReadAllBytes(path)));   // checkpointed items survived

                // ---- restart: Resume continues at the next slot; producer replays the lost item ----
                using (var session = PstExportSession.Resume(path))
                {
                    session.AddMessage("Inbox", new MessageItem { Subject = "lost (never checkpointed)" });
                    session.AddMessage("Inbox", new MessageItem { Subject = "a3" });
                    session.Complete();
                }

                Assert.True(File.Exists(part2));
                Assert.Equal(2, MessageCount(File.ReadAllBytes(part2)));  // resumed content, partial overwritten
                Assert.Equal(2, MessageCount(File.ReadAllBytes(path)));   // part 1 still intact
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Checkpoint_WithNothingNew_ReturnsNull()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstbuilder-resume-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "export.pst");
            try
            {
                using var session = PstExportSession.CreateResumable(path);
                Assert.Null(session.Checkpoint());   // nothing added yet → nothing to make durable
                session.AddMessage("Inbox", new MessageItem { Subject = "x" });
                session.Complete();
                Assert.Equal(1, MessageCount(File.ReadAllBytes(path)));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Checkpoint_OnNonSplitSession_Throws()
        {
            using var ms = new MemoryStream();
            using var os = new PstOutputStream(ms, leaveOpen: true);
            using var session = PstExportSession.Create(os);
            Assert.Throws<InvalidOperationException>(() => session.Checkpoint());
        }

        // A plain single-file (path) session must roll into numbered parts instead of failing when it
        // would exceed the single-file limit. Exercised via a small auto-roll size so we don't write GBs.
        [Fact]
        public void SingleFileExport_AutoRollsIntoNumberedParts_WhenOverLimit()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstbuilder-autoroll-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "big.pst");
            try
            {
                PstExportResult result;
                using (var session = PstExportSession.CreateWithAutoRoll(path, autoRollBytes: 300_000))
                {
                    for (int i = 0; i < 1200; i++)
                        session.AddMessage("Inbox", new MessageItem
                        {
                            Subject = $"message {i}",
                            Body = new string('x', 400),   // give each item some heft to cross the tiny cap
                        });
                    result = session.Complete();
                }

                Assert.True(result.Parts.Count > 1, "export should have rolled into multiple parts");
                Assert.True(File.Exists(path));                                   // first part keeps the base name
                Assert.True(File.Exists(Path.Combine(dir, "big-002.pst")));       // later parts get -NNN
                foreach (var part in result.Parts)
                    new NdbRoundTripReader(File.ReadAllBytes(part.Name)).ReadAndValidate();  // every part is a valid PST
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Compression runs in the background per part; rolling into several parts must still end up with
        // every part zipped (and no raw .pst left behind) by the time Complete() returns.
        [Fact]
        public void AutoRollWithCompress_EveryPartEndsUpZipped()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstbuilder-autoroll-zip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "big.pst");
            try
            {
                PstExportResult result;
                using (var session = PstExportSession.CreateWithAutoRoll(path, autoRollBytes: 300_000, compress: true))
                {
                    for (int i = 0; i < 1200; i++)
                        session.AddMessage("Inbox", new MessageItem
                        {
                            Subject = $"message {i}",
                            Body = new string('x', 400),
                        });
                    result = session.Complete();
                }

                Assert.True(result.Parts.Count > 1, "export should have rolled into multiple parts");
                foreach (var part in result.Parts)
                {
                    Assert.EndsWith(".zip", part.Name);
                    Assert.True(File.Exists(part.Name));
                    Assert.False(File.Exists(part.Name.Substring(0, part.Name.Length - ".zip".Length)));
                    Assert.True(part.WhenReady.IsCompletedSuccessfully);
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Checkpoint() must not block on compression (the whole point of running it in the background),
        // but by the time Complete() returns every checkpointed part must be durably zipped too.
        [Fact]
        public void CheckpointWithCompress_CheckpointDoesNotBlock_AllPartsZippedByComplete()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstbuilder-checkpoint-zip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "export.pst");
            try
            {
                using var session = PstExportSession.CreateResumable(path, compress: true);
                session.AddMessage("Inbox", new MessageItem { Subject = "a1" });
                var part1 = session.Checkpoint();
                Assert.NotNull(part1);
                Assert.Equal(path + ".zip", part1!.Name);

                session.AddMessage("Inbox", new MessageItem { Subject = "a2" });
                var result = session.Complete();

                foreach (var part in result.Parts)
                {
                    Assert.True(part.WhenReady.IsCompletedSuccessfully);
                    Assert.True(File.Exists(part.Name));
                    Assert.False(File.Exists(part.Name.Substring(0, part.Name.Length - ".zip".Length)));
                }
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Resume() must recognize an already-compressed part as complete (raw .pst deleted, .zip present)
        // and continue at the next slot rather than re-doing it.
        [Fact]
        public async Task Resume_RecognizesCompressedPartAsComplete()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstbuilder-resume-zip-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "export.pst");
            string part2 = Path.Combine(dir, "export-002.pst");
            try
            {
                using (var session = PstExportSession.CreateResumable(path, compress: true))
                {
                    session.AddMessage("Inbox", new MessageItem { Subject = "a1" });
                    var sealed1 = session.Checkpoint();
                    Assert.NotNull(sealed1);
                    // Checkpoint() deliberately doesn't wait for compression (that's the point of running
                    // it in the background) — wait on WhenReady explicitly before relying on the .zip below.
                    await sealed1!.WhenReady;
                    // fall out WITHOUT Complete() → simulates a crash right after the checkpoint
                }

                Assert.False(File.Exists(path));
                Assert.True(File.Exists(path + ".zip"));

                using (var session = PstExportSession.Resume(path, compress: true))
                {
                    session.AddMessage("Inbox", new MessageItem { Subject = "a2" });
                    session.Complete();
                }

                Assert.True(File.Exists(part2 + ".zip"));   // resumed straight into part 2, part 1 left alone
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
