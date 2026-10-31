using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class ReportQueueTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        private static ErrorReport Report(string message)
        {
            return new ErrorReport
            {
                Plugin = Texts.DisplayName,
                Version = "1.1.0.0",
                Message = message,
                First = Start,
                Last = Start,
                Session = Start,
            };
        }

        // Definitions of a world that loads the studio's mods, and of one
        // that does not.
        private static readonly string[] OurWorld = { "LargeBlockArmorBlock", "ContratsSirius", "Welder" };
        private static readonly string[] OtherWorld = { "LargeBlockArmorBlock", "Welder", "SomeoneElsesMod" };

        [Fact]
        public void NothingIsSentOutsideOurServers()
        {
            using (var folder = new TemporaryFolder())
            {
                var queue = new ReportQueue(folder.Path);
                Assert.True(queue.Enqueue(Report("first")));
                Assert.True(queue.Enqueue(Report("second")));

                var sent = new List<byte[]>();
                var server = new OurServer();

                // Main menu, single player, a game the player hosts.
                server.Update(null, false);
                Assert.False(server.NeedsALook);
                Assert.False(server.Recognize(OurWorld));
                Assert.Equal(0, queue.Pump(server.IsOurs, sent.Add, 10));

                // Single player or a hosted game, even with the studio's mods.
                server.Update(new object(), false);
                Assert.False(server.NeedsALook);
                Assert.False(server.Recognize(OurWorld));
                Assert.False(server.IsOurs);
                Assert.Equal(0, queue.Pump(server.IsOurs, sent.Add, 10));

                // Another dedicated server, joined as a client, without the
                // studio's mods: looked at once, never ours.
                var elsewhere = new object();
                server.Update(elsewhere, true);
                Assert.True(server.NeedsALook);
                Assert.False(server.Recognize(OtherWorld));
                Assert.False(server.NeedsALook);
                Assert.False(server.Recognize(OurWorld));
                server.Update(elsewhere, true);
                Assert.False(server.IsOurs);
                Assert.False(OurServer.HasMarker(null));
                Assert.False(OurServer.HasMarker(new string[] { null, "" }));
                Assert.Equal(0, queue.Pump(server.IsOurs, sent.Add, 10));

                Assert.Empty(sent);
                Assert.Equal(2, queue.Count);
                Assert.Equal(2, Directory.GetFiles(queue.Directory, "*.json").Length);
            }
        }

        [Fact]
        public void QueuedReportsAreSentOnNextConnection()
        {
            using (var folder = new TemporaryFolder())
            {
                var queue = new ReportQueue(folder.Path);
                queue.Enqueue(Report("first"));
                queue.Enqueue(Report("second"));
                queue.Enqueue(Report("third"));

                var server = new OurServer();
                var game = new object();
                server.Update(game, true);
                Assert.False(server.IsOurs);
                Assert.True(server.Recognize(OurWorld));
                Assert.True(server.IsOurs);
                server.Update(game, true);
                Assert.True(server.IsOurs);
                queue.NewConnection();

                var sent = new List<string>();
                Assert.Equal(2, queue.Pump(server.IsOurs, b => sent.Add(Encoding.UTF8.GetString(b)), 2));
                Assert.Equal(1, queue.Pump(server.IsOurs, b => sent.Add(Encoding.UTF8.GetString(b)), 2));
                Assert.Equal(0, queue.Pump(server.IsOurs, b => sent.Add(Encoding.UTF8.GetString(b)), 2));

                // Oldest first, as written, and erased once sent.
                Assert.Equal(3, sent.Count);
                Assert.Contains("\"message\":\"first\"", sent[0]);
                Assert.Contains("\"message\":\"second\"", sent[1]);
                Assert.Contains("\"message\":\"third\"", sent[2]);
                Assert.Equal(0, queue.Count);

                // Leaving: the next game is not ours until it is looked at.
                server.Update(new object(), true);
                Assert.False(server.IsOurs);
                server.Update(null, false);
                Assert.False(server.IsOurs);
            }
        }

        [Fact]
        public void SendFailureIsSwallowed()
        {
            using (var folder = new TemporaryFolder())
            {
                var queue = new ReportQueue(folder.Path);
                queue.Enqueue(Report("kept"));

                var tries = 0;
                Action<byte[]> failing = b =>
                {
                    tries++;
                    throw new InvalidOperationException("the network is gone");
                };

                // A send that throws never reaches the caller; the report is
                // kept for later.
                Assert.Equal(0, queue.Pump(true, failing, 5));
                Assert.Equal(1, queue.Count);

                // After a few failures, the queue waits for the next connection.
                for (var i = 0; i < 10; i++)
                    queue.Pump(true, failing, 5);
                Assert.Equal(ReportQueue.MaxFailures, tries);

                queue.NewConnection();
                var sent = 0;
                Assert.Equal(1, queue.Pump(true, b => sent++, 5));
                Assert.Equal(1, sent);
                Assert.Equal(0, queue.Count);

                // Writing where nothing can be written: no fault either.
                var blocked = Path.Combine(folder.Path, "a-file");
                File.WriteAllText(blocked, "");
                var unwritable = new ReportQueue(blocked);
                Assert.False(unwritable.Enqueue(Report("lost")));
                Assert.Equal(0, unwritable.Pump(true, b => { }, 5));
                Assert.False(unwritable.Enqueue(null));

                // The collector and the log never throw either.
                var log = new PluginLog("sir-correctifs-pulsar", line => { throw new IOException("log closed"); },
                    new ErrorReports(null, null, Start, null), () => { throw new InvalidOperationException("no clock"); });
                log.Error("still fine");
                log.Error("still fine", null);
                Assert.Equal(2, log.Reports.TakeAll(Start).Count);
            }
        }

        [Fact]
        public void TheChannelIsTheOneTheServerListensTo()
        {
            Assert.Equal(5465479001L, OurServer.RuleChannel);
            Assert.Equal(46479, OurServer.Channel);
            Assert.True(OurServer.Channel <= ushort.MaxValue);
        }
    }
}
