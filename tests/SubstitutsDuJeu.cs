using System;

// Un substitut, dans les tests seulement, de l'orchestrateur des mises à jour
// du jeu : même espace de noms, même nom de type et mêmes noms de méthodes,
// pour éprouver le registre et le point d'entrée des accroches sur une vraie
// méthode, sans le jeu.
namespace Sandbox.Game.Entities
{
    internal class MyParallelEntityUpdateOrchestrator
    {
        public int Appels;

        private void PerformParallelUpdate(Action<object> miseAJour)
        {
            Appels++;
        }

        public void DispatchBeforeSimulation()
        {
            PerformParallelUpdate(null);
        }
    }

    // Un orchestrateur dérivé qui redéclare la méthode sous le même nom.
    internal class OrchestrateurDerive : MyParallelEntityUpdateOrchestrator
    {
        private void PerformParallelUpdate(Action<object> miseAJour)
        {
        }
    }
}
