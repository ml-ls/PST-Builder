namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: a progress ticket the exporter hands back now and then, saying "this many items in,
    /// this many bytes on disk, currently writing file N".
    ///
    /// <para>Reported through the optional <see cref="System.IProgress{T}"/> passed to
    /// <see cref="PstExportSession.Create(string,string,int,System.IProgress{ExportProgress},int)"/> and its
    /// siblings. Callbacks are raised from the internal writer, so keep the handler quick and thread-safe;
    /// a final snapshot is always reported once the file(s) are finalized.</para>
    /// </summary>
    public readonly struct ExportProgress
    {
        /// <summary>Number of items (messages/contacts/appointments/…) written so far across all parts.</summary>
        public long ItemsWritten { get; }
        /// <summary>Bytes written to the current output file so far.</summary>
        public long BytesWritten { get; }
        /// <summary>1-based index of the PST file currently being written.</summary>
        public int PartNumber { get; }
        /// <summary>True for the final snapshot reported after all files are finalized.</summary>
        public bool IsCompleted { get; }

        internal ExportProgress(long itemsWritten, long bytesWritten, int partNumber, bool isCompleted)
        {
            ItemsWritten = itemsWritten;
            BytesWritten = bytesWritten;
            PartNumber = partNumber;
            IsCompleted = isCompleted;
        }
    }
}
