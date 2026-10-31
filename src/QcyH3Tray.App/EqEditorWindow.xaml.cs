using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 均衡器编辑器（阶段 3b 重构，替换删除原 EqDetailWindow）：
/// <list type="bullet">
/// <item>顶部 7 段曲线画布：Polyline 折线实时跟随滑块，叠加 0dB 虚线基线对比；</item>
/// <item>总增益滑块（-12.0~+12.0 dB，0.5 步进）+ 数值显示；</item>
/// <item>7 根竖滑块（55/220/500/1000/1800/2800/7500 Hz）+ 每段 dB 数值 +
/// Q 只读显示（读到的设备 Q；协议支持写 Q，但 UI 首版不编辑——避免与曲线语义失真）；</item>
/// <item>读自耳机（GetEqV1Async）/ 应用到耳机（SetEqV1Async，Core 带回读确认 + 乐观快照）/
/// 重置为平线 / 恢复耳机当前值；</item>
/// <item>本地配置：保存/加载/覆盖/删除 + 导入/导出 JSON（%APPDATA%\QcyH3Controller\eq-profiles）；</item>
/// <item>未保存修改（dirty）关窗三选：应用并关闭 / 放弃 / 取消。</item>
/// </list>
/// 状态处理：断连/读取失败 → 滑块区禁用 + 明确提示；EQ validity≠Available → 禁用全部写操作。
/// 铁律：只有 7 段原生通道（<see cref="EqV1BandMap"/>），不做十段视觉伪装。
/// </summary>
public sealed partial class EqEditorWindow : Window
{
    /// <summary>七段中心频率短标签（完整频率见 <see cref="EqV1BandMap.Frequencies"/>）。</summary>
    private static readonly string[] FrequencyLabels = ["55", "220", "500", "1k", "1.8k", "2.8k", "7.5k"];

    private const double CurvePaddingX = 36;   // 右侧 +12/0/-12 刻度留白
    private const double CurvePaddingY = 12;

    private readonly AppState _app;
    private readonly EqProfileStore _profiles = new();
    private readonly Slider[] _bandSliders = new Slider[EqV1BandMap.SevenBandCount];
    private readonly TextBlock[] _bandValueTexts = new TextBlock[EqV1BandMap.SevenBandCount];
    private readonly TextBlock[] _bandQTexts = new TextBlock[EqV1BandMap.SevenBandCount];
    private readonly int[] _bandQs = new int[EqV1BandMap.SevenBandCount];
    private readonly EqDirtyTracker _dirty = new();

    private byte _eqType = EqDetailDefaults.EqType;   // 应用时保持读到的值（无读取用 Core 既定回退）
    private bool _busy;
    private bool _hasCurve;                           // 编辑器已持有可用曲线（读取/配置加载/导入）
    private bool _syncingUi;
    private bool _forceClose;                         // "放弃修改"路径的放行标记

    internal EqEditorWindow(AppState app)
    {
        InitializeComponent();
        Icon = AppIconSource.TryGetSource();   // 任务栏/标题栏应用图标（阶段 5）
        _app = app;

        Array.Fill(_bandQs, EqDetailDefaults.Q);
        BuildBandEditors();
        CurveCanvas.SizeChanged += (_, _) => RedrawCurve();

        _app.StateUpdated += OnAppStateChanged;
        Closed += (_, _) => _app.StateUpdated -= OnAppStateChanged;

        // 列表选择变化会改变加载/覆盖/删除按钮的可用性
        ProfileCombo.SelectionChanged += (_, _) => UpdateButtons();
        RefreshProfileList();

        Loaded += async (_, _) => await ReadFromHeadsetAsync().ConfigureAwait(true);
    }

    /// <summary>生成七列：dB 数值 / Q 只读 / 竖向滑块 / 频率标签。</summary>
    private void BuildBandEditors()
    {
        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var valueText = new TextBlock
            {
                Text = "0.0 dB",
                FontSize = (double)FindResource("Font/Size.Body"),
                Foreground = (Brush)FindResource("Brush/Text.Secondary"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var qText = new TextBlock
            {
                Text = FormatQ(_bandQs[i]),
                FontSize = (double)FindResource("Font/Size.Caption"),
                Foreground = (Brush)FindResource("Brush/Text.Tertiary"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 0),
            };
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = EqCurveGeometry.MinDb,
                Maximum = EqCurveGeometry.MaxDb,
                TickFrequency = 0.5,
                IsSnapToTickEnabled = true,
                IsMoveToPointEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(0, 8, 0, 4),
                Style = (Style)FindResource("EqSliderVertical"),
            };
            slider.ValueChanged += OnSliderValueChanged;
            var frequencyText = new TextBlock
            {
                Text = FrequencyLabels[i],
                FontSize = (double)FindResource("Font/Size.Caption"),
                Foreground = (Brush)FindResource("Brush/Text.Tertiary"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(valueText, 0);
            Grid.SetRow(qText, 1);
            Grid.SetRow(slider, 2);
            Grid.SetRow(frequencyText, 3);
            column.Children.Add(valueText);
            column.Children.Add(qText);
            column.Children.Add(slider);
            column.Children.Add(frequencyText);
            Grid.SetColumn(column, i);
            BandGrid.Children.Add(column);

            _bandSliders[i] = slider;
            _bandValueTexts[i] = valueText;
            _bandQTexts[i] = qText;
        }
    }

    // ───────────────────────── 编辑值（滑块镜像） ─────────────────────────

    /// <summary>当前编辑值（滑块 → dB 数组；用于 dirty 比对与曲线绘制）。</summary>
    private double[] CurrentGains() => [.. _bandSliders.Select(slider => slider.Value)];

    private double CurrentMasterGain => MasterGainSlider.Value;

    private void UpdateLabels()
    {
        MasterGainText.Text = EqUiLogic.FormatDb(CurrentMasterGain);
        for (int i = 0; i < _bandSliders.Length; i++)
        {
            _bandValueTexts[i].Text = EqUiLogic.FormatDb(_bandSliders[i].Value);
        }
    }

    private void RedrawCurve()
    {
        double width = CurveCanvas.ActualWidth;
        double height = CurveCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        CurveLine.Points = new PointCollection(
            EqCurveGeometry.BandCurvePoints(CurrentGains(), width, height, CurvePaddingX, CurvePaddingY));
        (Point start, Point end) = EqCurveGeometry.BaselineLine(width, height, CurvePaddingX, CurvePaddingY);
        BaselineLine.Points = new PointCollection([start, end]);
    }

    private static string FormatQ(int q) => $"Q {q / 100.0:0.00}";

    // ───────────────────────── 交互事件 ─────────────────────────

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (_syncingUi)
        {
            return;
        }
        UpdateLabels();
        RedrawCurve();
        _dirty.Touch();
        UpdateButtons();
    }

    private async void OnReadClick(object sender, RoutedEventArgs eventArgs)
        => await ReadFromHeadsetAsync().ConfigureAwait(true);

    private async void OnRestoreClick(object sender, RoutedEventArgs eventArgs)
        => await RestoreFromHeadsetAsync().ConfigureAwait(true);

    private void OnResetClick(object sender, RoutedEventArgs eventArgs)
    {
        SetEditorValues(Enumerable.Repeat(0d, EqV1BandMap.SevenBandCount), 0, _bandQs);
        _dirty.Touch();
        HintTextBlock.Text = "已重置为平线（尚未应用到耳机）。";
        UpdateButtons();
    }

    private async void OnApplyClick(object sender, RoutedEventArgs eventArgs)
        => await ApplyToHeadsetAsync().ConfigureAwait(true);

    // ───────────────────────── 读 / 恢复 / 应用 ─────────────────────────

    /// <summary>读自耳机（EQ v1）并刷新滑块/曲线/Q。打开窗口与链路恢复时自动调用。</summary>
    private async Task ReadFromHeadsetAsync()
    {
        if (_busy || !_app.IsLinkEstablished)
        {
            return;
        }
        _busy = true;
        UpdateButtons();
        HintTextBlock.Text = "正在读取耳机当前均衡器…";
        try
        {
            EqV1Info? info = await _app.GetEqV1Async().ConfigureAwait(true);
            if (info is null)
            {
                HintTextBlock.Text = _app.IsConnected
                    ? "无法读取耳机当前均衡器：耳机未返回 EQ 数据。"
                    : "无法读取耳机当前均衡器：耳机未连接。";
                return;
            }
            LoadCurve(info.EqType, info);
            _dirty.SetBaseline(CurrentGains(), CurrentMasterGain);
            HintTextBlock.Text = "已读取耳机当前均衡器。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write("读取 EQ 失败", exception);
            HintTextBlock.Text = $"无法读取耳机当前均衡器：{exception.Message}";
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>恢复耳机当前值：优用最新快照（Available），否则重新读取。</summary>
    private async Task RestoreFromHeadsetAsync()
    {
        if (_busy || !_app.IsLinkEstablished)
        {
            return;
        }
        _busy = true;
        UpdateButtons();
        HintTextBlock.Text = "正在恢复耳机当前值…";
        try
        {
            EqV1Info? info = _app.Data.EqValidity == DataValidity.Available
                ? _app.Data.EqV1
                : await _app.GetEqV1Async().ConfigureAwait(true);
            if (info is null)
            {
                HintTextBlock.Text = _app.IsConnected
                    ? "无法读取耳机当前均衡器：耳机未返回 EQ 数据。"
                    : "无法读取耳机当前均衡器：耳机未连接。";
                return;
            }
            LoadCurve(info.EqType, info);
            _dirty.SetBaseline(CurrentGains(), CurrentMasterGain);
            HintTextBlock.Text = "已恢复耳机当前值。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write("恢复 EQ 失败", exception);
            HintTextBlock.Text = $"恢复失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>应用到耳机（SetEqV1Async：Core 已带回读确认 + 乐观快照）。成功后基线=已应用值。</summary>
    private async Task<bool> ApplyToHeadsetAsync()
    {
        if (_busy)
        {
            return false;
        }
        _busy = true;
        UpdateButtons();
        HintTextBlock.Text = "正在应用到耳机…";
        try
        {
            int masterGain = EqProfileConverter.ToHundredths(CurrentMasterGain);
            var bands = new List<EqV1Band>(EqV1BandMap.SevenBandCount);
            for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
            {
                bands.Add(new EqV1Band(
                    EqV1BandMap.Frequencies[i],
                    EqProfileConverter.ToHundredths(_bandSliders[i].Value),
                    _bandQs[i]));
            }

            bool ok = await _app.RunWriteCommandAsync("EQ·自定义", async () =>
                await _app.Manager.SetEqV1Async(_eqType, masterGain, bands).ConfigureAwait(false) is not null)
                .ConfigureAwait(true);
            if (ok)
            {
                _dirty.SetBaseline(CurrentGains(), CurrentMasterGain);
                HintTextBlock.Text = "已应用到耳机。";
            }
            else
            {
                HintTextBlock.Text = "耳机无响应，应用失败（可重试）。";
            }
            return ok;
        }
        catch (Exception exception)
        {
            ErrorLog.Write("应用 EQ 失败", exception);
            HintTextBlock.Text = $"应用失败：{exception.Message}";
            return false;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    /// <summary>把设备曲线落到编辑器（eqType/Q 同步更新；程序赋值不触发 dirty）。</summary>
    private void LoadCurve(byte eqType, EqV1Info info)
    {
        double[] gains = new double[EqV1BandMap.SevenBandCount];
        int[] qs = new int[EqV1BandMap.SevenBandCount];
        Array.Fill(qs, EqDetailDefaults.Q);
        for (int i = 0; i < info.Bands.Count && i < EqV1BandMap.SevenBandCount; i++)
        {
            gains[i] = Math.Clamp(info.Bands[i].Gain / 100.0, EqCurveGeometry.MinDb, EqCurveGeometry.MaxDb);
            qs[i] = info.Bands[i].Q;
        }
        _eqType = eqType;
        SetEditorValues(gains, Math.Clamp(info.MasterGain / 100.0, EqCurveGeometry.MinDb, EqCurveGeometry.MaxDb), qs);
        _hasCurve = true;
    }

    /// <summary>程序化设置编辑器值（滑块 dB / Q 表），并刷新标签、曲线与按钮。</summary>
    private void SetEditorValues(IEnumerable<double> gainsDb, double masterGainDb, IReadOnlyList<int> qs)
    {
        _syncingUi = true;
        try
        {
            double[] gains = gainsDb.Take(EqV1BandMap.SevenBandCount).ToArray();
            for (int i = 0; i < _bandSliders.Length; i++)
            {
                _bandSliders[i].Value = i < gains.Length ? gains[i] : 0;
                _bandQs[i] = i < qs.Count ? qs[i] : EqDetailDefaults.Q;
                _bandQTexts[i].Text = FormatQ(_bandQs[i]);
            }
            MasterGainSlider.Value = masterGainDb;
        }
        finally
        {
            _syncingUi = false;
        }
        UpdateLabels();
        RedrawCurve();
    }

    // ───────────────────────── AppState 同步（validity / 链路变化） ─────────────────────────

    private void OnAppStateChanged()
    {
        if (!_hasCurve && !_busy)
        {
            if (_app.Data.EqValidity == DataValidity.Available && _app.Data.EqV1 is { } snapshot)
            {
                // 其它入口（快捷面板）已同步过 EQ：直接用快照填充，不发命令
                LoadCurve(snapshot.EqType, snapshot);
                _dirty.SetBaseline(CurrentGains(), CurrentMasterGain);
            }
            else if (_app.IsLinkEstablished
                && _app.Data.EqValidity is DataValidity.Loading or DataValidity.Unknown)
            {
                _ = ReadFromHeadsetAsync();   // 链路同步中：自动补读（读取失败不自动重试，走"读自耳机"）
            }
        }
        else if (_hasCurve
            && !_busy
            && !_dirty.IsDirty(CurrentGains(), CurrentMasterGain)
            && _app.Data.EqValidity == DataValidity.Available
            && _app.Data.EqV1 is { } info
            && CurveDiffersFromDevice(info))
        {
            // 用户无未保存修改时跟随耳机实际值（如面板里点了预设）
            LoadCurve(info.EqType, info);
            _dirty.SetBaseline(CurrentGains(), CurrentMasterGain);
        }

        if (!_busy && _app.IsLinkEstablished && !_hasCurve)
        {
            HintTextBlock.Text = "正在读取耳机当前均衡器…";
        }
        else if (!_busy && !_app.IsLinkEstablished)
        {
            HintTextBlock.Text = "耳机未连接：读取与写入不可用，等待自动重连。";
        }
        UpdateButtons();
    }

    private bool CurveDiffersFromDevice(EqV1Info info)
    {
        if (info.Bands.Count < EqV1BandMap.SevenBandCount)
        {
            return false;
        }
        double[] current = CurrentGains();
        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            if (Math.Abs(current[i] - info.Bands[i].Gain / 100.0) > EqDirtyTracker.Tolerance)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 按钮与编辑区可用性：
    /// 滑块区 = 已持有曲线（断连/读取失败 → 禁用 + 提示"无法读取耳机当前均衡器"）；
    /// 应用/恢复 = 链路建立 且 EQ validity==Available（≠Available 禁用全部写操作）；
    /// 读自耳机 = 链路建立；重置平线/本地配置 = 本地操作（持有曲线即可）。
    /// </summary>
    private void UpdateButtons()
    {
        bool link = _app.IsLinkEstablished;
        bool eqWritable = link && _app.Data.EqValidity == DataValidity.Available;

        BandArea.IsEnabled = _hasCurve;
        MasterGainRow.IsEnabled = _hasCurve;

        ReadButton.IsEnabled = !_busy && link;
        RestoreButton.IsEnabled = !_busy && _hasCurve && eqWritable;
        ApplyButton.IsEnabled = !_busy && _hasCurve && eqWritable;
        ResetButton.IsEnabled = !_busy && _hasCurve;

        LoadProfileButton.IsEnabled = !_busy && ProfileCombo.SelectedItem is string;
        OverwriteProfileButton.IsEnabled = !_busy && _hasCurve && ProfileCombo.SelectedItem is string;
        DeleteProfileButton.IsEnabled = !_busy && ProfileCombo.SelectedItem is string;
        ImportProfileButton.IsEnabled = !_busy;
        ExportProfileButton.IsEnabled = !_busy && _hasCurve;
        SaveProfileButton.IsEnabled = !_busy && _hasCurve;
    }

    // ───────────────────────── 本地配置 ─────────────────────────

    private void RefreshProfileList(string? select = null)
    {
        IReadOnlyList<string> names = _profiles.ListNames();
        ProfileCombo.ItemsSource = names;
        if (select is not null && names.Contains(select))
        {
            ProfileCombo.SelectedItem = select;
        }
        else if (ProfileCombo.SelectedItem is null && names.Count > 0)
        {
            ProfileCombo.SelectedIndex = 0;
        }
        UpdateButtons();
    }

    private string? SelectedProfileName => ProfileCombo.SelectedItem as string;

    private EqProfile BuildProfileFromEditor(string name)
        => EqProfileConverter.FromEditor(name, _eqType, CurrentGains(), CurrentMasterGain, _bandQs);

    /// <summary>把配置档案落进编辑器（程序赋值 + 标记 dirty：尚未应用到耳机）。</summary>
    private void LoadProfileIntoEditor(EqProfile profile)
    {
        (double[] gains, double master, int[] qs) = EqProfileConverter.ToEditor(profile);
        _eqType = profile.EqType;
        SetEditorValues(gains, master, qs);
        _hasCurve = true;
        _dirty.Touch();
    }

    private void OnProfileLoadClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedProfileName is not string name)
        {
            HintTextBlock.Text = "请先在列表中选择要加载的配置。";
            return;
        }
        EqProfile? profile = _profiles.Load(name);
        if (profile is null)
        {
            HintTextBlock.Text = $"配置「{name}」读取失败或文件已损坏。";
            return;
        }
        LoadProfileIntoEditor(profile);
        HintTextBlock.Text = $"已加载配置「{name}」（尚未应用到耳机）。";
        UpdateButtons();
    }

    private void OnProfileOverwriteClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedProfileName is not string name)
        {
            HintTextBlock.Text = "请先在列表中选择要覆盖的配置。";
            return;
        }
        if (MessageBox.Show(this, $"用当前曲线覆盖配置「{name}」？", "覆盖配置",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            _profiles.Save(BuildProfileFromEditor(name));
            HintTextBlock.Text = $"已覆盖保存配置「{name}」。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write($"覆盖 EQ 配置「{name}」失败", exception);
            HintTextBlock.Text = $"覆盖保存失败：{exception.Message}";
        }
    }

    private void OnProfileDeleteClick(object sender, RoutedEventArgs eventArgs)
    {
        if (SelectedProfileName is not string name)
        {
            HintTextBlock.Text = "请先在列表中选择要删除的配置。";
            return;
        }
        if (MessageBox.Show(this, $"删除配置「{name}」？该操作不可撤销。", "删除配置",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }
        _profiles.Delete(name);
        HintTextBlock.Text = $"已删除配置「{name}」。";
        RefreshProfileList();
    }

    private void OnProfileSaveClick(object sender, RoutedEventArgs eventArgs)
    {
        string name = ProfileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            HintTextBlock.Text = "请先输入配置名称再保存。";
            return;
        }
        try
        {
            _profiles.Save(BuildProfileFromEditor(name));
            ProfileNameBox.Text = string.Empty;
            RefreshProfileList(select: EqProfileStore.SafeFileName(name));
            HintTextBlock.Text = $"已保存配置「{name}」。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write($"保存 EQ 配置「{name}」失败", exception);
            HintTextBlock.Text = $"保存失败：{exception.Message}";
        }
    }

    private void OnProfileImportClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 EQ 配置",
            Filter = "EQ 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        EqProfile? profile = EqProfileStore.Import(dialog.FileName);
        if (profile is null)
        {
            MessageBox.Show(
                this,
                "无法导入该文件：不是有效的 EQ 配置 JSON，或路径不可读。",
                "导入 EQ 配置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        LoadProfileIntoEditor(profile);
        HintTextBlock.Text = $"已导入「{Path.GetFileName(dialog.FileName)}」（尚未应用到耳机）。";
        UpdateButtons();
    }

    private void OnProfileExportClick(object sender, RoutedEventArgs eventArgs)
    {
        string defaultName = EqProfileStore.SafeFileName(
            string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? SelectedProfileName ?? "eq-profile" : ProfileNameBox.Text);
        var dialog = new SaveFileDialog
        {
            Title = "导出 EQ 配置",
            Filter = "EQ 配置 (*.json)|*.json",
            FileName = defaultName + ".json",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            EqProfileStore.Export(dialog.FileName, BuildProfileFromEditor(defaultName));
            HintTextBlock.Text = $"已导出到 {dialog.FileName}";
        }
        catch (Exception exception)
        {
            ErrorLog.Write($"导出 EQ 配置失败：{dialog.FileName}", exception);
            MessageBox.Show(this, $"导出失败：{exception.Message}", "导出 EQ 配置",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ───────────────────────── 关窗：未保存修改三选 ─────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (_forceClose || !_dirty.IsDirty(CurrentGains(), CurrentMasterGain))
        {
            return;   // 无未应用修改（或已选择放弃）：正常关闭
        }
        eventArgs.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        MessageBoxResult choice = MessageBox.Show(
            this,
            "当前均衡器有未应用到耳机的修改。\n\n是：应用并关闭\n否：放弃修改并关闭\n取消：留在编辑器",
            "均衡器编辑器",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        switch (choice)
        {
            case MessageBoxResult.Yes:
                if (await ApplyToHeadsetAsync().ConfigureAwait(true))
                {
                    Close();
                }
                break;
            case MessageBoxResult.No:
                _forceClose = true;
                // 此刻仍在 OnClosing（WM_CLOSE 处理）调用栈内：直接 Close() 会撞上
                // 原始关闭流程（VerifyNotClosing 抛 InvalidOperationException，终验实测）。
                // 排队到 Dispatcher，待 eventArgs.Cancel=true 的本次关闭处理返回后再关。
                _ = Dispatcher.BeginInvoke(() => Close());
                break;
        }
    }
}
