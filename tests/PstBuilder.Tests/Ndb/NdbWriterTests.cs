using System;
using System.IO;
using System.Linq;
using PstBuilder.Foundation;
using PstBuilder.Ndb;
using Xunit;

namespace PstBuilder.Tests.Ndb
{
    public class NdbWriterTests
    {
        [Theory]
        [InlineData(0, 64)]     // 0 + 16 trailer -> round up to 64
        [InlineData(48, 64)]    // 48 + 16 = 64 exactly
        [InlineData(49, 128)]   // 65 -> 128
        [InlineData(236, 256)]  // spec "Anatomy of a Block" example
        [InlineData(240, 256)]  // 256 exactly
        [InlineData(8176, 8192)]// max payload
        public void TotalBlockSize_RoundsToSpec(int raw, int expected)
        {
            Assert.Equal(expected, NdbFormat.TotalBlockSize(raw));
        }

        [Fact]
        public void AMapPage_MarksItselfAndAllocatedRange()
        {
            long amapIb = PstConstants.FirstAMapOffset;
            long dataEnd = amapIb + 64 * 10; // 10 allocated units
            byte[] page = AllocationMapBuilder.BuildAMapPage((ulong)amapIb, dataEnd);

            Assert.Equal(PstConstants.PageSize, page.Length);
            Assert.Equal(0xFF, page[0]);          // first 8 units (the AMap page) allocated
            // 10 units total => byte0 = 0xFF (8 units), byte1 has the two high bits set (units 8,9).
            Assert.Equal(0xC0, page[1]);
            Assert.Equal(0x00, page[2]);

            // Trailer: ptype AMap, wSig 0, bid == ib.
            int t = NdbFormat.PageTrailerOffset;
            Assert.Equal((byte)PageType.AMap, page[t]);
            Assert.Equal((byte)PageType.AMap, page[t + 1]);
            Assert.Equal(0, BitConverter.ToUInt16(page, t + 2));
            Assert.Equal(Crc.Compute(page.AsSpan(0, t)), BitConverter.ToUInt32(page, t + 4));
            Assert.Equal((ulong)amapIb, BitConverter.ToUInt64(page, t + 8));
        }

        [Fact]
        public void EmptyStore_ProducesValidContainer()
        {
            byte[] file = BuildPst(_ => { });

            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();
            Assert.Empty(reader.Nodes);
            Assert.Empty(reader.Blocks);
            // File padded to one full AMap region.
            Assert.Equal((ulong)(PstConstants.FirstAMapOffset + PstConstants.AMapSpan), reader.IbFileEof);
        }

        [Fact]
        public void BlocksAndNodes_RoundTrip()
        {
            byte[] payloadA = Enumerable.Range(0, 236).Select(i => (byte)i).ToArray();
            byte[] payloadB = Enumerable.Range(0, 1000).Select(i => (byte)(i * 7)).ToArray();
            var storeNid = Nid.MessageStore;
            var folderNid = new Nid(NidType.NormalFolder, 0x401);
            Bid bidA = default, bidB = default;

            byte[] file = BuildPst(ndb =>
            {
                bidA = ndb.AddBlock(payloadA);
                bidB = ndb.AddBlock(payloadB, isInternal: true);
                ndb.AddNode(storeNid, bidA, new Bid(0), new Nid(0));
                ndb.AddNode(folderNid, bidB, new Bid(0), storeNid);
            });

            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();

            Assert.Equal(2, reader.Blocks.Count);
            Assert.Equal(2, reader.Nodes.Count);
            Assert.True(reader.Blocks.ContainsKey(bidA.Value));
            Assert.Equal(236, reader.Blocks[bidA.Value].Cb);
            Assert.Equal(1000, reader.Blocks[bidB.Value].Cb);
            Assert.True(bidB.IsInternal);

            var folder = reader.Nodes[folderNid.Value];
            Assert.Equal(bidB.Value, folder.BidData.Value);
            Assert.Equal(storeNid.Value, folder.NidParent.Value);
        }

        [Fact]
        public void ManyNodes_BuildMultiLevelTrees()
        {
            byte[] file = BuildPst(ndb =>
            {
                // Enough nodes to force NBT intermediate pages (> 15 per leaf).
                for (uint i = 0; i < 40; i++)
                {
                    Bid bid = ndb.AddBlock(new byte[32]);
                    ndb.AddNode(new Nid(NidType.NormalMessage, 0x10000 + i), bid, new Bid(0), Nid.RootFolder);
                }
            });

            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();
            Assert.Equal(40, reader.Nodes.Count);
            Assert.Equal(40, reader.Blocks.Count);
        }

        private static byte[] BuildPst(Action<NdbWriter> build)
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            {
                var ndb = new NdbWriter(os);
                build(ndb);
                ndb.Finalize();
            }
            return ms.ToArray();
        }
    }
}
