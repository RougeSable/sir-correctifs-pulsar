using System.Collections.Generic;
using System.Reflection;
using Sandbox.Game.Entities;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class PointDAccrocheTests
    {
        private const BindingFlags Toutes = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void RefuseUneMethodeInterditeEtLEcritAuJournal()
        {
            var lignes = new List<string>();
            var journal = new JournalDuGreffon("sir-correctifs-pulsar", lignes.Add, null);
            var methode = typeof(MyParallelEntityUpdateOrchestrator).GetMethod("PerformParallelUpdate", Toutes);
            var posee = false;

            var resultat = PointDAccroche.Accrocher(methode, "chronomètre des mises à jour en parallèle", () => posee = true, journal);

            // Rien n'est posé.
            Assert.False(resultat);
            Assert.False(posee);

            // Le refus est écrit au journal du jeu, en ligne d'erreur, avec la
            // méthode et sa raison.
            var ligne = Assert.Single(lignes);
            Assert.StartsWith("[sir-correctifs-pulsar] " + JournalDuGreffon.MarqueDErreur + PointDAccroche.MarqueDuRefus, ligne);
            Assert.Contains("« chronomètre des mises à jour en parallèle »", ligne);
            Assert.Contains("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator.PerformParallelUpdate", ligne);
            Assert.Contains(RegistreDesMethodesInterdites.RaisonDesVaisseauxQuiMontent, ligne);
        }

        [Fact]
        public void RefuseAussiLaMethodeInterditeVueDepuisUnTypeDerive()
        {
            var lignes = new List<string>();
            var journal = new JournalDuGreffon("sir-correctifs-pulsar", lignes.Add, null);
            var methode = typeof(OrchestrateurDerive).GetMethod("PerformParallelUpdate", Toutes | BindingFlags.DeclaredOnly);
            Assert.Equal(typeof(OrchestrateurDerive), methode.DeclaringType);
            var posee = false;

            Assert.False(PointDAccroche.Accrocher(methode, "chronomètre", () => posee = true, journal));
            Assert.False(posee);
            Assert.Single(lignes);
        }

        [Fact]
        public void PoseUneMethodePermiseSansRienEcrire()
        {
            var lignes = new List<string>();
            var journal = new JournalDuGreffon("sir-correctifs-pulsar", lignes.Add, null);
            var methode = typeof(MyParallelEntityUpdateOrchestrator).GetMethod("DispatchBeforeSimulation", Toutes);
            var posees = 0;

            Assert.True(PointDAccroche.Accrocher(methode, "étape avant la simulation", () => posees++, journal));
            Assert.Equal(1, posees);
            Assert.Empty(lignes);
        }
    }
}
