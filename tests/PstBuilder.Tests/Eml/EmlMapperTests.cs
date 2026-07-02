using System;
using System.IO;
using System.Text;
using PstBuilder.Eml;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Eml
{
    public class EmlMapperTests
    {
        private const string SampleEml =
            "From: Alice Example <alice@example.com>\r\n" +
            "To: Bob Tester <bob@example.com>\r\n" +
            "Cc: Carol <carol@example.com>\r\n" +
            "Subject: Quarterly report\r\n" +
            "Date: Tue, 23 Jun 2026 14:05:00 +0000\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Content-Type: multipart/alternative; boundary=\"b\"\r\n" +
            "\r\n" +
            "--b\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\n" +
            "Here is the plain text body.\r\n" +
            "--b\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n\r\n" +
            "<html><body><p>Here is the <b>HTML</b> body.</p></body></html>\r\n" +
            "--b--\r\n";

        [Fact]
        public void ParsesHeadersBodiesAndRecipients()
        {
            var msg = EmlMapper.ToMessageItem(Encoding.ASCII.GetBytes(SampleEml));

            Assert.Equal("Quarterly report", msg.Subject);
            Assert.Equal("Alice Example", msg.SenderName);
            Assert.Equal("alice@example.com", msg.SenderEmail);
            Assert.Contains("plain text body", msg.Body);
            Assert.Contains("<b>HTML</b>", msg.BodyHtml);
            Assert.Equal(new DateTime(2026, 6, 23, 14, 5, 0, DateTimeKind.Utc), msg.DeliveryTimeUtc);

            // To + Cc become recipients with the right types.
            Assert.Equal(2, msg.Recipients.Count);
            Assert.Contains(msg.Recipients, r => r.EmailAddress == "bob@example.com" && r.RecipientType == 0x01);
            Assert.Contains(msg.Recipients, r => r.EmailAddress == "carol@example.com" && r.RecipientType == 0x02);
            Assert.Contains("Bob Tester", msg.DisplayTo);
        }

        [Fact]
        public void EmlAddedToStore_RoundTripsAsValidPst()
        {
            var store = new StoreWriter { StoreDisplayName = "EML Test" };
            var inbox = store.IpmSubtree.AddFolder("Inbox");
            inbox.AddEml(Encoding.ASCII.GetBytes(SampleEml));

            byte[] file;
            using (var ms = new MemoryStream())
            {
                using (var os = new PstOutputStream(ms, leaveOpen: true))
                    store.Write(os);
                file = ms.ToArray();
            }

            var reader = new NdbRoundTripReader(file);
            reader.ReadAndValidate();

            bool hasMessage = false;
            foreach (var kv in reader.Nodes)
                if (new Nid(kv.Key).Type == NidType.NormalMessage) hasMessage = true;
            Assert.True(hasMessage);
        }
    }
}
