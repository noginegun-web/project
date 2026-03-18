param(
    [string]$PackageDir = 'C:\Users\User\Desktop\NDJ_RELAY_DROPIN_2026-03-18\UPLOAD_TO_SERVER',
    [string]$TestRoot = 'C:\Users\User\Desktop\_NDJ_RELAY_HOSTING_TEST'
)

$ErrorActionPreference = 'Stop'

if (Test-Path $TestRoot) {
    Remove-Item $TestRoot -Recurse -Force
}

Copy-Item $PackageDir $TestRoot -Recurse -Force

$logDir = Join-Path $TestRoot 'NeDjin\oxygen\logs'
if (Test-Path $logDir) {
    Get-ChildItem $logDir -File | Remove-Item -Force
}

$controlPath = Join-Path $TestRoot 'NeDjin\oxygen\configs\control.json'
Set-Content $controlPath '{"Enabled":false,"WsUrl":"","ServerId":"server-1","Token":""}' -Encoding UTF8

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class NativeLoad
{
    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32", SetLastError = true)]
    public static extern bool FreeLibrary(IntPtr hModule);
}
"@

$dllPath = Join-Path $TestRoot 'version.dll'
$handle = [NativeLoad]::LoadLibrary($dllPath)
Start-Sleep -Seconds 5

Write-Output "HANDLE=$handle"

try {
    $status = Invoke-WebRequest 'http://127.0.0.1:8090/api/status' -Headers @{ 'X-API-KEY' = 'ApiKey' } -UseBasicParsing -TimeoutSec 5
    Write-Output '---HTTP STATUS---'
    Write-Output $status.Content
} catch {
    Write-Output '---HTTP STATUS ERROR---'
    Write-Output $_.Exception.Message
}

try {
    $index = Invoke-WebRequest 'http://127.0.0.1:8090/' -UseBasicParsing -TimeoutSec 5
    Write-Output '---HTTP INDEX OK---'
    Write-Output $index.StatusCode
} catch {
    Write-Output '---HTTP INDEX ERROR---'
    Write-Output $_.Exception.Message
}

$proxyLog = Join-Path $TestRoot 'NeDjin\oxygen\logs\proxy.log'
$oxygenLog = Join-Path $TestRoot 'NeDjin\oxygen\logs\Oxygen.log'

if (Test-Path $proxyLog) {
    Write-Output '---PROXY---'
    Get-Content $proxyLog -Tail 50
}

if (Test-Path $oxygenLog) {
    Write-Output '---OXYGEN---'
    Get-Content $oxygenLog -Tail 50
}

if ($handle -ne [IntPtr]::Zero) {
    [void][NativeLoad]::FreeLibrary($handle)
}
