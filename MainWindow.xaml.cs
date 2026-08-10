using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NRftWManagerUI.Core;

namespace NRftWManagerUI;

public partial class MainWindow : Window
{
    private const string ItemQuantityPattern = "89 3B 0F 94 C0 EB ??";
    private const string CurrencyPattern = "45 01 75 04 45 33 C9";

    private readonly Dictionary<string, FrameworkElement> _views;
    private readonly Dictionary<string, (string Title, string Subtitle)> _pageText = new()
    {
        ["overview"] = ("概览", "连接游戏后核对已验证特征码，再逐项启用。"),
        ["items"] = ("物品数量", "捕获最后一次变化的可堆叠材料，再写入该堆数量。"),
        ["equipment"] = ("装备与词条", "品质与词条地址尚待定位；请用字段探测器逐项验证。"),
        ["character"] = ("角色", "货币有已验证定位；属性需要先探测实际字段。"),
        ["detector"] = ("字段探测器", "用游戏内的一次确定数值变化筛选候选内存字段。"),
        ["loadouts"] = ("配置与快照", "等待物品实例结构定位后再实现外置创建与复制。"),
        ["logs"] = ("活动记录", "记录连接、扫描、捕获和写入动作。")
    };

    private GameSession? _session;
    private RemoteCaptureHook? _itemCapture;
    private RemoteCaptureHook? _currencyCapture;
    private ValueChangeDetector? _detector;
    private long? _itemQuantityAddress;
    private long? _currencyAddress;
    private long? _itemQuantitySite;
    private long? _currencySite;
    private string _selectedCurrency = "Gold";

    public MainWindow()
    {
        InitializeComponent();
        _views = new Dictionary<string, FrameworkElement>
        {
            ["overview"] = OverviewView,
            ["items"] = ItemsView,
            ["equipment"] = EquipmentView,
            ["character"] = CharacterView,
            ["detector"] = DetectorView,
            ["loadouts"] = LoadoutsView,
            ["logs"] = LogsView
        };

        Closed += (_, _) => Disconnect();
        AddLog("界面启动：尚未连接游戏。不会安装或加载任何游戏模组。");
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
        {
            ShowPage(page);
        }
    }

    private void ShowPage(string page)
    {
        foreach (var view in _views)
        {
            view.Value.Visibility = view.Key == page ? Visibility.Visible : Visibility.Collapsed;
        }

        var text = _pageText[page];
        PageTitle.Text = text.Title;
        PageSubtitle.Text = text.Subtitle;
    }

    private void OnConnectGame(object sender, RoutedEventArgs e)
    {
        Disconnect();
        _session = new GameSession();
        var result = _session.Attach();

        if (!_session.IsAttached)
        {
            _session.Dispose();
            _session = null;
            SetStatus(result);
            return;
        }

        RuntimeStateText.Text = "已连接";
        RuntimeDetailText.Text = result;
        ConnectionText.Text = "已连接游戏";
        SetStatus(result);
        AddLog("请点击“检查已知定位”。特征码唯一匹配前，写入功能保持锁定。");
    }

    private void OnScanKnownSignatures(object sender, RoutedEventArgs e)
    {
        if (!RequireSession())
        {
            return;
        }

        try
        {
            SetStatus("正在扫描 GameAssembly.dll 中已验证的数量与货币特征码…");
            var itemMatches = _session!.ScanGameAssembly(AobPattern.Parse(ItemQuantityPattern));
            var currencyMatches = _session.ScanGameAssembly(AobPattern.Parse(CurrencyPattern));

            _itemQuantitySite = itemMatches.Count == 1 ? itemMatches[0] : null;
            _currencySite = currencyMatches.Count == 1 ? currencyMatches[0] : null;

            var summary = $"材料数量：{itemMatches.Count} 个匹配；货币：{currencyMatches.Count} 个匹配。";
            SignatureStateText.Text = _itemQuantitySite.HasValue && _currencySite.HasValue ? "已验证" : "不匹配";
            SignatureDetailText.Text = summary;

            if (_itemQuantitySite.HasValue && _currencySite.HasValue)
            {
                WriteStateText.Text = "可捕获";
                WriteStateDetailText.Text = "可先捕获目标地址；捕获后开启该项写入。";
                SetStatus($"{summary} 当前游戏版本可验证。先从材料数量开始。", true);
            }
            else
            {
                WriteStateText.Text = "已锁定";
                WriteStateDetailText.Text = "游戏更新后特征码可能已变化；请不要尝试写入。";
                SetStatus($"{summary} 未通过唯一匹配检查，因此本程序不会写入游戏。", false);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"特征码扫描失败：{exception.Message}", false);
        }
    }

    private void OnStartItemCapture(object sender, RoutedEventArgs e)
    {
        if (!RequireKnownSite(_itemQuantitySite, "物品数量"))
        {
            return;
        }

        _itemCapture?.Dispose();
        _itemQuantityAddress = null;
        try
        {
            _itemCapture = new RemoteCaptureHook(
                _session!,
                _itemQuantitySite!.Value,
                5,
                CaptureRegister.Rbx,
                0,
                KeepLastItemCheck.IsChecked == true);
            var result = _itemCapture.Arm();
            ItemCaptureText.Text = result;
            SetStatus(result);
        }
        catch (Exception exception)
        {
            _itemCapture = null;
            SetStatus($"无法开始材料捕获：{exception.Message}", false);
        }
    }

    private void OnReadItemCapture(object sender, RoutedEventArgs e)
    {
        if (_itemCapture is null || !_itemCapture.TryReadCapturedAddress(out var address))
        {
            SetStatus("尚未捕获材料地址。开始捕获后，回游戏卖出或使用目标材料 1 个，再重试。", false);
            return;
        }

        _itemQuantityAddress = address;
        var valueText = _session!.TryReadInt32(address, out var current) ? current.ToString() : "读取失败";
        if (_itemCapture.PreventsZeroQuantity)
        {
            ItemCaptureText.Text = $"已捕获材料数量地址：0x{address:X}，当前值：{valueText}。最后 1 个保留仍已开启；关闭程序或点“停止材料保护”才会恢复正常删除。";
        }
        else
        {
            _itemCapture.Dispose();
            _itemCapture = null;
            ItemCaptureText.Text = $"已捕获材料数量地址：0x{address:X}，当前值：{valueText}。";
        }
        SetStatus(ItemCaptureText.Text, true);
    }

    private void OnStopItemCapture(object sender, RoutedEventArgs e)
    {
        if (_itemCapture is null)
        {
            SetStatus("材料保护当前没有开启。", false);
            return;
        }

        _itemCapture.Dispose();
        _itemCapture = null;
        ItemCaptureText.Text = "材料保护已停止：游戏恢复正常删除逻辑。已捕获的数量地址仍可用于当前会话写入。";
        SetStatus(ItemCaptureText.Text, true);
    }

    private void OnWriteItemQuantity(object sender, RoutedEventArgs e)
    {
        if (!TryGetWriteTarget(_itemQuantityAddress, ItemQuantityBox.Text, 1, 9999, out var address, out var value))
        {
            return;
        }

        WriteValue(address, value, "材料数量");
    }

    private void OnCurrencyClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string currency })
        {
            _selectedCurrency = currency;
            CurrencyCaptureText.Text = $"已选择 {currency}。开始捕获后，请在游戏内让该货币增减一次。";
            SetStatus(CurrencyCaptureText.Text);
        }
    }

    private void OnStartCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (!RequireKnownSite(_currencySite, "货币"))
        {
            return;
        }

        _currencyCapture?.Dispose();
        _currencyAddress = null;
        _currencyCapture = new RemoteCaptureHook(_session!, _currencySite!.Value, 7, CaptureRegister.R13, 4);
        var result = _currencyCapture.Arm();
        CurrencyCaptureText.Text = $"{_selectedCurrency} 自动定位：{result}";
        SetStatus(CurrencyCaptureText.Text);
    }

    private void OnReadCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (_currencyCapture is null || !_currencyCapture.TryReadCapturedAddress(out var address))
        {
            SetStatus("尚未捕获货币地址。开始捕获后，在游戏里让所选货币增减一次，再重试。", false);
            return;
        }

        _currencyAddress = address;
        var valueText = _session!.TryReadInt32(address, out var current) ? current.ToString() : "读取失败";
        _currencyCapture.Dispose();
        _currencyCapture = null;
        CurrencyCaptureText.Text = $"已捕获 {_selectedCurrency} 地址：0x{address:X}，当前值：{valueText}。";
        SetStatus(CurrencyCaptureText.Text, true);
    }

    private void OnWriteCurrency(object sender, RoutedEventArgs e)
    {
        if (!TryGetWriteTarget(_currencyAddress, CurrencyValueBox.Text, 0, 9_999_999, out var address, out var value))
        {
            return;
        }

        WriteValue(address, value, _selectedCurrency);
    }

    private async void OnStartDetector(object sender, RoutedEventArgs e)
    {
        if (!RequireSession() || !TryParseInt32(DetectorBeforeBox.Text, out var value))
        {
            SetStatus("请填入游戏内当前显示的整数。", false);
            return;
        }

        _detector = new ValueChangeDetector(_session!);
        DetectorStatusText.Text = "正在初始扫描…大内存游戏可能需要一两分钟，请勿关闭游戏。";
        try
        {
            var result = await Task.Run(() => _detector.Start(value));
            DetectorStatusText.Text = result;
            DetectorResults.Items.Clear();
            SetStatus(result);
        }
        catch (Exception exception)
        {
            DetectorStatusText.Text = $"初始扫描失败：{exception.Message}";
            SetStatus(DetectorStatusText.Text, false);
        }
    }

    private async void OnFilterDetector(object sender, RoutedEventArgs e)
    {
        if (_detector is null)
        {
            SetStatus("请先执行“初始扫描”。", false);
            return;
        }

        if (!TryParseInt32(DetectorAfterBox.Text, out var value))
        {
            SetStatus("请填入变化后的游戏内整数。", false);
            return;
        }

        DetectorStatusText.Text = "正在筛选候选地址…";
        try
        {
            var result = await Task.Run(() => _detector.Filter(value));
            DetectorStatusText.Text = result;
            DetectorResults.Items.Clear();
            foreach (var line in _detector.BuildReport().Split(Environment.NewLine))
            {
                DetectorResults.Items.Add(line);
            }

            SetStatus(result);
        }
        catch (Exception exception)
        {
            DetectorStatusText.Text = $"筛选失败：{exception.Message}";
            SetStatus(DetectorStatusText.Text, false);
        }
    }

    private void OnCopyDetectorReport(object sender, RoutedEventArgs e)
    {
        if (_detector is null)
        {
            SetStatus("没有可复制的探测结果。", false);
            return;
        }

        Clipboard.SetText(_detector.BuildReport());
        SetStatus("候选地址已复制。把这段内容发给我，我会判断下一轮怎么继续缩小范围。", true);
    }

    private void OnOpenDetector(object sender, RoutedEventArgs e)
    {
        ShowPage("detector");
    }

    private void OnRarityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rarity })
        {
            return;
        }

        RarityHint.Text = rarity switch
        {
            "Common" => "Common：先用字段探测器确认稀有度整数值。",
            "Magical" => "Magical：请对一件不重要蓝装做字段探测。",
            "Plagued" => "Plagued：先定位品质字段及附魔数量字段，不能只改颜色。",
            "Gold" => "Gold：仅作为原生目录武器的验证目标。",
            _ => "尚未选择品质。"
        };
        SetStatus($"已选择 {rarity} 作为下一项字段探测目标。");
    }

    private void OnPreviewAction(object sender, RoutedEventArgs e)
    {
        SetStatus("这个功能尚未完成字段定位，因此不会执行写入。请先用字段探测器取得候选结果。", false);
    }

    private bool RequireSession()
    {
        if (_session is not { IsAttached: true })
        {
            SetStatus("请先在概览页连接游戏。", false);
            return false;
        }

        return true;
    }

    private bool RequireKnownSite(long? site, string feature)
    {
        if (!RequireSession())
        {
            return false;
        }

        if (!site.HasValue)
        {
            SetStatus($"{feature}特征码尚未唯一匹配。请先在概览页点击“检查已知定位”。", false);
            return false;
        }

        return true;
    }

    private bool TryGetWriteTarget(long? storedAddress, string valueText, int minimum, int maximum, out long address, out int value)
    {
        address = 0;
        value = 0;
        if (!RequireSession())
        {
            return false;
        }

        if (!storedAddress.HasValue)
        {
            SetStatus("请先捕获目标地址；程序不会猜测或写入未知地址。", false);
            return false;
        }

        if (!TryParseInt32(valueText, out value) || value < minimum || value > maximum)
        {
            SetStatus($"请输入 {minimum:N0} 至 {maximum:N0} 的整数。", false);
            return false;
        }

        address = storedAddress.Value;
        return true;
    }

    private void WriteValue(long address, int value, string feature)
    {
        var current = _session!.TryReadInt32(address, out var before) ? before.ToString() : "读取失败";
        if (!_session.WriteInt32(address, value))
        {
            SetStatus($"写入 {feature} 失败；游戏可能已切换场景或退出。", false);
            return;
        }

        var after = _session.TryReadInt32(address, out var readBack) ? readBack.ToString() : "读取失败";
        SetStatus($"已写入 {feature}：{current} → {after}。回游戏关闭并重新打开背包/角色页确认。", true);
    }

    private static bool TryParseInt32(string text, out int value)
    {
        return int.TryParse(text.Trim(), out value);
    }

    private void Disconnect()
    {
        _itemCapture?.Dispose();
        _currencyCapture?.Dispose();
        _itemCapture = null;
        _currencyCapture = null;
        _session?.Dispose();
        _session = null;
    }

    private void SetStatus(string message, bool success = false)
    {
        ActionStatusText.Text = message;
        if (success)
        {
            AddLog(message);
            return;
        }

        AddLog(message);
    }

    private void AddLog(string message)
    {
        ActivityLog.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
    }
}
