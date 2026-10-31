using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class SanitizerTests
    {
        [Fact]
        public void RealGameLogLeavesNoPlayerData()
        {
            var log = File.ReadAllLines(TestFiles.GameLog);

            // The log does carry the player's data...
            foreach (var data in TestFiles.PlayerData)
                Assert.Contains(log, l => l.Contains(data));

            // ...but neither the crash report built from it...
            var sanitizer = new Sanitizer();
            var crash = CrashReport.FromLog(log, TestFiles.Session, TestFiles.Crash, "sir-correctifs-pulsar",
                Texts.DisplayName, "1.1.0.0", sanitizer);
            var crashJson = crash.ToJson();

            // ...nor an error report of the session whose lines and message
            // come from it.
            var reports = new ErrorReports(Texts.DisplayName, "1.1.0.0", TestFiles.Session, sanitizer);
            reports.Record(string.Join("\n", log.Take(60)), string.Join("\n", log.Skip(60)), log.ToList(), TestFiles.Session);
            var errorJson = reports.TakeAll(TestFiles.Crash).Single().ToJson();

            foreach (var json in new[] { crashJson, errorJson })
            {
                foreach (var data in TestFiles.PlayerData)
                    Assert.DoesNotContain(data, json, StringComparison.OrdinalIgnoreCase);
                // The Windows user folder is still there, without its name.
                Assert.Contains("%USERPROFILE%", json);
                Assert.DoesNotContain("C:\\\\Users", json, StringComparison.OrdinalIgnoreCase);
            }

            // What is kept is what the studio needs.
            Assert.Contains(crash.Lines, l => l == "IsGPU: True");
            Assert.Contains(crash.Lines, l => l.EndsWith("Error: GPU timestamp query timed out", StringComparison.Ordinal));
            Assert.DoesNotContain(crash.Lines, l => l.StartsWith("AnalyticId", StringComparison.Ordinal));

            // The same on the game logs this machine really wrote, when there
            // are any: the studio's test client, joined to PTU with a Steam
            // account and a Windows user of its own.
            foreach (var path in TestFiles.RealGameLogs())
                RealLogLeavesNoPlayerData(path);
        }

        // Every line of a real game log, every report built from it: none
        // names the player, his Steam id, the server joined or the Windows
        // user, all read here from the log itself.
        private static void RealLogLeavesNoPlayerData(string path)
        {
            var log = CrashReport.ReadShared(path);
            var data = TestFiles.PlayerDataIn(log);
            Assert.True(data.Count > 0, "no player data found in " + path);

            var sanitizer = new Sanitizer();
            sanitizer.LearnFromLog(log);
            var userFolders = 0;
            foreach (var line in log)
            {
                var clean = sanitizer.Line(line);
                if (clean == null)
                    continue;
                foreach (var value in data)
                    Assert.False(clean.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0,
                        Path.GetFileName(path) + " leaks a player value in: " + clean);
                if (line.IndexOf(":\\Users\\", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    userFolders++;
                    Assert.Contains("%USERPROFILE%", clean);
                }
            }

            var crash = CrashReport.FromLog(log, TestFiles.Session, TestFiles.Crash, "sir-correctifs-pulsar",
                Texts.DisplayName, "1.1.0.0", new Sanitizer());
            var reports = new ErrorReports(Texts.DisplayName, "1.1.0.0", TestFiles.Session, new Sanitizer());
            reports.Record(string.Join("\n", log.Take(60)), string.Join("\n", log.Skip(60).Take(200)), log, TestFiles.Session);
            var error = reports.TakeAll(TestFiles.Crash).Single();

            foreach (var report in new[] { crash, error })
            {
                var texts = new List<string>(report.Lines) { report.Message, report.Stack, report.ToJson() };
                foreach (var text in texts)
                {
                    foreach (var value in data)
                        Assert.False(text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0,
                            Path.GetFileName(path) + " leaks a player value in a report");
                }
            }
        }

        [Fact]
        public void UserFolderBecomesUserProfile()
        {
            var sanitizer = new Sanitizer();
            Assert.Equal(
                "Could not find file '%USERPROFILE%\\AppData\\Roaming\\SpaceEngineers\\Storage\\settings.xml'.",
                sanitizer.Line("Could not find file 'C:\\Users\\rsable\\AppData\\Roaming\\SpaceEngineers\\Storage\\settings.xml'."));
            Assert.Equal(
                "   at Plugin.Load() in %USERPROFILE%/source/plugin/Plugin.cs:line 12",
                sanitizer.Line("   at Plugin.Load() in c:/users/rsable/source/plugin/Plugin.cs:line 12"));
            Assert.Equal(
                "config \"%USERPROFILE%\\\\AppData\\\\Roaming\"",
                sanitizer.Line("config \"D:\\\\Users\\\\rsable\\\\AppData\\\\Roaming\""));

            // The folder this machine gives, whatever its drive or shape, and
            // the user name it carries.
            var known = new Sanitizer("E:\\Profiles\\Jean Dupont");
            Assert.Equal("%USERPROFILE%\\Documents\\x.txt", known.Line("e:\\profiles\\jean dupont\\Documents\\x.txt"));
            Assert.Null(known.Line("logged in as Jean Dupont"));

            // A line without a user folder is left as it is.
            Assert.Equal("Error: GPU timestamp query timed out", sanitizer.Line("Error: GPU timestamp query timed out"));
        }

        [Fact]
        public void LinesNamingAPlayerAGridOrAServerAreRemoved()
        {
            var sanitizer = new Sanitizer(null, new[] { "RougeSable" });
            Assert.Null(sanitizer.Line("Service.UserId: 76561198012345678"));
            Assert.Null(sanitizer.Line("hello RougeSable"));
            Assert.Null(sanitizer.Line("Joining server: Name:Any ConnectionString:steam://198.51.100.4:27016"));
            Assert.Null(sanitizer.Line("connected to 198.51.100.4"));
            Assert.Null(sanitizer.Line("Error: Grid 'Red Baron' exceeded block limit"));
            Assert.Null(sanitizer.Line("CreateCompressedMsg: Type: MyObjectBuilder_CubeGrid  Name: Red Baron  EntityID: 7A1C33F0"));
            Assert.Null(sanitizer.Line("Player entered: Somebody (76561198000000000)"));

            // Type names in a stack are no player data, nor are versions.
            Assert.Equal("   at Sandbox.Game.World.MyPlayer.get_Identity()", sanitizer.Line("   at Sandbox.Game.World.MyPlayer.get_Identity()"));
            Assert.Equal("Sandbox.Game, Version=1.0.0.0, Culture=neutral", sanitizer.Line("Sandbox.Game, Version=1.0.0.0, Culture=neutral"));
            Assert.Equal("[sir-correctifs-pulsar] hooked on MyCubeGrid.UpdatePredictionFlag",
                sanitizer.Line("[sir-correctifs-pulsar] hooked on MyCubeGrid.UpdatePredictionFlag"));
        }
    }
}
