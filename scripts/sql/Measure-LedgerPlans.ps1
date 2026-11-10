<#
.SYNOPSIS
  Before/after execution plans for ClearPay's hot queries, measured from SQL Server's actual plans.

.DESCRIPTION
  1. Creates a throw-away database, applies every EF migration (dotnet ef database update) and seeds
     synthetic demo data (scripts/sql/Seed-PlanBenchmark.sql).
  2. BEFORE: puts the original index back (IX_LedgerEntry_WalletId_CreatedAt without INCLUDE, no
     IX_PaymentOperation_CreatedAt_Type) and runs the query shapes EF Core generated before the change.
  3. AFTER: restores the migration's covering indexes and runs the stored procedures.
  Each query runs once to warm the cache, then 5 times with SET STATISTICS XML ON. Logical reads, CPU and
  elapsed time come from the actual plan (RunTimeCountersPerThread / QueryTimeStats); the median run is
  reported and its .sqlplan saved (open it in SSMS or Azure Data Studio).

.EXAMPLE
  $env:CLEARPAY_BENCH_SA_PASSWORD = '<local sa password>'
  ./scripts/sql/Measure-LedgerPlans.ps1 -Server '127.0.0.1,14339'
#>
param(
    [Parameter(Mandatory = $true)][string]$Server,
    [string]$User = 'sa',
    [string]$Database = 'ClearPayPlanBenchmark',
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\docs\sql\plans'),
    [int]$Runs = 5,
    [switch]$SkipSeed
)

$ErrorActionPreference = 'Stop'
$password = $env:CLEARPAY_BENCH_SA_PASSWORD
if ([string]::IsNullOrWhiteSpace($password)) { throw 'Set CLEARPAY_BENCH_SA_PASSWORD (local throw-away container only).' }
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$master = "Server=$Server;Database=master;User Id=$User;Password=$password;TrustServerCertificate=True;Encrypt=True"
$cs = "Server=$Server;Database=$Database;User Id=$User;Password=$password;TrustServerCertificate=True;Encrypt=True"
$ns = @{ p = 'http://schemas.microsoft.com/sqlserver/2004/07/showplan' }

function Invoke-Sql([string]$connectionString, [string]$sql, [int]$timeout = 600) {
    $c = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $c.Open()
    try {
        foreach ($batch in ($sql -split '(?m)^\s*GO\s*$')) {
            if ($batch.Trim().Length -eq 0) { continue }
            $cmd = $c.CreateCommand(); $cmd.CommandTimeout = $timeout; $cmd.CommandText = $batch
            $r = $cmd.ExecuteReader()
            do { while ($r.Read()) { $row = @{}; for ($i = 0; $i -lt $r.FieldCount; $i++) { $row[$r.GetName($i)] = $r.GetValue($i) }; [pscustomobject]$row } } while ($r.NextResult())
            $r.Close()
        }
    } finally { $c.Close() }
}

if (-not $SkipSeed) {
    Write-Host "Recreating $Database and applying migrations..."
    Invoke-Sql $master "IF DB_ID(N'$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END; CREATE DATABASE [$Database];" | Out-Null
    Push-Location $root
    try {
        & dotnet ef database update --project src/ClearPay.Infrastructure --startup-project src/ClearPay.Infrastructure --context ClearPayDbContext --connection $cs | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet ef database update failed' }
    } finally { Pop-Location }
    Write-Host 'Seeding synthetic demo data (about a minute)...'
    Invoke-Sql $cs (Get-Content (Join-Path $PSScriptRoot 'Seed-PlanBenchmark.sql') -Raw) 1800 | Format-Table | Out-Host
}

$hot = '11111111-1111-1111-1111-111111111111'
$queries = @(
    @{ Name = 'balance'; Title = 'Wallet balance (overdraft check)';
       Before = "DECLARE @w uniqueidentifier = '$hot'; SELECT [l].[Amount] FROM [LedgerEntry] AS [l] WHERE [l].[WalletId] = @w;";
       After = "EXEC dbo.usp_LedgerWalletBalance @WalletId = '$hot';" },
    @{ Name = 'summary'; Title = 'Wallet summary (balance, month in/out, last 5)';
       Before = "DECLARE @w uniqueidentifier = '$hot'; SELECT [l].[Id], [l].[Amount], [l].[CorrelationId], [l].[CreatedAt], [l].[Description], [l].[Kind], [l].[PairId], [l].[TransferId], [l].[WalletId] FROM [LedgerEntry] AS [l] WHERE [l].[WalletId] = @w;";
       After = "EXEC dbo.usp_LedgerWalletSummary @WalletId = '$hot', @MonthStart = '2026-11-01T00:00:00+00:00';" },
    @{ Name = 'settlement'; Title = 'Reconciliation: one day of captures/refunds';
       Before = "SELECT [p].[Id], [p].[Amount], [p].[CorrelationId], [p].[CreatedAt], [p].[PaymentId], [p].[Provider], [p].[ProviderIdempotencyKey], [p].[ProviderReference], [p].[Status], [p].[Type], [p].[UpdatedAt] FROM [PaymentOperation] AS [p] WHERE [p].[Type] IN (1, 2) AND [p].[Status] <> 2;";
       After = "EXEC dbo.usp_PaymentSettlementCandidates @From = '2026-10-01T00:00:00+00:00', @To = '2026-10-02T00:00:00+00:00';" }
)

$beforeIndexes = @"
DROP INDEX IF EXISTS IX_PaymentOperation_CreatedAt_Type ON dbo.PaymentOperation;
CREATE INDEX IX_LedgerEntry_WalletId_CreatedAt ON dbo.LedgerEntry (WalletId, CreatedAt) WITH (DROP_EXISTING = ON);
"@
$afterIndexes = @"
CREATE INDEX IX_LedgerEntry_WalletId_CreatedAt ON dbo.LedgerEntry (WalletId, CreatedAt) INCLUDE (Amount, Kind, CorrelationId) WITH (DROP_EXISTING = ON);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PaymentOperation_CreatedAt_Type')
  CREATE INDEX IX_PaymentOperation_CreatedAt_Type ON dbo.PaymentOperation (CreatedAt, Type) INCLUDE (Status, Provider, ProviderReference, CorrelationId, Amount);
"@

function Measure-Query([string]$sql) {
    $samples = @()
    for ($run = 0; $run -le $Runs; $run++) {
        $c = New-Object System.Data.SqlClient.SqlConnection $cs
        $c.Open()
        try {
            $set = $c.CreateCommand(); $set.CommandText = 'SET STATISTICS XML ON;'; [void]$set.ExecuteNonQuery()
            $cmd = $c.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 600
            $r = $cmd.ExecuteReader()
            $rows = 0; $plans = @()
            do {
                if ($r.FieldCount -eq 1 -and $r.GetName(0) -like '*Showplan*') { while ($r.Read()) { $plans += [xml]$r.GetString(0) } }
                else { while ($r.Read()) { $rows++ } }
            } while ($r.NextResult())
            $r.Close()
        } finally { $c.Close() }
        if ($run -eq 0) { continue } # warm-up
        $reads = 0; $cpu = 0; $elapsed = 0; $ops = @()
        foreach ($plan in $plans) {
            foreach ($t in (Select-Xml -Xml $plan -XPath '//p:QueryTimeStats' -Namespace $ns)) {
                $cpu += [int]$t.Node.CpuTime; $elapsed += [int]$t.Node.ElapsedTime }
            foreach ($c2 in (Select-Xml -Xml $plan -XPath '//p:RunTimeCountersPerThread' -Namespace $ns)) {
                if ($c2.Node.ActualLogicalReads) { $reads += [long]$c2.Node.ActualLogicalReads } }
            foreach ($op in (Select-Xml -Xml $plan -XPath '//p:RelOp' -Namespace $ns)) {
                $index = ($op.Node.GetElementsByTagName('Object', $ns.p) | Select-Object -First 1)
                $name = $op.Node.PhysicalOp
                if ($index -and $index.Index -and $name -match 'Seek|Scan|Lookup') { $name = "$name ($($index.Index.Trim('[',']')))" }
                if ($name -match 'Seek|Scan|Lookup|Sort|Top|Aggregate') { $ops += $name } }
        }
        $samples += [pscustomobject]@{ Reads = $reads; Cpu = $cpu; Elapsed = $elapsed; Rows = $rows; Ops = ($ops | Select-Object -Unique) -join ', '; Plans = $plans }
    }
    $median = $samples | Sort-Object Elapsed | Select-Object -Index ([int][math]::Floor($samples.Count / 2))
    return $median
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = @()
foreach ($phase in @('Before', 'After')) {
    Invoke-Sql $cs ($(if ($phase -eq 'Before') { $beforeIndexes } else { $afterIndexes })) | Out-Null
    foreach ($q in $queries) {
        $m = Measure-Query $q[$phase]
        $i = 0
        foreach ($plan in $m.Plans) {
            $i++
            $suffix = $(if ($m.Plans.Count -gt 1) { "-$i" } else { '' })
            $plan.Save((Join-Path $OutDir "$($q.Name)-$($phase.ToLower())$suffix.sqlplan"))
        }
        $results += [pscustomobject]@{ Query = $q.Title; Phase = $phase; RowsToClient = $m.Rows; LogicalReads = $m.Reads; CpuMs = $m.Cpu; ElapsedMs = $m.Elapsed; Operators = $m.Ops }
        Write-Host ("{0,-48} {1,-6} rows {2,7}  reads {3,7}  cpu {4,5} ms  elapsed {5,5} ms  {6}" -f $q.Title, $phase, $m.Rows, $m.Reads, $m.Cpu, $m.Elapsed, $m.Ops)
    }
}

$counts = Invoke-Sql $cs "SELECT (SELECT COUNT(*) FROM dbo.LedgerEntry) AS LedgerRows, (SELECT COUNT(*) FROM dbo.LedgerEntry WHERE WalletId = '$hot') AS HotRows, (SELECT COUNT(*) FROM dbo.PaymentOperation) AS Ops, @@VERSION AS Version"
$md = @("# Execution plans: before / after", "",
        "Measured $(Get-Date -Format 'yyyy-MM-dd') with ``scripts/sql/Measure-LedgerPlans.ps1`` on $(($counts.Version -split "`n")[0].Trim()).",
        "Data: $($counts.LedgerRows) ledger rows ($($counts.HotRows) on the measured wallet), $($counts.Ops) payment operations (synthetic).",
        "Warm cache; median of $Runs runs; numbers from the actual plans. ``.sqlplan`` files are in this folder.", "",
        "| Query | Phase | Rows to client | Logical reads | CPU ms | Elapsed ms | Plan operators |",
        "|-------|-------|---------------:|--------------:|-------:|-----------:|----------------|")
$md += $results | ForEach-Object { "| $($_.Query) | $($_.Phase) | $($_.RowsToClient) | $($_.LogicalReads) | $($_.CpuMs) | $($_.ElapsedMs) | $($_.Operators) |" }
$md | Set-Content -Encoding utf8 (Join-Path $OutDir '..\plan-results.md')
Write-Host "Wrote $(Join-Path $OutDir '..\plan-results.md')"
