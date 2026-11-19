using System;

namespace SirCorrectifsPulsar
{
    // Les lignes du greffon dans le journal du jeu, toutes précédées de
    // [sir-correctifs-pulsar]. Une ligne d'erreur, avec ou sans exception,
    // donne aussi un rapport « erreur » ; les lignes simples servent de
    // contexte aux rapports. Appelable de n'importe quel fil, ne lève jamais.
    public sealed class JournalDuGreffon
    {
        public const string MarqueDErreur = "ERREUR : ";

        private readonly string m_prefixe;
        private readonly Action<string> m_ecrire;
        private volatile RapporteurDErreurs m_rapporteur;

        public JournalDuGreffon(string identifiant, Action<string> ecrire, RapporteurDErreurs rapporteur)
        {
            m_prefixe = "[" + identifiant + "] ";
            m_ecrire = ecrire ?? (l => { });
            m_rapporteur = rapporteur;
        }

        public RapporteurDErreurs Rapporteur
        {
            get { return m_rapporteur; }
            set { m_rapporteur = value; }
        }

        public void Info(string texte)
        {
            var ligne = m_prefixe + texte;
            Ecrire(ligne);
            var rapporteur = m_rapporteur;
            if (rapporteur != null)
                rapporteur.Retenir(ligne);
        }

        public void Erreur(string texte, Exception erreur)
        {
            var ligne = m_prefixe + MarqueDErreur + texte + (erreur == null ? "" : " : " + erreur);
            Ecrire(ligne);
            var rapporteur = m_rapporteur;
            if (rapporteur == null)
                return;
            rapporteur.Noter(texte, erreur);
            rapporteur.Retenir(m_prefixe + MarqueDErreur + texte);
        }

        private void Ecrire(string ligne)
        {
            try
            {
                m_ecrire(ligne);
            }
            catch (Exception)
            {
            }
        }
    }
}
