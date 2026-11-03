# 2026-11-02 Windows UI / 3D 耳机视觉改造

## 改造范围

- **统一选中态**：`PresetChip` 与 `PresetChipSmall` 删除原先内嵌的蓝色 `Overlay`，改为由最外层 `Border` 绘制完整背景；深蓝选中态使用白色文字，并明确 Disabled 的触发器优先级。修复「高 / 室内 / 摇滚」一类胶囊只在文字区域上色的问题。
- **快捷面板**：宽度从 300 DIP 增加到 380 DIP；增加设备副标题与耳机产品展示卡；降噪等级、降噪场景拆成两行，均衡器按钮放宽；引入垂直滚动并按可用工作区限制最大高度，避免 720p/高 DPI 屏幕内容溢出。
- **控制中心**：默认窗口放宽到 960×680（最小 830×600），左侧导航拓宽；概览换成双栏结构，一栏展示产品、一栏显示实时设备状态与操作。仍复用原有 AppState、设备快照、状态映射及命令事件。
- **3D 视觉**：在 `Assets/headphones_3d.png` 新增独立生成的透明头戴式耳机概念渲染图（120×160，透明调色板 PNG，5.4 KB），`App.xaml` 优先注册为 `AppImage/HeadphoneHero`；保留 `Resources/HeadphoneArt.xaml` 原始矢量资源与深色版本。图片不属于官方 QCY 产品摄影或商标素材，后续如需更高保真可替换同名资源。

## 不变项

未改动 BLE 扫描、RCSP 命令与连接状态机，也未修改设备数据写入路径。控件 `x:Name`、`Checked`/`Unchecked` 事件和命令入口保持不变。当前改造不增删耳机协议功能。

## 验收说明

已通过源码层面的 XAML 标签平衡与主要 `x:Name` 唯一性检查，已核对新增 PNG 在 Git 树中的 blob 与本地文件 SHA 一致。

**尚未完成 Windows 真机编译 / 运行 / DPI 截图 / 蓝牙联调**（当前工作环境不具备 Windows WPF Runtime 或设备）。因此这是一份供 Windows 侧验收的开发分支，暂不应直接合并主线。

### Windows 端手动核对

- `dotnet build` 和 `dotnet test` 全绿。
- 100% / 125% / 150% / 200% DPI：字形无截断、选中背景完整覆盖整个胶囊；「高」「室内」「摇滚」蓝底白字清晰，Hover、Checked、Disabled 不互相覆盖。
- 720p、1080p 屏幕：快捷面板靠右下定位正确，内容超高时可滚动到「设置」「控制中心」。
- 控制中心概览新双栏在最小宽度下没有溢出；图片透明且不包含概念图里的其他 UI。
- 开机未连接 → 打开耳机 → 重连成功 → 断连后重连：设备状态、禁用逻辑和故障提示不回归。
- ANC 档位、场景、EQ 预设及游戏模式切换回读正确。
- 实测图片在高 DPI 下的清晰度；当前随包素材优先满足快捷面板，小图放大可能不够清晰，可后续替换更高分辨率素材。

## 后续建议

Windows 验收确认后再考虑更新应用图标与正式发布。若 3D 概念图与耳机实物外形差异过大，优先使用授权的实机渲染作为替换，不依靠猜测修改硬件造型。

## 2026-11-02 追加：全应用悬浮玻璃（参考用户提供的悬浮导航截图）

用户期望的重点并非改主色，而是 **半透明、真实背景模糊、边缘高光、面板层次和触控般的抬升反馈**。本次延伸同一开发分支覆盖：

- **原生模糊**：`Services/BackdropService.cs` 对控制中心及弹出窗口均优先尝试 Windows Acrylic Accent Blur；失败回退到系统 Backdrop（主窗 Mica / 浮窗系统 Acrylic），再回退不透明浅灰。Windows 高对比度模式直接使用不透明兜底。保持 `QCYH3TRAY_BACKDROP=system|accent|opaque` 调试开关；由于 AccentBlur 为 Windows 未公开 API，各系统版本必须实测。
- **玻璃材质层级**：`Resources/Tokens.xaml` 新增 Shell/Sidebar/FloatingSurface/Control/NavSelected/Edge 渐变画刷及四级海拔阴影 Token，统一圆角和弱边缘反光。模糊归原生背景板负责，WPF 半透明渐变只承担材质深度，**不是把 WPF BlurEffect 加到文字上**。
- **托盘快捷面板**：宽 410 DIP，窗口外沿留 18 DIP 空间，独立阴影空层叠在磨砂主体之后，避免外壳阴影栅格化内容文字；电量、降噪、EQ、游戏模式、底部入口都各自有玻璃卡片。保留原代码的控件命名、控制回调、状态禁用和自动定位。
- **控制中心**：半透明底层，渐变侧栏和独立悬浮选中导航；信息卡片使用一致的高光和阴影层。
- **按钮体验**：`PresetChip` 正常态半透明、边缘发丝描边和轻阴影；悬停上移 2 DIP，按下回落；选中保持蓝底白字完整覆盖整颗胶囊；Shadow 为单独装饰视觉，文字不施加 BlurEffect。
- **其他窗口**：设备选择窗、EQ 编辑器、按键编辑器统一引入玻璃面、悬浮边界和主次操作按钮，不再遗留旧式原生按钮。
- **自动化**：新增 `.github/workflows/windows-ci.yml`，在 Windows 执行 restore/build/test，供 PR 自动检验。但这一自动化不能替代真实 Windows 视觉和耳机 BLE 验收。

### 静态检查与待验证边界

当前已对修改后 9 份 XAML 文件运行结构与资源引用静态检查：标签配对、StaticResource 命名及窗口控件唯一性未发现错误。但这**不等于 WPF 已编译或视觉已通过**，完整构建结果以 Windows CI 为准。

必须在 Windows 10/11 上确认：白色和深色桌面壁纸前的实际磨砂强度、透明圆角、悬浮阴影、按钮悬停动画、文本抗锯齿质量（尤其 125% 与 150% DPI）、低配设备渲染性能、720p 屏幕滚动，以及 Windows 高对比度或透明效果关闭时的退化表现。真机控制仍需确认 ANC、EQ、游戏模式和连接状态不回归。

若低端 Windows 图形驱动对未公开 Acrylic Accent Blur 出现卡顿，可在本机设置 `QCYH3TRAY_BACKDROP=system` 以强制改用系统背景板；如仍不适用，可用 `opaque` 启用不透明模式。

## 2026-11-03 视觉纠偏：单层纯白磨砂玻璃

根据真实 Windows 截图，上一版出现 **窗口矩形 Acrylic 底板露出 + 内层圆角卡片叠加 + 玻璃渐变色**。对应直接修复：

1. **原生窗口圆角**：新增 `Services/RoundedWindowRegion.cs`，使用 `CreateRoundRectRgn / SetWindowRgn` 为托盘弹窗的整个 HWND 应用圆角裁剪；在 SourceInitialized、SizeChanged 时按真实窗口像素和 DPI 重新应用，避免只给 WPF 子 Border 加圆角而原生模糊区域仍是矩形。原生裁剪是窗口级操作，可能影响 DWM 外部阴影，需真机验证系统合成效果。
2. **去除外框重影**：托盘快捷面板去掉 18 DIP 透明外框间距和背后的白色阴影 Border，仅保留 `RootCard` 一个全尺寸主外壳，不再出现内外双框。
3. **去掉内部叠放卡片**：电量、降噪、EQ、游戏模式、页脚均改为没有背景/阴影的平面内容区，以半透明细分隔线区分；耳机主视觉移除背景卡与阴影。保留 ANC 分段、EQ 可点击选项和游戏模式开关。
4. **纯白无渐变**：`Tokens.xaml` 中的 Glass 材质替换成 `SolidColorBrush` 半透明纯白（不再有 `LinearGradientBrush`），面板外壳 `#99FFFFFF`。实际的背景模糊来自 Windows Acrylic，而不是 WPF 控件本身模糊。
5. **原生 Acrylic tint**：`BackdropService` 改成低透明度纯白 tint `#38FFFFFF`，取消蓝灰色混色；同步更新 `BackdropServiceTests` 以验证新设计。仍保留 SystemBackdrop / Opaque 降级及高对比度安全模式。
6. **按钮降低浮雕感**：普通未选中 chip 不再常驻多层投影，只有选中/悬停时显示轻微升起反馈；选中按钮继续完整蓝底白字、未连接时正确禁用。
7. **修复之前 CI 的两项视觉契约失败**：此前 `GlassTokenTests` 期望 SolidColorBrush 和 `CornerRadius/Chip=8`，但上一版引入了 GradientBrush / 17。这次已恢复原契约，请以最新 Windows CI 结果为准。

**验收边界**：自动构建/单元测试不能证明 Windows Acrylic 实机呈现正确。务必在真实 Windows 上用明暗两种复杂桌面壁纸检查四角是否仍有方框、纯白玻璃后方是否真实模糊、是否存在重复边框或遮挡，并在不同 DPI 和断连/重连状态下截图。合格后才可合并 PR。
