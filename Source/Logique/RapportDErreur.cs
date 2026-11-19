using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SirCorrectifsPulsar
{
    // Où vont les rapports d'erreurs : le canal commun à tous les greffons
    // côté joueur du studio, et les serveurs qui l'écoutent.
    public static class CanalDesRapports
    {
        // Le numéro fixé par la règle des greffons côté joueur du studio.
        public const long Numero = 5465479001L;

        // Les messages réseau du jeu (MyAPIGateway.Multiplayer) prennent un
        // canal sur 16 bits : 5465479001 n'y tient pas. On en garde les 16 bits
        // de poids faible, comme Sir Cel Shading et Sir Holomap : 38745.
        public static readonly ushort CanalDuJeu = unchecked((ushort)(Numero & 0xFFFF));

        // Les serveurs du studio, PU et PTU, portent tous ce mot dans leur nom
        // (« [FR] Sirius Immersion - PTU »).
        public const string MarqueDuServeur = "Sirius";

        // Vrai sur un serveur dédié du studio, rejoint en client. Solo, partie
        // hébergée ou serveur d'un autre : faux, et rien ne quitte la machine du
        // joueur.
        public static bool EstUnDeNosServeurs(bool multijoueur, bool estServeur, bool dedie, string nomDuServeur, string nomDuMonde)
        {
            if (!multijoueur || estServeur || !dedie)
                return false;
            return Porte(nomDuServeur) || Porte(nomDuMonde);
        }

        private static bool Porte(string nom)
        {
            return !string.IsNullOrEmpty(nom) && nom.IndexOf(MarqueDuServeur, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // Un rapport envoyé au serveur, dans la forme fixée par la règle des
    // greffons côté joueur : un objet JSON en UTF-8
    //   { greffon, version, genre, message, pile, premiere, derniere,
    //     nombre, session, lignes }
    // Rien sur le joueur n'y entre : tout est nettoyé (NettoyeurDeRapports)
    // avant d'arriver ici.
    public sealed class RapportDErreur
    {
        public const string GenreErreur = "erreur";
        public const string GenrePlantage = "plantage";

        // Au-delà, le rapport est raccourci : les lignes les plus anciennes
        // d'abord, puis la pile.
        public const int OctetsMaximum = 32000;
        public const int LongueurDeLigneMaximum = 1000;

        public string Greffon = "";
        public string Version = "";
        public string Genre = GenreErreur;
        public string Message = "";
        public string Pile = "";
        public DateTime Premiere;
        public DateTime Derniere;
        public int Nombre = 1;
        public DateTime Session;
        public List<string> Lignes = new List<string>();

        // ISO 8601, en UTC, à la milliseconde : 2026-11-19T13:56:02.123Z
        public static string HeureIso(DateTime moment)
        {
            return FormeDesReleves.Heure(moment);
        }

        public string Json()
        {
            var json = new StringBuilder(256);
            json.Append('{');
            Champ(json, "greffon", Greffon); json.Append(',');
            Champ(json, "version", Version); json.Append(',');
            Champ(json, "genre", Genre); json.Append(',');
            Champ(json, "message", Message); json.Append(',');
            Champ(json, "pile", Pile); json.Append(',');
            Champ(json, "premiere", HeureIso(Premiere)); json.Append(',');
            Champ(json, "derniere", HeureIso(Derniere)); json.Append(',');
            json.Append("\"nombre\":").Append(Nombre.ToString(CultureInfo.InvariantCulture)).Append(',');
            Champ(json, "session", HeureIso(Session)); json.Append(',');
            json.Append("\"lignes\":[");
            for (var i = 0; i < Lignes.Count; i++)
            {
                if (i > 0)
                    json.Append(',');
                Chaine(json, Lignes[i]);
            }
            json.Append("]}");
            return json.ToString();
        }

        // Les octets envoyés au serveur : UTF-8 sans marque d'ordre des octets,
        // jamais au-dessus de OctetsMaximum.
        public byte[] Utf8()
        {
            var encodage = new UTF8Encoding(false);
            var ajuste = Copie();
            for (var i = 0; i < ajuste.Lignes.Count; i++)
                ajuste.Lignes[i] = Raccourcir(ajuste.Lignes[i], LongueurDeLigneMaximum);
            ajuste.Message = Raccourcir(ajuste.Message, LongueurDeLigneMaximum);

            var octets = encodage.GetBytes(ajuste.Json());
            while (octets.Length > OctetsMaximum && ajuste.Lignes.Count > 0)
            {
                ajuste.Lignes.RemoveAt(0);
                octets = encodage.GetBytes(ajuste.Json());
            }
            if (octets.Length > OctetsMaximum)
            {
                ajuste.Pile = Raccourcir(ajuste.Pile, OctetsMaximum / 4);
                octets = encodage.GetBytes(ajuste.Json());
            }
            return octets;
        }

        public RapportDErreur Copie()
        {
            var copie = (RapportDErreur)MemberwiseClone();
            copie.Lignes = new List<string>(Lignes);
            return copie;
        }

        private static string Raccourcir(string texte, int longueur)
        {
            if (texte == null || texte.Length <= longueur)
                return texte ?? "";
            return texte.Substring(0, longueur) + "...";
        }

        private static void Champ(StringBuilder json, string nom, string valeur)
        {
            json.Append('"').Append(nom).Append("\":");
            Chaine(json, valeur);
        }

        private static void Chaine(StringBuilder json, string valeur)
        {
            json.Append('"');
            foreach (var c in valeur ?? "")
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    case '\n': json.Append("\\n"); break;
                    case '\r': json.Append("\\r"); break;
                    case '\t': json.Append("\\t"); break;
                    case '\b': json.Append("\\b"); break;
                    case '\f': json.Append("\\f"); break;
                    default:
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029)
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
