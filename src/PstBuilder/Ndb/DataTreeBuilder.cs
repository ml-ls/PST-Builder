using System;
using System.Collections.Generic;
using System.IO;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: when something is too big for one box, this splits it across several boxes and
    /// keeps a little index (an XBLOCK) listing them in order.
    /// Stores a logical data stream as one or more NDB blocks (MS-PST 2.2.2.8.3.2). Single-block values
    /// are written directly; larger ones are split across data blocks referenced by an XBLOCK, and very
    /// large ones use an XXBLOCK over multiple XBLOCKs. Returns the BID the node/subnode references.
    /// </summary>
    public static class DataTreeBuilder
    {
        private const int LeafData = PstConstants.MaxBlockDataSize; // 8176
        private const int MaxBidsPerXBlock = (PstConstants.MaxBlockDataSize - 8) / 8; // 1021

        /// <summary>Stages a contiguous byte stream, splitting into 8176-byte leaves as needed.</summary>
        public static Bid Build(NdbWriter ndb, byte[] data)
        {
            if (ndb == null) throw new ArgumentNullException(nameof(ndb));
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length <= LeafData) return ndb.AddBlock(data);

            var bids = new List<Bid>();
            var lens = new List<int>();
            for (int off = 0; off < data.Length; off += LeafData)
            {
                int len = Math.Min(LeafData, data.Length - off);
                var chunk = new byte[len];
                Array.Copy(data, off, chunk, 0, len);
                bids.Add(ndb.AddBlock(chunk));
                lens.Add(len);
            }
            return BuildTree(ndb, bids, lens);
        }

        /// <summary>
        /// Stages a byte stream of known length, reading it in 8176-byte leaves straight to disk — the full
        /// value is never held in memory (only one leaf at a time). Used for large attachment payloads
        /// supplied as a <see cref="System.IO.Stream"/>. <paramref name="open"/> is invoked once; the
        /// returned stream is read sequentially and disposed here. <paramref name="length"/> must equal the
        /// exact number of bytes the stream yields.
        /// </summary>
        public static Bid BuildFromStream(NdbWriter ndb, Func<Stream> open, long length)
        {
            if (ndb == null) throw new ArgumentNullException(nameof(ndb));
            if (open == null) throw new ArgumentNullException(nameof(open));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));

            using (var stream = open() ?? throw new InvalidOperationException("Attachment stream source returned null."))
            {
                if (length <= LeafData)
                {
                    var single = new byte[length];
                    ReadExact(stream, single, (int)length);
                    return ndb.AddBlock(single);
                }

                var bids = new List<Bid>();
                var lens = new List<int>();
                long remaining = length;
                while (remaining > 0)
                {
                    int len = (int)Math.Min(LeafData, remaining);
                    var chunk = new byte[len];
                    ReadExact(stream, chunk, len);
                    bids.Add(ndb.AddBlock(chunk));
                    lens.Add(len);
                    remaining -= len;
                }
                return BuildTree(ndb, bids, lens);
            }
        }

        // Reads exactly count bytes (streams may satisfy a read in several passes).
        private static void ReadExact(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException(
                    $"Attachment stream ended early: expected {count} bytes, got {read}. Check the declared length.");
                read += n;
            }
        }

        /// <summary>
        /// Stages a set of pre-formed block payloads (e.g. the blocks of a multi-block Heap-on-Node) and
        /// returns the BID to reference them by.
        /// </summary>
        public static Bid BuildFromBlocks(NdbWriter ndb, IReadOnlyList<byte[]> blocks)
        {
            if (ndb == null) throw new ArgumentNullException(nameof(ndb));
            if (blocks == null || blocks.Count == 0) throw new ArgumentException("No blocks.", nameof(blocks));
            if (blocks.Count == 1) return ndb.AddBlock(blocks[0]);

            var bids = new List<Bid>(blocks.Count);
            var lens = new List<int>(blocks.Count);
            foreach (var b in blocks)
            {
                if (b.Length > LeafData) throw new ArgumentException("HN block payload exceeds 8176 bytes.");
                bids.Add(ndb.AddBlock(b));
                lens.Add(b.Length);
            }
            return BuildTree(ndb, bids, lens);
        }

        /// <summary>
        /// Stages a Table Context row matrix as a row-aligned data-tree (MS-PST 2.3.4.4.1). A reader locates
        /// row N with the constant <c>rowsPerBlock = floor(8176 / rowSize)</c>: block = N / rowsPerBlock,
        /// offset = (N % rowsPerBlock) * rowSize. That only works if every block except the last holds
        /// exactly <c>rowsPerBlock</c> whole rows — and, because a non-final data-tree block must be a full
        /// 8176 bytes (or Outlook reports "middle page not full"), each such block is PADDED to 8176 after
        /// its rows. The trailing pad is ignored by the reader (it never indexes past rowsPerBlock in a
        /// block). The last block holds the remaining rows and is left short (unpadded). Writing this as a
        /// flat 8176-chunked stream instead makes rows straddle block boundaries and the reader desyncs
        /// (scanpst: "bad row count" + spurious "missing required column").
        /// </summary>
        public static Bid BuildRowMatrix(NdbWriter ndb, byte[] rows, int rowSize)
        {
            if (ndb == null) throw new ArgumentNullException(nameof(ndb));
            if (rowSize <= 0) throw new ArgumentOutOfRangeException(nameof(rowSize));
            if (rows.Length <= LeafData) return ndb.AddBlock(rows);

            int rowsPerBlock = LeafData / rowSize;
            int bytesPerBlock = rowsPerBlock * rowSize; // whole rows per non-final block
            var bids = new List<Bid>();
            var lens = new List<int>();
            for (int off = 0; off < rows.Length; off += bytesPerBlock)
            {
                int rowBytes = Math.Min(bytesPerBlock, rows.Length - off);
                bool isFinal = off + rowBytes >= rows.Length;
                // Non-final blocks are padded to a full 8176; the final block stays row-sized (short).
                int blockLen = isFinal ? rowBytes : LeafData;
                var chunk = new byte[blockLen];
                Array.Copy(rows, off, chunk, 0, rowBytes);
                bids.Add(ndb.AddBlock(chunk));
                lens.Add(blockLen);
            }
            return BuildTree(ndb, bids, lens);
        }

        private static Bid BuildTree(NdbWriter ndb, List<Bid> leafBids, List<int> leafLens)
        {
            long total = 0;
            foreach (int l in leafLens) total += l;

            if (leafBids.Count <= MaxBidsPerXBlock)
                return ndb.AddBlock(BuildXBlock(leafBids, (uint)total, level: 1), isInternal: true);

            var xbids = new List<Bid>();
            var xlens = new List<int>();
            for (int i = 0; i < leafBids.Count; i += MaxBidsPerXBlock)
            {
                int n = Math.Min(MaxBidsPerXBlock, leafBids.Count - i);
                var group = leafBids.GetRange(i, n);
                int glen = 0;
                for (int k = 0; k < n; k++) glen += leafLens[i + k];
                xbids.Add(ndb.AddBlock(BuildXBlock(group, (uint)glen, level: 1), isInternal: true));
                xlens.Add(glen);
            }
            if (xbids.Count > MaxBidsPerXBlock)
                throw new NotSupportedException("Value too large for a single XXBLOCK; deeper trees not implemented.");
            return ndb.AddBlock(BuildXBlock(xbids, (uint)total, level: 2), isInternal: true);
        }

        private static byte[] BuildXBlock(List<Bid> bids, uint lcbTotal, int level)
        {
            var buffer = new byte[8 + bids.Count * 8];
            var w = new SpanWriter(buffer);
            w.WriteByte(0x01);                  // btype
            w.WriteByte((byte)level);           // cLevel: 1 = XBLOCK, 2 = XXBLOCK
            w.WriteUInt16((ushort)bids.Count);  // cEnt
            w.WriteUInt32(lcbTotal);            // lcbTotal
            foreach (var b in bids) w.WriteBid(b);
            return buffer;
        }
    }
}
