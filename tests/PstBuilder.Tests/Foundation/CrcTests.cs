using System;
using PstBuilder.Foundation;
using Xunit;

namespace PstBuilder.Tests.Foundation
{
    public class CrcTests
    {
        // Independent, trivially-correct bitwise reference: reflected CRC-32, poly 0xEDB88320,
        // seed 0, no final inversion. This is what MS-PST 5.3 specifies. Validating the
        // table-driven implementation against this catches any transcription error in the table.
        private static uint BitwiseReference(ReadOnlySpan<byte> data)
        {
            uint crc = 0;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return crc;
        }

        [Fact]
        public void EmptyInput_IsZero()
        {
            Assert.Equal(0u, Crc.Compute(ReadOnlySpan<byte>.Empty));
        }

        [Fact]
        public void SingleByte_EqualsTableEntry()
        {
            // CRC of a single byte b with seed 0 is exactly Table[b].
            for (int b = 0; b < 256; b++)
                Assert.Equal(Crc.Table[b], Crc.Compute(new[] { (byte)b }));
        }

        [Fact]
        public void KnownStandardTableEntries()
        {
            // Well-known reflected CRC-32 table anchors (poly 0xEDB88320).
            Assert.Equal(0x00000000u, Crc.Table[0]);
            Assert.Equal(0x77073096u, Crc.Table[1]);
            Assert.Equal(0x2D02EF8Du, Crc.Table[255]);
        }

        [Fact]
        public void MatchesBitwiseReference_AcrossLengths()
        {
            var rng = new Random(12345);
            foreach (int len in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 63, 64, 100, 496, 8176 })
            {
                var buf = new byte[len];
                rng.NextBytes(buf);
                Assert.Equal(BitwiseReference(buf), Crc.Compute(buf));
            }
        }
    }
}
