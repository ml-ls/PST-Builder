using System;
using System.Collections.Generic;
using System.Text;
using PstBuilder.Ltp;
using Xunit;

namespace PstBuilder.Tests.Ltp
{
    public class PropertyContextTests
    {
        /// <summary>Minimal in-test parser for a single-block PC, mirroring a real LTP reader.</summary>
        private sealed class PcReader
        {
            private readonly byte[] _hn;
            private readonly int[] _rgibAlloc;
            public byte ClientSig { get; }
            public Hid UserRoot { get; }

            public PcReader(byte[] hn)
            {
                _hn = hn;
                ushort ibHnpm = BitConverter.ToUInt16(hn, 0);
                Assert.Equal(0xEC, hn[2]); // bSig
                ClientSig = hn[3];
                UserRoot = new Hid(BitConverter.ToUInt32(hn, 4));

                int cAlloc = BitConverter.ToUInt16(hn, ibHnpm);
                _rgibAlloc = new int[cAlloc + 1];
                for (int i = 0; i <= cAlloc; i++)
                    _rgibAlloc[i] = BitConverter.ToUInt16(hn, ibHnpm + 4 + i * 2);
            }

            public ArraySegment<byte> Resolve(Hid hid)
            {
                int idx = hid.HidIndex; // 1-based
                int start = _rgibAlloc[idx - 1];
                int end = _rgibAlloc[idx];
                return new ArraySegment<byte>(_hn, start, end - start);
            }

            public Dictionary<ushort, (PropertyType type, uint hnid)> ReadRecords()
            {
                var bth = Resolve(UserRoot);
                Assert.Equal(0xB5, _hn[bth.Offset]);       // bType
                Assert.Equal(2, _hn[bth.Offset + 1]);      // cbKey
                Assert.Equal(6, _hn[bth.Offset + 2]);      // cbEnt
                var hidRoot = new Hid(BitConverter.ToUInt32(_hn, bth.Offset + 4));

                var result = new Dictionary<ushort, (PropertyType, uint)>();
                if (hidRoot.IsNull) return result;
                var rec = Resolve(hidRoot);
                int count = rec.Count / 8;
                ushort prev = 0;
                for (int i = 0; i < count; i++)
                {
                    int o = rec.Offset + i * 8;
                    ushort propId = BitConverter.ToUInt16(_hn, o);
                    Assert.True(i == 0 || propId > prev, "PC records must be sorted by propId.");
                    prev = propId;
                    var type = (PropertyType)BitConverter.ToUInt16(_hn, o + 2);
                    uint hnid = BitConverter.ToUInt32(_hn, o + 4);
                    result[propId] = (type, hnid);
                }
                return result;
            }
        }

        [Fact]
        public void Pc_WithInlineAndHeapValues_RoundTrips()
        {
            const ushort pidContentCount = 0x3602; // PtypInteger32
            const ushort pidDisplayName = 0x3001;  // PtypString

            byte[] hn = new PropertyContextBuilder()
                .Add(Property.Int32(pidContentCount, 42))
                .Add(Property.Unicode(pidDisplayName, "Inbox"))
                .Build().MainBlocks[0];

            var reader = new PcReader(hn);
            Assert.Equal(HeapOnNodeBuilder.ClientSigPc, reader.ClientSig);

            var records = reader.ReadRecords();
            Assert.Equal(2, records.Count);

            // Inline integer: dwValueHnid holds the value directly.
            Assert.Equal(PropertyType.Integer32, records[pidContentCount].type);
            Assert.Equal(42u, records[pidContentCount].hnid);

            // Heap string: dwValueHnid is an HID into the heap.
            Assert.Equal(PropertyType.String, records[pidDisplayName].type);
            var strHid = new Hid(records[pidDisplayName].hnid);
            Assert.True(Hnid.IsHid(strHid.Value));
            var seg = reader.Resolve(strHid);
            string value = Encoding.Unicode.GetString(_seg(seg));
            Assert.Equal("Inbox", value);
        }

        [Fact]
        public void Pc_EmptyVariableProperty_UsesNullHid()
        {
            const ushort pidComment = 0x3004; // PtypString
            byte[] hn = new PropertyContextBuilder()
                .Add(Property.Unicode(pidComment, ""))
                .Build().MainBlocks[0];

            var reader = new PcReader(hn);
            var records = reader.ReadRecords();
            Assert.Equal(0u, records[pidComment].hnid); // null HID for empty value
        }

        private static byte[] _seg(ArraySegment<byte> s)
        {
            var a = new byte[s.Count];
            Array.Copy(s.Array!, s.Offset, a, 0, s.Count);
            return a;
        }
    }
}
