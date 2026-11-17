#!/usr/bin/env bash
# Regenerates ReStore.Tests/Fixtures/v160 and v170 with the REAL v1.6.0 / v1.7.0 CLIs.
#
# Usage: generate-old-fixtures.sh <tag: v1.6.0|v1.7.0> <repo-with-tags> <output-fixture-dir>
#   e.g. generate-old-fixtures.sh v1.7.0 /path/to/ReStore ReStore.Tests/Fixtures/v170
#
# Needs: git, dotnet SDK (10), python3 with the 'cryptography' package, bash, tar.
# The old CLI is built from `git archive <tag>` (read-only; no checkout). HOME is redirected to a
# temp directory so the old CLI (which has no RESTORE_HOME support) never touches ~/ReStore.
# v1.6.0 targets net9.0 and is built framework-dependent; DOTNET_ROLL_FORWARD=Major runs it on the net10 runtime.
set -euo pipefail

TAG="${1:?tag}"; REPO="${2:?repo}"; OUT="$(realpath -m "${3:?output dir}")"
WORK="$(mktemp -d /tmp/restore-fixture-gen.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
export DOTNET_ROLL_FORWARD=Major DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
PASSWORD="fixture-pw-Old#1"

mkdir -p "$WORK/srcdir" "$WORK/bin"
git -C "$REPO" archive "$TAG" | tar -x -C "$WORK/srcdir"
(cd "$WORK/srcdir" && dotnet build ReStore.Core/ReStore.Core.csproj -c Release -o "$WORK/bin" \
    -p:SelfContained=false -p:RuntimeIdentifier= >"$WORK/build.log" 2>&1) || { cat "$WORK/build.log"; exit 1; }

rm -rf "$OUT"; mkdir -p "$OUT"

# 1. config.json exactly as the old version writes it on first run.
FRESH_HOME="$WORK/fresh-home"; mkdir -p "$FRESH_HOME"
HOME="$FRESH_HOME" USERPROFILE="$FRESH_HOME" dotnet "$WORK/bin/ReStore.Core.dll" --validate-config >/dev/null 2>&1 || true
cp "$FRESH_HOME/ReStore/config.json" "$OUT/config.default.json"

make_source_stage() { # $1 = stage number, $2 = src dir
python3 - "$1" "$2" <<'PY'
import os, random, sys, shutil
stage, src = int(sys.argv[1]), sys.argv[2]
def w(rel, data):
    p = os.path.join(src, rel); os.makedirs(os.path.dirname(p), exist_ok=True)
    open(p, 'wb').write(data if isinstance(data, bytes) else data.encode())
rng = random.Random(1600)
big = bytes(rng.getrandbits(8) for _ in range(40 * 1024))
if stage == 1:
    w('docs/readme.txt', 'hello fixture\n')
    w('docs/notes/deep/nested/unicode-é.txt', 'line of text\n' * 400)
    w('big.bin', big)
    w('empty.txt', b'')
    w('rename-me.txt', 'this file will be renamed in stage 2\n' * 20)
    w('delete-me.txt', 'this file will be deleted in stage 3\n' * 20)
elif stage == 2:
    os.replace(os.path.join(src, 'rename-me.txt'), os.path.join(src, 'renamed.txt'))
    w('docs/readme.txt', 'hello fixture, edited in stage 2\n')
    w('added/new.bin', bytes(rng.getrandbits(8) for _ in range(9000)))
else:
    os.remove(os.path.join(src, 'delete-me.txt'))
    w('big.bin', big[:20000] + b'PATCH' + big[20005:] + b'tail-appended')
    w('empty2.txt', b'')
PY
}

dump_expected() { # $1 src dir -> JSON {rel: {size, sha256}}
python3 - "$1" <<'PY'
import hashlib, json, os, sys
src = sys.argv[1]; out = {}
for r, _, fs in os.walk(src):
    for f in fs:
        p = os.path.join(r, f); data = open(p, 'rb').read()
        out[os.path.relpath(p, src).replace(os.sep, '/')] = {"size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
print(json.dumps(dict(sorted(out.items())), indent=2, ensure_ascii=True))
PY
}

generate() { # $1 = plain|encrypted
  local kind="$1" home="$WORK/home-$1" src="$WORK/src-$1" repo="$WORK/repo-$1"
  mkdir -p "$home" "$src" "$repo" "$OUT/$kind/expected"
  HOME="$home" USERPROFILE="$home" dotnet "$WORK/bin/ReStore.Core.dll" --validate-config >/dev/null 2>&1 || true
  python3 - "$home/ReStore/config.json" "$repo" "$kind" "$PASSWORD" "$src" <<'PY'
import base64, hashlib, json, os, sys
cfg_path, repo, kind, pw, src = sys.argv[1:6]
cfg = json.load(open(cfg_path))
cfg["watchDirectories"] = [{"path": src, "storageType": None}]
cfg["excludedPaths"] = []   # default %TEMP% exclusion would exclude our /tmp sources
cfg['storageSources']['local']['path'] = repo
cfg['chunkDiffing'].update(minChunkSizeKB=2, targetChunkSizeKB=4, maxChunkSizeKB=8)
if kind == 'encrypted':
    salt = hashlib.sha256(b'restore-fixture-salt').digest()[:16]
    iters = 10000
    key = hashlib.pbkdf2_hmac('sha256', pw.encode(), salt, iters, 32)
    from cryptography.hazmat.primitives.ciphers.aead import AESGCM
    iv = hashlib.sha256(b'restore-fixture-iv').digest()[:12]
    ct_tag = AESGCM(key).encrypt(iv, b'ReStore_Password_Verification_Token', None)
    ct, tag = ct_tag[:-16], ct_tag[-16:]
    cfg['encryption'].update(enabled=True, salt=base64.b64encode(salt).decode(), keyDerivationIterations=iters,
                             verificationToken=base64.b64encode(iv + tag + ct).decode())
json.dump(cfg, open(cfg_path, 'w'), indent=2)
PY
  for stage in 1 2 3; do
    make_source_stage "$stage" "$src"
    dump_expected "$src" > "$OUT/$kind/expected/stage$stage.json"
    HOME="$home" USERPROFILE="$home" RESTORE_ENCRYPTION_PASSWORD="$PASSWORD" \
      dotnet "$WORK/bin/ReStore.Core.dll" backup "$src" --storage local >"$WORK/$kind-backup$stage.log" 2>&1 \
      || { cat "$WORK/$kind-backup$stage.log"; exit 1; }
    sleep 1.2   # snapshot ids carry a one-second timestamp
  done
  # Config with a placeholder for the repository path (tests substitute their own temp dir).
  sed "s#$repo#__REPO__#g" "$home/ReStore/config.json" > "$OUT/$kind/config.json"
  mkdir -p "$OUT/$kind/state"
  # State file records absolute paths of the generator's temp dirs; keep it verbatim (loader must tolerate foreign paths).
  cp -r "$home/ReStore/state/." "$OUT/$kind/state/"
  cp -r "$repo" "$OUT/$kind/repo"
  echo "$src" > "$OUT/$kind/source-path.txt"
  if [ "$kind" = encrypted ]; then echo "$PASSWORD" > "$OUT/$kind/password.txt"; fi
}

generate plain
generate encrypted
# v1.6.0 predates the chunker change (algorithm 1 -> 2): current code tells the two apart for manifests that do not record
# the algorithm by their CreatedUtc (SnapshotManifest.GetChunkingAlgorithmVersion). Manifests generated today would look
# like algorithm 2, so back-date the v1.6.0 ones to before the v1.7.0 release and recompute the v2 RootHash, which makes the
# fixture what a real v1.6 user has. Only CreatedUtc and RootHash text are replaced; nothing else is re-serialised.
backdate_v160_manifests() {
python3 - "$1" <<'PY'
import glob, hashlib, json, re, sys
from datetime import datetime
def ticks(s):
    m = re.match(r'(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(?:\.(\d+))?(?:Z|\+00:00)?$', s)
    y, mo, d, h, mi, se = (int(x) for x in m.groups()[:6])
    frac = int((m.group(7) or '0').ljust(7, '0')[:7])
    days = (datetime(y, mo, d) - datetime(1, 1, 1)).days
    return ((days * 86400 + h * 3600 + mi * 60 + se) * 10_000_000) + frac
def root_hash(m):
    ns = (m.get('ChunkStorageNamespace') or '').strip().lower()
    b = f"{m['Version']}|{m['SnapshotId']}|{m['Group']}|{ticks(m['CreatedUtc'])}|{m['BackupMode']}|{'True' if m['EncryptionEnabled'] else 'False'}|{m['KeyDerivationIterations']}|"
    p = m['Profile']
    b += f"{p['MinChunkSizeBytes']}|{p['TargetChunkSizeBytes']}|{p['MaxChunkSizeBytes']}|{p['RollingHashWindowSize']}|"
    if ns: b += ns + '|'
    b += f"{len(m['Files'])}\n"
    for f in sorted(m['Files'], key=lambda f: f['RelativePath'].encode('utf-16-be')):
        b += f"{f['RelativePath']}|{f['SizeBytes']}|{ticks(f['LastModifiedUtc'])}|{f['ContentHash']}|{len(f['Chunks'])}\n"
        for c in f['Chunks']:
            b += f"{c['ChunkId']}|{c['ContentHash']}|{c['PlainSizeBytes']}|{c['StoredSizeBytes']}\n"
    return hashlib.sha256(b.encode()).hexdigest()
for kind in ('plain', 'encrypted'):
    files = sorted(glob.glob(f"{sys.argv[1]}/{kind}/repo/snapshots/*/snapshot_*.manifest.json"))
    for i, path in enumerate(files):
        text = open(path, encoding='utf-8').read()
        m = json.loads(text)
        assert root_hash(m) == m['RootHash'], 'python RootHash port disagrees with the old CLI for ' + path
        new_created = f"2026-05-0{i + 1}T10:00:00.1234567Z"
        text = text.replace(f'"CreatedUtc": "{m["CreatedUtc"]}"', f'"CreatedUtc": "{new_created}"', 1)
        m['CreatedUtc'] = new_created
        text = text.replace(f'"RootHash": "{m["RootHash"]}"', f'"RootHash": "{root_hash(m)}"', 1)
        open(path, 'w', encoding='utf-8', newline='').write(text)
        if i == len(files) - 1:   # HEAD = manifest path + the newest manifest's root hash
            head = glob.glob(path.rsplit('/', 1)[0] + '/HEAD')[0]
            lines = open(head, encoding='utf-8', newline='').read().split('\n')
            lines[1] = root_hash(m)
            open(head, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
PY
}
if [ "$TAG" = "v1.6.0" ]; then backdate_v160_manifests "$OUT"; fi

rm -rf "$OUT"/*/state/*.bak 2>/dev/null || true
echo "Generated $TAG fixtures in $OUT ($(du -sk "$OUT" | cut -f1) KB)"
