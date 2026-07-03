using System;
using System.IO;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;

namespace PstBuilder
{
    /// <summary>
    /// In plain words: the front door. One short line to make a PST — either compose a folder tree in
    /// memory and write it (<see cref="Write"/>), or open a streaming session and push items as you get
    /// them (<see cref="Create"/> / <see cref="CreateSplit"/>).
    ///
    /// <para>These are thin, discoverable shortcuts over <see cref="StoreWriter"/> and
    /// <see cref="PstExportSession"/>; reach for those directly when you need the full set of knobs.</para>
    /// </summary>
    public static class Pst
    {
        /// <summary>
        /// Builds a complete PST from an in-memory folder tree in one call. Compose the store inside
        /// <paramref name="compose"/> — add subfolders and messages under the supplied "Top of Personal
        /// Folders" node — then this writes the file at <paramref name="path"/> and returns its header.
        /// Best for small/medium exports where holding the tree in memory is fine; for large or streamed
        /// exports use <see cref="Create"/>.
        /// </summary>
        /// <example>
        /// <code>
        /// Pst.Write("out.pst", top =>
        /// {
        ///     var inbox = top.AddFolder("Inbox");
        ///     inbox.AddMessage(new MessageItem { Subject = "Hello", Body = "Hi there" });
        /// });
        /// </code>
        /// </example>
        public static PstHeader Write(string path, Action<FolderItem> compose, string storeDisplayName = "Personal Folders")
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (compose == null) throw new ArgumentNullException(nameof(compose));

            var store = new StoreWriter { StoreDisplayName = storeDisplayName };
            compose(store.IpmSubtree);
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite))
            using (var output = new PstOutputStream(file))
                return store.Write(output);
        }

        /// <summary>Opens a streaming export writing a single PST at <paramref name="path"/> (shortcut for
        /// <see cref="PstExportSession.Create(string,string,int,IProgress{ExportProgress},int,bool)"/>). Pass
        /// <paramref name="compress"/> to zip each part in the background as it closes.</summary>
        public static PstExportSession Create(string path, string storeDisplayName = "Personal Folders",
            int queueCapacity = 1024, IProgress<ExportProgress>? progress = null, int progressInterval = 256,
            bool compress = false) =>
            PstExportSession.Create(path, storeDisplayName, queueCapacity, progress, progressInterval, compress);

        /// <summary>Opens a streaming export that rolls over to a new PST file once the current one reaches
        /// <paramref name="maxBytesPerFile"/> (shortcut for <see cref="PstExportSession.CreateSplit"/>). Pass
        /// <paramref name="compress"/> to zip each part in the background as it closes.</summary>
        public static PstExportSession CreateSplit(string path, long maxBytesPerFile,
            string storeDisplayName = "Personal Folders", int queueCapacity = 1024,
            IProgress<ExportProgress>? progress = null, int progressInterval = 256, bool compress = false) =>
            PstExportSession.CreateSplit(path, maxBytesPerFile, storeDisplayName, queueCapacity, progress, progressInterval, compress);
    }
}
