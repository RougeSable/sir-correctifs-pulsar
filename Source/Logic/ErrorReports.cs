using System;
using System.Collections.Generic;
using System.Text;

namespace SirCorrectifsPulsar
{
    // The errors of one game session. The same error (same message, same top
    // of the stack) is counted, and leaves once per session as one report
    // with its count and its first and last times.
    //
    // A report is ready when its error has been quiet for QuietTime, or at
    // the latest LongestWait after it first appeared, so that a burst of
    // repetitions goes in one report. Whatever is still waiting when the game
    // closes is taken at once (TakeAll). Repetitions after a report left are
    // still counted, but nothing more leaves for that error in the session.
    //
    // Errors may be recorded from any thread; texts are cleaned (Sanitizer)
    // only when a report is built, on the caller's thread.
    public sealed class ErrorReports
    {
        public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);

        // Different errors kept in a session, at most: a plugin that fails in
        // a thousand ways says enough with the first ones.
        public const int MaxErrors = 50;

        private const int MaxStackLines = 60;

        private sealed class Entry
        {
            public string Message;
            public string Stack;
            public DateTime First;
            public DateTime Last;
            public int Count;
            public List<string> Lines;
            public bool Sent;
        }

        private readonly object m_lock = new object();
        private readonly Dictionary<string, Entry> m_entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly List<Entry> m_order = new List<Entry>();
        private readonly string m_plugin;
        private readonly string m_version;
        private readonly DateTime m_session;
        private readonly Sanitizer m_sanitizer;

        public ErrorReports(string plugin, string version, DateTime sessionUtc, Sanitizer sanitizer)
        {
            m_plugin = plugin ?? "";
            m_version = version ?? "";
            m_session = sessionUtc;
            m_sanitizer = sanitizer ?? new Sanitizer();
        }

        public DateTime Session
        {
            get { return m_session; }
        }

        public Sanitizer Sanitizer
        {
            get { return m_sanitizer; }
        }

        // An error line, or an exception split in message and stack. lines:
        // the plugin's last lines in the game log when it first appears.
        public void Record(string message, string stack, IList<string> lines, DateTime nowUtc)
        {
            try
            {
                message = (message ?? "").Trim();
                stack = stack ?? "";
                var key = message + "\n" + TopOfStack(stack);
                lock (m_lock)
                {
                    Entry entry;
                    if (m_entries.TryGetValue(key, out entry))
                    {
                        entry.Count++;
                        if (nowUtc > entry.Last)
                            entry.Last = nowUtc;
                        return;
                    }
                    if (m_order.Count >= MaxErrors)
                        return;
                    entry = new Entry
                    {
                        Message = message,
                        Stack = stack,
                        First = nowUtc,
                        Last = nowUtc,
                        Count = 1,
                        Lines = lines == null ? new List<string>() : new List<string>(lines),
                    };
                    m_entries.Add(key, entry);
                    m_order.Add(entry);
                }
            }
            catch (Exception)
            {
                // Reporting never breaks the plugin, nor the game.
            }
        }

        public void RecordException(string context, Exception exception, IList<string> lines, DateTime nowUtc)
        {
            Record(MessageOf(context, exception), StackOf(exception), lines, nowUtc);
        }

        // "context: Type: message", on one line.
        public static string MessageOf(string context, Exception exception)
        {
            var text = exception == null
                ? "(no exception)"
                : exception.GetType().FullName + ": " + FirstLine(exception.Message);
            return string.IsNullOrEmpty(context) ? text : context + ": " + text;
        }

        // The stack of the exception, then of each inner exception.
        public static string StackOf(Exception exception)
        {
            var stack = new StringBuilder();
            var depth = 0;
            while (exception != null && depth < 8)
            {
                if (depth > 0)
                    stack.Append("--- inner ").Append(exception.GetType().FullName).Append(": ").Append(FirstLine(exception.Message)).Append('\n');
                if (!string.IsNullOrEmpty(exception.StackTrace))
                    stack.Append(exception.StackTrace.Replace("\r\n", "\n").TrimEnd('\n')).Append('\n');
                exception = exception.InnerException;
                depth++;
            }
            return stack.ToString().TrimEnd('\n');
        }

        public static string TopOfStack(string stack)
        {
            foreach (var line in Sanitizer.SplitLines(stack))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    return trimmed;
            }
            return "";
        }

        private static string FirstLine(string text)
        {
            var lines = Sanitizer.SplitLines(text ?? "");
            return lines.Length == 0 ? "" : lines[0].Trim();
        }

        // Reports ready now, each one taken once.
        public List<ErrorReport> TakeDue(DateTime nowUtc)
        {
            return Take(nowUtc, false);
        }

        // Every report still waiting: the game is closing.
        public List<ErrorReport> TakeAll(DateTime nowUtc)
        {
            return Take(nowUtc, true);
        }

        private List<ErrorReport> Take(DateTime nowUtc, bool all)
        {
            var taken = new List<Entry>();
            var copies = new List<Entry>();
            lock (m_lock)
            {
                foreach (var entry in m_order)
                {
                    if (entry.Sent)
                        continue;
                    if (!all && nowUtc - entry.Last < QuietTime && nowUtc - entry.First < LongestWait)
                        continue;
                    entry.Sent = true;
                    taken.Add(entry);
                    copies.Add(new Entry
                    {
                        Message = entry.Message,
                        Stack = entry.Stack,
                        First = entry.First,
                        Last = entry.Last,
                        Count = entry.Count,
                        Lines = entry.Lines,
                    });
                }
            }

            var reports = new List<ErrorReport>();
            foreach (var entry in copies)
            {
                try
                {
                    reports.Add(Build(entry));
                }
                catch (Exception)
                {
                    // A report that cannot be built is dropped, never thrown.
                }
            }
            return reports;
        }

        private ErrorReport Build(Entry entry)
        {
            var message = m_sanitizer.Text(entry.Message);
            if (message.Length == 0)
                message = "(message removed: it named the player, a grid or a server)";

            var stack = new List<string>();
            foreach (var line in m_sanitizer.Lines(Sanitizer.SplitLines(entry.Stack)))
            {
                if (line.Trim().Length == 0)
                    continue;
                if (stack.Count >= MaxStackLines)
                    break;
                stack.Add(line);
            }

            return new ErrorReport
            {
                Plugin = m_plugin,
                Version = m_version,
                Kind = ErrorReport.ErrorKind,
                Message = message,
                Stack = string.Join("\n", stack.ToArray()),
                First = entry.First,
                Last = entry.Last,
                Count = entry.Count,
                Session = m_session,
                Lines = m_sanitizer.Lines(entry.Lines),
            };
        }
    }
}
