using System;
using System.Collections.Generic;
using System.IO;

namespace SirCorrectifsPulsar
{
    // Les rapports d'erreurs qui attendent de partir, un fichier chacun dans le
    // dossier du greffon (Storage\sir-correctifs-pulsar\Rapports). Ils ne
    // partent que tant que le joueur est sur l'un des serveurs du studio ;
    // ailleurs ils attendent, d'un lancement du jeu à l'autre.
    //
    // Rien ici ne casse jamais le jeu : une faute est avalée, le rapport reste
    // où il est et le prochain essai a lieu plus tard.
    public sealed class FileDesRapports
    {
        public const string Extension = ".json";

        // Au-delà, les plus anciens sont abandonnés : la file ne remplit jamais
        // le disque du joueur.
        public const int FichiersMaximum = 100;

        // Au plus ce nombre de rapports part par tour : jamais une rafale.
        public const int EnvoisParTour = 5;

        private readonly string m_dossier;

        public FileDesRapports(string dossier)
        {
            m_dossier = dossier;
        }

        public string Dossier
        {
            get { return m_dossier; }
        }

        public bool Poser(string nom, byte[] contenu)
        {
            try
            {
                Directory.CreateDirectory(m_dossier);
                var chemin = Path.Combine(m_dossier, nom);

                // Écrit à côté, puis mis à sa place : un plantage ne laisse jamais
                // la moitié d'un rapport.
                var temporaire = chemin + ".tmp";
                File.WriteAllBytes(temporaire, contenu);
                if (File.Exists(chemin))
                    File.Delete(chemin);
                File.Move(temporaire, chemin);

                Elaguer();
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool Contient(string nom)
        {
            try
            {
                return File.Exists(Path.Combine(m_dossier, nom));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Les rapports en attente, du plus ancien au plus récent (leur nom
        // commence par la session).
        public List<string> Noms()
        {
            var noms = new List<string>();
            try
            {
                if (!Directory.Exists(m_dossier))
                    return noms;
                foreach (var chemin in Directory.GetFiles(m_dossier, "*" + Extension))
                    noms.Add(Path.GetFileName(chemin));
                noms.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
            }
            return noms;
        }

        public byte[] Lire(string nom)
        {
            try
            {
                return File.ReadAllBytes(Path.Combine(m_dossier, nom));
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Envoie les rapports prêts, seulement sur l'un de nos serveurs. Un
        // rapport quitte le dossier une fois envoyé. Un envoi raté arrête le tour.
        public List<string> Envoyer(bool surNosServeurs, Func<string, bool> pret, Func<byte[], bool> envoi)
        {
            var envoyes = new List<string>();
            if (!surNosServeurs || envoi == null)
                return envoyes;

            try
            {
                foreach (var nom in Noms())
                {
                    if (envoyes.Count >= EnvoisParTour)
                        break;
                    if (pret != null && !pret(nom))
                        continue;

                    var contenu = Lire(nom);
                    if (contenu == null || contenu.Length == 0)
                    {
                        Effacer(nom);
                        continue;
                    }

                    if (!envoi(contenu))
                        break;

                    Effacer(nom);
                    envoyes.Add(nom);
                }
            }
            catch (Exception)
            {
            }
            return envoyes;
        }

        private void Effacer(string nom)
        {
            try
            {
                File.Delete(Path.Combine(m_dossier, nom));
            }
            catch (Exception)
            {
            }
        }

        private void Elaguer()
        {
            var noms = Noms();
            for (var i = 0; i < noms.Count - FichiersMaximum; i++)
                Effacer(noms[i]);
        }
    }
}
