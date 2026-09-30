$ErrorActionPreference = 'Stop'

# Detected from $args (not a param block) so the `irm ... | iex` install path,
# which cannot forward named parameters, is unaffected.
$DryRun = ($args -contains '-DryRun') -or ($args -contains '--dry-run')

$Repo = 'MrBildo/collattice'
$ArtifactName = 'collattice-win-x64'

# Fresh vs. existing install detection. A brand-new install uses the current
# (Collattice) directory and database name. An install already present under the
# earlier name is detected and kept exactly where it is -- its data never moves, so
# a fresh install can never land beside and orphan an operator's real database.
# Migrating an existing install onto the new name is a separate, later step.
$oldInstallDir = Join-Path $env:LOCALAPPDATA 'Collaboard'
$newInstallDir = Join-Path $env:LOCALAPPDATA 'Collattice'

if ((Test-Path $oldInstallDir) -and ((Test-Path (Join-Path $oldInstallDir 'appsettings.json')) -or (Test-Path (Join-Path (Join-Path $oldInstallDir 'data') 'collaboard.db')))) {
    $InstallDir = $oldInstallDir
    $DbFileName = 'collaboard.db'
    $InstallKind = 'existing'
}
else {
    $InstallDir = $newInstallDir
    $DbFileName = 'collattice.db'
    $InstallKind = 'fresh'
}

$dbPath = Join-Path (Join-Path $InstallDir 'data') $DbFileName

# -DryRun: report the resolved install directory and database path, then exit
# without downloading or touching the filesystem -- so an operator (or CI) can
# confirm up front which location an install would use (fresh vs. detected-existing).
if ($DryRun) {
    Write-Host "install-kind: $InstallKind"
    Write-Host "install-dir: $InstallDir"
    Write-Host "db-path: $dbPath"
    exit 0
}

Write-Host "Install directory: $InstallDir"
if ($InstallKind -eq 'existing') {
    Write-Host 'Existing installation detected; keeping it in place, no data moved.'
}
Write-Host

# COLLATTICE_VERSION optionally pins the release to install (for example v3.1.0,
# or a pre-release such as v3.2.0-rc.1). Unset, the latest release is installed.
# GitHub's "latest release" never points at a pre-release, so the pin is also how
# a release candidate gets installed.
if ($env:COLLATTICE_VERSION) {
    if ($env:COLLATTICE_VERSION -cnotmatch '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') {
        Write-Error "COLLATTICE_VERSION '$($env:COLLATTICE_VERSION)' is not a release tag (expected vX.Y.Z or vX.Y.Z-<pre-release>)."
        exit 1
    }
    $tag = $env:COLLATTICE_VERSION
    Write-Host "Requested release: $tag"
} else {
    # Get latest release tag
    Write-Host 'Fetching latest release...'
    $release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest"
    $tag = $release.tag_name

    if (-not $tag) {
        Write-Error 'Failed to fetch latest release.'
        exit 1
    }

    Write-Host "Latest release: $tag"
}

# Download artifact
$downloadUrl = "https://github.com/$Repo/releases/download/$tag/$ArtifactName.zip"
$tempFile = Join-Path ([IO.Path]::GetTempPath()) "$ArtifactName.zip"

Write-Host "Downloading $ArtifactName.zip..."
Invoke-WebRequest -Uri $downloadUrl -OutFile $tempFile -UseBasicParsing

# Extract to temp location first, then merge (preserving data/ and operator config)
Write-Host "Extracting to $InstallDir..."
$tempExtract = Join-Path ([IO.Path]::GetTempPath()) 'collattice-extract'
if (Test-Path $tempExtract) {
    Remove-Item $tempExtract -Recurse -Force
}

Expand-Archive -Path $tempFile -DestinationPath $tempExtract -Force

# Release archives are flat (contract items at the archive root, no wrapping
# directory -- enforced by publish.yml's "Verify archive contents" step), so the
# files land directly in $tempExtract. The $inner probe is retained as a
# defensive fallback for any older archive that still wraps its contents.
$inner = Join-Path $tempExtract $ArtifactName
$sourceDir = if (Test-Path $inner) { $inner } else { $tempExtract }

# Ensure install dir exists
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}

# Copy new files over existing, preserving data/ and appsettings.json (the operator-editable
# config — smart-merged below via Collabot.Collattice.Api --merge-appsettings, #235).
Get-ChildItem $sourceDir | ForEach-Object {
    $dest = Join-Path $InstallDir $_.Name
    # Skip data directory (contains the database)
    if ($_.Name -eq 'data') { return }
    # Carve out appsettings.json — merged below, never overwritten wholesale.
    if ($_.Name -eq 'appsettings.json') { return }
    if (Test-Path $dest) {
        Remove-Item $dest -Recurse -Force
    }
    Move-Item $_.FullName -Destination $dest -Force
}

# appsettings.json: smart-merge on upgrade, seed on first install (#235).
#
# First install: copy the archive's shipped appsettings.json into place AND seed the
# sidecar baseline (appsettings.shipped.json) so the next upgrade has a reference for
# distinguishing operator-edited keys from untouched defaults. Then seed an absolute
# ConnectionStrings:Board into appsettings.json (Collattice requires it; no default).
#
# Upgrade: invoke `Collabot.Collattice.Api.exe --merge-appsettings <shipped> <ondisk> --baseline
# <baseline>` to perform the three-way merge.
$shippedSrc = Join-Path $sourceDir 'appsettings.json'
$appsettingsDst = Join-Path $InstallDir 'appsettings.json'
$baselineDst = Join-Path $InstallDir 'appsettings.shipped.json'
$collatticeBin = Join-Path $InstallDir 'Collabot.Collattice.Api.exe'

if (-not (Test-Path $appsettingsDst)) {
    # First install — copy shipped → appsettings.json AND seed the baseline sidecar
    # (#235 C-3: required so the next upgrade is not stuck in conservative mode).
    Copy-Item -Path $shippedSrc -Destination $appsettingsDst -Force
    Copy-Item -Path $shippedSrc -Destination $baselineDst -Force
    Write-Host "Seeded $appsettingsDst and $baselineDst from shipped defaults"

    # Seed the absolute ConnectionStrings:Board into appsettings.json. Collattice requires
    # this key with no default. PowerShell's native ConvertFrom-Json / ConvertTo-Json
    # handles this without the python3/awk fallback chain the bash installer needs.
    try {
        $settings = Get-Content -Path $appsettingsDst -Raw | ConvertFrom-Json
        if (-not $settings.PSObject.Properties.Match('ConnectionStrings').Count) {
            $settings | Add-Member -MemberType NoteProperty -Name 'ConnectionStrings' -Value ([pscustomobject]@{ Board = "Data Source=$dbPath" })
        }
        elseif ([string]::IsNullOrWhiteSpace($settings.ConnectionStrings.Board)) {
            $settings.ConnectionStrings.Board = "Data Source=$dbPath"
        }
        $settings | ConvertTo-Json -Depth 10 | Set-Content -Path $appsettingsDst -Encoding utf8
        Write-Host "Seeded ConnectionStrings:Board = Data Source=$dbPath"
    }
    catch {
        Write-Warning "Could not seed ConnectionStrings:Board into ${appsettingsDst}: $($_.Exception.Message)"
        Write-Warning "Edit $appsettingsDst manually, setting ConnectionStrings:Board to ""Data Source=$dbPath""."
    }
}
else {
    # Upgrade — invoke the C# merge subcommand. The binary was just unpacked above, so
    # it is guaranteed to be the version-correct artifact that ships --merge-appsettings.
    # Every skip path inside the subcommand is loud + non-zero exit (#235 C-4 / AC-3),
    # so a failure here surfaces and $ErrorActionPreference = 'Stop' aborts the installer
    # rather than silently leaving an unmerged appsettings.json behind.
    & $collatticeBin --merge-appsettings $shippedSrc $appsettingsDst --baseline $baselineDst
    if ($LASTEXITCODE -ne 0) {
        Write-Error "merge-appsettings failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }
    Write-Host "Smart-merged $appsettingsDst (operator edits preserved, new shipped keys added)"
}

Remove-Item $tempExtract -Recurse -Force

# Clean up
Remove-Item $tempFile -Force

Write-Host
Write-Host "Collattice installed to $InstallDir"
Write-Host

# Suggest adding to PATH
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$InstallDir*") {
    Write-Host 'To add Collattice to your PATH, run:'
    Write-Host "  [Environment]::SetEnvironmentVariable('Path', `"$InstallDir;`$env:Path`", 'User')"
    Write-Host
}

Write-Host 'To start Collattice:'
Write-Host "  & '$InstallDir\Collabot.Collattice.Api.exe'"
Write-Host
Write-Host 'Then open http://localhost:8080 in your browser.'
