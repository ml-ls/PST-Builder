namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: what kind of thing a node is — a folder, a message, a table, and so on — packed
    /// into the low 5 bits of its ID.
    /// Node identifier types. MS-PST 2.2.2.1. Occupies the low 5 bits of a <see cref="Nid"/>.
    /// </summary>
    public enum NidType : byte
    {
        /// <summary>Heap node (HID). Internal to LTP, not a real NBT node.</summary>
        Hid = 0x00,
        /// <summary>Internal node (NID_TYPE_INTERNAL).</summary>
        Internal = 0x01,
        /// <summary>Normal folder object.</summary>
        NormalFolder = 0x02,
        /// <summary>Search folder object.</summary>
        SearchFolder = 0x03,
        /// <summary>Normal message object.</summary>
        NormalMessage = 0x04,
        /// <summary>Attachment object.</summary>
        Attachment = 0x05,
        /// <summary>Queue of changed objects for search folders.</summary>
        SearchUpdateQueue = 0x06,
        /// <summary>Search folder criteria object.</summary>
        SearchCriteriaObject = 0x07,
        /// <summary>Folder-associated information (FAI) message object.</summary>
        AssocMessage = 0x08,
        /// <summary>Internal, persisted view-related.</summary>
        ContentsTableIndex = 0x0A,
        /// <summary>Receive-folder table.</summary>
        ReceiveFolderTable = 0x0B,
        /// <summary>Outbound-queue table.</summary>
        OutgoingQueueTable = 0x0C,
        /// <summary>Hierarchy table.</summary>
        HierarchyTable = 0x0D,
        /// <summary>Contents table.</summary>
        ContentsTable = 0x0E,
        /// <summary>FAI contents table.</summary>
        AssocContentsTable = 0x0F,
        /// <summary>Search-folder contents table.</summary>
        SearchContentsTable = 0x10,
        /// <summary>Attachment table.</summary>
        AttachmentTable = 0x11,
        /// <summary>Recipient table.</summary>
        RecipientTable = 0x12,
        /// <summary>Internal, persisted view-related.</summary>
        SearchTableIndex = 0x13,
        /// <summary>LTP-internal node (generic).</summary>
        Ltp = 0x1F,
    }
}
