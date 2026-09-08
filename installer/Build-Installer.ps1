[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.0.0',

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'BetterBluetoothAudioConnector\BetterBluetoothAudioConnector.csproj'
$installerScript = Join-Path $PSScriptRoot 'BetterBluetoothAudioConnector.iss'
$publishDirectory = Join-Path $repositoryRoot 'BetterBluetoothAudioConnector\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$applicationPath = Join-Path $publishDirectory 'Better Bluetooth Audio Connector.exe'
$resourceIndexPath = Join-Path $publishDirectory 'resources.pri'
$installerDirectory = Join-Path $repositoryRoot 'artifacts\installer'
$installerPath = Join-Path $installerDirectory "BetterBluetoothAudioConnector-Setup-$Version-x64.exe"

if (-not $SkipPublish) {
    & dotnet publish $projectPath --configuration Release --no-restore -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}

foreach ($requiredFile in @($applicationPath, $resourceIndexPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Required publish output was not found: $requiredFile"
    }
}

$compilerCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
)

$compilerPath = $compilerCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1

if (-not $compilerPath) {
    throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isdl.php and run this script again.'
}

& $compilerPath '/Qp' "/DAppVersion=$Version" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "The expected installer was not produced: $installerPath"
}

$installer = Get-Item -LiteralPath $installerPath
$hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256

[pscustomobject]@{
    Installer = $installer.FullName
    SizeMiB = [math]::Round($installer.Length / 1MB, 2)
    SHA256 = $hash.Hash
}
