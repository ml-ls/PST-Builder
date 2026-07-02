using System;
using System.Globalization;
using System.Text;
using PstBuilder.Messaging;

namespace PstBuilder.Pim
{
    /// <summary>
    /// In plain words: reads a calendar file (.ics) and fills in one of our appointments or tasks.
    /// Maps iCalendar (.ics) components to PstBuilder items: VEVENT → <see cref="AppointmentItem"/>,
    /// VTODO → <see cref="TaskItem"/>. Covers common fields (SUMMARY, LOCATION, DESCRIPTION, DTSTART,
    /// DTEND, DUE, STATUS, PERCENT-COMPLETE). Time zones other than UTC are treated as UTC (the common
    /// case for backup exports, which usually emit Z-suffixed times).
    /// </summary>
    public static class IcalMapper
    {
        /// <summary>Parses the first VEVENT into an appointment.</summary>
        public static AppointmentItem ToAppointmentItem(byte[] ics) => ToAppointmentItem(Decode(ics));

        /// <summary>Parses the first VEVENT into an appointment.</summary>
        public static AppointmentItem ToAppointmentItem(string ics)
        {
            var a = new AppointmentItem();
            bool inEvent = false, allDay = false;
            foreach (var line in ContentLine.Parse(ics))
            {
                if (line.Name == "BEGIN" && line.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) { inEvent = true; continue; }
                if (!inEvent) continue;
                if (line.Name == "END" && line.Value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)) break;
                switch (line.Name)
                {
                    case "SUMMARY": a.Subject = line.Value; break;
                    case "LOCATION": a.Location = line.Value; break;
                    case "DESCRIPTION": a.Body = line.Value; break;
                    case "DTSTART": a.StartUtc = ParseDate(line, out allDay); break;
                    case "DTEND": a.EndUtc = ParseDate(line, out _); break;
                    case "RRULE": a.Recurrence = ParseRRule(line.Value); break;
                }
            }
            a.AllDay = allDay;
            if (a.EndUtc == default) a.EndUtc = a.StartUtc;
            return a;
        }

        /// <summary>Parses the first VTODO into a task.</summary>
        public static TaskItem ToTaskItem(byte[] ics) => ToTaskItem(Decode(ics));

        /// <summary>Parses the first VTODO into a task.</summary>
        public static TaskItem ToTaskItem(string ics)
        {
            var t = new TaskItem();
            bool inTodo = false;
            foreach (var line in ContentLine.Parse(ics))
            {
                if (line.Name == "BEGIN" && line.Value.Equals("VTODO", StringComparison.OrdinalIgnoreCase)) { inTodo = true; continue; }
                if (!inTodo) continue;
                if (line.Name == "END" && line.Value.Equals("VTODO", StringComparison.OrdinalIgnoreCase)) break;
                switch (line.Name)
                {
                    case "SUMMARY": t.Subject = line.Value; break;
                    case "DESCRIPTION": t.Body = line.Value; break;
                    case "DUE": t.DueUtc = ParseDate(line, out _); break;
                    case "DTSTART": t.StartUtc = ParseDate(line, out _); break;
                    case "PERCENT-COMPLETE":
                        if (int.TryParse(line.Value, out int pct)) t.PercentComplete = pct / 100.0;
                        break;
                    case "STATUS":
                        if (line.Value.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) { t.Complete = true; t.Status = 2; }
                        else if (line.Value.Equals("IN-PROCESS", StringComparison.OrdinalIgnoreCase)) t.Status = 1;
                        break;
                }
            }
            return t;
        }

        /// <summary>True if the stream contains a VTODO (vs a VEVENT).</summary>
        public static bool IsTask(string ics) => ics.IndexOf("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase) >= 0;

        private static Recurrence ParseRRule(string value)
        {
            var r = new Recurrence();
            foreach (var part in value.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string key = part.Substring(0, eq).Trim().ToUpperInvariant();
                string val = part.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "FREQ":
                        r.Frequency = val.ToUpperInvariant() switch
                        {
                            "DAILY" => RecurrenceFrequency.Daily,
                            "WEEKLY" => RecurrenceFrequency.Weekly,
                            "MONTHLY" => RecurrenceFrequency.Monthly,
                            "YEARLY" => RecurrenceFrequency.Yearly,
                            _ => r.Frequency,
                        };
                        break;
                    case "INTERVAL":
                        if (int.TryParse(val, out int iv)) r.Interval = iv;
                        break;
                    case "COUNT":
                        if (int.TryParse(val, out int c)) { r.End = RecurrenceEnd.AfterCount; r.Count = c; }
                        break;
                    case "UNTIL":
                        if (DateTime.TryParseExact(val.TrimEnd('Z'), new[] { "yyyyMMdd'T'HHmmss", "yyyyMMdd" },
                            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until))
                        { r.End = RecurrenceEnd.ByDate; r.UntilUtc = DateTime.SpecifyKind(until, DateTimeKind.Utc); }
                        break;
                    case "BYDAY":
                        r.Days = ParseByDay(val);
                        break;
                }
            }
            return r;
        }

        private static RecurrenceDays ParseByDay(string value)
        {
            RecurrenceDays days = RecurrenceDays.None;
            foreach (var token in value.Split(','))
            {
                string t = token.Trim().ToUpperInvariant();
                if (t.EndsWith("SU")) days |= RecurrenceDays.Sunday;
                else if (t.EndsWith("MO")) days |= RecurrenceDays.Monday;
                else if (t.EndsWith("TU")) days |= RecurrenceDays.Tuesday;
                else if (t.EndsWith("WE")) days |= RecurrenceDays.Wednesday;
                else if (t.EndsWith("TH")) days |= RecurrenceDays.Thursday;
                else if (t.EndsWith("FR")) days |= RecurrenceDays.Friday;
                else if (t.EndsWith("SA")) days |= RecurrenceDays.Saturday;
            }
            return days;
        }

        private static DateTime ParseDate(ContentLine line, out bool dateOnly)
        {
            string v = line.Value.Trim();
            bool isDate = string.Equals(line.Param("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase)
                          || (v.Length == 8 && v.IndexOf('T') < 0);
            dateOnly = isDate;

            string[] formats =
            {
                "yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmmss", "yyyyMMdd",
            };
            if (DateTime.TryParseExact(v, formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            return DateTime.UtcNow;
        }

        private static string Decode(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
