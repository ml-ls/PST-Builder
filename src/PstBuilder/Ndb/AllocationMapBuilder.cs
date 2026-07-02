using System;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: draws the map showing which shelf spots in the file are used and which are free.
    /// Builds AMap pages (MS-PST 2.2.2.7.2.4). Each AMap page is 512 bytes: 496 map bytes (one bit per
    /// 64-byte unit, covering 253,952 bytes starting at the page's own offset) followed by a PAGETRAILER
    /// (ptype 0x84, wSig 0, bid == ib). The page maps itself: its first 512 bytes (first byte = 0xFF)
    /// are marked allocated.
    /// </summary>
    /// <remarks>
    /// This builder targets the append-only, contiguous layout: within a region, bytes
    /// <c>[regionStart, dataEnd)</c> are allocated and the remainder is free. Multi-region files with
    /// blocks that would straddle an AMap boundary are a deferred enhancement.
    /// </remarks>
    public static class AllocationMapBuilder
    {
        /// <summary>
        /// Builds one AMap page located at <paramref name="amapIb"/>. <paramref name="allocatedEnd"/> is
        /// the absolute offset of the first free byte (must be ≥ amapIb and 64-byte aligned). Allocation
        /// is clamped to this AMap's region span.
        /// </summary>
        public static byte[] BuildAMapPage(ulong amapIb, long allocatedEnd)
        {
            if (allocatedEnd < (long)amapIb)
                throw new ArgumentOutOfRangeException(nameof(allocatedEnd), "Allocated end precedes the AMap page.");
            if (allocatedEnd % PstConstants.BlockAlignment != 0)
                throw new ArgumentException("Allocated end must be 64-byte aligned.", nameof(allocatedEnd));

            var buffer = new byte[PstConstants.PageSize];

            long allocatedBytes = allocatedEnd - (long)amapIb;
            long regionSpan = PstConstants.AMapSpan;
            if (allocatedBytes > regionSpan) allocatedBytes = regionSpan;

            long allocatedUnits = allocatedBytes / PstConstants.BlockAlignment; // bits to set
            SetBits(buffer, allocatedUnits);

            // PAGETRAILER: ptype 0x84, ptypeRepeat 0x84, wSig 0, dwCRC over first 496 bytes, bid == ib.
            ushort dummyZeroSig = 0;
            uint crc = Crc.Compute(buffer.AsSpan(0, NdbFormat.PageTrailerOffset));
            var w = new SpanWriter(buffer.AsSpan(NdbFormat.PageTrailerOffset));
            w.WriteByte((byte)PageType.AMap);
            w.WriteByte((byte)PageType.AMap);
            w.WriteUInt16(dummyZeroSig);
            w.WriteUInt32(crc);
            w.WriteUInt64(amapIb); // bid == ib for AMap pages
            return buffer;
        }

        /// <summary>
        /// Builds one PMap (Page Map) page at <paramref name="pmapIb"/>. MS-PST 2.2.2.7.3. The PMap is
        /// deprecated (the Density List supersedes it) but every PMap page MUST still be present at its
        /// fixed interval with a valid PAGETRAILER, or clients reject the file. Each bit maps a 512-byte
        /// page; we mark them all allocated (0xFF) so a client never reuses a page that already holds data
        /// (it extends the file instead) — always safe for an append-only writer.
        /// </summary>
        public static byte[] BuildPMapPage(ulong pmapIb)
        {
            var buffer = new byte[PstConstants.PageSize];
            for (int i = 0; i < NdbFormat.PageTrailerOffset; i++) buffer[i] = 0xFF; // all pages allocated

            uint crc = Crc.Compute(buffer.AsSpan(0, NdbFormat.PageTrailerOffset));
            var w = new SpanWriter(buffer.AsSpan(NdbFormat.PageTrailerOffset));
            w.WriteByte((byte)PageType.PMap);
            w.WriteByte((byte)PageType.PMap);
            w.WriteUInt16(0);      // wSig: 0 for map pages
            w.WriteUInt32(crc);
            w.WriteUInt64(pmapIb); // bid == ib for map pages
            return buffer;
        }

        /// <summary>
        /// Builds one FMap (Free Map) page at <paramref name="fmapIb"/>. MS-PST 2.2.2.7.4. Like the PMap the
        /// FMap is deprecated but every FMap page MUST be present at its fixed interval (first at 0x1F04800,
        /// then every 496 AMaps) with a valid PAGETRAILER (ptype 0x82, wSig 0, bid == ib) or clients reject
        /// the file (scanpst: "FMap page @…: PTYPE mismatch (expected 82)"). Each of the 496 map bytes would
        /// hold the largest free run in one AMap; the value is advisory and never validated by scanpst, so we
        /// write 0x00 (no free run → an allocating client extends the file rather than reusing space, always
        /// safe for an append-only writer). Only the trailer's ptype/bid/CRC/sig are checked.
        /// </summary>
        public static byte[] BuildFMapPage(ulong fmapIb, byte[] mapBytes)
        {
            var buffer = new byte[PstConstants.PageSize];
            // Each of the 496 map bytes = min(free 64-byte slots, 255) of the AMap it covers (this FMap's
            // AMap onward). scanpst validates these against the actual AMap free counts ("AMap page @X has
            // csFree of <fmapByte>, but should have <actual>"); zeros here are only correct for full AMaps.
            if (mapBytes != null)
                Array.Copy(mapBytes, 0, buffer, 0, Math.Min(mapBytes.Length, NdbFormat.PageTrailerOffset));

            uint crc = Crc.Compute(buffer.AsSpan(0, NdbFormat.PageTrailerOffset));
            var w = new SpanWriter(buffer.AsSpan(NdbFormat.PageTrailerOffset));
            w.WriteByte((byte)PageType.FMap);
            w.WriteByte((byte)PageType.FMap);
            w.WriteUInt16(0);      // wSig: 0 for map pages
            w.WriteUInt32(crc);
            w.WriteUInt64(fmapIb); // bid == ib for map pages
            return buffer;
        }

        /// <summary>
        /// Builds one FPMap (Free Page Map) page at <paramref name="fpmapIb"/>. MS-PST 2.2.2.7.5. Deprecated
        /// like the PMap, but every FPMap page MUST be present at its fixed interval with a valid PAGETRAILER
        /// (ptype 0x85, wSig 0, bid == ib) once the file is large enough (first at 0x7C004800), or clients
        /// reject it (scanpst: "FPMap page @…: PTYPE mismatch (expected 85)"). It is a page-allocation
        /// bitmap; we mark everything allocated (0xFF) as for the PMap so a client extends the file rather
        /// than reusing space (always safe for an append-only writer).
        /// </summary>
        public static byte[] BuildFPMapPage(ulong fpmapIb)
        {
            var buffer = new byte[PstConstants.PageSize];
            for (int i = 0; i < NdbFormat.PageTrailerOffset; i++) buffer[i] = 0xFF; // all pages allocated

            uint crc = Crc.Compute(buffer.AsSpan(0, NdbFormat.PageTrailerOffset));
            var w = new SpanWriter(buffer.AsSpan(NdbFormat.PageTrailerOffset));
            w.WriteByte((byte)PageType.FPMap);
            w.WriteByte((byte)PageType.FPMap);
            w.WriteUInt16(0);       // wSig: 0 for map pages
            w.WriteUInt32(crc);
            w.WriteUInt64(fpmapIb); // bid == ib for map pages
            return buffer;
        }

        /// <summary>Free bytes mapped by this AMap page given the allocated end (for cbAMapFree).</summary>
        public static long FreeBytes(ulong amapIb, long allocatedEnd)
        {
            long allocatedBytes = Math.Min(allocatedEnd - (long)amapIb, PstConstants.AMapSpan);
            return PstConstants.AMapSpan - allocatedBytes;
        }

        private static void SetBits(byte[] buffer, long bits)
        {
            // The bit for unit i is bit (7 - i%8) of byte (i/8): the first unit is the most-significant
            // bit of byte 0, so a fully-allocated leading run yields 0xFF bytes.
            long fullBytes = bits / 8;
            int remBits = (int)(bits % 8);
            for (long b = 0; b < fullBytes; b++)
                buffer[b] = 0xFF;
            if (remBits > 0)
            {
                byte mask = 0;
                for (int k = 0; k < remBits; k++)
                    mask |= (byte)(0x80 >> k);
                buffer[fullBytes] = mask;
            }
        }
    }
}
