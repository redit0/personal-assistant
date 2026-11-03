#Requires -Version 7
<#
.SYNOPSIS
Builds the stdio bridge and packages it as a Claude Desktop extension (.mcpb).

.DESCRIPTION
Publishes Assistant.Mcp.Stdio as a self-contained win-x64 exe, stages it with extension/manifest.json
and extension/icon.png, stamps the version from Directory.Build.props, validates the manifest against
the MCPB v0.3 schema, and zips everything into artifacts/personal-assistant.mcpb.

A .mcpb file is a zip archive with manifest.json at its root. Packing it here avoids needing a recent
Node.js for the mcpb CLI. The schema in build/schemas comes from @anthropic-ai/mcpb 2.1.2 (MIT license,
copyright Anthropic, PBC).
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root 'artifacts\extension'
$output = Join-Path $root 'artifacts\personal-assistant.mcpb'

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'Version not found in Directory.Build.props.' }

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force (Join-Path $staging 'server') | Out-Null

Write-Host "Publishing bridge $version..."
dotnet publish (Join-Path $root 'src\Assistant.Mcp.Stdio\Assistant.Mcp.Stdio.csproj') `
    -c $Configuration -r win-x64 -o (Join-Path $staging 'server') --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Only the exe is needed; drop anything else publish leaves behind.
Get-ChildItem (Join-Path $staging 'server') | Where-Object { $_.Name -ne 'assistant-mcp.exe' } | Remove-Item -Recurse -Force

$manifest = Get-Content (Join-Path $root 'extension\manifest.json') -Raw | ConvertFrom-Json
$manifest.version = $version

# Claude Desktop refuses prompts that aren't declared in the manifest, so take them from the bridge itself.
$promptsJson = & (Join-Path $staging 'server\assistant-mcp.exe') --manifest-prompts
if ($LASTEXITCODE -ne 0 -or -not $promptsJson) { throw 'Could not read prompts from the bridge.' }
$manifest | Add-Member -NotePropertyName prompts -NotePropertyValue @($promptsJson | ConvertFrom-Json) -Force
$manifestJson = $manifest | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $staging 'manifest.json'), $manifestJson, [Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $root 'extension\icon.png') $staging

Write-Host 'Validating manifest...'
$schema = Get-Content (Join-Path $PSScriptRoot 'schemas\mcpb-manifest-v0.3.schema.json') -Raw
if (-not (Test-Json -Json $manifestJson -Schema $schema)) { throw 'manifest.json does not match the MCPB v0.3 schema.' }

$entryPoint = Join-Path $staging $manifest.server.entry_point
if (-not (Test-Path $entryPoint)) { throw "Entry point $($manifest.server.entry_point) is missing." }
if (-not (Test-Path (Join-Path $staging $manifest.icon))) { throw "Icon $($manifest.icon) is missing." }

Write-Host 'Packing...'
if (Test-Path $output) { Remove-Item -Force $output }
$zip = [IO.Compression.ZipFile]::Open($output, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $staging -Recurse -File) {
        # Zip entries must use forward slashes.
        $entryName = [IO.Path]::GetRelativePath($staging, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $zip.Dispose()
}

Write-Host "Packed $output ($([math]::Round((Get-Item $output).Length / 1MB, 1)) MB)"
