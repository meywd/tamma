<#
.SYNOPSIS
  Story 28-1 AC2 — idempotent bootstrap for Tamma's shared databases.
  PowerShell mirror of bootstrap-shared-dbs.sh (identical behaviour).

.DESCRIPTION
  TOPOLOGY (unified schema-per-tenant):
  Story 28-1 AC2 was written against the original db-per-tenant design
  (separate `tamma_control` + `tamma_global_elsa` databases). The current
  deployment uses the UNIFIED tenancy model: the central `tamma` database
  hosts the control plane + Elsa's own tables and is pool member #1
  ("central") in `tenant_databases`; every tenant lives in its own
  `t_<hex>` schema with a per-tenant role and an AES-GCM-encrypted
  connection string (see root CLAUDE.md "Multi-tenant provisioning
  (Cranl)").

  So the databases this script ENSURES today are just `tamma` (control +
  tenant schemas) and — only if Elsa is split onto its own database — a
  separate Elsa DB. Both default to the single central `tamma` DB and are
  PARAMETERISED so this script is forward-compatible with additional pool
  databases: point -ControlDb / -GlobalElsaDb at the real split databases
  and the same create-if-missing + summary logic applies unchanged.

  WHAT "apply migrations" MEANS HERE:
  The Tamma app SELF-MIGRATES on boot (tamma-api → Database.Migrate();
  elsa-server → ef.RunMigrations = true). In the normal Docker flow this
  script only guarantees the target databases EXIST before the containers
  start; the containers then apply their own migrations idempotently.
  Set -RunEfMigrations (or TAMMA_RUN_EF_MIGRATIONS=1) for a fresh-cluster
  / CI flow that applies the schema without booting the app — requires the
  .NET SDK on the host.

  BEHAVIOUR (AC2):
    * Creates each target DB if missing (guarded by a pg_database probe).
    * Safe to re-run (second run is a no-op for present DBs).
    * Exits non-zero on ANY failure.
    * Emits one JSON-lines summary per DB:
        { "db": "...", "migrationsApplied": N, "durationMs": N }

  CONNECTION PARAMS (env, with defaults):
    PGHOST (postgres), PGPORT (5432), PGUSER (tamma),
    PGPASSWORD ($DB_PASSWORD), TAMMA_CONTROL_DB (tamma),
    TAMMA_GLOBAL_ELSA_DB (tamma), TAMMA_RUN_EF_MIGRATIONS (0)
#>
[CmdletBinding()]
param(
    [string]$PgHost        = $(if ($env:PGHOST) { $env:PGHOST } else { 'postgres' }),
    [int]   $PgPort        = $(if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 }),
    [string]$PgUser        = $(if ($env:PGUSER) { $env:PGUSER } else { 'tamma' }),
    [string]$PgPassword    = $(if ($env:PGPASSWORD) { $env:PGPASSWORD } elseif ($env:DB_PASSWORD) { $env:DB_PASSWORD } else { '' }),
    [string]$ControlDb     = $(if ($env:TAMMA_CONTROL_DB) { $env:TAMMA_CONTROL_DB } else { 'tamma' }),
    [string]$GlobalElsaDb  = $(if ($env:TAMMA_GLOBAL_ELSA_DB) { $env:TAMMA_GLOBAL_ELSA_DB } else { 'tamma' }),
    [switch]$RunEfMigrations = $($env:TAMMA_RUN_EF_MIGRATIONS -eq '1')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Export connection params so psql (libpq) and child `dotnet` see them.
$env:PGHOST     = $PgHost
$env:PGPORT     = "$PgPort"
$env:PGUSER     = $PgUser
$env:PGPASSWORD = $PgPassword

if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
    Write-Error '[bootstrap] FATAL: psql not found on PATH'
    exit 1
}

function Wait-ForPostgres {
    $attempts = 30
    for ($i = 0; $i -lt $attempts; $i++) {
        & pg_isready -h $env:PGHOST -p $env:PGPORT -U $env:PGUSER *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Seconds 2
    }
    Write-Error "[bootstrap] FATAL: Postgres at $($env:PGHOST):$($env:PGPORT) not ready after $attempts probes"
    exit 1
}

# Run a psql command against the maintenance `postgres` DB. Returns the
# trimmed stdout; throws on non-zero exit.
function Invoke-PsqlMaint {
    param([Parameter(Mandatory)][string]$Command, [string]$Db = 'postgres')
    $out = & psql --dbname=$Db --no-psqlrc --quiet --tuples-only --no-align `
        --set=ON_ERROR_STOP=on --command=$Command 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "psql failed (db=$Db): $out"
    }
    return ($out | Out-String).Trim()
}

function Invoke-EfMigrations {
    param([Parameter(Mandatory)][string]$Db)
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw '[bootstrap] FATAL: TAMMA_RUN_EF_MIGRATIONS=1 but dotnet SDK not on PATH'
    }
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $dataProj  = Join-Path $scriptDir '..\..\apps\tamma-elsa\src\Tamma.Data'
    if (-not (Test-Path $dataProj)) {
        throw "[bootstrap] FATAL: Tamma.Data project not found at $dataProj"
    }

    $countBefore = 0
    try {
        $countBefore = [int](Invoke-PsqlMaint -Db $Db -Command 'SELECT count(*) FROM "__TammaMigrationsHistory";')
    } catch { $countBefore = 0 }

    $conn = "Host=$($env:PGHOST);Port=$($env:PGPORT);Database=$Db;Username=$($env:PGUSER);Password=$($env:PGPASSWORD)"
    $env:ConnectionStrings__TammaDb = $conn
    & dotnet ef database update --project $dataProj
    if ($LASTEXITCODE -ne 0) {
        throw "[bootstrap] FATAL: dotnet ef database update failed for $Db"
    }

    $countAfter = 0
    try {
        $countAfter = [int](Invoke-PsqlMaint -Db $Db -Command 'SELECT count(*) FROM "__TammaMigrationsHistory";')
    } catch { $countAfter = 0 }
    return ($countAfter - $countBefore)
}

# Idempotently ensure a database exists; emit the JSON-lines summary.
function Confirm-Database {
    param([Parameter(Mandatory)][string]$Db)
    $startMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $applied = 0

    $exists = Invoke-PsqlMaint -Command "SELECT 1 FROM pg_database WHERE datname = '$Db';"
    if ($exists -ne '1') {
        Invoke-PsqlMaint -Command "CREATE DATABASE ""$Db"";" | Out-Null
        Write-Host "[bootstrap] created database $Db" -ErrorAction SilentlyContinue
    } else {
        Write-Host "[bootstrap] database $Db already present (no-op)"
    }

    if ($RunEfMigrations) {
        $applied = Invoke-EfMigrations -Db $Db
    }

    $durationMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - $startMs
    # AC2 structured summary — one JSON object per DB on stdout.
    Write-Output ('{{ "db": "{0}", "migrationsApplied": {1}, "durationMs": {2} }}' -f $Db, $applied, $durationMs)
}

try {
    Wait-ForPostgres

    # De-dupe target set (shared mode collapses both names to `tamma`).
    $dbs = @($ControlDb)
    if ($GlobalElsaDb -ne $ControlDb) { $dbs += $GlobalElsaDb }

    foreach ($db in $dbs) { Confirm-Database -Db $db }

    Write-Host "[bootstrap] complete — $($dbs.Count) shared database(s) ensured."
    exit 0
}
catch {
    Write-Error "[bootstrap] $_"
    exit 2
}
