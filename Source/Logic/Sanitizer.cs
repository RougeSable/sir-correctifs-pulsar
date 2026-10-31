using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SirCorrectifsPulsar
{
    // Cleans every text before it goes into a report: nothing about the
    // player may leave his machine. A line that carries a Steam id, a player
    // name, a grid name or a server address is removed whole; the Windows
    // user folder (C:\Users\<name>) becomes %USERPROFILE%.
    //
    // Two kinds of rules:
    // - patterns, for what the game writes in a known shape: long numbers
    //   (Steam ids, identities), network addresses, words that announce a
    //   player, a server or a name ("Player entered:", "Name: ...");
    // - known values, learned from the game (the player's name and Steam id)
    //   and from the log itself (the game writes the player's name, his Steam
    //   id, the server joined and the players met in fixed lines): any later
    //   line that names one of them is removed too.
    //
    // Stack frames ("   at Namespace.Type.Method()") only go through the
    // value rules: a type name such as MyPlayer or MyCubeGrid is not data
    // about the player.
    public sealed class Sanitizer
    {
        public const string UserProfile = "%USERPROFILE%";

        // The longest line kept, so that a report always stays small.
        public const int MaxLineLength = 400;

        private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

        // C:\Users\name, C:/Users/name, C:\\Users\\name (escaped), and the old
        // Documents and Settings.
        private static readonly Regex UserFolder = new Regex(
            @"[A-Za-z]:(?:\\\\|\\|/)+(?:Users|Documents and Settings)(?:\\\\|\\|/)+[^\\/\r\n""'<>|:*?]+", Options);

        // Steam ids (17 digits), identity ids (18), entity ids, analytics ids.
        private static readonly Regex LongNumber = new Regex(@"(?<![0-9A-Fa-fx])\d{15,}(?![0-9A-Fa-f])", Options);

        // An IPv4 address, not a version number written after '='
        // (Version=1.0.0.0).
        private static readonly Regex Ipv4 = new Regex(
            @"(?<![\w.=])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\w.])", Options);

        private static readonly Regex Ipv6 = new Regex(@"(?<![\w:])(?:[0-9a-f]{1,4}:){4,7}[0-9a-f]{1,4}(?![\w:])", Options);

        // steam://..., any address with a scheme, except local files.
        private static readonly Regex Address = new Regex(@"\b[a-z][a-z0-9+.-]*://(?!/)", Options);

        // Words that announce data about a player, a server or a session.
        private static readonly Regex Words = new Regex(
            @"\b(?:steam|user|users|player|players|identity|identities|nickname|nick|pseudo|gamertag|lobby|server|servers|host|ip|address|peer|faction|factions|email|e-mail|chat|gps|world)\b",
            Options);

        // Parts of identifiers that carry such data (Service.UserName,
        // OnlineUserId, AnalyticId...).
        private static readonly string[] Fragments =
        {
            "userid", "username", "steamid", "analyticid", "playername", "displayname", "connectionstring",
            "ipaddress", "hostname", "identityid", "endpoint", "memberid", "membername",
        };

        // A name written after a word that says so: "Name: Iron Heron",
        // "Grid 'Iron Heron'", "named "Kestrel"".
        private static readonly Regex Names = new Regex(
            @"\bname\s*[:=]|\b(?:grid|grids|ship|ships|station|vehicle|entity|block)\s*(?:name)?\s*[:=]?\s*['""]|\b(?:named|called)\s+['""]",
            Options);

        private static readonly Regex StackFrame = new Regex(@"^\s*(?:at\s|---\s|--->)", Options);

        // Fixed lines where the game writes a value to learn.
        private static readonly Regex[] Learners =
        {
            new Regex(@"Service\.UserName:\s*(?<v>.+?)\s*$", Options),
            new Regex(@"Service\.UserId:\s*(?<v>\S+)", Options),
            new Regex(@"Joining server:\s*Name:(?<v>.*?)\s*ConnectionString:(?<w>\S*)", Options),
            new Regex(@"Player (?:entered|left|disconnected):\s*(?<v>.+?)\s*\(", Options),
            new Regex(@"\bName:\s*(?<v>.+?)\s{2,}EntityID", Options),
            new Regex(@"Loading session:\s*(?<p>.+)$", Options),
            new Regex(@"[A-Za-z]:(?:\\\\|\\|/)+Users(?:\\\\|\\|/)+(?<v>[^\\/\r\n""'<>|:*?]+)", Options),
        };

        // Folders under the users folder that name nobody.
        private static readonly HashSet<string> CommonFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Public", "Default", "Default User", "All Users",
        };

        private readonly object m_lock = new object();
        private readonly List<string> m_userFolders = new List<string>();
        private readonly List<Regex> m_values = new List<Regex>();
        private readonly HashSet<string> m_known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // userFolder: the Windows user folder of this machine, if known.
        public Sanitizer(string userFolder = null, IEnumerable<string> knownValues = null)
        {
            if (!string.IsNullOrEmpty(userFolder))
            {
                m_userFolders.Add(userFolder.TrimEnd('\\', '/'));
                var name = LastPart(userFolder);
                if (name != null)
                    Learn(name);
            }
            if (knownValues != null)
            {
                foreach (var value in knownValues)
                    Learn(value);
            }
        }

        // A value that must never leave: any line that names it is removed.
        public void Learn(string value)
        {
            if (value == null)
                return;
            value = value.Trim();
            if (value.Length < 2 || value == UserProfile)
                return;

            lock (m_lock)
            {
                if (!m_known.Add(value))
                    return;
                // Whole word: "Bob" removes "Player Bob", not "Bobbing".
                m_values.Add(new Regex(@"(?<![\p{L}\p{N}_])" + Regex.Escape(value) + @"(?![\p{L}\p{N}_])", Options));
            }
        }

        // Learns the values the game writes in its own fixed lines: the
        // player's name and Steam id, the server joined, the players met, the
        // grids named, the Windows user name.
        public void LearnFromLog(IEnumerable<string> lines)
        {
            if (lines == null)
                return;
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line))
                    continue;
                foreach (var learner in Learners)
                {
                    var match = learner.Match(line);
                    if (!match.Success)
                        continue;
                    var value = match.Groups["v"];
                    if (value.Success && !CommonFolders.Contains(value.Value.Trim()))
                        Learn(value.Value);
                    var address = match.Groups["w"];
                    if (address.Success)
                        LearnAddress(address.Value);
                    var path = match.Groups["p"];
                    if (path.Success)
                    {
                        // The name of the world, last part of its folder.
                        var name = LastPart(path.Value.Trim());
                        if (name != null)
                            Learn(name);
                    }
                }
            }
        }

        private void LearnAddress(string connection)
        {
            if (string.IsNullOrEmpty(connection))
                return;
            Learn(connection);
            var host = connection;
            var scheme = host.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
                host = host.Substring(scheme + 3);
            host = host.TrimEnd('/');
            var port = host.LastIndexOf(':');
            if (port > 0 && host.IndexOf(':') == port)
                host = host.Substring(0, port);
            Learn(host);
        }

        private static string LastPart(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var parts = path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts[parts.Length - 1].Trim();
        }

        // The line as it may leave, or null when it must be removed.
        public string Line(string line)
        {
            if (line == null)
                return null;
            line = line.TrimEnd('\r', '\n');

            // The user folder first: a path is useful once the user name is
            // out of it.
            lock (m_lock)
            {
                foreach (var folder in m_userFolders)
                    line = Replace(line, folder, UserProfile);
            }
            line = UserFolder.Replace(line, UserProfile);

            if (LongNumber.IsMatch(line) || Ipv4.IsMatch(line) || Ipv6.IsMatch(line) || Address.IsMatch(line))
                return null;

            lock (m_lock)
            {
                foreach (var value in m_values)
                {
                    if (value.IsMatch(line))
                        return null;
                }
            }

            if (!StackFrame.IsMatch(line))
            {
                if (Words.IsMatch(line) || Names.IsMatch(line))
                    return null;
                var lower = line.ToLowerInvariant();
                foreach (var fragment in Fragments)
                {
                    if (lower.Contains(fragment))
                        return null;
                }
            }

            if (line.Length > MaxLineLength)
                line = line.Substring(0, MaxLineLength) + "...";
            return line;
        }

        public List<string> Lines(IEnumerable<string> lines)
        {
            var kept = new List<string>();
            if (lines == null)
                return kept;
            foreach (var line in lines)
            {
                var clean = Line(line);
                if (clean != null)
                    kept.Add(clean);
            }
            return kept;
        }

        // A text of several lines (a message, a stack): each line is cleaned,
        // removed lines are left out.
        public string Text(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            return string.Join("\n", Lines(SplitLines(text)).ToArray());
        }

        public static string[] SplitLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static string Replace(string text, string search, string replacement)
        {
            if (string.IsNullOrEmpty(search))
                return text;
            var result = new StringBuilder(text.Length);
            var start = 0;
            while (true)
            {
                var index = text.IndexOf(search, start, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                    break;
                result.Append(text, start, index - start).Append(replacement);
                start = index + search.Length;
            }
            result.Append(text, start, text.Length - start);
            return result.ToString();
        }
    }
}
