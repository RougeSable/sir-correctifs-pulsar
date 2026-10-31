namespace SirCorrectifsPulsar
{
    // Everything the player reads, in one place.
    public static class Texts
    {
        public const string DisplayName = "Sir Correctifs Pulsar";

        public const string StopPrefix = "Sir Correctifs Pulsar is stopped for this session: ";
        public const string StopUnexpectedGame = StopPrefix + "the game has changed. Vehicles are driven as in the unmodified game.";
        public const string StopFault = StopPrefix + "a fault occurred while the game chose what to predict. Vehicles are driven as in the unmodified game.";
        public const string StopPatch = StopPrefix + "it could not hook into the game's vehicle prediction.";
        public const string StopCoexistence = StopPrefix + "another plugin already changes how the game predicts piloted grids ({0}). Sir Correctifs Pulsar steps aside.";

        // Settings screen, opened from Pulsar.
        public const string ScreenTitle = "Sir Correctifs Pulsar";

        public const string WheeledVehiclePrediction = "Wheeled vehicle prediction";
        public const string WheeledVehiclePredictionHelp =
            "The rover you drive and each of its wheels are simulated on your machine: it answers your keys at once, "
            + "without waiting for the server. Unchecked: the game's own behavior.";

        public const string MechanicalBlocksPrediction = "Mechanical blocks prediction";
        public const string MechanicalBlocksPredictionHelp =
            "A ship that carries a rotor, a piston or a hinge is simulated on your machine when you pilot it, "
            + "its attached parts with it. Unchecked: the game's own behavior.";

        public const string ScreenOnlyAsClient = "Applies in multiplayer games joined as a client. Only error reports go to Sirius servers.";
        public const string ScreenStopped = "Stopped for this session, see the game log.";
        public const string CloseButton = "Close";
    }
}
