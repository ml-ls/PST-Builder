namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: what shape a property's value is — a number, a date, some text, raw bytes, etc.
    /// MAPI property types (PtypXxx). Values from [MS-OXCDATA] 2.11.1. Only the subset needed for the
    /// mail/contact/calendar/task/note item set is enumerated; others can be added as required.
    /// </summary>
    public enum PropertyType : ushort
    {
        /// <summary>2-byte signed integer.</summary>
        Integer16 = 0x0002,
        /// <summary>4-byte signed integer.</summary>
        Integer32 = 0x0003,
        /// <summary>4-byte floating point.</summary>
        Floating32 = 0x0004,
        /// <summary>8-byte floating point.</summary>
        Floating64 = 0x0005,
        /// <summary>8-byte currency.</summary>
        Currency = 0x0006,
        /// <summary>8-byte application time.</summary>
        AppTime = 0x0007,
        /// <summary>4-byte error code.</summary>
        ErrorCode = 0x000A,
        /// <summary>1-byte boolean (stored inline as a 4-byte slot).</summary>
        Boolean = 0x000B,
        /// <summary>8-byte signed integer.</summary>
        Integer64 = 0x0014,
        /// <summary>Unicode (UTF-16LE) string.</summary>
        String = 0x001F,
        /// <summary>8-bit/codepage string.</summary>
        String8 = 0x001E,
        /// <summary>8-byte FILETIME.</summary>
        Time = 0x0040,
        /// <summary>16-byte GUID.</summary>
        Guid = 0x0048,
        /// <summary>Binary blob.</summary>
        Binary = 0x0102,
        /// <summary>Multiple 32-bit integers (PtypMultipleInteger32). Stored variable-length (HNID).</summary>
        MultipleInteger32 = 0x1003,
    }

    /// <summary>Classification helpers for how a property value is stored in a PC/TC.</summary>
    public static class PropertyTypes
    {
        /// <summary>
        /// Returns the fixed size in bytes for a fixed-length type, or -1 for variable-length types
        /// (String/String8/Binary and multi-valued variants).
        /// </summary>
        public static int FixedSize(PropertyType type)
        {
            switch (type)
            {
                case PropertyType.Integer16: return 2;
                case PropertyType.Integer32: return 4;
                case PropertyType.Floating32: return 4;
                case PropertyType.ErrorCode: return 4;
                case PropertyType.Boolean: return 1;
                case PropertyType.Floating64: return 8;
                case PropertyType.Currency: return 8;
                case PropertyType.AppTime: return 8;
                case PropertyType.Integer64: return 8;
                case PropertyType.Time: return 8;
                case PropertyType.Guid: return 16;
                default: return -1; // variable
            }
        }

        /// <summary>
        /// True when a PC stores the value directly in the 4-byte dwValueHnid slot (fixed types ≤ 4 bytes).
        /// All other types are referenced via an HNID.
        /// </summary>
        public static bool IsInlineInPc(PropertyType type)
        {
            int size = FixedSize(type);
            return size >= 0 && size <= 4;
        }
    }
}
