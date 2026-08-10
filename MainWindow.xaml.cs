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
    private const string WalletContextPattern = "48 89 5C 24 08 56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string WalletContextEntry = "48 89 5C 24 08 56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string CurrentGameAssemblySha256 = "5B00EE90833B1BE2EA73E01CB83E710E09E86199D12AC769BE3C0E82ADD8B4BB";
    private const int GetInventoryComponentRva = 0x5D7F970;

    private readonly Dictionary<string, FrameworkElement> _views;
    private readonly Dictionary<string, (string Title, string Subtitle)> _pageText = new()
    {
        ["overview"] = ("概览", "连接游戏后核对已验证特征码，再逐项启用。"),
        ["items"] = ("物品数量", "捕获最后一次变化的可堆叠材料，再写入该堆数量。"),
        ["equipment"] = ("装备与词条", "品质与词条地址尚待定位；请用字段探测器逐项验证。"),
        ["character"] = ("角色", "当前版本的钱包可自动定位；属性仍需要先探测实际字段。"),
        ["detector"] = ("字段探测器", "用游戏内的一次确定数值变化筛选候选内存字段。"),
        ["loadouts"] = ("配置与快照", "等待物品实例结构定位后再实现外置创建与复制。"),
        ["logs"] = ("活动记录", "记录连接、扫描、捕获和写入动作。")
    };

    private GameSession? _session;
    private RemoteCaptureHook? _itemCapture;
    private RemoteWalletContextHook? _currencyCapture;
    private ValueChangeDetector? _detector;
    private long? _itemQuantityAddress;
    private long? _currencyAddress;
    private long? _itemQuantitySite;
    private long? _walletContextSite;
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
            SetStatus("正在扫描 GameAssembly.dll 中已验证的数量与自动钱包定位入口…");
            var itemMatches = _session!.ScanGameAssembly(AobPattern.Parse(ItemQuantityPattern));
            var walletProfileMatches = _session.HasGameAssemblyHash(CurrentGameAssemblySha256)
                ? _session.ScanGameAssembly(AobPattern.Parse(WalletContextPattern))
                : Array.Empty<long>();

            _itemQuantitySite = itemMatches.Count == 1 ? itemMatches[0] : null;
            _walletContextSite = walletProfileMatches.Count == 1 ? walletProfileMatches[0] : null;

            var summary = $"材料数量：{itemMatches.Count} 个匹配；自动钱包入口：{walletProfileMatches.Count} 个匹配。";
            SignatureStateText.Text = _itemQuantitySite.HasValue && _walletContextSite.HasValue ? "已验证" : "不匹配";
            SignatureDetailText.Text = summary;

            if (_itemQuantitySite.HasValue && _walletContextSite.HasValue)
            {
                WriteStateText.Text = "可捕获";
                WriteStateDetailText.Text = "材料需捕获；钱包可自动定位后直接写入。";
                SetStatus($"{summary} 当前游戏版本可验证。钱包无需先让货币变化。", true);
            }
            else
            {
                WriteStateText.Text = "已锁定";
                WriteStateDetailText.Text = "游戏更新或文件不匹配；请不要尝试写入。";
                SetStatus($"{summary} 未通过当前版本的唯一匹配检查，因此本程序不会写入游戏。", false);
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
            CurrencyCaptureText.Text = currency == "Gloamseed"
                ? "Gloamseed 使用单独的地牢货币 API；当前自动钱包定位不写入它。"
                : $"已选择 {currency}。自动定位后可直接写入；无需让货币变化。";
            SetStatus(CurrencyCaptureText.Text);
        }
    }

    private void OnStartCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (_selectedCurrency == "Gloamseed")
        {
            SetStatus("Gloamseed 不是普通钱包字段，当前版本暂不写入它。", false);
            return;
        }

        if (!RequireKnownSite(_walletContextSite, "自动钱包"))
        {
            return;
        }

        _currencyCapture?.Dispose();
        _currencyAddress = null;
        try
        {
            _currencyCapture = new RemoteWalletContextHook(
                _session!,
                _walletContextSite!.Value,
                _session.GameAssemblyBase + GetInventoryComponentRva,
                AobPattern.Parse(WalletContextEntry).Bytes);
            var result = _currencyCapture.Arm();
            CurrencyCaptureText.Text = $"{_selectedCurrency} 自动定位：{result}";
            SetStatus(CurrencyCaptureText.Text);
        }
        catch (Exception exception)
        {
            _currencyCapture = null;
            SetStatus($"无法开始自动钱包定位：{exception.Message}", false);
        }
    }

    private void OnReadCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (_currencyCapture is null || !_currencyCapture.TryReadWalletAddress(out var address))
        {
            SetStatus("自动钱包定位尚未取得有效组件。请进入已加载的角色画面，停留约 1 秒后重试；无需让货币变化。", false);
            return;
        }

        _currencyAddress = address;
        var valueText = _session!.TryReadInt32(address, out var current) ? current.ToString() : "读取失败";
        CurrencyCaptureText.Text = $"已自动定位 {_selectedCurrency} 钱包：0x{address:X}，内部基数：{valueText}。定位保持开启，以跟随当前游戏帧。";
        SetStatus(CurrencyCaptureText.Text, true);
    }

    private void OnWriteCurrency(object sender, RoutedEventArgs e)
    {
        if (_selectedCurrency == "Gloamseed")
        {
            SetStatus("Gloamseed 不是普通钱包字段，当前版本暂不写入它。", false);
            return;
        }

        if (!TryGetWriteTarget(_currencyAddress, CurrencyValueBox.Text, 0, 9_999_999, out var address, out var value))
        {
            return;
        }

        var multiplier = _selectedCurrency switch
        {
            "Copper" => 1,
            "Silver" => 100,
            "Gold" => 10_000,
            _ => 1
        };

        if ((long)value * multiplier > int.MaxValue)
        {
            SetStatus($"{_selectedCurrency} 数值过大，换算为内部基数后超出游戏允许范围。", false);
            return;
        }

        WriteValue(address, value * multiplier, _selectedCurrency);
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
