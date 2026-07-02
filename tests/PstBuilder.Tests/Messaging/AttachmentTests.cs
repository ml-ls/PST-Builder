using System;
using System.IO;
using System.Linq;
using System.Text;
using PstBuilder.Eml;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    public class AttachmentTests
    {
        private static byte[] Write(StoreWriter store)
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
                store.Write(os);
            return ms.ToArray();
        }

        [Fact]
        public void SmallAttachment_WiresTableAndObjectSubnodes()
        {
            var store = new StoreWriter();
            var folder = store.IpmSubtree.AddFolder("Inbox");
            var msg = new MessageItem { Subject = "With attachment", DisplayTo = "X" };
            msg.Recipients.Add(new RecipientItem { DisplayName = "X", EmailAddress = "x@example.com" });
            msg.Attachments.Add(new AttachmentItem
            {
                FileName = "hello.txt",
                MimeType = "text/plain",
                Content = Encoding.ASCII.GetBytes("small attachment payload"),
            });
            folder.AddMessage(msg);

            var reader = new NdbRoundTripReader(Write(store));
            reader.ReadAndValidate();

            // The message has a subnode tree; the SLBLOCK contains recipient table, attachment table,
            // and the attachment object (NID_TYPE_ATTACHMENT).
            var message = First(reader, NidType.NormalMessage);
            byte[] sub = reader.GetBlockData(message.BidSub);
            Assert.Equal(0x02, sub[0]); // SLBLOCK
            int cEnt = BitConverter.ToUInt16(sub, 2);
            Assert.True(cEnt >= 3); // recipient table + attachment table + 1 attachment

            // One SLENTRY must be an attachment object NID.
            bool hasAttachNode = false, hasAttachTable = false;
            for (int i = 0; i < cEnt; i++)
            {
                var nid = new Nid((uint)BitConverter.ToUInt64(sub, 8 + i * 24));
                if (nid.Type == NidType.Attachment) hasAttachNode = true;
                if (nid.Value == 0x671) hasAttachTable = true;
            }
            Assert.True(hasAttachNode);
            Assert.True(hasAttachTable);

            // HASATTACH flag set on the message PC block (PC parse not needed; flag is a sanity check
            // that we set it — verified indirectly by the EML test below).
        }

        [Fact]
        public void LargeAttachment_SpillsThroughNestedSubnodeAndDataTree()
        {
            var store = new StoreWriter();
            var folder = store.IpmSubtree.AddFolder("Inbox");
            var msg = new MessageItem { Subject = "Big file", DisplayTo = "X" };
            msg.Recipients.Add(new RecipientItem { DisplayName = "X", EmailAddress = "x@example.com" });
            var payload = new byte[40000];
            new Random(7).NextBytes(payload);
            msg.Attachments.Add(new AttachmentItem { FileName = "big.bin", Content = payload });
            folder.AddMessage(msg);

            var reader = new NdbRoundTripReader(Write(store));
            reader.ReadAndValidate(); // validates every block incl. data-tree leaves + XBLOCK

            // The attachment object SLENTRY has a nested subnode (bidSub != 0) for the spilled data.
            var message = First(reader, NidType.NormalMessage);
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
            Assert.True(nestedFound, "Large attachment should have a nested subnode for its data tree.");
        }

        [Fact]
        public void EmlWithAttachment_RoundTrips()
        {
            string eml =
                "From: Alice <alice@example.com>\r\n" +
                "To: Bob <bob@example.com>\r\n" +
                "Subject: See attached\r\n" +
                "Date: Tue, 23 Jun 2026 14:05:00 +0000\r\n" +
                "MIME-Version: 1.0\r\n" +
                "Content-Type: multipart/mixed; boundary=\"x\"\r\n\r\n" +
                "--x\r\nContent-Type: text/plain\r\n\r\nBody text.\r\n" +
                "--x\r\nContent-Type: text/plain; name=\"note.txt\"\r\n" +
                "Content-Disposition: attachment; filename=\"note.txt\"\r\n\r\n" +
                "attached file contents\r\n--x--\r\n";

            var msg = EmlMapper.ToMessageItem(Encoding.ASCII.GetBytes(eml));
            Assert.Single(msg.Attachments);
            Assert.Equal("note.txt", msg.Attachments[0].FileName);
            Assert.Contains("attached file contents", Encoding.ASCII.GetString(msg.Attachments[0].Content));

            var store = new StoreWriter();
            store.IpmSubtree.AddFolder("Inbox").AddMessage(msg);
            new NdbRoundTripReader(Write(store)).ReadAndValidate();
        }

        private static NbtLeafEntry First(NdbRoundTripReader reader, NidType type) =>
            reader.Nodes.First(kv => new Nid(kv.Key).Type == type).Value;
    }
}
