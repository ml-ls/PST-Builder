using System;
using PstBuilder.Foundation;
using Xunit;

namespace PstBuilder.Tests.Ndb
{
    /// <summary>
    /// Validates the CRC implementation and header field offsets against the real MS-PST sample header
    /// (spec section 2.6.4 "Sample Header"). The stored dwCRCPartial/dwCRCFull are authoritative test
    /// vectors: dwCRCPartial covers 471 bytes from offset 8; dwCRCFull covers 516 bytes from offset 8.
    /// </summary>
    public class GoldenHeaderCrcTests
    {
        // The 564-byte sample header, transcribed from the spec's binary dump.
        private static byte[] BuildSampleHeader()
        {
            string[] rows0 =
            {
                "21 42 44 4E 0E A9 9A 37 53 4D 17 00 13 00 01 01",
                "5C 07 00 00 D0 7B 99 0B 04 00 00 00 01 00 00 00",
                "54 02 00 00 00 00 00 00 45 00 00 00 00 04 00 00",
                "00 04 00 00 04 04 00 00 00 40 00 00 02 00 01 00",
                "04 04 00 00 00 04 00 00 00 04 00 00 00 80 00 00",
                "00 04 00 00 00 04 00 00 00 04 00 00 00 04 00 00",
                "04 04 00 00 04 04 00 00 04 04 00 00 00 04 00 00",
                "00 04 00 00 00 04 00 00 00 04 00 00 00 04 00 00",
                "00 04 00 00 00 04 00 00 00 04 00 00 00 04 00 00",
                "00 04 00 00 00 04 00 00 00 04 00 00 00 04 00 00",
                "00 04 00 00 00 04 00 00 0F 04 00 00 00 00 00 00",
                "00 00 00 00 00 00 00 00 00 24 9F 00 00 00 00 00",
                "00 44 9B 00 00 00 00 00 40 F2 12 00 00 00 00 00",
                "00 00 00 00 00 00 00 00 4B 02 00 00 00 00 00 00",
                "00 52 90 00 00 00 00 00 53 02 00 00 00 00 00 00",
                "00 0A 90 00 00 00 00 00 02 00 00 00 00 00 00 00",
            };
            string[] rows2 =
            {
                "80 01 00 00 34 14 00 00 00 00 00 00 D6 83 D2 1F",
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
                "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00",
                "00 00 00 00",
            };

            var bytes = new byte[PstConstants.HeaderSize];
            int pos = 0;
            foreach (var row in rows0) pos = AppendHex(bytes, pos, row);   // 0x000..0x0FF
            for (int i = 0; i < 256; i++) bytes[pos++] = 0xFF;             // 0x100..0x1FF (rgbFM/rgbFP)
            foreach (var row in rows2) pos = AppendHex(bytes, pos, row);   // 0x200..0x233
            Assert.Equal(PstConstants.HeaderSize, pos);
            return bytes;
        }

        private static int AppendHex(byte[] dest, int pos, string row)
        {
            foreach (var tok in row.Split(' '))
                dest[pos++] = Convert.ToByte(tok, 16);
            return pos;
        }

        [Fact]
        public void DwCrcPartial_MatchesSpecVector()
        {
            var h = BuildSampleHeader();
            uint expected = 0x379AA90E; // bytes at offset 4
            Assert.Equal(expected, Crc.Compute(h.AsSpan(8, 471)));
        }

        [Fact]
        public void DwCrcFull_MatchesSpecVector()
        {
            var h = BuildSampleHeader();
            uint expected = 0x1FD283D6; // bytes at offset 524
            Assert.Equal(expected, Crc.Compute(h.AsSpan(8, 516)));
        }

        [Fact]
        public void SampleHeader_FieldsParseAtExpectedOffsets()
        {
            var h = BuildSampleHeader();
            Assert.Equal(PstConstants.Magic, BitConverter.ToUInt32(h, 0));
            Assert.Equal(PstConstants.MagicClient, BitConverter.ToUInt16(h, 8));
            Assert.Equal(PstConstants.VersionUnicode, BitConverter.ToUInt16(h, 10));
            Assert.Equal(0x80, h[512]);                                  // bSentinel
            Assert.Equal(0x1434UL, BitConverter.ToUInt64(h, 516));       // bidNextB
            Assert.Equal(0x02, h[180 + 68]);                            // ROOT.fAMapValid
        }
    }
}
