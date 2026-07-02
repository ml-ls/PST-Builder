using System;

namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: the well-known "family names" (GUIDs) that group named fields — e.g. the contact
    /// family vs. the calendar family — so a field name means the right thing.
    /// Well-known MAPI property-set GUIDs ([MS-OXPROPS] 1.3.2) used by named properties on contacts,
    /// appointments, tasks, and notes.
    /// </summary>
    public static class PropertySets
    {
        /// <summary>PS_MAPI.</summary>
        public static readonly Guid Mapi = new Guid("00020328-0000-0000-C000-000000000046");
        /// <summary>PS_PUBLIC_STRINGS.</summary>
        public static readonly Guid PublicStrings = new Guid("00020329-0000-0000-C000-000000000046");
        /// <summary>PSETID_Common.</summary>
        public static readonly Guid Common = new Guid("00062008-0000-0000-C000-000000000046");
        /// <summary>PSETID_Address (contacts).</summary>
        public static readonly Guid Address = new Guid("00062004-0000-0000-C000-000000000046");
        /// <summary>PSETID_Appointment (calendar).</summary>
        public static readonly Guid Appointment = new Guid("00062002-0000-0000-C000-000000000046");
        /// <summary>PSETID_Task (tasks).</summary>
        public static readonly Guid Task = new Guid("00062003-0000-0000-C000-000000000046");
        /// <summary>PSETID_Note (sticky notes).</summary>
        public static readonly Guid Note = new Guid("0006200E-0000-0000-C000-000000000046");
    }
}
