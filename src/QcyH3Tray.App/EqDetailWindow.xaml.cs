using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QcyH3Tray.Core.Rcsp;

namespace QcyH3Tray.App;

/// <summary>
/// 均衡器 · 自定义编辑器：顶部总增益（masterGain）滑块 + 七段竖向增益滑块
/// （-12.0 ~ +12.0 dB，0.5 步进），每段实时显示 dB 值与中心频率。
/// 打开时自动读自耳机（EQ v1）；eqType / 每段 Q 保持读到的值不变。
/// 读自耳机 / 应用到耳机 / 重置 三个按钮；窗口失焦不自动关闭。
/// </summary>
public sealed partial class EqDetailWindow : Window
{
    /// <summary>七段中心频率短标签（完整频率见 <see cref="EqV1BandMap.Frequencies"/>）。</summary>
    private static readonly string[] FrequencyLabels = ["55", "220", "500", "1k", "1.8k", "2.8k", "7.5k"];

    /// <summary>未知 Q 时的默认值（真机基线 Q=1.20，与 Quicky 实现一致）。</summary>
    private const int DefaultQ = 120;

    private readonly AppState _app;
    private readonly Slider[] _bandSliders;
    private readonly TextBlock[] _bandValueTexts;
    private readonly int[] _bandQs = new int[EqV1BandMap.SevenBandCount];

    private byte _eqType;                  // 应用时保持读到的值不变
    private bool _busy;

    internal EqDetailWindow(AppState app)
    {
        InitializeComponent();
        _app = app;
        _eqType = 0x02;                    // 真机基线；读取成功后被覆盖
        Array.Fill(_bandQs, DefaultQ);

        (_bandSliders, _bandValueTexts) = BuildBandEditors();
        UpdateGainLabels();

        Loaded += async (_, _) => await ReadFromHeadsetAsync();
    }

    /// <summary>生成七列：dB 数值 / 竖向滑块 / 频率标签。</summary>
    private (Slider[] Sliders, TextBlock[] ValueTexts) BuildBandEditors()
    {
        var sliders = new Slider[EqV1BandMap.SevenBandCount];
        var valueTexts = new TextBlock[EqV1BandMap.SevenBandCount];

        for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
        {
            var valueText = new TextBlock
            {
                Text = "0.0 dB",
                FontSize = 10.5,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4D4D4D")),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                TickFrequency = 0.5,
                IsSnapToTickEnabled = true,
                IsMoveToPointEnabled = true,
                Height = 132,
                Margin = new Thickness(0, 6, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Style = (Style)FindResource("EqSliderVertical"),
            };
            int index = i;
            slider.ValueChanged += (_, _) => UpdateGainLabels();

            var frequencyText = new TextBlock
            {
                Text = FrequencyLabels[i],
                FontSize = 10,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A8A8A")),
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var column = new StackPanel();
            column.Children.Add(valueText);
            column.Children.Add(slider);
            column.Children.Add(frequencyText);
            BandGrid.Children.Add(column);

            sliders[i] = slider;
            valueTexts[i] = valueText;
        }
        return (sliders, valueTexts);
    }

    private void OnGainSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
        => UpdateGainLabels();

    private void UpdateGainLabels()
    {
        MasterGainTextBlock.Text = FormatDb(MasterGainSlider.Value);
        for (int i = 0; i < _bandSliders.Length; i++)
        {
            _bandValueTexts[i].Text = FormatDb(_bandSliders[i].Value);
        }
    }

    private static string FormatDb(double value) => value.ToString("+0.0;-0.0;0.0") + " dB";

    // ───────────────────────── 读 / 写 / 重置 ─────────────────────────

    private async void OnReadClick(object sender, RoutedEventArgs eventArgs)
        => await ReadFromHeadsetAsync();

    /// <summary>读自耳机（EQ v1）并刷新滑块。打开窗口时也自动调用。</summary>
    private async Task ReadFromHeadsetAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        ReadButton.IsEnabled = ApplyButton.IsEnabled = false;
        HintTextBlock.Text = "正在读取耳机 EQ…";
        try
        {
            EqV1Info? info = await _app.GetEqV1Async().ConfigureAwait(true);
            if (info is null)
            {
                HintTextBlock.Text = _app.IsConnected
                    ? "耳机未返回 EQ 数据。"
                    : "未连接耳机，无法读取。";
                return;
            }

            _eqType = info.EqType;
            SyncSliders(info);
            UpdateGainLabels();
            HintTextBlock.Text = "已读取当前曲线。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write("读取 EQ 失败", exception);
            HintTextBlock.Text = $"读取失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
            ReadButton.IsEnabled = ApplyButton.IsEnabled = true;
        }
    }

    private void SyncSliders(EqV1Info info)
    {
        for (int i = 0; i < _bandSliders.Length; i++)
        {
            if (i < info.Bands.Count)
            {
                _bandSliders[i].Value = Math.Clamp(info.Bands[i].Gain / 100.0, -12, 12);
                _bandQs[i] = info.Bands[i].Q;
            }
        }
        MasterGainSlider.Value = Math.Clamp(info.MasterGain / 100.0, -12, 12);
    }

    private void OnResetClick(object sender, RoutedEventArgs eventArgs)
    {
        MasterGainSlider.Value = 0;
        foreach (Slider slider in _bandSliders)
        {
            slider.Value = 0;
        }
        UpdateGainLabels();
        HintTextBlock.Text = "已重置为全零（尚未应用到耳机）。";
    }

    private async void OnApplyClick(object sender, RoutedEventArgs eventArgs)
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        ApplyButton.IsEnabled = false;
        HintTextBlock.Text = "正在应用到耳机…";
        try
        {
            int masterGain = (int)Math.Round(MasterGainSlider.Value * 100);
            var bands = new List<EqV1Band>(EqV1BandMap.SevenBandCount);
            for (int i = 0; i < EqV1BandMap.SevenBandCount; i++)
            {
                int gain = (int)Math.Round(_bandSliders[i].Value * 100);
                bands.Add(new EqV1Band(EqV1BandMap.Frequencies[i], gain, _bandQs[i]));
            }

            bool ok = await _app.RunWriteCommandAsync("EQ·自定义", async () =>
                await _app.Manager.SetEqV1Async(_eqType, masterGain, bands).ConfigureAwait(false) is not null)
                .ConfigureAwait(true);
            HintTextBlock.Text = ok
                ? "已应用到耳机。"
                : "耳机无响应，应用失败（可重试）。";
        }
        catch (Exception exception)
        {
            ErrorLog.Write("应用 EQ 失败", exception);
            HintTextBlock.Text = $"应用失败：{exception.Message}";
        }
        finally
        {
            _busy = false;
            ApplyButton.IsEnabled = true;
        }
    }
}
