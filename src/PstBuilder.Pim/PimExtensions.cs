using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PstBuilder.Messaging;

namespace PstBuilder.Pim
{
    /// <summary>
    /// In plain words: the door you pour contact cards (<c>.vcf</c>) and calendar files (<c>.ics</c>)
    /// through — it turns them into contacts, appointments, or tasks in a folder.
    /// Convenience methods for adding vCard/iCalendar items to folders and export sessions.
    /// </summary>
    public static class PimExtensions
    {
        /// <summary>Parses a vCard and adds the contact to this folder.</summary>
        public static MessageItem AddVcard(this FolderItem folder, byte[] vcard) =>
            folder.AddContact(VcardMapper.ToContactItem(vcard));

        /// <summary>Parses an iCalendar VEVENT/VTODO and adds the appointment or task to this folder.</summary>
        public static MessageItem AddIcal(this FolderItem folder, byte[] ics)
        {
            string text = System.Text.Encoding.UTF8.GetString(ics);
            return IcalMapper.IsTask(text)
                ? folder.AddTask(IcalMapper.ToTaskItem(text))
                : folder.AddAppointment(IcalMapper.ToAppointmentItem(text));
        }

        /// <summary>Parses a vCard and queues the contact into the export session. Thread-safe.</summary>
        public static void AddVcard(this PstExportSession session, string folderPath, byte[] vcard)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.AddContact(folderPath, VcardMapper.ToContactItem(vcard));
        }

        /// <summary>Parses an iCalendar item and queues it into the export session. Thread-safe.</summary>
        public static void AddIcal(this PstExportSession session, string folderPath, byte[] ics)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            string text = System.Text.Encoding.UTF8.GetString(ics);
            if (IcalMapper.IsTask(text)) session.AddTask(folderPath, IcalMapper.ToTaskItem(text));
            else session.AddAppointment(folderPath, IcalMapper.ToAppointmentItem(text));
        }

        /// <summary>Awaitable counterpart to <see cref="AddVcard(PstExportSession,string,byte[])"/>.</summary>
        public static Task AddVcardAsync(this PstExportSession session, string folderPath, byte[] vcard,
            CancellationToken cancellationToken = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return session.AddContactAsync(folderPath, VcardMapper.ToContactItem(vcard), cancellationToken);
        }

        /// <summary>Awaitable counterpart to <see cref="AddIcal(PstExportSession,string,byte[])"/>.</summary>
        public static Task AddIcalAsync(this PstExportSession session, string folderPath, byte[] ics,
            CancellationToken cancellationToken = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            string text = System.Text.Encoding.UTF8.GetString(ics);
            return IcalMapper.IsTask(text)
                ? session.AddTaskAsync(folderPath, IcalMapper.ToTaskItem(text), cancellationToken)
                : session.AddAppointmentAsync(folderPath, IcalMapper.ToAppointmentItem(text), cancellationToken);
        }

        /// <summary>Reads a .vcf file and adds the contact to this folder.</summary>
        public static MessageItem AddVcardFile(this FolderItem folder, string path) =>
            folder.AddVcard(File.ReadAllBytes(path));

        /// <summary>Reads an .ics file and adds the item to this folder.</summary>
        public static MessageItem AddIcalFile(this FolderItem folder, string path) =>
            folder.AddIcal(File.ReadAllBytes(path));
    }
}
