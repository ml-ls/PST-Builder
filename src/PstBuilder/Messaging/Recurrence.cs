using System;

namespace PstBuilder.Messaging
{
    /// <summary>Recurrence frequency.</summary>
    public enum RecurrenceFrequency
    {
        /// <summary>Every N days.</summary>
        Daily,
        /// <summary>Every N weeks on the given day(s).</summary>
        Weekly,
        /// <summary>Every N months on the start day-of-month.</summary>
        Monthly,
        /// <summary>Every N years on the start month/day.</summary>
        Yearly,
    }

    /// <summary>How a recurrence ends.</summary>
    public enum RecurrenceEnd
    {
        /// <summary>No end date.</summary>
        Never,
        /// <summary>Ends after a fixed number of occurrences.</summary>
        AfterCount,
        /// <summary>Ends on/after a date.</summary>
        ByDate,
    }

    /// <summary>Days of the week (bit flags; values match the MS-OXOCAL weekly bitmask).</summary>
    [Flags]
    public enum RecurrenceDays
    {
        /// <summary>None.</summary>
        None = 0,
        /// <summary>Sunday.</summary>
        Sunday = 0x01,
        /// <summary>Monday.</summary>
        Monday = 0x02,
        /// <summary>Tuesday.</summary>
        Tuesday = 0x04,
        /// <summary>Wednesday.</summary>
        Wednesday = 0x08,
        /// <summary>Thursday.</summary>
        Thursday = 0x10,
        /// <summary>Friday.</summary>
        Friday = 0x20,
        /// <summary>Saturday.</summary>
        Saturday = 0x40,
    }

    /// <summary>A simple recurrence rule (no per-instance exceptions) attached to an appointment.
    /// In plain words: the "repeat every…" setting for a calendar event.</summary>
    public sealed class Recurrence
    {
        /// <summary>Frequency.</summary>
        public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Weekly;
        /// <summary>Interval (every N days/weeks/months/years). Default 1.</summary>
        public int Interval { get; set; } = 1;
        /// <summary>How the series ends.</summary>
        public RecurrenceEnd End { get; set; } = RecurrenceEnd.Never;
        /// <summary>Occurrence count when <see cref="End"/> is <see cref="RecurrenceEnd.AfterCount"/>.</summary>
        public int Count { get; set; }
        /// <summary>End date (UTC) when <see cref="End"/> is <see cref="RecurrenceEnd.ByDate"/>.</summary>
        public DateTime UntilUtc { get; set; }
        /// <summary>Weekly days; when None, the appointment's start weekday is used.</summary>
        public RecurrenceDays Days { get; set; } = RecurrenceDays.None;
    }
}
