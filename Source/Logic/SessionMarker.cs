using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SirCorrectifsPulsar
{
    // A mark left in the plugin's folder while the game runs: written when
    // the session starts, erased at a clean exit. Found at the next start, it
    // says the previous session crashed, when it started and which game log
    // it wrote.
    //
    // The mark stays on the player's machine: it is never sent.
    public static class SessionMarker
    {
        public const string FileName = "session.marker";

        public sealed class Leftover
        {
            public DateTime Session;
            public string LogPath;
        }

        public static string PathIn(string directory)
        {
            return Path.Combine(directory, FileName);
        }

        // Never throws: without its mark, a crash goes unreported, the game
        // goes on.
        public static bool Begin(string directory, DateTime sessionUtc, string logPath)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var text = "session=" + ErrorReport.Iso(sessionUtc) + "\n" + "log=" + (logPath ?? "") + "\n";
                File.WriteAllText(PathIn(directory), text, new UTF8Encoding(false));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The clean exit.
        public static bool End(string directory)
        {
            try
            {
                var path = PathIn(directory);
                if (File.Exists(path))
                    File.Delete(path);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The mark of a session that did not end cleanly, or null.
        public static Leftover Read(string directory)
        {
            try
            {
                var path = PathIn(directory);
                if (!File.Exists(path))
                    return null;

                var leftover = new Leftover { Session = File.GetLastWriteTimeUtc(path) };
                foreach (var line in File.ReadAllLines(path))
                {
                    var equals = line.IndexOf('=');
                    if (equals <= 0)
                        continue;
                    var key = line.Substring(0, equals).Trim();
                    var value = line.Substring(equals + 1).Trim();
                    DateTime session;
                    if (key == "session" && DateTime.TryParse(value, CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out session))
                        leftover.Session = DateTime.SpecifyKind(session, DateTimeKind.Utc);
                    else if (key == "log" && value.Length > 0)
                        leftover.LogPath = value;
                }
                return leftover;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
