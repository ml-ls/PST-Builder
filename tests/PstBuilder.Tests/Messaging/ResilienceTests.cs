using System;
using System.IO;
using System.Linq;
using System.Threading;
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
    }
}
