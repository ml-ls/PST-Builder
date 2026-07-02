namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: the one-byte "what am I" stamp on every 512-byte page — is it a phone-book page,
    /// or one of the shelf-usage maps (AMap/PMap/FMap/FPMap)?
    /// Page types (<c>ptype</c>) stored in a PAGETRAILER. MS-PST 2.2.2.7.1.
    /// </summary>
    public enum PageType : byte
    {
        /// <summary>Block BTree page.</summary>
        Bbt = 0x80,
        /// <summary>Node BTree page.</summary>
        Nbt = 0x81,
        /// <summary>Free Map page (FMap).</summary>
        FMap = 0x82,
        /// <summary>Page Map (PMap).</summary>
        PMap = 0x83,
        /// <summary>Allocation Map page (AMap).</summary>
        AMap = 0x84,
        /// <summary>Free Page Map (FPMap).</summary>
        FPMap = 0x85,
        /// <summary>Density List page (DList).</summary>
        DensityList = 0x86,
    }

    /// <summary>
    /// In plain words: the magic numbers and fixed sizes the PST format demands — header size, page size,
    /// where the shelf-usage maps go, how big a single file may grow — all in one place.
    /// Layout constants for the Unicode (64-bit) PST file format. MS-PST 2.2.2.x.
    /// </summary>
    public static class PstConstants
    {
        /// <summary>Magic value at the start of the header: "!BDN".</summary>
        public const uint Magic = 0x4E444221; // little-endian bytes 21 42 44 4E -> '!','B','D','N'

        /// <summary>Client magic (<c>wMagicClient</c>): "SM".</summary>
        public const ushort MagicClient = 0x4D53; // 'S','M'

        /// <summary>File format version for Unicode PST (<c>wVer</c>): 23 (0x17). 36 (0x24) also seen.</summary>
        public const ushort VersionUnicode = 23;

        /// <summary>Client file format version (<c>wVerClient</c>): 19.</summary>
        public const ushort VersionClient = 19;

        /// <summary>Platform create byte (<c>bPlatformCreate</c>): 0x01.</summary>
        public const byte PlatformCreate = 0x01;

        /// <summary>Platform access byte (<c>bPlatformAccess</c>): 0x01.</summary>
        public const byte PlatformAccess = 0x01;

        /// <summary>No-encoding crypt method (<c>NDB_CRYPT_NONE</c>).</summary>
        public const byte CryptNone = 0x00;

        /// <summary>Header size in bytes (Unicode).</summary>
        public const int HeaderSize = 564;

        /// <summary>Page size in bytes. All pages (BTree, AMap, etc.) are this size.</summary>
        public const int PageSize = 512;

        /// <summary>PAGETRAILER size in bytes (Unicode).</summary>
        public const int PageTrailerSize = 16;

        /// <summary>BLOCKTRAILER size in bytes (Unicode).</summary>
        public const int BlockTrailerSize = 16;

        /// <summary>Maximum total size of a single block (data + trailer + padding).</summary>
        public const int MaxBlockSize = 8192;

        /// <summary>Block sizes are rounded up to a multiple of this many bytes (Unicode).</summary>
        public const int BlockAlignment = 64;

        /// <summary>Maximum data payload in a single block (MaxBlockSize - BlockTrailerSize).</summary>
        public const int MaxBlockDataSize = MaxBlockSize - BlockTrailerSize; // 8176

        /// <summary>Bytes of file space mapped by one AMap page (one bit per 64-byte unit, 496 data bytes).</summary>
        public const int AMapDataBytes = PageSize - PageTrailerSize; // 496

        /// <summary>Number of bytes of file each AMap page accounts for: 496 * 8 bits * 64 bytes/bit.</summary>
        public const long AMapSpan = (long)AMapDataBytes * 8 * BlockAlignment; // 253952

        /// <summary>The fixed file offset of the first AMap page. MS-PST 2.2.2.7.2.4.</summary>
        public const long FirstAMapOffset = 0x4400; // 17408

        /// <summary>The fixed file offset of the first PMap page (immediately after the first AMap). MS-PST 2.2.2.7.3.</summary>
        public const long FirstPMapOffset = 0x4600; // 17920

        /// <summary>Number of AMap pages between consecutive PMap pages: one PMap per eight AMaps.</summary>
        public const int AMapsPerPMap = 8;

        /// <summary>Bytes mapped by one PMap page: 496 * 8 bits * 512-byte pages = 8 * AMapSpan.</summary>
        public const long PMapSpan = AMapSpan * AMapsPerPMap; // 0x1F0000 = 2,031,616

        /// <summary>Number of AMap pages one FMap page accounts for (one FMap byte per AMap). MS-PST 2.2.2.7.4.</summary>
        public const int AMapsPerFMap = 496;

        /// <summary>
        /// AMap region index of the first FMap page. Confirmed by scanpst: the first FMap is expected at
        /// file offset 0x1F04800 (region 128 starts at 0x1F04400; its AMap+PMap occupy 0x400, so the FMap
        /// follows at regionStart + 0x400). Thereafter an FMap is due every <see cref="AMapsPerFMap"/>
        /// regions (0x7820000 bytes = 496 * AMapSpan) — i.e. in region k where k ≥ 128 and (k-128) % 496 == 0.
        /// Every such region is also a PMap region (128 and 496 are multiples of <see cref="AMapsPerPMap"/>),
        /// so the FMap always sits immediately after the PMap.
        /// </summary>
        public const int FirstFMapRegion = 128;

        /// <summary>Byte offset of the first FMap page (region 128 start 0x1F04400 + AMap 0x200 + PMap 0x200).</summary>
        public const long FirstFMapOffset = 0x1F04800;

        /// <summary>Bytes mapped by one FMap page: 496 AMaps.</summary>
        public const long FMapSpan = AMapSpan * AMapsPerFMap; // 0x7820000 = 125,960,192

        /// <summary>
        /// AMap region index of the first FPMap page (MS-PST 2.2.2.7.5). Confirmed by scanpst on a 3 GB
        /// file: the first (and, within our size cap, only) FPMap is expected at file offset 0x7C004800 —
        /// region 8192 (amapIb 0x7C004400) after its AMap+PMap, so at regionStart + 0x400. Region 8192 is a
        /// PMap region (multiple of 8) but not an FMap region, so the preamble there is AMap, PMap, FPMap.
        /// </summary>
        public const int FirstFPMapRegion = 8192;

        /// <summary>Byte offset of the first FPMap page (region 8192 start 0x7C004400 + AMap 0x200 + PMap 0x200).</summary>
        public const long FirstFPMapOffset = 0x7C004800;

        /// <summary>
        /// AMap regions between FPMap pages. Only one FPMap appeared across a 3 GB (~12,940-region) file, so
        /// the interval is large; this value keeps the second FPMap beyond <see cref="MaxAMapRegions"/> (its
        /// exact value matters only past ~10 GB, which requires a further oracle round to confirm).
        /// </summary>
        public const int AMapsPerFPMap = 31744;

        /// <summary>
        /// Maximum AMap regions per single file (~3.4 GB). AMap/PMap/FMap and the first FPMap page are all
        /// emitted, so single files &gt;2 GB are supported. Beyond this, split (PstExportSession.CreateSplit)
        /// or confirm the FPMap interval with another oracle round.
        /// </summary>
        public const int MaxAMapRegions = 14000;
    }
}
