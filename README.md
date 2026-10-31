# QcyH3Tray

QCY H3 头戴式蓝牙耳机（杰理 JieLi 芯片）的 Windows 托盘控制工具。通过 BLE 上的 RCSP
（Remote Control Service Protocol）协议控制耳机：降噪模式、EQ、游戏模式、自动关机、
按键自定义、电量与低电量提醒等。

**相比初版托盘原型（四态连接、单窗口、只能切降噪/EQ/游戏模式），1.0.0 完成了产品化升级：
九态连接状态机 + 数据有效性语义（未知/过期/失败绝不被当作现值）、快捷面板与控制中心双形态 UI、
EQ 七段编辑器与本地配置、自动关机与按键自定义编辑器、低电量系统通知、全矢量产品视觉与
便携/安装发布包，测试从 123 项增至 458 项。**

## 功能清单（1.0.0）

| 领域 | 功能 |
| --- | --- |
| 连接 | BLE 扫描与首启设备选择、自动重连（广播监听 + 1s/5s/30s 退避 + 断开/拒绝访问冷却）、蓝牙关闭一等状态与自动恢复、耳机不可达持续监听、单实例二次启动唤起 |
| 状态 | 九态状态机（未连接/重连中/扫描中/不可达/连接中/同步中/已连接/响应异常/蓝牙不可用）、字段级数据有效性（Unknown/Loading/Available/Stale/Error/Unsupported）、「耳机无响应」横幅（连续 2 次命令失败进入，任一成功恢复） |
| 降噪 | 13 模式完整读写：关闭/通透/降噪低中高/自适应 + 通勤低中高/室内低中高/抗风，写后回读确认，主动推送同步 |
| EQ | v1 七段读写（eqprobe3 真机闭环）、7 官方预设 + 自定义曲线匹配高亮、七段编辑器（曲线预览/总增益/逐段 Q 只读）、本地配置档案保存/加载/删除/导入/导出 |
| 电量 | 单一电量读取 + 15s 轮询 + 主动推送、托盘电量徽章（分级色）、低电量系统通知（阈值 + 迟滞 + 会话去重） |
| 游戏模式 | 开关读写（写后回读确认） |
| 自动关机 | 不自动关机/5/10/15/30/60 分钟档位，选择即写 + 回读确认（真机探针验证过） |
| 按键自定义 | 4 键 × 单击/双击/三击/四击/长按整表读取与写入（整表写守卫，出厂槽只读） |
| 查找/提示音 | 查找耳机响铃、播放提示音（确认弹窗，ack 通道已验证；音效效果待首用确认） |
| 设置 | 开机自启（HKCU Run 键）、桌面通知开关、低电量阈值、诊断信息（状态/有效性/日志目录） |
| 视觉 | 全代码矢量原创绘制：3/4 视角主视觉（浅/深底）、应用图标（16–256 七帧 ICO）、托盘耳机剪影 + 电量徽章 |

未经真机协议验证的功能（固件升级、恢复出厂、LDAC/空间音频、双设备开关等）一律不上 UI，
完整边界见 [docs/upgrade/feature-matrix.md](docs/upgrade/feature-matrix.md) §8。

## 下载与安装

从发布产物 `dist/` 目录（或 Release 页）获取：

- **便携版** `QcyH3Tray-1.0.0-portable.zip`（自包含 .NET 运行时）：解压即用，
  运行 `QcyH3Tray.App.exe`，无需安装任何依赖。
- **安装包** `QcyH3Tray-1.0.0-setup.zip`（框架依赖版）：解压后按「安装说明.txt」执行
  `install.ps1`——安装到 `%LOCALAPPDATA%\QcyH3Tray`、创建开始菜单快捷方式（需 .NET 8 Desktop Runtime x64）；
  卸载用 `uninstall.ps1`。

> 便携版与安装包二选一即可；两者数据目录相同（`%APPDATA%\QcyH3Controller` 配置、
> `%LOCALAPPDATA%\QcyH3Tray` 日志），混装前先卸载或退出另一份。

### 配置与数据位置

- 配置：`%APPDATA%\QcyH3Controller\config.json`（目标设备/自启/通知/阈值/窗口尺寸等，缺失字段回默认）
- EQ 本地配置：`%APPDATA%\QcyH3Controller\eq-profiles\`（纯本地 JSON，不涉及耳机）
- 日志：`%LOCALAPPDATA%\QcyH3Tray\error.log`（全局异常与诊断）

## 托盘应用使用说明

### 启动与首启

- 单实例：重复启动会唤起已运行实例（快捷面板；带 `--show-control-center` 参数则唤起控制中心）。
- 首次启动（配置无目标设备）弹出**设备选择窗**：点击「扫描」→ 列表按名称含 QCY/H3 置顶加粗 →
  双击或选中后「确定」保存目标并开始连接；「取消」= 退出应用。
- 已有目标设备时后台自动连接：失败自动重连（广播监听 + 退避；蓝牙关闭显示「蓝牙不可用」并在
  蓝牙恢复后 30 秒内自动回连）。
- 全局异常写入 `%LOCALAPPDATA%\QcyH3Tray\error.log`，不崩溃。

### 双形态 UI

**快捷面板**（托盘左键 / 二次启动唤起，托盘上方弹出，失焦自动收起）——「快」：
耳机主视觉、电量（含有效性渲染）、降噪四段 + 档位/场景子行、均衡器 7 chips、游戏模式开关、
页脚（控制中心入口 + 设置菜单）。打开时立即全量刷新；未连接时立即触发重连。
所有写操作乐观更新 UI，失败回滚到耳机实际状态；「耳机无响应」横幅只在连续 2 次命令失败后出现。

**控制中心**（页脚按钮 / 托盘右键 / 二次启动参数，独立可调窗口，关窗=隐藏复用）——「全」：
左侧导航六页：

- **概览**：主视觉、设备地址/状态、电量、降噪模式、游戏模式、立即重连/断开；
- **降噪**：四段 + 档位 + 三场景 + 抗风 + 各模式一句话说明；
- **声音**：EQ 预设 chips、总增益（耳机当前值）、打开均衡器编辑器；
- **功能**：游戏模式、自动关机（选择即写）、按键自定义入口、查找耳机/播放提示音（确认弹窗）、双设备连接说明；
- **连接**：设备地址、首连时间、切换设备、立即重连；
- **设置**：开机自启、桌面通知、低电量阈值、关于、诊断信息（展开显示连接状态/数据有效性/日志路径）。

**托盘右键菜单**：`打开控制面板` / `打开控制中心` / `立即重连` / `开机自启`（勾选）/ `退出`。
**托盘图标**：迷你耳机剪影 + 右下角电量徽章（>50 绿 / ≤50 橙 / ≤20 红 / 未知灰；断连=灰剪影无徽章），
悬停显示状态与电量。

### 自动关机使用说明

控制中心 → 功能页 → 自动关机：档位（不自动关机 / 5 / 10 / 15 / 30 / 60 分钟）**选择即写**，
Core 写后回读确认（失败自动回滚显示并计入命令健康）；耳机无操作超过设定时长会**真实自动关机**。
耳机当前值不是档位值时显示为「耳机当前值」原样保留，绝不静默改写。

### 按键自定义使用说明

控制中心 → 功能页 → 「打开按键编辑器」：整表读取耳机当前配置（4 键 × 单击/双击/三击/四击/长按），
按物理键编号分组编辑（固件未公开物理位置，以耳机实际按键为准）；**出厂配置只读展示**；
保存时整表写回（协议要求必须整表——部分表会清掉其余按键，编辑器有 `StructurePreserved` 守卫：
条数一致 + 出厂槽逐字节一致才允许写入），写后回读确认。关窗时有未保存修改会三选确认。

### 低电量通知

设置页可开关「桌面通知」与阈值（10/15/20/30%）。电量 ≤ 阈值时提醒一次；恢复到阈值 +10% 以上或
重新连接后可再次提醒（同一连接会话不重复轰炸）。通知用应用图标呈现为系统通知，点击打开快捷面板。

### 均衡器 · 编辑器

声音页 → 「打开均衡器编辑器」：打开时自动读自耳机（曲线/总增益/各段 Q 只读展示与真机一致）；
总增益与七段滑块 -12.0 ~ +12.0 dB、0.5 步进；`读自耳机` / `恢复耳机当前值` / `重置为平线` /
`应用到耳机`（写后回读确认）；本地配置档案（另存为/加载/覆盖/删除/导入/导出，纯本地 JSON）。
关闭窗口时若有未应用到耳机的修改会三选确认。

## 工程结构

| 工程 | 说明 |
| --- | --- |
| `src/QcyH3Tray.Core` | 协议层（`QcyH3Tray.Core.Rcsp`：帧编解码、TLV、`H3Client`、EQ v1/按键表/自动关机/提示音编解码）+ BLE 层（`QcyH3Tray.Core.Ble`：WinRT 扫描/传输/radio 监听）+ 连接管理（`QcyH3Tray.Core.App`：九态状态机、有效性快照、命令健康与串行化）+ 配置（`QcyH3Tray.Core.Config`） |
| `src/QcyH3Tray.Cli` | 命令行入口（scan / info / setmode / probe / debug / eqprobe 系 / probe-off / probe-keys / probe-tone / probe-multi，真机探针通道） |
| `src/QcyH3Tray.App` | WPF 托盘应用（快捷面板、控制中心、EQ/按键编辑器、通知、托盘宿主、矢量视觉） |
| `tests/QcyH3Tray.Tests` | xunit 测试（458 项）：协议黄金向量、状态机矩阵、命令健康、串行化、有效性、冷却、资源释放、UI 纯逻辑映射 |
| `tools/IconGen` | 应用图标再生工具（独立 console，不入 sln；矢量绘制生成 `Assets/app.ico`） |
| `tools/smoke.ps1` | 长期后台稳定性冒烟（内存/GDI 采样、面板开关、低电量气泡验证、error.log 检查） |
| `tools/packaging/` | install.ps1 / uninstall.ps1 / 安装与使用说明（进安装包） |
| `docs/upgrade/` | 产品化升级文档：plan.md（审查与各阶段执行记录）、feature-matrix.md（功能矩阵与协议依据）、pending-verification.md（人工验收清单）、verification/（终验截图与 UIA dump） |

## 构建与发布

```bash
dotnet build      # 需要 .NET SDK 8+（Windows），0 警告
dotnet test       # 458 项测试
dotnet run --project tools/IconGen        # 重新生成应用图标 Assets/app.ico（--png-dir 可导出帧 PNG）
powershell -ExecutionPolicy Bypass -File publish.ps1   # 发布 + 组包 dist/（便携 zip + 安装 zip）
powershell -ExecutionPolicy Bypass -File tools/smoke.ps1   # 真机冒烟（需耳机在旁）
```

单项目发布：`dotnet publish src/QcyH3Tray.App -c Release -r win-x64 --self-contained false -o publish`。

## 协议来源与致谢

协议来自社区对 QCY H3 的逆向成果，本项目的 C# 实现以下列 MIT 项目为权威蓝本：

- [zhang-jh-1/qcy-h3-controller-zh](https://github.com/zhang-jh-1/qcy-h3-controller-zh)（Python，本仓库协议层逐字节对齐其 `rcsp.py` / `h3_device.py`）
- [Gmarzochi/QCY_H3_WIndowsEdition](https://github.com/Gmarzochi/QCY_H3_WIndowsEdition)（C#，思路参考）
- Quicky（Go，EQ v1 / 按键映射 / 0x14 自动关机权威）、qcy-h3s-windows（H3S 耳塞扩展命令边界参考）
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon)（系统托盘）

以上第三方项目均为 MIT 许可。本项目基于 MIT 协议的上游项目二次创作，同样以 MIT 协议发布，许可证见 [LICENSE](LICENSE)。

## EQ v1 发现（2026-10，真机验证）

- 本机固件对 EQ v2 读（`FE 01 22`）回 status=0 空载荷，但对 **v1 读（`FE 01 20`）稳定回 47 字节**：
  `[0x20][len=45][eqType][masterGain i16LE][freq u16LE + gain i16LE + q u16LE] × 7`
  （55/220/500/1000/1800/2800/7500 Hz；q 为 Q×100，如 120 = Q1.20）。
- v1 写与读同 command ID（0x20）：`20 [len=N×6+3] [eqIndex] [masterGain i16LE] + 6B/段`，
  段增益钳 ±12.7 dB——与 Quicky（`docs/protocol.md`、`internal/command/EQ.go`）逐字节一致
  （Core 实现：`Rcsp/EqV1.cs` + `H3Client.Get/SetEqV1Async`）。
- v1 每段第 3 个 u16 按 Quicky 语义是 **Q×100**；v2 7B/段记录里的 "refGain+Q" 同理应为 "q+bandType"
  （既有 v2 代码保持原样未动，本机固件不可用，仅作旧固件兼容通道）。
- 写入生效与否用 `eqprobe3` 实机验证（全零平线写 → 读回对比 → 基线恢复）。

## CLI 用法

真机探针/诊断通道，与 App 共用 Core（不做任何 UI）：

```bash
# 扫描附近 BLE 设备（默认 6 秒，QCY 设备排前）
dotnet run --project src/QcyH3Tray.Cli -- scan 5

# 连接设备并读取电量 / 降噪模式 / EQ / 游戏模式（成功后保存为默认目标）
dotnet run --project src/QcyH3Tray.Cli -- info "QCY H3"

# 切换降噪模式并回读确认
dotnet run --project src/QcyH3Tray.Cli -- setmode 降噪高

# 真机自检：检查蓝牙 radio + 扫描 5 秒（只扫描，绝不连接）
dotnet run --project src/QcyH3Tray.Cli -- probe

# 抓包诊断（静默收 1s → 降噪读 → EQ 读，各收 3s）
dotnet run --project src/QcyH3Tray.Cli -- debug "QCY H3"

# EQ 实验：eqprobe（首轮探测）/ eqprobe2（二轮纯读）/ eqprobe3（v1 读写验证，写后恢复基线）
dotnet run --project src/QcyH3Tray.Cli -- eqprobe3 "QCY H3"

# 自动关机探针：读 → 写 5 分钟 → 等 → 回读 → 恢复原值（真机验证 0x14 读写闭环）
dotnet run --project src/QcyH3Tray.Cli -- probe-off "QCY H3"

# 按键表探针：整表读 → 单键写 → 回读 → 恢复整表（真机验证按键读写闭环）
dotnet run --project src/QcyH3Tray.Cli -- probe-keys "QCY H3"

# 提示音/查找耳机 ack 探针；组合探针（电量/降噪/EQ/游戏模式/自动关机顺序读）
dotnet run --project src/QcyH3Tray.Cli -- probe-tone "QCY H3"
dotnet run --project src/QcyH3Tray.Cli -- probe-multi "QCY H3"

# 帮助
dotnet run --project src/QcyH3Tray.Cli -- help
```

说明：

- 地址按 12 位十六进制解析（`AA:BB:CC:DD:EE:FF`，冒号/横线可省略）；其余输入按设备名匹配（先精确后前缀）。
- 退出码：成功 `0`，失败 `1`，用法错误 `2`。蓝牙未开启、设备不可见等均输出可读中文错误。
- 配置文件：`%APPDATA%\QcyH3Controller\config.json`，损坏时自动回退默认。
- probe-off / probe-keys / probe-tone 属真机验证探针：读 → 写 → 回读 → **恢复原值**，
  供后续固件行为复核与回归对照。

## 产品视觉说明

本项目的全部视觉资产均为**代码矢量原创绘制**（无外部图片文件、无生成式图片、无第三方库），
与 QCY 官方素材无任何关系；造型为通用头戴式耳机剪影的原创演绎。

- **应用图标** `src/QcyH3Tray.App/Assets/app.ico`：由 `tools/IconGen` 矢量绘制生成 16–256 七帧
  PNG-ICO（Accent 蓝渐变 + 细白外圈，深/浅任务栏均可辨）；修改几何即源。
- **产品主视觉** `src/QcyH3Tray.App/Resources/HeadphoneArt.xaml`：3/4 视角头戴式耳机矢量渲染图
  （512 设计坐标），浅底版 `AppImage/HeadphoneHero` / 深底版 `.Dark` 两键共用同一批几何保证造型一致。
- **托盘图标** `src/QcyH3Tray.App/TrayIconRenderer.cs`：迷你耳机剪影 + 电量徽章胶囊，运行时矢量绘制、
  按状态缓存（`TrayIconRendererTests` 覆盖分级色与缓存键）。
