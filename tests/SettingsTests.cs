using System;
using System.IO;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class SettingsTests : IDisposable
    {
        private readonly string m_directory = Path.Combine(Path.GetTempPath(), "sir-correctifs-pulsar-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(m_directory))
                Directory.Delete(m_directory, true);
        }

        private void Write(string content)
        {
            Directory.CreateDirectory(m_directory);
            File.WriteAllText(SettingsFile.PathIn(m_directory), content);
        }

        [Fact]
        public void BothSettingsAreOnFromInstallation()
        {
            var s = new Settings();
            Assert.True(s.WheeledVehiclePrediction);
            Assert.True(s.MechanicalBlocksPrediction);
            Assert.True(s.AnyOn);
        }

        [Fact]
        public void ANewPlayerGetsBothSettingsOn()
        {
            string problem;
            var s = SettingsFile.Load(m_directory, out problem);

            Assert.Null(problem);
            Assert.True(s.WheeledVehiclePrediction);
            Assert.True(s.MechanicalBlocksPrediction);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(true, true)]
        public void EachSettingIsKeptSeparatelyFromOneGameToTheNext(bool wheels, bool mechanical)
        {
            SettingsFile.Save(m_directory, new Settings { WheeledVehiclePrediction = wheels, MechanicalBlocksPrediction = mechanical });

            string problem;
            var s = SettingsFile.Load(m_directory, out problem);

            Assert.Null(problem);
            Assert.Equal(wheels, s.WheeledVehiclePrediction);
            Assert.Equal(mechanical, s.MechanicalBlocksPrediction);
            Assert.Equal(wheels || mechanical, s.AnyOn);
        }

        [Fact]
        public void ASettingMissingFromTheFileKeepsItsDefault()
        {
            Write("<?xml version=\"1.0\"?><Settings><WheeledVehiclePrediction>false</WheeledVehiclePrediction></Settings>");

            string problem;
            var s = SettingsFile.Load(m_directory, out problem);

            Assert.Null(problem);
            Assert.False(s.WheeledVehiclePrediction);
            Assert.True(s.MechanicalBlocksPrediction);
        }

        [Fact]
        public void AnUnreadableFileGivesTheDefaultsAndSaysWhy()
        {
            Write("not a settings file");

            string problem;
            var s = SettingsFile.Load(m_directory, out problem);

            Assert.NotNull(problem);
            Assert.Contains(SettingsFile.FileName, problem);
            Assert.True(s.WheeledVehiclePrediction);
            Assert.True(s.MechanicalBlocksPrediction);
        }

        [Fact]
        public void SavingTwiceReplacesTheFileAndLeavesNoTemporaryFile()
        {
            SettingsFile.Save(m_directory, new Settings { WheeledVehiclePrediction = false });
            SettingsFile.Save(m_directory, new Settings { MechanicalBlocksPrediction = false });

            string problem;
            var s = SettingsFile.Load(m_directory, out problem);

            Assert.Null(problem);
            Assert.True(s.WheeledVehiclePrediction);
            Assert.False(s.MechanicalBlocksPrediction);
            Assert.Equal(new[] { SettingsFile.PathIn(m_directory) }, Directory.GetFiles(m_directory));
        }

        [Fact]
        public void ACopyIsIndependent()
        {
            var original = new Settings();
            var copy = original.Copy();
            copy.WheeledVehiclePrediction = false;

            Assert.True(original.WheeledVehiclePrediction);
            Assert.False(original.SameAs(copy));
            Assert.True(original.SameAs(original.Copy()));
        }
    }
}
