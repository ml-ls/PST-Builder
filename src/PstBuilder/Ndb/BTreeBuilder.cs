using System;
using System.Collections.Generic;
using System.Linq;
using PstBuilder.Foundation;

namespace PstBuilder.Ndb
{
    /// <summary>A leaf entry of the Node BTree (NBTENTRY). MS-PST 2.2.2.7.7.4.</summary>
    public readonly struct NbtLeafEntry
    {
        /// <summary>Node identifier (the BTree key).</summary>
        public Nid Nid { get; }
        /// <summary>BID of the node's data block.</summary>
        public Bid BidData { get; }
        /// <summary>BID of the node's subnode block (0 if none).</summary>
        public Bid BidSub { get; }
        /// <summary>NID of the parent Folder object, or 0.</summary>
        public Nid NidParent { get; }

        /// <summary>Creates an NBT leaf entry.</summary>
        public NbtLeafEntry(Nid nid, Bid bidData, Bid bidSub, Nid nidParent)
        {
            Nid = nid;
            BidData = bidData;
            BidSub = bidSub;
            NidParent = nidParent;
        }

        internal ulong Key => Nid.Value;
    }

    /// <summary>A leaf entry of the Block BTree (BBTENTRY). MS-PST 2.2.2.7.7.3.</summary>
    public readonly struct BbtLeafEntry
    {
        /// <summary>BID of the block (the BTree key).</summary>
        public Bid Bid { get; }
        /// <summary>Absolute file offset of the block.</summary>
        public ulong Ib { get; }
        /// <summary>Raw data byte count (excludes trailer/padding).</summary>
        public ushort Cb { get; }
        /// <summary>Reference count.</summary>
        public ushort CRef { get; }

        /// <summary>Creates a BBT leaf entry.</summary>
        public BbtLeafEntry(Bid bid, ulong ib, ushort cb, ushort cRef)
        {
            Bid = bid;
            Ib = ib;
            Cb = cb;
            CRef = cRef;
        }

        internal ulong Key => Bid.Value;
    }

    /// <summary>A single BTPAGE node in a packed B-tree (leaf or intermediate).</summary>
    public sealed class BTreePageNode
    {
        /// <summary>PAGETRAILER ptype: ptypeNBT (0x81) or ptypeBBT (0x80).</summary>
        public PageType Ptype { get; internal set; }
        /// <summary>Tree depth: 0 = leaf.</summary>
        public byte CLevel { get; internal set; }
        /// <summary>Page BID (allocated from bidNextP).</summary>
        public Bid Bid { get; internal set; }
        /// <summary>Per-entry size (cbEnt).</summary>
        public byte CbEnt { get; internal set; }
        /// <summary>Max entries per page (cEntMax).</summary>
        public byte CEntMax { get; internal set; }
        /// <summary>Smallest key in this page's subtree (used as parent btkey).</summary>
        public ulong FirstKey { get; internal set; }
        /// <summary>Absolute file offset, assigned during layout. -1 until assigned.</summary>
        public long Ib { get; internal set; } = -1;

        // Exactly one of the following is non-null depending on level/type.
        internal List<NbtLeafEntry>? NbtLeaves;
        internal List<BbtLeafEntry>? BbtLeaves;
        internal List<(ulong Key, BTreePageNode Child)>? Children;
    }

    /// <summary>A fully built, packed B-tree: a root plus every page (for layout and serialization).</summary>
    public sealed class BTree
    {
        /// <summary>The root page (referenced by the header BREF).</summary>
        public BTreePageNode Root { get; }
        /// <summary>Every page in the tree, leaves first then ascending levels.</summary>
        public IReadOnlyList<BTreePageNode> AllPages { get; }

        internal BTree(BTreePageNode root, IReadOnlyList<BTreePageNode> allPages)
        {
            Root = root;
            AllPages = allPages;
        }
    }

    /// <summary>
    /// In plain words: builds the sorted "phone books" (a tree of pages) so a reader can quickly find
    /// any box or node by its number.
    /// Builds NBT and BBT pages packed and balanced bottom-up in a single pass (MS-PST 1.3.1.1).
    /// Leaves are filled to capacity greedily; intermediate levels are synthesized until one root
    /// remains. No incremental insert or rebalancing.
    /// </summary>
    public static class BTreeBuilder
    {
        /// <summary>Builds the Node BTree from leaf entries.</summary>
        public static BTree BuildNbt(IEnumerable<NbtLeafEntry> entries, Func<Bid> allocPageBid)
        {
            var sorted = entries.OrderBy(e => e.Key).ToList();
            var all = new List<BTreePageNode>();
            var leaves = new List<BTreePageNode>();
            foreach (var chunk in Chunk(sorted, NdbFormat.NbtLeafMax))
            {
                var page = new BTreePageNode
                {
                    Ptype = PageType.Nbt,
                    CLevel = 0,
                    Bid = allocPageBid(),
                    CbEnt = NdbFormat.NbtEntrySize,
                    CEntMax = NdbFormat.NbtLeafMax,
                    NbtLeaves = chunk,
                    FirstKey = chunk[0].Key,
                };
                leaves.Add(page);
                all.Add(page);
            }
            if (leaves.Count == 0)
                leaves.Add(EmptyLeaf(PageType.Nbt, NdbFormat.NbtEntrySize, NdbFormat.NbtLeafMax, allocPageBid, all, isNbt: true));

            var root = BuildUp(leaves, PageType.Nbt, allocPageBid, all);
            return new BTree(root, all);
        }

        /// <summary>Builds the Block BTree from leaf entries.</summary>
        public static BTree BuildBbt(IEnumerable<BbtLeafEntry> entries, Func<Bid> allocPageBid)
        {
            var sorted = entries.OrderBy(e => e.Key).ToList();
            var all = new List<BTreePageNode>();
            var leaves = new List<BTreePageNode>();
            foreach (var chunk in Chunk(sorted, NdbFormat.BbtLeafMax))
            {
                var page = new BTreePageNode
                {
                    Ptype = PageType.Bbt,
                    CLevel = 0,
                    Bid = allocPageBid(),
                    CbEnt = NdbFormat.BbtEntrySize,
                    CEntMax = NdbFormat.BbtLeafMax,
                    BbtLeaves = chunk,
                    FirstKey = chunk[0].Key,
                };
                leaves.Add(page);
                all.Add(page);
            }
            if (leaves.Count == 0)
                leaves.Add(EmptyLeaf(PageType.Bbt, NdbFormat.BbtEntrySize, NdbFormat.BbtLeafMax, allocPageBid, all, isNbt: false));

            var root = BuildUp(leaves, PageType.Bbt, allocPageBid, all);
            return new BTree(root, all);
        }

        private static BTreePageNode EmptyLeaf(PageType ptype, byte cbEnt, byte cEntMax, Func<Bid> allocPageBid,
            List<BTreePageNode> all, bool isNbt)
        {
            var page = new BTreePageNode
            {
                Ptype = ptype,
                CLevel = 0,
                Bid = allocPageBid(),
                CbEnt = cbEnt,
                CEntMax = cEntMax,
                FirstKey = 0,
            };
            if (isNbt) page.NbtLeaves = new List<NbtLeafEntry>();
            else page.BbtLeaves = new List<BbtLeafEntry>();
            all.Add(page);
            return page;
        }

        private static BTreePageNode BuildUp(List<BTreePageNode> level, PageType ptype, Func<Bid> allocPageBid,
            List<BTreePageNode> all)
        {
            while (level.Count > 1)
            {
                var parents = new List<BTreePageNode>();
                foreach (var chunk in Chunk(level, NdbFormat.IntermediateMax))
                {
                    var page = new BTreePageNode
                    {
                        Ptype = ptype,
                        CLevel = (byte)(chunk[0].CLevel + 1),
                        Bid = allocPageBid(),
                        CbEnt = NdbFormat.BtEntrySize,
                        CEntMax = NdbFormat.IntermediateMax,
                        Children = chunk.Select(c => (c.FirstKey, c)).ToList(),
                        FirstKey = chunk[0].FirstKey,
                    };
                    parents.Add(page);
                    all.Add(page);
                }
                level = parents;
            }
            return level[0];
        }

        private static List<List<T>> Chunk<T>(List<T> items, int size)
        {
            var result = new List<List<T>>();
            for (int i = 0; i < items.Count; i += size)
                result.Add(items.GetRange(i, Math.Min(size, items.Count - i)));
            return result;
        }
    }
}
