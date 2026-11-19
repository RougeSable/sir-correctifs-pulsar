using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SirCorrectifsPulsar
{
    // Rassemble les erreurs du greffon pour cette session de jeu en rapports
    // « erreur ». Chaque exception rattrapée et chaque ligne d'erreur que le
    // greffon écrit au journal du jeu arrive ici. La même erreur (même message,
    // même haut de pile) donne un seul rapport par session, avec son nombre de
    // répétitions et les heures de la première et de la dernière.
    //
    // Noter est rapide et ne touche aucun fichier : appelable de n'importe quel
    // fil. Tour, sur le fil du jeu, écrit les rapports dans la file et envoie
    // ce qui est prêt tant que le joueur est sur l'un de nos serveurs.
    public sealed class RapporteurDErreurs
    {
        // Une erreur attend ce délai avant de partir, pour compter ses répétitions.
        public static readonly TimeSpan DelaiDeRepos = TimeSpan.FromSeconds(30);

        // Les dernières lignes du greffon, jointes à chaque rapport.
        public const int LignesDeContexte = 20;

        // Erreurs distinctes gardées par session.
        public const int ErreursMaximum = 100;

        public const string MessageRetire = "(ligne retirée : elle portait des données du joueur)";

        private sealed class Entree
        {
            public string Fichier;
            public string Message;
            public string Pile;
            public DateTime Premiere;
            public DateTime Derniere;
            public int Nombre;
            public List<string> Lignes;
            public bool Modifiee;
            public bool Envoyee;
        }

        private readonly object m_verrou = new object();
        private readonly string m_greffon;
        private readonly string m_version;
        private readonly DateTime m_session;
        private readonly string m_prefixe;
        private readonly FileDesRapports m_file;
        private readonly NettoyeurDeRapports m_nettoyeur;
        private readonly Func<DateTime> m_horloge;
        private readonly Dictionary<string, Entree> m_entrees = new Dictionary<string, Entree>();
        private readonly Dictionary<string, Entree> m_parFichier = new Dictionary<string, Entree>();
        private readonly Queue<string> m_recentes = new Queue<string>();

        public RapporteurDErreurs(string greffon, string version, DateTime sessionUtc,
            FileDesRapports file, NettoyeurDeRapports nettoyeur, Func<DateTime> horloge)
        {
            m_greffon = greffon ?? "";
            m_version = version ?? "";
            m_session = sessionUtc;
            m_prefixe = PrefixeDeSession(sessionUtc);
            m_file = file;
            m_nettoyeur = nettoyeur ?? new NettoyeurDeRapports(null, null);
            m_horloge = horloge ?? (() => DateTime.UtcNow);
        }

        public NettoyeurDeRapports Nettoyeur
        {
            get { return m_nettoyeur; }
        }

        public DateTime Session
        {
            get { return m_session; }
        }

        // Une ligne du greffon au journal du jeu, gardée nettoyée comme contexte.
        public void Retenir(string ligne)
        {
            try
            {
                foreach (var partie in NettoyeurDeRapports.Decouper(ligne))
                {
                    var nette = m_nettoyeur.Ligne(partie);
                    if (string.IsNullOrEmpty(nette))
                        continue;
                    lock (m_verrou)
                    {
                        m_recentes.Enqueue(nette);
                        while (m_recentes.Count > LignesDeContexte)
                            m_recentes.Dequeue();
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        public void Noter(string contexte, Exception erreur)
        {
            if (erreur == null)
            {
                Noter(contexte, (string)null);
                return;
            }
            string message, pile;
            Decrire(contexte, erreur, out message, out pile);
            Noter(message, pile);
        }

        // Ne lève jamais : une faute ici ne doit pas s'ajouter à celle qu'on rapporte.
        public void Noter(string message, string pile)
        {
            try
            {
                var maintenant = m_horloge();
                var messageNet = PremiereLigne(m_nettoyeur.Texte(message));
                if (messageNet.Length == 0)
                    messageNet = MessageRetire;
                var pileNette = m_nettoyeur.Texte(pile);
                var cle = messageNet + "\n" + HautDePile(pileNette);

                lock (m_verrou)
                {
                    Entree entree;
                    if (m_entrees.TryGetValue(cle, out entree))
                    {
                        entree.Nombre++;
                        entree.Derniere = maintenant;
                        if (!entree.Envoyee)
                            entree.Modifiee = true;
                        return;
                    }

                    if (m_entrees.Count >= ErreursMaximum)
                        return;

                    entree = new Entree
                    {
                        Fichier = m_prefixe + "-" + RapportDErreur.GenreErreur + "-" + Empreinte(cle) + FileDesRapports.Extension,
                        Message = messageNet,
                        Pile = pileNette,
                        Premiere = maintenant,
                        Derniere = maintenant,
                        Nombre = 1,
                        Lignes = new List<string>(m_recentes),
                        Modifiee = true,
                    };
                    m_entrees[cle] = entree;
                    m_parFichier[entree.Fichier] = entree;
                }
            }
            catch (Exception)
            {
            }
        }

        // Un rapport fait ailleurs (le plantage de la session précédente).
        public bool MettreEnFile(RapportDErreur rapport)
        {
            try
            {
                var nom = PrefixeDeSession(rapport.Session) + "-" + rapport.Genre + "-"
                    + Empreinte(rapport.Message + "\n" + rapport.Pile) + FileDesRapports.Extension;
                return m_file.Poser(nom, rapport.Utf8());
            }
            catch (Exception)
            {
                return false;
            }
        }

        public List<RapportDErreur> EnAttente()
        {
            var rapports = new List<RapportDErreur>();
            lock (m_verrou)
            {
                foreach (var entree in m_entrees.Values)
                {
                    if (!entree.Envoyee)
                        rapports.Add(Rapport(entree));
                }
            }
            return rapports;
        }

        // Fil du jeu, environ une fois par seconde. Rend le nombre de rapports partis.
        public int Tour(bool surNosServeurs, Func<byte[], bool> envoi)
        {
            try
            {
                Ecrire();
                if (!surNosServeurs)
                    return 0;

                var envoyes = m_file.Envoyer(true, Pret, envoi);
                lock (m_verrou)
                {
                    foreach (var nom in envoyes)
                    {
                        Entree entree;
                        if (m_parFichier.TryGetValue(nom, out entree))
                        {
                            entree.Envoyee = true;
                            entree.Modifiee = false;
                        }
                    }
                }
                return envoyes.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Écrit chaque rapport modifié dans la file : à chaque tour et à la
        // sortie propre du jeu.
        public void Ecrire()
        {
            try
            {
                var aEcrire = new List<KeyValuePair<string, RapportDErreur>>();
                lock (m_verrou)
                {
                    foreach (var entree in m_entrees.Values)
                    {
                        if (!entree.Modifiee || entree.Envoyee)
                            continue;
                        entree.Modifiee = false;
                        aEcrire.Add(new KeyValuePair<string, RapportDErreur>(entree.Fichier, Rapport(entree)));
                    }
                }

                foreach (var element in aEcrire)
                {
                    if (!m_file.Poser(element.Key, element.Value.Utf8()))
                    {
                        lock (m_verrou)
                        {
                            Entree entree;
                            if (m_parFichier.TryGetValue(element.Key, out entree) && !entree.Envoyee)
                                entree.Modifiee = true;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        // Les rapports des sessions précédentes partent aussitôt ; ceux de cette
        // session une fois le délai de repos écoulé.
        private bool Pret(string nom)
        {
            if (!nom.StartsWith(m_prefixe, StringComparison.Ordinal))
                return true;
            lock (m_verrou)
            {
                Entree entree;
                if (!m_parFichier.TryGetValue(nom, out entree))
                    return true;
                return m_horloge() - entree.Premiere >= DelaiDeRepos;
            }
        }

        private RapportDErreur Rapport(Entree entree)
        {
            return new RapportDErreur
            {
                Greffon = m_greffon,
                Version = m_version,
                Genre = RapportDErreur.GenreErreur,
                Message = entree.Message,
                Pile = entree.Pile,
                Premiere = entree.Premiere,
                Derniere = entree.Derniere,
                Nombre = entree.Nombre,
                Session = m_session,
                Lignes = new List<string>(entree.Lignes),
            };
        }

        // « contexte : Type : message » pour le message, le reste de
        // l'exception (exceptions internes, cadres) pour la pile.
        public static void Decrire(string contexte, Exception erreur, out string message, out string pile)
        {
            var lignes = NettoyeurDeRapports.Decouper(erreur.ToString());
            var tete = lignes.Length > 0 ? lignes[0] : erreur.GetType().FullName;
            message = string.IsNullOrEmpty(contexte) ? tete : contexte + " : " + tete;

            var reste = new StringBuilder();
            for (var i = 1; i < lignes.Length; i++)
            {
                if (lignes[i].Length == 0)
                    continue;
                if (reste.Length > 0)
                    reste.Append('\n');
                reste.Append(lignes[i]);
            }
            pile = reste.ToString();
        }

        // Le premier cadre d'une pile : ce qui distingue deux erreurs de même message.
        public static string HautDePile(string pile)
        {
            string premiere = null;
            foreach (var ligne in NettoyeurDeRapports.Decouper(pile))
            {
                var nette = ligne.Trim();
                if (nette.Length == 0)
                    continue;
                if (nette.StartsWith("at ", StringComparison.Ordinal) || nette.StartsWith("à ", StringComparison.Ordinal))
                    return nette;
                if (premiere == null)
                    premiere = nette;
            }
            return premiere ?? "";
        }

        public static string PrefixeDeSession(DateTime sessionUtc)
        {
            var utc = sessionUtc.Kind == DateTimeKind.Local ? sessionUtc.ToUniversalTime() : sessionUtc;
            return utc.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        }

        private static string PremiereLigne(string texte)
        {
            foreach (var ligne in NettoyeurDeRapports.Decouper(texte))
            {
                if (ligne.Trim().Length > 0)
                    return ligne.Trim();
            }
            return "";
        }

        // FNV-1a 64 : le même d'un lancement à l'autre.
        public static string Empreinte(string texte)
        {
            unchecked
            {
                var empreinte = 14695981039346656037UL;
                foreach (var c in texte ?? "")
                {
                    empreinte ^= c;
                    empreinte *= 1099511628211UL;
                }
                return empreinte.ToString("x16", CultureInfo.InvariantCulture);
            }
        }
    }
}
