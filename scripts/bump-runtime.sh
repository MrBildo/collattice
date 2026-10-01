#!/usr/bin/env bash
# Bump the pinned .NET runtime version and regenerate THIRD-PARTY-NOTICES.md in one
# step, so a monthly servicing update stays a five-minute, single-commit PR.
#
# Why this exists. The self-contained publish bundles the entire .NET runtime into
# every release archive, so the version is pinned (RuntimeFrameworkVersion in
# Collabot.Collattice.Api.csproj) rather than floating: a rebuild from a given source
# revision then reproduces the same runtime, and a servicing update is a deliberate
# change the team makes rather than one the SDK makes for it. But the pin and the
# notices file are two records of the same fact -- the notices file names the
# runtime pack version it redistributes, so it goes stale the instant the pin
# moves. Bumping one without the other is exactly the drift the notices gate exists
# to catch; it would red every open pull request until someone regenerated the file
# by hand. This does both together, from the same ground truth (a real publish's
# deps.json and the browser bundle's sourcemaps) that the CI gate checks against, so
# the two records cannot disagree.
#
# What it changes, and nothing else: the <RuntimeFrameworkVersion> line in the
# csproj, and the generated inventory block in THIRD-PARTY-NOTICES.md. Everything it
# builds to derive the new inventory (a self-contained publish and the frontend
# bundle sourcemaps) lands in a scratch directory that is removed on exit, so the
# working tree is left with those two edits and no residue.
#
# It deliberately leaves the SDK line in global.json alone. That line is a floor (any
# .NET 10 SDK at or above it builds the repo), not a tracked version, and the runtime
# packs this pin selects come from NuGet rather than from the installed SDK, so a
# runtime bump never needs a newer SDK. Raising the floor in step with the runtime
# would make every developer install a new SDK each month, and would lock out the
# 10.0.1xx feature band that Ubuntu's own .NET packages carry.
#
# Usage:
#   bump-runtime.sh <version>
#     <version>  the .NET runtime servicing version to pin, e.g. 10.0.12
#
# Requires dotnet, npm/npx, and jq on PATH -- the same tools the release build uses.
# Run it from anywhere; it locates the repository from its own path.

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 <version>   (e.g. $0 10.0.12)" >&2
  exit 2
fi

NEW_VERSION="$1"

# A plain X.Y.Z servicing version. Previews and release candidates are deliberately
# not accepted -- we pin released servicing builds -- and a typo naming a version
# that does not exist would otherwise surface only as a package-restore failure
# minutes into the build. Reject it here, before anything runs.
if [[ ! "${NEW_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "bump-runtime: '${NEW_VERSION}' is not an X.Y.Z runtime version (e.g. 10.0.12)." >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

CSPROJ="${REPO_ROOT}/backend/Collabot.Collattice.Api/Collabot.Collattice.Api.csproj"
NOTICES="${REPO_ROOT}/THIRD-PARTY-NOTICES.md"
FRONTEND_DIR="${REPO_ROOT}/frontend"
GENERATE="${SCRIPT_DIR}/generate-third-party-notices.sh"
EXTRACT="${SCRIPT_DIR}/extract-bundle-sourcemaps.sh"
CURRENCY="${SCRIPT_DIR}/check-runtime-currency.sh"

for f in "${CSPROJ}" "${NOTICES}" "${GENERATE}" "${EXTRACT}" "${CURRENCY}"; do
  [[ -f "${f}" ]] || { echo "bump-runtime: expected file not found: ${f}" >&2; exit 2; }
done
[[ -d "${FRONTEND_DIR}" ]] || { echo "bump-runtime: frontend directory not found: ${FRONTEND_DIR}" >&2; exit 2; }

for tool in dotnet npm npx jq; do
  command -v "${tool}" >/dev/null 2>&1 || { echo "bump-runtime: '${tool}' is required on PATH." >&2; exit 2; }
done

BEGIN_MARKER='<!-- BEGIN GENERATED INVENTORY -->'
END_MARKER='<!-- END GENERATED INVENTORY -->'

# --------------------------------------------------------------- 1. rewrite the pin
# Assert the pin is present exactly once before touching it. A csproj that grew a
# second <RuntimeFrameworkVersion>, or lost the one this keys on, must stop the run
# rather than let a blind replace edit the wrong line or silently match nothing.
PIN_COUNT="$(grep -c '<RuntimeFrameworkVersion>' "${CSPROJ}" || true)"
if [[ "${PIN_COUNT}" -ne 1 ]]; then
  echo "bump-runtime: expected exactly one <RuntimeFrameworkVersion> in" >&2
  echo "  ${CSPROJ}" >&2
  echo "but found ${PIN_COUNT}. The pin this script maintains is not where it expects" >&2
  echo "it; resolve that by hand rather than letting an automated edit guess." >&2
  exit 1
fi

CURRENT_VERSION="$(sed -n 's|.*<RuntimeFrameworkVersion>\(.*\)</RuntimeFrameworkVersion>.*|\1|p' "${CSPROJ}")"

if [[ "${NEW_VERSION}" == "${CURRENT_VERSION}" ]]; then
  echo "bump-runtime: the pin is already ${NEW_VERSION}; regenerating the notices to confirm no drift."
else
  echo "bump-runtime: pin ${CURRENT_VERSION} -> ${NEW_VERSION}"
fi

# Portable in-place rewrite (no sed -i, whose syntax differs across GNU and BSD):
# edit to a sibling temp file, then move it over.
sed "s|<RuntimeFrameworkVersion>[^<]*</RuntimeFrameworkVersion>|<RuntimeFrameworkVersion>${NEW_VERSION}</RuntimeFrameworkVersion>|" \
  "${CSPROJ}" > "${CSPROJ}.bump-tmp"
mv "${CSPROJ}.bump-tmp" "${CSPROJ}"

# ----------------------------------------------------- 2. regenerate the inventory
# Everything below builds into a scratch directory outside the tree, so the only
# working-tree changes this script leaves are the csproj pin and the notices file.
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

PUBLISH_DIR="${WORK}/publish"
MAPS_DIR="${WORK}/sourcemaps"

# The frontend half of the inventory is derived from the bundle's own sourcemaps,
# so the frontend must be built exactly as CI builds it -- npm ci for a lockfile-
# faithful install, then a sourcemap build. Anything less risks a notices block
# that does not match what CI regenerates in --check.
echo "bump-runtime: installing frontend dependencies (npm ci)..."
( cd "${FRONTEND_DIR}" && npm ci )

echo "bump-runtime: building the frontend bundle with sourcemaps..."
( cd "${FRONTEND_DIR}" && npx vite build --sourcemap hidden )

# Move the sourcemaps out of the build output into the scratch dir -- the same step
# CI runs before deriving the inventory, so the npm side of the block comes from
# exactly the build a release would ship.
bash "${EXTRACT}" "${FRONTEND_DIR}/dist" "${MAPS_DIR}"

# Self-contained publish at the new pin. win-x64 to match CI's contract job; the
# runtime pack version and the inventory it yields are RID-independent (the
# generator normalizes the RID out of both the runtime-pack and native-library
# names), so one RID represents all five.
echo "bump-runtime: publishing self-contained (win-x64) at the new pin..."
dotnet publish "${CSPROJ}" \
  -c Release \
  -r win-x64 \
  --self-contained \
  --nologo \
  -o "${PUBLISH_DIR}"

DEPS_JSON="${PUBLISH_DIR}/Collabot.Collattice.Api.deps.json"
[[ -f "${DEPS_JSON}" ]] || { echo "bump-runtime: publish produced no ${DEPS_JSON}." >&2; exit 1; }

echo "bump-runtime: regenerating the notices inventory..."
bash "${GENERATE}" "${DEPS_JSON}" "${FRONTEND_DIR}" "${MAPS_DIR}" > "${WORK}/block"
[[ -s "${WORK}/block" ]] || { echo "bump-runtime: generated an empty inventory block." >&2; exit 1; }

# ------------------------------------------------------------ 3. splice the block
# Replace only the lines between the BEGIN/END markers, preserving everything
# around them, so the notices diff is exactly the inventory that changed and
# nothing else. LC_ALL=C keeps awk byte-transparent over the file's UTF-8 content.
# Fail if the markers are missing or out of order rather than write a mangled file.
LC_ALL=C awk \
  -v beginm="${BEGIN_MARKER}" -v endm="${END_MARKER}" -v blockfile="${WORK}/block" '
    BEGIN { while ((getline line < blockfile) > 0) { block = block line "\n" } }
    $0 == beginm { print; printf "%s", block; seen_begin = 1; skipping = 1; next }
    $0 == endm   { if (!seen_begin) { exit 4 } print; skipping = 0; seen_end = 1; next }
    skipping     { next }
                 { print }
    END { if (!seen_begin || !seen_end) { exit 3 } }
  ' "${NOTICES}" > "${NOTICES}.bump-tmp" || {
  echo "bump-runtime: could not find the BEGIN/END inventory markers, in order, in" >&2
  echo "  ${NOTICES}" >&2
  echo "The file's structure has changed; splice the regenerated block by hand." >&2
  rm -f "${NOTICES}.bump-tmp"
  exit 1
}
mv "${NOTICES}.bump-tmp" "${NOTICES}"

# --------------------------------------------------------------- 4. self-verify
# Regenerate-and-diff against what was just written: the same gate CI runs, so a
# green result here means the committed file will pass CI. This also runs the
# license-text coverage check, catching a newly bundled component whose license
# text is not yet reproduced in the file.
echo "bump-runtime: verifying the regenerated notices file..."
bash "${GENERATE}" --check "${NOTICES}" "${DEPS_JSON}" "${FRONTEND_DIR}" "${MAPS_DIR}"

# Report the currency verdict for the new pin. Informational -- the CI gate is the
# enforcement -- but a bump that lands behind the current servicing release is
# almost certainly a mistake, so say so loudly. Do not fail on it: an intentional
# rollback to an older pin is a legitimate, if rare, reason to run this.
echo
if bash "${CURRENCY}" "${DEPS_JSON}"; then
  :
else
  echo
  echo "bump-runtime: WARNING -- the new pin ${NEW_VERSION} is not the current servicing" >&2
  echo "release (see above). CI's runtime-currency gate will fail on this pin. Unless" >&2
  echo "this is a deliberate rollback, bump to the current release instead." >&2
fi

echo
echo "bump-runtime: done. Working-tree changes (expected: these two files, nothing else):"
if git -C "${REPO_ROOT}" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  git -C "${REPO_ROOT}" status --short -- "${CSPROJ}" "${NOTICES}"
else
  echo "  M ${CSPROJ}"
  echo "  M ${NOTICES}"
fi
echo
echo "Review the diff, then open a single-commit PR (build: / chore:)."
