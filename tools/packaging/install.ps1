# QCY H3 托盘控制工具 —— 安装脚本（用户机器上运行；随 setup.zip 分发）
# 用法（二选一）：
#   1) 右键 install.ps1 →「使用 PowerShell 运行」（不带参数）
#   2) PowerShell 命令行：
#        powershell -ExecutionPolicy Bypass -File .\install.ps1 [-Autostart]
# 参数：
#   -Autostart   可选：同时注册开机自启（HKCU Run 键，与 App 内「开机自启」开关同一键值）
#   -WhatIf      干跑：只打印将要执行的动作，不做任何更改
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$Autostart
)

$ErrorActionPreference = "Stop"
$SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$TargetDir = Join-Path $env:LOCALAPPDATA "Programs\QcyH3Tray"
$ExePath = Join-Path $TargetDir "QcyH3Tray.App.exe"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$ShortcutName = "QCY H3 控制中心.lnk"

if (-not (Test-Path (Join-Path $SourceDir "QcyH3Tray.App.exe"))) {
    throw "未在脚本目录找到 QcyH3Tray.App.exe：请先将压缩包完整解压，再运行本脚本。"
}

# 0) 旧版本在运行则先退出（托盘常驻应用）
if ($PSCmdlet.ShouldProcess("QcyH3Tray.App", "停止正在运行的实例")) {
    Get-Process -Name "QcyH3Tray.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 1) 程序复制到 %LOCALAPPDATA%\Programs\QcyH3Tray（仅当前用户，无需管理员权限；含 uninstall.ps1 便于日后卸载）
if ($PSCmdlet.ShouldProcess($TargetDir, "复制程序文件")) {
    Write-Host "安装目录：$TargetDir"
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null
    Copy-Item (Join-Path $SourceDir "*") $TargetDir -Recurse -Force
}

# 2) 开始菜单快捷方式
$programs = [Environment]::GetFolderPath("Programs")
$shortcutPath = Join-Path $programs $ShortcutName
if ($PSCmdlet.ShouldProcess($shortcutPath, "创建开始菜单快捷方式")) {
    $wsh = New-Object -ComObject WScript.Shell
    $shortcut = $wsh.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $ExePath
    $shortcut.WorkingDirectory = $TargetDir
    $shortcut.Description = "QCY H3 耳机托盘控制工具"
    $shortcut.Save()
    Write-Host "开始菜单快捷方式：$shortcutPath"
}

# 3) 可选：开机自启（与 App 内设置读写同一注册表键，互认互斥）
if ($Autostart) {
    if ($PSCmdlet.ShouldProcess("$RunKey\QcyH3Tray", "写入开机自启")) {
        New-ItemProperty -Path $RunKey -Name "QcyH3Tray" -Value ('"{0}"' -f $ExePath) -PropertyType String -Force | Out-Null
        Write-Host "开机自启：已启用"
    }
}

Write-Host ""
Write-Host "安装完成。启动方式：开始菜单「QCY H3 控制中心」，或直接运行："
Write-Host "  $ExePath"
