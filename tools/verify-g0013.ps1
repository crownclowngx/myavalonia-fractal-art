# G0013 本地开发门禁。只使用 Debug、测试与只读格式检查；不调用 Host 发布 Gate、ZIP、安装或 CI。
# 设计思路：每条原生命令独立检查退出码，并把结果保存到忽略目录，避免 PowerShell 后续成功掩盖前面的失败。
param(
    [string]$FractalRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$ImageLabRoot,
    [string]$StudioRoot
)
$ErrorActionPreference = 'Stop'
$FractalRoot = (Resolve-Path -LiteralPath $FractalRoot).Path
if (-not $ImageLabRoot) { $ImageLabRoot = Join-Path (Split-Path $FractalRoot -Parent) 'myavalonia-image-lab' }
if (-not $StudioRoot) { $StudioRoot = Join-Path (Split-Path $FractalRoot -Parent) 'myavalonia-workflow-studio' }
$ImageLabRoot = (Resolve-Path -LiteralPath $ImageLabRoot).Path
$StudioRoot = (Resolve-Path -LiteralPath $StudioRoot).Path
$taskEvidence = Join-Path $FractalRoot 'artifacts/test-results/G0013/local-gate'
New-Item -ItemType Directory -Force -Path $taskEvidence | Out-Null
[pscustomobject]@{status='running';createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O')} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskEvidence 'summary.json') -Encoding utf8
$taskSteps = [Collections.Generic.List[object]]::new()
function Invoke-LocalGate {
    param([string]$Name, [string]$Root, [string[]]$Arguments)
    Push-Location -LiteralPath $Root
    try {
        Write-Host "$Name : dotnet $($Arguments -join ' ')"
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $taskEvidence "$Name.log")
        $taskExitCode = $LASTEXITCODE
        $taskSteps.Add([pscustomobject]@{ name=$Name; exitCode=$taskExitCode; elapsedMs=$timer.ElapsedMilliseconds })
        if ($taskExitCode -ne 0) { throw "$Name 失败，退出码 $taskExitCode；请查看本地日志。" }
    }
    finally { Pop-Location }
}
$taskSolutions = @(
    @{name='fractal'; root=$FractalRoot; solution='FractalArtPlugin.slnx'},
    @{name='image-lab'; root=$ImageLabRoot; solution='ImageLabPlugin.slnx'},
    @{name='studio'; root=$StudioRoot; solution='WorkflowStudio.slnx'}
)
foreach ($solution in $taskSolutions) {
    Invoke-LocalGate "$($solution.name)-restore" $solution.root @('restore',$solution.solution,'--locked-mode')
}
foreach ($solution in $taskSolutions) {
    Invoke-LocalGate "$($solution.name)-build" $solution.root @('build',$solution.solution,'-c','Debug','--no-restore','-warnaserror')
}
$taskTests = @(
    @{name='fractal-tests';root=$FractalRoot;project='tests/FractalArtPlugin.Tests/FractalArtPlugin.Tests.csproj'},
    @{name='integration-tests';root=$FractalRoot;project='tests/FractalArtPlugin.WorkflowIntegration.Tests/FractalArtPlugin.WorkflowIntegration.Tests.csproj'},
    @{name='image-lab-tests';root=$ImageLabRoot;project='tests/ImageLabPlugin.Tests/ImageLabPlugin.Tests.csproj'},
    @{name='studio-tests';root=$StudioRoot;project='tests/WorkflowStudio.Tests/WorkflowStudio.Tests.csproj'}
)
$taskCounters = [Collections.Generic.List[object]]::new()
foreach ($test in $taskTests) {
    Invoke-LocalGate $test.name $test.root @('test',$test.project,'-c','Debug','--no-build','--no-restore','--logger',"trx;LogFileName=$($test.name).trx",'--results-directory',$taskEvidence)
    [xml]$taskTrx = Get-Content -Raw -LiteralPath (Join-Path $taskEvidence "$($test.name).trx")
    $counter = $taskTrx.TestRun.ResultSummary.Counters
    $taskCounters.Add([pscustomobject]@{name=$test.name; total=[int]$counter.total; passed=[int]$counter.passed; failed=[int]$counter.failed; notExecuted=[int]$counter.notExecuted})
}
foreach ($solution in $taskSolutions) {
    Invoke-LocalGate "$($solution.name)-format" $solution.root @('format',$solution.solution,'--verify-no-changes','--no-restore')
}
Invoke-LocalGate 'fractal-benchmark' $FractalRoot @('run','--project','tools/FractalArtPlugin.Benchmarks','-c','Debug','--no-build','--',(Join-Path $taskEvidence 'benchmark.json'))
Invoke-LocalGate 'studio-self-test' $StudioRoot @('run','--project','src/WorkflowStudio.Standalone','-c','Debug','--no-build','--','--g3-self-test')

# 仅管理本脚本新启动的空白 Standalone 进程，不附着或结束用户已经打开的窗口。
$taskSmokes = [Collections.Generic.List[object]]::new()
foreach ($entry in @(
    @{root=$FractalRoot;name='FractalArtPlugin'},
    @{root=$ImageLabRoot;name='ImageLabPlugin'},
    @{root=$StudioRoot;name='WorkflowStudio'}
)) {
    $executable = Join-Path $entry.root "src/$($entry.name).Standalone/bin/Debug/net10.0/$($entry.name).Standalone.exe"
    $process = Start-Process -FilePath $executable -WorkingDirectory $entry.root -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $taskEvidence "$($entry.name)-standalone-out.log") -RedirectStandardError (Join-Path $taskEvidence "$($entry.name)-standalone-error.log")
    try {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited) { throw "$($entry.name) Standalone 提前退出：$($process.ExitCode)" }
        } while ($process.MainWindowHandle -eq 0 -and $timer.Elapsed.TotalSeconds -lt 15)
        if ($process.MainWindowHandle -eq 0 -or -not $process.Responding) { throw "$($entry.name) Standalone 窗口未就绪" }
        $taskSmokes.Add([pscustomobject]@{name=$entry.name;processId=$process.Id;title=$process.MainWindowTitle;windowHandle=$process.MainWindowHandle.ToInt64();responding=$process.Responding;startupMs=$timer.ElapsedMilliseconds})
    }
    finally {
        if (-not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
        }
        $process.Dispose()
    }
}
$taskSummary = [pscustomobject]@{
    status='passed'
    createdAtUtc=[DateTimeOffset]::UtcNow.ToString('O'); configuration='Debug'
    steps=$taskSteps; tests=$taskCounters; standalone=$taskSmokes
    realHostValidated=$false; releaseGatesExecuted=$false; windowsCiAdded=$false
}
$taskSummary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskEvidence 'summary.json') -Encoding utf8
Write-Host "G0013_LOCAL_GATE_OK $taskEvidence"
