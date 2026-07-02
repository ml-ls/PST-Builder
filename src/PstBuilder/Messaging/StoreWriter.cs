using System;
using System.Collections.Generic;
using System.Text;
using PstBuilder.Foundation;
using PstBuilder.Ltp;
using PstBuilder.Ndb;

namespace PstBuilder.Messaging
{
    /// <summary>Opaque handle to a folder within a streaming export (see <see cref="StoreWriter"/>).</summary>
    public sealed class FolderState
    {
        internal Nid Nid;
        internal Nid ParentNid;
        internal string Name = string.Empty;
        internal string? ContainerClass;
        internal int MessageCount;
        internal int UnreadCount;
        internal readonly List<FolderState> Children = new List<FolderState>();
        internal readonly List<ContentsRow> Rows = new List<ContentsRow>();
    }

    internal struct ContentsRow
    {
        public uint RowId;
        public string MessageClass;
        public string Subject;
        public string Sender;
        public string DisplayTo;
        public int Flags;
        public long DeliveryFileTime;
        public long SubmitFileTime;
        public long LastModFileTime;
        public int Size;
    }

    /// <summary>
    /// In plain words: the chef. It turns real things (folders, emails, contacts…) into bags and
    /// spreadsheets and hands them to the NDB writer to put in boxes.
    /// Assembles a complete Unicode PST message store (MS-PST 2.4) and streams it through the NDB writer.
    /// Two ways to drive it: the graph one-shot <see cref="Write"/> (build an <see cref="IpmSubtree"/> tree
    /// then write), or the streaming API (<see cref="Begin"/> → <see cref="GetOrCreateFolder"/>/
    /// <see cref="AddMessage"/> → <see cref="Complete"/>) used by <see cref="PstExportSession"/> so that
    /// message bodies are written to disk as they arrive and only lightweight per-folder/per-node metadata
    /// is retained in memory.
    /// </summary>
    public sealed class StoreWriter
    {
        private static readonly Bid NoBid = new Bid(0);
        private static readonly Nid NoNid = new Nid(0);
        private static readonly Nid RecipientTableNid = new Nid(0x692);  // NID_RECIPIENT_TABLE
        private static readonly Nid AttachmentTableNid = new Nid(0x671); // NID_ATTACHMENT_TABLE
        private const int NativeBodyPlain = 1;
        private const int NativeBodyHtml = 3;

        private readonly FolderItem _ipmSubtree = new FolderItem { Name = "Top of Personal Folders" };

        private NdbWriter _ndb = null!;
        private NamedPropertyRegistry _named = null!;
        private byte[] _storeUid = null!;
        private FolderState _topState = null!, _deletedState = null!, _finderState = null!, _searchRoot = null!;
        private readonly List<FolderState> _allFolders = new List<FolderState>();
        private readonly Dictionary<string, FolderState> _folderByPath = new Dictionary<string, FolderState>(StringComparer.OrdinalIgnoreCase);
        private uint _nextFolderIdx;
        private uint _nextMessageIdx;
        private uint _nextRecipientRowId;
        private bool _begun;

        /// <summary>Display name for the store (shown as the root node in Outlook).</summary>
        public string StoreDisplayName { get; set; } = "Personal Folders";

        /// <summary>The IPM subtree folder users add their folders and messages to (graph API).</summary>
        public FolderItem IpmSubtree => _ipmSubtree;

        // ----- Graph one-shot API -----

        /// <summary>Writes the PST from the in-memory <see cref="IpmSubtree"/> graph.</summary>
        public PstHeader Write(PstOutputStream output)
        {
            Begin(output);
            MapGraph(_ipmSubtree, _topState);
            return Complete();
        }

        private void MapGraph(FolderItem graphFolder, FolderState state)
        {
            foreach (var msg in graphFolder.Messages) AddMessage(state, msg);
            foreach (var sub in graphFolder.Subfolders)
                MapGraph(sub, NewChild(state, sub.Name, sub.ContainerClass));
        }

        // ----- Streaming API -----

        /// <summary>Begins a streaming export: sets up the NDB writer and the standard folder skeleton.</summary>
        public void Begin(PstOutputStream output)
        {
            if (_begun) throw new InvalidOperationException("This StoreWriter has already begun.");
            _begun = true;
            _ndb = new NdbWriter(output);
            _named = new NamedPropertyRegistry();
            _storeUid = Guid.NewGuid().ToByteArray();
            _nextFolderIdx = 0x401;
            _nextMessageIdx = 0x10000;
            _nextRecipientRowId = 1;

            var root = NewFolder("Root", Nid.RootFolder, Nid.RootFolder, null);
            _topState = NewChild(root, "Top of Personal Folders", null);
            _deletedState = NewChild(_topState, "Deleted Items", null);
            _finderState = NewChild(root, "Finder", null);

            // Search root (NID_SEARCH_FOLDER 0x2223): a search folder that is a child of the root folder,
            // so it appears in the root hierarchy, but it is NOT added to _allFolders — search folders use
            // a search-contents table (0x2230), not the normal folder-table triple. Built in BuildStoreSkeleton.
            _searchRoot = new FolderState { Nid = new Nid(0x2223), ParentNid = root.Nid, Name = "Search Root" };
            root.Children.Add(_searchRoot);
        }

        /// <summary>Resolves (creating as needed) a folder by '/'- or '\'-separated path under the IPM subtree.</summary>
        public FolderState GetOrCreateFolder(string path, string? containerClass = null)
        {
            var segments = (path ?? string.Empty).Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var current = _topState;
            var key = string.Empty;
            foreach (var segment in segments)
            {
                key = key.Length == 0 ? segment : key + "/" + segment;
                if (!_folderByPath.TryGetValue(key, out var next))
                {
                    next = NewChild(current, segment, null);
                    _folderByPath[key] = next;
                }
                current = next;
            }
            if (!string.IsNullOrEmpty(containerClass) && string.IsNullOrEmpty(current.ContainerClass))
                current.ContainerClass = containerClass;
            return current;
        }

        /// <summary>Streams a message into <paramref name="folder"/>, releasing its body/attachment bytes after write.</summary>
        public void AddMessage(FolderState folder, MessageItem m)
        {
            m.Nid = new Nid(NidType.NormalMessage, _nextMessageIdx++);
            int flags = m.Attachments.Count > 0 ? m.Flags | PropertyTags.MsgFlagHasAttach : m.Flags;
            int messageSize = StreamMessage(folder, m, flags);

            folder.MessageCount++;
            if ((m.Flags & PropertyTags.MsgFlagRead) == 0) folder.UnreadCount++;
            folder.Rows.Add(new ContentsRow
            {
                RowId = m.Nid.Value,
                MessageClass = m.MessageClass,
                Subject = m.Subject,
                Sender = m.SenderName,
                DisplayTo = m.DisplayTo,
                Flags = flags,
                DeliveryFileTime = m.DeliveryTimeUtc.ToFileTimeUtc(),
                SubmitFileTime = m.SubmitTimeUtc.ToFileTimeUtc(),
                LastModFileTime = m.LastModificationTimeUtc.ToFileTimeUtc(),
                Size = messageSize, // must equal the message's PidTagMessageSize (scanpst compares them)
            });
        }

        /// <summary>Builds the folder objects, store, and named-property map, then finalizes the file.</summary>
        public PstHeader Complete()
        {
            foreach (var folder in _allFolders) BuildFolderObject(folder);
            BuildStorePc();
            BuildStoreSkeleton();
            BuildNameToIdMap();
            return _ndb.Finalize();
        }

        private FolderState NewFolder(string name, Nid nid, Nid parentNid, string? containerClass)
        {
            var fs = new FolderState { Nid = nid, ParentNid = parentNid, Name = name, ContainerClass = containerClass };
            _allFolders.Add(fs);
            return fs;
        }

        private FolderState NewChild(FolderState parent, string name, string? containerClass)
        {
            var fs = NewFolder(name, new Nid(NidType.NormalFolder, _nextFolderIdx++), parent.Nid, containerClass);
            parent.Children.Add(fs);
            return fs;
        }

        // ----- Folder objects (built at Complete from accumulated state) -----

        private void BuildFolderObject(FolderState f)
        {
            var pc = new PropertyContextBuilder()
                .Add(Property.Unicode(PropertyTags.DisplayName, f.Name))
                .Add(Property.Int32(PropertyTags.ContentCount, f.MessageCount))
                .Add(Property.Int32(PropertyTags.ContentUnreadCount, f.UnreadCount))
                .Add(Property.Bool(PropertyTags.Subfolders, f.Children.Count > 0))
                .Add(Property.Int32(PropertyTags.PstHiddenCount, 0))
                .Add(Property.Int32(PropertyTags.PstHiddenUnread, 0));
            if (!string.IsNullOrEmpty(f.ContainerClass))
                pc.Add(Property.Unicode(PropertyTags.ContainerClass, f.ContainerClass!));
            var (pcBid, pcSub) = Stage(pc.Build());
            _ndb.AddNode(f.Nid, pcBid, pcSub, f.ParentNid);

            var hier = new TableContextBuilder()
                .AddColumn(PropertyTags.DisplayName, PropertyType.String)
                .AddColumn(PropertyTags.ContentCount, PropertyType.Integer32)
                .AddColumn(PropertyTags.ContentUnreadCount, PropertyType.Integer32)
                .AddColumn(PropertyTags.Subfolders, PropertyType.Boolean);
            AddHierarchyTemplateColumns(hier);
            foreach (var sub in f.Children)
            {
                var row = new Dictionary<uint, byte[]>
                {
                    [Col(PropertyTags.DisplayName, PropertyType.String)] = Utf16(sub.Name),
                    [Col(PropertyTags.ContentCount, PropertyType.Integer32)] = BitConverter.GetBytes(sub.MessageCount),
                    [Col(PropertyTags.ContentUnreadCount, PropertyType.Integer32)] = BitConverter.GetBytes(sub.UnreadCount),
                    [Col(PropertyTags.Subfolders, PropertyType.Boolean)] = new[] { (byte)(sub.Children.Count > 0 ? 1 : 0) },
                    [Col(PropertyTags.PstHiddenCount, PropertyType.Integer32)] = BitConverter.GetBytes(0),
                    [Col(PropertyTags.PstHiddenUnread, PropertyType.Integer32)] = BitConverter.GetBytes(0),
                };
                if (!string.IsNullOrEmpty(sub.ContainerClass))
                    row[Col(PropertyTags.ContainerClass, PropertyType.String)] = Utf16(sub.ContainerClass!);
                hier.AddRow(sub.Nid.Value, row);
            }
            var (hierBid, hierSub) = Stage(hier.Build());
            _ndb.AddNode(new Nid(NidType.HierarchyTable, f.Nid.Index), hierBid, hierSub, NoNid);

            var contents = new TableContextBuilder()
                .AddColumn(PropertyTags.MessageClass, PropertyType.String)
                .AddColumn(PropertyTags.Subject, PropertyType.String)
                .AddColumn(PropertyTags.SentRepresentingName, PropertyType.String)
                .AddColumn(PropertyTags.MessageFlags, PropertyType.Integer32)
                .AddColumn(PropertyTags.MessageDeliveryTime, PropertyType.Time)
                .AddColumn(PropertyTags.MessageSize, PropertyType.Integer32)
                .AddColumn(PropertyTags.DisplayTo, PropertyType.String);
            AddContentsTemplateColumns(contents);
            foreach (var row in f.Rows)
                contents.AddRow(row.RowId, new Dictionary<uint, byte[]>
                {
                    [Col(PropertyTags.MessageClass, PropertyType.String)] = Utf16(row.MessageClass),
                    [Col(PropertyTags.Subject, PropertyType.String)] = Utf16(row.Subject),
                    [Col(PropertyTags.SentRepresentingName, PropertyType.String)] = Utf16(row.Sender),
                    [Col(PropertyTags.MessageFlags, PropertyType.Integer32)] = BitConverter.GetBytes(row.Flags),
                    [Col(PropertyTags.MessageDeliveryTime, PropertyType.Time)] = BitConverter.GetBytes(row.DeliveryFileTime),
                    [Col(PropertyTags.MessageSize, PropertyType.Integer32)] = BitConverter.GetBytes(row.Size),
                    [Col(PropertyTags.DisplayTo, PropertyType.String)] = Utf16(row.DisplayTo),
                    [Col(PropertyTags.Importance, PropertyType.Integer32)] = BitConverter.GetBytes(1),
                    [Col(PropertyTags.Sensitivity, PropertyType.Integer32)] = BitConverter.GetBytes(0),
                    [Col(PropertyTags.MessageStatus, PropertyType.Integer32)] = BitConverter.GetBytes(0),
                    [Col(PropertyTags.ClientSubmitTime, PropertyType.Time)] = BitConverter.GetBytes(row.SubmitFileTime),
                    [Col(PropertyTags.LastModificationTime, PropertyType.Time)] = BitConverter.GetBytes(row.LastModFileTime),
                    [Col(PropertyTags.ConversationTopic, PropertyType.String)] = Utf16(row.Subject),
                });
            var (contBid, contSub) = Stage(contents.Build());
            _ndb.AddNode(new Nid(NidType.ContentsTable, f.Nid.Index), contBid, contSub, NoNid);

            var fai = new TableContextBuilder().AddColumn(PropertyTags.MessageClass, PropertyType.String);
            AddFaiTemplateColumns(fai);
            var (faiBid, faiSub) = Stage(fai.Build());
            _ndb.AddNode(new Nid(NidType.AssocContentsTable, f.Nid.Index), faiBid, faiSub, NoNid);
        }

        // ----- Messages (streamed on arrival) -----

        // Fixed structural overhead added to PidTagMessageSize beyond the sum of property/recipient/
        // attachment value bytes. Derived from a real Outlook message (PR_MESSAGE_SIZE minus the summed
        // value bytes of the message PC, its recipient rows, and its attachment). See size notes.
        private const int MessageSizeBase = 2234;

        private int StreamMessage(FolderState folder, MessageItem m, int flags)
        {
            // PidTagMessageSize (0x0E08) and PidTagAttachSize (0x0E20) are computed properties: Outlook (and
            // scanpst) recompute them as the summed byte-length of the object's property values, so a static
            // estimate makes scanpst report "row doesn't match sub-object". Compute the exact sums here.
            var attachSizes = new int[m.Attachments.Count];
            int sa = 0;
            for (int i = 0; i < m.Attachments.Count; i++) { attachSizes[i] = AttachmentSize(m.Attachments[i], i); sa += attachSizes[i]; }
            int sr = 0;
            foreach (var r in m.Recipients) sr += RecipientRowSize(r);

            var props = new List<Property>
            {
                Property.Unicode(PropertyTags.MessageClass, m.MessageClass),
                Property.Int32(PropertyTags.MessageFlags, flags),
                Property.Int32(PropertyTags.Importance, 1),
                Property.Int32(PropertyTags.Sensitivity, 0),
                Property.Int32(PropertyTags.MessageStatus, 0),
                Property.Unicode(PropertyTags.ConversationTopic, m.Subject),
                Property.Unicode(PropertyTags.Subject, m.Subject),
                Property.Unicode(PropertyTags.Body, m.Body),
                Property.Unicode(PropertyTags.DisplayTo, m.DisplayTo),
                Property.Unicode(PropertyTags.SenderName, m.SenderName),
                Property.Unicode(PropertyTags.SenderEmailAddress, m.SenderEmail),
                Property.Unicode(PropertyTags.SentRepresentingName, m.SenderName),
                Property.Unicode(PropertyTags.SentRepresentingEmailAddress, m.SenderEmail),
                Property.Time(PropertyTags.MessageDeliveryTime, m.DeliveryTimeUtc),
                Property.Time(PropertyTags.ClientSubmitTime, m.SubmitTimeUtc),
                Property.Time(PropertyTags.CreationTime, m.CreationTimeUtc),
                Property.Time(PropertyTags.LastModificationTime, m.LastModificationTimeUtc),
                Property.Binary(PropertyTags.SearchKey, Guid.NewGuid().ToByteArray()),
            };
            if (!string.IsNullOrEmpty(m.BodyHtml))
            {
                props.Add(new Property(PropertyTags.Html, PropertyType.Binary, Encoding.UTF8.GetBytes(m.BodyHtml!)));
                props.Add(Property.Int32(PropertyTags.NativeBody, NativeBodyHtml));
            }
            else
            {
                props.Add(Property.Int32(PropertyTags.NativeBody, NativeBodyPlain));
            }
            foreach (var p in m.Properties) props.Add(p);
            foreach (var np in m.NamedProperties) props.Add(new Property(_named.Resolve(np), np.Type, np.Data));

            int smsg = 4; // the PidTagMessageSize int32 (added below) counts toward its own total
            foreach (var p in props) smsg += p.Data.Length;
            int messageSize = smsg + sr + sa + MessageSizeBase;
            props.Add(Property.Int32(PropertyTags.MessageSize, messageSize));

            var pc = new PropertyContextBuilder();
            foreach (var p in props) pc.Add(p);

            var subEntries = new List<SlEntry>();
            var (recipData, recipSub) = Stage(BuildRecipientTable(m));
            subEntries.Add(new SlEntry(RecipientTableNid, recipData, recipSub));
            if (m.Attachments.Count > 0) AddAttachmentSubnodes(m, subEntries, attachSizes);

            var (pcBid, subBid) = Stage(pc.Build(), subEntries);
            _ndb.AddNode(m.Nid, pcBid, subBid, folder.Nid);
            return messageSize;
        }

        private void AddAttachmentSubnodes(MessageItem m, List<SlEntry> subEntries, int[] attachSizes)
        {
            var attachTable = new TableContextBuilder()
                .AddColumn(PropertyTags.AttachSize, PropertyType.Integer32)
                .AddColumn(PropertyTags.AttachMethod, PropertyType.Integer32)
                .AddColumn(PropertyTags.RenderingPosition, PropertyType.Integer32)
                .AddColumn(PropertyTags.AttachFilename, PropertyType.String)
                .AddColumn(PropertyTags.AttachLongFilename, PropertyType.String)
                .AddColumn(PropertyTags.DisplayName, PropertyType.String);

            for (int i = 0; i < m.Attachments.Count; i++)
            {
                var att = m.Attachments[i];
                var attachNid = new Nid(NidType.Attachment, (uint)i);
                attachTable.AddRow(attachNid.Value, new Dictionary<uint, byte[]>
                {
                    [Col(PropertyTags.AttachSize, PropertyType.Integer32)] = BitConverter.GetBytes(attachSizes[i]),
                    [Col(PropertyTags.AttachMethod, PropertyType.Integer32)] = BitConverter.GetBytes(PropertyTags.AttachByValue),
                    [Col(PropertyTags.RenderingPosition, PropertyType.Integer32)] = BitConverter.GetBytes(PropertyTags.RenderingPositionNone),
                    [Col(PropertyTags.AttachFilename, PropertyType.String)] = Utf16(att.FileName),
                    [Col(PropertyTags.AttachLongFilename, PropertyType.String)] = Utf16(att.FileName),
                    [Col(PropertyTags.DisplayName, PropertyType.String)] = Utf16(att.FileName),
                });

                var (aData, aSub) = Stage(BuildAttachmentPc(att, i, attachSizes[i]));
                subEntries.Add(new SlEntry(attachNid, aData, aSub));
            }

            var (atData, atSub) = Stage(attachTable.Build());
            subEntries.Add(new SlEntry(AttachmentTableNid, atData, atSub));
        }

        // The attachment's non-size properties, in one place so the size sum and the PC never drift.
        private static List<Property> BuildAttachmentProps(AttachmentItem att, int number)
        {
            var props = new List<Property>
            {
                Property.Int32(PropertyTags.AttachMethod, PropertyTags.AttachByValue),
                Property.Int32(PropertyTags.AttachNumber, number),
                Property.Int32(PropertyTags.RenderingPosition, PropertyTags.RenderingPositionNone),
                Property.Int32(PropertyTags.ObjectType, PropertyTags.ObjectTypeAttachment),
                Property.Unicode(PropertyTags.DisplayName, att.FileName),
                Property.Unicode(PropertyTags.AttachFilename, att.FileName),
                Property.Unicode(PropertyTags.AttachLongFilename, att.FileName),
                // Byte[] content is stored by value; a stream source is read on demand (never buffered).
                att.OpenContent != null
                    ? Property.BinaryStream(PropertyTags.AttachDataBinary, att.OpenContent, att.ContentLength)
                    : Property.Binary(PropertyTags.AttachDataBinary, att.Content),
            };
            if (!string.IsNullOrEmpty(att.MimeType))
                props.Add(Property.Unicode(PropertyTags.AttachMimeTag, att.MimeType!));
            if (!string.IsNullOrEmpty(att.ContentId))
                props.Add(Property.Unicode(PropertyTags.AttachContentId, att.ContentId!));
            if (att.IsInline)
                props.Add(Property.Int32(PropertyTags.AttachFlags, PropertyTags.AttachFlagMhtmlRef));
            return props;
        }

        // PidTagAttachSize = sum of the attachment's property value byte-lengths (incl. the 4-byte size
        // property itself); overhead 0, confirmed against a real Outlook attachment.
        private static int AttachmentSize(AttachmentItem att, int number)
        {
            long s = 4; // PidTagAttachSize (0x0E20) int32 counts toward its own total
            foreach (var p in BuildAttachmentProps(att, number)) s += p.ValueLength; // ValueLength covers streamed data
            return checked((int)s);
        }

        private static LtpContent BuildAttachmentPc(AttachmentItem att, int number, int attachSize)
        {
            var pc = new PropertyContextBuilder();
            foreach (var p in BuildAttachmentProps(att, number)) pc.Add(p);
            pc.Add(Property.Int32(PropertyTags.AttachSize, attachSize));
            return pc.Build();
        }

        // Bytes a recipient row contributes to PidTagMessageSize: the sum of its cached property values
        // (see BuildRecipientTable) plus the TC LtpRowId/LtpRowVer pair.
        private static int RecipientRowSize(RecipientItem r)
        {
            int dn = (r.DisplayName ?? string.Empty).Length;
            int email = (r.EmailAddress ?? string.Empty).Length;
            int at = (r.AddressType ?? string.Empty).Length;
            return 4 + 4 + 4            // RecipientType, ObjectType, RecipientFlags
                 + dn * 2               // DisplayName
                 + email * 2            // EmailAddress
                 + at * 2               // AddressType
                 + 1                    // Responsibility
                 + 4                    // DisplayType
                 + dn * 2               // AddressBookDisplayNamePrintable
                 + 1                    // SendRichInfo
                 + 4 + 4;               // LtpRowId, LtpRowVer
        }

        private LtpContent BuildRecipientTable(MessageItem m)
        {
            var tc = new TableContextBuilder()
                .AddColumn(PropertyTags.RecipientType, PropertyType.Integer32)
                .AddColumn(PropertyTags.ObjectType, PropertyType.Integer32)
                .AddColumn(PropertyTags.RecipientFlags, PropertyType.Integer32)
                .AddColumn(PropertyTags.DisplayName, PropertyType.String)
                .AddColumn(PropertyTags.EmailAddress, PropertyType.String)
                .AddColumn(PropertyTags.AddressType, PropertyType.String)
                .AddColumn(PropertyTags.Responsibility, PropertyType.Boolean)
                .AddColumn(PropertyTags.RecordKey, PropertyType.Binary)
                .AddColumn(PropertyTags.EntryId, PropertyType.Binary)
                .AddColumn(PropertyTags.SearchKey, PropertyType.Binary)
                .AddColumn(PropertyTags.DisplayType, PropertyType.Integer32)
                .AddColumn(PropertyTags.AddressBookDisplayNamePrintable, PropertyType.String)
                .AddColumn(PropertyTags.SendRichInfo, PropertyType.Boolean);
            foreach (var r in m.Recipients)
                tc.AddRow(_nextRecipientRowId++, new Dictionary<uint, byte[]>
                {
                    [Col(PropertyTags.RecipientType, PropertyType.Integer32)] = BitConverter.GetBytes(r.RecipientType),
                    [Col(PropertyTags.ObjectType, PropertyType.Integer32)] = BitConverter.GetBytes(PropertyTags.ObjectTypeMailUser),
                    [Col(PropertyTags.RecipientFlags, PropertyType.Integer32)] = BitConverter.GetBytes(1),
                    [Col(PropertyTags.DisplayName, PropertyType.String)] = Utf16(r.DisplayName),
                    [Col(PropertyTags.EmailAddress, PropertyType.String)] = Utf16(r.EmailAddress),
                    [Col(PropertyTags.AddressType, PropertyType.String)] = Utf16(r.AddressType),
                    [Col(PropertyTags.Responsibility, PropertyType.Boolean)] = new byte[] { 0 },
                    [Col(PropertyTags.DisplayType, PropertyType.Integer32)] = BitConverter.GetBytes(0),
                    [Col(PropertyTags.AddressBookDisplayNamePrintable, PropertyType.String)] = Utf16(r.DisplayName),
                    [Col(PropertyTags.SendRichInfo, PropertyType.Boolean)] = new byte[] { 0 },
                });
            return tc.Build();
        }

        private void BuildStorePc()
        {
            var pc = new PropertyContextBuilder()
                .Add(Property.Binary(PropertyTags.RecordKey, _storeUid))
                .Add(Property.Unicode(PropertyTags.DisplayName, StoreDisplayName))
                .Add(Property.Int32(PropertyTags.PstPassword, 0))
                .Add(Property.Int32(PropertyTags.ValidFolderMask,
                    PropertyTags.FolderMaskIpmSubtree | PropertyTags.FolderMaskWastebasket | PropertyTags.FolderMaskFinder))
                .Add(Property.Int32(PropertyTags.ReplFlags, 0))
                .Add(Property.Binary(PropertyTags.ReplVersionHistory, Array.Empty<byte>()))
                .Add(Property.Bool(PropertyTags.PstLrNoRestrictions, true))
                .Add(Property.Int32(PropertyTags.PstIdsToPids, 0))
                .Add(Property.Int32(PropertyTags.PstStoreVersion, 0))
                .Add(Property.Binary(PropertyTags.IpmSubTreeEntryId, EntryId.ForNode(_storeUid, _topState.Nid)))
                .Add(Property.Binary(PropertyTags.IpmWastebasketEntryId, EntryId.ForNode(_storeUid, _deletedState.Nid)))
                .Add(Property.Binary(PropertyTags.FinderEntryId, EntryId.ForNode(_storeUid, _finderState.Nid)));
            var (bid, sub) = Stage(pc.Build());
            _ndb.AddNode(Nid.MessageStore, bid, sub, NoNid);
        }

        // Store-level template/system objects Outlook expects in every store (MS-PST 2.4). Column schemas
        // taken byte-for-byte from a real blank Outlook PST (decoded reference). Missing these yields
        // scanpst "missing template / receive folder table / outgoing queue / search …" and an Outlook
        // "out of memory" mount failure. Templates are empty TCs (schema only).
        private void BuildStoreSkeleton()
        {
            // Hierarchy / contents / FAI / search-contents folder-table templates (NIDs 0x60D-0x610).
            var hierCols = new TableContextBuilder();
            hierCols.AddColumn(PropertyTags.DisplayName, PropertyType.String)
                .AddColumn(PropertyTags.ContentCount, PropertyType.Integer32)
                .AddColumn(PropertyTags.ContentUnreadCount, PropertyType.Integer32)
                .AddColumn(PropertyTags.Subfolders, PropertyType.Boolean);
            AddHierarchyTemplateColumns(hierCols);
            StageStoreNode(new Nid(0x60D), hierCols.Build());

            var contentsCols = new TableContextBuilder();
            contentsCols.AddColumn(PropertyTags.MessageClass, PropertyType.String)
                .AddColumn(PropertyTags.Subject, PropertyType.String)
                .AddColumn(PropertyTags.SentRepresentingName, PropertyType.String)
                .AddColumn(PropertyTags.MessageFlags, PropertyType.Integer32)
                .AddColumn(PropertyTags.MessageDeliveryTime, PropertyType.Time)
                .AddColumn(PropertyTags.MessageSize, PropertyType.Integer32)
                .AddColumn(PropertyTags.DisplayTo, PropertyType.String);
            AddContentsTemplateColumns(contentsCols);
            StageStoreNode(new Nid(0x60E), contentsCols.Build());

            var faiCols = new TableContextBuilder();
            faiCols.AddColumn(PropertyTags.MessageClass, PropertyType.String);
            AddFaiTemplateColumns(faiCols);
            StageStoreNode(new Nid(0x60F), faiCols.Build());

            // Search-contents template (NID 0x610).
            StageStoreNode(new Nid(0x610), Tc(
                (0x0017, PropertyType.Integer32), (0x001A, PropertyType.String), (0x0036, PropertyType.Integer32),
                (0x0037, PropertyType.String), (0x0042, PropertyType.String), (0x0057, PropertyType.Boolean),
                (0x0058, PropertyType.Boolean), (0x0E03, PropertyType.String), (0x0E04, PropertyType.String),
                (0x0E05, PropertyType.String), (0x0E06, PropertyType.Time), (0x0E07, PropertyType.Integer32),
                (0x0E08, PropertyType.Integer32), (0x0E17, PropertyType.Integer32), (0x0E2A, PropertyType.Boolean),
                (0x3008, PropertyType.Time), (0x67F1, PropertyType.Integer32)));

            // Receive folder table (NID 0x62B): MessageClass + 0x6605 (delivery folder NID). One default
            // row (empty message class → the IPM subtree) so Outlook has a default receive folder.
            var receive = new TableContextBuilder()
                .AddColumn(0x001A, PropertyType.String)
                .AddColumn(0x6605, PropertyType.Integer32);
            receive.AddRow(_topState.Nid.Value, new Dictionary<uint, byte[]>
            {
                [Col(0x001A, PropertyType.String)] = Utf16(string.Empty),
                [Col(0x6605, PropertyType.Integer32)] = BitConverter.GetBytes((int)_topState.Nid.Value),
            });
            StageStoreNode(new Nid(0x62B), receive.Build());

            // Outgoing queue table (NID 0x64C): empty.
            StageStoreNode(new Nid(0x64C), Tc(
                (0x0039, PropertyType.Time), (0x0E10, PropertyType.Integer32), (0x0E14, PropertyType.Integer32)));

            // Attachment-table template (NID 0x671).
            StageStoreNode(new Nid(0x671), Tc(
                (PropertyTags.AttachSize, PropertyType.Integer32), (PropertyTags.AttachFilename, PropertyType.String),
                (PropertyTags.AttachMethod, PropertyType.Integer32), (PropertyTags.RenderingPosition, PropertyType.Integer32)));

            // Recipient-table template (NID 0x692).
            StageStoreNode(new Nid(0x692), Tc(
                (PropertyTags.RecipientType, PropertyType.Integer32), (PropertyTags.Responsibility, PropertyType.Boolean),
                (PropertyTags.RecordKey, PropertyType.Binary), (PropertyTags.ObjectType, PropertyType.Integer32),
                (PropertyTags.EntryId, PropertyType.Binary), (PropertyTags.DisplayName, PropertyType.String),
                (PropertyTags.AddressType, PropertyType.String), (PropertyTags.EmailAddress, PropertyType.String),
                (PropertyTags.SearchKey, PropertyType.Binary), (PropertyTags.DisplayType, PropertyType.Integer32),
                (PropertyTags.AddressBookDisplayNamePrintable, PropertyType.String), (PropertyTags.SendRichInfo, PropertyType.Boolean)));

            // Misc templates (NIDs 0x6B6/0x6D7/0x6F8) observed in a real store.
            StageStoreNode(new Nid(0x6B6), Tc(
                (0x0E33, PropertyType.Integer64), (0x0E37, PropertyType.Binary), (0x0E38, PropertyType.Integer32)));
            StageStoreNode(new Nid(0x6D7), Tc(
                (0x001A, PropertyType.String), (0x0E30, PropertyType.Binary), (0x0E31, PropertyType.Binary),
                (0x0E33, PropertyType.Integer64), (0x0E34, PropertyType.Binary), (0x0E38, PropertyType.Integer32),
                (0x0E3E, PropertyType.Binary)));
            StageStoreNode(new Nid(0x6F8), Tc(
                (0x0E33, PropertyType.Integer64), (0x3007, PropertyType.Time)));

            // Search root folder object (NID 0x2223): a search folder. Its PC mirrors a folder PC (so the
            // root hierarchy row Outlook builds for it matches) but it uses a search-contents table, not the
            // normal folder-table triple.
            var searchPc = new PropertyContextBuilder()
                .Add(Property.Unicode(PropertyTags.DisplayName, _searchRoot.Name))
                .Add(Property.Int32(PropertyTags.ContentCount, 0))
                .Add(Property.Int32(PropertyTags.ContentUnreadCount, 0))
                .Add(Property.Bool(PropertyTags.Subfolders, false))
                .Add(Property.Int32(PropertyTags.PstHiddenCount, 0))
                .Add(Property.Int32(PropertyTags.PstHiddenUnread, 0));
            var (srBid, srSub) = Stage(searchPc.Build());
            _ndb.AddNode(_searchRoot.Nid, srBid, srSub, _searchRoot.ParentNid); // parent = root folder

            // Search-contents table (NID 0x2230) — same schema as the search-contents template.
            StageStoreNode(new Nid(0x2230), Tc(
                (0x0017, PropertyType.Integer32), (0x001A, PropertyType.String), (0x0036, PropertyType.Integer32),
                (0x0037, PropertyType.String), (0x0042, PropertyType.String), (0x0057, PropertyType.Boolean),
                (0x0058, PropertyType.Boolean), (0x0E03, PropertyType.String), (0x0E04, PropertyType.String),
                (0x0E05, PropertyType.String), (0x0E06, PropertyType.Time), (0x0E07, PropertyType.Integer32),
                (0x0E08, PropertyType.Integer32), (0x0E17, PropertyType.Integer32), (0x0E2A, PropertyType.Boolean),
                (0x3008, PropertyType.Time), (0x67F1, PropertyType.Integer32)));

            // Search criteria object (NID 0x2227): PC with the single search-state property.
            StageStoreNode(new Nid(0x2227),
                new PropertyContextBuilder().Add(Property.Int32(0x660B, 0)).Build());

            // Search-management skeleton nodes Outlook expects to exist (empty NBT entries).
            foreach (uint nid in new uint[] { 0x1E1, 0x201, 0x261, 0xE41, 0xEC1, 0xF21, 0x2226 })
                _ndb.AddNode(new Nid(nid), NoBid, NoBid, NoNid);
        }

        // Builds an empty Table Context with the given data columns (LtpRowId/LtpRowVer added automatically).
        private static LtpContent Tc(params (ushort id, PropertyType type)[] cols)
        {
            var tc = new TableContextBuilder();
            foreach (var c in cols) tc.AddColumn(c.id, c.type);
            return tc.Build();
        }

        private void StageStoreNode(Nid nid, LtpContent content)
        {
            var (bid, sub) = Stage(content);
            _ndb.AddNode(nid, bid, sub, NoNid);
        }

        private void BuildNameToIdMap()
        {
            var pc = new PropertyContextBuilder()
                .Add(Property.Int32(PropertyTags.NameidBucketCount, NamedPropertyRegistry.BucketCount))
                .Add(Property.Binary(PropertyTags.NameidStreamGuid, _named.GuidStream()))
                .Add(Property.Binary(PropertyTags.NameidStreamEntry, _named.EntryStream()))
                .Add(Property.Binary(PropertyTags.NameidStreamString, _named.StringStream()));
            foreach (var bucket in _named.Buckets())
                pc.Add(Property.Binary((ushort)(PropertyTags.NameidBucketBase + bucket.Key), bucket.Value));
            var (bid, sub) = Stage(pc.Build());
            _ndb.AddNode(Nid.NameToIdMap, bid, sub, NoNid);
        }

        private (Bid data, Bid sub) Stage(LtpContent content, IEnumerable<SlEntry>? extraSubnodes = null)
        {
            Bid dataBid = DataTreeBuilder.BuildFromBlocks(_ndb, content.MainBlocks);

            var entries = new List<SlEntry>();
            foreach (var sn in content.Subnodes)
            {
                // A streamed value (e.g. a large attachment) is read straight to disk block by block;
                // an in-memory value is byte-split as before.
                Bid valueBid = sn.StreamSource != null
                    ? DataTreeBuilder.BuildFromStream(_ndb, sn.StreamSource, sn.StreamLength)
                    : DataTreeBuilder.Build(_ndb, sn.Data!);
                entries.Add(new SlEntry(sn.Nid, valueBid, NoBid));
            }
            if (content.RowMatrix is RowMatrixSpill rm)
                // A subnode row matrix is row-aligned across data-tree blocks: each non-final block holds
                // floor(8176/rowSize) whole rows padded to 8176, so the reader can index row N by
                // (N / rowsPerBlock, N % rowsPerBlock). Rows must NOT span block boundaries.
                entries.Add(new SlEntry(rm.Nid, DataTreeBuilder.BuildRowMatrix(_ndb, rm.Rows, rm.RowSize), NoBid));
            if (extraSubnodes != null) entries.AddRange(extraSubnodes);

            Bid subBid = entries.Count > 0
                ? _ndb.AddBlock(SubnodeBlockBuilder.BuildSlBlock(entries), isInternal: true)
                : NoBid;
            return (dataBid, subBid);
        }

        // Table "template" columns Outlook requires in every folder hierarchy/contents/FAI table
        // (MS-PST 2.4.4.x). The columns must exist in the schema even when a row leaves them absent; a
        // missing column makes Outlook misread the row layout. Obscure replication/view tags use literals.
        private static void AddHierarchyTemplateColumns(TableContextBuilder tc)
        {
            tc.AddColumn(PropertyTags.ContainerClass, PropertyType.String);   // 0x3613
            tc.AddColumn(0x0E30, PropertyType.Binary);        // PidTagReplItemid
            tc.AddColumn(0x0E33, PropertyType.Integer64);     // PidTagReplChangenum
            tc.AddColumn(0x0E34, PropertyType.Binary);        // PidTagReplVersionHistory
            tc.AddColumn(0x0E38, PropertyType.Integer32);     // PidTagReplFlags
            tc.AddColumn(PropertyTags.PstHiddenCount, PropertyType.Integer32);  // 0x6635
            tc.AddColumn(PropertyTags.PstHiddenUnread, PropertyType.Integer32); // 0x6636
        }

        private static void AddContentsTemplateColumns(TableContextBuilder tc)
        {
            tc.AddColumn(PropertyTags.Importance, PropertyType.Integer32);      // 0x0017
            tc.AddColumn(PropertyTags.Sensitivity, PropertyType.Integer32);     // 0x0036
            tc.AddColumn(PropertyTags.ClientSubmitTime, PropertyType.Time);     // 0x0039
            tc.AddColumn(0x0057, PropertyType.Boolean);       // PidTagMessageToMe
            tc.AddColumn(0x0058, PropertyType.Boolean);       // PidTagMessageCcMe
            tc.AddColumn(PropertyTags.ConversationTopic, PropertyType.String);  // 0x0070
            tc.AddColumn(0x0071, PropertyType.Binary);        // PidTagConversationIndex
            tc.AddColumn(0x0E03, PropertyType.String);        // PidTagDisplayCc
            tc.AddColumn(PropertyTags.MessageStatus, PropertyType.Integer32);   // 0x0E17
            tc.AddColumn(0x1097, PropertyType.Integer32);     // PidTagItemTemporaryFlags
            tc.AddColumn(PropertyTags.LastModificationTime, PropertyType.Time); // 0x3008
            tc.AddColumn(0x3013, PropertyType.Binary);        // PidTagConversationId
            tc.AddColumn(0x65C6, PropertyType.Integer32);     // PidTagSecureSubmitFlags
            tc.AddColumn(0x0E30, PropertyType.Binary);        // PidTagReplItemid
            tc.AddColumn(0x0E33, PropertyType.Integer64);     // PidTagReplChangenum
            tc.AddColumn(0x0E34, PropertyType.Binary);        // PidTagReplVersionHistory
            tc.AddColumn(0x0E38, PropertyType.Integer32);     // PidTagReplFlags
            tc.AddColumn(0x0E3C, PropertyType.Binary);        // replication
            tc.AddColumn(0x0E3D, PropertyType.Binary);        // replication
        }

        private static void AddFaiTemplateColumns(TableContextBuilder tc)
        {
            tc.AddColumn(PropertyTags.MessageFlags, PropertyType.Integer32);    // 0x0E07
            tc.AddColumn(PropertyTags.MessageStatus, PropertyType.Integer32);   // 0x0E17
            tc.AddColumn(PropertyTags.DisplayName, PropertyType.String);   // 0x3001
            tc.AddColumn(0x7003, PropertyType.Integer32);     // view descriptor flags
            tc.AddColumn(0x7004, PropertyType.Binary);        // view descriptor link
            tc.AddColumn(0x7005, PropertyType.Binary);        // view descriptor view folder
            tc.AddColumn(0x7006, PropertyType.String);        // view descriptor name
            tc.AddColumn(0x7007, PropertyType.Integer32);     // view descriptor version
            tc.AddColumn(0x6800, PropertyType.String);        // user-config
            tc.AddColumn(0x6803, PropertyType.Boolean);       // user-config
            tc.AddColumn(0x6805, PropertyType.MultipleInteger32);
            tc.AddColumn(0x682F, PropertyType.String);        // user-config (roaming dictionary)
        }

        private static uint Col(ushort propId, PropertyType type) => TableContextBuilder.Tag(propId, type);
        private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s ?? string.Empty);
    }
}
