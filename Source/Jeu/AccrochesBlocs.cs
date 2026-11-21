using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Render.Scene;
using VRage.Render.Scene.Components;
using VRageMath;
using VRageRender;

namespace SirCorrectifsPulsar
{
    // Les accroches du suivi des blocs invisibles (cas de Guesn) : l'hologramme
    // du bloc en main, ou le bloc tout juste posé à son premier aspect de
    // chantier, reste invisible jusqu'à la soudure suivante.
    //
    // Piste lue dans le jeu : l'objet est créé côté affichage pendant que son
    // modèle charge encore. Il reçoit alors un modèle d'attente
    // (Models\Debug\LoadingQuad.mwm), et la bascule vers le vrai modèle peut
    // rater de trois façons : exception au chargement, chemin écrit avec une
    // autre casse (MyInstanceManager.OnReloadModels compare exactement), ou
    // modèle refusé puis converti à l'ancien chemin d'affichage, qui ne dessine
    // rien.
    //
    // On ne suit que l'hologramme en main et les blocs à moins de 15 m dont la
    // construction n'est pas finie, au plus 64 objets à la fois. Côté
    // affichage, chaque accroche ne fait qu'une recherche dans une table tant
    // que l'objet ou le modèle n'est pas suivi. Ni les passes d'affichage, ni
    // PrepareLodData, ni UpdateRenderEntity (appelé à chaque image) ne sont
    // accrochés. Ce qui est vu hors du fil du jeu passe par une file sans
    // verrou (Collecte.NoterDepuisUnAutreFil).
    internal static class AccrochesBlocs
    {
        public const string NomDeLHologramme = "Cube builder, part";
        private const double RayonDesBlocs = 15.0;
        private const int ObjetsSuivisMaximum = 64;
        private const int ImagesEntreDeuxReleves = 45;

        // Un objet de rendu suivi, et ce qui l'a créé.
        private sealed class ObjetSuivi
        {
            public uint Id;
            public string Sorte;
            public string Modele;
            public string Bloc;
            public long BlocId;
            public string DernierEtat;
            // L'affichage crée l'objet un peu après la demande du jeu : un objet
            // pas encore vu n'est retiré qu'après plusieurs relevés sans lui.
            public bool Vu;
            public int Absences;
        }

        private static readonly ConcurrentDictionary<uint, ObjetSuivi> s_objets = new ConcurrentDictionary<uint, ObjetSuivi>();

        // Les modèles des objets suivis, en minuscules et avec des « \ » : on
        // retrouve ainsi un modèle quelle que soit la façon dont son chemin est
        // écrit, ce qui est justement le piège à relever.
        private static readonly ConcurrentDictionary<string, string> s_modeles = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, string> s_etatsDesModeles = new ConcurrentDictionary<string, string>();

        // Le bloc dont le jeu crée l'objet de rendu, sur ce fil.
        [ThreadStatic] private static MyCubeBlock t_blocCourant;

        private static int s_images;
        private static bool s_pleinDit;

        private static Accroche s_creation, s_ajout, s_aspect, s_modeleDemande, s_chargement, s_finDeChargement,
            s_modelePose, s_rechargement, s_ancienChemin, s_reconstruction, s_releve;

        private static MethodInfo s_composantDInstance;
        private static MethodInfo s_composantRendable;
        private static MethodInfo s_dessine;
        private static FieldInfo s_instanceDuComposant;
        private static FieldInfo s_actifsDuGestionnaire;
        private static MethodInfo s_estLeModeleDAttente;
        private static PropertyInfo s_reglagesDeDetail;

        public static void Poser(Harmony harmony)
        {
            var moi = typeof(AccrochesBlocs);

            // Fil du jeu.
            s_creation = Accroches.Poser(harmony, Suivi.Blocs, "création d'un objet de rendu",
                () => Accroches.Methode(typeof(MyRenderProxy), "CreateRenderEntity"), null, "ObjetCree", moi);
            s_ajout = Accroches.Poser(harmony, Suivi.Blocs, "bloc ajouté à la scène",
                () => Accroches.Methode(typeof(MyCubeBlock), "OnAddedToScene"), "AvantLAjout", "ApresLAjout", moi);
            s_aspect = Accroches.Poser(harmony, Suivi.Blocs, "aspect d'un bloc",
                () => Accroches.Methode(typeof(MyCubeBlock), "UpdateVisual"), "AvantLAspect", "ApresLAspect", moi);

            // Fil de l'affichage (et fils de travail pour le chargement).
            Type usine, instance, gestionnaire, convertisseur, rendable;
            try
            {
                usine = Accroches.Type("VRage.Render11.GeometryStage2.Model.MyModelFactory");
                instance = Accroches.Type("VRage.Render11.GeometryStage2.Instancing.MyInstance");
                gestionnaire = Accroches.Type("VRage.Render11.GeometryStage2.Instancing.MyInstanceManager");
                convertisseur = Accroches.Type("VRage.Render11.GeometryStage2.Common.MyComponentConverter");
                rendable = Accroches.Type("VRage.Render11.Scene.Components.MyRenderableComponent");
                var composant = Accroches.Type("VRage.Render11.GeometryStage2.Instancing.MyInstanceComponent");
                s_composantDInstance = Accroches.Methode(typeof(MyActor), "GetComponent").MakeGenericMethod(composant);
                s_composantRendable = Accroches.Methode(typeof(MyActor), "GetComponent").MakeGenericMethod(rendable);
                s_dessine = Accroches.Methode(instance, "CheckGbufferVisible");
                s_instanceDuComposant = AccessTools.Field(composant, "m_instance");
                s_actifsDuGestionnaire = AccessTools.Field(gestionnaire, "m_instances");
                s_estLeModeleDAttente = Accroches.Methode(usine, "IsDummyModel");
                s_reglagesDeDetail = AccessTools.Property(Accroches.Type("VRageRender.MyCommon"), "LoddingSettings");
                if (s_instanceDuComposant == null || s_actifsDuGestionnaire == null || s_reglagesDeDetail == null)
                    throw new MissingMemberException("m_instance, m_instances ou LoddingSettings introuvable");
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("fil de l'affichage illisible : le chargement des modèles des blocs ne sera pas relevé", e);
                return;
            }

            s_modeleDemande = Accroches.Poser(harmony, Suivi.Blocs, "modèle demandé",
                () => Accroches.Methode(usine, "GetOrCreateModels"), null, "ModeleDemande", moi);
            PoserLeChargement(harmony, usine);
            s_finDeChargement = Accroches.Poser(harmony, Suivi.Blocs, "fin de chargement",
                () => Accroches.Methode(usine, "FinalizeLoad"), "AvantLaFinDeChargement", null, moi);
            s_modelePose = Accroches.Poser(harmony, Suivi.Blocs, "modèle posé sur l'objet",
                () => Accroches.Methode(instance, "SetModel"), null, "ModelePose", moi);
            s_rechargement = Accroches.Poser(harmony, Suivi.Blocs, "modèle rechargé",
                () => Accroches.Methode(gestionnaire, "OnReloadModels"), "AvantLeRechargement", "ApresLeRechargement", moi);
            s_ancienChemin = Accroches.Poser(harmony, Suivi.Blocs, "passage à l'ancien chemin d'affichage",
                () => Accroches.Methode(convertisseur, "ConvertActorToTheOldPipeline"), "AncienChemin", null, moi);
            s_reconstruction = Accroches.Poser(harmony, Suivi.Blocs, "objets d'affichage reconstruits",
                () => Accroches.Methode(rendable, "RebuildRenderProxies"), null, "ObjetsReconstruits", moi);
            s_releve = Accroches.Poser(harmony, Suivi.Blocs, "relevé des objets suivis",
                () => Accroches.Methode(usine, "CompleteAsyncTasks"), null, "ReleverLesObjets", moi);
        }

        // Le chargement d'un modèle est chronométré (duree_ms) : jamais par un
        // préfixe et un suffixe sur MyModelFactory.LoadInternal, qui tourne
        // sur les fils de travail. Son appel est encadré là où l'usine le
        // fait, lu dans le jeu : la demande d'un modèle (chargement direct),
        // le chargement en tâche de fond et le rechargement de tous les
        // modèles. Le modèle d'attente, chargé à l'ouverture du moteur de
        // rendu (OnDeviceInit), n'est jamais suivi : son appel n'est pas
        // encadré.
        private static void PoserLeChargement(Harmony harmony, Type usine)
        {
            try
            {
                s_charger = AppelDuChargement(Accroches.Methode(usine, "LoadInternal"), usine);
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("chargement des modèles illisible : sa durée ne sera pas relevée", e);
                return;
            }
            s_chargement = Accroches.Encadrer(harmony, Suivi.Blocs, "chargement d'un modèle",
                () => Accroches.Methode(usine, "LoadInternal"),
                () => new MethodBase[]
                {
                    Accroches.Methode(usine, "GetOrCreateModels"),
                    Accroches.Methode(usine, "LoadInternalAsync"),
                    Accroches.Methode(usine, "ReloadModels"),
                },
                "EncadrerLeChargement", typeof(AccrochesBlocs));
        }

        // L'appel de LoadInternal (une méthode privée de l'usine, sur un
        // modèle d'un type interne au moteur de rendu), tel que l'usine le
        // fait : l'usine, le chemin, le modèle.
        private static Action<object, string, object> s_charger;

        private static Action<object, string, object> AppelDuChargement(MethodInfo charger, Type usine)
        {
            var parametres = charger.GetParameters();
            if (charger.IsStatic || parametres.Length != 2 || parametres[0].ParameterType != typeof(string))
                throw new NotSupportedException("MyModelFactory.LoadInternal ne prend plus (chemin, modèle)");
            var lUsine = System.Linq.Expressions.Expression.Parameter(typeof(object), "usine");
            var leChemin = System.Linq.Expressions.Expression.Parameter(typeof(string), "filepath");
            var leModele = System.Linq.Expressions.Expression.Parameter(typeof(object), "model");
            var appel = System.Linq.Expressions.Expression.Call(
                System.Linq.Expressions.Expression.Convert(lUsine, usine), charger,
                leChemin, System.Linq.Expressions.Expression.Convert(leModele, parametres[1].ParameterType));
            return System.Linq.Expressions.Expression.Lambda<Action<object, string, object>>(appel, lUsine, leChemin, leModele).Compile();
        }

        public static void Oublier()
        {
            s_objets.Clear();
            s_modeles.Clear();
            s_etatsDesModeles.Clear();
            s_pleinDit = false;
        }

        // ---- Outils ----------------------------------------------------------

        private static string Normaliser(string chemin)
        {
            return chemin == null ? null : chemin.Replace('/', '\\').ToLowerInvariant();
        }

        // Le modèle suivi que désigne ce chemin, écrit de n'importe quelle
        // façon (relatif ou complet, n'importe quelle casse), ou nul.
        private static string ModeleSuivi(string chemin)
        {
            if (chemin == null || s_modeles.IsEmpty)
                return null;
            var normal = Normaliser(chemin);
            string modele;
            if (s_modeles.TryGetValue(normal, out modele))
                return modele;
            foreach (var paire in s_modeles)
            {
                if (normal.EndsWith(paire.Key, StringComparison.Ordinal))
                    return paire.Value;
            }
            return null;
        }

        private static ObjetSuivi Objet(uint id)
        {
            ObjetSuivi objet;
            return s_objets.IsEmpty || !s_objets.TryGetValue(id, out objet) ? null : objet;
        }

        private static readonly ConcurrentDictionary<string, MemberInfo> s_membres = new ConcurrentDictionary<string, MemberInfo>();

        // Lit un champ ou une propriété par son nom ; un nom introuvable lève,
        // et l'appelant le dit (Fautes).
        private static object Lire(object objet, string nom)
        {
            if (objet == null)
                return null;
            var type = objet.GetType();
            var membre = s_membres.GetOrAdd(type.FullName + "." + nom, c =>
                (MemberInfo)AccessTools.Field(type, nom) ?? AccessTools.Property(type, nom));
            var champ = membre as FieldInfo;
            if (champ != null)
                return champ.GetValue(objet);
            var propriete = membre as PropertyInfo;
            if (propriete != null)
                return propriete.GetValue(objet, null);
            throw new MissingMemberException(type.FullName + "." + nom + " introuvable");
        }

        private static string OuiNon(object valeur)
        {
            return valeur is bool ? ((bool)valeur ? "oui" : "non") : FormeDesReleves.Inconnu;
        }

        private static string Texte(object valeur)
        {
            if (valeur == null)
                return FormeDesReleves.Inconnu;
            var formattable = valeur as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : valeur.ToString();
        }

        private static void Noter(string evenement, ChampsDuReleve champs)
        {
            Collecte.NoterDepuisUnAutreFil(Suivi.Blocs, evenement, champs);
        }

        private static void DecrireLObjet(ChampsDuReleve champs, ObjetSuivi objet)
        {
            champs.Entier("objet", objet.Id);
            champs.Texte("sorte", objet.Sorte);
            champs.Texte("modele", objet.Modele);
            if (objet.Bloc != null)
            {
                champs.Texte("bloc", objet.Bloc);
                champs.Entier("bloc_id", objet.BlocId);
            }
        }

        // ---- Fil du jeu : quel bloc crée son objet de rendu ------------------

        // Le bloc est-il à suivre : à moins de 15 m du joueur, construction pas finie.
        private static bool ASuivre(MyCubeBlock bloc)
        {
            if (bloc == null || bloc.SlimBlock == null || bloc.SlimBlock.BuildLevelRatio >= 1f)
                return false;
            var joueur = Collecte.PositionDuJoueur();
            return Vector3D.DistanceSquared(bloc.PositionComp.GetPosition(), joueur) <= RayonDesBlocs * RayonDesBlocs;
        }

        private static void AvantLAjout(MyCubeBlock __instance, out MyCubeBlock __state)
        {
            __state = t_blocCourant;
            if (!Collecte.Actif)
                return;
            s_ajout.Compter();
            try
            {
                if (ASuivre(__instance))
                    t_blocCourant = __instance;
            }
            catch (Exception e)
            {
                Fautes.Noter("bloc ajouté à la scène", e);
            }
        }

        // Après l'ajout, le jeu a demandé le préchargement des aspects de
        // chantier du bloc : c'est son heure qu'on note, à comparer avec la
        // création de l'objet de rendu (faite juste avant, au premier aspect).
        private static void ApresLAjout(MyCubeBlock __instance, MyCubeBlock __state)
        {
            var suivi = ReferenceEquals(t_blocCourant, __instance) && !ReferenceEquals(__state, __instance);
            t_blocCourant = __state;
            if (!suivi || !Collecte.Actif)
                return;
            try
            {
                var champs = new ChampsDuReleve();
                DecrireLeBloc(champs, __instance);
                var definition = __instance.BlockDefinition;
                champs.Entier("aspects_precharges", definition == null || definition.BuildProgressModels == null ? 0 : definition.BuildProgressModels.Length);
                Collecte.Releveur.Noter(Suivi.Blocs, "aspects préchargés", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("bloc ajouté à la scène", e);
            }
        }

        private static void DecrireLeBloc(ChampsDuReleve champs, MyCubeBlock bloc)
        {
            champs.Texte("bloc", bloc.DisplayNameText);
            champs.Entier("bloc_id", bloc.EntityId);
            if (bloc.BlockDefinition != null)
                champs.Texte("type", bloc.BlockDefinition.Id.SubtypeName);
            champs.Texte("grille", bloc.CubeGrid == null ? null : bloc.CubeGrid.DisplayName);
            champs.Nombre("avancement", bloc.SlimBlock == null ? -1 : bloc.SlimBlock.BuildLevelRatio, 3);
            champs.Nombre("distance", Vector3D.Distance(bloc.PositionComp.GetPosition(), Collecte.PositionDuJoueur()), 1);
        }

        private struct EtatDeLAspect
        {
            public MyCubeBlock Precedent;
            public bool Suivi;
            public string Modele;
            public uint Objet;
        }

        private static uint PremierObjet(MyCubeBlock bloc)
        {
            var objets = bloc.Render == null ? null : bloc.Render.RenderObjectIDs;
            return objets == null || objets.Length == 0 ? uint.MaxValue : objets[0];
        }

        private static void AvantLAspect(MyCubeBlock __instance, out EtatDeLAspect __state)
        {
            __state = new EtatDeLAspect { Precedent = t_blocCourant };
            if (!Collecte.Actif)
                return;
            s_aspect.Compter();
            try
            {
                if (!ASuivre(__instance))
                    return;
                __state.Suivi = true;
                __state.Modele = __instance.Model == null ? null : __instance.Model.AssetName;
                __state.Objet = PremierObjet(__instance);
                t_blocCourant = __instance;
            }
            catch (Exception e)
            {
                Fautes.Noter("aspect d'un bloc", e);
            }
        }

        private static void ApresLAspect(MyCubeBlock __instance, EtatDeLAspect __state)
        {
            t_blocCourant = __state.Precedent;
            if (!__state.Suivi || !Collecte.Actif)
                return;
            try
            {
                var modele = __instance.Model == null ? null : __instance.Model.AssetName;
                var objet = PremierObjet(__instance);
                if (modele == __state.Modele && objet == __state.Objet)
                    return;
                var champs = new ChampsDuReleve();
                DecrireLeBloc(champs, __instance);
                champs.Texte("modele", modele);
                champs.Texte("modele_precedent", __state.Modele);
                champs.Texte("objet", objet == uint.MaxValue ? null : objet.ToString(CultureInfo.InvariantCulture));
                champs.Texte("objet_precedent", __state.Objet == uint.MaxValue ? null : __state.Objet.ToString(CultureInfo.InvariantCulture));
                champs.Nombre("transparence", __instance.Render == null ? 0 : __instance.Render.Transparency, 2);
                Collecte.Releveur.Noter(Suivi.Blocs, "aspect du bloc", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("aspect d'un bloc", e);
            }
        }

        // L'objet de rendu de l'hologramme, ou d'un bloc suivi, vient d'être
        // créé : on le suit désormais jusqu'à sa disparition.
        private static void ObjetCree(string debugName, string model, float dithering, bool fadeIn, uint __result)
        {
            if (!Collecte.Actif)
                return;
            s_creation.Compter();
            try
            {
                var bloc = t_blocCourant;
                var hologramme = debugName != null && debugName.StartsWith(NomDeLHologramme, StringComparison.Ordinal);
                if (!hologramme && bloc == null)
                    return;
                if (s_objets.Count >= ObjetsSuivisMaximum)
                {
                    if (!s_pleinDit)
                    {
                        s_pleinDit = true;
                        Collecte.Releveur.Noter(Suivi.Blocs, "suivi plein", new ChampsDuReleve().Entier("objets", ObjetsSuivisMaximum));
                    }
                    return;
                }
                var objet = new ObjetSuivi
                {
                    Id = __result,
                    Sorte = hologramme ? "hologramme" : "bloc",
                    Modele = model,
                    Bloc = hologramme ? null : bloc.DisplayNameText,
                    BlocId = hologramme ? 0 : bloc.EntityId,
                };
                if (model != null)
                    s_modeles[Normaliser(model)] = model;
                s_objets[__result] = objet;

                var champs = new ChampsDuReleve();
                DecrireLObjet(champs, objet);
                champs.Texte("nom", debugName);
                champs.Nombre("transparence", dithering, 2);
                champs.OuiNon("fondu", fadeIn);
                if (!hologramme)
                {
                    champs.Nombre("avancement", bloc.SlimBlock == null ? -1 : bloc.SlimBlock.BuildLevelRatio, 3);
                    champs.Nombre("distance", Vector3D.Distance(bloc.PositionComp.GetPosition(), Collecte.PositionDuJoueur()), 1);
                }
                Collecte.Releveur.Noter(Suivi.Blocs, "objet de rendu créé", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("création d'un objet de rendu", e);
            }
        }

        // ---- Fil de l'affichage ----------------------------------------------

        private static bool EstLeModeleDAttente(object usine, object modele)
        {
            return modele != null && (bool)s_estLeModeleDAttente.Invoke(usine, new[] { modele });
        }

        private static void ModeleDemande(object __instance, string filepath, object __result)
        {
            if (!Collecte.Actif)
                return;
            s_modeleDemande.Compter();
            if (s_modeles.IsEmpty)
                return;
            try
            {
                var modele = ModeleSuivi(filepath);
                if (modele == null)
                    return;
                var attente = EstLeModeleDAttente(__instance, __result);
                var etat = filepath + (attente ? "|attente" : "|chargé");
                string avant;
                if (s_etatsDesModeles.TryGetValue(Normaliser(filepath), out avant) && avant == etat)
                    return;
                s_etatsDesModeles[Normaliser(filepath)] = etat;
                var champs = new ChampsDuReleve();
                champs.Texte("modele", filepath);
                champs.OuiNon("modele_d_attente", attente);
                if (filepath != modele)
                    champs.Texte("modele_suivi", modele);
                Noter("modèle demandé", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("modèle demandé", e);
            }
        }

        // L'appel que l'usine fait de LoadInternal, fait ici à sa place,
        // exactement une fois et avec les mêmes arguments, entre deux lectures
        // de l'horloge. Sur un fil de travail (chargement en tâche de fond) ou
        // sur le fil de l'affichage. Une exception du chargement est relevée,
        // puis relancée telle quelle : le jeu la reçoit comme avant.
        public static void EncadrerLeChargement(object usine, string filepath, object model)
        {
            if (!Collecte.Actif)
            {
                s_charger(usine, filepath, model);
                return;
            }
            if (s_chargement != null)
                s_chargement.Compter();
            var debut = Stopwatch.GetTimestamp();
            try
            {
                s_charger(usine, filepath, model);
            }
            catch (Exception e)
            {
                ApresLeChargement(e, filepath, model, Collecte.Millisecondes(debut, Stopwatch.GetTimestamp()));
                throw;
            }
            ApresLeChargement(null, filepath, model, Collecte.Millisecondes(debut, Stopwatch.GetTimestamp()));
        }

        private static void ApresLeChargement(Exception exception, string filepath, object model, double dureeMs)
        {
            if (s_modeles.IsEmpty)
                return;
            try
            {
                if (ModeleSuivi(filepath) == null)
                    return;
                var champs = new ChampsDuReleve();
                champs.Texte("modele", filepath);
                champs.Nombre("duree_ms", dureeMs, 1);
                champs.Texte("etat_de_chargement", Texte(Lire(model, "LoadState")));
                champs.Texte("valide", OuiNon(Lire(model, "IsValid")));
                if (exception != null)
                {
                    champs.Texte("exception", exception.GetType().Name);
                    champs.Texte("message", exception.Message);
                }
                Noter(exception == null ? "modèle chargé" : "chargement du modèle en échec", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("fin du chargement d'un modèle", e);
            }
        }

        private static void AvantLaFinDeChargement(object modelData, bool ditherIn)
        {
            if (!Collecte.Actif)
                return;
            s_finDeChargement.Compter();
            if (s_modeles.IsEmpty)
                return;
            try
            {
                var chemin = Lire(modelData, "FilePath") as string;
                if (ModeleSuivi(chemin) == null)
                    return;
                var champs = new ChampsDuReleve();
                champs.Texte("modele", chemin);
                champs.Texte("chemin_complet", Lire(modelData, "FullFilePath") as string);
                champs.OuiNon("fondu", ditherIn);
                Noter("bascule des objets vers le modèle chargé", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("fin de chargement", e);
            }
        }

        // Faux = la bascule vers ce modèle a raté.
        private static void ModelePose(object __instance, string modelFilepath, bool isDummyModel, bool __result)
        {
            if (!Collecte.Actif)
                return;
            s_modelePose.Compter();
            if (s_objets.IsEmpty)
                return;
            try
            {
                var id = Lire(__instance, "ActorID");
                var objet = id is uint ? Objet((uint)id) : null;
                if (objet == null)
                    return;
                var champs = new ChampsDuReleve();
                DecrireLObjet(champs, objet);
                champs.Texte("modele_pose", modelFilepath);
                champs.OuiNon("modele_d_attente", isDummyModel);
                champs.OuiNon("reussi", __result);
                Noter(__result ? "modèle posé" : "bascule ratée", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("modèle posé sur l'objet", e);
            }
        }

        // Ce qu'un objet suivi était au moment du rechargement de son modèle.
        private sealed class AvantRechargement
        {
            public object Instance;
            public ObjetSuivi Objet;
            public string Chemin;
            public bool Exact;
            public bool AttenteAvant;
        }

        // Avant : quels objets suivis le jeu va-t-il basculer ? Un objet dont le
        // chemin ne correspond qu'en ignorant la casse est la preuve directe du
        // piège : le jeu compare exactement, et le laisse au modèle d'attente.
        private static void AvantLeRechargement(object __instance, string filePath, object model, out List<AvantRechargement> __state)
        {
            __state = null;
            if (!Collecte.Actif)
                return;
            s_rechargement.Compter();
            if (filePath == null || model == null || s_objets.IsEmpty)
                return;
            try
            {
                if (ModeleSuivi(filePath) == null)
                    return;
                var actifs = Lire(s_actifsDuGestionnaire.GetValue(__instance), "Active") as IEnumerable;
                if (actifs == null)
                    return;
                foreach (var instance in actifs)
                {
                    var id = Lire(instance, "ActorID");
                    var objet = id is uint ? Objet((uint)id) : null;
                    if (objet == null)
                        continue;
                    var chemin = Lire(instance, "ModelFilepath") as string;
                    if (chemin == null || !string.Equals(chemin, filePath, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (__state == null)
                        __state = new List<AvantRechargement>();
                    __state.Add(new AvantRechargement
                    {
                        Instance = instance,
                        Objet = objet,
                        Chemin = chemin,
                        Exact = chemin == filePath,
                        AttenteAvant = Lire(instance, "IsDummyModel") is bool && (bool)Lire(instance, "IsDummyModel"),
                    });
                }
            }
            catch (Exception e)
            {
                Fautes.Noter("avant le rechargement d'un modèle", e);
            }
        }

        private static void ApresLeRechargement(string filePath, List<AvantRechargement> __state)
        {
            if (__state == null)
                return;
            try
            {
                foreach (var avant in __state)
                {
                    var champs = new ChampsDuReleve();
                    DecrireLObjet(champs, avant.Objet);
                    champs.Texte("modele_charge", filePath);
                    champs.Texte("chemin_de_l_objet", avant.Chemin);
                    champs.OuiNon("meme_chemin", avant.Exact);
                    champs.OuiNon("casse_differente", !avant.Exact);
                    champs.OuiNon("attente_avant", avant.AttenteAvant);
                    var attenteApres = Lire(avant.Instance, "IsDummyModel");
                    champs.Texte("attente_apres", OuiNon(attenteApres));
                    var ratee = !avant.Exact || (attenteApres is bool && (bool)attenteApres);
                    Noter(ratee ? "bascule vers le modèle chargé ratée" : "bascule vers le modèle chargé", champs);
                }
            }
            catch (Exception e)
            {
                Fautes.Noter("après le rechargement d'un modèle", e);
            }
        }

        private static void AncienChemin(object actor)
        {
            if (!Collecte.Actif)
                return;
            s_ancienChemin.Compter();
            if (s_objets.IsEmpty)
                return;
            try
            {
                var acteur = actor as IMyActor;
                var objet = acteur == null ? null : Objet(acteur.ID);
                if (objet == null)
                    return;
                var champs = new ChampsDuReleve();
                DecrireLObjet(champs, objet);
                Noter("passage à l'ancien chemin d'affichage", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("passage à l'ancien chemin d'affichage", e);
            }
        }

        private static void ObjetsReconstruits(object __instance)
        {
            if (!Collecte.Actif)
                return;
            s_reconstruction.Compter();
            if (s_objets.IsEmpty)
                return;
            try
            {
                var composant = __instance as MyActorComponent;
                var acteur = composant == null ? null : composant.Owner;
                var objet = acteur == null ? null : Objet(acteur.ID);
                if (objet == null)
                    return;
                var niveaux = Lire(__instance, "m_lods") as Array;
                var champs = new ChampsDuReleve();
                DecrireLObjet(champs, objet);
                champs.Entier("niveaux_de_detail", niveaux == null ? 0 : niveaux.Length);
                champs.OuiNon("rien_a_dessiner", niveaux == null || niveaux.Length == 0);
                Noter("objets d'affichage reconstruits", champs);
            }
            catch (Exception e)
            {
                Fautes.Noter("objets d'affichage reconstruits", e);
            }
        }

        // Toutes les 45 images d'affichage, après la fin des chargements :
        // l'état de chaque objet suivi, noté seulement s'il a changé.
        private static void ReleverLesObjets(object __instance)
        {
            if (!Collecte.Actif)
                return;
            s_releve.Compter();
            if (s_objets.IsEmpty || ++s_images % ImagesEntreDeuxReleves != 0)
                return;
            try
            {
                var reglages = Reglages(__instance);
                foreach (var paire in s_objets)
                {
                    var objet = paire.Value;
                    var acteur = MyIDTracker<MyActor>.FindByID(objet.Id);
                    if (acteur == null && !objet.Vu && ++objet.Absences < 10)
                        continue;
                    if (acteur == null)
                    {
                        ObjetSuivi retire;
                        s_objets.TryRemove(objet.Id, out retire);
                        var fin = new ChampsDuReleve();
                        DecrireLObjet(fin, objet);
                        Noter("objet de rendu retiré", fin);
                        continue;
                    }
                    objet.Vu = true;
                    var champs = Etat(acteur);
                    var signature = Signature(champs);
                    if (signature == objet.DernierEtat)
                        continue;
                    objet.DernierEtat = signature;
                    var ligne = new ChampsDuReleve();
                    DecrireLObjet(ligne, objet);
                    foreach (var champ in champs.Liste)
                        ligne.Texte(champ.Key, champ.Value);
                    foreach (var champ in reglages.Liste)
                        ligne.Texte(champ.Key, champ.Value);
                    Noter("état d'affichage", ligne);
                }
                if (s_objets.IsEmpty)
                    s_modeles.Clear();
            }
            catch (Exception e)
            {
                Fautes.Noter("relevé des objets suivis", e);
            }
        }

        private static ChampsDuReleve Reglages(object usine)
        {
            var champs = new ChampsDuReleve();
            var reglages = s_reglagesDeDetail.GetValue(null, null) as MyNewLoddingSettings;
            if (reglages != null)
            {
                champs.Entier("detail_min", reglages.GBuffer.MinLod);
                champs.Entier("decalage_de_detail", reglages.GBuffer.LodShift);
                champs.Nombre("multiplicateur_de_distance", reglages.Global.ObjectDistanceMult, 3);
            }
            champs.Texte("detail_min_au_chargement", Texte(Lire(usine, "MinLoadingLod")));
            return champs;
        }

        private static ChampsDuReleve Etat(MyActor acteur)
        {
            var champs = new ChampsDuReleve();
            champs.OuiNon("visible", acteur.IsVisible);
            var boite = acteur.WorldAabb;
            champs.Texte("boite", boite.Min.X <= boite.Max.X ? Taille(boite.Size) : "invalide");

            var composant = s_composantDInstance.Invoke(acteur, null);
            if (composant != null)
            {
                var instance = s_instanceDuComposant.GetValue(composant);
                champs.Texte("chemin", "nouveau");
                champs.Texte("modele_d_attente", OuiNon(Lire(instance, "IsDummyModel")));
                champs.Texte("modele_de_l_objet", Lire(instance, "ModelFilepath") as string);
                var modele = Lire(instance, "Model");
                champs.Texte("modele_valide", OuiNon(Lire(modele, "IsValid")));
                champs.Texte("etat_de_chargement", Texte(Lire(modele, "LoadState")));
                var detail = Lire(instance, "LodStrategy");
                champs.Texte("detail", Texte(Lire(detail, "CurrentLod")));
                champs.Texte("detail_en_transition", Texte(Lire(detail, "TransitionLod")));
                champs.Texte("etat_de_detail", Texte(Lire(detail, "ExplicitLodState")));
                champs.Texte("tramage", Texte(Lire(detail, "ExplicitStateData")));
                champs.Texte("dessine", instance == null ? FormeDesReleves.Inconnu : OuiNon(s_dessine.Invoke(instance, null)));
                return champs;
            }

            // L'ancien chemin d'affichage.
            champs.Texte("chemin", "ancien");
            var ancien = s_composantRendable.Invoke(acteur, null);
            if (ancien != null)
            {
                var niveaux = Lire(ancien, "m_lods") as Array;
                champs.Entier("niveaux_de_detail", niveaux == null ? 0 : niveaux.Length);
                champs.Texte("detail", Texte(Lire(ancien, "CurrentLod")));
                champs.Texte("ecarte", OuiNon(Lire(ancien, "IsCulled")));
            }
            return champs;
        }

        private static string Taille(Vector3D taille)
        {
            return taille.X.ToString("0.##", CultureInfo.InvariantCulture) + "x" + taille.Y.ToString("0.##", CultureInfo.InvariantCulture)
                + "x" + taille.Z.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static string Signature(ChampsDuReleve champs)
        {
            var texte = new System.Text.StringBuilder();
            foreach (var champ in champs.Liste)
                texte.Append(champ.Key).Append('=').Append(champ.Value).Append(';');
            return texte.ToString();
        }
    }
}
