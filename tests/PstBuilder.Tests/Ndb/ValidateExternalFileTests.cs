using System;
using System.IO;
using Xunit;

namespace PstBuilder.Tests.Ndb
{
    /// <summary>
    /// On-demand validator: set PST_VALIDATE_FILE to a .pst path and run this test to walk it with the
    /// strict round-trip reader (header CRCs, NBT/BBT signatures+CRCs, every block, every AMap region).
    /// Skipped when the env var is unset so it never affects normal CI runs.
    /// </summary>
    public class ValidateExternalFileTests
    {
        [Fact]
        public void ValidatesFileFromEnvVar()
        {
            string? path = Environment.GetEnvironmentVariable("PST_VALIDATE_FILE");
            if (string.IsNullOrEmpty(path))
                return; // nothing to do

            Assert.True(File.Exists(path), $"File not found: {path}");
            byte[] file = File.ReadAllBytes(path);
            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();
            new LtpValidator(file).ValidateAll();
        }

        [Fact]
        public void DumpFileFromEnvVar()
        {
            string? path = Environment.GetEnvironmentVariable("PST_DUMP_FILE");
            if (string.IsNullOrEmpty(path)) return;
            byte[] file = File.ReadAllBytes(path);
            var lines = new LtpValidator(file).Dump();
            string outPath = path + ".dump.txt";
            File.WriteAllLines(outPath, lines);
            foreach (var l in lines) System.Console.WriteLine(l);

            string? node = Environment.GetEnvironmentVariable("PST_DUMP_NODE");
            if (!string.IsNullOrEmpty(node))
            {
                uint nid = Convert.ToUInt32(node, 16);
                var v = new LtpValidator(file).DumpNode(nid);
                File.WriteAllLines(path + ".node.txt", v);
                foreach (var l in v) System.Console.WriteLine(l);
            }
        }
    }
}
