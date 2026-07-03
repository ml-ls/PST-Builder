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
        /// <summary>
        /// In plain words: a claim ticket for this part's file — done immediately, unless compression is
        /// still shrink-wrapping it in the background, in which case this finishes when that's done.
        /// Already-completed when compression is off (the part is durable the moment it's returned).
        /// </summary>
        public Task WhenReady { get; }
        internal PstPart(string name, PstHeader header, Task whenReady) { Name = name; Header = header; WhenReady = whenReady; }
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
    /// folder tree for the items it holds. <see cref="Complete"/> returns one <see cref="PstPart"/> per file.
    /// Even a plain <see cref="Create(string,string,int,IProgress{ExportProgress},int)"/> (single-file) export
    /// rolls into numbered parts automatically if it would otherwise exceed the writer's ~3.4 GB single-file
    /// limit, so a large import never fails on size. Only a caller-owned output stream cannot roll.</para>
    ///
    /// <para><b>Crash resilience.</b> A producer may pause for any length of time (e.g. a source
    /// disconnect) and resume calling <c>Add*</c> — there is no idle timeout. For durability across a
    /// process crash, use <see cref="CreateResumable"/> and call <see cref="Checkpoint"/> to seal the
    /// current part as a standalone PST mid-export; a crash then costs only the items added since the last
    /// checkpoint. After a restart, <see cref="Resume"/> reopens the set and continues at the next part,
    /// leaving finished parts intact — your producer replays the items it added since its last checkpoint.</para>
    ///
    /// <para><b>Compression.</b> Pass <c>compress: true</c> to any file-backed factory and each part is
    /// zipped into a sibling <c>.pst.zip</c> the moment it closes, on a background task — the writer moves
    /// straight on to the next part without waiting. The raw <c>.pst</c> is deleted only once its archive
    /// is fully written, so nothing is ever lost to a crash mid-compression. <see cref="Complete"/> /
    /// <see cref="CompleteAsync"/> wait for any still-running compression before returning; a
    /// <see cref="PstPart.WhenReady"/> task is available per part if you need to know sooner. A zipped
    /// part must be extracted before Outlook (or any other PST reader) can open it.</para>
    /// </summary>
    public sealed class PstExportSession : IDisposable
    {
        private sealed class WorkItem
        {
            public string FolderPath = string.Empty;
            public MessageItem? Item;
            public string? ContainerClass;
            // Non-null → a checkpoint barrier: the consumer seals the current part in order and completes
            // this with the finalized part (or null if nothing new since the previous part boundary).
            public TaskCompletionSource<PstPart?>? Checkpoint;
        }

        private readonly Func<int, PstOutputStream> _openOutput; // creates the N-th part (1-based)
        private readonly Func<int, string> _partName;
        private readonly bool _ownsOutputs;
        private readonly bool _compress;
        private readonly SemaphoreSlim _compressionGate = new SemaphoreSlim(1, 1); // one zip job at a time
        private readonly List<Task> _compressionTasks = new List<Task>();
        private readonly long _maxBytes; // 0 = never split
        private readonly string _displayName;
        private readonly Channel<WorkItem> _queue;               // bounded → backpressure (sync + async)
        private readonly Task _consumer;                         // single ordered writer
        private readonly List<PstPart> _parts = new List<PstPart>();
        private readonly IProgress<ExportProgress>? _progress;   // optional progress sink
        private readonly int _progressInterval;                  // report every N items
        private readonly bool _supportsCheckpoint;               // true when parts get distinct file names
        private readonly bool _canRoll;                          // true when we own the outputs (can open a new part)
        private readonly long _rollThreshold;                    // roll into the next part at this many bytes

        // Default size at which an unsplit file-backed export rolls into the next numbered part (3 GiB is
        // within the size validated in real Outlook), and the ceiling no part may cross (just under the
        // writer's ~3.4 GB region cap, leaving room for the finalize pass).
        private const long DefaultAutoRollBytes = 3L * 1024 * 1024 * 1024;
        private static readonly long SafeMaxPartBytes = PstConstants.MaxSingleFileBytes - (128L * 1024 * 1024);

        private StoreWriter _store;
        private PstOutputStream _output;
        private int _partIndex = 1;
        private long _itemsWritten;
        private long _itemsInCurrentPart;
        private volatile Exception? _fault;
        private int _completed;

        private PstExportSession(Func<int, PstOutputStream> openOutput, Func<int, string> partName,
            bool ownsOutputs, long maxBytes, string storeDisplayName, int queueCapacity,
            IProgress<ExportProgress>? progress, int progressInterval,
            bool supportsCheckpoint = false, int startPartIndex = 1, long autoRollBytes = 0,
            bool compress = false)
        {
            _openOutput = openOutput;
            _partName = partName;
            _ownsOutputs = ownsOutputs;
            _compress = compress;
            _maxBytes = maxBytes;
            _displayName = storeDisplayName;
            _progress = progress;
            _progressInterval = progressInterval > 0 ? progressInterval : 1;
            _supportsCheckpoint = supportsCheckpoint;
            // We roll at the caller's split size if given, otherwise the safe default — but never past the
            // ceiling. A caller-owned output stream can't roll, so it relies on the writer's backstop instead.
            _canRoll = ownsOutputs;
            long requested = maxBytes > 0 ? maxBytes : (autoRollBytes > 0 ? autoRollBytes : DefaultAutoRollBytes);
            _rollThreshold = Math.Min(requested, SafeMaxPartBytes);
            _partIndex = startPartIndex;
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

        /// <summary>Creates a session that writes a PST file at <paramref name="path"/>. Stays a single
        /// file for ordinary exports; if the data would exceed the writer's ~3.4 GB single-file limit it
        /// rolls into numbered parts (<paramref name="path"/>, "name-002.ext", …) automatically rather than
        /// failing. Pass <paramref name="progress"/> to receive periodic <see cref="ExportProgress"/> updates
        /// (every <paramref name="progressInterval"/> items, plus a final snapshot). Pass
        /// <paramref name="compress"/> to zip each part in the background as it closes.</summary>
        public static PstExportSession Create(string path, string storeDisplayName = "Personal Folders",
            int queueCapacity = 1024, IProgress<ExportProgress>? progress = null, int progressInterval = 256,
            bool compress = false)
        {
            return new PstExportSession(
                i => new PstOutputStream(new FileStream(PartPath(path, i), FileMode.Create, FileAccess.ReadWrite)),
                i => PartPath(path, i), ownsOutputs: true, maxBytes: 0, storeDisplayName, queueCapacity,
                progress, progressInterval, compress: compress);
        }

        // Test seam: a file-backed session that rolls at a caller-chosen (small) size so the auto-roll
        // behavior can be exercised without generating multiple gigabytes.
        internal static PstExportSession CreateWithAutoRoll(string path, long autoRollBytes,
            string storeDisplayName = "Personal Folders", int queueCapacity = 1024, bool compress = false)
        {
            return new PstExportSession(
                i => new PstOutputStream(new FileStream(PartPath(path, i), FileMode.Create, FileAccess.ReadWrite)),
                i => PartPath(path, i), ownsOutputs: true, maxBytes: 0, storeDisplayName, queueCapacity,
                progress: null, progressInterval: 256, autoRollBytes: autoRollBytes, compress: compress);
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
            IProgress<ExportProgress>? progress = null, int progressInterval = 256, bool compress = false)
        {
            if (maxBytesPerFile <= PstConstants.FirstAMapOffset)
                throw new ArgumentOutOfRangeException(nameof(maxBytesPerFile), "Threshold is unreasonably small.");
            return new PstExportSession(
                i => new PstOutputStream(new FileStream(PartPath(path, i), FileMode.Create, FileAccess.ReadWrite)),
                i => PartPath(path, i), ownsOutputs: true, maxBytesPerFile, storeDisplayName, queueCapacity,
                progress, progressInterval, supportsCheckpoint: true, compress: compress);
        }

        /// <summary>
        /// Creates a resumable, checkpointable export. Parts are named like <see cref="CreateSplit"/>
        /// (<paramref name="path"/>, "name-002.ext", …) but a new part is only started when you call
        /// <see cref="Checkpoint"/> — or, if <paramref name="maxBytesPerFile"/> is non-zero, also on size.
        /// Each checkpoint writes a durable, standalone PST, so a later crash costs you only the items
        /// added since the last one. After a restart, reopen the set with <see cref="Resume"/>.
        /// </summary>
        public static PstExportSession CreateResumable(string path, long maxBytesPerFile = 0,
            string storeDisplayName = "Personal Folders", int queueCapacity = 1024,
            IProgress<ExportProgress>? progress = null, int progressInterval = 256, bool compress = false)
        {
            if (maxBytesPerFile != 0 && maxBytesPerFile <= PstConstants.FirstAMapOffset)
                throw new ArgumentOutOfRangeException(nameof(maxBytesPerFile), "Threshold is unreasonably small.");
            return new PstExportSession(
                i => new PstOutputStream(new FileStream(PartPath(path, i), FileMode.Create, FileAccess.ReadWrite)),
                i => PartPath(path, i), ownsOutputs: true, maxBytesPerFile, storeDisplayName, queueCapacity,
                progress, progressInterval, supportsCheckpoint: true, compress: compress);
        }

        /// <summary>
        /// Resumes a checkpointed export after a restart: scans for the parts already on disk
        /// (<paramref name="path"/>, "name-002.ext", …) and continues at the next slot, leaving every
        /// completed part untouched. A trailing <em>incomplete</em> part (left behind by the crash) is
        /// spotted by its missing header magic and overwritten. Your producer should replay only the items
        /// it added since its last successful <see cref="Checkpoint"/>.
        /// </summary>
        public static PstExportSession Resume(string path, long maxBytesPerFile = 0,
            string storeDisplayName = "Personal Folders", int queueCapacity = 1024,
            IProgress<ExportProgress>? progress = null, int progressInterval = 256, bool compress = false)
        {
            if (maxBytesPerFile != 0 && maxBytesPerFile <= PstConstants.FirstAMapOffset)
                throw new ArgumentOutOfRangeException(nameof(maxBytesPerFile), "Threshold is unreasonably small.");
            int next = 1;
            while (IsCompletePst(PartPath(path, next))) next++;   // first missing/incomplete slot
            return new PstExportSession(
                i => new PstOutputStream(new FileStream(PartPath(path, i), FileMode.Create, FileAccess.ReadWrite)),
                i => PartPath(path, i), ownsOutputs: true, maxBytesPerFile, storeDisplayName, queueCapacity,
                progress, progressInterval, supportsCheckpoint: true, startPartIndex: next, compress: compress);
        }

        // The file name of the N-th part: part 1 is the given path; later parts get a "-NNN" suffix.
        private static string PartPath(string path, int index)
        {
            if (index <= 1) return path;
            string dir = Path.GetDirectoryName(path) ?? string.Empty;
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            return Path.Combine(dir, $"{name}-{index:D3}{ext}");
        }

        // True only for a fully finalized PST: the header magic ("!BDN") is written at finalization, so a
        // partial file left by a crash reads as incomplete. A "{file}.zip" sibling is also proof of a
        // completed part — PstPartCompressor only ever deletes the raw file after the archive is fully
        // written and renamed into place, so the raw file surviving a crash mid-compression still reads
        // as complete via the check below.
        private static bool IsCompletePst(string file)
        {
            if (File.Exists(file + ".zip")) return true;
            if (!File.Exists(file)) return false;
            try
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var magic = new byte[4];
                    return fs.Read(magic, 0, 4) == 4
                        && magic[0] == 0x21 && magic[1] == 0x42 && magic[2] == 0x44 && magic[3] == 0x4E; // "!BDN"
                }
            }
            catch { return false; }
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

        // ----- checkpointing (durable part boundaries for crash resilience) -----

        /// <summary>
        /// Seals the current part to disk as a durable, standalone PST and starts the next one, so
        /// everything queued before this call survives a later crash. Returns the finalized part, or
        /// <c>null</c> if nothing new had been added since the previous part boundary. Only valid on a
        /// split/resumable session (see <see cref="CreateSplit"/> / <see cref="CreateResumable"/> /
        /// <see cref="Resume"/>) — otherwise throws. Thread-safe; blocks until the write completes. Items
        /// enqueued on other threads concurrently with this call may land in either part.
        /// </summary>
        public PstPart? Checkpoint() =>
            CheckpointAsync().ConfigureAwait(false).GetAwaiter().GetResult();

        /// <summary>Awaitable counterpart to <see cref="Checkpoint"/>. The token guards the enqueue only;
        /// once the barrier is accepted, the checkpoint runs to completion.</summary>
        public async Task<PstPart?> CheckpointAsync(CancellationToken cancellationToken = default)
        {
            if (!_supportsCheckpoint)
                throw new InvalidOperationException(
                    "Checkpoint requires a split or resumable session (CreateSplit / CreateResumable / Resume).");
            var barrier = new TaskCompletionSource<PstPart?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await EnqueueAsync(new WorkItem { Checkpoint = barrier }, cancellationToken).ConfigureAwait(false);
            return await barrier.Task.ConfigureAwait(false);
        }

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
                        if (work.Checkpoint != null)
                        {
                            // Seal the current part in order; skip (return null) if nothing new to make durable.
                            try { work.Checkpoint.TrySetResult(_itemsInCurrentPart > 0 ? RollToNextPart() : null); }
                            catch (Exception ex) { work.Checkpoint.TrySetException(ex); throw; }
                            continue;
                        }

                        var folder = _store.GetOrCreateFolder(work.FolderPath, work.ContainerClass);
                        if (work.Item != null)
                        {
                            _store.AddMessage(folder, work.Item);
                            _itemsWritten++;
                            _itemsInCurrentPart++;
                            if (_progress != null && _itemsWritten % _progressInterval == 0)
                                _progress.Report(new ExportProgress(_itemsWritten, _output.Position, _partIndex, false));
                        }
                        if (_canRoll && _output.Position >= _rollThreshold) RollToNextPart();
                    }
                }
            }
            catch (Exception ex)
            {
                _fault = ex;
                // Don't leave a queued checkpoint's awaiter hanging on the fault.
                while (_queue.Reader.TryRead(out var pending))
                    pending.Checkpoint?.TrySetException(
                        new InvalidOperationException("Export failed; see inner exception.", ex));
            }
        }

        // Finalizes the current store, closes its output, and (if enabled) kicks off background
        // compression before recording the durable part. Compression must only start once the file is
        // closed — the header/AMap patches in NdbWriter.Finalize() are still landing on disk up to that
        // point, so reading the file any earlier would race the last writes.
        private PstPart FinalizeCurrentPart()
        {
            var header = _store.Complete();
            CloseCurrentOutput();
            string rawPath = _partName(_partIndex);
            string name = rawPath;
            Task ready = Task.CompletedTask;
            if (_compress && _ownsOutputs)
            {
                name = rawPath + ".zip";
                var task = Task.Run(() => PstPartCompressor.CompressAndReplaceAsync(rawPath, _compressionGate));
                _compressionTasks.Add(task);
                ready = task;
            }
            var part = new PstPart(name, header, ready);
            _parts.Add(part);
            return part;
        }

        private void CloseCurrentOutput()
        {
            if (_ownsOutputs) { _output.FlushToDisk(); _output.Dispose(); }
        }

        private void OpenNextPart()
        {
            _partIndex++;
            _output = _openOutput(_partIndex);
            _store = new StoreWriter { StoreDisplayName = _displayName };
            _store.Begin(_output);
            _itemsInCurrentPart = 0;
        }

        // Seals the current part durably and opens the next; used by size-based splitting and Checkpoint.
        private PstPart RollToNextPart()
        {
            var part = FinalizeCurrentPart();
            OpenNextPart();
            return part;
        }

        /// <summary>Drains the queue and finalizes all PST files. Returns one part per file. Call once.</summary>
        public PstExportResult Complete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
                throw new InvalidOperationException("Complete() has already been called.");

            _queue.Writer.Complete();
            _consumer.GetAwaiter().GetResult();   // drain (the consumer captures faults; it never throws here)
            ThrowIfFaulted();

            FinalizeCurrentPart();
            _progress?.Report(new ExportProgress(_itemsWritten, _output.Position, _partIndex, true));
            if (_compressionTasks.Count > 0) Task.WaitAll(_compressionTasks.ToArray());
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

            await Task.Run(() =>
            {
                FinalizeCurrentPart();
                _progress?.Report(new ExportProgress(_itemsWritten, _output.Position, _partIndex, true));
            }, cancellationToken).ConfigureAwait(false);
            if (_compressionTasks.Count > 0) await Task.WhenAll(_compressionTasks).ConfigureAwait(false);
            return new PstExportResult(_parts);
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
