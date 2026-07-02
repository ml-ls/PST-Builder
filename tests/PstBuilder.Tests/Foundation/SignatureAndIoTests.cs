using System;
using System.IO;
using PstBuilder.Foundation;
using Xunit;

namespace PstBuilder.Tests.Foundation
{
    public class SignatureAndIoTests
    {
        [Fact]
        public void Signature_FoldsIbXorBid()
        {
            // x = ib ^ bid = 0x4404; high16 (0) ^ low16 (0x4404) = 0x4404.
            Assert.Equal((ushort)0x4404, Signature.Compute(0x4400UL, 0x4UL));
        }

        [Fact]
        public void Signature_HighBitsContribute()
        {
            // x = 0x00012345 ^ 0 = 0x00012345; (x>>16)=1, (x&0xFFFF)=0x2345; 1 ^ 0x2345 = 0x2344.
            Assert.Equal((ushort)0x2344, Signature.Compute(0x00012345UL, 0UL));
        }

        [Fact]
        public void SpanWriter_WritesLittleEndian()
        {
            Span<byte> buf = stackalloc byte[8];
            var w = new SpanWriter(buf);
            w.WriteUInt32(0x11223344);
            w.WriteUInt16(0xAABB);
            w.WriteByte(0xCC);
            Assert.Equal(7, w.Position);
            Assert.Equal(new byte[] { 0x44, 0x33, 0x22, 0x11, 0xBB, 0xAA, 0xCC, 0x00 }, buf.ToArray());
        }

        [Fact]
        public void SpanWriter_WritesBrefBidThenIb()
        {
            Span<byte> buf = stackalloc byte[16];
            var w = new SpanWriter(buf);
            w.WriteBref(new Bref(new Bid(0x0102030405060708UL), 0x1112131415161718UL));
            Assert.Equal(new byte[]
            {
                0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01,
                0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
            }, buf.ToArray());
        }

        [Fact]
        public void OutputStream_ReservesHeaderAndAppends()
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            {
                os.ReserveHeader(PstConstants.HeaderSize);
                Assert.Equal(PstConstants.HeaderSize, os.Position);

                long at = os.Append(new byte[] { 1, 2, 3, 4 });
                Assert.Equal(PstConstants.HeaderSize, at);
                Assert.Equal(PstConstants.HeaderSize + 4, os.Position);
            }
            var bytes = ms.ToArray();
            Assert.Equal(PstConstants.HeaderSize + 4, bytes.Length);
            Assert.Equal(1, bytes[PstConstants.HeaderSize]);
        }

        [Fact]
        public void OutputStream_AlignTo_PadsWithZeros()
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            {
                os.ReserveHeader(PstConstants.HeaderSize);
                os.Append(new byte[] { 0xFF });
                long aligned = os.AlignTo(PstConstants.BlockAlignment);
                Assert.Equal(0, aligned % PstConstants.BlockAlignment);
            }
        }

        [Fact]
        public void OutputStream_PatchAt_RewritesHeaderRegion()
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            {
                os.ReserveHeader(16);
                os.Append(new byte[] { 9, 9, 9, 9 });
                os.PatchAt(0, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            }
            var bytes = ms.ToArray();
            Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, bytes[..4]);
            Assert.Equal(9, bytes[16]); // appended data untouched
        }
    }
}
