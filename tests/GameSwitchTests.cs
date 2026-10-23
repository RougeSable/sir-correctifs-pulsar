using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class GameSwitchTests
    {
        private bool m_field;
        private int m_writes;

        private GameSwitch NewSwitch()
        {
            return new GameSwitch(() => m_field, v =>
            {
                m_field = v;
                m_writes++;
            });
        }

        [Fact]
        public void TurnedOnThenOffTheGameGetsItsValueBack()
        {
            var s = NewSwitch();

            Assert.True(s.Update(true));
            Assert.True(m_field);
            Assert.True(s.IsApplied);

            Assert.True(s.Update(false));
            Assert.False(m_field);
            Assert.False(s.IsApplied);
            Assert.Equal(2, m_writes);
        }

        [Fact]
        public void NothingIsWrittenWhileTheWishDoesNotChange()
        {
            var s = NewSwitch();

            Assert.False(s.Update(false));
            Assert.True(s.Update(true));
            Assert.False(s.Update(true));
            Assert.False(s.Update(true));
            Assert.Equal(1, m_writes);
        }

        [Fact]
        public void ASwitchTheGameAlreadyHadOnStaysOn()
        {
            m_field = true;
            var s = NewSwitch();

            Assert.False(s.Update(true));
            Assert.False(s.Update(false));
            Assert.True(m_field);
            Assert.Equal(0, m_writes);
        }

        [Fact]
        public void TheGameIsNeverTouchedWithTheSettingOff()
        {
            var s = NewSwitch();
            for (var i = 0; i < 10; i++)
                Assert.False(s.Update(GameSwitch.Wanted(false, true, false)));
            Assert.Equal(0, m_writes);
            Assert.False(m_field);
        }

        [Theory]
        [InlineData(true, true, false, true)]
        [InlineData(false, true, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, true, true, false)]
        public void ThePluginOnlyActsAsAClientAndNeverAfterAStop(bool setting, bool joinedAsClient, bool stopped, bool expected)
        {
            Assert.Equal(expected, GameSwitch.Wanted(setting, joinedAsClient, stopped));
        }

        [Fact]
        public void AStopGivesTheGameItsValueBack()
        {
            var s = NewSwitch();
            s.Update(GameSwitch.Wanted(true, true, false));
            Assert.True(m_field);

            s.Update(GameSwitch.Wanted(true, true, true));
            Assert.False(m_field);
        }
    }
}
