using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;

namespace SirCorrectifsPulsar
{
    // Une méthode du jeu que le greffon ne doit jamais accrocher, et pourquoi.
    internal sealed class MethodeInterdite
    {
        public MethodeInterdite(string type, string methode, string raison)
        {
            Type = type;
            Methode = methode;
            Raison = raison;
        }

        // Le nom complet du type qui déclare la méthode.
        public string Type { get; private set; }

        public string Methode { get; private set; }

        public string Raison { get; private set; }

        public string NomComplet
        {
            get { return Type + "." + Methode; }
        }
    }

    // Le registre des méthodes interdites : aucune accroche (préfixe,
    // suffixe, finaliseur ou réécriture) ne s'y pose, quel que soit le
    // relevé qui la demande. Le point d'entrée des accroches (PointDAccroche)
    // le consulte avant de poser quoi que ce soit.
    //
    // C'est le seul fichier des sources où le nom de ces méthodes peut
    // s'écrire : un test casse la construction s'il apparaît ailleurs.
    internal static class RegistreDesMethodesInterdites
    {
        // Le 20/11, un chronomètre de Sir Studio Torch posé en préfixe et
        // suffixe sur cette méthode a fait monter les vaisseaux dont les
        // amortisseurs étaient allumés.
        public const string RaisonDesVaisseauxQuiMontent = "les vaisseaux qui montent du 20/11 : un chronomètre posé en préfixe et suffixe sur cette méthode a fait monter les vaisseaux aux amortisseurs allumés";

        private static readonly ReadOnlyCollection<MethodeInterdite> s_toutes = new ReadOnlyCollection<MethodeInterdite>(new List<MethodeInterdite>
        {
            new MethodeInterdite("Sandbox.Game.Entities.MyParallelEntityUpdateOrchestrator", "PerformParallelUpdate", RaisonDesVaisseauxQuiMontent),
        });

        public static ReadOnlyCollection<MethodeInterdite> Toutes
        {
            get { return s_toutes; }
        }

        // La méthode interdite de ce nom, ou null.
        public static MethodeInterdite Trouver(string type, string methode)
        {
            foreach (var interdite in s_toutes)
            {
                if (string.Equals(interdite.Type, type, StringComparison.Ordinal)
                    && string.Equals(interdite.Methode, methode, StringComparison.Ordinal))
                    return interdite;
            }
            return null;
        }

        // La méthode interdite que désigne cette méthode, ou null. Une
        // méthode générique ou sa forme fermée, comme une méthode héritée et
        // redéfinie dans un type dérivé du type interdit, est refusée aussi.
        public static MethodeInterdite Trouver(MethodBase methode)
        {
            if (methode == null)
                return null;
            for (var type = methode.DeclaringType; type != null; type = type.BaseType)
            {
                var nomDuType = type.IsGenericType ? type.GetGenericTypeDefinition().FullName : type.FullName;
                var interdite = Trouver(nomDuType, methode.Name);
                if (interdite != null)
                    return interdite;
            }
            return null;
        }
    }
}
