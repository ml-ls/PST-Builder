using System;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: one fixed-size box of bytes, with a checksum stamp added on the end.
    /// A block staged for output: raw payload plus its assigned <see cref="Bid"/>. The on-disk image
    /// (payload + alignment padding + BLOCKTRAILER) is produced at finalisation by
    /// <see cref="BlockSerializer"/>; the file offset (IB) is assigned during the layout pass.
    /// </summary>
    public sealed class Block
    {
        /// <summary>The block identifier (internal bit set for XBLOCK/XXBLOCK/SLBLOCK/SIBLOCK).</summary>
        public Bid Bid { get; }

        /// <summary>The raw payload bytes (excludes trailer/padding). Length is the BBTENTRY <c>cb</c>.</summary>
        public byte[] Data { get; }

        /// <summary>Reference count to record in the BBT for this block.</summary>
        public ushort RefCount { get; }

        /// <summary>Total on-disk size including trailer and 64-byte alignment padding.</summary>
        public int TotalSize => NdbFormat.TotalBlockSize(Data.Length);

        /// <summary>Absolute file offset assigned during layout. -1 until assigned.</summary>
        public long Ib { get; internal set; } = -1;

        /// <summary>Creates a staged block.</summary>
        public Block(Bid bid, byte[] data, ushort refCount = NdbFormat.DefaultRefCount)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length > PstConstants.MaxBlockDataSize)
                throw new ArgumentException($"Block payload {data.Length} exceeds max {PstConstants.MaxBlockDataSize}.", nameof(data));
            Bid = bid;
            Data = data;
            RefCount = refCount;
        }
    }

    /// <summary>
    /// Serializes a <see cref="Block"/> to its on-disk image: payload, padding, then the 16-byte
    /// BLOCKTRAILER (cb, wSig, dwCRC, bid). MS-PST 2.2.2.8.1.
    /// </summary>
    public static class BlockSerializer
    {
        /// <summary>Writes the full on-disk block image (length == <see cref="Block.TotalSize"/>).</summary>
        public static byte[] Serialize(Block block)
        {
            if (block.Ib < 0) throw new InvalidOperationException("Block IB must be assigned before serialization.");
            int total = block.TotalSize;
            var buffer = new byte[total];

            // Payload at the start; padding between payload and trailer is left zero (CRC excludes it).
            Array.Copy(block.Data, 0, buffer, 0, block.Data.Length);

            ushort cb = (ushort)block.Data.Length;
            ushort sig = Signature.Compute((ulong)block.Ib, block.Bid.Value);
            uint crc = Crc.Compute(block.Data); // over the cb raw bytes only

            var writer = new SpanWriter(buffer.AsSpan(total - PstConstants.BlockTrailerSize));
            writer.WriteUInt16(cb);
            writer.WriteUInt16(sig);
            writer.WriteUInt32(crc);
            writer.WriteBid(block.Bid);
            return buffer;
        }
    }
}
