# Updates

ReStore can tell you when a new version is out and, for the MSIX and portable installs, install it for you. Nothing is ever
installed without you asking: checking is automatic, installing is not.

## How the check works

- The check downloads one small file, `latest.json`, from the newest GitHub release
  (`https://github.com/js302/ReStore/releases/latest/download/latest.json`). It does not use the GitHub API, so shared networks
  do not run into its rate limit. If the file is missing, ReStore reads the version number from the `releases/latest` redirect
  instead; that is enough to tell you a version exists, but not to install it.
- With **Include pre-releases** (`updates.includePrereleases`) the check lists the ten newest releases through the GitHub API and
  uses the highest version that has a `latest.json`. If the API refuses (rate limit), the stable path is used.
- Versions are compared as `MAJOR.MINOR.PATCH` with an optional `-prerelease`; a release is newer than its own pre-releases.
- `latest.json` lists, per install channel and CPU architecture, the file to download with its SHA-256 and size, a link to the
  release notes and a `critical` flag.
- Automatic checks run at most once every 24 hours (a failed check is retried after an hour). The GUI checks about a minute after
  it starts and then keeps checking in the background; the CLI refreshes after `run-due` and `--service`. Ordinary commands never
  touch the network: they only read the cached answer in `state/update-check.json` and print a one-line hint on stderr (at most
  once a day, never with `--json` or `--quiet`).
- `restore update check` and the GUI's **Check now** always ask GitHub (the CLI reuses an answer younger than an hour unless you pass
  `--force`).

## Privacy

Only GitHub is contacted (`github.com`, its download CDN, and `api.github.com` for pre-releases). The only thing sent is the
`User-Agent: ReStore/<version>` header that every HTTP client sends. No identifier, no machine or user name, no paths, no
telemetry. (Anonymous usage telemetry is a separate, independently switchable feature.)

## Turning it off

Any of these disables the automatic checks (an explicit `update check` still works):

- Settings > Application Behavior > **Check for updates automatically**, or `"updates": { "checkAutomatically": false }` in `config.json`
- the environment variable `RESTORE_NO_UPDATE_CHECK=1`
- a set `CI` environment variable (pipelines never phone home)

`RESTORE_UPDATE_MANIFEST_URL` points the check at another `latest.json` (a mirror or a test server); GitHub is then not contacted at all. The address must be `https://` (plain `http://` is accepted only for `127.0.0.1` or `localhost`, which the tests use): the manifest carries the download address and hash, so an unencrypted mirror would let anyone on the network replace both.

## Installing

`restore update install` (or **Update now** in the Dashboard banner) works per install channel:

| Channel | What happens |
|---------|--------------|
| MSIX | Downloads `ReStore_<version>_<arch>.msix` to `<data folder>\updates\<version>\`, verifies size and SHA-256, opens the package and checks that its identity is `ReStore` and its publisher equals the running package's. Then a hidden PowerShell waits two seconds and runs `Add-AppxPackage -ForceApplicationShutdown`; its outcome is appended to `logs\update-install.log`. From the GUI the app is started again afterwards. The running process exits. |
| Portable | Downloads `ReStore-cli_<version>_win-<arch>.zip`, verifies it, extracts it to a staging folder, checks that `restore.exe` is inside, renames the running `restore.exe` to `restore.exe.old`, copies the new files over the install folder (`config\config.example.json`, `ReStore.channel`, `LICENSE.txt`; your `config.json` is never touched) and rolls everything back if any step fails. `restore.exe.old` is deleted the next time the CLI starts. |
| Built from source | Not installable: pull the repository and rebuild. |

Safety rules:

- A file whose SHA-256 is missing from `latest.json`, or does not match, or whose size differs, is deleted and never installed.
- Only `https` download addresses are accepted.
- The installer takes the repository lease first: while a backup or another ReStore process is using the repository the update is
  refused (CLI exit code 5; use `--wait <seconds>` to wait). In the GUI, protection is stopped before the update starts.
- **Portable zip trust model.** The SHA-256 of the zip comes from the release's `latest.json`, fetched over https from github.com
  (the same release that hosts the zip). That protects against a corrupted or truncated download and against a tampered transport,
  but not against someone who can publish to the release itself: the portable `restore.exe` is not code-signed yet, so there is
  no second, independent check. Code signing (SignPath) is planned and will add one; until then install the portable CLI from
  the official release page only.
- **Same certificate required.** Windows only accepts an MSIX update that is signed with a certificate it already trusts for this
  package, and whose publisher matches the installed one. Releases are signed with the certificate published as `ReStore.cer`; if
  you installed ReStore with it, updates keep working. A package signed by anyone else is refused by ReStore before Windows sees it.

`restore update install --dry-run` shows what would be downloaded and installed and changes nothing. Interactive runs ask `y/N`;
non-interactive runs (scripts, agents) need `--yes`, otherwise they exit with code 4.

## Skipping a version

`restore update skip <version>` (or **Skip this version** in the banner) stops that version from being offered or announced;
a newer one is offered again, and releases marked critical ignore the skip. `restore update skip --clear` undoes it. Closing the
banner only hides it until ReStore restarts.

The command reference is in [CLI Usage](usage/cli.md#updates).
