#!/usr/bin/env python3
"""Materializza i sorgenti di riferimento del protocollo in refs/ (git-ignorato).

Il pin vive in tools/refs.lock, che e' tracciato: repo, tipo di riferimento
(tag o commit), e sha256 del tarball. refs/ non entra in git.

Uso:
    python tools/fetch-refs.py            # scarica cio' che dice il lock
    python tools/fetch-refs.py --update   # ri-risolve il tag delle voci "tag" (bump del pin)
    python tools/fetch-refs.py --check    # non scarica: verifica che refs/ sia presente e coerente
"""
import hashlib
import io
import json
import os
import sys
import tarfile
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCK = os.path.join(ROOT, "tools", "refs.lock")
REFS = os.path.join(ROOT, "refs")

DEST = {
    "tailscale/tailscale": "tailscale",
    "tailscale/wireguard-go": "wireguard-go",
}


def _api(url):
    req = urllib.request.Request(url, headers={"User-Agent": "tailscale-wp81-fetch"})
    with urllib.request.urlopen(req, timeout=60) as resp:
        return json.loads(resp.read().decode("utf-8"))


def resolve_tag(repo):
    """Ultima release stabile; in mancanza l'ultimo tag per nome/versione.

    Non si usa /releases/latest: Tailscale marca come pre-release le release
    correnti, quindi quell'endpoint risponde 404 e il bump del pin si
    bloccherebbe. Le pre-release non vanno pinnate di default: il protocollo
    puo' cambiare senza garanzie.
    """
    for rel in _api("https://api.github.com/repos/%s/releases?per_page=30" % repo):
        if not rel.get("draft") and not rel.get("prerelease"):
            return rel["tag_name"]
    tags = _api("https://api.github.com/repos/%s/tags?per_page=100" % repo)
    names = [t["name"] for t in tags]
    if not names:
        raise SystemExit("nessun tag utilizzabile per %s" % repo)
    # L'ordine dei tag non e' cronologico: si prende il maggiore per data
    # incorporata nel nome, con fallback all'ordine restituito.
    dated = sorted((n for n in names if _date_key(n)), key=_date_key)
    return dated[-1] if dated else names[0]


def _date_key(tag):
    digits = "".join(c for c in tag if c.isdigit())
    return digits if len(digits) >= 8 else ""


def download_tarball(repo, ref):
    url = "https://codeload.github.com/%s/tar.gz/%s" % (repo, ref)
    req = urllib.request.Request(url, headers={"User-Agent": "tailscale-wp81-fetch"})
    with urllib.request.urlopen(req, timeout=300) as resp:
        return resp.read()


def _rmtree(path):
    for name in os.listdir(path):
        sub = os.path.join(path, name)
        if os.path.isdir(sub):
            _rmtree(sub)
        else:
            os.remove(sub)
    os.rmdir(path)


def _safe_member(name):
    """Vero se il percorso del membro resta dentro la cartella di destinazione.

    Niente percorsi assoluti, niente componenti "..", niente unita' di disco:
    un tarball malevolo non deve scrivere fuori da refs/.
    """
    name = name.replace("\\", "/")
    if name.startswith("/") or os.path.splitdrive(name)[0]:
        return False
    return all(part != ".." for part in name.split("/"))


def extract(blob, dest_dir):
    if os.path.isdir(dest_dir):
        _rmtree(dest_dir)
    os.makedirs(dest_dir)
    with tarfile.open(fileobj=io.BytesIO(blob), mode="r:gz") as tar:
        for member in tar.getmembers():
            parts = member.name.split("/", 1)
            if len(parts) < 2 or not parts[1]:
                continue
            if not (member.isfile() or member.isdir()):
                raise SystemExit("membro non regolare nel tarball: %s" % member.name)
            if not _safe_member(parts[1]):
                raise SystemExit("percorso pericoloso nel tarball: %s" % member.name)
            member.name = parts[1]
            tar.extract(member, dest_dir)


def load_lock():
    if not os.path.exists(LOCK):
        raise SystemExit("manca tools/refs.lock")
    with open(LOCK, "r", encoding="utf-8") as fh:
        return json.load(fh)


def save_lock(lock):
    with open(LOCK, "w", encoding="utf-8") as fh:
        json.dump(lock, fh, indent=2)
        fh.write("\n")


def dest_of(entry):
    return os.path.join(REFS, DEST.get(entry["repo"], entry["repo"].split("/")[-1]))


def write_pin(entry, dest):
    pin = "%s\nref=%s (%s)\nsha256(tarball)=%s\n" % (
        entry["repo"], entry["ref"], entry.get("kind", "tag"), entry.get("sha256") or "(non calcolato)")
    with open(os.path.join(dest, "PINNED_VERSION.txt"), "w", encoding="utf-8") as fh:
        fh.write(pin)
    return pin.replace("\n", " ").strip()


def pin_matches(pin, entry):
    """Il marcatore punta esattamente questo ref? Solo uguaglianza, mai 'in'.

    Un ref che condivide il prefisso con quello pinnato (v1.2 contro v1.20)
    non e' lo stesso ref: il confronto va fatto sul campo, e su repo e tipo
    esattamente come sono scritti nel lock.
    """
    lines = [line.strip() for line in pin.splitlines() if line.strip()]
    if not lines or lines[0] != entry["repo"]:
        return False
    for line in lines:
        if line.startswith("ref="):
            ref, sep, kind = line[len("ref="):].strip().partition(" (")
            return (bool(sep) and kind.endswith(")")
                    and ref == entry["ref"]
                    and kind[:-1] == entry.get("kind", "tag"))
    return False


def main(argv):
    update = "--update" in argv
    check_only = "--check" in argv
    lock = load_lock()
    failed = False

    if update:
        for entry in lock["refs"]:
            if entry.get("kind", "tag") == "tag":
                new = resolve_tag(entry["repo"])
                if new != entry["ref"]:
                    print("[bump] %s: %s -> %s" % (entry["repo"], entry["ref"], new))
                    entry["ref"] = new
                    entry["sha256"] = None
            else:
                print("[keep] %s: pin a commit %s (da go.mod di tailscale)" % (entry["repo"], entry["ref"]))

    for entry in lock["refs"]:
        dest = dest_of(entry)
        marker = os.path.join(dest, "PINNED_VERSION.txt")
        present = os.path.isdir(dest) and os.path.exists(marker)
        if present:
            with open(marker, "r", encoding="utf-8") as fh:
                if pin_matches(fh.read(), entry):
                    print("[ok]   %s@%s" % (entry["repo"], entry["ref"]))
                    continue
        if check_only:
            print("[FAIL] %s@%s non materializzato in %s" % (entry["repo"], entry["ref"], os.path.relpath(dest, ROOT)))
            failed = True
            continue

        print("[get]  %s@%s" % (entry["repo"], entry["ref"]))
        blob = download_tarball(entry["repo"], entry["ref"])
        digest = hashlib.sha256(blob).hexdigest()
        if entry.get("sha256") and digest != entry["sha256"]:
            print("[FAIL] %s: sha256 %s != atteso %s" % (entry["repo"], digest, entry["sha256"]))
            failed = True
            continue
        if not entry.get("sha256"):
            entry["sha256"] = digest
        extract(blob, dest)
        print("       %d byte in %s" % (len(blob), os.path.relpath(dest, ROOT)))

    save_lock(lock)
    if not check_only:
        for entry in lock["refs"]:
            dest = dest_of(entry)
            if os.path.isdir(dest):
                print("       " + write_pin(entry, dest))

    if failed:
        return 1
    print("refs ok")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
