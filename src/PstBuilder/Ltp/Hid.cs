using System;
using PstBuilder.Foundation;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: a sticker number for one little item sitting on the scratch pad (the heap).
    /// Heap ID (HID). MS-PST 2.3.1.1. A 4-byte value identifying an item allocated from a Heap-on-Node:
    /// hidType (5 bits, MUST be 0 = NID_TYPE_HID), hidIndex (11 bits, 1-based, ≠0), hidBlockIndex
    /// (16 bits, 0-based data block index). Stored little-endian.
    /// </summary>
    public readonly struct Hid : IEquatable<Hid>
    {
        /// <summary>The raw 32-bit value.</summary>
        public uint Value { get; }

        /// <summary>Wraps a raw HID value.</summary>
        public Hid(uint value) => Value = value;

        /// <summary>Composes an HID from a 1-based heap index and a 0-based block index.</summary>
        public Hid(int hidIndex, int hidBlockIndex)
        {
            if (hidIndex <= 0 || hidIndex > 0x7FF)
                throw new ArgumentOutOfRangeException(nameof(hidIndex), hidIndex, "hidIndex must be 1..2047.");
            if (hidBlockIndex < 0 || hidBlockIndex > 0xFFFF)
                throw new ArgumentOutOfRangeException(nameof(hidBlockIndex));
            Value = ((uint)hidBlockIndex << 16) | ((uint)hidIndex << 5); // hidType = 0
        }

        /// <summary>The null HID (no allocation).</summary>
        public static readonly Hid Null = new Hid(0);

        /// <summary>True when this is the null HID.</summary>
        public bool IsNull => Value == 0;

        /// <summary>Heap type bits (always 0 for a valid HID).</summary>
        public int HidType => (int)(Value & 0x1F);

        /// <summary>1-based heap index.</summary>
        public int HidIndex => (int)((Value >> 5) & 0x7FF);

        /// <summary>0-based data block index.</summary>
        public int HidBlockIndex => (int)((Value >> 16) & 0xFFFF);

        /// <inheritdoc/>
        public bool Equals(Hid other) => Value == other.Value;
        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Hid o && Equals(o);
        /// <inheritdoc/>
        public override int GetHashCode() => (int)Value;
        /// <inheritdoc/>
        public override string ToString() => $"HID(0x{Value:X8}, idx={HidIndex}, blk={HidBlockIndex})";
    }

    /// <summary>
    /// Helpers for HNID values (MS-PST 2.3.3.2): a 32-bit value that is an <see cref="Hid"/> when its
    /// low 5 bits (NID_TYPE) are zero, or otherwise a subnode <see cref="Nid"/>.
    /// </summary>
    public static class Hnid
    {
        /// <summary>Encodes an HID as an HNID value.</summary>
        public static uint FromHid(Hid hid) => hid.Value;

        /// <summary>Encodes a subnode NID as an HNID value.</summary>
        public static uint FromNid(Nid nid) => nid.Value;

        /// <summary>True when the HNID refers to a heap item (HID) rather than a subnode (NID).</summary>
        public static bool IsHid(uint hnid) => (hnid & 0x1F) == 0;
    }
}
