using System;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: the front cover of the box — where the phone books start, how big the file is,
    /// and the checksums that prove it isn't damaged.
    /// The values needed to write a Unicode PST HEADER (MS-PST 2.2.2.6) and its embedded ROOT
    /// (2.2.2.5). All offsets/file sizes are 64-bit.
    /// </summary>
    public sealed class PstHeader
    {
        /// <summary>Next block BID counter (raw value, advances by 4 per block).</summary>
        public ulong BidNextB { get; set; }
        /// <summary>Next page BID counter.</summary>
        public ulong BidNextP { get; set; }
        /// <summary>Monotonic header-modification counter.</summary>
        public uint DwUnique { get; set; } = 1;
        /// <summary>Per-NID_TYPE next nidIndex counters (32 entries).</summary>
        public uint[] Rgnid { get; set; } = CreateDefaultRgnid();

        /// <summary>File size in bytes.</summary>
        public ulong IbFileEof { get; set; }
        /// <summary>Offset of the last AMap page.</summary>
        public ulong IbAMapLast { get; set; }
        /// <summary>Total free space across all AMaps.</summary>
        public ulong CbAMapFree { get; set; }
        /// <summary>Reference to the NBT root page.</summary>
        public Bref BrefNbt { get; set; }
        /// <summary>Reference to the BBT root page.</summary>
        public Bref BrefBbt { get; set; }

        /// <summary>The default rgnid array a blank PST starts with (MS-PST 2.2.2.6).</summary>
        public static uint[] CreateDefaultRgnid()
        {
            var rgnid = new uint[32];
            for (int i = 0; i < 32; i++) rgnid[i] = 0x400;
            rgnid[(int)NidType.SearchFolder] = 0x4000;
            rgnid[(int)NidType.NormalMessage] = 0x10000;
            rgnid[(int)NidType.AssocMessage] = 0x8000;
            return rgnid;
        }
    }

    /// <summary>Serializes a <see cref="PstHeader"/> to its 564-byte on-disk image, including both CRCs.</summary>
    public static class HeaderWriter
    {
        /// <summary>Builds the 564-byte header buffer.</summary>
        public static byte[] Serialize(PstHeader h)
        {
            if (h.Rgnid == null || h.Rgnid.Length != 32)
                throw new ArgumentException("Rgnid must contain exactly 32 entries.", nameof(h));

            var buffer = new byte[PstConstants.HeaderSize];
            var w = new SpanWriter(buffer);

            w.WriteUInt32(PstConstants.Magic);            // dwMagic @0
            w.WriteUInt32(0);                             // dwCRCPartial @4 (patched below)
            w.WriteUInt16(PstConstants.MagicClient);      // wMagicClient @8
            w.WriteUInt16(PstConstants.VersionUnicode);   // wVer @10
            w.WriteUInt16(PstConstants.VersionClient);    // wVerClient @12
            w.WriteByte(PstConstants.PlatformCreate);     // bPlatformCreate @14
            w.WriteByte(PstConstants.PlatformAccess);     // bPlatformAccess @15
            w.WriteUInt32(0);                             // dwReserved1 @16
            w.WriteUInt32(0);                             // dwReserved2 @20
            w.WriteUInt64(0);                             // bidUnused @24
            w.WriteUInt64(h.BidNextP);                    // bidNextP @32
            w.WriteUInt32(h.DwUnique);                    // dwUnique @40
            for (int i = 0; i < 32; i++) w.WriteUInt32(h.Rgnid[i]); // rgnid[] @44 (128 bytes)
            w.WriteUInt64(0);                             // qwUnused @172

            // ROOT @180 (72 bytes).
            w.WriteUInt32(0);                             // dwReserved
            w.WriteUInt64(h.IbFileEof);                   // ibFileEof
            w.WriteUInt64(h.IbAMapLast);                  // ibAMapLast
            w.WriteUInt64(h.CbAMapFree);                  // cbAMapFree
            w.WriteUInt64(0);                             // cbPMapFree
            w.WriteBref(h.BrefNbt);                       // BREFNBT
            w.WriteBref(h.BrefBbt);                       // BREFBBT
            w.WriteByte(0x02);                            // fAMapValid = VALID_AMAP2 (AMap + PMap written)
            w.WriteByte(0);                               // bReserved
            w.WriteUInt16(0);                             // wReserved

            w.WriteUInt32(0);                             // dwAlign @252
            for (int i = 0; i < 128; i++) w.WriteByte(0xFF); // rgbFM @256 (deprecated, 0xFF)
            for (int i = 0; i < 128; i++) w.WriteByte(0xFF); // rgbFP @384 (deprecated, 0xFF)
            w.WriteByte(0x80);                            // bSentinel @512
            w.WriteByte(PstConstants.CryptNone);          // bCryptMethod @513
            w.WriteUInt16(0);                             // rgbReserved @514
            w.WriteUInt64(h.BidNextB);                    // bidNextB @516
            w.WriteUInt32(0);                             // dwCRCFull @524 (patched below)
            // rgbReserved2 (3) + bReserved (1) + rgbReserved3 (32) remain zero @528..564.

            // dwCRCPartial: CRC of 471 bytes from offset 8.
            uint crcPartial = Crc.Compute(buffer.AsSpan(8, 471));
            new SpanWriter(buffer.AsSpan(4)).WriteUInt32(crcPartial);

            // dwCRCFull: CRC of 516 bytes from offset 8 (through end of bidNextB).
            uint crcFull = Crc.Compute(buffer.AsSpan(8, 516));
            new SpanWriter(buffer.AsSpan(524)).WriteUInt32(crcFull);

            return buffer;
        }
    }
}
