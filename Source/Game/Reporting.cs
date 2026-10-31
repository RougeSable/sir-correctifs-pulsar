using System;
using System.Collections.Generic;
using System.Reflection;
using Sandbox.Definitions;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.FileSystem;
using VRage.GameServices;
using VRage.Utils;

namespace SirCorrectifsPulsar
{
    // The plugin's error reports, in the game: the session mark, the crash
    // report of a session that did not end cleanly, the errors of this
    // session, and the only exchange of the plugin with the server, the
    // reports sent on our servers (PU and PTU) by
    // MyAPIGateway.Multiplayer.SendMessageToServer.
    //
    // Every step is wrapped: a fault of the reports is swallowed and never
    // reaches the game, nor the rest of the plugin.
    internal sealed class Reporting
    {
        // Updates between two looks at the reports (60 per second).
        private const int Interval = 60;

        // Reports sent at most per look, so that a long queue leaves slowly.
        private const int PerLook = 5;

        private readonly PluginLog m_log;
        private readonly string m_directory;
        private readonly string m_version;
        private readonly OurServer m_server = new OurServer();
        private ReportQueue m_queue;
        private ErrorReports m_reports;
        private Sanitizer m_sanitizer;
        private object m_game;
        private int m_counter;

        public Reporting(PluginLog log, string pluginDirectory, string version)
        {
            m_log = log;
            m_directory = pluginDirectory;
            m_version = version ?? "";
        }

        // At the plugin's start, before anything else can fail.
        public void Start()
        {
            try
            {
                var session = DateTime.UtcNow;
                m_sanitizer = new Sanitizer(UserFolder(), KnownValues());
                m_reports = new ErrorReports(Texts.DisplayName, m_version, session, m_sanitizer);
                m_log.Reports = m_reports;
                m_queue = new ReportQueue(m_directory);

                var currentLog = CurrentLog();
                var crash = CrashReport.FromLeftoverMarker(m_directory, MyFileSystem.UserDataPath, currentLog,
                    SirCorrectifsPulsarPlugin.Id, Texts.DisplayName, m_version, m_sanitizer);
                SessionMarker.Begin(m_directory, session, currentLog);
                if (crash != null && m_queue.Enqueue(crash))
                    m_log.Info("the previous game session did not end cleanly: a crash report waits for one of our servers");
            }
            catch (Exception)
            {
                // Without reports, the plugin and the game go on.
            }
        }

        // Every update, on the game's main thread.
        public void Update()
        {
            try
            {
                var session = MySession.Static;
                var joinedAsClient = session != null && Sync.MultiplayerActive && !Sync.IsServer
                    && IsDedicatedServer(MyMultiplayer.Static);
                m_server.Update(session, joinedAsClient);
                if (!ReferenceEquals(session, m_game))
                {
                    m_game = session;
                    if (joinedAsClient)
                        LearnTheGame(session);
                }

                // Once the game is loaded, its definitions say whether it is
                // one of our servers.
                if (m_server.NeedsALook && session.Ready && m_server.Recognize(DefinitionNames()))
                {
                    if (m_queue != null)
                        m_queue.NewConnection();
                    m_counter = Interval;
                }

                if (++m_counter < Interval)
                    return;
                m_counter = 0;

                if (m_reports != null && m_queue != null)
                {
                    foreach (var report in m_reports.TakeDue(DateTime.UtcNow))
                        m_queue.Enqueue(report);
                    if (m_server.IsOurs)
                        m_queue.Pump(true, Send, PerLook);
                }
            }
            catch (Exception)
            {
                // Swallowed: the reports never break the game.
            }
        }

        // The clean exit: waiting errors go to the queue, the mark is erased.
        public void Stop()
        {
            try
            {
                if (m_reports != null && m_queue != null)
                {
                    foreach (var report in m_reports.TakeAll(DateTime.UtcNow))
                        m_queue.Enqueue(report);
                }
            }
            catch (Exception)
            {
            }

            SessionMarker.End(m_directory);
        }

        private static void Send(byte[] report)
        {
            var multiplayer = MyAPIGateway.Multiplayer;
            if (multiplayer == null || !multiplayer.SendMessageToServer((ushort)OurServer.Channel, report, true))
                throw new InvalidOperationException("report not sent");
        }

        // The names of the current game's definitions, the studio's mods
        // included.
        private static IEnumerable<string> DefinitionNames()
        {
            var names = new List<string>();
            var definitions = MyDefinitionManager.Static;
            if (definitions == null)
                return names;
            foreach (var definition in definitions.GetAllDefinitions())
            {
                if (definition != null)
                    names.Add(definition.Id.SubtypeName);
            }
            return names;
        }

        // The client of a dedicated server keeps the server item it joined in
        // an internal class: found by its type, never by a name that could
        // change (as Sir Holomap does). A game joined through a lobby has none.
        private static bool IsDedicatedServer(object game)
        {
            if (game == null)
                return false;
            const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var property in game.GetType().GetProperties(Flags))
            {
                if (property.PropertyType == typeof(MyGameServerItem) && property.GetIndexParameters().Length == 0
                    && property.GetValue(game, null) != null)
                    return true;
            }
            foreach (var field in game.GetType().GetFields(Flags))
            {
                if (field.FieldType == typeof(MyGameServerItem) && field.GetValue(game) != null)
                    return true;
            }
            return false;
        }

        // What names the player in this game: never in a report.
        private void LearnTheGame(MySession session)
        {
            try
            {
                if (m_sanitizer == null)
                    return;
                m_sanitizer.Learn(session.Name);
                var player = session.LocalHumanPlayer;
                if (player != null)
                    m_sanitizer.Learn(player.DisplayName);
                var game = MyMultiplayer.Static;
                if (game != null)
                    m_sanitizer.Learn(game.HostName);
            }
            catch (Exception)
            {
            }
        }

        private static string[] KnownValues()
        {
            var values = new string[3];
            try
            {
                values[0] = MyGameService.UserName;
            }
            catch (Exception)
            {
            }
            try
            {
                var id = MyGameService.UserId;
                if (id != 0)
                    values[1] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
            }
            try
            {
                values[2] = Environment.UserName;
            }
            catch (Exception)
            {
            }
            return values;
        }

        private static string UserFolder()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string CurrentLog()
        {
            try
            {
                return MyLog.Default == null ? null : MyLog.Default.GetFilePath();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
