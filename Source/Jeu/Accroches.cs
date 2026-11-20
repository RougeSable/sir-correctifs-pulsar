using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace SirCorrectifsPulsar
{
    // Une accroche posée sur une méthode du jeu, et le nombre de fois où elle a
    // été appelée. Une accroche dont le compteur reste à 0 est signalée : le
    // jeu a pu intégrer la méthode à ses appelants, et l'accroche ne partirait
    // jamais.
    internal sealed class Accroche
    {
        public string Nom;
        public Suivi Suivi;
        public bool Posee;
        public int Appels;
        public MethodBase Methode;

        public void Compter()
        {
            Interlocked.Increment(ref Appels);
        }
    }

    // Pose les accroches du greffon. Tout nom du jeu se résout par réflexion :
    // une méthode introuvable laisse son accroche de côté, avec une ligne
    // d'erreur au journal du jeu (qui donne un rapport), sans rien casser.
    //
    // Le greffon ne remplace rien : ses accroches observent seulement (aucun
    // préfixe ne saute la méthode du jeu). Il partage donc sans conflit une
    // méthode avec un autre greffon, et le dit au journal.
    internal static class Accroches
    {
        private static readonly List<Accroche> s_toutes = new List<Accroche>();

        public static IList<Accroche> Toutes
        {
            get { return s_toutes; }
        }

        public static Type Type(string nom)
        {
            var type = AccessTools.TypeByName(nom);
            if (type == null)
                throw new MissingMemberException("type introuvable : " + nom);
            return type;
        }

        public static MethodInfo Methode(Type type, string nom, params Type[] arguments)
        {
            var methode = arguments == null || arguments.Length == 0
                ? AccessTools.Method(type, nom)
                : AccessTools.Method(type, nom, arguments);
            if (methode == null)
                throw new MissingMemberException(type.FullName + "." + nom + " introuvable");
            return methode;
        }

        // La méthode d'une interface, telle que la classe l'implémente
        // (implémentation explicite comprise).
        public static MethodInfo MethodeDInterface(Type type, Type interfaceType, string nom)
        {
            var carte = type.GetInterfaceMap(interfaceType);
            for (var i = 0; i < carte.InterfaceMethods.Length; i++)
            {
                if (carte.InterfaceMethods[i].Name == nom)
                    return carte.TargetMethods[i];
            }
            throw new MissingMemberException(type.FullName + " n'implémente pas " + interfaceType.Name + "." + nom);
        }

        // Pose une accroche ; rend l'accroche, posée ou non.
        public static Accroche Poser(Harmony harmony, Suivi suivi, string nom, Func<MethodBase> cible,
            string prefixe, string postfixe, Type porteur)
        {
            return Poser(harmony, suivi, nom, cible, prefixe, postfixe, null, porteur);
        }

        // La même, avec une réécriture (transpiler) : elle ne sert qu'à faire
        // passer un appel du jeu par le greffon, qui fait ce même appel.
        public static Accroche Poser(Harmony harmony, Suivi suivi, string nom, Func<MethodBase> cible,
            string prefixe, string postfixe, string reecriture, Type porteur)
        {
            var accroche = new Accroche { Nom = nom, Suivi = suivi };
            s_toutes.Add(accroche);
            try
            {
                var methode = cible();
                accroche.Methode = methode;
                harmony.Patch(methode,
                    prefix: prefixe == null ? null : new HarmonyMethod(AccessTools.Method(porteur, prefixe)),
                    postfix: postfixe == null ? null : new HarmonyMethod(AccessTools.Method(porteur, postfixe)),
                    transpiler: reecriture == null ? null : new HarmonyMethod(AccessTools.Method(porteur, reecriture)));
                accroche.Posee = true;
                DireLesVoisins(methode, harmony.Id, nom);
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("accroche « " + nom + " » non posée, ce relevé manquera", e);
            }
            return accroche;
        }

        private static void DireLesVoisins(MethodBase methode, string nous, string nom)
        {
            try
            {
                var info = Harmony.GetPatchInfo(methode);
                if (info == null)
                    return;
                var autres = new List<string>();
                foreach (var proprietaire in info.Owners)
                {
                    if (proprietaire != nous && !autres.Contains(proprietaire))
                        autres.Add(proprietaire);
                }
                if (autres.Count > 0)
                    GreffonSirCorrectifsPulsar.Journal.Info("« " + nom + " » est aussi accrochée par " + string.Join(", ", autres.ToArray())
                        + " : le greffon ne fait qu'observer, il ne lui dispute rien");
            }
            catch (Exception e)
            {
                Fautes.Noter("voisins d'une accroche", e);
            }
        }

        // Les autres greffons déjà accrochés à une méthode du jeu (Harmony).
        public static List<string> AutresProprietaires(MethodBase methode, string nous)
        {
            var autres = new List<string>();
            var info = Harmony.GetPatchInfo(methode);
            if (info == null)
                return autres;
            foreach (var proprietaire in info.Owners)
            {
                if (proprietaire != nous && !autres.Contains(proprietaire))
                    autres.Add(proprietaire);
            }
            return autres;
        }

        // Les accroches posées qui n'ont jamais été appelées, pour un suivi.
        public static List<string> JamaisAppelees(Suivi suivi)
        {
            var noms = new List<string>();
            foreach (var accroche in s_toutes)
            {
                if (accroche.Suivi == suivi && accroche.Posee && Volatile.Read(ref accroche.Appels) == 0)
                    noms.Add(accroche.Nom);
            }
            return noms;
        }

        public static List<string> NonPosees(Suivi suivi)
        {
            var noms = new List<string>();
            foreach (var accroche in s_toutes)
            {
                if (accroche.Suivi == suivi && !accroche.Posee)
                    noms.Add(accroche.Nom);
            }
            return noms;
        }
    }

    // Une faute dans une accroche : une ligne au journal la première fois,
    // ensuite seulement comptée dans son rapport. Ne lève jamais.
    internal static class Fautes
    {
        private static readonly HashSet<string> s_vues = new HashSet<string>();

        public static void Noter(string ou, Exception erreur)
        {
            try
            {
                bool premiere;
                lock (s_vues)
                    premiere = s_vues.Add(ou);
                if (premiere)
                {
                    GreffonSirCorrectifsPulsar.Journal.Erreur("faute dans « " + ou + " »", erreur);
                    return;
                }
                var rapporteur = GreffonSirCorrectifsPulsar.Journal.Rapporteur;
                if (rapporteur != null)
                    rapporteur.Noter("faute dans « " + ou + " »", erreur);
            }
            catch (Exception)
            {
            }
        }
    }
}
