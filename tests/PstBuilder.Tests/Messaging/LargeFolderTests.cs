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
    public class LargeFolderTests
    {
        private static byte[] Write(StoreWriter store)
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
                store.Write(os);
            return ms.ToArray();
        }

        [Theory]
        [InlineData(50)]    // overflows a single-block HN (cells across blocks)
        [InlineData(400)]   // near the old single-heap-item Row ID BTH limit (~447)
        [InlineData(1500)]  // forces a multi-level Row ID BTH (bIdxLevels > 0)
        public void FolderWithManyMessages_RoundTrips(int count)
        {
            var store = new StoreWriter();
            var folder = store.IpmSubtree.AddFolder("Big Folder");
            for (int i = 0; i < count; i++)
            {
                var m = new MessageItem
                {
                    Subject = $"Message number {i} with a reasonably long subject line for bulk",
                    SenderName = $"Sender {i}",
                    DisplayTo = "Recipient",
                    Body = $"Body of message {i}.",
                };
                m.Recipients.Add(new RecipientItem { DisplayName = "Recipient", EmailAddress = "r@example.com" });
                folder.AddMessage(m);
            }

            var reader = new NdbRoundTripReader(Write(store));
            reader.ReadAndValidate(); // validates every block: HN data-tree, row-matrix subnode, etc.

            Assert.Equal(count, reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage));

            // For 400 rows the big folder's contents table must use a multi-block HN (data-tree, internal
            // XBLOCK) and spill its row matrix to a subnode.
            if (count >= 400)
            {
                var contentsTables = reader.Nodes.Values.Where(n => n.Nid.Type == NidType.ContentsTable).ToList();
                Assert.Contains(contentsTables, n => n.BidData.IsInternal);          // multi-block HN
                Assert.Contains(contentsTables, n => n.BidSub.Value != 0);           // row-matrix subnode
            }

            // For 1500 rows the Row ID BTH must be multi-level. Parse the big folder's contents-table HN
            // (resolving its multi-block data-tree) and read the BTHHEADER's bIdxLevels.
            if (count >= 1500)
            {
                var contents = reader.Nodes.Values
                    .Where(n => n.Nid.Type == NidType.ContentsTable && n.BidData.IsInternal)
                    .OrderByDescending(n => n.BidData.Value).First();
                byte idxLevels = ReadRowIndexLevels(reader, contents.BidData);
                Assert.True(idxLevels >= 1, $"Expected a multi-level Row ID BTH, got bIdxLevels={idxLevels}.");
            }
        }

        // Resolves a TC's (possibly multi-block) HN, then reads TCINFO.hidRowIndex -> BTHHEADER.bIdxLevels.
        private static byte ReadRowIndexLevels(NdbRoundTripReader reader, Bid bidData)
        {
            byte[][] blocks = ReadHnBlocks(reader, bidData);
            byte[] hdrBlock = blocks[0];
            var userRoot = new PstBuilder.Ltp.Hid(BitConverter.ToUInt32(hdrBlock, 4)); // HNHDR.hidUserRoot
            byte[] tcInfo = ResolveHid(blocks, userRoot);
            var hidRowIndex = new PstBuilder.Ltp.Hid(BitConverter.ToUInt32(tcInfo, 10)); // TCINFO.hidRowIndex @10
            byte[] bthHeader = ResolveHid(blocks, hidRowIndex);
            return bthHeader[3]; // BTHHEADER.bIdxLevels
        }

        private static byte[][] ReadHnBlocks(NdbRoundTripReader reader, Bid bidData)
        {
            byte[] raw = reader.GetBlockData(bidData);
            if (!(bidData.IsInternal && raw.Length >= 2 && raw[0] == 0x01))
                return new[] { raw };

            int cEnt = BitConverter.ToUInt16(raw, 2);
            var leaves = new System.Collections.Generic.List<byte[]>();
            for (int i = 0; i < cEnt; i++)
            {
                var childBid = new Bid(BitConverter.ToUInt64(raw, 8 + i * 8));
                if (raw[1] == 0x02) leaves.AddRange(ReadHnBlocks(reader, childBid)); // XXBLOCK -> XBLOCKs
                else leaves.Add(reader.GetBlockData(childBid));                       // XBLOCK -> data blocks
            }
            return leaves.ToArray();
        }

        private static byte[] ResolveHid(byte[][] blocks, PstBuilder.Ltp.Hid hid)
        {
            byte[] block = blocks[hid.HidBlockIndex];
            int ibHnpm = BitConverter.ToUInt16(block, 0);
            int start = BitConverter.ToUInt16(block, ibHnpm + 4 + (hid.HidIndex - 1) * 2);
            int end = BitConverter.ToUInt16(block, ibHnpm + 4 + hid.HidIndex * 2);
            var slice = new byte[end - start];
            Array.Copy(block, start, slice, 0, end - start);
            return slice;
        }
    }
}
