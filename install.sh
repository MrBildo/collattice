#!/usr/bin/env bash
set -euo pipefail

REPO="MrBildo/collattice"

# Fresh vs. existing install detection. A brand-new install uses the current
# (Collattice) directory and database name. An install already present under the
# earlier name is detected and kept exactly where it is -- its data never moves, so
# a fresh install can never land beside and orphan an operator's real database.
# Migrating an existing install onto the new name is a separate, later step.
OLD_INSTALL_DIR="${HOME}/.collaboard"
NEW_INSTALL_DIR="${HOME}/.collattice"

if [ -d "${OLD_INSTALL_DIR}" ] && { [ -f "${OLD_INSTALL_DIR}/appsettings.json" ] || [ -f "${OLD_INSTALL_DIR}/data/collaboard.db" ]; }; then
    INSTALL_DIR="${OLD_INSTALL_DIR}"
    DB_FILENAME="collaboard.db"
    INSTALL_KIND="existing"
else
    INSTALL_DIR="${NEW_INSTALL_DIR}"
    DB_FILENAME="collattice.db"
    INSTALL_KIND="fresh"
fi

DB_PATH="${INSTALL_DIR}/data/${DB_FILENAME}"

# --dry-run: report the resolved install directory and database path, then exit
# without downloading or touching the filesystem -- so an operator (or CI) can
# confirm up front which location an install would use (fresh vs. detected-existing).
if [ "${1:-}" = "--dry-run" ]; then
    echo "install-kind: ${INSTALL_KIND}"
    echo "install-dir: ${INSTALL_DIR}"
    echo "db-path: ${DB_PATH}"
    exit 0
fi

# Detect platform and architecture
detect_platform() {
    local os arch
    os="$(uname -s)"
    arch="$(uname -m)"

    case "$os" in
        Linux)  os="linux" ;;
        Darwin) os="osx" ;;
        *)      echo "Unsupported OS: $os" >&2; exit 1 ;;
    esac

    case "$arch" in
        x86_64|amd64)  arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *)             echo "Unsupported architecture: $arch" >&2; exit 1 ;;
    esac

    echo "${os}-${arch}"
}

PLATFORM="$(detect_platform)"
ARTIFACT_NAME="collattice-${PLATFORM}"

echo "Detected platform: ${PLATFORM}"
echo "Install directory: ${INSTALL_DIR}"
if [ "${INSTALL_KIND}" = "existing" ]; then
    echo "Existing installation detected; keeping it in place, no data moved."
fi
echo

# COLLATTICE_VERSION optionally pins the release to install (for example v3.1.0,
# or a pre-release such as v3.2.0-rc.1). Unset, the latest release is installed.
# GitHub's "latest release" never points at a pre-release, so the pin is also how
# a release candidate gets installed.
if [ -n "${COLLATTICE_VERSION:-}" ]; then
    if [[ ! "${COLLATTICE_VERSION}" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$ ]]; then
        echo "COLLATTICE_VERSION '${COLLATTICE_VERSION}' is not a release tag (expected vX.Y.Z or vX.Y.Z-<pre-release>)." >&2
        exit 1
    fi
    RELEASE_TAG="${COLLATTICE_VERSION}"
    echo "Requested release: ${RELEASE_TAG}"
else
    # Get latest release tag from GitHub API
    echo "Fetching latest release..."
    RELEASE_TAG=$(curl -sSf "https://api.github.com/repos/${REPO}/releases/latest" | grep '"tag_name"' | sed -E 's/.*"([^"]+)".*/\1/')

    if [ -z "$RELEASE_TAG" ]; then
        echo "Failed to fetch latest release." >&2
        exit 1
    fi

    echo "Latest release: ${RELEASE_TAG}"
fi

# Download artifact
DOWNLOAD_URL="https://github.com/${REPO}/releases/download/${RELEASE_TAG}/${ARTIFACT_NAME}.tar.gz"
TEMP_DIR="$(mktemp -d)"

echo "Downloading ${ARTIFACT_NAME}.tar.gz..."
curl -sSfL "$DOWNLOAD_URL" -o "${TEMP_DIR}/${ARTIFACT_NAME}.tar.gz"

# Extract to temp location first, then merge (preserving data/ and operator config).
# Release archives are flat (contract items at the archive root, no wrapping
# directory -- enforced by publish.yml's "Verify archive contents" step), so no
# --strip-components is used: the files land directly in $TEMP_EXTRACT.
echo "Extracting to ${INSTALL_DIR}..."
TEMP_EXTRACT="${TEMP_DIR}/extract"
mkdir -p "$TEMP_EXTRACT"
tar xzf "${TEMP_DIR}/${ARTIFACT_NAME}.tar.gz" -C "$TEMP_EXTRACT"

mkdir -p "$INSTALL_DIR"

# Copy new files, preserving data/ and appsettings.json (the operator-editable config —
# smart-merged below via Collabot.Collattice.Api --merge-appsettings, #235).
for item in "$TEMP_EXTRACT"/*; do
    name="$(basename "$item")"
    # Skip data directory (contains the database)
    [ "$name" = "data" ] && continue
    # Carve out appsettings.json — merged below, never overwritten wholesale.
    [ "$name" = "appsettings.json" ] && continue
    # Remove old version and move new one in
    rm -rf "${INSTALL_DIR}/${name}"
    mv "$item" "${INSTALL_DIR}/${name}"
done

# Make executable
chmod +x "${INSTALL_DIR}/Collabot.Collattice.Api"

# appsettings.json: smart-merge on upgrade, seed on first install (#235).
#
# First install: copy the archive's shipped appsettings.json into place AND seed the
# sidecar baseline (appsettings.shipped.json) so the next upgrade has a reference for
# distinguishing operator-edited keys from untouched defaults. Then seed an absolute
# ConnectionStrings:Board into appsettings.json (Collattice requires it; no default).
#
# Upgrade: invoke `Collabot.Collattice.Api --merge-appsettings <shipped> <ondisk> --baseline
# <baseline>` to perform the three-way merge. The binary owns the merge logic so the
# same shape runs on every platform without duplicating JSON-handling code.
SHIPPED_SRC="${TEMP_EXTRACT}/appsettings.json"
APPSETTINGS_DST="${INSTALL_DIR}/appsettings.json"
BASELINE_DST="${INSTALL_DIR}/appsettings.shipped.json"
COLLATTICE_BIN="${INSTALL_DIR}/Collabot.Collattice.Api"

if [ ! -f "${APPSETTINGS_DST}" ]; then
    # First install — copy shipped → appsettings.json AND seed the baseline sidecar
    # (#235 C-3: required so the next upgrade is not stuck in conservative mode).
    cp "${SHIPPED_SRC}" "${APPSETTINGS_DST}"
    cp "${SHIPPED_SRC}" "${BASELINE_DST}"
    echo "Seeded ${APPSETTINGS_DST} and ${BASELINE_DST} from shipped defaults"

    # Seed the absolute ConnectionStrings:Board into appsettings.json. Collattice requires
    # this key with no default and does not derive a path from the working or binary
    # directory. Prefer python3 (always present on macOS, near-universal on modern Linux);
    # fall back to awk against the known shape when python3 is unavailable.
    seed_with_python() {
        python3 - "${APPSETTINGS_DST}" "${DB_PATH}" <<'PY'
import json
import sys

settings_path, db_path = sys.argv[1], sys.argv[2]
with open(settings_path, 'r', encoding='utf-8') as fh:
    data = json.load(fh)
conn = data.get('ConnectionStrings')
if not isinstance(conn, dict):
    conn = {}
    data['ConnectionStrings'] = conn
existing = conn.get('Board')
if existing is None or (isinstance(existing, str) and existing.strip() == ''):
    conn['Board'] = f"Data Source={db_path}"
    with open(settings_path, 'w', encoding='utf-8') as fh:
        json.dump(data, fh, indent=2)
        fh.write('\n')
PY
    }

    seed_with_awk() {
        # Fallback: append a ConnectionStrings block to the shipped JSON. Targets the
        # case where appsettings.json does not yet contain a ConnectionStrings key.
        # Bails (no rewrite) if the appsettings.json shape is unexpected -- the
        # operator will need to edit appsettings.json by hand.
        awk -v db="${DB_PATH}" '
            BEGIN { written = 0 }
            /"ConnectionStrings"[[:space:]]*:/ { written = 1 }
            { lines[NR] = $0 }
            END {
                if (written) {
                    for (i = 1; i <= NR; i++) print lines[i]
                    exit 0
                }
                # Find the last closing brace and insert a ConnectionStrings entry before it.
                last_brace = 0
                for (i = NR; i >= 1; i--) {
                    if (lines[i] ~ /^[[:space:]]*\}[[:space:]]*$/) {
                        last_brace = i
                        break
                    }
                }
                if (last_brace == 0) {
                    for (i = 1; i <= NR; i++) print lines[i]
                    exit 1
                }
                for (i = 1; i < last_brace; i++) print lines[i]
                # Ensure the previous content line ends with a comma.
                # (Simplest reliable approach: add the new block with a leading comma.)
                printf "  ,\n  \"ConnectionStrings\": {\n    \"Board\": \"Data Source=%s\"\n  }\n", db
                for (i = last_brace; i <= NR; i++) print lines[i]
            }
        ' "${APPSETTINGS_DST}" > "${APPSETTINGS_DST}.new" && mv "${APPSETTINGS_DST}.new" "${APPSETTINGS_DST}"
    }

    if command -v python3 >/dev/null 2>&1; then
        if ! seed_with_python; then
            echo "Warning: could not seed ConnectionStrings:Board via python3." >&2
            echo "Edit ${APPSETTINGS_DST} manually, setting ConnectionStrings:Board to \"Data Source=${DB_PATH}\"." >&2
        else
            echo "Seeded ConnectionStrings:Board = Data Source=${DB_PATH}"
        fi
    else
        if ! seed_with_awk; then
            echo "Warning: could not seed ConnectionStrings:Board via awk." >&2
            echo "Edit ${APPSETTINGS_DST} manually, setting ConnectionStrings:Board to \"Data Source=${DB_PATH}\"." >&2
        else
            echo "Seeded ConnectionStrings:Board = Data Source=${DB_PATH}"
        fi
    fi
else
    # Upgrade — invoke the C# merge subcommand. The binary was just unpacked above, so
    # it is guaranteed to be the version-correct artifact that ships --merge-appsettings.
    # Every skip path inside the subcommand is loud + non-zero exit (#235 C-4 / AC-3),
    # so a failure here surfaces; `set -e` aborts the installer rather than silently
    # leaving an unmerged appsettings.json behind.
    "${COLLATTICE_BIN}" --merge-appsettings "${SHIPPED_SRC}" "${APPSETTINGS_DST}" --baseline "${BASELINE_DST}"
    echo "Smart-merged ${APPSETTINGS_DST} (operator edits preserved, new shipped keys added)"
fi

# Clean up
rm -rf "$TEMP_DIR"

echo
echo "Collattice installed to ${INSTALL_DIR}"
echo

# Suggest adding to PATH
SHELL_NAME="$(basename "$SHELL")"
case "$SHELL_NAME" in
    zsh)  RC_FILE="$HOME/.zshrc" ;;
    bash) RC_FILE="$HOME/.bashrc" ;;
    *)    RC_FILE="$HOME/.profile" ;;
esac

if [[ ":$PATH:" != *":${INSTALL_DIR}:"* ]]; then
    echo "To add Collattice to your PATH, run:"
    echo "  echo 'export PATH=\"${INSTALL_DIR}:\$PATH\"' >> ${RC_FILE}"
    echo "  source ${RC_FILE}"
    echo
fi

echo "To start Collattice:"
echo "  ${INSTALL_DIR}/Collabot.Collattice.Api"
echo
echo "Then open http://localhost:8080 in your browser."
