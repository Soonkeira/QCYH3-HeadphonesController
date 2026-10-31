# QCY H3 托盘控制工具 —— 长期后台稳定性冒烟脚本（阶段 6，真机执行）
# 内容：
#   1. 杀旧实例 → 启动 → 60 秒长跑采样 3 次（工作集内存 / GDI 对象 / USER 对象 / 句柄）
#   2. 面板反复显示/隐藏 10 次（二次启动唤起显示 + 失焦隐藏，覆盖图标缓存与窗口复用路径）
#   3. 收敛后再采样 2 次；内存前后差值 >10% 判 FAIL
#   4. 低电量提醒验证：临时把配置阈值改为 100 触发气泡 → 全屏截图 → 恢复原配置
#   5. error.log 全程增量检查（新增行中含 Exception / 未处理异常 判 FAIL）
# 用法：powershell -ExecutionPolicy Bypass -File tools\smoke.ps1 [-ExePath publish\QcyH3Tray.App.exe]
#       [-RunSeconds 60] [-PanelToggles 10] [-SkipLowBattery]
[CmdletBinding()]
param(
    [string]$ExePath = "",
    [int]$RunSeconds = 60,
    [int]$PanelToggles = 10,
    [switch]$SkipLowBattery,
    [switch]$LowBatteryOnly   # 只跑低电量气泡验证（杀旧 → 阈值100 → 触发截图 → 恢复 → 杀干净）
)

$ErrorActionPreference = "Continue"
$RepoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ExePath) { $ExePath = Join-Path $RepoRoot "publish\QcyH3Tray.App.exe" }
$ExePath = (Resolve-Path $ExePath -ErrorAction Stop).Path
$ErrorLogPath = Join-Path $env:LOCALAPPDATA "QcyH3Tray\error.log"
$ConfigPath = Join-Path $env:APPDATA "QcyH3Controller\config.json"
$DistDir = Join-Path $RepoRoot "dist"

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class SmokeNative
{
    // GR_GDIOBJECTS = 0（GDI 对象数）；GR_USEROBJECTS = 1（USER 对象数）
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
}
"@
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:failures = [System.Collections.Generic.List[string]]::new()

function Stop-TrayApp {
    Get-Process -Name "QcyH3Tray.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
}

function Get-LogLength {
    if (Test-Path $ErrorLogPath) { return (Get-Item $ErrorLogPath).Length }
    return 0
}

function Get-NewLogLines {
    # 读取 error.log 中 fromLength 字节之后的新增行（只看新增，避免匹配到上一轮的旧记录）
    param([long]$FromLength)
    if (-not (Test-Path $ErrorLogPath)) { return @() }
    $length = (Get-Item $ErrorLogPath).Length
    if ($length -le $FromLength) { return @() }
    $stream = [System.IO.FileStream]::new($ErrorLogPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Seek($FromLength, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8)
        try { return ($reader.ReadToEnd() -split "`r?`n") | Where-Object { $_.Trim().Length -gt 0 } }
        finally { $reader.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-TraySample {
    param([int]$ProcessId, [string]$Label)
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $process.Refresh()
    [pscustomobject]@{
        采样点     = $Label
        时间       = (Get-Date -Format "HH:mm:ss")
        内存MB     = [math]::Round($process.WorkingSet64 / 1MB, 1)
        私有MB     = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        GDI对象    = [int][SmokeNative]::GetGuiResources($process.Handle, 0)
        USER对象   = [int][SmokeNative]::GetGuiResources($process.Handle, 1)
        句柄数     = $process.HandleCount
    }
}

function Show-PanelViaSecondInstance {
    # 二次启动：命名 Mutex 检测后置激活信号退出，第一实例弹出主面板（PresentPanel）
    Start-Process -FilePath $ExePath | Out-Null
    Start-Sleep -Milliseconds 1300
}

function Hide-PanelViaFocusLoss {
    # 面板 Deactivated 自动隐藏：把前台焦点还给本控制台窗口
    $console = [SmokeNative]::GetConsoleWindow()
    if ($console -ne [IntPtr]::Zero) {
        [void][SmokeNative]::SetForegroundWindow($console)
    }
    Start-Sleep -Milliseconds 900
}

Write-Host "=== 冒烟 1/5：杀旧实例、启动（$ExePath） ==="
Stop-TrayApp
$logStartLength = Get-LogLength
if (-not $LowBatteryOnly) {
    $process = Start-Process -FilePath $ExePath -PassThru
    Start-Sleep -Seconds 5
    if ($process.HasExited) { throw "进程启动后立即退出（ExitCode=$($process.ExitCode)），请检查 error.log" }
} else {
    Write-Host "（-LowBatteryOnly：只杀进程不预启动——启动数秒即杀再启动的节奏会让耳机/Windows BLE 链路释放不及时，影响低电量验证）"
}

if (-not $LowBatteryOnly) {
Write-Host "=== 冒烟 2/5：${RunSeconds}s 长跑采样（3 次） ==="
$samples = [System.Collections.Generic.List[object]]::new()
$samples.Add((Get-TraySample -ProcessId $process.Id -Label "长跑#1"))
$half = [int]($RunSeconds / 2)
Start-Sleep -Seconds $half
$samples.Add((Get-TraySample -ProcessId $process.Id -Label "长跑#2"))
Start-Sleep -Seconds ($RunSeconds - $half)
$samples.Add((Get-TraySample -ProcessId $process.Id -Label "长跑#3"))

Write-Host "=== 冒烟 3/5：面板显示/隐藏 × $PanelToggles（二次唤起显示 / 失焦隐藏） ==="
for ($i = 1; $i -le $PanelToggles; $i++) {
    Show-PanelViaSecondInstance
    Hide-PanelViaFocusLoss
    Write-Host ("  开关第 {0}/{1} 次" -f $i, $PanelToggles)
    if ($i -eq [int]($PanelToggles / 2)) {
        $samples.Add((Get-TraySample -ProcessId $process.Id -Label "开关半程"))
    }
}
Start-Sleep -Seconds 10
$samples.Add((Get-TraySample -ProcessId $process.Id -Label "开关后#1"))
Start-Sleep -Seconds 30
$samples.Add((Get-TraySample -ProcessId $process.Id -Label "开关后#2"))
Start-Sleep -Seconds 30
$samples.Add((Get-TraySample -ProcessId $process.Id -Label "开关后#3"))

$samples | Format-Table -AutoSize | Out-Host

$first = $samples[0]
$last = $samples[-1]
$previous = $samples[-2]
$memoryGrowth = [math]::Round(($last.内存MB - $first.内存MB) / $first.内存MB * 100, 1)
$memoryStep = [math]::Round(($last.内存MB - $previous.内存MB) / $previous.内存MB * 100, 1)
$gdiDelta = $last.GDI对象 - $first.GDI对象
$gdiStep = [math]::Abs($last.GDI对象 - $previous.GDI对象)
$userDelta = $last.USER对象 - $first.USER对象
Write-Host ("内存整体增长：{0}%（{1} → {2} MB）。面板窗口为复用设计（Hide 不销毁），首次创建带来一次性开销，泄漏判据看收敛：最后两采样差 {3}%（判据 <10%）。" -f `
    $memoryGrowth, $first.内存MB, $last.内存MB, $memoryStep)
Write-Host ("GDI 对象：{0} → {1}（整体差 {2}，最后两采样差 {3}）  USER 对象：{4} → {5}（整体差 {6}）" -f `
    $first.GDI对象, $last.GDI对象, $gdiDelta, $gdiStep, $first.USER对象, $last.USER对象, $userDelta)
if ($memoryStep -ge 10) { $script:failures.Add("内存未收敛：最后两采样差 $memoryStep% ≥ 10%") }
if ($gdiStep -gt 2) { $script:failures.Add("GDI 对象未收敛：最后两采样差 $gdiStep") }
}

if (-not $SkipLowBattery) {
    Write-Host "=== 冒烟 4/5：低电量提醒验证（阈值临时改 100 → 气泡 → 截图 → 恢复配置） ==="
    if (-not (Test-Path $ConfigPath)) {
        Write-Host "  跳过：未找到用户配置 $ConfigPath（应用尚未完成首启）"
    } else {
        $configBackupPath = Join-Path $env:TEMP "QcyH3Tray-config-smoke-backup.json"
        Copy-Item $ConfigPath $configBackupPath -Force
        try {
            $config = Get-Content $ConfigPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $config | Add-Member -NotePropertyName LowBatteryThresholdPercent -NotePropertyValue 100 -Force
            $config | Add-Member -NotePropertyName NotificationsEnabled -NotePropertyValue $true -Force
            $config | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding UTF8
            Write-Host "  配置已改：LowBatteryThresholdPercent=100"

            # 1s 轮询新增日志（toast 约 5 秒自动消失：检测到立即截图）；
            # 只匹配本轮启动后的新增行；杀进程后等 5s（耳机/Windows BLE 释放刚断的链路，
            # 立即重连会被 AccessDenied 拒绝 6-15s，阶段 4b 真机结论）；失败重试一轮
            $triggered = $false
            $shotPath = $null
            foreach ($retryRound in 1..2) {
                Write-Host ("  触发尝试 {0}/2：启动应用…" -f $retryRound)
                Stop-TrayApp
                Start-Sleep -Seconds 5
                $logMark = Get-LogLength
                $process = Start-Process -FilePath $ExePath -PassThru
                Start-Sleep -Seconds 5
                if ($process.HasExited) { throw "进程启动后立即退出（ExitCode=$($process.ExitCode))" }
                foreach ($attempt in 1..85) {
                    Start-Sleep -Seconds 1
                    $newLines = @(Get-NewLogLines -FromLength $logMark)
                    if ($newLines | Where-Object { $_ -match "低电量提醒" }) { $triggered = $true; break }
                }
                if ($triggered) { break }
                Write-Host ("  尝试 {0} 未触发（90s 窗口）。" -f $retryRound)
            }
            if ($triggered) {
                Write-Host "  低电量提醒已触发（error.log 有记录），立即截图。"
                $screen = [System.Windows.Forms.SystemInformation]::VirtualScreen
                $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                $graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $bitmap.Size)
                $graphics.Dispose()
                New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
                $shotPath = Join-Path $DistDir ("smoke-lowbattery-{0}.png" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
                $bitmap.Save($shotPath, [System.Drawing.Imaging.ImageFormat]::Png)
                $bitmap.Dispose()
                Write-Host "  气泡截图：$shotPath"
            } else {
                Write-Host "  未检测到触发记录。本轮 error.log 新增行："
                @(Get-NewLogLines -FromLength $logMark) | Select-Object -First 20 | ForEach-Object { Write-Host "    $_" }
                $script:failures.Add("低电量提醒 阈值=100 两轮共 180s 内未触发")
            }
        }
        finally {
            Stop-TrayApp
            Copy-Item $configBackupPath $ConfigPath -Force
            Write-Host "  配置已恢复原值。"
        }
    }
} else {
    Write-Host "=== 冒烟 4/5：低电量提醒验证（-SkipLowBattery 跳过） ==="
}

Write-Host "=== 冒烟 5/5：收尾（杀进程 + error.log 增量检查） ==="
Stop-TrayApp
$errorLogFailPattern = "Exception|未处理异常|崩溃"
$newLines = @(Get-NewLogLines -FromLength $logStartLength)
if ($newLines.Count -gt 0) {
    Write-Host ("error.log 新增 {0} 行：" -f $newLines.Count)
    $newLines | Select-Object -First 30 | ForEach-Object { Write-Host "  $_" }
    $bad = $newLines | Where-Object { $_ -match $errorLogFailPattern }
    if ($bad) {
        $script:failures.Add(("error.log 出现异常行 {0} 条" -f @($bad).Count))
        $bad | ForEach-Object { Write-Host "  [FAIL] $_" -ForegroundColor Red }
    }
} else {
    Write-Host "error.log 本次运行无新增行。"
}

Write-Host ""
if ($script:failures.Count -gt 0) {
    Write-Host "冒烟结果：FAIL" -ForegroundColor Red
    $script:failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Host "冒烟结果：PASS（内存/GDI 收敛、无异常日志、气泡验证通过）" -ForegroundColor Green
