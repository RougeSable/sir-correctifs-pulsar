using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // What the plugin writes to the game log, each line prefixed with
    // [sir-correctifs-pulsar]. An error line and a caught exception also
    // become an error report (ErrorReports), with the plugin's last lines.
    //
    // Callable from any thread. Writing never throws: a log that fails is
    // ignored.
    public sealed class PluginLog
    {
        // The plugin's last lines kept for a report.
        public const int RecentLines = 20;

        public const string ErrorMark = "ERROR ";

        private readonly string m_prefix;
        private readonly Action<string> m_write;
        private readonly Func<DateTime> m_clock;
        private readonly object m_lock = new object();
        private readonly Queue<string> m_recent = new Queue<string>();
        private volatile ErrorReports m_reports;

        public PluginLog(string id, Action<string> write, ErrorReports reports = null, Func<DateTime> clock = null)
        {
            m_prefix = "[" + id + "] ";
            m_write = write ?? (_ => { });
            m_reports = reports;
            m_clock = clock ?? (() => DateTime.UtcNow);
        }

        public ErrorReports Reports
        {
            get { return m_reports; }
            set { m_reports = value; }
        }

        public string Prefix
        {
            get { return m_prefix; }
        }

        public void Info(string text)
        {
            Write(m_prefix + text);
        }

        // An error line: written to the game log, and reported.
        public void Error(string text)
        {
            Write(m_prefix + ErrorMark + text);
            var reports = m_reports;
            if (reports != null)
                reports.Record(text, "", Recent(), Now());
        }

        // A caught exception: written to the game log with its stack, and
        // reported.
        public void Error(string context, Exception exception)
        {
            Write(m_prefix + ErrorMark + context + ": " + exception);
            var reports = m_reports;
            if (reports != null)
                reports.RecordException(context, exception, Recent(), Now());
        }

        public List<string> Recent()
        {
            lock (m_lock)
                return new List<string>(m_recent);
        }

        private DateTime Now()
        {
            try
            {
                return m_clock();
            }
            catch (Exception)
            {
                return DateTime.UtcNow;
            }
        }

        private void Write(string text)
        {
            try
            {
                lock (m_lock)
                {
                    foreach (var line in Sanitizer.SplitLines(text))
                    {
                        if (line.Length == 0)
                            continue;
                        m_recent.Enqueue(line);
                        while (m_recent.Count > RecentLines)
                            m_recent.Dequeue();
                    }
                }
                m_write(text);
            }
            catch (Exception)
            {
                // The log must never break the game.
            }
        }
    }
}
