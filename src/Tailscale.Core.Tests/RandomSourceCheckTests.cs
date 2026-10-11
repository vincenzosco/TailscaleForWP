using System;
using System.Collections.Generic;
using Tailscale.Core;
using Tailscale.Core.Keys;

namespace Tailscale.Core.Tests
{
    /// <summary>
    /// Il caso che questi test difendono: una sorgente che restituisce la stessa
    /// sequenza a ogni estrazione non e' degenere (i byte fra loro sono diversi) ma
    /// produce la stessa identita' a ogni avvio. Se il controllo sbaglia, l'app
    /// parte, il server rifiuta la node key, e l'unica cosa che resta e' un errore
    /// che non spiega niente.
    /// </summary>
    public static class RandomSourceCheckTests
    {
        public static void Run()
        {
            Case("sorgente che ripete: inutilizzabile", ARepeatingSourceIsRejected);
            Case("sorgente che varia: utilizzabile", AVaryingSourceIsAccepted);
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
                Console.WriteLine("FAIL casuale/" + name + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ARepeatingSourceIsRejected()
        {
            List<string> lines = new List<string>();
            T.False(RandomSourceCheck.IsUsable(new RepeatingRandomSource(), new DelegateLogSink(lines.Add)),
                    "caso random: una sorgente che ripete la stessa sequenza e' inutilizzabile");
            T.True(HasLine(lines, "restituisce sempre gli stessi byte"),
                   "caso random: il registro dice perche'");
            T.True(HasLine(lines, "identiche a ogni avvio"),
                   "caso random: il registro dice la conseguenza, non solo il guasto");
        }

        private static void AVaryingSourceIsAccepted()
        {
            List<string> lines = new List<string>();
            T.True(RandomSourceCheck.IsUsable(new VaryingRandomSource(), new DelegateLogSink(lines.Add)),
                   "caso random: una sorgente che varia e' utilizzabile");
            T.Eq(0, lines.Count, "caso random: quando va tutto bene il registro resta pulito");
        }

        private static bool HasLine(List<string> lines, string fragment)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i] != null && lines[i].IndexOf(fragment, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        /// <summary>
        /// Byte diversi fra loro — quindi non degenere per il core, che rifiuterebbe
        /// una chiave tutta uguale — ma identici a ogni estrazione: e' esattamente il
        /// caso che il controllo esiste per trovare.
        /// </summary>
        private sealed class RepeatingRandomSource : IRandomSource
        {
            private static readonly byte[] Pattern = Build();

            private static byte[] Build()
            {
                byte[] pattern = new byte[32];
                for (int i = 0; i < pattern.Length; i++) pattern[i] = (byte)(i + 1);
                return pattern;
            }

            public void NextBytes(byte[] buffer)
            {
                Array.Copy(Pattern, buffer, buffer.Length);
            }
        }

        private sealed class VaryingRandomSource : IRandomSource
        {
            private int _calls;

            public void NextBytes(byte[] buffer)
            {
                _calls++;
                for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i + _calls);
            }
        }
    }
}
