using System;

namespace Tailscale.Core.Wg
{
    /// <summary>
    /// I timer di una sessione WireGuard, con le soglie di WgConstants:
    /// scadenza dopo RejectAfterTime, rekey dopo RekeyAfterTime o
    /// RekeyAfterMessages, keepalive dopo KeepaliveTimeout di silenzio in
    /// invio. Il tempo non si legge: arriva dal chiamante, ogni volta.
    /// </summary>
    public sealed class WgTimers
    {
        private readonly DateTime _createdAt;
        private DateTime _lastSentAt;
        private ulong _sentMessages;

        public WgTimers(DateTime createdAt)
        {
            _createdAt = createdAt;
            _lastSentAt = createdAt;
        }

        public void NoteSent(DateTime now)
        {
            _lastSentAt = now;
            _sentMessages++;
        }

        /// <summary>La chiave e' vecchia oltre RejectAfterTime: non si usa piu'.</summary>
        public bool IsExpired(DateTime now)
        {
            return (now - _createdAt).TotalSeconds >= WgConstants.RejectAfterSeconds;
        }

        /// <summary>Silenzio in invio oltre KeepaliveTimeout: serve un keepalive.</summary>
        public bool NeedsKeepalive(DateTime now)
        {
            return (now - _lastSentAt).TotalSeconds >= WgConstants.KeepaliveSeconds;
        }

        /// <summary>Oltre RekeyAfterTime o RekeyAfterMessages: serve una chiave nuova.</summary>
        public bool NeedsRekey(DateTime now)
        {
            return (now - _createdAt).TotalSeconds >= WgConstants.RekeyAfterSeconds
                || _sentMessages >= WgConstants.RekeyAfterMessages;
        }
    }
}
