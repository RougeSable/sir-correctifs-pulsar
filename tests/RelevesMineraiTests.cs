using System.Collections.Generic;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class RelevesMineraiTests
    {
        private static readonly IdentiteDuJoueur Guesn = new IdentiteDuJoueur("Guesn", 76561198000000001UL);

        private static List<string> Lignes(out SuiviDesMarqueurs suivi)
        {
            var lignes = new List<string>();
            var releveur = new Releveur(() => new System.DateTime(2026, 11, 19, 13, 56, 2, 123, System.DateTimeKind.Utc),
                () => 81234, () => Guesn, (s, l) => lignes.Add(l));
            suivi = new SuiviDesMarqueurs((e, c) => releveur.Noter(Suivi.Minerai, e, c));
            return lignes;
        }

        private static DescriptionDuDetecteur DetecteurDeLaBase()
        {
            return new DescriptionDuDetecteur
            {
                Identifiant = 111,
                Bloc = "Détecteur Sir 3500",
                TypeDeBloc = "LargeOreDetector",
                Grille = "Base de Guesn",
                GrilleId = 222,
                Portee = 3500,
                Antenne = true,
            };
        }

        [Fact]
        public void UnMarqueurApparuOuDisparuPorteMineraiDistanceEtDetecteur()
        {
            List<string> lignes;
            SuiviDesMarqueurs suivi;
            lignes = Lignes(out suivi);
            var detecteur = DetecteurDeLaBase();

            // Un premier passage trouve du magnésium à 300 m du joueur.
            var magnesium = new MarqueurVu("9:1:2:3:Magnésium", "Magnésium", 300, 0, 0);
            Assert.Equal(1, suivi.FinDePassage(7, detecteur, new[] { magnesium }, 0, 0, 0));
            var apparu = lignes[0];
            Assert.Contains("evenement=\"marqueur apparu\"", apparu);
            Assert.Contains("minerai=Magnésium", apparu);
            Assert.Contains("distance=300.0", apparu);
            Assert.Contains("detecteur=bloc", apparu);
            Assert.Contains("bloc=\"Détecteur Sir 3500\"", apparu);
            Assert.Contains("grille=\"Base de Guesn\"", apparu);
            Assert.Contains("portee=3500", apparu);
            Assert.Contains("antenne=oui", apparu);
            Assert.Contains("joueur=76561198000000001 pseudo=\"Guesn\"", apparu);

            // Le passage suivant retrouve le même marqueur : aucune ligne.
            Assert.Equal(0, suivi.FinDePassage(7, detecteur, new[] { magnesium }, 10, 0, 0));
            Assert.Single(lignes);

            // Puis il ne le trouve plus : une ligne de disparition, distance au
            // joueur d'alors.
            Assert.Equal(1, suivi.FinDePassage(7, detecteur, new MarqueurVu[0], 100, 0, 0));
            var disparu = lignes[1];
            Assert.Contains("evenement=\"marqueur disparu\"", disparu);
            Assert.Contains("minerai=Magnésium", disparu);
            Assert.Contains("distance=200.0", disparu);
            Assert.Contains("bloc=\"Détecteur Sir 3500\"", disparu);
            Assert.Contains("grille=\"Base de Guesn\"", disparu);
            Assert.Contains("portee=3500", disparu);
            Assert.Contains("antenne=oui", disparu);
        }

        [Fact]
        public void UnMarqueurDeLaForeusePortativeLeDit()
        {
            List<string> lignes;
            SuiviDesMarqueurs suivi;
            lignes = Lignes(out suivi);
            var foreuse = DescriptionDuDetecteur.ForeusePortative(333, 20);

            suivi.FinDePassage(8, foreuse, new[] { new MarqueurVu("9:0:0:0:Fer", "Fer", 3, 4, 0) }, 0, 0, 0);
            Assert.Contains("minerai=Fer", lignes[0]);
            Assert.Contains("distance=5.0", lignes[0]);
            Assert.Contains("detecteur=\"foreuse portative\"", lignes[0]);
            Assert.Contains("portee=20", lignes[0]);
            Assert.DoesNotContain("antenne=", lignes[0]);
        }

        [Fact]
        public void UnNettoyageFaitDisparaitreChaqueMarqueurAvecSaCause()
        {
            List<string> lignes;
            SuiviDesMarqueurs suivi;
            lignes = Lignes(out suivi);
            var detecteur = DetecteurDeLaBase();
            suivi.FinDePassage(7, detecteur, new[]
            {
                new MarqueurVu("a", "Magnésium", 0, 0, 10),
                new MarqueurVu("b", "Nickel", 0, 0, 20),
            }, 0, 0, 0);
            lignes.Clear();

            Assert.Equal(2, suivi.Retirer(7, detecteur, "perte d'accès", 0, 0, 0));
            Assert.Equal(2, lignes.Count);
            Assert.All(lignes, l => Assert.Contains("evenement=\"marqueur disparu\"", l));
            Assert.All(lignes, l => Assert.Contains("cause=\"perte d'accès\"", l));
            Assert.Equal(0, suivi.Retirer(7, detecteur, "perte d'accès", 0, 0, 0));
        }

        [Fact]
        public void UnPassageDonneUneLigneDeDebutEtUneDeFinAvecPorteeEtDuree()
        {
            var lignes = new List<string>();
            var moment = new System.DateTime(2026, 11, 19, 14, 0, 0, System.DateTimeKind.Utc);
            var releveur = new Releveur(() => moment, () => 1, () => Guesn, (s, l) => lignes.Add(l));
            var passages = new SuiviDesPassages((e, c) => releveur.Noter(Suivi.Minerai, e, c));
            var detecteur = DetecteurDeLaBase();

            passages.Appel(7, "sauté", detecteur);
            passages.Commencer(7, detecteur, 12700, 0, moment);
            passages.Requete(new MesureDeRequete { Groupe = 7, AttenteMs = 1500, TravailMs = 800, ProcesseurMs = 200, Gisements = 3, CellulesVides = 10 });
            passages.Terminer(7, detecteur, moment.AddSeconds(42), 0, 3, 3, 0, 3, false);

            Assert.Equal(2, lignes.Count);
            Assert.Contains("evenement=\"début de passage\"", lignes[0]);
            Assert.Contains("portee=3500", lignes[0]);
            Assert.Contains("cellules=12700", lignes[0]);
            Assert.Contains("appels_sautes=1", lignes[0]);
            Assert.Contains("evenement=\"fin de passage\"", lignes[1]);
            Assert.Contains("portee=3500", lignes[1]);
            Assert.Contains("duree_ms=42000", lignes[1]);
            Assert.Contains("attente_file_max_ms=1500.0", lignes[1]);
            Assert.Contains("processeur_ms=200.0", lignes[1]);
            Assert.Contains("apparus=3", lignes[1]);
        }
    }
}
