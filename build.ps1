param(
    [string]$NotchCorePath = "$env:LOCALAPPDATA\Programs\Notch\Notch.Core.dll"
)

$ErrorActionPreference = 'Stop'
$pluginOutput = Join-Path $PSScriptRoot 'dist\brick-bread.paynow'
$projectFile = Join-Path $PSScriptRoot 'Notch.PayNow.csproj'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Raw | ConvertFrom-Json
$pluginVersion = [version]::Parse($manifest.version).ToString()
$archivePath = Join-Path $PSScriptRoot "dist\paynow-sales-$pluginVersion.zip"

dotnet publish $projectFile -c Release -o $pluginOutput "-p:NotchCorePath=$NotchCorePath" "-p:Version=$pluginVersion" --nologo
if ($LASTEXITCODE -ne 0) { throw 'The plugin build failed.' }
if (Test-Path -LiteralPath (Join-Path $pluginOutput 'Notch.Core.dll')) {
    throw 'Notch.Core.dll must not be shipped with a plugin.'
}
Remove-Item (Join-Path $pluginOutput '*.pdb') -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $pluginOutput '*') -DestinationPath $archivePath -Force
Write-Output "Plugin folder: $pluginOutput"
Write-Output "Release archive: $archivePath"
