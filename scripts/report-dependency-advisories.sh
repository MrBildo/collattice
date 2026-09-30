#!/usr/bin/env bash
# Report known security advisories against the project's dependencies as a
# visible, NON-FAILING pull-request signal.
#
#   report-dependency-advisories.sh npm <frontend-dir>
#   report-dependency-advisories.sh dotnet <solution-file>
#
# Why this exists: the dependency-advisory check used to run only at release
# time, so a pull request could go fully green while leaving the next release
# blocked -- nothing a pull request ran ever looked at the advisory state. This
# puts that state in front of the reviewer on every pull request instead of at
# the release cut, where a fix is most expensive to sequence.
#
# Why it never fails the build: which severities should block a merge is an
# operator policy decision that has not been made yet. Until it is, findings are
# reported as a warning annotation plus a table in the job summary, and the
# script exits 0. When a threshold is chosen, this is the place to enforce it.
#
# Three outcomes are kept distinct, because a scanner that silently failed looks
# exactly like a clean scan:
#   - advisories found      -> warning annotation + table
#   - scanned, none found   -> notice, with the count of what was scanned
#   - scan could not run    -> warning saying so (never reported as "clean")
# Each clean verdict is printed next to how much was actually scanned, so a scan
# that read nothing cannot pass itself off as a clean result.
#
# Requires: jq, plus npm or the dotnet SDK for the chosen mode. The dotnet mode
# needs the solution already restored (the vulnerability list reads the restore
# output).

set -euo pipefail

usage() {
  echo "usage: $0 npm <frontend-dir> | dotnet <solution-file>" >&2
  exit 2
}

[[ $# -eq 2 ]] || usage
mode="$1"
target="$2"

summary_file="${GITHUB_STEP_SUMMARY:-/dev/null}"

# Some Windows builds of jq end every output line with CRLF; strip the carriage
# return so a count read from jq compares as a number.
jqr() { jq "$@" | tr -d '\r'; }

annotate() {
  # $1 = warning|notice, $2 = title, $3 = message. GitHub Actions turns these
  # lines into annotations on the run; anywhere else they are plain log lines.
  echo "::$1 title=$2::$3"
}

could_not_run() {
  # $1 = ecosystem label, $2 = reason
  annotate warning "$1 advisory check did not run" "$2 -- advisory state is UNKNOWN for this run, not clean."
  {
    echo "### $1"
    echo
    echo "**Could not run:** $2"
    echo
    echo "The advisory state is unknown for this run. This is not a clean result."
    echo
  } >>"$summary_file"
  exit 0
}

report_npm() {
  local dir="$1" label="npm (frontend, production dependencies)"
  [[ -f "$dir/package-lock.json" ]] || could_not_run "$label" "no package-lock.json in $dir"

  local json
  # npm audit exits non-zero when it FINDS advisories, which is the expected
  # finding rather than a failure, so its exit code is not the signal here.
  # Whether it actually ran is decided from the JSON below.
  json="$(cd "$dir" && npm audit --omit=dev --json 2>/dev/null)" || true

  if ! jq -e '.metadata.vulnerabilities.total' >/dev/null 2>&1 <<<"$json"; then
    local reason
    reason="$(jqr -r '.error.summary // empty' 2>/dev/null <<<"$json" || true)"
    could_not_run "$label" "npm audit produced no report${reason:+ ($reason)}"
  fi

  local scanned total
  scanned="$(jqr -r '.metadata.dependencies.prod // 0' <<<"$json")"
  total="$(jqr -r '.metadata.vulnerabilities.total' <<<"$json")"

  if [[ "$scanned" -eq 0 ]]; then
    could_not_run "$label" "npm audit reported 0 production dependencies scanned"
  fi

  if [[ "$total" -eq 0 ]]; then
    annotate notice "$label: no known advisories" "Scanned $scanned production dependencies; 0 advisories."
    {
      echo "### $label"
      echo
      echo "No known advisories. Scanned $scanned production dependencies."
      echo
  } >>"$summary_file"
    return
  fi

  local by_severity table
  by_severity="$(jqr -r '.metadata.vulnerabilities | "critical \(.critical), high \(.high), moderate \(.moderate), low \(.low), info \(.info)"' <<<"$json")"
  table="$(jqr -r '.vulnerabilities | to_entries[] | .value
    | "| \(.name) | \(.severity) | `\(.range)` | \(if .fixAvailable == false then "no" else "yes" end) |"' <<<"$json")" \
    || could_not_run "$label" "npm audit's report could not be parsed"
  annotate warning "$label: $total advisories" "$by_severity -- see the job summary. Non-blocking: no severity threshold has been set yet."

  {
    echo "### $label"
    echo
    echo "**$total** vulnerable package(s) across $scanned production dependencies ($by_severity)."
    echo "Non-blocking: no severity threshold has been set yet."
    echo
    echo "| Package | Severity | Affected range | Fix available |"
    echo "|---|---|---|---|"
    echo "$table"
    echo
  } >>"$summary_file"

  echo "npm: $total vulnerable package(s) across $scanned production dependencies ($by_severity)"
}

report_dotnet() {
  local solution="$1" label=".NET (backend, including transitive packages)"
  [[ -f "$solution" ]] || could_not_run "$label" "solution file not found: $solution"

  local json err rc=0
  err="$(mktemp)"
  json="$(dotnet list "$solution" package --vulnerable --include-transitive --format json 2>"$err")" || rc=$?

  if [[ $rc -ne 0 ]] || ! jq -e '.projects' >/dev/null 2>&1 <<<"$json"; then
    local detail
    detail="$(head -c 400 "$err" | tr '\r\n' '  ')"
    rm -f "$err"
    could_not_run "$label" "dotnet list package exited $rc${detail:+: $detail}"
  fi
  rm -f "$err"

  # A project the tool could not read shows up under "problems" rather than as a
  # failure; treat that as an incomplete scan, not as a clean one.
  local problems
  problems="$(jqr -r '[.problems // [] | .[] | .text] | join("; ")' <<<"$json")"
  if [[ -n "$problems" ]]; then
    could_not_run "$label" "the scan reported problems: $problems"
  fi

  local projects rows count
  projects="$(jqr -r '.projects | length' <<<"$json")"
  if [[ "$projects" -eq 0 ]]; then
    could_not_run "$label" "the solution listed 0 projects"
  fi

  rows="$(jqr -r '
    .projects[]
    | (.path | split("/") | last | sub("\\.csproj$"; "")) as $project
    | (.frameworks // [])[]
    | ((.topLevelPackages // []) | map(. + {kind: "direct"}))
      + ((.transitivePackages // []) | map(. + {kind: "transitive"}))
    | .[]
    | . as $pkg
    | .vulnerabilities[]
    | "| \($project) | \($pkg.id) \($pkg.resolvedVersion) | \($pkg.kind) | \(.severity) | \(.advisoryurl) |"
  ' <<<"$json" | sort -u)" || could_not_run "$label" "the vulnerability report could not be parsed"

  if [[ -z "$rows" ]]; then
    annotate notice "$label: no known advisories" "Scanned $projects projects; 0 advisories."
    {
      echo "### $label"
      echo
      echo "No known advisories. Scanned $projects projects."
      echo
  } >>"$summary_file"
    return
  fi

  count="$(grep -c '^|' <<<"$rows")"
  local affected
  affected="$(cut -d'|' -f2 <<<"$rows" | sed 's/^ *//; s/ *$//' | sort -u | paste -sd, - | sed 's/,/, /g')"
  annotate warning "$label: $count advisories" "In: $affected -- see the job summary. Non-blocking: no severity threshold has been set yet."

  {
    echo "### $label"
    echo
    echo "**$count** advisory match(es) across $projects projects (affected: $affected)."
    echo "Non-blocking: no severity threshold has been set yet. A project that is not"
    echo "part of the release archive (for example the local orchestration host) is"
    echo "listed too; check whether a package actually ships before treating it as urgent."
    echo
    echo "| Project | Package | Reference | Severity | Advisory |"
    echo "|---|---|---|---|---|"
    echo "$rows"
    echo
  } >>"$summary_file"

  echo ".NET: $count advisory match(es) across $projects projects (affected: $affected)"
}

case "$mode" in
  npm) report_npm "$target" ;;
  dotnet) report_dotnet "$target" ;;
  *) usage ;;
esac

exit 0
