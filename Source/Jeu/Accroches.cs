using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
    //
    // Toute accroche passe par le point d'entrée unique (PointDAccroche), qui
    // refuse les méthodes du registre des méthodes interdites. Une mesure
    // (un chronomètre) ne pose jamais de préfixe ni de suffixe sur une
    // méthode du jeu : elle encadre l'appel que le jeu en fait, par une
    // réécriture de l'appelant (Encadrer, Reecrire).
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

        // Pose une accroche d'observation (préfixe, suffixe, finaliseur) ;
        // rend l'accroche, posée ou non. Jamais pour une mesure : une méthode
        // passée en préfixe, suffixe ou finaliseur ne lit jamais l'horloge (un
        // test casse la construction sinon). Une mesure encadre l'appel que
        // le jeu fait, par une réécriture de l'appelant (Encadrer, Reecrire).
        public static Accroche Poser(Harmony harmony, Suivi suivi, string nom, Func<MethodBase> cible,
            string prefixe, string postfixe, Type porteur)
        {
            return Accrocher(harmony, suivi, nom, cible, prefixe, postfixe, null, null, porteur);
        }

        // La même, avec un finaliseur : il voit l'exception de la méthode du
        // jeu sans la changer.
        public static Accroche Poser(Harmony harmony, Suivi suivi, string nom, Func<MethodBase> cible,
            string prefixe, string postfixe, string finaliseur, Type porteur)
        {
            return Accrocher(harmony, suivi, nom, cible, prefixe, postfixe, finaliseur, null, porteur);
        }

        // Une réécriture (transpiler), seule : elle ne sert qu'à faire passer
        // un appel du jeu par le greffon, qui fait ce même appel. C'est ainsi
        // que les mesures encadrent un appel, sans préfixe ni suffixe.
        public static Accroche Reecrire(Harmony harmony, Suivi suivi, string nom, Func<MethodBase> cible,
            string reecriture, Type porteur)
        {
            return Accrocher(harmony, suivi, nom, cible, null, null, null, reecriture, porteur);
        }

        // Encadre un appel du jeu, pour une mesure : dans chacun de ses
        // appelants, l'appel de « appel » devient l'appel de « encadrement »,
        // une méthode statique publique du porteur, qui reçoit les mêmes
        // arguments (l'instance d'abord) et fait elle-même ce même appel,
        // exactement une fois, entre deux lectures de l'horloge. Rien n'est
        // posé sur la méthode appelée. Une seule accroche pour tous les
        // appelants : c'est l'encadrement qui la compte. Un appelant où l'appel
        // ne se trouve pas n'est pas réécrit, et le dit au journal.
        public static Accroche Encadrer(Harmony harmony, Suivi suivi, string nom, Func<MethodInfo> appel,
            Func<IEnumerable<MethodBase>> appelants, string encadrement, Type porteur)
        {
            var accroche = new Accroche { Nom = nom, Suivi = suivi };
            s_toutes.Add(accroche);
            try
            {
                var appelDuJeu = appel();
                accroche.Methode = appelDuJeu;

                // L'appel d'une méthode interdite ne s'encadre pas non plus.
                if (PointDAccroche.Refuser(appelDuJeu, nom, GreffonSirCorrectifsPulsar.Journal))
                    return accroche;
                var notre = porteur.GetMethod(encadrement, BindingFlags.Public | BindingFlags.Static);
                if (notre == null)
                    throw new MissingMemberException(porteur.Name + "." + encadrement + " introuvable");
                VerifierLEncadrement(appelDuJeu, notre);
                lock (s_encadrements)
                    s_encadrements[appelDuJeu] = notre;

                var poses = 0;
                foreach (var appelant in appelants())
                {
                    var ou = appelant.DeclaringType.Name + "." + appelant.Name;
                    if (AppelsDans(appelant, appelDuJeu) == 0)
                    {
                        GreffonSirCorrectifsPulsar.Journal.Erreur("« " + nom + " » : aucun appel à encadrer dans " + ou
                            + ", ce relevé y manquera", null);
                        continue;
                    }
                    try
                    {
                        if (PoserSur(harmony, appelant, nom + " (dans " + ou + ")", null, null, null, "EncadrerLesAppels", typeof(Accroches)))
                            poses++;
                    }
                    catch (Exception e)
                    {
                        GreffonSirCorrectifsPulsar.Journal.Erreur("« " + nom + " » non encadré dans " + ou + ", ce relevé y manquera", e);
                    }
                }
                accroche.Posee = poses > 0;
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("accroche « " + nom + " » non posée, ce relevé manquera", e);
            }
            return accroche;
        }

        // Les appels du jeu encadrés, et leur encadrement : la réécriture des
        // appelants les lit (le jeu peut la refaire, quand un autre greffon
        // accroche la même méthode).
        private static readonly Dictionary<MethodInfo, MethodInfo> s_encadrements = new Dictionary<MethodInfo, MethodInfo>();

        // L'encadrement prend la place de l'appel sur la pile : les mêmes
        // arguments, l'instance d'abord, et le même retour.
        private static void VerifierLEncadrement(MethodInfo appel, MethodInfo encadrement)
        {
            var attendus = new List<Type>();
            if (!appel.IsStatic)
                attendus.Add(appel.DeclaringType);
            foreach (var parametre in appel.GetParameters())
                attendus.Add(parametre.ParameterType);
            var recus = encadrement.GetParameters();
            var accorde = recus.Length == attendus.Count && encadrement.ReturnType == appel.ReturnType;
            for (var i = 0; accorde && i < recus.Length; i++)
                accorde = recus[i].ParameterType.IsAssignableFrom(attendus[i]);
            if (!accorde)
                throw new NotSupportedException(encadrement.Name + " ne prend pas les arguments de " + appel.DeclaringType.Name + "." + appel.Name);
        }

        // Combien de fois l'appelant fait cet appel, dans son code d'origine.
        private static int AppelsDans(MethodBase appelant, MethodInfo appel)
        {
            var n = 0;
            foreach (var instruction in PatchProcessor.GetOriginalInstructions(appelant))
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && Equals(instruction.operand, appel))
                    n++;
            }
            return n;
        }

        // Réécriture d'un appelant : l'appel encadré devient l'appel de son
        // encadrement, avec les mêmes arguments. Rien d'autre ne change.
        private static IEnumerable<CodeInstruction> EncadrerLesAppels(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                var appel = instruction.operand as MethodInfo;
                MethodInfo encadrement = null;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && appel != null)
                {
                    lock (s_encadrements)
                        s_encadrements.TryGetValue(appel, out encadrement);
                }
                if (encadrement != null)
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = encadrement;
                }
                yield return instruction;
            }
        }

        // Toute accroche passe par ici.
        private static Accroche Accrocher(Harmony harmony, Suivi suivi, string nom, Func<MethodBase> cible,
            string prefixe, string postfixe, string finaliseur, string reecriture, Type porteur)
        {
            var accroche = new Accroche { Nom = nom, Suivi = suivi };
            s_toutes.Add(accroche);
            try
            {
                var methode = cible();
                accroche.Methode = methode;
                accroche.Posee = PoserSur(harmony, methode, nom, prefixe, postfixe, finaliseur, reecriture, porteur);
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("accroche « " + nom + " » non posée, ce relevé manquera", e);
            }
            return accroche;
        }

        // La seule pose d'accroche du greffon : par le point d'entrée unique,
        // qui refuse les méthodes du registre des méthodes interdites. Rend
        // vrai si l'accroche est posée.
        private static bool PoserSur(Harmony harmony, MethodBase methode, string nom,
            string prefixe, string postfixe, string finaliseur, string reecriture, Type porteur)
        {
            var posee = PointDAccroche.Accrocher(methode, nom, () => harmony.Patch(methode,
                prefix: MethodeDuGreffon(porteur, prefixe),
                postfix: MethodeDuGreffon(porteur, postfixe),
                transpiler: MethodeDuGreffon(porteur, reecriture),
                finalizer: MethodeDuGreffon(porteur, finaliseur)), GreffonSirCorrectifsPulsar.Journal);
            if (posee)
                DireLesVoisins(methode, harmony.Id, nom);
            return posee;
        }

        private static HarmonyMethod MethodeDuGreffon(Type porteur, string nom)
        {
            if (nom == null)
                return null;
            var methode = AccessTools.Method(porteur, nom);
            if (methode == null)
                throw new MissingMemberException(porteur.Name + "." + nom + " introuvable");
            return new HarmonyMethod(methode);
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
