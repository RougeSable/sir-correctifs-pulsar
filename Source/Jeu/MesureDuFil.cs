using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;

namespace SirCorrectifsPulsar
{
    // Le relevé « blocs du fil » : ce que chaque type de bloc coûte au fil du
    // jeu du joueur, minute par minute.
    //
    // Lu dans le jeu : toutes les mises à jour des entités qui tournent sur le
    // fil du jeu passent par l'orchestrateur (MyParallelEntityUpdateOrchestrator),
    // qui appelle, entité par entité, UpdateOnceBeforeFrame,
    // UpdateBeforeSimulation (et ses variantes 10 et 100), Simulate et
    // UpdateAfterSimulation (10, 100). Les blocs (réservoirs, propulseurs,
    // écrans...) y sont des entités à part entière, et leurs scripts de mods
    // passent par ces mêmes appels. Une construction, elle, appelle à son tour
    // ce que ses blocs et ses systèmes ont confié à sa propre file
    // (MyCubeGrid.Dispatch).
    //
    // Le greffon réécrit ces boucles pour que chacun de ces appels passe par
    // lui : il fait exactement le même appel, à la même place, dans le même
    // ordre, et laisse passer toute exception du jeu. Hors des images mesurées,
    // il ne fait qu'un test avant l'appel. Une image sur « intervalle »
    // (37 d'abord, toujours premier avec 10 et 100) est mesurée : chaque appel
    // y est chronométré, et son temps propre (sans ce qu'il a lui-même appelé
    // de mesuré) va à son type. Seul le fil du jeu est mesuré : une
    // construction mise à jour sur un fil de travail ne l'est pas.
    //
    // Ce que la mesure coûte est chronométré avec elle ; si elle prend plus de
    // 0,5 % du fil sur une minute, la suivante mesure moins souvent.
    //
    // Ces réécritures remplacent ce que l'étape utilise : si un autre greffon
    // est déjà accroché à l'une de ces méthodes, la mesure lui cède la place
    // pour la session et le dit au joueur.
    public static class MesureDuFil
    {
        private const string TexteDeLaPlaceCedee = "Sir Correctifs Pulsar : la mesure des blocs du fil cède la place à {0}, déjà accroché aux mises à jour des blocs. Rien d'autre ne change.";
        private const int DureeDeLaNotification = 10000;

        internal static readonly ReleveBlocsDuFil Releve = new ReleveBlocsDuFil(Stopwatch.Frequency, Stopwatch.GetTimestamp, NomDeLaConstruction);

        // Lu à chaque appel du jeu : une image mesurée.
        private static bool s_echantillon;
        private static int s_filDuJeu = -1;
        private static bool s_enRoute;
        private static int s_intervalle = ReleveBlocsDuFil.Intervalles[0];
        private static long s_images;

        // La mesure de l'image en cours (fil du jeu seulement).
        private static long s_enfants;
        private static long s_cout;
        private static long s_ticksDesEntites;
        private static long s_debutDesEntites;

        private static DateTime s_debutDeLaMinute;
        private static long s_debutDeLaMinuteTicks;
        private static double s_ticksParAppelHorsEchantillon;

        private static Harmony s_harmony;
        private static readonly List<Accroche> s_accroches = new List<Accroche>();
        private static readonly Dictionary<MethodBase, Accroche> s_accrochesDesEtapes = new Dictionary<MethodBase, Accroche>();
        private static string s_cedeeA;
        private static bool s_cessionDite;

        private static Accroche s_uneFois, s_avant, s_avant10, s_avant100, s_simulation, s_apres, s_apres10, s_apres100, s_fileDeLaConstruction;

        // Les appels du jeu que les réécritures font passer par le greffon.
        private static readonly Dictionary<MethodInfo, MethodInfo> s_remplacants = new Dictionary<MethodInfo, MethodInfo>();
        private static int s_remplaces;
        private static MethodInfo s_invoke;
        private static FieldInfo s_rappelDeLaFile;

        private static readonly Dictionary<MyCubeBlockDefinition, TypeDuFil> s_typesDesBlocs =
            new Dictionary<MyCubeBlockDefinition, TypeDuFil>(new MemeObjet<MyCubeBlockDefinition>());
        private static readonly Dictionary<Type, TypeDuFil> s_typesDesEntites = new Dictionary<Type, TypeDuFil>();
        private static readonly Dictionary<MethodInfo, TypeDuFil> s_typesDesSystemes = new Dictionary<MethodInfo, TypeDuFil>();
        private static TypeDuFil s_personnages;
        private static TypeDuFil s_constructions;

        private static readonly Action s_vide = Rien;

        private sealed class MemeObjet<T> : IEqualityComparer<T> where T : class
        {
            public bool Equals(T a, T b)
            {
                return ReferenceEquals(a, b);
            }

            public int GetHashCode(T objet)
            {
                return RuntimeHelpers.GetHashCode(objet);
            }
        }

        // À l'initialisation du greffon, avant que la partie tourne.
        internal static void Poser(Harmony harmony)
        {
            s_harmony = harmony;
            var moi = typeof(MesureDuFil);
            Type orchestrateur;
            MethodInfo[] etapes, boucles;
            MethodBase dispatch;
            try
            {
                orchestrateur = Accroches.Type("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator");
                etapes = new[]
                {
                    Accroches.Methode(orchestrateur, "DispatchOnceBeforeFrame"),
                    Accroches.Methode(orchestrateur, "DispatchBeforeSimulation"),
                    Accroches.Methode(orchestrateur, "DispatchSimulate"),
                    Accroches.Methode(orchestrateur, "DispatchAfterSimulation"),
                };
                boucles = new[]
                {
                    Accroches.Methode(orchestrateur, "UpdateBeforeSimulation"),
                    Accroches.Methode(orchestrateur, "UpdateBeforeSimulation10"),
                    Accroches.Methode(orchestrateur, "UpdateBeforeSimulation100"),
                    Accroches.Methode(orchestrateur, "UpdateAfterSimulation"),
                    Accroches.Methode(orchestrateur, "UpdateAfterSimulation10"),
                    Accroches.Methode(orchestrateur, "UpdateAfterSimulation100"),
                };
                Remplacer(typeof(MyEntity), "UpdateOnceBeforeFrame", "UneFoisAvantLImage");
                Remplacer(typeof(MyEntity), "UpdateBeforeSimulation", "AvantLaSimulation");
                Remplacer(typeof(MyEntity), "UpdateBeforeSimulation10", "AvantLaSimulation10");
                Remplacer(typeof(MyEntity), "UpdateBeforeSimulation100", "AvantLaSimulation100");
                Remplacer(typeof(MyEntity), "Simulate", "Simulation");
                Remplacer(typeof(MyEntity), "UpdateAfterSimulation", "ApresLaSimulation");
                Remplacer(typeof(MyEntity), "UpdateAfterSimulation10", "ApresLaSimulation10");
                Remplacer(typeof(MyEntity), "UpdateAfterSimulation100", "ApresLaSimulation100");
                dispatch = FileDeLaConstruction();
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("mises à jour des entités illisibles : les blocs du fil ne seront pas relevés", e);
                return;
            }

            // Deux greffons ne se disputent jamais une même étape du jeu.
            var cibles = new List<MethodBase>(etapes);
            cibles.AddRange(boucles);
            if (dispatch != null)
                cibles.Add(dispatch);
            var autres = AutresGreffons(cibles);
            if (autres.Count > 0)
            {
                Ceder(autres);
                return;
            }

            s_uneFois = PoserLaReecriture("mises à jour uniques des entités", etapes[0], "AvantLesEntites", "ApresLesEntites");
            PoserLEtape("mises à jour des entités avant la simulation", etapes[1]);
            s_simulation = PoserLaReecriture("simulation des entités", etapes[2], "AvantLesEntites", "ApresLesEntites");
            PoserLEtape("mises à jour des entités après la simulation", etapes[3]);
            s_avant = PoserLaReecriture("entités avant la simulation", boucles[0], null, null);
            s_avant10 = PoserLaReecriture("entités avant la simulation, toutes les 10 images", boucles[1], null, null);
            s_avant100 = PoserLaReecriture("entités avant la simulation, toutes les 100 images", boucles[2], null, null);
            s_apres = PoserLaReecriture("entités après la simulation", boucles[3], null, null);
            s_apres10 = PoserLaReecriture("entités après la simulation, toutes les 10 images", boucles[4], null, null);
            s_apres100 = PoserLaReecriture("entités après la simulation, toutes les 100 images", boucles[5], null, null);
            if (dispatch != null)
                s_fileDeLaConstruction = PoserLaReecriture("file des constructions", dispatch, null, null, "ReecrireLaFile");
        }

        private static void Remplacer(Type type, string methode, string remplacant)
        {
            var duJeu = Accroches.Methode(type, methode);
            var notre = typeof(MesureDuFil).GetMethod(remplacant, BindingFlags.Public | BindingFlags.Static);
            if (notre == null)
                throw new MissingMemberException("MesureDuFil." + remplacant + " introuvable");
            s_remplacants[duJeu] = notre;
        }

        // La file d'une construction : MyCubeGrid.Dispatch appelle chaque
        // rappel par MyCubeGrid.Invoke, qui ne fait que « u.Callback() ». Si
        // Invoke fait autre chose dans cette version du jeu, la file n'est pas
        // réécrite : son temps reste alors compté à la construction.
        private static MethodBase FileDeLaConstruction()
        {
            try
            {
                var grille = typeof(MyCubeGrid);
                var update = AccessTools.Inner(grille, "Update");
                s_rappelDeLaFile = update == null ? null : AccessTools.Field(update, "Callback");
                s_invoke = AccessTools.GetDeclaredMethods(grille).FirstOrDefault(m => m.Name == "Invoke"
                    && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == update.MakeByRefType());
                var dispatch = AccessTools.GetDeclaredMethods(grille).FirstOrDefault(m => m.Name == "Dispatch" && m.GetParameters().Length == 2);
                if (s_rappelDeLaFile == null || s_invoke == null || dispatch == null)
                    throw new MissingMemberException("MyCubeGrid.Dispatch, Invoke ou Update.Callback introuvable");

                var corps = PatchProcessor.GetOriginalInstructions(s_invoke).Where(i => i.opcode != OpCodes.Nop).ToList();
                var attendu = corps.Count == 4
                    && corps[0].opcode == OpCodes.Ldarg_1
                    && corps[1].opcode == OpCodes.Ldfld && Equals(corps[1].operand, s_rappelDeLaFile)
                    && corps[2].opcode == OpCodes.Callvirt && corps[2].operand is MethodInfo
                    && ((MethodInfo)corps[2].operand).DeclaringType == typeof(Action)
                    && corps[3].opcode == OpCodes.Ret;
                if (!attendu)
                    throw new NotSupportedException("MyCubeGrid.Invoke ne fait plus seulement « u.Callback() »");
                return dispatch;
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("file des constructions illisible : ses rappels seront comptés à la construction", e);
                return null;
            }
        }

        private static List<string> AutresGreffons(IEnumerable<MethodBase> cibles)
        {
            var autres = new List<string>();
            foreach (var cible in cibles)
            {
                try
                {
                    foreach (var autre in Accroches.AutresProprietaires(cible, s_harmony.Id))
                    {
                        if (!autres.Contains(autre))
                            autres.Add(autre);
                    }
                }
                catch (Exception e)
                {
                    Fautes.Noter("greffons voisins des blocs du fil", e);
                }
            }
            return autres;
        }

        private static void Ceder(List<string> autres)
        {
            s_cedeeA = string.Join(", ", autres.ToArray());
            GreffonSirCorrectifsPulsar.Journal.Info("les mises à jour des entités sont déjà accrochées par " + s_cedeeA
                + " : la mesure des blocs du fil lui cède la place pour la session");
        }

        private static Accroche PoserLaReecriture(string nom, MethodBase cible, string prefixe, string postfixe, string reecriture = "Reecrire")
        {
            s_remplaces = 0;
            var accroche = Accroches.Poser(s_harmony, Suivi.BlocsDuFil, nom, () => cible, prefixe, postfixe, reecriture, typeof(MesureDuFil));
            s_accroches.Add(accroche);
            if (prefixe != null)
                s_accrochesDesEtapes[cible] = accroche;
            if (accroche.Posee && s_remplaces == 0)
            {
                // Rien à réécrire : la méthode a changé dans cette version du jeu.
                accroche.Posee = false;
                GreffonSirCorrectifsPulsar.Journal.Erreur("« " + nom + " » : aucun appel d'entité trouvé à mesurer, ce relevé manquera", null);
            }
            return accroche;
        }

        private static void PoserLEtape(string nom, MethodBase cible)
        {
            var accroche = Accroches.Poser(s_harmony, Suivi.BlocsDuFil, nom, () => cible, "AvantLesEntites", "ApresLesEntites", typeof(MesureDuFil));
            s_accroches.Add(accroche);
            s_accrochesDesEtapes[cible] = accroche;
        }

        // Réécriture des boucles de l'orchestrateur : « entite.UpdateX() »
        // devient « MesureDuFil.X(entite) », qui fait le même appel.
        private static IEnumerable<CodeInstruction> Reecrire(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                var appel = instruction.operand as MethodInfo;
                MethodInfo remplacant;
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
                    && appel != null && s_remplacants.TryGetValue(appel, out remplacant))
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = remplacant;
                    s_remplaces++;
                }
                yield return instruction;
            }
        }

        // Réécriture de la file d'une construction : « this.Invoke(ref u, queue) »
        // devient « MesureDuFil.AppelDeLaConstruction(this, u.Callback) ».
        private static IEnumerable<CodeInstruction> ReecrireLaFile(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                    && instruction.operand is MethodInfo && Equals(instruction.operand, s_invoke))
                {
                    var retrait = new CodeInstruction(OpCodes.Pop);
                    retrait.labels.AddRange(instruction.labels);
                    retrait.blocks.AddRange(instruction.blocks);
                    yield return retrait;
                    yield return new CodeInstruction(OpCodes.Ldfld, s_rappelDeLaFile);
                    yield return new CodeInstruction(OpCodes.Call, typeof(MesureDuFil).GetMethod("AppelDeLaConstruction", BindingFlags.Public | BindingFlags.Static));
                    s_remplaces++;
                    continue;
                }
                yield return instruction;
            }
        }

        // ---- Les appels du jeu, faits par le greffon -------------------------

        public static void UneFoisAvantLImage(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateOnceBeforeFrame();
                return;
            }
            try
            {
                entite.UpdateOnceBeforeFrame();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_uneFois);
            }
        }

        public static void AvantLaSimulation(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateBeforeSimulation();
                return;
            }
            try
            {
                entite.UpdateBeforeSimulation();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_avant);
            }
        }

        public static void AvantLaSimulation10(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateBeforeSimulation10();
                return;
            }
            try
            {
                entite.UpdateBeforeSimulation10();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_avant10);
            }
        }

        public static void AvantLaSimulation100(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateBeforeSimulation100();
                return;
            }
            try
            {
                entite.UpdateBeforeSimulation100();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_avant100);
            }
        }

        public static void Simulation(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.Simulate();
                return;
            }
            try
            {
                entite.Simulate();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_simulation);
            }
        }

        public static void ApresLaSimulation(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateAfterSimulation();
                return;
            }
            try
            {
                entite.UpdateAfterSimulation();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_apres);
            }
        }

        public static void ApresLaSimulation10(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateAfterSimulation10();
                return;
            }
            try
            {
                entite.UpdateAfterSimulation10();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_apres10);
            }
        }

        public static void ApresLaSimulation100(MyEntity entite)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                entite.UpdateAfterSimulation100();
                return;
            }
            try
            {
                entite.UpdateAfterSimulation100();
            }
            finally
            {
                FinirLEntite(entite, debut, enfants, s_apres100);
            }
        }

        public static void AppelDeLaConstruction(MyCubeGrid construction, Action rappel)
        {
            long debut, enfants;
            if (!s_echantillon || !Debuter(out debut, out enfants))
            {
                rappel();
                return;
            }
            try
            {
                rappel();
            }
            finally
            {
                FinirLeRappel(construction, rappel, debut, enfants);
            }
        }

        // Une construction peut être mise à jour sur un fil de travail : seul
        // le fil du jeu est mesuré.
        private static bool Debuter(out long debut, out long enfants)
        {
            if (Environment.CurrentManagedThreadId != s_filDuJeu)
            {
                debut = 0;
                enfants = 0;
                return false;
            }
            enfants = s_enfants;
            s_enfants = 0;
            debut = Stopwatch.GetTimestamp();
            return true;
        }

        private static void FinirLEntite(MyEntity entite, long debut, long enfants, Accroche accroche)
        {
            var fin = Stopwatch.GetTimestamp();
            try
            {
                Attribuer(entite, fin - debut - s_enfants);
                if (accroche != null)
                    accroche.Appels++;
            }
            catch (Exception e)
            {
                Fautes.Noter("mesure d'une entité au fil du jeu", e);
            }
            Clore(debut, fin, enfants);
        }

        private static void FinirLeRappel(MyCubeGrid construction, Action rappel, long debut, long enfants)
        {
            var fin = Stopwatch.GetTimestamp();
            try
            {
                AttribuerLeRappel(construction, rappel, fin - debut - s_enfants);
                if (s_fileDeLaConstruction != null)
                    s_fileDeLaConstruction.Appels++;
            }
            catch (Exception e)
            {
                Fautes.Noter("mesure d'un rappel de construction au fil du jeu", e);
            }
            Clore(debut, fin, enfants);
        }

        // Ce que la mesure a pris après l'appel est son coût ; l'appel entier,
        // mesure comprise, est retiré du temps propre de celui qui l'a fait.
        private static void Clore(long debut, long fin, long enfants)
        {
            var apres = Stopwatch.GetTimestamp();
            s_cout += apres - fin;
            s_enfants = enfants + (apres - debut);
        }

        private static void Attribuer(MyEntity entite, long ticks)
        {
            var bloc = entite as MyCubeBlock;
            if (bloc == null)
            {
                var morceau = entite as MyEntitySubpart;
                if (morceau != null)
                    bloc = morceau.Parent as MyCubeBlock;
            }
            if (bloc != null)
            {
                AttribuerAuBloc(bloc, ticks);
                return;
            }
            if (entite is MyCharacter)
            {
                Releve.Noter(Personnages(), entite.EntityId, 0, null, ticks);
                return;
            }
            var construction = entite as MyCubeGrid;
            if (construction != null)
            {
                Releve.Noter(Constructions(), construction.EntityId, construction.EntityId, construction, ticks);
                return;
            }
            Releve.Noter(TypeDeLEntite(entite.GetType()), entite.EntityId, 0, null, ticks);
        }

        private static void AttribuerLeRappel(MyCubeGrid construction, Action rappel, long ticks)
        {
            var cible = rappel.Target;
            var bloc = cible as MyCubeBlock;
            if (bloc == null)
            {
                var composant = cible as MyEntityComponentBase;
                if (composant != null)
                    bloc = composant.Entity as MyCubeBlock;
            }
            if (bloc != null)
            {
                AttribuerAuBloc(bloc, ticks);
                return;
            }
            var id = construction == null ? 0 : construction.EntityId;
            Releve.Noter(TypeDuSysteme(rappel.Method), id, id, construction, ticks);
        }

        private static void AttribuerAuBloc(MyCubeBlock bloc, long ticks)
        {
            var construction = bloc.CubeGrid;
            Releve.Noter(TypeDuBloc(bloc), bloc.EntityId, construction == null ? 0 : construction.EntityId, construction, ticks);
        }

        // ---- Les types -------------------------------------------------------

        private static TypeDuFil TypeDuBloc(MyCubeBlock bloc)
        {
            var definition = bloc.BlockDefinition;
            if (definition == null)
            {
                var nom = bloc.GetType().Name;
                return Releve.Type("bloc:" + nom, SorteDuFil.Bloc, nom, null, null);
            }
            TypeDuFil type;
            if (s_typesDesBlocs.TryGetValue(definition, out type))
                return type;
            var typeId = definition.Id.TypeId.ToString();
            const string Prefixe = "MyObjectBuilder_";
            if (typeId.StartsWith(Prefixe, StringComparison.Ordinal))
                typeId = typeId.Substring(Prefixe.Length);
            var sousType = definition.Id.SubtypeName;
            type = Releve.Type("bloc:" + typeId + "/" + sousType, SorteDuFil.Bloc, typeId, sousType, definition.DisplayNameText);
            s_typesDesBlocs[definition] = type;
            return type;
        }

        private static TypeDuFil TypeDeLEntite(Type genre)
        {
            TypeDuFil type;
            if (s_typesDesEntites.TryGetValue(genre, out type))
                return type;
            type = Releve.Type("autre:" + genre.Name, SorteDuFil.Autre, genre.Name, null, null);
            s_typesDesEntites[genre] = type;
            return type;
        }

        private static TypeDuFil TypeDuSysteme(MethodInfo methode)
        {
            TypeDuFil type;
            if (methode != null && s_typesDesSystemes.TryGetValue(methode, out type))
                return type;
            var porteur = methode == null || methode.DeclaringType == null ? "-" : methode.DeclaringType.Name;
            var nom = methode == null ? "-" : methode.Name;
            type = Releve.Type("systeme:" + porteur + "." + nom, SorteDuFil.Systeme, porteur, nom, null);
            if (methode != null)
                s_typesDesSystemes[methode] = type;
            return type;
        }

        private static TypeDuFil Personnages()
        {
            return s_personnages ?? (s_personnages = Releve.Type("personnage", SorteDuFil.Personnage, "personnage", null, null));
        }

        private static TypeDuFil Constructions()
        {
            return s_constructions ?? (s_constructions = Releve.Type("construction", SorteDuFil.Construction, "construction", null, null));
        }

        private static string NomDeLaConstruction(object porteur)
        {
            var construction = porteur as MyCubeGrid;
            return construction == null ? null : construction.DisplayName;
        }

        // ---- Le temps de toutes les mises à jour des entités -----------------

        private static void AvantLesEntites(MethodBase __originalMethod)
        {
            Accroche accroche;
            if (__originalMethod != null && s_accrochesDesEtapes.TryGetValue(__originalMethod, out accroche))
                accroche.Appels++;
            s_debutDesEntites = s_echantillon && Environment.CurrentManagedThreadId == s_filDuJeu ? Stopwatch.GetTimestamp() : 0;
        }

        private static void ApresLesEntites()
        {
            if (s_debutDesEntites == 0)
                return;
            s_ticksDesEntites += Stopwatch.GetTimestamp() - s_debutDesEntites;
            s_debutDesEntites = 0;
        }

        // ---- Sur le fil du jeu, par Collecte ---------------------------------

        // La partie est prête, sur l'un de nos serveurs.
        internal static void Commencer()
        {
            try
            {
                s_filDuJeu = Environment.CurrentManagedThreadId;
                if (s_cedeeA == null && s_accroches.Count > 0)
                {
                    // Un greffon chargé après le nôtre a pu s'accrocher depuis.
                    var cibles = new List<MethodBase>();
                    foreach (var accroche in s_accroches)
                    {
                        if (accroche.Methode != null)
                            cibles.Add(accroche.Methode);
                    }
                    var autres = AutresGreffons(cibles);
                    if (autres.Count > 0)
                    {
                        Retirer(cibles);
                        Ceder(autres);
                    }
                }

                var champs = new ChampsDuReleve();
                champs.Texte("version_du_greffon", typeof(MesureDuFil).Assembly.GetName().Version.ToString());
                if (s_cedeeA != null)
                {
                    champs.Texte("mesure", "cédée");
                    champs.Texte("greffons", s_cedeeA);
                    Collecte.Releveur.Noter(Suivi.BlocsDuFil, "partie prête", champs);
                    DireLaPlaceCedee();
                    return;
                }
                if (!Posee())
                {
                    champs.Texte("mesure", "impossible");
                    Collecte.Releveur.Noter(Suivi.BlocsDuFil, "partie prête", champs);
                    return;
                }

                var orchestrateur = MyEntities.Orchestrator;
                if (orchestrateur != null && orchestrateur.GetType().FullName != "Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator")
                    GreffonSirCorrectifsPulsar.Journal.Info("le jeu met à jour ses entités par " + orchestrateur.GetType().FullName
                        + " : les blocs du fil risquent de manquer");

                Etalonner();
                s_intervalle = ReleveBlocsDuFil.Intervalles[0];
                s_images = 0;
                s_enfants = 0;
                s_ticksDesEntites = 0;
                s_debutDeLaMinute = DateTime.UtcNow;
                s_debutDeLaMinuteTicks = Stopwatch.GetTimestamp();
                Releve.Oublier();
                s_enRoute = true;

                champs.Texte("mesure", "en route");
                champs.Entier("intervalle", s_intervalle);
                champs.Nombre("cout_par_appel_hors_echantillon_ns", s_ticksParAppelHorsEchantillon * 1e9 / Stopwatch.Frequency, 2);
                Collecte.Releveur.Noter(Suivi.BlocsDuFil, "partie prête", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("début des blocs du fil", e);
            }
        }

        private static bool Posee()
        {
            foreach (var accroche in s_accroches)
            {
                if (accroche.Posee)
                    return true;
            }
            return false;
        }

        private static void Retirer(List<MethodBase> cibles)
        {
            foreach (var cible in cibles)
            {
                try
                {
                    s_harmony.Unpatch(cible, HarmonyPatchType.All, s_harmony.Id);
                }
                catch (Exception e)
                {
                    Fautes.Noter("retrait des accroches des blocs du fil", e);
                }
            }
            foreach (var accroche in s_accroches)
                accroche.Posee = false;
        }

        private static void DireLaPlaceCedee()
        {
            if (s_cessionDite)
                return;
            s_cessionDite = true;
            try
            {
                var utilitaires = MyAPIGateway.Utilities;
                if (utilitaires != null)
                    utilitaires.ShowNotification(string.Format(TexteDeLaPlaceCedee, s_cedeeA), DureeDeLaNotification, "White");
            }
            catch (Exception e)
            {
                Fautes.Noter("notification des blocs du fil", e);
            }
        }

        // Ce que coûte l'accroche à un appel d'une image non mesurée : un test
        // et un appel de plus. Mesuré une fois, au début de la partie.
        private static void Etalonner()
        {
            const int Tours = 100000;
            s_echantillon = false;
            var debut = Stopwatch.GetTimestamp();
            for (var i = 0; i < Tours; i++)
                AppelDeLaConstruction(null, s_vide);
            var milieu = Stopwatch.GetTimestamp();
            for (var i = 0; i < Tours; i++)
                s_vide();
            var fin = Stopwatch.GetTimestamp();
            s_ticksParAppelHorsEchantillon = Math.Max(0.0, ((milieu - debut) - (fin - milieu)) / (double)Tours);
            s_cout += fin - debut;
        }

        private static void Rien()
        {
        }

        // À chaque image, sur le fil du jeu : clôt la mesure de l'image
        // écoulée, la minute si elle a changé, et décide de la suivante.
        internal static void Image()
        {
            if (!s_enRoute)
                return;
            try
            {
                Releve.Image(s_echantillon, s_ticksDesEntites);
                Releve.Cout(s_cout);
                s_cout = 0;
                s_ticksDesEntites = 0;
                s_enfants = 0;

                var maintenant = DateTime.UtcNow;
                if (maintenant.Minute != s_debutDeLaMinute.Minute || maintenant - s_debutDeLaMinute >= TimeSpan.FromMinutes(1))
                    FermerLaMinute(maintenant);

                s_echantillon = ++s_images % s_intervalle == 0;
            }
            catch (Exception e)
            {
                s_echantillon = false;
                Fautes.Noter("image des blocs du fil", e);
            }
        }

        private static void FermerLaMinute(DateTime maintenant)
        {
            var ticks = Stopwatch.GetTimestamp();
            var lignes = Releve.Fermer(s_debutDeLaMinute, maintenant, ticks - s_debutDeLaMinuteTicks, s_intervalle, s_ticksParAppelHorsEchantillon);
            var debutDeLEnvoi = Stopwatch.GetTimestamp();
            foreach (var ligne in lignes)
                Collecte.Releveur.Noter(Suivi.BlocsDuFil, ligne.Evenement, ligne.Champs);
            s_intervalle = ReleveBlocsDuFil.IntervalleSuivant(s_intervalle, Releve.DernierePart);
            s_debutDeLaMinute = maintenant;
            s_debutDeLaMinuteTicks = ticks;
            // Les lignes mises en forme : compté à la minute suivante.
            s_cout += Stopwatch.GetTimestamp() - debutDeLEnvoi;
        }

        // La partie est quittée.
        internal static void Oublier()
        {
            s_enRoute = false;
            s_echantillon = false;
            s_enfants = 0;
            s_cout = 0;
            s_ticksDesEntites = 0;
            s_debutDesEntites = 0;
            Releve.Oublier();
            s_typesDesBlocs.Clear();
            s_typesDesEntites.Clear();
            s_typesDesSystemes.Clear();
        }
    }
}
