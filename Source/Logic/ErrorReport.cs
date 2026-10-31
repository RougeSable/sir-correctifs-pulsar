using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SirCorrectifsPulsar
{
    // One report sent to our servers, in the form fixed by the studio's rule
    // for client plugins ("error reports"): a UTF-8 JSON object
    // { greffon, version, genre, message, pile, premiere, derniere, nombre,
    // session, lignes }. The field names and the two kinds are the studio's,
    // kept as written. Nothing about the player goes in it: every text is
    // cleaned (Sanitizer) before a report is built.
    public sealed class ErrorReport
    {
        // A caught exception, or an error line the plugin wrote to the game log.
        public const string ErrorKind = "erreur";

        // The previous game session ended without a clean exit.
        public const string CrashKind = "plantage";

        // The product's name.
        public string Plugin = "";

        // The assembly's version.
        public string Version = "";

        public string Kind = ErrorKind;

        public string Message = "";

        // The stack, one frame per line.
        public string Stack = "";

        // First and last time the error was seen, UTC.
        public DateTime First;
        public DateTime Last;

        // How many times it was seen in the session.
        public int Count = 1;

        // When the game session concerned started, UTC.
        public DateTime Session;

        // Lines of the game log.
        public List<string> Lines = new List<string>();

        public static string Iso(DateTime utc)
        {
            if (utc.Kind == DateTimeKind.Local)
                utc = utc.ToUniversalTime();
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        public string ToJson()
        {
            var json = new StringBuilder(1024);
            json.Append('{');
            Json.Property(json, "greffon", Plugin, true);
            Json.Property(json, "version", Version, false);
            Json.Property(json, "genre", Kind, false);
            Json.Property(json, "message", Message, false);
            Json.Property(json, "pile", Stack, false);
            Json.Property(json, "premiere", Iso(First), false);
            Json.Property(json, "derniere", Iso(Last), false);
            json.Append(",\"nombre\":").Append(Count.ToString(CultureInfo.InvariantCulture));
            Json.Property(json, "session", Iso(Session), false);
            json.Append(",\"lignes\":[");
            var first = true;
            foreach (var line in Lines ?? new List<string>())
            {
                if (!first)
                    json.Append(',');
                Json.String(json, line);
                first = false;
            }
            json.Append("]}");
            return json.ToString();
        }

        // What goes over the network: the JSON in UTF-8, without a byte
        // order mark.
        public byte[] ToBytes()
        {
            return new UTF8Encoding(false).GetBytes(ToJson());
        }
    }

    // The few JSON pieces the reports need, so that the plugin depends on no
    // library Pulsar does not give it.
    public static class Json
    {
        public static void Property(StringBuilder json, string name, string value, bool first)
        {
            if (!first)
                json.Append(',');
            String(json, name);
            json.Append(':');
            String(json, value);
        }

        public static void String(StringBuilder json, string value)
        {
            json.Append('"');
            foreach (var c in value ?? "")
            {
                switch (c)
                {
                    case '"':
                        json.Append("\\\"");
                        break;
                    case '\\':
                        json.Append("\\\\");
                        break;
                    case '\n':
                        json.Append("\\n");
                        break;
                    case '\r':
                        json.Append("\\r");
                        break;
                    case '\t':
                        json.Append("\\t");
                        break;
                    default:
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                            json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            json.Append(c);
                        break;
                }
            }
            json.Append('"');
        }
    }
}
