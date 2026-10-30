# QcyH3Tray

QCY H3 头戴式蓝牙耳机（杰理 JieLi 芯片）的 Windows 托盘控制工具。通过 BLE 上的 RCSP
（Remote Control Service Protocol）协议控制耳机：降噪模式、EQ、游戏模式、电量查询等。

## 最终形态规划

常驻系统托盘的 WPF 应用：

- 托盘菜单直接切换降噪模式（关闭 / 通透 / 降噪低中高 / 自适应）
- EQ 读取与写入（自定义曲线 + 内置预设）
- 游戏模式（低延迟）开关
- 电量显示
- 连接管理（扫描、自动重连）

## 工程结构

| 工程 | 说明 |
| --- | --- |
| `src/QcyH3Tray.Core` | 协议层（`QcyH3Tray.Core.Rcsp`：帧编解码、TLV、`H3Client`）+ BLE 层（`QcyH3Tray.Core.Ble`：WinRT 扫描/传输）+ 连接管理（`QcyH3Tray.Core.App`：状态机、自动重连）+ 配置（`QcyH3Tray.Core.Config`） |
| `src/QcyH3Tray.Cli` | 命令行入口（scan / info / setmode / probe / debug / eqprobe / eqprobe2，阶段 2 真机自测通道） |
| `src/QcyH3Tray.App` | WPF 托盘应用（阶段 3 填充） |
| `tests/QcyH3Tray.Tests` | xunit 协议黄金向量测试 + 连接状态机测试（假传输层/假扫描器注入） |

## 协议来源与致谢

协议来自社区对 QCY H3 的逆向成果，本项目的 C# 实现以下列 MIT 项目为权威蓝本：

- [zhang-jh-1/qcy-h3-controller-zh](https://github.com/zhang-jh-1/qcy-h3-controller-zh)（Python，本仓库协议层逐字节对齐其 `rcsp.py` / `h3_device.py`）
- [Gmarzochi/QCY_H3_WIndowsEdition](https://github.com/Gmarzochi/QCY_H3_WIndowsEdition)（C#，思路参考）

两个项目均为 MIT 许可。本项目基于 MIT 协议的上游项目二次创作，同样以 MIT 协议发布，许可证见 [LICENSE](LICENSE)。

## EQ v1 发现（2026-10，真机 AA:BB:CC:DD:EE:FF）

- 本机固件对 EQ v2 读（`FE 01 22`）回 status=0 空载荷，但对 **v1 读（`FE 01 20`）稳定回 47 字节**：
  `[0x20][len=45][eqType][masterGain i16LE][freq u16LE + gain i16LE + q u16LE] × 7`
  （55/220/500/1000/1800/2800/7500 Hz；q 为 Q×100，如 120 = Q1.20）。
- v1 写与读同 command ID（0x20）：`20 [len=N×6+3] [eqIndex] [masterGain i16LE] + 6B/段`，
  段增益钳 ±12.7 dB——与 Quicky（`docs/protocol.md`、`internal/command/EQ.go`、
  `internal/response/response.go`）逐字节一致（Core 实现：`Rcsp/EqV1.cs` + `H3Client.Get/SetEqV1Async`）。
- v1 每段第 3 个 u16 按 Quicky 语义是 **Q×100**（此前猜测的"参考增益"实为 Q）；v2 7B/段记录里的
  "refGain+Q"同理应为 "q+bandType"（既有 v2 代码保持原样未动）。
- 写入生效与否用 `eqprobe3` 实机验证（全零平线写 → 读回对比 → 基线恢复）。

## 托盘应用使用说明（阶段 3）

`src/QcyH3Tray.App` 是常驻系统托盘的 WPF 应用（发布：`dotnet publish src/QcyH3Tray.App -c Release -r win-x64 --no-self-contained -o publish`，运行 `publish/QcyH3Tray.App.exe`）。

### 启动与首启

- 单实例：重复启动直接退出（命名 Mutex `QcyH3Tray_SingleInstance`）。
- 首次启动（配置无目标设备）弹出**设备选择窗**：点击「扫描」（6 秒）→ 列表按名称含 QCY/H3 置顶加粗 → 双击或选中后「确定」保存目标并开始连接；「取消」= 退出应用。
- 蓝牙未开启时扫描会给出明确中文提示。
- 已有目标设备时后台自动连接，失败自动重连（广播监听 + 1s/5s/30s 退避）。
- 全局异常写入 `%LOCALAPPDATA%\QcyH3Tray\error.log`，不崩溃。

### 托盘

- **图标 = 电量数字徽章**：圆角底 + 居中电量数字（断连/未知=灰，≤20=红，≤50=橙，>50=绿）。
- **悬停提示**：`QCY H3 — 已连接\n电量 80%`；未连接时显示当前状态（连接中…/重连中…/未连接）。
- **左键**：显示/隐藏主面板。
- **右键菜单**：`打开控制面板` / `立即重连` / `开机自启`（勾选，写 HKCU `Software\Microsoft\Windows\CurrentVersion\Run`）/ 分隔线 / `退出`。

### 主面板

托盘上方弹出（宽 300，白卡片圆角 16 + 柔和阴影），失焦自动收起，Esc 也可收起。打开时立即全量刷新（电量/降噪/EQ/游戏模式）；未连接时立即触发重连。

- **状态行**：`QCY H3` + 右侧状态文字（已连接=绿；连接中…/重连中…/未连接=灰，未连接时附 ↻ 重连按钮）。
- **耳机无响应横幅**：连续 2 次写命令失败后出现（红底红字），任一次成功即消失。
- **电量**：单一整数百分比；未连接显示 `--`。
- **噪声控制**：`关闭 | 通透 | 降噪 | 自适应` 四段分段控件（滑块式高亮，带过渡动画）。选中「降噪」出现第二行：`低 | 中 | 高` 档位 + `通勤 | 室内 | 抗风` 场景小按钮。当前模式是场景模式时，四段停在「降噪」，场景组高亮对应项，档位显示该场景的档位（抗风噪无档位则三个档位都不亮）。
- **均衡器**：`默认 流行 重低音 摇滚 柔和 古典 自定义` chips。点前六个 = 立即把预设 10 段偏移经 `EqV1BandMap` 映射成 7 段曲线写入（eqType/masterGain/各段 Q 保持耳机当前值）。**协议读不到"当前预设 ID"**，高亮靠曲线匹配：当前 7 段与某预设映射一致则高亮它（全零平线匹配到「默认」），否则高亮「自定义」。点「自定义」打开 EQ 编辑窗。
- **游戏模式**：开关（写 `SET_ADV_INFO`，回读确认）。
- **页脚**：右侧「⚙ 设置」小菜单：`开机自启`（勾选，与托盘菜单同一开关）/ `关于`。
- 所有写操作乐观更新 UI；失败自动重试一次，再失败计入横幅并把 UI 回滚到耳机实际状态。

### 均衡器 · 自定义（EQ 编辑窗）

- 顶部总增益（masterGain）滑块 + 七段竖向增益滑块，全部 -12.0 ~ +12.0 dB、0.5 步进，实时显示 dB 值与中心频率（55/220/500/1000/1800/2800/7500 Hz）。
- `读自耳机`（EQ v1 读取刷新滑块）/ `应用到耳机`（EQ v1 写入，eqType 与各段 Q 保持耳机当前值）/ `重置`（全零，需再点应用）。
- 打开窗口时自动读自耳机；窗口失焦**不**自动关闭（与主面板不同）。

### EQ v1（本机固件行为）

本机固件 EQ 读必须走 **v1**（`FE 01 20`，7 段 + masterGain + eqType）；v2 读回空载荷。
`ConnectionManager` 连接初始化与全量刷新都 v1 优先，v1 为空时回退 v2（旧固件兼容）。
协议细节见上文「EQ v1 发现」与 `Rcsp/EqV1.cs`。

## 当前进度

- [x] 阶段 1：解决方案骨架 + RCSP 协议层移植 + 黄金向量单元测试（内存假 `IBleLink` 驱动）
- [x] 阶段 2：WinRT BLE 传输层（`BleTransport`/`BleScanner`）+ 连接管理（`ConnectionManager`）+ CLI 接线
- [x] 阶段 3：托盘 UI（电量徽章图标 / 降噪四段+场景 / EQ 预设与自定义编辑 / 游戏模式 / 自动重连 / 首启设备选择）

## 构建

```bash
dotnet build   # 需要 .NET SDK 8+（Windows）
dotnet test    # 协议层 + 连接管理单元测试
```

## CLI 用法

阶段 2 的真机自测通道，与 App 共用 Core（不做任何 UI）：

```bash
# 扫描附近 BLE 设备（默认 6 秒，QCY 设备排前；输出地址/名称/RSSI/QCY 厂商标记）
dotnet run --project src/QcyH3Tray.Cli -- scan 5

# 连接设备并读取电量 / 降噪模式 / EQ（预设 + 10 段增益）/ 游戏模式
# 成功后把该设备保存为默认目标（%APPDATA%\QcyH3Controller\config.json）
dotnet run --project src/QcyH3Tray.Cli -- info AA:BB:CC:DD:EE:FF
dotnet run --project src/QcyH3Tray.Cli -- info "QCY H3"

# 切换降噪模式并回读确认（省略地址时使用上次保存的默认目标）
# 模式: 关闭|通透|降噪低|降噪中|降噪高|通勤低|通勤中|通勤高|室内低|室内中|室内高|抗风|自适应
dotnet run --project src/QcyH3Tray.Cli -- setmode 降噪高
dotnet run --project src/QcyH3Tray.Cli -- setmode 室内高 "QCY H3"

# 真机自检：检查蓝牙 radio + 扫描 5 秒（只扫描，绝不连接）
dotnet run --project src/QcyH3Tray.Cli -- probe

# 诊断/实验通道（真机排查专用，输出全量原始帧）：
# debug  = 抓包诊断（静默收 1s → 降噪读 → EQ 读，各收 3s）
# eqprobe = EQ 读取实验第一轮（5 组探测；唯一写操作为默认预设 0x01 全零偏移，听感无变化）
# eqprobe2 = EQ 读取实验第二轮（纯读无写：feature map / EQ 读变体 FE 01 20·46·47·44·27 /
#            GET_SYS_INFO 单属性探针（预设 ID）/ GATT 特征枚举与直读 / FE 01 01..63 全 ID 扫描）
# eqprobe3 = EQ v1 读写验证（读基线 → 写全零平线 → 等 1s 读回对比 → 原样恢复基线 → 读回验证）
dotnet run --project src/QcyH3Tray.Cli -- debug "QCY H3"
dotnet run --project src/QcyH3Tray.Cli -- eqprobe "QCY H3"
dotnet run --project src/QcyH3Tray.Cli -- eqprobe2 <地址或名称>
dotnet run --project src/QcyH3Tray.Cli -- eqprobe3 AA:BB:CC:DD:EE:FF

# 帮助
dotnet run --project src/QcyH3Tray.Cli -- help
```

说明：

- 地址按 12 位十六进制解析（`AA:BB:CC:DD:EE:FF`，冒号/横线可省略）；其余输入按设备名匹配（先精确后前缀）。
- 退出码：成功 `0`，失败 `1`，用法错误 `2`。蓝牙未开启、设备不可见等均输出可读中文错误。
- 配置文件：`%APPDATA%\QcyH3Controller\config.json`（`TargetAddress` / `TargetName` / `AutoStart` 预留），损坏时自动回退默认。
- 连接流程与 Python 参考实现对齐：GATT 连接 → 握手（opCode 0x0B，params `00 00`）→ 200ms → 依次读电量/降噪模式/EQ/游戏模式。
