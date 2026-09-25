#!/usr/bin/env pwsh
#Requires -Version 7.4
<#
.SYNOPSIS
    Creates or replaces the platform's Codefresh projects, pipelines, contexts and registry integrations by name
    (ADR-IR34 §11.7.1 item 6; contract §7.0 "Codefresh").

.DESCRIPTION
    Idempotent: a second run with the same inputs changes nothing but the replaced payloads.

    Modes (exactly one):
      --preview    Projects and every pipeline (platform-env and every app), each without triggers, cron triggers,
                   contexts or variables, marked "preview". No context, no registry integration.
      --full       Projects and every pipeline from the full specs (triggers, crons, contexts, variables), plus the
                   contexts and registry integrations declared in codefresh/platform/integrations.yaml and
                   codefresh/apps/*/integrations.yaml.
      --app <app>  Project(s) and pipelines of one app (codefresh/apps/<app>/specs/*.yml) and the app-owned contexts
                   of codefresh/apps/<app>/integrations.yaml. The platform contexts it attaches must already exist.
    Options:
      --dry-run    Print the payloads and a plan, every context and registry value masked; call no API. Also
                   DRY_RUN=1.
      --recreate-missing-hooks
                   Delete and create again every pipeline whose git trigger repository has no Codefresh webhook
                   record, so that Codefresh installs the hook (see Webhooks).
      --prune      With --full: delete the superseded pipelines and contexts listed in
                   codefresh/platform/integrations.yaml (the objects that ADR-IR34 §11.9 replaces). Run it after the
                   first release has gone through platform-octopus.
      --root <dir> The environment-repo root (default: the parent of this script's folder).
    The PowerShell spellings -Preview, -Full, -App, -DryRun, -RecreateMissingHooks, -Prune and -Root work as well.

    Every pipeline gets spec.runtimeEnvironment.name = $CF_RUNTIME (default aks-platform-build/codefresh, the
    account default runtime <cf-runtime>).

    Frozen apps (ADR-IR34 decision 28): when the descriptor apps/<app>.yaml says status: frozen, --app <app> and
    --full register the app's pipelines with every git and cron trigger disabled and skip their webhook check;
    status: active and a rerun restore the triggers of the specs. The script never starts a build.

    Webhooks: Codefresh installs a repository's webhook only when it creates a pipeline whose git trigger names that
    repository; a replace (PUT) never does. A pipeline registered before its repository existed therefore never
    starts on a push (the sandbox fixture, 2026-09-24). After the pipelines, every git trigger repository is checked
    for a webhook record (GET /api/repos/webhooks/<owner>/<repo>/github/<context>): a missing one is a WARN naming
    the remedy, or with --recreate-missing-hooks the affected pipelines are deleted and created again (their build
    history goes with them).

    Secrets: none in this repository. A context or registry integration is created only when the operator's
    environment holds every value its declaration names (fromEnv); otherwise it is reported as pending. A spec
    variable whose committed value is a <placeholder> takes the value of the environment variable with the same name
    (for example PLATFORM_BOT_AUTHORS); any other spec value that is a whole <token> takes the environment variable
    TOKEN (upper case, dashes to underscores: <sandbox-app-repo> -> SANDBOX_APP_REPO). --full and --app refuse the
    pipeline while one is missing. Request bodies are built in memory and sent with .NET's HttpClient, so no value
    and no API key reaches a command line, a file or the log; answers quoted in errors have those values masked.

    YAML: specs and declarations are read by a strict reader for the YAML these files use: block mappings and
    sequences, flow collections on one line, quoted and plain scalars on one line (typed as PyYAML's safe_load types
    them: null, bool, int, float) and comments. Anything else stops the run with the file and line. Of a descriptor
    only its top-level status: line is read, the line that Platform.Onboarding retire --freeze writes.

    Environment
      CF_API_KEY   Codefresh API key of the operator (required unless --dry-run; never printed)
      CF_URL       Codefresh URL (default https://g.codefresh.io)
      CF_RUNTIME   Runtime environment for every pipeline (default aks-platform-build/codefresh)
      DRY_RUN      1 is the same as --dry-run
      The values that the integrations.yaml files name (docs/preview-codefresh.md, docs/bootstrap.md).

    REST routes (the ones the codefresh CLI uses):
      GET /api/projects/name/<name>, POST /api/projects
      GET|PUT /api/contexts/<name>, POST /api/contexts, DELETE /api/contexts/<name>
      GET /api/registries, POST /api/registries, PATCH /api/registries/<id>
      GET /api/pipelines/<name>, POST /api/pipelines, PUT /api/pipelines/<name>, DELETE /api/pipelines/<name>
      GET /api/repos/webhooks/<owner>/<repo>/github/<context>
    Codefresh answers some lookups of missing objects with HTTP 500 and a "not found" body; those count as 404.
    Names are URL-encoded (<app>/ci -> <app>%2Fci).

    Exit codes: 0 done (or planned), 1 an error (fix it and rerun: the script is idempotent), 2 usage error.

.EXAMPLE
    pwsh codefresh/register.ps1 --full --dry-run

.EXAMPLE
    pwsh codefresh/register.ps1 --app sandbox
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    # Projects and every pipeline, without triggers, contexts or variables.
    [switch] $Preview,

    # Projects, every pipeline, and the platform and app contexts and registry integrations.
    [switch] $Full,

    # Projects, pipelines and app-owned contexts of one app.
    [string] $App = '',

    # Print the payloads and a plan with every value masked; call no API.
    [Alias('dry-run')]
    [switch] $DryRun,

    # Recreate the pipelines whose git trigger repository has no Codefresh webhook.
    [Alias('recreate-missing-hooks')]
    [switch] $RecreateMissingHooks,

    # With -Full: delete the superseded pipelines and contexts.
    [switch] $Prune,

    # The environment-repo root.
    [string] $Root = '',

    # Print the usage.
    [Alias('h')]
    [switch] $Help,

    # GNU-style arguments that the host did not bind (--full, --app <app>, ...).
    [Parameter(ValueFromRemainingArguments)]
    [string[]] $Arguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$Usage = @'
Usage: register.ps1 --preview | --full | --app <app> [--dry-run] [--recreate-missing-hooks] [--prune] [--root <dir>]
  --preview      projects and every pipeline, without triggers, contexts or variables
  --full         projects, every pipeline, the contexts and the registry integrations
  --app <app>    projects, pipelines and app-owned contexts of one app
  --dry-run      print the payloads and a plan, values masked; call no API (also DRY_RUN=1)
  --recreate-missing-hooks
                 recreate the pipelines whose git trigger repository has no Codefresh webhook
  --prune        with --full: delete the superseded pipelines and contexts
Environment: CF_API_KEY (unless --dry-run), CF_URL, CF_RUNTIME, and the values named by codefresh/**/integrations.yaml.
More: the comment at the top of codefresh/register.ps1.
'@

function Write-Line([string] $Message) {
    Write-Host $Message
}

function Write-Note([string] $Message) {
    [Console]::Error.WriteLine("register.ps1: $Message")
}

function Stop-Usage([string] $Message) {
    [Console]::Error.WriteLine($Usage)
    Write-Note $Message
    exit 2
}

function Stop-Run([string] $Message) {
    Write-Note $Message
    exit 1
}

# ---------------------------------------------------------------- arguments
if ($Help) {
    Write-Line $Usage
    exit 0
}
for ($index = 0; $index -lt $Arguments.Count; $index++) {
    $argument = $Arguments[$index]
    switch -CaseSensitive -Regex ($argument) {
        '^--preview$' { $Preview = [switch]::Present; break }
        '^--full$' { $Full = [switch]::Present; break }
        '^--dry-run$' { $DryRun = [switch]::Present; break }
        '^--recreate-missing-hooks$' { $RecreateMissingHooks = [switch]::Present; break }
        '^--prune$' { $Prune = [switch]::Present; break }
        '^(-h|--help)$' { Write-Line $Usage; exit 0 }
        '^--(app|root)$' {
            if ($index + 1 -ge $Arguments.Count) {
                Stop-Usage "--$($Matches[1]) needs a value"
            }
            if ($Matches[1] -eq 'root') {
                $Root = $Arguments[++$index]
            }
            elseif ($App) {
                Stop-Usage 'one mode at a time'
            }
            else {
                $App = $Arguments[++$index]
            }
            break
        }
        default { Stop-Usage "unknown argument: $argument" }
    }
}

$modes = @()
if ($Preview) { $modes += 'preview' }
if ($Full) { $modes += 'full' }
if ($App) { $modes += 'app' }
if ($modes.Count -gt 1) {
    Stop-Usage 'one mode at a time'
}
if ($modes.Count -eq 0) {
    Stop-Usage 'choose --preview, --full or --app <app>'
}
$Mode = $modes[0]
if ($Prune -and $Mode -ne 'full') {
    Stop-Usage '--prune works with --full only'
}
if ($Mode -eq 'app' -and $App -cnotmatch '^[a-z][a-z0-9]{2,11}$') {
    Stop-Usage "invalid app name '$App' (^[a-z][a-z0-9]{2,11}`$)"
}
$IsDryRun = $DryRun.IsPresent -or $env:DRY_RUN -eq '1'

if (-not $Root) {
    $Root = Join-Path $PSScriptRoot '..'
}
if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    Stop-Usage "root '$Root' not found"
}
$Root = (Resolve-Path -LiteralPath $Root).Path
$CfUrl = if ($env:CF_URL) { $env:CF_URL } else { 'https://g.codefresh.io' }
if ($CfUrl.EndsWith('/')) {
    $CfUrl = $CfUrl.Substring(0, $CfUrl.Length - 1)
}
$CfRuntime = if ($env:CF_RUNTIME) { $env:CF_RUNTIME } else { 'aks-platform-build/codefresh' }
if (-not $IsDryRun -and -not $env:CF_API_KEY) {
    Stop-Run 'CF_API_KEY is not set'
}

$Placeholder = '^<[^<>]+>$'
$YamlPlaceholder = '<(acr-name|ci-image-version|ci-image-digest|[a-z0-9-]+-digest|[a-z0-9-]+-version)>'

# ---------------------------------------------------------------- YAML
# The strict reader of .DESCRIPTION: mappings become ordered dictionaries, sequences lists, and plain scalars take
# the types of PyYAML's implicit resolvers (YAML 1.1; timestamps stay strings). Errors are FormatExceptions.

$YamlEscapes = @{
    [char] '0' = "`0"; [char] 'a' = "`a"; [char] 'b' = "`b"; [char] 't' = "`t"; [char] "`t" = "`t"; [char] 'n' = "`n"
    [char] 'v' = "`v"; [char] 'f' = "`f"; [char] 'r' = "`r"; [char] 'e' = "`e"; [char] ' ' = ' '; [char] '"' = '"'
    [char] '/' = '/'; [char] '\' = '\'; [char] 'N' = "`u{85}"; [char] '_' = "`u{A0}"; [char] 'L' = "`u{2028}"
    [char] 'P' = "`u{2029}"
}
$YamlHexEscapes = @{ [char] 'x' = 2; [char] 'u' = 4; [char] 'U' = 8 }

function Stop-Yaml([string] $Message) {
    throw [System.FormatException]::new("$($script:yaml.Label) line $($script:yaml.Pos + 1): $Message")
}

function Test-YamlBlank([string] $Line) {
    return $Line -match '^[ \t]*(#.*)?$'
}

function Test-YamlItem([string] $Content) {
    return $Content -cmatch '^-( |$)'
}

# The indentation of the current line, which holds a node.
function Get-YamlIndent {
    $line = $script:yaml.Lines[$script:yaml.Pos]
    $indent = $line.Length - $line.TrimStart(' ').Length
    if ($line[$indent] -eq "`t") {
        Stop-Yaml 'a tab in the indentation'
    }
    return $indent
}

# Moves to the next line that holds a node; $false at the end of the text.
function Skip-YamlBlank {
    while ($script:yaml.Pos -lt $script:yaml.Lines.Count -and (Test-YamlBlank $script:yaml.Lines[$script:yaml.Pos])) {
        $script:yaml.Pos++
    }
    return $script:yaml.Pos -lt $script:yaml.Lines.Count
}

function ConvertFrom-YamlText([string] $Text, [string] $Label) {
    $lines = [System.Collections.Generic.List[string]]::new([string[]] (($Text -replace '^﻿', '') -split '\r?\n'))
    $script:yaml = @{ Lines = $lines; Pos = 0; Label = $Label }
    if (-not (Skip-YamlBlank)) {
        return $null
    }
    if ($lines[$script:yaml.Pos] -cmatch '^(%|---|\.\.\.)') {
        Stop-Yaml 'directives and document markers are not supported'
    }
    $node = Read-YamlBlock (Get-YamlIndent) -1
    if (Skip-YamlBlank) {
        Stop-Yaml 'unexpected content (a line indented less than its block, or a second document)'
    }
    return , $node
}

# The node that starts on the current line at column $Indent, inside a block indented by $Parent.
function Read-YamlBlock([int] $Indent, [int] $Parent) {
    $content = $script:yaml.Lines[$script:yaml.Pos].Substring($Indent)
    if (Test-YamlItem $content) {
        return , (Read-YamlSequence $Indent)
    }
    if ($null -ne (Split-YamlKey $content)) {
        return , (Read-YamlMapping $Indent)
    }
    return , (Read-YamlValue $content $Parent)
}

function Read-YamlSequence([int] $Indent) {
    $items = [System.Collections.Generic.List[object]]::new()
    while ((Skip-YamlBlank) -and (Get-YamlIndent) -ge $Indent) {
        if ((Get-YamlIndent) -gt $Indent) {
            Stop-Yaml 'unexpected indentation'
        }
        $content = $script:yaml.Lines[$script:yaml.Pos].Substring($Indent)
        if (-not (Test-YamlItem $content)) {
            break
        }
        $item = $content.Substring(1).TrimStart(' ')
        if (Test-YamlBlank $item) {
            $script:yaml.Pos++
            $value = $null
            if ((Skip-YamlBlank) -and (Get-YamlIndent) -gt $Indent) {
                $value = Read-YamlBlock (Get-YamlIndent) $Indent
            }
            $items.Add($value)
            continue
        }
        # The item starts on the dash line: it is read as a line of its own at its column.
        $column = $Indent + $content.Length - $item.Length
        $script:yaml.Lines[$script:yaml.Pos] = (' ' * $column) + $item
        $items.Add((Read-YamlBlock $column $Indent))
    }
    return , $items
}

function Read-YamlMapping([int] $Indent) {
    $map = [ordered]@{}
    while ((Skip-YamlBlank) -and (Get-YamlIndent) -ge $Indent) {
        if ((Get-YamlIndent) -gt $Indent) {
            Stop-Yaml 'unexpected indentation'
        }
        $content = $script:yaml.Lines[$script:yaml.Pos].Substring($Indent)
        $pair = Split-YamlKey $content
        if ($null -eq $pair) {
            Stop-Yaml $(if ($content -cmatch '^(---|\.\.\.)') { 'several documents are not supported' } else { 'expected a mapping key' })
        }
        $value = $null
        if (Test-YamlBlank $pair.Rest) {
            $script:yaml.Pos++
            if ((Skip-YamlBlank) -and (Get-YamlIndent) -gt $Indent) {
                $value = Read-YamlBlock (Get-YamlIndent) $Indent
            }
            elseif ($script:yaml.Pos -lt $script:yaml.Lines.Count -and (Get-YamlIndent) -eq $Indent -and
                (Test-YamlItem $script:yaml.Lines[$script:yaml.Pos].Substring($Indent))) {
                $value = Read-YamlSequence $Indent
            }
        }
        else {
            $value = Read-YamlValue $pair.Rest.TrimStart(' ', "`t") $Indent
        }
        $map[$pair.Key] = $value
    }
    return $map
}

# @{ Key; Rest } of a "key: value" line (Rest: the text after the colon), or $null.
function Split-YamlKey([string] $Content) {
    if ($Content.Length -eq 0) {
        return $null
    }
    if ($Content[0] -in @('"', "'")) {
        $quoted = Read-YamlQuoted $Content 0
        $after = if ($quoted) { $Content.Substring($quoted.End).TrimStart(' ') } else { '' }
        if ($after -cmatch '^:([ \t]|$)') {
            return @{ Key = $quoted.Value; Rest = $after.Substring(1) }
        }
        return $null
    }
    if ('#[{&*!|>%@`'.Contains($Content[0]) -or $Content -cmatch '^[-?:]( |$)') {
        return $null
    }
    $match = [regex]::Match($Content, '^(.*?)[ \t]*:([ \t]|$)')
    if (-not $match.Success -or $match.Groups[1].Value -match '[ \t]#') {
        return $null
    }
    $key = $match.Groups[1].Value
    if ((ConvertFrom-YamlPlain $key) -isnot [string]) {
        Stop-Yaml "key '$key' is not a string"
    }
    return @{ Key = $key; Rest = $Content.Substring($match.Length - $match.Groups[2].Length) }
}

# A value on one line, after a key or a dash; moves past the line.
function Read-YamlValue([string] $Text, [int] $Parent) {
    $first = $Text[0]
    $end = $Text.Length
    if ($first -in @('"', "'")) {
        $quoted = Read-YamlQuoted $Text 0
        if ($null -eq $quoted) {
            Stop-Yaml 'multi-line quoted scalars are not supported'
        }
        $value, $end = $quoted.Value, $quoted.End
    }
    elseif ($first -in @('[', '{')) {
        $position = 0
        $value = Read-YamlFlow $Text ([ref] $position)
        $end = $position
    }
    elseif ('|>&*!%@`'.Contains($first) -or $Text -cmatch '^\?( |$)') {
        Stop-Yaml "block scalars, anchors, aliases, tags and complex keys are not supported ('$first')"
    }
    else {
        $plain = [regex]::Match($Text, '^(.*?)([ \t]+#.*)?$').Groups[1].Value.TrimEnd(' ', "`t")
        if ($plain -cmatch ':([ \t]|$)') {
            Stop-Yaml 'mapping values are not allowed here'
        }
        $value = ConvertFrom-YamlPlain $plain
    }
    if (-not (Test-YamlBlank $Text.Substring($end))) {
        Stop-Yaml "unexpected text after a value: $($Text.Substring($end).Trim())"
    }
    $script:yaml.Pos++
    if ($Parent -ge 0 -and (Skip-YamlBlank) -and (Get-YamlIndent) -gt $Parent) {
        Stop-Yaml 'multi-line scalars are not supported'
    }
    return , $value
}

# @{ Value; End } of the quoted scalar at $Start (End: the index after the closing quote), or $null when it does
# not close on this line.
function Read-YamlQuoted([string] $Text, [int] $Start) {
    $quote = $Text[$Start]
    $builder = [System.Text.StringBuilder]::new()
    for ($i = $Start + 1; $i -lt $Text.Length; $i++) {
        $char = $Text[$i]
        if ($char -eq "'" -and $quote -eq "'" -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq "'") {
            [void] $builder.Append("'")
            $i++
        }
        elseif ($char -eq $quote) {
            return @{ Value = $builder.ToString(); End = $i + 1 }
        }
        elseif ($char -ne '\' -or $quote -eq "'") {
            [void] $builder.Append($char)
        }
        elseif ($i + 1 -ge $Text.Length) {
            return $null
        }
        elseif ($YamlEscapes.ContainsKey($Text[$i + 1])) {
            [void] $builder.Append($YamlEscapes[$Text[++$i]])
        }
        else {
            $width = $YamlHexEscapes[$Text[$i + 1]]
            $hex = if ($width -and $i + 2 + $width -le $Text.Length) { $Text.Substring($i + 2, $width) } else { '' }
            if ($hex -notmatch '^[0-9A-Fa-f]+$') {
                Stop-Yaml "unknown escape \$($Text[$i + 1]) in a double-quoted scalar"
            }
            [void] $builder.Append([char]::ConvertFromUtf32([Convert]::ToInt32($hex, 16)))
            $i += 1 + $width
        }
    }
    return $null
}

# Moves past spaces in a flow collection; returns the next character, or '' at the end of the line.
function Skip-FlowSpace([string] $Text, [ref] $Position) {
    while ($Position.Value -lt $Text.Length -and $Text[$Position.Value] -eq ' ') {
        $Position.Value++
    }
    return $(if ($Position.Value -lt $Text.Length) { $Text[$Position.Value] } else { '' })
}

# The flow collection or flow scalar at $Position; it must end on this line.
function Read-YamlFlow([string] $Text, [ref] $Position) {
    $open = Skip-FlowSpace $Text $Position
    if ($open -in @('[', '{')) {
        $close = if ($open -eq '[') { ']' } else { '}' }
        if ($open -eq '{') {
            $node = [ordered]@{}
        }
        else {
            $node = [System.Collections.Generic.List[object]]::new()
        }
        $Position.Value++
        while ((Skip-FlowSpace $Text $Position) -ne $close) {
            if ((Skip-FlowSpace $Text $Position) -in @('', '#')) {
                Stop-Yaml 'multi-line flow collections are not supported'
            }
            $item = Read-YamlFlow $Text $Position
            $hasValue = (Skip-FlowSpace $Text $Position) -eq ':'
            if ($open -eq '[') {
                if ($hasValue) {
                    Stop-Yaml 'single-pair mappings in flow sequences are not supported'
                }
                $node.Add($item)
            }
            else {
                if ($item -isnot [string]) {
                    Stop-Yaml 'flow mapping keys must be strings'
                }
                $value = $null
                if ($hasValue) {
                    $Position.Value++
                    if ((Skip-FlowSpace $Text $Position) -notin @(',', '}')) {
                        $value = Read-YamlFlow $Text $Position
                    }
                }
                $node[$item] = $value
            }
            $next = Skip-FlowSpace $Text $Position
            if ($next -eq ',') {
                $Position.Value++
            }
            elseif ($next -ne $close) {
                Stop-Yaml "expected ',' or '$close' in a flow collection"
            }
        }
        $Position.Value++
        return , $node
    }
    if ($open -in @('"', "'")) {
        $quoted = Read-YamlQuoted $Text $Position.Value
        if ($null -eq $quoted) {
            Stop-Yaml 'multi-line quoted scalars are not supported'
        }
        $Position.Value = $quoted.End
        return $quoted.Value
    }
    if ($open -in @('&', '*', '!')) {
        Stop-Yaml 'anchors, aliases and tags are not supported'
    }
    $match = [regex]::Match($Text.Substring($Position.Value), '^[^,?\[\]{}]*?(?=[ ]*(,|\?|\[|\]|\{|\}|:[ ,\[\]{}]|:$| #|$))')
    $Position.Value += $match.Length
    return , (ConvertFrom-YamlPlain $match.Value)
}

# The typed value of a plain scalar (PyYAML's implicit resolvers).
function ConvertFrom-YamlPlain([string] $Text) {
    if ($Text -cmatch '^(~|null|Null|NULL|)$') {
        return $null
    }
    if ($Text -cmatch '^(yes|Yes|YES|true|True|TRUE|on|On|ON)$') {
        return $true
    }
    if ($Text -cmatch '^(no|No|NO|false|False|FALSE|off|Off|OFF)$') {
        return $false
    }
    if ($Text -cmatch '^([-+]?)(0b[0-1_]+|0[0-7_]+|0|[1-9][0-9_]*|0x[0-9a-fA-F_]+|[1-9][0-9_]*(:[0-5]?[0-9])+)$') {
        $sign = if ($Matches[1] -eq '-') { -1 } else { 1 }
        $digits = $Matches[2].Replace('_', '')
        $value = switch -Regex -CaseSensitive ($digits) {
            '^0$' { [long] 0; break }
            '^0b' { [Convert]::ToInt64($digits.Substring(2), 2); break }
            '^0x' { [Convert]::ToInt64($digits.Substring(2), 16); break }
            '^0' { [Convert]::ToInt64($digits, 8); break }
            ':' {
                $total = [long] 0
                foreach ($part in $digits.Split(':')) {
                    $total = $total * 60 + [long] $part
                }
                $total
                break
            }
            default { [long]::Parse($digits, [System.Globalization.CultureInfo]::InvariantCulture) }
        }
        return $sign * $value
    }
    if ($Text -cmatch '^([-+]?[0-9][0-9_]*\.[0-9_]*([eE][-+][0-9]+)?|\.[0-9_]+([eE][-+][0-9]+)?)$') {
        return [double]::Parse($Text.Replace('_', ''), [System.Globalization.CultureInfo]::InvariantCulture)
    }
    if ($Text -cmatch '^([-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN)|[-+]?[0-9][0-9_]*(:[0-5]?[0-9])+\.[0-9_]*|=|<<)$') {
        Stop-Yaml "unsupported plain scalar '$Text'"
    }
    return $Text
}

function Read-YamlFile([string] $Path) {
    return , (ConvertFrom-YamlText ([System.IO.File]::ReadAllText($Path)) (Get-RelativePath $Path))
}

# ---------------------------------------------------------------- helpers
function Get-RelativePath([string] $Path) {
    return [System.IO.Path]::GetRelativePath($Root, $Path).Replace('\', '/')
}

# Python truthiness: $null, $false, 0, '' and empty collections are false.
function Test-Truthy($Value) {
    if ($null -eq $Value) { return $false }
    if ($Value -is [bool]) { return $Value }
    if ($Value -is [string]) { return $Value.Length -gt 0 }
    if ($Value -is [System.Collections.ICollection]) { return $Value.Count -gt 0 }
    if ($Value -is [long] -or $Value -is [int] -or $Value -is [double]) { return $Value -ne 0 }
    return $true
}

function Get-Key($Node, [string] $Key) {
    if ($Node -is [System.Collections.IDictionary] -and $Node.Contains($Key)) {
        return , $Node[$Key]
    }
    return $null
}

# The items of a sequence, or the keys of a mapping ("for x in node or []").
function Get-SequenceItem($Node) {
    if (-not (Test-Truthy $Node)) { return }
    if ($Node -is [System.Collections.IDictionary]) { return @($Node.PSBase.Keys) }
    return @($Node)
}

# Python's str() of a YAML value.
function ConvertTo-PyString($Value) {
    if ($null -eq $Value) { return 'None' }
    if ($Value -is [bool]) { return $(if ($Value) { 'True' } else { 'False' }) }
    if ($Value -is [double]) { return $Value.ToString('R', [System.Globalization.CultureInfo]::InvariantCulture) }
    return [string] $Value
}

function Get-OrdinalSorted([string[]] $Items) {
    if (-not $Items) { return }
    $copy = [string[]] $Items.Clone()
    [Array]::Sort($copy, [System.StringComparer]::Ordinal)
    return $copy
}

# A deep copy with every mapping's keys in ordinal order (Python's sort_keys).
function Copy-SortedNode($Node) {
    if ($Node -is [System.Collections.IDictionary]) {
        $sorted = [ordered]@{}
        foreach ($key in @(Get-OrdinalSorted @($Node.PSBase.Keys))) {
            $sorted[$key] = Copy-SortedNode $Node[$key]
        }
        return $sorted
    }
    if ($Node -is [System.Collections.IList]) {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Node) {
            $items.Add((Copy-SortedNode $item))
        }
        return , $items
    }
    return , $Node
}

# JSON with sorted keys and two-space indentation, laid out like Python's json.dump(indent=2, sort_keys=True).
function ConvertTo-PayloadJson($Node) {
    return ConvertTo-Json -InputObject (Copy-SortedNode $Node) -Depth 20 -EscapeHandling EscapeNonAscii
}

# Every value this run read for a context or an integration, and the API key: masked wherever text is logged.
$Secrets = [System.Collections.Generic.List[string]]::new()
function Protect-Text([string] $Text) {
    foreach ($secret in $Secrets) {
        if ($secret.Length -ge 4) {
            $Text = $Text.Replace($secret, '***')
        }
    }
    return $Text
}

function Get-SafeName([string] $Name) {
    return [Uri]::EscapeDataString($Name)
}

# Files matching $Filter (for example specs/*.yml) under $Folder, or under each of its subfolders, sorted by path.
function Get-SortedFile([string] $Folder, [string] $Filter, [switch] $InSubfolders) {
    $base = Join-Path $Root $Folder
    if (-not (Test-Path -LiteralPath $base -PathType Container)) { return }
    $folders = if ($InSubfolders) { @(Get-ChildItem -LiteralPath $base -Directory | ForEach-Object FullName) } else { @($base) }
    $files = [System.Collections.Generic.List[string]]::new()
    foreach ($folder in $folders) {
        $parent = Split-Path $Filter -Parent
        $directory = if ($parent) { Join-Path $folder $parent } else { $folder }
        if (Test-Path -LiteralPath $directory -PathType Container) {
            foreach ($file in @(Get-ChildItem -LiteralPath $directory -File -Filter (Split-Path $Filter -Leaf))) {
                $files.Add($file.FullName)
            }
        }
    }
    return Get-OrdinalSorted @($files)
}

# The status of an app (ADR-IR34 decision 28): the top-level status: line of apps/<app>.yaml, as
# Platform.Onboarding retire --freeze writes it; active when the descriptor, the line or its value is absent.
$AppStatus = @{}
function Get-AppStatus([string] $Name) {
    if (-not $AppStatus.ContainsKey($Name)) {
        $path = Join-Path $Root "apps/$Name.yaml"
        $status = ''
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            foreach ($line in [System.IO.File]::ReadAllLines($path)) {
                if ($line -cmatch '^status:[ \t]*(["'']?)([A-Za-z]*)\1[ \t]*(#.*)?$') {
                    $status = $Matches[2]
                }
                elseif ($line -cmatch '^status:') {
                    $status = $line
                }
            }
        }
        if (-not $status) {
            $status = 'active'
        }
        if ($status -cnotin @('active', 'frozen')) {
            throw [System.FormatException]::new("apps/${Name}.yaml: status must be active or frozen ($status)")
        }
        $AppStatus[$Name] = $status
    }
    return $AppStatus[$Name]
}

# ---------------------------------------------------------------- plan
# One entry per object, in the order of registration: projects (in reverse order of first appearance); per declaration
# its contexts, registries and prune entries; per spec its pipeline (and warnings).
$Plan = [System.Collections.Generic.List[object]]::new()
$Warnings = [System.Collections.Generic.List[string]]::new()

# {value: x} -> x; {fromEnv: V} -> the value of V, or Missing = V.
function Resolve-Declared($Declared, [string] $Label) {
    if ($Declared -is [System.Collections.IDictionary] -and $Declared.Contains('value')) {
        return @{ Value = ConvertTo-PyString $Declared['value']; Source = 'committed'; Missing = $null; Optional = $false }
    }
    if ($Declared -is [System.Collections.IDictionary] -and $Declared.Contains('fromEnv')) {
        $variable = [string] $Declared['fromEnv']
        $value = [Environment]::GetEnvironmentVariable($variable)
        if (-not $value -or $value -match $Placeholder) {
            return @{ Value = $null; Source = $variable; Missing = $variable; Optional = Test-Truthy (Get-Key $Declared 'optional') }
        }
        $Secrets.Add($value)
        return @{ Value = $value; Source = $variable; Missing = $null; Optional = $false }
    }
    throw [System.FormatException]::new("${Label}: expected {value: ...} or {fromEnv: ...}")
}

function Get-Name($Node, [string] $Label) {
    $name = Get-Key $Node 'name'
    if ($name -isnot [string] -or -not $name) {
        throw [System.FormatException]::new("${Label}: an entry without a name")
    }
    return $name
}

function Add-Declaration([string] $Path, [System.Collections.Generic.HashSet[string]] $OptionalContexts) {
    $doc = Read-YamlFile $Path
    $relative = Get-RelativePath $Path
    foreach ($context in @(Get-SequenceItem (Get-Key $doc 'contexts'))) {
        $name = Get-Name $context $relative
        $optional = Test-Truthy (Get-Key $context 'optional')
        if ($optional) {
            [void] $OptionalContexts.Add($name)
        }
        $data = [ordered]@{}
        $sources = [System.Collections.Generic.List[string]]::new()
        $missing = [System.Collections.Generic.List[string]]::new()
        $declared = Get-Key $context 'data'
        foreach ($key in @(Get-SequenceItem $declared)) {
            $resolved = Resolve-Declared $declared[$key] "$relative $name.$key"
            if ($null -ne $resolved.Value) {
                $data[$key] = $resolved.Value
                $sources.Add($(if ($resolved.Source -eq 'committed') { "$key (committed)" } else { "$key <- $($resolved.Source)" }))
            }
            elseif (-not $resolved.Optional) {
                $missing.Add($resolved.Missing)
            }
        }
        $payload = $null
        if ($missing.Count -eq 0) {
            $type = if ($context.Contains('type')) { $context['type'] } else { 'secret' }
            $payload = [ordered]@{ apiVersion = 'v1'; kind = 'context'; metadata = [ordered]@{ name = $name }; spec = [ordered]@{ type = $type; data = $data } }
        }
        $Plan.Add([pscustomobject]@{ Kind = 'context'; Name = $name; Payload = $payload; Missing = @($missing); Optional = $optional; Sources = @($sources) })
    }
    foreach ($registry in @(Get-SequenceItem (Get-Key $doc 'registries'))) {
        $name = Get-Name $registry $relative
        $fields = @{}
        $missing = [System.Collections.Generic.List[string]]::new()
        foreach ($key in @('domain', 'username', 'password')) {
            $resolved = Resolve-Declared (Get-Key $registry $key) "$relative $name.$key"
            if ($null -eq $resolved.Value) { $missing.Add($resolved.Missing) } else { $fields[$key] = $resolved.Value }
        }
        $primary = Test-Truthy (Get-Key $registry 'primary')
        $payload = $null
        if ($missing.Count -eq 0) {
            $payload = [ordered]@{
                name = $name; provider = 'other'; domain = $fields['domain']; username = $fields['username']; password = $fields['password']
                behindFirewall = $false; primary = $primary; default = $false; denyCompositeDomain = $false
            }
        }
        $Plan.Add([pscustomobject]@{ Kind = 'registry'; Name = $name; Payload = $payload; Missing = @($missing); Primary = $primary })
    }
    if ($Mode -eq 'full' -and $Prune) {
        $superseded = Get-Key $doc 'superseded'
        foreach ($what in @('pipeline', 'context')) {
            foreach ($name in @(Get-SequenceItem (Get-Key $superseded "${what}s"))) {
                $Plan.Add([pscustomobject]@{ Kind = 'prune'; What = $what; Name = ConvertTo-PyString $name })
            }
        }
    }
}

# Replaces every whole-string <token> outside `variables` with the environment variable TOKEN; names the missing ones.
function Resolve-SpecToken($Node, [System.Collections.Generic.List[string]] $Missing) {
    if ($Node -is [System.Collections.IDictionary]) {
        foreach ($key in @($Node.PSBase.Keys)) {
            if ($key -cne 'variables') {
                $Node[$key] = Resolve-SpecToken $Node[$key] $Missing
            }
        }
        return , $Node
    }
    if ($Node -is [System.Collections.IList]) {
        $items = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Node) {
            $items.Add((Resolve-SpecToken $item $Missing))
        }
        return , $items
    }
    if ($Node -is [string] -and $Node -match $Placeholder) {
        $variable = ([regex]::Replace($Node.Substring(1, $Node.Length - 2), '[^A-Za-z0-9]', '_')).ToUpperInvariant()
        $value = [Environment]::GetEnvironmentVariable($variable)
        if ($value -and $value -notmatch $Placeholder) {
            return $value
        }
        $Missing.Add($variable)
    }
    return , $Node
}

function Add-Pipeline([string] $Path, [System.Collections.Generic.HashSet[string]] $OptionalContexts, [System.Collections.Generic.List[string]] $Projects, [System.Collections.Generic.HashSet[string]] $Seen) {
    $relative = Get-RelativePath $Path
    $doc = Read-YamlFile $Path
    $meta, $spec = (Get-Key $doc 'metadata'), (Get-Key $doc 'spec')
    if ((Get-Key $doc 'kind') -cne 'pipeline') {
        throw [System.FormatException]::new("${relative}: not a pipeline spec")
    }
    if ($spec -isnot [System.Collections.IDictionary] -or (Get-Key $meta 'name') -isnot [string] -or (Get-Key $meta 'project') -isnot [string]) {
        throw [System.FormatException]::new("${relative}: metadata.name, metadata.project and spec are required")
    }
    $name, $project = $meta['name'], $meta['project']
    if (-not $name.StartsWith("$project/", [System.StringComparison]::Ordinal)) {
        throw [System.FormatException]::new("${relative}: pipeline $name is not in project $project")
    }
    if (-not $Seen.Add($name)) {
        throw [System.FormatException]::new("${relative}: pipeline $name is declared twice")
    }
    if (-not $Projects.Contains($project)) {
        $Projects.Add($project)
    }
    $spec['runtimeEnvironment'] = [ordered]@{ name = $CfRuntime }
    $errors = [System.Collections.Generic.List[string]]::new()
    $frozen = $null
    if ($Mode -eq 'preview') {
        foreach ($key in @('triggers', 'cronTriggers', 'contexts', 'variables')) {
            $spec.Remove($key)
        }
        $meta['description'] = 'PREVIEW: visible only, not runnable (no triggers, no contexts). Replace with codefresh/register.ps1 --full (docs/preview-codefresh.md).'
        if (-not $meta.Contains('labels')) {
            $meta['labels'] = [ordered]@{}
        }
        $meta['labels']['tags'] = [System.Collections.Generic.List[object]]::new([object[]] @('preview'))
    }
    else {
        $missing = [System.Collections.Generic.List[string]]::new()
        foreach ($variable in @(Get-SequenceItem (Get-Key $spec 'variables'))) {
            if ((ConvertTo-PyString $(if ($variable.Contains('value')) { $variable['value'] } else { '' })) -match $Placeholder) {
                $value = [Environment]::GetEnvironmentVariable([string] $variable['key'])
                if ($value -and $value -notmatch $Placeholder) { $variable['value'] = $value } else { $missing.Add([string] $variable['key']) }
            }
        }
        if ($missing.Count -gt 0) {
            $errors.Add("set $($missing -join ',') (spec variables still hold placeholders)")
        }
        # Any other spec value that is a whole <token> (for example a trigger's repo: "<sandbox-app-repo>")
        # takes the environment variable TOKEN (upper case, dashes to underscores: SANDBOX_APP_REPO).
        $tokens = [System.Collections.Generic.List[string]]::new()
        [void] (Resolve-SpecToken $spec $tokens)
        if ($tokens.Count -gt 0) {
            $errors.Add("set $(@(Get-OrdinalSorted @($tokens | Select-Object -Unique)) -join ',') (spec placeholders)")
        }
        # A frozen app keeps its pipelines with every trigger off (ADR-IR34 decision 28).
        $app = if ($relative -cmatch '^codefresh/apps/([^/]+)/specs/') { $Matches[1] } else { '' }
        if ($app -and (Get-AppStatus $app) -eq 'frozen') {
            $frozen = "apps/$app.yaml"
            foreach ($trigger in @(Get-SequenceItem (Get-Key $spec 'triggers')) + @(Get-SequenceItem (Get-Key $spec 'cronTriggers'))) {
                if ($trigger -is [System.Collections.IDictionary]) {
                    $trigger['disabled'] = $true
                }
            }
        }
    }
    $template = [string] (Get-Key (Get-Key $spec 'specTemplate') 'path')
    $yamlPath = if ($template) { Join-Path $Root $template.TrimStart('.', '/') } else { '' }
    if ($yamlPath -and (Test-Path -LiteralPath $yamlPath -PathType Leaf)) {
        $text = @([System.IO.File]::ReadAllLines($yamlPath) | Where-Object { -not $_.TrimStart().StartsWith('#') }) -join "`n"
        $found = @(Get-OrdinalSorted @([regex]::Matches($text, $YamlPlaceholder) | ForEach-Object Value | Select-Object -Unique))
        if ($found.Count -gt 0) {
            $Warnings.Add("${name}: $template still holds $($found -join ' '); its builds fail until they are pinned")
        }
    }
    elseif ($template) {
        $Warnings.Add("${name}: specTemplate.path $template does not exist in this checkout")
    }
    $contexts = @(Get-SequenceItem (Get-Key $spec 'contexts') | ForEach-Object { ConvertTo-PyString $_ })
    $Plan.Add([pscustomobject]@{
            Kind     = 'pipeline'
            Name     = $name
            Payload  = $doc
            Required = @($contexts | Where-Object { -not $OptionalContexts.Contains($_) })
            Optional = @($contexts | Where-Object { $OptionalContexts.Contains($_) })
            Error    = $errors -join '; '
            Frozen   = $frozen
        })
}

function New-Plan {
    $declarations = @()
    if ($Mode -eq 'full') {
        $declarations = @(Join-Path $Root 'codefresh/platform/integrations.yaml') + @(Get-SortedFile 'codefresh/apps' 'integrations.yaml' -InSubfolders)
    }
    elseif ($Mode -eq 'app' -and (Test-Path -LiteralPath (Join-Path $Root "codefresh/apps/$App/integrations.yaml") -PathType Leaf)) {
        $declarations = @(Join-Path $Root "codefresh/apps/$App/integrations.yaml")
    }
    $optionalContexts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $declarations) {
        Add-Declaration $path $optionalContexts
    }

    if ($Mode -eq 'app') {
        $specs = @(Get-SortedFile "codefresh/apps/$App/specs" '*.yml')
        if ($specs.Count -eq 0) {
            throw [System.FormatException]::new("no specs under codefresh/apps/$App/specs/")
        }
    }
    else {
        $specs = @(Get-SortedFile 'codefresh/platform/specs' '*.yml') + @(Get-SortedFile 'codefresh/apps' 'specs/*.yml' -InSubfolders)
    }
    $projects = [System.Collections.Generic.List[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $specs) {
        Add-Pipeline $path $optionalContexts $projects $seen
    }
    foreach ($project in $projects) {
        $Plan.Insert(0, [pscustomobject]@{ Kind = 'project'; Name = $project })
    }
}

try {
    New-Plan
}
catch {
    Write-Note $_.Exception.Message
    Stop-Run 'rendering failed'
}

# ---------------------------------------------------------------- API
$script:http = $null
$script:response = ''

# Sends one request and returns the HTTP status as three digits ('000' without an answer); the answer's body stays
# for Get-ResponseHead. HTTP 500 with "not found" in the body counts as 404.
function Invoke-CodefreshApi([string] $Method, [string] $Path, [string] $Body = '') {
    if ($null -eq $script:http) {
        $handler = [System.Net.Http.HttpClientHandler]::new()
        $handler.AllowAutoRedirect = $false
        $script:http = [System.Net.Http.HttpClient]::new($handler)
        $script:http.Timeout = [TimeSpan]::FromMinutes(2)
        [void] $script:http.DefaultRequestHeaders.TryAddWithoutValidation('Authorization', $env:CF_API_KEY)
        [void] $script:http.DefaultRequestHeaders.TryAddWithoutValidation('User-Agent', 'platform-register/1.0')
        $Secrets.Add($env:CF_API_KEY)
    }
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($Method), "$CfUrl/api$Path")
    try {
        if ($Body) {
            $request.Content = [System.Net.Http.ByteArrayContent]::new([System.Text.Encoding]::UTF8.GetBytes($Body))
            $request.Content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/json')
        }
        $answer = $script:http.SendAsync($request).GetAwaiter().GetResult()
        $status = [int] $answer.StatusCode
        $script:response = $answer.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $answer.Dispose()
    }
    catch {
        Write-Note "$Method /api$Path failed: $(Protect-Text $_.Exception.GetBaseException().Message)"
        $script:response = ''
        return '000'
    }
    finally {
        $request.Dispose()
    }
    if ($status -eq 500 -and $script:response -match 'not found') {
        return '404'
    }
    return '{0:D3}' -f $status
}

# The first 300 bytes of the last answer on one line, with the secrets masked.
function Get-ResponseHead {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($script:response)
    return Protect-Text ([System.Text.Encoding]::UTF8.GetString($bytes, 0, [Math]::Min(300, $bytes.Length)) -replace "`n", ' ')
}

function Test-Success([string] $Status) {
    return $Status -match '^2\d\d$'
}

function ConvertTo-RequestBody($Payload) {
    return (ConvertTo-PayloadJson $Payload) + "`n"
}

# A context or registry payload with every value masked (dry run).
function ConvertTo-MaskedJson($Payload) {
    $copy = Copy-SortedNode $Payload
    if ((Get-Key $copy 'kind') -ceq 'context') {
        foreach ($key in @($copy['spec']['data'].PSBase.Keys)) {
            $copy['spec']['data'][$key] = '***'
        }
    }
    foreach ($key in @('domain', 'username', 'password')) {
        if ($copy.Contains($key)) {
            $copy[$key] = '***'
        }
    }
    return ConvertTo-PayloadJson $copy
}

$script:failures = 0
$Pending = [System.Collections.Generic.List[string]]::new()
$PresentContexts = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

function Add-Failure([string] $Message) {
    Write-Note "ERROR $Message"
    $script:failures++
}

function Get-PlanEntry([string] $Kind) {
    return @($Plan | Where-Object Kind -CEQ $Kind)
}

# Whether a context exists: from this run, or by a lookup.
function Test-Context([string] $Name) {
    if ($PresentContexts.Contains($Name)) {
        return $true
    }
    if ((Invoke-CodefreshApi GET "/contexts/$(Get-SafeName $Name)") -eq '200') {
        [void] $PresentContexts.Add($Name)
        return $true
    }
    return $false
}

# ---------------------------------------------------------------- warnings
foreach ($warning in $Warnings) {
    Write-Note "WARN $warning"
}

# ---------------------------------------------------------------- projects
foreach ($entry in @(Get-PlanEntry 'project')) {
    $name = $entry.Name
    if ($IsDryRun) {
        Write-Line "### project $name"
        continue
    }
    $status = Invoke-CodefreshApi GET "/projects/name/$(Get-SafeName $name)"
    if ($status -eq '200') {
        Write-Line "project ${name}: exists"
    }
    elseif ($status -eq '404') {
        $status = Invoke-CodefreshApi POST '/projects' ('{"projectName": ' + (ConvertTo-Json -InputObject $name -Compress -EscapeHandling EscapeNonAscii) + ', "tags": ["platform"]}')
        if (Test-Success $status) {
            Write-Line "project ${name}: created"
        }
        else {
            Add-Failure "project ${name}: create returned HTTP ${status}: $(Get-ResponseHead)"
        }
    }
    else {
        Add-Failure "project ${name}: lookup returned HTTP ${status}: $(Get-ResponseHead)"
    }
}

# ---------------------------------------------------------------- contexts
foreach ($entry in @(Get-PlanEntry 'context')) {
    $name = $entry.Name
    if ($null -eq $entry.Payload) {
        $Pending.Add("context ${name}: set $($entry.Missing -join ' ')")
        if (-not $IsDryRun -and (Invoke-CodefreshApi GET "/contexts/$(Get-SafeName $name)") -eq '200') {
            [void] $PresentContexts.Add($name)
        }
        continue
    }
    if ($IsDryRun) {
        Write-Line "### context $name"
        Write-Line (ConvertTo-MaskedJson $entry.Payload)
        [void] $PresentContexts.Add($name)
        continue
    }
    $encoded = Get-SafeName $name
    $status = Invoke-CodefreshApi GET "/contexts/$encoded"
    if ($status -eq '200') {
        $method, $path, $verb = 'PUT', "/contexts/$encoded", 'replaced'
    }
    elseif ($status -eq '404') {
        $method, $path, $verb = 'POST', '/contexts', 'created'
    }
    else {
        Add-Failure "context ${name}: lookup returned HTTP ${status}: $(Get-ResponseHead)"
        continue
    }
    $status = Invoke-CodefreshApi $method $path (ConvertTo-RequestBody $entry.Payload)
    if (Test-Success $status) {
        Write-Line "context ${name}: $verb"
        [void] $PresentContexts.Add($name)
    }
    else {
        Add-Failure "context ${name}: $method returned HTTP ${status}: $(Get-ResponseHead)"
    }
}

# ---------------------------------------------------------------- registry integrations
$registries = $null
foreach ($entry in @(Get-PlanEntry 'registry')) {
    $name = $entry.Name
    if ($null -eq $entry.Payload) {
        $Pending.Add("registry ${name}: set $($entry.Missing -join ' ')")
        continue
    }
    if ($IsDryRun) {
        Write-Line "### registry $name"
        Write-Line (ConvertTo-MaskedJson $entry.Payload)
        continue
    }
    if ($null -eq $registries) {
        $status = Invoke-CodefreshApi GET '/registries'
        if ($status -ne '200') {
            Add-Failure "registries: list returned HTTP ${status}: $(Get-ResponseHead)"
            break
        }
        try {
            $listed = ConvertFrom-Json -InputObject $script:response -AsHashtable -NoEnumerate
        }
        catch {
            Add-Failure "registries: the list is not JSON: $(Get-ResponseHead)"
            break
        }
        if ($listed -is [System.Collections.IDictionary]) {
            $registries = @(Get-SequenceItem $(if (Test-Truthy (Get-Key $listed 'docs')) { , $listed['docs'] } else { , (Get-Key $listed 'items') }))
        }
        else {
            $registries = @(Get-SequenceItem $listed)
        }
    }
    $id = ''
    foreach ($registry in $registries) {
        if ((Get-Key $registry 'name') -ceq $name) {
            $id = @((Get-Key $registry '_id'), (Get-Key $registry 'id')) | Where-Object { Test-Truthy $_ } | Select-Object -First 1
            break
        }
    }
    if ($id) {
        $status = Invoke-CodefreshApi PATCH "/registries/$id" (ConvertTo-RequestBody $entry.Payload)
        $verb = 'replaced'
    }
    else {
        $status = Invoke-CodefreshApi POST '/registries' (ConvertTo-RequestBody $entry.Payload)
        $verb = 'created'
    }
    if (Test-Success $status) {
        Write-Line "registry ${name}: $verb"
    }
    else {
        Add-Failure "registry ${name}: returned HTTP ${status}: $(Get-ResponseHead)"
    }
}

# ---------------------------------------------------------------- pipelines
$registered = [System.Collections.Generic.List[object]]::new()
foreach ($entry in @(Get-PlanEntry 'pipeline')) {
    $name = $entry.Name
    if ($entry.Error) {
        Add-Failure "pipeline ${name}: $($entry.Error)"
        continue
    }
    $payload = $entry.Payload
    if ($Mode -ne 'preview' -and -not $IsDryRun) {
        $missingRequired = @($entry.Required | Where-Object { -not (Test-Context $_) })
        if ($missingRequired.Count -gt 0) {
            Add-Failure "pipeline ${name}: context(s) $($missingRequired -join ' ') do not exist; supply their values and rerun"
            continue
        }
        $drop = @($entry.Optional | Where-Object { -not (Test-Context $_) })
        if ($drop.Count -gt 0) {
            Write-Note "WARN ${name}: optional context(s) $($drop -join ' ') absent; attached next time they exist"
            $kept = @(Get-SequenceItem (Get-Key $payload['spec'] 'contexts') | Where-Object { $_ -cnotin $drop })
            $payload['spec']['contexts'] = [System.Collections.Generic.List[object]]::new([object[]] $kept)
        }
    }
    if ($IsDryRun) {
        Write-Line "### pipeline $name"
        Write-Line (ConvertTo-PayloadJson $payload)
        continue
    }
    $body = ConvertTo-RequestBody $payload
    $encoded = Get-SafeName $name
    $status = Invoke-CodefreshApi GET "/pipelines/$encoded"
    if ($status -eq '200') {
        $method, $path, $verb = 'PUT', "/pipelines/${encoded}?disableRevisionCheck=true", 'replaced'
    }
    elseif ($status -eq '404') {
        $method, $path, $verb = 'POST', '/pipelines', 'created'
    }
    else {
        Add-Failure "pipeline ${name}: lookup returned HTTP ${status}: $(Get-ResponseHead)"
        continue
    }
    $status = Invoke-CodefreshApi $method $path $body
    if (-not (Test-Success $status)) {
        Add-Failure "pipeline ${name}: $method returned HTTP ${status}: $(Get-ResponseHead)"
    }
    elseif ($entry.Frozen) {
        Write-Line "pipeline ${name}: $verb (runtime $CfRuntime); every trigger off, the app is frozen ($($entry.Frozen))"
    }
    else {
        Write-Line "pipeline ${name}: $verb (runtime $CfRuntime)"
        $registered.Add([pscustomobject]@{ Name = $name; Payload = $payload; Body = $body })
    }
}

# ---------------------------------------------------------------- webhooks
# Every git trigger repository of the registered pipelines needs a webhook record (see Webhooks). The pipelines of a
# frozen app are not in $registered: their triggers are off.
$hooks = @{}
foreach ($pipeline in $registered) {
    foreach ($trigger in @(Get-SequenceItem (Get-Key $pipeline.Payload['spec'] 'triggers'))) {
        $type = if ($trigger.Contains('type')) { $trigger['type'] } else { 'git' }
        $repo, $context = (Get-Key $trigger 'repo'), (Get-Key $trigger 'context')
        if ($type -cne 'git' -or -not (Test-Truthy $repo) -or -not (Test-Truthy $context)) {
            continue
        }
        $key = "$repo $context"
        if (-not $hooks.ContainsKey($key)) {
            $owner, $repository = if ($repo.Contains('/')) { $repo.Split('/', 2) } else { $repo, $repo }
            $hooks[$key] = $false
            if ((Invoke-CodefreshApi GET "/repos/webhooks/$owner/$repository/github/$(Get-SafeName $context)") -eq '200') {
                try {
                    $hooks[$key] = Test-Truthy (Get-Key (ConvertFrom-Json -InputObject $script:response -AsHashtable) 'endpoint')
                }
                catch {
                    $hooks[$key] = $false
                }
            }
        }
        if ($hooks[$key]) {
            continue
        }
        if (-not $RecreateMissingHooks) {
            Write-Note "WARN $($pipeline.Name): no Codefresh webhook for $repo (context $context); pushes will not start it. Rerun with --recreate-missing-hooks."
        }
        elseif ((Invoke-CodefreshApi DELETE "/pipelines/$(Get-SafeName $pipeline.Name)") -eq '200' -and (Test-Success (Invoke-CodefreshApi POST '/pipelines' $pipeline.Body))) {
            Write-Line "pipeline $($pipeline.Name): recreated so that Codefresh installs the webhook of $repo"
        }
        else {
            Add-Failure "pipeline $($pipeline.Name): recreation for the webhook of $repo failed: $(Get-ResponseHead)"
        }
    }
}

# ---------------------------------------------------------------- prune
foreach ($entry in @(Get-PlanEntry 'prune')) {
    $what, $name = $entry.What, $entry.Name
    if ($IsDryRun) {
        Write-Line "### prune $what $name"
        continue
    }
    $status = Invoke-CodefreshApi DELETE "/$($what)s/$(Get-SafeName $name)"
    if (Test-Success $status) {
        Write-Line "$what ${name}: deleted"
    }
    elseif ($status -eq '404') {
        Write-Line "$what ${name}: already absent"
    }
    else {
        Add-Failure "$what ${name}: delete returned HTTP ${status}: $(Get-ResponseHead)"
    }
}

# ---------------------------------------------------------------- plan (dry run)
# One line per object, in the order a real run acts on them; never a value.
function Get-TriggerSummary($Spec) {
    $triggers = foreach ($key in @('triggers', 'cronTriggers')) {
        foreach ($trigger in @(Get-SequenceItem (Get-Key $Spec $key))) {
            $kind = if ($key -eq 'cronTriggers') { 'cron' } elseif ($trigger.Contains('type')) { ConvertTo-PyString $trigger['type'] } else { 'git' }
            '{0} ({1}) {2}' -f (ConvertTo-PyString (Get-Key $trigger 'name')), $kind, $(if (Test-Truthy (Get-Key $trigger 'disabled')) { 'off' } else { 'on' })
        }
    }
    return $(if ($triggers) { $triggers -join ', ' } else { 'none' })
}

function Get-PlanLine($Entry) {
    switch ($Entry.Kind) {
        'project' { return 'create if missing' }
        'prune' { return "delete $($Entry.What) if present" }
        'registry' {
            if ($null -eq $Entry.Payload) { return "PENDING: set $($Entry.Missing -join ' ')" }
            return "create or replace$(if ($Entry.Primary) { ' (primary)' }): domain, username, password"
        }
        'context' {
            if ($null -eq $Entry.Payload) { return "PENDING: set $($Entry.Missing -join ' ')" }
            return "create or replace$(if ($Entry.Optional) { ' (optional)' }): $(if ($Entry.Sources) { $Entry.Sources -join ', ' } else { 'no data' })"
        }
        'pipeline' {
            if ($Entry.Error) { return "REFUSED: $($Entry.Error)" }
            $contexts = @($Entry.Required) + @($Entry.Optional | ForEach-Object { "$_ (optional, attached if it exists)" })
            $frozen = if ($Entry.Frozen) { "; FROZEN ($($Entry.Frozen))" } else { '' }
            return "create or replace$frozen; triggers: $(Get-TriggerSummary $Entry.Payload['spec']); contexts: $(if ($contexts) { $contexts -join ', ' } else { 'none' })"
        }
    }
}

if ($IsDryRun) {
    Write-Line "### PLAN ($Mode): what a run without --dry-run does, in this order; no value is shown"
    foreach ($entry in $Plan) {
        Write-Line ('  {0,-9}{1,-38} {2}' -f $entry.Kind, $entry.Name, (Get-PlanLine $entry))
    }
}

# ---------------------------------------------------------------- summary
foreach ($item in $Pending) {
    Write-Note "PENDING $item"
}
if ($IsDryRun) {
    Write-Line "### DRY RUN ($Mode): no API call made"
}
if ($script:failures -gt 0) {
    Stop-Run "$($script:failures) error(s); fix them and rerun (the script is idempotent)"
}
Write-Line "done ($Mode): $(@(Get-PlanEntry 'pipeline').Count) pipeline(s), $(@(Get-PlanEntry 'project').Count) project(s); runtime $CfRuntime"
exit 0
