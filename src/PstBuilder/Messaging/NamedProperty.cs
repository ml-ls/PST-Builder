using System;
using System.Text;
using PstBuilder.Ltp;

namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: a special "named" field (the kind contacts and calendar items use) before it's
    /// been handed its numeric slot in this particular file.
    /// A named property value carried on an item before resolution. Identified by a property-set GUID
    /// plus either a numeric long ID (LID) or a string name. The store assigns its NPID (≥ 0x8000) at
    /// write time via the <see cref="NamedPropertyRegistry"/>.
    /// </summary>
    public sealed class NamedProperty
    {
        /// <summary>Property-set GUID.</summary>
        public Guid Set { get; }
        /// <summary>Numeric long id (LID) when this is a numeric named property; otherwise null.</summary>
        public uint? Lid { get; }
        /// <summary>String name when this is a string named property; otherwise null.</summary>
        public string? Name { get; }
        /// <summary>Value type.</summary>
        public PropertyType Type { get; }
        /// <summary>Raw little-endian value bytes.</summary>
        public byte[] Data { get; }

        private NamedProperty(Guid set, uint? lid, string? name, PropertyType type, byte[] data)
        {
            Set = set;
            Lid = lid;
            Name = name;
            Type = type;
            Data = data ?? Array.Empty<byte>();
        }

        /// <summary>A numeric (LID) named property from raw value bytes.</summary>
        public static NamedProperty Numeric(Guid set, uint lid, PropertyType type, byte[] data) =>
            new NamedProperty(set, lid, null, type, data);

        /// <summary>A string-named property from raw value bytes.</summary>
        public static NamedProperty String(Guid set, string name, PropertyType type, byte[] data) =>
            new NamedProperty(set, null, name, type, data);

        // Convenience constructors for numeric (LID) named properties — the common case.
        /// <summary>A numeric named Unicode string property.</summary>
        public static NamedProperty Text(Guid set, uint lid, string value) =>
            Numeric(set, lid, PropertyType.String, Encoding.Unicode.GetBytes(value ?? string.Empty));
        /// <summary>A numeric named 32-bit integer property.</summary>
        public static NamedProperty Int32(Guid set, uint lid, int value) =>
            Numeric(set, lid, PropertyType.Integer32, BitConverter.GetBytes(value));
        /// <summary>A numeric named boolean property.</summary>
        public static NamedProperty Bool(Guid set, uint lid, bool value) =>
            Numeric(set, lid, PropertyType.Boolean, new[] { (byte)(value ? 1 : 0) });
        /// <summary>A numeric named 8-byte floating-point property.</summary>
        public static NamedProperty Double(Guid set, uint lid, double value) =>
            Numeric(set, lid, PropertyType.Floating64, BitConverter.GetBytes(value));
        /// <summary>A numeric named FILETIME property.</summary>
        public static NamedProperty Time(Guid set, uint lid, DateTime utc) =>
            Numeric(set, lid, PropertyType.Time, BitConverter.GetBytes(utc.ToFileTimeUtc()));
    }
}
