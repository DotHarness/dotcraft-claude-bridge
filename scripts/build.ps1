$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")

$desktop = Join-Path $root 'desktop'
& npm install --prefix $desktop --no-audit --no-fund
if ($LASTEXITCODE) { throw 'Desktop module install failed.' }
& npm run build --prefix $desktop
if ($LASTEXITCODE) { throw 'Desktop module build failed.' }

$project = Join-Path $root 'src/DotCraft.ClaudeCode/DotCraft.ClaudeCode.csproj'
& dotnet build $project -c Release --nologo
if ($LASTEXITCODE) { throw 'Claude bridge build failed.' }

$staged = Join-Path $root 'src/DotCraft.ClaudeCode/bin/Release/net10.0/plugin'
$manifest = Get-Content -LiteralPath "$staged/.craft-plugin/plugin.json" -Raw | ConvertFrom-Json
$output = [IO.Path]::GetFullPath((Join-Path $root 'build'))
$bundle = [IO.Path]::GetFullPath((Join-Path $output $manifest.id))
if ((Split-Path -Path $bundle -Parent) -ne $output) {
    throw 'Plugin output must be inside the build directory.'
}
if (Test-Path -LiteralPath $bundle) {
    Remove-Item -LiteralPath $bundle -Recurse -Force
}
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
foreach ($name in @('.craft-plugin', 'settings.schema.json', 'assets', 'desktop', 'lib')) {
    Copy-Item -LiteralPath (Join-Path $staged $name) -Destination $bundle -Recurse
}

Write-Output "Plugin ready: $bundle"
