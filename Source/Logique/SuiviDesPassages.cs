using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Ce qu'une requête du détecteur a coûté sur son fil de travail.
    public struct MesureDeRequete
    {
        public long Groupe;
        public double AttenteMs;
        public double TravailMs;
        // Le temps processeur du fil pendant le travail, ou -1 s'il est inconnu.
        public double ProcesseurMs;
        public int Gisements;
        public int CellulesVides;
        public bool Annulee;
    }

    // Les passages de chaque groupe (un détecteur sur un voxel). Le jeu du
    // joueur lance quatre requêtes par passage, en priorité « très basse » ;
    // les marqueurs ne s'enregistrent qu'à la fin d'un passage complet. Une
    // ligne au début, une à la fin (durée, portée, marqueurs), une quand le
    // jeu jette une requête ou nettoie le groupe.
    //
    // Fil du jeu seulement.
    public sealed class SuiviDesPassages
    {
        public const string Debut = "début de passage";
        public const string Fin = "fin de passage";
        public const string RequeteJetee = "requête jetée";
        public const string Nettoyage = "nettoyage des marqueurs";
        public const string EnAttente = "passage en attente";

        private sealed class Passage
        {
            public DateTime? Debut;
            public int AppelsOccupe;
            public int AppelsSautes;
            public int Requetes;
            public double AttenteMaxMs;
            public double TravailMs;
            public double ProcesseurMs;
            public bool ProcesseurConnu = true;
            public int Gisements;
            public int CellulesVides;
            public int Annulees;
            public string Attente;
        }

        private readonly Dictionary<long, Passage> m_passages = new Dictionary<long, Passage>();
        private readonly Action<string, ChampsDuReleve> m_noter;

        public SuiviDesPassages(Action<string, ChampsDuReleve> noter)
        {
            m_noter = noter ?? ((e, c) => { });
        }

        public int EnCours
        {
            get
            {
                var n = 0;
                foreach (var passage in m_passages.Values)
                {
                    if (passage.Debut.HasValue)
                        n++;
                }
                return n;
            }
        }

        private Passage De(long groupe)
        {
            Passage passage;
            if (!m_passages.TryGetValue(groupe, out passage))
            {
                passage = new Passage();
                m_passages[groupe] = passage;
            }
            return passage;
        }

        // Un appel du jeu qui n'a pas lancé de passage : « occupé » (un passage
        // tourne encore), « sauté » (rien n'a bougé), « session pas prête ».
        // Seule l'attente de la session fait une ligne, à son début ; les deux
        // autres se comptent dans la ligne du passage suivant.
        public void Appel(long groupe, string issue, DescriptionDuDetecteur detecteur)
        {
            var passage = De(groupe);
            if (issue == "occupé")
                passage.AppelsOccupe++;
            else if (issue == "sauté")
                passage.AppelsSautes++;
            else if (passage.Attente != issue)
            {
                passage.Attente = issue;
                var champs = new ChampsDuReleve();
                champs.Texte("raison", issue);
                Decrire(champs, detecteur);
                m_noter(EnAttente, champs);
            }
        }

        public void Commencer(long groupe, DescriptionDuDetecteur detecteur, long cellules, int marqueurs, DateTime maintenant)
        {
            var passage = De(groupe);
            var champs = new ChampsDuReleve();
            Decrire(champs, detecteur);
            champs.Entier("cellules", cellules);
            champs.Entier("marqueurs", marqueurs);
            champs.Entier("appels_occupe", passage.AppelsOccupe);
            champs.Entier("appels_sautes", passage.AppelsSautes);
            m_noter(Debut, champs);

            m_passages[groupe] = new Passage { Debut = maintenant };
        }

        public void Requete(MesureDeRequete mesure)
        {
            var passage = De(mesure.Groupe);
            passage.Requetes++;
            passage.AttenteMaxMs = Math.Max(passage.AttenteMaxMs, mesure.AttenteMs);
            passage.TravailMs += mesure.TravailMs;
            if (mesure.ProcesseurMs < 0)
                passage.ProcesseurConnu = false;
            else
                passage.ProcesseurMs += mesure.ProcesseurMs;
            passage.Gisements += mesure.Gisements;
            passage.CellulesVides += mesure.CellulesVides;
            if (mesure.Annulee)
                passage.Annulees++;
        }

        // La fin d'un passage complet : sa durée, ce que ses requêtes ont
        // coûté, et les marqueurs avant et après.
        public void Terminer(long groupe, DescriptionDuDetecteur detecteur, DateTime maintenant,
            int marqueursAvant, int marqueursApres, int apparus, int disparus, int marqueursDuHud, bool resultatJete)
        {
            var passage = De(groupe);
            var champs = new ChampsDuReleve();
            Decrire(champs, detecteur);
            if (passage.Debut.HasValue)
                champs.Entier("duree_ms", (long)(maintenant - passage.Debut.Value).TotalMilliseconds);
            else
                champs.Texte("duree_ms", FormeDesReleves.Inconnu);
            champs.Entier("requetes", passage.Requetes);
            champs.Nombre("attente_file_max_ms", passage.AttenteMaxMs, 1);
            champs.Nombre("travail_ms", passage.TravailMs, 1);
            if (passage.ProcesseurConnu && passage.Requetes > 0)
                champs.Nombre("processeur_ms", passage.ProcesseurMs, 1);
            champs.Entier("gisements", passage.Gisements);
            champs.Entier("cellules_vides", passage.CellulesVides);
            if (passage.Annulees > 0)
                champs.Entier("requetes_annulees", passage.Annulees);
            champs.Entier("marqueurs_avant", marqueursAvant);
            champs.Entier("marqueurs_apres", marqueursApres);
            champs.Entier("apparus", apparus);
            champs.Entier("disparus", disparus);
            champs.Entier("marqueurs_du_hud", marqueursDuHud);
            if (resultatJete)
                champs.OuiNon("resultat_jete", true);
            m_noter(Fin, champs);

            m_passages[groupe] = new Passage();
        }

        // Le rappel d'une requête est arrivé sur un groupe déjà libéré.
        public void Jetee(long groupe, DescriptionDuDetecteur detecteur, int requetesRestantes)
        {
            var champs = new ChampsDuReleve();
            Decrire(champs, detecteur);
            champs.Entier("requetes_restantes", requetesRestantes);
            m_noter(RequeteJetee, champs);
        }

        // Le jeu retire d'un coup les marqueurs du groupe, sans annuler les
        // requêtes en cours (orphelines).
        public void Nettoyer(long groupe, DescriptionDuDetecteur detecteur, string cause, int requetesOrphelines, int marqueursRetires)
        {
            var champs = new ChampsDuReleve();
            Decrire(champs, detecteur);
            champs.Texte("cause", cause ?? FormeDesReleves.Inconnu);
            champs.Entier("requetes_orphelines", requetesOrphelines);
            champs.Entier("marqueurs_retires", marqueursRetires);
            Passage passage;
            if (m_passages.TryGetValue(groupe, out passage) && passage.Debut.HasValue)
                champs.OuiNon("passage_interrompu", true);
            m_noter(Nettoyage, champs);
            m_passages.Remove(groupe);
        }

        public void Oublier()
        {
            m_passages.Clear();
        }

        private static void Decrire(ChampsDuReleve champs, DescriptionDuDetecteur detecteur)
        {
            if (detecteur != null)
                detecteur.Decrire(champs);
            else
                champs.Texte("detecteur", FormeDesReleves.Inconnu);
        }
    }
}
