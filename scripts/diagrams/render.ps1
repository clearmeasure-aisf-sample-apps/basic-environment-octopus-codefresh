#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Renders the design diagrams (design/diagrams/*.puml) to PNG and records every render in
    design/diagrams/manifest.sha256 (design/diagrams/README.md).

.DESCRIPTION
    Renderer: the first that works, unless one is forced.
      1. docker  The plantuml/plantuml image, pinned by digest, when a Docker daemon answers.
      2. jar     PLANTUML_JAR, or PlantUML from Maven Central, cached under $XDG_CACHE_HOME (or ~/.cache)
                 /platform-diagrams and checked against its SHA-256; needs Java 11 or later.
    Both run PlantUML 1.2026.8. include/palette.puml selects Smetana, the layout engine built into PlantUML,
    so neither needs Graphviz and both draw the same layout.

    The manifest line of a diagram is "<input-sha256> <png-sha256> <name>". The input hash covers the PlantUML
    version, the source and every file in include/, so an edited source or include makes the line stale
    (scripts/diagrams/check.ps1). Lines of diagrams not rendered in this run stay as they were.

    Environment: PLANTUML_JAR, PLANTUML_IMAGE, DOCKER, JAVA.

.PARAMETER Name
    Diagrams to render, as <name> or <name>.puml; every diagram when omitted.

.PARAMETER Renderer
    auto (default), docker or jar.

.EXAMPLE
    pwsh scripts/diagrams/render.ps1 c4-1-system-context
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments)]
    [string[]] $Name = @(),

    [ValidateSet('auto', 'docker', 'jar')]
    [string] $Renderer = 'auto'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'

$PlantUmlVersion = '1.2026.8'
$PlantUmlImage = if ($env:PLANTUML_IMAGE) { $env:PLANTUML_IMAGE } else {
    'plantuml/plantuml:1.2026.8@sha256:d08610df482510844382caa4e016ba2bf7e3231f630f02ee12f250f3416c62b1'
}
$PlantUmlJarUrl = "https://repo1.maven.org/maven2/net/sourceforge/plantuml/plantuml/$PlantUmlVersion/plantuml-$PlantUmlVersion.jar"
$PlantUmlJarSha256 = '0f77e5f769836b3dee340e207fe497c3e4c43e973d559e3c306915da9c32e34c'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$dir = Join-Path $root 'design/diagrams'
$manifest = Join-Path $dir 'manifest.sha256'
$docker = if ($env:DOCKER) { $env:DOCKER } else { 'docker' }
$java = if ($env:JAVA) { $env:JAVA } else { 'java' }

. (Join-Path $PSScriptRoot 'diagram-hash.ps1')

function Get-PlantUmlJar {
    if ($env:PLANTUML_JAR) {
        if (-not (Test-Path -LiteralPath $env:PLANTUML_JAR)) {
            throw "PLANTUML_JAR '$env:PLANTUML_JAR' does not exist"
        }
        return $env:PLANTUML_JAR
    }
    $cacheRoot = if ($env:XDG_CACHE_HOME) { $env:XDG_CACHE_HOME } else { Join-Path $HOME '.cache' }
    $cache = Join-Path $cacheRoot 'platform-diagrams'
    $jar = Join-Path $cache "plantuml-$PlantUmlVersion.jar"
    if (-not (Test-Path -LiteralPath $jar)) {
        New-Item -ItemType Directory -Path $cache -Force | Out-Null
        $download = Join-Path $cache "plantuml-$([Guid]::NewGuid().ToString('N')).part"
        try {
            Invoke-WebRequest -Uri $PlantUmlJarUrl -OutFile $download -MaximumRetryCount 3 -RetryIntervalSec 5
            $actual = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $PlantUmlJarSha256) {
                throw "the downloaded PlantUML jar has SHA-256 $actual, not $PlantUmlJarSha256"
            }
            Move-Item -LiteralPath $download -Destination $jar -Force
        }
        finally {
            if (Test-Path -LiteralPath $download) {
                Remove-Item -LiteralPath $download -Force
            }
        }
    }
    return $jar
}

function Test-DockerDaemon {
    if (-not (Get-Command -Name $docker -CommandType Application -ErrorAction SilentlyContinue)) {
        return $false
    }
    $PSNativeCommandUseErrorActionPreference = $false
    & $docker info *> $null
    return $LASTEXITCODE -eq 0
}

$names = @($Name | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_) } | Where-Object { $_ })
foreach ($n in $names) {
    if (-not (Test-Path -LiteralPath (Join-Path $dir "$n.puml"))) {
        throw "no diagram design/diagrams/$n.puml"
    }
}
if ($names.Count -eq 0) {
    $names = @(Get-ChildItem -LiteralPath $dir -Filter '*.puml' -File | Sort-Object Name | ForEach-Object BaseName)
}
if ($names.Count -eq 0) {
    throw 'no diagrams under design/diagrams'
}
if ($Renderer -eq 'auto') {
    $Renderer = if (Test-DockerDaemon) { 'docker' } else { 'jar' }
}

$sources = @($names | ForEach-Object { "$_.puml" })
Write-Host "render.ps1: $($names.Count) diagram(s) with $Renderer"
switch ($Renderer) {
    'docker' {
        $user = "$(& id -u):$(& id -g)"
        & $docker run --rm --user $user --env HOME=/tmp --volume "${dir}:/data" --workdir /data $PlantUmlImage -tpng @sources
    }
    'jar' {
        if (-not (Get-Command -Name $java -CommandType Application -ErrorAction SilentlyContinue)) {
            throw 'Java 11 or later is required (or a Docker daemon)'
        }
        $jar = Get-PlantUmlJar
        Push-Location -LiteralPath $dir
        try {
            & $java -jar $jar -tpng @sources
        }
        finally {
            Pop-Location
        }
    }
}

$lines = [ordered]@{}
if (Test-Path -LiteralPath $manifest) {
    foreach ($line in [System.IO.File]::ReadAllLines($manifest)) {
        $fields = $line -split '\s+'
        if ($line -match '^\s*(#|$)' -or $fields.Count -lt 3) {
            continue
        }
        $lines[$fields[2]] = $line
    }
}
foreach ($n in $names) {
    $png = Join-Path $dir "$n.png"
    if (-not (Test-Path -LiteralPath $png)) {
        throw "PlantUML wrote no design/diagrams/$n.png"
    }
    $pngHash = (Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash.ToLowerInvariant()
    $lines[$n] = "$(Get-DiagramInputHash -Directory $dir -Name $n -Version $PlantUmlVersion) $pngHash $n"
}
$text = [System.Text.StringBuilder]::new()
[void] $text.Append("# Generated by scripts/diagrams/render.ps1; do not edit. <input-sha256> <png-sha256> <diagram>`n")
[string[]] $keys = @($lines.Keys)
[Array]::Sort($keys, [StringComparer]::Ordinal)
foreach ($key in $keys) {
    if (Test-Path -LiteralPath (Join-Path $dir "$key.puml")) {
        [void] $text.Append("$($lines[$key])`n")
    }
}
[System.IO.File]::WriteAllText($manifest, $text.ToString())
Write-Host "render.ps1: wrote $($names.Count) PNG file(s) and design/diagrams/manifest.sha256"
