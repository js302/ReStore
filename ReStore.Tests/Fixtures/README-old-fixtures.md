# Old-version fixtures (v1.6.0, v1.7.0)

`v160/` and `v170/` are data written by the REAL v1.6.0 and v1.7.0 CLIs (built from `git archive <tag>`, never from a checkout),
used by `OldVersionFixtureTests` to prove the current code still reads everything older versions wrote. Together with the
current-code `v2/` fixtures they are a compatibility contract; do not edit them by hand.

Layout of each `vNNN/`:

| Path | Content |
|---|---|
| `config.default.json` | the `config.json` the old version writes on its first run (schema 3 for v1.6.0, 4 for v1.7.0) |
| `plain/`, `encrypted/` | one repository each; the encrypted one uses the password in `encrypted/password.txt` (`fixture-pw-Old#1`, PBKDF2 10 000 iterations) |
| `*/config.json` | config used to create the repository; local storage path replaced by the placeholder `__REPO__` (tests substitute their temp dir) |
| `*/state/system_state.json` | the state file the old CLI saved after the three backups (absolute paths are those of the generator's temp dirs, as in real life after moving a profile) |
| `*/repo/` | the local-storage repository: `snapshots/<group>/{HEAD,snapshot_*.manifest.json}` and `chunks/` (3 snapshots, small chunk profile 2/4/8 KB so files span several chunks) |
| `*/expected/stage{1,2,3}.json` | relative path -> size and SHA-256 of the source tree at each of the three snapshots |
| `*/source-path.txt` | the generator's source folder (recorded in the manifests as the group) |

The source tree changes between snapshots: text, a 40 KB random binary, an empty file, nested directories (stage 1);
`rename-me.txt` renamed to `renamed.txt`, one file edited, one added (stage 2); `delete-me.txt` deleted, the binary patched and
extended, another empty file added (stage 3).

## v1.6.0 specifics

* v1.6.0 predates the chunker change (algorithm 1 -> 2). Current code tells them apart by `CreatedUtc` for manifests that do not record
  the algorithm, so the generator back-dates the v1.6.0 manifests to May 2026 and recomputes `RootHash` and the HEAD hash
  (the Python port is checked against the hash the old CLI itself wrote before anything is changed).
* It targets net9.0; it is built framework-dependent (`-p:SelfContained=false -p:RuntimeIdentifier=`) and run with `DOTNET_ROLL_FORWARD=Major`
  on the net10 runtime.

## Regenerating

```
ReStore.Tests/Fixtures/generate-old-fixtures.sh v1.7.0 <repo-with-tags> ReStore.Tests/Fixtures/v170
ReStore.Tests/Fixtures/generate-old-fixtures.sh v1.6.0 <repo-with-tags> ReStore.Tests/Fixtures/v160
```

Needs `git`, the .NET SDK, `python3` with `cryptography`, `tar`. The old CLIs read `~/ReStore` from `HOME`/`USERPROFILE` (they have no
`RESTORE_HOME`), so the script points both at a temp directory; it never touches your profile. Regenerating gives equivalent but not
byte-identical fixtures (timestamps, snapshot GUIDs and the generator's temp paths differ); after regenerating, delete the stale
`bin/**/Fixtures/vNNN` output folder because MSBuild copies fixtures with PreserveNewest and does not remove old files.

## Downgrade check

`Downgrade_TheV170Cli_...` runs a built v1.7.0 CLI as a child process against a repository the current code wrote into. Set
`RESTORE_COMPAT_OLD_CLI` to the `ReStore.Core.dll` of that build (the `compat-old-cli` CI job does; otherwise the test is skipped):

```
git archive v1.7.0 | tar -x -C /tmp/v170
(cd /tmp/v170 && dotnet build ReStore.Core/ReStore.Core.csproj -c Release -o /tmp/v170-bin -p:SelfContained=false -p:RuntimeIdentifier=)
RESTORE_COMPAT_OLD_CLI=/tmp/v170-bin/ReStore.Core.dll dotnet test ReStore.Tests --filter "FullyQualifiedName~Downgrade"
```
