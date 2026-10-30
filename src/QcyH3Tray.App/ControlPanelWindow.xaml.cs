using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 主弹出面板：托盘左键唤起，宽 300、高度自适应，贴系统托盘上方；
/// Deactivated 自动隐藏（复用不 Close）。头部状态 / 电量 / 噪声控制（四段 +
/// 等级/场景第二行）/ 均衡器预设 chips / 游戏模式开关 / 设置菜单。
/// UI 状态统一从 <see cref="AppState"/> 同步；用户操作直接写耳机（乐观显示，
/// 失败由 <see cref="AppState.RunWriteCommandAsync"/> 重试并计入横幅）。
/// </summary>
public sealed partial class ControlPanelWindow : Window
{
    private const byte EqPresetChipMin = EqPresets.Default;   // 0x01
    private const byte EqPresetChipMax = EqPresets.Classic;   // 0x06

    private static readonly string[] LevelNames = ["低", "中", "高"];

    private readonly AppState _app;
    private readonly RadioButton[] _eqPresetChips;
    private readonly RadioButton[] _noiseSegments;

    private bool _syncingUi;
    private int _currentLevel = 1;             // 记住最近一次档位（默认中）
    private AncFamily? _currentFamily;         // 记住最近一次模式家族
    private EqDetailWindow? _eqDetail;

    /// <summary>最近一次被自动隐藏的时间（托盘左键"弹回"宽限用）。</summary>
    public DateTime HiddenAtUtc { get; private set; } = DateTime.MinValue;

    internal ControlPanelWindow(AppState app)
    {
        InitializeComponent();
        _app = app;

        _noiseSegments = [SegOff, SegTransparency, SegNoiseCancelling, SegAdaptive];
        _eqPresetChips =
        [
            EqPresetDefault, EqPresetPop, EqPresetHeavyBass,
            EqPresetRock, EqPresetSoft, EqPresetClassic,
        ];

        NoiseSegmentGrid.SizeChanged += (_, _) =>
            LayoutThumb(SelectedSegmentIndex() is int index and >= 0 ? index : 2, animate: false);

        _app.StateUpdated += OnAppStateChanged;
        _app.CommandHealthChanged += OnCommandHealthChanged;
        _app.AutostartChanged += OnAutostartChanged;
        Closed += (_, _) =>
        {
            _app.StateUpdated -= OnAppStateChanged;
            _app.CommandHealthChanged -= OnCommandHealthChanged;
            _app.AutostartChanged -= OnAutostartChanged;
        };
    }

    /// <summary>托盘唤起：同步状态 → 贴托盘定位 → 显示 → 入场动画 → 全量刷新/重连。</summary>
    public void Present()
    {
        SyncFromState();
        PositionAtTray();
        Show();
        Activate();
        PlayEntranceAnimation();
        _ = TriggerRefreshAsync();
    }

    /// <summary>供 TrayHost 在状态变化时刷新（UI 线程调用）。</summary>
    public void RefreshFromState()
    {
        SyncFromState();
        if (IsVisible)
        {
            UpdateLayout();
            PositionAtTray();
        }
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
                ErrorLog.Write("面板全量刷新失败", exception);
            }
        }
        else
        {
            // 未连接：立即触发刷新/重连（面板打开即拉起连接流程）
            await _app.ConnectAsync().ConfigureAwait(true);
        }
    }

    // ───────────────────────── 定位与动画 ─────────────────────────

    /// <summary>贴系统托盘上方：Left=WorkArea.Right-宽-12，Top=WorkArea.Bottom-高-8。</summary>
    private void PositionAtTray()
    {
        var workArea = SystemParameters.WorkArea;
        double availableWidth = Width - RootCard.Margin.Left - RootCard.Margin.Right;
        RootCard.Measure(new Size(availableWidth, double.PositiveInfinity));
        double height = RootCard.DesiredSize.Height;   // DesiredSize 已含 8×2 外边距

        Left = workArea.Right - Width - 12;
        Top = Math.Max(workArea.Top, workArea.Bottom - height - 8);
    }

    /// <summary>入场：淡入 + 轻微上移，约 150ms。</summary>
    private void PlayEntranceAnimation()
    {
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = easing };
        var rise = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(150)) { EasingFunction = easing };
        RootCard.BeginAnimation(OpacityProperty, fade);
        RootShift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    // ───────────────────────── 状态同步 ─────────────────────────

    private void OnAppStateChanged() => RefreshFromState();

    private void OnCommandHealthChanged(bool degraded)
    {
        ErrorBanner.Visibility = degraded ? Visibility.Visible : Visibility.Collapsed;
        if (IsVisible)
        {
            UpdateLayout();
            PositionAtTray();
        }
    }

    private void OnAutostartChanged() => SyncFromState();

    private void SyncFromState()
    {
        _syncingUi = true;
        try
        {
            // 头部状态：已连接=绿，其余=灰；未连接时显示 ↻ 刷新按钮
            bool connected = _app.IsConnected;
            StatusTextBlock.Text = _app.StateLabel;
            StatusTextBlock.Foreground = connected
                ? (Brush)FindResource("ConnectedBrush")
                : (Brush)FindResource("DisconnectedBrush");
            RefreshButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;

            // 电量：未连接显示 --
            BatteryTextBlock.Text = connected && _app.BatteryPercent is int percent
                ? $"{percent}%"
                : "--";

            ApplyNoiseState(_app.Data.NoiseModeInfo);
            ApplyEqState(_app.Data.EqV1);

            GameModeSwitch.IsChecked = _app.Data.GameMode == true;

            ErrorBanner.Visibility = _app.Degraded ? Visibility.Visible : Visibility.Collapsed;
            AutostartMenuItem.IsChecked = _app.IsAutostartEnabled;
        }
        finally
        {
            _syncingUi = false;
        }
    }

    // ───────────────────────── 噪声控制 ─────────────────────────

    private void ApplyNoiseState(AncModeInfo? info)
    {
        int segment = info?.Family switch
        {
            AncFamily.Off => 0,
            AncFamily.Transparency => 1,
            AncFamily.NoiseCancelling or AncFamily.Commuting or AncFamily.Indoor or AncFamily.AntiWind => 2,
            AncFamily.Adaptive => 3,
            _ => -1,
        };
        if (segment >= 0)
        {
            SetChecked(_noiseSegments[segment]);
        }
        _currentFamily = info?.Family;

        // 场景模式（通勤/室内/抗风）与降噪共用"降噪"段，第二行同时显示
        bool showSubRow = segment == 2;
        NoiseSubRow.Visibility = showSubRow ? Visibility.Visible : Visibility.Collapsed;
        if (!showSubRow || info is null)
        {
            return;
        }

        // 等级：低/中/高（抗风噪等无档位 → 三个都不亮）
        if (info.Level is int level and >= 0 and <= 2)
        {
            _currentLevel = level;
        }
        SetChecked(
            (info.Level == 0, LevelLow),
            (info.Level == 1, LevelMedium),
            (info.Level == 2, LevelHigh));

        // 场景组：通勤/室内/抗风（纯降噪时不亮）
        SetChecked(
            (info.Family == AncFamily.Commuting, SceneCommuting),
            (info.Family == AncFamily.Indoor, SceneIndoor),
            (info.Family == AncFamily.AntiWind, SceneAntiWind));
    }

    private int SelectedSegmentIndex()
    {
        for (int i = 0; i < _noiseSegments.Length; i++)
        {
            if (_noiseSegments[i].IsChecked == true)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>滑动高亮块到指定段（过渡动画 ~150ms）。</summary>
    private void LayoutThumb(int index, bool animate)
    {
        double column = NoiseSegmentGrid.ActualWidth / _noiseSegments.Length;
        if (column <= 0 || index < 0)
        {
            return;
        }
        NoiseThumb.Width = column - 6;
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
                _ = WriteNoiseModeAsync(ModeName(AncFamily.NoiseCancelling, _currentLevel),
                    ModeBytes(AncFamily.NoiseCancelling, _currentLevel));
                break;
            case 3:
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
        // 在降噪/通勤/室内家族内调档位；其它家族（通透等）点档位 → 降噪·档位
        AncFamily family = _currentFamily is AncFamily.Commuting or AncFamily.Indoor
            ? _currentFamily.Value
            : AncFamily.NoiseCancelling;
        _ = WriteNoiseModeAsync(ModeName(family, level), ModeBytes(family, level));
    }

    private void OnSceneChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        if (sender == SceneCommuting)
        {
            _currentFamily = AncFamily.Commuting;
            _ = WriteNoiseModeAsync(ModeName(AncFamily.Commuting, _currentLevel),
                ModeBytes(AncFamily.Commuting, _currentLevel));
        }
        else if (sender == SceneIndoor)
        {
            _currentFamily = AncFamily.Indoor;
            _ = WriteNoiseModeAsync(ModeName(AncFamily.Indoor, _currentLevel),
                ModeBytes(AncFamily.Indoor, _currentLevel));
        }
        else if (sender == SceneAntiWind)
        {
            _currentFamily = AncFamily.AntiWind;
            _ = WriteNoiseModeAsync("抗风噪", AncMode.AntiWind);
        }
    }

    private async Task WriteNoiseModeAsync(string name, byte[] modeBytes)
    {
        bool ok = await _app.RunWriteCommandAsync(name, async () =>
            await _app.Manager.SetNoiseModeAsync(modeBytes).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            RefreshFromState();   // 写入失败回滚到耳机实际状态
        }
    }

    private static byte[] ModeBytes(AncFamily family, int level) => (family, level) switch
    {
        (AncFamily.NoiseCancelling, 0) => AncMode.NoiseCancelLow,
        (AncFamily.NoiseCancelling, 1) => AncMode.NoiseCancelMedium,
        (AncFamily.NoiseCancelling, _) => AncMode.NoiseCancelHigh,
        (AncFamily.Commuting, 0) => AncMode.CommutingLow,
        (AncFamily.Commuting, 1) => AncMode.CommutingMedium,
        (AncFamily.Commuting, _) => AncMode.CommutingHigh,
        (AncFamily.Indoor, 0) => AncMode.IndoorLow,
        (AncFamily.Indoor, 1) => AncMode.IndoorMedium,
        (AncFamily.Indoor, _) => AncMode.IndoorHigh,
        _ => AncMode.NoiseCancelMedium,
    };

    private static string ModeName(AncFamily family, int level) => family switch
    {
        AncFamily.NoiseCancelling => $"降噪·{LevelNames[level]}",
        AncFamily.Commuting => $"通勤·{LevelNames[level]}",
        AncFamily.Indoor => $"室内·{LevelNames[level]}",
        _ => "降噪",
    };

    // ───────────────────────── 均衡器 ─────────────────────────

    private void ApplyEqState(EqV1Info? info)
    {
        if (info is null)
        {
            return;   // 未知曲线：保持现状，不乱高亮
        }
        if (MatchPreset(info) is byte presetId)
        {
            SetChecked(_eqPresetChips[presetId - EqPresetChipMin]);
        }
        else
        {
            SetChecked(EqCustomChip);   // 与所有预设都不一致 → 高亮"自定义"
        }
    }

    /// <summary>
    /// 当前 7 段曲线与哪个预设的 10→7 映射一致（0x01=默认 0x06=古典）；
    /// 不一致返回 null（→"自定义"）。全零平线会匹配到"默认"。
    /// </summary>
    private static byte? MatchPreset(EqV1Info? info)
    {
        if (info is null || info.Bands.Count < EqV1BandMap.SevenBandCount)
        {
            return null;
        }
        int[] current = [.. info.Bands.Take(EqV1BandMap.SevenBandCount).Select(band => band.Gain)];
        for (byte presetId = EqPresetChipMin; presetId <= EqPresetChipMax; presetId++)
        {
            if (EqV1BandMap.TenToSeven(EqPresets.GetOffsets(presetId)).SequenceEqual(current))
            {
                return presetId;
            }
        }
        return null;
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
        _ = WriteEqPresetAsync((byte)(index + EqPresetChipMin));
    }

    /// <summary>写预设 = 10 段偏移经 <see cref="EqV1BandMap.TenToSeven"/> 映射成 7 段曲线写入（eqType/masterGain/Q 保持读到的值）。</summary>
    private async Task WriteEqPresetAsync(byte presetId)
    {
        EqV1Info? current = await _app.GetEqV1Async().ConfigureAwait(true);
        byte eqType = current?.EqType ?? 0x02;      // 真机基线 eqType
        int masterGain = current?.MasterGain ?? 0;
        int[] gains = EqV1BandMap.TenToSeven(EqPresets.GetOffsets(presetId));
        var bands = new List<EqV1Band>(EqV1BandMap.SevenBandCount);
        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            int q = current is not null && i < current.Bands.Count ? current.Bands[i].Q : 120;
            bands.Add(new EqV1Band(EqV1BandMap.Frequencies[i], gains[i], q));
        }

        bool ok = await _app.RunWriteCommandAsync($"EQ 预设·{EqPresets.Names[presetId]}", async () =>
            await _app.Manager.SetEqV1Async(eqType, masterGain, bands).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            RefreshFromState();
        }
    }

    private void OnEqCustomChecked(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        OpenEqDetail();
    }

    private void OpenEqDetail()
    {
        if (_eqDetail is { IsLoaded: true })
        {
            _eqDetail.Activate();
            return;
        }
        _eqDetail = new EqDetailWindow(_app);
        _eqDetail.Show();
        _eqDetail.Activate();
    }

    // ───────────────────────── 游戏模式 / 页脚 ─────────────────────────

    private void OnGameModeChanged(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        bool enabled = GameModeSwitch.IsChecked == true;
        _ = WriteGameModeAsync(enabled);
    }

    private async Task WriteGameModeAsync(bool enabled)
    {
        bool ok = await _app.RunWriteCommandAsync(enabled ? "游戏模式·开" : "游戏模式·关", async () =>
            await _app.Manager.SetGameModeAsync(enabled).ConfigureAwait(false) is not null)
            .ConfigureAwait(true);
        if (!ok)
        {
            RefreshFromState();
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs eventArgs)
        => _ = _app.ConnectAsync();

    private void OnSettingsClick(object sender, RoutedEventArgs eventArgs)
    {
        var menu = SettingsButton.ContextMenu;
        menu.PlacementTarget = SettingsButton;
        menu.Placement = PlacementMode.Top;   // 面板贴屏幕底部，菜单向上弹
        menu.IsOpen = true;
    }

    private void OnAutostartToggleClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        try
        {
            _app.SetAutostart(AutostartMenuItem.IsChecked);
        }
        catch (Exception exception)
        {
            ErrorLog.Write("切换开机自启失败", exception);
        }
        SyncFromState();   // 用注册表实际状态回填两个菜单的勾选
    }

    private void OnAboutClick(object sender, RoutedEventArgs eventArgs)
    {
        string version = typeof(ControlPanelWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
        MessageBox.Show(
            this,
            $"QCY H3 托盘控制工具\n版本 {version}\n\n" +
            "通过 BLE RCSP 协议控制 QCY H3（杰理 JieLi 芯片）耳机：\n" +
            "降噪模式 / 均衡器 / 游戏模式 / 电量显示。",
            "关于",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ───────────────────────── 窗口行为 ─────────────────────────

    /// <summary>失焦自动收起（复用不 Close）。</summary>
    private void OnDeactivated(object sender, EventArgs eventArgs) => HidePanel();

    private void OnPreviewKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Escape)
        {
            HidePanel();
        }
    }

    private void HidePanel()
    {
        HiddenAtUtc = DateTime.UtcNow;
        Hide();
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
