using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class CrashReportTests
    {
        private const string Id = "sir-correctifs-pulsar";

        private static string CopyGameLog(TemporaryFolder folder, string name)
        {
            var path = Path.Combine(folder.Path, name);
            File.Copy(TestFiles.GameLog, path);
            File.SetLastWriteTimeUtc(path, TestFiles.Crash);
            return path;
        }

        [Fact]
        public void LeftoverMarkerProducesCrashReportFromPreviousLog()
        {
            using (var appData = new TemporaryFolder())
            {
                var pluginFolder = Path.Combine(appData.Path, "Storage", Id);
                var previousLog = CopyGameLog(appData, TestFiles.GameLogName);

                // The crashed session wrote its mark and never erased it.
                Assert.True(SessionMarker.Begin(pluginFolder, TestFiles.Session, previousLog));

                // The next start, with its own new log.
                var currentLog = Path.Combine(appData.Path, "SpaceEngineers_20261027_213000000.log");
                File.WriteAllText(currentLog, "2026-10-27 21:30:00.000 - Thread:   1 ->  Log Started\n");

                var report = CrashReport.FromLeftoverMarker(pluginFolder, appData.Path, currentLog, Id,
                    Texts.DisplayName, "1.1.0.0", new Sanitizer());

                Assert.NotNull(report);
                Assert.Equal("plantage", report.Kind);
                Assert.Equal("Sir Correctifs Pulsar", report.Plugin);
                Assert.Equal("1.1.0.0", report.Version);
                Assert.Equal(TestFiles.Session, report.Session);
                Assert.Equal(TestFiles.Crash, report.First);
                Assert.Equal(TestFiles.Crash, report.Last);
                Assert.Equal(1, report.Count);

                // The exception that brought the game down, and its stack.
                Assert.StartsWith("SharpDX.SharpDXException: HRESULT: [0x887A0005]", report.Message);
                Assert.Contains("DXGI_ERROR_DEVICE_REMOVED", report.Message);
                var stack = report.Stack.Split('\n');
                Assert.Equal("   at SharpDX.Result.CheckError()", stack[0]);
                Assert.Equal("   at VRage.Render11.Common.MyRenderThread.RenderFrame()", stack.Last());

                // The game's CRASH INFO block, first.
                var lines = report.Lines;
                Assert.Equal(CrashReport.StartMark, lines[0]);
                Assert.Contains("IsGPU: True", lines);
                Assert.Contains("PCUCount: 18240", lines);
                var blockEnd = lines.IndexOf(CrashReport.EndMark);
                Assert.True(blockEnd > 0);

                // Then the game's last 20 error lines before the crash.
                var errorsStart = lines.IndexOf(CrashReport.GameErrorsHeader);
                var pluginStart = lines.IndexOf(CrashReport.PluginHeader(Id));
                Assert.Equal(blockEnd + 1, errorsStart);
                var gameErrors = lines.Skip(errorsStart + 1).Take(pluginStart - errorsStart - 1).ToList();
                Assert.Equal(CrashReport.LastLines, gameErrors.Count);
                Assert.EndsWith("Exception occurred: SharpDX.SharpDXException: HRESULT: [0x887A0005], Module: [SharpDX.DXGI], ApiCode: [DXGI_ERROR_DEVICE_REMOVED/DeviceRemoved], Message: The GPU device instance has been suspended.", gameErrors.Last());
                Assert.Contains(gameErrors, l => l.EndsWith("Error: render frame took 15012 ms", StringComparison.Ordinal));
                Assert.DoesNotContain(gameErrors, l => l.Contains("[" + Id + "]"));
                // The oldest error lines are beyond the last 20.
                Assert.DoesNotContain(gameErrors, l => l.Contains("checksum"));

                // Then the plugin's last lines.
                var pluginLines = lines.Skip(pluginStart + 1).ToList();
                Assert.InRange(pluginLines.Count, 1, CrashReport.LastLines);
                Assert.All(pluginLines, l => Assert.Contains("[" + Id + "]", l));
                Assert.Contains(pluginLines, l => l.EndsWith("hooked on MyCubeGrid.UpdatePredictionFlag", StringComparison.Ordinal));
                Assert.Contains(pluginLines, l => l.EndsWith("ERROR constraints of a grid not updated: Object reference not set to an instance of an object.", StringComparison.Ordinal));
            }
        }

        [Fact]
        public void CleanExitLeavesNoCrashReport()
        {
            using (var appData = new TemporaryFolder())
            {
                var pluginFolder = Path.Combine(appData.Path, "Storage", Id);
                var previousLog = CopyGameLog(appData, TestFiles.GameLogName);

                // A session that ends cleanly erases its mark.
                Assert.True(SessionMarker.Begin(pluginFolder, TestFiles.Session, previousLog));
                Assert.True(File.Exists(SessionMarker.PathIn(pluginFolder)));
                Assert.True(SessionMarker.End(pluginFolder));
                Assert.False(File.Exists(SessionMarker.PathIn(pluginFolder)));

                // The next start finds no mark: no crash report, even with a
                // crash in an older log.
                var currentLog = Path.Combine(appData.Path, "SpaceEngineers_20261027_213000000.log");
                File.WriteAllText(currentLog, "");
                Assert.Null(CrashReport.FromLeftoverMarker(pluginFolder, appData.Path, currentLog, Id,
                    Texts.DisplayName, "1.1.0.0", new Sanitizer()));

                var queue = new ReportQueue(pluginFolder);
                Assert.Equal(0, queue.Count);
            }
        }

        [Fact]
        public void WithoutTheMarkedLogTheMostRecentOtherLogIsRead()
        {
            using (var appData = new TemporaryFolder())
            {
                var older = CopyGameLog(appData, "SpaceEngineers_20261020_100000000.log");
                File.SetLastWriteTimeUtc(older, TestFiles.Crash.AddDays(-7));
                var previous = CopyGameLog(appData, TestFiles.GameLogName);
                var current = Path.Combine(appData.Path, "SpaceEngineers_20261027_213000000.log");
                File.WriteAllText(current, "");
                File.SetLastWriteTimeUtc(current, TestFiles.Crash.AddMinutes(15));

                Assert.Equal(previous, CrashReport.PreviousLog(null, appData.Path, current));
                Assert.Equal(previous, CrashReport.PreviousLog(Path.Combine(appData.Path, "gone.log"), appData.Path, current));
            }
        }

        [Fact]
        public void ALogWithoutCrashInfoStillGivesACrashReport()
        {
            var lines = new[]
            {
                "2026-10-27 21:15:02.123 - Thread:   1 ->  Log Started",
                "2026-10-27 21:15:03.402 - Thread:   1 ->  [sir-correctifs-pulsar] hooked on MyCubeGrid.UpdatePredictionFlag",
                "2026-10-27 21:17:20.002 - Thread:   1 ->  Error: GPU timestamp query timed out",
            };
            var report = CrashReport.FromLog(lines, TestFiles.Session, TestFiles.Crash, Id, Texts.DisplayName, "1.1.0.0", new Sanitizer());

            Assert.Equal("plantage", report.Kind);
            Assert.Equal(CrashReport.NoException, report.Message);
            Assert.Equal(CrashReport.NoCrashInfo, report.Lines[0]);
            Assert.Contains("2026-10-27 21:17:20.002 - Thread:   1 ->  Error: GPU timestamp query timed out", report.Lines);
        }
    }
}
