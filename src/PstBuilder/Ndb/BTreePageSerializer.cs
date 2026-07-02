using System;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>
    /// In plain words: stamps one page of a phone-book tree onto the exact 512 bytes Outlook expects.
    /// Serializes a <see cref="BTreePageNode"/> into its 512-byte BTPAGE image (MS-PST 2.2.2.7.7).
    /// Child IBs must be assigned before serializing an intermediate page.
    /// </summary>
    public static class BTreePageSerializer
    {
        /// <summary>Produces the 512-byte page image. Requires <see cref="BTreePageNode.Ib"/> assigned.</summary>
        public static byte[] Serialize(BTreePageNode page)
        {
            if (page.Ib < 0) throw new InvalidOperationException("Page IB must be assigned before serialization.");
            var buffer = new byte[PstConstants.PageSize];
            var w = new SpanWriter(buffer);

            int count = WriteEntries(ref w, page);

            // Trailer fields after the rgentries region.
            w.Seek(NdbFormat.BtPageCEntOffset);
            w.WriteByte((byte)count);          // cEnt
            w.WriteByte(page.CEntMax);          // cEntMax
            w.WriteByte(page.CbEnt);            // cbEnt
            w.WriteByte(page.CLevel);           // cLevel
            w.WriteUInt32(0);                   // dwPadding

            // PAGETRAILER.
            ushort sig = Signature.Compute((ulong)page.Ib, page.Bid.Value);
            uint crc = Crc.Compute(buffer.AsSpan(0, NdbFormat.PageTrailerOffset));
            w.Seek(NdbFormat.PageTrailerOffset);
            w.WriteByte((byte)page.Ptype);
            w.WriteByte((byte)page.Ptype);     // ptypeRepeat
            w.WriteUInt16(sig);
            w.WriteUInt32(crc);
            w.WriteBid(page.Bid);
            return buffer;
        }

        private static int WriteEntries(ref SpanWriter w, BTreePageNode page)
        {
            if (page.Children != null)
            {
                foreach (var (key, child) in page.Children)
                {
                    if (child.Ib < 0) throw new InvalidOperationException("Child IB must be assigned before serializing parent.");
                    w.WriteUInt64(key);                                  // btkey
                    w.WriteBref(new Bref(child.Bid, (ulong)child.Ib));   // BREF
                }
                return page.Children.Count;
            }

            if (page.NbtLeaves != null)
            {
                foreach (var e in page.NbtLeaves)
                {
                    w.WriteUInt64(e.Nid.Value);     // nid (4-byte value zero-extended to 8)
                    w.WriteBid(e.BidData);
                    w.WriteBid(e.BidSub);
                    w.WriteUInt32(e.NidParent.Value);
                    w.WriteUInt32(0);               // dwPadding
                }
                return page.NbtLeaves.Count;
            }

            if (page.BbtLeaves != null)
            {
                foreach (var e in page.BbtLeaves)
                {
                    w.WriteBref(new Bref(e.Bid, e.Ib));
                    w.WriteUInt16(e.Cb);
                    w.WriteUInt16(e.CRef);
                    w.WriteUInt32(0);               // dwPadding
                }
                return page.BbtLeaves.Count;
            }

            return 0;
        }
    }
}
