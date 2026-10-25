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
    // another grid. Its own wheel prediction makes an exception for a pure
    // wheeled vehicle, but in a branch of its own that skips its safeguards
    // (too many predicted contacts, locked connector, anchored grid).
    //
    // The plugin lifts the rule instead, so that the game's main branch, with
    // all its safeguards, decides: wheels with setting 1, grids attached by
    // mechanical blocks with setting 2, and nothing else.
    public static class AttachedGrids
    {
        // True when the plugin lets the game treat the piloted grid as if it
        // carried nothing, so that the game's own checks decide (anchored to a
        // station, locked connector, too many predicted contacts).
        //
        // - at least one grid is attached;
        // - every attached grid, at any depth, is a wheel (setting 1) or
        //   attached by a mechanical block (setting 2): a docked ship keeps
        //   the game's refusal;
        // - each kind of attachment needs its own setting: a rover with a
        //   turret on a rotor needs both.
        public static bool LiftsTheRule(IEnumerable<Attachment> attachments, bool wheeledVehiclePrediction, bool mechanicalBlocksPrediction)
        {
            if ((!wheeledVehiclePrediction && !mechanicalBlocksPrediction) || attachments == null)
                return false;

            var any = false;
            foreach (var attachment in attachments)
            {
                switch (attachment)
                {
                    case Attachment.Mechanical:
                        if (!mechanicalBlocksPrediction)
                            return false;
                        break;
                    case Attachment.Wheel:
                        if (!wheeledVehiclePrediction)
                            return false;
                        break;
                    default:
                        return false;
                }
                any = true;
            }
            return any;
        }
    }
}
