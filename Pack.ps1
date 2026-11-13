<#
.SYNOPSIS
    Builds the NuGet package into artifacts\, versioned by the build time (UTC) as yyyy.MM.dd.HHmm, like the other Fungusware projects.
    NuGet drops the leading zero of the last part, so 2026.11.13.0619 is the package version 2026.11.13.619.
.EXAMPLE
    .\Pack.ps1                  # builds artifacts\Fungusware.Mft.<version>.nupkg
    .\Pack.ps1 -Push <apikey>   # and pushes it to nuget.org
#>
param([string]$Push)

$ErrorActionPreference = 'Stop'
$stamp = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm')
$out = Join-Path $PSScriptRoot 'artifacts'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet pack (Join-Path $PSScriptRoot 'src\Fungusware.Mft') -c Release -o $out "-p:Version=$stamp" "-p:RepositoryCommit=$(git -C $PSScriptRoot rev-parse HEAD)"
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }

$package = Get-ChildItem $out -Filter *.nupkg | Select-Object -First 1
Write-Host "Package $($package.FullName)  (version $stamp)"

if ($Push) { dotnet nuget push $package.FullName --api-key $Push --source https://api.nuget.org/v3/index.json --skip-duplicate }
