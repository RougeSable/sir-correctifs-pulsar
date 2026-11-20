using System;
using System.IO;
using HarmonyLib;
using VRage.FileSystem;
using VRage.Plugins;
using VRage.Utils;

namespace SirCorrectifsPulsar
{
    // Sir Correctifs Pulsar, un greffon côté joueur chargé par Pulsar. Il
    // relève chez le joueur volontaire l'apparition de ses marqueurs de
    // minerai, l'affichage de ses écrans et celui des blocs en chantier près
    // de lui (blocs invisibles), ce que chaque type de bloc coûte à son fil
    // du jeu (blocs du fil), et envoie ces relevés au serveur
    // (PU ou PTU), où Sir Correctifs Torch les écrit. Il ne change rien au
    // jeu : il observe ce que le jeu calcule déjà.
    public class GreffonSirCorrectifsPulsar : IPlugin
    {
        public const string Identifiant = "sir-correctifs-pulsar";
        public const string NomDuProduit = "Sir Correctifs Pulsar";

        // Les lignes du greffon dans le journal du jeu ; les lignes d'erreur
        // donnent aussi des rapports.
        internal static readonly JournalDuGreffon Journal = new JournalDuGreffon(Identifiant, l => MyLog.Default.WriteLine(l), null);

        private Harmony m_harmony;
        private RapportsDuJeu m_rapports;

        public void Init(object instanceDuJeu)
        {
            // La ligne qui dit que le produit est chargé : elle ne se retire jamais.
            MyLog.Default.WriteLine(NomDuProduit + " chargé");

            // %AppData%\SpaceEngineers\Storage\sir-correctifs-pulsar
            var dossier = Path.Combine(MyFileSystem.UserDataPath, "Storage", Identifiant);
            m_rapports = RapportsDuJeu.Demarrer(dossier, Identifiant);
            Journal.Rapporteur = m_rapports == null ? null : m_rapports.Rapporteur;

            try
            {
                m_harmony = new Harmony(Identifiant);
                AccrochesMinerai.Poser(m_harmony);
                AccrochesEcrans.Poser(m_harmony);
                AccrochesBlocs.Poser(m_harmony);
                MesureDuFil.Poser(m_harmony);
                var posees = 0;
                foreach (var accroche in Accroches.Toutes)
                {
                    if (accroche.Posee)
                        posees++;
                }
                Journal.Info(posees + " accroches posées sur " + Accroches.Toutes.Count
                    + " ; relevés envoyés au serveur sur le canal " + Collecte.CanalDesReleves + ", seulement sur PU et PTU");
            }
            catch (Exception e)
            {
                Journal.Erreur("accroches impossibles à poser : aucun relevé", e);
            }
        }

        public void Update()
        {
            Collecte.MettreAJour();
            if (m_rapports != null)
                m_rapports.MettreAJour();
        }

        public void Dispose()
        {
            if (m_rapports != null)
                m_rapports.Terminer();
        }
    }
}
