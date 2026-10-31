using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QcyH3Tray.Core.App;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 按键自定义编辑器（阶段 4b）：
/// <list type="bullet">
/// <item>按 keyNum 分组（<see cref="KeyMapEditorSupport.GetKeyNumbers"/>，首现顺序）的卡片，
/// 每组渲染 单击/双击/三击/四击/长按 5 行（<see cref="KeyMapEditorSupport.EditableActions"/>），
/// ComboBox 选功能（选项 = 已知全集 + 表内未知值，见 <see cref="KeyMapEditorSupport.BuildFunctionOptions"/>）；
/// 表中不存在的 (keyNum, action) 行显示「未配置」且不参与写入（不凭空发明数据）。</item>
/// <item>出厂槽（action ≥ 0x81）折叠区只读展示——语义未知标「出厂配置」，禁止编辑。</item>
/// <item>读自耳机 = GetKeyMappingsAsync（快照同步 DeviceData.KeyMap）；保存到耳机 =
/// 编辑差异 → <see cref="KeyMapEditorSupport.ApplyEdits"/> 组装整表 → SetKeyMappingsAsync
/// （Core 写后回读确认 + 乐观快照）。写前 <see cref="KeyMapEditorSupport.StructurePreserved"/>
/// 复核：条数一致、出厂槽逐字节一致——「整表或全不写」，绝不写出厂槽或部分表。</item>
/// <item>断连 / 读取失败 → 编辑区禁用 + 明确提示；未保存修改（dirty）关窗三选：
/// 保存并关闭 / 放弃修改 / 取消（对齐 EqEditorWindow 模式）。</item>
/// </list>
/// </summary>
public sealed partial class KeyMapEditorWindow : Window
{
    /// <summary>ComboBox 选项（值 + 显示名；DisplayMemberPath 用）。</summary>
    private sealed record FunctionOption(byte Value, string Name);

    private readonly AppState _app;
    private readonly Dictionary<KeyMapRowKey, ComboBox> _rowCombos = [];
    private readonly Dictionary<KeyMapRowKey, byte> _edits = [];

    private List<KeyMappingEntry> _baseline = [];      // 最近一次与耳机确认一致的整表
    private List<byte> _functionOptions = [];          // ComboBox 值集（全集 + 表内未知值）
    private bool _hasTable;
    private bool _busy;
    private bool _syncingUi;
    private bool _forceClose;                          // “放弃修改”路径的放行标记

    internal KeyMapEditorWindow(AppState app)
    {
        InitializeComponent();
        Icon = AppIconSource.TryGetSource();   // 任务栏/标题栏应用图标（阶段 5）
        _app = app;

        _app.StateUpdated += OnAppStateChanged;
        Closed += (_, _) => _app.StateUpdated -= OnAppStateChanged;

        Loaded += async (_, _) => await ReadFromHeadsetAsync().ConfigureAwait(true);
    }

    // ───────────────────────── 读 / 保存 ─────────────────────────

    /// <summary>读自耳机（按键映射整表）并重建编辑区。打开窗口与链路恢复时自动调用。</summary>
    private async Task ReadFromHeadsetAsync()
    {
        if (_busy || !_app.IsLinkEstablished)
        {
            return;
        }
        _busy = true;
        UpdateButtons();
        HintTextBlock.Text = "正在读取耳机按键配置…";
        try
        {
            List<KeyMappingEntry>? entries = await _app.GetKeyMappingsAsync().ConfigureAwait(true);
            if (entries is null || entries.Count == 0)
            {
                _hasTable = false;
                HintTextBlock.Text = _app.IsLinkEstablished
                    ? "无法读取耳机按键配置：耳机未返回按键表。"
                    : "无法读取耳机按键配置：耳机未连接。";
                UpdateEditorArea();
                return;
            }
            _baseline = entries;
            _edits.Clear();
            _hasTable = true;
            RebuildEditor();
            HintTextBlock.Text = $"已读取耳机按键配置（{_baseline.Count} 条映射，其中出厂配置 "
                + $"{KeyMapEditorSupport.FactoryEntries(_baseline).Count} 条）。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write("读取按键映射失败", exception);
            _hasTable = false;
            HintTextBlock.Text = $"无法读取耳机按键配置：{exception.Message}";
        }
        finally
        {
            _busy = false;
            UpdateEditorArea();
            UpdateButtons();
        }
    }

    /// <summary>保存到耳机：编辑差异 → 整表组装 → SetKeyMappingsAsync（Core 写后回读确认 + 乐观快照）。</summary>
    private async Task<bool> SaveToHeadsetAsync()
    {
        if (_busy || !_hasTable)
        {
            return false;
        }
        List<KeyMappingEntry> fullTable = KeyMapEditorSupport.ApplyEdits(_baseline, _edits);
        if (!KeyMapEditorSupport.StructurePreserved(_baseline, fullTable))
        {
            // 守卫被触发即代码缺陷（ApplyEdits 结构性保证成立），宁可拒绝写也不冒险
            HintTextBlock.Text = "整表校验失败（出厂配置或行数发生变化），已拒绝写入。";
            return false;
        }
        _busy = true;
        UpdateButtons();
        HintTextBlock.Text = "正在保存按键配置到耳机…";
        try
        {
            IReadOnlyList<KeyMappingEntry>? confirmed = null;
            bool ok = await _app.RunWriteCommandAsync("按键自定义", async () =>
            {
                confirmed = await _app.SetKeyMappingsAsync(fullTable).ConfigureAwait(false);
                return confirmed is not null;
            }).ConfigureAwait(true);
            if (ok && confirmed is not null)
            {
                // Core 的返回值 = 回读确认值（或回读失败时的乐观快照），与 DeviceData.KeyMap 一致
                _baseline = [.. confirmed];
                _edits.Clear();
                RebuildEditor();
                HintTextBlock.Text = "已保存按键配置到耳机。";
                return true;
            }
            HintTextBlock.Text = "耳机无响应，保存失败（可重试）。";
            return false;
        }
        catch (Exception exception)
        {
            ErrorLog.Write("保存按键映射失败", exception);
            HintTextBlock.Text = $"保存失败：{exception.Message}";
            return false;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    // ───────────────────────── 编辑区构建 ─────────────────────────

    /// <summary>按键分组卡片 + 出厂槽折叠区整体重建（读取 / 保存成功后调用；程序赋值不触发 dirty）。</summary>
    private void RebuildEditor()
    {
        _syncingUi = true;
        try
        {
            _functionOptions = KeyMapEditorSupport.BuildFunctionOptions(_baseline);
            var options = _functionOptions
                .Select(value => new FunctionOption(value, KeyMapEditorSupport.FunctionOptionName(value)))
                .ToList();

            KeysPanel.Children.Clear();
            _rowCombos.Clear();
            int marginIndex = 0;
            foreach (byte keyNumber in KeyMapEditorSupport.GetKeyNumbers(_baseline))
            {
                if (marginIndex > 0)
                {
                    KeysPanel.Children.Add(new Border { Height = 10 });
                }
                marginIndex++;
                KeysPanel.Children.Add(BuildKeyCard(keyNumber, options));
            }

            RebuildFactoryArea();
        }
        finally
        {
            _syncingUi = false;
        }
    }

    /// <summary>单个按键卡片：标题（按键 0xNN + 提示）+ 5 行动作（ComboBox 或「未配置」）。</summary>
    private Border BuildKeyCard(byte keyNumber, List<FunctionOption> options)
    {
        var rows = new StackPanel();
        foreach (byte action in KeyMapEditorSupport.EditableActions)
        {
            var actionLabel = new TextBlock
            {
                Text = KeyMapEditorSupport.ActionName(action),
                FontSize = (double)FindResource("Font/Size.Body"),
                Foreground = (Brush)FindResource("Brush/Text.Secondary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(actionLabel, 0);

            var valueHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(valueHost, 1);

            byte? current = KeyMapEditorSupport.GetFunction(_baseline, keyNumber, action);
            if (current is byte function)
            {
                var combo = new ComboBox
                {
                    ItemsSource = options,
                    DisplayMemberPath = nameof(FunctionOption.Name),
                    SelectedValuePath = nameof(FunctionOption.Value),
                    SelectedValue = function,
                    MinWidth = 190,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                combo.SelectionChanged += OnFunctionSelectionChanged;
                _rowCombos[new KeyMapRowKey(keyNumber, action)] = combo;
                valueHost.Children.Add(combo);
            }
            else
            {
                valueHost.Children.Add(new TextBlock
                {
                    Text = "未配置（不参与写入）",
                    FontSize = (double)FindResource("Font/Size.Caption"),
                    Foreground = (Brush)FindResource("Brush/Text.Tertiary"),
                    HorizontalAlignment = HorizontalAlignment.Right,
                });
            }

            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(actionLabel);
            row.Children.Add(valueHost);
            rows.Children.Add(row);
        }

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = KeyMapEditorSupport.KeyDisplayName(keyNumber),
            Style = (Style)FindResource("KeyTitle"),
        });
        header.Children.Add(new TextBlock
        {
            Text = "物理位置请以耳机实际按键为准。",
            Style = (Style)FindResource("NoteText"),
            Margin = new Thickness(0, 1, 0, 0),
        });

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(rows);

        return new Border
        {
            Style = (Style)FindResource("Card"),
            Child = stack,
        };
    }

    /// <summary>出厂槽折叠区（只读文本行；无出厂槽时整块折叠）。</summary>
    private void RebuildFactoryArea()
    {
        List<KeyMappingEntry> factory = KeyMapEditorSupport.FactoryEntries(_baseline);
        FactoryPanel.Children.Clear();
        FactoryExpander.Visibility = factory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FactoryHeaderText.Text = $"出厂配置（只读，{factory.Count} 条，语义未知）";
        foreach (KeyMappingEntry entry in factory)
        {
            FactoryPanel.Children.Add(new TextBlock
            {
                Text = $"{KeyMapEditorSupport.KeyDisplayName(entry.KeyNumber)} · "
                    + $"{KeyMapEditorSupport.ActionName(entry.Action)} · "
                    + $"{KeyMapEditorSupport.FunctionOptionName(entry.Function)}"
                    + $"（keyNum=0x{entry.KeyNumber:X2} action=0x{entry.Action:X2} function=0x{entry.Function:X2}）",
                Style = (Style)FindResource("NoteText"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }
    }

    // ───────────────────────── 交互事件 ─────────────────────────

    private async void OnReadClick(object sender, RoutedEventArgs eventArgs)
        => await ReadFromHeadsetAsync().ConfigureAwait(true);

    private async void OnSaveClick(object sender, RoutedEventArgs eventArgs)
        => await SaveToHeadsetAsync().ConfigureAwait(true);

    private void OnFunctionSelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (_syncingUi || sender is not ComboBox combo)
        {
            return;
        }
        KeyMapRowKey? key = _rowCombos
            .Where(pair => ReferenceEquals(pair.Value, combo))
            .Select(pair => (KeyMapRowKey?)pair.Key)
            .FirstOrDefault();
        if (key is KeyMapRowKey rowKey && combo.SelectedValue is byte function)
        {
            _edits[rowKey] = function;
            UpdateButtons();
        }
    }

    // ───────────────────────── AppState 同步（链路变化） ─────────────────────────

    private void OnAppStateChanged()
    {
        if (!_hasTable && !_busy && _app.IsLinkEstablished)
        {
            _ = ReadFromHeadsetAsync();   // 链路恢复：自动补读（失败走“读自耳机”手动重试）
        }
        else if (!_busy && !_app.IsLinkEstablished)
        {
            HintTextBlock.Text = "耳机未连接：读取与保存不可用，等待自动重连。";
        }
        UpdateEditorArea();
        UpdateButtons();
    }

    /// <summary>编辑区可用性 = 已持有整表（断连 / 读取失败 → 禁用 + 提示）。</summary>
    private void UpdateEditorArea() => EditorArea.IsEnabled = _hasTable && !_busy;

    /// <summary>按钮可用性：读 = 链路建立；保存 = 链路建立且已持有整表（编辑器入口不做写之外的事）。</summary>
    private void UpdateButtons()
    {
        bool link = _app.IsLinkEstablished;
        ReadButton.IsEnabled = !_busy && link;
        SaveButton.IsEnabled = !_busy && link && _hasTable;
    }

    // ───────────────────────── 关窗：未保存修改三选 ─────────────────────────

    protected override void OnClosing(System.ComponentModel.CancelEventArgs eventArgs)
    {
        if (_forceClose || !_hasTable || !KeyMapEditorSupport.IsDirty(_baseline, _edits))
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
            "当前按键配置有未保存到耳机的修改。\n\n是：保存并关闭\n否：放弃修改并关闭\n取消：留在编辑器",
            "按键自定义编辑器",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        switch (choice)
        {
            case MessageBoxResult.Yes:
                if (await SaveToHeadsetAsync().ConfigureAwait(true))
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
