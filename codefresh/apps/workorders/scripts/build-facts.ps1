#!/usr/bin/env pwsh
#Requires -Version 7.4

<#
.SYNOPSIS
    Step image_reuse of workorders/release: writes build-facts.json, the record of the build that the released UI
    image carries and the deployed app answers at GET /_build (the health dashboard's "Code" card reads it).

.DESCRIPTION
    The file is written into the staged UI image context (<image-contexts>/ui/built/, which the app repository's
    Dockerfile copies to /app, the content root), after the gate has passed and before the image is built. The app
    serves it unchanged, with the version of its own assembly.

    Every fact is read from where this build already has it; nothing is measured twice and nothing is estimated.
    A fact this build does not have is null:

      version     VERSION (prepare.ps1; the same value build.ps1 stamps into the assemblies as BUILD_BUILDNUMBER)
      commit      CF_REVISION, else HEAD of the application checkout
      commitUrl   https://github.com/<CF_REPO_OWNER>/<CF_REPO_NAME>/commit/<commit>
      builtAt     the time this script runs (UTC): the moment the record is sealed, just before the image build
      buildUrl    CF_BUILD_URL
      code        tracked source files of the application checkout (git ls-files): non-blank lines and files per
                  language, by file extension; generated and vendored files are left out. No build output holds this
                  number, so it is counted here, without any tool beside git.
      tests       tests that ran (passed or failed; skipped ones are not counted), per suite, from this build's TRX
                  files (trx-summary.ps1 -PassThru, the reader the gate summary and the release notes use): unit
                  and integration from the gate build_sql (else
                  build_sqlite, never both: the suites are the same), acceptance from the gate acceptance. A suite
                  that did not run in this build is null (acceptance unless RELEASE_ACCEPTANCE=true).
      coverage    the Cobertura files coverlet wrote for the unit and integration runs of build_sql
                  (<artifacts>/build_sql/test/**/coverage.cobertura.xml), merged per line
      complexity  the cyclomatic complexity coverlet records for every method in the same files
      crap        the application's own CRAP audit of build_sql (<artifacts>/build_sql/crap-metrics/
                  crap-by-file.json and crap-production-violations.json): worst production score, the gate's
                  threshold, production methods over it
      analysis    qodanaProblems: results of this build's Qodana scan (<artifacts>/qodana/qodana.sarif.json) that are
                  new or unchanged against the baseline. Null when the scan did not run here: with
                  CI_TREE_VERIFIED=true the release does not re-run Qodana, and the committed baseline is not a scan
                  of this commit.

    A section that cannot be read is null and a warning in the log; the script still writes the record. Only a missing
    VERSION or an output file that cannot be written fails it, and the step ignores that failure: the image is then
    built without a record and the app answers its version only.

    Environment: VERSION, CF_REVISION, CF_BUILD_URL, CF_REPO_OWNER, CF_REPO_NAME, ARTIFACTS_DIR.

    Usage (Codefresh step image_reuse, in the application checkout):
      pwsh -NoProfile -File <env-repo>/codefresh/apps/workorders/scripts/build-facts.ps1 -Out <image-contexts>/ui/built/build-facts.json

.PARAMETER Out
    The file to write (its folder is created).

.PARAMETER ArtifactsDir
    The build's artifact folder (default: ARTIFACTS_DIR).

.PARAMETER Repo
    The application checkout (default: the current folder).

.OUTPUTS
    The file; the log goes to standard error.

.NOTES
    Exit codes: 0 written; 1 failed (no VERSION, the file cannot be written).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Out,
    [string] $ArtifactsDir = $(if ($env:ARTIFACTS_DIR) { $env:ARTIFACTS_DIR } else { '' }),
    [string] $Repo = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Suite folder of build.ps1 (build/test/<suite>) -> the test kind of the record.
$SuiteKinds = [ordered]@{ UnitTests = 'unit'; IntegrationTests = 'integration'; AcceptanceTests = 'acceptance' }
# The gate folders under ARTIFACTS_DIR whose runs are read, in order: the same suites run in several gates, and each
# kind is counted once, from the first gate that has it.
$TestGates = @('build_sql', 'build_sqlite', 'acceptance')
$MeasuredGates = @('build_sql', 'build_sqlite')

# Languages counted as code, by file extension.
$LanguageByExtension = @{
    '.cs' = 'C#'; '.csx' = 'C#'
    '.razor' = 'Razor'; '.cshtml' = 'Razor'
    '.ts' = 'TypeScript'; '.tsx' = 'TypeScript'
    '.js' = 'JavaScript'; '.jsx' = 'JavaScript'; '.mjs' = 'JavaScript'; '.cjs' = 'JavaScript'
    '.css' = 'CSS'; '.scss' = 'CSS'; '.sass' = 'CSS'; '.less' = 'CSS'
    '.html' = 'HTML'; '.htm' = 'HTML'
    '.sql' = 'SQL'
    '.ps1' = 'PowerShell'; '.psm1' = 'PowerShell'; '.psd1' = 'PowerShell'
    '.sh' = 'Shell'; '.bash' = 'Shell'
    '.py' = 'Python'
    '.proto' = 'Protocol Buffers'
    '.yml' = 'YAML'; '.yaml' = 'YAML'
    '.bicep' = 'Bicep'
}

# Not written by hand: build output, packages, vendored libraries, minified and generated files.
$GeneratedOrVendored = [regex]::new(
    '(^|/)(bin|obj|node_modules|generated)/|(^|/)wwwroot/lib/|\.min\.[^/]+$|\.designer\.cs$|\.g(\.i)?\.cs$|modelsnapshot\.cs$',
    [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')

# One match per line that has anything but white space.
$NonBlankLine = [regex]::new('(?m)^[^\S\n]*\S', [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("build-facts.ps1: $Message")
}

function Exit-Failure([string] $Message) {
    Write-Note $Message
    exit 1
}

# An environment value, or '' when it is unset, 'none' or an unresolved Codefresh ${{...}} expression.
function Get-Setting([string] $Name) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if (-not $value -or $value -ceq 'none' -or $value.Contains('${{')) {
        return ''
    }
    return $value.Trim()
}

# The folder of a gate under the artifact folder, or '' when this build has none.
function Get-GateFolder([string] $Artifacts, [string] $Gate, [string] $Child) {
    if (-not $Artifacts) {
        return ''
    }
    $folder = Join-Path (Join-Path $Artifacts $Gate) $Child
    if (Test-Path -LiteralPath $folder -PathType Container) {
        return $folder
    }
    return ''
}

# Reads an XML file without fetching or expanding a DTD, whatever the file declares.
function Read-XmlFile([string] $Path) {
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally {
        $reader.Dispose()
    }
}

# The value of a property whatever the case of its name: the CRAP reports mix camelCase and PascalCase.
function Get-JsonValue($Map, [string] $Name) {
    if ($Map -isnot [System.Collections.IDictionary]) {
        return $null
    }
    foreach ($key in $Map.Keys) {
        if ($key -ieq $Name) {
            return $Map[$key]
        }
    }
    return $null
}

function Read-JsonFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -AsHashtable -Depth 100
}

function Get-Commit([string] $Checkout) {
    $commit = Get-Setting 'CF_REVISION'
    if ($commit -cmatch '^[0-9a-f]{40}$') {
        return $commit
    }
    if (-not (Get-Command -Name git -CommandType Application -ErrorAction SilentlyContinue)) {
        return ''
    }
    $PSNativeCommandUseErrorActionPreference = $false
    $head = (@(& git -C $Checkout rev-parse HEAD 2>$null) -join '').Trim()
    $found = $LASTEXITCODE -eq 0
    $PSNativeCommandUseErrorActionPreference = $true
    if ($found -and $head -cmatch '^[0-9a-f]{40}$') {
        return $head
    }
    return ''
}

function Get-CodeFact([string] $Checkout) {
    $tracked = @(& git -C $Checkout -c core.quotepath=false ls-files)
    $byLanguage = @{}
    foreach ($relativePath in $tracked) {
        if (-not $relativePath -or $GeneratedOrVendored.IsMatch($relativePath)) {
            continue
        }
        $language = $LanguageByExtension[[System.IO.Path]::GetExtension($relativePath).ToLowerInvariant()]
        if (-not $language) {
            continue
        }
        $path = Join-Path $Checkout $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        if (-not $byLanguage.ContainsKey($language)) {
            $byLanguage[$language] = @{ lines = 0; files = 0 }
        }
        $byLanguage[$language].lines += $NonBlankLine.Matches([System.IO.File]::ReadAllText($path)).Count
        $byLanguage[$language].files += 1
    }
    if ($byLanguage.Count -eq 0) {
        return $null
    }

    # Largest first; the name decides between equals, so the order never depends on the machine.
    $languages = @(
        $byLanguage.GetEnumerator() |
            Sort-Object -Property @{ Expression = { $_.Value.lines }; Descending = $true }, @{ Expression = { $_.Key } } |
            ForEach-Object { [ordered]@{ name = $_.Key; lines = [int] $_.Value.lines; files = [int] $_.Value.files } }
    )
    return [ordered]@{
        linesOfCode = [int] ($languages | ForEach-Object { $_.lines } | Measure-Object -Sum).Sum
        files       = [int] ($languages | ForEach-Object { $_.files } | Measure-Object -Sum).Sum
        languages   = $languages
    }
}

# Tests that ran to a verdict (passed or failed), per kind: a skipped test is in a TRX file's total but did not run,
# and the VSTest logger leaves the notExecuted counter at 0 for it, so the total is not used. A kind comes from one
# gate only; a kind whose gate holds an unreadable TRX file is null, because the files that could be read would
# understate it.
function Get-TestFact([string] $Artifacts) {
    if (-not $Artifacts -or -not (Test-Path -LiteralPath $Artifacts -PathType Container)) {
        return $null
    }
    $runs = @(& (Join-Path $PSScriptRoot 'trx-summary.ps1') -Path $Artifacts -PassThru)
    $counts = @{}
    foreach ($gate in $TestGates) {
        $inGate = @{}
        $unreadable = @{}
        foreach ($run in $runs) {
            $segments = @($run.Path -split '/')
            if ($segments[0] -cne $gate) {
                continue
            }
            $suite = @($segments | Where-Object { $SuiteKinds.Contains($_) } | Select-Object -First 1)
            if ($suite.Count -eq 0) {
                continue
            }
            $kind = $SuiteKinds[$suite[0]]
            if (-not $run.Readable) {
                $unreadable[$kind] = $true
                continue
            }
            $inGate[$kind] = [int] $inGate[$kind] + $run.Passed + $run.Failed
        }
        foreach ($kind in @($inGate.Keys) + @($unreadable.Keys) | Select-Object -Unique) {
            if ($counts.ContainsKey($kind)) {
                continue
            }
            if ($unreadable.ContainsKey($kind)) {
                Write-Note "WARN tests: a TRX file of the $kind tests in $gate is unreadable; $kind is null"
                $counts[$kind] = $null
            }
            else {
                $counts[$kind] = [int] $inGate[$kind]
            }
        }
    }
    if (@($counts.Values | Where-Object { $null -ne $_ }).Count -eq 0) {
        return $null
    }
    return [ordered]@{
        unit        = $counts['unit']
        integration = $counts['integration']
        acceptance  = $counts['acceptance']
    }
}

# The Cobertura files of the first measured gate that has any. The unit and the integration run instrument the same
# assemblies: a line counts once, covered when either run hit it; of a line's branches, as many count as the run
# that covered most of them.
function Read-Cobertura([string] $Artifacts) {
    $files = @()
    foreach ($gate in $MeasuredGates) {
        $folder = Get-GateFolder $Artifacts $gate 'test'
        if (-not $folder) {
            continue
        }
        $files = @(Get-ChildItem -LiteralPath $folder -Recurse -File -Filter 'coverage.cobertura.xml' | Sort-Object -Property FullName)
        if ($files.Count -gt 0) {
            break
        }
    }
    if ($files.Count -eq 0) {
        return $null
    }

    $lineHits = [System.Collections.Generic.Dictionary[string, bool]]::new()
    $branchesCovered = [System.Collections.Generic.Dictionary[string, int]]::new()
    $branchesTotal = [System.Collections.Generic.Dictionary[string, int]]::new()
    $complexity = [System.Collections.Generic.Dictionary[string, int]]::new()
    $conditions = [regex]::new('\((\d+)/(\d+)\)')
    $invariant = [System.Globalization.CultureInfo]::InvariantCulture

    foreach ($file in $files) {
        $document = Read-XmlFile $file.FullName
        foreach ($class in $document.SelectNodes('//class')) {
            $classKey = "$($class.GetAttribute('filename').Replace('\', '/'))|$($class.GetAttribute('name'))"

            foreach ($line in $class.SelectNodes('lines/line')) {
                $key = "$classKey|$($line.GetAttribute('number'))"
                $hit = $line.GetAttribute('hits') -cne '0'
                $known = $false
                if (-not $lineHits.TryGetValue($key, [ref] $known) -or ($hit -and -not $known)) {
                    $lineHits[$key] = $hit
                }

                $match = $conditions.Match($line.GetAttribute('condition-coverage'))
                if (-not $match.Success) {
                    continue
                }
                $covered = [int] $match.Groups[1].Value
                $total = [int] $match.Groups[2].Value
                $knownCount = 0
                if (-not $branchesCovered.TryGetValue($key, [ref] $knownCount) -or $covered -gt $knownCount) {
                    $branchesCovered[$key] = $covered
                }
                if (-not $branchesTotal.TryGetValue($key, [ref] $knownCount) -or $total -gt $knownCount) {
                    $branchesTotal[$key] = $total
                }
            }

            foreach ($method in $class.SelectNodes('methods/method')) {
                $value = $method.GetAttribute('complexity')
                if (-not $value) {
                    continue
                }
                $methodKey = "$classKey|$($method.GetAttribute('name'))|$($method.GetAttribute('signature'))"
                $complexity[$methodKey] = [int] [double]::Parse($value, $invariant)
            }
        }
    }

    return @{
        lines           = $lineHits.Count
        linesCovered    = @($lineHits.Values | Where-Object { $_ }).Count
        branches        = [int] ($branchesTotal.Values | Measure-Object -Sum).Sum
        branchesCovered = [int] ($branchesCovered.Values | Measure-Object -Sum).Sum
        complexity      = @($complexity.Values)
    }
}

function Get-CoverageFact($Cobertura) {
    if ($null -eq $Cobertura -or $Cobertura.lines -eq 0) {
        return $null
    }
    return [ordered]@{
        linePercent   = [Math]::Round(100.0 * $Cobertura.linesCovered / $Cobertura.lines, 1)
        branchPercent = $(if ($Cobertura.branches -gt 0) { [Math]::Round(100.0 * $Cobertura.branchesCovered / $Cobertura.branches, 1) } else { $null })
    }
}

function Get-ComplexityFact($Cobertura) {
    if ($null -eq $Cobertura -or $Cobertura.complexity.Count -eq 0) {
        return $null
    }
    $measured = $Cobertura.complexity | Measure-Object -Average -Maximum
    return [ordered]@{
        average = [Math]::Round([double] $measured.Average, 1)
        max     = [int] $measured.Maximum
        methods = [int] $measured.Count
    }
}

# rollup-file-scores.csx of the application writes both files: scores per file (production or not), and the
# production methods over the gate's threshold (scripts/crap/crap-gate-threshold.json).
function Get-CrapFact([string] $Artifacts) {
    $byFile = $null
    $violations = $null
    foreach ($gate in $MeasuredGates) {
        $folder = Get-GateFolder $Artifacts $gate 'crap-metrics'
        if (-not $folder) {
            continue
        }
        $byFile = Read-JsonFile (Join-Path $folder 'crap-by-file.json')
        $violations = Read-JsonFile (Join-Path $folder 'crap-production-violations.json')
        if ($null -ne $byFile -or $null -ne $violations) {
            break
        }
    }
    if ($null -eq $byFile -and $null -eq $violations) {
        return $null
    }

    $max = $null
    $threshold = $null
    $overThreshold = $null
    if ($null -ne $byFile) {
        $production = @(Get-JsonValue $byFile 'files' | Where-Object { Get-JsonValue $_ 'isProduction' })
        if ($production.Count -gt 0) {
            $max = [Math]::Round([double] ($production | ForEach-Object { Get-JsonValue $_ 'maxCrap' } | Measure-Object -Maximum).Maximum, 1)
            $overThreshold = [int] ($production | ForEach-Object { Get-JsonValue $_ 'crappyMethodCount' } | Measure-Object -Sum).Sum
        }
        $threshold = Get-JsonValue $byFile 'threshold'
    }
    if ($null -ne $violations) {
        $gateThreshold = Get-JsonValue $violations 'threshold'
        $violationCount = Get-JsonValue $violations 'violationCount'
        if ($null -ne $gateThreshold) {
            $threshold = $gateThreshold
        }
        if ($null -ne $violationCount) {
            $overThreshold = [int] $violationCount
        }
    }
    if ($null -eq $max -and $null -eq $threshold -and $null -eq $overThreshold) {
        return $null
    }
    return [ordered]@{
        max           = $max
        threshold     = $threshold
        overThreshold = $overThreshold
    }
}

# The problems the code has now, as this build's own scan saw them: new ones and those the baseline already knew;
# "absent" ones are gone. The qodana step writes its results to <artifacts>/qodana; a release that reused the CI
# result ran no scan and has no such file.
function Get-AnalysisFact([string] $Artifacts) {
    if (-not $Artifacts) {
        return $null
    }
    $sarif = Read-JsonFile (Join-Path (Join-Path $Artifacts 'qodana') 'qodana.sarif.json')
    $runs = Get-JsonValue $sarif 'runs'
    if ($null -eq $runs) {
        return $null
    }
    $problems = 0
    foreach ($run in @($runs)) {
        foreach ($result in @(Get-JsonValue $run 'results')) {
            if ($null -eq $result) {
                continue
            }
            if ((Get-JsonValue $result 'baselineState') -in @($null, 'new', 'unchanged')) {
                $problems++
            }
        }
    }
    return [ordered]@{ qodanaProblems = $problems }
}

# One section of the record: null with a note when this build has no input for it or it cannot be read.
function Get-Section([string] $Name, [scriptblock] $Read) {
    try {
        $value = & $Read
    }
    catch {
        Write-Note "WARN ${Name}: could not be read: $($_.Exception.Message)"
        return $null
    }
    if ($null -eq $value) {
        Write-Note "SKIP ${Name}: this build has no input for it"
        return $null
    }
    Write-Note "PASS $Name"
    return $value
}

$version = Get-Setting 'VERSION'
if (-not $version) {
    Exit-Failure 'VERSION is not set'
}
$checkout = if ($Repo) { $Repo } else { $PWD.ProviderPath }
$artifacts = $ArtifactsDir
$owner = Get-Setting 'CF_REPO_OWNER'
$name = Get-Setting 'CF_REPO_NAME'
if (-not $owner) { $owner = 'clearmeasure-aisf-sample-apps' }
if (-not $name) { $name = '20260923-001' }
$commit = Get-Commit $checkout
$buildUrl = Get-Setting 'CF_BUILD_URL'
if ($buildUrl -cnotmatch '^https?://\S+$') {
    $buildUrl = ''
}

$cobertura = Get-Section 'coverage reports' { Read-Cobertura $artifacts }
$facts = [ordered]@{
    version    = $version
    commit     = $(if ($commit) { $commit } else { $null })
    commitUrl  = $(if ($commit) { "https://github.com/$owner/$name/commit/$commit" } else { $null })
    builtAt    = [datetimeoffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    buildUrl   = $(if ($buildUrl) { $buildUrl } else { $null })
    code       = Get-Section 'code' { Get-CodeFact $checkout }
    tests      = Get-Section 'tests' { Get-TestFact $artifacts }
    coverage   = Get-Section 'coverage' { Get-CoverageFact $cobertura }
    complexity = Get-Section 'complexity' { Get-ComplexityFact $cobertura }
    crap       = Get-Section 'crap' { Get-CrapFact $artifacts }
    analysis   = Get-Section 'analysis' { Get-AnalysisFact $artifacts }
}

# Written beside the target and moved into place, so that a failure never leaves half a record in the image context.
$full = [System.IO.Path]::GetFullPath($Out, $PWD.ProviderPath)
$partial = "$full.partial"
try {
    $null = New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($full))
    [System.IO.File]::WriteAllText($partial, (($facts | ConvertTo-Json -Depth 20) + "`n"), [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::Move($partial, $full, $true)
}
catch {
    Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue
    Exit-Failure "cannot write ${Out}: $($_.Exception.Message)"
}
Write-Note "wrote $Out (version $version, commit $(if ($commit) { $commit.Substring(0, 7) } else { 'unknown' }))"
exit 0
