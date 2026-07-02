using System;
using System.Collections.Generic;
using System.Linq;
using PstBuilder.Foundation;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: makes a spreadsheet (rows and columns), e.g. the list of all emails in a folder.
    /// Builds a Table Context (TC). MS-PST 2.3.4. A TC is a TCINFO header + column descriptors + a
    /// Row ID BTH + a Row Matrix, all layered on a Heap-on-Node. For the small tables the milestone
    /// needs, the Row Matrix is stored in a heap allocation (<c>hnidRows</c> = HID); larger tables
    /// requiring a data-tree/subnode Row Matrix are a deferred enhancement.
    /// </summary>
    public sealed class TableContextBuilder
    {
        private const byte TcType = 0x7C;
        private const byte BthType = 0xB5;

        private int _nextSubnodeIndex = 1;

        /// <summary>PidTagLtpRowId property tag (Integer32) — the required first column / row key.</summary>
        public const uint TagLtpRowId = 0x67F20003;
        /// <summary>PidTagLtpRowVer property tag (Integer32) — the required second column.</summary>
        public const uint TagLtpRowVer = 0x67F30003;

        private sealed class Column
        {
            public uint Tag;
            public PropertyType Type;
            public byte CbData;
            public byte IBit;
            public ushort IbData;
        }

        private sealed class Row
        {
            public uint RowId;
            public int RowIndex;
            public readonly Dictionary<uint, byte[]> Cells = new Dictionary<uint, byte[]>();
        }

        private readonly List<Column> _columns = new List<Column>();
        private readonly List<Row> _rows = new List<Row>();

        /// <summary>Creates a TC with the two mandatory columns (PidTagLtpRowId, PidTagLtpRowVer).</summary>
        public TableContextBuilder()
        {
            _columns.Add(new Column { Tag = TagLtpRowId, Type = PropertyType.Integer32, CbData = 4, IBit = 0 });
            _columns.Add(new Column { Tag = TagLtpRowVer, Type = PropertyType.Integer32, CbData = 4, IBit = 1 });
        }

        /// <summary>Builds a property tag from a property id and type.</summary>
        public static uint Tag(ushort propId, PropertyType type) => ((uint)propId << 16) | (ushort)type;

        /// <summary>Adds a data column. iBit is assigned in addition order (after the two required columns).</summary>
        public TableContextBuilder AddColumn(ushort propId, PropertyType type)
        {
            byte cb = CellWidth(type);
            _columns.Add(new Column { Tag = Tag(propId, type), Type = type, CbData = cb, IBit = (byte)_columns.Count });
            return this;
        }

        /// <summary>
        /// Adds a row keyed by <paramref name="rowId"/> (its PidTagLtpRowId). <paramref name="cells"/> maps
        /// each column's property tag to its raw value bytes; omitted columns are marked absent in the CEB.
        /// </summary>
        public TableContextBuilder AddRow(uint rowId, IEnumerable<KeyValuePair<uint, byte[]>> cells)
        {
            var row = new Row { RowId = rowId, RowIndex = _rows.Count };
            foreach (var c in cells) row.Cells[c.Key] = c.Value ?? Array.Empty<byte>();
            _rows.Add(row);
            return this;
        }

        private static byte CellWidth(PropertyType type)
        {
            int size = PropertyTypes.FixedSize(type);
            return (byte)(size >= 1 && size <= 8 ? size : 4); // variable / >8 -> 4-byte HNID
        }

        /// <summary>
        /// Builds the TC. Cell values live in the (possibly multi-block) HN; a large row matrix spills
        /// to a row-aligned subnode. The Row ID BTH records currently must fit one heap item (~447 rows);
        /// beyond that a multi-level BTH is required (deferred).
        /// </summary>
        public LtpContent Build()
        {
            AssignIbData(out ushort[] rgib);
            int rowSize = rgib[3];
            int cebSize = (_columns.Count + 7) / 8;
            bool hasRows = _rows.Count > 0;

            var hn = new HeapOnNodeBuilder(HeapOnNodeBuilder.ClientSigTc);
            var subnodes = new List<LtpSubnode>();

            // Add variable-length cell values to the heap (large ones spill to subnodes), capturing HNIDs.
            var cellHnid = new Dictionary<(int row, uint tag), uint>();
            foreach (var row in _rows)
            {
                foreach (var col in _columns)
                {
                    int fixedSize = PropertyTypes.FixedSize(col.Type);
                    if (fixedSize >= 0 && fixedSize <= 8) continue; // inline in the row
                    if (!row.Cells.TryGetValue(col.Tag, out var data) || data.Length == 0) continue;
                    uint hnid;
                    if (data.Length > HeapOnNodeBuilder.MaxItemSize)
                    {
                        var subNid = new Nid(NidType.Ltp, (uint)_nextSubnodeIndex++);
                        subnodes.Add(new LtpSubnode(subNid, data));
                        hnid = subNid.Value;
                    }
                    else
                    {
                        hnid = hn.Add(data).Value;
                    }
                    cellHnid[(row.RowIndex, col.Tag)] = hnid;
                }
            }

            // Row matrix: heap when small, otherwise a row-aligned subnode.
            uint hnidRows = 0;
            RowMatrixSpill? rowMatrixSpill = null;
            if (hasRows)
            {
                byte[] rowMatrix = BuildRowMatrix(rowSize, cebSize, cellHnid);
                if (rowMatrix.Length <= HeapOnNodeBuilder.MaxItemSize)
                {
                    hnidRows = hn.Add(rowMatrix).Value;
                }
                else
                {
                    var rmNid = new Nid(NidType.Ltp, (uint)_nextSubnodeIndex++);
                    rowMatrixSpill = new RowMatrixSpill(rmNid, rowMatrix, rowSize);
                    hnidRows = rmNid.Value;
                }
            }

            // Row ID BTH (maps each row's dwRowID -> row index), packed multi-level when large.
            var (recordsHid, idxLevels) = BthBuilder.Build(hn, BuildRowIdRecords(), cbKey: 4, cbEnt: 4);
            Hid bthHeaderHid = hn.Add(BuildBthHeader(4, 4, idxLevels, recordsHid));

            // TCINFO references the Row ID BTH and the row matrix; it is the heap's user root.
            hn.UserRoot = hn.Add(BuildTcInfo(rgib, bthHeaderHid, hnidRows));

            return new LtpContent(hn.Build(), subnodes, rowMatrixSpill);
        }

        private void AssignIbData(out ushort[] rgib)
        {
            // Required columns are fixed at the start of the 4-byte group.
            int cursor = 8; // LtpRowId@0, LtpRowVer@4
            _columns[0].IbData = 0;
            _columns[1].IbData = 4;

            var others = _columns.Skip(2).ToList();
            foreach (var c in others.Where(c => c.CbData == 8)) { c.IbData = (ushort)cursor; cursor += 8; }
            foreach (var c in others.Where(c => c.CbData == 4)) { c.IbData = (ushort)cursor; cursor += 4; }
            int end4 = cursor;
            foreach (var c in others.Where(c => c.CbData == 2)) { c.IbData = (ushort)cursor; cursor += 2; }
            int end2 = cursor;
            foreach (var c in others.Where(c => c.CbData == 1)) { c.IbData = (ushort)cursor; cursor += 1; }
            int end1 = cursor;
            int cebSize = (_columns.Count + 7) / 8;
            rgib = new ushort[] { (ushort)end4, (ushort)end2, (ushort)end1, (ushort)(end1 + cebSize) };
        }

        private byte[] BuildRowMatrix(int rowSize, int cebSize, Dictionary<(int, uint), uint> cellHnid)
        {
            var buffer = new byte[rowSize * _rows.Count];
            foreach (var row in _rows)
            {
                int baseOff = row.RowIndex * rowSize;
                var w = new SpanWriter(buffer.AsSpan(baseOff, rowSize));
                // dwRowID at offset 0 (also the LtpRowId cell).
                w.Seek(0); w.WriteUInt32(row.RowId);
                w.Seek(4); w.WriteUInt32((uint)row.RowIndex); // LtpRowVer carries a row version; row index is fine

                int cebOff = rowSize - cebSize;
                foreach (var col in _columns)
                {
                    bool present = col.IBit <= 1 || row.Cells.ContainsKey(col.Tag);
                    if (col.IBit > 1 && present)
                        WriteCell(buffer, baseOff + col.IbData, col, row, cellHnid);
                    if (present)
                        buffer[baseOff + cebOff + (col.IBit / 8)] |= (byte)(1 << (7 - (col.IBit % 8)));
                }
            }
            return buffer;
        }

        private static void WriteCell(byte[] buffer, int offset, Column col, Row row, Dictionary<(int, uint), uint> cellHnid)
        {
            row.Cells.TryGetValue(col.Tag, out var data);
            data ??= Array.Empty<byte>();
            int fixedSize = PropertyTypes.FixedSize(col.Type);
            if (fixedSize >= 0 && fixedSize <= 8)
            {
                int n = Math.Min(data.Length, col.CbData);
                Array.Copy(data, 0, buffer, offset, n); // remaining bytes stay zero
            }
            else
            {
                uint hnid = cellHnid.TryGetValue((row.RowIndex, col.Tag), out var v) ? v : 0u;
                new SpanWriter(buffer.AsSpan(offset, 4)).WriteUInt32(hnid);
            }
        }

        private List<byte[]> BuildRowIdRecords()
        {
            var ordered = _rows.OrderBy(r => r.RowId).ToList();
            var records = new List<byte[]>(ordered.Count);
            foreach (var r in ordered)
            {
                var rec = new byte[8];
                var w = new SpanWriter(rec);
                w.WriteUInt32(r.RowId);
                w.WriteUInt32((uint)r.RowIndex);
                records.Add(rec);
            }
            return records;
        }

        private static byte[] BuildBthHeader(byte cbKey, byte cbEnt, byte bIdxLevels, Hid hidRoot)
        {
            var buffer = new byte[8];
            var w = new SpanWriter(buffer);
            w.WriteByte(BthType);
            w.WriteByte(cbKey);
            w.WriteByte(cbEnt);
            w.WriteByte(bIdxLevels);
            w.WriteUInt32(hidRoot.Value);
            return buffer;
        }

        private byte[] BuildTcInfo(ushort[] rgib, Hid hidRowIndex, uint hnidRows)
        {
            var sortedCols = _columns.OrderBy(c => c.Tag).ToList();
            var buffer = new byte[22 + sortedCols.Count * 8];
            var w = new SpanWriter(buffer);
            w.WriteByte(TcType);
            w.WriteByte((byte)_columns.Count);
            for (int i = 0; i < 4; i++) w.WriteUInt16(rgib[i]);
            w.WriteUInt32(hidRowIndex.Value);
            w.WriteUInt32(hnidRows);
            w.WriteUInt32(0); // hidIndex (deprecated)
            foreach (var c in sortedCols)
            {
                w.WriteUInt32(c.Tag);
                w.WriteUInt16(c.IbData);
                w.WriteByte(c.CbData);
                w.WriteByte(c.IBit);
            }
            return buffer;
        }
    }
}
