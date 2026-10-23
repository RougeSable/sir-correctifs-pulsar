using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class AttachedGridsTests
    {
        private static readonly Attachment[] Turret = { Attachment.Mechanical };
        private static readonly Attachment[] PistonAndHinge = { Attachment.Mechanical, Attachment.Mechanical };
        private static readonly Attachment[] Rover = { Attachment.Wheel, Attachment.Wheel, Attachment.Wheel, Attachment.Wheel };
        private static readonly Attachment[] RoverWithTurret = { Attachment.Wheel, Attachment.Wheel, Attachment.Mechanical };
        private static readonly Attachment[] TurretAndDockedShip = { Attachment.Mechanical, Attachment.Other };

        [Fact]
        public void BothSettingsOffNothingIsLifted()
        {
            Assert.False(AttachedGrids.LiftsTheRule(Turret, false, false));
            Assert.False(AttachedGrids.LiftsTheRule(RoverWithTurret, false, false));
        }

        [Fact]
        public void Setting2LiftsTheRuleForAShipWithARotorAPistonOrAHinge()
        {
            Assert.True(AttachedGrids.LiftsTheRule(Turret, false, true));
            Assert.True(AttachedGrids.LiftsTheRule(PistonAndHinge, false, true));
            Assert.True(AttachedGrids.LiftsTheRule(Turret, true, true));
        }

        [Fact]
        public void Setting1AloneNeverLiftsTheRuleForMechanicalBlocks()
        {
            Assert.False(AttachedGrids.LiftsTheRule(Turret, true, false));
            Assert.False(AttachedGrids.LiftsTheRule(RoverWithTurret, true, false));
        }

        [Fact]
        public void APureRoverIsLeftToTheGamesOwnWheelPrediction()
        {
            Assert.False(AttachedGrids.LiftsTheRule(Rover, true, true));
            Assert.False(AttachedGrids.LiftsTheRule(Rover, false, true));
        }

        [Fact]
        public void ARoverWithATurretNeedsBothSettings()
        {
            Assert.True(AttachedGrids.LiftsTheRule(RoverWithTurret, true, true));
            Assert.False(AttachedGrids.LiftsTheRule(RoverWithTurret, false, true));
        }

        [Fact]
        public void ADockedShipOrAnUnknownAttachmentKeepsTheGamesRefusal()
        {
            Assert.False(AttachedGrids.LiftsTheRule(TurretAndDockedShip, true, true));
            Assert.False(AttachedGrids.LiftsTheRule(new[] { Attachment.Other }, true, true));
        }

        [Fact]
        public void NothingAttachedIsLeftToTheGame()
        {
            Assert.False(AttachedGrids.LiftsTheRule(new Attachment[0], true, true));
            Assert.False(AttachedGrids.LiftsTheRule(null, true, true));
        }
    }
}
