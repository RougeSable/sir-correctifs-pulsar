using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class ReleveBlocsDuFilTests
    {
        private static readonly DateTime Debut = new DateTime(2026, 11, 20, 12, 34, 0, DateTimeKind.Utc);
        private static readonly DateTime Fin = new DateTime(2026, 11, 20, 12, 35, 0, DateTimeKind.Utc);

        // Une horloge à 1000 ticks par seconde : un tick vaut une milliseconde.
        private static ReleveBlocsDuFil Releve()
        {
            return new ReleveBlocsDuFil(1000, () => 0, o => (string)o);
        }

        [Fact]
        public void RegroupeParTypeEtGardeLesCinqConstructionsLesPlusCouteuses()
        {
            var releve = Releve();
            var reservoir = releve.Type("bloc:OxygenTank/LargeHydrogenTank", SorteDuFil.Bloc, "OxygenTank", "LargeHydrogenTank", "Réservoir d'hydrogène");
            var propulseur = releve.Type("bloc:Thrust/LargeBlockLargeHydrogenThrust", SorteDuFil.Bloc, "Thrust", "LargeBlockLargeHydrogenThrust", "Grand propulseur à hydrogène");
            var personnages = releve.Type("personnage", SorteDuFil.Personnage, "personnage", null, null);
            Assert.Same(reservoir, releve.Type("bloc:OxygenTank/LargeHydrogenTank", SorteDuFil.Bloc, "OxygenTank", "LargeHydrogenTank", "autre nom"));

            // Sept constructions portent des réservoirs : la construction n porte
            // n réservoirs, chacun coûte n ms par appel, deux appels chacun.
            var blocs = 0;
            for (var n = 1; n <= 7; n++)
            {
                for (var b = 0; b < n; b++)
                {
                    var bloc = 1000 * n + b;
                    blocs++;
                    releve.Noter(reservoir, bloc, n, "Base " + n, n);
                    releve.Noter(reservoir, bloc, n, "Base " + n, n);
                }
            }
            releve.Noter(propulseur, 50, 3, "Base 3", 4);
            releve.Noter(personnages, 900, 0, null, 2);
            releve.Noter(personnages, 901, 0, null, 3);
            for (var i = 0; i < 37; i++)
                releve.Image(i == 0, i == 0 ? 500 : 0);
            releve.Cout(1);

            var lignes = releve.Fermer(Debut, Fin, 60000, 37, 0.0);

            // Une ligne par type, du plus coûteux au moins coûteux, puis la minute.
            Assert.Equal(4, lignes.Count);
            Assert.All(lignes.Take(3), l => Assert.Equal("blocs du fil", l.Evenement));
            Assert.Equal("minute du fil", lignes[3].Evenement);

            var r = lignes[0].Champs;
            Assert.Equal("bloc", r.Valeur("sorte"));
            Assert.Equal("OxygenTank", r.Valeur("type"));
            Assert.Equal("LargeHydrogenTank", r.Valeur("sous_type"));
            Assert.Equal("Réservoir d'hydrogène", r.Valeur("nom"));
            Assert.Equal(blocs.ToString(), r.Valeur("blocs"));
            Assert.Equal((2 * blocs).ToString(), r.Valeur("appels"));
            // Σ n × n × 2 pour n de 1 à 7 = 280 ms.
            Assert.Equal("280.000", r.Valeur("temps_total_ms"));
            Assert.Equal("10360.0", r.Valeur("temps_estime_minute_ms"));
            Assert.Equal("7", r.Valeur("constructions"));
            Assert.Equal("2026-11-20T12:34:00.000Z", r.Valeur("debut"));

            // Les 5 constructions qui coûtent le plus, de la plus coûteuse à la moins coûteuse.
            for (var i = 1; i <= 5; i++)
            {
                var n = 8 - i;
                Assert.Equal("Base " + n, r.Valeur("construction_" + i));
                Assert.Equal(n.ToString(), r.Valeur("construction_" + i + "_id"));
                Assert.Equal((2 * n * n).ToString() + ".000", r.Valeur("construction_" + i + "_ms"));
                Assert.Equal((2 * n).ToString(), r.Valeur("construction_" + i + "_appels"));
            }
            Assert.Null(r.Valeur("construction_6"));
            Assert.DoesNotContain(r.Liste, c => c.Value == "Base 1" || c.Value == "Base 2");

            // Les personnages ont leur propre ligne, sans bloc ni construction.
            var p = lignes[1].Champs;
            Assert.Equal("personnage", p.Valeur("sorte"));
            Assert.Equal("2", p.Valeur("personnages"));
            Assert.Null(p.Valeur("blocs"));
            Assert.Equal("0", p.Valeur("constructions"));
            Assert.Equal("5.000", p.Valeur("temps_total_ms"));

            var t = lignes[2].Champs;
            Assert.Equal("Thrust", t.Valeur("type"));
            Assert.Equal("1", t.Valeur("blocs"));
            Assert.Equal("Base 3", t.Valeur("construction_1"));

            // La minute : le temps du fil, le coût de la mesure et sa part.
            var m = lignes[3].Champs;
            Assert.Equal("60000.0", m.Valeur("temps_du_fil_ms"));
            Assert.Equal("37", m.Valeur("images"));
            Assert.Equal("1", m.Valeur("images_mesurees"));
            Assert.Equal("500.000", m.Valeur("temps_des_entites_ms"));
            Assert.Equal("289.000", m.Valeur("temps_attribue_ms"));
            Assert.Equal("1.000", m.Valeur("cout_de_la_mesure_ms"));
            Assert.Equal("0.0017", m.Valeur("part_du_fil_pct"));
            Assert.Equal("3", m.Valeur("types"));

            // Tout est remis à zéro pour la minute suivante.
            Assert.Equal(0, releve.Images);
            var suivante = releve.Fermer(Fin, Fin.AddMinutes(1), 60000, 37, 0.0);
            Assert.Single(suivante);
            Assert.Equal("0", suivante[0].Champs.Valeur("types"));
        }

        [Fact]
        public void LeCoutHorsEchantillonSEstimeParLesAppelsMesures()
        {
            var releve = Releve();
            var type = releve.Type("bloc:Thrust/x", SorteDuFil.Bloc, "Thrust", "x", null);
            for (var i = 0; i < 100; i++)
                releve.Noter(type, i, 1, "Base", 0);
            // 2 images mesurées sur 10 : 50 appels par image, 400 appels hors échantillon.
            for (var i = 0; i < 10; i++)
                releve.Image(i < 2, 0);

            var minute = releve.Fermer(Debut, Fin, 1000, 37, 0.01).Last().Champs;

            Assert.Equal("4.000", minute.Valeur("cout_hors_echantillon_ms"));
            Assert.Equal("0.4000", minute.Valeur("part_du_fil_pct"));
            Assert.Equal(0.4, releve.DernierePart, 6);
        }

        [Fact]
        public void LaMesureSEspaceQuandElleCouteTrop()
        {
            Assert.Equal(37, ReleveBlocsDuFil.IntervalleSuivant(37, 0.2));
            Assert.Equal(71, ReleveBlocsDuFil.IntervalleSuivant(37, 0.6));
            Assert.Equal(587, ReleveBlocsDuFil.IntervalleSuivant(587, 3.0));
            foreach (var intervalle in ReleveBlocsDuFil.Intervalles)
            {
                Assert.NotEqual(0, intervalle % 2);
                Assert.NotEqual(0, intervalle % 5);
            }
        }

        [Fact]
        public void LeReleveVaDansSonPropreDossier()
        {
            var paquets = new PaquetsDeReleves();
            var releveur = new Releveur(() => Fin, () => 1, () => new IdentiteDuJoueur("Guesn", 7), paquets.Ajouter);
            releveur.Noter(Suivi.BlocsDuFil, "minute du fil", new ChampsDuReleve().Entier("images", 3600));
            releveur.Noter(Suivi.Blocs, "partie prête", new ChampsDuReleve());

            var lignes = PaquetsDeReleves.Lire(paquets.Suivant());

            Assert.StartsWith("blocs-du-fil 2026-11-20T12:35:00.000Z image=1 joueur=7 pseudo=\"Guesn\" evenement=\"minute du fil\" images=3600", lignes[0]);
            Assert.StartsWith("blocs ", lignes[1]);
        }
    }
}
