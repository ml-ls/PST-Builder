using System;
using System.IO;
using System.Linq;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    public class SplitPstTests
    {
        [Fact]
        public void Split_RollsOverBySize_AndEachPartIsValid()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pstsplit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string basePath = Path.Combine(dir, "mailbox.pst");
            try
            {
                PstExportResult result;
                using (var session = PstExportSession.CreateSplit(basePath, maxBytesPerFile: 350_000))
                {
                    for (int i = 0; i < 1500; i++)
                    {
                        var m = new MessageItem
                        {
                            Subject = $"Split message {i} with some length to grow the file",
                            SenderName = $"Sender {i % 13}",
                            DisplayTo = "Me",
                            Body = new string('x', 200),
                        };
                        m.Recipients.Add(new RecipientItem { DisplayName = "Me", EmailAddress = "me@example.com" });
                        session.AddMessage("Inbox", m);
                    }
                    result = session.Complete();
                }

                Assert.True(result.Parts.Count > 1, $"Expected multiple parts, got {result.Parts.Count}.");

                // First part keeps the base name; later parts are numbered.
                Assert.Equal(basePath, result.Parts[0].Name);
                Assert.EndsWith("-002.pst", result.Parts[1].Name);

                int total = 0;
                foreach (var part in result.Parts)
                {
                    Assert.True(File.Exists(part.Name));
                    var reader = new NdbRoundTripReader(File.ReadAllBytes(part.Name));
                    reader.ReadAndValidate(); // each part is a standalone, valid PST
                    total += reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage);
                }
                Assert.Equal(1500, total); // every message landed in exactly one part
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
