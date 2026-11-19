using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace SirCorrectifsPulsar
{
    // Les relevés en route vers le serveur. Une ligne s'ajoute de n'importe
    // quel fil (file sans verrou) ; le fil du jeu les regroupe en paquets et
    // les envoie, plusieurs fois par seconde : une ligne n'attend jamais plus
    // d'une fraction de seconde avant de partir, sauf rafale.
    //
    // Un paquet est un texte UTF-8, sans marque d'ordre des octets :
    //   sir-correctifs-pulsar releves 1
    //   minerai <ligne>
    //   ecrans <ligne>
    //   ...
    // Le premier mot de chaque ligne dit le dossier où Sir Correctifs Torch
    // l'écrit (minerai/, ecrans/, blocs/), le reste est la ligne telle quelle.
    public sealed class PaquetsDeReleves
    {
        public const string Entete = "sir-correctifs-pulsar releves 1";

        // Un paquet reste petit : bien en deçà de ce que portent les messages
        // réseau du jeu, et jamais une grosse rafale d'un coup.
        public const int OctetsParPaquet = 8000;

        // Au plus ce nombre de paquets part à chaque tour du fil du jeu.
        public const int PaquetsParTour = 4;

        // Au-delà, les lignes nouvelles sont comptées puis jetées : une
        // connexion coupée ne remplit jamais la mémoire du joueur.
        public const int LignesEnAttenteMaximum = 20000;

        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        private readonly ConcurrentQueue<string> m_lignes = new ConcurrentQueue<string>();
        private int m_enAttente;
        private int m_perdues;
        private string m_reportee;

        public int EnAttente
        {
            get { return Volatile.Read(ref m_enAttente); }
        }

        // Le nombre de lignes jetées depuis la dernière lecture, remis à zéro.
        public int PrendreLesPerdues()
        {
            return Interlocked.Exchange(ref m_perdues, 0);
        }

        public void Ajouter(Suivi suivi, string ligne)
        {
            if (string.IsNullOrEmpty(ligne))
                return;
            if (Interlocked.Increment(ref m_enAttente) > LignesEnAttenteMaximum)
            {
                Interlocked.Decrement(ref m_enAttente);
                Interlocked.Increment(ref m_perdues);
                return;
            }
            m_lignes.Enqueue(NomsDesSuivis.Dossier(suivi) + " " + ligne.Replace('\r', ' ').Replace('\n', ' '));
        }

        // Tout jeter : la partie est quittée, ses lignes n'ont plus de serveur.
        public void Vider()
        {
            string ligne;
            while (m_lignes.TryDequeue(out ligne))
                Interlocked.Decrement(ref m_enAttente);
            m_reportee = null;
        }

        // Le prochain paquet, ou nul s'il n'y a rien à envoyer. Fil du jeu
        // seulement. Une ligne trop longue pour un paquet est raccourcie.
        public byte[] Suivant()
        {
            var texte = new StringBuilder(OctetsParPaquet);
            texte.Append(Entete).Append('\n');
            var octets = Utf8.GetByteCount(texte.ToString());
            var lignes = 0;

            while (true)
            {
                string ligne = m_reportee;
                if (ligne == null)
                {
                    if (!m_lignes.TryDequeue(out ligne))
                        break;
                    Interlocked.Decrement(ref m_enAttente);
                }
                m_reportee = null;

                var taille = Utf8.GetByteCount(ligne) + 1;
                if (taille > OctetsParPaquet / 2)
                {
                    ligne = Raccourcir(ligne, OctetsParPaquet / 2);
                    taille = Utf8.GetByteCount(ligne) + 1;
                }
                if (octets + taille > OctetsParPaquet)
                {
                    m_reportee = ligne;
                    break;
                }
                texte.Append(ligne).Append('\n');
                octets += taille;
                lignes++;
            }

            return lignes == 0 ? null : Utf8.GetBytes(texte.ToString());
        }

        // Envoie ce qui attend, au plus PaquetsParTour paquets. Un envoi refusé
        // garde le paquet pour le tour suivant. Rend le nombre de paquets partis.
        public int Envoyer(Func<byte[], bool> envoi)
        {
            var partis = 0;
            for (var i = 0; i < PaquetsParTour; i++)
            {
                var paquet = m_paquetRefuse ?? Suivant();
                m_paquetRefuse = null;
                if (paquet == null)
                    break;
                bool parti;
                try
                {
                    parti = envoi(paquet);
                }
                catch (Exception)
                {
                    parti = false;
                }
                if (!parti)
                {
                    m_paquetRefuse = paquet;
                    break;
                }
                partis++;
            }
            return partis;
        }

        private byte[] m_paquetRefuse;

        // Les lignes d'un paquet, sans l'en-tête : ce que le serveur lit.
        public static List<string> Lire(byte[] paquet)
        {
            var lignes = new List<string>();
            if (paquet == null)
                return lignes;
            var texte = Utf8.GetString(paquet);
            var parties = texte.Split('\n');
            for (var i = 0; i < parties.Length; i++)
            {
                if (i == 0 && parties[i] == Entete)
                    continue;
                if (parties[i].Length > 0)
                    lignes.Add(parties[i]);
            }
            return lignes;
        }

        private static string Raccourcir(string ligne, int octets)
        {
            var longueur = Math.Min(ligne.Length, octets / 3);
            return ligne.Substring(0, longueur) + " tronquee=oui";
        }
    }
}
