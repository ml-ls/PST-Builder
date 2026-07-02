using System;
using System.Collections.Generic;
using System.Text;
using PstBuilder.Ltp;
using Xunit;

namespace PstBuilder.Tests.Ltp
{
    public class TableContextTests
    {
        /// <summary>Minimal in-test TC parser mirroring an LTP reader (heap-stored row matrix).</summary>
        private sealed class TcReader
        {
            private readonly byte[] _hn;
            private readonly int[] _rgib;
            public byte ClientSig { get; }
            public int CCols { get; }
            public ushort[] Rgib { get; } = new ushort[4];
            public List<(uint tag, ushort ibData, byte cbData, byte iBit)> Columns { get; } = new();
            public Hid HnidRows { get; }
            public int RowSize => Rgib[3];

            public TcReader(byte[] hn)
            {
                _hn = hn;
                Assert.Equal(0xEC, hn[2]);
                ClientSig = hn[3];
                var userRoot = new Hid(BitConverter.ToUInt32(hn, 4));

                ushort ibHnpm = BitConverter.ToUInt16(hn, 0);
                int cAlloc = BitConverter.ToUInt16(hn, ibHnpm);
                _rgib = new int[cAlloc + 1];
                for (int i = 0; i <= cAlloc; i++) _rgib[i] = BitConverter.ToUInt16(hn, ibHnpm + 4 + i * 2);

                var tci = Resolve(userRoot);
                Assert.Equal(0x7C, hn[tci.start]);          // bType TC
                CCols = hn[tci.start + 1];
                for (int i = 0; i < 4; i++) Rgib[i] = BitConverter.ToUInt16(hn, tci.start + 2 + i * 2);
                HnidRows = new Hid(BitConverter.ToUInt32(hn, tci.start + 14));

                int colBase = tci.start + 22;
                for (int i = 0; i < CCols; i++)
                {
                    int o = colBase + i * 8;
                    Columns.Add((BitConverter.ToUInt32(hn, o), BitConverter.ToUInt16(hn, o + 4), hn[o + 6], hn[o + 7]));
                }
            }

            public (int start, int len) Resolve(Hid hid)
            {
                int idx = hid.HidIndex;
                return (_rgib[idx - 1], _rgib[idx] - _rgib[idx - 1]);
            }

            public byte[] RowMatrix() => HnidRows.IsNull ? Array.Empty<byte>() : Slice(Resolve(HnidRows));

            public bool CellExists(int rowOff, byte iBit)
            {
                int cebOff = rowOff + RowSize - (CCols + 7) / 8;
                return (_hn[cebOff + iBit / 8] & (1 << (7 - iBit % 8))) != 0;
            }

            public byte[] Slice((int start, int len) s)
            {
                var a = new byte[s.len];
                Array.Copy(_hn, s.start, a, 0, s.len);
                return a;
            }
        }

        [Fact]
        public void EmptyTable_HasNoRowsButValidHeader()
        {
            byte[] hn = new TableContextBuilder()
                .AddColumn(0x3001, PropertyType.String) // PidTagDisplayName
                .Build().MainBlocks[0];

            var tc = new TcReader(hn);
            Assert.Equal(HeapOnNodeBuilder.ClientSigTc, tc.ClientSig);
            Assert.Equal(3, tc.CCols); // LtpRowId, LtpRowVer, DisplayName
            Assert.True(tc.HnidRows.IsNull);
            Assert.Empty(tc.RowMatrix());
        }

        [Fact]
        public void Table_WithRow_RoundTripsCells()
        {
            const ushort pidSubject = 0x0037;       // PtypString
            const ushort pidMessageFlags = 0x0E07;  // PtypInteger32

            uint tagSubject = TableContextBuilder.Tag(pidSubject, PropertyType.String);
            uint tagFlags = TableContextBuilder.Tag(pidMessageFlags, PropertyType.Integer32);

            byte[] hn = new TableContextBuilder()
                .AddColumn(pidSubject, PropertyType.String)
                .AddColumn(pidMessageFlags, PropertyType.Integer32)
                .AddRow(0x200004, new Dictionary<uint, byte[]>
                {
                    [tagSubject] = Encoding.Unicode.GetBytes("Hello"),
                    [tagFlags] = BitConverter.GetBytes(0x01),
                })
                .Build().MainBlocks[0];

            var tc = new TcReader(hn);
            Assert.Equal(4, tc.CCols);
            Assert.False(tc.HnidRows.IsNull);

            byte[] matrix = tc.RowMatrix();
            Assert.Equal(tc.RowSize, matrix.Length); // exactly one row

            // dwRowID is the first 4 bytes of the row.
            Assert.Equal(0x200004u, BitConverter.ToUInt32(matrix, 0));

            // Locate columns and read cells out of the single row (row offset 0 within the heap item).
            foreach (var col in tc.Columns)
            {
                Assert.True(tc.CellExists(tc.Resolve(tc.HnidRows).start, col.iBit));
                if (col.tag == tagFlags)
                {
                    int v = BitConverter.ToInt32(_hnAt(tc, col.ibData), 0);
                    Assert.Equal(1, v);
                }
                else if (col.tag == tagSubject)
                {
                    var cellHid = new Hid(BitConverter.ToUInt32(_hnAt(tc, col.ibData), 0));
                    Assert.True(Hnid.IsHid(cellHid.Value));
                    string s = Encoding.Unicode.GetString(tc.Slice(tc.Resolve(cellHid)));
                    Assert.Equal("Hello", s);
                }
            }
        }

        // Reads 8 bytes of the (single) row at the given in-row offset.
        private static byte[] _hnAt(TcReader tc, int ibData)
        {
            int rowStart = tc.Resolve(tc.HnidRows).start;
            return tc.Slice((rowStart + ibData, 8));
        }
    }
}
