using System;
using System.Collections.Generic;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class RelevesTests
    {
        [Fact]
        public void ChaqueReleveNommeLeJoueurParPseudoEtIdentifiantSteam()
        {
            var paquets = new PaquetsDeReleves();
            var releveur = new Releveur(() => new DateTime(2026, 11, 19, 13, 56, 0, 5, DateTimeKind.Utc), () => 4242,
                () => new IdentiteDuJoueur("Rouge \"Sable\"", 76561198000000002UL), paquets.Ajouter);

            // Un relevé de chaque suivi, par chacun des chemins qui en écrivent.
            releveur.Noter(Suivi.Minerai, "partie prête", new ChampsDuReleve());
            new SuiviDesMarqueurs((e, c) => releveur.Noter(Suivi.Minerai, e, c))
                .FinDePassage(1, DescriptionDuDetecteur.ForeusePortative(9, 20), new[] { new MarqueurVu("k", "Fer", 1, 0, 0) }, 0, 0, 0);
            new SuiviDesPassages((e, c) => releveur.Noter(Suivi.Minerai, e, c))
                .Commencer(1, DescriptionDuDetecteur.ForeusePortative(9, 20), 8, 0, DateTime.UtcNow);
            new SuiviDesEcrans((e, c) => releveur.Noter(Suivi.Ecrans, e, c))
                .Observer(new ObservationDEcran { Cle = 5, Distance = 12, PorteeDuBloc = 120, PorteeEffective = 80, Demande = ObservationDEcran.Online }, DateTime.UtcNow);
            releveur.Noter(Suivi.Ecrans, "réglages graphiques", new ChampsDuReleve().Texte("textures", "haute"));
            // Une ligne vue sur le fil de l'affichage, écrite plus tard avec l'heure de son événement.
            releveur.NoterA(Suivi.Blocs, new DateTime(2026, 11, 19, 13, 56, 0, 5, DateTimeKind.Utc), 4242, "objet de rendu créé",
                new ChampsDuReleve().Texte("sorte", "hologramme"));

            var lignes = new List<string>();
            byte[] paquet;
            while ((paquet = paquets.Suivant()) != null)
                lignes.AddRange(PaquetsDeReleves.Lire(paquet));

            Assert.Equal(6, lignes.Count);
            foreach (var ligne in lignes)
            {
                Assert.Contains(" joueur=76561198000000002 pseudo=\"Rouge \\\"Sable\\\"\" ", ligne);
                Assert.Contains("2026-11-19T13:56:00.005Z image=4242 ", ligne);
            }
            Assert.StartsWith("minerai ", lignes[0]);
            Assert.StartsWith("ecrans ", lignes[3]);
            Assert.StartsWith("ecrans ", lignes[4]);
            Assert.StartsWith("blocs ", lignes[5]);
        }

        [Fact]
        public void UnPseudoNeCassePasLaLigne()
        {
            var ligne = FormeDesReleves.Ligne(new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), 1,
                new IdentiteDuJoueur("a\nb", 7), "partie prête", new ChampsDuReleve().Texte("grille", "Base\r\n\"X\""));
            Assert.DoesNotContain("\n", ligne);
            Assert.DoesNotContain("\r", ligne);
            Assert.Contains("pseudo=\"a b\"", ligne);
            Assert.Contains("grille=\"Base  \\\"X\\\"\"", ligne);
            Assert.Contains("evenement=\"partie prête\"", ligne);
        }

        [Fact]
        public void LesPaquetsRestentPetitsEtGardentLOrdre()
        {
            var paquets = new PaquetsDeReleves();
            for (var i = 0; i < 300; i++)
                paquets.Ajouter(Suivi.Ecrans, "ligne " + i + " " + new string('x', 100));

            var lues = new List<string>();
            var envois = 0;
            while (paquets.EnAttente > 0 || envois == 0)
            {
                envois += paquets.Envoyer(p =>
                {
                    Assert.True(p.Length <= PaquetsDeReleves.OctetsParPaquet);
                    lues.AddRange(PaquetsDeReleves.Lire(p));
                    return true;
                });
            }
            Assert.Equal(300, lues.Count);
            Assert.StartsWith("ecrans ligne 0 ", lues[0]);
            Assert.StartsWith("ecrans ligne 299 ", lues[299]);
        }

        [Fact]
        public void UnEnvoiRefuseGardeLePaquet()
        {
            var paquets = new PaquetsDeReleves();
            paquets.Ajouter(Suivi.Minerai, "une");
            Assert.Equal(0, paquets.Envoyer(p => false));
            var recu = new List<string>();
            Assert.Equal(1, paquets.Envoyer(p => { recu.AddRange(PaquetsDeReleves.Lire(p)); return true; }));
            Assert.Equal(new[] { "minerai une" }, recu);
        }
    }
}
