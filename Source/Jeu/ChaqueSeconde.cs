using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ParallelTasks;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Game.Gui;

namespace SirCorrectifsPulsar
{
    // Ce qui se relit une fois par seconde, sur le fil du jeu, et ne se note
    // que s'il a changé : la file « très basse » des fils de travail (celle du
    // minerai, des textures et des modèles), les requêtes du détecteur en vol,
    // l'affichage des marqueurs, et les réglages graphiques du joueur.
    //
    // Rien n'y est calculé : on lit des compteurs que le jeu tient déjà. Le
    // jeu mesure sa charge avec ReadAndClearExecutionTime et
    // ReadAndClearWorklog : on ne les appelle jamais.
    internal static class ChaqueSeconde
    {
        private const long CleDeLaFile = 1;
        private const long CleDuHud = 2;
        private const long CleDesReglages = 3;

        // La composition de la file se relit au plus toutes les 10 s.
        private static readonly TimeSpan IntervalleDeLaComposition = TimeSpan.FromSeconds(10);

        private static bool s_fileIntrouvable;
        private static FieldInfo s_tableaux;
        private static FieldInfo s_correspondance;
        private static FieldInfo s_fileDesTaches;
        private static PropertyInfo s_ouvriers;
        private static FieldInfo s_requetes;
        private static PropertyInfo s_composantDuDetecteur;
        private static DateTime s_derniereComposition;

        public static string QualiteDesTextures = "MEDIUM";

        public static void Relever()
        {
            try
            {
                Reglages();
            }
            catch (Exception e)
            {
                Fautes.Noter("réglages graphiques", e);
            }
            try
            {
                Hud();
            }
            catch (Exception e)
            {
                Fautes.Noter("affichage des marqueurs", e);
            }
            try
            {
                File();
            }
            catch (Exception e)
            {
                Fautes.Noter("file très basse", e);
            }
        }

        private static void Reglages()
        {
            var rendu = MyVideoSettingsManager.CurrentGraphicsSettings.PerformanceSettings.RenderSettings;
            QualiteDesTextures = rendu.TextureQuality.ToString();
            var champs = new ChampsDuReleve();
            champs.Texte("textures", PorteeDesEcrans.Qualite(rendu.TextureQuality.ToString()));
            champs.Texte("textures_des_voxels", PorteeDesEcrans.Qualite(rendu.VoxelTextureQuality.ToString()));
            champs.Texte("modeles", PorteeDesEcrans.Qualite(rendu.ModelQuality.ToString()));
            champs.Texte("voxels", PorteeDesEcrans.Qualite(rendu.VoxelQuality.ToString()));
            champs.Texte("filtrage_anisotrope", PorteeDesEcrans.Anisotrope(rendu.AnisotropicFiltering.ToString()));
            champs.Texte("ombres", PorteeDesEcrans.Qualite(rendu.ShadowQuality.ToString()));
            var multiplicateur = MultiplicateurDesEcrans();
            AccrochesEcrans.Multiplicateur = multiplicateur;
            champs.Texte("multiplicateur_de_portee_des_ecrans", PorteeDesEcrans.Multiplicateur3(
                multiplicateur > 0 ? multiplicateur : PorteeDesEcrans.Multiplicateur(rendu.TextureQuality.ToString())));
            if (Collecte.Etats.AChange(CleDesReglages, Signature(champs)))
                Collecte.Releveur.Noter(Suivi.Ecrans, "réglages graphiques", champs);
        }

        // Le multiplicateur de portée que la réserve de dessin des écrans
        // applique vraiment (MySessionComponentPanels, au carré), ou -1.
        private static FieldInfo s_multiplicateurAuCarre;
        private static bool s_multiplicateurIllisible;

        private static double MultiplicateurDesEcrans()
        {
            if (s_multiplicateurIllisible)
                return -1;
            try
            {
                var session = Sandbox.Game.World.MySession.Static;
                var composant = session == null ? null : session.GetComponent<Sandbox.Game.SessionComponents.MySessionComponentPanels>();
                if (composant == null)
                    return -1;
                if (s_multiplicateurAuCarre == null)
                {
                    s_multiplicateurAuCarre = AccessTools.Field(composant.GetType(), "m_maxDistanceMultiplierSquared");
                    if (s_multiplicateurAuCarre == null)
                        throw new MissingMemberException("MySessionComponentPanels.m_maxDistanceMultiplierSquared introuvable");
                }
                var carre = Convert.ToDouble(s_multiplicateurAuCarre.GetValue(composant));
                return carre > 0 ? Math.Sqrt(carre) : -1;
            }
            catch (Exception e)
            {
                // Une fois : ensuite la qualité des textures en tient lieu.
                s_multiplicateurIllisible = true;
                Fautes.Noter("multiplicateur de portée des écrans", e);
                return -1;
            }
        }

        private static void Hud()
        {
            var personnage = Collecte.PersonnageDuJoueur();
            var champs = new ChampsDuReleve();
            champs.OuiNon("marqueurs_visibles", MyHud.OreMarkers.Visible);
            champs.OuiNon("hud_visible", MyHud.IsVisible);
            champs.OuiNon("hud_minimal", MyHud.MinimalHud || MyHud.IsHudMinimal);
            champs.Entier("etat_du_hud", MyHud.HudState);
            if (personnage != null)
                champs.OuiNon("iem", personnage.IsElectricityDisrupted);
            if (Collecte.Etats.AChange(CleDuHud, Signature(champs)))
                Collecte.Releveur.Noter(Suivi.Minerai, "affichage des marqueurs", champs);
        }

        // La longueur de la file « très basse » et les requêtes du détecteur en
        // vol ; une ligne quand elles changent.
        private static void File()
        {
            var champs = new ChampsDuReleve();
            object tableau;
            var longueur = LongueurDeLaFile(out tableau);
            if (longueur >= 0)
                champs.Entier("file_tres_basse", longueur);
            int occupes;
            var ouvriers = Ouvriers(tableau, out occupes);
            if (ouvriers >= 0)
                champs.Entier("fils_tres_bas", ouvriers);
            if (occupes >= 0)
                champs.Entier("fils_occupes", occupes);
            var requetes = RequetesEnVol();
            if (requetes >= 0)
                champs.Entier("requetes_en_vol", requetes);
            champs.Entier("passages_en_cours", Collecte.Passages.EnCours);
            if (AccrochesEcrans.FileHorsEcran >= 0)
                champs.Entier("ecrans_en_attente_de_rendu", AccrochesEcrans.FileHorsEcran);
            if (AccrochesEcrans.RendusEnVol >= 0)
                champs.Entier("rendus_d_ecran_en_vol", AccrochesEcrans.RendusEnVol);

            if (!Collecte.Etats.AChange(CleDeLaFile, Signature(champs)))
                return;

            if (longueur > 0 && DateTime.UtcNow - s_derniereComposition >= IntervalleDeLaComposition)
            {
                s_derniereComposition = DateTime.UtcNow;
                champs.Texte("composition", Composition(tableau));
            }
            Collecte.Releveur.Noter(Suivi.Minerai, "file très basse", champs);
        }

        // -1 si la file n'a pas la forme attendue (planificateur d'une autre
        // sorte) : la longueur manque alors aux lignes, sans erreur répétée.
        private static int LongueurDeLaFile(out object tableau)
        {
            tableau = null;
            if (s_fileIntrouvable)
                return -1;
            try
            {
                var planificateur = Parallel.Scheduler;
                if (planificateur == null)
                    return -1;
                var type = planificateur.GetType();
                if (s_tableaux == null)
                {
                    s_tableaux = AccessTools.Field(type, "m_workerArrays");
                    s_correspondance = AccessTools.Field(type, "m_mappingPriorityToWorker");
                }
                if (s_tableaux == null || s_correspondance == null)
                {
                    Introuvable("planificateur " + type.Name + " sans m_workerArrays");
                    return -1;
                }
                var tableaux = (Array)s_tableaux.GetValue(planificateur);
                var correspondance = (int[])s_correspondance.GetValue(planificateur);
                var indice = correspondance[(int)WorkPriority.VeryLow];
                tableau = tableaux.GetValue(indice);
                if (s_fileDesTaches == null)
                {
                    s_fileDesTaches = AccessTools.Field(tableau.GetType(), "m_taskQueue");
                    s_ouvriers = AccessTools.Property(tableau.GetType(), "Workers");
                }
                if (s_fileDesTaches == null)
                {
                    Introuvable("m_taskQueue introuvable");
                    return -1;
                }
                var file = s_fileDesTaches.GetValue(tableau) as ICollection;
                return file == null ? -1 : file.Count;
            }
            catch (Exception e)
            {
                s_fileIntrouvable = true;
                Fautes.Noter("file très basse", e);
                return -1;
            }
        }

        private static void Introuvable(string quoi)
        {
            s_fileIntrouvable = true;
            GreffonSirCorrectifsPulsar.Journal.Info("file très basse illisible (" + quoi + ") : sa longueur ne sera pas relevée");
        }

        // Le nombre de fils de la file, et combien travaillent en ce moment : un
        // fil occupé a son horodatage de travail (Worklog) posé, remis à 0 à la
        // fin de sa tâche. On le lit seulement, sans jamais le remettre à zéro.
        private static int Ouvriers(object tableau, out int occupes)
        {
            occupes = -1;
            if (tableau == null || s_ouvriers == null)
                return -1;
            var ouvriers = s_ouvriers.GetValue(tableau, null) as Array;
            if (ouvriers == null)
                return -1;
            if (ouvriers.Length > 0)
            {
                if (s_journalDuFil == null)
                    s_journalDuFil = AccessTools.Field(ouvriers.GetValue(0).GetType(), "Worklog");
                if (s_journalDuFil != null)
                {
                    occupes = 0;
                    foreach (var ouvrier in ouvriers)
                    {
                        if (ouvrier != null && (long)s_journalDuFil.GetValue(ouvrier) != 0)
                            occupes++;
                    }
                }
            }
            return ouvriers.Length;
        }

        private static FieldInfo s_journalDuFil;

        // Les sortes de tâches qui attendent, par nombre décroissant. Une tâche
        // de la file (ParallelTasks.Task) porte son travail dans Item
        // (WorkItem), dont Work est l'IWork à faire.
        private static string Composition(object tableau)
        {
            try
            {
                var file = s_fileDesTaches == null || tableau == null ? null : s_fileDesTaches.GetValue(tableau) as IEnumerable;
                if (file == null)
                    return null;
                var comptes = new Dictionary<string, int>();
                var vues = 0;
                foreach (var tache in file)
                {
                    if (++vues > 2000)
                        break;
                    var element = tache is Task ? ((Task)tache).Item : null;
                    var objet = element == null ? null : element.Work;
                    var nom = objet == null ? "inconnue" : objet.GetType().Name;
                    int n;
                    comptes.TryGetValue(nom, out n);
                    comptes[nom] = n + 1;
                }
                var liste = new List<KeyValuePair<string, int>>(comptes);
                liste.Sort((a, b) => b.Value.CompareTo(a.Value));
                var parties = new List<string>();
                foreach (var compte in liste)
                {
                    if (parties.Count >= 8)
                        break;
                    parties.Add(compte.Key + ":" + compte.Value);
                }
                return string.Join(",", parties.ToArray());
            }
            catch (Exception e)
            {
                Fautes.Noter("composition de la file très basse", e);
                return null;
            }
        }

        // MyOreDetectorSessionComponent.Static.m_queries.Count, ou -1.
        private static int RequetesEnVol()
        {
            try
            {
                if (s_composantDuDetecteur == null)
                {
                    var type = AccessTools.TypeByName("Sandbox.Game.Entities.Cube.MyOreDetectorSessionComponent");
                    if (type == null)
                        return -1;
                    s_composantDuDetecteur = AccessTools.Property(type, "Static");
                    s_requetes = AccessTools.Field(type, "m_queries");
                }
                if (s_composantDuDetecteur == null || s_requetes == null)
                    return -1;
                var composant = s_composantDuDetecteur.GetValue(null, null);
                if (composant == null)
                    return -1;
                var requetes = s_requetes.GetValue(composant) as ICollection;
                if (requetes != null)
                    return requetes.Count;
                var compte = s_requetes.FieldType.GetProperty("Count");
                var ensemble = s_requetes.GetValue(composant);
                return compte == null || ensemble == null ? -1 : (int)compte.GetValue(ensemble, null);
            }
            catch (Exception e)
            {
                Fautes.Noter("requêtes du détecteur en vol", e);
                return -1;
            }
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
