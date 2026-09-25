#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Stages lean Docker build contexts for the workorders images (contract §7.5).

.DESCRIPTION
    Inputs: the application checkout (clearmeasure-aisf-sample-apps/20260923-001; the
    working directory, or -Repo) after its Release build, and the image Dockerfiles
    of the environment repo (containers/apps/workorders/, found next to this script, or
    -Containers).

      <out>/ui/          Dockerfile + built/
          built/ is extracted from build/ChurchBulletin.UI.<VERSION>.nupkg exactly as
          build.yml "Publish Release Candidate" does (F6): unzip, locate
          ClearMeasure.Bootcamp.UI.Server.dll, copy its directory, drop the package
          metadata. The Dockerfile is the application repo's root Dockerfile, copied
          verbatim, with OCI labels appended; the file in the application repo never changes.
          -UiSource publish skips the nupkg and runs dotnet publish instead (previews).
      <out>/worker/      Dockerfile (containers/apps/workorders/worker/Dockerfile) + publish/
          dotnet publish src/Worker (Release, --no-build) -> Worker.dll
      <out>/db-migrator/ Dockerfile and migrate.sh (containers/apps/workorders/db-migrator/) + publish/ + scripts/
          dotnet publish src/Database (Release, --no-build) plus src/Database/scripts

    Lean contexts keep src/**/bin and obj, video/ and the Qodana baseline out of the
    Docker build context (the application repo has no .dockerignore).

    Usage: pwsh -NoProfile -File stage-built.ps1 -Version <VERSION> [-Out <dir>] [-Repo <dir>] [-Containers <dir>]
                [-UiSource nupkg|publish] [-Only ui,worker,db-migrator]
    Requires a Release build (`. ./build.ps1; Build`) and, for -UiSource nupkg,
    `Package-Everything` with the same BUILD_BUILDNUMBER; unzip and tar on PATH.

.PARAMETER Version
    VERSION, the package version of the UI nupkg.

.PARAMETER Out
    The contexts folder (default: ${CF_VOLUME_PATH}/image-contexts, else <repo>/build/image-contexts).

.PARAMETER Repo
    The application checkout (default: git rev-parse --show-toplevel).

.PARAMETER Containers
    The image folder of the environment repo (default: containers/apps/workorders, found from this script).

.PARAMETER UiSource
    nupkg (default) or publish.

.PARAMETER Only
    The contexts to stage, a comma-separated list of ui, worker and db-migrator (default: all three).

.OUTPUTS
    The contexts; the log goes to standard error.

.NOTES
    Exit codes: 0 staged; 1 failed (no repository, a missing package, Dockerfile or build output, a failing tool).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [string] $Out = '',
    [string] $Repo = '',
    [string] $Containers = (Join-Path $PSScriptRoot '../../../../containers/apps/workorders'),
    [ValidateSet('nupkg', 'publish', IgnoreCase = $false)]
    [string] $UiSource = 'nupkg',
    [string[]] $Only = @('ui,worker,db-migrator')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$ociSource = 'https://github.com/clearmeasure-aisf-sample-apps/20260923-001'
$uiDll = 'ClearMeasure.Bootcamp.UI.Server.dll'
$executable = [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute'

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("stage-built.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

# Recreates one context folder under the output folder.
function New-ContextFolder([string] $Name) {
    $folder = Join-Path $Out $Name
    if (Test-Path -LiteralPath $folder) {
        Remove-Item -LiteralPath $folder -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Force -Path $folder
    return $folder
}

# The first entry named $Name in a depth-first walk in directory order (find -name ... -print -quit).
function Find-FirstEntry([string] $Folder, [string] $Name) {
    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($Folder)) {
        if ([System.IO.Path]::GetFileName($entry) -ceq $Name) {
            return $entry
        }
        if ([System.IO.Directory]::Exists($entry) -and -not (Get-Item -LiteralPath $entry -Force).LinkTarget) {
            $found = Find-FirstEntry $entry $Name
            if ($found) {
                return $found
            }
        }
    }
    return $null
}

# Copies a file and makes it executable for everyone (install -m 0755).
function Install-Executable([string] $Source, [string] $Target) {
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    [System.IO.File]::SetUnixFileMode($Target, $executable)
}

function Invoke-UiStage([string] $Source, [string] $PackageVersion) {
    $context = New-ContextFolder 'ui'
    $built = Join-Path $context 'built'
    $null = New-Item -ItemType Directory -Force -Path $built

    if ($Source -ceq 'nupkg') {
        $package = "$Repo/build/ChurchBulletin.UI.$PackageVersion.nupkg"
        if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
            Exit-Failure "missing $package; run Package-Everything with BUILD_BUILDNUMBER=$PackageVersion"
        }

        $extract = [System.IO.Directory]::CreateTempSubdirectory('stage-built-').FullName
        $archive = [System.IO.Path]::GetTempFileName()
        try {
            unzip -q -o $package -d $extract
            # Octopus packages built on Windows can carry restrictive modes (build.yml does the same).
            foreach ($item in @(Get-Item -LiteralPath $extract -Force) + @(Get-ChildItem -LiteralPath $extract -Recurse -Force)) {
                $grant = if ($item.PSIsContainer) { [System.IO.UnixFileMode]'UserRead, UserWrite, UserExecute' } else { [System.IO.UnixFileMode]::UserRead }
                if (-not $item.LinkTarget) {
                    $item.UnixFileMode = $item.UnixFileMode -bor $grant
                }
            }

            $dll = Find-FirstEntry $extract $uiDll
            if (-not $dll) {
                Exit-Failure "$uiDll not found in $package"
            }
            # tar keeps modes, links and hidden files, as the Bash stage did.
            tar -C ([System.IO.Path]::GetDirectoryName($dll)) -cf $archive .
            tar -C $built -xf $archive
        }
        finally {
            Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
        }

        # Package metadata never belongs in the image.
        Get-ChildItem -LiteralPath $built -Recurse -Force -File |
            Where-Object { $_.Name -clike '*.nuspec' -or $_.Name -ceq '[Content_Types].xml' -or $_.Name -clike '*.psmdcp' } |
            Remove-Item -Force
        Get-ChildItem -LiteralPath $built -Recurse -Force -Directory |
            Where-Object { $_.Name -ceq '_rels' -or $_.FullName -clike '*/package/services/metadata' } |
            Sort-Object -Property { $_.FullName.Length } -Descending |
            ForEach-Object {
                if (Test-Path -LiteralPath $_.FullName) {
                    Remove-Item -LiteralPath $_.FullName -Recurse -Force
                }
            }
    }
    else {
        dotnet publish "$Repo/src/UI/Server/UI.Server.csproj" --configuration Release --no-restore --no-build --nologo -o $built
    }

    if (-not (Test-Path -LiteralPath (Join-Path $built $uiDll) -PathType Leaf)) {
        Exit-Failure "ui: built/ is missing $uiDll"
    }

    $dockerfile = Join-Path $context 'Dockerfile'
    Copy-Item -LiteralPath "$Repo/Dockerfile" -Destination $dockerfile -Force
    $labels = @(
        ''
        '# --- Appended by codefresh/apps/workorders/scripts/stage-built.ps1 (OCI labels, contract §7.5). ---'
        "# The application repo's Dockerfile is unchanged; only this staged copy carries the labels."
        'ARG VERSION=0.0.0-unset'
        'ARG REVISION=unknown'
        "LABEL org.opencontainers.image.source=`"$ociSource`" \"
        '      org.opencontainers.image.revision="${REVISION}" \'
        '      org.opencontainers.image.version="${VERSION}" \'
        '      org.opencontainers.image.title="workorders/ui-server"'
    )
    [System.IO.File]::AppendAllText($dockerfile, ($labels -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
    Write-Note "ui context ready at $context (source: $Source)"
}

function Invoke-WorkerStage([string] $Images) {
    $context = New-ContextFolder 'worker'
    dotnet publish "$Repo/src/Worker/Worker.csproj" --configuration Release --no-restore --no-build --nologo -o "$context/publish"
    if (-not (Test-Path -LiteralPath "$context/publish/Worker.dll" -PathType Leaf)) {
        Exit-Failure 'worker: publish/ is missing Worker.dll'
    }
    if (-not (Test-Path -LiteralPath "$Images/worker/Dockerfile" -PathType Leaf)) {
        Exit-Failure "worker: missing $Images/worker/Dockerfile"
    }
    Copy-Item -LiteralPath "$Images/worker/Dockerfile" -Destination "$context/Dockerfile" -Force
    Write-Note "worker context ready at $context"
}

function Invoke-MigratorStage([string] $Images) {
    $context = New-ContextFolder 'db-migrator'
    dotnet publish "$Repo/src/Database/Database.csproj" --configuration Release --no-restore --no-build --nologo -o "$context/publish"
    if (-not (Test-Path -LiteralPath "$context/publish/ClearMeasure.Bootcamp.Database.dll" -PathType Leaf)) {
        Exit-Failure 'db-migrator: publish/ is missing ClearMeasure.Bootcamp.Database.dll'
    }
    # The console reads scripts from a directory argument; the image ships them in /app/scripts.
    Copy-Item -LiteralPath "$Repo/src/Database/scripts" -Destination "$context/scripts" -Recurse -Force
    foreach ($file in @('Dockerfile', 'migrate.sh')) {
        if (-not (Test-Path -LiteralPath "$Images/db-migrator/$file" -PathType Leaf)) {
            Exit-Failure "db-migrator: missing $Images/db-migrator/$file"
        }
    }
    Copy-Item -LiteralPath "$Images/db-migrator/Dockerfile" -Destination "$context/Dockerfile" -Force
    Install-Executable "$Images/db-migrator/migrate.sh" "$context/migrate.sh"
    Write-Note "db-migrator context ready at $context"
}

if (-not $Repo) {
    $PSNativeCommandUseErrorActionPreference = $false
    $toplevel = git rev-parse --show-toplevel
    $found = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    if (-not $found) {
        Exit-Failure 'not inside a git repository'
    }
    $Repo = @($toplevel) -join "`n"
}
$Repo = (Resolve-Path -LiteralPath $Repo).ProviderPath

if (-not $Out) {
    $Out = if ($env:CF_VOLUME_PATH) { "$($env:CF_VOLUME_PATH)/image-contexts" } else { "$Repo/build/image-contexts" }
}
$null = New-Item -ItemType Directory -Force -Path $Out
$Out = (Resolve-Path -LiteralPath $Out).ProviderPath

$wanted = ",$($Only -join ','),"
if ($wanted.Contains(',ui,')) {
    Invoke-UiStage -Source $UiSource -PackageVersion $Version
}
if ($wanted.Contains(',worker,')) {
    Invoke-WorkerStage -Images $Containers
}
if ($wanted.Contains(',db-migrator,')) {
    Invoke-MigratorStage -Images $Containers
}
exit 0
