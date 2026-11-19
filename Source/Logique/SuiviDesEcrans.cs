using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Ce que le jeu du joueur demande pour un écran, vu à un instant sur le fil
    // du jeu (après sa mise à jour).
    public sealed class ObservationDEcran
    {
        // Ce que l'écran affiche, tel que le relevé l'écrit.
        public const string Online = "Online";
        public const string Offline = "Offline";
        public const string Contenu = "contenu";
        public const string Image = "image";
        public const string Vide = "vide";
        public const string Aucun = "aucun";

        // Les raisons de l'attente que le jeu connaît.
        public const string HorsPortee = "hors portée";
        public const string PasEncoreAdmis = "pas encore admis au dessin";
        public const string ObjetDeRenduPasPret = "objet de rendu pas prêt";
        public const string SansCourant = "bloc vu sans courant";
        public const string ReservePleine = "réserve de dessin pleine";
        public const string BlocEndommage = "bloc endommagé";
        public const string RaisonInconnue = "raison illisible";

        public long Cle;
        public string Bloc;
        public string TypeDeBloc;
        public string Ecran;
        public string Construction;
        public long ConstructionId;
        public string SorteDeContenu;
        public string Script;
        public double Distance;
        public double PorteeDuBloc;
        public double PorteeEffective;
        // Ce que le jeu a demandé d'afficher (Online, Offline, contenu...) ;
        // l'écran ne le montre qu'une fois l'échange fait par l'affichage.
        public string Demande;
        public string Raison;
        public string Alimentation;
        public DateTime? DernierCalcul;
        public double DureeDuCalculMs;
        public DateTime? DernierMorceau;
        public int MorceauxDepuis32m;
        public bool MorceauRecuHorsDes32m;

        public static bool EstUnVraiContenu(string affichage)
        {
            return affichage == Contenu || affichage == Image || affichage == Vide;
        }
    }

    // Une étape du rendu hors écran d'un écran, vue sur le fil de l'affichage.
    public enum EtapeDuRendu
    {
        // Le jeu a confié le dessin de l'écran à l'affichage (MyOffscreenRenderer.Add).
        MisEnFile,
        // L'affichage l'a lancé sur un fil de travail (SubmitWork).
        Soumis,
        // Le dessin est fini : la texture est prête à être échangée (SetTextureReady).
        TexturePrete
    }

    // Suit chaque écran à portée du joueur et ne note que ce qui change :
    //   - ce que le jeu demande d'afficher (« affichage demandé ») ;
    //   - ce que l'écran affiche vraiment (« affichage »), au moment où le fil
    //     de l'affichage échange sa texture : c'est l'heure que voit le joueur.
    //     La ligne dit l'écart avec la demande, les étapes du rendu hors écran
    //     et, à l'arrivée du vrai contenu, depuis combien de temps l'écran
    //     attendait à portée ;
    //   - la raison de l'attente, la sorte de contenu, l'entrée dans les 32 m
    //     et le premier morceau d'image reçu dans ce rayon.
    // Un écran qui ne change pas ne produit aucune ligne ; un écran au-delà de
    // la portée de son bloc plus la marge (PorteeDesEcrans) n'en produit
    // jamais, et il est oublié.
    //
    // Appelable de n'importe quel fil : un verrou protège l'état.
    public sealed class SuiviDesEcrans
    {
        public const string Releve = "écran relevé";
        public const string DemandeChangee = "affichage demandé";
        public const string AffichageChange = "affichage";
        public const string Attente = "attente";
        public const string ContenuChange = "contenu changé";
        public const string Entree32m = "entrée dans les 32 m";
        public const string Sortie32m = "sortie des 32 m";
        public const string PremierMorceau = "premier morceau dans les 32 m";

        private sealed class Etat
        {
            public ObservationDEcran Dernier;
            public string Demande;
            public DateTime DemandeA;
            public bool EchangeAttendu;
            // Ce que l'écran montre vraiment, connu au premier échange vu.
            public string Affichage;
            public DateTime AffichageA;
            public string Raison;
            public string SorteDeContenu;
            public string Script;
            public bool Dans32m;
            public bool AMorceaux;
            public DateTime? EntreeAPortee;
            // Les étapes du rendu hors écran depuis la dernière demande.
            public DateTime? MisEnFile;
            public DateTime? Soumis;
            public DateTime? TexturePrete;
            public int FileHorsEcran = -1;
            public int RendusEnVol = -1;
        }

        private readonly object m_verrou = new object();
        private readonly Dictionary<long, Etat> m_ecrans = new Dictionary<long, Etat>();
        private readonly Action<string, ChampsDuReleve, DateTime> m_noter;

        public SuiviDesEcrans(Action<string, ChampsDuReleve> noter)
        {
            var sortie = noter ?? ((e, c) => { });
            m_noter = (e, c, m) => sortie(e, c);
        }

        // La sortie reçoit aussi l'heure de l'événement, qui peut précéder
        // celle où la ligne est fabriquée (événement vu sur le fil de
        // l'affichage).
        public SuiviDesEcrans(Action<string, ChampsDuReleve, DateTime> noter)
        {
            m_noter = noter ?? ((e, c, m) => { });
        }

        public int Suivis
        {
            get
            {
                lock (m_verrou)
                    return m_ecrans.Count;
            }
        }

        public bool EstSuivi(long cle)
        {
            lock (m_verrou)
                return m_ecrans.ContainsKey(cle);
        }

        // Vrai quand l'écran attend encore l'échange de sa dernière demande.
        public bool EchangeAttendu(long cle)
        {
            lock (m_verrou)
            {
                Etat etat;
                return m_ecrans.TryGetValue(cle, out etat) && etat.EchangeAttendu;
            }
        }

        // L'écran vu après sa mise à jour par le jeu. Rend vrai quand
        // l'observation a donné une ligne.
        public bool Observer(ObservationDEcran vu, DateTime maintenant)
        {
            if (vu == null)
                return false;

            string evenement;
            Etat etat;
            string demandePrecedente = null;

            lock (m_verrou)
            {
                if (!PorteeDesEcrans.ARelever(vu.Distance, vu.PorteeDuBloc))
                {
                    m_ecrans.Remove(vu.Cle);
                    return false;
                }

                var aPortee = vu.Distance <= vu.PorteeEffective;
                var dans32m = vu.Distance <= PorteeDesEcrans.RayonDesMorceaux;
                var aMorceaux = dans32m && vu.MorceauxDepuis32m > 0;

                if (!m_ecrans.TryGetValue(vu.Cle, out etat))
                {
                    // La demande en cours peut ne pas être encore échangée :
                    // on attend son échange (il n'arrive pas si elle l'est déjà).
                    etat = new Etat
                    {
                        Dernier = vu,
                        Demande = vu.Demande,
                        DemandeA = maintenant,
                        EchangeAttendu = true,
                        Raison = vu.Raison,
                        SorteDeContenu = vu.SorteDeContenu,
                        Script = vu.Script,
                        Dans32m = dans32m,
                        AMorceaux = aMorceaux,
                        EntreeAPortee = aPortee ? (DateTime?)maintenant : null,
                    };
                    m_ecrans[vu.Cle] = etat;
                    evenement = Releve;
                }
                else
                {
                    etat.Dernier = vu;

                    // L'entrée à portée (portée du bloc et qualité des textures)
                    // se garde sans ligne : elle sert à lire combien de temps
                    // l'écran a attendu à portée.
                    if (aPortee && !etat.EntreeAPortee.HasValue)
                        etat.EntreeAPortee = maintenant;
                    else if (!aPortee)
                        etat.EntreeAPortee = null;

                    if (vu.Demande != etat.Demande)
                    {
                        evenement = DemandeChangee;
                        demandePrecedente = etat.Demande;
                        etat.Demande = vu.Demande;
                        etat.DemandeA = maintenant;
                        etat.EchangeAttendu = true;
                        etat.MisEnFile = null;
                        etat.Soumis = null;
                        etat.TexturePrete = null;
                        etat.FileHorsEcran = -1;
                        etat.RendusEnVol = -1;
                    }
                    else if (vu.Raison != etat.Raison)
                        evenement = Attente;
                    else if (vu.SorteDeContenu != etat.SorteDeContenu || vu.Script != etat.Script)
                        evenement = ContenuChange;
                    else if (dans32m != etat.Dans32m)
                        evenement = dans32m ? Entree32m : Sortie32m;
                    else if (aMorceaux && !etat.AMorceaux)
                        evenement = PremierMorceau;
                    else
                    {
                        etat.AMorceaux = aMorceaux;
                        return false;
                    }

                    etat.Raison = vu.Raison;
                    etat.SorteDeContenu = vu.SorteDeContenu;
                    etat.Script = vu.Script;
                    etat.Dans32m = dans32m;
                    etat.AMorceaux = aMorceaux;
                }
            }

            var champs = Champs(vu, etat);
            if (demandePrecedente != null)
                champs.Texte("demande_precedente", demandePrecedente);
            m_noter(evenement, champs, maintenant);
            return true;
        }

        // Une étape du rendu hors écran, vue sur le fil de l'affichage. Sans
        // ligne : elle est rapportée par la ligne de l'échange qui suit. Seule
        // la première de chaque étape depuis la dernière demande compte.
        public void Rendu(long cle, EtapeDuRendu etape, DateTime moment, int fileHorsEcran, int rendusEnVol)
        {
            lock (m_verrou)
            {
                Etat etat;
                if (!m_ecrans.TryGetValue(cle, out etat) || !etat.EchangeAttendu)
                    return;
                switch (etape)
                {
                    case EtapeDuRendu.MisEnFile:
                        if (!etat.MisEnFile.HasValue)
                        {
                            etat.MisEnFile = moment;
                            if (fileHorsEcran >= 0)
                                etat.FileHorsEcran = fileHorsEcran;
                        }
                        break;
                    case EtapeDuRendu.Soumis:
                        if (!etat.Soumis.HasValue)
                        {
                            etat.Soumis = moment;
                            if (rendusEnVol >= 0)
                                etat.RendusEnVol = rendusEnVol;
                        }
                        break;
                    default:
                        if (!etat.TexturePrete.HasValue)
                            etat.TexturePrete = moment;
                        break;
                }
            }
        }

        // Le fil de l'affichage a échangé la texture de l'écran : c'est ce que
        // le joueur voit désormais. Rend vrai quand l'échange a donné une
        // ligne : un échange qui ne change rien à ce qui est montré, hors de
        // toute demande, n'en donne pas.
        public bool Echange(long cle, string affichage, DateTime moment)
        {
            Etat etat;
            ObservationDEcran vu;
            var champs = new ChampsDuReleve();
            lock (m_verrou)
            {
                if (!m_ecrans.TryGetValue(cle, out etat))
                    return false;
                if (!etat.EchangeAttendu && affichage == etat.Affichage)
                    return false;
                vu = etat.Dernier;

                champs.Texte("affichage", affichage ?? ObservationDEcran.Aucun);
                champs.Texte("demande", etat.Demande ?? ObservationDEcran.Aucun);
                champs.Texte("affichage_precedent", etat.Affichage ?? FormeDesReleves.Inconnu);
                if (etat.Affichage != null)
                    champs.Entier("duree_precedent_ms", Millisecondes(etat.AffichageA, moment));
                if (etat.EchangeAttendu)
                {
                    champs.Heure("demande_a", etat.DemandeA);
                    champs.Entier("ecart_demande_ms", Millisecondes(etat.DemandeA, moment));
                    if (etat.MisEnFile.HasValue)
                        champs.Entier("rendu_en_file_ms", Millisecondes(etat.DemandeA, etat.MisEnFile.Value));
                    if (etat.Soumis.HasValue)
                        champs.Entier("rendu_lance_ms", Millisecondes(etat.DemandeA, etat.Soumis.Value));
                    if (etat.TexturePrete.HasValue)
                        champs.Entier("texture_prete_ms", Millisecondes(etat.DemandeA, etat.TexturePrete.Value));
                    if (etat.FileHorsEcran >= 0)
                        champs.Entier("file_hors_ecran", etat.FileHorsEcran);
                    if (etat.RendusEnVol >= 0)
                        champs.Entier("rendus_en_vol", etat.RendusEnVol);
                }
                if (ObservationDEcran.EstUnVraiContenu(affichage) && !ObservationDEcran.EstUnVraiContenu(etat.Affichage)
                    && etat.EntreeAPortee.HasValue)
                    champs.Entier("attente_a_portee_ms", Millisecondes(etat.EntreeAPortee.Value, moment));

                etat.Affichage = affichage;
                etat.AffichageA = moment;
                if (affichage == etat.Demande)
                    etat.EchangeAttendu = false;
            }

            Ajouter(champs, vu, etat);
            m_noter(AffichageChange, champs, moment);
            return true;
        }

        // L'écran a quitté le monde du joueur (bloc fermé, grille partie).
        public void Oublier(long cle)
        {
            lock (m_verrou)
                m_ecrans.Remove(cle);
        }

        public void ToutOublier()
        {
            lock (m_verrou)
                m_ecrans.Clear();
        }

        private static long Millisecondes(DateTime debut, DateTime fin)
        {
            return (long)Math.Round((fin - debut).TotalMilliseconds);
        }

        private static ChampsDuReleve Champs(ObservationDEcran vu, Etat etat)
        {
            var champs = new ChampsDuReleve();
            champs.Texte("demande", vu.Demande ?? ObservationDEcran.Aucun);
            champs.Texte("affichage", etat.Affichage ?? FormeDesReleves.Inconnu);
            Ajouter(champs, vu, etat);
            return champs;
        }

        private static void Ajouter(ChampsDuReleve champs, ObservationDEcran vu, Etat etat)
        {
            champs.Texte("raison", vu.Raison);
            champs.Nombre("distance", vu.Distance, 1);
            champs.Texte("bloc", string.IsNullOrEmpty(vu.Bloc) ? FormeDesReleves.Inconnu : vu.Bloc);
            champs.Texte("type", vu.TypeDeBloc);
            champs.Texte("ecran", vu.Ecran);
            champs.Texte("construction", string.IsNullOrEmpty(vu.Construction) ? FormeDesReleves.Inconnu : vu.Construction);
            champs.Entier("construction_id", vu.ConstructionId);
            champs.Texte("contenu", vu.SorteDeContenu ?? FormeDesReleves.Inconnu);
            champs.Texte("script", string.IsNullOrEmpty(vu.Script) ? null : vu.Script);
            champs.Nombre("portee", PorteeDesEcrans.PorteeDuBloc(vu.PorteeDuBloc), 0);
            champs.Nombre("portee_effective", vu.PorteeEffective, 0);
            champs.OuiNon("a_portee", vu.Distance <= vu.PorteeEffective);
            champs.Texte("alimentation", vu.Alimentation);
            champs.Heure("dernier_calcul", vu.DernierCalcul);
            if (vu.DernierCalcul.HasValue)
                champs.Nombre("duree_calcul_ms", vu.DureeDuCalculMs, 2);
            champs.Heure("dernier_morceau", vu.DernierMorceau);
            if (vu.DernierMorceau.HasValue || vu.MorceauxDepuis32m > 0)
                champs.Entier("morceaux_32m", vu.MorceauxDepuis32m);
            if (vu.MorceauRecuHorsDes32m)
                champs.OuiNon("image_peut_etre_perimee", true);
        }
    }
}
