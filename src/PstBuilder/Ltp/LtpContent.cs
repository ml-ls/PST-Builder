using System;
using System.Collections.Generic;
using System.IO;
using PstBuilder.Foundation;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: a big thing that didn't fit on the scratch pad, so it lives in its own side-box —
    /// either handed over whole (a byte array) or poured in from a tap (a stream) so it's never all in
    /// memory at once.
    /// A value too large for the heap, spilled into the node's subnode tree. The bytes are either held
    /// directly (<see cref="Data"/>) or read on demand from a stream (<see cref="StreamSource"/>), so a
    /// large attachment never has to be materialised as a single array.
    /// </summary>
    public readonly struct LtpSubnode
    {
        /// <summary>Local subnode NID referenced by the HNID in the PC/TC.</summary>
        public Nid Nid { get; }
        /// <summary>The raw value bytes to store in the subnode; null when the value is streamed.</summary>
        public byte[]? Data { get; }
        /// <summary>When set, the value is read on demand from this stream instead of from <see cref="Data"/>.</summary>
        public Func<Stream>? StreamSource { get; }
        /// <summary>Exact byte length of a streamed value (ignored when <see cref="Data"/> is used).</summary>
        public long StreamLength { get; }

        /// <summary>Creates a spilled subnode value held in memory.</summary>
        public LtpSubnode(Nid nid, byte[] data)
        {
            Nid = nid;
            Data = data ?? throw new ArgumentNullException(nameof(data));
            StreamSource = null;
            StreamLength = 0;
        }

        /// <summary>Creates a spilled subnode value read on demand from a stream (never fully buffered).</summary>
        public LtpSubnode(Nid nid, Func<Stream> streamSource, long length)
        {
            Nid = nid;
            Data = null;
            StreamSource = streamSource ?? throw new ArgumentNullException(nameof(streamSource));
            StreamLength = length >= 0 ? length : throw new ArgumentOutOfRangeException(nameof(length));
        }
    }

    /// <summary>
    /// A Table Context row matrix spilled to a subnode. Unlike a generic subnode value this MUST be
    /// blocked on row boundaries (MS-PST 2.3.4.3.1): each data block holds an integral number of rows.
    /// </summary>
    public readonly struct RowMatrixSpill
    {
        /// <summary>Local subnode NID (= TCINFO.hnidRows).</summary>
        public Nid Nid { get; }
        /// <summary>The packed row bytes.</summary>
        public byte[] Rows { get; }
        /// <summary>Size of one row in bytes.</summary>
        public int RowSize { get; }

        /// <summary>Creates a row-matrix spill.</summary>
        public RowMatrixSpill(Nid nid, byte[] rows, int rowSize)
        {
            Nid = nid;
            Rows = rows;
            RowSize = rowSize;
        }
    }

    /// <summary>
    /// In plain words: the finished bag-or-table, ready to hand to the box writer — the main scratch-pad
    /// pages plus any oversized values that had to be parked in side-boxes.
    /// The output of an LTP builder: the Heap-on-Node block(s) plus any values spilled to the node's
    /// subnode tree. The caller stages <see cref="MainBlocks"/> as the node's data (single block, or a
    /// data-tree when the HN spans multiple blocks) and wires the subnodes via an SLBLOCK.
    /// </summary>
    public sealed class LtpContent
    {
        /// <summary>The HN block payloads (one entry = single block; more = a data-tree).</summary>
        public IReadOnlyList<byte[]> MainBlocks { get; }
        /// <summary>Generic byte-stream values stored in the node's subnode tree.</summary>
        public IReadOnlyList<LtpSubnode> Subnodes { get; }
        /// <summary>Optional row-matrix spill (TC only), blocked on row boundaries.</summary>
        public RowMatrixSpill? RowMatrix { get; }

        /// <summary>Creates LTP content.</summary>
        public LtpContent(IReadOnlyList<byte[]> mainBlocks, IReadOnlyList<LtpSubnode> subnodes, RowMatrixSpill? rowMatrix = null)
        {
            MainBlocks = mainBlocks ?? throw new ArgumentNullException(nameof(mainBlocks));
            Subnodes = subnodes ?? Array.Empty<LtpSubnode>();
            RowMatrix = rowMatrix;
        }
    }
}
