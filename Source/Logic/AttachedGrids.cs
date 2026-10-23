using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // How a grid is attached to the grid it hangs from, in the game's
    // physical hierarchy.
    public enum Attachment
    {
        // A wheel on a suspension.
        Wheel,

        // A rotor, an advanced rotor, a hinge or a piston.
        Mechanical,

        // Anything else: a docked ship (connector), a suspension without its
        // wheel, a block the plugin does not know.
        Other,
    }

    // The game refuses the prediction to a piloted grid as soon as it carries
    // another grid. It only lifts that rule for a pure wheeled vehicle, behind
    // its own wheel prediction setting. Setting 2 lifts it for grids attached
    // by mechanical blocks, and for nothing else.
    public static class AttachedGrids
    {
        // True when the plugin lets the game treat the piloted grid as if it
        // carried nothing, so that the game's own checks decide (anchored to a
        // station, locked connector, too many predicted contacts).
        //
        // - at least one grid is attached by a mechanical block;
        // - every attached grid is a wheel or attached by a mechanical block,
        //   at any depth: a docked ship keeps the game's refusal;
        // - wheels are only accepted with setting 1 on: setting 2 alone never
        //   predicts a wheeled vehicle.
        public static bool LiftsTheRule(IEnumerable<Attachment> attachments, bool wheeledVehiclePrediction, bool mechanicalBlocksPrediction)
        {
            if (!mechanicalBlocksPrediction || attachments == null)
                return false;

            var mechanical = false;
            foreach (var attachment in attachments)
            {
                switch (attachment)
                {
                    case Attachment.Mechanical:
                        mechanical = true;
                        break;
                    case Attachment.Wheel:
                        if (!wheeledVehiclePrediction)
                            return false;
                        break;
                    default:
                        return false;
                }
            }
            return mechanical;
        }
    }
}
