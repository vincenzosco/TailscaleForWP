using System;

namespace Tailscale.Core.Net.Dns
{
    /// <summary>
    /// Dove girano le query DNS: il canale decide se la porta 53 la parla
    /// UDP, TCP o un finto server di test. Il resolver non conosce la rete.
    /// </summary>
    public interface IDnsChannel
    {
        /// <summary>La query su UDP: la risposta completa, o null.</summary>
        byte[] QueryUdp(byte[] request);

        /// <summary>La query su TCP (fallback per le risposte troncate): la risposta, o null.</summary>
        byte[] QueryTcp(byte[] request);
    }
}
