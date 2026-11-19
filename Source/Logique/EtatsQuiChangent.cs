using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Retient le dernier état vu de chaque chose (un détecteur, la foreuse,
    // les réglages graphiques...) sous la forme d'un texte, et ne dit vrai que
    // quand il change : on ne note jamais un état qui se répète.
    //
    // Appelable de n'importe quel fil : un verrou protège l'état.
    public sealed class EtatsQuiChangent
    {
        private readonly object m_verrou = new object();
        private readonly Dictionary<long, string> m_etats = new Dictionary<long, string>();

        // Vrai la première fois, puis à chaque changement.
        public bool AChange(long cle, string etat)
        {
            lock (m_verrou)
            {
                string avant;
                if (m_etats.TryGetValue(cle, out avant) && avant == etat)
                    return false;
                m_etats[cle] = etat;
                return true;
            }
        }

        public string Dernier(long cle)
        {
            lock (m_verrou)
            {
                string etat;
                return m_etats.TryGetValue(cle, out etat) ? etat : null;
            }
        }

        public void Oublier(long cle)
        {
            lock (m_verrou)
                m_etats.Remove(cle);
        }

        public void ToutOublier()
        {
            lock (m_verrou)
                m_etats.Clear();
        }
    }
}
