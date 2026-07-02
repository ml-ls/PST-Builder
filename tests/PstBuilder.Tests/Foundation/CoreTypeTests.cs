using System;
using PstBuilder.Foundation;
using Xunit;

namespace PstBuilder.Tests.Foundation
{
    public class CoreTypeTests
    {
        [Fact]
        public void Nid_ComposesAndDecomposes()
        {
            var nid = new Nid(NidType.NormalMessage, 0x123);
            Assert.Equal(NidType.NormalMessage, nid.Type);
            Assert.Equal(0x123u, nid.Index);
            // type in low 5 bits, index shifted up by 5.
            Assert.Equal((0x123u << 5) | (uint)NidType.NormalMessage, nid.Value);
        }

        [Fact]
        public void Nid_RoundTripsRawValue()
        {
            var nid = new Nid(0x0000122u); // NID_ROOT_FOLDER
            Assert.Equal(Nid.RootFolder, nid);
            Assert.Equal(NidType.NormalFolder, nid.Type);
        }

        [Fact]
        public void Nid_PredefinedValues()
        {
            Assert.Equal(0x21u, Nid.MessageStore.Value);
            Assert.Equal(0x61u, Nid.NameToIdMap.Value);
            Assert.Equal(0x122u, Nid.RootFolder.Value);
        }

        [Fact]
        public void Nid_IndexOverflowThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new Nid(NidType.Ltp, Nid.MaxIndex + 1));
        }

        [Fact]
        public void Bid_DataAndInternalFlag()
        {
            var data = new Bid(10, isInternal: false);
            Assert.False(data.IsInternal);
            Assert.Equal(10u, (uint)data.Index);
            Assert.Equal(40u, (uint)data.Value); // 10 << 2

            var internalBid = new Bid(10, isInternal: true);
            Assert.True(internalBid.IsInternal);
            Assert.Equal(10u, (uint)internalBid.Index);
            Assert.Equal(0x2u, internalBid.Value & Bid.InternalMask);
        }

        [Fact]
        public void Bid_ReservedBitNotSet()
        {
            var bid = new Bid(0x3FFF, isInternal: true);
            Assert.Equal(0u, bid.Value & Bid.ReservedMask);
        }

        [Fact]
        public void Bref_HoldsBidAndOffset()
        {
            var bref = new Bref(new Bid(5, true), 0x4400);
            Assert.Equal(new Bid(5, true), bref.Bid);
            Assert.Equal(0x4400u, (uint)bref.Ib);
        }
    }
}
