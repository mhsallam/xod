# The publishing run (docs/plans/PLAN-043-stress-publishing-run.md): capacity runs, then one soak,
# from a committed tree on mains power. Start it from anywhere and leave the machine alone; with the
# defaults it takes about 70 minutes. Results land in benchmarks/results/stress.
#
#   powershell -ExecutionPolicy Bypass -File benchmarks/run-stress.ps1
#   powershell -ExecutionPolicy Bypass -File benchmarks/run-stress.ps1 -SoakMinutes 45 -CapacityRuns 3
#
# The harness refuses (--publishable) a tree or a power state the site would not publish, so a
# mistake costs seconds, not the hour. Busy minutes are recorded and judged by the site, not here.
param(
    [int]$SoakMinutes = 60,
    [int]$CapacityRuns = 5,
    [long]$CapacityRows = 10000000
)

$ErrorActionPreference = 'Stop'
Set-Location (Resolve-Path (Join-Path $PSScriptRoot '..'))
$bench = 'benchmarks/XodBench/bin/Release/net9.0/XodBench.dll'

function Step([string]$name, [scriptblock]$run) {
    Write-Host "== $name ($(Get-Date -Format HH:mm:ss))"
    & $run | Out-Host # to the console, so only the exit code is returned
    return $LASTEXITCODE
}

# The harness and its tests, built and run as the gate: an hour on a broken harness is an hour lost.
if ((Step 'build' { dotnet build benchmarks/XodBench -c Release -v q --nologo }) -ne 0) { exit 1 }
if ((Step 'harness tests' { dotnet test benchmarks/XodBench.Tests -c Release -v q --nologo }) -ne 0) { exit 1 }
if ((Step 'publishable?' { dotnet $bench --stress --profile soak --publishable --dry-run }) -ne 0) { exit 2 }

$results = @()
for ($i = 1; $i -le $CapacityRuns; $i++) {
    $code = Step "capacity $i of $CapacityRuns" { dotnet $bench --stress --profile capacity --max-rows $CapacityRows --publishable }
    $results += "capacity $i of ${CapacityRuns}: exit $code"
}
$code = Step "soak, $SoakMinutes minutes" { dotnet $bench --stress --profile soak --minutes $SoakMinutes --publishable }
$results += "soak: exit $code"

Write-Host "== done ($(Get-Date -Format HH:mm:ss))"
$results | ForEach-Object { Write-Host $_ }
Get-ChildItem benchmarks/results/stress -Filter *.jsonl | Sort-Object LastWriteTime | Select-Object -Last ($CapacityRuns + 1) | ForEach-Object { Write-Host $_.Name }
