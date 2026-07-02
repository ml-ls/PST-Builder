using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: the exact sizes and spots the lowest layer must line up to, kept in one place so
    /// every writer and test agrees on the same numbers.
    /// Fixed sizes and offsets for NDB-layer on-disk structures (Unicode). MS-PST 2.2.2.7–2.2.2.8.
    /// Centralized so the writers and tests agree on a single source of truth.
    /// </summary>
    public static class NdbFormat
    {
        // BTPAGE (512 bytes total).
        /// <summary>Bytes available for entries in a BTPAGE.</summary>
        public const int BtPageEntriesSize = 488;
        /// <summary>Offset of cEnt within a BTPAGE.</summary>
        public const int BtPageCEntOffset = 488;
        /// <summary>Offset of the PAGETRAILER within a 512-byte page.</summary>
        public const int PageTrailerOffset = PstConstants.PageSize - PstConstants.PageTrailerSize; // 496

        // Entry sizes (Unicode).
        /// <summary>NBTENTRY size.</summary>
        public const int NbtEntrySize = 32;
        /// <summary>BBTENTRY size.</summary>
        public const int BbtEntrySize = 24;
        /// <summary>Intermediate BTENTRY size.</summary>
        public const int BtEntrySize = 24;

        /// <summary>Max NBTENTRY entries per leaf page (floor(488/32)).</summary>
        public const int NbtLeafMax = BtPageEntriesSize / NbtEntrySize; // 15
        /// <summary>Max BBTENTRY entries per leaf page (floor(488/24)).</summary>
        public const int BbtLeafMax = BtPageEntriesSize / BbtEntrySize; // 20
        /// <summary>Max BTENTRY entries per intermediate page (floor(488/24)).</summary>
        public const int IntermediateMax = BtPageEntriesSize / BtEntrySize; // 20

        /// <summary>Reference count written for a block that has at least one reference.</summary>
        public const ushort DefaultRefCount = 2;

        /// <summary>Rounds a raw data length up to the total on-disk block size (data + trailer, 64-aligned).</summary>
        public static int TotalBlockSize(int rawDataLength)
        {
            int needed = rawDataLength + PstConstants.BlockTrailerSize;
            int rem = needed % PstConstants.BlockAlignment;
            return rem == 0 ? needed : needed + (PstConstants.BlockAlignment - rem);
        }
    }
}
