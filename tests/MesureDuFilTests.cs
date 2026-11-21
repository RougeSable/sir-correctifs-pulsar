using System;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class MesureDuFilTests
    {
        private const string FichierDeLaMesure = "Source/Jeu/MesureDuFil.cs";

        private static readonly string[] Etapes = { "DispatchOnceBeforeFrame", "DispatchBeforeSimulation", "DispatchSimulate", "DispatchAfterSimulation" };

        [Fact]
        public void LesQuatreEtapesSontMesureesParEncadrementDeLAppel()
        {
            // Le plan : les quatre étapes, chacune encadrée dans son appelant,
            // une méthode de MyEntities.
            var plan = EtapesDeLOrchestrateur.Toutes;
            Assert.Equal(Etapes, plan.Select(e => e.Etape).ToArray());
            Assert.Equal(new[] { 0, 1, 2, 3 }, plan.Select(e => e.Numero).ToArray());
            Assert.Equal("Sandbox.Game.Entities.MyEntities", EtapesDeLOrchestrateur.TypeDesAppelants);
            Assert.Equal(new[] { "UpdateBeforeSimulation", "UpdateBeforeSimulation", "Simulate", "UpdateAfterSimulation" },
                plan.Select(e => e.Appelant).ToArray());
            Assert.Equal(plan.Length, plan.Select(e => e.Encadrement).Distinct().Count());
            Assert.Equal(plan.Length, plan.Select(e => e.Champ).Distinct().Count());

            // L'appel encadré : l'étape elle-même, par l'interface de
            // l'orchestrateur, sauf la première, que MyEntities.UpdateOnceBeforeFrame
            // fait en une ligne (une méthode d'une ligne n'est jamais accrochée).
            foreach (var etape in plan.Skip(1))
            {
                Assert.Equal(EtapesDeLOrchestrateur.InterfaceDesEtapes, etape.TypeAppele);
                Assert.Equal(etape.Etape, etape.Appel);
            }
            Assert.Equal(EtapesDeLOrchestrateur.TypeDesAppelants, plan[0].TypeAppele);
            Assert.Equal("UpdateOnceBeforeFrame", plan[0].Appel);

            var mesure = SourcesDuGreffon.Code(SourcesDuGreffon.Texte(FichierDeLaMesure));

            // Chaque encadrement fait l'appel de son étape dans le chronomètre,
            // sous son numéro.
            foreach (var etape in plan)
            {
                var corps = SourcesDuGreffon.Corps(mesure, "public static void " + etape.Encadrement + "(");
                var appel = Regex.Match(corps, @"s_chronometre\.Encadrer\(\s*" + etape.Numero + @"\s*,\s*EtapeMesuree\(\)\s*,[^;]*?(s_appel\w+)\s*\)\s*;");
                Assert.True(appel.Success, etape.Encadrement + " n'encadre pas l'appel de l'étape " + etape.Etape);
                var delegue = appel.Groups[1].Value;
                var attendu = etape.TypeAppele == EtapesDeLOrchestrateur.InterfaceDesEtapes
                    ? delegue + " = o => o." + etape.Appel + "();"
                    : delegue + " = () => MyEntities." + etape.Appel + "();";
                Assert.Contains(attendu, mesure);
            }

            // Les encadrements sont posés par réécriture des appelants, une par
            // appelant, et sur rien d'autre.
            Assert.Contains("foreach (var appelant in EtapesDeLOrchestrateur.ParAppelant())", mesure);
            Assert.Equal(new[] { "UpdateBeforeSimulation", "Simulate", "UpdateAfterSimulation" },
                EtapesDeLOrchestrateur.ParAppelant().Select(a => a.Key).ToArray());
            var poser = SourcesDuGreffon.Corps(mesure, "private static Accroche PoserLEncadrement(");
            var reecritures = SourcesDuGreffon.Appels(poser, "Accroches.Reecrire");
            Assert.Single(reecritures);
            Assert.Contains("\"EncadrerLesEtapes\"", reecritures[0]);

            // Aucun préfixe ni suffixe sur les étapes, nulle part dans le greffon.
            Assert.Empty(SourcesDuGreffon.Appels(mesure, "Accroches.Poser"));
            Assert.DoesNotContain("AvantLesEntites", mesure);
            Assert.DoesNotContain("ApresLesEntites", mesure);
            foreach (var fichier in SourcesDuGreffon.Fichiers())
            {
                foreach (var pose in SourcesDuGreffon.Appels(SourcesDuGreffon.Code(fichier.Value), "Accroches.Poser"))
                {
                    Assert.DoesNotContain("EtapesDeLOrchestrateur", pose);
                    Assert.DoesNotContain("etapes[", pose);
                    foreach (var etape in Etapes)
                        Assert.DoesNotContain(etape, pose);
                }
            }
        }

        [Fact]
        public void LeChronometreFaitLAppelUneFoisEtNeCompteQueLesImagesMesurees()
        {
            long maintenant = 0;
            var chronometre = new ChronometreDesEtapes(() => maintenant);
            var appels = 0;
            Action<int> etape = duree => { appels++; maintenant += duree; };

            // Image non mesurée : l'appel est fait, rien n'est compté.
            chronometre.Encadrer(1, false, 7, etape);
            chronometre.Encadrer(0, false, () => etape(9));
            Assert.Equal(2, appels);
            Assert.Equal(0, chronometre.Prendre(null));

            // Image mesurée : chaque étape a sa durée.
            chronometre.Encadrer(0, true, () => etape(2));
            chronometre.Encadrer(1, true, 5, etape);
            chronometre.Encadrer(2, true, 3, etape);
            chronometre.Encadrer(3, true, 1, etape);
            Assert.Equal(6, appels);
            Assert.Equal(5, chronometre.TicksDe(1));
            var parEtape = new long[4];
            Assert.Equal(11, chronometre.Prendre(parEtape));
            Assert.Equal(new long[] { 2, 5, 3, 1 }, parEtape);
            Assert.Equal(0, chronometre.TicksDe(1));

            // Une exception du jeu passe telle quelle, sa durée est comptée.
            var faute = new InvalidOperationException("faute du jeu");
            var levee = Assert.Throws<InvalidOperationException>(() =>
                chronometre.Encadrer<int>(2, true, 4, duree => { maintenant += duree; throw faute; }));
            Assert.Same(faute, levee);
            Assert.Equal(4, chronometre.Prendre(null));
        }

        [Fact]
        public void LaMinuteDuFilDonneUneDureePourChacuneDesQuatreEtapes()
        {
            // Une horloge à 1000 ticks par seconde : un tick vaut une milliseconde.
            var releve = new ReleveBlocsDuFil(1000, () => 0, o => (string)o);
            releve.Image(true, 10, new long[] { 1, 2, 3, 4 });
            releve.Image(false, 0, new long[] { 0, 0, 0, 0 });
            releve.Image(true, 20, new long[] { 2, 4, 6, 8 });
            var debut = new DateTime(2026, 11, 20, 12, 34, 0, DateTimeKind.Utc);
            var lignes = releve.Fermer(debut, debut.AddMinutes(1), 60000, 37, 0);
            var minute = lignes.Last();
            Assert.Equal(ReleveBlocsDuFil.EvenementDeLaMinute, minute.Evenement);
            var champs = minute.Champs;
            Assert.Equal("30.000", champs.Valeur("temps_des_entites_ms"));
            Assert.Equal("3.000", champs.Valeur("temps_une_fois_avant_l_image_ms"));
            Assert.Equal("6.000", champs.Valeur("temps_avant_la_simulation_ms"));
            Assert.Equal("9.000", champs.Valeur("temps_de_la_simulation_ms"));
            Assert.Equal("12.000", champs.Valeur("temps_apres_la_simulation_ms"));

            // Une minute sans rien de mesuré les donne quand même, à zéro.
            var vide = releve.Fermer(debut, debut.AddMinutes(1), 60000, 37, 0).Last().Champs;
            foreach (var etape in EtapesDeLOrchestrateur.Toutes)
                Assert.Equal("0.000", vide.Valeur(etape.Champ));
        }
    }
}
