#Requires -Version 7.4

<#
.SYNOPSIS
    Get-DiagramInputHash: the input hash of a diagram's manifest line (dot-sourced by render.ps1 and check.ps1).

.DESCRIPTION
    SHA-256 over "plantuml <version>\n", "<name>.puml\n", the source, then for every include/*.puml in ordinal
    order "include/<file>\n" and its content; byte for byte, so the hash is the same on every platform.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Get-DiagramInputHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Directory,
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Version
    )
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    $stream = [System.IO.MemoryStream]::new()
    try {
        $append = {
            param([byte[]] $Bytes)
            $stream.Write($Bytes, 0, $Bytes.Length)
        }
        & $append $utf8.GetBytes("plantuml $Version`n")
        & $append $utf8.GetBytes("$Name.puml`n")
        & $append ([System.IO.File]::ReadAllBytes((Join-Path $Directory "$Name.puml")))
        $includes = [System.IO.Directory]::GetFiles((Join-Path $Directory 'include'), '*.puml') |
            Sort-Object { [System.IO.Path]::GetFileName($_) } -CaseSensitive
        foreach ($include in $includes) {
            & $append $utf8.GetBytes("include/$([System.IO.Path]::GetFileName($include))`n")
            & $append ([System.IO.File]::ReadAllBytes($include))
        }
        $stream.Position = 0
        return ([System.Security.Cryptography.SHA256]::HashData($stream.ToArray()) | ForEach-Object { $_.ToString('x2') }) -join ''
    }
    finally {
        $stream.Dispose()
    }
}
