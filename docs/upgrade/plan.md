# QCYH3-HeadphonesController 产品化升级执行计划（阶段 1 代码审查产出）

> 审查基线：`dotnet build` 0 警告 / `dotnet test` 123/123 通过（2026-10-31，git `4981bc6`）。
> 本文档为后续阶段的执行与验收依据，所有结论均基于实际代码（file:line 可核对）。
> 配套文档：[feature-matrix.md](feature-matrix.md)（功能矩阵与协议依据）。

---

## 1. 架构现状与职责边界决策（关键前提）

### 1.1 现状

| 层 | 现状 | 证据 |
| --- | --- | --- |
| `ConnectionManager`（Core） | 4 态状态机（Disconnected/Scanning/Connecting/Connected）+ 重连循环 + 15s 电量轮询 + 设备数据快照 `DeviceData`（无有效性语义） | `src/QcyH3Tray.Core/App/ConnectionManager.cs:7-13, 16-44` |
| `AppState`（App） | UI 线程投影 + **独立**的命令失败计数 `_consecutiveFailures` | `src/QcyH3Tray.App/AppState.cs:18, 32` |
| `ConnectionManager._consecutiveFailures` | **同名但不同义**：连接失败计数（仅用于重连退避） | `ConnectionManager.cs:100, 512, 791-793` |

两套 `_consecutiveFailures` 同名不同义，正是用户怀疑的"语义混淆"来源之一（见缺陷 D1/D10）。

### 1.2 职责边界（后续阶段遵守）

**`ConnectionManager`（Core，可测试，无 WPF 依赖）负责：**

1. **统一连接状态机**，状态扩为 7 相（见 §2.1），全部状态转移经 `StateChanged` 抛出；
2. **命令健康**：连续写命令失败计数、成功清零、达到阈值抛 `CommandHealthChanged`——从 AppState 下沉（命令在 Core 发出，Core 最清楚成败；AppState 只投影）；
3. **设备数据快照 + 有效性**：`DeviceData` 每字段带 `DataValidity`（Unknown/Loading/Available/Stale/Error/Unsupported）与"最后确认时间"（见 §2.2）；
4. **写命令串行化**（§3 D8）。

**`AppState`（App，UI 线程）只负责：**

1. 把 Core 事件 marshal 到 Dispatcher（现状保留，`AppState.cs:167-197`）；
2. 暴露 UI 派生属性（StateLabel、托盘文案等，`AppState.cs:35-41`）；
3. 设置类状态（开机自启等）。

**删除** AppState 自己的失败计数（`AppState.cs:17-18, 132-147`），横幅语义完全由 Core 的命令健康事件驱动，并由连接状态转移自动清除——这是 D1 的根治方案。

---

## 2. 目标状态模型（后续 UI 与状态修复的对齐基准）

### 2.1 统一连接状态机

```
                    ┌──────────────────────────────────────────────┐
                    │            BluetoothUnavailable              │←── 扫描/连接抛 BluetoothUnavailableException
                    │  （radio 关闭/缺失，监听 Radio.StateChanged   │    （现 ReconnectLoopAsync:832-839 直接 return，
                    │    自动恢复 → 回 Reconnecting）               │     循环死亡永不恢复，见 D4）
                    └──────────────┬───────────────────────────────┘
                                   │ radio 恢复
 Disconnected ──选目标──▶ Connecting ──GATT+握手成功──▶ Syncing ──初始化读完成──▶ Connected
 （无目标/手动断开）          │                            │                      │
                              │ 失败                        │ 部分读失败→仍进 Connected，      │ 链路断开
                              ▼                            │ 失败字段标 Error                ▼
                          Reconnecting（= 现 Scanning，退避 1s/5s/30s）◀──────────────┘
                              │ 看到目标广播
                              ▼
                          Connecting
```

- `Connecting`（连接中）与 `Syncing`（同步中）分离：现在握手后、置 Connected 前做 4 个初始化读（`ConnectionManager.cs:464-467`），期间 UI 显示"连接中…"但电量事件已到达，状态与数据错位（见 D9）。
- `Reconnecting` 与 `Connecting` 在 UI 上可同为"连接中…"文案，但状态机内区分（是否处于退避循环）。
- `BluetoothUnavailable` 是一等状态（现在是异常路径一次性文案，`ConnectionManager.cs:836-839`）。
- `DeviceUnreachable` 不单列状态：由 `Reconnecting` + 状态文案承载（"耳机不可达/被其它设备占用"），避免状态爆炸。
- `Connected` 期间的命令失败不改变连接状态，只驱动命令健康（横幅）。

### 2.2 数据有效性语义

```csharp
public enum DataValidity { Unknown, Loading, Available, Stale, Error, Unsupported }
```

| 值 | 语义 | 触发 |
| --- | --- | --- |
| Unknown | 从未读到过 | 连接建立前；读回 null |
| Loading | 已发起读、未返回 | 面板打开/全量刷新进行中 |
| Available | 最近一次读成功且新鲜 | 正常 |
| Stale | 有值但超过保鲜期（电量 >60s、ANC/EQ/游戏模式 >5min 或断线） | 断线时全量转 Stale（现在是保留旧值照常显示，见 D5/D11） |
| Error | 最近一次读/写失败 | 命令失败、读超时 |
| Unsupported | 协议确认本机不支持（EQ v2 读回空载荷即典型） | 探针确认后落配置 |

`DeviceData` 现为纯数据快照（`ConnectionManager.cs:16-44`），升级为 `字段 × {值, validity, timestampUtc}`；UI 侧按 validity 决定显示 `--`/占位/旧值置灰/错误文案。**禁止**再出现"Unknown 显示为关"（D11）。

---

## 3. 缺陷清单（按严重度，每条含 file:line 证据与根因）

### P0（用户实锤 / 根治后才可谈产品化）

#### D1「耳机无响应」横幅在连接恢复后残留（用户截图实锤 1）

**现象**：启动时耳机未开机出现过告警；之后连接成功（电量 70% 正常显示），横幅仍持续显示。

**根因链（完整证据）**：

1. 触发：未连接时任何 UI 写操作（点 ANC 分段/EQ chip/游戏模式开关）→ `Manager.SetNoiseModeAsync` 等先取 `CurrentClient()` 为 null 直接抛 `InvalidOperationException`（`ConnectionManager.cs:230-233, 266-270, 289-292`）→ `AppState.TryCommand` 捕获并按"命令失败"计账（`AppState.cs:119-130`）→ 两次用户操作后 `_consecutiveFailures == 2` → `CommandHealthChanged(true)`（`AppState.cs:140-147`）→ 横幅出现（`ControlPanelWindow.xaml.cs:134-142`）。**"未连接"被当成了"耳机无响应"。**
2. 残留：横幅唯一的清除路径是 `RunWriteCommandAsync` 里某次写命令最终成功时调用 `ResetHealth()`（`AppState.cs:107-117, 132-138`）。`OnManagerStateChanged` 收到 `Connected` 时**不做任何健康重置**（`AppState.cs:169-174`）；连接管理器侧连接成功只清自己的连接失败计数（`ConnectionManager.cs:476`），对 AppState 的命令健康一无所知。→ 连接恢复后无人清横幅。
3. 面板每次 `SyncFromState` 还会用 `_app.Degraded` 把横幅画回来（`ControlPanelWindow.xaml.cs:169`）。

**修复方向**：
- 命令健康下沉 Core（§1.2）；未连接时 UI 写操作直接短路（不发起命令、不计失败，触发连接）；
- 状态机进入 `Connected` 时强制清零命令健康并抛 `CommandHealthChanged(false)`；
- 横幅只在 `Connected` 且命令健康降级时显示。

**验收**：耳机关机→点任意控件→开机→自动重连成功→横幅自动消失；全程无"未连接也算命令失败"的日志。

#### D2「重低音」chip 文字垂直被裁切（用户截图实锤 2）

**现象**：EQ 预设 chip 中文字被拦腰截断。

**根因分析（代码证据）**：
- chip 模板 `ChipRadioButton`：`ContentPresenter Margin="0,-1,0,0"` 负上边距（`src/QcyH3Tray.App/Resources/Styles.xaml:104`）+ `Border Padding="12,5"` 垂直仅 5px（`Styles.xaml:98`）；小号 chip 同样 `Margin="0,-1,0,0"` + `Padding="9,4"`（`Styles.xaml:147, 153`）。
- 全工程未显式设置 CJK 字体家族（`Styles.xaml` 全文无 `FontFamily`；控件继承 WPF 默认 Segoe UI）。中文字形经字体回退（Microsoft YaHei UI）渲染，其实际字形 ink 高与行高均大于 Segoe UI 的拉丁度量；测量按主字体算、渲染按回退字体画，垂直余量被 5px padding + 负 margin 吃掉后即出现字形被裁。
- 附带风险：主面板 `SizeToContent="Height"` + `AllowsTransparency="True"`（`ControlPanelWindow.xaml:5-9`）在高 DPI 下窗口高度计算偏差会加剧底部内容裁切。

**修复方向**：移除负 margin；垂直 padding 提到 6-7px 或给模板设 `MinHeight`；应用级显式 `FontFamily="Microsoft YaHei UI"`（或 Segoe UI Variable + CJK 回退声明）；开启 `UseLayoutRounding`；重设计时控制中心窗口改固定尺寸 + 可滚动内容。验收必须覆盖 100% / 125% / 150% DPI 三档截图。

**验收**：三档 DPI 下全部 chip（含"重低音""自定义""抗风"）文字完整无裁切。

#### D3 统一状态机/数据有效性缺失（架构性 P0）

**证据**：
- 状态机只有 4 态（`ConnectionManager.cs:7-13`），蓝牙不可用不是状态（D4），无"同步中"（D9）；
- `DeviceData` 无有效性字段（`ConnectionManager.cs:16-44`），断线后快照原样保留（`TeardownSessionLocked` 不清数据，`ConnectionManager.cs:969-993`）；
- UI 层面：断连后面板仍照常显示旧 ANC/EQ/游戏模式选中态（`ControlPanelWindow.xaml.cs:164-167` 不检查连接状态）；游戏模式 Unknown（null）直接显示为关（`ControlPanelWindow.xaml.cs:167` `_app.Data.GameMode == true`）。

**修复方向**：按 §1.2 / §2 落地。这是 P0-1 的核心工作量。

#### D4 蓝牙关闭后自动重连循环永久死亡

**证据**：`ReconnectLoopAsync` 捕获 `BluetoothUnavailableException` 后 `SetState(Disconnected, exception.Message); return;`（`ConnectionManager.cs:832-839`）——循环退出且不再有任何恢复手段（无 radio 状态监听、无定时重试），用户打开蓝牙后只能手动点"立即重连"。

**修复方向**：进入 `BluetoothUnavailable` 状态并订阅 `Radio.StateChanged`（`Windows.Devices.Radios`，`BluetoothAvailability.cs` 已在用同一 API）或每 10s `EnsureAvailableAsync` 探测；radio 恢复后自动回到 `Reconnecting`。

**验收**：关蓝牙→状态显示"蓝牙不可用"→开蓝牙→30s 内自动恢复连接。

### P1（功能正确性 / 用户可感知）

#### D5 断线后数据不失效（已在 D3 覆盖语义，此处记 UI 行为缺陷）

`AppState.BatteryPercent` 断线从不清空（`AppState.cs:183-188` 只增不减），UI 用 `connected &&` 临时掩盖（`ControlPanelWindow.xaml.cs:160`、`TrayHost.cs:150`）；非电量字段（ANC/EQ/游戏模式）连掩盖都没有（见 D3 第 3 条）。修复随 D3。

#### D6 已关闭的主面板被复用 → 托盘左键永久失效（崩溃被全局钩子吞掉）

**证据**：`TrayHost.PresentPanel` 用 `_panel ??= new ControlPanelWindow(_app)`（`TrayHost.cs:118`）只判 null 不判关闭状态。主面板虽设计为 Hide 复用（`ControlPanelWindow.xaml.cs:536-550`），但 `WindowStyle="None"` 的窗口仍可被 Alt+F4/系统菜单关闭（`Closed` 会解绑事件，`ControlPanelWindow.xaml.cs:56-61`）；对已 Close 的窗口再 `Show()` 抛 `InvalidOperationException`，被 `App.xaml.cs:142-146` 的全局处理器 `args.Handled = true` 吞掉 → 之后每次点托盘都静默失败，面板永远打不开。
（对照：EQ 编辑窗的复用判的是 `_eqDetail is { IsLoaded: true }`，`ControlPanelWindow.xaml.cs:459`——主面板漏了同样的检查。）

**修复**：`if (_panel is not { IsLoaded: true }) _panel = new ControlPanelWindow(_app);` 并补测试。

#### D7 `H3Client` 流重组可能丢弃跨通知分片的帧头

**证据**：`HandleDataReceived` 找不到完整帧头时 `_rxBuffer.Clear()`（`src/QcyH3Tray.Core/Rcsp/H3Client.cs:118-121`）。若某次 notify 分片恰以 `FE DC` 结尾，该前缀被整段丢弃，下一片的 `BA …` 无法成帧 → 偶发丢帧（表现为某次读超时）。BLE 通知分片边界不受控，属真实风险。

**修复**：无完整帧头时，保留缓冲区末尾"恰好是 `FE DC BA` 前缀"的最长后缀再 break。补跨片重组单元测试（FakeBleLink 分两次投递）。

#### D8 写命令无串行化，可能交错

**证据**：`ConnectionManager` 的 `SetNoiseModeAsync/SetEqV1Async/SetGameModeAsync`（`ConnectionManager.cs:230-247, 266-283, 289-304`）均为"写 → 回读"两步，方法间无互斥；UI 快速连续操作（EQ chip 点击 + ANC 分段）可产生交错写。当前靠 WPF 单线程 + 用户手速兜底，属竞态隐患。

**修复**：Core 内 `SemaphoreSlim(1,1)` 串行化所有 RCSP 命令（读也纳入，防止初始化读与用户写交错）。

#### D9 连接初始化读失败会拖垮已建立的链路 + Connecting 期间事件乱序

**证据**：`ConnectCoreAsync` 在握手成功后、置 `Connected` 前串行执行 4 个初始化读（`ConnectionManager.cs:464-467`）；其中任何一个读抛异常（传输层异常路径）→ 走 catch → `DisposeSession` → 整次连接失败进入重连（`ConnectionManager.cs:499-523`）——链路本身是好的。且这些读的事件先于 `Connected` 状态事件到达 UI（状态在 479 才置）。

**修复**：初始化读逐项 try/catch，失败项标 `DataValidity.Error` 但仍进 `Connected`；状态先置 `Syncing` 再逐步填充。

#### D10 双 `_consecutiveFailures` 同名混淆

`ConnectionManager._consecutiveFailures`（连接失败，驱动退避，`ConnectionManager.cs:100`）与 `AppState._consecutiveFailures`（命令失败，驱动横幅，`AppState.cs:18`）同名不同义。随 D1 重构：前者更名 `_connectionFailures` 并保留在 Core，后者删除。

### P2（打磨 / 低概率）

#### D11 EQ v2 回退路径是"死数据"

`RefreshEqAsync` 在 v1 失败时回退 v2 并写 `EqPresetId/EqOffsets`（`ConnectionManager.cs:617-635`），但 UI 只消费 `EqV1`（`ControlPanelWindow.xaml.cs:165, 373-408`）——v2 数据写了没人用。保留为回退通道但需在矩阵中标注（见 feature-matrix），UI 不得依赖。

#### D12 托盘图标为 32px 单帧 ICO，高 DPI 托盘模糊

`BatteryIconRenderer` 固定 `SideLength = 32`、单帧 PNG-ICO（`src/QcyH3Tray.App/BatteryIconRenderer.cs:22, 97-105, 109-128`）。P1 图标改造时一并产出多分辨率（16/20/24/32/48）与产品图标底。

#### D13 `_icon.Icon` 每次状态刷新都重新赋值

`TrayHost.UpdateVisual` 每次都 `_icon.Icon = icon`（`TrayHost.cs:153`），即使缓存命中也是一次属性写入（H.NotifyIcon 内部重建原生句柄）。低频事件下无害，图标改造时顺手加"变化才赋值"。H.NotifyIcon 已修的 `ForceCreate(enablesEfficiencyMode: false)`（`TrayHost.cs:48-67`）**确认无回归风险**：逻辑独立、有 `IsCreated` 双向日志。

#### D14 全局异常钩子一律吞掉 UI 异常

`App.xaml.cs:142-146` `args.Handled = true` 无差别吞掉 Dispatcher 异常（D6 的崩溃正是被它掩盖）。保留兜底但产品化阶段应区分"已知可恢复"与"未知"（未知异常弹一次性提示 + 写日志）。

#### D15 测试缺口（详见 §6）

AppState 命令健康逻辑 0 测试；H3Client 跨片重组边界无测试；蓝牙不可用路径、`Dispose` 竞态、EQ v2 回退、`DevicePickerWindow`、UI 三态 DPI 均无覆盖。

---

## 4. 分阶段执行计划

> 每项含：涉及文件 / 具体改动 / 验收标准。阶段顺序即执行顺序；P0 内部允许并行。

### P0-1 连接与状态修复（最高优先级，含全部用户实锤缺陷 1）

> **✅ 已完成（阶段 2，2026-10-31）**：8/8 项落地，`dotnet build` 0 警告 / `dotnet test` 222/222（原 123 全绿 + 新增 99 断言用例），App 冒烟通过。实际实现与计划的偏差记录见下方「P0-1 执行记录」。

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | ✅ 状态机扩为 §2.1 七相 | `Core/App/ConnectionManager.cs` | 实现 9 态（见执行记录）：`Disconnected/Reconnecting/Scanning/Unreachable/Connecting/Syncing/Connected/Degraded/BluetoothOff`；`Radio.StateChanged` 监听恢复（修 D4）；带转换合法性校验（非法转换经 `TransitionRejected` 上报，不崩溃） |
| 2 | ✅ 命令健康下沉 Core | `ConnectionManager.cs`、`AppState.cs` | Core 唯一持有 `_commandFailures`，达阈值 2 → `Degraded` 相（替代独立 `CommandHealthChanged` 事件）；`Connected` 转换时清零；删除 AppState 计数（修 D1/D10） |
| 3 | ✅ 未连接时写命令短路 | `AppState.cs`（`RunWriteCommandAsync`）+ Core 命令入口 | 非链路态直接短路返回 false（中文原因走状态行），不计命令失败、不触发横幅；Core `WithCommandGateAsync` 拿到信号量后复查会话（修 D1 触发面） |
| 4 | ✅ DeviceData 有效性 | 新文件 `Core/App/DeviceData.cs`、`AppState.cs` | `DataValidity`（Unknown/Loading/Available/Stale/Error/Unsupported）+ timestamp；断线全量转 Stale、值保留；快照不可变整体发布（修 D3/D5） |
| 5 | ✅ 写命令串行化 | `ConnectionManager.cs` | `SemaphoreSlim(1,1)` 包裹全部读/写命令（含回读两步整体持锁）（修 D8） |
| 6 | ✅ 初始化读容错 | `ConnectionManager.cs` | 逐项 try/catch 标 Error 并计健康；状态先 `Syncing`（状态事件先于数据事件）；完成时健康≥2 → `Degraded`（链路保持）（修 D9） |
| 7 | ✅ 面板复用防崩 | `TrayHost.cs` | `{ IsLoaded: true }` 检查 + `Closed` 通知 TrayHost 置空引用（修 D6） |
| 8 | ✅ 横幅显示条件 | `ControlPanelWindow.xaml(.cs)` | 横幅可见性 = 状态机 `Degraded` 相，文案「耳机无响应，正在重试」；连接失败文案走状态行（含 ToolTip 详细原因） |

**P0-1 执行记录（实际实现与计划的偏差）**：

1. **状态机 9 相而非 7 相**：§2.1 的七相全部保留（`BluetoothUnavailable` 更名 `BluetoothOff`）；新增两个环境/健康态——`Unreachable`（目标广播消失超过 `UnreachableAfter`（默认 30s，可配）后宣告、持续监听，广播出现即连）与 `Degraded`（命令健康降级为一等相，恢复条件=任一命令成功或重新完成全量同步）。§2.1 中「`DeviceUnreachable` 不单列状态」「`Connected` 期间命令失败不改变连接状态」两条按实现推翻并在此回填：链路级判断统一走 `ConnectionManager.IsLinkEstablished`（Syncing/Connected/Degraded），UI 不需要感知相细分。
2. **`CommandHealthChanged(bool)` 事件未单独实现**：Degraded 相本身承载该语义（进出相即 StateChanged），App 侧横幅由 `State == Degraded` 驱动，减少一条并行事件通道。
3. **AppState 写命令「失败自动重试一次」移除**：重试会让单次用户操作产生 2 次失败计数，与「连续 2 次 → Degraded」阈值冲突；现在一条命令一次记账（读命令同样计数——「单次读超时 ≠ 断线，进 Degraded」）。
4. **`DeviceData` 保留原字段**（`NoiseMode/EqPresetId/EqOffsets/EqV1/GameMode`）保证 CLI 与既有测试零改动，新增各字段 validity/`UpdatedUtc`，并把 `BatteryPercent/BatteryValidity` 并入快照（电量同样整体发布）；`BatteryChanged` 事件保留（CLI 兼容通道）。`ConnectionPhase.cs` 未需要——枚举留在 `ConnectionManager.cs`。
5. **新文件**：`Core/App/DeviceData.cs`（validity 枚举 + 快照 + `MarkAllStale`）；`Core/Ble/IBluetoothRadioMonitor.cs`（D4 抽象 + `WindowsRadioMonitor`：优先订阅 `Radio.StateChanged`，Radio API 不可用/无适配器时内部退化为 10s `EnsureAvailableAsync` 轮询）。
6. **D10 计数器**：`ConnectionManager._consecutiveFailures` → `_reconnectFailures`（连接退避）；命令健康 `_commandFailures` 唯一所有者在 Core（计划中提议的 `_connectionFailures` 命名改为语义更直白的 `_reconnectFailures`）。
7. **连接生命周期加固（计划未单列，随 D9 一并落地）**：`ConnectAsync` 幂等检查带代数（`_connectTaskGeneration == _generation`）——修复了「断开后旧在途连接任务占住幂等通道导致新连接永久挂起」的隐藏缺陷；过期代数的连接流程在 GATT 连接完成、握手完成、安装会话、同步完成四个关口校验代数，静默退出；蓝牙不可用异常从连接流程分流进 `BluetoothOff`（不再计入设备退避，恢复后立即重试）；transport 断开事件是唯一断线判据（单次读超时只影响命令健康）。
8. **EQ v2 回退**（D11 语义保持）：v1 失败 → v2 成功时 `EqValidity = Unsupported`（UI 消费通道不可用），`EqPresetId/EqOffsets` 照旧写入供 CLI；两者都失败 → `Error`。

**验收标准（真机，由主智能体执行）**：
1. 复现原缺陷场景：耳机关机启动 → 点 UI 控件 → 开机自动重连 → 横幅自动消失，状态依次经过 蓝牙不可达文案/重连中/同步中/已连接；
2. 关闭 Windows 蓝牙 → 托盘与面板显示"蓝牙不可用"→ 打开蓝牙 → 30s 内自动重连；
3. 断电耳机 → 面板字段全部转灰/Stale，不再显示旧值当作现值；重连后恢复；
4. Alt+F4 关闭面板后再点托盘 → 面板正常弹出（error.log 无新异常）；
5. `dotnet build` 0 警告、`dotnet test` 全绿（含新增用例）。

### P0-2 UI 缺陷修复（用户实锤缺陷 2，可与 P0-1 并行）

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | chip 模板度量修正 | `App/Resources/Styles.xaml:89-136, 141-185` | 移除 `Margin="0,-1,0,0"`；垂直 padding/MinHeight；显式 CJK 字体家族；`UseLayoutRounding` |
| 2 | 主面板窗口度量 | `App/ControlPanelWindow.xaml` | SizeToContent 或固定高度二选一，高 DPI 实测 |

**验收标准**：100%/125%/150% DPI 三档下，"默认/流行/重低音/摇滚/柔和/古典/自定义""低/中/高""通勤/室内/抗风"全部文字完整；截图留档。

### P1-1 UI 重设计（双形态 + Design Tokens）

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | Design Tokens 落地 | 新文件 `App/Resources/Tokens.xaml`，改造 `Styles.xaml` | 颜色（Accent/状态色/文本三级/危险）、字号阶梯（11/12/14/20）、间距（4/8/12/18）、圆角（chip 13 / 卡片 16）、阴影；全部现有硬编码色（`Styles.xaml:10-19` 及各 XAML 内 `#XXXXXX`）收敛为 Token 引用 |
| 2 | 托盘快捷面板重构 | `ControlPanelWindow.xaml(.cs)` | 保留"快捷"定位：状态头（统一状态机文案 + 同步中 spinner）→ 电量（带 validity）→ ANC 分段 → EQ chips → 游戏模式；每字段按 validity 渲染（Unknown=`--`、Loading=骨架/占位、Stale=置灰+时间、Error=重试按钮） |
| 3 | 控制中心窗口（完整形态） | 新文件 `ControlCenterWindow.xaml(.cs)` | 独立窗口（任务栏可见、可最小化）：设备信息卡（名称/地址/电量）、ANC 全 13 模式矩阵（现 UI 已有全量数据，`ControlPanelWindow.xaml.cs:180-219`）、EQ 详细（预设+编辑器入口）、游戏模式、设置中心入口、诊断（日志目录快捷方式） |
| 4 | 托盘菜单升级 | `TrayHost.cs:69-95` | 增：ANC 模式子菜单（直切）、打开控制中心、设备状态文案；保留立即重连/自启/退出 |
| 5 | 状态文案统一 | `AppState.cs:35-41`、`ConnectionManager.cs` 各 `SetState` 调用点 | 状态机每相一个标准文案，杜绝各处手拼 |

**验收标准**：双形态职责清晰（快捷面板 ≤300px 宽、失焦收起；控制中心独立窗口）；所有 UI 状态与 §2.1/§2.2 对齐；Tokens 之外无散落颜色值；123 测试全绿 + 新 UI 逻辑可测部分有测试。

### P1-2 功能补齐

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | EQ 编辑器升级 | `EqDetailWindow.xaml(.cs)`、Core `EqV1.cs` | ① 曲线实时预览（Polyline，7 点折线即可）；② 预设 A/B 对比参考线；③ 本地配置持久化（用户自定义曲线存 `%APPDATA%`，`AppConfig` 扩展字段）；④ 应用后回读校验不一致时提示（真机读写已实锤可回读）；⑤ 增益范围沿用 ±12.0 UI（协议钳 ±12.7，`EqV1.cs:49`） |
| 2 | 低电量通知 | 新文件 `App/Notifications/BatteryNotifier.cs` + `TrayHost.cs` | 阈值 20%/10% 边沿触发（跨会话去重，恢复充电/重连后复位）；用托盘气泡（H.NotifyIcon `ShowNotification`）起步，Toast 属 P2；通知点击打开面板 |
| 3 | 设置中心 | 新文件 `SettingsWindow.xaml(.cs)`、`Core/Config/AppConfig.cs` | 目标设备（重新扫描/切换设备，复用 `DevicePickerWindow`）、开机自启、重连开关、通知开关、自定义 EQ 管理、日志目录；`AppConfig` 现仅 3 字段（`AppConfig.cs:14-25`）需扩展并保持向后兼容（缺失字段回默认已支持） |
| 4 | 面板打开时 Loading 语义 | `ControlPanelWindow.xaml.cs:86-104` | `TriggerRefreshAsync` 期间字段标 Loading，防止旧值被当现值 |
| 5 | EQ v2 回退降级为诊断 | `ConnectionManager.cs:617-635` | v2 结果只进诊断日志与 `Unsupported` 标记，不再写 UI 数据（修 D11） |

**验收标准**：EQ 曲线保存/加载/应用回读一致；20% 以下电量收到一次且仅一次通知，恢复后可再次触发；设置中心可完成设备切换且重启后记忆；无真机不可验证项不进 UI。

### P1-3 托盘图标改造（产品图标 + 电量徽章）

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | 产品底图 + 徽章 | `BatteryIconRenderer.cs` | 底图=产品剪影（耳机图形，矢量绘制或嵌入资产），电量改为环形进度 + 百分比小字（或角标）；断连/未知=灰、≤20=红、≤50=橙、>50=绿（沿用 `BatteryIconRenderer.cs:28-34` 分级） |
| 2 | 多分辨率 | `BatteryIconRenderer.cs:109-128` | 单帧改 16/20/24/32/48 五帧 PNG-ICO |
| 3 | 缓存与赋值 | `BatteryIconRenderer.cs:36-49`、`TrayHost.cs:148-157` | 键扩展为 (样式, 环值, 色)；仅在渲染结果变化时赋值 `_icon.Icon`（修 D13） |

**验收标准**：100%/125%/150% 托盘缩放下清晰不糊；`GetIcon` 缓存命中（单元测试可测键与渲染参数）；GDI 句柄不泄漏（ClearCache 覆盖）。

### P2-1 产品化（P1 完成后）

| # | 改动 | 说明 | 前置条件 |
| --- | --- | --- | --- |
| 1 | 按键自定义 UI | GET_ADV_INFO type=02 按键表已可读（真机 153B 回包实锤），**写命令未在本机验证**（参考 Quicky `KeyFunction.go` 与 qcy-h3s `set_key_settings` 两种方案，见 feature-matrix §按键） | 先走 CLI 新探针（只读+单键写+回读+恢复）真机验证通过后才允许进 UI |
| 2 | 自动关机设置 | CUSTOM 0x14 读回 `14 04 FF FF 00 00` 与 Quicky 0x14 格式吻合（当前=65535min 即不自动关机）；写 `14 04 timeLo timeHi 00 00` 未验证 | 同上，探针验证后进设置中心 |
| 3 | Windows 音频设备跳转 | 非协议功能：`mmshotkey`/`AudioDeviceCmdlets` 式跳转到耳机渲染设备 | 与协议无关，可直接做，进控制中心 |
| 4 | Toast 通知替换气泡 | 低电量/断连/被手机抢占 | P1-2 完成后 |
| 5 | 打包与签名 | 单文件发布、版本号、代码签名、开机自启路径校验 | `publish/` 流程已有 |
| 6 | i18n 骨架 | 中文为先，文案集中资源字典 | Tokens 落地后 |

### P2-2 测试与验收收口

见 §6。

---

## 5. 明确禁止项（未经协议验证绝不许上 UI 按钮）

以下功能**禁止**在真机协议验证通过前出现在任何 UI 上（依据见 feature-matrix.md）：

1. **固件升级**——永久禁止（杰理私有升级通道，变砖风险）；
2. **按键映射写入**——读已有真机证据，写未验证（Quicky 与 h3s 两套参考方案不一致，本机走哪套未知）；
3. **查找耳机（0x4B）**——仅 h3s 参考，本机 CUSTOM 扫描 0x4B 读回空，写行为未知，且 over-ear 大音量有听力风险；
4. **提示音/ Tone Volume（0x1D）**——本机读回空载荷，判定当前固件不支持；
5. **LDAC（0x23）**——h3s 专属且"可能重启耳机"，H3 无此硬件能力；
6. **恢复出厂/清除配对（0x02/0x03）**——破坏性命令，无验收手段前禁止；
7. **任何 CUSTOM 全扫描中未解码的 ID（如 0x14 写路径）**——必须先经 `QcyH3Tray.Cli` 新探针（读→写→回读→恢复）真机验证。

---

## 6. 测试补充计划（随对应阶段落地）

| 缺口 | 对应阶段 | 新增测试 |
| --- | --- | --- |
| 命令健康（已下沉 Core） | ✅ P0-1 | `CommandHealthTests`：未连接写命令不计数、写失败×2 → Degraded、读失败×2 → Degraded、成功恢复清零、重连全量同步清零 |
| 状态机九相 | ✅ P0-1 | `ConnectionStateMachineTests`：全相转换合法性矩阵（81 组合）、蓝牙关闭→恢复自动重连（FakeRadioMonitor）、重连等待中 radio 消失、BluetoothOff 显式重连、Unreachable 超时与恢复、generation 竞争（旧任务迟到结果被丢弃）、初始化读失败 → Degraded 不掉线 / 单项失败 → Connected+Error |
| DeviceData validity | ✅ P0-1 | `DeviceDataValidityTests`：断线转 Stale 值保留、Unknown 保持 Unknown、读失败 Error 不落默认值、游戏模式未知值 → Unknown、快照整体发布、Syncing 中 Loading 可观察 |
| 写命令串行化 | ✅ P0-1 | `CommandHealthTests.ConcurrentSetCommands_SerializeWritesOnTransport`：并发 5 个 Set（传输层带延迟）传输层峰值并发=1、SET/GET 严格成对 |
| H3Client 跨片重组 | P0-1 或 P1 | ⬜ 未做（D7 不在本阶段范围）：`FE DC` 结尾分片 + `BA …` 续片能成帧（FakeBleLink 两段投递） |
| 面板复用 | P0-1 | ⬜ 修复已落地（`TrayHost.PresentPanel` IsLoaded 检查 + Closed 置空）；WPF 窗口生命周期测试需 STA 线程设施，留待 UI 阶段 |
| BatteryIconRenderer | P1-3 | 缓存键、分级颜色、ICO 帧数 |
| EQ 本地配置 | P1-2 | AppConfig 新字段往返与向后兼容（沿用 `AppConfigTests.cs` 模式） |
| 预设匹配推断 | 已有 | `EqTests`/`EqV1Tests` 已覆盖映射，补"匹配不到→自定义高亮"的 UI 逻辑抽出测试 |

---

## 7. 里程碑与依赖

```
P0-1 状态修复 ──┬──▶ P1-1 UI 重设计 ──▶ P2-1 产品化 ──▶ P2-2 收口
P0-2 chip 修复 ─┘        │
                         ├──▶ P1-2 功能补齐（依赖 P0-1 的 validity/loading 语义）
                         └──▶ P1-3 图标改造（独立，可并行）
```

- P0-1 是一切的前提：状态机与有效性语义不定，UI 重设计会返工。
- P1-3 与 P0 无耦合，可随时插入。
- P2-1 的按键/自动关机两项存在"真机探针验证"外部依赖，由主智能体执行验证后解锁。

---

## 8. 阶段 4b 执行记录（2026-10-31，真机已裁决功能的 UI 接线）

> 前置：阶段 4a 已在 Core 落地 `PowerManager.cs`/`KeyMap.cs` 编解码与 `H3Client` 读命令 + CLI 探针；
> 主智能体真机裁决结论——自动关机 0x14 读→写→回读全通（基线 0xFFFF）；按键 type=02 整表读写
> 回读逐字节一致（4 键×10 槽=40 条三元组，action≥0x81 为出厂槽）；提示音 `3D 01 <toneID>` /
> 查找耳机 `4B 01 01` ack 通道正常（R2 级外放，效果待首用验证）；双设备 0x24 读回空、无写出处（禁做开关）；
> **新发现**：断开链路后立即重连被 Windows 拒绝（AccessDenied），≥6-15 秒才稳定。

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | ✅ 自动关机命令 + 快照 | `Core/App/ConnectionManager.cs`、`Core/App/DeviceData.cs` | `GetPowerManagerAsync`/`SetPowerManagerAsync(分钟)`（写→回读确认，回读失败用写入值乐观快照，模式对齐 `SetEqV1Async`）；初始化同步与 `RefreshAllAsync` 增自动关机读；`DeviceData.AutoOffMinutes/AutoOffValidity/AutoOffUpdatedUtc`（validity 全沿用：断线 Stale、失败 Error 不落默认值） |
| 2 | ✅ 按键映射命令 + 快照 | 同上 | `GetKeyMappingsAsync`/`SetKeyMappingsAsync(整表)`（写→回读确认 + 乐观快照；**必须整表**）；`DeviceData.KeyMap/KeyMapValidity/KeyMapUpdatedUtc`；按键表不进初始化读（40 条整表由编辑器按需读，见偏差 3） |
| 3 | ✅ 提示音 / 查找耳机 | 新文件 `Core/Rcsp/ToneCommands.cs`、`H3Client.cs` | `PlayToneAsync(toneId)`/`FindEarbudsAsync()`：写 + ack(status=0) 判定，无回读；字节出处注释（Quicky TonePlay.go / qcy-h3s h3s_device.py）；成败计入命令健康，不更新快照 |
| 4 | ✅ 连接冷却 / AccessDenied 退避 | `Core/App/ConnectionManager.cs` | 新增 `PostDisconnectReconnectDelay`（默认 2s：计划内断开后首次重连最小延迟）与 `AccessDeniedReconnectDelay`（默认 5s：AccessDenied 类失败专用冷却，保持标记直至非 AccessDenied 失败或成功）；`IsAccessDenied`（UnauthorizedAccessException / denied / 拒绝访问变体，穿透内异常链）；Reconnecting 文案区分「连接断开 / 系统拒绝访问 / 连接失败」 |
| 5 | ✅ AppState 投影与通道 | `App/AppState.cs` | 快照投影 `AutoOffMinutes/AutoOffValidity/KeyMappings/KeyMapValidity`；`Get/SetPowerManagerAsync`、`Get/SetKeyMappingsAsync`、`PlayToneAsync`、`FindEarbudsAsync` 透传（写命令走既有 `RunWriteCommandAsync`：健康计数/乐观更新/失败回滚自动继承） |
| 6 | ✅ 功能页扩容 | `App/ControlCenterWindow.xaml(.cs)` | 自动关机（当前值 + 6 档 ComboBox 选择即写，validity≠Available 禁用 + 状态文案）；按键自定义（打开按键编辑器按钮）；查找耳机 / 播放提示音（按钮 + 确认弹窗，文案注明「首次使用将验证效果」）；双设备连接（仅说明文字）；连接页重连文案更新 |
| 7 | ✅ 按键编辑器 | 新文件 `App/KeyMapEditorSupport.cs`、`App/KeyMapEditorWindow.xaml(.cs)`、`App/WindowCoordinator.cs` | 720×600 普通窗口（`ShowKeyMapEditor` 单例复用）；按 keyNum 分组卡片（首现顺序）×5 行（单击..长按）ComboBox；出厂槽 action≥0x81 折叠区只读；整表写守卫 `StructurePreserved`（条数一致 + 出厂槽逐字节一致）；dirty 关窗三选；断连/读取失败 → 编辑区禁用 + 提示 |
| 8 | ✅ 测试 | 新文件 `tests/.../AutoOffKeyMapTests.cs`（15）、`ReconnectCooldownTests.cs`（7）、`KeyMapEditorSupportTests.cs`（9）；扩 `ScriptedH3Transport`（自动关机/按键表/提示音应答 + `ConnectException`） | 写→回读确认、乐观快照、validity 流转、ack 路径与命令健康、冷却退避（断开后立即重连失败→冷却后成功）、编辑表↔整表编码往返、出厂槽保护；既有 364 全绿 → **397 全绿** |

**验收**：`dotnet build` 0 警告；`dotnet test` 397/397（原 364 + 新增 33）；真机冒烟（只读核对功能页控件与按键编辑器 4 组×5 行 + 出厂槽只读，全程禁写）由主智能体执行。

**执行偏差（与派发说明的差异）**：

1. **AutoOff 进初始化同步、KeyMap 不进**：自动关机为单值读，随连接同步读一次（功能页立即可显示当前值）；按键表 40 条整表放初始化读会让每次连接多一轮整表读，改为编辑器打开时按需读（`GetKeyMappingsAsync` 同样更新快照与 validity）。
2. **自动关机「选择即写」而非「应用按钮」**：与游戏模式开关 / EQ chips 的既有交互模式一致（选择即写、失败回滚），避免双控件状态不一致；非档位值（读到 6 档之外的配置）追加「耳机当前值」选项原样显示，绝不静默改写。
3. **`InitReadFailures_EnterDegraded` 测试同步调整**：初始化读末尾新增自动关机读后，既有「连续失败」场景需一并静音该读（否则按既有语义「任一成功清零」会打断连续失败链）；断言 `CommandFailures == 阈值` 放宽为 `>= 阈值`（3 项全静音时为 3），语义不变。
4. **AccessDenied 检测用消息/异常类型启发式**：WinRT 拒绝访问的异常类型与消息随系统语言、API 路径变化，按 `UnauthorizedAccessException` + 消息含 denied/拒绝访问（穿透内异常链）判定；误判代价仅是多等 5 秒，方向安全。
5. **提示音/查找按钮用 `IsConnected` 门控**（写命令类，与游戏模式开关一致），无需先读任何快照值；结果以 ack 回填提示行。

---

## 9. 阶段 5 执行记录（2026-10-31，产品视觉与图标资源）

> 本环境无生成式图片工具，全部视觉资产为**代码矢量原创绘制**（无外部图片、无第三方库、不联网）。
> 渲染自检：临时工具（%TEMP%，不入仓库）把主视觉渲 512px、托盘图标渲 32px、ico 七帧导出 PNG，逐张目检并迭代 4 轮。

| # | 改动 | 涉及文件 | 要点 |
| --- | --- | --- | --- |
| 1 | ✅ 主视觉矢量图 | 新文件 `App/Resources/HeadphoneArt.xaml`、`App.xaml`（尾部合并） | `AppImage/HeadphoneHero`（浅底：深色机身 + 黑色柔和投影）/ `.Dark`（深底：石墨灰提亮 + 冷色弱光晕）两键均为 DrawingImage；512×512 设计坐标；共享几何 + 双版本画刷（双版本造型永一致）；3/4 视角（远侧耳罩略小略暗），头梁双弧夹层描边、耳垫月牙从内/底缘露出、外盖同心环 + 哑光镜面高光、右耳罩按键点缀 |
| 2 | ✅ 应用图标 | 新目录 `tools/IconGen`（独立 console，**不入 sln**——资产再生工具非产品组件）、新资产 `App/Assets/app.ico`、`QcyH3Tray.App.csproj`（`<ApplicationIcon>` + `<Resource>`） | 正面简化耳机剪影（Accent 蓝 #2563EB 渐变 + 细白外圈保深色任务栏可见）；256/128/64/48/32/24/16 七帧 PNG-ICO（16/24 用加粗简化几何）；ICO 容器打包逻辑与原 BatteryIconRenderer 同思路泛化为多帧 |
| 3 | ✅ 窗口图标接线 | 新文件 `App/AppIconSource.cs`；`ControlCenterWindow` / `EqEditorWindow` / `KeyMapEditorWindow` 构造器 | 统一从 pack URI 加载 app.ico 首帧（256px）；资源缺失只记日志返回 null 不阻断窗口创建 |
| 4 | ✅ 托盘图标改造（P1-3 落地） | 新文件 `App/TrayIconRenderer.cs`（删除 `BatteryIconRenderer.cs`）、`TrayHost.cs` | 迷你耳机剪影（正面轮廓，浅白填充 + 深灰描边适配浅色托盘）+ 右下角电量徽章胶囊（≈13×10 @32 设计坐标，分级色沿用：>50 绿/≤50 橙/≤20 红/未知灰）；断连/未知 = 灰剪影 + 无徽章（tooltip 承载完整状态）；缓存键 (文本, 徽章色, 剪影状态)；TrayHost 仅在渲染实例变化时赋值 `_icon.Icon`（修 D13）；公开签名改为 `GetIcon(int?)`，纯函数 `BadgeTextFor`/`ColorFor`/`SilhouetteFillFor` 可单测 |
| 5 | ✅ 测试 | 新文件 `HeadphoneArtResourceTests.cs`（4）、`TrayIconRendererTests.cs`（18） | 资源两键存在且为 ImageSource（XamlReader 按文件解析，无需 Application 宿主）、App.xaml 尾部合并断言；徽章文本/分级色/剪影状态纯函数。既有 HeadphoneHeroController 测试不动（资源键契约不变） |

**验收**：`dotnet build` 0 警告；`dotnet test` 419/419（原 397 + 新增 22）；渲染自检与真机冒烟（杀旧实例 → publish → 面板/控制中心截图 → 杀干净，全程无写操作）已执行。

**执行偏差（与派发说明的差异）**：

1. **托盘断连态 = 无数字徽章**（而非 "—" 或 "?"）：剪影转灰已表达断连，空徽章避免 32px 下微小字形发糊；tooltip 文案保持现状（状态全量可读）。
2. **托盘图标保持 32px 单帧物理渲染**（pixelsPerDip=1.0，与现状一致）：任务派发允许 16px 数字不可辨（tooltip 兜底）；多分辨率托盘帧（D12 完整收口）留待后续需要时按 `tools/IconGen` 帧表模式扩展。
3. **HeadphoneArt 两版共用同一批 Geometry 资源**：仅画刷/钢笔分版本，保证浅/深底版造型零漂移；文件内注释标明层次与渐变参数语义。
4. **HeadphoneArtResourceTests 按文件路径解析字典**（测试宿主无 Application 资源作用域，与运行时为同一份文件而非拷贝）；App.xaml 合并位置用文本序断言（Styles 之后 = 尾部）。


---

## 10. 阶段 7 执行记录（2026-10-31，收尾：测试缺口补齐 + 全窗口终验 + 交付文档）

> 前置：阶段 1-6 全部完成，453/453 测试绿。本阶段为收尾，不新增功能（终验发现的缺陷除外）。
> 终验设施：延续 UIA 脚本模式（本阶段脚本在 `%TEMP%\qcy_final\`，不入仓库）；截图与 dump 归档
> `docs/upgrade/verification/`（01-16 序号文件）。全程遵守只读铁律：未应用 EQ、未保存按键、
> 未点查找耳机/播放提示音、未写自动关机；BLE 重启间隔 ≥5s（实际 6s）。

### 10.1 测试缺口补齐（5 项新增，453 → 458）

| # | 测试 | 文件 | 要点 |
| --- | --- | --- | --- |
| 1 | `Dispose_DuringConnectingWithHungTransport_ReturnsPromptlyAndStaleAttemptExits` | `ResourceReleaseTests.cs`（新） | 连接进行中（fake 传输挂起在 ConnectAsync）Dispose：立即返回不挂死、状态发布 Disconnected"已释放。"、挂起连接完成后按过期代数静默退出（不改状态/不进重连）、Dispose 幂等 |
| 2 | `Dispose_WhileConnected_TearsSessionDown_RejectsRestart_KeepsTailSafe` | 同上 | 已连接（电量轮询活跃）时释放：数据转 Stale、状态 Disconnected、重启连接/断开抛 ObjectDisposedException（契约）、刷新与迟到广播为安全空操作 |
| 3 | `Dispose_DuringReconnectBackoff_StopsLoop_NoFurtherAttempts` | 同上 | 重连退避等待中释放：循环取消，等待窗口内零新连接尝试 |
| 4 | `TransportWriteException_Propagates_CountsFailure_MarksError` | `CommandHealthTests.cs` | 传输层 WriteAsync 抛异常（会话已被系统释放）≠ 静默超时：异常按契约上抛（AppState.RunWriteCommandAsync 捕获），照常计健康账 + 字段 Error；成功清零恢复。`ScriptedH3Transport` 增 `WriteException` 注入口 |
| 5 | `TransportWriteException_Twice_EntersDegraded_LinkKept_ThenRecovers` | 同上 | 连续两次写异常 → Degraded、链路不重建（真断线由 transport.Disconnected 判定）、异常消除后恢复 Connected |

**审查后认为不需要补的项及理由**：

1. **PanelPresentations**：九态 `ToneFor`、四种状态色调 → Token 画刷键、电量 validity 全分支
   （Available±值 / Loading / Stale / Unknown / Error / Unsupported）已有测试覆盖，无未覆盖分支。
2. **NoiseModeLogic**：全 8 家族 SegmentFor、未知家族 → -1、ModeBytes 黄金载荷 + 越界钳位、
   ModeName、ResolveLevelFamily、Describe 全家族 + 未知兜底均已覆盖。
3. **EqUiLogic**：MatchPreset（null / 段数不足 / 自定义 / 全部预设 + 默认平线）、BuildPresetBands
   （有/无当前值回退）、FormatMasterGain（正/负/零/未知）均已覆盖；`FormatDb` 为纯格式化且经
   FormatMasterGain 间接覆盖。
4. **App 退出链（App.OnExit / TrayHost.Dispose / AppState.Dispose）**：WPF 生命周期绑定
   （`Application.Current`、H.NotifyIcon 原生句柄、Dispatcher），单测需要 STA + Application.Run 宿主，
   plan §6 早已明确推迟该设施；其释放的核心资源 `ConnectionManager.Dispose` 已由新增 3 项直接覆盖，
   退出链真实执行由每次冒烟的杀进程/退出路径覆盖，AppState.Dispose 为三行无分支编排。
5. **D7 "FE DC 半个帧头"边界**：已有决断——C# 移植保持 Python 参考实现语义（缓冲无完整帧头 → 清空），
   `H3ClientTests.FragmentedNotifyStream_ReassemblesResponse` 注释明确记录；不按 D7 原建议改语义
   （逐字节对齐参考实现的既定原则优先于推测性加固）。
6. **BatteryIconRenderer**：已被 TrayIconRenderer 替代（阶段 5），18 项测试覆盖缓存键/分级色/徽章文本。

### 10.2 全窗口真机终验（UIA 走查，全部通过）

| # | 项 | 结果 | 归档 |
| --- | --- | --- | --- |
| 1 | 快捷面板：Hero / 电量（50% 现值）/ 四段+子行（降噪·高 = 耳机实际态）/ 7 chips（重低音选中）/ 游戏模式开关 / 页脚 | ✅ 50 控件，无文字裁切 | `01-quick-panel.*` |
| 2 | 控制中心六页：概览 / 降噪 / 声音 / 功能（自动关机当前值"不自动关机"、按键入口、查找/提示音按钮、双设备说明）/ 连接（地址、首连时间、切换设备、重连）/ 设置（通知开关+阈值、关于 1.0.0、诊断展开：状态/有效性/日志路径） | ✅ | `02`–`08` |
| 3 | EQ 编辑器：打开自动读耳机（曲线 +5/+4/+2/0/-1/0/+1、总增益 0.0 dB、Q 0.40–1.30 与真机一致）→ 本地配置 保存→加载→删除 往返（JSON 与耳机曲线逐字段一致；删除经确认弹窗）→ 关闭守卫（改总增益 2.0 → 关窗 → 三选 → 否=放弃） | ✅ 全程零耳机写 | `09`–`12` |
| 4 | 按键编辑器：4 组 × 5 行 + 出厂槽（20 条，action 0x81–0x85）只读展开，整表读取提示"40 条映射，其中出厂配置 20 条" | ✅ 未保存 | `13`–`14` |
| 5 | 托盘右键菜单：打开控制面板 / 打开控制中心 / 立即重连 / 开机自启 / ─ / 退出（五项齐全） | ✅ | `15-tray-menu.*` |
| 6 | 设置页通知开关 关→开（config.json False→True 恢复原值）；低电量通知：阈值临时 100 → 日志触发（17:07:55、17:11:08）+ 系统 PushNotification-Platform 事件日志"Toast delivered"（Id 3052/3153，AUMID=NotifyIconGeneratedAumid_*）→ 恢复 20 | ✅ 横幅屏上目视留人工（见 pending-verification ①） | `16-lowbattery-toast.png` |

### 10.3 终验发现的真实缺陷（均已修复 + 真机复验）

1. **最小化窗口唤起不还原**：控制中心被最小化后（本机多屏/焦点变化可触发），`Present()` 的
   `Show()+Activate()` 不会还原最小化窗口——托盘「打开控制中心」表现为"点了没反应"。
   修复：控制中心 `Present()`、快捷面板 `Present()`、`WindowCoordinator.ShowEqEditor/ShowKeyMapEditor`
   唤起前统一 `WindowState == Minimized → Normal`。
2. **EQ / 按键编辑器关窗守卫重入 Close 异常**：未保存修改选「否：放弃并关闭」时，`Close()` 在
   `OnClosing`（WM_CLOSE 处理栈）内同步重入 → `VerifyNotClosing` 抛 `InvalidOperationException`
   （error.log 实锤，`EqEditorWindow.ConfirmCloseAsync` line 644）。修复：两处改为
   `Dispatcher.BeginInvoke(() => Close())` 延迟关闭。复验：真机改总增益 → 关窗 → 否 →
   窗口正常关闭、error.log 零新增异常、耳机值未变（0.0 dB）。

### 10.4 验收（实际执行）

- `dotnet build`：0 警告 0 错误；`dotnet test`：**458/458**（原 453 + 新增 5）。
- `dotnet publish`（框架依赖 → publish/）+ `publish.ps1` 重跑：dist zip 更新到最终代码
  （`QcyH3Tray-1.0.0-portable.zip` 73.3 MB / 474 文件、`-setup.zip` 7.2 MB / 21 文件）。
- 未执行 git 提交（主智能体统一处理）；未真装 install.ps1。

### 10.5 交付文档

- `docs/upgrade/pending-verification.md`（新）：人工验收清单 10 项（通知横幅目视、查找/提示音首用效果、
  降噪听感、自动关机定时、DPI 100/150/200%、混合 DPI、蓝牙关→开、休眠唤醒、手机抢占回连、托盘菜单真人复核），
  每项含步骤与预期。
- `CHANGELOG.md`（新，仓库根）：1.0.0 版本块（新增/修复/改进/禁止项）。
- `README.md` 终稿：1.0.0 功能清单、双窗口说明、自动关机/按键使用说明、下载安装（便携/安装包 + install.ps1）、
  构建（含 IconGen/publish/smoke）、升级前后对比、CLI 全命令（含 probe-off/keys/tone/multi）。
- 本节（§10）。
