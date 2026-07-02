using System;
using System.IO;

namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: turns "every Tuesday" into the exact bytes Outlook uses for a repeating appointment.
    /// Builds the binary value of PidLidAppointmentRecur (MS-OXOCAL 2.2.1.44): a RecurrencePattern
    /// followed by the AppointmentRecurrencePattern fields. Supports daily/weekly/monthly/yearly series
    /// with an interval and a Never/AfterCount/ByDate end. Per-instance exceptions are not emitted.
    /// </summary>
    public static class RecurrenceBuilder
    {
        private static readonly DateTime Epoch1601 = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const uint NeverEndDate = 0x5AE980DF;

        /// <summary>Builds the PidLidAppointmentRecur blob for the given rule and appointment times (UTC).</summary>
        public static byte[] Build(Recurrence r, DateTime startUtc, DateTime endUtc)
        {
            if (r == null) throw new ArgumentNullException(nameof(r));
            int interval = Math.Max(1, r.Interval);

            ushort recurFrequency, patternType;
            uint period;
            byte[] patternTypeSpecific;
            switch (r.Frequency)
            {
                case RecurrenceFrequency.Daily:
                    recurFrequency = 0x200A; patternType = 0x0000; period = (uint)(interval * 1440);
                    patternTypeSpecific = Array.Empty<byte>();
                    break;
                case RecurrenceFrequency.Weekly:
                    recurFrequency = 0x200B; patternType = 0x0001; period = (uint)interval;
                    uint mask = (uint)(r.Days != RecurrenceDays.None ? r.Days : (RecurrenceDays)(1 << (int)startUtc.DayOfWeek));
                    patternTypeSpecific = BitConverter.GetBytes(mask);
                    break;
                case RecurrenceFrequency.Monthly:
                    recurFrequency = 0x200C; patternType = 0x0002; period = (uint)interval;
                    patternTypeSpecific = BitConverter.GetBytes((uint)startUtc.Day);
                    break;
                default: // Yearly
                    recurFrequency = 0x200D; patternType = 0x0002; period = 12;
                    patternTypeSpecific = BitConverter.GetBytes((uint)startUtc.Day);
                    break;
            }

            uint startDate = Minutes1601(startUtc.Date);
            uint firstDateTime = FirstDateTime(r.Frequency, startUtc, period, startDate);

            uint endType;
            uint occurrenceCount;
            uint endDate;
            switch (r.End)
            {
                case RecurrenceEnd.AfterCount:
                    endType = 0x00002022;
                    occurrenceCount = (uint)Math.Max(1, r.Count);
                    endDate = Minutes1601(LastOccurrence(r, startUtc, (int)occurrenceCount).Date);
                    break;
                case RecurrenceEnd.ByDate:
                    endType = 0x00002021;
                    endDate = Minutes1601(r.UntilUtc.Date);
                    occurrenceCount = (uint)Math.Max(1, EstimateCount(r, startUtc, r.UntilUtc));
                    break;
                default:
                    endType = 0x00002023;
                    occurrenceCount = 0x0000000A;
                    endDate = NeverEndDate;
                    break;
            }

            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms); // BinaryWriter is little-endian
            // RecurrencePattern
            w.Write((ushort)0x3004); // ReaderVersion
            w.Write((ushort)0x3004); // WriterVersion
            w.Write(recurFrequency);
            w.Write(patternType);
            w.Write((ushort)0x0000); // CalendarType
            w.Write(firstDateTime);
            w.Write(period);
            w.Write((uint)0);        // SlidingFlag
            w.Write(patternTypeSpecific);
            w.Write(endType);
            w.Write(occurrenceCount);
            w.Write((uint)0);        // FirstDOW = Sunday
            w.Write((uint)0);        // DeletedInstanceCount
            w.Write((uint)0);        // ModifiedInstanceCount
            w.Write(startDate);
            w.Write(endDate);
            // AppointmentRecurrencePattern
            w.Write((uint)0x00003006); // ReaderVersion2
            w.Write((uint)0x00003008); // WriterVersion2 (< 0x3009 => no ChangeHighlight blocks)
            w.Write((uint)startUtc.TimeOfDay.TotalMinutes); // StartTimeOffset
            w.Write((uint)endUtc.TimeOfDay.TotalMinutes);   // EndTimeOffset
            w.Write((ushort)0);      // ExceptionCount
            w.Write((uint)0);        // ReservedBlock1Size
            w.Write((uint)0);        // ReservedBlock2Size
            w.Flush();
            return ms.ToArray();
        }

        private static uint Minutes1601(DateTime date) =>
            (uint)(DateTime.SpecifyKind(date, DateTimeKind.Utc) - Epoch1601).TotalMinutes;

        private static uint FirstDateTime(RecurrenceFrequency freq, DateTime startUtc, uint period, uint startDate)
        {
            switch (freq)
            {
                case RecurrenceFrequency.Daily:
                    return startDate % period;
                case RecurrenceFrequency.Weekly:
                    int back = ((int)startUtc.DayOfWeek - 0 + 7) % 7; // weeks begin Sunday (FirstDOW = 0)
                    uint weekStart = Minutes1601(startUtc.Date.AddDays(-back));
                    return weekStart % (period * 10080);
                default: // Monthly / Yearly
                    int monthsSince1601 = (startUtc.Year - 1601) * 12 + (startUtc.Month - 1);
                    DateTime fdt = Epoch1601.AddMonths((int)(monthsSince1601 % period));
                    return Minutes1601(fdt);
            }
        }

        private static DateTime LastOccurrence(Recurrence r, DateTime startUtc, int count)
        {
            int n = count - 1;
            int interval = Math.Max(1, r.Interval);
            switch (r.Frequency)
            {
                case RecurrenceFrequency.Daily: return startUtc.AddDays((long)n * interval);
                case RecurrenceFrequency.Weekly: return startUtc.AddDays((long)n * interval * 7);
                case RecurrenceFrequency.Monthly: return startUtc.AddMonths(n * interval);
                default: return startUtc.AddMonths(n * 12);
            }
        }

        private static int EstimateCount(Recurrence r, DateTime startUtc, DateTime untilUtc)
        {
            int interval = Math.Max(1, r.Interval);
            switch (r.Frequency)
            {
                case RecurrenceFrequency.Daily: return (int)((untilUtc.Date - startUtc.Date).TotalDays / interval) + 1;
                case RecurrenceFrequency.Weekly: return (int)((untilUtc.Date - startUtc.Date).TotalDays / (interval * 7)) + 1;
                case RecurrenceFrequency.Monthly:
                    return ((untilUtc.Year - startUtc.Year) * 12 + (untilUtc.Month - startUtc.Month)) / interval + 1;
                default: return (untilUtc.Year - startUtc.Year) + 1;
            }
        }
    }
}
