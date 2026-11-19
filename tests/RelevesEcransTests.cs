using System;
using System.Collections.Generic;
using Xunit;

namespace SirCorrectifsPulsar.Tests
{
    public class RelevesEcransTests
    {
        private static readonly DateTime Debut = new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc);

        // Chaque ligne porte l'heure de son événement, comme dans le greffon.
        private static SuiviDesEcrans Suivi(List<string> lignes)
        {
            var releveur = new Releveur(() => Debut, () => 1, () => new IdentiteDuJoueur("Guesn", 76561198000000001UL),
                (s, l) => lignes.Add(l));
            return new SuiviDesEcrans((e, c, m) => releveur.NoterA(SirCorrectifsPulsar.Suivi.Ecrans, m, 1, e, c));
        }

        private static ObservationDEcran Panneau(double distance, string demande, string raison = null)
        {
            return new ObservationDEcran
            {
                Cle = 42,
                Bloc = "LCD de l'entrée",
                TypeDeBloc = "LargeLCDPanel",
                Construction = "Base de Guesn",
                ConstructionId = 1234,
                SorteDeContenu = "texte",
                Distance = distance,
                PorteeDuBloc = 120,
                PorteeEffective = 80,
                Demande = demande,
                Raison = raison,
            };
        }

        [Fact]
        public void UnEcranQuiNeChangePasNeDonneAucuneLigne()
        {
            var lignes = new List<string>();
            var suivi = Suivi(lignes);
            var maintenant = Debut;

            Assert.True(suivi.Observer(Panneau(100, ObservationDEcran.Online, ObservationDEcran.HorsPortee), maintenant));
            Assert.Single(lignes);
            Assert.Contains("evenement=\"écran relevé\"", lignes[0]);
            Assert.Contains("bloc=\"LCD de l'entrée\"", lignes[0]);
            Assert.Contains("construction=\"Base de Guesn\"", lignes[0]);
            Assert.Contains("construction_id=1234", lignes[0]);
            Assert.Contains("contenu=texte", lignes[0]);
            Assert.Contains("distance=100.0", lignes[0]);

            // L'affichage échange la texture demandée : une ligne, à l'heure de l'échange.
            maintenant = Debut.AddMilliseconds(50);
            Assert.True(suivi.Echange(42, ObservationDEcran.Online, maintenant));
            Assert.Equal(2, lignes.Count);
            Assert.Contains("evenement=affichage", lignes[1]);
            Assert.Contains("affichage=Online", lignes[1]);
            Assert.Contains("ecart_demande_ms=50", lignes[1]);

            // Le joueur bouge, l'écran reste tel quel : aucune ligne, même cent fois.
            for (var i = 0; i < 100; i++)
            {
                maintenant = maintenant.AddMilliseconds(166);
                Assert.False(suivi.Observer(Panneau(100 - i * 0.1, ObservationDEcran.Online, ObservationDEcran.HorsPortee), maintenant));
                // Un échange qui ne change rien à ce qui est montré ne dit rien non plus.
                Assert.False(suivi.Echange(42, ObservationDEcran.Online, maintenant));
            }
            Assert.Equal(2, lignes.Count);

            // Le jeu demande le vrai contenu : une ligne de demande, puis une
            // ligne quand l'affichage l'échange vraiment, avec l'écart.
            maintenant = Debut.AddSeconds(30);
            Assert.True(suivi.Observer(Panneau(70, ObservationDEcran.Contenu), maintenant));
            Assert.Equal(3, lignes.Count);
            Assert.Contains("evenement=\"affichage demandé\"", lignes[2]);
            Assert.Contains("demande=contenu", lignes[2]);
            Assert.Contains("affichage=Online", lignes[2]);
            Assert.Contains("demande_precedente=Online", lignes[2]);

            Assert.True(suivi.Echange(42, ObservationDEcran.Contenu, maintenant.AddMilliseconds(700)));
            Assert.Equal(4, lignes.Count);
            Assert.Contains("2026-11-19T15:00:30.700Z", lignes[3]);
            Assert.Contains("evenement=affichage", lignes[3]);
            Assert.Contains("affichage=contenu", lignes[3]);
            Assert.Contains("affichage_precedent=Online", lignes[3]);
            Assert.Contains("duree_precedent_ms=30650", lignes[3]);
            Assert.Contains("ecart_demande_ms=700", lignes[3]);
            Assert.Contains("distance=70.0", lignes[3]);

            // Et redevient muet.
            Assert.False(suivi.Observer(Panneau(60, ObservationDEcran.Contenu), maintenant.AddSeconds(1)));
            Assert.False(suivi.Echange(42, ObservationDEcran.Contenu, maintenant.AddSeconds(1)));
            Assert.Equal(4, lignes.Count);
        }

        [Fact]
        public void RienNEstReleveAuDelaDe130MetresOu410PourLesGrandsLcdSciFi()
        {
            Assert.Equal(130.0, PorteeDesEcrans.Limite(120));
            Assert.Equal(410.0, PorteeDesEcrans.Limite(400));
            Assert.Equal(130.0, PorteeDesEcrans.Limite(0));

            var lignes = new List<string>();
            var suivi = Suivi(lignes);

            // Un LCD ordinaire (120 m) : relevé à 130 m, jamais au-delà.
            Assert.False(suivi.Observer(Panneau(130.5, ObservationDEcran.Online, ObservationDEcran.HorsPortee), Debut));
            Assert.False(suivi.Observer(Panneau(300, ObservationDEcran.Offline, ObservationDEcran.SansCourant), Debut));
            Assert.False(suivi.Echange(42, ObservationDEcran.Offline, Debut));
            Assert.Empty(lignes);
            Assert.True(suivi.Observer(Panneau(129.9, ObservationDEcran.Online, ObservationDEcran.HorsPortee), Debut));
            Assert.Single(lignes);

            // En repartant au-delà, il est oublié sans ligne, et son échange ne dit plus rien.
            Assert.False(suivi.Observer(Panneau(131, ObservationDEcran.Online, ObservationDEcran.HorsPortee), Debut));
            Assert.False(suivi.EstSuivi(42));
            Assert.False(suivi.Echange(42, ObservationDEcran.Online, Debut));
            Assert.Single(lignes);

            // Un grand LCD sci-fi (400 m) : relevé jusqu'à 410 m.
            var sciFi = Panneau(405, ObservationDEcran.Online, ObservationDEcran.HorsPortee);
            sciFi.Cle = 43;
            sciFi.PorteeDuBloc = 400;
            sciFi.PorteeEffective = 266;
            Assert.True(suivi.Observer(sciFi, Debut));
            sciFi.Distance = 410.5;
            sciFi.Demande = ObservationDEcran.Offline;
            Assert.False(suivi.Observer(sciFi, Debut));
            Assert.Equal(2, lignes.Count);
        }

        [Fact]
        public void LesReglagesDuJeuSEcriventEnFrancais()
        {
            Assert.Equal("haute", PorteeDesEcrans.Qualite("HIGH"));
            Assert.Equal("coupée", PorteeDesEcrans.Qualite("DISABLED"));
            Assert.Equal("aucun", PorteeDesEcrans.Anisotrope("NONE"));
            Assert.Equal("x16", PorteeDesEcrans.Anisotrope("ANISO_16"));
            Assert.Equal("sans courant", PorteeDesEcrans.EtatDuReseau("NoPower"));
            Assert.Equal("1/3", PorteeDesEcrans.Multiplicateur3(PorteeDesEcrans.Multiplicateur("LOW")));
            Assert.Equal(40.0, PorteeDesEcrans.PorteeEffective(120, "LOW"), 6);
        }

        [Fact]
        public void LAttenteAPorteeSeLitALaLigneDeLEchangeReel()
        {
            var lignes = new List<string>();
            var suivi = Suivi(lignes);

            suivi.Observer(Panneau(120, ObservationDEcran.Online, ObservationDEcran.HorsPortee), Debut);
            suivi.Echange(42, ObservationDEcran.Online, Debut);
            // Entre à portée (80 m) : pas de ligne tant que rien ne change.
            Assert.False(suivi.Observer(Panneau(79, ObservationDEcran.Online, ObservationDEcran.HorsPortee), Debut.AddSeconds(10)));
            // La raison change : une ligne d'attente.
            Assert.True(suivi.Observer(Panneau(75, ObservationDEcran.Online, ObservationDEcran.PasEncoreAdmis), Debut.AddSeconds(11)));
            Assert.Contains("evenement=attente", lignes[2]);
            Assert.Contains("raison=\"pas encore admis au dessin\"", lignes[2]);
            // Le jeu demande le contenu 2 s après l'entrée à portée...
            Assert.True(suivi.Observer(Panneau(72, ObservationDEcran.Contenu), Debut.AddSeconds(12)));
            Assert.Contains("evenement=\"affichage demandé\"", lignes[3]);
            // ... le rendu hors écran avance, sans ligne ...
            suivi.Rendu(42, EtapeDuRendu.MisEnFile, Debut.AddSeconds(12.1), 37, -1);
            suivi.Rendu(42, EtapeDuRendu.Soumis, Debut.AddSeconds(13), -1, 5);
            suivi.Rendu(42, EtapeDuRendu.TexturePrete, Debut.AddSeconds(13.5), -1, -1);
            Assert.Equal(4, lignes.Count);
            // ... et l'image n'est vraiment montrée que 4 s après l'entrée à portée.
            Assert.True(suivi.Echange(42, ObservationDEcran.Contenu, Debut.AddSeconds(14)));
            var ligne = lignes[4];
            Assert.Contains("evenement=affichage", ligne);
            Assert.Contains("affichage=contenu", ligne);
            Assert.Contains("attente_a_portee_ms=4000", ligne);
            Assert.Contains("ecart_demande_ms=2000", ligne);
            Assert.Contains("rendu_en_file_ms=100", ligne);
            Assert.Contains("rendu_lance_ms=1000", ligne);
            Assert.Contains("texture_prete_ms=1500", ligne);
            Assert.Contains("file_hors_ecran=37", ligne);
            Assert.Contains("rendus_en_vol=5", ligne);
            Assert.Contains("distance=72.0", ligne);
            Assert.Contains("2026-11-19T15:00:14.000Z", ligne);
        }

        [Fact]
        public void UnEcranAScriptPorteSonDernierCalculEtLesMorceauxDans32m()
        {
            var lignes = new List<string>();
            var suivi = Suivi(lignes);
            var ecran = Panneau(40, ObservationDEcran.Contenu);
            ecran.SorteDeContenu = "image du serveur";
            ecran.DernierCalcul = Debut.AddSeconds(-1);
            ecran.DureeDuCalculMs = 0.25;
            suivi.Observer(ecran, Debut);
            Assert.Contains("dernier_calcul=2026-11-19T14:59:59.000Z", lignes[0]);

            ecran.Distance = 30;
            Assert.True(suivi.Observer(ecran, Debut.AddSeconds(2)));
            Assert.Contains("evenement=\"entrée dans les 32 m\"", lignes[1]);

            ecran.MorceauxDepuis32m = 3;
            ecran.DernierMorceau = Debut.AddSeconds(3);
            Assert.True(suivi.Observer(ecran, Debut.AddSeconds(3)));
            Assert.Contains("evenement=\"premier morceau dans les 32 m\"", lignes[2]);
            Assert.Contains("dernier_morceau=2026-11-19T15:00:03.000Z", lignes[2]);
            Assert.Contains("morceaux_32m=3", lignes[2]);

            ecran.MorceauxDepuis32m = 9;
            Assert.False(suivi.Observer(ecran, Debut.AddSeconds(4)));
        }
    }
}
