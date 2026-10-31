using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class ErrorReportTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture
        {
            public readonly List<string> GameLog = new List<string>();
            public DateTime Now = Start;
            public readonly ErrorReports Reports;
            public readonly PluginLog Log;

            public Fixture()
            {
                Reports = new ErrorReports(Texts.DisplayName, "1.1.0.0", Start, new Sanitizer());
                Log = new PluginLog("sir-correctifs-pulsar", GameLog.Add, Reports, () => Now);
            }
        }

        private static Exception Thrown()
        {
            try
            {
                throw new InvalidOperationException("the hierarchy changed while it was read");
            }
            catch (Exception e)
            {
                return e;
            }
        }

        [Fact]
        public void CaughtExceptionBecomesErrorReport()
        {
            var fixture = new Fixture();
            var exception = Thrown();

            fixture.Log.Error("fault while the game decided a prediction", exception);

            // Written to the game log, with its stack.
            Assert.Single(fixture.GameLog);
            Assert.StartsWith("[sir-correctifs-pulsar] ERROR fault while the game decided a prediction: System.InvalidOperationException", fixture.GameLog[0]);

            var reports = fixture.Reports.TakeAll(Start.AddSeconds(1));
            var report = Assert.Single(reports);
            Assert.Equal(ErrorReport.ErrorKind, report.Kind);
            Assert.Equal("erreur", report.Kind);
            Assert.Equal("Sir Correctifs Pulsar", report.Plugin);
            Assert.Equal("1.1.0.0", report.Version);
            Assert.Equal("fault while the game decided a prediction: System.InvalidOperationException: the hierarchy changed while it was read", report.Message);
            Assert.Contains(nameof(Thrown), report.Stack);
            Assert.Equal(1, report.Count);
            Assert.Equal(Start, report.First);
            Assert.Equal(Start, report.Last);
            Assert.Equal(Start, report.Session);
            Assert.Contains(report.Lines, l => l.StartsWith("[sir-correctifs-pulsar] ERROR fault while", StringComparison.Ordinal));
        }

        [Fact]
        public void LoggedErrorLineBecomesErrorReport()
        {
            var fixture = new Fixture();
            fixture.Log.Info("loaded, wheeled vehicle prediction on, mechanical blocks prediction on");
            fixture.Now = Start.AddSeconds(3);

            fixture.Log.Error("settings not saved: access to the path is denied");

            Assert.Equal("[sir-correctifs-pulsar] ERROR settings not saved: access to the path is denied", fixture.GameLog.Last());

            var report = Assert.Single(fixture.Reports.TakeAll(Start.AddSeconds(4)));
            Assert.Equal("erreur", report.Kind);
            Assert.Equal("settings not saved: access to the path is denied", report.Message);
            Assert.Equal("", report.Stack);
            Assert.Equal(Start.AddSeconds(3), report.First);
            Assert.Equal(new[]
            {
                "[sir-correctifs-pulsar] loaded, wheeled vehicle prediction on, mechanical blocks prediction on",
                "[sir-correctifs-pulsar] ERROR settings not saved: access to the path is denied",
            }, report.Lines);

            // An information line is no error: nothing more.
            fixture.Log.Info("hooked on MyCubeGrid.UpdatePredictionFlag");
            Assert.Empty(fixture.Reports.TakeAll(Start.AddMinutes(10)));
        }

        [Fact]
        public void ReportJsonHasAllRuleFields()
        {
            var report = new ErrorReport
            {
                Plugin = "Sir Correctifs Pulsar",
                Version = "1.1.0.0",
                Kind = ErrorReport.ErrorKind,
                Message = "a \"quoted\" message\twith a tab",
                Stack = "   at A.B()\n   at C.D()",
                First = Start,
                Last = Start.AddMinutes(2),
                Count = 4,
                Session = Start.AddHours(-1),
                Lines = new List<string> { "line one", "line \\ two" },
            };

            var bytes = report.ToBytes();
            // UTF-8 without a byte order mark.
            Assert.NotEqual(0xEF, bytes[0]);
            using (var document = JsonDocument.Parse(Encoding.UTF8.GetString(bytes)))
            {
                var root = document.RootElement;
                Assert.Equal(new[] { "greffon", "version", "genre", "message", "pile", "premiere", "derniere", "nombre", "session", "lignes" },
                    root.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.Equal("Sir Correctifs Pulsar", root.GetProperty("greffon").GetString());
                Assert.Equal("1.1.0.0", root.GetProperty("version").GetString());
                Assert.Equal("erreur", root.GetProperty("genre").GetString());
                Assert.Equal("a \"quoted\" message\twith a tab", root.GetProperty("message").GetString());
                Assert.Equal("   at A.B()\n   at C.D()", root.GetProperty("pile").GetString());
                Assert.Equal("2026-10-28T09:00:00.000Z", root.GetProperty("premiere").GetString());
                Assert.Equal("2026-10-28T09:02:00.000Z", root.GetProperty("derniere").GetString());
                Assert.Equal(4, root.GetProperty("nombre").GetInt32());
                Assert.Equal("2026-10-28T08:00:00.000Z", root.GetProperty("session").GetString());
                Assert.Equal(JsonValueKind.Array, root.GetProperty("lignes").ValueKind);
                Assert.Equal(new[] { "line one", "line \\ two" }, root.GetProperty("lignes").EnumerateArray().Select(e => e.GetString()).ToArray());
            }

            report.Kind = ErrorReport.CrashKind;
            using (var document = JsonDocument.Parse(report.ToJson()))
                Assert.Equal("plantage", document.RootElement.GetProperty("genre").GetString());
        }

        [Fact]
        public void SameErrorIsSentOncePerSessionWithCount()
        {
            var fixture = new Fixture();
            for (var i = 0; i < 3; i++)
            {
                fixture.Now = Start.AddSeconds(i * 5);
                fixture.Log.Error("prediction of a grid not recomputed", new NullReferenceException("Object reference not set to an instance of an object."));
            }
            // Another message: another report.
            fixture.Log.Error("settings not saved: disk full");

            // Still repeating: nothing is ready yet.
            Assert.Empty(fixture.Reports.TakeDue(Start.AddSeconds(15)));

            // Quiet for 30 seconds: one report per error, with its count and times.
            var reports = fixture.Reports.TakeDue(Start.AddSeconds(45));
            Assert.Equal(2, reports.Count);
            var repeated = reports.Single(r => r.Message.StartsWith("prediction of a grid", StringComparison.Ordinal));
            Assert.Equal(3, repeated.Count);
            Assert.Equal(Start, repeated.First);
            Assert.Equal(Start.AddSeconds(10), repeated.Last);
            Assert.Equal(1, reports.Single(r => r.Message.StartsWith("settings", StringComparison.Ordinal)).Count);

            // Seen again in the same session: never sent twice.
            fixture.Now = Start.AddMinutes(2);
            fixture.Log.Error("prediction of a grid not recomputed", new NullReferenceException("Object reference not set to an instance of an object."));
            Assert.Empty(fixture.Reports.TakeDue(Start.AddMinutes(20)));
            Assert.Empty(fixture.Reports.TakeAll(Start.AddMinutes(20)));
        }

        [Fact]
        public void AnErrorThatKeepsRepeatingLeavesAfterFiveMinutesAtMost()
        {
            var fixture = new Fixture();
            for (var i = 0; i <= 300; i += 10)
            {
                fixture.Now = Start.AddSeconds(i);
                fixture.Log.Error("render hook failed");
                if (i < 300)
                    Assert.Empty(fixture.Reports.TakeDue(fixture.Now));
            }
            var report = Assert.Single(fixture.Reports.TakeDue(Start.AddSeconds(300)));
            Assert.Equal(31, report.Count);
        }

        [Fact]
        public void TheSameMessageWithAnotherTopOfStackIsAnotherError()
        {
            var reports = new ErrorReports("Sir Correctifs Pulsar", "1.1.0.0", Start, new Sanitizer());
            reports.Record("fault", "   at A.First()\n   at Z.Main()", null, Start);
            reports.Record("fault", "   at A.Second()\n   at Z.Main()", null, Start);
            reports.Record("fault", "   at A.First()\n   at Y.Other()", null, Start);
            var taken = reports.TakeAll(Start);
            Assert.Equal(2, taken.Count);
            Assert.Equal(2, taken[0].Count);
        }
    }
}
