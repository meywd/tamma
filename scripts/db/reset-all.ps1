<#
.SYNOPSIS
  Story 28-1 AC3 — wipe-and-replay for Tamma's shared databases.
  PowerShell mirror of reset-all.sh (identical behaviour).

.DESCRIPTION
  Drops + recreates the shared control / global-Elsa databases, then
  invokes bootstrap-shared-dbs.ps1. For CI / local-dev resets and the
  integration-test setup — NEVER for production.

  TOPOLOGY: read bootstrap-shared-dbs.ps1's header first. The unified
  tenancy model puts the control plane + tenant `t_<hex>` schemas + Elsa
  data in ONE central `tamma` DB (pool member #1 in tenant_databases).
  "Drop the shared DBs" today means dropping `tamma` (+ a separate Elsa
  DB when one exists). Both default to `tamma` and are parameterised for
  a future tamma_control / tamma_global_elsa split.

  DOES NOT touch per-tenant databases. Per-tenant DBs
  (`tamma_tenant_<guid>` / `..._elsa`) are workflow-provisioned. This
  script REFUSES to drop any database whose name matches `tamma_tenant_*`.

  IDEMPOTENCY (AC3): running twice yields an identical final schema
  (DROP DATABASE IF EXISTS + bootstrap's create-if-missing + the same EF
  migration set).

  SAFETY GATE: destructive. Refuses to run unless EITHER
  TAMMA_RESET_CONFIRM=yes (env) OR -Force is passed; and always refuses
  when ASPNETCORE_ENVIRONMENT=Production.
#>
[CmdletBinding()]
param(
    [string]$PgHost       = $(if ($env:PGHOST) { $env:PGHOST } else { 'postgres' }),
    [int]   $PgPort       = $(if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 }),
    [string]$PgUser       = $(if ($env:PGUSER) { $env:PGUSER } else { 'tamma' }),
    [string]$PgPassword   = $(if ($env:PGPASSWORD) { $env:PGPASSWORD } elseif ($env:DB_PASSWORD) { $env:DB_PASSWORD } else { '' }),
    [string]$ControlDb    = $(if ($env:TAMMA_CONTROL_DB) { $env:TAMMA_CONTROL_DB } else { 'tamma' }),
    [string]$GlobalElsaDb = $(if ($env:TAMMA_GLOBAL_ELSA_DB) { $env:TAMMA_GLOBAL_ELSA_DB } else { 'tamma' }),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$env:PGHOST     = $PgHost
$env:PGPORT     = "$PgPort"
$env:PGUSER     = $PgUser
$env:PGPASSWORD = $PgPassword

# ── Safety gate ─────────────────────────────────────────────────────────
if ($env:ASPNETCORE_ENVIRONMENT -eq 'Production') {
    Write-Error '[reset-all] refusing to reset databases while ASPNETCORE_ENVIRONMENT=Production.'
    exit 1
}
if (-not $Force -and $env:TAMMA_RESET_CONFIRM -ne 'yes') {
    Write-Error '[reset-all] destructive. Confirm with TAMMA_RESET_CONFIRM=yes or -Force.'
    exit 1
}

if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
    Write-Error '[reset-all] FATAL: psql not found on PATH'
    exit 1
}

function Invoke-PsqlMaint {
    param([Parameter(Mandatory)][string]$Command)
    $out = & psql --dbname=postgres --no-psqlrc --quiet --tuples-only --no-align `
        --set=ON_ERROR_STOP=on --command=$Command 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed: $out" }
    return ($out | Out-String).Trim()
}

function Assert-NotTenantDb {
    param([Parameter(Mandatory)][string]$Db)
    if ($Db -like 'tamma_tenant_*') {
        Write-Error "[reset-all] FATAL: refusing to drop per-tenant database '$Db'. Per-tenant DBs are workflow-provisioned; reset-all only touches the shared control / global-Elsa databases."
        exit 1
    }
}

function Remove-Database {
    param([Parameter(Mandatory)][string]$Db)
    Assert-NotTenantDb -Db $Db

    # Terminate other backends so DROP doesn't fail on open pools.
    try {
        Invoke-PsqlMaint -Command "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$Db' AND pid <> pg_backend_pid();" | Out-Null
    } catch { }

    Invoke-PsqlMaint -Command "DROP DATABASE IF EXISTS ""$Db"";" | Out-Null
    Write-Host "[reset-all] dropped database $Db (if it existed)"
}

try {
    # De-dupe target set (shared mode collapses both names to `tamma`).
    $dbs = @($ControlDb)
    if ($GlobalElsaDb -ne $ControlDb) { $dbs += $GlobalElsaDb }

    Write-Host "[reset-all] dropping shared database(s): $($dbs -join ', ')"
    foreach ($db in $dbs) { Remove-Database -Db $db }

    # Recreate + (optionally) migrate by delegating to the bootstrap script.
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $bootstrap = Join-Path $scriptDir 'bootstrap-shared-dbs.ps1'
    Write-Host '[reset-all] invoking bootstrap-shared-dbs.ps1 to recreate + ensure schema'
    & $bootstrap -ControlDb $ControlDb -GlobalElsaDb $GlobalElsaDb
    if ($LASTEXITCODE -ne 0) {
        throw '[reset-all] FATAL: bootstrap step failed'
    }

    Write-Host '[reset-all] complete — shared database(s) reset and bootstrapped.'
    exit 0
}
catch {
    Write-Error "[reset-all] $_"
    exit 2
}
