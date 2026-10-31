# QCYH3-HeadphonesController 功能矩阵（阶段 1 代码审查产出）

> 逐行格式：功能 × {Core 协议支持 / 已实现 / 有缺陷 / UI 暴露 / 验证状态}。
> 结论基于实际代码（file:line）与真机验证记录（README「EQ v1 发现」、CLI eqprobe 系列说明、任务下发的真机事实清单）。
> 参考仓库对照：`qcy-h3-controller-zh`（Python，协议蓝本）、`Quicky`（Go，EQ v1/按键权威）、`qcy-h3s-windows`（H3S 耳塞，破坏性/扩展命令参考）、`qcy-menubar`（macOS 交互参考）。
> 配套文档：[plan.md](plan.md)（缺陷编号 D1-D15 的根因与修复计划）。

**图例**

- Core 协议支持：✅ 有封装 API ｜ ⚠️ 仅 CLI 探针通道/部分能力 ｜ ❌ 无
- 已实现 / UI 暴露：✅ ｜ ⚠️ 部分 ｜ ❌
- 验证状态：**真机已验证** ｜ **协议有据未验证**（参考实现有、本机未跑通写路径）｜ **仅官方App有**（参考仓库有线索，本机未证实）｜ **不可行**（本机固件明确回空/硬件不具备/被禁）

---

## 1. 连接与扫描

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| BLE 扫描（0x521C 厂商标记） | ✅ `BleScanner` | ✅ | — | ✅ 首启设备选择窗 | 真机已验证 | `BleScanner.cs:44-65`；QCY 置顶逻辑 `DevicePickerWindow.xaml.cs:118-120` |
| 目标设备选择与持久化 | ✅ | ✅ | — | ✅ | 真机已验证 | `AppConfig.cs:14-25`；仅首启可选，**无切换设备入口**（P1 设置中心补） |
| GATT 连接（A002 / 写 0001 / 通知 0002） | ✅ `BleTransport` | ✅ | — | — | 真机已验证 | `BleTransport.cs:42-48, 75-152` |
| 握手（opCode 0x0B，params 00 00） | ✅ | ✅ | — | — | 真机已验证 | `H3Client.cs:211-214`；占用时超时提示 `ConnectionManager.cs:443` |
| 自动重连（广播监听 + 冷却/退避） | ✅ | ✅ | — | ⚠️（仅"立即重连"按钮） | 真机已验证 | `ConnectionManager.cs` ReconnectLoopAsync；阶段 2 修复 D4：搜索超时转 `Unreachable` 持续监听，广播出现即连；阶段 4b：计划内断开后首次重连 ≥2s（`PostDisconnectReconnectDelay`），AccessDenied 类失败单独延迟 5s（`AccessDeniedReconnectDelay`，真机实测 Windows 对刚断开链路的冷却 ≥6-15s） |
| 地址漂移兜底（名称前缀匹配） | ✅ | ✅ | ⚠️ | — | 协议有据未验证 | `MatchesTargetLocked`，`ConnectionManager.cs:733-745`；兜底 "QCY H3" 前缀可能误配同型号其它设备，多机场景未实测 |
| 单控制连接限制 / 被手机 App 抢占 | ✅（超时→失败文案） | ⚠️ | — | ⚠️（仅状态文案） | 真机已验证 | 抢占表现为握手无响应；无"被抢占"专属状态，重连循环会持续尝试（P1 可加抢占提示） |
| 手动断开 | ✅ | ✅ | — | ❌（无 UI 入口） | 真机已验证 | `ConnectionManager.Disconnect`，`ConnectionManager.cs:326-340` |
| 蓝牙不可用状态与自动恢复 | ✅ | ✅ | — | ✅（状态文案 + 恢复提示） | 真机已验证（提示）/ 待真机验收（恢复） | 阶段 2：`BluetoothOff` 一等状态（D4 修复）；`IBluetoothRadioMonitor`/`WindowsRadioMonitor` 订阅 `Radio.StateChanged`，API 不可用退化 10s 轮询；恢复后自动回到重连流程 |
| 单实例 / 二次启动唤起 | — | ✅ | — | ✅ | 真机已验证 | `App.xaml.cs:25-58, 79-110` |
| 全局异常日志 | — | ✅ | D14 | ❌ | 真机已验证 | `ErrorLog.cs`；`App.xaml.cs:140-156` 无差别吞异常 |
| 应用图标（exe/窗口） | —（非协议） | ✅ | — | ✅（exe 图标 + 控制中心/EQ/按键编辑器窗口） | — | 阶段 5：`tools/IconGen` 矢量生成多分辨率 `Assets/app.ico`（16-256 七帧）；窗口经 `AppIconSource` 加载同一 ico |
| 产品主视觉（3D 耳机渲染图） | —（非协议） | ✅ | — | ✅（快捷面板 96 高 / 控制中心概览 128 高） | — | 阶段 5：`Resources/HeadphoneArt.xaml` 原创 3/4 视角矢量（512 设计坐标，双版本 `AppImage/HeadphoneHero` / `.Dark`），经 `HeadphoneHeroController` 契约接入（3a） |

## 2. 电量

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 电量读取（0xC1 掩码 0x01，TLV type=0） | ✅ | ✅ | — | ✅ | 真机已验证 | `H3Client.cs:469-484`；**单一电量**，无左右耳/充电盒语义 |
| 电量主动推送 | ✅ | ✅ | — | ✅ | 真机已验证 | `ConnectionManager.cs:665-677`（NotifyAdvInfo TLV） |
| 15s 电量轮询 | ✅ | ✅ | — | ✅ | 真机已验证 | `ConnectionManager.cs:548-574`；轮询失败静默（真断线由 transport 事件处理） |
| 托盘电量徽章图标 | — | ✅ | — | ✅ | 真机已验证 | 阶段 5 重构为 `TrayIconRenderer.cs`（替代 `BatteryIconRenderer`）：迷你耳机剪影 + 右下角电量徽章胶囊（分级色沿用），断连=灰剪影无徽章；(文本,徽章色,剪影状态) 缓存；TrayHost 仅在渲染实例变化时赋值（修 D13） |
| 电量有效性语义（Unknown/Stale） | ✅ `DataValidity` | ✅ | — | ✅（面板：Unknown/Error=`--`、Stale=置灰；托盘：非 Available=`?`） | — | 阶段 2：`DeviceData.BatteryValidity/UpdatedUtc`，断线全量转 Stale（`DeviceData.MarkAllStale`），读失败 Error 不落默认值 |
| 低电量提醒 | — | ❌ | — | ❌ | — | 计划 P1-2（阈值 20%/10% 边沿触发） |

## 3. 降噪（ANC，13 模式）

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 13 模式完整解码读（GET 0x17） | ✅ | ✅ | — | ✅ | 真机已验证 | `AncMode.Decode`，`AncMode.cs:61-87`；场景组 `17 03 01 03 02`（室内·高）抓包实锤 |
| 13 模式写入（CUSTOM `17 03 …`） | ✅ | ✅ | — | ✅ | 真机已验证 | 载荷表 `AncMode.cs:20-36`；CLI `setmode` + UI 分段控件 |
| 关闭 / 通透 / 降噪低中高 / 自适应 | ✅ | ✅ | — | ✅ 四段+档位 | 真机已验证 | `ControlPanelWindow.xaml.cs:259-296` |
| 场景族：通勤低中高 / 室内低中高 | ✅ | ✅ | — | ✅ 第二行场景+档位 | 真机已验证 | `ControlPanelWindow.xaml.cs:313-336` |
| 抗风噪 | ✅ | ✅ | — | ✅ 场景按钮 | 真机已验证 | 无档位语义，`AncMode.cs:35` |
| ANC 模式主动推送更新 | ✅ | ✅ | — | ✅ | 真机已验证 | `ConnectionManager.cs:680-691` |
| 写后回读确认 | ✅ | ✅ | — | — | 真机已验证 | `ConnectionManager.cs:230-247`（`SetNoiseModeAsync` 写→GET 0x17） |
| 全 13 模式一屏矩阵 | ✅（数据全有） | ❌ | — | ❌（现 UI 需两次交互） | — | 控制中心（P1-1）直接铺 13 模式 |

## 4. 均衡器（EQ）

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| EQ v1 读（FE 01 20，7 段+masterGain+eqType） | ✅ | ✅ | — | ✅ | 真机已验证 | `EqV1.ParseResponse`，`EqV1.cs:56-85`；**本机固件唯一可用 EQ 通道** |
| EQ v1 写（同 cmd 0x20） | ✅ | ✅ | — | ✅ | 真机已验证 | `EqV1.BuildWritePayload`，`EqV1.cs:93-134`（增益钳 ±1270）；eqprobe3 读写闭环实锤 |
| 各段 Q 读取/保持 | ✅ | ✅ | — | ⚠️（只读保持，不编辑） | 真机已验证 | Q=Q×100（Quicky 权威），UI 不暴露 Q 编辑 |
| masterGain 读取/写入 | ✅ | ✅ | — | ✅ 编辑器滑杆 | 真机已验证 | 真机基线 -5.5dB（`EqV1Info` 文档，`EqV1.cs:17`） |
| EQ v2 读回退（FE 01 22，10 段） | ✅ | ✅ | D11 | ❌ | 不可行（本机回空载荷） | `H3Client.cs:284-325`；保留作旧固件兼容，本机标 Unsupported |
| 7 官方预设（00-06）+ 10→7 段映射写入 | ✅ | ✅ | — | ✅ chips | 真机已验证 | `EqPresets.cs:16-46` + `EqV1BandMap.TenToSeven`（`EqV1.cs:141-178`） |
| 「当前预设」识别 | ❌（无协议读取通道） | ⚠️（曲线匹配推断） | ⚠️ | ✅ | 真机已验证（推断法局限实锤） | `ControlPanelWindow.MatchPreset`，`ControlPanelWindow.xaml.cs:393-408`；映射一致才高亮，否则"自定义"；耳机端直接切预设若映射一致可正确显示 |
| 自定义 EQ 编辑器 | ✅ | ✅ | ⚠️（无曲线预览/无本地保存/无回读校验） | ✅ | 真机已验证 | `EqDetailWindow.xaml.cs`；升级项见 plan P1-2 |
| 用户自定义曲线本地持久化 | — | ❌ | — | ❌ | — | 计划 P1-2（`AppConfig` 扩展） |
| EQ 编辑器窗口关闭后复用 | — | ✅ | — | ✅ | — | `_eqDetail is { IsLoaded: true }` 检查在位（`ControlPanelWindow.xaml.cs`）；主面板同款检查已补（阶段 2 修复 D6：`TrayHost.PresentPanel` 判 `{ IsLoaded: true }` + `Closed` 置空引用） |

## 5. 游戏模式（低延迟）

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 读（0xC1 掩码 0x20，TLV 值 02=开） | ✅ | ✅ | — | ✅ 开关 | 真机已验证 | `H3Client.GetWorkModeAsync`，`H3Client.cs:407-422`；阶段 2：协议未知值 → `GameModeValidity=Unknown`（绝不显示为关） |
| 写（0xC0 `02 05 02/01`）+ 回读 | ✅ | ✅ | — | ✅ | 真机已验证 | `H3Client.cs:425-429`；`ConnectionManager.cs:289-304` |
| 推送更新（WorkMode TLV） | ✅ | ✅ | — | ✅ | 协议有据未验证 | `ConnectionManager.cs:673-676`；单测覆盖（`ConnectionManagerTests.cs:234-243`），真机推送路径未单独验证 |

## 6. 设备信息与诊断

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 设备名读取（GET_ADV_INFO type=01） | ⚠️（仅全掩码回包含） | ❌ | — | ❌ | 真机已验证（回包含 "QCY H3"） | 153B TLV 实锤；未封装成 API/未进 UI（P1 设备信息卡可用） |
| **按键映射读**（GET_ADV_INFO type=02，本机 4 键×10 槽=40 条三元组） | ✅ `H3Client.GetKeyMappingsAsync` | ✅ | — | ✅ 按键编辑器 | 真机已验证 | 阶段 4a 探针验证（整表读，keyNum 0x07/0x03/0x05/0x09）；阶段 4b 接 UI（`KeyMapEditorWindow`：整表读→分组编辑，出厂槽 action≥0x81 只读） |
| **按键映射写**（SET_ADV_INFO 0xC0 LTV `[len=1+3N][0x02][三元组×N]`） | ✅ `H3Client.SetKeyMappingsAsync` | ✅ | — | ✅ 按键编辑器（整表写） | 真机已验证 | 阶段 4a 探针验证（整表写→回读逐字节一致）；**必须整表**（部分表会清掉其余按键，BOTOES.md 实测）；阶段 4b 接 UI：编辑差异→整表组装（`KeyMapEditorSupport.ApplyEdits`）→`StructurePreserved` 守卫→`SetKeyMappingsAsync` 写后回读确认 |
| 固件版本 | ⚠️ | ❌ | — | ❌ | 不可行 | GET_TARGET_INFO / GET_TARGET_FEATURE_MAP / GET_SYS_INFO 本机回空或仅回声（eqprobe2 实测） |
| 固件升级 | ❌ | ❌ | — | ❌ | **禁止** | 杰理私有升级通道，变砖风险，永不开放 |
| 自动关机（CUSTOM 0x14 读/写） | ✅ `H3Client.Get/SetPowerManagerAsync` | ✅ | — | ✅ 功能页档位选择 | 真机已验证 | 阶段 4a 探针验证（读→写→回读全通，基线 `14 04 FF FF 00 00`）；阶段 4b 接 UI：不自动关机(0xFFFF)/5/10/15/30/60 分钟档位，选择即写，Core 写后回读确认 + 乐观快照（`DeviceData.AutoOffMinutes` + validity） |
| 播放提示音（CUSTOM `3D 01 <toneID>`） | ✅ `H3Client.PlayToneAsync` | ✅ | — | ✅ 功能页按钮（确认弹窗） | 协议有据未验证（效果待首用验证） | 字节出处 Quicky `TonePlay.go`；R2 级外放音效无破坏性；真机 ack 通道正常但音效未触发验证——UI 弹窗注明「首次使用将验证效果」，ack(status=0) 判定成功 |
| 查找耳机（CUSTOM `4B 01 01`） | ✅ `H3Client.FindEarbudsAsync` | ✅ | — | ✅ 功能页按钮（确认弹窗） | 协议有据未验证（效果待首用验证） | 字节出处 qcy-h3s `find_earphones`（响铃定位，R2 级）；真机 ack 通道正常但效果未触发验证——UI 弹窗确认后触发，ack(status=0) 判定成功 |
| 提示音音量 / Tone Volume（0x1D 读） | ❌ | ❌ | — | ❌ | 不可行（本机读回空） | CUSTOM 全扫描 `FE 01 01..63` 除 0x14/0x17/0x20 外全部空载荷 |
| 双设备连接（0x24） | ❌ | ❌ | — | ❌（仅功能页说明文字） | 不可行 | 真机读回空、无写命令出处——功能页固定说明「由手机 QCY App 管理：当前固件不支持通过第三方工具读写该设置」，不做开关 |
| 佩戴检测（0x29/0x2C） | ❌ | ❌ | — | ❌ | 仅官方App有 | h3s 参考（`h3s_device.py:333-346, 401`）；本机扫描空 |
| 空间音频（0x2D）/ LDAC（0x23） | ❌ | ❌ | — | ❌ | 不可行 | h3s 专属；LDAC "可能重启耳机"（`h3s_device.py:451`），H3 无此能力，**禁止** |
| 音量（0x08）/ 声道平衡（0x16） | ❌ | ❌ | — | ❌ | 仅官方App有 | 本机 CUSTOM 读空；无证据前不实现 |
| GATT 特征枚举/直读（诊断） | ✅ | ✅（CLI 专用） | — | ❌ | 真机已验证 | `BleTransport.cs:191-278`，eqprobe2 使用 |
| CLI 诊断通道（scan/info/setmode/debug/eqprobe/2/3） | ✅ | ✅ | — | ❌（设计如此） | 真机已验证 | `QcyH3Tray.Cli/Program.cs`；**新功能探针必须走这里**（plan §5 禁止项） |

## 7. 多设备与系统联动

| 功能 | Core 协议支持 | 已实现 | 有缺陷 | UI 暴露 | 验证状态 | 备注 |
| --- | --- | --- | --- | --- | --- | --- |
| 多设备同时连接 | ❌ | ❌ | — | ❌ | 不可行 | 耳机单控制连接 + 手机抢占（真机实锤）；产品形态=单目标 + 快速切换设备（设置中心重扫） |
| 切换目标设备 | ⚠️ | ⚠️（仅删配置重装可达） | — | ❌ | — | `SetTarget` 存在（`AppState.cs:66-72`）但无 UI 入口 |
| Windows 音频设备跳转（ms-settings:sound / 渲染设备切换） | —（非 BLE 协议） | ❌ | — | ❌ | — | 纯 OS 能力，可直接做（P2 控制中心） |
| 开机自启 | —（非协议） | ✅ | — | ✅（托盘菜单+设置菜单双入口） | 真机已验证 | `AutostartManager.cs`（HKCU Run 键） |
| 低电量/断连/被抢占系统通知 | — | ❌ | — | ❌ | — | P1-2 起步（托盘气泡），P2 Toast |

---

## 8. 「绝不允许未经协议验证就上 UI 按钮」清单（硬性约束）

| 功能 | 状态 | 解锁条件 |
| --- | --- | --- |
| 固件升级 | **永久禁止** | 无 |
| 按键映射写 | ✅ 已解锁（阶段 4a 探针验证整表读写回读一致 → 阶段 4b 已接 UI，整表写守卫） | 已闭环 |
| 自动关机写（0x14） | ✅ 已解锁（阶段 4a 探针验证读→写→回读 → 阶段 4b 已接 UI） | 已闭环 |
| 查找耳机（0x4B） | ⚠️ 已接 UI（带确认弹窗，ack 通道正常）；音效效果待首用验证 | 首次使用观察响铃效果；无效则回撤按钮并回探针复核 |
| 播放提示音（0x3D） | ⚠️ 已接 UI（带确认弹窗，ack 通道正常）；音效效果待首用验证 | 同上（0x1D 音量读仍不可行） |
| 恢复出厂 / 清除配对（0x02/0x03） | 破坏性 | 无验收手段前禁止 |
| LDAC / 空间音频 / 佩戴检测 | h3s 专属 | H3 硬件不具备，不做 |
| 双设备连接（0x24） | 不可行（真机读回空、无写命令出处） | 功能页仅说明文字，由手机 QCY App 管理 |

---

## 9. 参考仓库对照速查

| 仓库 | 对本项目价值 | 关键文件 |
| --- | --- | --- |
| qcy-h3-controller-zh | RCSP 帧层/ANC/EQ v2 蓝本（逐字节对齐） | `rcsp.py`、`h3_device.py` |
| Quicky | **EQ v1 权威**（读/写/Q 语义）、按键映射方案 A、0x14 自动关机格式 | `internal/command/EQ.go`、`internal/response/response.go`、`internal/command/KeyFunction.go`、`docs/protocol.md` |
| qcy-h3s-windows | 按键映射方案 B（CUSTOM 0x2B）、0x4B/0x23/0x29/0x2C/0x2D 等扩展命令边界（本机多数不可行） | `h3s_device.py`、`docs/COMANDOS-DESTRUTIVOS.md` |
| qcy-menubar | 常驻菜单栏交互形态参考（双形态 UI 的 macOS 对照） | `Sources/QCYMenubar/` |
