using System;
using System.Collections.Generic;
using PstBuilder.Foundation;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: a scratch pad you stick little items onto and find again by a sticker number (HID).
    /// Builds a Heap-on-Node (HN). MS-PST 2.3.1. The heap may span multiple data blocks: block 0 begins
    /// with an HNHDR, blocks 1+ with an HNPAGEHDR, and blocks 8/136/… with an HNBITMAPHDR. Each block
    /// carries its own HNPAGEMAP. Items are addressed by <see cref="Hid"/> (heap index + block index)
    /// and MUST be ≤ 3580 bytes; larger values belong in a subnode. <see cref="Build"/> returns one
    /// payload per block, which the caller stages directly (single block) or as a data-tree (multiple).
    /// </summary>
    public sealed class HeapOnNodeBuilder
    {
        /// <summary>HNHDR block signature.</summary>
        public const byte HnSignature = 0xEC;
        /// <summary>bClientSig for a Property Context.</summary>
        public const byte ClientSigPc = 0xBC;
        /// <summary>bClientSig for a Table Context.</summary>
        public const byte ClientSigTc = 0x7C;
        /// <summary>bClientSig for a bare BTree-on-Heap.</summary>
        public const byte ClientSigBth = 0xB5;

        /// <summary>Maximum size of a single heap allocation (MS-PST 2.3.1 / "Allocating from the HN").</summary>
        public const int MaxItemSize = 3580;

        private const int HnHdrSize = 12;
        private const int HnPageHdrSize = 2;
        private const int HnBitmapHdrSize = 66; // ibHnpm (2) + rgbFillLevel (64)
        private const int BlockPayloadMax = PstConstants.MaxBlockDataSize; // 8176

        private sealed class BlockState
        {
            public readonly List<byte[]> Items = new List<byte[]>();
            public int ItemBytes;
        }

        private readonly byte _clientSig;
        private readonly List<BlockState> _blocks = new List<BlockState> { new BlockState() };

        /// <summary>Creates a heap builder for the given higher-level client type.</summary>
        public HeapOnNodeBuilder(byte clientSig) => _clientSig = clientSig;

        /// <summary>The HID designated as the heap's user root (HNHDR.hidUserRoot).</summary>
        public Hid UserRoot { get; set; } = Hid.Null;

        /// <summary>True when the heap currently spans more than one block.</summary>
        public bool IsMultiBlock => _blocks.Count > 1;

        /// <summary>Appends a heap item and returns its HID (placed in the first block with room).</summary>
        public Hid Add(byte[] item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (item.Length == 0) throw new ArgumentException("Heap items must be non-empty.", nameof(item));
            if (item.Length > MaxItemSize)
                throw new ArgumentException($"Heap item {item.Length} exceeds max {MaxItemSize}; use a subnode.", nameof(item));

            int blockIndex = _blocks.Count - 1;
            if (!Fits(_blocks[blockIndex], blockIndex, item.Length))
            {
                _blocks.Add(new BlockState());
                blockIndex = _blocks.Count - 1;
            }

            var block = _blocks[blockIndex];
            block.Items.Add(item);
            block.ItemBytes += item.Length;
            return new Hid(block.Items.Count, blockIndex); // hidIndex is 1-based within the block
        }

        private static bool Fits(BlockState block, int blockIndex, int itemLen)
        {
            int count = block.Items.Count + 1;
            int header = HeaderSize(blockIndex);
            int pageMap = 4 + 2 * (count + 1);
            // Reserve a few bytes so a non-final block always has room for a "filler" allocation that pads
            // it to exactly full (see BuildBlock). +1 possible align, +4 slack (filler entry + a byte).
            int total = header + block.ItemBytes + itemLen + 1 + pageMap + 6;
            return total <= BlockPayloadMax;
        }

        private static int HeaderSize(int blockIndex)
        {
            if (blockIndex == 0) return HnHdrSize;
            if (blockIndex >= 8 && (blockIndex - 8) % 128 == 0) return HnBitmapHdrSize;
            return HnPageHdrSize;
        }

        /// <summary>Serializes the HN, returning one raw block payload per data block.</summary>
        public IReadOnlyList<byte[]> Build()
        {
            var result = new List<byte[]>(_blocks.Count);
            for (int bi = 0; bi < _blocks.Count; bi++)
                result.Add(BuildBlock(bi, isFinal: bi == _blocks.Count - 1));
            return result;
        }

        private byte[] BuildBlock(int blockIndex, bool isFinal)
        {
            var block = _blocks[blockIndex];
            int header = HeaderSize(blockIndex);
            int cReal = block.Items.Count;

            int itemsEnd = header;
            for (int i = 0; i < cReal; i++) itemsEnd += block.Items[i].Length;

            // A multi-block heap becomes a data tree; every non-final data block MUST be exactly full
            // (MaxBlockDataSize), AND the HNPAGEMAP MUST sit immediately after the last allocation (no gap,
            // or a client reports "last alloc doesn't point to front of page map"). We satisfy both with a
            // filler allocation that extends the last item to the point where the page map begins.
            int fillerLen = 0;
            if (!isFinal)
            {
                int mapSize = 4 + 2 * (cReal + 2);          // page map including the filler's entry
                int target = BlockPayloadMax - mapSize;     // where the filler ends == ibHnpm
                if (target - itemsEnd >= 1) fillerLen = target - itemsEnd;
            }
            int cAlloc = cReal + (fillerLen > 0 ? 1 : 0);

            var rgibAlloc = new int[cAlloc + 1];
            int cursor = header;
            for (int i = 0; i < cReal; i++) { rgibAlloc[i] = cursor; cursor += block.Items[i].Length; }
            if (fillerLen > 0) { rgibAlloc[cReal] = cursor; cursor += fillerLen; } // filler = zero bytes
            rgibAlloc[cAlloc] = cursor;

            int ibHnpm = cursor + (cursor % 2);
            int rawLength = ibHnpm + 4 + 2 * (cAlloc + 1);
            var buffer = new byte[rawLength];
            var w = new SpanWriter(buffer);

            // Block header.
            w.WriteUInt16((ushort)ibHnpm);
            if (blockIndex == 0)
            {
                w.WriteByte(HnSignature);
                w.WriteByte(_clientSig);
                w.WriteUInt32(UserRoot.Value);
                w.WriteUInt32(0); // rgbFillLevel (hint only)
            }
            else if (header == HnBitmapHdrSize)
            {
                for (int i = 0; i < 64; i++) w.WriteByte(0); // rgbFillLevel (hint only)
            }

            // Real items (the filler allocation is left as zero bytes).
            for (int i = 0; i < cReal; i++)
            {
                w.Seek(rgibAlloc[i]);
                w.WriteBytes(block.Items[i]);
            }

            // HNPAGEMAP.
            w.Seek(ibHnpm);
            w.WriteUInt16((ushort)cAlloc);
            w.WriteUInt16(0); // cFree
            for (int i = 0; i <= cAlloc; i++) w.WriteUInt16((ushort)rgibAlloc[i]);
            return buffer;
        }
    }
}
