using System;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;
using Tailscale.Core.Protocol;

namespace Tailscale.Cli
{
    /// <summary>
    /// Verifica reale dell'handshake: contatta controlplane.tailscale.com, carica
    /// le chiavi del server da /key e apre /ts2021 con l'upgrade.
    ///
    /// Questo e' il controllo che i test in memoria non possono fare: se il server
    /// accetta il nostro primo messaggio e risponde con un tag che verifica contro
    /// la sua chiave pubblica, allora la nostra trascrizione dell'handshake e'
    /// interoperabile con Tailscale, non solo coerente con se stessa.
    /// </summary>
    public static class CommandHandshake
    {
        public static int Run(ILogSink log)
        {
            IRandomSource random = new DesktopRandomSource();
            KeyPair machineKey = KeyPair.Generate(random);

            // Solo la pubblica: la privata non deve comparire nell'output.
            Console.WriteLine("chiave macchina (pubblica): " + MachineKey.FromBytes(machineKey.PublicKey));

            ControlServerKeys keys;
            try
            {
                keys = ControlServerKeyLoader.Load(
                    new DesktopHttpFetcher(), ProtocolConstants.DefaultControlUrl,
                    ProtocolConstants.CapabilityVersion, log);
            }
            catch (ControlServerKeyException ex)
            {
                Console.Error.WriteLine("ERRORE chiavi del server: " + ex.Message);
                return 1;
            }

            Console.WriteLine("server key ts2021=" + keys.NoiseKey
                              + " legacy=" + (keys.LegacyKey == null ? "<assente>" : keys.LegacyKey.ToString()));

            try
            {
                using (NoiseTransport transport = ControlHttpDialer.Dial(
                    ProtocolConstants.ControlServerHost, machineKey, keys,
                    ProtocolConstants.CapabilityVersion, random, new TcpStreamConnector(), log))
                {
                    Console.WriteLine("handshake ok, peer=" + transport.PeerKey);
                    Console.WriteLine("handshake hash=" + Hex.Encode(transport.HandshakeHash));
                    Console.WriteLine("versione del protocollo=" + transport.ProtocolVersion);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERRORE handshake: " + ex.Message);
                return 1;
            }
        }
    }
}
