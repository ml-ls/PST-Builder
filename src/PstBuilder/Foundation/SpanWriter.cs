using System;
using System.Buffers.Binary;
using System.Text;

namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: writes numbers into a buffer in the exact order and byte-direction the PST expects.
    /// Sequential little-endian writer over a destination <see cref="Span{T}"/>. All PST on-disk
    /// integers are little-endian; this centralizes that and tracks a cursor so structure layout
    /// reads top-to-bottom. Bounds are enforced by the underlying span slices.
    /// </summary>
    public ref struct SpanWriter
    {
        private readonly Span<byte> _buffer;
        private int _pos;

        /// <summary>Creates a writer over <paramref name="buffer"/> starting at offset 0.</summary>
        public SpanWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _pos = 0;
        }

        /// <summary>Current write position (bytes from the start of the buffer).</summary>
        public int Position => _pos;

        /// <summary>Remaining capacity in bytes.</summary>
        public int Remaining => _buffer.Length - _pos;

        /// <summary>Moves the cursor to an absolute offset within the buffer.</summary>
        public void Seek(int position)
        {
            if ((uint)position > (uint)_buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(position));
            _pos = position;
        }

        /// <summary>Advances the cursor by <paramref name="count"/> bytes, leaving them unchanged.</summary>
        public void Skip(int count)
        {
            if (count < 0 || _pos + count > _buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            _pos += count;
        }

        /// <summary>Writes a single byte.</summary>
        public void WriteByte(byte value) => _buffer[_pos++] = value;

        /// <summary>Writes a little-endian unsigned 16-bit integer.</summary>
        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_pos), value);
            _pos += sizeof(ushort);
        }

        /// <summary>Writes a little-endian unsigned 32-bit integer.</summary>
        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_pos), value);
            _pos += sizeof(uint);
        }

        /// <summary>Writes a little-endian signed 32-bit integer.</summary>
        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_pos), value);
            _pos += sizeof(int);
        }

        /// <summary>Writes a little-endian unsigned 64-bit integer.</summary>
        public void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_pos), value);
            _pos += sizeof(ulong);
        }

        /// <summary>Writes a little-endian signed 64-bit integer.</summary>
        public void WriteInt64(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_pos), value);
            _pos += sizeof(long);
        }

        /// <summary>Writes a raw byte sequence.</summary>
        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_buffer.Slice(_pos));
            _pos += bytes.Length;
        }

        /// <summary>Writes a NID as its 32-bit on-disk value.</summary>
        public void WriteNid(Nid nid) => WriteUInt32(nid.Value);

        /// <summary>Writes a BID as its 64-bit on-disk value.</summary>
        public void WriteBid(Bid bid) => WriteUInt64(bid.Value);

        /// <summary>Writes a BREF: 8-byte BID then 8-byte IB.</summary>
        public void WriteBref(Bref bref)
        {
            WriteBid(bref.Bid);
            WriteUInt64(bref.Ib);
        }
    }
}
