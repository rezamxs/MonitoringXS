@{
    # Repository-level PSScriptAnalyzer settings for Monitoring XS.
    # Scope: scripts/**/*.ps1, installer scripts, broker management, validation.
    # Designed to avoid massive unrelated churn while catching real issues.

    Severity = @('Error', 'Warning', 'Information')

    IncludeRules = @(
        'PSAvoidUsingCmdletAliases'
        'PSAvoidUsingWriteHost'
        'PSAvoidUsingEmptyCatchBlock'
        'PSAvoidUsingPositionalParameters'
        'PSAvoidGlobalVars'
        'PSUseDeclaredVarsMoreThanAssignments'
        'PSUseShouldProcessForStateChangingFunctions'
        'PSMissingModuleManifestField'
        'PSAvoidUsingPlainTextForPassword'
        'PSAvoidUsingConvertToSecureStringWithPlainText'
        'PSUseCompatibleCommands'
        'PSUseCompatibleTypes'
    )

    ExcludeRules = @(
        # Style rules that would create churn in existing scripts
        'PSUseConsistentIndentation'
        'PSUseConsistentWhitespace'
        'PSAlignAssignmentStatement'
        'PSUseCorrectCasing'
        'PSPlaceOpenBrace'
        'PSPlaceCloseBrace'
    )

    Rules = @{
        PSAvoidUsingCmdletAliases = @{
            Enable = $true
        }
        PSAvoidUsingWriteHost = @{
            Enable = $true
            # Allow Write-Host in interactive validation scripts
            ExceptCommands = @()
        }
    }
}