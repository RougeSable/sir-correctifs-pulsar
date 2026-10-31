using System;
using System.IO;
using HarmonyLib;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;

namespace SirCorrectifsPulsar
{
    // Sir Correctifs Pulsar, a client-side plugin loaded by Pulsar. It turns
    // on, on the player's machine only, two predictions the game already has
    // but keeps off: wheeled vehicles, and ships that carry rotors, pistons or
    // hinges. A player without the plugin plays as usual. Its only exchange
    // with the server is its error reports, on our servers only (Reporting).
    public class SirCorrectifsPulsarPlugin : IPlugin
    {
        public const string Id = "sir-correctifs-pulsar";

        // How many updates between two checks that no other plugin hooked the
        // game's prediction choice after us (60 per second).
        private const int CoexistenceInterval = 600;

        private static readonly PluginLog s_log = new PluginLog(Id, text => MyLog.Default.WriteLine(text));

        private Harmony m_harmony;
        private Reporting m_reporting;
        private GameNames m_names;
        private SessionStop m_stop;
        private GameSwitch m_carPrediction;
        private GameSwitch m_clientConstraints;
        private string m_settingsDirectory;
        private volatile Settings m_settings = new Settings();
        private bool m_patched;
        private int m_counter;

        public static SirCorrectifsPulsarPlugin Instance { get; private set; }

        public SessionStop Stop
        {
            get { return m_stop; }
        }

        public Settings Settings
        {
            get { return m_settings.Copy(); }
        }

        public static void Log(string text)
        {
            s_log.Info(text);
        }

        // An error line in the game log, which also becomes an error report.
        public static void LogError(string text)
        {
            s_log.Error(text);
        }

        // A caught exception, written with its stack and reported.
        public static void LogError(string context, Exception exception)
        {
            s_log.Error(context, exception);
        }

        public static string Version
        {
            get { return typeof(SirCorrectifsPulsarPlugin).Assembly.GetName().Version.ToString(); }
        }

        public void Init(object gameInstance)
        {
            Instance = this;

            // The player's settings folder: %AppData%\SpaceEngineers\Storage\sir-correctifs-pulsar
            m_settingsDirectory = Path.Combine(MyFileSystem.UserDataPath, "Storage", Id);

            // First, so that whatever fails next is reported.
            m_reporting = new Reporting(s_log, m_settingsDirectory, Version);
            m_reporting.Start();

            m_stop = new SessionStop(LogError, LogError);
            PredictionPatch.Stop = m_stop;

            string problem;
            m_settings = SettingsFile.Load(m_settingsDirectory, out problem);
            if (problem != null)
                LogError(problem + "; using the defaults");

            Log("loaded, wheeled vehicle prediction " + OnOff(m_settings.WheeledVehiclePrediction)
                + ", mechanical blocks prediction " + OnOff(m_settings.MechanicalBlocksPrediction));

            try
            {
                Prepare();
            }
            catch (Exception e)
            {
                m_stop.Stop("preparation failed", e, Texts.StopPatch);
            }
        }

        private static string OnOff(bool value)
        {
            return value ? "on" : "off";
        }

        private void Prepare()
        {
            string missing;
            m_names = GameNames.Resolve(out missing);
            if (m_names == null)
            {
                m_stop.MissingGameName(missing);
                return;
            }
            PredictionPatch.Names = m_names;

            var car = m_names.CarPrediction;
            var constraints = m_names.ClientConstraints;
            m_carPrediction = new GameSwitch(() => (bool)car.GetValue(null), v => car.SetValue(null, v));
            m_clientConstraints = new GameSwitch(() => (bool)constraints.GetValue(null), v => constraints.SetValue(null, v));

            var calls = PredictionPatch.CountHasChildrenCalls(m_names.UpdatePredictionFlag, m_names.HasChildren);
            if (calls != 1)
            {
                m_stop.Stop("unexpected game: MyCubeGrid.UpdatePredictionFlag asks HasChildren "
                    + (calls.HasValue ? calls.Value + " times" : "an unknown number of times") + ", expected once",
                    Texts.StopUnexpectedGame);
                return;
            }

            // Two plugins never fight over the same step of the game.
            if (StepAsideIfTaken())
                return;

            m_harmony = new Harmony(Id);
            var patch = typeof(PredictionPatch);
            m_harmony.Patch(m_names.UpdatePredictionFlag,
                postfix: new HarmonyMethod(patch.GetMethod("Postfix")),
                transpiler: new HarmonyMethod(patch.GetMethod("Transpiler")));
            m_patched = true;
            Log("hooked on MyCubeGrid.UpdatePredictionFlag");
        }

        // True if another plugin is hooked on the game's prediction choice:
        // the plugin then stops for the session, and our patch, if placed,
        // stays inert (both settings off, it answers what the game answers).
        private bool StepAsideIfTaken()
        {
            var info = Harmony.GetPatchInfo(m_names.UpdatePredictionFlag);
            var others = Coexistence.OtherOwners(info == null ? null : info.Owners, Id);
            if (others.Count == 0)
                return false;

            var names = string.Join(", ", others.ToArray());
            m_stop.Stop("MyCubeGrid.UpdatePredictionFlag is already patched by " + names + ": stepping aside",
                string.Format(Texts.StopCoexistence, names));
            return true;
        }

        public void Update()
        {
            // Apart, so that a fault of the reports never touches the rest.
            if (m_reporting != null)
                m_reporting.Update();

            try
            {
                var gameOpen = MySession.Static != null && MyAPIGateway.Utilities != null;
                m_stop.Deliver(gameOpen, Notify);

                if (m_patched && !m_stop.IsStopped && ++m_counter >= CoexistenceInterval)
                {
                    m_counter = 0;
                    StepAsideIfTaken();
                }

                ApplyToTheGame();
            }
            catch (Exception e)
            {
                // A failure here must never bring the game down.
                PredictionPatch.Set(false, false);
                m_stop.Stop("fault on the main thread", e, Texts.StopFault);
            }
        }

        // Every update, on the main thread: what the plugin does follows the
        // settings, the kind of game and the stops. A change recomputes the
        // prediction of every grid with the game's own method.
        private void ApplyToTheGame()
        {
            if (m_carPrediction == null || m_clientConstraints == null)
                return;

            var joinedAsClient = MySession.Static != null && Sync.MultiplayerActive && !Sync.IsServer;
            var settings = m_settings;
            var stopped = m_stop.IsStopped || !m_patched;
            var wheels = GameSwitch.Wanted(settings.WheeledVehiclePrediction, joinedAsClient, stopped);
            var mechanical = GameSwitch.Wanted(settings.MechanicalBlocksPrediction, joinedAsClient, stopped);

            var changed = m_carPrediction.Update(wheels);
            if (m_clientConstraints.Update(mechanical))
            {
                changed = true;
                if (joinedAsClient)
                    PredictionPatch.ResyncConstraints(mechanical);
            }
            if (PredictionPatch.Wheels != wheels || PredictionPatch.Mechanical != mechanical)
            {
                PredictionPatch.Set(wheels, mechanical);
                changed = true;
            }

            if (changed && joinedAsClient)
            {
                Log("wheeled vehicle prediction " + OnOff(wheels) + ", mechanical blocks prediction " + OnOff(mechanical));
                PredictionPatch.RecomputeAll();
            }
        }

        private static void Notify(string message)
        {
            var red = message.StartsWith(Texts.StopPrefix, StringComparison.Ordinal);
            MyAPIGateway.Utilities.ShowNotification(message, red ? 10000 : 3000, red ? "Red" : "White");
        }

        // Taken into account at the next update, without restarting the game,
        // and saved for the next games.
        public void Apply(Settings settings)
        {
            m_settings = settings.Copy();
            try
            {
                SettingsFile.Save(m_settingsDirectory, m_settings);
            }
            catch (Exception e)
            {
                LogError("settings not saved", e);
            }
        }

        // Called by Pulsar, the plugin's settings button.
        public void OpenConfigDialog()
        {
            MyGuiSandbox.AddScreen(new SettingsScreen(this));
        }

        public void Dispose()
        {
            try
            {
                PredictionPatch.Set(false, false);
                if (m_carPrediction != null)
                    m_carPrediction.Update(false);
                if (m_clientConstraints != null)
                    m_clientConstraints.Update(false);

                // Only our patches, never anyone else's.
                if (m_harmony != null)
                    m_harmony.UnpatchAll(Id);
            }
            catch (Exception e)
            {
                LogError("incomplete shutdown", e);
            }

            // Last: the clean exit erases the session mark.
            if (m_reporting != null)
                m_reporting.Stop();
            Instance = null;
        }
    }
}
