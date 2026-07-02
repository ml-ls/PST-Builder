using System;
using System.IO;
using System.Linq;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    /// <summary>
    /// Verifies that supplying an attachment as a <see cref="Stream"/> (read on demand) produces exactly
    /// the same on-disk bytes as the in-memory <c>byte[]</c> path, and round-trips cleanly.
    /// </summary>
    public class StreamingAttachmentTests
    {
        // Stages blocks into a fresh NdbWriter and returns the raw bytes written so far (no Finalize).
        // BIDs/offsets are deterministic, so two runs of the same block sequence are byte-identical.
        private static byte[] StageToBytes(Action<NdbWriter> stage)
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            {
                var ndb = new NdbWriter(os);
                stage(ndb);
            }
            return ms.ToArray();
        }

        [Fact]
        public void StreamedDataTree_MatchesInMemoryDataTree_AcrossSizes()
        {
            foreach (int size in new[] { 10, 8176, 8177, 40000, 3_000_000 })
            {
                var payload = new byte[size];
                new Random(size).NextBytes(payload);

                byte[] inMemory = StageToBytes(ndb => DataTreeBuilder.Build(ndb, payload));
                byte[] streamed = StageToBytes(ndb =>
                    DataTreeBuilder.BuildFromStream(ndb, () => new MemoryStream(payload), payload.Length));

                Assert.Equal(inMemory, streamed);
            }
        }

        [Fact]
        public void StreamedAttachment_RoundTripsWithNestedSubnode()
        {
            var payload = new byte[50000];
            new Random(3).NextBytes(payload);

            var store = new StoreWriter();
            var folder = store.IpmSubtree.AddFolder("Inbox");
            var msg = new MessageItem { Subject = "Streamed", DisplayTo = "X" };
            msg.Recipients.Add(new RecipientItem { DisplayName = "X", EmailAddress = "x@example.com" });
            msg.Attachments.Add(AttachmentItem.FromStream("big.bin", () => new MemoryStream(payload), payload.Length));
            folder.AddMessage(msg);

            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
                store.Write(os);

            var reader = new NdbRoundTripReader(ms.ToArray());
            reader.ReadAndValidate(); // validates every block incl. the streamed data-tree leaves + XBLOCK

            var message = reader.Nodes.First(kv => new Nid(kv.Key).Type == NidType.NormalMessage).Value;
            byte[] sub = reader.GetBlockData(message.BidSub);
            int cEnt = BitConverter.ToUInt16(sub, 2);
            bool nestedFound = false;
            for (int i = 0; i < cEnt; i++)
            {
                int e = 8 + i * 24;
                var nid = new Nid((uint)BitConverter.ToUInt64(sub, e));
                ulong bidSub = BitConverter.ToUInt64(sub, e + 16);
                if (nid.Type == NidType.Attachment && bidSub != 0) nestedFound = true;
            }
            Assert.True(nestedFound, "Streamed attachment should spill its data into a nested subnode data-tree.");
        }

        [Fact]
        public void StreamSource_ShorterThanDeclaredLength_Throws()
        {
            Assert.Throws<EndOfStreamException>(() =>
                StageToBytes(ndb => DataTreeBuilder.BuildFromStream(
                    ndb, () => new MemoryStream(new byte[100]), length: 40000)));
        }

        [Fact]
        public void FromFile_StreamsFileContent()
        {
            string path = Path.Combine(Path.GetTempPath(), "pstbuilder-attach-" + Guid.NewGuid().ToString("N") + ".bin");
            var payload = new byte[20000];
            new Random(9).NextBytes(payload);
            File.WriteAllBytes(path, payload);
            try
            {
                var att = AttachmentItem.FromFile(path);
                Assert.Equal(payload.Length, att.ContentLength);
                Assert.Equal(Path.GetFileName(path), att.FileName);

                var store = new StoreWriter();
                var msg = new MessageItem { Subject = "File attach", DisplayTo = "X" };
                msg.Recipients.Add(new RecipientItem { DisplayName = "X", EmailAddress = "x@example.com" });
                msg.Attachments.Add(att);
                store.IpmSubtree.AddFolder("Inbox").AddMessage(msg);

                using var ms = new MemoryStream();
                using (var os = new PstOutputStream(ms, leaveOpen: true))
                    store.Write(os);
                new NdbRoundTripReader(ms.ToArray()).ReadAndValidate();
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
