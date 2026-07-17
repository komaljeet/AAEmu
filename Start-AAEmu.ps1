<#
.SYNOPSIS
    One-click start for the AAEmu stack: Docker Desktop -> MySQL container -> Login -> Game.

.DESCRIPTION
    After a fresh boot, just run this script. It will:
      1. Start Docker Desktop (if not already running) and wait for the engine.
      2. Start the MySQL `db` container via docker compose and wait until it's healthy.
      3. Start the aaemu-custom Rust sidecar container (HTTP API on 127.0.0.1:1281) and wait for port 1281.
      4. Launch the Login server in its own window and wait for port 1237.
      5. Launch the Game server in its own window (cwd = AAEmu.Game, required for ClientData) and wait for port 1239.

    Config (.env) must contain DB_PASSWORD matching AAEmu.*/Config.Local.json (default: "password").
    Both Config.Local.json files must point MySQL at 127.0.0.1:3306.

    The sidecar image `aaemu-custom:latest` must be built once beforehand (a release
    build takes several minutes and is deliberately not done here):
      docker buildx build -t aaemu-custom:latest --load .\aaemu-custom
    The sidecar reads aaemu-custom\config.docker.toml (gitignored, holds DB creds);
    it reaches MySQL via host.docker.internal:3306.

.EXAMPLE
    .\Start-AAEmu.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = $PSScriptRoot,
    [int]$DockerTimeoutSec = 120,
    [int]$DbTimeoutSec = 120,
    [int]$ServerTimeoutSec = 180
)

$ErrorActionPreference = 'Stop'
$RepoRoot = (Resolve-Path $RepoRoot).Path

function Wait-Until([scriptblock]$Condition, [string]$Message, [int]$TimeoutSec) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Seconds 2
    }
    Write-Warning "Timed out waiting for: $Message"
    return $false
}

function Test-DockerReady {
    # `docker info` writes a benign "No blkio throttle.read_bps_device support"
    # warning to stderr on this host (WSL2/cgroup note) even when the engine is
    # fully ready. Under $ErrorActionPreference='Stop' that stderr line becomes a
    # terminating NativeCommandError, so `2>$null` alone can't suppress it and
    # "did it throw" is not a valid readiness signal. We drop to 'Continue' for
    # the call (scoped to this function) and use the exit code instead: 0 = ready.
    $ErrorActionPreference = 'Continue'
    $null = docker info 2>$null
    return ($LASTEXITCODE -eq 0)
}

function Test-PortOpen([string]$Computer, [int]$Port) {
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $iar = $c.BeginConnect($Computer, $Port, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(1500, $false)
        if ($ok -and $c.Connected) { $c.EndConnect($iar); return $true }
        return $false
    }
    catch { return $false }
    finally { if ($c) { $c.Close() } }
}

# 1. Start Docker Desktop -----------------------------------------------------
$dockerExe = "C:\Program Files\Docker\Docker\Docker Desktop.exe"
Write-Host "==> Checking Docker engine..." -ForegroundColor Cyan
$dockerReady = Test-DockerReady

if (-not $dockerReady) {
    if (Test-Path $dockerExe) {
        Write-Host "    Starting Docker Desktop..." -ForegroundColor Yellow
        Start-Process $dockerExe
    } else {
        throw "Docker Desktop not found at '$dockerExe'. Start it manually and re-run."
    }
    $ok = Wait-Until { Test-DockerReady } "Docker engine" $DockerTimeoutSec
    if (-not $ok) { throw "Docker engine did not become ready in $DockerTimeoutSec s." }
}
Write-Host "    Docker engine ready." -ForegroundColor Green

# 2. Start the MySQL db container --------------------------------------------
Write-Host "==> Starting MySQL container (docker compose up -d db)..." -ForegroundColor Cyan
Push-Location $RepoRoot
try {
    docker compose up -d db
    if ($LASTEXITCODE -ne 0) { throw "docker compose up -d db failed (exit $LASTEXITCODE)." }
} finally { Pop-Location }

Write-Host "    Waiting for db to be healthy..." -ForegroundColor Yellow
$ok = Wait-Until {
    $s = docker inspect --format '{{.State.Health.Status}}' archeage-db-1 2>$null
    $s -eq 'healthy'
} "db healthy" $DbTimeoutSec
if (-not $ok) { throw "MySQL container did not become healthy. Run: docker logs archeage-db-1" }
Write-Host "    MySQL container healthy on 127.0.0.1:3306." -ForegroundColor Green

# 3. Start the aaemu-custom sidecar ------------------------------------------
# The Rust sidecar exposes the HTTP API the C# Game server calls (127.0.0.1:1281).
# It only depends on MySQL, so it can come up before Login/Game. It runs as a
# detached container (not in docker-compose.yaml) so the nested sidecar repo stays
# decoupled from AAEmu's compose file. An existing container is reused on re-launch
# rather than recreated, to avoid name collisions and stale duplicates.
Write-Host "==> Starting aaemu-custom sidecar..." -ForegroundColor Cyan
$sidecarDir = Join-Path $RepoRoot 'aaemu-custom'
$sidecarConfig = Join-Path $sidecarDir 'config.docker.toml'
if (-not (Test-Path $sidecarConfig)) {
    throw "Sidecar config not found at '$sidecarConfig'. Copy config.example.toml -> config.docker.toml and edit the [database] connection."
}

$existing = docker ps -a --filter "name=^/aaemu-custom$" --format "{{.Names}}" 2>$null
if ($existing -eq 'aaemu-custom') {
    $state = docker inspect --format '{{.State.Running}}' aaemu-custom 2>$null
    if ($state -eq 'true') {
        Write-Host "    Container 'aaemu-custom' already running - reusing." -ForegroundColor DarkGray
    } else {
        Write-Host "    Starting existing 'aaemu-custom' container..." -ForegroundColor Yellow
        docker start aaemu-custom | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "docker start aaemu-custom failed (exit $LASTEXITCODE)." }
    }
} else {
    # Image must already be built - a release build takes several minutes and is
    # not done inside a launch script. Build it once with:
    #   docker buildx build -t aaemu-custom:latest --load .\aaemu-custom
    $img = docker images aaemu-custom:latest --format "{{.Repository}}:{{.Tag}}" 2>$null
    if ($img -ne "aaemu-custom:latest") {
        throw "Docker image 'aaemu-custom:latest' not found. Build it once: docker buildx build -t aaemu-custom:latest --load `"$sidecarDir`""
    }
    Write-Host "    Creating + starting 'aaemu-custom' container..." -ForegroundColor Yellow
    docker run -d --name aaemu-custom -p 1281:1281 `
        --add-host=host.docker.internal:host-gateway `
        -v "${sidecarConfig}:/app/config.toml:ro" `
        aaemu-custom:latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run aaemu-custom failed (exit $LASTEXITCODE)." }
}

Write-Host "    Waiting for sidecar port 1281..." -ForegroundColor Yellow
$ok = Wait-Until { Test-PortOpen '127.0.0.1' 1281 } "sidecar :1281" $ServerTimeoutSec
if (-not $ok) { Write-Warning "Sidecar did not open :1281 in time. Run: docker logs aaemu-custom" }
else { Write-Host "    Sidecar listening on 1281 (AAEmu calls 127.0.0.1:1281)." -ForegroundColor Green }

# 4. Start Login server ------------------------------------------------------
Write-Host "==> Launching Login server..." -ForegroundColor Cyan
$loginDir = Join-Path $RepoRoot 'AAEmu.Login'
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$loginDir'; dotnet run" -WorkingDirectory $loginDir

Write-Host "    Waiting for Login port 1237..." -ForegroundColor Yellow
$ok = Wait-Until { Test-PortOpen '127.0.0.1' 1237 } "Login :1237" $ServerTimeoutSec
if (-not $ok) { Write-Warning "Login did not open :1237 in time. Check its window." }
else { Write-Host "    Login listening on 1237 (clients) / 1234 (internal)." -ForegroundColor Green }

# 5. Start Game server (cwd MUST be AAEmu.Game) ------------------------------
Write-Host "==> Launching Game server..." -ForegroundColor Cyan
$gameDir = Join-Path $RepoRoot 'AAEmu.Game'
Start-Process powershell -ArgumentList "-NoExit", "-Command", "Set-Location '$gameDir'; dotnet run" -WorkingDirectory $gameDir

Write-Host "    Waiting for Game port 1239..." -ForegroundColor Yellow
# Game loads a ~24GB pak first; allow extra time.
$ok = Wait-Until { Test-PortOpen '127.0.0.1' 1239 } "Game :1239" ($ServerTimeoutSec * 2)
if (-not $ok) { Write-Warning "Game did not open :1239 in time. It may still be loading client data - check its window." }
else { Write-Host "    Game listening on 1239. Registration with Login happens during boot." -ForegroundColor Green }

Write-Host ""
Write-Host "==> Done. Stack is up:" -ForegroundColor Green
Write-Host "    MySQL :3306   Sidecar :1281   Login :1237 / :1234   Game :1239 / :1250"
Write-Host "    To stop everything, run: .\Stop-AAEmu.ps1"