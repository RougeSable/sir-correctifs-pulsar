using System;
using System.Reflection;
using HarmonyLib;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;

namespace SirCorrectifsPulsar
{
    // Every internal name of the game the plugin relies on, resolved by
    // reflection at startup. One missing name and the plugin stops for the
    // session: the game keeps its own behavior.
    internal sealed class GameNames
    {
        // The game's switches, off by default, shown only in its debug screens.
        // "Prediction" in the wheels screen:
        public const string FakesType = "Sandbox.Engine.Utils.MyFakes";
        public const string CarPredictionField = "MULTIPLAYER_CLIENT_SIMULATE_CONTROLLED_CAR";
        // "Client physics constraints" in the network screen:
        public const string ClientConstraintsField = "MULTIPLAYER_CLIENT_CONSTRAINTS";

        public FieldInfo CarPrediction { get; private set; }
        public FieldInfo ClientConstraints { get; private set; }

        // Where the game decides whether a grid is predicted, and the question
        // it asks there: does the piloted grid carry any other grid?
        public MethodInfo UpdatePredictionFlag { get; private set; }
        public MethodInfo HasChildren { get; private set; }

        // Private setters: the snapshot profiles the game uses for a predicted
        // wheel and a predicted car.
        public Action<MyCubeGrid, bool> SetPredictedWheel { get; private set; }
        public Action<MyCubeGrid, bool> SetPredictedCar { get; private set; }

        // The constraints of a physics body (rotors, pistons, hinges, wheels).
        public PropertyInfo Constraints { get; private set; }

        public static GameNames Resolve(out string missing)
        {
            var names = new GameNames();

            var fakes = AccessTools.TypeByName(FakesType);
            if (fakes == null)
            {
                missing = FakesType;
                return null;
            }

            names.CarPrediction = StaticBool(fakes, CarPredictionField);
            if (names.CarPrediction == null)
            {
                missing = FakesType + "." + CarPredictionField;
                return null;
            }

            names.ClientConstraints = StaticBool(fakes, ClientConstraintsField);
            if (names.ClientConstraints == null)
            {
                missing = FakesType + "." + ClientConstraintsField;
                return null;
            }

            names.UpdatePredictionFlag = AccessTools.Method(typeof(MyCubeGrid), "UpdatePredictionFlag", Type.EmptyTypes);
            if (names.UpdatePredictionFlag == null)
            {
                missing = "MyCubeGrid.UpdatePredictionFlag";
                return null;
            }

            names.HasChildren = AccessTools.Method(typeof(MyGridPhysicalHierarchy), "HasChildren", new[] { typeof(MyCubeGrid) });
            if (names.HasChildren == null || names.HasChildren.ReturnType != typeof(bool))
            {
                missing = "MyGridPhysicalHierarchy.HasChildren";
                return null;
            }

            names.SetPredictedWheel = Setter("IsClientPredictedWheel");
            if (names.SetPredictedWheel == null)
            {
                missing = "MyCubeGrid.IsClientPredictedWheel";
                return null;
            }

            names.SetPredictedCar = Setter("IsClientPredictedCar");
            if (names.SetPredictedCar == null)
            {
                missing = "MyCubeGrid.IsClientPredictedCar";
                return null;
            }

            names.Constraints = AccessTools.Property(typeof(MyPhysicsBody), "Constraints");
            if (names.Constraints == null)
            {
                missing = "MyPhysicsBody.Constraints";
                return null;
            }

            missing = null;
            return names;
        }

        private static FieldInfo StaticBool(Type type, string name)
        {
            var field = AccessTools.Field(type, name);
            if (field == null || !field.IsStatic || field.FieldType != typeof(bool) || field.IsInitOnly || field.IsLiteral)
                return null;
            return field;
        }

        private static Action<MyCubeGrid, bool> Setter(string property)
        {
            var setter = AccessTools.PropertySetter(typeof(MyCubeGrid), property);
            if (setter == null)
                return null;
            try
            {
                return (Action<MyCubeGrid, bool>)Delegate.CreateDelegate(typeof(Action<MyCubeGrid, bool>), setter);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
