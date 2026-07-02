using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PstBuilder.Foundation;
using PstBuilder.Messaging;
using PstBuilder.Ndb;
using PstBuilder.Tests.Ndb;
using Xunit;

namespace PstBuilder.Tests.Messaging
{
    public class PstExportSessionTests
    {
        [Fact]
        public void ParallelProducers_SingleWrite_RoundTrips()
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            using (var session = PstExportSession.Create(os, "Parallel Export"))
            {
                // Many threads push items concurrently into several folders.
                Parallel.For(0, 600, i =>
                {
                    string folder = "Inbox/" + (i % 3);
                    var m = new MessageItem
                    {
                        Subject = $"Concurrent message {i}",
                        SenderName = $"Sender {i % 17}",
                        DisplayTo = "Me",
                        Body = $"Body {i}",
                    };
                    m.Recipients.Add(new RecipientItem { DisplayName = "Me", EmailAddress = "me@example.com" });
                    session.AddMessage(folder, m);
                });

                // Mixed item types from the calling thread too.
                session.AddContact("Contacts", new ContactItem { DisplayName = "Jane", Email = "jane@example.com" });
                session.AddAppointment("Calendar", new AppointmentItem
                {
                    Subject = "Sync", StartUtc = DateTime.UtcNow, EndUtc = DateTime.UtcNow.AddHours(1),
                });

                session.Complete();
            }

            var reader = new NdbRoundTripReader(ms.ToArray());
            reader.ReadAndValidate();

            int messages = reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage);
            Assert.Equal(602, messages); // 600 mail + 1 contact + 1 appointment
        }

        [Fact]
        public void AddAfterComplete_Throws()
        {
            using var ms = new MemoryStream();
            using var os = new PstOutputStream(ms, leaveOpen: true);
            var session = PstExportSession.Create(os);
            session.AddMessage("Inbox", new MessageItem { Subject = "x" });
            session.Complete();
            Assert.Throws<InvalidOperationException>(() => session.AddMessage("Inbox", new MessageItem { Subject = "y" }));
            session.Dispose();
        }

        [Fact]
        public async Task AsyncProducers_CompleteAsync_RoundTrips()
        {
            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            using (var session = PstExportSession.Create(os, "Async Export", queueCapacity: 8)) // tiny queue → exercises await backpressure
            {
                // Several concurrent async producers awaiting queue space.
                var producers = Enumerable.Range(0, 4).Select(t => Task.Run(async () =>
                {
                    for (int i = t; i < 500; i += 4)
                    {
                        var m = new MessageItem
                        {
                            Subject = $"Async message {i}", SenderName = $"Sender {i % 13}",
                            DisplayTo = "Me", Body = $"Body {i}",
                        };
                        m.Recipients.Add(new RecipientItem { DisplayName = "Me", EmailAddress = "me@example.com" });
                        await session.AddMessageAsync("Inbox/" + (i % 3), m);
                    }
                })).ToArray();
                await Task.WhenAll(producers);

                await session.AddContactAsync("Contacts", new ContactItem { DisplayName = "Jane", Email = "jane@example.com" });

                await session.CompleteAsync();
            }

            var reader = new NdbRoundTripReader(ms.ToArray());
            reader.ReadAndValidate();
            int messages = reader.Nodes.Count(kv => new Nid(kv.Key).Type == NidType.NormalMessage);
            Assert.Equal(501, messages); // 500 mail + 1 contact
        }

        // Captures reports synchronously (unlike Progress<T>, which marshals via a captured context).
        private sealed class SyncProgress : IProgress<ExportProgress>
        {
            public readonly ConcurrentQueue<ExportProgress> Reports = new ConcurrentQueue<ExportProgress>();
            public void Report(ExportProgress value) => Reports.Enqueue(value);
        }

        [Fact]
        public void Progress_ReportsInterimAndFinalSnapshot()
        {
            var progress = new SyncProgress();

            using var ms = new MemoryStream();
            using (var os = new PstOutputStream(ms, leaveOpen: true))
            using (var session = PstExportSession.Create(os, "Progress", queueCapacity: 64,
                       progress: progress, progressInterval: 50))
            {
                for (int i = 0; i < 200; i++)
                    session.AddMessage("Inbox", new MessageItem { Subject = "m" + i });
                session.Complete();
            }

            var all = progress.Reports.ToArray();
            var final = all.Single(r => r.IsCompleted);
            Assert.Equal(200, final.ItemsWritten);
            Assert.True(final.BytesWritten > 0);
            Assert.Contains(all, r => !r.IsCompleted); // at least one interim tick fired (every 50 items)
        }

        [Fact]
        public async Task AddAsyncAfterCompleteAsync_Throws()
        {
            using var ms = new MemoryStream();
            using var os = new PstOutputStream(ms, leaveOpen: true);
            var session = PstExportSession.Create(os);
            await session.AddMessageAsync("Inbox", new MessageItem { Subject = "x" });
            await session.CompleteAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.AddMessageAsync("Inbox", new MessageItem { Subject = "y" }));
            session.Dispose();
        }
    }
}
