using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PstBuilder.Messaging;

namespace PstBuilder.Eml
{
    /// <summary>
    /// In plain words: the door you pour raw email files (<c>.eml</c>) through — hand it the bytes and it
    /// drops a ready-made message into a folder.
    /// Convenience methods for adding RFC822/MIME messages straight into the store graph.
    /// </summary>
    public static class EmlExtensions
    {
        /// <summary>Parses <paramref name="eml"/> and adds it to this folder. Returns the created message.</summary>
        public static MessageItem AddEml(this FolderItem folder, byte[] eml,
            DateTime? receivedUtc = null, DateTime? sentUtc = null, int? flags = null)
        {
            if (folder == null) throw new ArgumentNullException(nameof(folder));
            var message = EmlMapper.ToMessageItem(eml, receivedUtc, sentUtc, flags);
            folder.AddMessage(message);
            return message;
        }

        /// <summary>Reads an .eml file from disk and adds it to this folder.</summary>
        public static MessageItem AddEmlFile(this FolderItem folder, string path,
            DateTime? receivedUtc = null, DateTime? sentUtc = null, int? flags = null)
        {
            return folder.AddEml(File.ReadAllBytes(path), receivedUtc, sentUtc, flags);
        }

        /// <summary>
        /// Parses an .eml and queues it into the export session at <paramref name="folderPath"/>.
        /// Thread-safe: parsing happens on the calling thread, the write is serialized by the session.
        /// </summary>
        public static void AddEml(this PstExportSession session, string folderPath, byte[] eml,
            DateTime? receivedUtc = null, DateTime? sentUtc = null, int? flags = null)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            session.AddMessage(folderPath, EmlMapper.ToMessageItem(eml, receivedUtc, sentUtc, flags));
        }

        /// <summary>
        /// Awaitable counterpart to <see cref="AddEml(PstExportSession,string,byte[],DateTime?,DateTime?,int?)"/>:
        /// parses on the calling thread, then awaits queue space (backpressure) instead of blocking.
        /// </summary>
        public static Task AddEmlAsync(this PstExportSession session, string folderPath, byte[] eml,
            DateTime? receivedUtc = null, DateTime? sentUtc = null, int? flags = null,
            CancellationToken cancellationToken = default)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            return session.AddMessageAsync(folderPath, EmlMapper.ToMessageItem(eml, receivedUtc, sentUtc, flags),
                cancellationToken: cancellationToken);
        }
    }
}
