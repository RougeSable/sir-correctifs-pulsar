using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.ModAPI;
using VRage.FileSystem;
using VRage.GameServices;
using VRage.Utils;

namespace SirCorrectifsPulsar
{
    // Les rapports d'erreurs et de plantages du greffon, envoyés sur le canal
    // de rapports du studio tant que le joueur est sur PU ou PTU. Ailleurs ils
    // attendent dans Storage\sir-correctifs-pulsar\Rapports. Aucune donnée sur
    // le joueur n'y entre (NettoyeurDeRapports). Rien ici ne casse le jeu.
    internal sealed class RapportsDuJeu
    {
        // Environ une fois par seconde (60 mises à jour).
        private const int Intervalle = 60;

        private readonly string m_dossier;
        private readonly RapporteurDErreurs m_rapporteur;
        private int m_compteur;
        private bool m_nomDuJoueurConnu;

        public RapporteurDErreurs Rapporteur
        {
            get { return m_rapporteur; }
        }

        private RapportsDuJeu(string dossier, RapporteurDErreurs rapporteur)
        {
            m_dossier = dossier;
            m_rapporteur = rapporteur;
        }

        // Au démarrage du jeu : le rapporteur, puis le rapport de plantage de la
        // session précédente si elle ne s'est jamais terminée. Nul si même cela
        // échoue.
        public static RapportsDuJeu Demarrer(string dossier, string identifiant)
        {
            try
            {
                var version = typeof(RapportsDuJeu).Assembly.GetName().Version.ToString();
                var session = DebutDuJeu();
                var nettoyeur = new NettoyeurDeRapports(ProfilDeLUtilisateur(), NomsConnus());
                var file = new FileDesRapports(Path.Combine(dossier, "Rapports"));
                var rapporteur = new RapporteurDErreurs(GreffonSirCorrectifsPulsar.NomDuProduit, version, session, file, nettoyeur, () => DateTime.UtcNow);
                var rapports = new RapportsDuJeu(dossier, rapporteur);

                var plantage = SurveillanceDesPlantages.Demarrer(dossier, MyFileSystem.UserDataPath, JournalActuel(), session,
                    GreffonSirCorrectifsPulsar.NomDuProduit, version, identifiant, nettoyeur);
                if (plantage != null)
                {
                    rapporteur.MettreEnFile(plantage);
                    MyLog.Default.WriteLine("[" + identifiant + "] la session de jeu précédente ne s'est pas terminée proprement : rapport de plantage mis en file");
                }
                return rapports;
            }
            catch (Exception e)
            {
                try
                {
                    MyLog.Default.WriteLine("[" + identifiant + "] rapports d'erreurs indisponibles : " + e.Message);
                }
                catch (Exception)
                {
                }
                return null;
            }
        }

        // Fil du jeu, à chaque mise à jour.
        public void MettreAJour()
        {
            if (++m_compteur < Intervalle)
                return;
            m_compteur = 0;
            try
            {
                ApprendreLeNomDuJoueur();
                m_rapporteur.Tour(SurNosServeurs(), Envoyer);
            }
            catch (Exception)
            {
            }
        }

        // La sortie propre du jeu : les rapports attendent sur le disque, la
        // marque disparaît.
        public void Terminer()
        {
            try
            {
                m_rapporteur.Ecrire();
            }
            catch (Exception)
            {
            }
            SurveillanceDesPlantages.Terminer(m_dossier);
        }

        private static bool Envoyer(byte[] rapport)
        {
            try
            {
                var multijoueur = MyAPIGateway.Multiplayer;
                if (multijoueur == null)
                    return false;
                return multijoueur.SendMessageToServer(CanalDesRapports.CanalDuJeu, rapport, true);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Vrai sur PU ou PTU, rejoints en client.
        public static bool SurNosServeurs()
        {
            try
            {
                var session = MyAPIGateway.Session;
                var multijoueur = MyAPIGateway.Multiplayer;
                if (session == null || multijoueur == null)
                    return false;
                var serveur = ServeurDedie(MyMultiplayer.Static);
                return CanalDesRapports.EstUnDeNosServeurs(
                    multijoueur.MultiplayerActive,
                    multijoueur.IsServer,
                    serveur != null,
                    serveur != null ? serveur.Name : null,
                    session.Name);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Le client d'un serveur dédié garde le serveur rejoint dans une classe
        // interne : trouvé par son type, jamais par un nom qui pourrait changer.
        private static MyGameServerItem ServeurDedie(object jeu)
        {
            if (jeu == null)
                return null;
            try
            {
                const BindingFlags drapeaux = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var propriete in jeu.GetType().GetProperties(drapeaux))
                {
                    if (propriete.PropertyType == typeof(MyGameServerItem) && propriete.GetIndexParameters().Length == 0)
                        return propriete.GetValue(jeu, null) as MyGameServerItem;
                }
                foreach (var champ in jeu.GetType().GetFields(drapeaux))
                {
                    if (champ.FieldType == typeof(MyGameServerItem))
                        return champ.GetValue(jeu) as MyGameServerItem;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static DateTime DebutDuJeu()
        {
            try
            {
                return Process.GetCurrentProcess().StartTime.ToUniversalTime();
            }
            catch (Exception)
            {
                return DateTime.UtcNow;
            }
        }

        private static string JournalActuel()
        {
            try
            {
                return MyLog.Default.GetFilePath();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ProfilDeLUtilisateur()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Ce qui identifie le joueur sur cette machine : jamais dans un rapport.
        private static IEnumerable<string> NomsConnus()
        {
            var noms = new List<string>();
            Ajouter(noms, () => Environment.UserName);
            Ajouter(noms, () => MyGameService.UserName);
            Ajouter(noms, () => MyGameService.OnlineName);
            Ajouter(noms, () => MyGameService.UserId == 0 ? null : MyGameService.UserId.ToString());
            Ajouter(noms, () => MyGameService.OnlineUserId == 0 ? null : MyGameService.OnlineUserId.ToString());
            return noms;
        }

        private void ApprendreLeNomDuJoueur()
        {
            if (m_nomDuJoueurConnu)
                return;
            var session = MyAPIGateway.Session;
            var joueur = session == null ? null : session.Player;
            if (joueur == null)
                return;
            m_rapporteur.Nettoyeur.AjouterUnNom(joueur.DisplayName);
            if (joueur.SteamUserId != 0)
                m_rapporteur.Nettoyeur.AjouterUnNom(joueur.SteamUserId.ToString());
            m_nomDuJoueurConnu = true;
        }

        private static void Ajouter(List<string> noms, Func<string> lire)
        {
            try
            {
                var nom = lire();
                if (!string.IsNullOrEmpty(nom))
                    noms.Add(nom);
            }
            catch (Exception)
            {
            }
        }
    }
}
