using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Tailscale.Core;
using Tailscale.Core.Control;
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
            Case("lettura interrotta: il canale si dichiara rotto", AbortedReadMarksTheChannelBroken);
            Case("onJson che fallisce: non e' un guasto di canale", OnJsonFailureIsNotChannelFailure);
        }

        private const string Sentinel = "sentinella";

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

        /// <summary>
        /// Lo stream che il telefono consegna al trasporto e' un adattatore WinRT:
        /// quando il sistema interrompe la lettura l'errore arriva da li'. Qui si
        /// sostituisce con uno stream che si puo' far fallire a comando, per
        /// esercitare la stessa catena (lettura -> Http2Exception ->
        /// ControlStreamException) senza WinRT.
        /// </summary>
        private sealed class SwitchableReadStream : Stream
        {
            private readonly Stream _inner;
            private bool _failing;

            public SwitchableReadStream(Stream inner) { _inner = inner; }

            public void StartFailing() { _failing = true; }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override void Flush() { _inner.Flush(); }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { _inner.Write(buffer, offset, count); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (_failing) throw new IOException(AbortedText);
                return _inner.Read(buffer, offset, count);
            }
        }

        /// <summary>Il testo che il sistema consegna quando interrompe la lettura.</summary>
        private const string AbortedText =
            "The I/O operation has been aborted because of either a thread exit or an application request. "
            + "(Exception from HRESULT: 0x800703E3)";

        /// <summary>
        /// Il caso del telefono: il sistema sospende l'app con una lettura in volo,
        /// il socket muore e la lettura fallisce. Il canale deve dichiararsi rotto,
        /// perche' e' quello che permette al client di rifarlo: senza, il ciclo
        /// della netmap riprova per sempre sullo stesso canale morto e l'app non si
        /// aggiorna piu'.
        /// </summary>
        private static void AbortedReadMarksTheChannelBroken()
        {
            NoiseSession clientSession, serverSession;
            Stream clientStream, serverStream;
            NoiseHandshakeTests.EstablishConnected(
                out clientSession, out serverSession, out clientStream, out serverStream);

            // L'involucro sta sopra lo stream dell'handshake: il trasporto ci scrive
            // la sua prefazione e poi ci legge, esattamente come sul telefono.
            SwitchableReadStream wire = new SwitchableReadStream(clientStream);
            NoiseControlConnection channel = new NoiseControlConnection(
                new NoiseTransport(wire, clientSession), NullLogSink.Instance);

            T.False(channel.IsBroken, "canale: appena fatto non e' rotto");
            wire.StartFailing();

            string failure = null;
            try
            {
                channel.Post("/machine/register", "{}", new List<KeyValuePair<string, string>>());
                T.True(false, "canale: la lettura interrotta deve essere segnalata");
            }
            catch (ControlStreamException ex)
            {
                failure = ex.Message;
            }

            T.True(failure != null, "canale: l'errore arriva come ControlStreamException");
            T.True(channel.IsBroken, "canale: dopo una lettura interrotta il canale e' rotto (va rifatto)");
            T.True(failure != null && failure.IndexOf("canale di controllo", StringComparison.Ordinal) >= 0,
                   "canale: il messaggio e' in italiano e dice che il canale e' caduto");
        }

        /// <summary>
        /// Un corpo della netmap che il chiamante non riesce a interpretare e' un
        /// guasto dei dati, non del canale: se venisse etichettato come guasto di
        /// canale il client rifarebbe l'handshake per un messaggio storto e la
        /// diagnosi mentirebbe.
        /// </summary>
        private static void OnJsonFailureIsNotChannelFailure()
        {
            NoiseSession clientSession, serverSession;
            Stream clientStream, serverStream;
            NoiseHandshakeTests.EstablishConnected(
                out clientSession, out serverSession, out clientStream, out serverStream);

            NoiseControlConnection channel = new NoiseControlConnection(
                new NoiseTransport(clientStream, clientSession), NullLogSink.Instance);

            string serverFailure = null;
            Thread server = new Thread(delegate()
            {
                try
                {
                    NoiseTransport wire = new NoiseTransport(serverStream, serverSession);
                    WriteFrame(wire, new Http2Frame(Http2FrameType.Settings, 0, 0, new byte[0]));

                    List<HpackHeader> headers = new List<HpackHeader>();
                    headers.Add(new HpackHeader(":status", "200"));
                    headers.Add(new HpackHeader("content-type", "application/json"));
                    WriteFrame(wire, new Http2Frame(Http2FrameType.Headers, Http2Frame.FlagEndHeaders, 1,
                        new HpackEncoder(true).Encode(headers)));

                    // Un messaggio della netmap: lunghezza uint32 little-endian + "{}".
                    byte[] message = new byte[] { 0x02, 0x00, 0x00, 0x00, 0x7b, 0x7d };
                    WriteFrame(wire, new Http2Frame(Http2FrameType.Data, Http2Frame.FlagEndStream, 1,
                        message));
                }
                catch (Exception ex)
                {
                    serverFailure = ex.GetType().Name + ": " + ex.Message;
                }
            });
            server.IsBackground = true;
            server.Start();

            object seen = null;
            string clientFailure = null;

            Thread caller = new Thread(delegate()
            {
                try
                {
                    channel.PostStream("/machine/map", "{}",
                        new List<KeyValuePair<string, string>>(),
                        delegate(string json) { throw new InvalidOperationException(Sentinel); },
                        ProtocolConstants.MapStreamWatchdogSeconds);
                }
                catch (InvalidOperationException ex)
                {
                    seen = ex;
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
                clientStream.Close();
                serverStream.Close();
                caller.Join(2000);
                server.Join(2000);
                T.Failures++;
                Console.WriteLine("FAIL canale/onJson che fallisce: il client non ha concluso entro "
                                  + (TimeoutMs / 1000) + "s");
                return;
            }

            server.Join(2000);

            T.True(serverFailure == null, "netmap: il server finto non ha fallito (" + serverFailure + ")");
            T.True(clientFailure == null, "netmap: nessun altro errore sul canale (" + clientFailure + ")");
            T.True(seen != null, "netmap: l'errore di onJson arriva al chiamante tale e quale");
            T.False(channel.IsBroken, "netmap: un messaggio illeggibile non dichiara rotto il canale");
        }

        private static void WriteFrame(NoiseTransport wire, Http2Frame frame)
        {
            byte[] bytes = frame.ToBytes();
            wire.WriteFrame(bytes);
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
