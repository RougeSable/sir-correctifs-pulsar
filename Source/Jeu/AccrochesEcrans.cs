using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sandbox.Game.Components;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.SessionComponents;
using Sandbox.Game.World;
using Sandbox.ModAPI.Ingame;
using VRage.Game.GUI.TextPanel;
using VRageMath;
using VRageRender.Messages;

namespace SirCorrectifsPulsar
{
    // Les accroches du suivi des écrans. Chaque écran (panneau, écran d'un
    // cockpit, d'un bloc programmable ou d'un autre bloc) est mis à jour par
    // le jeu toutes les 10 images : on regarde, avant et après, ce qu'il
    // demande d'afficher et pourquoi, sans rien recalculer. Un écran au-delà de
    // la portée de son bloc plus la marge coûte une soustraction de positions,
    // rien de plus. Seuls les changements font une ligne (SuiviDesEcrans).
    //
    // Ce que le jeu demande n'est pas encore ce que le joueur voit : le fil de
    // l'affichage n'échange la texture de l'écran que lorsqu'elle est prête
    // (rendu hors écran fini pour un contenu dessiné, fichier chargé pour une
    // image). On l'observe là-bas, seulement pour les écrans qui attendent un
    // échange : MyOffscreenRenderer.Add et SubmitWork (file et rendus en vol),
    // SetTextureReady (texture dessinée), et le premier vrai de
    // CheckChangesAvailable pour l'objet de rendu de l'écran (échange fait).
    // Ces événements passent par une file sans verrou, vidée sur le fil du
    // jeu ; rien n'est écrit depuis le fil de l'affichage.
    internal static class AccrochesEcrans
    {
        private static AccessTools.FieldRef<MyTextPanelComponent, MyTerminalBlock> s_bloc;
        private static AccessTools.FieldRef<MyTextPanelComponent, string> s_textureActuelle;
        private static AccessTools.FieldRef<MyTextPanelComponent, bool> s_statique;
        private static AccessTools.FieldRef<MyTextPanelComponent, int> s_objetDeRendu;
        private static AccessTools.FieldRef<MyTextPanelComponent, int> s_dernierCalcul;
        private static AccessTools.FieldRef<MyTextPanelComponent, int> s_zone;
        private static AccessTools.FieldRef<MyTextPanelComponent, MyRenderComponentScreenAreas> s_rendu;
        private static AccessTools.FieldRef<MyTextPanelComponent, object> s_script;
        private static AccessTools.FieldRef<MyTextPanelComponent, MySerializableSpriteCollection> s_morceaux;

        // Les zones d'écran d'un bloc (PanelScreenArea) : objets de rendu et matériau.
        private static FieldInfo s_zonesDuRendu;
        private static FieldInfo s_objetsDeLaZone;
        private static FieldInfo s_materiauDeLaZone;

        // Le fil de l'affichage.
        private static FieldInfo s_fileHorsEcran;
        private static FieldInfo s_rendusEnVol;
        private static FieldInfo s_messageDuRendu;
        private static PropertyInfo s_nomDeLaTexture;

        private static Accroche s_miseAJour, s_reception, s_ajoutHorsEcran, s_lancement, s_texturePrete, s_echange;

        private static string[] s_texturesOnline;
        private static string[] s_texturesOffline;

        // Ce qu'on retient de chaque écran entre deux mises à jour.
        private sealed class Ecran
        {
            public long Cle;
            public double Portee = -1;
            public object Fournisseur;
            public bool FournisseurCherche;

            // Avant la mise à jour du jeu.
            public bool AvantSuivi;
            public bool EnMarche;
            public bool APortee;
            public bool Fonctionnel;
            public bool ObjetDeRenduPret;
            public int CalculAvant;
            public double Distance;

            // Le dernier calcul du script chez le joueur.
            public DateTime? DernierCalcul;
            public double DureeDuCalcul;

            // Les morceaux d'image reçus du serveur.
            public object DernierTableau;
            public DateTime? DernierMorceau;
            public int MorceauxDepuis32m;
            public bool Dans32m;
            public bool MorceauHorsDes32m;

            // La dernière forme vue, pour ne rien bâtir quand rien ne change.
            public string Demande;
            public string Texture;
            public string Raison;
            public string Contenu;
            public string Script;
            public bool AMorceaux;
            public bool ZoneVue;
            public bool APorteeEffectiveVue;

            // Ce que l'écran attend du fil de l'affichage.
            public List<string> ClesDEchange;
            public string TextureAttendue;
        }

        private static readonly ConditionalWeakTable<MyTextPanelComponent, Ecran> s_ecrans = new ConditionalWeakTable<MyTextPanelComponent, Ecran>();
        private static readonly Dictionary<Type, FieldInfo> s_champsDuFournisseur = new Dictionary<Type, FieldInfo>();

        // Ce que le fil de l'affichage doit guetter, posé par le fil du jeu :
        // « objet de rendu|matériau » de chaque écran qui attend un échange, et
        // le nom de la texture dessinée de chaque écran qui attend son contenu.
        private static readonly ConcurrentDictionary<string, long> s_echangesAttendus = new ConcurrentDictionary<string, long>();
        private static readonly ConcurrentDictionary<string, long> s_texturesAttendues = new ConcurrentDictionary<string, long>();

        // Ce que le fil de l'affichage a vu, vidé par le fil du jeu.
        private struct VuParLAffichage
        {
            public long Cle;
            public bool Echange;
            public EtapeDuRendu Etape;
            public string Texture;
            public DateTime Moment;
            public int File;
            public int EnVol;
        }

        private static readonly ConcurrentQueue<VuParLAffichage> s_vus = new ConcurrentQueue<VuParLAffichage>();

        // Les longueurs de la file du rendu hors écran, lues sur le fil de
        // l'affichage à chaque ajout, relues une fois par seconde.
        public static volatile int FileHorsEcran = -1;
        public static volatile int RendusEnVol = -1;

        // Le multiplicateur de portée de la réserve de dessin, relu une fois
        // par seconde dans le jeu (MySessionComponentPanels), ou celui de la
        // qualité des textures tant qu'il n'est pas lu.
        public static double Multiplicateur = -1;

        public static void Poser(Harmony harmony)
        {
            var moi = typeof(AccrochesEcrans);
            try
            {
                s_bloc = AccessTools.FieldRefAccess<MyTextPanelComponent, MyTerminalBlock>("m_block");
                s_textureActuelle = AccessTools.FieldRefAccess<MyTextPanelComponent, string>("m_previousTextureID");
                s_statique = AccessTools.FieldRefAccess<MyTextPanelComponent, bool>("m_staticContent");
                s_objetDeRendu = AccessTools.FieldRefAccess<MyTextPanelComponent, int>("m_renderObjectIndex");
                s_dernierCalcul = AccessTools.FieldRefAccess<MyTextPanelComponent, int>("m_lastUpdate");
                s_zone = AccessTools.FieldRefAccess<MyTextPanelComponent, int>("m_area");
                s_rendu = AccessTools.FieldRefAccess<MyTextPanelComponent, MyRenderComponentScreenAreas>("m_render");
                s_script = AccessTools.FieldRefAccess<MyTextPanelComponent, object>("m_script");
                s_morceaux = AccessTools.FieldRefAccess<MyTextPanelComponent, MySerializableSpriteCollection>("m_externalSprites");
                s_zonesDuRendu = Champ(typeof(MyRenderComponentScreenAreas), "m_screenAreas");
                var zone = Accroches.Type("Sandbox.Game.Components.MyRenderComponentScreenAreas+PanelScreenArea");
                s_objetsDeLaZone = Champ(zone, "RenderObjectIDs");
                s_materiauDeLaZone = Champ(zone, "Material");
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("champs des écrans introuvables : suivi des écrans coupé", e);
                return;
            }

            // La mise à jour d'un écran est chronométrée (la durée du calcul) :
            // son appel est encadré là où le jeu le fait, jamais par un préfixe
            // et un suffixe sur la méthode elle-même. Trois appelants, lus dans
            // le jeu : le panneau toutes les 10 images et à ses changements
            // (courant, modèle), et les écrans d'un bloc qui en porte plusieurs
            // (cockpit, bloc programmable…).
            s_miseAJour = Accroches.Encadrer(harmony, Suivi.Ecrans, "mise à jour d'un écran",
                () => Accroches.Methode(typeof(MyTextPanelComponent), "UpdateAfterSimulation", typeof(bool), typeof(bool)),
                () => new MethodBase[]
                {
                    Accroches.Methode(typeof(MyTextPanel), "UpdateAfterSimulation10"),
                    Accroches.Methode(typeof(MyTextPanel), "UpdateScreen"),
                    Accroches.Methode(typeof(MyMultiTextPanelComponent), "UpdateScreen"),
                },
                "EncadrerLaMiseAJour", moi);
            s_reception = Accroches.Poser(harmony, Suivi.Ecrans, "morceaux reçus du serveur",
                () => Accroches.Methode(typeof(MyTextPanelComponent), "m_externalSprites_ValueChanged"),
                "MorceauxRecus", null, moi);

            // Le fil de l'affichage.
            Type rendeur = null, texture = null, changements = null;
            try
            {
                rendeur = Accroches.Type("VRage.Render11.Render.MyOffscreenRenderer");
                texture = Accroches.Type("VRage.Render11.Resources.Internal.MyUserGeneratedTexture");
                changements = Accroches.Type("VRage.Render11.Render.MyTextureChangeManager");
                s_fileHorsEcran = Champ(rendeur, "m_toSubmitSortedList");
                s_rendusEnVol = Champ(rendeur, "m_toConsume");
                s_messageDuRendu = Champ(Accroches.Type("VRage.Render11.Render.MyOffscreenRenderer+MyData"), "Message");
                s_nomDeLaTexture = AccessTools.Property(texture, "Name");
                if (s_nomDeLaTexture == null)
                    throw new MissingMemberException(texture.FullName + ".Name introuvable");
            }
            catch (Exception e)
            {
                GreffonSirCorrectifsPulsar.Journal.Erreur("fil de l'affichage illisible : l'échange réel des images d'écran ne sera pas relevé", e);
                return;
            }
            s_ajoutHorsEcran = Accroches.Poser(harmony, Suivi.Ecrans, "rendu hors écran confié",
                () => Accroches.Methode(rendeur, "Add"), null, "RenduConfie", moi);
            s_lancement = Accroches.Poser(harmony, Suivi.Ecrans, "rendu hors écran lancé",
                () => Accroches.Methode(rendeur, "SubmitWork"), "RenduLance", null, moi);
            // Appelée par une interface depuis le rendu hors écran : le jeu ne
            // peut pas l'intégrer à cet appelant, même si elle est courte.
            s_texturePrete = Accroches.Poser(harmony, Suivi.Ecrans, "texture d'écran prête",
                () => Accroches.Methode(texture, "SetTextureReady"), null, "TexturePrete", moi);
            s_echange = Accroches.Poser(harmony, Suivi.Ecrans, "échange de texture",
                () => Accroches.Methode(changements, "CheckChangesAvailable"), null, "EchangePossible", moi);
        }

        private static FieldInfo Champ(Type type, string nom)
        {
            var champ = AccessTools.Field(type, nom);
            if (champ == null)
                throw new MissingMemberException(type.FullName + "." + nom + " introuvable");
            return champ;
        }

        public static void Oublier()
        {
            // Les états sont attachés aux écrans eux-mêmes et partent avec eux ;
            // on n'oublie ici que les textures, relues à la partie suivante, et
            // ce que le fil de l'affichage guettait.
            s_texturesOnline = null;
            s_texturesOffline = null;
            s_echangesAttendus.Clear();
            s_texturesAttendues.Clear();
            VuParLAffichage vu;
            while (s_vus.TryDequeue(out vu))
            {
            }
        }

        // Fil du jeu, à chaque image : ce que le fil de l'affichage a vu.
        public static void Vider()
        {
            VuParLAffichage vu;
            var n = 0;
            while (n++ < 512 && s_vus.TryDequeue(out vu))
            {
                if (vu.Echange)
                    Collecte.Ecrans.Echange(vu.Cle, Categorie(vu.Texture), vu.Moment);
                else
                    Collecte.Ecrans.Rendu(vu.Cle, vu.Etape, vu.Moment, vu.File, vu.EnVol);
            }
        }

        // ---- Avant / après la mise à jour du jeu ----------------------------

        // L'appel que le jeu fait de la mise à jour d'un écran, fait ici à sa
        // place, exactement une fois et avec les mêmes arguments. Un écran
        // suivi est regardé avant et après, et l'appel chronométré entre deux
        // lectures de l'horloge. Une exception du jeu passe telle quelle.
        public static void EncadrerLaMiseAJour(MyTextPanelComponent panneau, bool isWorking, bool isInRange)
        {
            var ecran = panneau == null || !Collecte.Actif ? null : AvantLaMiseAJour(panneau, isWorking, isInRange);
            if (ecran == null)
            {
                panneau.UpdateAfterSimulation(isWorking, isInRange);
                return;
            }
            var debut = Stopwatch.GetTimestamp();
            panneau.UpdateAfterSimulation(isWorking, isInRange);
            var fin = Stopwatch.GetTimestamp();
            ApresLaMiseAJour(panneau, ecran, Collecte.Millisecondes(debut, fin));
        }

        // L'écran, s'il est suivi pour cette mise à jour ; sinon nul.
        private static Ecran AvantLaMiseAJour(MyTextPanelComponent __instance, bool isWorking, bool isInRange)
        {
            try
            {
                var bloc = s_bloc(__instance);
                var camera = MySector.MainCamera;
                if (bloc == null || camera == null)
                    return null;
                s_miseAJour.Compter();
                var ecran = s_ecrans.GetValue(__instance, c => new Ecran());
                ecran.AvantSuivi = false;
                if (ecran.Portee < 0)
                {
                    ecran.Cle = unchecked(bloc.EntityId * 31 + s_zone(__instance));
                    ecran.Portee = PorteeDuBloc(bloc);
                }
                var distance = Vector3D.Distance(__instance.WorldPosition, camera.Position);
                if (!PorteeDesEcrans.ARelever(distance, ecran.Portee))
                {
                    if (ecran.Demande != null)
                        OublierLEcran(ecran);
                    return null;
                }

                ecran.AvantSuivi = true;
                ecran.EnMarche = isWorking;
                ecran.APortee = isInRange;
                ecran.Distance = distance;
                ecran.Fonctionnel = bloc.IsFunctional;
                var rendu = s_rendu(__instance);
                ecran.ObjetDeRenduPret = rendu != null && rendu.IsRenderObjectAssigned(s_objetDeRendu(__instance));
                ecran.CalculAvant = s_dernierCalcul(__instance);
                return ecran;
            }
            catch (Exception e)
            {
                Fautes.Noter("avant la mise à jour d'un écran", e);
                return null;
            }
        }

        private static void OublierLEcran(Ecran ecran)
        {
            ecran.Demande = null;
            ecran.Texture = null;
            NePlusGuetter(ecran);
            Collecte.Ecrans.Oublier(ecran.Cle);
        }

        private static void NePlusGuetter(Ecran ecran)
        {
            long ignore;
            if (ecran.ClesDEchange != null)
            {
                foreach (var cle in ecran.ClesDEchange)
                    s_echangesAttendus.TryRemove(cle, out ignore);
                ecran.ClesDEchange = null;
            }
            if (ecran.TextureAttendue != null)
            {
                s_texturesAttendues.TryRemove(ecran.TextureAttendue, out ignore);
                ecran.TextureAttendue = null;
            }
        }

        // Après la mise à jour du jeu, avec sa durée.
        private static void ApresLaMiseAJour(MyTextPanelComponent __instance, Ecran ecran, double dureeMs)
        {
            if (!ecran.AvantSuivi)
                return;
            try
            {
                ecran.AvantSuivi = false;
                var maintenant = DateTime.UtcNow;
                var calcul = s_dernierCalcul(__instance);
                if (calcul != ecran.CalculAvant && calcul != 0)
                {
                    ecran.DernierCalcul = maintenant;
                    ecran.DureeDuCalcul = dureeMs;
                }

                var dans32m = ecran.Distance <= PorteeDesEcrans.RayonDesMorceaux;
                if (dans32m && !ecran.Dans32m)
                {
                    ecran.MorceauxDepuis32m = 0;
                    ecran.MorceauHorsDes32m = false;
                }
                ecran.Dans32m = dans32m;

                var texture = s_textureActuelle(__instance);
                var demande = Categorie(texture);
                var raison = ObservationDEcran.EstUnVraiContenu(demande) ? null : Raison(__instance, ecran);
                string script;
                var contenu = SorteDeContenu(__instance, out script);
                var aMorceaux = dans32m && ecran.MorceauxDepuis32m > 0;
                var porteeEffective = PorteeEffective(ecran.Portee);
                var aPorteeEffective = ecran.Distance <= porteeEffective;
                var nouvelleTexture = texture != ecran.Texture || !Collecte.Ecrans.EstSuivi(ecran.Cle);

                // Rien n'a changé depuis la dernière fois : on ne bâtit rien.
                if (!nouvelleTexture && demande == ecran.Demande && raison == ecran.Raison && contenu == ecran.Contenu
                    && script == ecran.Script && aMorceaux == ecran.AMorceaux && ecran.ZoneVue == dans32m
                    && ecran.APorteeEffectiveVue == aPorteeEffective)
                    return;

                // Une nouvelle demande : le fil de l'affichage guette son échange.
                if (nouvelleTexture)
                    Guetter(__instance, ecran, texture, demande);

                var bloc = s_bloc(__instance);
                var grille = bloc.CubeGrid;
                var vu = new ObservationDEcran
                {
                    Cle = ecran.Cle,
                    Bloc = bloc.DisplayNameText,
                    TypeDeBloc = bloc.BlockDefinition == null ? null : bloc.BlockDefinition.Id.SubtypeName,
                    Ecran = NomDeLEcran(__instance),
                    Construction = grille == null ? null : grille.DisplayName,
                    ConstructionId = grille == null ? 0 : grille.EntityId,
                    SorteDeContenu = contenu,
                    Script = script,
                    Distance = ecran.Distance,
                    PorteeDuBloc = ecran.Portee,
                    PorteeEffective = porteeEffective,
                    Demande = demande,
                    Raison = raison,
                    Alimentation = raison == ObservationDEcran.SansCourant ? Alimentation(bloc) : null,
                    DernierCalcul = ecran.DernierCalcul,
                    DureeDuCalculMs = ecran.DureeDuCalcul,
                    DernierMorceau = ecran.DernierMorceau,
                    MorceauxDepuis32m = ecran.MorceauxDepuis32m,
                    MorceauRecuHorsDes32m = ecran.MorceauHorsDes32m,
                };
                Collecte.Ecrans.Observer(vu, maintenant);

                ecran.Demande = demande;
                ecran.Texture = texture;
                ecran.Raison = raison;
                ecran.Contenu = contenu;
                ecran.Script = script;
                ecran.AMorceaux = aMorceaux;
                ecran.ZoneVue = dans32m;
                ecran.APorteeEffectiveVue = aPorteeEffective;
            }
            catch (Exception e)
            {
                Fautes.Noter("après la mise à jour d'un écran", e);
            }
        }

        // Pose ce que le fil de l'affichage doit guetter pour cet écran :
        // l'échange de son matériau sur chacun de ses objets de rendu et, pour
        // un contenu dessiné, le rendu hors écran de sa texture.
        private static void Guetter(MyTextPanelComponent panneau, Ecran ecran, string texture, string demande)
        {
            NePlusGuetter(ecran);
            var rendu = s_rendu(panneau);
            var zones = rendu == null ? null : s_zonesDuRendu.GetValue(rendu) as IList;
            var indice = s_zone(panneau);
            if (zones == null || indice < 0 || indice >= zones.Count)
                return;
            var zone = zones[indice];
            var objets = s_objetsDeLaZone.GetValue(zone) as uint[];
            var materiau = s_materiauDeLaZone.GetValue(zone) as string;
            if (objets == null || materiau == null)
                return;
            ecran.ClesDEchange = new List<string>(objets.Length);
            foreach (var objet in objets)
            {
                if (objet == uint.MaxValue)
                    continue;
                var cle = CleDEchange(objet, materiau);
                s_echangesAttendus[cle] = ecran.Cle;
                ecran.ClesDEchange.Add(cle);
            }
            if (demande == ObservationDEcran.Contenu && !string.IsNullOrEmpty(texture))
            {
                ecran.TextureAttendue = texture;
                s_texturesAttendues[texture] = ecran.Cle;
            }
        }

        private static string CleDEchange(uint objet, string materiau)
        {
            return objet.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + materiau;
        }

        // ---- Fil de l'affichage ----------------------------------------------
        // Rien de lourd : une recherche dans une table, et seulement pour les
        // écrans qui attendent quelque chose. Toute faute est notée sans jamais
        // remonter au rendu.

        private static int Longueur(FieldInfo champ)
        {
            var liste = champ.GetValue(null) as ICollection;
            return liste == null ? -1 : liste.Count;
        }

        private static void RenduConfie(MyRenderMessageRenderOffscreenTexture rMessage)
        {
            if (!Collecte.Actif)
                return;
            try
            {
                s_ajoutHorsEcran.Compter();
                var file = Longueur(s_fileHorsEcran);
                FileHorsEcran = file;
                long cle;
                if (rMessage == null || rMessage.OffscreenTexture == null || s_texturesAttendues.IsEmpty
                    || !s_texturesAttendues.TryGetValue(rMessage.OffscreenTexture, out cle))
                    return;
                s_vus.Enqueue(new VuParLAffichage { Cle = cle, Etape = EtapeDuRendu.MisEnFile, Moment = DateTime.UtcNow, File = file, EnVol = -1 });
            }
            catch (Exception e)
            {
                Fautes.Noter("rendu hors écran confié", e);
            }
        }

        private static void RenduLance(object data)
        {
            if (!Collecte.Actif)
                return;
            try
            {
                s_lancement.Compter();
                var enVol = Longueur(s_rendusEnVol);
                RendusEnVol = enVol;
                FileHorsEcran = Longueur(s_fileHorsEcran);
                long cle;
                var message = data == null ? null : s_messageDuRendu.GetValue(data) as MyRenderMessageRenderOffscreenTexture;
                if (message == null || message.OffscreenTexture == null || s_texturesAttendues.IsEmpty
                    || !s_texturesAttendues.TryGetValue(message.OffscreenTexture, out cle))
                    return;
                // Le rendu de cet écran s'ajoute à ceux déjà en vol.
                s_vus.Enqueue(new VuParLAffichage { Cle = cle, Etape = EtapeDuRendu.Soumis, Moment = DateTime.UtcNow, File = -1, EnVol = enVol + 1 });
            }
            catch (Exception e)
            {
                Fautes.Noter("rendu hors écran lancé", e);
            }
        }

        // Sur un fil de travail (le rendu hors écran) ou sur le fil de l'affichage.
        private static void TexturePrete(object __instance)
        {
            if (!Collecte.Actif)
                return;
            s_texturePrete.Compter();
            try
            {
                if (s_texturesAttendues.IsEmpty)
                    return;
                var nom = s_nomDeLaTexture.GetValue(__instance, null) as string;
                long cle;
                if (nom == null || !s_texturesAttendues.TryGetValue(nom, out cle))
                    return;
                s_vus.Enqueue(new VuParLAffichage { Cle = cle, Etape = EtapeDuRendu.TexturePrete, Moment = DateTime.UtcNow, File = -1, EnVol = -1 });
            }
            catch (Exception e)
            {
                Fautes.Noter("texture d'écran prête", e);
            }
        }

        // Le premier vrai pour un objet suivi : l'affichage échange la texture
        // dans cette même image (MyTextureChangeManager.OnUpdate).
        private static void EchangePossible(MyRenderMessageChangeMaterialTexture rMessage, bool __result)
        {
            if (!Collecte.Actif)
                return;
            s_echange.Compter();
            if (!__result || s_echangesAttendus.IsEmpty)
                return;
            try
            {
                if (rMessage == null || rMessage.Changes == null)
                    return;
                foreach (var changement in rMessage.Changes)
                {
                    long cle;
                    var cleDEchange = CleDEchange(rMessage.RenderObjectID, changement.Key.String);
                    if (!s_echangesAttendus.TryRemove(cleDEchange, out cle))
                        continue;
                    s_vus.Enqueue(new VuParLAffichage
                    {
                        Cle = cle,
                        Echange = true,
                        Texture = changement.Value.ColorMetalFileName,
                        Moment = DateTime.UtcNow,
                    });
                }
            }
            catch (Exception e)
            {
                Fautes.Noter("échange de texture", e);
            }
        }

        // ---- Morceaux d'image reçus du serveur -------------------------------

        private static void MorceauxRecus(MyTextPanelComponent __instance)
        {
            if (!Collecte.Actif)
                return;
            s_reception.Compter();
            try
            {
                var morceaux = s_morceaux(__instance);
                var tableau = morceaux.Sprites;
                if (tableau == null || tableau.Length == 0)
                    return;
                Ecran ecran;
                if (!s_ecrans.TryGetValue(__instance, out ecran) || ReferenceEquals(ecran.DernierTableau, tableau))
                    return;
                ecran.DernierTableau = tableau;
                ecran.DernierMorceau = DateTime.UtcNow;
                if (ecran.Dans32m)
                    ecran.MorceauxDepuis32m++;
                else
                    ecran.MorceauHorsDes32m = true;
            }
            catch (Exception e)
            {
                Fautes.Noter("morceaux reçus du serveur", e);
            }
        }

        // ---- Ce que l'écran affiche et pourquoi -------------------------------

        // La sorte d'une texture d'écran : Online, Offline, vide, contenu
        // dessiné par le jeu, image, ou aucune (la texture du modèle).
        private static string Categorie(string texture)
        {
            if (string.IsNullOrEmpty(texture))
                return ObservationDEcran.Aucun;
            if (s_texturesOnline == null)
            {
                s_texturesOnline = new[] { MyTextPanelComponent.GetPathForID("Online"), MyTextPanelComponent.GetPathForID("Online_wide") };
                s_texturesOffline = new[] { MyTextPanelComponent.GetPathForID("Offline"), MyTextPanelComponent.GetPathForID("Offline_wide") };
            }
            if (texture == s_texturesOnline[0] || texture == s_texturesOnline[1])
                return ObservationDEcran.Online;
            if (texture == s_texturesOffline[0] || texture == s_texturesOffline[1])
                return ObservationDEcran.Offline;
            if (texture == "EMPTY")
                return ObservationDEcran.Vide;
            // La texture dessinée par le jeu pour l'écran (LCDOffscreenTexture_...).
            if (texture.StartsWith("LCDOffscreenTexture", StringComparison.Ordinal))
                return ObservationDEcran.Contenu;
            return ObservationDEcran.Image;
        }

        private static double PorteeEffective(double porteeDuBloc)
        {
            var multiplicateur = Multiplicateur;
            return multiplicateur > 0
                ? PorteeDesEcrans.PorteeDuBloc(porteeDuBloc) * multiplicateur
                : PorteeDesEcrans.PorteeEffective(porteeDuBloc, ChaqueSeconde.QualiteDesTextures);
        }

        private static string Raison(MyTextPanelComponent panneau, Ecran ecran)
        {
            if (!ecran.Fonctionnel)
                return ObservationDEcran.BlocEndommage;
            if (!ecran.EnMarche)
                return ObservationDEcran.SansCourant;
            if (!ecran.APortee && !s_statique(panneau))
            {
                if (ecran.Distance >= PorteeEffective(ecran.Portee))
                    return ObservationDEcran.HorsPortee;
                return RaisonDeLaReserve(panneau, ecran);
            }
            if (!ecran.ObjetDeRenduPret)
                return ObservationDEcran.ObjetDeRenduPasPret;
            return null;
        }

        // À portée mais pas encore dessiné : la réserve de dessin du jeu dit
        // si l'écran attend ses 6 passages ou s'il est au-delà du budget. Si
        // elle ne se lit pas, la raison le dit (jamais une raison inventée).
        private static string RaisonDeLaReserve(MyTextPanelComponent panneau, Ecran ecran)
        {
            try
            {
                var fournisseur = Fournisseur(panneau, ecran);
                var session = MySession.Static;
                var composant = session == null ? null : session.GetComponent<MySessionComponentPanels>();
                if (fournisseur == null || composant == null)
                    return ObservationDEcran.RaisonInconnue;
                var indice = (int)ProprieteRangeIndex(fournisseur.GetType()).GetValue(fournisseur, null);
                if (indice < 0)
                    return ObservationDEcran.PasEncoreAdmis;

                if (s_listeInterne == null)
                {
                    var type = typeof(MySessionComponentPanels);
                    s_indiceDeBudget = Champ(type, "m_insideListBudgetIndex");
                    s_listeInterne = Champ(type, "m_insideList");
                }
                var liste = s_listeInterne.GetValue(composant);
                if (s_tableauInterne == null)
                {
                    s_tableauInterne = AccessTools.Method(liste.GetType(), "GetInternalArray");
                    if (s_tableauInterne == null)
                        throw new MissingMemberException(liste.GetType().FullName + ".GetInternalArray introuvable");
                }
                var tableau = (Array)s_tableauInterne.Invoke(liste, null);
                var donnee = tableau.GetValue(indice);
                if (s_indiceOrdonne == null)
                    s_indiceOrdonne = Champ(donnee.GetType(), "OrderedIndex");
                var ordre = (int)s_indiceOrdonne.GetValue(donnee);
                var budget = (int)s_indiceDeBudget.GetValue(composant);
                return ordre >= budget ? ObservationDEcran.ReservePleine : ObservationDEcran.PasEncoreAdmis;
            }
            catch (Exception e)
            {
                Fautes.Noter("réserve de dessin des écrans", e);
                return ObservationDEcran.RaisonInconnue;
            }
        }

        private static FieldInfo s_listeInterne;
        private static FieldInfo s_indiceDeBudget;
        private static MethodInfo s_tableauInterne;
        private static FieldInfo s_indiceOrdonne;

        private static readonly Dictionary<Type, PropertyInfo> s_proprietesRangeIndex = new Dictionary<Type, PropertyInfo>();

        private static PropertyInfo ProprieteRangeIndex(Type type)
        {
            PropertyInfo propriete;
            lock (s_proprietesRangeIndex)
            {
                if (!s_proprietesRangeIndex.TryGetValue(type, out propriete))
                {
                    propriete = AccessTools.Property(type, "RangeIndex");
                    s_proprietesRangeIndex[type] = propriete;
                }
            }
            if (propriete == null)
                throw new MissingMemberException(type.FullName + ".RangeIndex introuvable");
            return propriete;
        }

        // Ce que le jeu range dans sa réserve de dessin pour cet écran : le
        // panneau lui-même, ou le composant multi-écrans du bloc.
        private static object Fournisseur(MyTextPanelComponent panneau, Ecran ecran)
        {
            if (ecran.FournisseurCherche)
                return ecran.Fournisseur;
            ecran.FournisseurCherche = true;
            var bloc = s_bloc(panneau);
            if (bloc is IMyTextPanelProvider)
            {
                ecran.Fournisseur = bloc;
                return bloc;
            }
            var champ = ChampMultiEcrans(bloc.GetType());
            ecran.Fournisseur = champ == null ? null : champ.GetValue(bloc);
            return ecran.Fournisseur;
        }

        private static FieldInfo ChampMultiEcrans(Type type)
        {
            FieldInfo champ;
            lock (s_champsDuFournisseur)
            {
                if (s_champsDuFournisseur.TryGetValue(type, out champ))
                    return champ;
                for (var t = type; t != null && champ == null; t = t.BaseType)
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                    {
                        if (typeof(MyMultiTextPanelComponent).IsAssignableFrom(f.FieldType))
                        {
                            champ = f;
                            break;
                        }
                    }
                }
                s_champsDuFournisseur[type] = champ;
            }
            return champ;
        }

        // La portée du bloc, telle que le jeu la donne à sa réserve de dessin
        // (m_maxRenderDistanceSquared du panneau ou du composant multi-écrans).
        // Un panneau la tient de sa définition : c'est elle qui sert si le
        // champ ne se lit pas, après le rapport de la faute.
        private static double PorteeDuBloc(MyTerminalBlock bloc)
        {
            var panneau = bloc as MyTextPanel;
            try
            {
                object porteur = panneau;
                if (porteur == null)
                {
                    var champ = ChampMultiEcrans(bloc.GetType());
                    porteur = champ == null ? null : champ.GetValue(bloc);
                }
                // Un bloc sans écran du jeu (écran d'un mod) : la portée de la plupart.
                if (porteur == null)
                    return PorteeDesEcrans.PorteeParDefaut;
                var carre = Champ(porteur.GetType(), "m_maxRenderDistanceSquared");
                var valeur = Convert.ToDouble(carre.GetValue(porteur));
                if (valeur > 0)
                    return Math.Sqrt(valeur);
            }
            catch (Exception e)
            {
                Fautes.Noter("portée d'un écran", e);
            }
            if (panneau != null && panneau.BlockDefinition != null && panneau.BlockDefinition.MaxScreenRenderDistance > 0)
                return panneau.BlockDefinition.MaxScreenRenderDistance;
            return PorteeDesEcrans.PorteeParDefaut;
        }

        private static string NomDeLEcran(MyTextPanelComponent panneau)
        {
            var nom = panneau.DisplayName;
            return string.IsNullOrEmpty(nom) ? panneau.Name : nom;
        }

        // Texte, image, script et son nom, programme d'écran d'un mod, ou image
        // envoyée par le serveur (bloc programmable).
        private static string SorteDeContenu(MyTextPanelComponent panneau, out string script)
        {
            script = null;
            switch (panneau.ContentType)
            {
                case ContentType.TEXT_AND_IMAGE:
                    var texte = !string.IsNullOrEmpty(panneau.Text);
                    var image = panneau.SelectedTexturesToDraw != null && panneau.SelectedTexturesToDraw.Count > 0;
                    return texte && image ? "texte et image" : texte ? "texte" : image ? "image" : "vide";
                case ContentType.SCRIPT:
                    script = panneau.Script;
                    if (string.IsNullOrEmpty(script))
                    {
                        script = null;
                        return "image du serveur";
                    }
                    var programme = s_script(panneau);
                    return programme != null && DUnMod(programme.GetType()) ? "programme d'écran d'un mod" : "script";
                default:
                    return "aucun";
            }
        }

        private static bool DUnMod(Type type)
        {
            var nom = type.Assembly.GetName().Name ?? "";
            return !(nom.StartsWith("Sandbox.", StringComparison.Ordinal) || nom.StartsWith("SpaceEngineers.", StringComparison.Ordinal)
                     || nom.StartsWith("VRage", StringComparison.Ordinal));
        }

        // Ce que le jeu du joueur sait de l'alimentation d'un bloc qu'il voit
        // sans courant (calcul local, jamais répliqué), sans rien recalculer.
        private static string Alimentation(MyTerminalBlock bloc)
        {
            try
            {
                var parties = new List<string>();
                var fonctionnel = bloc as MyFunctionalBlock;
                if (fonctionnel != null)
                    parties.Add("allumé:" + (fonctionnel.Enabled ? "oui" : "non"));
                parties.Add("fonctionnel:" + (bloc.IsFunctional ? "oui" : "non"));
                var puits = bloc.ResourceSink;
                if (puits != null)
                {
                    parties.Add("alimenté:" + (puits.IsPoweredByType(MyResourceDistributorComponent.ElectricityId) ? "oui" : "non"));
                    parties.Add("demande:" + puits.RequiredInputByType(MyResourceDistributorComponent.ElectricityId).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
                }
                var grille = bloc.CubeGrid;
                if (grille != null)
                {
                    var distributeur = grille.GridSystems == null ? null : grille.GridSystems.ResourceDistributor;
                    if (distributeur != null)
                        parties.Add("réseau:" + PorteeDesEcrans.EtatDuReseau(
                            distributeur.ResourceStateByType(MyResourceDistributorComponent.ElectricityId, false).ToString()).Replace(' ', '_'));
                    var groupe = MyCubeGridGroups.Static.Logical.GetGroup(grille);
                    if (groupe != null)
                        parties.Add("grilles_du_groupe:" + groupe.Nodes.Count);
                }
                return string.Join(" ", parties.ToArray());
            }
            catch (Exception e)
            {
                Fautes.Noter("alimentation d'un écran", e);
                return "illisible";
            }
        }
    }
}
