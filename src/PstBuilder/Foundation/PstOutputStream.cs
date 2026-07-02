using System;
using System.IO;

namespace PstBuilder.Foundation
{
    /// <summary>
    /// In plain words: a notebook we only ever add to the end of — never erasing — except for one tidy-up
    /// at the very end to fill in the front cover (the header).
    /// Append-only output sink for the PST file. Blocks and pages only ever grow forward, so this
    /// exposes a strictly increasing 64-bit write position plus alignment helpers. The header region
    /// (the first <see cref="PstConstants.HeaderSize"/> bytes) is reserved up front and patched in a
    /// single seek at finalisation; everything else is written sequentially.
    /// </summary>
    public sealed class PstOutputStream : IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private long _position;

        /// <summary>Wraps a seekable, writable stream. The stream must be empty/at position 0.</summary>
        public PstOutputStream(Stream stream, bool leaveOpen = false)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            if (!_stream.CanWrite) throw new ArgumentException("Stream must be writable.", nameof(stream));
            if (!_stream.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(stream));
            _leaveOpen = leaveOpen;
            _position = 0;
        }

        /// <summary>The next byte offset that will be written (the current logical end of file).</summary>
        public long Position => _position;

        /// <summary>
        /// Reserves <paramref name="length"/> zero-filled bytes at the front for the header, advancing
        /// the position past them. Call once, first.
        /// </summary>
        public void ReserveHeader(int length)
        {
            if (_position != 0) throw new InvalidOperationException("Header must be reserved before any other write.");
            Span<byte> zeros = stackalloc byte[256];
            int remaining = length;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, zeros.Length);
                _stream.Write(GetArray(zeros.Slice(0, chunk)), 0, chunk);
                remaining -= chunk;
            }
            _position = length;
        }

        /// <summary>Appends bytes at the current end of file and returns the offset they were written at.</summary>
        public long Append(ReadOnlySpan<byte> data)
        {
            long start = _position;
            _stream.Write(GetArray(data), 0, data.Length);
            _position += data.Length;
            return start;
        }

        /// <summary>
        /// Pads the output with zero bytes until the position is a multiple of <paramref name="alignment"/>,
        /// returning the new aligned position.
        /// </summary>
        public long AlignTo(int alignment)
        {
            long misaligned = _position % alignment;
            if (misaligned != 0)
            {
                int pad = (int)(alignment - misaligned);
                Span<byte> zeros = stackalloc byte[64];
                while (pad > 0)
                {
                    int chunk = Math.Min(pad, zeros.Length);
                    _stream.Write(GetArray(zeros.Slice(0, chunk)), 0, chunk);
                    _position += chunk;
                    pad -= chunk;
                }
            }
            return _position;
        }

        /// <summary>
        /// Overwrites bytes at an absolute offset that was previously written/reserved (used only to
        /// patch the header at finalisation), then restores the append position.
        /// </summary>
        public void PatchAt(long offset, ReadOnlySpan<byte> data)
        {
            if (offset < 0 || offset + data.Length > _position)
                throw new ArgumentOutOfRangeException(nameof(offset), "Patch range lies outside written data.");
            long resume = _stream.Position;
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(GetArray(data), 0, data.Length);
            _stream.Seek(resume, SeekOrigin.Begin);
        }

        /// <summary>Flushes the underlying stream.</summary>
        public void Flush() => _stream.Flush();

        /// <summary>
        /// Flushes all the way to disk (a real fsync for a <see cref="FileStream"/>), so a finalized part
        /// survives a power loss, not just a process crash. Best-effort for non-file streams.
        /// </summary>
        public void FlushToDisk()
        {
            if (_stream is FileStream fs) fs.Flush(flushToDisk: true);
            else _stream.Flush();
        }

        // netstandard2.0 Stream has no Span overload; copy through a pooled-free temporary.
        // Kept private and small; hot paths pass already-materialized arrays via Append(byte[]).
        private static byte[] GetArray(ReadOnlySpan<byte> data)
        {
            var arr = new byte[data.Length];
            data.CopyTo(arr);
            return arr;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            _stream.Flush();
            if (!_leaveOpen) _stream.Dispose();
        }
    }
}
