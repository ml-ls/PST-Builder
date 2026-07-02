#pragma warning disable CS1591 // self-describing MAPI property-id constants
namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: the numeric labels for the fields we set — Subject, Sender, Size, and friends.
    /// MAPI property ids (the upper 16 bits of a property tag). Names follow [MS-OXPROPS]. Only the
    /// subset used by the store/folder/message/recipient builders is listed.
    /// </summary>
    public static class PropertyTags
    {
        // Message store (MS-PST 2.4.3).
        public const ushort RecordKey = 0x0FF9;             // PtypBinary
        public const ushort DisplayName = 0x3001;           // PtypString
        public const ushort IpmSubTreeEntryId = 0x35E0;     // PtypBinary
        public const ushort IpmWastebasketEntryId = 0x35E3; // PtypBinary
        public const ushort FinderEntryId = 0x35E7;         // PtypBinary

        // Folder (MS-PST 2.4.4).
        public const ushort ContentCount = 0x3602;          // PtypInteger32
        public const ushort ContentUnreadCount = 0x3603;    // PtypInteger32
        public const ushort Subfolders = 0x360A;            // PtypBoolean
        public const ushort ContainerClass = 0x3613;        // PtypString (IPF.Contact, IPF.Appointment, …)
        public const ushort PstHiddenCount = 0x6635;        // PtypInteger32
        public const ushort PstHiddenUnread = 0x6636;       // PtypInteger32

        // Message store extras (MS-PST 2.4.3) — observed in a real blank Outlook store.
        public const ushort PstPassword = 0x67FF;           // PtypInteger32 (0 = no password)
        public const ushort ValidFolderMask = 0x35DF;       // PtypInteger32 (which IPM folders exist)
        public const ushort ReplVersionHistory = 0x0E34;    // PtypBinary
        public const ushort ReplFlags = 0x0E38;             // PtypInteger32
        public const ushort PstLrNoRestrictions = 0x6633;   // PtypBoolean
        public const ushort PstIdsToPids = 0x66FA;          // PtypInteger32 (store-internal)
        public const ushort PstStoreVersion = 0x66FC;       // PtypInteger32 (store-internal)

        // ValidFolderMask bits (MS-PST 2.4.3.1): which special folders the store advertises.
        public const int FolderMaskIpmSubtree = 0x1;
        public const int FolderMaskInbox = 0x2;
        public const int FolderMaskOutbox = 0x4;
        public const int FolderMaskWastebasket = 0x8;
        public const int FolderMaskSentmail = 0x10;
        public const int FolderMaskViews = 0x20;
        public const int FolderMaskCommonViews = 0x40;
        public const int FolderMaskFinder = 0x80;

        // Contents-table template extras (MS-PST 2.4.4.5).
        public const ushort MessageStatus = 0x0E17;         // PtypInteger32
        public const ushort ConversationTopic = 0x0070;     // PtypString

        // Recipient-table template extras (MS-PST 2.4.5.3).
        public const ushort Responsibility = 0x0E0F;        // PtypBoolean
        public const ushort DisplayType = 0x3900;           // PtypInteger32
        public const ushort AddressBookDisplayNamePrintable = 0x39FF; // PtypString (PidTag7BitDisplayName)
        public const ushort SendRichInfo = 0x3A40;          // PtypBoolean

        // Contact (standard tags; e-mail/IM live in PSETID_Address named props).
        public const ushort GivenName = 0x3A06;             // PtypString
        public const ushort Surname = 0x3A11;               // PtypString
        public const ushort CompanyName = 0x3A16;           // PtypString
        public const ushort JobTitle = 0x3A17;              // PtypString
        public const ushort BusinessTelephoneNumber = 0x3A08; // PtypString
        public const ushort HomeTelephoneNumber = 0x3A09;   // PtypString
        public const ushort MobileTelephoneNumber = 0x3A1C; // PtypString

        // Message (MS-PST 2.4.5).
        public const ushort MessageClass = 0x001A;          // PtypString
        public const ushort Importance = 0x0017;            // PtypInteger32
        public const ushort Sensitivity = 0x0036;           // PtypInteger32
        public const ushort Subject = 0x0037;               // PtypString
        public const ushort ClientSubmitTime = 0x0039;      // PtypTime
        public const ushort SentRepresentingName = 0x0042;  // PtypString (the "From" column)
        public const ushort SentRepresentingEmailAddress = 0x0065; // PtypString
        public const ushort SenderName = 0x0C1A;            // PtypString
        public const ushort SenderEmailAddress = 0x0C1F;    // PtypString
        public const ushort DisplayTo = 0x0E04;             // PtypString
        public const ushort MessageDeliveryTime = 0x0E06;   // PtypTime
        public const ushort MessageFlags = 0x0E07;          // PtypInteger32
        public const ushort MessageSize = 0x0E08;           // PtypInteger32
        public const ushort Body = 0x1000;                  // PtypString
        public const ushort Html = 0x1013;                  // PtypBinary (HTML body)
        public const ushort NativeBody = 0x1016;            // PtypInteger32 (1=plain, 2=RTF, 3=HTML)
        public const ushort CreationTime = 0x3007;          // PtypTime
        public const ushort LastModificationTime = 0x3008;  // PtypTime
        public const ushort SearchKey = 0x300B;             // PtypBinary

        // Attachment object / table (MS-PST 2.4.6).
        public const ushort AttachSize = 0x0E20;            // PtypInteger32
        public const ushort AttachNumber = 0x0E21;          // PtypInteger32
        public const ushort AttachDataBinary = 0x3701;      // PtypBinary
        public const ushort AttachFilename = 0x3704;        // PtypString (short name)
        public const ushort AttachMethod = 0x3705;          // PtypInteger32
        public const ushort AttachLongFilename = 0x3707;    // PtypString
        public const ushort RenderingPosition = 0x370B;     // PtypInteger32
        public const ushort AttachMimeTag = 0x370E;         // PtypString
        public const ushort AttachContentId = 0x3712;       // PtypString
        public const ushort AttachFlags = 0x3714;           // PtypInteger32

        // Recipient (MS-PST 2.4.5.3).
        public const ushort RecipientType = 0x0C15;         // PtypInteger32
        public const ushort ObjectType = 0x0FFE;            // PtypInteger32
        public const ushort EntryId = 0x0FFF;               // PtypBinary
        public const ushort AddressType = 0x3002;           // PtypString
        public const ushort EmailAddress = 0x3003;          // PtypString
        public const ushort RecipientFlags = 0x5FFD;        // PtypInteger32

        // Name-to-id map (MS-PST 2.4.7).
        public const ushort NameidBucketCount = 0x0001;     // PtypInteger32
        public const ushort NameidStreamGuid = 0x0002;      // PtypBinary
        public const ushort NameidStreamEntry = 0x0003;     // PtypBinary
        public const ushort NameidStreamString = 0x0004;    // PtypBinary
        public const ushort NameidBucketBase = 0x1000;      // hash buckets: 0x1000 + index (PtypBinary)

        // Common values.
        public const int MsgFlagRead = 0x01;
        public const int MsgFlagHasAttach = 0x10;
        public const int RecipTypeTo = 0x01;
        public const int ObjectTypeMailUser = 0x06;
        public const int ObjectTypeAttachment = 0x07;
        public const int AttachByValue = 0x01;
        public const int RenderingPositionNone = unchecked((int)0xFFFFFFFF);
        public const int AttachFlagMhtmlRef = 0x04; // ATT_MHTML_REF: attachment is referenced by the HTML body
    }
}
