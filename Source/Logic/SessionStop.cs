using System;
using System.Collections.Concurrent;
using System.Threading;

namespace SirCorrectifsPulsar
{
    // The only way the plugin stops: a name missing from the game, a step
    // already used by another plugin, a fault while the game decides a
    // prediction.
    //
    // A stop lasts for the whole game session: the game gets its own behavior
    // back, a line goes to the game log and the player gets a notification as
    // soon as a game is open.
    //
    // Callable from any thread: the game decides predictions on its parallel
    // update threads, the main thread delivers the notifications.
    public sealed class SessionStop
    {
        private readonly Action<string> m_log;
        private readonly Action<string, Exception> m_logException;
        private readonly ConcurrentQueue<string> m_notifications = new ConcurrentQueue<string>();
        private int m_stopped;
        private volatile string m_reason;

        public SessionStop(Action<string> log)
            : this(log, null)
        {
        }

        // log: an error line in the game log (and an error report);
        // logException: the same for a caught exception, with its stack.
        public SessionStop(Action<string> log, Action<string, Exception> logException)
        {
            m_log = log ?? (_ => { });
            m_logException = logException ?? ((detail, e) => m_log(detail + ": " + e));
        }

        public bool IsStopped
        {
            get { return Volatile.Read(ref m_stopped) != 0; }
        }

        // What stopped the plugin, in the words given to the player.
        public string Reason
        {
            get { return m_reason; }
        }

        // True on the first stop only: later ones repeat neither the log line
        // nor the notification.
        public bool Stop(string detailForTheLog, string messageForThePlayer)
        {
            if (Interlocked.Exchange(ref m_stopped, 1) != 0)
                return false;

            m_reason = messageForThePlayer;
            m_log(detailForTheLog);
            m_notifications.Enqueue(messageForThePlayer);
            return true;
        }

        // The same, for a caught exception: the log line carries its stack.
        public bool Stop(string detailForTheLog, Exception exception, string messageForThePlayer)
        {
            if (Interlocked.Exchange(ref m_stopped, 1) != 0)
                return false;

            m_reason = messageForThePlayer;
            m_logException(detailForTheLog, exception);
            m_notifications.Enqueue(messageForThePlayer);
            return true;
        }

        // A name the plugin patches or reads is missing from the game: the
        // game has changed under it.
        public bool MissingGameName(string name)
        {
            return Stop("unexpected game, not found: " + name, Texts.StopUnexpectedGame);
        }

        // Delivers what is waiting, but only while a game is open: in the main
        // menu, messages wait.
        public int Deliver(bool gameOpen, Action<string> show)
        {
            if (!gameOpen || show == null)
                return 0;

            var count = 0;
            string message;
            while (m_notifications.TryDequeue(out message))
            {
                show(message);
                count++;
            }
            return count;
        }
    }
}
