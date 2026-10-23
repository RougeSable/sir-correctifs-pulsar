using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class CoexistenceTests
    {
        private const string Us = "sir-correctifs-pulsar";

        [Fact]
        public void NobodyElseNoStepAside()
        {
            Assert.Empty(Coexistence.OtherOwners(null, Us));
            Assert.Empty(Coexistence.OtherOwners(new string[0], Us));
            Assert.Empty(Coexistence.OtherOwners(new[] { Us, Us }, Us));
        }

        [Fact]
        public void AnotherOwnerIsNamedOnce()
        {
            var others = Coexistence.OtherOwners(new[] { Us, "other.plugin", "", null, "other.plugin" }, Us);
            Assert.Equal(new[] { "other.plugin" }, others);
        }
    }
}
