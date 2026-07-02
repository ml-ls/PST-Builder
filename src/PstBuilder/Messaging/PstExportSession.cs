using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PstBuilder.Foundation;
using PstBuilder.Ndb;

namespace PstBuilder.Messaging
{
    /// <summary>One output file produced by an export (a "part" when splitting is enabled).</summary>
    public sealed class PstPart
    {
        /// <summary>The file name/identifier of this part.</summary>
        public string Name { get; }
        /// <summary>The finalized header of this part.</summary>
        public PstHeader Header { get; }
        internal PstPart(string name, PstHeader header) { Name = name; Header = header; }
    }

    /// <summary>The result of a completed export: one part per PST file written.</summary>
    public sealed class PstExportResult
    {
        /// <summary>All PST files written, in order.</summary>
        public IReadOnlyList<PstPart> Parts { get; }
        internal PstExportResult(IReadOnlyList<PstPart> parts) { Parts = parts; }
    }

    /// <summary>
    /// In plain words: a conveyor belt. Many helpers can drop items on at once, but one careful worker
    /// writes them into the PST one at a time (and can start a new PST file when one gets too big).
    /// High-level entry point for exporting a mailbox to a PST when items are <em>pushed</em> by a caller
    /// (e.g. a backup application reading from its own store), rather than scanned from disk.
    ///
    /// <para><b>Threading contract.</b> The <c>Add*</c> methods are safe to call concurrently from many
    /// threads — items go on a bounded queue (backpressure throttles producers and keeps memory bounded).
    /// A single internal consumer applies items in order, and PST writing is single-threaded. Never call
    /// <c>Add*</c> after <see cref="Complete"/>.</para>
    ///
    /// <para><b>Sync or async.</b> Both surfaces write to the same bounded queue. The sync <c>Add*</c> block
    /// the calling thread when the queue is full; the <c>Add*Async</c> variants <em>await</em> free space
    /// instead (better for async hosts) and accept a <see cref="CancellationToken"/>. Finish with either
    /// <see cref="Complete"/> or <see cref="CompleteAsync"/>. Don't mix a sync <c>Complete</c> with async
    /// producers still in flight — pick one style per session.</para>
    ///
    /// <para><b>Items.</b> Each item is addressed by a <c>folderPath</c> using '/' or '\' separators
    /// (e.g. <c>"Inbox"</c>, <c>"Projects/2026"</c>); intermediate folders are created automatically. A
    /// folder's container class is inferred from the first item type added to it.</para>
    ///
    /// <para><b>Splitting.</b> Use <see cref="CreateSplit"/> with a byte threshold to roll over to a new
    /// PST file once the current one reaches that size; each part is a standalone PST that recreates the
    /// folder tree for the items it holds. <see cref="Complete"/> returns one <see cref="PstPart"/> per file.</para>
    /// </summary>
    public sealed class PstExportSession : IDisposable
    {
        private sealed class WorkItem
        {
            public string FolderPath = string.Empty;
            public MessageItem Item = null!;
            public string? ContainerClass;
        }

        private readonly Func<int, PstOutputStream> _openOutput; // creates the N-th part (1-based)
        private readonly Func<int, string> _partName;
        private readonly bool _ownsOutputs;
        private readonly long _maxBytes; // 0 = never split
        private readonly string _displayName;
        private readonly Channel<WorkItem> _queue;               // bounded → backpressure (sync + async)
        private readonly Task _consumer;                         // single ordered writer
        private readonly List<PstPart> _parts = new List<PstPart>();
        private readonly IProgress<ExportProgress>? _progress;   // optional progress sink
        private readonly int _progressInterval;                  // report every N items

        private StoreWriter _store;
        private PstOutputStream _output;
        private int _partIndex = 1;
        private long _itemsWritten;
        private volatile Exception? _fault;
        private int _completed;

        private PstExportSession(Func<int, PstOutputStream> openOutput, Func<int, string> partName,
            bool ownsOutputs, long maxBytes, string storeDisplayName, int queueCapacity,
            IProgress<ExportProgress>? progress, int progressInterval)
        {
            _openOutput = openOutput;
            _partName = partName;
            _ownsOutputs = ownsOutputs;
            _maxBytes = maxBytes;
            _displayName = storeDisplayName;
            _progress = progress;
            _progressInterval = progressInterval > 0 ? progressInterval : 1;
            _output = _openOutput(_partIndex);
            _store = new StoreWriter { StoreDisplayName = _displayName };
            _store.Begin(_output);
            _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = true,      // exactly one consumer
                SingleWriter = false,     // any number of producer threads
                FullMode = BoundedChannelFullMode.Wait,  // producers wait when full (the backpressure)
            });
            // The consumer does synchronous, potentially long block writes, so give it a dedicated thread.
            _consumer = Task.Factory.StartNew(ConsumeLoopAsync, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        /// <summary>Creates a session that writes a single PST file at <paramref name="path"/>. Pass
        /// <paramref name="progress"/> to receive periodic <see cref="ExportProgress"/> updates (every
        /// <paramref name="progressInterval"/> items, plus a final snapshot).</summary>
        public static PstExportSession Create(string path, string storeDisplayName = "Personal Folders",
            int queueCapacity = 1024, IProgress<ExportProgress>? progress = null, int progressInterval = 256)
        {
            return new PstExportSession(
                _ => new PstOutputStream(new FileStream(path, FileMode.Create, FileAccess.ReadWrite)),
                _ => path, ownsOutputs: true, maxBytes: 0, storeDisplayName, queueCapacity, progress, progressInterval);
        }

        /// <summary>Creates a session that writes to <paramref name="output"/> (caller keeps ownership; no splitting).</summary>
        public static PstExportSession Create(PstOutputStream output, string storeDisplayName = "Personal Folders",
            int queueCapacity = 1024, IProgress<ExportProgress>? progress = null, int progressInterval = 256)
        {
            if (output == null) throw new ArgumentNullException(nameof(output));
            return new PstExportSession(
                i => i == 1 ? output : throw new InvalidOperationException("Caller-owned output cannot be split."),
                _ => "(stream)", ownsOutputs: false, maxBytes: 0, storeDisplayName, queueCapacity, progress, progressInterval);
        }

        /// <summary>
        /// Creates a split export: the first file is <paramref name="path"/>, then "name-002.ext",
        /// "name-003.ext", … each rolled over once the current file reaches <paramref name="maxBytesPerFile"/>.
        /// </summary>
        public static PstExportSession CreateSplit(string path, long maxBytesPerFile,
            string storeDisplayName = "Personal Folders", int queueCapacity = 1024,
            IProgress<ExportProgress>? progress = null, int progressInterval = 256)
        {
            if (maxBytesPerFile <= PstConstants.FirstAMapOffset)
                throw new ArgumentOutOfRangeException(nameof(maxBytesPerFile), "Threshold is unreasonably small.");
            string PartPath(int index)
            {
                if (index == 1) return path;
                string dir = Path.GetDirectoryName(path) ?? string.Empty;
                string name = Path.GetFileNameWithoutExtension(path);
                string ext = Path.GetExtension(path);
                return Path.Combine(dir, $"{name}-{index:D3}{ext}");
            }
            return new PstExportSession(
                i => new PstOutputStream(new FileStream(PartPath(i), FileMode.Create, FileAccess.ReadWrite)),
                PartPath, ownsOutputs: true, maxBytesPerFile, storeDisplayName, queueCapacity, progress, progressInterval);
        }

        /// <summary>Bytes written to the current output so far (lags queued-but-unwritten items). Used by
        /// size-targeted generation to know when to stop adding content.</summary>
        public long Position => _output.Position;

        // ----- sync producers (block on backpressure) -----

        /// <summary>Queues a fully-built message into <paramref name="folderPath"/>. Thread-safe; blocks if the queue is full.</summary>
        public void AddMessage(string folderPath, MessageItem item, string? containerClass = null)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            Enqueue(new WorkItem { FolderPath = folderPath, Item = item, ContainerClass = containerClass });
        }

        /// <summary>Queues a contact. Thread-safe.</summary>
        public void AddContact(string folderPath, ContactItem contact) =>
            AddMessage(folderPath, contact.ToMessageItem(), "IPF.Contact");

        /// <summary>Queues a calendar appointment. Thread-safe.</summary>
        public void AddAppointment(string folderPath, AppointmentItem appointment) =>
            AddMessage(folderPath, appointment.ToMessageItem(), "IPF.Appointment");

        /// <summary>Queues a task. Thread-safe.</summary>
        public void AddTask(string folderPath, TaskItem task) =>
            AddMessage(folderPath, task.ToMessageItem(), "IPF.Task");

        /// <summary>Queues a sticky note. Thread-safe.</summary>
        public void AddNote(string folderPath, NoteItem note) =>
            AddMessage(folderPath, note.ToMessageItem(), "IPF.StickyNote");

        /// <summary>Pre-creates a folder and sets its container class. Thread-safe.</summary>
        public void EnsureFolder(string folderPath, string? containerClass = null) =>
            Enqueue(new WorkItem { FolderPath = folderPath, Item = null!, ContainerClass = containerClass });

        // ----- async producers (await backpressure; cancellable) -----

        /// <summary>Awaitably queues a message, awaiting free queue space instead of blocking. Thread-safe.</summary>
        public Task AddMessageAsync(string folderPath, MessageItem item, string? containerClass = null, CancellationToken cancellationToken = default)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            return EnqueueAsync(new WorkItem { FolderPath = folderPath, Item = item, ContainerClass = containerClass }, cancellationToken);
        }

        /// <summary>Awaitably queues a contact. Thread-safe.</summary>
        public Task AddContactAsync(string folderPath, ContactItem contact, CancellationToken cancellationToken = default) =>
            AddMessageAsync(folderPath, contact.ToMessageItem(), "IPF.Contact", cancellationToken);

        /// <summary>Awaitably queues a calendar appointment. Thread-safe.</summary>
        public Task AddAppointmentAsync(string folderPath, AppointmentItem appointment, CancellationToken cancellationToken = default) =>
            AddMessageAsync(folderPath, appointment.ToMessageItem(), "IPF.Appointment", cancellationToken);

        /// <summary>Awaitably queues a task. Thread-safe.</summary>
        public Task AddTaskAsync(string folderPath, TaskItem task, CancellationToken cancellationToken = default) =>
            AddMessageAsync(folderPath, task.ToMessageItem(), "IPF.Task", cancellationToken);

        /// <summary>Awaitably queues a sticky note. Thread-safe.</summary>
        public Task AddNoteAsync(string folderPath, NoteItem note, CancellationToken cancellationToken = default) =>
            AddMessageAsync(folderPath, note.ToMessageItem(), "IPF.StickyNote", cancellationToken);

        /// <summary>Awaitably pre-creates a folder and sets its container class. Thread-safe.</summary>
        public Task EnsureFolderAsync(string folderPath, string? containerClass = null, CancellationToken cancellationToken = default) =>
            EnqueueAsync(new WorkItem { FolderPath = folderPath, Item = null!, ContainerClass = containerClass }, cancellationToken);

        private void ThrowIfFaulted()
        {
            var fault = _fault;
            if (fault != null) throw new InvalidOperationException("Export failed; see inner exception.", fault);
        }

        private void Enqueue(WorkItem work)
        {
            ThrowIfFaulted();
            // Block until there is room (backpressure). WaitToWriteAsync returns false once writing is
            // completed (i.e. after Complete()), at which point TryWrite also fails.
            while (!_queue.Writer.TryWrite(work))
            {
                if (!_queue.Writer.WaitToWriteAsync().AsTask().ConfigureAwait(false).GetAwaiter().GetResult())
                    throw new InvalidOperationException("Cannot add items after Complete().");
                ThrowIfFaulted();
            }
        }

        private async Task EnqueueAsync(WorkItem work, CancellationToken cancellationToken)
        {
            ThrowIfFaulted();
            try { await _queue.Writer.WriteAsync(work, cancellationToken).ConfigureAwait(false); }
            catch (ChannelClosedException) { throw new InvalidOperationException("Cannot add items after Complete()."); }
            ThrowIfFaulted();
        }

        private async Task ConsumeLoopAsync()
        {
            try
            {
                var reader = _queue.Reader;
                while (await reader.WaitToReadAsync().ConfigureAwait(false))
                {
                    while (reader.TryRead(out var work))
                    {
                        var folder = _store.GetOrCreateFolder(work.FolderPath, work.ContainerClass);
                        if (work.Item != null)
                        {
                            _store.AddMessage(folder, work.Item);
                            _itemsWritten++;
                            if (_progress != null && _itemsWritten % _progressInterval == 0)
                                _progress.Report(new ExportProgress(_itemsWritten, _output.Position, _partIndex, false));
                        }
                        if (_maxBytes > 0 && _output.Position >= _maxBytes) RollOver();
                    }
                }
            }
            catch (Exception ex)
            {
                _fault = ex;
            }
        }

        private void RollOver()
        {
            _parts.Add(new PstPart(_partName(_partIndex), _store.Complete()));
            if (_ownsOutputs) _output.Dispose();
            _partIndex++;
            _output = _openOutput(_partIndex);
            _store = new StoreWriter { StoreDisplayName = _displayName };
            _store.Begin(_output);
        }

        /// <summary>Drains the queue and finalizes all PST files. Returns one part per file. Call once.</summary>
        public PstExportResult Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
                throw new InvalidOperationException("Complete() has already been called.");

            _queue.Writer.Complete();
            _consumer.GetAwaiter().GetResult();   // drain (the consumer captures faults; it never throws here)
            ThrowIfFaulted();

            _parts.Add(new PstPart(_partName(_partIndex), _store.Complete()));
            _progress?.Report(new ExportProgress(_itemsWritten, _output.Position, _partIndex, true));
            if (_ownsOutputs) _output.Dispose();
            return new PstExportResult(_parts);
        }

        /// <summary>
        /// Awaitable counterpart to <see cref="Complete"/>: drains the queue, then finalizes the PST file(s)
        /// off the calling context. The final B-tree pack/write is a single non-interruptible operation, so
        /// <paramref name="cancellationToken"/> only takes effect before finalization begins. Call once.
        /// </summary>
        public async Task<PstExportResult> CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
                throw new InvalidOperationException("Complete() has already been called.");

            _queue.Writer.Complete();
            await _consumer.ConfigureAwait(false);   // drain
            ThrowIfFaulted();
            cancellationToken.ThrowIfCancellationRequested();

            return await Task.Run(() =>
            {
                _parts.Add(new PstPart(_partName(_partIndex), _store.Complete()));
                _progress?.Report(new ExportProgress(_itemsWritten, _output.Position, _partIndex, true));
                if (_ownsOutputs) _output.Dispose();
                return new PstExportResult(_parts);
            }, cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _completed, 1, 0) == 0)
            {
                // Abandoned without Complete(): stop the consumer without finalizing the (partial) file.
                _queue.Writer.TryComplete();
                try { _consumer.GetAwaiter().GetResult(); } catch { /* ignore faults on abandon */ }
                if (_ownsOutputs) _output.Dispose();
            }
        }
    }
}
