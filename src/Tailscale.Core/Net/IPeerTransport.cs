using System;

namespace Tailscale.Core.Net
{
    /// <summary>
    /// Il trasporto che un peer conosce: manda e riceve datagrammi WireGuard
    /// (gia' crittati). Per il tunnel e' indifferente che siano frame DERP o
    /// pacchetti UDP: il solo verbo e' "spedisci questo datagramma".
    /// </summary>
    public interface IPeerTransport
    {
        void Send(byte[] datagram);
    }
}
