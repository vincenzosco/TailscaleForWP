using System;
using System.Threading.Tasks;
using Tailscale.Core;
using Windows.Storage;

namespace Tailscale.Services
{
    /// <summary>
    /// Il file del registro, scritto con le API di storage di WinRT.
    ///
    /// Perche' non `System.IO.File`: il profilo WP8.1 non ce l'ha — e' la stessa
    /// ragione per cui il progetto dell'app esclude `Util\FileStateStore.cs`. Il
    /// resto del progetto non deve saperlo: quello che vede e' `IFileText`.
    ///
    /// La politica (quando tagliare, cosa fare se la scrittura fallisce, quanto
    /// segnalare) sta in `src/Tailscale.Core/Util/TextFileLog.cs`, dove si prova
    /// senza telefono. Qui resta la chiamata: aprire il file una volta e appenderci
    /// il testo.
    /// </summary>
    internal sealed class WinRtLogFile : IFileText
    {
        public const string FileName = "registro.log";

        private readonly object _gate = new object();
        private StorageFile _file;

        /// <summary>
        /// Dove sta il file. Se non si riesce nemmeno ad aprirlo si restituisce il
        /// nome: la riga di registro che lo annuncia non deve essere la riga che fa
        /// fallire l'avvio.
        /// </summary>
        public string Location
        {
            get
            {
                try { return Open().Path; }
                catch (Exception) { return FileName; }
            }
        }

        public void Append(string text)
        {
            FileIO.AppendTextAsync(Open(), text).AsTask().Wait();
        }

        public string ReadAll()
        {
            return FileIO.ReadTextAsync(Open()).AsTask().Result;
        }

        public void Write(string text)
        {
            FileIO.WriteTextAsync(Open(), text).AsTask().Wait();
        }

        /// <summary>
        /// Apre il file una volta sola e lo tiene: crearlo a ogni riga costerebbe una
        /// chiamata di storage per ogni riga di registro.
        /// </summary>
        private StorageFile Open()
        {
            lock (_gate)
            {
                if (_file == null)
                {
                    _file = ApplicationData.Current.LocalFolder
                        .CreateFileAsync(FileName, CreationCollisionOption.OpenIfExists)
                        .AsTask().Result;
                }
                return _file;
            }
        }
    }
}
