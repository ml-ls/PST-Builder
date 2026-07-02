using System;

namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: a name tag for one "thing" (a folder, message, table…) so it can be looked up.
    /// Node identifier (NID). MS-PST 2.2.2.1. A 32-bit value: the low 5 bits hold the
    /// <see cref="NidType"/>, the high 27 bits hold the node index. NIDs are unique within
    /// a node level (NBT entry, or subnode within a node). Stored little-endian on disk.
    /// </summary>
    public readonly struct Nid : IEquatable<Nid>
    {
        /// <summary>Number of bits used by the type field.</summary>
        public const int TypeBits = 5;

        /// <summary>Mask for the type field (low 5 bits).</summary>
        public const uint TypeMask = (1u << TypeBits) - 1; // 0x1F

        /// <summary>Maximum representable node index (27 bits).</summary>
        public const uint MaxIndex = (1u << (32 - TypeBits)) - 1;

        /// <summary>The raw 32-bit value as stored on disk.</summary>
        public uint Value { get; }

        /// <summary>Wraps an existing raw 32-bit NID value.</summary>
        public Nid(uint value) => Value = value;

        /// <summary>Composes a NID from a type and a node index.</summary>
        public Nid(NidType type, uint index)
        {
            if (index > MaxIndex)
                throw new ArgumentOutOfRangeException(nameof(index), index, "NID index exceeds 27 bits.");
            Value = ((index & MaxIndex) << TypeBits) | ((uint)type & TypeMask);
        }

        /// <summary>The node type (low 5 bits).</summary>
        public NidType Type => (NidType)(Value & TypeMask);

        /// <summary>The node index (high 27 bits).</summary>
        public uint Index => Value >> TypeBits;

        // Predefined NIDs (MS-PST 2.4.1). These are well-known nodes the store must contain.

        /// <summary>The message store node. NID_MESSAGE_STORE.</summary>
        public static readonly Nid MessageStore = new Nid(0x21);

        /// <summary>The named-property lookup map node. NID_NAME_TO_ID_MAP.</summary>
        public static readonly Nid NameToIdMap = new Nid(0x61);

        /// <summary>Template for normal folders. NID_NORMAL_FOLDER_TEMPLATE.</summary>
        public static readonly Nid NormalFolderTemplate = new Nid(0xA21);

        /// <summary>Template for search folders. NID_SEARCH_FOLDER_TEMPLATE.</summary>
        public static readonly Nid SearchFolderTemplate = new Nid(0xE21);

        /// <summary>The root folder of the store. NID_ROOT_FOLDER.</summary>
        public static readonly Nid RootFolder = new Nid(0x122);

        /// <summary>Search-management queue. NID_SEARCH_MANAGEMENT_QUEUE.</summary>
        public static readonly Nid SearchManagementQueue = new Nid(0x1E1);

        /// <inheritdoc/>
        public bool Equals(Nid other) => Value == other.Value;

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is Nid other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => (int)Value;

        /// <summary>Equality operator.</summary>
        public static bool operator ==(Nid a, Nid b) => a.Value == b.Value;

        /// <summary>Inequality operator.</summary>
        public static bool operator !=(Nid a, Nid b) => a.Value != b.Value;

        /// <inheritdoc/>
        public override string ToString() => $"NID(0x{Value:X8}, {Type}, idx={Index})";
    }
}
