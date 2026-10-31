using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SirCorrectifsPulsar.Tests
{
    // A folder of its own for a test, erased afterwards.
    public sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sir-correctifs-pulsar-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; private set; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception)
            {
            }
        }
    }

    public static class TestFiles
    {
        public const string GameLogName = "SpaceEngineers_20261027_211502123.log";

        // A game log in the shape the game writes it (VRage.Utils.MyLog and
        // Sandbox.MyInitializer): startup, Steam identity, joining a server,
        // players met, a named grid, error lines, then a GPU crash with its
        // exception and the CRASH INFO block.
        public static string GameLog
        {
            get { return Path.Combine(AppContext.BaseDirectory, "Data", GameLogName); }
        }

        // The player data the log carries, none of which may leave.
        public static readonly string[] PlayerData =
        {
            "76561198012345678", // the player's Steam id
            "76561198087654321", // another player's Steam id
            "144115188075855873", // an identity id
            "101532748263918544", // an entity id
            "RougeSable", // the player's name
            "Kestrel_77", // another player's name
            "Iron Heron", // a grid name
            "Sirius Immersion PTU", // the server's name
            "203.0.113.24", // the server's address
            "27016",
            "steam://",
            "rsable", // the Windows user name
        };

        // The game logs this machine really wrote: those of the Windows
        // account running the tests (%AppData%\SpaceEngineers), and the copy
        // the studio puts next to the repository ("journaux-du-jeu"). None
        // of them is part of the repository.
        public static List<string> RealGameLogs()
        {
            var folders = new List<string>();
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                    folders.Add(Path.Combine(appData, "SpaceEngineers"));
            }
            catch (Exception)
            {
            }
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                folders.Add(Path.Combine(dir.FullName, "journaux-du-jeu"));

            var logs = new List<string>();
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.GetFiles(folder, "SpaceEngineers_*.log"))
                {
                    if (!logs.Contains(file))
                        logs.Add(file);
                }
            }
            return logs;
        }

        // The player data a real game log carries, read from the game's own
        // lines: the Steam account and its name, the server joined and its
        // address, the Windows user name, and any Steam id.
        public static List<string> PlayerDataIn(IList<string> log)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var patterns = new[]
            {
                new Regex(@"Service\.UserId:\s*(?<v>\d+)"),
                new Regex(@"Service\.UserName:\s*(?<v>.+?)\s*$"),
                new Regex(@"Joining server:\s*Name:(?<v>.*?)\s*ConnectionString:"),
                new Regex(@"ConnectionString:(?:steam://)?(?<v>[^\s/]+)"),
                new Regex(@"ConnectionString:(?:steam://)?(?<v>[^\s:/]+)"),
                new Regex(@"[A-Za-z]:\\Users\\(?<v>[^\\/\r\n""'<>|:*?]+)", RegexOptions.IgnoreCase),
                new Regex(@"(?<!\d)(?<v>7656119\d{10})(?!\d)"),
            };
            foreach (var line in log)
            {
                foreach (var pattern in patterns)
                {
                    foreach (Match match in pattern.Matches(line ?? ""))
                    {
                        var value = match.Groups["v"].Value.Trim();
                        if (value.Length >= 3 && !string.Equals(value, "Public", StringComparison.OrdinalIgnoreCase))
                            found.Add(value);
                    }
                }
            }
            return found.ToList();
        }

        public static readonly DateTime Session = new DateTime(2026, 10, 27, 20, 15, 2, 123, DateTimeKind.Utc);
        public static readonly DateTime Crash = new DateTime(2026, 10, 27, 20, 17, 25, 720, DateTimeKind.Utc);
    }
}
