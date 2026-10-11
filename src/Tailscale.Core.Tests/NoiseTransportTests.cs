using System;
using System.IO;
using Tailscale.Core.Protocol;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Framing del trasporto controlbase: tipo di frame, lunghezza, limiti e
    /// autenticazione. I limiti sono quelli di conn.go (4096 byte totali, 4077 di
    /// plaintext per frame).
    /// </summary>
    public static class NoiseTransportTests
    {
        public static void Run()
        {
            RoundTrip();
            FrameBytes();
            ChunkingAtTheLimit();
            RejectsOversizedIncoming();
            RejectsWrongType();
            RejectsTampering();
        }

        private static void RoundTrip()
        {
            NoiseTransport sender, receiver;
            Connect(out sender, out receiver);

            int[] sizes = new int[] { 0, 1, 1000, ProtocolConstants.MaxPlaintextSize };
            foreach (int size in sizes)
            {
                byte[] payload = Pattern(size);
                sender.WriteFrame(payload);
                T.Eq(payload, receiver.ReadFrame(), "trasporto: frame di " + size + " byte riletto identico");
            }

            byte[] backward = Pattern(300);
            receiver.WriteFrame(backward);
            T.Eq(backward, sender.ReadFrame(), "trasporto: frame server->client riletto identico");
        }

        private static void FrameBytes()
        {
            NoiseSession client, server;
            Stream clientStream, serverStream;
            NoiseHandshakeTests.EstablishConnected(out client, out server, out clientStream, out serverStream);

            // Uno stream di cattura al posto della rete: il trasporto ci scrive il
            // frame e noi ne leggiamo i byte grezzi.
            MemoryStream wire = new MemoryStream();
            new NoiseTransport(wire, client).WriteFrame(Pattern(10));

            byte[] raw = wire.ToArray();
            T.Eq(29, raw.Length, "trasporto: 10 byte di plaintext diventano 29 byte sul filo (3 + 10 + 16)");
            T.Eq((int)ProtocolConstants.MsgTypeRecord, (int)raw[0], "trasporto: il frame comincia con 0x04");
            T.Eq(26, (raw[1] << 8) | raw[2], "trasporto: la lunghezza dichiarata e' plaintext + 16");
        }

        private static void ChunkingAtTheLimit()
        {
            NoiseTransport sender, receiver;
            Connect(out sender, out receiver);

            // Un byte in piu' del massimo: due frame.
            byte[] big = Pattern(ProtocolConstants.MaxPlaintextSize + 1);
            sender.Write(big);

            byte[] first = receiver.ReadFrame();
            byte[] second = receiver.ReadFrame();
            T.Eq(ProtocolConstants.MaxPlaintextSize, first.Length, "trasporto: il primo frame e' al massimo");
            T.Eq(1, second.Length, "trasporto: il resto va nel secondo frame");
            T.Eq(big, Concat(first, second), "trasporto: i due frame ricompongono il messaggio");

            // Lo stesso messaggio spezzato su molti frame.
            byte[] veryBig = Pattern(ProtocolConstants.MaxPlaintextSize * 3 + 5);
            sender.Write(veryBig);
            byte[] a = receiver.ReadFrame();
            byte[] b = receiver.ReadFrame();
            byte[] c = receiver.ReadFrame();
            byte[] d = receiver.ReadFrame();
            T.Eq(veryBig, Concat(Concat(a, b), Concat(c, d)), "trasporto: messaggio grande ricomposto da 4 frame");

            // Un solo frame esplicitamente troppo grande viene invece rifiutato.
            T.Throws(delegate { sender.WriteFrame(new byte[ProtocolConstants.MaxPlaintextSize + 1]); },
                     "trasporto: WriteFrame rifiuta un plaintext oltre il massimo");

            byte[] atLimit = Pattern(ProtocolConstants.MaxPlaintextSize);
            sender.WriteFrame(atLimit);
            T.Eq(atLimit, receiver.ReadFrame(), "trasporto: il plaintext massimo esatto passa");

            // Un messaggio vuoto non produce frame (come Conn.Write).
            NoiseSession otherClient, otherServer;
            Stream otherClientStream, otherServerStream;
            NoiseHandshakeTests.EstablishConnected(out otherClient, out otherServer, out otherClientStream, out otherServerStream);
            MemoryStream wire = new MemoryStream();
            new NoiseTransport(wire, otherClient).Write(new byte[0]);
            T.Eq(0, (int)wire.Length, "trasporto: un messaggio vuoto non scrive frame");
        }

        private static void RejectsOversizedIncoming()
        {
            Stream clientStream, serverStream;
            NoiseSession client, server;
            NoiseHandshakeTests.EstablishConnected(out client, out server, out clientStream, out serverStream);
            NoiseTransport receiver = new NoiseTransport(serverStream, server);

            // Header non cifrato che dichiara 5000 byte di ciphertext (0x1388): e'
            // cio' che puo' fare un attaccante, e va rifiutato prima di leggere.
            WriteRaw(clientStream, ProtocolConstants.MsgTypeRecord, 5000);
            T.Throws(delegate { receiver.ReadFrame(); },
                     "trasporto: un frame oltre 4096 byte viene rifiutato senza leggerlo");

            T.Eq(4096, ProtocolConstants.HeaderLen + ProtocolConstants.MaxCiphertextSize,
                 "trasporto: il frame massimo e' header piu' ciphertext massimo");
            T.Eq(ProtocolConstants.MaxCiphertextSize - 16, ProtocolConstants.MaxPlaintextSize,
                 "trasporto: il plaintext massimo e' il ciphertext massimo meno il tag");
        }

        private static void RejectsWrongType()
        {
            Stream clientStream, serverStream;
            NoiseSession client, server;
            NoiseHandshakeTests.EstablishConnected(out client, out server, out clientStream, out serverStream);

            WriteRaw(clientStream, ProtocolConstants.MsgTypeResponse, 16);
            T.Throws(delegate { new NoiseTransport(serverStream, server).ReadFrame(); },
                     "trasporto: un tipo di frame diverso da 0x04 viene rifiutato");
        }

        private static void RejectsTampering()
        {
            // Il byte 101 e' il primo che il server legge dopo l'handshake (header
            // della initiation + payload); il +3+5 punta dentro il ciphertext del
            // primo frame di trasporto.
            Stream clientStream, serverStream;
            InMemoryDuplex.Create(out clientStream, out serverStream);
            Stream tamperedServer = new TamperingStream(serverStream, 101 + 3 + 5);

            NoiseSession client, server;
            NoiseHandshakeTests.EstablishOn(clientStream, tamperedServer, out client, out server);

            byte[] payload = Pattern(32);
            new NoiseTransport(clientStream, client).WriteFrame(payload);

            byte[] received = null;
            Exception failure = null;
            try
            {
                received = new NoiseTransport(tamperedServer, server).ReadFrame();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            T.True(failure != null, "trasporto: un ciphertext manomesso non autentica");
            T.True(received == null, "trasporto: un ciphertext manomesso non produce alcun dato");
        }

        /// <summary>
        /// Scrive un header grezzo sul filo, senza passare dal trasporto: e' quello
        /// che farebbe un attaccante, e serve a verificare i controlli di formato.
        /// </summary>
        private static void WriteRaw(Stream stream, byte type, int declaredLength)
        {
            byte[] header = new byte[] { type, (byte)(declaredLength >> 8), (byte)declaredLength };
            stream.Write(header, 0, header.Length);
        }

        private static void Connect(out NoiseTransport sender, out NoiseTransport receiver)
        {
            NoiseSession client, server;
            Stream clientStream, serverStream;
            NoiseHandshakeTests.EstablishConnected(out client, out server, out clientStream, out serverStream);
            sender = new NoiseTransport(clientStream, client);
            receiver = new NoiseTransport(serverStream, server);
        }

        private static byte[] Pattern(int length)
        {
            byte[] result = new byte[length];
            for (int i = 0; i < length; i++) result[i] = (byte)(i * 31 + 7);
            return result;
        }

        private static byte[] Concat(byte[] first, byte[] second)
        {
            byte[] result = new byte[first.Length + second.Length];
            Array.Copy(first, 0, result, 0, first.Length);
            Array.Copy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}
