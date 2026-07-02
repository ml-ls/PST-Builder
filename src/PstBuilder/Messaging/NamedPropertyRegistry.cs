using System;
using System.Collections.Generic;
using System.Text;
using PstBuilder.Foundation;

namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: a dictionary for special fields (used by contacts/calendar/tasks) that gives each
    /// one its own number so the reader knows what it means.
    /// Store-wide registry that assigns NPIDs (≥ 0x8000) to named properties and serializes the three
    /// streams of the Name-to-ID map (MS-PST 2.4.7): the Entry stream (NAMEID records), the GUID stream,
    /// and the String stream. One registry per store; the same (set, id) always resolves to one NPID.
    /// </summary>
    public sealed class NamedPropertyRegistry
    {
        private const ushort FirstNpid = 0x8000;

        /// <summary>Recommended hash-bucket count (MS-PST 2.4.7.4); readers MUST read the stored value.</summary>
        public const int BucketCount = 251;

        private struct Entry
        {
            public uint DwPropertyId; // Entry stream: numeric id, or string-stream offset when IsString
            public uint BucketKey;    // Bucket hash key: numeric id, or CRC32 of the name when IsString
            public ushort GuidIndex;  // 1=PS_MAPI, 2=PS_PUBLIC_STRINGS, >=3 -> GUID stream
            public bool IsString;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<Guid> _guids = new List<Guid>();              // custom GUIDs (wGuid >= 3)
        private readonly List<byte> _stringStream = new List<byte>();
        private readonly Dictionary<(Guid, uint), ushort> _numericCache = new Dictionary<(Guid, uint), ushort>();
        private readonly Dictionary<(Guid, string), ushort> _stringCache = new Dictionary<(Guid, string), ushort>();

        /// <summary>Resolves a named property to its NPID, assigning a new one on first use.</summary>
        public ushort Resolve(NamedProperty named)
        {
            if (named.Lid.HasValue) return ResolveNumeric(named.Set, named.Lid.Value);
            return ResolveString(named.Set, named.Name ?? string.Empty);
        }

        private ushort ResolveNumeric(Guid set, uint lid)
        {
            if (_numericCache.TryGetValue((set, lid), out var existing)) return existing;
            ushort npid = (ushort)(FirstNpid + _entries.Count);
            _entries.Add(new Entry { DwPropertyId = lid, BucketKey = lid, GuidIndex = GuidIndex(set), IsString = false });
            _numericCache[(set, lid)] = npid;
            return npid;
        }

        private ushort ResolveString(Guid set, string name)
        {
            if (_stringCache.TryGetValue((set, name), out var existing)) return existing;
            uint offset = (uint)_stringStream.Count;
            byte[] nameBytes = Encoding.Unicode.GetBytes(name);
            AppendUInt32(_stringStream, (uint)nameBytes.Length);
            _stringStream.AddRange(nameBytes);
            while (_stringStream.Count % 4 != 0) _stringStream.Add(0); // 4-byte alignment

            ushort npid = (ushort)(FirstNpid + _entries.Count);
            _entries.Add(new Entry
            {
                DwPropertyId = offset,                 // Entry stream points at the string
                BucketKey = Crc.Compute(nameBytes),    // bucket hashes by CRC32 of the name
                GuidIndex = GuidIndex(set),
                IsString = true,
            });
            _stringCache[(set, name)] = npid;
            return npid;
        }

        private ushort GuidIndex(Guid set)
        {
            if (set == PropertySets.Mapi) return 1;
            if (set == PropertySets.PublicStrings) return 2;
            int pos = _guids.IndexOf(set);
            if (pos < 0) { _guids.Add(set); pos = _guids.Count - 1; }
            return (ushort)(pos + 3);
        }

        /// <summary>The GUID stream (PidTagNameidStreamGuid, 0x0002).</summary>
        public byte[] GuidStream()
        {
            var buffer = new byte[_guids.Count * 16];
            for (int i = 0; i < _guids.Count; i++)
                _guids[i].ToByteArray().CopyTo(buffer, i * 16);
            return buffer;
        }

        /// <summary>The Entry stream (PidTagNameidStreamEntry, 0x0003): one 8-byte NAMEID per property.</summary>
        public byte[] EntryStream()
        {
            var buffer = new byte[_entries.Count * 8];
            var w = new SpanWriter(buffer);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                w.WriteUInt32(e.DwPropertyId);
                w.WriteUInt16((ushort)((e.GuidIndex << 1) | (e.IsString ? 1 : 0)));
                w.WriteUInt16((ushort)i); // wPropIdx (NPID = 0x8000 + i)
            }
            return buffer;
        }

        /// <summary>The String stream (PidTagNameidStreamString, 0x0004).</summary>
        public byte[] StringStream() => _stringStream.ToArray();

        /// <summary>
        /// The non-empty hash buckets, keyed by bucket index (property id = 0x1000 + index). Each value
        /// is a flat array of 8-byte bucket NAMEID records (key + (wGuid&lt;&lt;1|N) + wPropIdx).
        /// </summary>
        public IEnumerable<KeyValuePair<int, byte[]>> Buckets()
        {
            var map = new Dictionary<int, List<byte>>();
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                ushort guidField = (ushort)((e.GuidIndex << 1) | (e.IsString ? 1 : 0));
                int bucket = (int)((e.BucketKey ^ guidField) % BucketCount);
                if (!map.TryGetValue(bucket, out var list)) { list = new List<byte>(); map[bucket] = list; }
                AppendUInt32(list, e.BucketKey);
                list.Add((byte)guidField); list.Add((byte)(guidField >> 8));
                list.Add((byte)i); list.Add((byte)(i >> 8)); // wPropIdx
            }
            foreach (var kv in map)
                yield return new KeyValuePair<int, byte[]>(kv.Key, kv.Value.ToArray());
        }

        private static void AppendUInt32(List<byte> dest, uint value)
        {
            dest.Add((byte)value);
            dest.Add((byte)(value >> 8));
            dest.Add((byte)(value >> 16));
            dest.Add((byte)(value >> 24));
        }
    }
}
