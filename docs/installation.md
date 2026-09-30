# Installation

## One-Line Install

### macOS / Linux

```bash
curl -sSL https://raw.githubusercontent.com/MrBildo/collattice/main/install.sh | bash
~/.collattice/Collabot.Collattice.Api
```

### Windows (PowerShell)

```powershell
irm https://raw.githubusercontent.com/MrBildo/collattice/main/install.ps1 | iex
& "$env:LOCALAPPDATA\Collattice\Collabot.Collattice.Api.exe"
```

> **A note on the install paths — fresh vs. existing.** A fresh install places the
> install directory and database under the Collattice name: `~/.collattice`
> (macOS/Linux) or `%LOCALAPPDATA%\Collattice` (Windows), with `collattice.db` inside.
> An install already present under the earlier name (`~/.collaboard`,
> `%LOCALAPPDATA%\Collaboard`, `collaboard.db`) is detected by the installer and kept
> exactly where it is — no data is moved, so upgrading in place is safe. Migrating an
> existing install onto the new names lands in a later release. The commands above
> show the fresh-install location; if you are upgrading an existing install, run the
> binary from your existing directory instead.

### Installing a specific release

By default the installers fetch the latest release. To pin a particular one, set
`COLLATTICE_VERSION` to its release tag. GitHub's "latest release" never points at a
pre-release, so this is also how you install a release candidate.

```bash
curl -sSL https://raw.githubusercontent.com/MrBildo/collattice/main/install.sh | COLLATTICE_VERSION=v3.1.0 bash
```

```powershell
$env:COLLATTICE_VERSION = 'v3.1.0'
irm https://raw.githubusercontent.com/MrBildo/collattice/main/install.ps1 | iex
```

The value must be a release tag such as `v3.1.0`, or a pre-release tag such as
`v3.2.0-rc.1`; anything else stops the installer before it downloads. Pin `v3.0.0` or
later: older releases were published under the project's previous name, and the installer
can't find their downloads.

In PowerShell the variable stays set for the rest of the window, so a later plain install
would reinstall the pinned release. Clear it when you're done:

```powershell
Remove-Item Env:COLLATTICE_VERSION
```

## Manual Download

Download the latest release for your platform from [GitHub Releases](https://github.com/MrBildo/collattice/releases/latest):

| Platform | Artifact |
|----------|----------|
| Windows 64-bit | `collattice-win-x64.zip` |
| macOS Apple Silicon | `collattice-osx-arm64.tar.gz` |
| macOS Intel | `collattice-osx-x64.tar.gz` |
| Linux 64-bit | `collattice-linux-x64.tar.gz` |
| Linux ARM64 | `collattice-linux-arm64.tar.gz` |

Extract the archive. Before the first run, edit `appsettings.json` next to the
executable to set an absolute database path — Collattice requires
`ConnectionStrings:Board` and does not derive a path from the working or binary
directory (the one-line installers above do this for you):

```jsonc
// appsettings.json
{
  "ConnectionStrings": {
    "Board": "Data Source=/absolute/path/to/data/collattice.db"
  }
}
```

Your edits to `appsettings.json` survive upgrades — when you re-run the one-line
installer it performs a smart three-way merge.

Then run the executable. No runtime or framework installation required.

## macOS Gatekeeper

On macOS, you may need to remove the quarantine attribute after downloading:

```bash
xattr -d com.apple.quarantine ./Collabot.Collattice.Api
```

## First Run

1. Run the executable
2. Open **http://localhost:8080** in your browser
3. Copy the admin auth key from the console output
4. Enter the key on the login screen

The admin key is printed once on first startup. To set a persistent key, see [Host Configuration](../README.md#host-configuration).

## Updating

1. Stop the running process
2. Re-run the one-line installer (recommended) — it preserves your `appsettings.json`
   edits via smart-merge, refreshes untouched shipped defaults, and adds any new
   shipped keys. The `data/` directory is left untouched.
3. For a manual install: extract the new release and run
   `./Collabot.Collattice.Api --merge-appsettings <new-appsettings.json> ./appsettings.json --baseline ./appsettings.shipped.json`
   from the install directory.
4. Start the app — migrations run automatically, database is backed up first.
