using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRageMath;

namespace SirCorrectifsPulsar
{
    // Le cœur du greffon sur le fil du jeu : il sait si l'on relève (joueur
    // connecté à PU ou à PTU, partie chargée), qui joue, et il envoie les
    // relevés au serveur plusieurs fois par seconde. Les accroches lui
    // confient ce qu'elles voient ; ce qui vient d'un autre fil passe par une
    // file sans verrou, vidée ici.
    internal static class Collecte
    {
        // Le canal des relevés, de chaque joueur vers le serveur, que Sir
        // Correctifs Torch écoute. Aucun autre produit du studio ne l'utilise.
        public const ushort CanalDesReleves = 47815;

        // Les paquets partent toutes les 15 images (4 fois par seconde).
        private const int ImagesEntreDeuxEnvois = 15;

        // Le contrôle des accroches a lieu 5 minutes après la partie prête.
        private static readonly TimeSpan DelaiDuControle = TimeSpan.FromMinutes(5);

        public static readonly PaquetsDeReleves Paquets = new PaquetsDeReleves();
        public static readonly Releveur Releveur = new Releveur(() => DateTime.UtcNow, Image, () => s_joueur, Paquets.Ajouter);
        public static readonly SuiviDesMarqueurs Marqueurs = new SuiviDesMarqueurs((e, c) => Releveur.Noter(Suivi.Minerai, e, c));
        public static readonly SuiviDesPassages Passages = new SuiviDesPassages((e, c) => Releveur.Noter(Suivi.Minerai, e, c));
        public static readonly SuiviDesEcrans Ecrans = new SuiviDesEcrans((e, c, m) => Releveur.NoterA(Suivi.Ecrans, m, Image(), e, c));
        public static readonly EtatsQuiChangent Etats = new EtatsQuiChangent();

        // Les mesures des requêtes du détecteur, faites sur les fils de travail.
        public static readonly ConcurrentQueue<MesureDeRequete> Mesures = new ConcurrentQueue<MesureDeRequete>();

        // Les lignes fabriquées sur un autre fil (fil de l'affichage, fils de
        // travail), avec l'heure et l'image de leur événement.
        private struct LigneDUnAutreFil
        {
            public Suivi Suivi;
            public DateTime Moment;
            public long Image;
            public string Evenement;
            public ChampsDuReleve Champs;
        }

        private static readonly ConcurrentQueue<LigneDUnAutreFil> s_lignesDesAutresFils = new ConcurrentQueue<LigneDUnAutreFil>();
        private const int LignesDesAutresFilsMaximum = 5000;
        private static int s_nombreDeLignesDesAutresFils;

        private static volatile bool s_actif;
        private static IdentiteDuJoueur s_joueur;
        private static object s_session;
        private static DateTime s_sessionVue;
        private static DateTime? s_partiePrete;
        private static bool s_controleFait;
        private static int s_compteur;
        private static int s_fautesDEnvoi;

        // Vrai quand les accroches doivent relever : joueur nommé, sur l'un de
        // nos serveurs, partie prête. Lu de n'importe quel fil.
        public static bool Actif
        {
            get { return s_actif; }
        }

        public static IdentiteDuJoueur Joueur
        {
            get { return s_joueur; }
        }

        static Collecte()
        {
            Releveur.Fautes = e => Fautes.Noter("fabrication d'une ligne de relevé", e);
        }

        // Une ligne vue sur un autre fil que celui du jeu : elle attend dans une
        // file sans verrou, vidée par le fil du jeu. Ne lève jamais.
        public static void NoterDepuisUnAutreFil(Suivi suivi, string evenement, ChampsDuReleve champs)
        {
            if (!s_actif)
                return;
            if (System.Threading.Interlocked.Increment(ref s_nombreDeLignesDesAutresFils) > LignesDesAutresFilsMaximum)
            {
                System.Threading.Interlocked.Decrement(ref s_nombreDeLignesDesAutresFils);
                return;
            }
            long image;
            try
            {
                image = Image();
            }
            catch (Exception)
            {
                // Lue hors du fil du jeu, l'image peut manquer : la ligne garde son heure.
                image = 0;
            }
            s_lignesDesAutresFils.Enqueue(new LigneDUnAutreFil
            {
                Suivi = suivi,
                Moment = DateTime.UtcNow,
                Image = image,
                Evenement = evenement,
                Champs = champs,
            });
        }

        private static void ViderLesLignesDesAutresFils()
        {
            LigneDUnAutreFil ligne;
            while (s_lignesDesAutresFils.TryDequeue(out ligne))
            {
                System.Threading.Interlocked.Decrement(ref s_nombreDeLignesDesAutresFils);
                Releveur.NoterA(ligne.Suivi, ligne.Moment, ligne.Image, ligne.Evenement, ligne.Champs);
            }
        }

        private static long Image()
        {
            var session = MySession.Static;
            return session == null ? 0 : session.GameplayFrameCounter;
        }

        public static Vector3D PositionDuJoueur()
        {
            var session = MySession.Static;
            if (session == null)
                return Vector3D.Zero;
            var personnage = session.LocalCharacter;
            if (personnage != null)
                return personnage.PositionComp.GetPosition();
            var camera = Sandbox.Game.World.MySector.MainCamera;
            return camera == null ? Vector3D.Zero : camera.Position;
        }

        public static MyCharacter PersonnageDuJoueur()
        {
            var session = MySession.Static;
            return session == null ? null : session.LocalCharacter;
        }

        // À chaque image, sur le fil du jeu. Ne lève jamais.
        public static void MettreAJour()
        {
            try
            {
                Suivre();
            }
            catch (Exception e)
            {
                Fautes.Noter("mise à jour des relevés", e);
            }
        }

        private static void Suivre()
        {
            var session = MySession.Static;
            if (session == null || !ReferenceEquals(session, s_session))
            {
                if (s_session != null)
                    QuitterLaPartie();
                s_session = session;
                s_sessionVue = DateTime.UtcNow;
                if (session == null)
                    return;
            }

            if (!s_partiePrete.HasValue)
            {
                if (!Prete(session))
                    return;
                if (!SurNosServeurs())
                {
                    // Ailleurs que sur PU ou PTU, rien n'est relevé de toute la partie.
                    s_partiePrete = DateTime.MaxValue;
                    return;
                }
                CommencerLaPartie();
            }

            if (!s_actif)
                return;

            ViderLesMesures();
            AccrochesEcrans.Vider();
            ViderLesLignesDesAutresFils();

            if (++s_compteur % 60 == 0)
                ChaqueSeconde.Relever();

            if (s_compteur % ImagesEntreDeuxEnvois == 0)
                Envoyer();

            if (!s_controleFait && s_partiePrete.HasValue && DateTime.UtcNow - s_partiePrete.Value >= DelaiDuControle)
            {
                s_controleFait = true;
                ControlerLesAccroches();
            }
        }

        private static bool Prete(MySession session)
        {
            if (!session.Ready || session.LocalCharacter == null)
                return false;
            var joueur = MyAPIGateway.Session == null ? null : MyAPIGateway.Session.Player;
            if (joueur == null || joueur.SteamUserId == 0)
                return false;
            s_joueur = new IdentiteDuJoueur(joueur.DisplayName, joueur.SteamUserId);
            return true;
        }

        private static bool SurNosServeurs()
        {
            return RapportsDuJeu.SurNosServeurs();
        }

        private static void CommencerLaPartie()
        {
            var maintenant = DateTime.UtcNow;
            s_partiePrete = maintenant;
            s_controleFait = false;
            s_actif = true;

            var champs = new ChampsDuReleve();
            champs.Entier("chargement_ms", (long)(maintenant - s_sessionVue).TotalMilliseconds);
            champs.Texte("version_du_greffon", typeof(Collecte).Assembly.GetName().Version.ToString());
            Releveur.Noter(Suivi.Minerai, "partie prête", champs);
            Releveur.Noter(Suivi.Ecrans, "partie prête", champs);
            Releveur.Noter(Suivi.Blocs, "partie prête", champs);
            GreffonSirCorrectifsPulsar.Journal.Info("partie prête : relevés du minerai, des écrans et des blocs en route vers le serveur");

            ChaqueSeconde.Relever();
        }

        private static void QuitterLaPartie()
        {
            s_actif = false;
            s_partiePrete = null;
            s_session = null;
            Paquets.Vider();
            Marqueurs.Oublier();
            Passages.Oublier();
            Ecrans.ToutOublier();
            Etats.ToutOublier();
            AccrochesMinerai.Oublier();
            AccrochesEcrans.Oublier();
            AccrochesBlocs.Oublier();
            MesureDeRequete mesure;
            while (Mesures.TryDequeue(out mesure))
            {
            }
            LigneDUnAutreFil ligne;
            while (s_lignesDesAutresFils.TryDequeue(out ligne))
                System.Threading.Interlocked.Decrement(ref s_nombreDeLignesDesAutresFils);
        }

        // Fil du jeu seulement : à chaque image, et au rappel de chaque requête
        // du détecteur (avant la fin de son passage).
        public static void ViderLesMesures()
        {
            MesureDeRequete mesure;
            var n = 0;
            while (n++ < 256 && Mesures.TryDequeue(out mesure))
                Passages.Requete(mesure);
        }

        private static void Envoyer()
        {
            var perdues = Paquets.PrendreLesPerdues();
            if (perdues > 0)
                Releveur.Noter(Suivi.Minerai, "lignes perdues", new ChampsDuReleve().Entier("lignes", perdues));

            Paquets.Envoyer(EnvoyerUnPaquet);
        }

        private static bool EnvoyerUnPaquet(byte[] paquet)
        {
            try
            {
                var multijoueur = MyAPIGateway.Multiplayer;
                if (multijoueur == null)
                    return false;
                return multijoueur.SendMessageToServer(CanalDesReleves, paquet, true);
            }
            catch (Exception e)
            {
                // L'envoi ne casse jamais le jeu ; la faute est dite une fois.
                if (s_fautesDEnvoi++ == 0)
                    Fautes.Noter("envoi des relevés", e);
                return false;
            }
        }

        private static void ControlerLesAccroches()
        {
            foreach (Suivi suivi in new[] { Suivi.Minerai, Suivi.Ecrans, Suivi.Blocs })
            {
                var jamais = Accroches.JamaisAppelees(suivi);
                var absentes = Accroches.NonPosees(suivi);
                var champs = new ChampsDuReleve();
                champs.Entier("accroches", Accroches.Toutes.Count);
                champs.Texte("jamais_appelees", jamais.Count == 0 ? "aucune" : string.Join(", ", jamais.ToArray()));
                champs.Texte("non_posees", absentes.Count == 0 ? "aucune" : string.Join(", ", absentes.ToArray()));
                Releveur.Noter(suivi, "contrôle des accroches", champs);
                if (jamais.Count > 0)
                    GreffonSirCorrectifsPulsar.Journal.Info("accroches jamais appelées après 5 minutes (" + NomsDesSuivis.Dossier(suivi) + ") : "
                        + string.Join(", ", jamais.ToArray()));
            }
        }

        public static double Millisecondes(long debut, long fin)
        {
            return (fin - debut) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
