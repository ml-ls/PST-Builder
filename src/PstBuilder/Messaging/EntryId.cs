using System;
using PstBuilder.Foundation;

namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: makes the little "address label" that points the store at a specific folder.
    /// Builds PST EntryIDs (MS-PST 2.4.3.2). An EntryID is 24 bytes: 4 zero flag bytes, the 16-byte
    /// store provider UID (= PidTagRecordKey), then the 4-byte NID of the referenced object.
    /// </summary>
    public static class EntryId
    {
        /// <summary>Size of an EntryID in bytes.</summary>
        public const int Size = 24;

        /// <summary>Builds an EntryID referencing the node <paramref name="nid"/> in the store identified by <paramref name="storeUid"/>.</summary>
        public static byte[] ForNode(byte[] storeUid, Nid nid)
        {
            if (storeUid == null || storeUid.Length != 16)
                throw new ArgumentException("Store UID must be 16 bytes.", nameof(storeUid));
            var b = new byte[Size];
            // bytes [0,4) are flags = 0
            Array.Copy(storeUid, 0, b, 4, 16);
            BitConverter.GetBytes(nid.Value).CopyTo(b, 20);
            return b;
        }
    }
}
