#!/usr/bin/env bash
# Move the browser bundle's sourcemap sidecars out of the build output, so the
# inventory in THIRD-PARTY-NOTICES.md can be derived from the SAME build that
# ships rather than from a second build compared against it.
#
# Why a move rather than a copy-then-delete, and why this exists at all:
#
#   The notices file needs sourcemaps (they name the npm packages that actually
#   reached the bundle). The archive must not contain them. Those two facts used
#   to be reconciled by building the frontend twice -- once plain to ship, once
#   with sourcemaps to read -- and asserting the two builds were byte-identical.
#   They are not required to be: under Vite 5, repeated builds of an unchanged
#   tree drew one of two variants of one shared chunk, so the assertion failed on
#   a healthy tree often enough to teach people to re-run it. Building once
#   removes the question instead of answering it.
#
#   That makes the removal load-bearing in a way it never was before. A build
#   that emitted no sourcemaps at all, or a removal that quietly matched nothing,
#   would leave the inventory derived from an empty set (silently describing
#   nothing) or leave .map files in the tree that is copied into wwwroot and
#   packaged (silently shipping our own source layout). A third thing can go
#   wrong that neither of those catches: the build mode itself. `--sourcemap
#   hidden` is what lets this move happen without leaving the code pointing at
#   the files it just relocated -- `hidden` writes the sidecars without adding
#   a `sourceMappingURL` comment naming them, where the more familiar
#   `--sourcemap true` writes both. Lose the word and every check above still
#   passes; every shipped file would just ask a browser for a map that no
#   longer exists. So all three are asserted here -- maps were produced, none
#   remain, nothing left references one -- and verify-archive.sh independently
#   rejects any .map that reaches a packaged archive.
#
#   The move happens before anything reads the build output, so every later step
#   inherits a tree with no sourcemaps in it rather than having to remember.
#
# Usage:
#   extract-bundle-sourcemaps.sh <dist-dir> <sourcemap-dir>
#     <dist-dir>       the Vite build output (sourcemaps are moved OUT of this)
#     <sourcemap-dir>  where they are moved TO; recreated on each run. Keep this
#                      outside the Vite root -- Tailwind's content scan is rooted
#                      there and skips only what git ignores, so an unignored
#                      directory inside it becomes an input to the next build.

set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <dist-dir> <sourcemap-dir>" >&2
  exit 2
fi

DIST="$1"
MAPS="$2"

if [[ ! -d "${DIST}" ]]; then
  echo "extract-bundle-sourcemaps: '${DIST}' is not a directory." >&2
  exit 2
fi

rm -rf "${MAPS}"
mkdir -p "${MAPS}"

# mapfile consumes the producer to completion, so there is no early-exiting
# consumer for `set -o pipefail` to trip over.
declare -a found=()
mapfile -t found < <(find "${DIST}" -type f -name '*.map' | LC_ALL=C sort)

if [[ "${#found[@]}" -eq 0 ]]; then
  echo "SOURCEMAPS: no .map files found in '${DIST}'." >&2
  echo "The notices inventory is derived from these, and deriving it from an empty" >&2
  echo "set would report no bundled npm packages while looking like a clean run." >&2
  echo "Build the frontend with sourcemaps enabled (vite build --sourcemap hidden)." >&2
  exit 1
fi

for map in "${found[@]}"; do
  target="${MAPS}/$(basename "${map}")"
  if [[ -e "${target}" ]]; then
    echo "SOURCEMAPS: two sourcemaps share the base name '$(basename "${map}")'," >&2
    echo "so moving them into one directory would discard one of them." >&2
    exit 1
  fi
  mv "${map}" "${target}"
done

# The whole point of the move is that the build output is left clean. Assert it
# rather than assume the move matched everything -- this is the step whose silent
# failure would put our source layout in a published archive.
declare -a residue=()
mapfile -t residue < <(find "${DIST}" -type f -name '*.map' | LC_ALL=C sort)

if [[ "${#residue[@]}" -ne 0 ]]; then
  echo "SOURCEMAPS: .map files are still present in '${DIST}' after the move:" >&2
  printf '%s\n' "${residue[@]}" >&2
  exit 1
fi

# The two checks above assert on FILES. Neither one notices whether the code
# still ASKS for one: `hidden` is what makes the shipped bundle safe to point
# a browser at once the sidecars above are gone -- it writes them without
# adding a `sourceMappingURL` comment naming them, unlike `--sourcemap true`
# (the more familiar mode, and the likely slip if this ever gets copy-pasted
# or "simplified"). Lose that word and both checks above still pass: the build
# still emits every map, and the move above still finds and relocates every
# one of them. What breaks is invisible to both -- every shipped .js/.css
# would carry a reference to a map that was moved out from under it here and
# deleted before packaging, so a browser's devtools would 404 fetching it.
# Cosmetic, but exactly the unasserted-load-bearing-step shape this pipeline
# exists to remove, so it gets its own check rather than riding on the other two.
declare -a referencing=()
mapfile -t referencing < <(grep -rl 'sourceMappingURL=' "${DIST}" --include='*.js' --include='*.css' | LC_ALL=C sort)

if [[ "${#referencing[@]}" -ne 0 ]]; then
  echo "SOURCEMAPS: the following shipped files still reference a sourcemap:" >&2
  printf '%s\n' "${referencing[@]}" >&2
  echo "The .map files were already moved out above, so these references now" >&2
  echo "point at nothing a browser can fetch. This means the build did not use" >&2
  echo "a sourceMappingURL-suppressing mode (vite build --sourcemap hidden)." >&2
  exit 1
fi

echo "Moved ${#found[@]} sourcemap(s) out of '${DIST}' into '${MAPS}'; the build output holds none, and nothing left in it references one."
