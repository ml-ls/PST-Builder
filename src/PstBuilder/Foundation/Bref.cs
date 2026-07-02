using System;

namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: a signpost — "the block/page with this ID lives at this spot in the file."
    /// Block reference (BREF). MS-PST 2.2.2.4 (Unicode form, 16 bytes): an 8-byte <see cref="Bid"/>
    /// followed by an 8-byte absolute file offset (IB). Used by NBT/BBT entries and header roots
    /// to locate a block or page on disk.
    /// </summary>
    public readonly struct Bref : IEquatable<Bref>
    {
        /// <summary>Serialized size in bytes (Unicode).</summary>
        public const int Size = 16;

        /// <summary>The block identifier.</summary>
        public Bid Bid { get; }

        /// <summary>The absolute byte offset (IB) of the referenced block/page in the file.</summary>
        public ulong Ib { get; }

        /// <summary>Creates a BREF from a BID and a file offset.</summary>
        public Bref(Bid bid, ulong ib)
        {
            Bid = bid;
            Ib = ib;
        }

        /// <inheritdoc/>
        public bool Equals(Bref other) => Bid == other.Bid && Ib == other.Ib;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Bref other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => unchecked((Bid.GetHashCode() * 397) ^ Ib.GetHashCode());

        /// <summary>Equality operator.</summary>
        public static bool operator ==(Bref a, Bref b) => a.Equals(b);

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(Bref a, Bref b) => !a.Equals(b);

        /// <inheritdoc/>
        public override string ToString() => $"BREF({Bid}, ib=0x{Ib:X})";
    }
}
