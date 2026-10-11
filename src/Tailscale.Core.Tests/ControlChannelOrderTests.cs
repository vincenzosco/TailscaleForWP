using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Tailscale.Core;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il caso che questo test difende: un server HTTP/2 non manda nulla prima di
    /// aver letto la prefazione del client (RFC 9113 §3.4), e l'early payload e'
    /// opzionale. Un client che legge l'early payload *prima* di scrivere la
    /// prefazione blocca entrambe le direzioni: e' il modo in cui il canale di
    /// controllo finiva per non connettersi mai.
    ///
    /// Il server qui e' finto ma la sua attesa e' vera: legge i 24 byte, e solo
    /// dopo risponde. Le letture bloccanti vengono da InMemoryDuplex, lo stesso
    /// doppio stream che esercita l'handshake su due thread.
    ///
    /// Lo stream del client e' avvolto in PushbackStream come nel canale vero
    /// (NoiseControlConnection): senza quell'involucro il controllo dell'early
    /// payload non verrebbe nemmeno tentato e il test non difenderebbe nulla.
    /// </summary>
    public static class ControlChannelOrderTests
    {
        private const string Preface = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n";
        private const int TimeoutMs = 15000;

        public static void Run()
        {
            Case("server che aspetta la prefazione", ServerWaitsForThePreface);
        }

        private static void Case(string name, Action test)
        {
            try
            {
                test();
            }
            catch (Exception ex)
            {
                T.Failures++;
                Console.WriteLine("FAIL canale/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ServerWaitsForThePreface()
        {
            Stream clientStream;
            Stream serverStream;
            InMemoryDuplex.Create(out clientStream, out serverStream);

            string serverFailure = null;

            // Il server finto: prima la prefazione, poi la risposta. Non legge
            // nient'altro della richiesta, che non gli serve per rispondere.
            Thread server = new Thread(delegate()
            {
                try
                {
                    byte[] preface = new byte[Preface.Length];
                    ReadExactly(serverStream, preface, preface.Length);
                    string received = AsciiOf(preface);
                    if (received != Preface)
                        throw new Exception("il client non ha mandato la prefazione HTTP/2, ma \""
                                            + received + "\"");

                    byte[] settings = new Http2Frame(Http2FrameType.Settings, 0, 0, new byte[0]).ToBytes();
                    serverStream.Write(settings, 0, settings.Length);

                    List<HpackHeader> headers = new List<HpackHeader>();
                    headers.Add(new HpackHeader(":status", "200"));
                    headers.Add(new HpackHeader("content-type", "application/json"));
                    byte[] head = new Http2Frame(Http2FrameType.Headers,
                        Http2Frame.FlagEndHeaders, 1, new HpackEncoder(true).Encode(headers)).ToBytes();
                    serverStream.Write(head, 0, head.Length);

                    byte[] body = new Http2Frame(Http2FrameType.Data,
                        Http2Frame.FlagEndStream, 1, new byte[] { 0x7b, 0x7d }).ToBytes();
                    serverStream.Write(body, 0, body.Length);
                }
                catch (Exception ex)
                {
                    serverFailure = ex.GetType().Name + ": " + ex.Message;
                }
            });
            server.IsBackground = true;
            server.Start();

            int status = -1;
            string clientFailure = null;

            Thread caller = new Thread(delegate()
            {
                try
                {
                    // Come il canale di controllo: lo stream nudo non ha un early
                    // payload da guardare, quindi il test passerebbe senza
                    // esercitare il percorso che qui conta.
                    Http2Connection connection = new Http2Connection(
                        new PushbackStream(clientStream), NullLogSink.Instance);
                    Http2Response response = connection.Post("/machine/register",
                        new byte[] { 0x7b, 0x7d }, new List<KeyValuePair<string, string>>());
                    status = response.StatusCode;
                }
                catch (Exception ex)
                {
                    clientFailure = ex.GetType().Name + ": " + ex.Message;
                }
            });
            caller.IsBackground = true;
            caller.Start();

            if (!caller.Join(TimeoutMs))
            {
                // Sblocca i due thread prima di dichiarare il fallimento: un client
                // che aspetta prima di scrivere non si sblocca da solo, e il test
                // non deve restare appeso.
                clientStream.Close();
                serverStream.Close();
                caller.Join(2000);
                server.Join(2000);
                T.Failures++;
                Console.WriteLine("FAIL canale/server che aspetta la prefazione: il client non ha "
                                  + "completato la richiesta entro " + (TimeoutMs / 1000) + "s "
                                  + "(lettura prima della scrittura)");
                return;
            }

            server.Join(2000);

            T.True(clientFailure == null, "canale: il client non ha fallito (" + clientFailure + ")");
            T.True(serverFailure == null, "canale: il server finto non ha fallito (" + serverFailure + ")");
            T.Eq(200, status, "canale: la richiesta si conclude con 200");
        }

        /// <summary>Legge esattamente count byte, o lancia: qui un troncamento e' un guasto.</summary>
        private static void ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0) throw new EndOfStreamException("stream chiuso dopo " + read + " di " + count + " byte");
                read += n;
            }
        }

        private static string AsciiOf(byte[] bytes)
        {
            char[] chars = new char[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) chars[i] = (char)bytes[i];
            return new string(chars);
        }
    }
}
