[CmdletBinding()]
param(
    [string]$DotnetCommand = 'dotnet',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [string]$ReleaseSigningPropertiesFile,
    [string]$ArtifactsPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $repoRoot 'artifacts/android' }
$buildArguments = @('build', (Join-Path $repoRoot 'android/Brs.AndroidHost.csproj'), '-t:SignAndroidPackage',
    '--configuration', $Configuration, '--artifacts-path', ([IO.Path]::GetFullPath($ArtifactsPath)), '--verbosity', 'minimal')
foreach ($propertyName in @('AndroidSdkDirectory', 'JavaSdkDirectory', 'ReleaseSigningPropertiesFile')) {
    $propertyValue = Get-Variable -Name $propertyName -ValueOnly
    if ($propertyValue) { $buildArguments += "-p:${propertyName}=$([IO.Path]::GetFullPath($propertyValue))" }
}
& $DotnetCommand @buildArguments
if ($LASTEXITCODE -ne 0) { throw "Android build failed (exit $LASTEXITCODE)." }
