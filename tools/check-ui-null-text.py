#!/usr/bin/env python
"""Controlla che nessun testo nullo possa arrivare a un TextBlock della pagina.

Il caso che questo controllo esiste per prendere e' quello del 2026-10-11: l'app
si apriva con lo **schermo nero**, perche' il primo disegno assegnava al testo del
dispositivo una proprieta' che il motore non aveva ancora riempito. Un TextBlock
non accetta un testo nullo (`TextBlock.put_Text` solleva ArgumentNullException),
l'eccezione arriva sul thread della UI e l'app termina. Non e' un guasto che un
test del core possa vedere: il TextBlock esiste solo sul telefono.

Tre regole, tutte cose che si rompono in silenzio:

1. le assegnazioni guardate sono quelle ai controlli di testo dichiarati nel XAML
   (`<TextBlock x:Name="...">`): sono i nomi che a runtime hanno un TextBlock
   vero dietro;
2. ogni espressione assegnata deve essere **dimostrabilmente** non nulla per una
   delle forme che il linguaggio garantisce: un letterale, una concatenazione
   (`+` con almeno un testo), un `ToString()`, una lettura di risorse
   (`GetString`), `string.Format`/`Concat`, un ripiego esplicito (`?? ...`), una
   variabile locale costruita da una di queste;
3. una proprieta' del motore si accetta solo se la sua **dichiarazione** garantisce
   il non-nullo — e la dichiarazione viene riletta per davvero. Le forme accettate
   sono `??` e `== null ? ... : ...`. Se qualcuno la riporta a una proprieta'
   automatica (`public string X { get; private set; }`), qui si torna rossi: e'
   esattamente il difetto che ha prodotto la schermata nera.

Il controllo non e' vuoto per costruzione: se il XAML non dichiara nessun
TextBlock, o se la pagina non riempie piu' i testi che deve riempire, si fallisce
invece di non controllare niente.

Quello che **non** vede, e che resta da guardare a mano: i legami del XAML
(`{Binding Title}`), che sono dati e non codice, e le proprieta' che accettano un
nullo senza guasti (un `Button.Content` nullo non e' un errore).

Uso: python tools/check-ui-null-text.py
"""
import os
import re
import sys

APP = "Tailscale"

# I testi che la pagina deve riempire. Se una di queste assegnazioni sparisce, la
# schermata resta muta e nessun altro controllo se ne accorge.
EXPECTED_ASSIGNED = ("DeviceText", "StatusText", "VpnText", "PeersHeader", "LogText")

# Proprieta' accettate come non nulle, con il file dove sono dichiarate. Non e' un
# permesso: la dichiarazione viene riletta e deve garantire il non-nullo da sola.
NEVER_NULL = {
    "DeviceName": (os.path.join(APP, "Services", "EngineService.cs"),
                   "il motore la riempie appena parte, quindi la pagina puo' leggerla vuota ma non nulla"),
}

# Chiamate che restituiscono sempre un testo, o sollevano invece di restituire
# nullo. Il perche' sta accanto, cosi' chi legge sa cosa si sta assumendo.
SAFE_CALLS = (
    (re.compile(r"\.ToString\(\)\s*$"), "ToString() restituisce sempre un testo"),
    (re.compile(r"\bstring\.Format\(|\bString\.Format\(|\bstring\.Concat\(|\bString\.Concat\("),
     "string.Format e string.Concat restituiscono sempre un testo"),
    (re.compile(r"\.GetString\("),
     "ResourceLoader.GetString restituisce il testo della risorsa (o solleva, se la chiave manca)"),
)

# Forme di dichiarazione che garantiscono il non-nullo.
GUARDS = ("??", "== null ?", "!= null ?")

LOCAL_DECL = re.compile(r"\bstring\s+([A-Za-z_]\w*)\s*=\s*([^;]+);")
# Dopo il `;` che chiude puo' restare qualcos'altro (un commento, un'altra
# istruzione): il `\s*$` esigeva la fine riga e rendeva invisibili le
# riassegnazioni seguite da un commento.
LOCAL_ASSIGN = re.compile(r"^\s*([A-Za-z_]\w*)\s*=\s*([^;]+);", re.M)

# L'assegnazione a un testo di controllo: il nome non deve per forza aprire
# l'istruzione, perche' davanti puo' esserci un costrutto (`if (c) X.Text = ...`,
# `lock (g) { X.Text = ... }`). Il confine accettato e' l'inizio dell'istruzione
# o la fine del costrutto che la precede.
TEXT_ASSIGN = re.compile(
    r"(?:^|[;{})])\s*(?:else\s+|do\s+)?([A-Za-z_]\w*)\s*\.\s*Text\s*=\s*(.+)$", re.S)


def read(path):
    with open(path, "r", encoding="utf-8-sig") as handle:
        return handle.read()


def strip_comments(text):
    """Toglie i commenti dal codice, rispettando i letterali: un URL dentro una
    stringa non e' un commento. I caratteri a capo restano al loro posto, cosi'
    i numeri di riga non cambiano. Senza questo un commento dopo un `;` fa
    sparire la riassegnazione, e un commento attaccato all'espressione la fa
    sembrare non dimostrabile."""
    out = []
    index = 0
    quote = ""
    verbatim = False
    while index < len(text):
        char = text[index]
        if quote:
            out.append(char)
            if not verbatim and char == "\\" and index + 1 < len(text):
                out.append(text[index + 1])
                index += 1
            elif char == quote:
                if verbatim and index + 1 < len(text) and text[index + 1] == '"':
                    # Dentro una stringa verbatim le virgolette si raddoppiano.
                    out.append(text[index + 1])
                    index += 1
                else:
                    quote = ""
                    verbatim = False
        elif char in "\"'":
            quote = char
            verbatim = char == '"' and index > 0 and text[index - 1] == "@"
            out.append(char)
        elif text.startswith("//", index):
            while index < len(text) and text[index] != "\n":
                index += 1
            continue
        elif text.startswith("/*", index):
            index += 2
            while index < len(text) and not text.startswith("*/", index):
                if text[index] == "\n":
                    out.append("\n")
                index += 1
            index += 2
            continue
        else:
            out.append(char)
        index += 1
    return "".join(out)


def text_blocks(root):
    """I nomi dei TextBlock dichiarati nel XAML: gli unici nomi dietro cui c'e' un
    TextBlock vero. Nel XAML gli attributi stanno anche su piu' righe, quindi il
    tag si legge intero e non riga per riga."""
    found = {}
    for base, dirs, files in os.walk(os.path.join(root, APP)):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in files:
            if not name.endswith(".xaml"):
                continue
            path = os.path.join(base, name)
            xaml = read(path)
            for match in re.finditer(r"<([A-Za-z_]\w*)\b([^>]*)>", xaml, re.S):
                if match.group(1) != "TextBlock":
                    continue
                named = re.search(r'x:Name="([^"]+)"', match.group(2))
                if named:
                    found[named.group(1)] = path
    return found


def code_files(root):
    found = []
    for base, dirs, files in os.walk(os.path.join(root, APP)):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for name in sorted(files):
            if name.endswith(".xaml.cs"):
                found.append(os.path.join(base, name))
    return found


def statements(text):
    """Le assegnazioni, una alla volta: una puo' stare su piu' righe."""
    lines = text.split("\n")
    index = 0
    while index < len(lines):
        line = lines[index]
        if ".Text" not in line or "=" not in line:
            index += 1
            continue
        statement = line
        start = index
        while ";" not in statement and index + 1 < len(lines):
            index += 1
            statement = statement + "\n" + lines[index]
        yield start + 1, statement.strip()
        index += 1


def expression_of(statement):
    """Il valore assegnato dentro l'istruzione: fino al `;` che la chiude (o
    alla `}` che chiude il blocco). Un `;` o una `}` dentro un letterale non
    chiude niente, e il commento e' gia' stato tolto: senza questo pezzo
    l'espressione si porta dietro il `;`, la `}` del blocco o il commento, e il
    controllo fallisce (o passa) su roba che non e' l'espressione."""
    quote = ""
    verbatim = False
    index = 0
    while index < len(statement):
        char = statement[index]
        if quote:
            if not verbatim and char == "\\" and index + 1 < len(statement):
                index += 1
            elif char == quote:
                if verbatim and index + 1 < len(statement) and statement[index + 1] == '"':
                    index += 1
                else:
                    quote = ""
                    verbatim = False
        elif char in "\"'":
            quote = char
            verbatim = char == '"' and index > 0 and statement[index - 1] == "@"
        elif char in ";}":
            return statement[:index]
        index += 1
    return statement


def top_level_parts(text, operator):
    """Divide sulle occorrenze dell'operatore che stanno fuori da parentesi e
    virgolette: dentro non sono operatori dell'espressione."""
    parts = []
    depth = 0
    quote = False
    current = ""
    index = 0
    while index < len(text):
        char = text[index]
        if quote:
            current += char
            if char == "\\" and index + 1 < len(text):
                current += text[index + 1]
                index += 1
            elif char == '"':
                quote = False
        elif char == '"':
            quote = True
            current += char
        elif char in "([{":
            depth += 1
            current += char
        elif char in ")]}":
            depth -= 1
            current += char
        elif depth == 0 and text.startswith(operator, index):
            parts.append(current)
            current = ""
            index += len(operator) - 1
        else:
            current += char
        index += 1
    parts.append(current)
    return parts


def declaration(path, member):
    """Il blocco della dichiarazione di un membro, o None se non e' una
    dichiarazione con corpo: una proprieta' automatica non ha corpo, ed e' proprio
    il caso che deve tornare rosso."""
    text = read(path)
    for match in re.finditer(r"\b" + re.escape(member) + r"\b", text):
        # Una dichiarazione con corpo: dopo il nome viene il blocco. Un uso
        # (`X = ...`) o una proprieta' automatica senza corpo non lo hanno.
        if text[match.end():].lstrip()[:1] != "{":
            continue
        start = text.index("{", match.end())
        depth = 0
        for offset in range(start, len(text)):
            if text[offset] == "{":
                depth += 1
            elif text[offset] == "}":
                depth -= 1
                if depth == 0:
                    return text[start:offset + 1]
    return None


def ternary_split(text):
    """(condizione, ramo_se_vero, ramo_se_falso) se l'espressione e' un
    condizionale a livello superiore, altrimenti None. Il `:` da abbinare si
    trova contando i `?` annidati: un ramo puo' contenere un altro condizionale
    senza parentesi, e un `??` non apre un ramo."""
    depth = 0
    quote = ""
    verbatim = False
    index = 0
    mark = -1
    nested = 0
    while index < len(text):
        char = text[index]
        if quote:
            if not verbatim and char == "\\" and index + 1 < len(text):
                index += 1
            elif char == quote:
                if verbatim and index + 1 < len(text) and text[index + 1] == '"':
                    index += 1
                else:
                    quote = ""
                    verbatim = False
        elif char in "\"'":
            quote = char
            verbatim = char == '"' and index > 0 and text[index - 1] == "@"
        elif char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
        elif depth == 0 and text.startswith("??", index):
            index += 1
        elif depth == 0 and char == "?":
            if mark < 0:
                mark = index
            nested += 1
        elif depth == 0 and char == ":" and mark >= 0:
            nested -= 1
            if nested == 0:
                return text[:mark], text[mark + 1:index], text[index + 1:]
        index += 1
    return None


def non_null(expression, locals_by_name, seen):
    """Vero se l'espressione non puo' essere nulla, con il perche'."""
    # La coda di commento dopo l'espressione non ne fa parte: un `engine.DeviceName;
    # // aggiornato` deve valere come `engine.DeviceName`. Un `//` dentro un
    # letterale non e' un commento, e strip_comments lo sa.
    expr = strip_comments(expression).strip().rstrip(";").strip()
    if not expr:
        return False, "espressione vuota"

    # Un condizionale e' non nullo solo se lo sono TUTTI i rami: classificarlo
    # dall'ultimo identificatore faceva passare `flag ? Nullo() : motore.Nome`,
    # che FINISCE con un membro mai nullo ma ha un ramo nullo.
    conditional = ternary_split(expr)
    if conditional is not None:
        for branch in (conditional[1], conditional[2]):
            ok, why = non_null(branch, locals_by_name, seen)
            if not ok:
                return False, "un ramo del condizionale puo' essere nullo (%s)" % why
        return True, "tutti i rami del condizionale sono non nulli"

    # Un ripiego dichiarato vale: `A ?? B` e' non nullo se lo e' B.
    coalesce = top_level_parts(expr, "??")
    if len(coalesce) > 1:
        return non_null(coalesce[-1], locals_by_name, seen)

    # In C# un `+` fra testi non da' mai nullo, anche se un pezzo e' nullo.
    somma = top_level_parts(expr, "+")
    if len(somma) > 1:
        for part in somma:
            if ('"' in part or "ToString()" in part or ".GetString(" in part
                    or "string.Format(" in part or "String.Format(" in part):
                return True, "concatenazione di testi"

    for pattern, why in SAFE_CALLS:
        if pattern.search(expr):
            return True, why

    if expr.startswith('"') and expr.endswith('"') and len(expr) > 1:
        return True, "letterale"
    if expr in ("string.Empty", "String.Empty"):
        return True, "ripiego vuoto esplicito"

    if re.match(r"^[A-Za-z_]\w*$", expr):
        assigned = locals_by_name.get(expr)
        if not assigned:
            return False, ("la variabile locale %s non e' assegnata da nessuna parte "
                           "in questo file, quindi non si sa cosa contenga" % expr)
        for value in assigned:
            ok, _ = non_null(value, locals_by_name, seen | set([expr]))
            if not ok:
                return False, ("la variabile %s prende il valore da un'espressione "
                               "che puo' essere nulla" % expr)
        return True, "variabile locale costruita da un testo non nullo"

    # Un membro mai nullo vale solo se l'intera espressione e' la catena di
    # accessi al membro: guardare il solo ultimo pezzo lasciava passare
    # qualunque espressione che ci FINISSE, come `F(motore.Nome)`.
    if re.match(r"^[A-Za-z_]\w*(\s*\.\s*[A-Za-z_]\w*)*$", expr):
        member = expr.split(".")[-1].strip()
        if member in NEVER_NULL and member not in seen:
            path, why = NEVER_NULL[member]
            if not os.path.exists(path):
                return False, "la dichiarazione di %s non e' piu' in %s" % (member, path)
            body = declaration(path, member)
            if body is None:
                return False, ("%s non e' dichiarata con un corpo in %s: una proprieta' "
                               "automatica puo' essere nulla" % (member, path))
            if not any(guard in body for guard in GUARDS):
                return False, ("%s non garantisce piu' il non-nullo (%s): usa %s, oppure "
                               "ripiega qui nell'assegnazione"
                               % (member, path, " o ".join(GUARDS)))
            return True, "%s: %s" % (member, why)

    return False, "il controllo non sa dimostrare che non sia nulla"


def main():
    problems = []
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    blocks = text_blocks(root)
    if not blocks:
        print("FAIL: nessun TextBlock con x:Name nel XAML: il controllo non avrebbe niente da guardare")
        return 1

    checked = 0
    assigned_names = set()
    for path in code_files(root):
        text = strip_comments(read(path))
        relative = os.path.relpath(path, root).replace("\\", "/")

        # Le variabili locali di testo: valgono come l'espressione da cui nascono.
        locals_by_name = {}
        for name, value in LOCAL_DECL.findall(text):
            locals_by_name.setdefault(name, []).append(value)
        for name, value in LOCAL_ASSIGN.findall(text):
            if name in locals_by_name:
                locals_by_name[name].append(value)

        for line, statement in statements(text):
            match = TEXT_ASSIGN.search(statement)
            if not match:
                continue
            control = match.group(1)
            if control not in blocks:
                continue
            checked += 1
            assigned_names.add(control)
            ok, why = non_null(expression_of(match.group(2)), locals_by_name, set())
            if not ok:
                problems.append("%s:%d: %s.Text prende un testo che puo' essere nullo (%s)"
                                % (relative, line, control, why))

    if not checked:
        print("FAIL: nessuna assegnazione a un TextBlock trovata: il controllo non ha verificato niente")
        return 1

    for name in EXPECTED_ASSIGNED:
        if name not in assigned_names:
            problems.append("%s.Text non viene piu' riempito (il testo resta muto)" % name)

    if problems:
        for problem in problems:
            print("FAIL: " + problem)
        return 1

    print("testo della UI non nullo: %d assegnazioni su %d TextBlock, %d proprieta' mai nulle"
          % (checked, len(blocks), len(NEVER_NULL)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
