using System;

namespace SirCorrectifsPulsar
{
    // One of the game's own switches (a static field the game keeps off),
    // turned on while the plugin wants it and given back afterwards.
    //
    // The value the game had is read when the plugin turns the switch on, and
    // put back when it turns it off: off, the game is exactly as it was,
    // whatever its value was.
    public sealed class GameSwitch
    {
        private readonly Func<bool> m_read;
        private readonly Action<bool> m_write;
        private bool m_applied;
        private bool m_before;

        public GameSwitch(Func<bool> read, Action<bool> write)
        {
            if (read == null)
                throw new ArgumentNullException("read");
            if (write == null)
                throw new ArgumentNullException("write");
            m_read = read;
            m_write = write;
        }

        // True while the plugin holds the switch on.
        public bool IsApplied
        {
            get { return m_applied; }
        }

        // Returns true when the game's value has just changed.
        public bool Update(bool wanted)
        {
            if (wanted == m_applied)
                return false;

            m_applied = wanted;
            if (wanted)
            {
                m_before = m_read();
                if (m_before)
                    return false;
                m_write(true);
                return true;
            }

            if (m_read() == m_before)
                return false;
            m_write(m_before);
            return true;
        }

        // When the plugin acts at all: in a multiplayer game joined as a
        // client, never on a server (a hosted game, a single player game),
        // never after a stop.
        public static bool Wanted(bool setting, bool joinedAsClient, bool stopped)
        {
            return setting && joinedAsClient && !stopped;
        }
    }
}
