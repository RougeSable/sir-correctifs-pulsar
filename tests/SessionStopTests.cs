using System.Collections.Generic;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class SessionStopTests
    {
        [Fact]
        public void AStopWritesToTheLogAndTellsThePlayer()
        {
            var log = new List<string>();
            var stop = new SessionStop(log.Add);

            Assert.True(stop.Stop("technical detail", Texts.StopUnexpectedGame));

            Assert.True(stop.IsStopped);
            Assert.Equal(new[] { "technical detail" }, log);
            Assert.Equal(Texts.StopUnexpectedGame, stop.Reason);

            var seen = new List<string>();
            stop.Deliver(true, seen.Add);
            Assert.Equal(new[] { Texts.StopUnexpectedGame }, seen);
        }

        [Fact]
        public void AStopLastsForTheSession()
        {
            var log = new List<string>();
            var stop = new SessionStop(log.Add);

            stop.Stop("first", "first message");
            Assert.False(stop.Stop("second", "second message"));

            Assert.True(stop.IsStopped);
            Assert.Single(log);
            Assert.Equal("first message", stop.Reason);
        }

        [Fact]
        public void InTheMenuNotificationsWaitForAGame()
        {
            var stop = new SessionStop(null);
            stop.Stop("detail", "message");

            var seen = new List<string>();
            Assert.Equal(0, stop.Deliver(false, seen.Add));
            Assert.Empty(seen);

            Assert.Equal(1, stop.Deliver(true, seen.Add));
            Assert.Equal(new[] { "message" }, seen);

            Assert.Equal(0, stop.Deliver(true, seen.Add));
        }

        [Fact]
        public void SteppingAsideTellsThePlayer()
        {
            var stop = new SessionStop(null);
            stop.Stop("patched by other.plugin", string.Format(Texts.StopCoexistence, "other.plugin"));

            Assert.StartsWith(Texts.StopPrefix, stop.Reason);
            Assert.Contains("other.plugin", stop.Reason);
            Assert.Contains("steps aside", stop.Reason);
        }
    }
}
