using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class TextsTests
    {
        [Fact]
        public void TheSettingsCarryTheNamesThePlayerIsToldAbout()
        {
            Assert.Equal("Wheeled vehicle prediction", Texts.WheeledVehiclePrediction);
            Assert.Equal("Mechanical blocks prediction", Texts.MechanicalBlocksPrediction);
        }

        [Fact]
        public void EveryStopMessageStartsWithThePluginName()
        {
            foreach (var message in new[] { Texts.StopUnexpectedGame, Texts.StopFault, Texts.StopPatch, Texts.StopCoexistence })
                Assert.StartsWith(Texts.DisplayName, message);
        }
    }
}
