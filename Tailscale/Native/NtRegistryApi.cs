using System;
using System.Runtime.InteropServices;
using System.Text;
using Tailscale.Core;

namespace Tailscale.Native
{
    /// <summary>
    /// Il registro di sistema attraverso ntdll: NtCreateKey, NtOpenKey,
    /// NtQueryValueKey, NtSetValueKey, NtDeleteValueKey, NtClose.
    ///
    /// Perche' ntdll e non `Microsoft.Win32.Registry`: WP8.1 non ce l'ha, ed e'
    /// lo stesso motivo per cui il core inietta invece di chiamare. Il probe
    /// distingue l'API nativa assente da un accesso negato (che puo' dipendere
    /// dalla sandbox dell'app); in entrambi i casi il piano non scrive niente.
    ///
    /// I layout di UNICODE_STRING, OBJECT_ATTRIBUTES e
    /// KEY_VALUE_PARTIAL_INFORMATION sono quelli di winternl.h, commentati per
    /// intero perche' qui non c'e' nessun header da leggere. Il telefono e' a 32
    /// bit, ma ogni campo e' IntPtr o intero: lo stesso codice vale per ARM e x86.
    ///
    /// Sul telefono si apre solo HKLM. HKCU e' l'alveare dell'utente, il cui
    /// percorso `\Registry\User\&lt;sid&gt;` non e' noto e non si inventa: il
    /// manifest dell'app parla solo HKLM per questo motivo.
    /// </summary>
    public sealed class NtRegistryApi : IRegistryApi
    {
        private const int KeyQueryValue = 0x0001;
        private const int KeySetValue = 0x0002;
        private const int KeyCreateSubKey = 0x0004;

        private const int ObjCaseInsensitive = 0x00000040;
        private const int RegOptionNonVolatile = 0;

        private const int RegSz = 1;
        private const int RegDword = 4;
        private const int KeyValuePartialInformation = 2;

        private const int StatusSuccess = 0;
        private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
        private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
        private const int StatusBufferOverflow = unchecked((int)0x80000005);
        private const int StatusAccessDenied = unchecked((int)0xC0000022);

        /// <summary>
        /// UNICODE_STRING di winternl.h: lunghezza in byte SENZA il terminatore,
        /// spazio allocato, e il puntatore al testo. Un `Length` che include il
        /// nullo legge un nome con un carattere spazzatura alla fine.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        /// <summary>OBJECT_ATTRIBUTES di winternl.h (la variante senza il campo Reserved).</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public int Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtOpenKey(out IntPtr handle, int access, ref ObjectAttributes attributes);

        [DllImport("ntdll.dll")]
        private static extern int NtCreateKey(out IntPtr handle, int access, ref ObjectAttributes attributes,
                                              int titleIndex, IntPtr keyClass, int options, out int disposition);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryValueKey(IntPtr handle, ref UnicodeString name, int informationClass,
                                                  byte[] information, int length, out int resultLength);

        [DllImport("ntdll.dll")]
        private static extern int NtSetValueKey(IntPtr handle, ref UnicodeString name, int titleIndex,
                                                int type, byte[] data, int dataSize);

        [DllImport("ntdll.dll")]
        private static extern int NtDeleteValueKey(IntPtr handle, ref UnicodeString name);

        [DllImport("ntdll.dll")]
        private static extern int NtClose(IntPtr handle);

        public bool TryRead(string hive, string path, string name, out RegValue value)
        {
            value = null;
            IntPtr handle;
            int status = Open(hive, path, KeyQueryValue, false, out handle);
            if (status == StatusObjectNameNotFound) return false;
            Check(status, "NtOpenKey", hive, path, name);

            try
            {
                UnicodeString valueName = AllocateUnicodeString(name);
                try
                {
                    int needed;
                    status = NtQueryValueKey(handle, ref valueName, KeyValuePartialInformation, null, 0, out needed);
                    if (status == StatusObjectNameNotFound) return false;
                    if (status != StatusBufferTooSmall && status != StatusBufferOverflow)
                        Check(status, "NtQueryValueKey", hive, path, name);

                    byte[] information = new byte[needed];
                    status = NtQueryValueKey(handle, ref valueName, KeyValuePartialInformation,
                                             information, information.Length, out needed);
                    if (status == StatusObjectNameNotFound) return false;
                    Check(status, "NtQueryValueKey", hive, path, name);

                    // KEY_VALUE_PARTIAL_INFORMATION di winternl.h: TitleIndex (4
                    // byte), Type (4), DataLength (4), poi i dati. I dati iniziano a
                    // offset 12 e vanno per DataLength byte: la struttura non ha
                    // nessun campo DataOffset, e leggere a offset 12 come se ci fosse
                    // prenderebbe i primi byte del dato per una posizione.
                    int type = BitConverter.ToInt32(information, 4);
                    int dataLength = BitConverter.ToInt32(information, 8);

                    if (type == RegSz)
                    {
                        // Il testo e' Unicode; il terminatore, quando c'e', non fa
                        // parte del valore.
                        string text = Encoding.Unicode.GetString(information, 12, dataLength);
                        int end = text.Length;
                        while (end > 0 && text[end - 1] == '\0') end--;
                        if (end != text.Length) text = text.Substring(0, end);
                        value = new RegValue(RegValue.Sz, text);
                        return true;
                    }
                    if (type == RegDword)
                    {
                        uint number = BitConverter.ToUInt32(information, 12);
                        value = new RegValue(RegValue.Dword, number.ToString());
                        return true;
                    }

                    // Un tipo che il piano non sa descrivere: non si inventa.
                    return false;
                }
                finally
                {
                    Marshal.FreeHGlobal(valueName.Buffer);
                }
            }
            finally
            {
                NtClose(handle);
            }
        }

        public void Write(string hive, string path, string name, RegValue value)
        {
            if (value == null) throw new ArgumentNullException("value");

            IntPtr handle;
            int status = Open(hive, path, KeyQueryValue | KeySetValue | KeyCreateSubKey, true, out handle);
            Check(status, "NtCreateKey", hive, path, name);

            try
            {
                byte[] data;
                int type;
                if (value.Kind == RegValue.Dword)
                {
                    type = RegDword;
                    data = BitConverter.GetBytes(uint.Parse(value.Text));
                }
                else
                {
                    type = RegSz;
                    // REG_SZ nel registro e' Unicode con terminatore incluso.
                    data = Encoding.Unicode.GetBytes(value.Text + "\0");
                }

                UnicodeString valueName = AllocateUnicodeString(name);
                try
                {
                    status = NtSetValueKey(handle, ref valueName, 0, type, data, data.Length);
                    Check(status, "NtSetValueKey", hive, path, name);
                }
                finally
                {
                    Marshal.FreeHGlobal(valueName.Buffer);
                }
            }
            finally
            {
                NtClose(handle);
            }
        }

        public void Delete(string hive, string path, string name)
        {
            IntPtr handle;
            int status = Open(hive, path, KeySetValue, false, out handle);
            if (status == StatusObjectNameNotFound) return;  // la chiave non c'e': niente da cancellare
            Check(status, "NtOpenKey", hive, path, name);

            try
            {
                UnicodeString valueName = AllocateUnicodeString(name);
                try
                {
                    status = NtDeleteValueKey(handle, ref valueName);
                    if (status == StatusObjectNameNotFound) return;  // gia' assente: e' quello che serve
                    Check(status, "NtDeleteValueKey", hive, path, name);
                }
                finally
                {
                    Marshal.FreeHGlobal(valueName.Buffer);
                }
            }
            finally
            {
                NtClose(handle);
            }
        }

        /// <summary>
        /// Apre (o crea, se `create`) la chiave del percorso, un segmento alla
        /// volta: NtCreateKey crea il livello finale ma non i padri mancanti, e un
        /// manifest con una sottochiave nuova deve funzionare al primo colpo.
        /// </summary>
        private static int Open(string hive, string path, int access, bool create, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            string root;
            if (string.Equals(hive, "HKLM", StringComparison.OrdinalIgnoreCase)) root = "\\Registry\\Machine";
            else
                throw new NotSupportedException("sul telefono si apre solo HKLM, non " + hive);

            string[] segments = path.Replace('/', '\\').Trim('\\').Split('\\');
            string current = root;
            for (int i = 0; i < segments.Length; i++)
            {
                current = current + "\\" + segments[i];
                bool last = i == segments.Length - 1;

                if (handle != IntPtr.Zero) NtClose(handle);
                int status;
                if (create)
                {
                    int disposition;
                    status = Create(current, last ? access : KeyCreateSubKey, out handle, out disposition);
                }
                else
                {
                    status = OpenKey(current, last ? access : KeyQueryValue, out handle);
                }

                if (status != StatusSuccess) return status;
            }
            return StatusSuccess;
        }

        private static int OpenKey(string fullPath, int access, out IntPtr handle)
        {
            UnicodeString name = AllocateUnicodeString(fullPath);
            ObjectAttributes attributes;
            IntPtr namePointer;
            try
            {
                namePointer = NewAttributes(ref name, out attributes);
            }
            catch
            {
                Marshal.FreeHGlobal(name.Buffer);
                throw;
            }
            try
            {
                return NtOpenKey(out handle, access, ref attributes);
            }
            finally
            {
                Marshal.FreeHGlobal(namePointer);
                Marshal.FreeHGlobal(name.Buffer);
            }
        }

        private static int Create(string fullPath, int access, out IntPtr handle, out int disposition)
        {
            UnicodeString name = AllocateUnicodeString(fullPath);
            ObjectAttributes attributes;
            IntPtr namePointer;
            try
            {
                namePointer = NewAttributes(ref name, out attributes);
            }
            catch
            {
                Marshal.FreeHGlobal(name.Buffer);
                throw;
            }
            try
            {
                return NtCreateKey(out handle, access, ref attributes, 0, IntPtr.Zero,
                                   RegOptionNonVolatile, out disposition);
            }
            finally
            {
                Marshal.FreeHGlobal(namePointer);
                Marshal.FreeHGlobal(name.Buffer);
            }
        }

        /// <summary>
        /// Alloca esplicitamente il testo UTF-16 referenziato da UNICODE_STRING.
        /// RtlInitUnicodeString su un parametro string gestito lascerebbe Buffer
        /// puntare a memoria temporanea liberata dal marshaller al ritorno.
        /// </summary>
        private static UnicodeString AllocateUnicodeString(string text)
        {
            int byteLength = Encoding.Unicode.GetByteCount(text);
            if (byteLength > ushort.MaxValue - 2)
                throw new ArgumentException("nome Unicode troppo lungo", "text");

            IntPtr buffer = Marshal.StringToHGlobalUni(text);
            UnicodeString value = new UnicodeString();
            value.Length = (ushort)byteLength;
            value.MaximumLength = (ushort)(byteLength + 2);
            value.Buffer = buffer;
            return value;
        }

        /// <summary>
        /// Costruisce gli OBJECT_ATTRIBUTES. Il campo ObjectName e' un puntatore
        /// alla struttura UNICODE_STRING: la si alloca fuori dallo stack e chi
        /// chiama la libera appena la chiamata al kernel e' tornata.
        /// </summary>
        private static IntPtr NewAttributes(ref UnicodeString name, out ObjectAttributes attributes)
        {
            IntPtr namePointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(UnicodeString)));
            try
            {
                Marshal.StructureToPtr(name, namePointer, false);
                attributes = new ObjectAttributes();
                attributes.Length = Marshal.SizeOf(typeof(ObjectAttributes));
                attributes.RootDirectory = IntPtr.Zero;
                attributes.ObjectName = namePointer;
                attributes.Attributes = ObjCaseInsensitive;
                attributes.SecurityDescriptor = IntPtr.Zero;
                attributes.SecurityQualityOfService = IntPtr.Zero;
                return namePointer;
            }
            catch
            {
                Marshal.FreeHGlobal(namePointer);
                throw;
            }
        }

        /// <summary>
        /// Uno stato che non e' successo diventa un'eccezione con il codice
        /// esadecimale e il percorso: un fallimento deve essere diagnosticabile,
        /// non un "non funziona". ACCESS_DENIED diventa UnauthorizedAccessException,
        /// che il probe puo' distinguere da un errore di comando.
        /// </summary>
        private static void Check(int status, string call, string hive, string path, string name)
        {
            if (status == StatusSuccess) return;
            string where = hive + "\\" + path + "\\" + name;
            string message = call + " " + Hex(status) + " su " + where;
            if (status == StatusAccessDenied)
                throw new UnauthorizedAccessException(message);
            throw new InvalidOperationException(message);
        }

        private static string Hex(int status)
        {
            return "0x" + unchecked((uint)status).ToString("X8");
        }
    }
}