using System;

namespace Tailscale.Core
{
    /// <summary>
    /// Un file di testo, visto da fuori: quello che serve a un registro.
    ///
    /// Esiste perche' il profilo WP8.1 non ha `System.IO.File` (il progetto
    /// dell'app esclude `FileStateStore.cs` proprio per questo): il file si scrive
    /// con `Windows.Storage`, ma la politica - quando scrivere, quando tagliare,
    /// cosa fare se fallisce - non e' una cosa che si prova solo con un telefono.
    /// </summary>
    public interface IFileText
    {
        /// <summary>Dove sta il file: si mostra all'utente, non e' un segreto.</summary>
        string Location { get; }

        void Append(string text);

        string ReadAll();

        void Write(string text);
    }

    /// <summary>
    /// Il registro scritto su file, con una coda limitata.
    ///
    /// Il problema che risolve: sul telefono il registro viveva solo in memoria, e
    /// quando l'app veniva sospesa, chiusa o moriva, la diagnosi moriva con lei —
    /// per questo ogni guasto costava un debugger collegato. Il file la fa
    /// sopravvivere e la rende recuperabile dal PC.
    ///
    /// Il problema che non deve creare: un file che cresce senza limite riempie lo
    /// storage dell'app, e una scrittura che fallisce non deve zittire il registro a
    /// schermo ne' fermare il motore. Per questo il taglio e' a `maxLines` e la
    /// riscrittura usa le ultime righe che il chiamante sa di avere (`recent`), e
    /// per questo qualunque errore viene assorbito e segnalato **una volta sola**.
    /// </summary>
    public sealed class TailLogFile : ILogSink
    {
        private readonly IFileText _file;
        private readonly Func<string[]> _recent;
        private readonly int _maxLines;
        private readonly ILogSink _report;
        private readonly object _gate = new object();

        /// <summary>Righe scritte da quando il file e' stato tagliato l'ultima volta.</summary>
        private int _appended;
        private bool _reported;

        public TailLogFile(IFileText file, Func<string[]> recent, int maxLines, ILogSink report)
        {
            if (file == null) throw new ArgumentNullException("file");
            if (recent == null) throw new ArgumentNullException("recent");
            if (maxLines <= 0) throw new ArgumentOutOfRangeException("maxLines");
            _file = file;
            _recent = recent;
            _maxLines = maxLines;
            _report = report ?? NullLogSink.Instance;
        }

        /// <summary>Dove sta il file: lo dice il registro all'avvio, cosi' chi legge sa dove guardare.</summary>
        public string Location { get { return _file.Location; } }

        public void Log(string line)
        {
            if (line == null) return;

            lock (_gate)
            {
                try
                {
                    _file.Append(line + "\r\n");
                    _appended++;
                    if (_appended >= _maxLines) Trim();
                }
                catch (Exception ex)
                {
                    // Il registro non ferma mai il motore, e non ripete lo stesso
                    // errore a ogni riga: una volta basta, e chi legge lo vede nel
                    // registro a schermo perche' il sink di segnalazione e' quello.
                    if (!_reported)
                    {
                        _reported = true;
                        _report.Log("registro su file non disponibile: " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Riscrive il file con le ultime righe conosciute. Si tiene il conto delle
        /// righe *scritte*, non si rilegge il file a ogni riga: una scrittura in piu'
        /// ogni `maxLines` invece di una lettura in piu' sempre.
        /// </summary>
        private void Trim()
        {
            string[] keep = _recent();
            if (keep == null) keep = new string[0];
            _file.Write(string.Join("\r\n", keep) + "\r\n");
            _appended = 0;
        }
    }
}
