using System.Linq;
using System.Reflection;
using Sandbox.Game.Entities;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class RegistreDesMethodesInterditesTests
    {
        private const BindingFlags Toutes = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void ContientPerformParallelUpdateAvecSaRaison()
        {
            var interdite = RegistreDesMethodesInterdites.Trouver("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator", "PerformParallelUpdate");
            Assert.NotNull(interdite);
            Assert.Equal("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator.PerformParallelUpdate", interdite.NomComplet);
            Assert.StartsWith("les vaisseaux qui montent du 20/11", interdite.Raison);
            Assert.Contains(RegistreDesMethodesInterdites.Toutes, m => ReferenceEquals(m, interdite));

            // La méthode elle-même, telle que la réflexion la rend.
            var methode = typeof(MyParallelEntityUpdateOrchestrator).GetMethod("PerformParallelUpdate", Toutes);
            Assert.Same(interdite, RegistreDesMethodesInterdites.Trouver(methode));
        }

        [Fact]
        public void LesAutresMethodesDeLOrchestrateurRestentPermises()
        {
            Assert.Null(RegistreDesMethodesInterdites.Trouver(typeof(MyParallelEntityUpdateOrchestrator).GetMethod("DispatchBeforeSimulation", Toutes)));
            Assert.Null(RegistreDesMethodesInterdites.Trouver("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator", "DispatchBeforeSimulation"));
            Assert.Null(RegistreDesMethodesInterdites.Trouver("Un.Autre.Type", "PerformParallelUpdate"));
            Assert.Null(RegistreDesMethodesInterdites.Trouver((MethodBase)null));
        }

        [Fact]
        public void ChaqueMethodeInterditeASaRaisonEtNApparaitQuUneFois()
        {
            foreach (var interdite in RegistreDesMethodesInterdites.Toutes)
            {
                Assert.False(string.IsNullOrWhiteSpace(interdite.Type));
                Assert.False(string.IsNullOrWhiteSpace(interdite.Methode));
                Assert.False(string.IsNullOrWhiteSpace(interdite.Raison));
            }
            Assert.Equal(RegistreDesMethodesInterdites.Toutes.Count,
                RegistreDesMethodesInterdites.Toutes.Select(m => m.NomComplet).Distinct().Count());
        }
    }
}
