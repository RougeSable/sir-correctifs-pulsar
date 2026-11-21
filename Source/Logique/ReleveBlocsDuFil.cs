using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Ce que mesure le relevé « blocs du fil » : un type de bloc, les
    // personnages, une construction elle-même (la grille, hors de ses blocs),
    // un système de construction (propulsion, réseau électrique...) ou une
    // autre entité du jeu (objets flottants, voxels...).
    public enum SorteDuFil
    {
        Bloc,
        Personnage,
        Construction,
        Systeme,
        Autre
    }

    public static class NomsDesSortesDuFil
    {
        public static string Nom(SorteDuFil sorte)
        {
            switch (sorte)
            {
                case SorteDuFil.Bloc: return "bloc";
                case SorteDuFil.Personnage: return "personnage";
                case SorteDuFil.Construction: return "construction";
                case SorteDuFil.Systeme: return "systeme";
                default: return "autre";
            }
        }

        // Le nom du champ qui compte les entités mesurées d'une ligne.
        public static string ChampDuNombre(SorteDuFil sorte)
        {
            switch (sorte)
            {
                case SorteDuFil.Bloc: return "blocs";
                case SorteDuFil.Personnage: return "personnages";
                case SorteDuFil.Construction: return "constructions";
                default: return "entites";
            }
        }
    }

    // Un type mesuré, et ce qu'il a coûté pendant la minute en cours. Le fil
    // du jeu seul y touche.
    public sealed class TypeDuFil
    {
        public readonly string Cle;
        public readonly SorteDuFil Sorte;
        public readonly string Type;
        public readonly string SousType;
        public readonly string Nom;

        internal long Ticks;
        internal int Appels;
        internal bool Vu;
        internal readonly HashSet<long> Entites = new HashSet<long>();
        internal readonly Dictionary<long, ConstructionDuFil> Constructions = new Dictionary<long, ConstructionDuFil>();

        internal TypeDuFil(string cle, SorteDuFil sorte, string type, string sousType, string nom)
        {
            Cle = cle;
            Sorte = sorte;
            Type = type;
            SousType = sousType;
            Nom = nom;
        }
    }

    // Ce qu'un type a coûté sur une construction (une grille).
    internal sealed class ConstructionDuFil
    {
        public long Id;
        public object Porteur;
        public long Ticks;
        public int Appels;
    }

    // Une ligne du relevé, prête à partir : son événement et ses champs.
    public struct LigneDuFil
    {
        public readonly string Evenement;
        public readonly ChampsDuReleve Champs;

        public LigneDuFil(string evenement, ChampsDuReleve champs)
        {
            Evenement = evenement;
            Champs = champs;
        }
    }

    // Le relevé d'une minute de ce que chaque type de bloc coûte au fil du jeu
    // du joueur. La mesure est un échantillon : une image sur « intervalle »
    // seulement est mesurée, appel par appel ; chaque appel mesuré est confié
    // ici avec son temps propre (ce qu'il a appelé de mesuré en est retiré).
    // À la fin de la minute, Fermer rend une ligne par type (temps, nombre
    // d'entités, nombre d'appels, et les 5 constructions qui en coûtent le
    // plus), puis une ligne « minute du fil » qui dit le temps du fil, le coût
    // de la mesure elle-même et sa part. Tout s'y fait sur le fil du jeu ; rien
    // n'y lève vers le jeu.
    public sealed class ReleveBlocsDuFil
    {
        public const int ConstructionsGardees = 5;

        // Au-delà, les types les moins coûteux ne font pas de ligne : ils sont
        // comptés dans « types_non_ecrits ».
        public const int TypesParMinuteMaximum = 200;

        public const string EvenementDuType = "blocs du fil";
        public const string EvenementDeLaMinute = "minute du fil";

        // Les intervalles d'échantillonnage, en images : tous premiers avec 10
        // et 100. Le jeu répartit les blocs « toutes les 10 (100) images » en
        // tranches, une par image et rangées par type : un intervalle multiple
        // de 10 mesurerait toujours la même tranche.
        public static readonly int[] Intervalles = { 37, 71, 149, 293, 587 };

        // Au-delà de cette part du fil, la minute suivante mesure moins souvent.
        public const double PartDuFilVisee = 0.5;

        private readonly double m_ticksParMilliseconde;
        private readonly Func<long> m_horloge;
        private readonly Func<object, string> m_nomDeLaConstruction;

        private readonly Dictionary<string, TypeDuFil> m_types = new Dictionary<string, TypeDuFil>();
        private readonly List<TypeDuFil> m_vus = new List<TypeDuFil>();

        private int m_images;
        private int m_imagesMesurees;
        private long m_ticksDesEntites;
        private readonly long[] m_ticksDesEtapes = new long[EtapesDeLOrchestrateur.Toutes.Length];
        private long m_ticksDeLaMesure;
        private long m_appels;

        // frequence : les ticks de l'horloge par seconde (Stopwatch.Frequency
        // dans le jeu). horloge : l'heure en ticks, pour le coût du relevé
        // lui-même. nomDeLaConstruction : le nom d'une construction, lu
        // seulement pour les 5 gardées.
        public ReleveBlocsDuFil(long frequence, Func<long> horloge, Func<object, string> nomDeLaConstruction)
        {
            m_ticksParMilliseconde = frequence <= 0 ? 1.0 : frequence / 1000.0;
            m_horloge = horloge ?? (() => 0L);
            m_nomDeLaConstruction = nomDeLaConstruction ?? (o => o == null ? null : o.ToString());
        }

        public int Images
        {
            get { return m_images; }
        }

        public int ImagesMesurees
        {
            get { return m_imagesMesurees; }
        }

        public long AppelsMesures
        {
            get { return m_appels; }
        }

        // Le type d'une clé ; créé à sa première demande, puis le même toute
        // la session.
        public TypeDuFil Type(string cle, SorteDuFil sorte, string type, string sousType, string nom)
        {
            TypeDuFil existant;
            if (m_types.TryGetValue(cle, out existant))
                return existant;
            var nouveau = new TypeDuFil(cle, sorte, type, sousType, nom);
            m_types[cle] = nouveau;
            return nouveau;
        }

        // Un appel mesuré : son type, l'entité (le bloc), sa construction (0
        // sans construction) et son temps propre, en ticks.
        public void Noter(TypeDuFil type, long entite, long construction, object porteur, long ticks)
        {
            if (type == null)
                return;
            if (!type.Vu)
            {
                type.Vu = true;
                m_vus.Add(type);
            }
            if (ticks < 0)
                ticks = 0;
            type.Ticks += ticks;
            type.Appels++;
            m_appels++;
            type.Entites.Add(entite);
            if (construction == 0)
                return;
            ConstructionDuFil acc;
            if (!type.Constructions.TryGetValue(construction, out acc))
            {
                acc = new ConstructionDuFil { Id = construction, Porteur = porteur };
                type.Constructions[construction] = acc;
            }
            acc.Ticks += ticks;
            acc.Appels++;
        }

        // Une image du jeu ; mesurée, avec le temps de toutes les mises à jour
        // des entités de cette image.
        public void Image(bool mesuree, long ticksDesEntites)
        {
            Image(mesuree, ticksDesEntites, null);
        }

        // La même, avec le temps de chacune des quatre étapes de
        // l'orchestrateur (dans l'ordre de EtapesDeLOrchestrateur.Toutes).
        public void Image(bool mesuree, long ticksDesEntites, long[] ticksDesEtapes)
        {
            m_images++;
            if (!mesuree)
                return;
            m_imagesMesurees++;
            if (ticksDesEntites > 0)
                m_ticksDesEntites += ticksDesEntites;
            if (ticksDesEtapes == null)
                return;
            for (var i = 0; i < m_ticksDesEtapes.Length && i < ticksDesEtapes.Length; i++)
            {
                if (ticksDesEtapes[i] > 0)
                    m_ticksDesEtapes[i] += ticksDesEtapes[i];
            }
        }

        // Ce que la mesure a coûté elle-même, en ticks.
        public void Cout(long ticks)
        {
            if (ticks > 0)
                m_ticksDeLaMesure += ticks;
        }

        // Tout oublier : la partie est quittée.
        public void Oublier()
        {
            foreach (var type in m_vus)
                Remettre(type);
            m_vus.Clear();
            m_images = 0;
            m_imagesMesurees = 0;
            m_ticksDesEntites = 0;
            Array.Clear(m_ticksDesEtapes, 0, m_ticksDesEtapes.Length);
            m_ticksDeLaMesure = 0;
            m_appels = 0;
        }

        // Clôt la minute : une ligne par type mesuré, du plus coûteux au moins
        // coûteux, puis la ligne de la minute. Remet tout à zéro.
        //   ticksDuFil : le temps écoulé sur le fil du jeu pendant la minute ;
        //   ticksParAppelHorsEchantillon : ce que coûte une accroche à un appel
        //   d'une image non mesurée (estimé au début de la partie).
        public List<LigneDuFil> Fermer(DateTime debut, DateTime fin, long ticksDuFil, int intervalle, double ticksParAppelHorsEchantillon)
        {
            var debutDuReleve = Horloge();
            var lignes = new List<LigneDuFil>();

            m_vus.Sort(PlusCouteuxDAbord);
            long ticksAttribues = 0;
            foreach (var type in m_vus)
                ticksAttribues += type.Ticks;

            var ecrits = Math.Min(m_vus.Count, TypesParMinuteMaximum);
            for (var i = 0; i < ecrits; i++)
                lignes.Add(new LigneDuFil(EvenementDuType, ChampsDuType(m_vus[i], debut)));

            // Les appels des images non mesurées passent aussi par les
            // accroches : leur coût s'estime par les appels des images mesurées.
            double appelsHorsEchantillon = 0;
            if (m_imagesMesurees > 0 && m_images > m_imagesMesurees)
                appelsHorsEchantillon = (double)m_appels / m_imagesMesurees * (m_images - m_imagesMesurees);
            var ticksHorsEchantillon = appelsHorsEchantillon * Math.Max(0.0, ticksParAppelHorsEchantillon);

            var champs = new ChampsDuReleve();
            champs.Heure("debut", debut);
            champs.Heure("fin", fin);
            champs.Nombre("temps_du_fil_ms", Ms(ticksDuFil), 1);
            champs.Entier("images", m_images);
            champs.Entier("images_mesurees", m_imagesMesurees);
            champs.Entier("intervalle", intervalle);
            champs.Nombre("temps_des_entites_ms", Ms(m_ticksDesEntites), 3);
            foreach (var etape in EtapesDeLOrchestrateur.Toutes)
                champs.Nombre(etape.Champ, Ms(m_ticksDesEtapes[etape.Numero]), 3);
            champs.Nombre("temps_attribue_ms", Ms(ticksAttribues), 3);
            champs.Entier("types", m_vus.Count);
            champs.Entier("types_non_ecrits", m_vus.Count - ecrits);
            champs.Entier("appels_mesures", m_appels);
            champs.Nombre("cout_de_la_mesure_ms", Ms(m_ticksDeLaMesure), 3);
            champs.Nombre("cout_hors_echantillon_ms", Ms(ticksHorsEchantillon), 3);

            // Le coût du relevé lui-même (tri, lignes) compte aussi.
            var ticksDuReleve = Math.Max(0L, Horloge() - debutDuReleve);
            var coutTotal = m_ticksDeLaMesure + ticksHorsEchantillon + ticksDuReleve;
            champs.Nombre("cout_du_releve_ms", Ms(ticksDuReleve), 3);
            champs.Nombre("cout_total_ms", Ms(coutTotal), 3);
            champs.Nombre("part_du_fil_pct", PartDuFil(coutTotal, ticksDuFil), 4);
            lignes.Add(new LigneDuFil(EvenementDeLaMinute, champs));

            DernierePart = PartDuFil(coutTotal, ticksDuFil);
            Oublier();
            return lignes;
        }

        // La part du fil prise par la mesure à la dernière minute close, en %.
        public double DernierePart { get; private set; }

        // L'intervalle de la minute suivante : plus long si la mesure a pris
        // plus que la part visée du fil.
        public static int IntervalleSuivant(int intervalle, double partDuFil)
        {
            if (partDuFil <= PartDuFilVisee)
                return intervalle;
            for (var i = 0; i < Intervalles.Length; i++)
            {
                if (Intervalles[i] > intervalle)
                    return Intervalles[i];
            }
            return Intervalles[Intervalles.Length - 1];
        }

        public static double PartDuFil(double ticksDeLaMesure, long ticksDuFil)
        {
            return ticksDuFil <= 0 ? 0.0 : ticksDeLaMesure * 100.0 / ticksDuFil;
        }

        private ChampsDuReleve ChampsDuType(TypeDuFil type, DateTime debut)
        {
            var champs = new ChampsDuReleve();
            champs.Heure("debut", debut);
            champs.Texte("sorte", NomsDesSortesDuFil.Nom(type.Sorte));
            champs.Texte("type", type.Type ?? FormeDesReleves.Inconnu);
            champs.Texte("sous_type", string.IsNullOrEmpty(type.SousType) ? null : type.SousType);
            champs.Texte("nom", string.IsNullOrEmpty(type.Nom) ? null : type.Nom);
            champs.Nombre("temps_total_ms", Ms(type.Ticks), 3);
            if (m_imagesMesurees > 0)
            {
                champs.Nombre("temps_par_image_ms", Ms(type.Ticks) / m_imagesMesurees, 4);
                champs.Nombre("temps_estime_minute_ms", Ms(type.Ticks) * m_images / m_imagesMesurees, 1);
            }
            champs.Entier(NomsDesSortesDuFil.ChampDuNombre(type.Sorte), type.Entites.Count);
            champs.Entier("appels", type.Appels);
            champs.Entier("images_mesurees", m_imagesMesurees);

            var constructions = new List<ConstructionDuFil>(type.Constructions.Values);
            constructions.Sort(ConstructionPlusCouteuseDAbord);
            var gardees = Math.Min(constructions.Count, ConstructionsGardees);
            champs.Entier("constructions", constructions.Count);
            for (var i = 0; i < gardees; i++)
            {
                var c = constructions[i];
                var prefixe = "construction_" + (i + 1);
                champs.Texte(prefixe, NomDe(c.Porteur));
                champs.Entier(prefixe + "_id", c.Id);
                champs.Nombre(prefixe + "_ms", Ms(c.Ticks), 3);
                champs.Entier(prefixe + "_appels", c.Appels);
            }
            return champs;
        }

        private string NomDe(object porteur)
        {
            try
            {
                var nom = m_nomDeLaConstruction(porteur);
                return string.IsNullOrEmpty(nom) ? FormeDesReleves.Inconnu : nom;
            }
            catch (Exception)
            {
                return FormeDesReleves.Inconnu;
            }
        }

        private static void Remettre(TypeDuFil type)
        {
            type.Vu = false;
            type.Ticks = 0;
            type.Appels = 0;
            type.Entites.Clear();
            type.Constructions.Clear();
        }

        private long Horloge()
        {
            try
            {
                return m_horloge();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private double Ms(double ticks)
        {
            return ticks / m_ticksParMilliseconde;
        }

        private static int PlusCouteuxDAbord(TypeDuFil a, TypeDuFil b)
        {
            var c = b.Ticks.CompareTo(a.Ticks);
            return c != 0 ? c : string.CompareOrdinal(a.Cle, b.Cle);
        }

        private static int ConstructionPlusCouteuseDAbord(ConstructionDuFil a, ConstructionDuFil b)
        {
            var c = b.Ticks.CompareTo(a.Ticks);
            return c != 0 ? c : a.Id.CompareTo(b.Id);
        }
    }
}
