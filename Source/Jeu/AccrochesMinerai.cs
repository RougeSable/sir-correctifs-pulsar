using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using ParallelTasks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Gui;
using Sandbox.Game.Weapons;
using Sandbox.Game.World;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRageMath;

namespace SirCorrectifsPulsar
{
    // Les accroches du suivi des marqueurs de minerai. Le jeu du joueur calcule
    // lui-même chaque détecteur de sa faction relié par antenne (et sa foreuse
    // portative) : un passage lance quatre requêtes en priorité « très basse »,
    // et les marqueurs ne s'enregistrent qu'à sa fin. On observe ce passage
    // sans jamais relire le terrain.
    //
    // Aucune méthode d'une ligne n'est accrochée (RegisterMarker, Track,
    // accesseurs) : le jeu pourrait l'intégrer à ses appelants.
    internal static class AccrochesMinerai
    {
        private const string Groupe = "Sandbox.Game.Entities.Cube.MyOreDepositGroup";
        private const string Requete = "Sandbox.Game.Entities.Cube.MyDepositQuery";

        private static Type s_groupe;
        private static AccessTools.FieldRef<object, int> s_tachesEnCours;
        private static AccessTools.FieldRef<object, bool> s_libere;
        private static AccessTools.FieldRef<object, MyVoxelBase> s_voxel;
        private static AccessTools.FieldRef<object, MyOreDetectorComponent> s_composant;
        private static AccessTools.FieldRef<object, Vector3I> s_min;
        private static AccessTools.FieldRef<object, Vector3I> s_max;
        private static AccessTools.FieldRef<object, IDictionary> s_principal;
        private static AccessTools.FieldRef<object, IDictionary> s_echange;
        private static AccessTools.FieldRef<object, IList> s_resultat;
        private static AccessTools.FieldRef<object, IList> s_cellulesVides;
        private static AccessTools.FieldRef<object, bool> s_annulee;
        private static AccessTools.FieldRef<MyHudOreMarkers, HashSet<MyEntityOreDeposit>> s_marqueursDuHud;
        private static AccessTools.FieldRef<MyOreDetector, MyOreDetectorComponent> s_composantDuBloc;
        private static AccessTools.FieldRef<MyHandDrill, MyOreDetectorComponent> s_composantDeLaForeuse;

        private static Accroche s_debut, s_fin, s_nettoyage, s_miseEnFile, s_acces, s_foreuse, s_foreuseEnMain, s_foreuseRangee;

        // Les accroches qui ne posent qu'une cause : comptées dans leur préfixe,
        // comme les autres, pour que le contrôle des accroches les juge aussi.
        private static Accroche s_causeBlocEteint, s_causeAlimentation, s_causeRetireDeLaGrille, s_causeFermeture,
            s_causeVoxelFerme, s_causeVoxelSorti, s_causeControlePerdu;

        // L'identité de chaque groupe, et ce qu'on sait de son détecteur.
        private sealed class InfoDuGroupe
        {
            public long Identifiant;
            public long DetecteurId;
            public double Portee;
            public DescriptionDuDetecteur Detecteur;
        }

        private static readonly ConditionalWeakTable<object, InfoDuGroupe> s_groupes = new ConditionalWeakTable<object, InfoDuGroupe>();
        private static long s_prochainGroupe;

        // Ce que l'appelant du jeu est en train de faire quand il retire des
        // marqueurs : posé avant, relu au nettoyage. Fil du jeu seulement.
        private static string s_cause;

        public static void Poser(Harmony harmony)
        {
            var moi = typeof(AccrochesMinerai);
            try
            {
                s_groupe = Accroches.Type(Groupe);
                s_tachesEnCours = AccessTools.FieldRefAccess<int>(s_groupe, "m_tasksRunning");
                s_libere = AccessTools.FieldRefAccess<bool>(s_groupe, "m_disposed");
                s_voxel = AccessTools.FieldRefAccess<MyVoxelBase>(s_groupe, "m_voxelMap");
                s_composant = AccessTools.FieldRefAccess<MyOreDetectorComponent>(s_groupe, "m_orderDetectorComponent");
                s_min = AccessTools.FieldRefAccess<Vector3I>(s_groupe, "m_lastDetectionMin");
                s_max = AccessTools.FieldRefAccess<Vector3I>(s_groupe, "m_lastDetectionMax");
                s_principal = AccessTools.FieldRefAccess<IDictionary>(s_groupe, "m_depositsByCellCoord_Main");
                s_echange = AccessTools.FieldRefAccess<IDictionary>(s_groupe, "m_depositsByCellCoord_Swap");
                var requete = Accroches.Type(Requete);
                s_resultat = AccessTools.FieldRefAccess<IList>(requete, "m_result");
                s_cellulesVides = AccessTools.FieldRefAccess<IList>(requete, "m_emptyCells");
                s_annulee = AccessTools.FieldRefAccess<bool>(requete, "m_canceled");
                s_marqueursDuHud = AccessTools.FieldRefAccess<MyHudOreMarkers, HashSet<MyEntityOreDeposit>>("m_markers");
                s_composantDuBloc = AccessTools.FieldRefAccess<MyOreDetector, MyOreDetectorComponent>("m_oreDetectorComponent");
                s_composantDeLaForeuse = AccessTools.FieldRefAccess<MyHandDrill, MyOreDetectorComponent>("m_oreDetectorBase");
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("champs du détecteur de minerai introuvables : suivi du minerai coupé", e);
                return;
            }

            s_debut = Accroches.Poser(harmony, Suivi.Minerai, "début de passage", () => Accroches.Methode(s_groupe, "UpdateDeposits"),
                "AvantLePassage", "ApresLePassage", moi);
            s_fin = Accroches.Poser(harmony, Suivi.Minerai, "fin de requête", () => Accroches.Methode(s_groupe, "OnDepositQueryComplete"),
                "AvantLaFin", "ApresLaFin", moi);
            s_nettoyage = Accroches.Poser(harmony, Suivi.Minerai, "nettoyage", () => Accroches.Methode(s_groupe, "Cleanup"),
                "AvantLeNettoyage", null, moi);
            // Le travail d'une requête est chronométré (travail_ms,
            // processeur_ms) : jamais par un préfixe et un suffixe sur son
            // DoWork, qui tourne sur les fils de travail. L'appel par lequel
            // MyDepositQuery.Start confie la requête au jeu est encadré : le
            // greffon lui confie à la place une enveloppe de la requête, qui
            // fait son DoWork entre deux lectures de l'horloge.
            try
            {
                s_rappelDeLaRequete = AccessTools.Property(Accroches.Type(Requete), "CompletionCallback");
                if (s_rappelDeLaRequete == null)
                    throw new MissingMemberException(Requete + ".CompletionCallback introuvable");
                s_miseEnFile = Accroches.Encadrer(harmony, Suivi.Minerai, "mise en file",
                    () => Accroches.Methode(typeof(Parallel), "Start", typeof(IWork), typeof(Action)),
                    () => new System.Reflection.MethodBase[] { Accroches.Methode(Accroches.Type(Requete), "Start") },
                    "LancerLaRequete", moi);
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("requêtes du détecteur illisibles : leur travail ne sera pas mesuré", e);
            }
            s_acces = Accroches.Poser(harmony, Suivi.Minerai, "accès du détecteur", () => Accroches.Methode(typeof(MyOreDetector), "UpdateBeforeSimulation100"),
                "CauseAccesPerdu", "ApresLeDetecteur", moi);
            s_foreuse = Accroches.Poser(harmony, Suivi.Minerai, "foreuse portative", () => Accroches.Methode(typeof(MyHandDrill), "UpdateBeforeSimulation100"),
                "CauseForeuseSansCourant", "ApresLaForeuse", moi);
            s_foreuseEnMain = Accroches.Poser(harmony, Suivi.Minerai, "foreuse en main", () => Accroches.Methode(typeof(MyHandDrill), "OnControlAcquired"),
                null, "ForeusePrise", moi);
            s_foreuseRangee = Accroches.Poser(harmony, Suivi.Minerai, "foreuse rangée", () => Accroches.Methode(typeof(MyHandDrill), "OnControlReleased"),
                "ForeuseRangee", "FinDeCause", moi);

            // Les autres appelants qui retirent des marqueurs : seule leur cause est posée.
            s_causeBlocEteint = PoserUneCause(harmony, "cause : bloc éteint", typeof(MyOreDetector), "OnEnabledChanged", "CauseBlocEteint");
            s_causeAlimentation = PoserUneCause(harmony, "cause : alimentation", typeof(MyOreDetector), "Receiver_IsPoweredChanged", "CauseAlimentation");
            s_causeRetireDeLaGrille = PoserUneCause(harmony, "cause : retiré de la grille", typeof(MyOreDetector), "OnUnregisteredFromGridSystems", "CauseRetireDeLaGrille");
            s_causeFermeture = PoserUneCause(harmony, "cause : fermeture", typeof(MyOreDetector), "MyOreDetector_OnClose", "CauseFermeture");
            s_causeVoxelFerme = PoserUneCause(harmony, "cause : voxel fermé", s_groupe, "VoxelMap_OnClose", "CauseVoxelFerme");
            s_causeVoxelSorti = PoserUneCause(harmony, "cause : voxel sorti", typeof(MyOreDetectorComponent), "RemoveVoxelMapsOutOfRange", "CauseVoxelSorti");
            s_causeControlePerdu = PoserUneCause(harmony, "cause : contrôle perdu", typeof(MyOreDetectorComponent), "Update", "CauseControlePerdu");
        }

        private static Accroche PoserUneCause(Harmony harmony, string nom, Type type, string methode, string prefixe)
        {
            return Accroches.Poser(harmony, Suivi.Minerai, nom, () => Accroches.Methode(type, methode), prefixe, "FinDeCause", typeof(AccrochesMinerai));
        }

        public static void Oublier()
        {
            s_cause = null;
        }

        // ---- Causes des retraits -------------------------------------------

        private static void Poser(string cause, out string avant)
        {
            avant = s_cause;
            s_cause = cause;
        }

        private static void CauseAccesPerdu(out string __state) { Poser("perte d'accès", out __state); }
        private static void CauseForeuseSansCourant(out string __state) { Poser("foreuse sans courant", out __state); }

        // Pose la cause et compte l'appel de son accroche quand la collecte tourne.
        private static void PoserEtCompter(Accroche accroche, string cause, out string avant)
        {
            Poser(cause, out avant);
            if (Collecte.Actif && accroche != null)
                accroche.Compter();
        }

        private static void CauseBlocEteint(out string __state) { PoserEtCompter(s_causeBlocEteint, "bloc éteint", out __state); }
        private static void CauseAlimentation(out string __state) { PoserEtCompter(s_causeAlimentation, "alimentation", out __state); }
        private static void CauseRetireDeLaGrille(out string __state) { PoserEtCompter(s_causeRetireDeLaGrille, "retiré des systèmes de la grille", out __state); }
        private static void CauseFermeture(out string __state) { PoserEtCompter(s_causeFermeture, "fermeture du bloc", out __state); }
        private static void CauseVoxelFerme(out string __state) { PoserEtCompter(s_causeVoxelFerme, "voxel fermé", out __state); }
        private static void CauseVoxelSorti(out string __state) { PoserEtCompter(s_causeVoxelSorti, "voxel sorti de portée", out __state); }
        private static void CauseControlePerdu(out string __state) { PoserEtCompter(s_causeControlePerdu, "contrôle perdu", out __state); }

        private static void FinDeCause(string __state)
        {
            s_cause = __state;
        }

        // ---- Groupes et détecteurs -----------------------------------------

        private static InfoDuGroupe Info(object groupe)
        {
            return s_groupes.GetValue(groupe, g => new InfoDuGroupe { Identifiant = System.Threading.Interlocked.Increment(ref s_prochainGroupe) });
        }

        private static DescriptionDuDetecteur Detecteur(InfoDuGroupe info, object groupe)
        {
            if (info.Detecteur != null && info.Detecteur.Identifiant == info.DetecteurId)
            {
                if (info.Portee > 0)
                    info.Detecteur.Portee = info.Portee;
                return info.Detecteur;
            }
            info.Detecteur = Decrire(info.DetecteurId, info.Portee, groupe == null ? null : s_composant(groupe));
            return info.Detecteur;
        }

        // La foreuse que le joueur tient en main, ou nulle.
        private static MyHandDrill ForeuseEnMain()
        {
            var personnage = Collecte.PersonnageDuJoueur();
            return personnage == null ? null : personnage.CurrentWeapon as MyHandDrill;
        }

        public static DescriptionDuDetecteur Decrire(long detecteurId, double portee, MyOreDetectorComponent composant = null)
        {
            // La foreuse en main se reconnaît à son composant de détection : elle
            // n'est pas toujours enregistrée parmi les entités du monde.
            if (composant != null)
            {
                var enMain = ForeuseEnMain();
                if (enMain != null && ReferenceEquals(s_composantDeLaForeuse(enMain), composant))
                    return DescriptionDuDetecteur.ForeusePortative(detecteurId, portee > 0 ? portee : composant.DetectionRadius);
            }

            MyEntity entite;
            if (!MyEntities.TryGetEntityById(detecteurId, out entite) || entite == null)
                return new DescriptionDuDetecteur { Identifiant = detecteurId, Bloc = FormeDesReleves.Inconnu, Portee = portee };

            var foreuse = entite as MyHandDrill;
            if (foreuse != null)
                return DescriptionDuDetecteur.ForeusePortative(detecteurId, portee);

            var bloc = entite as MyOreDetector;
            if (bloc != null)
            {
                var grille = bloc.CubeGrid;
                return new DescriptionDuDetecteur
                {
                    Identifiant = detecteurId,
                    Bloc = bloc.DisplayNameText,
                    TypeDeBloc = bloc.BlockDefinition == null ? null : bloc.BlockDefinition.Id.SubtypeName,
                    Grille = grille == null ? null : grille.DisplayName,
                    GrilleId = grille == null ? 0 : grille.EntityId,
                    Portee = portee > 0 ? portee : s_composantDuBloc(bloc).DetectionRadius,
                    Antenne = bloc.BroadcastUsingAntennas,
                };
            }
            return new DescriptionDuDetecteur { Identifiant = detecteurId, Bloc = entite.DisplayName ?? entite.GetType().Name, Portee = portee };
        }

        private static int MarqueursDuHud()
        {
            var marqueurs = s_marqueursDuHud(MyHud.OreMarkers);
            return marqueurs == null ? 0 : marqueurs.Count;
        }

        // Les marqueurs que le groupe vient d'enregistrer, tels que le joueur
        // les voit : un par minerai et par cellule.
        private static List<MarqueurVu> MarqueursDu(object groupe)
        {
            var liste = new List<MarqueurVu>();
            var principal = s_principal(groupe);
            if (principal == null)
                return liste;
            foreach (var valeur in principal.Values)
            {
                var gisement = valeur as MyEntityOreDeposit;
                if (gisement == null || gisement.VoxelMap == null)
                    continue;
                var voxel = gisement.VoxelMap;
                var origine = voxel.PositionComp.GetPosition() - (Vector3D)voxel.StorageMin;
                var cellule = gisement.CellCoord;
                foreach (var donnee in gisement.Materials)
                {
                    var materiau = donnee.Material;
                    var minerai = materiau == null ? null : NomDuMinerai(materiau);
                    var position = origine + donnee.AverageLocalPosition;
                    var cle = voxel.EntityId + ":" + cellule.X + ":" + cellule.Y + ":" + cellule.Z + ":" + (materiau == null ? "?" : materiau.Id.SubtypeName);
                    liste.Add(new MarqueurVu(cle, minerai, position.X, position.Y, position.Z));
                }
            }
            return liste;
        }

        private static string NomDuMinerai(VRage.Game.MyVoxelMaterialDefinition materiau)
        {
            try
            {
                var nom = MyHud.OreMarkers.GetOreName(materiau);
                if (!string.IsNullOrEmpty(nom))
                    return nom;
            }
            catch (Exception e)
            {
                Fautes.Noter("nom du minerai", e);
            }
            return materiau.MinedOre;
        }

        // ---- Passages -------------------------------------------------------

        private static void AvantLePassage(object __instance, out int __state)
        {
            __state = -1;
            if (!Collecte.Actif)
                return;
            try
            {
                __state = s_tachesEnCours(__instance);
            }
            catch (Exception e)
            {
                Fautes.Noter("début de passage", e);
            }
        }

        private static void ApresLePassage(object __instance, int __state, ref BoundingSphereD worldDetectionSphere, long detectorId)
        {
            if (__state < 0 || !Collecte.Actif)
                return;
            try
            {
                s_debut.Compter();
                var info = Info(__instance);
                info.DetecteurId = detectorId;
                info.Portee = worldDetectionSphere.Radius;
                var detecteur = Detecteur(info, __instance);
                var apres = s_tachesEnCours(__instance);

                if (__state == 0 && apres > 0)
                {
                    var min = s_min(__instance);
                    var max = s_max(__instance);
                    long cellules = (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1);
                    Collecte.Passages.Commencer(info.Identifiant, detecteur, cellules, Collecte.Marqueurs.Marqueurs(info.Identifiant), DateTime.UtcNow);
                }
                else if (__state != 0)
                    Collecte.Passages.Appel(info.Identifiant, "occupé", detecteur);
                else if (MySession.Static == null || !MySession.Static.Ready)
                    Collecte.Passages.Appel(info.Identifiant, "session pas prête", detecteur);
                else
                    Collecte.Passages.Appel(info.Identifiant, "sauté", detecteur);
            }
            catch (Exception e)
            {
                Fautes.Noter("début de passage", e);
            }
        }

        private struct EtatDeLaFin
        {
            public bool Suivi;
            public bool Libere;
            public int Principal;
        }

        private static void AvantLaFin(object __instance, out EtatDeLaFin __state)
        {
            __state = default(EtatDeLaFin);
            if (!Collecte.Actif)
                return;
            try
            {
                // Le rappel tourne dans la mise à jour de la session, avant celle
                // du greffon : la mesure de cette requête, prise sur son fil de
                // travail, attend encore dans la file. On la vide ici, sur le fil
                // du jeu, pour qu'elle compte dans ce passage et non dans le
                // suivant.
                Collecte.ViderLesMesures();
                __state.Suivi = true;
                __state.Libere = s_libere(__instance);
                var principal = s_principal(__instance);
                __state.Principal = principal == null ? 0 : principal.Count;
            }
            catch (Exception e)
            {
                __state.Suivi = false;
                Fautes.Noter("fin de requête", e);
            }
        }

        private static void ApresLaFin(object __instance, EtatDeLaFin __state)
        {
            if (!__state.Suivi || !Collecte.Actif)
                return;
            try
            {
                s_fin.Compter();
                var info = Info(__instance);
                var detecteur = Detecteur(info, __instance);
                var restantes = s_tachesEnCours(__instance);
                if (__state.Libere)
                {
                    Collecte.Passages.Jetee(info.Identifiant, detecteur, restantes);
                    return;
                }
                if (restantes != 0)
                    return;

                // Le passage est complet : le jeu vient de retirer les anciens
                // marqueurs et d'enregistrer les nouveaux (ou de tout jeter).
                var composant = s_composant(__instance);
                var jete = composant != null && composant.WillDiscardNextQuery;
                var actuels = jete ? new List<MarqueurVu>() : MarqueursDu(__instance);
                var position = Collecte.PositionDuJoueur();
                int apparus, disparus;
                Collecte.Marqueurs.Ecart(info.Identifiant, actuels, out apparus, out disparus);
                var principal = s_principal(__instance);
                Collecte.Passages.Terminer(info.Identifiant, detecteur, DateTime.UtcNow, __state.Principal,
                    principal == null ? 0 : principal.Count, apparus, disparus, MarqueursDuHud(), jete);
                Collecte.Marqueurs.FinDePassage(info.Identifiant, detecteur, actuels, position.X, position.Y, position.Z);
            }
            catch (Exception e)
            {
                Fautes.Noter("fin de passage", e);
            }
        }

        private static void AvantLeNettoyage(object __instance)
        {
            if (!Collecte.Actif)
                return;
            try
            {
                s_nettoyage.Compter();
                if (s_libere(__instance))
                    return;
                var info = Info(__instance);
                var principal = s_principal(__instance);
                var echange = s_echange(__instance);
                var retires = (principal == null ? 0 : principal.Count) + (echange == null ? 0 : echange.Count);
                var orphelines = s_tachesEnCours(__instance);
                var detecteur = Detecteur(info, __instance);
                if (retires > 0 || orphelines > 0)
                    Collecte.Passages.Nettoyer(info.Identifiant, detecteur, s_cause, orphelines, retires);
                var position = Collecte.PositionDuJoueur();
                Collecte.Marqueurs.Retirer(info.Identifiant, detecteur, s_cause ?? FormeDesReleves.Inconnu, position.X, position.Y, position.Z);
            }
            catch (Exception e)
            {
                Fautes.Noter("nettoyage des marqueurs", e);
            }
        }

        // ---- Requêtes (fils de travail) ------------------------------------

        private static System.Reflection.PropertyInfo s_rappelDeLaRequete;

        // L'appel par lequel MyDepositQuery.Start confie la requête au jeu
        // (Parallel.Start), fait ici à sa place, exactement une fois. Quand la
        // collecte tourne, le jeu reçoit l'enveloppe de la requête au lieu de
        // la requête elle-même : mêmes options, même priorité, même rappel.
        public static Task LancerLaRequete(IWork travail, Action rappel)
        {
            var requete = travail as IPrioritizedWork;
            if (!Collecte.Actif || requete == null || s_miseEnFile == null)
                return Parallel.Start(travail, rappel);
            RequeteMesuree enveloppe = null;
            try
            {
                s_miseEnFile.Compter();
                var fin = s_rappelDeLaRequete.GetValue(requete, null) as Delegate;
                var cible = fin == null ? null : fin.Target;
                var groupe = cible == null || s_groupe == null || !s_groupe.IsInstanceOfType(cible) ? 0 : Info(cible).Identifiant;
                enveloppe = new RequeteMesuree(requete, groupe, Stopwatch.GetTimestamp());
            }
            catch (Exception e)
            {
                Fautes.Noter("mise en file d'une requête", e);
            }
            return Parallel.Start(enveloppe ?? travail, rappel);
        }

        // L'enveloppe d'une requête : son DoWork fait celui de la requête,
        // exactement une fois, avec les mêmes données, entre deux lectures de
        // l'horloge (et du temps processeur du fil). Une exception du jeu
        // passe telle quelle, sans mesure.
        private sealed class RequeteMesuree : IPrioritizedWork
        {
            private readonly IPrioritizedWork m_requete;
            private readonly long m_groupe;
            private readonly long m_miseEnFile;

            public RequeteMesuree(IPrioritizedWork requete, long groupe, long miseEnFile)
            {
                m_requete = requete;
                m_groupe = groupe;
                m_miseEnFile = miseEnFile;
            }

            public WorkOptions Options
            {
                get { return m_requete.Options; }
            }

            public WorkPriority Priority
            {
                get { return m_requete.Priority; }
            }

            public void DoWork(WorkData workData = null)
            {
                var processeur = TempsProcesseur();
                var debut = Stopwatch.GetTimestamp();
                m_requete.DoWork(workData);
                var fin = Stopwatch.GetTimestamp();
                ApresLeTravail(m_requete, m_groupe, m_miseEnFile, debut, fin, processeur, TempsProcesseur());
            }
        }

        private static void ApresLeTravail(object requete, long groupe, long miseEnFile, long debut, long fin,
            long processeurAvant, long processeurApres)
        {
            if (!Collecte.Actif)
                return;
            try
            {
                var resultat = s_resultat(requete);
                var vides = s_cellulesVides(requete);
                Collecte.Mesures.Enqueue(new MesureDeRequete
                {
                    Groupe = groupe,
                    AttenteMs = Collecte.Millisecondes(miseEnFile, debut),
                    TravailMs = Collecte.Millisecondes(debut, fin),
                    ProcesseurMs = processeurApres < 0 || processeurAvant < 0 ? -1 : (processeurApres - processeurAvant) / 10000.0,
                    Gisements = resultat == null ? 0 : resultat.Count,
                    CellulesVides = vides == null ? 0 : vides.Count,
                    Annulee = s_annulee(requete),
                });
            }
            catch (Exception e)
            {
                Fautes.Noter("fin du travail d'une requête", e);
            }
        }

        // Le temps processeur du fil courant (noyau et utilisateur), en
        // centaines de nanosecondes ; -1 hors de Windows.
        private static bool s_sansTempsProcesseur;

        private static long TempsProcesseur()
        {
            if (s_sansTempsProcesseur)
                return -1;
            try
            {
                long creation, sortie, noyau, utilisateur;
                if (!GetThreadTimes(GetCurrentThread(), out creation, out sortie, out noyau, out utilisateur))
                    return -1;
                return noyau + utilisateur;
            }
            catch (Exception e)
            {
                // Une seule fois : ensuite le temps processeur manque aux lignes.
                s_sansTempsProcesseur = true;
                Fautes.Noter("temps processeur d'un fil", e);
                return -1;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long sortie, out long noyau, out long utilisateur);

        // ---- Détecteurs : accès gagné ou perdu ------------------------------

        private static void ApresLeDetecteur(MyOreDetector __instance, string __state)
        {
            s_cause = __state;
            if (!Collecte.Actif)
                return;
            try
            {
                s_acces.Compter();
                var composant = s_composantDuBloc(__instance);
                var accesLocal = __instance.HasLocalPlayerAccess();
                var enMarche = __instance.IsWorking;
                var antenne = composant.BroadcastUsingAntennas;
                var groupe = false;
                var accesAuGroupe = false;
                var personnage = Collecte.PersonnageDuJoueur();
                var grille = __instance.GetTopMostParent() as MyCubeGrid;
                if (grille != null)
                {
                    var logique = MyCubeGridGroups.Static.Logical.GetGroup(grille);
                    groupe = logique != null;
                    accesAuGroupe = logique != null && personnage != null && personnage.HasAccessToLogicalGroup(logique.GroupData);
                }
                var memeGrille = MemeGrilleQueLeJoueur(__instance);
                var calcule = CalculeChezLeJoueur(accesLocal, enMarche, personnage != null, antenne, accesAuGroupe, memeGrille);
                var portee = composant.DetectionRadius;

                var etat = (accesLocal ? "A" : "a") + (enMarche ? "M" : "m") + (antenne ? "N" : "n") + (groupe ? "G" : "g")
                    + (accesAuGroupe ? "L" : "l") + (memeGrille ? "R" : "r") + (calcule ? "C" : "c") + ((int)portee);
                var avant = Collecte.Etats.Dernier(__instance.EntityId);
                if (!Collecte.Etats.AChange(__instance.EntityId, etat))
                    return;
                // Un détecteur qui n'a jamais été à la portée du joueur ne fait pas de ligne.
                if (avant == null && !(accesLocal && enMarche))
                    return;
                if (avant != null && !(avant[0] == 'A' && avant[1] == 'M') && !(accesLocal && enMarche))
                    return;

                var champs = new ChampsDuReleve();
                Decrire(__instance.EntityId, portee).Decrire(champs);
                champs.OuiNon("acces_local", accesLocal);
                champs.OuiNon("en_marche", enMarche);
                champs.OuiNon("groupe_logique", groupe);
                champs.OuiNon("acces_au_groupe", accesAuGroupe);
                champs.OuiNon("joueur_sur_la_grille", memeGrille);
                champs.OuiNon("personnage", personnage != null);
                champs.OuiNon("calcule_chez_le_joueur", calcule);
                Collecte.Releveur.Noter(Suivi.Minerai, "accès du détecteur", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("accès du détecteur", e);
            }
        }

        // La condition même de MyOreDetector.UpdateBeforeSimulation100 : le
        // jeu du joueur ne calcule le détecteur que s'il marche, que le joueur
        // y a accès et qu'il a un personnage ; diffusé par antenne, il faut en
        // plus l'accès au groupe logique de sa grille ; sinon, l'entité que le
        // joueur contrôle doit être sur la même grille ou le même groupe logique.
        private static bool CalculeChezLeJoueur(bool accesLocal, bool enMarche, bool personnage, bool antenne,
            bool accesAuGroupe, bool memeGrille)
        {
            if (!enMarche || !accesLocal || !personnage)
                return false;
            return antenne ? accesAuGroupe : memeGrille;
        }

        // L'entité que le joueur contrôle est-elle sur la grille du détecteur
        // (ou dans son groupe logique) ? Comme le jeu, sans antenne.
        private static bool MemeGrilleQueLeJoueur(MyOreDetector detecteur)
        {
            var session = MySession.Static;
            var controle = session == null ? null : session.ControlledEntity;
            if (controle == null || controle.Entity == null)
                return false;
            var sienne = controle.Entity.GetTopMostParent() as MyCubeGrid;
            if (sienne == null)
                return false;
            if (ReferenceEquals(sienne, detecteur.CubeGrid))
                return true;
            var groupeDuJoueur = MyCubeGridGroups.Static.Logical.GetGroup(sienne);
            var groupeDuDetecteur = MyCubeGridGroups.Static.Logical.GetGroup(detecteur.CubeGrid);
            return groupeDuJoueur != null && groupeDuDetecteur != null && groupeDuJoueur.GroupData == groupeDuDetecteur.GroupData;
        }

        // ---- Foreuse portative ---------------------------------------------

        private static bool DuJoueur(MyHandDrill foreuse)
        {
            var personnage = Collecte.PersonnageDuJoueur();
            return foreuse != null && personnage != null && ReferenceEquals(foreuse.Owner, personnage);
        }

        private static void NoterLaForeuse(MyHandDrill foreuse, string evenement, bool siChange)
        {
            var personnage = foreuse.Owner;
            var alimentee = foreuse.SinkComp != null && foreuse.SinkComp.IsPoweredByType(MyResourceDistributorComponent.ElectricityId);
            var iem = personnage != null && personnage.IsElectricityDisrupted;
            var etat = (alimentee ? "A" : "a") + (iem ? "I" : "i");
            var change = Collecte.Etats.AChange(-foreuse.EntityId, etat);
            if (siChange && !change)
                return;
            var champs = new ChampsDuReleve();
            champs.OuiNon("alimentee", alimentee && !iem);
            champs.OuiNon("courant", alimentee);
            champs.OuiNon("iem", iem);
            var composant = s_composantDeLaForeuse(foreuse);
            if (composant != null)
                champs.Nombre("portee", composant.DetectionRadius, 0);
            Collecte.Releveur.Noter(Suivi.Minerai, evenement, champs);
        }

        private static void ForeusePrise(MyHandDrill __instance)
        {
            if (!Collecte.Actif)
                return;
            try
            {
                s_foreuseEnMain.Compter();
                if (DuJoueur(__instance))
                    NoterLaForeuse(__instance, "foreuse en main", false);
            }
            catch (Exception e)
            {
                Fautes.Noter("foreuse en main", e);
            }
        }

        private static void ForeuseRangee(MyHandDrill __instance, out string __state)
        {
            Poser("foreuse rangée", out __state);
            if (!Collecte.Actif)
                return;
            try
            {
                s_foreuseRangee.Compter();
                if (!DuJoueur(__instance))
                    return;
                Collecte.Etats.Oublier(-__instance.EntityId);
                Collecte.Releveur.Noter(Suivi.Minerai, "foreuse rangée", new ChampsDuReleve());
            }
            catch (Exception e)
            {
                Fautes.Noter("foreuse rangée", e);
            }
        }

        private static void ApresLaForeuse(MyHandDrill __instance, string __state)
        {
            s_cause = __state;
            if (!Collecte.Actif)
                return;
            try
            {
                s_foreuse.Compter();
                if (DuJoueur(__instance))
                    NoterLaForeuse(__instance, "foreuse", true);
            }
            catch (Exception e)
            {
                Fautes.Noter("foreuse portative", e);
            }
        }
    }
}
