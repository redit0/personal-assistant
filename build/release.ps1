#Requires -Version 7
<#
.SYNOPSIS
Builds an installer and update packages with Velopack, and optionally publishes them as a GitHub release.

.DESCRIPTION
Publishes the tray app self-contained for win-x64 (so no .NET runtime needs to be installed), packs it with
vpk into artifacts/releases (Setup.exe, a full package, and a delta from the previous release when one is
found), and with -Repo uploads it as a GitHub release that installed copies update from.

The pack ID is PersonalAssistantApp, so Velopack installs to %LOCALAPPDATA%\PersonalAssistantApp. The app's
data stays in %LOCALAPPDATA%\PersonalAssistant, which uninstalling doesn't touch.

.EXAMPLE
./build/release.ps1 -Version 0.2.0
Builds artifacts/releases locally without publishing.

.EXAMPLE
./build/release.ps1 -Version 0.2.0 -Repo https://github.com/you/personal-assistant
Builds and publishes a release. The token comes from -Token, $env:GITHUB_TOKEN, or `gh auth token`.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$Repo,
    [string]$Token,
    [switch]$Prerelease
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish'
$releases = Join-Path $root 'artifacts\releases'

if (-not $Version) {
    [xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if (-not $Version) { throw 'Pass -Version, or set <Version> in Directory.Build.props.' }

if ($Repo -and -not $Token) {
    $Token = $env:GITHUB_TOKEN
    if (-not $Token -and (Get-Command gh -ErrorAction SilentlyContinue)) { $Token = (gh auth token 2>$null) }
    if (-not $Token) { throw 'Publishing needs a GitHub token with contents write access: pass -Token, set GITHUB_TOKEN, or sign in with gh.' }
}

Push-Location $root
try {
    dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
    Write-Host "Publishing Personal Assistant $Version..."
    dotnet publish src\Assistant.App\Assistant.App.csproj -c Release -r win-x64 --self-contained -o $publish -p:Version=$Version --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    New-Item -ItemType Directory -Force $releases | Out-Null
    if ($Repo) {
        # The previous release lets vpk build a small delta package. The first release has none, which is fine.
        Write-Host 'Fetching the previous release for a delta...'
        $pre = if ($Prerelease) { @('--pre') } else { @() }
        dotnet vpk download github --repoUrl $Repo --token $Token -o $releases @pre
        if ($LASTEXITCODE -ne 0) { Write-Warning 'No previous release found; building a full package only.' }
    }

    Write-Host 'Packing...'
    dotnet vpk pack --packId PersonalAssistantApp --packVersion $Version --packDir $publish --mainExe PersonalAssistant.exe `
        --packTitle 'Personal Assistant' --packAuthors 'redit0' --icon src\Assistant.App\assistant.ico -o $releases
    if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

    if ($Repo) {
        Write-Host "Publishing release v$Version to $Repo..."
        $pre = if ($Prerelease) { @('--pre') } else { @() }
        dotnet vpk upload github --repoUrl $Repo --token $Token -o $releases --publish --tag "v$Version" `
            --releaseName "Personal Assistant $Version" @pre
        if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed.' }
    }

    Write-Host ''
    Write-Host "Done. Installer: $(Join-Path $releases 'PersonalAssistantApp-win-Setup.exe')"
}
finally {
    Pop-Location
}
