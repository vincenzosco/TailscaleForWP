using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tailscale.Core.Control;

namespace Tailscale.Core
{
    /// <summary>
    /// Store su file, per la CLI: un oggetto JSON con i valori come stringhe.
    ///
    /// Il file non e' un segreto in se' (le chiavi private sono in chiaro, come nel
    /// client Go quando non c'e' un portachiavi): per questo sta in una cartella
    /// utente e non nel repo, e per questo il telefono usa invece `PasswordVault`.
    ///
    /// Un file mancante o illeggibile non e' un errore fatale: si logga e si riparte
    /// da uno state vuoto, perche' l'alternativa (rifiutarsi di partire) lascerebbe
    /// l'utente senza modo di rimediare.
    /// </summary>
    public sealed class FileStateStore : IStateStore
    {
        private readonly string _path;
        private readonly ILogSink _log;
        private readonly object _gate = new object();
        private Dictionary<string, string> _values;

        public FileStateStore(string path) : this(path, NullLogSink.Instance) { }

        public FileStateStore(string path, ILogSink log)
        {
            if (path == null) throw new ArgumentNullException("path");
            _path = path;
            _log = log ?? NullLogSink.Instance;
            _values = Load();
        }

        public string Path { get { return _path; } }

        public string Read(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            lock (_gate)
            {
                string value;
                return _values.TryGetValue(name, out value) ? value : null;
            }
        }

        public void Write(string name, string value)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (value == null) throw new ArgumentNullException("value");
            lock (_gate)
            {
                _values[name] = value;
                Save();
            }
        }

        public void Delete(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            lock (_gate)
            {
                if (!_values.Remove(name)) return;
                Save();
            }
        }

        private Dictionary<string, string> Load()
        {
            Dictionary<string, string> values = new Dictionary<string, string>();
            if (!File.Exists(_path))
            {
                _log.Log("stato: nessun file in " + _path + ", si parte vuoti");
                return values;
            }

            try
            {
                string text = File.ReadAllText(_path, Encoding.UTF8);
                JsonValue root = JsonBody.Parse(text);
                foreach (KeyValuePair<string, JsonValue> member in root.ObjectMembers)
                    values[member.Key] = member.Value.AsString("");
                _log.Log("stato: letti " + values.Count + " valori da " + _path);
            }
            catch (Exception ex)
            {
                // Non si lancia: uno state corrotto deve costare un login in piu',
                // non un'app che non parte.
                _log.Log("stato: " + _path + " non leggibile (" + ex.GetType().Name + ": "
                         + ex.Message + "), si riparte da vuoto");
                values.Clear();
            }
            return values;
        }

        private void Save()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(_path, ToJson(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                _log.Log("stato: non riesco a scrivere " + _path + " (" + ex.GetType().Name
                         + ": " + ex.Message + "): lo stato non sopravvivera' al riavvio");
            }
        }

        /// <summary>Serializza i valori come oggetto JSON, con l'escaping necessario.</summary>
        public string ToJson()
        {
            lock (_gate)
            {
                StringBuilder text = new StringBuilder();
                text.Append("{");
                bool first = true;
                foreach (KeyValuePair<string, string> pair in _values)
                {
                    if (!first) text.Append(",");
                    first = false;
                    Escape(text, pair.Key);
                    text.Append(":");
                    Escape(text, pair.Value);
                }
                text.Append("}");
                return text.ToString();
            }
        }

        private static void Escape(StringBuilder text, string value)
        {
            text.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"': text.Append("\\\""); break;
                    case '\\': text.Append("\\\\"); break;
                    case '\n': text.Append("\\n"); break;
                    case '\r': text.Append("\\r"); break;
                    case '\t': text.Append("\\t"); break;
                    default:
                        if (c < ' ') text.Append("\\u").Append(((int)c).ToString("x4"));
                        else text.Append(c);
                        break;
                }
            }
            text.Append('"');
        }
    }
}
