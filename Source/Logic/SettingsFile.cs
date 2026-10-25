using System;
using System.IO;
using System.Xml.Serialization;

namespace SirCorrectifsPulsar
{
    // Reading and writing the settings. A missing or unreadable file never
    // blocks the game: the defaults are used instead.
    public static class SettingsFile
    {
        public const string FileName = "settings.xml";

        private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(Settings));

        public static string PathIn(string directory)
        {
            return Path.Combine(directory, FileName);
        }

        // A setting missing from the file keeps its default: a file written
        // before a setting existed turns it on, like a new installation.
        public static Settings Load(string directory, out string problem)
        {
            problem = null;
            var path = PathIn(directory);
            if (!File.Exists(path))
                return new Settings();

            try
            {
                using (var stream = File.OpenRead(path))
                {
                    var read = Serializer.Deserialize(stream) as Settings;
                    if (read == null)
                    {
                        problem = "empty settings file " + path;
                        return new Settings();
                    }
                    return read;
                }
            }
            catch (Exception e)
            {
                problem = "unreadable settings file " + path + " (" + e.Message + ")";
                return new Settings();
            }
        }

        public static void Save(string directory, Settings settings)
        {
            Directory.CreateDirectory(directory);
            var path = PathIn(directory);

            // Written next to the file, then swapped in: a crash of the game
            // never leaves a half-written settings file.
            var temporary = path + ".tmp";
            using (var stream = File.Create(temporary))
                Serializer.Serialize(stream, settings.Copy());

            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);
        }
    }
}
