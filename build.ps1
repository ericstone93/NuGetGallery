[CmdletBinding(DefaultParameterSetName='RegularBuild')]
param (
    [ValidateSet("debug", "release")]
    [string]$Configuration = 'debug',
    [int]$BuildNumber,
    [switch]$SkipRestore,
    [switch]$CleanCache,
    [string]$SimpleVersion = '1.0.0',
    [string]$SemanticVersion = '1.0.0-zlocal',
    [string]$Branch,
    [string]$CommitSHA
)

# For TeamCity - If any issue occurs, this script fails the build. - By default, TeamCity returns an exit code of 0 for all powershell scripts, even if they fail
trap {
    Write-Host "BUILD FAILED: $_" -ForegroundColor Red
    Write-Host "ERROR DETAILS:" -ForegroundColor Red
    Write-Host $_.Exception -ForegroundColor Red
    Write-Host ("`r`n" * 3)
    exit 1
}

. "$PSScriptRoot\build\common.ps1"

Function Clean-Tests {
    [CmdletBinding()]
    param()
    
    Trace-Log 'Cleaning test results'
    
    Remove-Item (Join-Path $PSScriptRoot "Results.*.xml")
}
    
Write-Host ("`r`n" * 3)
Trace-Log ('=' * 60)

$startTime = [DateTime]::UtcNow
if (-not $BuildNumber) {
    $BuildNumber = Get-BuildNumber
}
Trace-Log "Build #$BuildNumber started at $startTime"

$BuildErrors = @()

Invoke-BuildStep 'Getting private build tools' { Install-PrivateBuildTools } `
    -ev +BuildErrors

Invoke-BuildStep 'Cleaning test results' { Clean-Tests } `
    -ev +BuildErrors

Invoke-BuildStep 'Installing NuGet.exe' { Install-NuGet } `
    -ev +BuildErrors
    
Invoke-BuildStep 'Clearing package cache' { Clear-PackageCache } `
    -skip:(-not $CleanCache) `
    -ev +BuildErrors
    
Invoke-BuildStep 'Clearing artifacts' { Clear-Artifacts } `
    -ev +BuildErrors

Invoke-BuildStep 'Set version metadata in AssemblyInfo.cs' { `
        Set-VersionInfo -Path "$PSScriptRoot\src\NuGet.Services.KeyVault\Properties\AssemblyInfo.g.cs" -Version $SimpleVersion -Branch $Branch -Commit $CommitSHA
        Set-VersionInfo -Path "$PSScriptRoot\src\NuGet.Services.Logging\Properties\AssemblyInfo.g.cs" -Version $SimpleVersion -Branch $Branch -Commit $CommitSHA
        Set-VersionInfo -Path "$PSScriptRoot\src\NuGet.Services.Configuration\Properties\AssemblyInfo.g.cs" -Version $SimpleVersion -Branch $Branch -Commit $CommitSHA
    } `
    -ev +BuildErrors
    
Invoke-BuildStep 'Restoring solution packages' { `
    Install-SolutionPackages -path (Join-Path $PSScriptRoot ".nuget\packages.config") -output (Join-Path $PSScriptRoot "packages") -excludeversion } `
    -skip:$SkipRestore `
    -ev +BuildErrors
        
Invoke-BuildStep 'Building solution' { `
        $SolutionPath = Join-Path $PSScriptRoot "NuGet.Server.Common.sln"
        Build-Solution $Configuration $BuildNumber -MSBuildVersion "14" $SolutionPath -SkipRestore:$SkipRestore
    } `
    -ev +BuildErrors
    
Invoke-BuildStep 'Creating artifacts' { `
        New-Package (Join-Path $PSScriptRoot "src\NuGet.Services.KeyVault\NuGet.Services.KeyVault.csproj") -Configuration $Configuration -Symbols -IncludeReferencedProjects
        New-Package (Join-Path $PSScriptRoot "src\NuGet.Services.Logging\NuGet.Services.Logging.csproj") -Configuration $Configuration -Symbols -IncludeReferencedProjects
        New-Package (Join-Path $PSScriptRoot "src\NuGet.Services.Configuration\NuGet.Services.Configuration.csproj") -Configuration $Configuration -Symbols -IncludeReferencedProjects
    } `
    -ev +BuildErrors

Invoke-BuildStep 'Patching versions of artifacts' {`
        $NupkgWrenchExe = (Join-Path $PSScriptRoot "packages\NupkgWrench\tools\NupkgWrench.exe")
        
        Trace-Log "Patching versions of NuGet packages to $SemanticVersion"
        
        & $NupkgWrenchExe release "$Artifacts" --new-version $SemanticVersion
        
        Trace-Log "Done"
    }`
    -ev +BuildErrors
    
Trace-Log ('-' * 60)

## Calculating Build time
$endTime = [DateTime]::UtcNow
Trace-Log "Build #$BuildNumber ended at $endTime"
Trace-Log "Time elapsed $(Format-ElapsedTime ($endTime - $startTime))"

Trace-Log ('=' * 60)

if ($BuildErrors) {
    $ErrorLines = $BuildErrors | %{ ">>> $($_.Exception.Message)" }
    Error-Log "Builds completed with $($BuildErrors.Count) error(s):`r`n$($ErrorLines -join "`r`n")" -Fatal
}

Write-Host ("`r`n" * 3)