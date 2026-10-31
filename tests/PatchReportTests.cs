using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class PatchReportTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void MissingTargetMethodBecomesErrorReport()
        {
            var gameLog = new List<string>();
            var reports = new ErrorReports(Texts.DisplayName, "1.1.0.0", Start, new Sanitizer());
            var log = new PluginLog("sir-correctifs-pulsar", gameLog.Add, reports, () => Start);
            var stop = new SessionStop(log.Error, log.Error);

            // The method the plugin patches is not in this game.
            Assert.True(stop.MissingGameName("MyCubeGrid.UpdatePredictionFlag"));

            Assert.True(stop.IsStopped);
            Assert.Equal(Texts.StopUnexpectedGame, stop.Reason);
            Assert.Equal("[sir-correctifs-pulsar] ERROR unexpected game, not found: MyCubeGrid.UpdatePredictionFlag", gameLog.Single());

            var report = Assert.Single(reports.TakeAll(Start));
            Assert.Equal("erreur", report.Kind);
            Assert.Equal("Sir Correctifs Pulsar", report.Plugin);
            Assert.Equal("unexpected game, not found: MyCubeGrid.UpdatePredictionFlag", report.Message);
            Assert.Equal(1, report.Count);
        }

        [Fact]
        public void AFailedPatchBecomesErrorReportWithItsStack()
        {
            var reports = new ErrorReports(Texts.DisplayName, "1.1.0.0", Start, new Sanitizer());
            var log = new PluginLog("sir-correctifs-pulsar", null, reports, () => Start);
            var stop = new SessionStop(log.Error, log.Error);

            Exception failure;
            try
            {
                throw new MissingMethodException("Sandbox.Game.Entities.MyCubeGrid", "UpdatePredictionFlag");
            }
            catch (Exception e)
            {
                failure = e;
            }
            Assert.True(stop.Stop("preparation failed", failure, Texts.StopPatch));

            var report = Assert.Single(reports.TakeAll(Start));
            Assert.Equal("erreur", report.Kind);
            Assert.StartsWith("preparation failed: System.MissingMethodException:", report.Message);
            Assert.Contains(nameof(AFailedPatchBecomesErrorReportWithItsStack), report.Stack);
        }
    }
}
