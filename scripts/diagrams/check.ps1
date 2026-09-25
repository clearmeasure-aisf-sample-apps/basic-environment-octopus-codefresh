#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Checks the design diagrams (design/diagrams/README.md); 'validate-all.ps1 diagrams' runs it.

.DESCRIPTION
    1. Every design/diagrams/*.puml has a PNG and a manifest line whose input hash matches the source and the
       include files: a source edited without scripts/diagrams/render.ps1 fails.
    2. Every PNG matches the hash in its manifest line (no hand-edited PNG) and has a source.
    3. Every PNG is embedded in at least one Markdown file (design/diagrams/README.md does not count).
    4. Every Markdown image link to a .png under design/diagrams resolves.
    5. Unless -SkipSyntax: with PlantUML at hand (PLANTUML_JAR, or /opt/plantuml.jar as in the plantuml/plantuml
       image, and Java), every source passes 'plantuml -checkonly'; without it the check is skipped locally
       and fails when CI=true. env-checks runs the syntax check as its own step in the plantuml/plantuml image.

    Exit codes: 0 pass, 1 failure.

.PARAMETER Root
    The environment-repo root (default: two levels above this script).

.PARAMETER SkipSyntax
    Leave the PlantUML syntax check to another step.
#>
[CmdletBinding()]
param(
    [string] $Root = '',

    [switch] $SkipSyntax
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$PlantUmlVersion = '1.2026.8'
if (-not $Root) {
    $Root = Join-Path $PSScriptRoot '..' '..'
}
$Root = (Resolve-Path -LiteralPath $Root).Path
$dir = Join-Path $Root 'design/diagrams'
$manifest = Join-Path $dir 'manifest.sha256'
$script:status = 0

. (Join-Path $PSScriptRoot 'diagram-hash.ps1')

function Write-Failure([string] $Message) {
    Write-Host "FAIL diagrams: $Message"
    $script:status = 1
}

function Test-Ci {
    return $env:CI -in @('true', 'TRUE', 'True', '1', 'yes')
}

if (-not (Test-Path -LiteralPath $dir)) {
    Write-Host 'SKIP diagrams: no design/diagrams'
    exit 0
}

$wantInput = @{}
$wantPng = @{}
if (Test-Path -LiteralPath $manifest) {
    foreach ($line in [System.IO.File]::ReadAllLines($manifest)) {
        $fields = $line -split '\s+'
        if ($line -match '^\s*(#|$)' -or $fields.Count -lt 3) {
            continue
        }
        $wantInput[$fields[2]] = $fields[0]
        $wantPng[$fields[2]] = $fields[1]
    }
}
else {
    Write-Failure 'design/diagrams/manifest.sha256 is missing; run scripts/diagrams/render.ps1'
}

# 1 and 2: sources, renders and the manifest agree.
$names = @(Get-ChildItem -LiteralPath $dir -Filter '*.puml' -File | Sort-Object Name | ForEach-Object BaseName)
foreach ($name in $names) {
    $png = Join-Path $dir "$name.png"
    if (-not (Test-Path -LiteralPath $png)) {
        Write-Failure "$name.png is missing; run scripts/diagrams/render.ps1 $name"
        continue
    }
    if (-not $wantInput.ContainsKey($name)) {
        Write-Failure "$name has no manifest line; run scripts/diagrams/render.ps1 $name"
        continue
    }
    if ((Get-DiagramInputHash -Directory $dir -Name $name -Version $PlantUmlVersion) -ne $wantInput[$name]) {
        Write-Failure "$name.puml or an include changed after the last render; run scripts/diagrams/render.ps1 $name"
    }
    if ((Get-FileHash -LiteralPath $png -Algorithm SHA256).Hash.ToLowerInvariant() -ne $wantPng[$name]) {
        Write-Failure "$name.png differs from its render; never edit a PNG by hand"
    }
}
foreach ($name in $wantInput.Keys) {
    if (-not (Test-Path -LiteralPath (Join-Path $dir "$name.puml"))) {
        Write-Failure "manifest line for $name has no source; run scripts/diagrams/render.ps1"
    }
}

# 2 and 3: every PNG has a source and is embedded somewhere.
$pruned = @('.git', 'node_modules', 'bin', 'obj', 'TestResults')
$markdown = [System.Collections.Generic.List[string]]::new()
$pending = [System.Collections.Generic.Stack[string]]::new()
$pending.Push($Root)
while ($pending.Count -gt 0) {
    $current = $pending.Pop()
    foreach ($sub in [System.IO.Directory]::EnumerateDirectories($current)) {
        if ([System.IO.Path]::GetFileName($sub) -notin $pruned) {
            $pending.Push($sub)
        }
    }
    foreach ($file in [System.IO.Directory]::EnumerateFiles($current, '*.md')) {
        if ($file -ne (Join-Path $dir 'README.md')) {
            $markdown.Add($file)
        }
    }
}
$markdownText = @{}
foreach ($file in $markdown) {
    $markdownText[$file] = [System.IO.File]::ReadAllText($file)
}
foreach ($png in (Get-ChildItem -LiteralPath $dir -Filter '*.png' -File | Sort-Object Name)) {
    $name = $png.BaseName
    if (-not (Test-Path -LiteralPath (Join-Path $dir "$name.puml"))) {
        Write-Failure "$name.png has no source $name.puml"
    }
    $embedded = $false
    foreach ($text in $markdownText.Values) {
        if ($text.Contains("diagrams/$name.png")) {
            $embedded = $true
            break
        }
    }
    if (-not $embedded) {
        Write-Failure "$name.png is embedded in no Markdown file"
    }
}

# 4: image links resolve.
foreach ($file in $markdown) {
    foreach ($match in [regex]::Matches($markdownText[$file], '!\[[^\]]*\]\(([^)\s]*diagrams/[^)\s]*\.png)[^)]*\)')) {
        $target = $match.Groups[1].Value
        if ($target -match '^https?://') {
            continue
        }
        $resolved = Join-Path ([System.IO.Path]::GetDirectoryName($file)) $target
        if (-not (Test-Path -LiteralPath $resolved)) {
            Write-Failure "$([System.IO.Path]::GetRelativePath($Root, $file)): image $target does not exist"
        }
    }
}

# 5: syntax, when PlantUML is at hand.
if (-not $SkipSyntax) {
    $jar = if ($env:PLANTUML_JAR) { $env:PLANTUML_JAR } elseif (Test-Path -LiteralPath '/opt/plantuml.jar') { '/opt/plantuml.jar' } else { '' }
    $java = if ($env:JAVA) { $env:JAVA } else { 'java' }
    if ($jar -and (Test-Path -LiteralPath $jar) -and (Get-Command -Name $java -CommandType Application -ErrorAction SilentlyContinue)) {
        if ($names.Count -gt 0) {
            $sources = @($names | ForEach-Object { "$_.puml" })
            Push-Location -LiteralPath $dir
            try {
                $PSNativeCommandUseErrorActionPreference = $false
                $output = & $java -jar $jar -checkonly @sources 2>&1
                if ($LASTEXITCODE -ne 0) {
                    Write-Failure 'PlantUML -checkonly reports a syntax error:'
                    $output | Where-Object { "$_" -notmatch 'JAVA_TOOL_OPTIONS' } | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
                }
            }
            finally {
                $PSNativeCommandUseErrorActionPreference = $true
                Pop-Location
            }
        }
    }
    elseif (Test-Ci) {
        Write-Failure 'PlantUML is not available for the syntax check (set PLANTUML_JAR, run in the plantuml/plantuml image, or pass -SkipSyntax)'
    }
    else {
        Write-Host 'WARN diagrams: PlantUML not found; syntax check skipped locally (fails when CI=true)'
    }
}

if ($script:status -eq 0) {
    Write-Host "PASS diagrams: $($names.Count) diagram(s) rendered, current and embedded"
}
exit $script:status
