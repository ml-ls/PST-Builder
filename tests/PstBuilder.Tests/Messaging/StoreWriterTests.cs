using System;
using System.IO;
using PstBuilder.Foundation;
using PstBuilder.Ndb;
using PstBuilder.Messaging;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    public class StoreWriterTests
    {
        [Fact]
        public void Milestone_OneFolderOneMessage_ProducesValidContainer()
        {
            var store = new StoreWriter { StoreDisplayName = "Test PST" };
            var folder = store.IpmSubtree.AddFolder("Test Folder");
            var msg = new MessageItem
            {
                Subject = "Hello from PstBuilder",
                Body = "This is a plain-text test message.\r\n",
                DisplayTo = "Test User",
            };
            msg.Recipients.Add(new RecipientItem { DisplayName = "Test User", EmailAddress = "test@example.com" });
            folder.AddMessage(msg);

            byte[] file;
            using (var ms = new MemoryStream())
            {
                using (var os = new PstOutputStream(ms, leaveOpen: true))
                    store.Write(os);
                file = ms.ToArray();
            }

            // The container must be structurally valid: header CRCs, NBT/BBT walk, every block CRC, AMap.
            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();

            // Well-known nodes must be present.
            Assert.Contains(Nid.MessageStore.Value, reader.Nodes.Keys);
            Assert.Contains(Nid.NameToIdMap.Value, reader.Nodes.Keys);
            Assert.Contains(Nid.RootFolder.Value, reader.Nodes.Keys);

            // The message node exists with a subnode (recipient table) and parents to its folder.
            var message = FindFirst(reader, NidType.NormalMessage);
            Assert.NotEqual(0u, message.BidSub.Value);

            // The user folder exists and parents to the IPM subtree.
            Assert.True(CountOfType(reader, NidType.NormalFolder) >= 4); // root + top + deleted + finder + test
            Assert.True(CountOfType(reader, NidType.HierarchyTable) >= 5);
            Assert.True(CountOfType(reader, NidType.ContentsTable) >= 5);

            // LTP wiring: the store node points to a PC (HN bSig=0xEC, bClientSig=0xBC).
            byte[] storeBlock = reader.GetBlockData(reader.Nodes[Nid.MessageStore.Value].BidData);
            Assert.Equal(0xEC, storeBlock[2]);
            Assert.Equal(0xBC, storeBlock[3]);

            // The message's data block is a PC; its subnode block is an SLBLOCK (btype 0x02).
            byte[] msgBlock = reader.GetBlockData(message.BidData);
            Assert.Equal(0xBC, msgBlock[3]);
            byte[] subBlock = reader.GetBlockData(message.BidSub);
            Assert.Equal(0x02, subBlock[0]); // SLBLOCK btype
            Assert.Equal(1, BitConverter.ToUInt16(subBlock, 2)); // one SLENTRY (recipient table)

            // The recipient table referenced by the SLENTRY is a TC (bClientSig=0x7C) with a row.
            var recipBid = new Bid(BitConverter.ToUInt64(subBlock, 8 + 8)); // SLENTRY.bidData
            byte[] recipBlock = reader.GetBlockData(recipBid);
            Assert.Equal(0x7C, recipBlock[3]);
        }

        [Fact]
        public void LargeHtmlBody_SpillsToDataTree_AndRoundTrips()
        {
            var store = new StoreWriter();
            var folder = store.IpmSubtree.AddFolder("Big");
            var msg = new MessageItem { Subject = "Large", DisplayTo = "X" };
            // ~50 KB HTML forces data blocks + an XBLOCK behind a subnode.
            msg.BodyHtml = "<html><body>" + new string('A', 50000) + "</body></html>";
            msg.Recipients.Add(new RecipientItem { DisplayName = "X", EmailAddress = "x@example.com" });
            folder.AddMessage(msg);

            byte[] file;
            using (var ms = new MemoryStream())
            {
                using (var os = new PstOutputStream(ms, leaveOpen: true))
                    store.Write(os);
                file = ms.ToArray();
            }

            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate(); // validates every block incl. data blocks + XBLOCK + SLBLOCK

            // The message has a subnode tree (recipient table + spilled HTML body).
            var message = FindFirst(reader, NidType.NormalMessage);
            Assert.NotEqual(0u, message.BidSub.Value);
            // Many blocks exist (50 KB / 8 KB => ~7 data blocks + xblock + others).
            Assert.True(reader.Blocks.Count > 8);
        }

        [Fact]
        public void EmptyStore_StillValid()
        {
            var store = new StoreWriter();
            byte[] file;
            using (var ms = new MemoryStream())
            {
                using (var os = new PstOutputStream(ms, leaveOpen: true))
                    store.Write(os);
                file = ms.ToArray();
            }
            new NdbRoundTripReader(file).ReadAndValidate();
        }

        private static NbtLeafEntry FindFirst(NdbRoundTripReader reader, NidType type)
        {
            foreach (var kv in reader.Nodes)
                if (new Nid(kv.Key).Type == type) return kv.Value;
            throw new Xunit.Sdk.XunitException($"No node of type {type}.");
        }

        private static int CountOfType(NdbRoundTripReader reader, NidType type)
        {
            int n = 0;
            foreach (var kv in reader.Nodes)
                if (new Nid(kv.Key).Type == type) n++;
            return n;
        }
    }
}
