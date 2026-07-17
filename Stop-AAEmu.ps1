<#
.SYNOPSIS
    Stop the AAEmu stack: kill Login + Game server processes, stop the sidecar container, then stop the MySQL container.

.DESCRIPTION
    Companion to Start-AAEmu.ps1. Stops the dotnet-run processes for AAEmu.Login and
    AAEmu.Game (matching by command line), stops the aaemu-custom sidecar container,
    then runs `docker compose stop db`. The sidecar container and the database data
    volume are preserved - the sidecar is reused on next start; run
    `docker compose down -v` to wipe the DB.

.EXAMPLE
    .\Stop-AAEmu.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = $PSScriptRoot,
    [switch]$KeepDockerRunning
)

$RepoRoot = (Resolve-Path $RepoRoot).Path

function Stop-ProjectProcess([string]$ProjectName) {
    # Match dotnet processes whose command line references the project path.
    $pattern = [regex]::Escape($ProjectName)
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -match $pattern }
    foreach ($p in $procs) {
        try {
            Stop-Process -Id $p.ProcessId -Force -ErrorAction Stop
            Write-Host "    Stopped dotnet PID $($p.ProcessId) ($ProjectName)" -ForegroundColor Green
        } catch {
            Write-Warning "    Could not stop PID $($p.ProcessId): $_"
        }
    }
    if (-not $procs) { Write-Host "    No running dotnet process found for $ProjectName" -ForegroundColor DarkGray }
}

Write-Host "==> Stopping AAEmu.Game..." -ForegroundColor Cyan
Stop-ProjectProcess 'AAEmu.Game'

Write-Host "==> Stopping AAEmu.Login..." -ForegroundColor Cyan
Stop-ProjectProcess 'AAEmu.Login'

Write-Host "==> Stopping aaemu-custom sidecar..." -ForegroundColor Cyan
$existing = docker ps -a --filter "name=^/aaemu-custom$" --format "{{.Names}}" 2>$null
if ($existing -eq 'aaemu-custom') {
    docker stop aaemu-custom | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "docker stop aaemu-custom failed (exit $LASTEXITCODE)." }
    else { Write-Host "    Stopped container 'aaemu-custom' (preserved - reused on next start)." -ForegroundColor Green }
} else {
    Write-Host "    No 'aaemu-custom' container found." -ForegroundColor DarkGray
}

Write-Host "==> Stopping MySQL container..." -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    docker compose -f docker-compose.yaml -f docker-compose.dev.yaml stop db
} finally { Pop-Location }

if (-not $KeepDockerRunning) {
    Write-Host "    (Docker Desktop left running. Quit it from the tray if you want to fully stop Docker.)" -ForegroundColor DarkGray
}
Write-Host "==> Stopped. Data volume preserved." -ForegroundColor Green