using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace PstBuilder.Messaging
{
    /// <summary>
    /// In plain words: once a box is sealed shut, shrink-wrap it in the background — but only throw away
    /// the original box once the shrink-wrapped one is fully done.
    /// Compresses a finalized PST part into a sibling <c>.zip</c> and removes the raw file, off the
    /// writer's thread. The raw file is only deleted after the archive is fully written and moved into
    /// place under its final name, so a crash mid-compression never leaves neither artifact behind.
    /// </summary>
    internal static class PstPartCompressor
    {
        /// <summary>
        /// Compresses <paramref name="pstPath"/> into <c>{pstPath}.zip</c> and deletes the source, holding
        /// <paramref name="gate"/> for the duration so only one compression runs at a time.
        /// </summary>
        public static async Task CompressAndReplaceAsync(string pstPath, SemaphoreSlim gate)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            string zipPath = pstPath + ".zip";
            string tempZipPath = zipPath + ".tmp";
            try
            {
                using (var zipStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.ReadWrite))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create))
                {
                    var entry = archive.CreateEntry(Path.GetFileName(pstPath), CompressionLevel.Optimal);
                    using (var entryStream = entry.Open())
                    using (var source = new FileStream(pstPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        await source.CopyToAsync(entryStream).ConfigureAwait(false);
                }
                if (File.Exists(zipPath)) File.Delete(zipPath);
                File.Move(tempZipPath, zipPath);
                File.Delete(pstPath);
            }
            catch
            {
                try { if (File.Exists(tempZipPath)) File.Delete(tempZipPath); } catch { /* best-effort cleanup */ }
                throw;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
