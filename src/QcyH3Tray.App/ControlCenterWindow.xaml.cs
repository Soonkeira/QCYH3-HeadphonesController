using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Navigation;
using QcyH3Tray.App.Services;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Ble;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 控制中心（阶段 3b，plan.md P1-1 #3）：可调整大小的独立普通窗口，左侧导航 + 右侧六分区
/// （概览 / 降噪 / 声音 / 功能 / 连接 / 设置）。关窗 = Hide 复用不销毁（由
/// <see cref="WindowCoordinator"/> 单例管理）；尺寸与位置持久化到
/// <see cref="QcyH3Tray.Core.Config.AppConfig"/>（向后兼容默认值）。
/// <para>
/// 数据全部来自 <see cref="AppState"/> 快照订阅（九态 + validity 语义与快捷面板一致：
/// validity≠Available 禁用并降透明度、Unknown 不显示为关）；控制区写操作共用
/// <see cref="AppState.RunWriteCommandAsync"/> 通道（乐观显示、失败回滚到耳机实际状态）。
/// </para>
/// </summary>
public sealed partial class ControlCenterWindow : Window
{
    private readonly AppState _app;
    private readonly WindowCoordinator _windows;
    private readonly RadioButton[] _eqPresetChips;
    private readonly RadioButton[] _noiseSegments;

    private bool _syncingUi;
    private int _currentLevel = 1;             // 记住最近一次档位（默认中）
    private AncFamily? _currentFamily;         // 记住最近一次模式家族

    internal ControlCenterWindow(AppState app, WindowCoordinator windows)
    {
        InitializeComponent();
        Icon = AppIconSource.TryGetSource();   // 任务栏/标题栏应用图标（阶段 5）
        _app = app;
        _windows = windows;

        _noiseSegments = [SegOff, SegTransparency, SegNoiseCancelling, SegAdaptive];
        _eqPresetChips =
        [
            EqPresetDefault, EqPresetPop, EqPresetHeavyBass,
            EqPresetRock, EqPresetSoft, EqPresetClassic,
        ];

        NoiseSegmentGrid.SizeChanged += (_, _) =>
            LayoutThumb(NoiseModeLogic.SegmentFor(_currentFamily) is int index and >= 0 ? index : 2, animate: false);

        // 3D 耳机展示位：资源存在则显示（高度 128），不存在整块折叠（同面板，不放占位位图）
        HeadphoneHeroController.Apply(
            HeadphoneHero,
            HeadphoneHeroImage,
            TryFindResource(HeadphoneHeroController.ResourceKey),
            (double)FindResource("Size/Hero.Height.ControlCenter"));

        AboutVersionText.Text = typeof(ControlCenterWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        DiagLogPathText.Text = ErrorLog.LogPath;

        ApplySavedBounds();

        _app.StateUpdated += OnAppStateChanged;
        _app.AutostartChanged += OnAppStateChanged;
        Closed += (_, _) =>
        {
            _app.StateUpdated -= OnAppStateChanged;
            _app.AutostartChanged -= OnAppStateChanged;
        };

        NavOverview.IsChecked = true;   // 默认分区（此时全部元素已创建，Checked 事件安全）
        SyncFromState();
    }

    /// <summary>唤起：还原最小化 → 同步状态 → 显示 → 激活 → 全量刷新/重连（与面板 Present 同语义）。</summary>
    public void Present()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;   // 最小化时 Show/Activate 不会还原（终验实测发现）
        }
        SyncFromState();
        Show();
        Activate();
        _ = TriggerRefreshAsync();
    }

    private async Task TriggerRefreshAsync()
    {
        if (_app.IsConnected)
        {
            try
            {
                await _app.Manager.RefreshAllAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("控制中心全量刷新失败", exception);
            }
        }
        else
        {
            await _app.ConnectAsync().ConfigureAwait(true);
        }
    }

    // ───────────────────────── 导航 ─────────────────────────

    private void OnNavChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (sender == NavOverview)
        {
            ShowSection(OverviewSection);
        }
        else if (sender == NavNoise)
        {
            ShowSection(NoiseSection);
        }
        else if (sender == NavSound)
        {
            ShowSection(SoundSection);
        }
        else if (sender == NavFeature)
        {
            ShowSection(FeatureSection);
        }
        else if (sender == NavConnection)
        {
            ShowSection(ConnectionSection);
        }
        else if (sender == NavSettings)
        {
            ShowSection(SettingsSection);
        }
    }

    private void ShowSection(UIElement section)
    {
        OverviewSection.Visibility = section == OverviewSection ? Visibility.Visible : Visibility.Collapsed;
        NoiseSection.Visibility = section == NoiseSection ? Visibility.Visible : Visibility.Collapsed;
        SoundSection.Visibility = section == SoundSection ? Visibility.Visible : Visibility.Collapsed;
        FeatureSection.Visibility = section == FeatureSection ? Visibility.Visible : Visibility.Collapsed;
        ConnectionSection.Visibility = section == ConnectionSection ? Visibility.Visible : Visibility.Collapsed;
        SettingsSection.Visibility = section == SettingsSection ? Visibility.Visible : Visibility.Collapsed;
    }

    // ───────────────────────── 状态同步 ─────────────────────────

    private void OnAppStateChanged() => SyncFromState();

    private void SyncFromState()
    {
        _syncingUi = true;
        try
        {
            ApplyOverview();
            ApplyNoiseState(_app.Data);
            ApplySoundState(_app.Data);
            ApplyFeatureState(_app.Data);
            ApplyConnectionState();
            ApplySettingsState();
        }
        finally
        {
            _syncingUi = false;
        }
    }

    // ───────────────────────── 1. 概览 ─────────────────────────

    private void ApplyOverview()
    {
        bool connected = _app.IsConnected;

        OverviewDeviceName.Text = string.IsNullOrWhiteSpace(_app.Config.TargetName)
            ? "QCY H3"
            : _app.Config.TargetName;

        // 九态状态文字 + 语义色调（与面板同一映射：PanelPresentations 纯函数）
        OverviewStatusText.Text = _app.StateLabel;
        OverviewStatusText.ToolTip = _app.StatusText;
        OverviewStatusText.SetResourceReference(
            TextBlock.ForegroundProperty, PanelPresentations.BrushKeyFor(PanelPresentations.ToneFor(_app.State)));

        ApplyBatteryText();
        ApplyNoiseModeHeadline();

        // 游戏模式：两处开关（概览/功能）同源同步；Unknown/Loading/Error 不亮（绝不显示为关）
        bool gameModeKnown = _app.Data.GameModeValidity is DataValidity.Available or DataValidity.Stale;
        bool gameModeOn = gameModeKnown && _app.Data.GameMode == true;
        if (OverviewGameModeSwitch.IsChecked != gameModeOn)
        {
            OverviewGameModeSwitch.IsChecked = gameModeOn;
        }
        if (FeatureGameModeSwitch.IsChecked != gameModeOn)
        {
            FeatureGameModeSwitch.IsChecked = gameModeOn;
        }
        bool gameModeInteractive = connected && _app.Data.GameModeValidity == DataValidity.Available;
        OverviewGameModeRow.IsEnabled = gameModeInteractive;
        FeatureGameModeRow.IsEnabled = gameModeInteractive;

        OverviewReconnectButton.IsEnabled = true;   // ConnectAsync 幂等，任意状态可点
        OverviewDisconnectButton.IsEnabled = _app.State != ConnectionState.Disconnected;
    }

    private void ApplyBatteryText()
    {
        (string text, BatteryTone tone) = PanelPresentations.FormatBattery(_app.BatteryValidity, _app.BatteryPercent);
        OverviewBatteryText.Text = text;
        OverviewBatteryText.SetResourceReference(TextBlock.ForegroundProperty, PanelPresentations.BrushKeyFor(tone));
    }

    /// <summary>降噪当前模式大字：Available=现值；Stale=旧值置灰；Loading="…"；其余 "--"。</summary>
    private void ApplyNoiseModeHeadline()
    {
        string text;
        BatteryTone tone;
        switch (_app.Data.NoiseModeValidity)
        {
            case DataValidity.Available when _app.Data.NoiseModeInfo is not null:
                text = _app.Data.NoiseModeInfo.DisplayName;
                tone = BatteryTone.Normal;
                break;
            case DataValidity.Stale when _app.Data.NoiseModeInfo is not null:
                text = _app.Data.NoiseModeInfo.DisplayName;
                tone = BatteryTone.Muted;
                break;
            case DataValidity.Loading:
                text = "…";
                tone = BatteryTone.Muted;
                break;
            default:
                text = "--";
                tone = BatteryTone.Muted;
                break;
        }
        OverviewNoiseModeText.Text = text;
        OverviewNoiseModeText.SetResourceReference(TextBlock.ForegroundProperty, PanelPresentations.BrushKeyFor(tone));
    }

    // ───────────────────────── 2. 降噪（与快捷面板同逻辑，尺寸放宽） ─────────────────────────

    private void ApplyNoiseState(DeviceData data)
    {
        bool interactive = _app.IsConnected && data.NoiseModeValidity == DataValidity.Available;
        NoiseSectionRoot.IsEnabled = interactive;

        if (data.NoiseModeValidity == DataValidity.Error)
        {
            // 读取/写入失败：不高亮任何模式（不拿旧值或默认值当现值）
            SetChecked(
                (false, SegOff),
                (false, SegTransparency),
                (false, SegNoiseCancelling),
                (false, SegAdaptive));
            _currentFamily = null;
            NoiseSubRow.Visibility = Visibility.Collapsed;
            NoiseDescribeText.Text = NoiseModeLogic.Describe(null);
            return;
        }

        var info = data.NoiseModeInfo;
        int segment = NoiseModeLogic.SegmentFor(info?.Family);
        if (segment >= 0)
        {
            SetChecked(_noiseSegments[segment]);
            LayoutThumb(segment, animate: false);
        }
        _currentFamily = info?.Family;

        bool showSubRow = segment == 2;
        NoiseSubRow.Visibility = showSubRow ? Visibility.Visible : Visibility.Collapsed;
        if (showSubRow && info is not null)
        {
            if (info.Level is int level and >= 0 and <= 2)
            {
                _currentLevel = level;
            }
            SetChecked(
                (info.Level == 0, LevelLow),
                (info.Level == 1, LevelMedium),
                (info.Level == 2, LevelHigh));
            SetChecked(
                (info.Family == AncFamily.Commuting, SceneCommuting),
                (info.Family == AncFamily.Indoor, SceneIndoor),
                (info.Family == AncFamily.AntiWind, SceneAntiWind));
        }

        NoiseDescribeText.Text = NoiseModeLogic.Describe(info?.Family);
    }

    private void LayoutThumb(int index, bool animate)
    {
        double column = NoiseSegmentGrid.ActualWidth / _noiseSegments.Length;
        double inset = NoiseThumb.Margin.Left;
        if (column <= 0 || index < 0)
        {
            return;
        }
        NoiseThumb.Width = column - inset * 2;
        double x = index * column;
        if (animate)
        {
            NoiseThumbShift.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            NoiseThumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            NoiseThumbShift.X = x;
        }
    }

    private void OnSegmentChecked(object sender, RoutedEventArgs eventArgs)
    {
        var radio = (RadioButton)sender;
        int index = Array.IndexOf(_noiseSegments, radio);
        LayoutThumb(index, animate: true);
        if (_syncingUi)
        {
            return;
        }

        switch (index)
        {
            case 0:
                _ = WriteNoiseModeAsync("降噪·关闭", AncMode.Off);
                break;
            case 1:
                _ = WriteNoiseModeAsync("降噪·通透", AncMode.Transparency);
                break;
            case 2:
                // 选中"降噪"：展开第二行，按记住的档位写入
                NoiseSubRow.Visibility = Visibility.Visible;
                _currentFamily = AncFamily.NoiseCancelling;
                SetChecked(
                    (_currentLevel == 0, LevelLow),
                    (_currentLevel == 1, LevelMedium),
                    (_currentLevel == 2, LevelHigh));
                SetChecked(
                    (false, SceneCommuting),
                    (false, SceneIndoor),
                    (false, SceneAntiWind));
                NoiseDescribeText.Text = NoiseModeLogic.Describe(AncFamily.NoiseCancelling);
                _ = WriteNoiseModeAsync(
                    NoiseModeLogic.ModeName(AncFamily.NoiseCancelling, _currentLevel),
                    NoiseModeLogic.ModeBytes(AncFamily.NoiseCancelling, _currentLevel));
                break;
            case 3:
                NoiseDescribeText.Text = NoiseModeLogic.Describe(AncFamily.Adaptive);
                _ = WriteNoiseModeAsync("降噪·自适应", AncMode.Adaptive);
                break;
        }
    }

    private void OnLevelChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        int level = sender == LevelLow ? 0 : sender == LevelMedium ? 1 : 2;
        _currentLevel = level;
        AncFamily family = NoiseModeLogic.ResolveLevelFamily(_currentFamily);
        _ = WriteNoiseModeAsync(NoiseModeLogic.ModeName(family, level), NoiseModeLogic.ModeBytes(family, level));
    }

    private void OnSceneChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        AncFamily family = sender == SceneCommuting ? AncFamily.Commuting
            : sender == SceneIndoor ? AncFamily.Indoor
            : AncFamily.AntiWind;
        _currentFamily = family;
        NoiseDescribeText.Text = NoiseModeLogic.Describe(family);
        byte[] bytes = family == AncFamily.AntiWind
            ? AncMode.AntiWind
            : NoiseModeLogic.ModeBytes(family, _currentLevel);
        _ = WriteNoiseModeAsync(
            family == AncFamily.AntiWind ? "抗风噪" : NoiseModeLogic.ModeName(family, _currentLevel),
            bytes);
    }

    private async Task WriteNoiseModeAsync(string name, byte[] modeBytes)
    {
        bool ok = await _app.RunWriteCommandAsync(name, async () =>
            await _app.Manager.SetNoiseModeAsync(modeBytes).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            SyncFromState();   // 写入失败回滚到耳机实际状态
        }
    }

    // ───────────────────────── 3. 声音 ─────────────────────────

    private void ApplySoundState(DeviceData data)
    {
        bool interactive = _app.IsConnected && data.EqValidity == DataValidity.Available;
        EqChipsPanel.IsEnabled = interactive;
        OpenEqEditorButton.IsEnabled = true;   // 编辑器自带断连/读取失败处理，入口保持可用

        if (data.EqValidity == DataValidity.Error)
        {
            var all = new RadioButton[_eqPresetChips.Length + 1];
            _eqPresetChips.CopyTo(all, 0);
            all[^1] = EqCustomChip;
            SetChecked(all.Select(chip => (Selected: false, Radio: chip)).ToArray());
            EqMasterGainText.Text = "--";
            return;
        }

        EqV1Info? info = data.EqV1;
        EqMasterGainText.Text = EqUiLogic.FormatMasterGain(info?.MasterGain);
        if (info is null)
        {
            return;   // 未知曲线：保持现状，不乱高亮
        }
        if (EqUiLogic.MatchPreset(info) is byte presetId)
        {
            SetChecked(_eqPresetChips[presetId - EqUiLogic.PresetChipMin]);
        }
        else
        {
            SetChecked(EqCustomChip);
        }
    }

    private void OnEqPresetChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        var chip = (RadioButton)sender;
        int index = Array.IndexOf(_eqPresetChips, chip);
        if (index < 0)
        {
            return;
        }
        _ = WriteEqPresetAsync((byte)(index + EqUiLogic.PresetChipMin));
    }

    /// <summary>写预设 = 10 段偏移映射成 7 段曲线写入（eqType/masterGain/Q 保持读到的值）。</summary>
    private async Task WriteEqPresetAsync(byte presetId)
    {
        EqV1Info? current = await _app.GetEqV1Async().ConfigureAwait(true);
        (byte eqType, int masterGain, List<EqV1Band> bands) = EqUiLogic.BuildPresetBands(presetId, current);

        bool ok = await _app.RunWriteCommandAsync($"EQ 预设·{EqPresets.Names[presetId]}", async () =>
            await _app.Manager.SetEqV1Async(eqType, masterGain, bands).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            SyncFromState();
        }
    }

    private void OnEqCustomChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        _windows.ShowEqEditor();
    }

    private void OnOpenEqEditorClick(object sender, RoutedEventArgs eventArgs)
        => _windows.ShowEqEditor();

    // ───────────────────────── 4. 功能（阶段 4b：自动关机 / 按键编辑器 / 查找耳机 / 提示音） ─────────────────────────

    /// <summary>自动关机 ComboBox 选项（值 = 分钟，0xFFFF = 不自动关机）。</summary>
    private sealed record AutoOffOption(int Minutes, string Label);

    /// <summary>自动关机 UI 档位（真机裁决：不自动关机 0xFFFF / 5 / 10 / 15 / 30 / 60 分钟）。</summary>
    private static readonly int[] AutoOffPresets =
    [
        PowerManagerInfo.NeverOffMinutes, 5, 10, 15, 30, 60,
    ];

    private static string AutoOffLabel(int minutes)
        => minutes == PowerManagerInfo.NeverOffMinutes ? "不自动关机" : $"{minutes} 分钟";

    private void ApplyFeatureState(DeviceData data)
    {
        bool connected = _app.IsConnected;

        // 自动关机：Available=现值可选；Stale=旧值置灰（整行禁用）；Loading/Error/Unknown=空选 + 状态文案。
        // 非档位值（耳机读到 5/10/…/60/0xFFFF 之外的配置）追加为「耳机当前值」选项，绝不静默改写。
        var options = AutoOffPresets.Select(minutes => new AutoOffOption(minutes, AutoOffLabel(minutes))).ToList();
        int? current = data.AutoOffMinutes;
        if (current is int value && options.All(option => option.Minutes != value))
        {
            options.Add(new AutoOffOption(value, $"{AutoOffLabel(value)}（耳机当前值）"));
        }
        AutoOffCombo.ItemsSource = options;
        if (!Equals(AutoOffCombo.SelectedValue, current))
        {
            AutoOffCombo.SelectedValue = current;
        }
        AutoOffRow.IsEnabled = connected && data.AutoOffValidity == DataValidity.Available;
        string? state = data.AutoOffValidity switch
        {
            DataValidity.Available => null,
            DataValidity.Stale => current is int staleMinutes
                ? $"耳机未连接：显示的「{AutoOffLabel(staleMinutes)}」为断开前的旧值。"
                : "耳机未连接：尚未读取到当前设置。",
            DataValidity.Loading => "正在读取耳机当前设置…",
            DataValidity.Error => "读取/写入失败：当前值未知，可点其它页触发重读或重连后重试。",
            _ => "尚未读取到耳机当前设置。",
        };
        AutoOffStateText.Text = state ?? "";
        AutoOffStateText.Visibility = state is null ? Visibility.Collapsed : Visibility.Visible;

        // 按键编辑器自带断连/读取失败处理，入口保持可用；查找/提示音为写命令，链路建立才可点
        OpenKeyMapEditorButton.IsEnabled = true;
        FindEarbudsButton.IsEnabled = connected;
        PlayToneButton.IsEnabled = connected;
    }

    /// <summary>自动关机「选择即写」（与游戏模式开关 / EQ chips 同交互模式），失败回滚到耳机实际状态。</summary>
    private void OnAutoOffSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        if (AutoOffCombo.SelectedValue is not int minutes || minutes == _app.Data.AutoOffMinutes)
        {
            return;
        }
        _ = WriteAutoOffAsync(minutes);
    }

    private async Task WriteAutoOffAsync(int minutes)
    {
        bool ok = await _app.RunWriteCommandAsync(
            minutes == PowerManagerInfo.NeverOffMinutes ? "自动关机·不自动关机" : $"自动关机·{minutes} 分钟",
            async () => await _app.SetPowerManagerAsync(minutes).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            SyncFromState();   // 写入失败回滚到耳机实际状态
        }
    }

    private void OnOpenKeyMapEditorClick(object sender, RoutedEventArgs eventArgs)
        => _windows.ShowKeyMapEditor();

    /// <summary>查找耳机（0x4B，R2 级外放音效）：确认弹窗 → 写命令，ack（status=0）即成功。</summary>
    private async void OnFindEarbudsClick(object sender, RoutedEventArgs eventArgs)
    {
        if (MessageBox.Show(
                this,
                "耳机将响铃定位（以当前音量外放）。\n\n确认执行？（首次使用将验证效果）",
                "查找耳机",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        bool ok = await _app.RunWriteCommandAsync(
            "查找耳机", async () => await _app.FindEarbudsAsync().ConfigureAwait(false))
            .ConfigureAwait(true);
        FeatureHintText.Text = ok
            ? "已触发查找耳机：请循声定位。"
            : "耳机无响应，查找耳机失败（可重试）。";
    }

    /// <summary>播放提示音（0x3D，toneID=1，R2 级外放音效）：确认弹窗 → 写命令，ack（status=0）即成功。</summary>
    private async void OnPlayToneClick(object sender, RoutedEventArgs eventArgs)
    {
        if (MessageBox.Show(
                this,
                "耳机将外放提示音（以当前音量播放）。\n\n确认播放？（首次使用将验证效果）",
                "播放提示音",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        bool ok = await _app.RunWriteCommandAsync(
            "播放提示音", async () => await _app.PlayToneAsync(toneId: 1).ConfigureAwait(false))
            .ConfigureAwait(true);
        FeatureHintText.Text = ok
            ? "已触发播放提示音。"
            : "耳机无响应，播放提示音失败（可重试）。";
    }

    // ───────────────────────── 游戏模式（概览/功能双向同步） ─────────────────────────

    private void OnGameModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        bool enabled = sender == OverviewGameModeSwitch
            ? OverviewGameModeSwitch.IsChecked == true
            : FeatureGameModeSwitch.IsChecked == true;
        _ = WriteGameModeAsync(enabled);
    }

    private async Task WriteGameModeAsync(bool enabled)
    {
        bool ok = await _app.RunWriteCommandAsync(enabled ? "游戏模式·开" : "游戏模式·关", async () =>
            await _app.Manager.SetGameModeAsync(enabled).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            SyncFromState();
        }
    }

    // ───────────────────────── 5. 连接 ─────────────────────────

    private void ApplyConnectionState()
    {
        ConnectionDeviceName.Text = _app.Config.TargetName.Length > 0 ? _app.Config.TargetName : "--";
        ConnectionDeviceAddress.Text = _app.Config.TargetAddress is ulong address
            ? BleScanResult.FormatAddress(address)
            : "--";
        ConnectionFirstConnected.Text = _app.Config.FirstConnectedAt is DateTimeOffset first
            ? first.ToString("yyyy-MM-dd HH:mm")
            : "尚未连接过";

        SwitchDeviceButton.IsEnabled = true;
        ConnectionReconnectButton.IsEnabled = true;

        // 诊断折叠区（随状态实时刷新，展开时可见）
        DiagStateText.Text = $"{_app.StateLabel}（{_app.State}）";
        DiagStatusText.Text = _app.StatusText;
        DeviceData data = _app.Data;
        DiagValidityText.Text = $"数据有效性：电量 {data.BatteryValidity} · 降噪 {data.NoiseModeValidity}"
            + $" · EQ {data.EqValidity} · 游戏模式 {data.GameModeValidity}";
    }

    private void OnReconnectClick(object sender, RoutedEventArgs eventArgs)
        => _ = _app.ConnectAsync();

    private void OnDisconnectClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            _app.Manager.Disconnect();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("手动断开失败", exception);
        }
    }

    private void OnSwitchDeviceClick(object sender, RoutedEventArgs eventArgs)
    {
        var picker = new DevicePickerWindow(_app) { Owner = this };
        if (picker.ShowDialog() == true)
        {
            _ = _app.ConnectAsync();
        }
    }

    // ───────────────────────── 6. 设置 ─────────────────────────

    private void ApplySettingsState()
    {
        bool autostart = _app.IsAutostartEnabled;
        if (AutostartSwitch.IsChecked != autostart)
        {
            AutostartSwitch.IsChecked = autostart;
        }

        // 阶段 6：通知开关 + 低电量阈值档位（阈值非法值经 ClampThreshold 回退默认）
        bool notifications = _app.Config.NotificationsEnabled;
        if (NotificationsSwitch.IsChecked != notifications)
        {
            NotificationsSwitch.IsChecked = notifications;
        }
        var thresholds = LowBatteryAdvisor.ConfigurableThresholds
            .Select(percent => new ThresholdOption(percent, $"{percent}%"))
            .ToArray();
        LowBatteryThresholdCombo.ItemsSource = thresholds;
        int threshold = LowBatteryAdvisor.ClampThreshold(_app.Config.LowBatteryThresholdPercent);
        if (!Equals(LowBatteryThresholdCombo.SelectedValue, threshold))
        {
            LowBatteryThresholdCombo.SelectedValue = threshold;
        }
        LowBatteryThresholdRow.IsEnabled = notifications;   // 通知关闭时阈值档位不可选
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        try
        {
            _app.SetAutostart(AutostartSwitch.IsChecked == true);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("切换开机自启失败", exception);
        }
        SyncFromState();   // 用注册表实际状态回填开关
    }

    /// <summary>低电量阈值 UI 档位（值 = 百分比，与 <see cref="LowBatteryAdvisor.ConfigurableThresholds"/> 同源）。</summary>
    private sealed record ThresholdOption(int Percent, string Label);

    private void OnNotificationsChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        _app.Config.NotificationsEnabled = NotificationsSwitch.IsChecked == true;
        SaveNotificationSettings();
        SyncFromState();   // 回填并联动阈值行可用性
    }

    private void OnLowBatteryThresholdChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        if (LowBatteryThresholdCombo.SelectedValue is int percent
            && percent != _app.Config.LowBatteryThresholdPercent)
        {
            _app.Config.LowBatteryThresholdPercent = percent;
            SaveNotificationSettings();
        }
    }

    /// <summary>通知设置落盘（即时生效：AppState 判定与 TrayHost 展示都读同一 Config 实例）。</summary>
    private void SaveNotificationSettings()
    {
        try
        {
            _app.Config.Save();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("保存通知设置失败", exception);
        }
    }

    private void OnProjectLinkNavigate(object sender, RequestNavigateEventArgs eventArgs)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = eventArgs.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ErrorLog.Write("打开项目主页失败", exception);
        }
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Directory.CreateDirectory(ErrorLog.LogDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = ErrorLog.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ErrorLog.Write("打开日志文件夹失败", exception);
        }
    }

    // ───────────────────────── 窗口行为：尺寸位置与复用 ─────────────────────────

    /// <summary>从配置恢复上次尺寸与位置（缺失时保持 XAML 居中默认）。</summary>
    private void ApplySavedBounds()
    {
        var config = _app.Config;
        if (config.ControlCenterWidth >= MinWidth && config.ControlCenterWidth < 10000)
        {
            Width = config.ControlCenterWidth;
        }
        if (config.ControlCenterHeight >= MinHeight && config.ControlCenterHeight < 10000)
        {
            Height = config.ControlCenterHeight;
        }
        if (config.ControlCenterLeft is double left && config.ControlCenterTop is double top)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
    }

    /// <summary>位置越界保护（换显示器/改分辨率后不可找回窗口）：标题栏不在工作区内则居中。</summary>
    private void OnSourceInitialized(object sender, EventArgs eventArgs)
    {
        BackdropService.Apply(this, BackdropMode.Main);   // 主窗口 Mica（玻璃拟态，降级梯队见 BackdropService）

        var workArea = SystemParameters.WorkArea;
        bool visible = Left >= workArea.Left - 40
            && Top >= workArea.Top - 10
            && Left + 120 <= workArea.Right
            && Top + 60 <= workArea.Bottom;
        if (!visible)
        {
            Left = Math.Max(workArea.Left, (workArea.Right + workArea.Left - Width) / 2);
            Top = Math.Max(workArea.Top, (workArea.Bottom + workArea.Top - Height) / 2);
        }
    }

    /// <summary>关窗 = 隐藏复用（单例常驻），并记忆尺寸位置。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs eventArgs)
    {
        SaveBounds();
        eventArgs.Cancel = true;
        Hide();
    }

    private void SaveBounds()
    {
        var config = _app.Config;
        config.ControlCenterWidth = ActualWidth > 0 ? ActualWidth : Width;
        config.ControlCenterHeight = ActualHeight > 0 ? ActualHeight : Height;
        config.ControlCenterLeft = Left;
        config.ControlCenterTop = Top;
        try
        {
            config.Save();
        }
        catch (Exception exception)
        {
            ErrorLog.Write("保存控制中心窗口尺寸失败", exception);
        }
    }

    // ───────────────────────── UI 小工具 ─────────────────────────

    /// <summary>编程选中（触发 Checked → 同步视觉；_syncingUi 保证不回写耳机）。</summary>
    private void SetChecked(RadioButton radio)
    {
        if (radio.IsChecked != true)
        {
            radio.IsChecked = true;
        }
    }

    /// <summary>批量设置一组互斥 radio 的勾选态（仅在实际变化时赋值，避免多余事件）。</summary>
    private void SetChecked(params (bool Selected, RadioButton Radio)[] items)
    {
        foreach ((bool selected, RadioButton radio) in items)
        {
            if (radio.IsChecked != selected)
            {
                radio.IsChecked = selected;
            }
        }
    }
}
