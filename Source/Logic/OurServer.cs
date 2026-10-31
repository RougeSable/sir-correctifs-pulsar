using System;
using System.Collections.Generic;

namespace SirCorrectifsPulsar
{
    // The channel of the error reports, and whether the player is on one of
    // our servers right now.
    //
    // The studio's rule names the channel 5465479001, but the game's network
    // channels for mods are 16 bits wide (SendMessageToServer takes a
    // ushort). The studio's server plugin (Sir Studio Torch) listens to the
    // player plugins' reports on network channel 46479, with
    // RegisterSecureMessageHandler: that is the channel the reports use.
    //
    // Our servers (PU and PTU) are recognized on the player's side, without
    // asking the server anything: they are dedicated servers joined as a
    // client whose world loads the studio's own mods. The game gives the
    // client every block and item those mods define; one of their names, in
    // the list below, is enough. Until a game is recognized, nothing leaves:
    // on any other server, in single player or in a game the player hosts,
    // reports wait.
    public sealed class OurServer
    {
        public const long RuleChannel = 5465479001L;

        // The network channel the studio's server plugin listens to
        // (Sir Studio Torch, RapportsErreursClient.CanalDesRapports). This is
        // not a channel of this plugin: it never listens on it, it only sends
        // to that one listener, its only counterpart. Kept as an int and cast
        // where it is sent, so it is not declared as a second channel owner.
        public const int Channel = 46479;

        // Names of blocks and items that only the studio's mods define, as
        // the game names their definitions (subtype).
        public static readonly string[] Markers =
        {
            "ContratsSirius",
            "SirContratsCompagnon",
            "SirContratsTransport",
            "MissionsSirius",
            "LargeMissionsSirius",
            "SmallMissionsSirius",
            "SiriusEmetteurRenfortA",
            "SiriusEmetteurRenfortB",
            "SiriusEmetteurRenfortC",
            "SiriusGenerateurVehiculeLeger",
            "SirDetecteurAsteroides",
            "TerrainToolSirius",
            "PhysicalTerrainToolSirius",
            "CanonAbordage",
        };

        private static readonly HashSet<string> MarkerSet = new HashSet<string>(Markers, StringComparer.Ordinal);

        private object m_game;
        private bool m_ours;
        private bool m_looked;
        private bool m_joinedAsClient;

        public bool IsOurs
        {
            get { return m_ours; }
        }

        // True while the current game, joined as a client of a dedicated
        // server, still has to be looked at.
        public bool NeedsALook
        {
            get { return m_joinedAsClient && !m_looked; }
        }

        // Every update, with the current game (any object that changes from
        // one game to the next) and whether it is a game joined as a client
        // of a dedicated server. A new game, or none, forgets the previous
        // server.
        public void Update(object game, bool joinedAsClient)
        {
            if (!ReferenceEquals(game, m_game) || !joinedAsClient || game == null)
            {
                m_ours = false;
                m_looked = false;
            }
            m_game = game;
            m_joinedAsClient = joinedAsClient && game != null;
        }

        // Looks once at the names of the current game's definitions. Returns
        // true when it makes the current server one of ours.
        public bool Recognize(IEnumerable<string> definitionNames)
        {
            if (!m_joinedAsClient || m_looked)
                return false;
            m_looked = true;
            if (!HasMarker(definitionNames))
                return false;
            m_ours = true;
            return true;
        }

        public static bool HasMarker(IEnumerable<string> definitionNames)
        {
            if (definitionNames == null)
                return false;
            foreach (var name in definitionNames)
            {
                if (name != null && MarkerSet.Contains(name))
                    return true;
            }
            return false;
        }
    }
}
