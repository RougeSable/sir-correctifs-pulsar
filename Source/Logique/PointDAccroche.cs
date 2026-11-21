using System;
using System.Reflection;

namespace SirCorrectifsPulsar
{
    // Le point d'entrée unique des accroches du greffon : toute accroche
    // posée sur une méthode du jeu passe par ici (Accroches.Poser,
    // Accroches.Encadrer et Accroches.Reecrire l'appellent, et un test casse
    // la construction si une accroche est posée ailleurs).
    //
    // Une méthode du registre des méthodes interdites est refusée : rien
    // n'est posé, et le refus est écrit au journal du jeu, en ligne d'erreur
    // (qui donne aussi un rapport).
    internal static class PointDAccroche
    {
        public const string MarqueDuRefus = "accroche refusée";

        // Pose l'accroche par « poser », sauf sur une méthode interdite.
        // Rend vrai si elle a été posée ; une faute de « poser » remonte à
        // l'appelant.
        public static bool Accrocher(MethodBase methode, string nom, Action poser, JournalDuGreffon journal)
        {
            if (Refuser(methode, nom, journal))
                return false;
            poser();
            return true;
        }

        // Vrai si la méthode est au registre des méthodes interdites : le
        // refus est alors écrit au journal du jeu. Sert aussi à l'encadrement
        // d'un appel : l'appel d'une méthode interdite ne s'encadre pas non plus.
        public static bool Refuser(MethodBase methode, string nom, JournalDuGreffon journal)
        {
            var interdite = RegistreDesMethodesInterdites.Trouver(methode);
            if (interdite == null)
                return false;
            if (journal != null)
                journal.Erreur(MarqueDuRefus + " « " + nom + " » : " + interdite.NomComplet
                    + " est au registre des méthodes interdites (" + interdite.Raison + "), rien n'y est posé", null);
            return true;
        }
    }
}
