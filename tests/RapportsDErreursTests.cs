using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class RapportsDErreursTests
    {
        [Fact]
        public void LeCanalDesRapportsEstLeMemePourTousLesGreffons()
        {
            Assert.Equal(5465479001L, CanalDesRapports.Numero);
            Assert.Equal((ushort)38745, CanalDesRapports.CanalDuJeu);
            Assert.True(CanalDesRapports.EstUnDeNosServeurs(true, false, true, "[FR] Sirius Immersion - PTU", null));
            Assert.False(CanalDesRapports.EstUnDeNosServeurs(true, false, true, "Un autre serveur", "Monde"));
            Assert.False(CanalDesRapports.EstUnDeNosServeurs(false, true, false, null, "Sirius solo"));
        }

        [Fact]
        public void UnRapportAToutesLesClesDeLaRegle()
        {
            var json = new RapportDErreur
            {
                Greffon = "Sir Correctifs Pulsar",
                Version = "2.0.0.0",
                Message = "faute \"x\"",
                Premiere = new DateTime(2026, 11, 19, 13, 56, 2, 123, DateTimeKind.Utc),
                Derniere = new DateTime(2026, 11, 19, 13, 57, 0, 0, DateTimeKind.Utc),
                Session = new DateTime(2026, 11, 19, 13, 0, 0, 0, DateTimeKind.Utc),
                Lignes = new List<string> { "une" },
            }.Json();
            foreach (var cle in new[] { "greffon", "version", "genre", "message", "pile", "premiere", "derniere", "nombre", "session", "lignes" })
                Assert.Contains("\"" + cle + "\":", json);
            Assert.Contains("\"premiere\":\"2026-11-19T13:56:02.123Z\"", json);
            Assert.Contains("\"message\":\"faute \\\"x\\\"\"", json);
        }

        [Fact]
        public void AucuneDonneeDuJoueurNEntreDansUnRapport()
        {
            var nettoyeur = new NettoyeurDeRapports(@"C:\Users\Guesn", new[] { "Guesn", "76561198000000001" });
            Assert.Equal(@"%USERPROFILE%\AppData\Roaming", nettoyeur.Ligne(@"C:\Users\Guesn\AppData\Roaming"));
            Assert.Null(nettoyeur.Ligne("joueur 76561198000000001 connecté"));
            Assert.Null(nettoyeur.Ligne("Guesn a rejoint"));
            Assert.Null(nettoyeur.Ligne("Joining server: Name:[FR] Sirius Immersion - PTU"));
            Assert.Null(nettoyeur.Ligne("adresse 192.168.1.20:27016"));
            Assert.Equal("accroche posée", nettoyeur.Ligne("accroche posée"));
        }

        [Fact]
        public void LaMemeErreurNePartQuUneFoisAvecSonNombre()
        {
            var dossier = Path.Combine(Path.GetTempPath(), "sir-correctifs-pulsar-" + Guid.NewGuid().ToString("N"));
            try
            {
                var maintenant = new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc);
                var rapporteur = new RapporteurDErreurs("Sir Correctifs Pulsar", "2.0.0.0", maintenant.AddHours(-1),
                    new FileDesRapports(dossier), new NettoyeurDeRapports(null, null), () => maintenant);
                rapporteur.Noter("faute dans « fin de passage »", "à Truc.Machin()");
                rapporteur.Noter("faute dans « fin de passage »", "à Truc.Machin()");
                rapporteur.Noter("faute dans « fin de passage »", "à Truc.Machin()");

                var envoyes = new List<string>();
                Assert.Equal(0, rapporteur.Tour(true, o => { envoyes.Add(Encoding.UTF8.GetString(o)); return true; }));
                maintenant = maintenant.AddSeconds(31);
                Assert.Equal(1, rapporteur.Tour(true, o => { envoyes.Add(Encoding.UTF8.GetString(o)); return true; }));
                Assert.Single(envoyes);
                Assert.Contains("\"nombre\":3", envoyes[0]);

                rapporteur.Noter("faute dans « fin de passage »", "à Truc.Machin()");
                maintenant = maintenant.AddMinutes(5);
                Assert.Equal(0, rapporteur.Tour(true, o => { envoyes.Add(Encoding.UTF8.GetString(o)); return true; }));
            }
            finally
            {
                if (Directory.Exists(dossier))
                    Directory.Delete(dossier, true);
            }
        }

        [Fact]
        public void UnPlantageSeReconnaitALaMarqueRestee()
        {
            var dossier = Path.Combine(Path.GetTempPath(), "sir-correctifs-pulsar-" + Guid.NewGuid().ToString("N"));
            try
            {
                var debut = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
                Assert.Null(SurveillanceDesPlantages.Demarrer(dossier, null, null, debut, "Sir Correctifs Pulsar", "2", "sir-correctifs-pulsar", null));
                var rapport = SurveillanceDesPlantages.Demarrer(dossier, null, null, debut.AddHours(1), "Sir Correctifs Pulsar", "2", "sir-correctifs-pulsar", null);
                Assert.NotNull(rapport);
                Assert.Equal(RapportDErreur.GenrePlantage, rapport.Genre);
                SurveillanceDesPlantages.Terminer(dossier);
                Assert.Null(SurveillanceDesPlantages.Demarrer(dossier, null, null, debut.AddHours(2), "Sir Correctifs Pulsar", "2", "sir-correctifs-pulsar", null));
            }
            finally
            {
                if (Directory.Exists(dossier))
                    Directory.Delete(dossier, true);
            }
        }

        [Fact]
        public void UnPlantageReprendLeBlocDuJeuEtLesLignesDuGreffon()
        {
            // Un journal de jeu écrit pour ce test, dans la forme du jeu (en-têtes
            // de ligne, fuseau, bloc de plantage) ; l'exception et sa pile sont
            // telles qu'un Windows français les écrit.
            var journal = new[]
            {
                "2026-11-19 13:00:00.000 - Thread:   1 ->  Timezone (local - UTC): 1h",
                "2026-11-19 13:00:01.000 - Thread:   1 ->  [sir-correctifs-pulsar] 18 accroches posées sur 18",
                "2026-11-19 13:05:00.000 - Thread:   1 ->  UnhandledException : System.NullReferenceException : la référence d'objet est nulle",
                "   à Sandbox.Game.Truc.Machin()",
                "2026-11-19 13:05:00.100 - Thread:   1 ->  ================================== CRASH INFO ==================================",
                "2026-11-19 13:05:00.100 - Thread:   1 ->  ================================== OFNI HSARC ==================================",
            };
            var rapport = SurveillanceDesPlantages.Construire(new SurveillanceDesPlantages.SessionPrecedente(), journal, DateTime.UtcNow,
                "Sir Correctifs Pulsar", "2", "sir-correctifs-pulsar", null);
            Assert.Contains("NullReferenceException", rapport.Message);
            Assert.Contains("à Sandbox.Game.Truc.Machin()", rapport.Pile);
            Assert.Contains(rapport.Lignes, l => l.Contains("CRASH INFO") && l.Contains("====="));
            Assert.Contains(rapport.Lignes, l => l.Contains("[sir-correctifs-pulsar] 18 accroches"));
            Assert.Equal(new DateTime(2026, 11, 19, 12, 5, 0, 100, DateTimeKind.Utc), rapport.Premiere);
        }
    }
}
