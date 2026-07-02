using System;
using System.Collections.Generic;
using PstBuilder.Ltp;

namespace PstBuilder.Messaging
{
    /// <summary>A contact (IPM.Contact). In plain words: one address-book card.</summary>
    public sealed class ContactItem
    {
        /// <summary>Full display name.</summary>
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>Given (first) name.</summary>
        public string? GivenName { get; set; }
        /// <summary>Surname (last name).</summary>
        public string? Surname { get; set; }
        /// <summary>Company.</summary>
        public string? Company { get; set; }
        /// <summary>Job title.</summary>
        public string? JobTitle { get; set; }
        /// <summary>Primary e-mail address.</summary>
        public string? Email { get; set; }
        /// <summary>Business phone.</summary>
        public string? BusinessPhone { get; set; }
        /// <summary>Mobile phone.</summary>
        public string? MobilePhone { get; set; }
        /// <summary>Free-text notes (body).</summary>
        public string? Notes { get; set; }

        /// <summary>Maps this contact to a message property bag.</summary>
        public MessageItem ToMessageItem()
        {
            string fileAs = !string.IsNullOrEmpty(DisplayName)
                ? DisplayName
                : string.Join(" ", Trim(GivenName), Trim(Surname)).Trim();

            var m = new MessageItem { MessageClass = "IPM.Contact", Subject = fileAs, Body = Notes ?? string.Empty };
            AddText(m.Properties, PropertyTags.DisplayName, DisplayName);
            AddText(m.Properties, PropertyTags.GivenName, GivenName);
            AddText(m.Properties, PropertyTags.Surname, Surname);
            AddText(m.Properties, PropertyTags.CompanyName, Company);
            AddText(m.Properties, PropertyTags.JobTitle, JobTitle);
            AddText(m.Properties, PropertyTags.BusinessTelephoneNumber, BusinessPhone);
            AddText(m.Properties, PropertyTags.MobileTelephoneNumber, MobilePhone);

            m.NamedProperties.Add(NamedProperty.Text(PropertySets.Address, ItemsLids.FileUnder, fileAs));
            if (!string.IsNullOrEmpty(Email))
            {
                m.NamedProperties.Add(NamedProperty.Text(PropertySets.Address, ItemsLids.Email1DisplayName, DisplayName));
                m.NamedProperties.Add(NamedProperty.Text(PropertySets.Address, ItemsLids.Email1AddressType, "SMTP"));
                m.NamedProperties.Add(NamedProperty.Text(PropertySets.Address, ItemsLids.Email1EmailAddress, Email!));
                m.NamedProperties.Add(NamedProperty.Text(PropertySets.Address, ItemsLids.Email1OriginalDisplayName, DisplayName));
            }
            return m;
        }

        private static string Trim(string? s) => s ?? string.Empty;
        private static void AddText(List<Property> list, ushort tag, string? value)
        {
            if (!string.IsNullOrEmpty(value)) list.Add(Property.Unicode(tag, value!));
        }
    }

    /// <summary>A calendar appointment (IPM.Appointment).</summary>
    public sealed class AppointmentItem
    {
        /// <summary>Subject.</summary>
        public string Subject { get; set; } = string.Empty;
        /// <summary>Location.</summary>
        public string? Location { get; set; }
        /// <summary>Body text.</summary>
        public string? Body { get; set; }
        /// <summary>Start time (UTC).</summary>
        public DateTime StartUtc { get; set; }
        /// <summary>End time (UTC).</summary>
        public DateTime EndUtc { get; set; }
        /// <summary>All-day event.</summary>
        public bool AllDay { get; set; }
        /// <summary>Busy status (0=Free,1=Tentative,2=Busy,3=OOF).</summary>
        public int BusyStatus { get; set; } = 2;
        /// <summary>Optional recurrence rule; when set the appointment becomes a recurring series.</summary>
        public Recurrence? Recurrence { get; set; }

        /// <summary>Maps this appointment to a message property bag.</summary>
        public MessageItem ToMessageItem()
        {
            var m = new MessageItem
            {
                MessageClass = "IPM.Appointment",
                Subject = Subject,
                Body = Body ?? string.Empty,
                DeliveryTimeUtc = StartUtc,
            };
            m.NamedProperties.Add(NamedProperty.Time(PropertySets.Appointment, ItemsLids.AppointmentStartWhole, StartUtc));
            m.NamedProperties.Add(NamedProperty.Time(PropertySets.Appointment, ItemsLids.AppointmentEndWhole, EndUtc));
            m.NamedProperties.Add(NamedProperty.Int32(PropertySets.Appointment, ItemsLids.BusyStatus, BusyStatus));
            m.NamedProperties.Add(NamedProperty.Bool(PropertySets.Appointment, ItemsLids.AppointmentSubType, AllDay));
            if (!string.IsNullOrEmpty(Location))
                m.NamedProperties.Add(NamedProperty.Text(PropertySets.Appointment, ItemsLids.Location, Location!));
            if (Recurrence != null)
            {
                byte[] recur = RecurrenceBuilder.Build(Recurrence, StartUtc, EndUtc);
                m.NamedProperties.Add(NamedProperty.Numeric(PropertySets.Appointment, ItemsLids.AppointmentRecur, PropertyType.Binary, recur));
                m.NamedProperties.Add(NamedProperty.Bool(PropertySets.Appointment, ItemsLids.Recurring, true));
                m.NamedProperties.Add(NamedProperty.Int32(PropertySets.Appointment, ItemsLids.RecurrenceType, (int)Recurrence.Frequency + 1));
                m.NamedProperties.Add(NamedProperty.Text(PropertySets.Appointment, ItemsLids.RecurrencePattern, Recurrence.Frequency.ToString()));
            }
            return m;
        }
    }

    /// <summary>A task (IPM.Task).</summary>
    public sealed class TaskItem
    {
        /// <summary>Subject.</summary>
        public string Subject { get; set; } = string.Empty;
        /// <summary>Body text.</summary>
        public string? Body { get; set; }
        /// <summary>Status (0=NotStarted,1=InProgress,2=Complete,3=Waiting,4=Deferred).</summary>
        public int Status { get; set; }
        /// <summary>Percent complete (0.0–1.0).</summary>
        public double PercentComplete { get; set; }
        /// <summary>Marked complete.</summary>
        public bool Complete { get; set; }
        /// <summary>Optional start date (UTC).</summary>
        public DateTime? StartUtc { get; set; }
        /// <summary>Optional due date (UTC).</summary>
        public DateTime? DueUtc { get; set; }

        /// <summary>Maps this task to a message property bag.</summary>
        public MessageItem ToMessageItem()
        {
            var m = new MessageItem { MessageClass = "IPM.Task", Subject = Subject, Body = Body ?? string.Empty };
            m.NamedProperties.Add(NamedProperty.Int32(PropertySets.Task, ItemsLids.TaskStatus, Status));
            m.NamedProperties.Add(NamedProperty.Double(PropertySets.Task, ItemsLids.PercentComplete, PercentComplete));
            m.NamedProperties.Add(NamedProperty.Bool(PropertySets.Task, ItemsLids.TaskComplete, Complete));
            if (StartUtc.HasValue)
                m.NamedProperties.Add(NamedProperty.Time(PropertySets.Task, ItemsLids.TaskStartDate, StartUtc.Value));
            if (DueUtc.HasValue)
                m.NamedProperties.Add(NamedProperty.Time(PropertySets.Task, ItemsLids.TaskDueDate, DueUtc.Value));
            return m;
        }
    }

    /// <summary>A sticky note (IPM.StickyNote).</summary>
    public sealed class NoteItem
    {
        /// <summary>Title (subject).</summary>
        public string Subject { get; set; } = string.Empty;
        /// <summary>Note text (body).</summary>
        public string Body { get; set; } = string.Empty;
        /// <summary>Color (0=Blue,1=Green,2=Pink,3=Yellow,4=White).</summary>
        public int Color { get; set; } = 3;

        /// <summary>Maps this note to a message property bag.</summary>
        public MessageItem ToMessageItem()
        {
            var m = new MessageItem { MessageClass = "IPM.StickyNote", Subject = Subject, Body = Body };
            m.NamedProperties.Add(NamedProperty.Int32(PropertySets.Note, ItemsLids.NoteColor, Color));
            return m;
        }
    }

    /// <summary>LIDs shared by the item mappers (kept internal to avoid leaking magic numbers).</summary>
    internal static class ItemsLids
    {
        public const uint FileUnder = 0x8005;
        public const uint Email1DisplayName = 0x8080;
        public const uint Email1AddressType = 0x8082;
        public const uint Email1EmailAddress = 0x8083;
        public const uint Email1OriginalDisplayName = 0x8084;
        public const uint BusyStatus = 0x8205;
        public const uint Location = 0x8208;
        public const uint AppointmentStartWhole = 0x820D;
        public const uint AppointmentEndWhole = 0x820E;
        public const uint AppointmentSubType = 0x8215;
        public const uint AppointmentRecur = 0x8216;
        public const uint Recurring = 0x8223;
        public const uint RecurrenceType = 0x8231;
        public const uint RecurrencePattern = 0x8232;
        public const uint TaskStatus = 0x8101;
        public const uint PercentComplete = 0x8102;
        public const uint TaskStartDate = 0x8104;
        public const uint TaskDueDate = 0x8105;
        public const uint TaskComplete = 0x811C;
        public const uint NoteColor = 0x8B00;
    }

    /// <summary>Convenience methods for adding typed items to a folder.</summary>
    public static class FolderItemExtensions
    {
        /// <summary>Adds a contact.</summary>
        public static MessageItem AddContact(this FolderItem folder, ContactItem contact) =>
            folder.AddMessage(contact.ToMessageItem());
        /// <summary>Adds an appointment.</summary>
        public static MessageItem AddAppointment(this FolderItem folder, AppointmentItem appt) =>
            folder.AddMessage(appt.ToMessageItem());
        /// <summary>Adds a task.</summary>
        public static MessageItem AddTask(this FolderItem folder, TaskItem task) =>
            folder.AddMessage(task.ToMessageItem());
        /// <summary>Adds a note.</summary>
        public static MessageItem AddNote(this FolderItem folder, NoteItem note) =>
            folder.AddMessage(note.ToMessageItem());
    }
}
