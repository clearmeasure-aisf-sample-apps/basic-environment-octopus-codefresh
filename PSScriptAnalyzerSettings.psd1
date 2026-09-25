# PSScriptAnalyzer settings of every PowerShell script in this repository (docs/scripting.md).
# scripts/checks/validate-all.ps1 powershell runs the analyzer with this file.
@{
    Severity     = @('Error', 'Warning')
    ExcludeRules = @(
        # The log of a check or a pipeline step is for people; data goes out with Write-Output.
        'PSAvoidUsingWriteHost',
        # Scripts, not modules: a Set-/New- helper inside a script does not take -WhatIf.
        'PSUseShouldProcessForStateChangingFunctions',
        # PowerShell 7 reads UTF-8 without a BOM, and a BOM would break the '#!/usr/bin/env pwsh' line.
        'PSUseBOMForUnicodeEncodedFile'
    )
    Rules        = @{
        PSUseCompatibleSyntax = @{
            Enable         = $true
            TargetVersions = @('7.4')
        }
    }
}
