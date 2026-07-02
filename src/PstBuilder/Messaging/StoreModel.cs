using System;
using System.Collections.Generic;
using System.IO;
using PstBuilder.Foundation;

namespace PstBuilder.Messaging
{
    /// <summary>A recipient of a message.</summary>
    public sealed class RecipientItem
    {
        /// <summary>Display name.</summary>
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>Email address.</summary>
        public string EmailAddress { get; set; } = string.Empty;
        /// <summary>Address type (e.g. "SMTP").</summary>
        public string AddressType { get; set; } = "SMTP";
        /// <summary>Recipient type (1 = To).</summary>
        public int RecipientType { get; set; } = PropertyTags.RecipTypeTo;
    }

    /// <summary>
    /// A file attachment on a message. In plain words: a file clipped to an email.
    ///
    /// <para>Give it content one of two ways. For small/in-memory data set <see cref="Content"/> (stored
    /// by value). For a large file, use <see cref="OpenContent"/> + <see cref="ContentLength"/> (or the
    /// <see cref="FromFile(string,string,string)"/> / <see cref="FromStream"/> helpers): the bytes are then
    /// read on demand and streamed to disk in blocks, so a multi-gigabyte attachment never has to be
    /// loaded into a single array. When both are set, the stream source wins.</para>
    /// </summary>
    public sealed class AttachmentItem
    {
        /// <summary>Display/file name (e.g. "report.pdf").</summary>
        public string FileName { get; set; } = string.Empty;
        /// <summary>Raw attachment bytes (stored by value). Ignored when <see cref="OpenContent"/> is set.</summary>
        public byte[] Content { get; set; } = Array.Empty<byte>();
        /// <summary>
        /// Opens a fresh, readable stream over the attachment content. When set, the bytes are read on
        /// demand and streamed to disk (never fully buffered), and <see cref="ContentLength"/> must give the
        /// exact byte count. Invoked once; the returned stream is disposed by the writer.
        /// </summary>
        public Func<Stream>? OpenContent { get; set; }
        /// <summary>Exact byte length of the streamed content. Required (and only used) when <see cref="OpenContent"/> is set.</summary>
        public long ContentLength { get; set; }
        /// <summary>MIME content type (e.g. "application/pdf"). Optional.</summary>
        public string? MimeType { get; set; }
        /// <summary>Content-ID for inline (cid:) attachments. Optional.</summary>
        public string? ContentId { get; set; }
        /// <summary>True for an inline (rendered-in-body) attachment.</summary>
        public bool IsInline { get; set; }

        /// <summary>
        /// Creates a streamed attachment backed by a file on disk: the file is opened and read block by
        /// block at write time, so its size never affects memory use. The file must still exist (and be
        /// unchanged in length) when the export runs.
        /// </summary>
        public static AttachmentItem FromFile(string path, string? fileName = null, string? mimeType = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            long length = new FileInfo(path).Length;
            return new AttachmentItem
            {
                FileName = fileName ?? Path.GetFileName(path),
                MimeType = mimeType,
                ContentLength = length,
                OpenContent = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
            };
        }

        /// <summary>
        /// Creates a streamed attachment from an arbitrary stream source. <paramref name="open"/> must
        /// return a fresh readable stream and <paramref name="length"/> its exact byte count.
        /// </summary>
        public static AttachmentItem FromStream(string fileName, Func<Stream> open, long length, string? mimeType = null)
        {
            if (open == null) throw new ArgumentNullException(nameof(open));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            return new AttachmentItem
            {
                FileName = fileName ?? string.Empty,
                MimeType = mimeType,
                ContentLength = length,
                OpenContent = open,
            };
        }
    }

    /// <summary>A mail message to place in a folder. In plain words: one email (or, with a different
    /// message class, one contact/appointment/task/note) you want to put in the box.</summary>
    public sealed class MessageItem
    {
        /// <summary>Message class. Defaults to IPM.Note (a mail item).</summary>
        public string MessageClass { get; set; } = "IPM.Note";
        /// <summary>Subject line.</summary>
        public string Subject { get; set; } = string.Empty;
        /// <summary>Plain-text body.</summary>
        public string Body { get; set; } = string.Empty;
        /// <summary>Optional HTML body. When set, written as PidTagHtml so viewers render rich content.</summary>
        public string? BodyHtml { get; set; }
        /// <summary>"To" display string.</summary>
        public string DisplayTo { get; set; } = string.Empty;
        /// <summary>Sender display name (shown in the "From" column).</summary>
        public string SenderName { get; set; } = string.Empty;
        /// <summary>Sender email address.</summary>
        public string SenderEmail { get; set; } = string.Empty;
        /// <summary>Message flags (0x01 = read).</summary>
        public int Flags { get; set; } = PropertyTags.MsgFlagRead;
        /// <summary>Delivery time (UTC).</summary>
        public DateTime DeliveryTimeUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Submit time (UTC).</summary>
        public DateTime SubmitTimeUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Creation time (UTC).</summary>
        public DateTime CreationTimeUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Last modification time (UTC).</summary>
        public DateTime LastModificationTimeUtc { get; set; } = DateTime.UtcNow;
        /// <summary>Recipients.</summary>
        public List<RecipientItem> Recipients { get; } = new List<RecipientItem>();
        /// <summary>Attachments.</summary>
        public List<AttachmentItem> Attachments { get; } = new List<AttachmentItem>();
        /// <summary>Additional standard (already-resolved) properties, e.g. for non-mail item types.</summary>
        public List<Ltp.Property> Properties { get; } = new List<Ltp.Property>();
        /// <summary>Named properties (resolved to NPIDs at write time), e.g. contact/calendar/task fields.</summary>
        public List<NamedProperty> NamedProperties { get; } = new List<NamedProperty>();

        internal Nid Nid;
    }

    /// <summary>A folder in the store tree. In plain words: a labelled drawer that can hold messages and
    /// other drawers.</summary>
    public sealed class FolderItem
    {
        /// <summary>Folder display name.</summary>
        public string Name { get; set; } = string.Empty;
        /// <summary>Container class (PidTagContainerClass), e.g. "IPF.Contact", "IPF.Appointment".</summary>
        public string? ContainerClass { get; set; }
        /// <summary>Child folders.</summary>
        public List<FolderItem> Subfolders { get; } = new List<FolderItem>();
        /// <summary>Messages contained directly in this folder.</summary>
        public List<MessageItem> Messages { get; } = new List<MessageItem>();

        /// <summary>Adds and returns a child folder.</summary>
        public FolderItem AddFolder(string name)
        {
            var f = new FolderItem { Name = name };
            Subfolders.Add(f);
            return f;
        }

        /// <summary>Adds and returns a message.</summary>
        public MessageItem AddMessage(MessageItem message)
        {
            Messages.Add(message);
            return message;
        }

        internal Nid Nid;
        internal Nid ParentNid;
    }
}
