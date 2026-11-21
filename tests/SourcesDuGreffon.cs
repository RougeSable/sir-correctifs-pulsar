using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    // Les sources du greffon (le dossier Source, ce que Pulsar compile), lues
    // depuis le dépôt pour les gardes qui cassent la construction.
    internal static class SourcesDuGreffon
    {
        private const string Projet = "sir-correctifs-pulsar.csproj";

        private static readonly Lazy<string> s_racine = new Lazy<string>(TrouverLaRacine);

        public static string Racine
        {
            get { return s_racine.Value; }
        }

        private static string TrouverLaRacine()
        {
            for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier != null; dossier = dossier.Parent)
            {
                if (File.Exists(Path.Combine(dossier.FullName, Projet)) && Directory.Exists(Path.Combine(dossier.FullName, "Source")))
                    return dossier.FullName;
            }
            throw new InvalidOperationException("dépôt introuvable au-dessus de " + AppContext.BaseDirectory);
        }

        // Chemin relatif (avec « / ») et texte de chaque fichier .cs des sources.
        public static List<KeyValuePair<string, string>> Fichiers()
        {
            var source = Path.Combine(Racine, "Source");
            var fichiers = Directory.GetFiles(source, "*.cs", SearchOption.AllDirectories)
                .Select(f => new KeyValuePair<string, string>(
                    Path.GetRelativePath(Racine, f).Replace(Path.DirectorySeparatorChar, '/'),
                    File.ReadAllText(f, Encoding.UTF8)))
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .ToList();
            Assert.True(fichiers.Count > 10, "trop peu de sources trouvées dans " + source);
            return fichiers;
        }

        public static string Texte(string chemin)
        {
            return File.ReadAllText(Path.Combine(Racine, chemin.Replace('/', Path.DirectorySeparatorChar)), Encoding.UTF8);
        }

        // Le code seul : les commentaires sont remplacés par des espaces, les
        // chaînes et caractères gardés tels quels (les positions ne changent pas).
        public static string Code(string texte)
        {
            var sortie = new StringBuilder(texte);
            var i = 0;
            while (i < texte.Length)
            {
                var c = texte[i];
                var suivant = i + 1 < texte.Length ? texte[i + 1] : '\0';
                if (c == '/' && suivant == '/')
                {
                    while (i < texte.Length && texte[i] != '\n')
                        sortie[i++] = ' ';
                }
                else if (c == '/' && suivant == '*')
                {
                    while (i < texte.Length && !(texte[i] == '*' && i + 1 < texte.Length && texte[i + 1] == '/'))
                    {
                        if (texte[i] != '\n')
                            sortie[i] = ' ';
                        i++;
                    }
                    if (i < texte.Length)
                    {
                        sortie[i] = ' ';
                        sortie[i + 1] = ' ';
                        i += 2;
                    }
                }
                else if (c == '@' && suivant == '"')
                {
                    i += 2;
                    while (i < texte.Length)
                    {
                        if (texte[i] == '"' && i + 1 < texte.Length && texte[i + 1] == '"')
                            i += 2;
                        else if (texte[i++] == '"')
                            break;
                    }
                }
                else if (c == '"' || c == '\'')
                {
                    i++;
                    while (i < texte.Length && texte[i] != c && texte[i] != '\n')
                        i += texte[i] == '\\' ? 2 : 1;
                    i++;
                }
                else
                {
                    i++;
                }
            }
            return sortie.ToString();
        }

        // L'appel qui commence à « debut » (sur le nom appelé), jusqu'à sa
        // parenthèse fermante.
        public static string Appel(string code, int debut)
        {
            var ouvrante = code.IndexOf('(', debut);
            Assert.True(ouvrante >= 0, "appel sans parenthèse à la position " + debut);
            var profondeur = 0;
            for (var i = ouvrante; i < code.Length; i++)
            {
                if (code[i] == '(')
                    profondeur++;
                else if (code[i] == ')' && --profondeur == 0)
                    return code.Substring(debut, i + 1 - debut);
            }
            throw new InvalidOperationException("parenthèse non fermée à la position " + debut);
        }

        // Le corps (entre accolades) de la première méthode dont la
        // déclaration contient « declaration ».
        public static string Corps(string code, string declaration)
        {
            var debut = code.IndexOf(declaration, StringComparison.Ordinal);
            Assert.True(debut >= 0, "déclaration introuvable : " + declaration);
            var ouvrante = code.IndexOf('{', debut);
            var profondeur = 0;
            for (var i = ouvrante; i < code.Length; i++)
            {
                if (code[i] == '{')
                    profondeur++;
                else if (code[i] == '}' && --profondeur == 0)
                    return code.Substring(ouvrante, i + 1 - ouvrante);
            }
            throw new InvalidOperationException("accolade non fermée après " + declaration);
        }

        // Tous les appels de « nom » (par exemple « Accroches.Poser ») dans le
        // code, chacun jusqu'à sa parenthèse fermante.
        public static List<string> Appels(string code, string nom)
        {
            var motif = new Regex(@"(?<![\w.])" + Regex.Escape(nom) + @"\s*\(");
            var appels = new List<string>();
            foreach (Match m in motif.Matches(code))
                appels.Add(Appel(code, m.Index));
            return appels;
        }

        // La même, avec la position de chaque appel dans le code.
        public static List<KeyValuePair<int, string>> AppelsPlaces(string code, string nom)
        {
            var motif = new Regex(@"(?<![\w.])" + Regex.Escape(nom) + @"\s*\(");
            var appels = new List<KeyValuePair<int, string>>();
            foreach (Match m in motif.Matches(code))
                appels.Add(new KeyValuePair<int, string>(m.Index, Appel(code, m.Index)));
            return appels;
        }

        // Les arguments d'un appel (tel que rendu par Appel), séparés aux
        // virgules de premier niveau, sans les espaces autour.
        public static List<string> Arguments(string appel)
        {
            var ouvrante = appel.IndexOf('(');
            var arguments = new List<string>();
            var profondeur = 0;
            var debut = ouvrante + 1;
            for (var i = ouvrante; i < appel.Length; i++)
            {
                var c = appel[i];
                if (c == '"' || c == '\'')
                {
                    i++;
                    while (i < appel.Length && appel[i] != c)
                        i += appel[i] == '\\' ? 2 : 1;
                    continue;
                }
                if (c == '(' || c == '[' || c == '{')
                {
                    profondeur++;
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (--profondeur == 0)
                    {
                        var dernier = appel.Substring(debut, i - debut).Trim();
                        if (dernier.Length > 0 || arguments.Count > 0)
                            arguments.Add(dernier);
                        return arguments;
                    }
                }
                else if (c == ',' && profondeur == 1)
                {
                    arguments.Add(appel.Substring(debut, i - debut).Trim());
                    debut = i + 1;
                }
            }
            throw new InvalidOperationException("appel non fermé : " + appel);
        }

        // Le texte d'une chaîne écrite en toutes lettres (« "Nom" »), ou null.
        public static string Litteral(string argument)
        {
            var m = Regex.Match(argument, @"^""(\w+)""$");
            return m.Success ? m.Groups[1].Value : null;
        }

        // Une méthode déclarée dans le code : son nom, le nom de ses
        // paramètres et la position de sa déclaration.
        public sealed class Declaration
        {
            public string Nom;
            public string[] Parametres;
            public int Position;
        }

        private static readonly Regex s_declaration = new Regex(
            @"(?<=[\s;{}\]])(?:(?:public|private|internal|protected|static|override|virtual|sealed|extern|unsafe)\s+)+[\w<>\[\],.?]+\s+(\w+)\s*\(([^()]*)\)\s*\{");

        public static List<Declaration> Declarations(string code)
        {
            var declarations = new List<Declaration>();
            foreach (Match m in s_declaration.Matches(code))
            {
                var parametres = m.Groups[2].Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => Regex.Replace(p.Trim(), @"\s*=.*$", ""))
                    .Select(p => p.Split(' ').Last())
                    .ToArray();
                declarations.Add(new Declaration { Nom = m.Groups[1].Value, Parametres = parametres, Position = m.Index });
            }
            return declarations;
        }

        // La méthode déclarée qui contient cette position du code, ou null.
        public static Declaration Englobante(string code, int position)
        {
            Declaration englobante = null;
            foreach (var declaration in Declarations(code))
            {
                if (declaration.Position > position)
                    break;
                var corps = Corps(code.Substring(declaration.Position), declaration.Nom + "(");
                var debut = code.IndexOf('{', declaration.Position);
                if (position < debut + corps.Length)
                    englobante = declaration;
            }
            return englobante;
        }

        // Le corps (entre accolades) de chaque méthode de ce nom déclarée
        // dans le code.
        public static List<string> CorpsDe(string code, string nom)
        {
            return Declarations(code).Where(d => d.Nom == nom)
                .Select(d => Corps(code.Substring(d.Position), nom + "("))
                .ToList();
        }
    }
}
