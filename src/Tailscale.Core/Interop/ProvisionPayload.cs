using System;
using System.Collections.Generic;
using System.Text;

namespace Tailscale.Core
{
    /// <summary>
    /// Il payload per CMD Injector: due file batch, `provision.bat` e
    /// `deprovision.bat`, generati dallo stesso manifest che l'app usa sul posto.
    ///
    /// E' il canale "via CMD Injector": il telefono interop-unlocked esegue
    /// comandi come SYSTEM, che e' quello che serve per scrivere chiavi di
    /// sistema. Gli stessi comandi si incollano in una shell via telnet se il
    /// dispositivo ne espone una: il batch non e' nient'altro che `reg query`,
    /// `reg add` e `reg delete` nell'ordine giusto.
    ///
    /// Tre cose che il batch fa per obbligo, tutte problemi veri e non difese
    /// teoriche:
    ///
    /// - **backup prima della scrittura.** Ogni valore gia' presente viene
    ///   salvato in `backup-<data>.txt` **prima** di essere toccato, nella forma
    ///   `hive|path|nome|tipo:valore`, con `assente` per i valori che non c'erano.
    /// - **idempotenza.** `reg add /f` non fallisce se il valore esiste gia':
    ///   rieseguire il batch lascia lo stato identico alla prima esecuzione.
    /// - **verifica finale.** Ogni valore scritto viene riletto con `reg query`:
    ///   il batch non dichiara "fatto" quello che non ha visto.
    ///
    /// E, scritto in testa a entrambi i file: il registro non crea alcun tunnel.
    /// Qui si persiste configurazione, niente di piu'.
    /// </summary>
    public static class ProvisionPayload
    {
        public static string Generate(InteropPlan plan, string stamp)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (stamp == null) throw new ArgumentNullException("stamp");

            StringBuilder text = new StringBuilder();
            List<RegistryEntry> entries = All(plan);

            text.Append("@echo off\r\n");
            text.Append("rem provision.bat - generato, non modificare a mano.\r\n");
            text.Append("rem Il registro NON crea alcun tunnel: qui si scrivono solo i valori\r\n");
            text.Append("rem del manifest. Il proxy di sistema serve soltanto se una porta locale\r\n");
            text.Append("rem e' in ascolto: senza, il telefono perde la rete.\r\n");
            text.Append("rem Ogni valore gia' presente viene salvato in backup-").Append(stamp)
                .Append(".txt PRIMA di essere scritto.\r\n");
            text.Append("\r\n");
            text.Append("setlocal\r\n");
            text.Append("set \"BACKUP=%~dp0backup-").Append(stamp).Append(".txt\"\r\n");
            text.Append("if not exist \"%BACKUP%\" type nul > \"%BACKUP%\"\r\n");
            text.Append("\r\n");
            text.Append("rem --- backup dei valori presenti, prima di ogni scrittura ---\r\n");
            text.Append("rem Ogni voce si salva solo se non e' gia' nel backup: rieseguire questo\r\n");
            text.Append("rem file non deve sostituire i valori originali con i nostri.\r\n");

            for (int i = 0; i < entries.Count; i++)
            {
                RegistryEntry entry = entries[i];

                // Il backup si prende una volta sola: se la riga c'e' gia', il
                // valore originale e' gia' al sicuro e non si riscrive con quello
                // che abbiamo messo noi alla corsa precedente. Senza questa
                // guardia (vista eseguendo il batch due volte) il backup
                // finirebbe per contenere i nostri valori, e deprovision.bat
                // ripristinerebbe quelli invece di quelli dell'utente. Il
                // confronto e' sulla riga con il campo delimitato: un nome che
                // inizia come un altro ("Vpn" e "Vpn Link") non deve far saltare
                // il backup dell'uno o dell'altro.
                text.Append("findstr /l /b /c:\"")
                    .Append(entry.Hive).Append("|").Append(entry.Path).Append("|").Append(entry.Name)
                    .Append("|\" \"%BACKUP%\" >nul 2>&1\r\n");
                text.Append("if not errorlevel 1 goto skip").Append(i + 1).Append("\r\n");

                text.Append("reg query \"").Append(KeyPath(entry)).Append("\" /v \"").Append(entry.Name)
                    .Append("\" >nul 2>&1\r\n");
                text.Append("if errorlevel 1 (\r\n");
                text.Append("  echo ").Append(EchoPart(entry)).Append("^|assente>>\"%BACKUP%\"\r\n");
                text.Append(") else (\r\n");
                // La riga di reg query e' `nome    REG_TIPO    dato`: il nome
                // puo' contenere spazi e il dato anche, con gli spazi interni che
                // contano. Il tipo e' il token subito dopo quelli del nome:
                // `tokens=N,*` prende il tipo in %%A e il resto della riga (il
                // dato, intatto) in %%B, senza spezzare niente sugli spazi. Il
                // filtro tiene la riga del valore, non l'intestazione.
                text.Append("  for /f \"tokens=").Append(TypeColumn(entry)).Append(",*\" %%A in ('reg query \"").Append(KeyPath(entry))
                    .Append("\" /v \"").Append(entry.Name).Append("\" 2^>nul ^| findstr /c:\" REG_\"') do (\r\n");
                text.Append("    if /i \"%%A\"==\"REG_DWORD\" (echo ").Append(EchoPart(entry))
                    .Append("^|dword:%%B>>\"%BACKUP%\") else (echo ").Append(EchoPart(entry))
                    .Append("^|sz:%%B>>\"%BACKUP%\")\r\n");
                text.Append("  )\r\n");
                text.Append(")\r\n");
                text.Append(":skip").Append(i + 1).Append("\r\n");
            }

            text.Append("\r\n");
            text.Append("rem --- scrittura ---\r\n");
            for (int i = 0; i < entries.Count; i++)
            {
                RegistryEntry entry = entries[i];
                text.Append("reg add \"").Append(KeyPath(entry)).Append("\" /v \"").Append(entry.Name)
                    .Append("\" /t ").Append(RegType(entry)).Append(" /d ").Append(RegData(entry))
                    .Append(" /f\r\n");
            }

            text.Append("\r\n");
            text.Append("rem --- verifica finale: non si dichiara fatto cio' che non si e' riletto ---\r\n");
            for (int i = 0; i < entries.Count; i++)
            {
                RegistryEntry entry = entries[i];
                text.Append("reg query \"").Append(KeyPath(entry)).Append("\" /v \"").Append(entry.Name)
                    .Append("\"\r\n");
            }

            text.Append("\r\n");
            text.Append("echo provision completato\r\n");
            text.Append("endlocal\r\n");
            return text.ToString();
        }

        public static string GenerateDeprovision(InteropPlan plan, string stamp)
        {
            if (plan == null) throw new ArgumentNullException("plan");
            if (stamp == null) throw new ArgumentNullException("stamp");

            StringBuilder text = new StringBuilder();
            text.Append("@echo off\r\n");
            text.Append("rem deprovision.bat - generato, non modificare a mano.\r\n");
            text.Append("rem Rimette ogni valore come era PRIMA di provision.bat, usando il suo\r\n");
            text.Append("rem backup. Funziona anche se il provisioning e' stato interrotto a meta':\r\n");
            text.Append("rem ripristina cio' che trova nel backup e non presume nulla del resto.\r\n");
            text.Append("\r\n");
            text.Append("setlocal\r\n");
            text.Append("set \"BACKUP=%~dp0backup-").Append(stamp).Append(".txt\"\r\n");
            text.Append("if not exist \"%BACKUP%\" (\r\n");
            text.Append("  echo nessun backup trovato: %BACKUP%\r\n");
            text.Append("  exit /b 1\r\n");
            text.Append(")\r\n");
            text.Append("\r\n");
            text.Append("for /f \"usebackq tokens=1,2,3* delims=|\" %%A in (\"%BACKUP%\") do call :restore \"%%A\" \"%%B\" \"%%C\" \"%%D\"\r\n");
            text.Append("\r\n");
            text.Append("echo deprovision completato\r\n");
            text.Append("endlocal\r\n");
            text.Append("exit /b 0\r\n");
            text.Append("\r\n");
            text.Append(":restore\r\n");
            text.Append("if /i \"%~4\"==\"assente\" (\r\n");
            text.Append("  reg delete \"%~1\\%~2\" /v \"%~3\" /f >nul 2>&1\r\n");
            text.Append("  exit /b 0\r\n");
            text.Append(")\r\n");
            text.Append("for /f \"tokens=1,* delims=:\" %%K in (\"%~4\") do set \"KIND=%%K\" & set \"VAL=%%L\"\r\n");
            text.Append("if /i \"%KIND%\"==\"dword\" (\r\n");
            text.Append("  reg add \"%~1\\%~2\" /v \"%~3\" /t REG_DWORD /d %VAL% /f\r\n");
            text.Append(") else (\r\n");
            text.Append("  reg add \"%~1\\%~2\" /v \"%~3\" /t REG_SZ /d \"%VAL%\" /f\r\n");
            text.Append(")\r\n");
            text.Append("exit /b 0\r\n");
            return text.ToString();
        }

        /// <summary>
        /// Connection piu' On, senza doppioni: il provision **aggiunge e attiva**.
        /// I valori [off] non entrano qui -- sono la disattivazione, che dall'app
        /// fa il pulsante e da fuori fa il ripristino del backup. Scriverli nel
        /// provision vorrebbe dire sovrascrivere l'attivazione con la
        /// disattivazione (guasto trovato eseguendo il batch: l'ultima scrittura
        /// vinceva e il collegamento restava spento).
        /// </summary>
        private static List<RegistryEntry> All(InteropPlan plan)
        {
            List<RegistryEntry> entries = new List<RegistryEntry>();
            List<string> seen = new List<string>();
            for (int i = 0; i < plan.Connection.Count; i++) AddDistinct(entries, seen, plan.Connection[i]);
            for (int i = 0; i < plan.On.Count; i++) AddDistinct(entries, seen, plan.On[i]);
            return entries;
        }

        private static void AddDistinct(List<RegistryEntry> entries, List<string> seen, RegistryEntry entry)
        {
            if (seen.Contains(entry.FullName)) return;
            seen.Add(entry.FullName);
            entries.Add(entry);
        }

        /// <summary>Il percorso per i comandi reg: HKLM\SOFTWARE\TailscaleWP.</summary>
        private static string KeyPath(RegistryEntry entry)
        {
            return entry.Hive + "\\" + entry.Path;
        }

        /// <summary>La parte prima del valore nel backup: hive|path|nome, con ^| per echo.</summary>
        private static string EchoPart(RegistryEntry entry)
        {
            return entry.Hive + "^|" + entry.Path + "^|" + entry.Name;
        }

        private static string RegType(RegistryEntry entry)
        {
            return entry.Value.Kind == RegValue.Dword ? "REG_DWORD" : "REG_SZ";
        }

        private static string RegData(RegistryEntry entry)
        {
            // Il dword si scrive nudo (e' un numero), il testo tra virgolette:
            // un valore con spazi deve passare intero a reg.exe.
            return entry.Value.Kind == RegValue.Dword ? entry.Value.Text : "\"" + entry.Value.Text + "\"";
        }

        /// <summary>
        /// La colonna del tipo in `reg query`: una per ogni parola del nome, piu'
        /// una. Un nome con spazi ("Vpn Link") occupa piu' token, ma il tipo
        /// resta quello dopo: la riga si legge per posizione e non per spazi.
        /// </summary>
        private static int TypeColumn(RegistryEntry entry)
        {
            string[] words = entry.Name.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length + 1;
        }
    }
}