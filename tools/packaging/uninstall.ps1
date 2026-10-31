# QCY H3 托盘控制工具 —— 卸载脚本（随 setup.zip 分发；安装后位于 %LOCALAPPDATA%\Programs\QcyH3Tray）
# 用法：powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 [-KeepConfig] [-WhatIf]
# 参数：
#   -KeepConfig   保留用户配置 %APPDATA%\QcyH3Controller（设备记忆、通知设置、EQ 配置等）；默认删除
#   -WhatIf       干跑：只打印将要执行的动作，不做任何更改
# 清理内容：运行中的实例、开始菜单快捷方式、开机自启注册表项、用户配置（默认）、程序目录
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$KeepConfig
)

$ErrorActionPreference = "Continue"
$TargetDir = Join-Path $env:LOCALAPPDATA "Programs\QcyH3Tray"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$ShortcutName = "QCY H3 控制中心.lnk"

# 1) 退出运行中的实例
if ($PSCmdlet.ShouldProcess("QcyH3Tray.App", "停止正在运行的实例")) {
    Get-Process -Name "QcyH3Tray.App" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# 2) 开始菜单快捷方式
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Programs")) $ShortcutName
if ($PSCmdlet.ShouldProcess($shortcutPath, "删除开始菜单快捷方式")) {
    Remove-Item $shortcutPath -Force -ErrorAction SilentlyContinue
    Write-Host "已移除开始菜单快捷方式。"
}

# 3) 开机自启注册表项
if ($PSCmdlet.ShouldProcess("$RunKey\QcyH3Tray", "删除开机自启注册表项")) {
    Remove-ItemProperty -Path $RunKey -Name "QcyH3Tray" -ErrorAction SilentlyContinue
    Write-Host "已移除开机自启注册表项（如存在）。"
}

# 4) 用户配置（默认删除；-KeepConfig 保留）
$configDir = Join-Path $env:APPDATA "QcyH3Controller"
if ($KeepConfig) {
    Write-Host "已保留用户配置：$configDir"
} elseif ($PSCmdlet.ShouldProcess($configDir, "删除用户配置（-KeepConfig 可保留）")) {
    Remove-Item $configDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "已删除用户配置（下次安装为全新状态；加 -KeepConfig 可保留）。"
}

# 5) 程序目录：本脚本自身位于目录内，延迟 2 秒交给独立进程删除
if ((Test-Path $TargetDir) -and $PSCmdlet.ShouldProcess($TargetDir, "删除程序目录")) {
    Start-Process cmd.exe -ArgumentList "/c", "timeout /t 2 /nobreak >nul & rd /s /q `"$TargetDir`"" -WindowStyle Hidden
    Write-Host "程序目录将在 2 秒后删除：$TargetDir"
}

Write-Host "卸载完成。"
