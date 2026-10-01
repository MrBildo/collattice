# Collattice — Setup Guide

## Deployment Shapes

Collattice supports two production deployment shapes. Pick the one that matches your environment, then follow the matching section below.

- **(a) LAN single-process.** One self-contained executable serves both the API and the embedded React Portal from the same origin. SQLite next to the binary, no reverse proxy, no CORS. Recommended for small teams on a trusted network. → **[Quick Start](#quick-start)** + **[Configuration](#configuration)** below.
- **(b) Portal + API hosted separately.** The headless API runs as one process (`Hosting:ServeSpa=false`); the React Portal is built and served by any static-file host on its own origin. [Collabhost](https://github.com/MrBildo/collabhost) is one worked example; any static-site host paired with any .NET process supervisor works. → **[Hosted separately (Portal + API)](#hosted-separately-portal--api)** below.

The same executable serves both shapes — the difference is configuration (`Hosting:ServeSpa`, `Cors:AllowedOrigins`) and how the Portal is hosted, not which build you download.

## Quick Start

If you used the one-line installer (see [Installation Guide](docs/installation.md)),
`appsettings.json` is already seeded with an absolute database path — skip to step 2.

1. **Set the database path (required before first run).** Collattice requires
   `ConnectionStrings:Board` to be an absolute path and does not derive one from
   the working or binary directory — startup fails loud if it is unset. Edit
   `appsettings.json` next to the executable to set an absolute path:

   ```jsonc
   // appsettings.json
   {
     "ConnectionStrings": {
       "Board": "Data Source=/absolute/path/to/data/collattice.db"
     }
   }
   ```

   On Windows, use a Windows absolute path, escaping backslashes for JSON:

   ```jsonc
   // appsettings.json
   {
     "ConnectionStrings": {
       "Board": "Data Source=C:\\collattice\\data\\collattice.db"
     }
   }
   ```

   The parent directory is created automatically on first run. Your edits to
   `appsettings.json` survive upgrades — the installer performs a smart three-way
   merge that preserves operator edits, refreshes untouched defaults, and adds
   new shipped keys.

2. Run the executable:

   **macOS / Linux:**
   ```bash
   ./Collabot.Collattice.Api
   ```

   **Windows:**
   ```powershell
   .\Collabot.Collattice.Api.exe
   ```

3. Open **http://localhost:8080** in your browser.

4. Copy the **admin auth key** from the console output — you'll need it to create users and manage boards.

## Configuration

Collattice uses `appsettings.json` for configuration. Edit it directly next to
the executable; your edits are preserved across upgrades via the smart-merge
performed by the installer.

| Setting | Default | Description |
|---------|---------|-------------|
| `Urls` | *(unset)* | Convenience override for bind address and port. When set (or `ASPNETCORE_URLS` is set), wins over `Hosting:ListenAddress`/`Hosting:ListenPort`. |
| `Hosting:ListenAddress` | `0.0.0.0` | Bind address. Used with `Hosting:ListenPort` when `Urls`/`ASPNETCORE_URLS` is unset. |
| `Hosting:ListenPort` | `8080` | Bind port. Used with `Hosting:ListenAddress` when `Urls`/`ASPNETCORE_URLS` is unset. |
| `Hosting:ServeSpa` | `true` | Serve the embedded React Portal from `wwwroot/`. Set to `false` for headless hosted-separately deployments. See [Hosted separately (Portal + API)](#hosted-separately-portal--api). |
| `Cors:AllowedOrigins` | `[]` (empty) | Allowed cross-origin Portal hosts. Empty disallows all cross-origin requests; required for hosted-separately deployments. |
| `ConnectionStrings:Board` | *(required — no default)* | SQLite database path. Must be an **absolute** path; the app does not derive a path from the working or binary directory. The installer writes this into `appsettings.json`. |
| `Admin:AuthKey` | *(auto-generated)* | Override the admin auth key |

### Environment Variables

All settings can be overridden with environment variables. Nested keys use a
double-underscore (`__`) separator; there is no application-specific prefix:

```bash
export Urls=http://0.0.0.0:9090
export Admin__AuthKey=my-secret-key
export ConnectionStrings__Board="Data Source=/var/data/collattice.db"
```

Environment variables win over `appsettings.json`.

## Database

- The SQLite database path is **required** configuration — `ConnectionStrings:Board`
  must be set to an absolute path. The installer writes this into `appsettings.json`
  (pointing at `<install-dir>/data/collattice.db` for a fresh install); if you run the binary without the
  installer, set it yourself before first run or startup fails loud with the missing
  key named.
- The database file and its parent directory are created automatically on first run
- Schema migrations run automatically on startup
- The database file is backed up automatically before applying new migrations
- Backups are saved next to the database as `<db-filename>.bak-{timestamp}` (e.g.
  `collattice.db.bak-{timestamp}` for a fresh install)
- **Fresh installs use Collattice-named locations; existing installs keep their
  earlier names.** A brand-new install places the install directory and database
  under the Collattice name — `~/.collattice` (macOS/Linux) or
  `%LOCALAPPDATA%\Collattice` (Windows), with `data/collattice.db` inside. An install
  already present under the earlier name (`~/.collaboard`, `%LOCALAPPDATA%\Collaboard`,
  `collaboard.db`) is **detected by the installer and kept exactly where it is** — no
  data is moved, so upgrading in place is safe. Migrating an existing install onto the
  new names is a deliberate, separate step that lands in a later release; the app,
  binary, and release archive are already named Collattice.

## Hosted separately (Portal + API)

In this shape the API runs headless (no embedded Portal); a static-file host serves the React Portal on its own origin and points it at the API's origin via a runtime `config.json`. [Collabhost](https://github.com/MrBildo/collabhost) is the worked example below — substitute any equivalent static-site host and any process supervisor that can run a self-contained .NET binary.

### API process — disable the embedded Portal, allow the Portal's origin

Run the same `Collabot.Collattice.Api` binary you would for the LAN shape, but flip `Hosting:ServeSpa` off and tell the API which Portal origin(s) to allow. Either edit `appsettings.json` next to the executable:

```jsonc
// appsettings.json
{
  "Hosting": {
    "ServeSpa": false,
    "ListenAddress": "127.0.0.1",
    "ListenPort": 5000
  },
  "Cors": {
    "AllowedOrigins": [
      "https://collattice.example.com"
    ]
  },
  "ConnectionStrings": {
    "Board": "Data Source=/srv/collattice/data/collattice.db"
  }
}
```

…or override via environment variables (typically how a process supervisor injects them):

```bash
export Hosting__ServeSpa=false
export Hosting__ListenAddress=127.0.0.1
export Hosting__ListenPort=5000
export Cors__AllowedOrigins__0=https://collattice.example.com
export ConnectionStrings__Board="Data Source=/srv/collattice/data/collattice.db"
```

Notes:

- `Hosting:ServeSpa=false` makes unmatched routes return 404 instead of the SPA shell. The API endpoints (`/api/v1/*`), MCP endpoint (`/mcp`), and health endpoints (`/health`, `/alive`) keep serving.
- `Hosting:ListenAddress=127.0.0.1` binds loopback only; the static-site host (or reverse proxy) is expected to be on the same machine. Use `0.0.0.0` if you need to reach the API across machines.
- `Cors:AllowedOrigins` is an array of full origins (`scheme://host[:port]`). Empty list (the LAN default) disallows all cross-origin requests; populate it with every Portal origin that will call the API.
- Behind a reverse proxy that injects `ASPNETCORE_URLS`, the API uses that and the structured `Hosting:ListenAddress`/`Hosting:ListenPort` pair is ignored. `Cors:AllowedOrigins` is unaffected by this — set it regardless.
- **TLS at the proxy.** When the proxy terminates HTTPS and talks to the API over plain HTTP, the API reads the original scheme from the proxy's `X-Forwarded-Proto` header, so the base URL that the MCP tool `get_api_info` reports is `https://`. The header is only honored when the proxy connects from the same machine (a loopback address). Requests from any other address keep the scheme they arrived with, and the `X-Forwarded-For` and `X-Forwarded-Host` headers are not used. A proxy on a different machine therefore gets the scheme its own connection uses. Caddy's `reverse_proxy` sends `X-Forwarded-Proto` by default. nginx does not, so add `proxy_set_header X-Forwarded-Proto $scheme;` to the location block.

### Portal — static-file host + runtime `config.json`

The Portal artifact is the `frontend/dist/` directory produced by `npx vite build` (also published in the release archives). Deploy it to any static-file host — Collabhost, nginx, Caddy, an S3+CloudFront pair, etc.

Place a `config.json` file at the root of the static-site bundle (next to `index.html`) telling the Portal where the API lives:

```json
{
  "apiBaseUrl": "https://api.collattice.example.com/api/v1"
}
```

The Portal fetches `/config.json` from its own origin once at boot, before rendering. `apiBaseUrl` is the absolute URL the Portal uses to reach the API — point it at the API origin's `/api/v1` path. The Portal's origin must also appear in the API's `Cors:AllowedOrigins` list, or the browser will block requests at the preflight stage.

If `/config.json` is absent, malformed, or returns a non-2xx status, the Portal falls back to a same-origin relative base URL (`/api/v1`). That fallback only makes sense for the LAN single-process shape — for hosted-separately deployments, treat a missing or invalid `config.json` as a deployment defect.

### Verifying the hosted-separately deployment

1. The API's `/health` endpoint returns 200 from the API origin.
2. The Portal loads at its own origin (the static-file host serves `index.html`).
3. The browser's Network tab shows a successful `GET /config.json` from the Portal origin with the expected `apiBaseUrl`.
4. Subsequent `GET /api/v1/...` calls from the Portal hit the API origin and return with the expected `Access-Control-Allow-Origin` header. A 204 preflight with no CORS headers means the Portal's origin is not in `Cors:AllowedOrigins`.

## Updating

1. Stop the running process
2. Re-run the one-line installer (recommended) — it preserves your `appsettings.json`
   edits via smart-merge, refreshes untouched shipped defaults, and adds any new
   shipped keys. The merge writes a baseline sidecar `appsettings.shipped.json`
   next to your `appsettings.json` so the next merge knows what shipped last time.
3. Or, for a manual install: download the new release, extract it, and run
   `./Collabot.Collattice.Api --merge-appsettings <shipped-appsettings.json> ./appsettings.json --baseline ./appsettings.shipped.json`
   from the install directory.
4. Start the app — migrations run automatically.

## Version

Check the installed version:

```bash
./Collabot.Collattice.Api --version
```
