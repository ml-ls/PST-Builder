using System;
using System.Collections.Generic;
using System.Linq;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>An entry in a leaf subnode block (SLENTRY). MS-PST 2.2.2.8.3.3.1.1.</summary>
    public readonly struct SlEntry
    {
        /// <summary>The subnode's local NID (the BTree key within the node's subnode tree).</summary>
        public Nid Nid { get; }
        /// <summary>BID of the subnode's data block.</summary>
        public Bid BidData { get; }
        /// <summary>BID of the subnode's own nested subnode block (0 if none).</summary>
        public Bid BidSub { get; }

        /// <summary>Creates a subnode leaf entry.</summary>
        public SlEntry(Nid nid, Bid bidData, Bid bidSub)
        {
            Nid = nid;
            BidData = bidData;
            BidSub = bidSub;
        }
    }

    /// <summary>
    /// In plain words: a message's "side-boxes" list — small attached things (like recipients and
    /// attachments) hung off the message, each with its own little name tag.
    /// Builds a single leaf subnode block (SLBLOCK). MS-PST 2.2.2.8.3.3.1. The result is the raw
    /// payload of an internal block (the caller stages it with the internal flag set). Entries are
    /// sorted by NID. Intermediate SIBLOCKs (more than one leaf) are a deferred enhancement.
    /// </summary>
    public static class SubnodeBlockBuilder
    {
        private const int HeaderSize = 8;   // btype + cLevel + cEnt + dwPadding
        private const int EntrySize = 24;   // nid + bidData + bidSub

        /// <summary>Serializes a leaf SLBLOCK from the given subnode entries.</summary>
        public static byte[] BuildSlBlock(IReadOnlyList<SlEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                throw new ArgumentException("A subnode block needs at least one entry.", nameof(entries));

            var sorted = entries.OrderBy(e => e.Nid.Value).ToList();
            int raw = HeaderSize + sorted.Count * EntrySize;
            if (raw > PstConstants.MaxBlockDataSize)
                throw new NotSupportedException("Subnode exceeds one SLBLOCK; SIBLOCK level not yet implemented.");

            var buffer = new byte[raw];
            var w = new SpanWriter(buffer);
            w.WriteByte(0x02);                  // btype (SL/SI)
            w.WriteByte(0x00);                  // cLevel = 0 (leaf)
            w.WriteUInt16((ushort)sorted.Count);// cEnt
            w.WriteUInt32(0);                   // dwPadding
            foreach (var e in sorted)
            {
                w.WriteUInt64(e.Nid.Value);     // nid (zero-extended to 8 bytes)
                w.WriteBid(e.BidData);
                w.WriteBid(e.BidSub);
            }
            return buffer;
        }
    }
}
