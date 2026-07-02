using System;
using System.Collections.Generic;
using PstBuilder.Foundation;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: builds the tiny sorted phone book that lives inside a scratch-pad (the heap) so a
    /// label or row can be found fast; if it outgrows one page, it grows extra index levels on top.
    /// Builds a BTree-on-Heap (BTH) body and returns its root HID and index depth. MS-PST 2.3.2.
    /// Leaf records (key + data) are packed into heap items; when they exceed one heap item, index
    /// levels of (key + 4-byte child HID) records are synthesized bottom-up until one root remains.
    /// The caller writes the BTHHEADER (bType, cbKey, cbEnt, bIdxLevels, hidRoot) separately.
    /// </summary>
    public static class BthBuilder
    {
        /// <summary>
        /// Packs the already key-sorted <paramref name="leafRecords"/> (each exactly cbKey + cbEnt bytes)
        /// into <paramref name="hn"/> and returns the BTH root HID and the number of index levels.
        /// </summary>
        public static (Hid Root, byte IdxLevels) Build(
            HeapOnNodeBuilder hn, IReadOnlyList<byte[]> leafRecords, int cbKey, int cbEnt)
        {
            if (leafRecords == null) throw new ArgumentNullException(nameof(leafRecords));
            if (leafRecords.Count == 0) return (Hid.Null, 0);

            int leafSize = cbKey + cbEnt;
            int idxSize = cbKey + 4; // intermediate record: key + hidNextLevel
            int maxLeaf = HeapOnNodeBuilder.MaxItemSize / leafSize;
            int maxIdx = HeapOnNodeBuilder.MaxItemSize / idxSize;
            if (maxLeaf < 1 || maxIdx < 1) throw new ArgumentException("Key/entry sizes too large for the heap.");

            // Leaf level: chunk records into heap items, remembering each item's first key and HID.
            var level = PackLevel(hn, leafRecords, leafSize, cbKey, maxLeaf);

            byte idxLevels = 0;
            while (level.Count > 1)
            {
                var indexRecords = new List<byte[]>(level.Count);
                foreach (var (firstKey, hid) in level)
                {
                    var rec = new byte[idxSize];
                    Array.Copy(firstKey, 0, rec, 0, cbKey);
                    new SpanWriter(rec.AsSpan(cbKey)).WriteUInt32(hid.Value);
                    indexRecords.Add(rec);
                }
                level = PackLevel(hn, indexRecords, idxSize, cbKey, maxIdx);
                idxLevels++;
            }

            return (level[0].Hid, idxLevels);
        }

        private static List<(byte[] FirstKey, Hid Hid)> PackLevel(
            HeapOnNodeBuilder hn, IReadOnlyList<byte[]> records, int recordSize, int cbKey, int maxPerItem)
        {
            var result = new List<(byte[], Hid)>();
            for (int i = 0; i < records.Count; i += maxPerItem)
            {
                int n = Math.Min(maxPerItem, records.Count - i);
                var item = new byte[n * recordSize];
                for (int k = 0; k < n; k++)
                    Array.Copy(records[i + k], 0, item, k * recordSize, recordSize);

                var firstKey = new byte[cbKey];
                Array.Copy(records[i], 0, firstKey, 0, cbKey);
                result.Add((firstKey, hn.Add(item)));
            }
            return result;
        }
    }
}
