#!/usr/bin/env bash
# Derives the bundled-third-party-component inventory that THIRD-PARTY-NOTICES.md
# publishes, from the two artifacts that actually determine what a release archive
# contains:
#
#   * the server's `dotnet publish` dependency manifest (Collabot.Collattice.Api.deps.json)
#   * the browser bundle's sourcemaps (which npm packages contributed code to the
#     JavaScript Vite emitted into wwwroot)
#
# Both are GROUND TRUTH about what ships, not declarations of intent. That
# distinction is the whole point: the frontend's declared dependency closure is
# several times larger than the set that survives tree-shaking into the bundle,
# and at least one declared dependency is never imported and never ships at all.
# A notices file built from the declaration would claim to describe what
# recipients receive while describing something else.
#
# (Deliberately no counts here. A number written into a comment is a claim
# nothing checks, and the defect this whole surface exists to prevent is exactly
# a claim about dependencies going quietly stale. The inventory below is the
# count; it is regenerated and gated.)
#
# Usage:
#   generate-third-party-notices.sh <deps-json> <frontend-dir> <sourcemap-dir>
#       Emit the inventory block to stdout.
#
#   generate-third-party-notices.sh --check <notices-file> \
#       <deps-json> <frontend-dir> <sourcemap-dir>
#       Regenerate and diff against the block delimited by the BEGIN/END markers
#       inside <notices-file>. Exit 1 with the delta when they disagree.
#
# The --check mode is what keeps the file honest. The defect this whole surface
# exists to fix was a dependency arriving with nobody noticing; a static notices
# file with no drift gate reproduces that defect on the next dependency change.
#
# This script never guesses a license. A component whose license cannot be read
# from its own manifest, and which has no entry in the override table below,
# fails the run loudly rather than shipping an inferred answer.

set -euo pipefail

BEGIN_MARKER='<!-- BEGIN GENERATED INVENTORY -->'
END_MARKER='<!-- END GENERATED INVENTORY -->'

CHECK_FILE=""
if [[ "${1:-}" == "--check" ]]; then
  CHECK_FILE="${2:-}"
  shift 2
  if [[ -z "${CHECK_FILE}" ]]; then
    echo "generate-third-party-notices: --check requires a notices file path." >&2
    exit 2
  fi
fi

if [[ $# -ne 3 ]]; then
  echo "usage: $0 [--check <notices-file>] <deps-json> <frontend-dir> <sourcemap-dir>" >&2
  exit 2
fi

DEPS_JSON="$1"
FRONTEND_DIR="$2"
SOURCEMAP_DIR="$3"

[[ -f "${DEPS_JSON}" ]] || { echo "generate-third-party-notices: '${DEPS_JSON}' is not a file." >&2; exit 2; }
for path in "${FRONTEND_DIR}" "${SOURCEMAP_DIR}" ; do
  [[ -d "${path}" ]] || { echo "generate-third-party-notices: '${path}' is not a directory." >&2; exit 2; }
done

command -v jq >/dev/null 2>&1 || { echo "generate-third-party-notices: jq is required." >&2; exit 2; }

# Every jq result below is read as line-oriented text -- package ids, versions,
# paths, license identifiers. The native Windows build of jq (the one winget
# installs, used from Git Bash) ends each output line with CRLF, and the stray
# carriage return then rides inside the value: a NuGet package path built from it
# never resolves, and the run fails at the first package. Stripping it here, at
# the one place jq is invoked, makes every read line-ending-agnostic; on Linux and
# WSL, where jq already emits LF, it changes nothing. (xargs cannot call a shell
# function, so the one jq run through xargs below strips it the same way inline.)
jq() { command jq "$@" | tr -d '\r'; }

NUGET_ROOT="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"

# Components whose own manifest does not state a copyright holder, resolved from
# a sibling artifact in the same distribution rather than from memory. Each entry
# records where the value was read from, so the resolution is auditable and a
# future reviewer can re-check it against the same source.
#
#   @radix-ui/*  -- the seven internal helper packages ship no LICENSE file of
#                   their own. Read from the sibling packages in the same
#                   published set that do carry one (@radix-ui/react-slot/LICENSE
#                   and radix-ui/LICENSE), which are identical.
#   fastdom      -- ships no LICENSE file; its package.json says MIT, and the
#                   package's own README.md carries the MIT text under its
#                   "License" heading, with this copyright line.
override_copyright() {
  case "$1" in
    @radix-ui/number|@radix-ui/react-compose-refs|@radix-ui/react-context|\
@radix-ui/react-direction|@radix-ui/react-use-layout-effect|\
@radix-ui/react-use-previous|@radix-ui/react-use-size)
      echo "Copyright (c) 2022 WorkOS" ;;
    fastdom) echo "Copyright (c) 2016 Wilson Page <wilsonpage@me.com>" ;;
    *) echo "" ;;
  esac
}

# Components whose package manifest omits a license field, resolved from the
# license file the package does ship. Same auditability rule as above.
#
#   khroma -- package.json carries no "license" key; the package ships a
#             `license` file whose body is the MIT text.
override_license() {
  case "$1" in
    khroma) echo "MIT" ;;
    *) echo "" ;;
  esac
}

# Extract the first copyright line from a license file. Leading comment or list
# punctuation is tolerated -- axios, for one, writes its line as "# Copyright ...".
#
# sed quits at the first match rather than piping into `head -n 1`. Under
# `set -o pipefail`, a downstream `head` that exits early can SIGPIPE the
# upstream producer and fail the whole command substitution -- but only when the
# producer has not already finished writing, which for small files is a race.
# This script gates every pull request, so a once-in-a-while failure would look
# like a notices defect and cost someone an afternoon. No pipe, no race.
first_copyright_line() {
  local file="$1"
  [[ -f "${file}" ]] || return 0
  sed -n '
    /^[[:space:]*#>-]*Copyright/ {
      s/^[[:space:]*#>-]*\(Copyright[^\r]*\)/\1/
      s/[[:space:]]*$//
      p
      q
    }
  ' "${file}"
}

# First value enclosed by <open>…</close> on a single line, or empty. Same
# reasoning as above: awk reads a file argument and stops at the first hit, so
# there is no pipe for an early-exiting consumer to break.
first_tag_value() {
  local open="$1" close="$2" file="$3"

  # `opening` / `closing` rather than the obvious names: `close` is a gawk
  # builtin and cannot be a variable.
  awk -v opening="${open}" -v closing="${close}" '
    {
      start = index($0, opening)
      if (start == 0) next

      rest = substr($0, start + length(opening))
      stop = index(rest, closing)
      if (stop == 0) next

      print substr(rest, 1, stop - 1)
      exit
    }
  ' "${file}"
}

fail_unresolved() {
  echo "generate-third-party-notices: cannot resolve ${1} for '${2}'." >&2
  echo "Read it from the component's own distribution and add an entry to the" >&2
  echo "override table in this script, recording where the value came from." >&2
  exit 1
}

emit_inventory() {
  # ---------------------------------------------------------------- .NET runtime
  # A self-contained publish carries the entire .NET runtime and the ASP.NET Core
  # shared framework as loose assemblies. deps.json records them as two runtimepack
  # entries rather than as packages, so they are read from there and asserted to be
  # the Microsoft-published runtime packs; an unrecognized runtimepack fails the run
  # instead of being folded silently into the Microsoft line.
  echo "### .NET runtime and shared framework"
  echo
  echo "Redistributed in full by the self-contained publish."
  echo

  local rp_count=0
  while IFS=$'\t' read -r id version; do
    case "${id}" in
      runtimepack.Microsoft.NETCore.App.Runtime*)
        echo "- \`Microsoft.NETCore.App.Runtime\` ${version} — MIT — Copyright (c) .NET Foundation and Contributors" ;;
      runtimepack.Microsoft.AspNetCore.App.Runtime*)
        echo "- \`Microsoft.AspNetCore.App.Runtime\` ${version} — MIT — Copyright (c) .NET Foundation and Contributors" ;;
      *)
        fail_unresolved "runtime pack identity" "${id}" ;;
    esac
    rp_count=$((rp_count + 1))
  done < <(
    jq -r '.libraries | to_entries[]
           | select(.value.type == "runtimepack")
           | (.key | split("/")) as $p
           | "\($p[0])\t\($p[1])"' "${DEPS_JSON}" \
      | sed 's/\.\(win\|linux\|osx\)-[a-z0-9]*\t/\t/' \
      | sort -u
  )
  if [[ "${rp_count}" -eq 0 ]]; then
    fail_unresolved "runtime packs (none found)" "${DEPS_JSON}"
  fi

  # The two runtimepack entries are not everything the .NET side redistributes.
  # A self-contained publish also emits a native launcher -- the SDK's app-host,
  # patched with the application name -- and that is the binary an operator
  # actually runs. It arrives from the SDK's app-host pack rather than from a
  # package reference, so deps.json never names it: an inventory that stopped at
  # the manifest would omit the one file every recipient definitely executes.
  #
  # Presence is read from the publish output beside the manifest, not assumed.
  # No version is claimed, because nothing in the published tree states one --
  # the app-host pack's version tracks the SDK, and deriving it from the runtime
  # pack's version would be an inference dressed up as a reading.
  local publish_dir app_name
  publish_dir="$(dirname "${DEPS_JSON}")"
  app_name="$(basename "${DEPS_JSON}" .deps.json)"

  if [[ ! -f "${publish_dir}/${app_name}" && ! -f "${publish_dir}/${app_name}.exe" ]]; then
    fail_unresolved "native launcher ('${app_name}' beside the manifest)" "Microsoft.NETCore.App.Host"
  fi

  echo "- \`Microsoft.NETCore.App.Host\` — MIT — Copyright (c) .NET Foundation and Contributors"
  echo "  Shipped as the \`${app_name}\` executable (\`${app_name}.exe\` on Windows): the"
  echo "  .NET SDK's native launcher, patched with the application name. It comes from"
  echo "  the SDK's app-host pack rather than from a package reference, which is why"
  echo "  \`deps.json\` does not record it and why no version is listed for it here."

  # ------------------------------------------------------------- NuGet packages
  echo
  echo "### NuGet packages (server)"
  echo
  echo "Shipped as managed assemblies alongside the executable."
  echo

  while IFS=$'\t' read -r id version; do
    local lower nuspec license copyright
    lower="$(echo "${id}" | tr '[:upper:]' '[:lower:]')"
    nuspec="${NUGET_ROOT}/${lower}/${version}/${lower}.nuspec"
    [[ -f "${nuspec}" ]] || fail_unresolved "nuspec (${nuspec})" "${id}"

    license="$(first_tag_value '<license type="expression">' '</license>' "${nuspec}")"
    if [[ -z "${license}" ]]; then
      license="$(first_tag_value '<licenseUrl>' '</licenseUrl>' "${nuspec}")"
    fi
    [[ -n "${license}" ]] || license="$(override_license "${id}")"
    [[ -n "${license}" ]] || fail_unresolved "license" "${id}"

    # Not every package states a copyright holder. Apache-2.0 and the MIT family
    # both ask that notices PRESENT in the work be retained, so where a package
    # states none there is none to reproduce -- but silently emitting a blank
    # would read as an oversight. Fall back to the manifest's declared authors,
    # labelled as authorship rather than as a copyright claim we invented.
    copyright="$(first_tag_value '<copyright>' '</copyright>' "${nuspec}")"
    [[ -n "${copyright}" ]] || copyright="$(override_copyright "${id}")"
    if [[ -z "${copyright}" ]]; then
      local authors
      authors="$(first_tag_value '<authors>' '</authors>' "${nuspec}")"
      [[ -n "${authors}" ]] || fail_unresolved "copyright holder or authors" "${id}"
      copyright="states no copyright notice; authored by ${authors}"
    fi

    echo "- \`${id}\` ${version} — ${license} — ${copyright}"
  done < <(
    jq -r '.libraries | to_entries[]
           | select(.value.type == "package")
           | (.key | split("/")) as $p
           | "\($p[0])\t\($p[1])"' "${DEPS_JSON}" \
      | LC_ALL=C sort -f
  )

  # --------------------------------------------------------- native third-party
  # Native libraries that arrive from a package rather than from the runtime pack.
  # The runtime pack's own native libraries are covered by the .NET runtime entry
  # above; this section is for everything else, and today that is exactly one
  # library whose file name is the only thing that varies across RIDs.
  echo
  echo "### Native libraries (server)"
  echo

  local native_rows
  native_rows="$(
    jq -r '.targets | to_entries[-1].value | to_entries[]
           | select(.key | startswith("runtimepack") | not)
           | select(.value.native != null)
           | .key as $k | .value.native | keys[]
           | "\($k)\t\(.)"' "${DEPS_JSON}"
  )"
  if [[ -z "${native_rows}" ]]; then
    echo "_None beyond the .NET runtime's own native libraries._"
  else
    while IFS=$'\t' read -r lib asset; do
      # The file name is platform-specific (libe_sqlite3.so / .dylib /
      # e_sqlite3.dll) while the component is not. Normalize to the platform-
      # neutral library name so this inventory is identical for every RID --
      # one archive's notices file would otherwise disagree with another's, and
      # the drift check would fail purely on which platform CI happened to build.
      local libname
      libname="$(basename "${asset}")"
      libname="${libname%.*}"
      libname="${libname#lib}"

      case "${lib}" in
        SQLitePCLRaw.lib.e_sqlite3/*)
          echo "- \`${libname}\` — a compiled build of **SQLite**, which its authors have released"
          echo "  into the **public domain**: no copyright is claimed and no license conditions"
          echo "  attach. Shipped as \`lib${libname}.so\` on Linux, \`lib${libname}.dylib\` on macOS"
          echo "  and \`${libname}.dll\` on Windows. The build is produced and distributed by"
          echo "  \`SQLitePCLRaw.lib.e_sqlite3\`, listed under Apache-2.0 above." ;;
        *)
          fail_unresolved "provenance of native asset '${asset}'" "${lib}" ;;
      esac
    done <<< "${native_rows}"
  fi

  # ----------------------------------------------------- npm packages in bundle
  # Derived from the sourcemaps of the JavaScript Vite emitted, so the list is
  # what the browser actually receives rather than what package.json declares.
  echo
  echo "### npm packages (browser bundle)"
  echo
  echo "Compiled into the JavaScript served from \`wwwroot\`."
  echo

  # A package can appear at more than one version in one bundle: npm nests a
  # second copy under a dependent when versions conflict, and Vite bundles
  # whichever copy the import resolved to. Resolving the manifest by name alone
  # would read the hoisted top-level copy and report a version that is not the
  # one shipped -- `parse5`, for instance, is bundled at both 8.0.0 (hoisted) and
  # 7.3.0 (nested under hast-util-raw). So the package DIRECTORY is carried
  # through from the sourcemap path, and each distinct directory is listed.
  #
  # Sourcemap sources are paths relative to the emitted chunk, always of the form
  # `../../node_modules/...`; stripping the leading `../` segments yields a path
  # relative to the frontend directory. The package directory then runs up to and
  # including the segment(s) after the LAST `node_modules/` -- two segments for a
  # scoped name, one otherwise.
  local pkg_list
  pkg_list="$(
    find "${SOURCEMAP_DIR}" -name '*.map' -type f -print0 \
      | xargs -0 -r jq -r '.sources[]?' \
      | tr -d '\r' \
      | sed 's|^\(\.\./\)*||' \
      | awk '
          {
            marker = "node_modules/"
            pos = 0; last = 0
            while ((p = index(substr($0, pos + 1), marker)) > 0) {
              pos = pos + p
              last = pos
            }
            if (last == 0) next

            rest = substr($0, last + length(marker))
            split(rest, seg, "/")
            name = (seg[1] ~ /^@/) ? seg[1] "/" seg[2] : seg[1]
            if (name == "" || (seg[1] ~ /^@/ && seg[2] == "")) next

            print name "\t" substr($0, 1, last - 1) marker name
          }' \
      | LC_ALL=C sort -u -t$'\t' -k1,1 -k2,2
  )"
  [[ -n "${pkg_list}" ]] || fail_unresolved "any bundled npm package (no sourcemaps?)" "${SOURCEMAP_DIR}"

  while IFS=$'\t' read -r name reldir; do
    [[ -n "${name}" ]] || continue
    local dir manifest license copyright licfile
    dir="${FRONTEND_DIR}/${reldir}"
    manifest="${dir}/package.json"
    [[ -f "${manifest}" ]] || fail_unresolved "package manifest" "${name}"

    license="$(jq -r 'if (.license | type) == "string" then .license
                      elif .license.type then .license.type
                      elif .licenses then ([.licenses[].type] | join(" OR "))
                      else "" end' "${manifest}")"
    [[ -n "${license}" && "${license}" != "null" ]] || license="$(override_license "${name}")"
    [[ -n "${license}" ]] || fail_unresolved "license" "${name}"

    # `find | sort` read whole into an array, taking element zero, rather than
    # `| head -n 1` -- same pipefail reasoning as first_copyright_line above.
    local -a licfiles=()
    mapfile -t licfiles < <(
      find "${dir}" -maxdepth 1 -type f \
        \( -iname 'LICENSE' -o -iname 'LICENSE.*' -o -iname 'LICENCE' -o -iname 'LICENCE.*' -o -iname 'COPYING' -o -iname 'COPYING.*' \) \
        | LC_ALL=C sort
    )
    licfile="${licfiles[0]:-}"
    copyright="$(first_copyright_line "${licfile}")"
    [[ -n "${copyright}" ]] || copyright="$(override_copyright "${name}")"
    [[ -n "${copyright}" ]] || fail_unresolved "copyright holder" "${name}"

    local version
    version="$(jq -r '.version' "${manifest}")"
    echo "- \`${name}\` ${version} — ${license} — ${copyright}"
  done <<< "${pkg_list}"

  # ---------------------------------------------------------------- CSS toolchain
  # Sourcemaps cannot see these: they contribute generated CSS to the stylesheet
  # rather than JavaScript modules to the bundle, so no module path names them.
  # They are read from styles.css's own @import/@plugin directives, which are
  # where a third-party CSS toolchain enters the build.
  #
  # That is narrower than "every stylesheet the bundle contains", and the
  # difference is worth knowing before someone adds one: a component can import a
  # .css file directly, and one does (MarkdownRenderer imports
  # src/styles/highlight.css, a .prose-scoped adaptation of highlight.js's own
  # vs and github-dark themes). That case is already covered -- highlight.js is
  # inventoried from the bundle, under the same license and the same copyright
  # holder as the themes derived from it -- but it is covered by coincidence, not
  # by this scan. A directly imported stylesheet copied from a project that does
  # NOT otherwise reach the bundle would be missed here.
  echo
  echo "### CSS toolchain (browser bundle)"
  echo
  echo "Emit generated CSS into the shipped stylesheet."
  echo

  while read -r name; do
    [[ -n "${name}" ]] || continue
    local manifest license copyright licfile
    manifest="${FRONTEND_DIR}/node_modules/${name}/package.json"
    [[ -f "${manifest}" ]] || fail_unresolved "package manifest" "${name}"
    license="$(jq -r 'if (.license | type) == "string" then .license else .license.type // "" end' "${manifest}")"
    [[ -n "${license}" && "${license}" != "null" ]] || fail_unresolved "license" "${name}"
    local -a licfiles=()
    mapfile -t licfiles < <(
      find "${FRONTEND_DIR}/node_modules/${name}" -maxdepth 1 -type f \
        \( -iname 'LICENSE' -o -iname 'LICENSE.*' \) \
        | LC_ALL=C sort
    )
    licfile="${licfiles[0]:-}"
    copyright="$(first_copyright_line "${licfile}")"
    [[ -n "${copyright}" ]] || fail_unresolved "copyright holder" "${name}"
    echo "- \`${name}\` $(jq -r '.version' "${manifest}") — ${license} — ${copyright}"
  done < <(
    sed -n "s/^@import '\([^']*\)'.*/\1/p; s/^@plugin '\([^']*\)'.*/\1/p" \
      "${FRONTEND_DIR}/src/styles.css" | LC_ALL=C sort -u
  )
}

# --------------------------------------------------------- license-text coverage
#
# The inventory and the license texts are one deliverable. An entry saying a
# component is Apache-2.0, in a document that reproduces no Apache-2.0 text,
# tells a recipient that a license governs what they received and then does not
# give it to them -- which discharges nothing.
#
# The realistic way that happens is not vandalism. A new dependency arrives under
# a license this file does not yet reproduce; the inventory check fails; someone
# regenerates the block; the check goes green; and the archive ships a named
# license with no text behind it. That is this file's own founding defect, one
# level up, and without this check it would be silent.
#
# So an unmapped license fails loudly here, on the same principle the inventory
# side already holds: the script never guesses, and never passes something it
# does not understand. Adding a component under a new license class is therefore
# a deliberate two-part edit -- the mapping below, and the text itself.
#
# Only the missing direction is enforced. A license text left in place after its
# last component is dropped gives a recipient more than is owed, which is not a
# defect worth failing a pull request over; a missing text gives them less, which
# is the whole point of the file.
license_text_sections() {
  case "$1" in
    MIT)          echo "MIT License" ;;
    ISC)          echo "ISC License" ;;
    BSD-2-Clause) echo "BSD 2-Clause License" ;;
    BSD-3-Clause) echo "BSD 3-Clause License" ;;
    Apache-2.0)   echo "Apache License 2.0" ;;
    # dompurify is offered under either license. Collattice receives and
    # redistributes it under Apache-2.0, so the document owes two things: the
    # section recording which arm was taken, and the Apache-2.0 text it points
    # at. Naming both here is what stops the dual entry from being discharged by
    # a narrative paragraph with no license behind it.
    "(MPL-2.0 OR Apache-2.0)")
      echo "Dual-licensed component: DOMPurify"
      echo "Apache License 2.0" ;;
    *) return 1 ;;
  esac
}

# Count the substantive lines under a "## <heading>" section, stopping at the
# next level-2 heading. Fenced blocks are tracked so a ``` -delimited license
# body containing a "## " line could not be read as the end of the section.
section_body_lines() {
  local file="$1" heading="$2"

  awk -v want="## ${heading}" '
    $0 == want          { inside = 1; next }
    !inside             { next }
    /^```/              { fence = !fence; next }
    !fence && /^## /    { inside = 0; next }
    NF                  { n++ }
    END                 { print n + 0 }
  ' "${file}"
}

# The shortest legitimate section in the document is the four-line DOMPurify
# note; every reproduced license text is far longer. Three separates "a heading
# with its body gone" from anything real.
readonly MIN_SECTION_LINES=3

check_license_texts() {
  local inventory="$1" notices="$2"
  local status=0 license mapped heading lines
  local -a checked=()

  while IFS= read -r license; do
    [[ -n "${license}" ]] || continue

    if ! mapped="$(license_text_sections "${license}")"; then
      echo "NOTICES: the inventory names the license '${license}', which this script" >&2
      echo "has no license-text mapping for, so it cannot confirm the text is present." >&2
      echo "Add the license's full text to '${notices}' under a '## ' heading, and add" >&2
      echo "that heading to license_text_sections() in this script." >&2
      status=1
      continue
    fi

    while IFS= read -r heading; do
      [[ -n "${heading}" ]] || continue

      lines="$(section_body_lines "${notices}" "${heading}")"
      if [[ "${lines}" -lt "${MIN_SECTION_LINES}" ]]; then
        echo "NOTICES: '${license}' is named in the inventory, but the '## ${heading}'" >&2
        echo "section of '${notices}' holds ${lines} line(s) of text. A listed license" >&2
        echo "with no text does not discharge the obligation to pass it on." >&2
        status=1
        continue
      fi

      checked+=("${heading}")
    done <<< "${mapped}"
  done < <(
    awk -F ' — ' '/^- `/ && NF >= 3 { print $2 }' "${inventory}" \
      | LC_ALL=C sort -u
  )

  [[ "${status}" -eq 0 ]] || return 1

  # A silently empty result would pass every check above while proving nothing --
  # the failure mode of any scanner is finding nothing and calling it clean.
  if [[ "${#checked[@]}" -eq 0 ]]; then
    echo "NOTICES: no licenses were read out of the inventory at all, so nothing was" >&2
    echo "checked. Either the inventory is empty or its line format has changed." >&2
    return 1
  fi

  local unique
  unique="$(printf '%s\n' "${checked[@]}" | LC_ALL=C sort -u | paste -sd ',' - | sed 's/,/, /g')"
  echo "License texts verified: every license the inventory names is reproduced (${unique})."
}

if [[ -z "${CHECK_FILE}" ]]; then
  emit_inventory
  exit 0
fi

# --check: compare the regenerated inventory against the one the file publishes.
[[ -f "${CHECK_FILE}" ]] || { echo "generate-third-party-notices: '${CHECK_FILE}' is not a file." >&2; exit 2; }

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

emit_inventory > "${TMP_DIR}/expected"

awk -v b="${BEGIN_MARKER}" -v e="${END_MARKER}" '
  $0 == b { inside = 1; next }
  $0 == e { inside = 0; next }
  inside  { print }
' "${CHECK_FILE}" > "${TMP_DIR}/actual"

if [[ ! -s "${TMP_DIR}/actual" ]]; then
  echo "NOTICES: no generated inventory block found in '${CHECK_FILE}'." >&2
  echo "Expected content between ${BEGIN_MARKER} and ${END_MARKER}." >&2
  exit 1
fi

# Both sides are emitted with a trailing blank line boundary; compare ignoring
# leading/trailing blank lines so the marker spacing in the document is free.
strip_blank_edges() {
  sed -e '/./,$!d' "$1" | sed -e :a -e '/^\n*$/{$d;N;};/\n$/ba'
}
strip_blank_edges "${TMP_DIR}/expected" > "${TMP_DIR}/expected.trim"
strip_blank_edges "${TMP_DIR}/actual"   > "${TMP_DIR}/actual.trim"

EXIT_STATUS=0

if diff -u "${TMP_DIR}/actual.trim" "${TMP_DIR}/expected.trim" > "${TMP_DIR}/delta"; then
  echo "Third-party notices verified: the published inventory matches what the build actually bundles."
else
  EXIT_STATUS=1
  echo "NOTICES DRIFT: the component inventory in '${CHECK_FILE}' no longer matches what the build bundles." >&2
  echo "'-' lines are published but no longer bundled; '+' lines are bundled but not published." >&2
  echo >&2
  cat "${TMP_DIR}/delta" >&2
  echo >&2
  echo "Regenerate with:" >&2
  echo "  scripts/generate-third-party-notices.sh <deps-json> frontend <sourcemap-dir>" >&2
  echo "and replace the block between the BEGIN/END markers in ${CHECK_FILE}." >&2
  echo "If a newly bundled component carries a license not already reproduced in that" >&2
  echo "file, add its license text too — the inventory and the license texts are one" >&2
  echo "deliverable, and a listed license with no text does not discharge the obligation." >&2
  echo >&2
fi

# Run against the REGENERATED inventory, and run it even when the inventory
# drifted. The two failures have one cause -- a dependency changed -- and the
# regeneration a drift failure asks for is exactly the step that would otherwise
# turn "license named with no text" green. Reporting both in one run means the
# person fixing it sees the whole job, rather than fixing the block, going green,
# and shipping the gap.
if ! check_license_texts "${TMP_DIR}/expected" "${CHECK_FILE}"; then
  EXIT_STATUS=1
fi

exit "${EXIT_STATUS}"
