using System;

namespace Tailscale.Core.Wg
{
    /// <summary>
    /// La finestra anti-replay RFC 6479, trascritta da
    /// refs/wireguard-go/replay/replay.go: anello di 128 blocchi da 64 bit,
    /// finestra di 8256 contatori. Un contatore oltre il limite di WireGuard,
    /// gia' visto, o troppo indietro viene rifiutato.
    ///
    /// Non thread-safe di per se': la sessione la usa sotto il suo lock.
    /// </summary>
    public sealed class ReplayWindow
    {
        private const int BlockBitLog = 6;
        private const int BlockBits = 1 << BlockBitLog;
        private const int RingBlocks = 1 << 7;
        private const int WindowSize = (RingBlocks - 1) * BlockBits;
        private const int BlockMask = RingBlocks - 1;
        private const int BitMask = BlockBits - 1;

        private ulong _last;
        private readonly ulong[] _ring = new ulong[RingBlocks];

        /// <summary>
        /// Vero se il contatore e' nuovo e dentro la finestra: il bit viene
        /// segnato. Falso per i replay, i contatori oltre il limite del
        /// protocollo e quelli troppo indietro.
        /// </summary>
        public bool CheckAndUpdate(ulong counter)
        {
            if (counter >= WgConstants.RejectAfterMessages) return false;

            ulong indexBlock = counter >> BlockBitLog;
            if (counter > _last)
            {
                ulong current = _last >> BlockBitLog;
                ulong diff = indexBlock - current;
                if (diff > (ulong)RingBlocks) diff = RingBlocks;
                for (ulong i = current + 1; i <= current + diff; i++)
                    _ring[i & BlockMask] = 0;
                _last = counter;
            }
            else if (_last - counter > (ulong)WindowSize)
            {
                return false;
            }

            int block = (int)(indexBlock & BlockMask);
            int bit = (int)(counter & BitMask);
            ulong old = _ring[block];
            _ring[block] = old | (1UL << bit);
            return old != _ring[block];
        }
    }
}
