[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [Parameter(Mandatory)] [string] $Godot,
    [string] $Sts2Path,
    [string] $BaseLibPath,
    [string] $CoinImage
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'CrookedPenny.csproj'
$assets = Join-Path $PSScriptRoot 'assets'
$output = Join-Path $PSScriptRoot 'artifacts\CrookedPenny'
$scratch = Join-Path $PSScriptRoot 'obj\coin-pack'
New-Item -ItemType Directory -Path $output, $scratch -Force | Out-Null

$buildArguments = @('build', $project, '--configuration', 'Release', '--nologo')
if ($Sts2Path) { $buildArguments += "-p:Sts2Path=$Sts2Path" }
if ($BaseLibPath) { $buildArguments += "-p:BaseLibPath=$BaseLibPath" }
& $Dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "Mod compilation failed ($LASTEXITCODE)." }

$pack = Join-Path $output 'CrookedPenny.pck'
$texture = Join-Path $scratch 'coin.res'
$packLog = Join-Path $scratch 'godot.log'
$localCoin = Join-Path $assets 'coin-source.png'
if (!$CoinImage) {
    $CoinImage = if (Test-Path -LiteralPath $localCoin) { $localCoin } else { Join-Path $assets 'coin-original.svg' }
}
$coinSource = (Resolve-Path -LiteralPath $CoinImage).Path
& $Godot --headless --log-file $packLog --path $assets --script 'res://pack.gd' -- $pack $texture $coinSource
if ($LASTEXITCODE -ne 0) { throw "Godot packaging failed ($LASTEXITCODE). See $packLog." }

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\net9.0\CrookedPenny.dll'),
    (Join-Path $PSScriptRoot 'CrookedPenny.json') -Destination $output -Force
Get-ChildItem -LiteralPath $output | Select-Object Name, Length
