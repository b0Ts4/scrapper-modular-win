<#
.SYNOPSIS
  Builds the Prescriva Agent MSIX package (Microsoft Store / sideload).

.DESCRIPTION
  Publishes the Desktop app self-contained for win-x64, lays out the package with the
  manifest and logos, and packs it with makeappx. For the Store, upload the unsigned
  package (the Store signs it); -PfxPath signs it for local/CI sideload testing only.

.EXAMPLE
  ./packaging/build-msix.ps1 -Version 1.0.0.0 -Name 12345Publisher.PrescrivaAgent -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "Nome do publicador"
#>
param(
    [string] $Version = "1.0.0.0",
    [string] $Name = "PrescrivaAgent.Dev",
    [string] $Publisher = "CN=Prescriva Agent Dev",
    [string] $PublisherDisplayName = "Prescriva Agent (desenvolvimento)",
    [string] $DisplayName = "Prescriva Agent (desenvolvimento)",
    [switch] $Store,
    [string] $Output = "out/msix",
    [string] $PfxPath,
    [string] $PfxPassword
)

$ErrorActionPreference = "Stop"

# -Store: the identity reserved in Partner Center (store-identity.json), unsigned (the Store signs).
if ($Store) {
    $identity = Get-Content (Join-Path $PSScriptRoot "store-identity.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    $Name = $identity.name
    $Publisher = $identity.publisher
    $PublisherDisplayName = $identity.publisherDisplayName
    $DisplayName = $identity.displayName
    if ($PfxPath) { throw "Do not sign the Store package: the Store signs it." }
}
$root = Split-Path -Parent $PSScriptRoot
$output = [System.IO.Path]::GetFullPath((Join-Path $root $Output))
$layout = Join-Path $output "layout"
$package = Join-Path $output "$($Name)_$($Version)_x64.msix"

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
New-Item -ItemType Directory -Path $layout | Out-Null

dotnet publish (Join-Path $root "src/Prescriva.Agent.Desktop/Prescriva.Agent.Desktop.csproj") `
    --configuration Release --runtime win-x64 --self-contained true --output $layout
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $layout "Assets") -Recurse
$manifest = Get-Content (Join-Path $PSScriptRoot "AppxManifest.xml") -Raw -Encoding UTF8
$manifest = $manifest.Replace("{{Name}}", $Name).Replace("{{Publisher}}", [System.Security.SecurityElement]::Escape($Publisher)).
    Replace("{{PublisherDisplayName}}", [System.Security.SecurityElement]::Escape($PublisherDisplayName)).Replace("{{Version}}", $Version).
    Replace("{{DisplayName}}", [System.Security.SecurityElement]::Escape($DisplayName))
[System.IO.File]::WriteAllText((Join-Path $layout "AppxManifest.xml"), $manifest, [System.Text.UTF8Encoding]::new($false))

$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" |
    Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
if (-not $sdkBin) { throw "makeappx.exe not found: install the Windows 10/11 SDK." }

& $sdkBin.FullName pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed." }

if ($PfxPath) {
    $signtool = Join-Path $sdkBin.Directory.FullName "signtool.exe"
    & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword $package
    if ($LASTEXITCODE -ne 0) { throw "signtool sign failed." }
}

Write-Output "MSIX: $package"
