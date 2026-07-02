using System;
using System.IO;
using System.Linq;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    public class NamedPropertyTests
    {
        [Fact]
        public void Registry_AssignsStableNpids_AndSerializesStreams()
        {
            var reg = new NamedPropertyRegistry();
            ushort a = reg.Resolve(NamedProperty.Int32(PropertySets.Task, 0x8101, 0));
            ushort b = reg.Resolve(NamedProperty.Text(PropertySets.Address, 0x8083, "x@y.com"));
            ushort aAgain = reg.Resolve(NamedProperty.Int32(PropertySets.Task, 0x8101, 5));

            Assert.Equal(0x8000, a);
            Assert.Equal(0x8001, b);
            Assert.Equal(a, aAgain); // same (set,id) -> same NPID

            // Two distinct property sets -> two GUIDs in the GUID stream.
            Assert.Equal(32, reg.GuidStream().Length);

            // Entry stream: 2 NAMEID records (8 bytes each); first is numeric LID 0x8101, wPropIdx 0.
            byte[] entries = reg.EntryStream();
            Assert.Equal(16, entries.Length);
            Assert.Equal(0x8101u, BitConverter.ToUInt32(entries, 0));
            ushort guidField = BitConverter.ToUInt16(entries, 4);
            Assert.Equal(0, guidField & 1);                 // N=0 (numeric)
            Assert.Equal(3, guidField >> 1);                // first custom GUID -> wGuid 3
            Assert.Equal(0, BitConverter.ToUInt16(entries, 6)); // wPropIdx 0
        }

        [Fact]
        public void Buckets_PlaceEntriesByTheSpecFormula()
        {
            var reg = new NamedPropertyRegistry();
            // Numeric named prop: bucket key = LID, guidField = (guidIndex<<1)|0.
            reg.Resolve(NamedProperty.Int32(PropertySets.Task, 0x8101, 0));   // wPropIdx 0, guidIndex 3
            reg.Resolve(NamedProperty.Int32(PropertySets.Common, 0x8503, 0)); // wPropIdx 1, guidIndex 4

            var buckets = reg.Buckets().ToDictionary(kv => kv.Key, kv => kv.Value);

            // Entry 0: key=0x8101, guidField=(3<<1)|0=6 -> bucket (0x8101 ^ 6) % 251.
            int expected0 = (int)((0x8101u ^ 6u) % 251);
            Assert.True(buckets.ContainsKey(expected0));
            byte[] rec = buckets[expected0];
            Assert.Equal(8, rec.Length);
            Assert.Equal(0x8101u, BitConverter.ToUInt32(rec, 0)); // bucket key
            Assert.Equal(6, BitConverter.ToUInt16(rec, 4));       // (wGuid<<1)|N
            Assert.Equal(0, BitConverter.ToUInt16(rec, 6));       // wPropIdx
        }

        [Fact]
        public void Store_WithAllItemTypes_RoundTripsAndPopulatesMap()
        {
            var store = new StoreWriter();

            var contacts = store.IpmSubtree.AddFolder("Contacts");
            contacts.ContainerClass = "IPF.Contact";
            contacts.AddContact(new ContactItem { DisplayName = "Jane Doe", Email = "jane@example.com", Company = "Acme" });

            var calendar = store.IpmSubtree.AddFolder("Calendar");
            calendar.ContainerClass = "IPF.Appointment";
            calendar.AddAppointment(new AppointmentItem
            {
                Subject = "Standup", Location = "Room 1",
                StartUtc = new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 6, 25, 9, 30, 0, DateTimeKind.Utc),
            });

            var tasks = store.IpmSubtree.AddFolder("Tasks");
            tasks.ContainerClass = "IPF.Task";
            tasks.AddTask(new TaskItem { Subject = "Ship PST builder", DueUtc = DateTime.UtcNow, PercentComplete = 0.5 });

            var notes = store.IpmSubtree.AddFolder("Notes");
            notes.ContainerClass = "IPF.StickyNote";
            notes.AddNote(new NoteItem { Subject = "Remember", Body = "Buy milk", Color = 1 });

            byte[] file;
            using (var ms = new MemoryStream())
            {
                using (var os = new PstOutputStream(ms, leaveOpen: true))
                    store.Write(os);
                file = ms.ToArray();
            }

            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();

            // Four item messages exist.
            Assert.Equal(4, reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage));

            // The Name-to-ID map node's PC carries a non-empty Entry stream (named props were registered).
            byte[] mapBlock = reader.GetBlockData(reader.Nodes[Nid.NameToIdMap.Value].BidData);
            Assert.Equal(0xEC, mapBlock[2]); // HN signature
            Assert.Equal(0xBC, mapBlock[3]); // PC client signature
        }
    }
}
