using System;
using System.Collections.Generic;
using System.Linq;
using PstBuilder.Foundation;
using PstBuilder.Ltp;
using PstBuilder.Ndb;

namespace PstBuilder.Tests.Ndb
{
    /// <summary>
    /// A strict, independent LTP-layer reader used by tests to validate writer output the way a real
    /// client (Outlook) does: it walks every NBT node, reads its Heap-on-Node, dispatches by client
    /// signature (PC/TC/BTH), and resolves every HID/HNID, record count, and value length with full
    /// bounds-checking. Any structure that would make a client read an out-of-bounds or impossibly large
    /// buffer throws here, naming the offending node — turning Outlook's opaque "out of memory" into a
    /// precise location.
    /// </summary>
    internal sealed class LtpValidator
    {
        private readonly NdbRoundTripReader _ndb;

        public LtpValidator(byte[] file)
        {
            _ndb = new NdbRoundTripReader(file);
            _ndb.ReadAndValidate();
        }

        /// <summary>Dumps the resolved property values (PC) or row cells (TC) for one node, for diffing.</summary>
        public List<string> DumpNode(uint targetNid)
        {
            var outp = new List<string>();
            foreach (var node in _ndb.Nodes.Values)
            {
                if (node.Nid.Value != targetNid) continue;
                var subnodes = node.BidSub.Value != 0 ? ReadSubnodes(node.BidSub) : new Dictionary<uint, (Bid, Bid)>();
                var blocks = ReadDataLeaves(node.BidData);
                var b0 = blocks[0];
                byte clientSig = b0[3];
                uint userRoot = BitConverter.ToUInt32(b0, 4);
                var maps = new HnPageMap[blocks.Count];
                for (int i = 0; i < blocks.Count; i++) maps[i] = ParsePageMap("dump", blocks[i], i);

                if (clientSig != 0xBC && clientSig != 0x7C)
                {
                    // Non-HN node (proprietary search/state structure): dump full raw block bytes.
                    foreach (var blk in blocks)
                    {
                        var sb = new System.Text.StringBuilder($"  raw[{blk.Length}] ");
                        foreach (var by in blk) sb.Append(by.ToString("X2"));
                        outp.Add(sb.ToString());
                    }
                }
                else if (clientSig == 0xBC)
                {
                    var hdr = ResolveHid("dump", blocks, maps, new Hid(userRoot));
                    foreach (var r in CollectBthFrom("dump", blocks, maps, hdr))
                    {
                        ushort propId = BitConverter.ToUInt16(r, 0), type = BitConverter.ToUInt16(r, 2);
                        uint hnid = BitConverter.ToUInt32(r, 4);
                        byte[] val = ResolveVal(blocks, maps, subnodes, type, hnid, r);
                        outp.Add($"  {propId:X4}{type:X4} = {Hex(val)}");
                    }
                }
                else if (clientSig == 0x7C)
                {
                    var info = ResolveHid("dump", blocks, maps, new Hid(userRoot));
                    int cCols = info[1];
                    var cols = new (uint tag, ushort ib, byte cb, byte ibit)[cCols];
                    for (int c = 0; c < cCols; c++)
                    {
                        int o = 22 + c * 8;
                        cols[c] = (BitConverter.ToUInt32(info, o), BitConverter.ToUInt16(info, o + 4), info[o + 6], info[o + 7]);
                    }
                    int rowSize = BitConverter.ToUInt16(info, 8);
                    uint hnidRows = BitConverter.ToUInt32(info, 14);
                    byte[] matrix = hnidRows == 0 ? new byte[0]
                        : (hnidRows & 0x1F) == 0 ? ResolveHid("dump", blocks, maps, new Hid(hnidRows))
                        : ReadRowMatrix("dump", subnodes[hnidRows].Item1, rowSize);
                    int cebSize = (cCols + 7) / 8;
                    for (int rs = 0; rs + rowSize <= matrix.Length; rs += rowSize)
                    {
                        outp.Add($"  row @{rs / rowSize}:");
                        int cebOff = rs + rowSize - cebSize;
                        foreach (var col in cols)
                        {
                            bool present = (matrix[cebOff + col.ibit / 8] & (0x80 >> (col.ibit % 8))) != 0;
                            if (!present) continue;
                            var cell = new byte[col.cb];
                            Array.Copy(matrix, rs + col.ib, cell, 0, col.cb);
                            ushort type = (ushort)(col.tag & 0xFFFF);
                            // Resolve variable cells (string/binary): the 4-byte cell is an HNID into the TC heap.
                            if (FixedSize(type) < 0 && col.cb == 4)
                            {
                                uint hnid = BitConverter.ToUInt32(cell, 0);
                                cell = hnid == 0 ? new byte[0]
                                    : (hnid & 0x1F) == 0 ? ResolveHid("dump", blocks, maps, new Hid(hnid))
                                    : ReadDataValue("dump", subnodes[hnid].Item1);
                            }
                            outp.Add($"    {col.tag:X8} = {Hex(cell)}");
                        }
                    }
                }

                // Recurse one level into subnodes (recipient/attachment tables, attachment PCs) and dump
                // their properties with VALUES — needed to inspect e.g. attachment PR_ATTACH_SIZE (0x0E20).
                foreach (var kv in subnodes.OrderBy(k => k.Key))
                {
                    if ((kv.Key & 0x1F) == 0x1F) continue; // raw spilled value / row matrix
                    var (sData, sSub) = kv.Value;
                    if (sData.Value == 0) continue;
                    var sblocks = ReadDataLeaves(sData);
                    byte scs = sblocks[0][3];
                    var smaps = new HnPageMap[sblocks.Count];
                    for (int i = 0; i < sblocks.Count; i++) smaps[i] = ParsePageMap("dump", sblocks[i], i);
                    uint sroot = BitConverter.ToUInt32(sblocks[0], 4);
                    var snested = sSub.Value != 0 ? ReadSubnodes(sSub) : new Dictionary<uint, (Bid, Bid)>();
                    if (scs == 0xBC)
                    {
                        outp.Add($"  subnode 0x{kv.Key:X} PC:");
                        foreach (var r in CollectBthFrom("dump", sblocks, smaps, ResolveHid("dump", sblocks, smaps, new Hid(sroot))))
                        {
                            ushort pid = BitConverter.ToUInt16(r, 0), ty = BitConverter.ToUInt16(r, 2);
                            uint hnid = BitConverter.ToUInt32(r, 4);
                            outp.Add($"    {pid:X4}{ty:X4} = {Hex(ResolveVal(sblocks, smaps, snested, ty, hnid, r))}");
                        }
                    }
                    else if (scs == 0x7C)
                    {
                        // Dump TC subnode rows (recipient/attachment tables) so we can sum cell value sizes.
                        var info = ResolveHid("dump", sblocks, smaps, new Hid(sroot));
                        int cCols = info[1];
                        var cols = new (uint tag, ushort ib, byte cb, byte ibit)[cCols];
                        for (int c = 0; c < cCols; c++)
                        {
                            int o = 22 + c * 8;
                            cols[c] = (BitConverter.ToUInt32(info, o), BitConverter.ToUInt16(info, o + 4), info[o + 6], info[o + 7]);
                        }
                        int rowSize = BitConverter.ToUInt16(info, 8);
                        uint hnidRows = BitConverter.ToUInt32(info, 14);
                        byte[] matrix = hnidRows == 0 ? new byte[0]
                            : (hnidRows & 0x1F) == 0 ? ResolveHid("dump", sblocks, smaps, new Hid(hnidRows))
                            : ReadRowMatrix("dump", snested[hnidRows].Item1, rowSize);
                        int cebSize = (cCols + 7) / 8;
                        outp.Add($"  subnode 0x{kv.Key:X} TC ({matrix.Length / Math.Max(rowSize,1)} rows):");
                        for (int rs = 0; rs + rowSize <= matrix.Length; rs += rowSize)
                        {
                            int cebOff = rs + rowSize - cebSize;
                            outp.Add($"    row @{rs / rowSize}:");
                            foreach (var col in cols)
                            {
                                if ((matrix[cebOff + col.ibit / 8] & (0x80 >> (col.ibit % 8))) == 0) continue;
                                var cell = new byte[col.cb];
                                Array.Copy(matrix, rs + col.ib, cell, 0, col.cb);
                                ushort type = (ushort)(col.tag & 0xFFFF);
                                if (FixedSize(type) < 0 && col.cb == 4)
                                {
                                    uint hnid = BitConverter.ToUInt32(cell, 0);
                                    cell = hnid == 0 ? new byte[0]
                                        : (hnid & 0x1F) == 0 ? ResolveHid("dump", sblocks, smaps, new Hid(hnid))
                                        : ReadDataValue("dump", snested[hnid].Item1);
                                }
                                outp.Add($"      {col.tag:X8} = {Hex(cell)}");
                            }
                        }
                    }
                    else outp.Add($"  subnode 0x{kv.Key:X} clientSig 0x{scs:X2}");
                }
            }
            return outp;
        }

        private byte[] ResolveVal(IReadOnlyList<byte[]> blocks, HnPageMap[] maps,
            Dictionary<uint, (Bid data, Bid sub)> subnodes, ushort type, uint hnid, byte[] rec)
        {
            int fixedSize = FixedSize(type);
            if (fixedSize >= 0 && fixedSize <= 4) { var v = new byte[fixedSize]; Array.Copy(rec, 4, v, 0, fixedSize); return v; }
            if (hnid == 0) return new byte[0];
            if ((hnid & 0x1F) == 0) return ResolveHid("dump", blocks, maps, new Hid(hnid));
            return subnodes.TryGetValue(hnid, out var sn) ? ReadDataValue("dump", sn.data) : new byte[0];
        }

        private static string Hex(byte[] b)
        {
            int n = System.Math.Min(b.Length, 24);
            var sb = new System.Text.StringBuilder($"[{b.Length}] ");
            for (int i = 0; i < n; i++) sb.Append(b[i].ToString("X2"));
            return sb.ToString();
        }

        public void ValidateAll()
        {
            foreach (var node in _ndb.Nodes.Values)
            {
                var subnodes = node.BidSub.Value != 0 ? ReadSubnodes(node.BidSub) : new Dictionary<uint, (Bid, Bid)>();
                if (node.BidData.Value != 0)
                    ValidateHnObject($"NID 0x{node.Nid.Value:X}", node.BidData, subnodes);

                // Validate each subnode's own object too (recipient/attachment tables, attachment PCs).
                // Skip Ltp-type (0x1F) subnodes: those are raw spilled values / row matrices, not
                // Heap-on-Nodes — they are validated through the parent PC/TC's HNID resolution.
                foreach (var kv in subnodes)
                {
                    if ((kv.Key & 0x1F) == 0x1F) continue; // NID_TYPE_LTP raw data, not an HN object
                    var (bidData, bidSub) = kv.Value;
                    if (bidData.Value == 0) continue;
                    var nested = bidSub.Value != 0 ? ReadSubnodes(bidSub) : new Dictionary<uint, (Bid, Bid)>();
                    ValidateHnObject($"NID 0x{node.Nid.Value:X} subnode 0x{kv.Key:X}", bidData, nested);
                }
            }
        }

        /// <summary>Dumps a human-readable structural summary (all NIDs, store PC props, TC column schemas).</summary>
        public List<string> Dump()
        {
            var outp = new List<string>();
            foreach (var node in _ndb.Nodes.Values.OrderBy(n => n.Nid.Value))
            {
                string line = $"NID 0x{node.Nid.Value:X6} (type 0x{node.Nid.Value & 0x1F:X2})";
                if (node.BidData.Value == 0) { outp.Add(line + " [no data]"); continue; }
                try
                {
                    var blocks = ReadDataLeaves(node.BidData);
                    var b0 = blocks[0];
                    byte clientSig = b0.Length >= 4 ? b0[3] : (byte)0;
                    var maps = new HnPageMap[blocks.Count];
                    for (int i = 0; i < blocks.Count; i++) maps[i] = ParsePageMap(line, blocks[i], i);
                    uint userRoot = BitConverter.ToUInt32(b0, 4);
                    if (clientSig == 0xBC)
                    {
                        var hdr = ResolveHid(line, blocks, maps, new Hid(userRoot));
                        var recs = CollectBthFrom(line, blocks, maps, hdr);
                        var props = recs.Select(r => $"{BitConverter.ToUInt16(r,0):X4}{BitConverter.ToUInt16(r,2):X4}");
                        outp.Add($"{line} PC props: {string.Join(" ", props)}");
                    }
                    else if (clientSig == 0x7C)
                    {
                        var info = ResolveHid(line, blocks, maps, new Hid(userRoot));
                        int cCols = info[1];
                        var cols = new List<string>();
                        for (int c = 0; c < cCols; c++)
                            cols.Add($"{BitConverter.ToUInt32(info, 22 + c * 8):X8}");
                        outp.Add($"{line} TC cols: {string.Join(" ", cols)}");
                    }
                    else outp.Add($"{line} clientSig 0x{clientSig:X2}");
                }
                catch (Exception ex) { outp.Add($"{line} [parse error: {ex.Message}]"); }
            }
            return outp;
        }

        // ---- object dispatch ------------------------------------------------------------------

        private void ValidateHnObject(string who, Bid dataBid, Dictionary<uint, (Bid data, Bid sub)> subnodes)
        {
            var blocks = ReadDataLeaves(dataBid);
            if (blocks.Count == 0) throw new InvalidOperationException($"{who}: data tree has no blocks.");

            // A multi-block heap is a data tree; every non-final block MUST be exactly full, else a client
            // reports "Middle page not full" and rejects the file.
            for (int i = 0; i < blocks.Count - 1; i++)
                if (blocks[i].Length != PstConstants.MaxBlockDataSize)
                    throw new InvalidOperationException(
                        $"{who}: HN data-tree block {i} is {blocks[i].Length} bytes, not full " +
                        $"({PstConstants.MaxBlockDataSize}) — middle page not full.");

            var b0 = blocks[0];
            Need(who, b0, 12, "HNHDR");
            byte bSig = b0[2];
            if (bSig != 0xEC) throw new InvalidOperationException($"{who}: HNHDR bSig 0x{bSig:X2} != 0xEC.");
            byte clientSig = b0[3];
            uint userRoot = BitConverter.ToUInt32(b0, 4);

            // Parse every block's HNPAGEMAP up front (also bounds-checks ibHnpm and rgibAlloc).
            var maps = new HnPageMap[blocks.Count];
            for (int i = 0; i < blocks.Count; i++) maps[i] = ParsePageMap(who, blocks[i], i);

            switch (clientSig)
            {
                case 0xBC: ValidatePc(who, blocks, maps, userRoot, subnodes); break;
                case 0x7C: ValidateTc(who, blocks, maps, userRoot, subnodes); break;
                case 0xB5: CollectBth(who, blocks, maps, new Hid(userRoot), out _); break; // bare BTH
                default: throw new InvalidOperationException($"{who}: unknown bClientSig 0x{clientSig:X2}.");
            }
        }

        // ---- Property Context -----------------------------------------------------------------

        private void ValidatePc(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps, uint userRoot,
            Dictionary<uint, (Bid data, Bid sub)> subnodes)
        {
            var header = ResolveHid(who, blocks, maps, new Hid(userRoot));
            if (header.Length != 8) throw new InvalidOperationException($"{who}: PC BTHHEADER must be 8 bytes, got {header.Length}.");
            if (header[0] != 0xB5) throw new InvalidOperationException($"{who}: PC BTHHEADER bType 0x{header[0]:X2} != 0xB5.");
            byte cbKey = header[1], cbEnt = header[2];
            if (cbKey != 2 || cbEnt != 6) throw new InvalidOperationException($"{who}: PC BTH cbKey/cbEnt = {cbKey}/{cbEnt}, expected 2/6.");

            var leaves = CollectBthFrom(who, blocks, maps, header);
            foreach (var rec in leaves)
            {
                if (rec.Length != 8) throw new InvalidOperationException($"{who}: PC record size {rec.Length} != 8.");
                ushort propId = BitConverter.ToUInt16(rec, 0);
                ushort propType = BitConverter.ToUInt16(rec, 2);
                uint hnid = BitConverter.ToUInt32(rec, 4);
                ResolvePropValue(who, blocks, maps, subnodes, propId, propType, hnid);
            }
        }

        private void ResolvePropValue(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps,
            Dictionary<uint, (Bid data, Bid sub)> subnodes, ushort propId, ushort propType, uint hnid)
        {
            int fixedSize = FixedSize(propType);
            bool inline = fixedSize >= 0 && fixedSize <= 4;
            if (inline) return;               // value carried in the 4-byte slot
            if (hnid == 0) return;            // empty/absent
            if ((hnid & 0x1F) == 0)
            {
                // HID into the heap.
                ResolveHid($"{who} prop 0x{propId:X4}", blocks, maps, new Hid(hnid));
            }
            else
            {
                // Subnode NID — must exist in this node's subnode tree and resolve.
                if (!subnodes.TryGetValue(hnid, out var sn))
                    throw new InvalidOperationException($"{who}: prop 0x{propId:X4} references missing subnode 0x{hnid:X}.");
                ReadDataValue($"{who} prop 0x{propId:X4} subnode", sn.data);
            }
        }

        // ---- Table Context --------------------------------------------------------------------

        private void ValidateTc(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps, uint userRoot,
            Dictionary<uint, (Bid data, Bid sub)> subnodes)
        {
            var info = ResolveHid(who, blocks, maps, new Hid(userRoot));
            Need(who, info, 22, "TCINFO");
            if (info[0] != 0x7C) throw new InvalidOperationException($"{who}: TCINFO bType 0x{info[0]:X2} != 0x7C.");
            int cCols = info[1];
            if (info.Length < 22 + cCols * 8)
                throw new InvalidOperationException($"{who}: TCINFO too short for {cCols} columns ({info.Length} bytes).");

            var rgib = new int[4];
            for (int i = 0; i < 4; i++) rgib[i] = BitConverter.ToUInt16(info, 2 + i * 2);
            uint hidRowIndex = BitConverter.ToUInt32(info, 10);
            uint hnidRows = BitConverter.ToUInt32(info, 14);
            int rowSize = rgib[3];
            if (rowSize <= 0) throw new InvalidOperationException($"{who}: TC rowSize {rowSize} invalid.");

            // Column descriptors must point inside the row.
            for (int c = 0; c < cCols; c++)
            {
                int o = 22 + c * 8;
                ushort ibData = BitConverter.ToUInt16(info, o + 4);
                byte cbData = info[o + 6];
                if (ibData + cbData > rowSize)
                    throw new InvalidOperationException($"{who}: TC column {c} cell [{ibData}+{cbData}] exceeds rowSize {rowSize}.");
            }

            // Row ID BTH (maps dwRowID -> row index).
            int cRowBth = -1;
            if (hidRowIndex != 0)
            {
                var bthHeader = ResolveHid(who, blocks, maps, new Hid(hidRowIndex));
                if (bthHeader.Length != 8 || bthHeader[0] != 0xB5)
                    throw new InvalidOperationException($"{who}: TC Row ID BTHHEADER invalid.");
                cRowBth = CollectBthFrom(who, blocks, maps, bthHeader).Count;
            }

            // Row matrix: heap HID (single block, inline) or subnode (row-aligned across data-tree blocks).
            if (hnidRows == 0) return;
            byte[] matrix;
            if ((hnidRows & 0x1F) == 0)
                matrix = ResolveHid($"{who} rowmatrix", blocks, maps, new Hid(hnidRows));
            else
            {
                if (!subnodes.TryGetValue(hnidRows, out var sn))
                    throw new InvalidOperationException($"{who}: row matrix references missing subnode 0x{hnidRows:X}.");
                matrix = ReadRowMatrix($"{who} rowmatrix subnode", sn.data, rowSize);
            }
            if (matrix.Length % rowSize != 0)
                throw new InvalidOperationException(
                    $"{who}: row matrix {matrix.Length} bytes is not a multiple of rowSize {rowSize} " +
                    $"(rows would desync — client reads a bogus row).");

            // cRow (rows the reader recovers, row-aligned) must equal the Row ID BTH count — this is the
            // exact consistency scanpst enforces ("bad row count (cRow=.., cRowBTH=..)").
            int cRow = matrix.Length / rowSize;
            if (cRowBth >= 0 && cRow != cRowBth)
                throw new InvalidOperationException(
                    $"{who}: row matrix yields cRow={cRow} but Row ID BTH has cRowBTH={cRowBth} " +
                    $"(rows straddle block boundaries — reader desyncs).");
        }

        // Reconstructs a subnode row matrix row-aligned (MS-PST 2.3.4.4.1): each non-final block MUST be a
        // full 8176 bytes and contributes floor(8176/rowSize) whole rows (trailing pad ignored); the final
        // block contributes floor(len/rowSize) rows. Concatenating flat instead would count padding bytes.
        private byte[] ReadRowMatrix(string who, Bid bid, int rowSize)
        {
            var leaves = ReadDataLeaves(bid);
            int rowsPerBlock = PstConstants.MaxBlockDataSize / rowSize;
            var outp = new List<byte>();
            for (int i = 0; i < leaves.Count; i++)
            {
                bool isFinal = i == leaves.Count - 1;
                if (!isFinal && leaves[i].Length != PstConstants.MaxBlockDataSize)
                    throw new InvalidOperationException(
                        $"{who}: data-tree block {i} is {leaves[i].Length} bytes, not full " +
                        $"({PstConstants.MaxBlockDataSize}) — middle page not full.");
                int rowsHere = isFinal ? leaves[i].Length / rowSize : rowsPerBlock;
                outp.AddRange(new ArraySegment<byte>(leaves[i], 0, rowsHere * rowSize));
            }
            return outp.ToArray();
        }

        // ---- BTree-on-Heap --------------------------------------------------------------------

        private List<byte[]> CollectBthFrom(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps, byte[] header)
        {
            byte cbKey = header[1], cbEnt = header[2], idxLevels = header[3];
            uint hidRoot = BitConverter.ToUInt32(header, 4);
            var leaves = new List<byte[]>();
            if (hidRoot == 0) return leaves; // empty BTH
            CollectBth(who, blocks, maps, new Hid(hidRoot), cbKey, cbEnt, idxLevels, leaves);
            return leaves;
        }

        // bare-BTH entry (used when an object's client sig is 0xB5 directly).
        private void CollectBth(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps, Hid headerHid, out int n)
        {
            var header = ResolveHid(who, blocks, maps, headerHid);
            var leaves = CollectBthFrom(who, blocks, maps, header);
            n = leaves.Count;
        }

        private void CollectBth(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps, Hid hid,
            byte cbKey, byte cbEnt, byte level, List<byte[]> leaves)
        {
            byte[] item = ResolveHid(who, blocks, maps, hid);
            int recSize = level > 0 ? cbKey + 4 : cbKey + cbEnt;
            if (recSize <= 0) throw new InvalidOperationException($"{who}: BTH record size {recSize} invalid.");
            if (item.Length % recSize != 0)
                throw new InvalidOperationException($"{who}: BTH item {item.Length} bytes not a multiple of record size {recSize}.");
            int count = item.Length / recSize;
            for (int i = 0; i < count; i++)
            {
                if (level == 0)
                {
                    var rec = new byte[recSize];
                    Array.Copy(item, i * recSize, rec, 0, recSize);
                    leaves.Add(rec);
                }
                else
                {
                    uint childHid = BitConverter.ToUInt32(item, i * recSize + cbKey);
                    CollectBth(who, blocks, maps, new Hid(childHid), cbKey, cbEnt, (byte)(level - 1), leaves);
                }
            }
        }

        // ---- Heap-on-Node primitives ----------------------------------------------------------

        private readonly struct HnPageMap
        {
            public readonly int[] RgibAlloc; // length cAlloc+1
            public HnPageMap(int[] rgib) => RgibAlloc = rgib;
            public int Count => RgibAlloc.Length - 1;
        }

        private static HnPageMap ParsePageMap(string who, byte[] block, int blockIndex)
        {
            Need($"{who} block {blockIndex}", block, 2, "ibHnpm");
            int ibHnpm = BitConverter.ToUInt16(block, 0);
            if (ibHnpm + 4 > block.Length)
                throw new InvalidOperationException($"{who} block {blockIndex}: ibHnpm 0x{ibHnpm:X} past block end {block.Length}.");
            int cAlloc = BitConverter.ToUInt16(block, ibHnpm);
            int need = ibHnpm + 4 + 2 * (cAlloc + 1);
            if (need > block.Length)
                throw new InvalidOperationException($"{who} block {blockIndex}: HNPAGEMAP for {cAlloc} allocs needs {need} > block {block.Length}.");
            var rgib = new int[cAlloc + 1];
            for (int i = 0; i <= cAlloc; i++)
            {
                rgib[i] = BitConverter.ToUInt16(block, ibHnpm + 4 + i * 2);
                if (rgib[i] > block.Length)
                    throw new InvalidOperationException($"{who} block {blockIndex}: rgibAlloc[{i}]=0x{rgib[i]:X} past block end {block.Length}.");
                if (i > 0 && rgib[i] < rgib[i - 1])
                    throw new InvalidOperationException($"{who} block {blockIndex}: rgibAlloc not monotonic at {i}.");
            }
            return new HnPageMap(rgib);
        }

        private static byte[] ResolveHid(string who, IReadOnlyList<byte[]> blocks, HnPageMap[] maps, Hid hid)
        {
            if (hid.IsNull) throw new InvalidOperationException($"{who}: null HID where a value was expected.");
            if (hid.HidType != 0) throw new InvalidOperationException($"{who}: HID 0x{hid.Value:X} has nonzero type bits.");
            int bi = hid.HidBlockIndex;
            if (bi >= blocks.Count) throw new InvalidOperationException($"{who}: HID block {bi} >= {blocks.Count}.");
            var map = maps[bi];
            int idx = hid.HidIndex; // 1-based
            if (idx < 1 || idx > map.Count)
                throw new InvalidOperationException($"{who}: HID index {idx} out of range 1..{map.Count} (block {bi}).");
            int start = map.RgibAlloc[idx - 1], end = map.RgibAlloc[idx];
            var bytes = new byte[end - start];
            Array.Copy(blocks[bi], start, bytes, 0, end - start);
            return bytes;
        }

        // ---- block / subnode reassembly -------------------------------------------------------

        private List<byte[]> ReadDataLeaves(Bid bid)
        {
            if (!_ndb.Blocks.ContainsKey(bid.Value))
                throw new InvalidOperationException($"BID 0x{bid.Value:X} not present in BBT.");
            byte[] payload = _ndb.GetBlockData(bid);
            if (!bid.IsInternal) return new List<byte[]> { payload };

            // Internal: XBLOCK/XXBLOCK (btype 0x01). (SLBLOCK 0x02 is handled by ReadSubnodes.)
            if (payload.Length < 8 || payload[0] != 0x01)
                throw new InvalidOperationException($"BID 0x{bid.Value:X}: expected XBLOCK (btype 0x01) for a data tree.");
            ushort cEnt = BitConverter.ToUInt16(payload, 2);
            int need = 8 + cEnt * 8;
            if (need > payload.Length)
                throw new InvalidOperationException($"BID 0x{bid.Value:X}: XBLOCK with {cEnt} entries needs {need} > {payload.Length}.");
            var result = new List<byte[]>();
            for (int i = 0; i < cEnt; i++)
                result.AddRange(ReadDataLeaves(new Bid(BitConverter.ToUInt64(payload, 8 + i * 8))));
            return result;
        }

        private byte[] ReadDataValue(string who, Bid bid)
        {
            var leaves = ReadDataLeaves(bid);
            if (leaves.Count == 1) return leaves[0];
            // Every non-final block of a data tree (row matrix, spilled value, attachment) MUST be full.
            for (int i = 0; i < leaves.Count - 1; i++)
                if (leaves[i].Length != PstConstants.MaxBlockDataSize)
                    throw new InvalidOperationException(
                        $"{who}: data-tree block {i} is {leaves[i].Length} bytes, not full " +
                        $"({PstConstants.MaxBlockDataSize}) — middle page not full.");
            int total = leaves.Sum(l => l.Length);
            var all = new byte[total];
            int o = 0;
            foreach (var l in leaves) { Array.Copy(l, 0, all, o, l.Length); o += l.Length; }
            return all;
        }

        private Dictionary<uint, (Bid data, Bid sub)> ReadSubnodes(Bid subBid)
        {
            var map = new Dictionary<uint, (Bid, Bid)>();
            ReadSubnodesInto(subBid, map);
            return map;
        }

        private void ReadSubnodesInto(Bid subBid, Dictionary<uint, (Bid, Bid)> map)
        {
            byte[] p = _ndb.GetBlockData(subBid);
            if (p.Length < 8 || p[0] != 0x02)
                throw new InvalidOperationException($"BID 0x{subBid.Value:X}: expected subnode block (btype 0x02).");
            byte level = p[1];
            ushort cEnt = BitConverter.ToUInt16(p, 2);
            if (level == 0)
            {
                int need = 8 + cEnt * 24;
                if (need > p.Length) throw new InvalidOperationException($"SLBLOCK 0x{subBid.Value:X}: {cEnt} entries need {need} > {p.Length}.");
                for (int i = 0; i < cEnt; i++)
                {
                    int o = 8 + i * 24;
                    uint nid = (uint)BitConverter.ToUInt64(p, o);
                    var bidData = new Bid(BitConverter.ToUInt64(p, o + 8));
                    var bidSub = new Bid(BitConverter.ToUInt64(p, o + 16));
                    map[nid] = (bidData, bidSub);
                }
            }
            else
            {
                int need = 8 + cEnt * 16;
                if (need > p.Length) throw new InvalidOperationException($"SIBLOCK 0x{subBid.Value:X}: {cEnt} entries need {need} > {p.Length}.");
                for (int i = 0; i < cEnt; i++)
                    ReadSubnodesInto(new Bid(BitConverter.ToUInt64(p, 8 + i * 16 + 8)), map);
            }
        }

        // ---- helpers --------------------------------------------------------------------------

        private static void Need(string who, byte[] buf, int n, string what)
        {
            if (buf.Length < n) throw new InvalidOperationException($"{who}: {what} needs {n} bytes, have {buf.Length}.");
        }

        private static int FixedSize(ushort propType)
        {
            switch (propType)
            {
                case 0x0002: return 2; // I2
                case 0x0003: return 4; // I4
                case 0x0004: return 4; // R4
                case 0x000A: return 4; // Error
                case 0x000B: return 1; // Bool (stored in 4-byte slot, but <=4 -> inline)
                case 0x0005: return 8; // R8
                case 0x0006: return 8; // Currency
                case 0x0007: return 8; // AppTime
                case 0x0014: return 8; // I8
                case 0x0040: return 8; // SysTime
                case 0x0048: return 16; // Guid
                default: return -1;    // variable (String/Binary/etc.)
            }
        }
    }
}
