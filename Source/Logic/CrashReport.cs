using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SirCorrectifsPulsar
{
    // The "plantage" report: at the start that follows a crash (the session
    // mark was left in place), the game log of the crashed session is read
    // again, and the report carries:
    // - the game's CRASH INFO block,
    // - the exception that brought the game down, and its stack,
    // - the game's last 20 error lines before the crash,
    // - the plugin's last 20 lines.
    // Every line goes through the Sanitizer first.
    public static class CrashReport
    {
        public const int LastLines = 20;

        // The game's own log files, in %AppData%\SpaceEngineers.
        public const string GameLogPattern = "SpaceEngineers_*.log";

        // Written by the game around its crash information (VRage.CrashInfo).
        public const string StartMark = "================================== CRASH INFO ==================================";
        public const string EndMark = "================================== OFNI HSARC ==================================";

        public const string GameErrorsHeader = "--- last game error lines ---";
        public const string NoCrashInfo = "(no CRASH INFO block in the game log)";
        public const string NoException = "the previous game session ended without a clean exit, and its log names no exception";

        private const int MaxStackLines = 60;
        private const int MaxBlockLines = 40;

        // "2026-10-27 21:17:25.700 - Thread:   1 ->  text"
        private static readonly Regex EntryStart = new Regex(
            @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} - Thread:\s*\d+\s*->", RegexOptions.CultureInvariant);

        private static readonly Regex ErrorWord = new Regex(
            @"\b(?:error|errors|exception|failed|failure|fatal|crash|crashed|assert)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] ExceptionLabels =
        {
            "var exception = ", "Exception occurred: ", "Native exception occurred:", "Unhandled exception",
        };

        private sealed class Entry
        {
            public string Raw;
            public string Text;
            public readonly List<string> More = new List<string>();
        }

        public static string PluginHeader(string pluginId)
        {
            return "--- last lines of " + pluginId + " ---";
        }

        // At startup, before the new session's mark is written: the report
        // of the previous session if it crashed, null otherwise. Never throws.
        public static ErrorReport FromLeftoverMarker(string pluginDirectory, string gameLogDirectory, string currentLogPath,
            string pluginId, string pluginName, string version, Sanitizer sanitizer)
        {
            try
            {
                var leftover = SessionMarker.Read(pluginDirectory);
                if (leftover == null)
                    return null;

                var log = PreviousLog(leftover.LogPath, gameLogDirectory, currentLogPath);
                var lines = log == null ? new List<string>() : ReadShared(log);
                var crashTime = log == null ? leftover.Session : File.GetLastWriteTimeUtc(log);
                return FromLog(lines, leftover.Session, crashTime, pluginId, pluginName, version, sanitizer);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The log the crashed session wrote: the one its mark names, else the
        // most recent game log other than the current one.
        public static string PreviousLog(string marked, string gameLogDirectory, string currentLogPath)
        {
            if (!string.IsNullOrEmpty(marked) && File.Exists(marked) && !SamePath(marked, currentLogPath))
                return marked;
            if (string.IsNullOrEmpty(gameLogDirectory) || !Directory.Exists(gameLogDirectory))
                return null;

            string best = null;
            var bestTime = DateTime.MinValue;
            foreach (var path in Directory.GetFiles(gameLogDirectory, GameLogPattern))
            {
                if (SamePath(path, currentLogPath))
                    continue;
                var time = File.GetLastWriteTimeUtc(path);
                if (best == null || time > bestTime)
                {
                    best = path;
                    bestTime = time;
                }
            }
            return best;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The game may still hold its log open: read it shared.
        public static List<string> ReadShared(string path)
        {
            var lines = new List<string>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    lines.Add(line);
            }
            return lines;
        }

        public static ErrorReport FromLog(IList<string> lines, DateTime sessionUtc, DateTime crashUtc,
            string pluginId, string pluginName, string version, Sanitizer sanitizer)
        {
            lines = lines ?? new List<string>();
            sanitizer = sanitizer ?? new Sanitizer();
            sanitizer.LearnFromLog(lines);

            // The CRASH INFO block.
            var blockStart = -1;
            var blockEnd = -1;
            for (var i = 0; i < lines.Count; i++)
            {
                var trimmed = (lines[i] ?? "").Trim();
                if (blockStart < 0 && trimmed == StartMark)
                    blockStart = i;
                else if (blockStart >= 0 && trimmed == EndMark)
                {
                    blockEnd = i;
                    break;
                }
            }
            if (blockStart >= 0 && blockEnd < 0)
                blockEnd = Math.Min(lines.Count - 1, blockStart + MaxBlockLines);

            // The log's entries, outside the block.
            var entries = new List<Entry>();
            for (var i = 0; i < lines.Count; i++)
            {
                if (blockStart >= 0 && i >= blockStart && i <= blockEnd)
                    continue;
                var line = (lines[i] ?? "").TrimEnd('\r');
                var start = EntryStart.Match(line);
                if (start.Success)
                {
                    entries.Add(new Entry { Raw = line, Text = line.Substring(start.Length).Trim() });
                }
                else if (entries.Count > 0 && line.Trim().Length > 0)
                {
                    entries[entries.Count - 1].More.Add(line);
                }
            }

            // Where the game starts telling its crash.
            var cut = entries.Count;
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Text == "Showing message" || entries[i].Text == "MyInitializer.OnCrash")
                {
                    cut = i;
                    break;
                }
            }

            var pluginMark = "[" + pluginId + "]";

            // The exception that brought the game down.
            string message = null;
            var stack = new List<string>();
            var crashing = FindException(entries);
            if (crashing != null)
            {
                var text = crashing.Text;
                foreach (var label in ExceptionLabels)
                {
                    var at = text.IndexOf(label, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        text = text.Substring(at + label.Length).Trim();
                        break;
                    }
                }
                var more = crashing.More;
                var first = 0;
                if (text.Length == 0 && more.Count > 0)
                {
                    text = more[0].Trim();
                    first = 1;
                }
                message = sanitizer.Text(text);
                for (var i = first; i < more.Count && stack.Count < MaxStackLines; i++)
                {
                    var clean = sanitizer.Line(more[i]);
                    if (clean != null && clean.Trim().Length > 0)
                        stack.Add(clean);
                }
                if (message.Length == 0)
                    message = "(exception message removed: it named the player, a grid or a server)";
            }

            // The game's error lines before the crash, and the plugin's lines.
            var gameErrors = new List<string>();
            var pluginLines = new List<string>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Raw.IndexOf(pluginMark, StringComparison.Ordinal) >= 0)
                {
                    AddClean(pluginLines, sanitizer, entry.Raw);
                    foreach (var more in entry.More)
                        AddClean(pluginLines, sanitizer, more);
                    continue;
                }
                if (i < cut && ErrorWord.IsMatch(entry.Text))
                    AddClean(gameErrors, sanitizer, entry.Raw);
            }

            var report = new List<string>();
            if (blockStart >= 0)
            {
                for (var i = blockStart; i <= blockEnd; i++)
                    AddClean(report, sanitizer, lines[i]);
            }
            else
            {
                report.Add(NoCrashInfo);
            }
            report.Add(GameErrorsHeader);
            report.AddRange(Last(gameErrors, LastLines));
            report.Add(PluginHeader(pluginId));
            report.AddRange(Last(pluginLines, LastLines));

            return new ErrorReport
            {
                Plugin = pluginName,
                Version = version,
                Kind = ErrorReport.CrashKind,
                Message = message ?? NoException,
                Stack = string.Join("\n", stack.ToArray()),
                First = crashUtc,
                Last = crashUtc,
                Count = 1,
                Session = sessionUtc,
                Lines = report,
            };
        }

        private static Entry FindException(List<Entry> entries)
        {
            foreach (var label in ExceptionLabels)
            {
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    if (entries[i].Text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                        return entries[i];
                }
            }
            return null;
        }

        private static void AddClean(List<string> into, Sanitizer sanitizer, string line)
        {
            var clean = sanitizer.Line(line);
            if (clean != null && clean.Trim().Length > 0)
                into.Add(clean);
        }

        private static List<string> Last(List<string> lines, int count)
        {
            return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
        }
    }
}
