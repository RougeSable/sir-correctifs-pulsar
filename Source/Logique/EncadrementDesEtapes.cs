using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Une étape de l'orchestrateur des mises à jour, telle que la mesure des
    // blocs du fil la chronomètre : par l'encadrement de l'appel que le jeu
    // en fait, dans une méthode de MyEntities (« Appelant »), jamais par un
    // préfixe ou un suffixe posé sur l'étape elle-même.
    internal sealed class EtapeEncadree
    {
        public EtapeEncadree(int numero, string nom, string etape, string appelant, string typeAppele, string appel,
            string encadrement, string champ)
        {
            Numero = numero;
            Nom = nom;
            Etape = etape;
            Appelant = appelant;
            TypeAppele = typeAppele;
            Appel = appel;
            Encadrement = encadrement;
            Champ = champ;
        }

        // Sa place parmi les quatre, de 0 à 3, dans l'ordre de l'image.
        public int Numero { get; private set; }

        // Son nom, tel qu'il paraît dans les relevés.
        public string Nom { get; private set; }

        // La méthode de l'orchestrateur.
        public string Etape { get; private set; }

        // La méthode de MyEntities qui fait l'appel encadré.
        public string Appelant { get; private set; }

        // L'appel encadré : le type qui le déclare et son nom.
        public string TypeAppele { get; private set; }

        public string Appel { get; private set; }

        // La méthode de MesureDuFil qui fait cet appel à la place du jeu.
        public string Encadrement { get; private set; }

        // Le champ de la ligne « minute du fil » qui porte sa durée.
        public string Champ { get; private set; }
    }

    // Les quatre étapes de l'orchestrateur que la mesure des blocs du fil
    // chronomètre. Lu dans le jeu : à chaque image, MyEntities.UpdateBeforeSimulation
    // appelle MyEntities.UpdateOnceBeforeFrame (qui ne fait qu'appeler
    // DispatchOnceBeforeFrame : une méthode d'une ligne, que le jeu peut
    // intégrer à son appelant, et que le greffon n'accroche donc jamais),
    // puis DispatchBeforeSimulation ; MyEntities.Simulate appelle
    // DispatchSimulate et MyEntities.UpdateAfterSimulation appelle
    // DispatchAfterSimulation, toutes trois par l'interface de l'orchestrateur.
    internal static class EtapesDeLOrchestrateur
    {
        public const string TypeDesAppelants = "Sandbox.Game.Entities.MyEntities";
        public const string InterfaceDesEtapes = "Sandbox.Game.Entities.IMyUpdateOrchestrator";

        public static readonly EtapeEncadree UneFoisAvantLImage = new EtapeEncadree(0,
            "mises à jour uniques des entités", "DispatchOnceBeforeFrame",
            "UpdateBeforeSimulation", TypeDesAppelants, "UpdateOnceBeforeFrame",
            "EtapeUneFoisAvantLImage", "temps_une_fois_avant_l_image_ms");

        public static readonly EtapeEncadree AvantLaSimulation = new EtapeEncadree(1,
            "mises à jour des entités avant la simulation", "DispatchBeforeSimulation",
            "UpdateBeforeSimulation", InterfaceDesEtapes, "DispatchBeforeSimulation",
            "EtapeAvantLaSimulation", "temps_avant_la_simulation_ms");

        public static readonly EtapeEncadree Simulation = new EtapeEncadree(2,
            "simulation des entités", "DispatchSimulate",
            "Simulate", InterfaceDesEtapes, "DispatchSimulate",
            "EtapeSimulation", "temps_de_la_simulation_ms");

        public static readonly EtapeEncadree ApresLaSimulation = new EtapeEncadree(3,
            "mises à jour des entités après la simulation", "DispatchAfterSimulation",
            "UpdateAfterSimulation", InterfaceDesEtapes, "DispatchAfterSimulation",
            "EtapeApresLaSimulation", "temps_apres_la_simulation_ms");

        public static readonly EtapeEncadree[] Toutes = { UneFoisAvantLImage, AvantLaSimulation, Simulation, ApresLaSimulation };

        // Les appelants, chacun une fois, avec les étapes qu'il encadre, dans l'ordre.
        public static List<KeyValuePair<string, List<EtapeEncadree>>> ParAppelant()
        {
            var appelants = new List<KeyValuePair<string, List<EtapeEncadree>>>();
            foreach (var etape in Toutes)
            {
                var trouve = appelants.FindIndex(a => a.Key == etape.Appelant);
                if (trouve < 0)
                    appelants.Add(new KeyValuePair<string, List<EtapeEncadree>>(etape.Appelant, new List<EtapeEncadree> { etape }));
                else
                    appelants[trouve].Value.Add(etape);
            }
            return appelants;
        }
    }

    // Le chronomètre des étapes : il fait l'appel du jeu, exactement une
    // fois, et sur une image mesurée ajoute sa durée à celle de son étape.
    // Une exception du jeu passe telle quelle ; la durée de l'appel est
    // comptée quand même.
    internal sealed class ChronometreDesEtapes
    {
        private readonly Func<long> m_horloge;
        private readonly long[] m_ticks = new long[EtapesDeLOrchestrateur.Toutes.Length];

        public ChronometreDesEtapes(Func<long> horloge)
        {
            if (horloge == null)
                throw new ArgumentNullException("horloge");
            m_horloge = horloge;
        }

        // Le temps d'une étape depuis la dernière prise.
        public long TicksDe(int etape)
        {
            return m_ticks[etape];
        }

        public void Encadrer<T>(int etape, bool mesuree, T cible, Action<T> appel)
        {
            if (!mesuree)
            {
                appel(cible);
                return;
            }
            var debut = m_horloge();
            try
            {
                appel(cible);
            }
            finally
            {
                m_ticks[etape] += m_horloge() - debut;
            }
        }

        public void Encadrer(int etape, bool mesuree, Action appel)
        {
            if (!mesuree)
            {
                appel();
                return;
            }
            var debut = m_horloge();
            try
            {
                appel();
            }
            finally
            {
                m_ticks[etape] += m_horloge() - debut;
            }
        }

        // Recopie le temps de chaque étape dans « parEtape » (s'il est donné),
        // rend leur somme et repart de zéro.
        public long Prendre(long[] parEtape)
        {
            long total = 0;
            for (var i = 0; i < m_ticks.Length; i++)
            {
                total += m_ticks[i];
                if (parEtape != null && i < parEtape.Length)
                    parEtape[i] = m_ticks[i];
                m_ticks[i] = 0;
            }
            return total;
        }
    }
}
