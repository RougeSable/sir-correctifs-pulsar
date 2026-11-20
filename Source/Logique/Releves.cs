using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SirCorrectifsPulsar
{
    // Les suivis du greffon. Chacun s'écrit chez Sir Correctifs Torch dans
    // son propre dossier, un fichier par jour : minerai/, ecrans/, blocs/ et
    // blocs-du-fil/ (ce que chaque type de bloc coûte au fil du jeu).
    public enum Suivi
    {
        Minerai,
        Ecrans,
        Blocs,
        BlocsDuFil
    }

    public static class NomsDesSuivis
    {
        // Le mot qui ouvre chaque ligne d'un paquet, et le nom du dossier où
        // Sir Correctifs Torch l'écrit.
        public static string Dossier(Suivi suivi)
        {
            switch (suivi)
            {
                case Suivi.Minerai: return "minerai";
                case Suivi.Ecrans: return "ecrans";
                case Suivi.BlocsDuFil: return "blocs-du-fil";
                default: return "blocs";
            }
        }
    }

    // Qui joue : son pseudo en jeu et son identifiant Steam. Chaque ligne le
    // nomme, comme les autres relevés du studio depuis le 18/11.
    public struct IdentiteDuJoueur
    {
        public readonly string Pseudo;
        public readonly ulong IdentifiantSteam;

        public IdentiteDuJoueur(string pseudo, ulong identifiantSteam)
        {
            Pseudo = pseudo;
            IdentifiantSteam = identifiantSteam;
        }

        public bool Connue
        {
            get { return IdentifiantSteam != 0; }
        }
    }

    // Les champs d'une ligne, dans l'ordre où ils s'écrivent : « clé=valeur ».
    public sealed class ChampsDuReleve
    {
        private readonly List<KeyValuePair<string, string>> m_champs = new List<KeyValuePair<string, string>>();

        public IList<KeyValuePair<string, string>> Liste
        {
            get { return m_champs; }
        }

        public ChampsDuReleve Texte(string cle, string valeur)
        {
            if (valeur != null)
                m_champs.Add(new KeyValuePair<string, string>(cle, valeur));
            return this;
        }

        public ChampsDuReleve Entier(string cle, long valeur)
        {
            m_champs.Add(new KeyValuePair<string, string>(cle, valeur.ToString(CultureInfo.InvariantCulture)));
            return this;
        }

        // Un nombre à virgule, écrit avec un point et le nombre de décimales voulu.
        public ChampsDuReleve Nombre(string cle, double valeur, int decimales)
        {
            if (double.IsNaN(valeur) || double.IsInfinity(valeur))
                return this;
            var forme = decimales <= 0 ? "0" : "0." + new string('0', decimales);
            m_champs.Add(new KeyValuePair<string, string>(cle, valeur.ToString(forme, CultureInfo.InvariantCulture)));
            return this;
        }

        public ChampsDuReleve OuiNon(string cle, bool valeur)
        {
            m_champs.Add(new KeyValuePair<string, string>(cle, valeur ? "oui" : "non"));
            return this;
        }

        public ChampsDuReleve Heure(string cle, DateTime? heure)
        {
            if (heure.HasValue)
                m_champs.Add(new KeyValuePair<string, string>(cle, FormeDesReleves.Heure(heure.Value)));
            return this;
        }

        public string Valeur(string cle)
        {
            foreach (var champ in m_champs)
            {
                if (champ.Key == cle)
                    return champ.Value;
            }
            return null;
        }
    }

    // La forme d'une ligne de relevé :
    //   2026-11-19T13:56:02.123Z image=81234 joueur=7656119... pseudo="Guesn" evenement="marqueur apparu" minerai="Magnésium" distance=412.5 ...
    // L'heure est en UTC à la milliseconde, « image » est le compteur d'images
    // de la partie (GameplayFrameCounter) : deux joueurs se comparent à la
    // milliseconde, et un même joueur image par image.
    public static class FormeDesReleves
    {
        public const string Inconnu = "-";

        public static string Heure(DateTime moment)
        {
            var utc = moment.Kind == DateTimeKind.Local ? moment.ToUniversalTime() : moment;
            return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        // Une valeur sans espace, guillemet ni signe égal s'écrit telle quelle ;
        // les autres entre guillemets, le guillemet et l'antislash précédés d'un
        // antislash. Rien ne peut casser la ligne : tout signe de commande
        // devient une espace.
        public static string Valeur(string valeur)
        {
            if (valeur == null)
                return Inconnu;
            var simple = valeur.Length > 0;
            foreach (var c in valeur)
            {
                if (c == ' ' || c == '"' || c == '=' || c == '\\' || char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    simple = false;
                    break;
                }
            }
            if (simple)
                return valeur;

            var texte = new StringBuilder(valeur.Length + 2);
            texte.Append('"');
            foreach (var c in valeur)
            {
                if (c == '"' || c == '\\')
                    texte.Append('\\').Append(c);
                else if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029)
                    texte.Append(' ');
                else
                    texte.Append(c);
            }
            texte.Append('"');
            return texte.ToString();
        }

        // « joueur=identifiant pseudo="pseudo" » : la forme de Sir Correctifs Torch.
        public static string Joueur(IdentiteDuJoueur joueur)
        {
            var pseudo = string.IsNullOrEmpty(joueur.Pseudo) ? Inconnu : joueur.Pseudo;
            var texte = new StringBuilder();
            texte.Append("joueur=").Append(joueur.IdentifiantSteam.ToString(CultureInfo.InvariantCulture));
            texte.Append(" pseudo=\"");
            foreach (var c in pseudo)
            {
                if (c == '"' || c == '\\')
                    texte.Append('\\').Append(c);
                else if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029)
                    texte.Append(' ');
                else
                    texte.Append(c);
            }
            texte.Append('"');
            return texte.ToString();
        }

        public static string Ligne(DateTime moment, long image, IdentiteDuJoueur joueur, string evenement, ChampsDuReleve champs)
        {
            var ligne = new StringBuilder(160);
            ligne.Append(Heure(moment));
            ligne.Append(" image=").Append(image.ToString(CultureInfo.InvariantCulture));
            ligne.Append(' ').Append(Joueur(joueur));
            ligne.Append(" evenement=").Append(Valeur(evenement));
            if (champs != null)
            {
                foreach (var champ in champs.Liste)
                    ligne.Append(' ').Append(champ.Key).Append('=').Append(Valeur(champ.Value));
            }
            return ligne.ToString();
        }
    }

    // Fabrique les lignes au moment même de l'événement (heure et image du
    // jeu), avec le joueur, et les confie à la sortie (les paquets en route
    // vers le serveur). Appelable de n'importe quel fil ; ne lève jamais.
    public sealed class Releveur
    {
        private readonly Func<DateTime> m_horloge;
        private readonly Func<long> m_image;
        private readonly Func<IdentiteDuJoueur> m_joueur;
        private readonly Action<Suivi, string> m_sortie;

        public Releveur(Func<DateTime> horloge, Func<long> image, Func<IdentiteDuJoueur> joueur, Action<Suivi, string> sortie)
        {
            m_horloge = horloge ?? (() => DateTime.UtcNow);
            m_image = image ?? (() => 0L);
            m_joueur = joueur ?? (() => default(IdentiteDuJoueur));
            m_sortie = sortie ?? ((s, l) => { });
        }

        public DateTime Maintenant
        {
            get
            {
                try
                {
                    return m_horloge();
                }
                catch (Exception)
                {
                    return DateTime.UtcNow;
                }
            }
        }

        // Une faute en fabriquant une ligne : dite par le greffon (rapport
        // d'erreur), jamais levée vers le jeu.
        public Action<Exception> Fautes;

        // Rend la ligne écrite, ou nulle si elle n'a pas pu l'être.
        public string Noter(Suivi suivi, string evenement, ChampsDuReleve champs)
        {
            long image;
            try
            {
                image = m_image();
            }
            catch (Exception e)
            {
                Signaler(e);
                image = 0;
            }
            return NoterA(suivi, Maintenant, image, evenement, champs);
        }

        // Une ligne pour un événement vu plus tôt, sur un autre fil (fils de
        // travail, fil de l'affichage) : elle porte l'heure et l'image de
        // l'événement lui-même, pas celles où le fil du jeu la reçoit.
        public string NoterA(Suivi suivi, DateTime moment, long image, string evenement, ChampsDuReleve champs)
        {
            try
            {
                var ligne = FormeDesReleves.Ligne(moment, image, m_joueur(), evenement, champs);
                m_sortie(suivi, ligne);
                return ligne;
            }
            catch (Exception e)
            {
                Signaler(e);
                return null;
            }
        }

        private void Signaler(Exception e)
        {
            try
            {
                var fautes = Fautes;
                if (fautes != null)
                    fautes(e);
            }
            catch (Exception)
            {
                // Le signalement lui-même ne doit jamais casser le jeu.
            }
        }
    }
}
