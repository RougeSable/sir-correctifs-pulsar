using System;

namespace SirCorrectifsPulsar
{
    // Jusqu'où un écran est relevé. Le jeu dessine un écran jusqu'à la portée
    // de son bloc (120 m le plus souvent, 60 m pour les LCD d'angle, inclinés,
    // encastrés, holographiques, de console et les panneaux de texte, 400 m
    // pour les grands LCD sci-fi 3x3 et 5x3), multipliée par 1/3, 2/3 ou 1
    // selon la qualité des textures du joueur. Le greffon relève jusqu'à la
    // portée du bloc plus une marge de 10 m (130 m, ou 410 m pour les grands
    // LCD sci-fi) : au-delà, rien.
    public static class PorteeDesEcrans
    {
        public const double Marge = 10.0;

        // La portée d'un bloc dont le jeu ne dit rien : celle de la plupart.
        public const double PorteeParDefaut = 120.0;

        // Un écran à script ne reçoit les morceaux changés de son image que
        // dans ce rayon.
        public const double RayonDesMorceaux = 32.0;

        public static double PorteeDuBloc(double portee)
        {
            return portee > 0 && !double.IsNaN(portee) && !double.IsInfinity(portee) ? portee : PorteeParDefaut;
        }

        public static double Limite(double porteeDuBloc)
        {
            return PorteeDuBloc(porteeDuBloc) + Marge;
        }

        public static bool ARelever(double distance, double porteeDuBloc)
        {
            return distance >= 0 && distance <= Limite(porteeDuBloc);
        }

        // Le multiplicateur de portée de la qualité des textures, comme le jeu
        // (MySessionComponentPanels) : basse 1/3, moyenne 2/3, haute 1.
        public static double Multiplicateur(string qualiteDesTextures)
        {
            switch ((qualiteDesTextures ?? "").ToUpperInvariant())
            {
                case "LOW": return 1.0 / 3.0;
                case "HIGH": return 1.0;
                default: return 2.0 / 3.0;
            }
        }

        public static double PorteeEffective(double porteeDuBloc, string qualiteDesTextures)
        {
            return PorteeDuBloc(porteeDuBloc) * Multiplicateur(qualiteDesTextures);
        }

        // Le nom français d'une qualité du jeu.
        public static string Qualite(string qualite)
        {
            switch ((qualite ?? "").ToUpperInvariant())
            {
                case "LOW": return "basse";
                case "MEDIUM": return "moyenne";
                case "HIGH": return "haute";
                case "EXTREME": return "extrême";
                case "OFF":
                case "DISABLED": return "coupée";
                case "NORMAL": return "normale";
                default: return string.IsNullOrEmpty(qualite) ? FormeDesReleves.Inconnu : qualite.ToLowerInvariant();
            }
        }

        // Le filtrage anisotrope du jeu (NONE, ANISO_4...) en français.
        public static string Anisotrope(string filtrage)
        {
            var valeur = (filtrage ?? "").ToUpperInvariant();
            if (valeur == "NONE" || valeur.Length == 0)
                return "aucun";
            return valeur.StartsWith("ANISO_", StringComparison.Ordinal) ? "x" + valeur.Substring(6) : valeur.ToLowerInvariant();
        }

        // L'état du réseau électrique d'une grille (MyResourceStateEnum) en français.
        public static string EtatDuReseau(string etat)
        {
            switch (etat)
            {
                case "Ok": return "correct";
                case "OverloadAdaptible": return "surcharge adaptable";
                case "OverloadBlackout": return "surcharge, coupure";
                case "NoPower": return "sans courant";
                case "Disconnected": return "déconnecté";
                default: return string.IsNullOrEmpty(etat) ? FormeDesReleves.Inconnu : etat;
            }
        }

        public static string Multiplicateur3(double multiplicateur)
        {
            if (Math.Abs(multiplicateur - 1.0 / 3.0) < 0.001)
                return "1/3";
            if (Math.Abs(multiplicateur - 2.0 / 3.0) < 0.001)
                return "2/3";
            if (Math.Abs(multiplicateur - 1.0) < 0.001)
                return "1";
            return multiplicateur.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
