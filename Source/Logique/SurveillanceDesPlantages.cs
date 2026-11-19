using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SirCorrectifsPulsar
{
    // Comment un plantage se reconnaît : une marque est posée dans le dossier
    // du greffon au début d'une session de jeu et effacée à sa sortie propre.
    // Une marque trouvée au démarrage suivant veut dire que cette session ne
    // s'est jamais terminée. Le greffon relit alors le journal du jeu de cette
    // session et construit un rapport « plantage » : le bloc CRASH INFO du jeu,
    // l'exception qui l'a fait tomber avec sa pile, les 20 dernières lignes
    // d'erreur du jeu avant la coupure et les 20 dernières lignes du greffon.
    public static class SurveillanceDesPlantages
    {
        public const string NomDeLaMarque = "session.marque";
        public const string JournauxDuJeu = "SpaceEngineers_*.log";
        public const int DernieresLignes = 20;
        public const int LignesDePileMaximum = 80;
        public const int LignesDuBlocMaximum = 40;

        public const string AucuneException = "La session de jeu s'est terminée sans sortie propre (aucune exception dans le journal du jeu).";
        public const string TitreDuBloc = "--- CRASH INFO ---";
        public const string TitreDesErreursDuJeu = "--- dernières lignes d'erreur du jeu ---";
        public const string TitreDesLignesDuGreffon = "--- dernières lignes du greffon ---";

        public sealed class SessionPrecedente
        {
            public DateTime DebutUtc;
            public string Journal;
        }

        // Rend la session restée inachevée, s'il y en a une, puis pose la
        // marque de celle-ci.
        public static SessionPrecedente Commencer(string dossierDuGreffon, DateTime debutUtc, string journal)
        {
            var chemin = Path.Combine(dossierDuGreffon, NomDeLaMarque);
            SessionPrecedente precedente = null;
            try
            {
                if (File.Exists(chemin))
                    precedente = LireLaMarque(File.ReadAllLines(chemin));
            }
            catch (Exception)
            {
                precedente = new SessionPrecedente();
            }

            try
            {
                Directory.CreateDirectory(dossierDuGreffon);
                File.WriteAllText(chemin, RapportDErreur.HeureIso(debutUtc) + "\n" + (journal ?? "") + "\n", new UTF8Encoding(false));
            }
            catch (Exception)
            {
            }
            return precedente;
        }

        // La sortie propre du jeu.
        public static void Terminer(string dossierDuGreffon)
        {
            try
            {
                var chemin = Path.Combine(dossierDuGreffon, NomDeLaMarque);
                if (File.Exists(chemin))
                    File.Delete(chemin);
            }
            catch (Exception)
            {
            }
        }

        private static SessionPrecedente LireLaMarque(string[] lignes)
        {
            var precedente = new SessionPrecedente();
            if (lignes.Length > 0)
            {
                DateTime debut;
                if (DateTime.TryParse(lignes[0].Trim(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out debut))
                    precedente.DebutUtc = DateTime.SpecifyKind(debut, DateTimeKind.Utc);
            }
            if (lignes.Length > 1 && lignes[1].Trim().Length > 0)
                precedente.Journal = lignes[1].Trim();
            return precedente;
        }

        // Le journal de la session plantée : celui de la marque, sinon le
        // dernier SpaceEngineers_*.log du dossier autre que l'actuel.
        public static string TrouverLeJournal(SessionPrecedente precedente, string dossierDesJournaux, string journalActuel)
        {
            try
            {
                if (precedente != null && !string.IsNullOrEmpty(precedente.Journal) && File.Exists(precedente.Journal)
                    && !MemeChemin(precedente.Journal, journalActuel))
                    return precedente.Journal;

                if (string.IsNullOrEmpty(dossierDesJournaux) || !Directory.Exists(dossierDesJournaux))
                    return null;

                var journaux = new List<string>(Directory.GetFiles(dossierDesJournaux, JournauxDuJeu));
                journaux.Sort(StringComparer.OrdinalIgnoreCase);
                for (var i = journaux.Count - 1; i >= 0; i--)
                {
                    if (!MemeChemin(journaux[i], journalActuel))
                        return journaux[i];
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static bool MemeChemin(string a, string b)
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

        // Les lignes d'un journal que le jeu peut encore tenir ouvert.
        public static IEnumerable<string> LireLesLignes(string chemin)
        {
            using (var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var lecteur = new StreamReader(flux, Encoding.UTF8, true))
            {
                string ligne;
                while ((ligne = lecteur.ReadLine()) != null)
                    yield return ligne;
            }
        }

        // Tout le début d'une session : le rapport de plantage si la précédente
        // ne s'est jamais terminée (sinon nul), puis la marque de celle-ci.
        public static RapportDErreur Demarrer(string dossierDuGreffon, string dossierDesJournaux, string journalActuel,
            DateTime debutUtc, string greffon, string version, string etiquette, NettoyeurDeRapports nettoyeur)
        {
            var precedente = Commencer(dossierDuGreffon, debutUtc, journalActuel);
            if (precedente == null)
                return null;

            try
            {
                var journal = TrouverLeJournal(precedente, dossierDesJournaux, journalActuel);
                IEnumerable<string> lignes = journal == null ? new string[0] : LireLesLignes(journal);
                var derniereEcriture = journal == null ? debutUtc : File.GetLastWriteTimeUtc(journal);
                return Construire(precedente, lignes, derniereEcriture, greffon, version, etiquette, nettoyeur);
            }
            catch (Exception)
            {
                return Construire(precedente, new string[0], debutUtc, greffon, version, etiquette, nettoyeur);
            }
        }

        // 2026-11-15 18:54:02.118 - Thread:   1 ->  texte
        private static readonly Regex Horodatee = new Regex(
            @"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) - Thread:\s*\d+\s*->\s*(.*)$",
            RegexOptions.CultureInvariant);

        private static readonly Regex Fuseau = new Regex(@"Timezone \(local - UTC\):\s*(-?\d+)h", RegexOptions.CultureInvariant);

        private static readonly Regex MotsDErreur = new Regex(
            @"\b(?:e[r]ror|e[r]rors|exception|critical|fatal|fai[l]ed|failure)\b|E[R]ROR:|ERREUR",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex LigneDException = new Regex(
            @"Exception occur+ed|Unhandled exception|UnhandledException",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Continue = new Regex(@"application\sis\scontinuing", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private sealed class BlocDException
        {
            public string Message;
            public readonly List<string> Pile = new List<string>();
            public bool Continue;
        }

        // Lit une seule fois le journal de la session plantée et ne garde que
        // ce dont le rapport a besoin.
        public static RapportDErreur Construire(SessionPrecedente precedente, IEnumerable<string> lignesDuJournal, DateTime derniereEcritureUtc,
            string greffon, string version, string etiquette, NettoyeurDeRapports nettoyeur)
        {
            nettoyeur = nettoyeur ?? new NettoyeurDeRapports(null, null);
            var marque = "[" + etiquette + "]";

            var erreursDuJeu = new Queue<string>();
            var lignesDuGreffon = new Queue<string>();
            List<string> bloc = null;
            List<string> enCapture = null;
            BlocDException courant = null;
            BlocDException fatale = null;
            BlocDException derniereGrave = null;
            var fuseau = 0;
            DateTime? dernierHorodatage = null;

            foreach (var brute in lignesDuJournal ?? new string[0])
            {
                var ligne = brute ?? "";
                var horodatee = Horodatee.Match(ligne);
                if (!horodatee.Success)
                {
                    if (courant != null && courant.Pile.Count < LignesDePileMaximum && ligne.Trim().Length > 0)
                        courant.Pile.Add(ligne);
                    continue;
                }

                DateTime moment;
                if (DateTime.TryParseExact(horodatee.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out moment))
                    dernierHorodatage = moment;

                var texte = horodatee.Groups[2].Value;
                courant = null;

                var zone = Fuseau.Match(texte);
                if (zone.Success)
                    int.TryParse(zone.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out fuseau);

                if (enCapture != null)
                {
                    enCapture.Add(ligne);
                    if (texte.IndexOf("OFNI HSARC", StringComparison.Ordinal) >= 0 || enCapture.Count >= LignesDuBlocMaximum)
                        enCapture = null;
                    continue;
                }

                if (texte.IndexOf("CRASH INFO", StringComparison.Ordinal) >= 0)
                {
                    bloc = new List<string> { ligne };
                    enCapture = bloc;
                    fatale = derniereGrave;
                    continue;
                }

                if (texte.StartsWith(marque, StringComparison.Ordinal))
                {
                    Garder(lignesDuGreffon, ligne);
                    continue;
                }

                if (LigneDException.IsMatch(texte))
                {
                    courant = new BlocDException { Message = texte.Trim(), Continue = Continue.IsMatch(texte) };
                    if (!courant.Continue || derniereGrave == null || derniereGrave.Continue)
                        derniereGrave = courant;
                }

                if (MotsDErreur.IsMatch(texte))
                    Garder(erreursDuJeu, ligne);
            }

            if (fatale == null)
                fatale = derniereGrave;

            var heureDuPlantage = dernierHorodatage.HasValue
                ? DateTime.SpecifyKind(dernierHorodatage.Value.AddHours(-fuseau), DateTimeKind.Utc)
                : DateTime.SpecifyKind(derniereEcritureUtc, DateTimeKind.Utc);

            var rapport = new RapportDErreur
            {
                Greffon = greffon ?? "",
                Version = version ?? "",
                Genre = RapportDErreur.GenrePlantage,
                Premiere = heureDuPlantage,
                Derniere = heureDuPlantage,
                Nombre = 1,
                Session = precedente != null && precedente.DebutUtc != default(DateTime) ? precedente.DebutUtc : heureDuPlantage,
            };

            var message = fatale == null ? "" : nettoyeur.Texte(fatale.Message);
            rapport.Message = message.Length > 0 ? message : (fatale == null ? AucuneException : RapporteurDErreurs.MessageRetire);
            rapport.Pile = fatale == null ? "" : string.Join("\n", nettoyeur.Lignes(fatale.Pile).ToArray());

            rapport.Lignes.Add(TitreDuBloc);
            rapport.Lignes.AddRange(nettoyeur.Lignes(bloc ?? new List<string>()));
            rapport.Lignes.Add(TitreDesErreursDuJeu);
            rapport.Lignes.AddRange(DernieresNettes(erreursDuJeu, nettoyeur));
            rapport.Lignes.Add(TitreDesLignesDuGreffon);
            rapport.Lignes.AddRange(DernieresNettes(lignesDuGreffon, nettoyeur));
            return rapport;
        }

        private static void Garder(Queue<string> lignes, string ligne)
        {
            lignes.Enqueue(ligne);
            while (lignes.Count > DernieresLignes * 4)
                lignes.Dequeue();
        }

        private static List<string> DernieresNettes(IEnumerable<string> lignes, NettoyeurDeRapports nettoyeur)
        {
            var nettes = nettoyeur.Lignes(lignes);
            if (nettes.Count > DernieresLignes)
                nettes.RemoveRange(0, nettes.Count - DernieresLignes);
            return nettes;
        }
    }
}
