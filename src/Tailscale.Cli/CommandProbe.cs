using System;
using System.Text;
using Tailscale.Core;
using Tailscale.Core.Control;
using Tailscale.Core.Keys;
using Tailscale.Core.Protocol;

namespace Tailscale.Cli
{
    /// <summary>
    /// Diagnostica: dopo l'handshake manda il preambolo HTTP/2 e un frame SETTINGS
    /// vuoto, poi stampa il primo frame che torna.
    ///
    /// Serve a stabilire con un fatto, invece che per ipotesi, se il control server
    /// parla HTTP/2 dentro Noise. Il client Go forza HTTP/2 su questo trasporto
    /// (`tr.Protocols.SetUnencryptedHTTP2(true)` in control/ts2021/client.go), quindi
    /// una richiesta HTTP/1.1 viene chiusa senza risposta.
    /// </summary>
    public static class CommandProbe
    {
        private static readonly byte[] H2Preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        public static int Run(ILogSink log)
        {
            IRandomSource random = new DesktopRandomSource();
            KeyPair machineKey = KeyPair.Generate(random);

            ControlServerKeys keys = ControlServerKeyLoader.Load(
                new DesktopHttpFetcher(), ProtocolConstants.DefaultControlUrl,
                ProtocolConstants.CapabilityVersion, log);

            using (NoiseTransport transport = ControlHttpDialer.Dial(
                ProtocolConstants.ControlServerHost, machineKey, keys,
                ProtocolConstants.CapabilityVersion, random, new TcpStreamConnector(), log))
            {
                Console.WriteLine("handshake ok; invio del preambolo HTTP/2");

                byte[] prefaceAndSettings = new byte[H2Preface.Length + 9];
                Array.Copy(H2Preface, prefaceAndSettings, H2Preface.Length);
                // SETTINGS vuoto: lunghezza 0, tipo 0x04, flags 0, stream 0.
                prefaceAndSettings[H2Preface.Length + 3] = 0x04;
                transport.Write(prefaceAndSettings);

                try
                {
                    byte[] frame = new byte[0];
                    for (int i = 0; i < 4; i++)
                    {
                        byte[] part = transport.ReadFrame();
                        Console.WriteLine("frame Noise #" + (i + 1) + ": " + part.Length
                                          + " byte: " + Hex.Encode(part));

                        byte[] merged = new byte[frame.Length + part.Length];
                        Array.Copy(frame, merged, frame.Length);
                        Array.Copy(part, 0, merged, frame.Length, part.Length);
                        frame = merged;

                        if (frame.Length >= 9) break;
                    }

                    int length = (frame[0] << 16) | (frame[1] << 8) | frame[2];
                    int type = frame[3];
                    int flags = frame[4];
                    long streamId = ((long)(frame[5] & 0x7F) << 24) | ((long)frame[6] << 16)
                                    | ((long)frame[7] << 8) | frame[8];

                    Console.WriteLine("frame HTTP/2: tipo=" + type + " (" + FrameTypeName(type) + ")"
                                      + " flags=0x" + flags.ToString("x2")
                                      + " stream=" + streamId
                                      + " lunghezza=" + length);

                    if (type == 0x04)
                    {
                        Console.WriteLine("CONFERMATO: il control server parla HTTP/2 dentro Noise");
                        return 0;
                    }

                    Console.WriteLine("il server ha risposto con un tipo inatteso");
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("nessuna risposta HTTP/2: " + ex.Message);
                    return 1;
                }
            }
        }

        private static string FrameTypeName(int type)
        {
            switch (type)
            {
                case 0x00: return "DATA";
                case 0x01: return "HEADERS";
                case 0x03: return "RST_STREAM";
                case 0x04: return "SETTINGS";
                case 0x06: return "PING";
                case 0x07: return "GOAWAY";
                case 0x08: return "WINDOW_UPDATE";
                default: return "sconosciuto";
            }
        }
    }
}
