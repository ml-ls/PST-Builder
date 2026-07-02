using System;
using System.Collections.Generic;
using System.Linq;
using PstBuilder.Foundation;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: makes a labelled bag of one thing's details, e.g. "DisplayName → Bob", "Size → 1200".
    /// Builds a Property Context (PC). MS-PST 2.3.3. A PC is a BTree-on-Heap (key = 2-byte property id,
    /// data = wPropType + dwValueHnid) layered on a Heap-on-Node. Produces the raw block payload; the
    /// caller wraps it as a node in the NDB layer.
    /// </summary>
    /// <remarks>
    /// Fixed types ≤ 4 bytes are stored inline per MS-PST 2.3.3.3. Variable-length values larger than
    /// <see cref="HeapOnNodeBuilder.MaxItemSize"/> — and any streamed value — are spilled to the node's
    /// subnode tree (their HNID becomes a subnode NID); the caller wires those via
    /// <see cref="LtpContent.Subnodes"/>.
    /// </remarks>
    public sealed class PropertyContextBuilder
    {
        private const byte BthType = 0xB5;
        private const byte PcKeySize = 2;   // cbKey: wPropId
        private const byte PcDataSize = 6;  // cbEnt: wPropType (2) + dwValueHnid (4)

        private readonly List<Property> _properties = new List<Property>();
        private int _nextSubnodeIndex = 1;

        /// <summary>Adds a property. Later additions with the same id overwrite earlier ones.</summary>
        public PropertyContextBuilder Add(Property property)
        {
            if (property == null) throw new ArgumentNullException(nameof(property));
            _properties.RemoveAll(p => p.PropId == property.PropId);
            _properties.Add(property);
            return this;
        }

        /// <summary>Builds the PC, returning the HN block payload plus any spilled subnode values.</summary>
        public LtpContent Build()
        {
            var sorted = _properties.OrderBy(p => p.PropId).ToList();
            var hn = new HeapOnNodeBuilder(HeapOnNodeBuilder.ClientSigPc);

            // Add heap-stored values first (capturing real HIDs, which may land in later blocks), then
            // the records array that references them, then the BTHHEADER that references the records.
            var valueHnidByProp = new Dictionary<ushort, uint>();
            var subnodes = new List<LtpSubnode>();
            foreach (var p in sorted)
            {
                if (PropertyTypes.IsInlineInPc(p.Type)) continue;
                if (p.StreamSource != null)
                {
                    // Streamed value: always spilled to a subnode and read on demand (never buffered whole).
                    var subNid = new Nid(NidType.Ltp, (uint)_nextSubnodeIndex++);
                    subnodes.Add(new LtpSubnode(subNid, p.StreamSource, p.ValueLength));
                    valueHnidByProp[p.PropId] = subNid.Value;
                    continue;
                }
                if (p.Data.Length == 0) { valueHnidByProp[p.PropId] = 0; continue; }
                if (p.Data.Length > HeapOnNodeBuilder.MaxItemSize)
                {
                    // Spill: the HNID becomes a subnode NID and the value lives in the subnode tree.
                    var subNid = new Nid(NidType.Ltp, (uint)_nextSubnodeIndex++);
                    subnodes.Add(new LtpSubnode(subNid, p.Data));
                    valueHnidByProp[p.PropId] = subNid.Value;
                    continue;
                }
                valueHnidByProp[p.PropId] = hn.Add(p.Data).Value;
            }

            var (recordsHid, idxLevels) = BthBuilder.Build(hn, BuildRecords(sorted, valueHnidByProp), PcKeySize, PcDataSize);
            hn.UserRoot = hn.Add(BuildBthHeader(idxLevels, recordsHid));

            return new LtpContent(hn.Build(), subnodes);
        }

        // Each PC BTH record: wPropId (key, 2) + wPropType (2) + dwValueHnid (4), sorted by property id.
        private static List<byte[]> BuildRecords(List<Property> sorted, Dictionary<ushort, uint> valueHnids)
        {
            var records = new List<byte[]>(sorted.Count);
            foreach (var p in sorted)
            {
                var rec = new byte[8];
                var w = new SpanWriter(rec);
                w.WriteUInt16(p.PropId);
                w.WriteUInt16((ushort)p.Type);
                w.WriteUInt32(ValueHnid(p, valueHnids));
                records.Add(rec);
            }
            return records;
        }

        private static uint ValueHnid(Property p, Dictionary<ushort, uint> valueHnids)
        {
            if (PropertyTypes.IsInlineInPc(p.Type))
            {
                Span<byte> slot = stackalloc byte[4];
                int n = Math.Min(p.Data.Length, 4);
                p.Data.AsSpan(0, n).CopyTo(slot);
                return BitConverter.ToUInt32(slot.ToArray(), 0);
            }
            return valueHnids[p.PropId]; // HID, subnode NID, or 0 for empty
        }

        private static byte[] BuildBthHeader(byte bIdxLevels, Hid recordsHid)
        {
            var buffer = new byte[8];
            var w = new SpanWriter(buffer);
            w.WriteByte(BthType);
            w.WriteByte(PcKeySize);
            w.WriteByte(PcDataSize);
            w.WriteByte(bIdxLevels);
            w.WriteUInt32(recordsHid.Value);
            return buffer;
        }
    }
}
