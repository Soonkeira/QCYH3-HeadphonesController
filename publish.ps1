# QCY H3 托盘控制工具 —— 发布打包脚本（阶段 6）
# 产出：
#   publish\                  框架依赖版（需 .NET 8 Desktop Runtime x64）
#   publish-selfcontained\    自包含版（内含 .NET 运行时，解压直跑）
#   dist\QcyH3Tray-<版本>-portable.zip   便携包：自包含版 + 使用说明.txt + LICENSE
#   dist\QcyH3Tray-<版本>-setup.zip      安装包：框架依赖版 + 安装/使用说明 + LICENSE + install/uninstall 脚本
# 用法：powershell -ExecutionPolicy Bypass -File publish.ps1 [-Version 1.0.0]
[CmdletBinding()]
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $Root

$PublishDir = Join-Path $Root "publish"
$SelfContainedDir = Join-Path $Root "publish-selfcontained"
$DistDir = Join-Path $Root "dist"
$PackagingDir = Join-Path $Root "tools\packaging"

Write-Host "=== [1/4] 框架依赖发布（Release win-x64） ==="
dotnet publish src/QcyH3Tray.App -c Release -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "框架依赖发布失败" }

Write-Host "=== [2/4] 自包含发布（Release win-x64，含 .NET 运行时） ==="
dotnet publish src/QcyH3Tray.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $SelfContainedDir
if ($LASTEXITCODE -ne 0) { throw "自包含发布失败" }

Write-Host "=== [3/4] 组包 dist ==="
$PortableStage = Join-Path $DistDir "stage-portable"
$SetupStage = Join-Path $DistDir "stage-setup"
Remove-Item $DistDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $PortableStage, $SetupStage -Force | Out-Null

# 便携版：自包含版 + 使用说明 + LICENSE（解压即用，无需 .NET）
Copy-Item (Join-Path $SelfContainedDir "*") $PortableStage -Recurse -Force
Copy-Item (Join-Path $PackagingDir "使用说明.txt") $PortableStage -Force
Copy-Item (Join-Path $Root "LICENSE") $PortableStage -Force

# 安装包：框架依赖版 + 安装/使用说明 + LICENSE + 安装/卸载脚本
Copy-Item (Join-Path $PublishDir "*") $SetupStage -Recurse -Force
Copy-Item (Join-Path $PackagingDir "安装说明.txt") $SetupStage -Force
Copy-Item (Join-Path $PackagingDir "使用说明.txt") $SetupStage -Force
Copy-Item (Join-Path $Root "LICENSE") $SetupStage -Force
Copy-Item (Join-Path $PackagingDir "install.ps1"), (Join-Path $PackagingDir "uninstall.ps1") $SetupStage -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$portableZip = Join-Path $DistDir "QcyH3Tray-$Version-portable.zip"
$setupZip = Join-Path $DistDir "QcyH3Tray-$Version-setup.zip"
# 显式 UTF-8 条目名（中文说明文件名在资源管理器中正确显示）
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $PortableStage, $portableZip, [System.IO.Compression.CompressionLevel]::Optimal, $false, [System.Text.Encoding]::UTF8)
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $SetupStage, $setupZip, [System.IO.Compression.CompressionLevel]::Optimal, $false, [System.Text.Encoding]::UTF8)
Remove-Item $PortableStage, $SetupStage -Recurse -Force

Write-Host "=== [4/4] 产物清单 ==="
foreach ($zip in @($portableZip, $setupZip)) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $sizeMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)
        Write-Host ("{0}  ({1} MB, {2} 个文件)" -f (Split-Path $zip -Leaf), $sizeMB, $archive.Entries.Count)
    }
    finally {
        $archive.Dispose()
    }
}
Write-Host ""
Write-Host "完成。产物位于 $DistDir"
