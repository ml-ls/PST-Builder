using System;
using System.IO;
using System.Linq;
using PstBuilder;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    /// <summary>Exercises the <see cref="Pst"/> one-line convenience façade.</summary>
    public class PstFacadeTests
    {
        [Fact]
        public void Write_ComposesTree_AndProducesValidPst()
        {
            string path = Path.Combine(Path.GetTempPath(), "pstbuilder-facade-" + Guid.NewGuid().ToString("N") + ".pst");
            try
            {
                Pst.Write(path, top =>
                {
                    var inbox = top.AddFolder("Inbox");
                    inbox.AddMessage(new MessageItem { Subject = "Hello", Body = "Hi there", DisplayTo = "Me" });
                    var projects = top.AddFolder("Projects");
                    projects.AddFolder("2026").AddMessage(new MessageItem { Subject = "Nested" });
                }, "My Store");

                var reader = new NdbRoundTripReader(File.ReadAllBytes(path));
                reader.ReadAndValidate();
                int messages = reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage);
                Assert.Equal(2, messages);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Create_ReturnsWorkingStreamingSession()
        {
            string path = Path.Combine(Path.GetTempPath(), "pstbuilder-facade-" + Guid.NewGuid().ToString("N") + ".pst");
            try
            {
                using (var session = Pst.Create(path, "Streamed"))
                {
                    session.AddMessage("Inbox", new MessageItem { Subject = "one" });
                    session.AddContact("Contacts", new ContactItem { DisplayName = "Jane", Email = "jane@example.com" });
                    session.Complete();
                }

                new NdbRoundTripReader(File.ReadAllBytes(path)).ReadAndValidate();
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
