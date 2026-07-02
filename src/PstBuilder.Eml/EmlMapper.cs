using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MimeKit;
using PstBuilder.Messaging;

namespace PstBuilder.Eml
{
    /// <summary>
    /// In plain words: reads a raw email file (.eml) and fills in one of our messages.
    /// Maps raw RFC822/MIME (.eml) messages to <see cref="MessageItem"/> instances using MimeKit.
    /// This is the default source adapter: it keeps the core PstBuilder library free of any MIME
    /// dependency, parsing happens here.
    /// </summary>
    public static class EmlMapper
    {
        /// <summary>Parses an .eml byte buffer into a <see cref="MessageItem"/>.</summary>
        /// <param name="eml">Raw RFC822 message bytes.</param>
        /// <param name="receivedUtc">Override for delivery time; defaults to the message Date header.</param>
        /// <param name="sentUtc">Override for submit time; defaults to the message Date header.</param>
        /// <param name="flags">Override for message flags (defaults to read).</param>
        public static MessageItem ToMessageItem(byte[] eml, DateTime? receivedUtc = null, DateTime? sentUtc = null, int? flags = null)
        {
            if (eml == null) throw new ArgumentNullException(nameof(eml));
            using var stream = new MemoryStream(eml);
            var mime = MimeMessage.Load(stream);
            return ToMessageItem(mime, receivedUtc, sentUtc, flags);
        }

        /// <summary>Maps an already-parsed <see cref="MimeMessage"/> to a <see cref="MessageItem"/>.</summary>
        public static MessageItem ToMessageItem(MimeMessage mime, DateTime? receivedUtc = null, DateTime? sentUtc = null, int? flags = null)
        {
            if (mime == null) throw new ArgumentNullException(nameof(mime));

            DateTime date = mime.Date == default ? DateTime.UtcNow : mime.Date.UtcDateTime;
            var sender = FirstMailbox(mime.From) ?? FirstMailbox(mime.Sender != null ? new InternetAddressList { mime.Sender } : null);

            var msg = new MessageItem
            {
                MessageClass = "IPM.Note",
                Subject = mime.Subject ?? string.Empty,
                Body = mime.TextBody ?? (mime.HtmlBody != null ? string.Empty : string.Empty),
                BodyHtml = mime.HtmlBody, // null when the message has no HTML part
                DeliveryTimeUtc = receivedUtc ?? date,
                SubmitTimeUtc = sentUtc ?? date,
                CreationTimeUtc = receivedUtc ?? date,
                LastModificationTimeUtc = receivedUtc ?? date,
                Flags = flags ?? PropertyTags.MsgFlagRead,
                SenderName = sender?.Name ?? sender?.Address ?? string.Empty,
                SenderEmail = sender?.Address ?? string.Empty,
                DisplayTo = JoinNames(mime.To),
            };

            AddRecipients(msg, mime.To, PropertyTags.RecipTypeTo);
            AddRecipients(msg, mime.Cc, RecipTypeCc);
            AddAttachments(msg, mime);
            return msg;
        }

        private static void AddAttachments(MessageItem msg, MimeMessage mime)
        {
            var explicitAttachments = new HashSet<MimeEntity>(mime.Attachments);
            foreach (var part in mime.Attachments.OfType<MimePart>())
                msg.Attachments.Add(ToAttachment(part, inline: part.ContentDisposition?.Disposition == "inline"));

            // Inline linked resources (e.g. images referenced by cid: in the HTML body) live in BodyParts,
            // not Attachments. Collect non-text parts that carry a Content-Id and aren't already attached.
            foreach (var part in mime.BodyParts.OfType<MimePart>())
            {
                if (explicitAttachments.Contains(part)) continue;
                if (string.IsNullOrEmpty(part.ContentId)) continue;
                if (part.ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase)) continue;
                msg.Attachments.Add(ToAttachment(part, inline: true));
            }
        }

        private static AttachmentItem ToAttachment(MimePart part, bool inline)
        {
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            return new AttachmentItem
            {
                FileName = part.FileName ?? part.ContentType?.Name ?? (inline ? "inline" : "attachment"),
                Content = ms.ToArray(),
                MimeType = part.ContentType?.MimeType,
                ContentId = part.ContentId,
                IsInline = inline,
            };
        }

        private const int RecipTypeCc = 0x02;

        private static void AddRecipients(MessageItem msg, InternetAddressList list, int recipientType)
        {
            if (list == null) return;
            foreach (var mailbox in list.Mailboxes)
            {
                msg.Recipients.Add(new RecipientItem
                {
                    DisplayName = string.IsNullOrEmpty(mailbox.Name) ? mailbox.Address : mailbox.Name,
                    EmailAddress = mailbox.Address ?? string.Empty,
                    AddressType = "SMTP",
                    RecipientType = recipientType,
                });
            }
        }

        private static MailboxAddress? FirstMailbox(InternetAddressList? list) =>
            list?.Mailboxes.FirstOrDefault();

        private static string JoinNames(InternetAddressList list)
        {
            if (list == null) return string.Empty;
            var names = list.Mailboxes.Select(m => string.IsNullOrEmpty(m.Name) ? m.Address : m.Name);
            return string.Join("; ", names);
        }
    }
}
