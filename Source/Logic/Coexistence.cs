using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // Two plugins never fight over the same step of the game. Before changing
    // how the game decides the prediction of a grid, we look at who already
    // hooked that step (Harmony.GetPatchInfo): any owner other than us, and
    // we step aside.
    public static class Coexistence
    {
        public static List<string> OtherOwners(IEnumerable<string> owners, string ourId)
        {
            var others = new List<string>();
            if (owners == null)
                return others;

            foreach (var owner in owners)
            {
                if (string.IsNullOrEmpty(owner))
                    continue;
                if (string.Equals(owner, ourId, StringComparison.Ordinal))
                    continue;
                if (!others.Contains(owner))
                    others.Add(owner);
            }
            return others;
        }
    }
}
