#!/usr/bin/env python3
"""Verifica che le skill in .agents/ descrivano il codice che c'e' davvero.

Una skill che cita un file rinominato, un simbolo spostato o una riga di registro
cambiata e' peggio di nessuna skill: manda chi la legge a cercare qualcosa che non
esiste. Questo controllo chiude quel buco verificando, contro i sorgenti:

  (a) il frontmatter di ogni SKILL.md: name uguale alla cartella, description
      non vuota, e la riga finale "Ultima verifica: YYYY-MM-DD" con una data vera
      e non futura;
  (b) ogni percorso del repository citato nei file di .agents/;
  (c) ogni simbolo citato come percorso/file.cs#Membro;
  (d) ogni riga di registro citata come log:<testo>: deve essere un letterale
      presente nei sorgenti;
  (e) le voci del calendario .agents/MAINTENANCE.md: data valida per ognuna;
  (f) nessun segreto e nessun URL di login completo.

Uscita: 0 se tutto corrisponde, 1 con l'elenco dei problemi (tutti, non solo il
primo). Non stampa mai il contenuto di un segreto trovato.

Percorsi saltati di proposito, perche' una copia pulita del repository non li ha:
  docs/ e .superpowers/ (non tracciati), refs/ (materializzato da
  tools/fetch-refs.py), bin/ e obj/ (output di build: questo controllo gira PRIMA
  delle build), AppPackages/, tools/tessdata/ (dati locali dell'OCR).
"""
import datetime
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AGENTS = os.path.join(ROOT, ".agents")
SKILLS = os.path.join(AGENTS, "skills")
CALENDAR = os.path.join(AGENTS, "MAINTENANCE.md")

SKIP_PREFIXES = ("docs/", "refs/", ".superpowers/", "AppPackages/", "tools/tessdata/",
                 "bin/", "obj/")
# Un output di build si riconosce anche quando bin/ o obj/ stanno in mezzo al
# percorso (src/Tailscale.Cli/bin/Release/...exe).
SKIP_PARTS = ("/bin/", "/obj/")
SKIP_SUBSTRINGS = ("Program Files", "AppDeployCmd.exe")

EXTENSIONS = ("cs", "md", "py", "cmd", "bat", "resw", "resx", "xaml", "lock", "sln",
              "csproj", "vbproj", "exe", "txt", "json")

TOKEN_RE = re.compile(
    r"[A-Za-z][\w./\\-]*\.(?:" + "|".join(EXTENSIONS) + r")\b"
    r"|(?:src|tools|Tailscale|refs|docs|\.agents)[/\\][\w./\\-]+"
)
SYMBOL_RE = re.compile(r"([A-Za-z][\w./\\-]*\.cs)#([A-Za-z_]\w*)")
LOG_RE = re.compile(r"`log:([^`\n]+)`")
DATE_RE = re.compile(r"Ultima (?:verifica|esecuzione):\s*([0-9]{4}-[0-9]{2}-[0-9]{2})")

SECRET_PATTERNS = [
    ("una chiave node", re.compile(r"nodekey:[0-9a-f]{64}")),
    ("una chiave machine", re.compile(r"\bmkey:[0-9a-f]{64}")),
    ("una chiave disco", re.compile(r"discokey:[0-9a-f]{64}")),
    ("una auth key", re.compile(r"tskey-auth-")),
    ("una chiave privata PEM", re.compile(r"BEGIN [A-Z ]*PRIVATE KEY")),
    ("un URL di login completo", re.compile(r"login\.tailscale\.com/a/\w")),
]

problems = []


def fail(where, message):
    problems.append("%s: %s" % (where, message))


def rel(path):
    return os.path.relpath(path, ROOT).replace("\\", "/")


def index_files():
    """Tutti i percorsi relativi del repository, per risolvere una citazione
    scritta rispetto a una sottocartella (per esempio Services/EngineService.cs)."""
    found = set()
    skip_dirs = {".git", ".vs", "bin", "obj", "refs", "AppPackages", ".superpowers", "tessdata"}
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in skip_dirs]
        for name in files:
            found.add(rel(os.path.join(base, name)))
    return found


def exists(token, known):
    normalized = token.replace("\\", "/")
    if os.path.exists(os.path.join(ROOT, normalized)):
        return True
    # Citazione scritta rispetto a una sottocartella: si accetta se un file del
    # repository finisce con quel percorso.
    return any(p == normalized or p.endswith("/" + normalized) for p in known)


def skipped(token, text=None, start=None):
    normalized = token.replace("\\", "/")
    if normalized.startswith(SKIP_PREFIXES) or any(part in normalized for part in SKIP_PARTS):
        return True
    if any(word in normalized for word in SKIP_SUBSTRINGS):
        return True
    # Un token preceduto da '*' e' un modello ("*Tests.cs"), non un percorso.
    if text is not None and start is not None and start > 0 and text[start - 1] == "*":
        return True
    return False


def agents_files():
    found = []
    for base, dirs, files in os.walk(AGENTS):
        for name in sorted(files):
            if name.endswith(".md"):
                found.append(os.path.join(base, name))
    return sorted(found)


def read(path):
    with open(path, "r", encoding="utf-8") as fh:
        return fh.read()


def check_skill_frontmatter(path):
    text = read(path)
    slug = os.path.basename(os.path.dirname(path))
    if not text.startswith("---\n"):
        fail(rel(path), "manca il frontmatter (deve cominciare con ---)")
        return
    end = text.find("\n---", 4)
    if end < 0:
        fail(rel(path), "frontmatter non chiuso")
        return
    block = text[4:end]
    name = None
    description = None
    for line in block.splitlines():
        if line.startswith("name:"):
            name = line[5:].strip()
        elif line.startswith("description:"):
            description = line[12:].strip()
    if name != slug:
        fail(rel(path), "name '%s' diverso dal nome della cartella '%s'" % (name, slug))
    if not description:
        fail(rel(path), "description mancante nel frontmatter")
    elif "quando" not in description.lower():
        fail(rel(path), "la description non dice quando la skill si attiva (manca 'quando')")

    matches = DATE_RE.findall(text)
    if not matches:
        fail(rel(path), "manca la riga finale 'Ultima verifica: YYYY-MM-DD'")
    today = datetime.date.today()
    for raw in matches:
        try:
            when = datetime.date(*[int(part) for part in raw.split("-")])
        except ValueError:
            fail(rel(path), "data non valida: " + raw)
            continue
        if when > today:
            fail(rel(path), "data nel futuro: " + raw)


def check_citations(path, known, source_blob):
    text = read(path)
    for match in TOKEN_RE.finditer(text):
        token = match.group(0).rstrip(".,;:)]}\"'")
        if not token or skipped(token, text, match.start()) or "<" in token or ">" in token:
            continue
        if not exists(token, known):
            fail(rel(path), "percorso inesistente: " + token)
    for symbol in SYMBOL_RE.finditer(text):
        target, member = symbol.group(1), symbol.group(2)
        if skipped(target, text, symbol.start()):
            continue
        if not exists(target, known):
            fail(rel(path), "simbolo su file inesistente: " + target + "#" + member)
            continue
        full = os.path.join(ROOT, target.replace("\\", "/"))
        if not os.path.exists(full):
            for candidate in known:
                if candidate.endswith("/" + target.replace("\\", "/")):
                    full = os.path.join(ROOT, candidate)
                    break
        if member not in read(full):
            fail(rel(path), "simbolo assente: " + target + "#" + member)
    for literal in LOG_RE.findall(text):
        if "<" in literal or ">" in literal:
            continue
        if literal not in source_blob:
            fail(rel(path), "riga di registro non trovata nei sorgenti: log:" + literal)


def check_calendar():
    if not os.path.exists(CALENDAR):
        fail(rel(CALENDAR), "il calendario non c'e'")
        return
    today = datetime.date.today()
    # Le voci si leggono intere: in Markdown una voce va a capo, e la data sta
    # sulla riga di continuazione. Guardare solo le righe che contengono gia' la
    # data lascerebbe passare proprio il caso peggiore, cioe' la voce a cui
    # nessuno ha mai messo una data.
    items = []
    current = None
    for number, line in enumerate(read(CALENDAR).splitlines(), start=1):
        if line.lstrip().startswith("- [ ]"):
            if current is not None:
                items.append(current)
            current = [number, line]
        elif current is not None:
            if line.strip() == "" or line.lstrip().startswith("#"):
                items.append(current)
                current = None
            else:
                current[1] += "\n" + line
    if current is not None:
        items.append(current)

    if not items:
        fail(rel(CALENDAR), "nessuna voce del calendario (righe '- [ ]')")
        return

    for number, body in items:
        where = rel(CALENDAR) + ":" + str(number)
        # Una voce che non si puo' eseguire (serve un telefono, un servizio) lo
        # deve dire con queste parole: una data inventata e' peggio di nessuna.
        if "non ancora eseguita" in body:
            continue
        found = DATE_RE.search(body)
        if not found:
            fail(where, "voce senza 'Ultima esecuzione: YYYY-MM-DD' "
                        "ne' 'non ancora eseguita'")
            continue
        try:
            when = datetime.date(*[int(part) for part in found.group(1).split("-")])
        except ValueError:
            fail(where, "data non valida: " + found.group(1))
            continue
        if when > today:
            fail(where, "data nel futuro: " + found.group(1))


def check_secrets(path):
    text = read(path)
    for what, pattern in SECRET_PATTERNS:
        if pattern.search(text):
            # Il segreto non si stampa: solo il fatto e il file.
            fail(rel(path), "contiene " + what)


def main():
    if not os.path.isdir(SKILLS):
        print("[FAIL] manca la cartella " + rel(SKILLS))
        return 1

    known = index_files()
    sources = []
    for base_name in ("src", "Tailscale"):
        base = os.path.join(ROOT, base_name)
        for walk_base, dirs, files in os.walk(base):
            dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
            for name in files:
                if name.endswith(".cs"):
                    sources.append(read(os.path.join(walk_base, name)))
    source_blob = "\n".join(sources)

    skills = []
    for base, dirs, files in os.walk(SKILLS):
        for name in sorted(files):
            if name == "SKILL.md":
                path = os.path.join(base, name)
                skills.append(path)
                check_skill_frontmatter(path)
    if not skills:
        fail(rel(SKILLS), "nessuna SKILL.md trovata")

    for path in agents_files():
        check_citations(path, known, source_blob)
        check_secrets(path)

    check_calendar()

    log_literals = len(set(LOG_RE.findall("\n".join(read(p) for p in agents_files()))))
    if problems:
        print("agents FALLITO:")
        for line in problems:
            print("  - " + line)
        return 1

    symbols = sum(len(SYMBOL_RE.findall(read(p))) for p in agents_files())
    print("agents ok (%d skill, %d simboli verificati, %d righe di registro)"
          % (len(skills), symbols, log_literals))
    return 0


if __name__ == "__main__":
    sys.exit(main())
