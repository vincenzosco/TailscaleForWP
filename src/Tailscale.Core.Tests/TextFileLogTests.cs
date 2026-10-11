using System;
using System.Collections.Generic;
using Tailscale.Core;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il registro su file esiste perche' sul telefono la diagnosi moriva con l'app:
    /// se non lascia traccia, o se cresce senza limite, o se una scrittura che
    /// fallisce zittisce tutto, il rimedio e' peggio del problema. Questi test
    /// fissano le tre cose.
    /// </summary>
    public static class TextFileLogTests
    {
        public static void Run()
        {
            Case("registro su file: la riga arriva", EveryLineReachesTheFile);
            Case("registro su file: la coda si taglia al limite", TheFileIsTrimmedAtTheLimit);
            Case("registro su file: un errore di scrittura non ferma il motore", AFileErrorNeverPropagates);
            Case("registro: un sink rotto non silenzia gli altri", AFailingSinkDoesNotSilenceTheOthers);
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
                Console.WriteLine("FAIL registro/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void EveryLineReachesTheFile()
        {
            FakeFileText file = new FakeFileText();
            List<string> reported = new List<string>();
            TailLogFile log = new TailLogFile(file, delegate { return new string[0]; }, 100,
                                              new DelegateLogSink(reported.Add));

            log.Log("avvio del motore");
            log.Log("registrazione: tentativo (primo, atteso 0s)");

            T.True(file.Content().IndexOf("avvio del motore", StringComparison.Ordinal) >= 0,
                   "registro: la prima riga e' nel file");
            T.True(file.Content().IndexOf("registrazione: tentativo", StringComparison.Ordinal) >= 0,
                   "registro: anche la seconda");
            T.Eq(0, reported.Count, "registro: quando il file funziona non si segnala niente");
        }

        private static void TheFileIsTrimmedAtTheLimit()
        {
            FakeFileText file = new FakeFileText();
            RingLogSink ring = new RingLogSink(4);
            List<string> reported = new List<string>();
            TailLogFile log = new TailLogFile(file, delegate { return ring.Snapshot(); }, 8,
                                              new DelegateLogSink(reported.Add));
            FanoutLogSink both = new FanoutLogSink(ring, log);

            for (int i = 1; i <= 9; i++) both.Log("riga " + i);

            // Il taglio avviene quando il conto arriva al limite (all'ottava riga),
            // una volta sola, e riscrive il file con le ultime righe conosciute: la
            // nona riga si aggiunge dopo, quindi si scrive una volta e non a ogni riga.
            T.Eq(1, file.Writes, "registro: il file si riscrive una volta per taglio, non a ogni riga");
            T.True(file.Content().IndexOf("riga 8", StringComparison.Ordinal) >= 0,
                   "registro: le righe recenti restano");
            T.False(file.Content().IndexOf("riga 1\r\n", StringComparison.Ordinal) >= 0,
                    "registro: le righe vecchie vengono tagliate");
            T.True(file.Content().IndexOf("riga 9", StringComparison.Ordinal) >= 0,
                   "registro: e si continua a scrivere dopo il taglio");
        }

        private static void AFileErrorNeverPropagates()
        {
            List<string> reported = new List<string>();
            TailLogFile log = new TailLogFile(new BrokenFileText(), delegate { return new string[0]; }, 100,
                                              new DelegateLogSink(reported.Add));

            for (int i = 0; i < 50; i++) log.Log("riga " + i);   // nessuna eccezione: e' questo il punto

            T.Eq(1, reported.Count, "registro: il guasto del file si segnala una volta, non a ogni riga");
            T.True(reported[0].IndexOf("registro su file non disponibile", StringComparison.Ordinal) >= 0,
                   "registro: e la riga dice che il registro su file non c'e'");
        }

        private static void AFailingSinkDoesNotSilenceTheOthers()
        {
            RingLogSink ring = new RingLogSink(4);
            FanoutLogSink both = new FanoutLogSink(new BrokenSink(), ring);

            both.Log("stato: Connecting -> NeedLogin");

            string[] lines = ring.Snapshot();
            T.Eq(1, lines.Length, "registro: il sink che funziona riceve la riga anche se l'altro lancia");
            T.True(lines[0].IndexOf("NeedLogin", StringComparison.Ordinal) >= 0, "registro: ed e' quella giusta");
        }

        /// <summary>Un file in memoria: registra cosa gli viene scritto e conta le riscritture.</summary>
        private sealed class FakeFileText : IFileText
        {
            private readonly List<string> _appended = new List<string>();
            private string _content = "";

            public int Writes;

            public string Location { get { return "<memoria>"; } }

            public void Append(string text) { _appended.Add(text); }

            public string ReadAll()
            {
                string all = _content;
                for (int i = 0; i < _appended.Count; i++) all += _appended[i];
                return all;
            }

            public void Write(string text)
            {
                Writes++;
                _appended.Clear();
                _content = text;
            }

            /// <summary>Cosa conterrebbe il file, letto come lo leggerebbe l'utente.</summary>
            public string Content() { return ReadAll(); }
        }

        private sealed class BrokenFileText : IFileText
        {
            public string Location { get { return "<rotto>"; } }

            public void Append(string text) { throw new InvalidOperationException("storage pieno"); }

            public string ReadAll() { return ""; }

            public void Write(string text) { throw new InvalidOperationException("storage pieno"); }
        }

        private sealed class BrokenSink : ILogSink
        {
            public void Log(string line) { throw new InvalidOperationException("sink rotto"); }
        }
    }
}
