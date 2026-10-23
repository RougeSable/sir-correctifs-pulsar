using System.Xml.Serialization;

namespace SirCorrectifsPulsar
{
    // The player's settings, saved in Storage\sir-correctifs-pulsar. No
    // dependency on the game: the class is tested as is.
    //
    // Two independent switches, both on from installation. Each one turns on
    // a prediction the game already knows but keeps off; off, the game is
    // left exactly as it is.
    [XmlRoot("Settings")]
    public class Settings
    {
        // Setting 1: the game's prediction of wheeled vehicles.
        public bool WheeledVehiclePrediction { get; set; } = true;

        // Setting 2: client physics constraints, and prediction of a piloted
        // grid that carries grids attached by a rotor, a piston or a hinge.
        public bool MechanicalBlocksPrediction { get; set; } = true;

        public bool AnyOn
        {
            get { return WheeledVehiclePrediction || MechanicalBlocksPrediction; }
        }

        public Settings Copy()
        {
            return new Settings
            {
                WheeledVehiclePrediction = WheeledVehiclePrediction,
                MechanicalBlocksPrediction = MechanicalBlocksPrediction,
            };
        }

        public bool SameAs(Settings other)
        {
            return other != null
                && other.WheeledVehiclePrediction == WheeledVehiclePrediction
                && other.MechanicalBlocksPrediction == MechanicalBlocksPrediction;
        }
    }
}
