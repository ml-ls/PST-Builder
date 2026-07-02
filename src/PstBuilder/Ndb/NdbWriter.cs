using System;
using System.Collections.Generic;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: puts data into fixed-size boxes and writes them out as they arrive, while keeping
    /// two "phone books" (NBT and BBT) and a shelf map so anything can be found again later.
    /// The NDB-layer writer. Blocks are <em>streamed</em> to the output as they are added — only the
    /// small per-block BBT leaf entries and per-node NBT leaf entries are retained in memory, so peak
    /// memory is bounded by the node/block count rather than the mailbox size. AMap pages are written at
    /// their fixed cadence (placeholder first, patched once the region's extent is known). <see cref="Finalize"/>
    /// packs and streams the NBT/BBT pages, patches the final AMap, and writes the header. Append-only.
    /// </summary>
    public sealed class NdbWriter
    {
        private const ulong FirstBlockBidRaw = 4; // advances by 4
        private const ulong FirstPageBid = 4;     // page counter advances by 1

        private readonly PstOutputStream _output;
        private readonly List<BbtLeafEntry> _bbtEntries = new List<BbtLeafEntry>();
        private readonly List<NbtLeafEntry> _nodes = new List<NbtLeafEntry>();
        private readonly Dictionary<uint, uint> _maxNidIndexByType = new Dictionary<uint, uint>();

        private ulong _nextBlockBidRaw = FirstBlockBidRaw;
        private ulong _nextPageBid = FirstPageBid;

        // AMap streaming state.
        private long _regionStart;
        private long _curAMapOffset;
        private long _cbAMapFree;
        private long _lastAMapIb;
        private int _regionIndex;
        private bool _finalized;

        // FMap patching state: each FMap page's file offset + the AMap region it starts covering, and the
        // per-region free-slot count (min(freeBytes/64, 255)) recorded as each AMap closes. FMap bytes are
        // filled in at Finalize because an FMap is written before the regions it covers exist.
        private readonly List<(long offset, int coverStart)> _fmaps = new List<(long, int)>();
        private readonly List<int> _regionFreeSlots = new List<int>();

        /// <summary>Begins a streaming write: reserves the header and the first region's map pages.</summary>
        public NdbWriter(PstOutputStream output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _output.ReserveHeader(PstConstants.HeaderSize);
            PadTo(PstConstants.FirstAMapOffset);          // 0x4400
            _regionIndex = 0;
            _regionStart = PstConstants.FirstAMapOffset;
            StartRegion();
        }

        // Writes the map pages that lead a region: the AMap placeholder (patched later); on every eighth
        // region the PMap page that must immediately follow that AMap (MS-PST 2.2.2.7.3); and, from region
        // 128 onward every 496 regions, the FMap page that follows the PMap (2.2.2.7.4). FMap regions are
        // always PMap regions (128 and 496 are multiples of 8), so the order is AMap, PMap, FMap.
        private void StartRegion()
        {
            if (_regionIndex >= PstConstants.MaxAMapRegions)
                throw new NotSupportedException(
                    $"PST exceeds {PstConstants.MaxAMapRegions} AMap regions (~485 MB); FPMap pages are " +
                    "required beyond this size and are not yet emitted. Split the export into smaller parts " +
                    "(see PstExportSession.CreateSplit).");

            _curAMapOffset = _regionStart;
            _lastAMapIb = _regionStart;
            WriteAMapPlaceholder();                       // patched at region close / Finalize
            if (_regionIndex % PstConstants.AMapsPerPMap == 0)
                _output.Append(AllocationMapBuilder.BuildPMapPage((ulong)_output.Position));
            if (_regionIndex >= PstConstants.FirstFMapRegion &&
                (_regionIndex - PstConstants.FirstFMapRegion) % PstConstants.AMapsPerFMap == 0)
            {
                long fmapOff = _output.Position;
                _fmaps.Add((fmapOff, _regionIndex));                    // patched with real free counts at Finalize
                _output.Append(AllocationMapBuilder.BuildFMapPage((ulong)fmapOff, null)); // placeholder (zeros)
            }
            if (_regionIndex >= PstConstants.FirstFPMapRegion &&
                (_regionIndex - PstConstants.FirstFPMapRegion) % PstConstants.AMapsPerFPMap == 0)
                _output.Append(AllocationMapBuilder.BuildFPMapPage((ulong)_output.Position));
        }

        /// <summary>Streams a block (assigning its BID) and returns the BID.</summary>
        public Bid AddBlock(byte[] data, bool isInternal = false, ushort refCount = NdbFormat.DefaultRefCount)
        {
            Bid bid = AllocateBlockBid(isInternal);
            var block = new Block(bid, data, refCount);
            block.Ib = ReserveSpace(block.TotalSize, PstConstants.BlockAlignment); // 64-byte-aligned block
            _output.Append(BlockSerializer.Serialize(block));
            _bbtEntries.Add(new BbtLeafEntry(bid, (ulong)block.Ib, (ushort)data.Length, refCount));
            return bid;
        }

        /// <summary>Records an NBT node and tracks the per-type max nidIndex for the header rgnid counters.</summary>
        public void AddNode(Nid nid, Bid bidData, Bid bidSub, Nid nidParent)
        {
            _nodes.Add(new NbtLeafEntry(nid, bidData, bidSub, nidParent));
            uint type = (uint)nid.Type;
            if (!_maxNidIndexByType.TryGetValue(type, out uint cur) || nid.Index > cur)
                _maxNidIndexByType[type] = nid.Index;
        }

        /// <summary>Packs the NBT/BBT, streams their pages, patches the final AMap, and writes the header.</summary>
        public PstHeader Finalize()
        {
            if (_finalized) throw new InvalidOperationException("Finalize has already been called.");
            _finalized = true;

            BTree bbt = BTreeBuilder.BuildBbt(_bbtEntries, AllocPageBid);
            BTree nbt = BTreeBuilder.BuildNbt(_nodes, AllocPageBid);
            foreach (var page in bbt.AllPages) StreamPage(page);
            foreach (var page in nbt.AllPages) StreamPage(page);

            PatchAMap(_curAMapOffset, _output.Position);   // final region (records its free count too)
            PatchFMaps();                                  // now every region's free count is known
            long fileEof = _regionStart + PstConstants.AMapSpan;
            PadTo(fileEof);

            var header = new PstHeader
            {
                BidNextB = _nextBlockBidRaw,
                BidNextP = _nextPageBid,
                IbFileEof = (ulong)fileEof,
                IbAMapLast = (ulong)_lastAMapIb,
                CbAMapFree = (ulong)_cbAMapFree,
                BrefNbt = new Bref(nbt.Root.Bid, (ulong)nbt.Root.Ib),
                BrefBbt = new Bref(bbt.Root.Bid, (ulong)bbt.Root.Ib),
                Rgnid = BuildRgnid(),
            };
            _output.PatchAt(0, HeaderWriter.Serialize(header));
            _output.Flush();
            return header;
        }

        private Bid AllocateBlockBid(bool isInternal)
        {
            ulong raw = _nextBlockBidRaw;
            _nextBlockBidRaw += 4;
            return new Bid(isInternal ? raw | Bid.InternalMask : raw);
        }

        private Bid AllocPageBid() => new Bid(_nextPageBid++);

        private void StreamPage(BTreePageNode page)
        {
            // Pages (BTPAGE/AMap) must be 512-byte aligned; Outlook rejects a page on a non-page boundary
            // ("Page has misaligned or zero ib"). Blocks are only 64-byte aligned, so a run of blocks
            // leaves the cursor off a page boundary — align up before writing the page.
            page.Ib = ReserveSpace(PstConstants.PageSize, PstConstants.PageSize);
            _output.Append(BTreePageSerializer.Serialize(page));
        }

        // Ensures there is room for an item of <paramref name="size"/> bytes, aligned up to
        // <paramref name="alignment"/>, before the next AMap boundary — inserting (and patching the
        // previous) AMap page as needed. Any alignment padding falls inside the region's allocated
        // extent, so the contiguous AMap mark-up still covers it. Returns the offset to write at.
        private long ReserveSpace(int size, int alignment)
        {
            long regionEnd = _regionStart + PstConstants.AMapSpan;
            long aligned = AlignUp(_output.Position, alignment);
            if (aligned + size > regionEnd)
            {
                PatchAMap(_curAMapOffset, _output.Position);   // close the current region
                PadTo(regionEnd);
                _regionStart = regionEnd;
                _regionIndex++;
                StartRegion();
                aligned = AlignUp(_output.Position, alignment);
            }
            PadTo(aligned);
            return aligned;
        }

        private static long AlignUp(long value, int alignment) =>
            (value + alignment - 1) / alignment * alignment;

        private void WriteAMapPlaceholder() => _output.Append(new byte[PstConstants.PageSize]);

        private void PatchAMap(long offset, long allocatedEnd)
        {
            _output.PatchAt(offset, AllocationMapBuilder.BuildAMapPage((ulong)offset, allocatedEnd));
            long free = AllocationMapBuilder.FreeBytes((ulong)offset, allocatedEnd);
            _cbAMapFree += free;
            // Record this region's free-slot count (capped to a byte) for the covering FMap page.
            _regionFreeSlots.Add((int)Math.Min(free / PstConstants.BlockAlignment, 255));
        }

        // Fills each FMap page's 496 bytes with the covered AMaps' free-slot counts (recorded as regions
        // closed) and patches the page in place. Called at Finalize once every region's free count is known.
        private void PatchFMaps()
        {
            foreach (var (offset, coverStart) in _fmaps)
            {
                var map = new byte[PstConstants.AMapDataBytes]; // 496 bytes, one per covered AMap
                for (int i = 0; i < map.Length; i++)
                {
                    int region = coverStart + i;
                    map[i] = region < _regionFreeSlots.Count ? (byte)_regionFreeSlots[region] : (byte)0;
                }
                _output.PatchAt(offset, AllocationMapBuilder.BuildFMapPage((ulong)offset, map));
            }
        }

        private void PadTo(long target)
        {
            long count = target - _output.Position;
            if (count < 0) throw new InvalidOperationException("Layout overran a boundary.");
            if (count > 0) _output.Append(new byte[count]);
        }

        private uint[] BuildRgnid()
        {
            var rgnid = PstHeader.CreateDefaultRgnid();
            foreach (var kv in _maxNidIndexByType)
                if (kv.Key < 32 && kv.Value + 1 > rgnid[kv.Key])
                    rgnid[kv.Key] = kv.Value + 1;
            return rgnid;
        }
    }
}
