using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    // Des gardes sur les sources du greffon : elles cassent la construction
    // avant qu'une accroche dangereuse ne parte chez les joueurs.
    public class GardeDesSourcesTests
    {
        private const string Registre = "Source/Logique/RegistreDesMethodesInterdites.cs";
        private const string FichierDesAccroches = "Source/Jeu/Accroches.cs";
        private const string FichierDeLaMesure = "Source/Jeu/MesureDuFil.cs";

        // Une lecture du chronomètre : l'horloge fine (Stopwatch et ses
        // ticks, Elapsed), le temps processeur d'un fil, ou ce qui les lit
        // dans le greffon (Collecte.Millisecondes, le chronomètre des étapes).
        // L'heure d'un événement (DateTime.UtcNow) n'en est pas une : elle
        // date une ligne de relevé, elle ne mesure aucun appel.
        private static readonly Regex s_chronometre = new Regex(
            @"\bStopwatch\b|\bGetTimestamp\b|\.Elapsed\w*\b|\bMillisecondes\s*\(|\bTempsProcesseur\s*\(|\bGetThreadTimes\b"
            + @"|\bTickCount\w*\b|\bQueryPerformance\w*\b|\bChronometreDesEtapes\b|\bs_chronometre\b");

        [Fact]
        public void PerformParallelUpdateNApparaitQueDansLeRegistre()
        {
            var fichiers = SourcesDuGreffon.Fichiers();
            Assert.Contains(fichiers, f => f.Key == Registre);
            foreach (var fichier in fichiers)
            {
                var present = fichier.Value.IndexOf("PerformParallelUpdate", StringComparison.OrdinalIgnoreCase) >= 0;
                if (fichier.Key == Registre)
                    Assert.True(present, "le registre des méthodes interdites doit nommer PerformParallelUpdate");
                else
                    Assert.False(present, fichier.Key + " nomme PerformParallelUpdate : ce nom ne s'écrit que dans le registre des méthodes interdites");
            }
        }

        [Fact]
        public void AucuneMesureNePoseDePrefixeNiDeSuffixe()
        {
            var fichiers = SourcesDuGreffon.Fichiers();

            // 1. Une seule pose d'accroche dans tout le greffon : celle du
            // point d'entrée, que le registre des méthodes interdites garde.
            var poses = fichiers
                .SelectMany(f => Regex.Matches(SourcesDuGreffon.Code(f.Value), @"\.\s*Patch\s*\(").Cast<Match>().Select(m => f.Key))
                .ToList();
            Assert.True(poses.Count == 1 && poses[0] == FichierDesAccroches,
                "une accroche contourne le point d'entrée : poses trouvées dans " + string.Join(", ", poses));
            var codeDesAccroches = SourcesDuGreffon.Code(SourcesDuGreffon.Texte(FichierDesAccroches));
            var points = SourcesDuGreffon.Appels(codeDesAccroches, "PointDAccroche.Accrocher");
            Assert.True(points.Count == 1 && Regex.IsMatch(points[0], @"\.\s*Patch\s*\("),
                "la pose d'accroche de " + FichierDesAccroches + " doit se faire dans l'appel de PointDAccroche.Accrocher");

            // 2. Aucun autre chemin vers Harmony pour poser une accroche.
            var detours = new[]
            {
                @"\bPatchAll\s*\(",
                @"\[\s*(HarmonyLib\.)?Harmony(Patch|Prefix|Postfix|Finalizer|Transpiler)\b",
                @"\bCreateClassProcessor\s*\(",
                @"\bCreateProcessor\s*\(",
                @"\bnew\s+(HarmonyLib\.)?PatchProcessor\b",
                @"\bReversePatch",
                @"\bCreateReversePatcher\s*\(",
                @"\bDetourMethod\s*\(",
                @"\bPatchClassProcessor\b",
            };
            foreach (var fichier in fichiers)
            {
                var code = SourcesDuGreffon.Code(fichier.Value);
                foreach (var detour in detours)
                    Assert.False(Regex.IsMatch(code, detour), fichier.Key + " contourne le point d'entrée des accroches (" + detour + ")");
                if (fichier.Key != FichierDesAccroches)
                    Assert.False(Regex.IsMatch(code, @"\bHarmonyMethod\b"), fichier.Key + " prépare une accroche hors du point d'entrée (HarmonyMethod)");
            }

            // 3. Les poses de mesure (réécriture, encadrement d'un appel) ne
            // passent ni préfixe ni suffixe ni finaliseur.
            var reecrire = SourcesDuGreffon.Corps(codeDesAccroches, "public static Accroche Reecrire(");
            Assert.Contains("Accrocher(harmony, suivi, nom, cible, null, null, null, reecriture, porteur)", reecrire);
            var encadrer = SourcesDuGreffon.Corps(codeDesAccroches, "public static Accroche Encadrer(");
            var posesSur = SourcesDuGreffon.Appels(codeDesAccroches, "PoserSur").Where(a => !a.Contains("Harmony harmony")).ToList();
            Assert.Equal(2, posesSur.Count);
            Assert.Contains("PoserSur(harmony, methode, nom, prefixe, postfixe, finaliseur, reecriture, porteur)", posesSur);
            var poseDeLEncadrement = Assert.Single(SourcesDuGreffon.Appels(encadrer, "PoserSur"));
            Assert.Matches(@"^PoserSur\(harmony, appelant, [^,]+, null, null, null, ""EncadrerLesAppels"", typeof\(Accroches\)\)$", poseDeLEncadrement);

            // 4. Une mesure des blocs du fil ne pose que des réécritures qui
            // encadrent les appels.
            var mesure = SourcesDuGreffon.Code(SourcesDuGreffon.Texte(FichierDeLaMesure));
            Assert.Empty(SourcesDuGreffon.Appels(mesure, "Accroches.Poser"));
            Assert.NotEmpty(SourcesDuGreffon.Appels(mesure, "Accroches.Reecrire"));
            foreach (var fichier in fichiers)
            {
                var code = SourcesDuGreffon.Code(fichier.Value);
                foreach (var pose in SourcesDuGreffon.Appels(code, "Accroches.Poser"))
                    Assert.False(pose.Contains("Suivi.BlocsDuFil"), fichier.Key + " pose une mesure en préfixe ou suffixe : " + pose);
            }

            // 5. Quel que soit son suivi, aucune méthode passée en préfixe,
            // suffixe ou finaliseur ne lit le chronomètre, ni elle-même ni par
            // une méthode du greffon qu'elle appelle : un chronomètre posé en
            // préfixe et suffixe est la forme du 20/11.
            var crochets = MethodesPasseesEnPrefixeSuffixeOuFinaliseur(fichiers);
            Assert.True(crochets.Count >= 20, "trop peu de préfixes et suffixes trouvés : " + crochets.Count);
            var codes = fichiers.ToDictionary(f => f.Key, f => SourcesDuGreffon.Code(f.Value));
            foreach (var crochet in crochets)
            {
                Assert.True(SourcesDuGreffon.CorpsDe(codes[crochet.Key], crochet.Value).Count > 0,
                    crochet.Key + " : " + crochet.Value + ", passée en préfixe, suffixe ou finaliseur, est introuvable");
                var lecture = LectureDuChronometre(codes, crochet.Key, crochet.Value);
                Assert.True(lecture == null, crochet.Key + " : " + crochet.Value + ", passée en préfixe, suffixe ou finaliseur, lit le chronomètre ("
                    + lecture + ") : une mesure encadre l'appel que le jeu fait (Accroches.Encadrer), elle ne se pose jamais sur la méthode du jeu");
            }

            // 6. Les chronomètres du greffon encadrent les appels du jeu : les
            // étapes et les entités des blocs du fil, la mise à jour d'un
            // écran, le travail d'une requête de minerai, le chargement d'un
            // modèle.
            var encadrements = fichiers
                .SelectMany(f => SourcesDuGreffon.Appels(SourcesDuGreffon.Code(f.Value), "Accroches.Encadrer"))
                .ToList();
            foreach (var attendu in new[] { "\"EncadrerLaMiseAJour\"", "\"LancerLaRequete\"", "\"EncadrerLeChargement\"" })
                Assert.Contains(encadrements, e => e.Contains(attendu));
        }

        // Chaque méthode passée en préfixe, suffixe ou finaliseur à
        // Accroches.Poser (fichier, nom). Un nom passé par un paramètre de la
        // méthode qui pose est lu à chacun de ses appels, où il doit être
        // écrit en toutes lettres.
        private static List<KeyValuePair<string, string>> MethodesPasseesEnPrefixeSuffixeOuFinaliseur(List<KeyValuePair<string, string>> fichiers)
        {
            var crochets = new List<KeyValuePair<string, string>>();
            foreach (var fichier in fichiers)
            {
                var code = SourcesDuGreffon.Code(fichier.Value);
                foreach (var pose in SourcesDuGreffon.AppelsPlaces(code, "Accroches.Poser"))
                {
                    // Poser(harmony, suivi, nom, cible, préfixe, suffixe[, finaliseur], porteur)
                    var arguments = SourcesDuGreffon.Arguments(pose.Value);
                    Assert.True(arguments.Count == 7 || arguments.Count == 8, fichier.Key + " : pose inattendue " + pose.Value);
                    for (var k = 4; k < arguments.Count - 1; k++)
                    {
                        var argument = arguments[k];
                        if (argument == "null")
                            continue;
                        var nom = SourcesDuGreffon.Litteral(argument);
                        if (nom != null)
                        {
                            crochets.Add(new KeyValuePair<string, string>(fichier.Key, nom));
                            continue;
                        }
                        var englobante = SourcesDuGreffon.Englobante(code, pose.Key);
                        Assert.True(englobante != null, fichier.Key + " : pose hors d'une méthode " + pose.Value);
                        var indice = Array.IndexOf(englobante.Parametres, argument);
                        Assert.True(indice >= 0, fichier.Key + " : « " + argument + " » n'est ni un nom écrit en toutes lettres ni un paramètre de "
                            + englobante.Nom + " (" + pose.Value + ")");
                        var corps = code.IndexOf('{', englobante.Position);
                        var appels = SourcesDuGreffon.AppelsPlaces(code, englobante.Nom)
                            .Where(a => a.Key < englobante.Position || a.Key > corps)
                            .ToList();
                        Assert.True(appels.Count > 0, fichier.Key + " : " + englobante.Nom + " n'est jamais appelée");
                        foreach (var appel in appels)
                        {
                            var donne = SourcesDuGreffon.Arguments(appel.Value);
                            var nomDonne = indice < donne.Count ? SourcesDuGreffon.Litteral(donne[indice]) : null;
                            Assert.True(nomDonne != null, fichier.Key + " : le nom passé à " + englobante.Nom + " doit s'écrire en toutes lettres (" + appel.Value + ")");
                            crochets.Add(new KeyValuePair<string, string>(fichier.Key, nomDonne));
                        }
                    }
                }
            }
            return crochets.Distinct().ToList();
        }

        // La première lecture du chronomètre que fait cette méthode, elle-même
        // ou par une méthode du greffon qu'elle appelle (de sa classe, ou
        // d'une autre classe nommée), avec le chemin qui y mène ; null s'il
        // n'y en a aucune.
        private static string LectureDuChronometre(Dictionary<string, string> codes, string fichier, string methode)
        {
            var classes = new Dictionary<string, string>();
            foreach (var code in codes)
            {
                foreach (Match m in Regex.Matches(code.Value, @"\bclass\s+(\w+)"))
                    classes[m.Groups[1].Value] = code.Key;
            }
            var declarees = codes.ToDictionary(c => c.Key, c => new HashSet<string>(SourcesDuGreffon.Declarations(c.Value).Select(d => d.Nom)));

            var vues = new HashSet<string>();
            var aVoir = new Queue<Tuple<string, string, string>>();
            aVoir.Enqueue(Tuple.Create(fichier, methode, methode));
            while (aVoir.Count > 0)
            {
                var suivante = aVoir.Dequeue();
                if (!vues.Add(suivante.Item1 + "|" + suivante.Item2))
                    continue;
                foreach (var corps in SourcesDuGreffon.CorpsDe(codes[suivante.Item1], suivante.Item2))
                {
                    var lecture = s_chronometre.Match(corps);
                    if (lecture.Success)
                        return suivante.Item3 + " : " + lecture.Value.Trim();
                    foreach (Match appel in Regex.Matches(corps, @"(?<![\w.])(\w+)\s*\("))
                    {
                        var nom = appel.Groups[1].Value;
                        if (declarees[suivante.Item1].Contains(nom))
                            aVoir.Enqueue(Tuple.Create(suivante.Item1, nom, suivante.Item3 + " → " + nom));
                    }
                    foreach (Match appel in Regex.Matches(corps, @"(?<![\w.])([A-Z]\w*)\.(\w+)\s*\("))
                    {
                        string autre;
                        if (classes.TryGetValue(appel.Groups[1].Value, out autre) && declarees[autre].Contains(appel.Groups[2].Value))
                            aVoir.Enqueue(Tuple.Create(autre, appel.Groups[2].Value, suivante.Item3 + " → " + appel.Groups[1].Value + "." + appel.Groups[2].Value));
                    }
                }
            }
            return null;
        }
    }
}
