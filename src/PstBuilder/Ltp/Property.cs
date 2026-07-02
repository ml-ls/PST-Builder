using System;
using System.IO;
using System.Text;

namespace PstBuilder.Ltp
{
    /// <summary>
    /// In plain words: one label-and-value pair to put in a bag, e.g. "Subject = Hello".
    /// A single property value destined for a Property Context. <see cref="Data"/> holds the raw
    /// little-endian value bytes (for fixed types) or the encoded payload (for String/Binary).
    ///
    /// <para>A large binary value (typically an attachment payload) can instead be supplied as a
    /// <em>stream source</em> via <see cref="BinaryStream"/>: the bytes are never held in memory as a
    /// single array — they are read on demand, block by block, straight to disk. Such a value is always
    /// spilled to the node's subnode tree, and its byte length is known up front (<see cref="ValueLength"/>).</para>
    /// </summary>
    public sealed class Property
    {
        /// <summary>Property id (upper 16 bits of the property tag).</summary>
        public ushort PropId { get; }
        /// <summary>Property type.</summary>
        public PropertyType Type { get; }
        /// <summary>Raw value bytes. Empty means a present-but-empty variable property (or a streamed value).</summary>
        public byte[] Data { get; }

        // When set, the value is produced on demand from a fresh stream rather than held in Data.
        internal Func<Stream>? StreamSource { get; }
        private readonly long _streamLength;

        /// <summary>Byte length of the value — <see cref="Data"/>.Length, or the declared length for a streamed value.</summary>
        public long ValueLength => StreamSource != null ? _streamLength : Data.Length;

        /// <summary>Creates a property from raw value bytes.</summary>
        public Property(ushort propId, PropertyType type, byte[] data)
        {
            PropId = propId;
            Type = type;
            Data = data ?? Array.Empty<byte>();
        }

        private Property(ushort propId, PropertyType type, Func<Stream> streamSource, long length)
        {
            PropId = propId;
            Type = type;
            Data = Array.Empty<byte>();
            StreamSource = streamSource ?? throw new ArgumentNullException(nameof(streamSource));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            _streamLength = length;
        }

        /// <summary>
        /// A binary property whose bytes are read on demand from the stream returned by
        /// <paramref name="open"/>. <paramref name="length"/> must equal the exact number of bytes the
        /// stream will yield. The value is streamed to disk in blocks (never fully buffered) and stored in
        /// the node's subnode tree. <paramref name="open"/> is invoked exactly once; the returned stream
        /// is read sequentially and disposed by the writer.
        /// </summary>
        public static Property BinaryStream(ushort propId, Func<Stream> open, long length) =>
            new Property(propId, PropertyType.Binary, open, length);

        /// <summary>A 32-bit integer property.</summary>
        public static Property Int32(ushort propId, int value) =>
            new Property(propId, PropertyType.Integer32, BitConverter.GetBytes(value));

        /// <summary>A 16-bit integer property.</summary>
        public static Property Int16(ushort propId, short value) =>
            new Property(propId, PropertyType.Integer16, BitConverter.GetBytes(value));

        /// <summary>A boolean property.</summary>
        public static Property Bool(ushort propId, bool value) =>
            new Property(propId, PropertyType.Boolean, new[] { (byte)(value ? 1 : 0) });

        /// <summary>A 64-bit integer property.</summary>
        public static Property Int64(ushort propId, long value) =>
            new Property(propId, PropertyType.Integer64, BitConverter.GetBytes(value));

        /// <summary>A Unicode (UTF-16LE) string property.</summary>
        public static Property Unicode(ushort propId, string value) =>
            new Property(propId, PropertyType.String, Encoding.Unicode.GetBytes(value ?? string.Empty));

        /// <summary>A binary property.</summary>
        public static Property Binary(ushort propId, byte[] value) =>
            new Property(propId, PropertyType.Binary, value ?? Array.Empty<byte>());

        /// <summary>A FILETIME property from a UTC timestamp.</summary>
        public static Property Time(ushort propId, DateTime utc) =>
            new Property(propId, PropertyType.Time, BitConverter.GetBytes(utc.ToFileTimeUtc()));
    }
}
