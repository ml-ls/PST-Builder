using System;
using System.IO;
using System.Linq;
using System.Text;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Pim;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    public class RecurrenceTests
    {
        [Fact]
        public void WeeklyRecurrence_Blob_HasExpectedHeaderFields()
        {
            var start = new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc); // Thursday
            var end = new DateTime(2026, 6, 25, 10, 0, 0, DateTimeKind.Utc);
            var r = new Recurrence
            {
                Frequency = RecurrenceFrequency.Weekly, Interval = 2,
                End = RecurrenceEnd.AfterCount, Count = 5, Days = RecurrenceDays.Thursday,
            };

            byte[] blob = RecurrenceBuilder.Build(r, start, end);

            Assert.Equal(0x3004, BitConverter.ToUInt16(blob, 0));   // ReaderVersion
            Assert.Equal(0x3004, BitConverter.ToUInt16(blob, 2));   // WriterVersion
            Assert.Equal(0x200B, BitConverter.ToUInt16(blob, 4));   // RecurFrequency = Weekly
            Assert.Equal(0x0001, BitConverter.ToUInt16(blob, 6));   // PatternType = Week
            Assert.Equal(2u, BitConverter.ToUInt32(blob, 14));      // Period = 2 weeks
            Assert.Equal((uint)RecurrenceDays.Thursday, BitConverter.ToUInt32(blob, 22)); // PatternTypeSpecific bitmask
            Assert.Equal(0x2022u, BitConverter.ToUInt32(blob, 26)); // EndType = after N
            Assert.Equal(5u, BitConverter.ToUInt32(blob, 30));      // OccurrenceCount

            // AppointmentRecurrencePattern follows the 54-byte weekly RecurrencePattern.
            const int arpStart = 54;
            Assert.Equal(0x3006u, BitConverter.ToUInt32(blob, arpStart));        // ReaderVersion2
            Assert.Equal(540u, BitConverter.ToUInt32(blob, arpStart + 8));       // StartTimeOffset = 9:00 = 540
            Assert.Equal(600u, BitConverter.ToUInt32(blob, arpStart + 12));      // EndTimeOffset = 10:00 = 600
        }

        [Fact]
        public void RecurringAppointment_RoundTripsInStore()
        {
            var store = new StoreWriter();
            var cal = store.IpmSubtree.AddFolder("Calendar");
            cal.ContainerClass = "IPF.Appointment";
            cal.AddAppointment(new AppointmentItem
            {
                Subject = "Standup",
                StartUtc = new DateTime(2026, 6, 25, 9, 0, 0, DateTimeKind.Utc),
                EndUtc = new DateTime(2026, 6, 25, 9, 15, 0, DateTimeKind.Utc),
                Recurrence = new Recurrence { Frequency = RecurrenceFrequency.Daily, Interval = 1, End = RecurrenceEnd.Never },
            });

            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
                store.Write(os);

            new NdbRoundTripReader(ms.ToArray()).ReadAndValidate();
        }

        [Fact]
        public void Ical_RRule_ParsesToRecurrence()
        {
            string ics =
                "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Weekly sync\r\n" +
                "DTSTART:20260625T090000Z\r\nDTEND:20260625T093000Z\r\n" +
                "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH;COUNT=10\r\n" +
                "END:VEVENT\r\nEND:VCALENDAR\r\n";

            var a = IcalMapper.ToAppointmentItem(Encoding.UTF8.GetBytes(ics));
            Assert.NotNull(a.Recurrence);
            Assert.Equal(RecurrenceFrequency.Weekly, a.Recurrence!.Frequency);
            Assert.Equal(2, a.Recurrence.Interval);
            Assert.Equal(RecurrenceEnd.AfterCount, a.Recurrence.End);
            Assert.Equal(10, a.Recurrence.Count);
            Assert.Equal(RecurrenceDays.Tuesday | RecurrenceDays.Thursday, a.Recurrence.Days);
        }
    }
}
