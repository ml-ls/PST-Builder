using System;

namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: a name tag for one box (block) of bytes.
    /// Block identifier (BID). MS-PST 2.2.2.2 (Unicode form, 64-bit). Bit layout from the LSB:
    /// bit 0 (r) reserved/0, bit 1 (i) internal flag, bits 2..63 the monotonically increasing
    /// <c>bidIndex</c>. Data block BIDs are even multiples (i = 0); internal block BIDs (XBLOCK,
    /// XXBLOCK, SLBLOCK, SIBLOCK) set i = 1. Stored little-endian on disk.
    /// </summary>
    public readonly struct Bid : IEquatable<Bid>
    {
        /// <summary>Reserved bit (bit 0).</summary>
        public const ulong ReservedMask = 0x1;

        /// <summary>Internal flag (bit 1).</summary>
        public const ulong InternalMask = 0x2;

        /// <summary>Mask for the index field (bits 2..63).</summary>
        public const ulong IndexShift = 2;

        /// <summary>The raw 64-bit value as stored on disk.</summary>
        public ulong Value { get; }

        /// <summary>Wraps an existing raw 64-bit BID value.</summary>
        public Bid(ulong value) => Value = value;

        /// <summary>Composes a BID from an index and the internal flag.</summary>
        public Bid(ulong index, bool isInternal)
        {
            if ((index << (int)IndexShift) >> (int)IndexShift != index)
                throw new ArgumentOutOfRangeException(nameof(index), index, "BID index exceeds 62 bits.");
            Value = (index << (int)IndexShift) | (isInternal ? InternalMask : 0UL);
        }

        /// <summary>True when this BID refers to an internal (NDB metadata) block.</summary>
        public bool IsInternal => (Value & InternalMask) != 0;

        /// <summary>The monotonically increasing index (bits 2..63).</summary>
        public ulong Index => Value >> (int)IndexShift;

        /// <inheritdoc/>
        public bool Equals(Bid other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Bid other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Value.GetHashCode();

        /// <summary>Equality operator.</summary>
        public static bool operator ==(Bid a, Bid b) => a.Value == b.Value;

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(Bid a, Bid b) => a.Value != b.Value;

        /// <inheritdoc/>
        public override string ToString() => $"BID(0x{Value:X16}, {(IsInternal ? "internal" : "data")}, idx={Index})";
    }
}
