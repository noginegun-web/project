param(
    [string]$TemplateDir = 'C:\Users\User\Desktop\SCUM_OXYGEN_FTP_READY_2026-03-17',
    [string]$OutputDir = 'C:\Users\User\Desktop\SCUM_OXYGEN_HOSTING_DROPIN_2026-03-17'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimePublishDir = Join-Path $repoRoot 'out\publish\ScumOxygen_win64'
$bootstrapPublishDir = Join-Path $repoRoot 'out\publish\ScumOxygen_Bootstrap_win64'
$proxyDll = Join-Path $repoRoot 'src\ScumOxygen.ServerProxy\build_hosted\bin\Release\version.dll'
$nativeCandidates = @(
    (Join-Path $repoRoot 'src\ScumOxygen.Native\build_codemod\Release\ScumOxygen.Native.dll'),
    (Join-Path $repoRoot 'src\ScumOxygen.Native\build\Release\ScumOxygen.Native.dll')
)
$bootstrapRuntimeConfig = Join-Path $bootstrapPublishDir 'ScumOxygen.Bootstrap.runtimeconfig.json'
$sampleDir = Join-Path $repoRoot 'src\ScumOxygen.SamplePlugin\bin\Release\net8.0-windows'
$webSourceDir = Join-Path $repoRoot 'src\ScumOxygen.Control\wwwroot'
$pluginSourceDir = Join-Path $repoRoot 'dist\server\oxygen\plugins'

if (-not (Test-Path $TemplateDir)) {
    throw "TemplateDir not found: $TemplateDir"
}

if (-not (Test-Path $runtimePublishDir)) {
    throw "Runtime publish dir not found: $runtimePublishDir. Run dotnet publish first."
}

if (-not (Test-Path $bootstrapPublishDir)) {
    throw "Bootstrap publish dir not found: $bootstrapPublishDir. Run dotnet publish first."
}

if (-not (Test-Path $proxyDll)) {
    throw "Proxy DLL not found: $proxyDll. Build ScumOxygen.ServerProxy first."
}

$nativeDll = $nativeCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $nativeDll) {
    throw "Native DLL not found. Build ScumOxygen.Native first."
}

$runtimeVersion = ((Get-Content $bootstrapRuntimeConfig -Raw | ConvertFrom-Json).runtimeOptions.framework.version)
$hostFxrRoot = 'C:\Program Files\dotnet\host\fxr'
$runtimeRoot = 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App'

$resolvedRuntimeVersion = $runtimeVersion
if (-not (Test-Path (Join-Path $hostFxrRoot $resolvedRuntimeVersion))) {
    $resolvedRuntimeVersion = Get-ChildItem $hostFxrRoot -Directory | Sort-Object Name | Select-Object -Last 1 -ExpandProperty Name
}

if (-not (Test-Path (Join-Path $runtimeRoot $resolvedRuntimeVersion))) {
    $resolvedRuntimeVersion = Get-ChildItem $runtimeRoot -Directory | Sort-Object Name | Select-Object -Last 1 -ExpandProperty Name
}

$hostFxrSource = Join-Path (Join-Path $hostFxrRoot $resolvedRuntimeVersion) 'hostfxr.dll'
$runtimeSource = Join-Path $runtimeRoot $resolvedRuntimeVersion

if (-not (Test-Path $hostFxrSource)) {
    throw "hostfxr.dll not found: $hostFxrSource"
}

if (-not (Test-Path $runtimeSource)) {
    throw "Microsoft.NETCore.App runtime not found: $runtimeSource"
}

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

Copy-Item $TemplateDir $OutputDir -Recurse -Force

$uploadDir = Join-Path $OutputDir 'UPLOAD_TO_SERVER'
$targetScumOxygen = Join-Path $uploadDir 'ScumOxygen'

Copy-Item $proxyDll (Join-Path $uploadDir 'version.dll') -Force

if (Test-Path $targetScumOxygen) {
    Remove-Item $targetScumOxygen -Recurse -Force
}
New-Item -ItemType Directory -Path $targetScumOxygen | Out-Null

Copy-Item (Join-Path $bootstrapPublishDir '*') $targetScumOxygen -Recurse -Force
Copy-Item (Join-Path $runtimePublishDir '*') $targetScumOxygen -Recurse -Force
Remove-Item (Join-Path $targetScumOxygen 'ScumOxygen.Core.dll') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $targetScumOxygen 'ScumOxygen.Core.pdb') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $targetScumOxygen 'ScumOxygen.Core.deps.json') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $runtimePublishDir 'ScumOxygen.Core.dll') (Join-Path $targetScumOxygen 'ScumOxygen.Runtime.dll') -Force
Copy-Item (Join-Path $runtimePublishDir 'ScumOxygen.Core.pdb') (Join-Path $targetScumOxygen 'ScumOxygen.Runtime.pdb') -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $runtimePublishDir 'ScumOxygen.Core.deps.json') (Join-Path $targetScumOxygen 'ScumOxygen.Runtime.deps.json') -Force -ErrorAction SilentlyContinue
Copy-Item $nativeDll (Join-Path $targetScumOxygen 'ScumOxygen.Native.dll') -Force
Copy-Item (Join-Path $TemplateDir 'UPLOAD_TO_SERVER\ScumOxygen\oxygen') (Join-Path $targetScumOxygen 'oxygen') -Recurse -Force

$runtimesDir = Join-Path $targetScumOxygen 'runtimes'
if (Test-Path $runtimesDir) {
    Get-ChildItem $runtimesDir -Directory | Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force
}

$oxygenRoot = Join-Path $targetScumOxygen 'oxygen'
$cacheDir = Join-Path $oxygenRoot 'cache'
$logsDir = Join-Path $oxygenRoot 'logs'
if (Test-Path $cacheDir) {
    Get-ChildItem $cacheDir -Force | Remove-Item -Recurse -Force
}
if (Test-Path $logsDir) {
    Get-ChildItem $logsDir -Force | Remove-Item -Recurse -Force
}

$targetWebDir = Join-Path $targetScumOxygen 'oxygen\web'
if (Test-Path $webSourceDir) {
    if (Test-Path $targetWebDir) {
        Remove-Item $targetWebDir -Recurse -Force
    }
    Copy-Item $webSourceDir $targetWebDir -Recurse -Force

    $localPanelDir = Join-Path $OutputDir 'LOCAL_PANEL'
    if (Test-Path $localPanelDir) {
        Remove-Item $localPanelDir -Recurse -Force
    }
    Copy-Item $webSourceDir $localPanelDir -Recurse -Force
}

$targetPluginSourceDir = Join-Path $targetScumOxygen 'oxygen\plugins'
if (Test-Path $pluginSourceDir) {
    New-Item -ItemType Directory -Path $targetPluginSourceDir -Force | Out-Null
    Copy-Item (Join-Path $pluginSourceDir '*') $targetPluginSourceDir -Recurse -Force
}

$pluginsDir = Join-Path $targetScumOxygen 'Plugins'
New-Item -ItemType Directory -Path $pluginsDir | Out-Null
Copy-Item (Join-Path $runtimePublishDir 'ScumOxygen.Core.dll') (Join-Path $pluginsDir 'ScumOxygen.Core.dll') -Force
Copy-Item (Join-Path $runtimePublishDir 'ScumOxygen.Core.pdb') (Join-Path $pluginsDir 'ScumOxygen.Core.pdb') -Force -ErrorAction SilentlyContinue

$sampleDll = Join-Path $sampleDir 'ScumOxygen.SamplePlugin.dll'
$samplePdb = Join-Path $sampleDir 'ScumOxygen.SamplePlugin.pdb'
$sampleDeps = Join-Path $sampleDir 'ScumOxygen.SamplePlugin.deps.json'
if (Test-Path $sampleDll) {
    Copy-Item $sampleDll (Join-Path $pluginsDir 'ScumOxygen.SamplePlugin.dll') -Force
}
if (Test-Path $samplePdb) {
    Copy-Item $samplePdb (Join-Path $pluginsDir 'ScumOxygen.SamplePlugin.pdb') -Force
}
if (Test-Path $sampleDeps) {
    Copy-Item $sampleDeps (Join-Path $pluginsDir 'ScumOxygen.SamplePlugin.deps.json') -Force
}

$dotnetRoot = Join-Path $targetScumOxygen 'dotnet'
$fxrTarget = Join-Path $dotnetRoot "host\fxr\$resolvedRuntimeVersion"
$runtimeTarget = Join-Path $dotnetRoot "shared\Microsoft.NETCore.App\$resolvedRuntimeVersion"
New-Item -ItemType Directory -Path $fxrTarget -Force | Out-Null
New-Item -ItemType Directory -Path $runtimeTarget -Force | Out-Null
Copy-Item $hostFxrSource (Join-Path $fxrTarget 'hostfxr.dll') -Force
Copy-Item (Join-Path $runtimeSource '*') $runtimeTarget -Recurse -Force

$runtimeJsonPath = Join-Path $targetScumOxygen 'oxygen\configs\runtime.json'
$runtimeJson = [ordered]@{
    EnableLocalWeb = $true
    LocalWebPrefix = 'http://+:8090/'
    ApiKey = 'ApiKey'
    AllowedIps = @()
    EnableCors = $true
    ServerId = ''
    ServerName = ''
    DatabasePath = ''
    MapImageUrl = 'https://scum-map.com/images/interactive_map/scum/island.jpg'
    MapSourceUrl = 'https://scum-map.com/en/map/'
    MapMinX = -905369.6875
    MapMaxX = 619646.5625
    MapMinY = -904357.625
    MapMaxY = 619659.75
    MapInvertX = $true
    MapInvertY = $true
}
$runtimeJson | ConvertTo-Json -Depth 5 | Set-Content $runtimeJsonPath -Encoding UTF8

$antiVpnConfigPath = Join-Path $targetScumOxygen 'oxygen\configs\Anti-VPN_System.json'
$antiVpnConfig = [ordered]@{
    Enabled = $false
    KickMessage = 'VPN or Proxy connections are not allowed on this server.'
    BypassPermission = 'antivpn.bypass'
}
$antiVpnConfig | ConvertTo-Json -Depth 3 | Set-Content $antiVpnConfigPath -Encoding UTF8

$panelFiles = @(
    (Join-Path $OutputDir 'LOCAL_PANEL\app.js'),
    (Join-Path $targetScumOxygen 'oxygen\web\app.js')
)

foreach ($panelFile in $panelFiles) {
    if (-not (Test-Path $panelFile)) { continue }
    $content = Get-Content $panelFile -Raw -Encoding UTF8
    $content = $content.Replace("host: '',", "host: '109.248.4.59:8090',")
    $content = $content.Replace("apiKey: ''", "apiKey: 'ApiKey'")
    Set-Content $panelFile $content -Encoding UTF8
}

$readme = @"
SCUM_OXYGEN_HOSTING_DROPIN_2026-03-17

Что заливать на хостинг:
1. Открой папку UPLOAD_TO_SERVER
2. Всё её содержимое залей в папку SCUM\Binaries\Win64 на хостинге
3. Если хостинг спросит про замену файлов - соглашайся
4. Ничего не редактируй - ApiKey уже прописан
5. Перезапусти сервер в панели хостинга

Что открывать потом:
- Игра: 109.248.4.59:7008
- Панель: http://109.248.4.59:8090/  (если хостинг не блокирует этот порт)
- Локальная панель: LOCAL_PANEL\index.html
- В локальной панели уже вшиты 109.248.4.59:8090 и ApiKey
"@
$readme | Set-Content (Join-Path $OutputDir 'README_QUICKSTART_RU.txt') -Encoding UTF8

Write-Output "READY: $OutputDir"
