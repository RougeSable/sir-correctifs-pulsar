using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SirCorrectifsPulsar
{
    // The reports waiting to leave, one file each in the plugin's folder
    // (Storage\sir-correctifs-pulsar\reports), already cleaned of any player
    // data. They only leave while the player is on one of our servers (PU or
    // PTU, OurServer), oldest first, and each file is erased once sent.
    // Elsewhere, and in single player, they wait for the next connection.
    //
    // Nothing here ever throws: a report that cannot be written or sent is
    // dropped or kept, the game goes on.
    public sealed class ReportQueue
    {
        public const string FolderName = "reports";
        public const string Extension = ".json";

        // Kept at most; the oldest go first when more pile up.
        public const int MaxFiles = 100;

        // Failed sends in a row before the queue waits for the next
        // connection.
        public const int MaxFailures = 3;

        private readonly string m_directory;
        private int m_counter;
        private int m_failures;

        public ReportQueue(string pluginDirectory)
        {
            m_directory = Path.Combine(pluginDirectory ?? "", FolderName);
        }

        public string Directory
        {
            get { return m_directory; }
        }

        public int Count
        {
            get { return Files().Count; }
        }

        public bool Enqueue(ErrorReport report)
        {
            if (report == null)
                return false;
            try
            {
                System.IO.Directory.CreateDirectory(m_directory);
                var name = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)
                    + "-" + Interlocked.Increment(ref m_counter).ToString("D4", CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + Extension;
                var path = Path.Combine(m_directory, name);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, report.ToBytes());
                File.Move(temporary, path);
                Trim();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // A new connection to one of our servers: sending is tried again.
        public void NewConnection()
        {
            m_failures = 0;
        }

        // Sends up to max waiting reports, only when onOurServer. Returns how
        // many left.
        public int Pump(bool onOurServer, Action<byte[]> send, int max)
        {
            if (!onOurServer || send == null || max <= 0 || m_failures >= MaxFailures)
                return 0;

            var sent = 0;
            try
            {
                foreach (var path in Files())
                {
                    if (sent >= max)
                        break;
                    byte[] bytes;
                    try
                    {
                        bytes = File.ReadAllBytes(path);
                    }
                    catch (Exception)
                    {
                        continue;
                    }
                    if (bytes.Length == 0)
                    {
                        TryDelete(path);
                        continue;
                    }

                    try
                    {
                        send(bytes);
                    }
                    catch (Exception)
                    {
                        // Kept for later; after a few failures, the queue
                        // waits for the next connection.
                        m_failures++;
                        break;
                    }
                    m_failures = 0;
                    sent++;
                    TryDelete(path);
                }
            }
            catch (Exception)
            {
                // The game never sees a fault of the reports.
            }
            return sent;
        }

        private List<string> Files()
        {
            var files = new List<string>();
            try
            {
                if (!System.IO.Directory.Exists(m_directory))
                    return files;
                foreach (var file in System.IO.Directory.GetFiles(m_directory, "*" + Extension))
                {
                    if (file.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                        files.Add(file);
                }
                files.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
            }
            return files;
        }

        private void Trim()
        {
            var files = Files();
            for (var i = 0; i < files.Count - MaxFiles; i++)
                TryDelete(files[i]);
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
