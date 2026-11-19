using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Ce qui a trouvé un marqueur : un détecteur de minerai (bloc, grille,
    // portée, diffusé par antenne ou non), ou la foreuse portative du joueur.
    public sealed class DescriptionDuDetecteur
    {
        public long Identifiant;
        public bool Foreuse;
        public string Bloc;
        public string TypeDeBloc;
        public string Grille;
        public long GrilleId;
        public double Portee;
        public bool Antenne;

        public static DescriptionDuDetecteur ForeusePortative(long identifiant, double portee)
        {
            return new DescriptionDuDetecteur { Identifiant = identifiant, Foreuse = true, Portee = portee };
        }

        // Les champs du détecteur, tels que chaque ligne de minerai les porte.
        public void Decrire(ChampsDuReleve champs)
        {
            if (Foreuse)
            {
                champs.Texte("detecteur", "foreuse portative");
                champs.Nombre("portee", Portee, 0);
                return;
            }
            champs.Texte("detecteur", "bloc");
            champs.Texte("bloc", string.IsNullOrEmpty(Bloc) ? FormeDesReleves.Inconnu : Bloc);
            if (!string.IsNullOrEmpty(TypeDeBloc))
                champs.Texte("type", TypeDeBloc);
            champs.Texte("grille", string.IsNullOrEmpty(Grille) ? FormeDesReleves.Inconnu : Grille);
            champs.Entier("grille_id", GrilleId);
            champs.Nombre("portee", Portee, 0);
            champs.OuiNon("antenne", Antenne);
        }
    }

    // Un marqueur tel que le joueur le voit : un minerai dans une cellule d'un
    // voxel, à une position du monde.
    public struct MarqueurVu
    {
        public string Cle;
        public string Minerai;
        public double X;
        public double Y;
        public double Z;

        public MarqueurVu(string cle, string minerai, double x, double y, double z)
        {
            Cle = cle;
            Minerai = minerai;
            X = x;
            Y = y;
            Z = z;
        }

        public double Distance(double x, double y, double z)
        {
            var dx = X - x;
            var dy = Y - y;
            var dz = Z - z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    // Les marqueurs de minerai de chaque groupe (un détecteur sur un voxel),
    // tels que le jeu du joueur les a enregistrés à la fin de son dernier
    // passage. Le jeu les retire tous et les réenregistre à chaque passage :
    // seul ce qui change d'un passage à l'autre fait une ligne.
    //
    // Fil du jeu seulement (le jeu enregistre ses marqueurs sur ce fil).
    public sealed class SuiviDesMarqueurs
    {
        public const string Apparu = "marqueur apparu";
        public const string Disparu = "marqueur disparu";

        private readonly Dictionary<long, Dictionary<string, MarqueurVu>> m_groupes = new Dictionary<long, Dictionary<string, MarqueurVu>>();
        private readonly Action<string, ChampsDuReleve> m_noter;

        public SuiviDesMarqueurs(Action<string, ChampsDuReleve> noter)
        {
            m_noter = noter ?? ((e, c) => { });
        }

        public int Groupes
        {
            get { return m_groupes.Count; }
        }

        public int Marqueurs(long groupe)
        {
            Dictionary<string, MarqueurVu> marqueurs;
            return m_groupes.TryGetValue(groupe, out marqueurs) ? marqueurs.Count : 0;
        }

        // Combien de marqueurs un passage fait apparaître et disparaître, sans
        // rien noter ni retenir : la ligne de fin de passage le dit avant les
        // lignes des marqueurs eux-mêmes.
        public void Ecart(long groupe, IEnumerable<MarqueurVu> actuels, out int apparus, out int disparus)
        {
            Dictionary<string, MarqueurVu> avant;
            if (!m_groupes.TryGetValue(groupe, out avant))
                avant = new Dictionary<string, MarqueurVu>();
            var apres = new HashSet<string>();
            apparus = 0;
            if (actuels != null)
            {
                foreach (var marqueur in actuels)
                {
                    if (marqueur.Cle == null || !apres.Add(marqueur.Cle))
                        continue;
                    if (!avant.ContainsKey(marqueur.Cle))
                        apparus++;
                }
            }
            disparus = 0;
            foreach (var cle in avant.Keys)
            {
                if (!apres.Contains(cle))
                    disparus++;
            }
        }

        // La fin d'un passage complet : les marqueurs que le jeu vient
        // d'enregistrer pour ce groupe. Une ligne par marqueur apparu, une par
        // marqueur disparu. Rend le nombre de lignes.
        public int FinDePassage(long groupe, DescriptionDuDetecteur detecteur, IEnumerable<MarqueurVu> actuels,
            double joueurX, double joueurY, double joueurZ)
        {
            Dictionary<string, MarqueurVu> avant;
            if (!m_groupes.TryGetValue(groupe, out avant))
                avant = new Dictionary<string, MarqueurVu>();

            var apres = new Dictionary<string, MarqueurVu>();
            if (actuels != null)
            {
                foreach (var marqueur in actuels)
                {
                    if (marqueur.Cle != null)
                        apres[marqueur.Cle] = marqueur;
                }
            }

            var lignes = 0;
            foreach (var ancien in avant.Values)
            {
                if (!apres.ContainsKey(ancien.Cle))
                {
                    Noter(Disparu, ancien, detecteur, joueurX, joueurY, joueurZ, null);
                    lignes++;
                }
            }
            foreach (var nouveau in apres.Values)
            {
                if (!avant.ContainsKey(nouveau.Cle))
                {
                    Noter(Apparu, nouveau, detecteur, joueurX, joueurY, joueurZ, null);
                    lignes++;
                }
            }

            if (apres.Count == 0)
                m_groupes.Remove(groupe);
            else
                m_groupes[groupe] = apres;
            return lignes;
        }

        // Le jeu a retiré tous les marqueurs du groupe (Clear, voxel sorti,
        // détecteur fermé...) : une ligne de disparition pour chacun, avec la
        // cause quand l'appelant la connaît.
        public int Retirer(long groupe, DescriptionDuDetecteur detecteur, string cause,
            double joueurX, double joueurY, double joueurZ)
        {
            Dictionary<string, MarqueurVu> avant;
            if (!m_groupes.TryGetValue(groupe, out avant))
                return 0;
            m_groupes.Remove(groupe);
            foreach (var ancien in avant.Values)
                Noter(Disparu, ancien, detecteur, joueurX, joueurY, joueurZ, cause);
            return avant.Count;
        }

        // La partie est quittée : tout est oublié, sans ligne.
        public void Oublier()
        {
            m_groupes.Clear();
        }

        private void Noter(string evenement, MarqueurVu marqueur, DescriptionDuDetecteur detecteur,
            double joueurX, double joueurY, double joueurZ, string cause)
        {
            var champs = new ChampsDuReleve();
            champs.Texte("minerai", string.IsNullOrEmpty(marqueur.Minerai) ? FormeDesReleves.Inconnu : marqueur.Minerai);
            champs.Nombre("distance", marqueur.Distance(joueurX, joueurY, joueurZ), 1);
            if (detecteur != null)
                detecteur.Decrire(champs);
            else
                champs.Texte("detecteur", FormeDesReleves.Inconnu);
            champs.Texte("cause", cause);
            champs.Texte("cellule", marqueur.Cle);
            m_noter(evenement, champs);
        }
    }
}
