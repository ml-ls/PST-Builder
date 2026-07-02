using System;
using System.Collections.Generic;
using PstBuilder.Foundation;
using PstBuilder.Ndb;
using Xunit;

namespace PstBuilder.Tests.Ndb
{
    /// <summary>
    /// A minimal, strict NDB reader used only by tests to validate writer output in-process
    /// (validation-in-parallel). It re-parses the header, walks the NBT/BBT, and verifies every
    /// signature and CRC, mirroring what any conformant PST reader does structurally.
    /// </summary>
    internal sealed class NdbRoundTripReader
    {
        private readonly byte[] _file;

        public NdbRoundTripReader(byte[] file) => _file = file;

        public ulong IbFileEof { get; private set; }
        public ulong IbAMapLast { get; private set; }
        public Bref BrefNbt { get; private set; }
        public Bref BrefBbt { get; private set; }
        public readonly Dictionary<uint, NbtLeafEntry> Nodes = new Dictionary<uint, NbtLeafEntry>();
        public readonly Dictionary<ulong, BbtLeafEntry> Blocks = new Dictionary<ulong, BbtLeafEntry>();

        public void ReadAndValidate()
        {
            ValidateHeader();
            WalkBbt(BrefBbt);
            WalkNbt(BrefNbt);
            ValidateBlocks();
            ValidateAMap();
        }

        private void ValidateHeader()
        {
            Assert.Equal(PstConstants.Magic, BitConverter.ToUInt32(_file, 0));
            Assert.Equal(PstConstants.MagicClient, BitConverter.ToUInt16(_file, 8));
            Assert.True(BitConverter.ToUInt16(_file, 10) >= 23, "wVer must be Unicode (>=23).");
            Assert.True(_file[513] == 0x00 || _file[513] == 0x01, "crypt method must be none or permute"); // 0=ours, 1=Outlook default
            Assert.Equal(0x80, _file[512]);

            uint storedPartial = BitConverter.ToUInt32(_file, 4);
            Assert.Equal(storedPartial, Crc.Compute(_file.AsSpan(8, 471)));
            uint storedFull = BitConverter.ToUInt32(_file, 524);
            Assert.Equal(storedFull, Crc.Compute(_file.AsSpan(8, 516)));

            int root = 180;
            IbFileEof = BitConverter.ToUInt64(_file, root + 4);
            IbAMapLast = BitConverter.ToUInt64(_file, root + 12);
            Assert.Equal((ulong)_file.Length, IbFileEof);

            BrefNbt = ReadBref(root + 36);
            BrefBbt = ReadBref(root + 52);
            Assert.Equal(0x02, _file[root + 68]); // fAMapValid = VALID_AMAP2
        }

        private Bref ReadBref(int off) =>
            new Bref(new Bid(BitConverter.ToUInt64(_file, off)), BitConverter.ToUInt64(_file, off + 8));

        private void ValidatePageTrailer(long ib, PageType expectedType)
        {
            // Pages must sit on a 512-byte boundary; Outlook rejects a misaligned page ib.
            Assert.True(ib > 0 && ib % PstConstants.PageSize == 0,
                $"Page ib 0x{ib:X} is misaligned (must be a multiple of {PstConstants.PageSize}).");
            int p = (int)ib;
            int t = p + NdbFormat.PageTrailerOffset;
            Assert.Equal((byte)expectedType, _file[t]);
            Assert.Equal((byte)expectedType, _file[t + 1]);
            ulong bid = BitConverter.ToUInt64(_file, t + 8);

            uint storedCrc = BitConverter.ToUInt32(_file, t + 4);
            Assert.Equal(storedCrc, Crc.Compute(_file.AsSpan(p, NdbFormat.PageTrailerOffset)));

            ushort storedSig = BitConverter.ToUInt16(_file, t + 2);
            bool zeroSig = expectedType == PageType.AMap || expectedType == PageType.PMap
                || expectedType == PageType.FMap || expectedType == PageType.FPMap;
            Assert.Equal(zeroSig ? (ushort)0 : Signature.Compute((ulong)ib, bid), storedSig);
        }

        private void WalkBbt(Bref bref)
        {
            ValidatePageTrailer((long)bref.Ib, PageType.Bbt);
            int p = (int)bref.Ib;
            int cEnt = _file[p + 488];
            int cbEnt = _file[p + 490];
            int cLevel = _file[p + 491];
            for (int i = 0; i < cEnt; i++)
            {
                int e = p + i * cbEnt;
                if (cLevel == 0)
                {
                    var bid = new Bid(BitConverter.ToUInt64(_file, e));
                    ulong ib = BitConverter.ToUInt64(_file, e + 8);
                    ushort cb = BitConverter.ToUInt16(_file, e + 16);
                    ushort cRef = BitConverter.ToUInt16(_file, e + 18);
                    Blocks[bid.Value] = new BbtLeafEntry(bid, ib, cb, cRef);
                }
                else
                {
                    WalkBbt(ReadBref(e + 8)); // BTENTRY: btkey(8) then BREF
                }
            }
        }

        private void WalkNbt(Bref bref)
        {
            ValidatePageTrailer((long)bref.Ib, PageType.Nbt);
            int p = (int)bref.Ib;
            int cEnt = _file[p + 488];
            int cbEnt = _file[p + 490];
            int cLevel = _file[p + 491];
            for (int i = 0; i < cEnt; i++)
            {
                int e = p + i * cbEnt;
                if (cLevel == 0)
                {
                    var nid = new Nid((uint)BitConverter.ToUInt64(_file, e));
                    var bidData = new Bid(BitConverter.ToUInt64(_file, e + 8));
                    var bidSub = new Bid(BitConverter.ToUInt64(_file, e + 16));
                    var nidParent = new Nid(BitConverter.ToUInt32(_file, e + 24));
                    Nodes[nid.Value] = new NbtLeafEntry(nid, bidData, bidSub, nidParent);
                }
                else
                {
                    WalkNbt(ReadBref(e + 8));
                }
            }
        }

        /// <summary>Returns the payload (cb bytes, trailer/padding stripped) of a block by BID, decoding
        /// permute-encoded leaf data blocks so callers see plaintext. Internal blocks (XBLOCK/SLBLOCK) are
        /// never encoded.</summary>
        public byte[] GetBlockData(Bid bid)
        {
            var e = Blocks[bid.Value];
            var data = new byte[e.Cb];
            Array.Copy(_file, (int)e.Ib, data, 0, e.Cb);
            if (_file[513] == 0x01 && !bid.IsInternal) // NDB_CRYPT_PERMUTE, data block
                PermuteCipher.Decode(data, 0, e.Cb);
            return data;
        }

        private void ValidateBlocks()
        {
            foreach (var entry in Blocks.Values)
            {
                int p = (int)entry.Ib;
                int total = NdbFormat.TotalBlockSize(entry.Cb);
                int t = p + total - PstConstants.BlockTrailerSize;
                Assert.Equal(entry.Cb, BitConverter.ToUInt16(_file, t));          // cb
                Assert.Equal(Signature.Compute(entry.Ib, entry.Bid.Value), BitConverter.ToUInt16(_file, t + 2));
                Assert.Equal(Crc.Compute(_file.AsSpan(p, entry.Cb)), BitConverter.ToUInt32(_file, t + 4));
                Assert.Equal(entry.Bid.Value, BitConverter.ToUInt64(_file, t + 8)); // bid
            }
        }

        private void ValidateAMap()
        {
            // Validate every AMap page (one per 253,952-byte region up to EOF) and that it maps itself.
            int regionIndex = 0;
            for (long amapIb = PstConstants.FirstAMapOffset; amapIb < (long)IbFileEof;
                 amapIb += PstConstants.AMapSpan, regionIndex++)
            {
                ValidatePageTrailer(amapIb, PageType.AMap);
                ulong bid = BitConverter.ToUInt64(_file, (int)amapIb + NdbFormat.PageTrailerOffset + 8);
                Assert.Equal((ulong)amapIb, bid); // AMap bid == ib
                AssertAllocated(amapIb); // the AMap maps itself

                // Every eighth region carries a PMap page immediately after its AMap (MS-PST 2.2.2.7.3).
                if (regionIndex % PstConstants.AMapsPerPMap == 0)
                {
                    long pmapIb = amapIb + PstConstants.PageSize;
                    ValidatePageTrailer(pmapIb, PageType.PMap);
                    ulong pbid = BitConverter.ToUInt64(_file, (int)pmapIb + NdbFormat.PageTrailerOffset + 8);
                    Assert.Equal((ulong)pmapIb, pbid); // PMap bid == ib
                    AssertAllocated(pmapIb);            // AMap marks the PMap page allocated
                }

                // From region 128, every 496th region carries an FMap page after the PMap (MS-PST 2.2.2.7.4).
                if (regionIndex >= PstConstants.FirstFMapRegion &&
                    (regionIndex - PstConstants.FirstFMapRegion) % PstConstants.AMapsPerFMap == 0)
                {
                    long fmapIb = amapIb + 2 * PstConstants.PageSize; // after AMap + PMap
                    ValidatePageTrailer(fmapIb, PageType.FMap);
                    ulong fbid = BitConverter.ToUInt64(_file, (int)fmapIb + NdbFormat.PageTrailerOffset + 8);
                    Assert.Equal((ulong)fmapIb, fbid); // FMap bid == ib
                    AssertAllocated(fmapIb);            // AMap marks the FMap page allocated
                    Assert.Equal(PstConstants.FirstFMapOffset, fmapIb - regionIndex / PstConstants.AMapsPerFMap * PstConstants.FMapSpan);

                    // Each FMap byte must equal the covered AMap's free-slot count (min 255) — the exact
                    // check scanpst enforces ("AMap page @X has csFree of <byte>, but should have <actual>").
                    for (int i = 0; i < PstConstants.AMapDataBytes; i++)
                    {
                        long coveredAmapIb = amapIb + (long)i * PstConstants.AMapSpan;
                        if (coveredAmapIb >= (long)IbFileEof) break;
                        int expected = Math.Min(CountFreeSlots(coveredAmapIb), 255);
                        int stored = _file[(int)fmapIb + i];
                        Assert.True(stored == expected,
                            $"FMap @0x{fmapIb:X} byte {i} = {stored}, but AMap @0x{coveredAmapIb:X} has {expected} free slots.");
                    }
                }
            }

            // Every allocated block/page offset must have its bit set in its own region's AMap.
            foreach (var entry in Blocks.Values)
                AssertAllocated((long)entry.Ib);
            AssertAllocated((long)BrefNbt.Ib);
            AssertAllocated((long)BrefBbt.Ib);
        }

        // Counts the free (zero) bits in an AMap page's 496-byte bitmap = free 64-byte slots in its region.
        private int CountFreeSlots(long amapIb)
        {
            int free = 0;
            for (int b = 0; b < PstConstants.AMapDataBytes; b++)
            {
                byte mapByte = _file[(int)amapIb + b];
                for (int bit = 0; bit < 8; bit++)
                    if ((mapByte & (0x80 >> bit)) == 0) free++;
            }
            return free;
        }

        private void AssertAllocated(long offset)
        {
            long regionIndex = (offset - PstConstants.FirstAMapOffset) / PstConstants.AMapSpan;
            long amapIb = PstConstants.FirstAMapOffset + regionIndex * PstConstants.AMapSpan;
            long unit = (offset - amapIb) / PstConstants.BlockAlignment;
            int b = (int)(unit / 8);
            int bit = (int)(unit % 8);
            byte mapByte = _file[(int)amapIb + b];
            Assert.True((mapByte & (0x80 >> bit)) != 0, $"AMap bit for offset 0x{offset:X} (region @0x{amapIb:X}) not set.");
        }
    }
}
