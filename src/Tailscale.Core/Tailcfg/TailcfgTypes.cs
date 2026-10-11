using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Tailscale.Core.Control;

namespace Tailscale.Core.Tailcfg
{
    /// <summary>
    /// Descrizione della macchina che il client manda al control server.
    ///
    /// Qui si manda il minimo che il server accetta: i campi che non servono al
    /// protocollo (distro, container, ecc.) vengono omessi dal server, non
    /// richiesti. Il nome del campo hostname viene dal fatto che il server lo
    /// mostra nel pannello di amministrazione.
    /// </summary>
    public sealed class Hostinfo
    {
        public string Hostname = "";
        public string OS = "";
        public string OSVersion = "";
        public string IPNVersion = "0.1.0-wp81";
        public bool? Container;

        public string ToJson()
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.String("IPNVersion", IPNVersion);
            w.String("OS", OS);
            w.String("OSVersion", OSVersion);
            w.String("Hostname", Hostname);
            w.EndObject();
            return w.ToString();
        }
    }

    /// <summary>
    /// Richiesta di registrazione. I nomi dei campi JSON sono quelli Go
    /// (`Version`, `NodeKey`, `OldNodeKey`, ...): key.NodePublic si serializza come
    /// stringa `nodekey:...`.
    /// </summary>
    public sealed class RegisterRequest
    {
        public int Version;
        public string NodeKey = "";
        public string OldNodeKey = "";
        public string Followup = "";
        public bool Ephemeral;
        public Hostinfo Hostinfo;

        /// <summary>
        /// Chiave di autenticazione ("tskey-auth-..."): quando c'e', il server
        /// autorizza il nodo senza che nessuno apra il browser (R6).
        /// </summary>
        public string Auth = "";

        public string ToJson()
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Number("Version", Version);
            w.String("NodeKey", NodeKey);
            if (OldNodeKey != null && OldNodeKey.Length > 0) w.String("OldNodeKey", OldNodeKey);
            if (Followup != null && Followup.Length > 0) w.String("Followup", Followup);
            if (Auth != null && Auth.Length > 0) w.String("Auth", Auth);
            if (Ephemeral) w.Bool("Ephemeral", true);
            if (Hostinfo != null)
            {
                w.Name("Hostinfo");
                w.Raw(Hostinfo.ToJson());
            }
            w.EndObject();
            return w.ToString();
        }
    }

    /// <summary>Risposta della registrazione, dai campi di tailcfg.RegisterResponse.</summary>
    public sealed class RegisterResponse
    {
        public bool NodeKeyExpired;
        public bool MachineAuthorized;
        public string AuthURL = "";
        public string Error = "";
        public string LoginName = "";
        public string DisplayName = "";
        public string ProfilePicURL = "";

        public static RegisterResponse FromJson(string json)
        {
            JsonValue root = JsonBody.Parse(json);
            RegisterResponse response = new RegisterResponse();

            response.NodeKeyExpired = root["NodeKeyExpired"] != null && root["NodeKeyExpired"].AsBool(false);
            response.MachineAuthorized = root["MachineAuthorized"] != null && root["MachineAuthorized"].AsBool(false);
            response.AuthURL = String(root, "AuthURL");
            response.Error = String(root, "Error");

            JsonValue login = root["Login"];
            if (login != null)
            {
                response.LoginName = String(login, "LoginName");
                response.DisplayName = String(login, "DisplayName");
                response.ProfilePicURL = String(login, "ProfilePicURL");
            }
            return response;
        }

        private static string String(JsonValue parent, string name)
        {
            JsonValue value = parent[name];
            return value == null ? "" : value.AsString("");
        }
    }

    /// <summary>Richiesta dello stream della netmap.</summary>
    public sealed class MapRequest
    {
        public int Version;
        public string NodeKey = "";
        public string DiscoKey = "";
        public bool Stream = true;
        public bool KeepAlive = true;
        public bool OmitPeers;
        public Hostinfo Hostinfo;
        public List<string> Endpoints = new List<string>();

        public string ToJson()
        {
            JsonWriter w = new JsonWriter();
            w.BeginObject();
            w.Number("Version", Version);
            w.Bool("Stream", Stream);
            w.Bool("KeepAlive", KeepAlive);
            w.String("NodeKey", NodeKey);
            w.String("DiscoKey", DiscoKey);
            if (OmitPeers) w.Bool("OmitPeers", true);
            if (Hostinfo != null)
            {
                w.Name("Hostinfo");
                w.Raw(Hostinfo.ToJson());
            }
            w.Name("Endpoints");
            w.BeginArray();
            foreach (string endpoint in Endpoints) w.Raw(JsonWriter.Quote(endpoint));
            w.EndArray();
            w.EndObject();
            return w.ToString();
        }
    }

    /// <summary>Un nodo della netmap, con solo i campi che l'app mostra o usa.</summary>
    public sealed class NodeInfo
    {
        public long ID;
        public string StableID = "";
        public string Name = "";
        public string Hostname = "";
        public string OS = "";
        public bool Online;
        public string Key = "";
        public string DiscoKey = "";
        public List<string> Addresses = new List<string>();
        public List<string> Endpoints = new List<string>();
        public bool Expired;

        public static NodeInfo FromJson(JsonValue node)
        {
            if (node == null || !node.IsObject) return null;

            NodeInfo info = new NodeInfo();
            info.ID = node["ID"] == null ? 0 : node["ID"].AsLong(0);
            info.StableID = Text(node, "StableID");
            info.Name = Text(node, "Name");
            info.Key = Text(node, "Key");
            info.DiscoKey = Text(node, "DiscoKey");

            JsonValue online = node["Online"];
            info.Online = online != null && online.AsBool(false);
            info.Expired = node["Expired"] != null && node["Expired"].AsBool(false);

            info.Addresses = StringArray(node["Addresses"]);
            info.Endpoints = StringArray(node["Endpoints"]);

            JsonValue hostinfo = node["Hostinfo"];
            if (hostinfo != null && hostinfo.IsObject)
            {
                info.Hostname = Text(hostinfo, "Hostname");
                info.OS = Text(hostinfo, "OS");
            }
            // Il nome breve e' quello che l'utente riconosce; se manca si ricade sul
            // nome DNS completo.
            if (info.Hostname.Length == 0) info.Hostname = ShortName(info.Name);
            return info;
        }

        /// <summary>"casa.tailnet.ts.net." -> "casa"</summary>
        private static string ShortName(string dnsName)
        {
            if (dnsName == null) return "";
            int dot = dnsName.IndexOf('.');
            return dot <= 0 ? dnsName : dnsName.Substring(0, dot);
        }

        private static string Text(JsonValue parent, string name)
        {
            JsonValue value = parent[name];
            return value == null ? "" : value.AsString("");
        }

        private static List<string> StringArray(JsonValue array)
        {
            List<string> result = new List<string>();
            if (array == null || !array.IsArray) return result;
            foreach (JsonValue item in array.ArrayItems)
                if (item.IsString) result.Add(item.AsString(""));
            return result;
        }

        public override string ToString()
        {
            return (Hostname.Length > 0 ? Hostname : Name)
                 + " (" + (OS.Length > 0 ? OS : "?") + ")"
                 + (Online ? " online" : " offline");
        }
    }

    /// <summary>
    /// Una risposta della netmap. Il server manda aggiornamenti incrementali, quindi
    /// ogni campo e' opzionale; l'accumulo in una NetworkMap e' compito del client.
    /// </summary>
    public sealed class MapResponse
    {
        public int Seq;
        public bool KeepAlive;
        public string Domain = "";
        public NodeInfo Self;
        public List<NodeInfo> Peers = new List<NodeInfo>();
        public List<NodeInfo> PeersChanged = new List<NodeInfo>();
        public List<long> PeersRemoved = new List<long>();
        public string Health = "";

        public static MapResponse FromJson(string json)
        {
            JsonValue root = JsonBody.Parse(json);
            MapResponse response = new MapResponse();

            response.Seq = root["Seq"] == null ? 0 : root["Seq"].AsInt(0);
            response.KeepAlive = root["KeepAlive"] != null && root["KeepAlive"].AsBool(false);
            response.Domain = root["Domain"] == null ? "" : root["Domain"].AsString("");

            JsonValue self = root["Node"];
            if (self != null) response.Self = NodeInfo.FromJson(self);

            JsonValue peers = root["Peers"];
            if (peers != null && peers.IsArray)
            {
                foreach (JsonValue peer in peers.ArrayItems)
                {
                    NodeInfo info = NodeInfo.FromJson(peer);
                    if (info != null) response.Peers.Add(info);
                }
            }

            // Il server manda la lista intera al primo map e poi solo le modifiche:
            // senza PeersChanged una netmap lunga resterebbe congelata.
            JsonValue changed = root["PeersChanged"];
            if (changed != null && changed.IsArray)
            {
                foreach (JsonValue peer in changed.ArrayItems)
                {
                    NodeInfo info = NodeInfo.FromJson(peer);
                    if (info != null) response.PeersChanged.Add(info);
                }
            }

            JsonValue removed = root["PeersRemoved"];
            if (removed != null && removed.IsArray)
                foreach (JsonValue id in removed.ArrayItems)
                    response.PeersRemoved.Add(id.AsLong(0));

            JsonValue health = root["Health"];
            if (health != null && health.IsArray)
            {
                List<string> messages = new List<string>();
                foreach (JsonValue message in health.ArrayItems)
                    if (message.IsString) messages.Add(message.AsString(""));
                response.Health = string.Join("; ", messages.ToArray());
            }

            return response;
        }
    }

    /// <summary>
    /// Scrittore JSON minimale: campi nell'ordine in cui li si scrive.
    ///
    /// Un dizionario generico non serve: le richieste hanno una decina di campi
    /// fissi, e l'ordine esplicito rende il codice leggibile.
    /// </summary>
    public sealed class JsonWriter
    {
        private readonly StringBuilder _buffer = new StringBuilder();
        private bool _firstInScope = true;
        private int _depth;

        public void BeginObject()
        {
            _buffer.Append('{');
            _firstInScope = true;
            _depth++;
        }

        public void EndObject()
        {
            _buffer.Append('}');
            _depth--;
            _firstInScope = false;
        }

        public void BeginArray()
        {
            _buffer.Append('[');
            _firstInScope = true;
            _depth++;
        }

        public void EndArray()
        {
            _buffer.Append(']');
            _depth--;
            _firstInScope = false;
        }

        public void Name(string name)
        {
            Separate();
            _buffer.Append(Quote(name)).Append(':');
            _firstInScope = true;
        }

        public void String(string name, string value)
        {
            Name(name);
            Raw(Quote(value));
        }

        public void Number(string name, int value)
        {
            Name(name);
            _buffer.Append(value.ToString(CultureInfo.InvariantCulture));
            _firstInScope = false;
        }

        public void Bool(string name, bool value)
        {
            Name(name);
            _buffer.Append(value ? "true" : "false");
            _firstInScope = false;
        }

        public void Raw(string json)
        {
            _buffer.Append(json);
            _firstInScope = false;
        }

        private void Separate()
        {
            if (!_firstInScope) _buffer.Append(',');
        }

        public override string ToString()
        {
            return _buffer.ToString();
        }

        /// <summary>Stringa JSON con l'escape dei caratteri che lo richiedono.</summary>
        public static string Quote(string value)
        {
            if (value == null) return "null";

            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // Fuori dall'ASCII si usa la forma \uXXXX: un hostname puo'
                        // contenere caratteri accentati.
                        if (c < 0x20 || c > 0x7E)
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
