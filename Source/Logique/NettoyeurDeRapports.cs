using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SirCorrectifsPulsar
{
    // Retire d'un rapport d'erreur tout ce qui concerne le joueur.
    //
    // - Le dossier de l'utilisateur Windows devient %USERPROFILE%.
    // - Une ligne qui porte un identifiant Steam, un pseudo, un nom de grille
    //   ou une adresse de serveur est retirée en entier.
    //
    // Les relevés, eux, nomment le joueur exprès et ne passent jamais par ici :
    // ce nettoyage ne vaut que pour les rapports d'erreurs.
    public sealed class NettoyeurDeRapports
    {
        public const string ProfilDeLUtilisateur = "%USERPROFILE%";

        private static readonly Regex DossierDeProfil = new Regex(
            @"[A-Za-z]:[\\/]+(?:Users|Documents\sand\sSettings)[\\/]+[^\\/\s:*?""<>|]+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Un identifiant Steam (7656119...) et tout long nombre qui pourrait
        // être un compte ou un identifiant d'entité.
        private static readonly Regex LongNombre = new Regex(@"(?<!\d)\d{15,20}(?!\d)", RegexOptions.CultureInvariant);

        private static readonly Regex Adresse = new Regex(
            @"(?<![\d.])(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}(?![\d.])",
            RegexOptions.CultureInvariant);

        private static readonly Regex LigneDeVersion = new Regex(@"\b(?:version|build)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex Lien = new Regex(
            @"\b(?:steam|eos|xbl|ps|ip|tcp|udp)://|\[[0-9a-f]*:[0-9a-f:]{2,}\](?::\d+)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Les mots du jeu qui accompagnent des données du joueur.
        private static readonly Regex Mots = new Regex(
            @"\w*(?:grid|grids|play[e]r|play[e]rs|grille|grilles|joueur|joueurs)\b|\b(?:user\s*id|user\s*name|online\s*name|online\s*user\s*id|steam\s*id|steam\s*user|persona\w*|nick\s*name|pseudo\w*|display\s*name|identity|logged\s+i[n]|ship\s*name|server|servers|serveur|serveurs|server\s*name|host\s*name|hostname|lobby|connecting\s+[t]o|connected\s+[t]o|joining|joined|address|ip|public\s+ip|world\s*name|session\s*name|e-?mail|faction)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // Une ligne de pile d'appels nomme du code, jamais des données.
        private static readonly Regex CadreDePile = new Regex(@"^\s*[a]t\s+[\w.<>`\[\],|+$]+\s*\(", RegexOptions.CultureInvariant);

        private readonly string m_profil;
        private readonly List<string> m_noms = new List<string>();
        private readonly object m_verrou = new object();

        public NettoyeurDeRapports(string profil, IEnumerable<string> noms)
        {
            m_profil = string.IsNullOrEmpty(profil) ? null : profil.TrimEnd('\\', '/');
            if (noms != null)
            {
                foreach (var nom in noms)
                    AjouterUnNom(nom);
            }
        }

        // Un nom appris plus tard (le pseudo du joueur une fois la partie ouverte).
        // Les noms de moins de trois signes sont ignorés : ils correspondraient à tout.
        public void AjouterUnNom(string nom)
        {
            if (string.IsNullOrEmpty(nom))
                return;
            var net = nom.Trim();
            if (net.Length < 3)
                return;
            lock (m_verrou)
            {
                if (!m_noms.Contains(net))
                    m_noms.Add(net);
            }
        }

        // La ligne à garder, ou nulle si elle doit être retirée.
        public string Ligne(string ligne)
        {
            if (ligne == null)
                return null;

            var texte = ligne;
            if (m_profil != null && m_profil.Length > 3)
                texte = RemplacerSansCasse(texte, m_profil, ProfilDeLUtilisateur);
            texte = DossierDeProfil.Replace(texte, ProfilDeLUtilisateur);

            if (LongNombre.IsMatch(texte) || Lien.IsMatch(texte))
                return null;
            if (Adresse.IsMatch(texte) && !LigneDeVersion.IsMatch(texte))
                return null;
            if (!CadreDePile.IsMatch(texte) && Mots.IsMatch(texte))
                return null;

            lock (m_verrou)
            {
                foreach (var nom in m_noms)
                {
                    if (ContientLeMot(texte, nom))
                        return null;
                }
            }
            return texte;
        }

        public List<string> Lignes(IEnumerable<string> lignes)
        {
            var gardees = new List<string>();
            if (lignes == null)
                return gardees;
            foreach (var ligne in lignes)
            {
                var nette = Ligne(ligne);
                if (nette != null)
                    gardees.Add(nette);
            }
            return gardees;
        }

        // Un texte de plusieurs lignes, chacune jugée pour elle-même.
        public string Texte(string texte)
        {
            if (string.IsNullOrEmpty(texte))
                return "";
            return string.Join("\n", Lignes(Decouper(texte)).ToArray());
        }

        public static string[] Decouper(string texte)
        {
            return (texte ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }

        private static bool ContientLeMot(string texte, string nom)
        {
            var index = texte.IndexOf(nom, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var fin = index + nom.Length;
                var avant = index == 0 || !char.IsLetterOrDigit(texte[index - 1]);
                var apres = fin >= texte.Length || !char.IsLetterOrDigit(texte[fin]);
                if (avant && apres)
                    return true;
                index = texte.IndexOf(nom, index + 1, StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        private static string RemplacerSansCasse(string texte, string quoi, string par)
        {
            var index = texte.IndexOf(quoi, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                texte = texte.Substring(0, index) + par + texte.Substring(index + quoi.Length);
                index = texte.IndexOf(quoi, index + par.Length, StringComparison.OrdinalIgnoreCase);
            }
            return texte;
        }
    }
}
