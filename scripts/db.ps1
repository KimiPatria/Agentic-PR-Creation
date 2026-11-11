<#
.SYNOPSIS
  Runs the app's own PostgreSQL cluster from the locally installed PostgreSQL binaries. No Docker.

.DESCRIPTION
  The cluster lives in .data/postgres and listens on localhost:5433 only, so it never touches a
  PostgreSQL server you already run on 5432. On first start it is created with:
    user     pragent
    password pragent_dev
    database pragent
  which is what appsettings.Development.json expects.

  The binaries are found in this order: $env:PG_BIN, pg_ctl on PATH, the newest
  C:\Program Files\PostgreSQL\<version>\bin.

.EXAMPLE
  ./scripts/db.ps1 start     # create on first run, then start
  ./scripts/db.ps1 status
  ./scripts/db.ps1 stop
  ./scripts/db.ps1 reset     # delete all data and start fresh
#>
param(
    [ValidateSet('start', 'stop', 'status', 'reset')]
    [string]$Command = 'start'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dataDir = Join-Path $root '.data/postgres'
$logFile = Join-Path $root '.data/postgres.log'
$port = 5433
$user = 'pragent'
$password = 'pragent_dev'
$database = 'pragent'

function Find-PgBin {
    if ($env:PG_BIN -and (Test-Path (Join-Path $env:PG_BIN 'pg_ctl.exe'))) { return $env:PG_BIN }
    $onPath = Get-Command pg_ctl -ErrorAction SilentlyContinue
    if ($onPath) { return Split-Path -Parent $onPath.Source }
    $installed = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName 'bin\pg_ctl.exe') } |
        Sort-Object { [int]($_.Name -replace '\D', '') } -Descending |
        Select-Object -First 1
    if ($installed) { return Join-Path $installed.FullName 'bin' }
    throw 'PostgreSQL binaries not found. Install PostgreSQL (https://www.postgresql.org/download/windows/), or set PG_BIN to its bin folder.'
}

$bin = Find-PgBin
$pgCtl = Join-Path $bin 'pg_ctl.exe'

function Test-Running {
    & $pgCtl status -D $dataDir *> $null
    return $LASTEXITCODE -eq 0
}

function Initialize-Cluster {
    Write-Host "Creating the database cluster in $dataDir (PostgreSQL from $bin)..."
    New-Item -ItemType Directory -Force (Split-Path -Parent $dataDir) | Out-Null
    $pwFile = New-TemporaryFile
    try {
        Set-Content -Path $pwFile -Value $password -NoNewline -Encoding ascii
        & (Join-Path $bin 'initdb.exe') -D $dataDir -U $user --pwfile=$pwFile --auth=scram-sha-256 -E UTF8 --no-locale | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'initdb failed.' }
    } finally {
        Remove-Item $pwFile -Force
    }
    # Local connections only.
    Add-Content -Path (Join-Path $dataDir 'postgresql.conf') -Value "`nlisten_addresses = 'localhost'`nport = $port`n"
}

function Start-Cluster {
    $isNew = -not (Test-Path (Join-Path $dataDir 'PG_VERSION'))
    if ($isNew) { Initialize-Cluster }

    if (Test-Running) {
        Write-Host "PostgreSQL is already running on localhost:$port."
    } else {
        $busy = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        if ($busy) { throw "Port $port is already in use by process $($busy[0].OwningProcess). Stop it, or change the port in this script and in appsettings.Development.json." }
        # pg_ctl's child, the postgres server, inherits the caller's output handles and keeps them open
        # for as long as it runs. Launched directly, a caller that reads those handles (a pipe, a CI
        # runner) would wait forever. A separate hidden process shares no handles with this one.
        $starter = Start-Process -FilePath $pgCtl -ArgumentList @('start', '-D', "`"$dataDir`"", '-l', "`"$logFile`"", '-w', '-t', '60') `
            -WindowStyle Hidden -PassThru
        $starter.WaitForExit()
        if ($starter.ExitCode -ne 0) { throw "PostgreSQL did not start. See $logFile." }
        Write-Host "PostgreSQL started on localhost:$port."
    }

    if ($isNew) {
        $env:PGPASSWORD = $password
        & (Join-Path $bin 'createdb.exe') -h localhost -p $port -U $user $database
        if ($LASTEXITCODE -ne 0) { throw "Could not create database $database." }
        Write-Host "Created database $database. The API creates the tables on its first start."
    }
}

switch ($Command) {
    'start' { Start-Cluster }
    'stop' {
        if (Test-Running) { & $pgCtl stop -D $dataDir -m fast -w | Out-Null; Write-Host 'PostgreSQL stopped.' }
        else { Write-Host 'PostgreSQL is not running.' }
    }
    'status' {
        if (-not (Test-Path (Join-Path $dataDir 'PG_VERSION'))) { Write-Host 'No cluster yet. Run: ./scripts/db.ps1 start' }
        elseif (Test-Running) { Write-Host "Running on localhost:$port (data in $dataDir)." }
        else { Write-Host 'Stopped. Run: ./scripts/db.ps1 start' }
    }
    'reset' {
        if (Test-Running) { & $pgCtl stop -D $dataDir -m fast -w | Out-Null }
        if (Test-Path $dataDir) { Remove-Item -Recurse -Force $dataDir }
        Start-Cluster
    }
}
