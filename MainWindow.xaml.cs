using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using NRftWManagerUI.Core;

namespace NRftWManagerUI;

public partial class MainWindow : Window
{
    private const string ItemQuantityPattern = "89 3B 0F 94 C0 EB ??";
    private const string PlayerContextPattern = "48 89 5C 24 08 56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string PlayerContextEntry = "48 89 5C 24 08 56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string ItemSelectionPattern = "40 53 48 83 EC 20 48 8B D9 48 85 C9 74 5C";
    private const string ItemSelectionEntry = "40 53 48 83 EC 20 48 8B D9 48 85 C9";
    private const string CurrentGameAssemblySha256 = "5B00EE90833B1BE2EA73E01CB83E710E09E86199D12AC769BE3C0E82ADD8B4BB";
    private const int GetInventoryComponentRva = 0x5D7F970;
    private const int GetHeroComponentRva = 0x5B32560;
    private const int GetHeroStatsRva = 0x5E02EC0;
    private const int LevelUpRva = 0x5DF50C0;
    private const int ChangeItemRarityRva = 0x5D66CF0;
    private const int AddItemEnchantmentRva = 0x5D6B250;
    private const int DuplicateItemRva = 0x5D884D0;
    private const int AddItemToInventoryRva = 0x5D792F0;
    private const int RepairItemRva = 0x5D617E0;
    private const int GetItemDataRva = 0x5D874F0;
    private const int CreateItemRva = 0x5D863B0;
    private const int SetItemLevelRva = 0x5D87920;
    private const int AwardGloamseedRva = 0x5F4BE60;
    private const int GiveMaxStatsRva = 0x5E00240;
    private const int UnlockFastTravelRva = 0x5F48E60;

    private GameSession? _session;
    private RemoteCaptureHook? _itemCapture;
    private RemotePlayerContextHook? _playerContextHook;
    private RemoteItemSelectionHook? _itemSelectionHook;
    private ValueChangeDetector? _detector;
    private PlayerRuntimeContext? _playerContext;
    private long? _itemQuantityAddress;
    private long? _currencyAddress;
    private long? _itemQuantitySite;
    private long? _playerContextSite;
    private long? _itemSelectionSite;
    private long? _selectedItemEntity;
    private string _selectedCurrency = "Gold";

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => Disconnect();
        AddLog("界面启动：尚未连接游戏。不会安装或加载任何游戏模组。");
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
        AddLog("已连接游戏，正在自动检查当前版本。角色定位仍需由你手动开启。");
        OnScanKnownSignatures(this, new RoutedEventArgs());
    }

    private void OnScanKnownSignatures(object sender, RoutedEventArgs e)
    {
        if (!RequireSession())
        {
            return;
        }

        try
        {
            SetStatus("正在扫描 GameAssembly.dll 中已验证的材料、角色上下文与物品详情入口…");
            var itemMatches = _session!.ScanGameAssembly(AobPattern.Parse(ItemQuantityPattern));
            var profileMatches = _session.HasGameAssemblyHash(CurrentGameAssemblySha256)
                ? _session.ScanGameAssembly(AobPattern.Parse(PlayerContextPattern))
                : Array.Empty<long>();
            var itemSelectionMatches = _session.HasGameAssemblyHash(CurrentGameAssemblySha256)
                ? _session.ScanGameAssembly(AobPattern.Parse(ItemSelectionPattern))
                : Array.Empty<long>();

            _itemQuantitySite = itemMatches.Count == 1 ? itemMatches[0] : null;
            _playerContextSite = profileMatches.Count == 1 ? profileMatches[0] : null;
            _itemSelectionSite = itemSelectionMatches.Count == 1 ? itemSelectionMatches[0] : null;

            var summary = $"材料数量：{itemMatches.Count} 个匹配；角色入口：{profileMatches.Count} 个匹配；物品详情：{itemSelectionMatches.Count} 个匹配。";
            SignatureStateText.Text = _itemQuantitySite.HasValue && _playerContextSite.HasValue && _itemSelectionSite.HasValue ? "已验证" : "不匹配";
            SignatureDetailText.Text = summary;

            if (_itemQuantitySite.HasValue && _playerContextSite.HasValue && _itemSelectionSite.HasValue)
            {
                WriteStateText.Text = "可捕获";
                WriteStateDetailText.Text = "角色、钱包和物品均通过当前版本入口校验。";
                SetStatus($"{summary} 当前游戏版本可验证。钱包和角色字段无需先让数值变化。", true);
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
                ? "已选择 Gloamseed（输入值为增加量）。"
                : $"已选择 {currency}。";
            SetStatus(CurrencyCaptureText.Text);
        }
    }

    private void OnStartCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (!RequireKnownSite(_playerContextSite, "角色上下文"))
        {
            return;
        }

        _playerContextHook?.Dispose();
        _playerContext = null;
        _currencyAddress = null;
        try
        {
            _playerContextHook = new RemotePlayerContextHook(
                _session!,
                _playerContextSite!.Value,
                AobPattern.Parse(PlayerContextEntry).Bytes.Select(value => value ?? throw new InvalidOperationException("角色入口签名不能包含通配符。")).ToArray(),
                _session.GameAssemblyBase + GetInventoryComponentRva,
                _session.GameAssemblyBase + GetHeroComponentRva,
                _session.GameAssemblyBase + GetHeroStatsRva,
                _session.GameAssemblyBase + LevelUpRva,
                _session.GameAssemblyBase + ChangeItemRarityRva,
                _session.GameAssemblyBase + AddItemEnchantmentRva,
                _session.GameAssemblyBase + DuplicateItemRva,
                _session.GameAssemblyBase + AddItemToInventoryRva,
                _session.GameAssemblyBase + RepairItemRva,
                _session.GameAssemblyBase + GetItemDataRva,
                _session.GameAssemblyBase + CreateItemRva,
                _session.GameAssemblyBase + SetItemLevelRva,
                _session.GameAssemblyBase + AwardGloamseedRva,
                _session.GameAssemblyBase + GiveMaxStatsRva,
                _session.GameAssemblyBase + UnlockFastTravelRva);
            var result = _playerContextHook.Arm();
            CurrencyCaptureText.Text = result;
            SetStatus(CurrencyCaptureText.Text);
        }
        catch (Exception exception)
        {
            _playerContextHook = null;
            SetStatus($"无法开始角色上下文定位：{exception.Message}", false);
        }
    }

    private void OnReadCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (!TryRefreshPlayerContext(out var context))
        {
            SetStatus("角色上下文尚未取得有效组件。请进入已加载的角色画面，停留约 1 秒后重试；无需让货币变化。", false);
            return;
        }

        _currencyAddress = context.InventoryComponent + sizeof(int);
        var valueText = _session!.TryReadInt32(_currencyAddress.Value, out var current) ? current.ToString() : "读取失败";
        CurrencyCaptureText.Text = $"角色上下文已就绪 · {_selectedCurrency} 当前基数：{valueText}";
        SetStatus(CurrencyCaptureText.Text, true);
    }

    private void OnWriteCurrency(object sender, RoutedEventArgs e)
    {
        if (_selectedCurrency == "Gloamseed")
        {
            if (!TryParseInt32(CurrencyValueBox.Text, out var gloamseed) || gloamseed is < 1 or > 9_999_999 ||
                _playerContextHook is null || !TryRefreshPlayerContext(out _))
            {
                SetStatus("Gloamseed 请输入 1 至 9,999,999，并先完成角色上下文定位。", false);
                return;
            }

            QueuePlayerCommand(PlayerCommand.AwardGloamseed, gloamseed, 0, "Gloamseed");
            return;
        }

        if (!TryRefreshPlayerContext(out var context) ||
            !TryGetWriteTarget(context.InventoryComponent + sizeof(int), CurrencyValueBox.Text, 0, 9_999_999, out var address, out var value))
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

    private void OnStartItemSelection(object sender, RoutedEventArgs e)
    {
        if (!RequireKnownSite(_itemSelectionSite, "物品详情"))
        {
            return;
        }

        _itemSelectionHook?.Dispose();
        _selectedItemEntity = null;
        try
        {
            _itemSelectionHook = new RemoteItemSelectionHook(
                _session!,
                _itemSelectionSite!.Value,
                AobPattern.Parse(ItemSelectionEntry).Bytes.Select(value => value ?? throw new InvalidOperationException("物品详情入口签名不能包含通配符。")).ToArray());
            var result = _itemSelectionHook.Arm();
            SelectedItemText.Text = result;
            SetStatus(result);
        }
        catch (Exception exception)
        {
            _itemSelectionHook = null;
            SetStatus($"无法开始物品选择捕获：{exception.Message}", false);
        }
    }

    private void OnReadItemSelection(object sender, RoutedEventArgs e)
    {
        if (_itemSelectionHook is null || !_itemSelectionHook.TryReadSelection(out _, out var itemEntity))
        {
            SetStatus("尚未捕获物品。请先开启物品选择捕获，再回游戏打开背包并点击目标物品详情。", false);
            return;
        }

        _selectedItemEntity = itemEntity;
        var linked = _playerContextHook?.SetSelectedItem(itemEntity) == true;
        SelectedItemText.Text = linked
            ? $"已选中当前物品：0x{itemEntity:X}。可执行原生改品质、加词条、耐久修复、复制或按该物品模板创建。"
            : $"已捕获物品：0x{itemEntity:X}。请先在“角色”页开启角色上下文定位，再执行物品操作。";
        SetStatus(SelectedItemText.Text, linked);
    }

    private void OnQueueRarity(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rarity } || !TryMapRarity(rarity, out var value) || !RequireSelectedItem())
        {
            return;
        }

        QueuePlayerCommand(PlayerCommand.ChangeSelectedItemRarity, value, 0, $"{rarity} 品质");
    }

    private void OnAddSelectedItemEnchantment(object sender, RoutedEventArgs e)
    {
        if (RequireSelectedItem())
        {
            QueuePlayerCommand(PlayerCommand.AddSelectedItemEnchantment, 0, 0, "随机新增词条");
        }
    }

    private void OnRepairSelectedItem(object sender, RoutedEventArgs e)
    {
        if (RequireSelectedItem())
        {
            QueuePlayerCommand(PlayerCommand.RepairSelectedItem, 0, 0, "修满选中物品耐久");
        }
    }

    private void OnDuplicateSelectedItem(object sender, RoutedEventArgs e)
    {
        if (RequireSelectedItem())
        {
            QueuePlayerCommand(PlayerCommand.DuplicateSelectedItem, 0, 0, "复制选中物品");
        }
    }

    private void OnCreateSelectedItem(object sender, RoutedEventArgs e)
    {
        if (!RequireSelectedItem())
        {
            return;
        }

        if (!TryParseInt32(CreateItemCountBox.Text, out var count) || count is < 1 or > 9999)
        {
            SetStatus("创建数量请输入 1 至 9,999。", false);
            return;
        }

        QueuePlayerCommand(PlayerCommand.CreateSelectedItem, count, CreateItemRarityBox.SelectedIndex, "以选中物品模板创建");
    }

    private void OnSetSelectedItemLevel(object sender, RoutedEventArgs e)
    {
        if (!RequireSelectedItem())
        {
            return;
        }

        if (!TryParseInt32(ItemLevelBox.Text, out var level) || level is < 1 or > 100)
        {
            SetStatus("物品等级请输入 1 至 100。", false);
            return;
        }

        QueuePlayerCommand(PlayerCommand.SetSelectedItemLevel, level, 0, "选中物品等级");
    }

    private void OnTogglePlayerFlag(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string feature, IsChecked: bool enabled } || !TryRefreshPlayerContext(out var context))
        {
            return;
        }

        switch (feature)
        {
            case "health":
                WriteRuntimeFlag(context.HealthComponent + 4, enabled, "无限生命");
                break;
            case "stamina":
                WriteRuntimeFlag(context.StaminaComponent, enabled, "无限耐力");
                break;
            case "focus":
                WriteRuntimeFlag(context.FocusComponent, enabled, "无限专注");
                break;
            case "instagib":
                WriteHeroDebugFlag(context, 8, enabled, "一击必杀");
                break;
            case "vendorCheat":
                WriteHeroDebugFlag(context, 1, enabled, "免费商店（游戏原生 VendorCheat）");
                break;
            case "ignoreRequirements":
                WriteHeroDebugFlag(context, 32, enabled, "忽略装备需求");
                break;
        }
    }

    private void OnWriteHeroMultiplier(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string feature } || !TryRefreshPlayerContext(out var context))
        {
            return;
        }

        var input = feature == "movement" ? MoveSpeedBox.Text : ExperienceMultiplierBox.Text;
        var maximum = feature == "movement" ? 5d : 100d;
        if (!double.TryParse(input, out var multiplier) || multiplier < 0.1d || multiplier > maximum)
        {
            SetStatus($"{(feature == "movement" ? "移动" : "经验")}倍率请输入 0.1 至 {maximum:0}。", false);
            return;
        }

        var address = context.HeroComponent + (feature == "movement" ? 0x60 : 0x40);
        var raw = checked((long)Math.Round(multiplier * 65_536d));
        if (!_session!.WriteInt64(address, raw))
        {
            SetStatus("写入角色倍率失败；游戏可能已切换场景。", false);
            return;
        }

        SetStatus($"已写入{(feature == "movement" ? "移动" : "经验")}倍率 {multiplier:0.##}。切换一次场景或获得经验后确认。", true);
    }

    private void OnWriteAttributePoints(object sender, RoutedEventArgs e)
    {
        if (!TryRefreshPlayerContext(out var context) || !TryParseInt32(AttributePointsBox.Text, out var points) || points is < 0 or > 9_999)
        {
            SetStatus("属性点请输入 0 至 9,999，并先完成角色上下文定位。", false);
            return;
        }

        WriteValue(context.AttributeComponent + 0x20, points, "未分配属性点");
    }

    private void OnSetLevel(object sender, RoutedEventArgs e)
    {
        if (!TryRefreshPlayerContext(out var context) || !TryParseInt32(LevelBox.Text, out var targetLevel) || targetLevel is < 1 or > 100)
        {
            SetStatus("目标等级请输入 1 至 100，并先完成角色上下文定位。", false);
            return;
        }

        if (!_session!.TryReadInt32(context.LevelComponent, out var currentLevel))
        {
            SetStatus("无法读取当前等级。请重新定位角色上下文。", false);
            return;
        }

        if (targetLevel <= currentLevel)
        {
            SetStatus($"当前等级为 {currentLevel}。为保护等级/经验一致性，此版本只通过原生 LevelUp 提升等级。", false);
            return;
        }

        QueuePlayerCommand(PlayerCommand.LevelUp, targetLevel - currentLevel, 0, $"升级到 {targetLevel}");
    }

    private void OnGiveMaxStats(object sender, RoutedEventArgs e)
    {
        if (!TryParseInt32(MaxStatsLevelBox.Text, out var level) || level is < 1 or > 100 || !TryRefreshPlayerContext(out _))
        {
            SetStatus("满属性等级请输入 1 至 100，并先完成角色上下文定位。", false);
            return;
        }

        QueuePlayerCommand(PlayerCommand.GiveMaxStats, level, 0, $"按等级 {level} 设置游戏原生最大属性");
    }

    private void OnUnlockFastTravel(object sender, RoutedEventArgs e)
    {
        if (TryRefreshPlayerContext(out _))
        {
            QueuePlayerCommand(PlayerCommand.UnlockFastTravel, 0, 0, "解锁快速旅行");
        }
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

    private bool TryRefreshPlayerContext(out PlayerRuntimeContext context)
    {
        context = default;
        if (_playerContextHook is null || !_playerContextHook.TryReadContext(out context))
        {
            SetStatus("请先在“角色”页开启并读取角色上下文定位。", false);
            return false;
        }

        _playerContext = context;
        _currencyAddress = context.InventoryComponent + sizeof(int);
        return true;
    }

    private bool RequireSelectedItem()
    {
        if (_selectedItemEntity is not { } selectedItem || selectedItem == 0)
        {
            SetStatus("请先开启物品选择捕获，在游戏背包里点击目标物品详情后读取结果。", false);
            return false;
        }

        if (!TryRefreshPlayerContext(out _))
        {
            return false;
        }

        if (_playerContextHook!.SetSelectedItem(selectedItem))
        {
            return true;
        }

        SetStatus("无法把选中物品交给当前角色上下文；请重新定位角色后重试。", false);
        return false;
    }

    private async void QueuePlayerCommand(PlayerCommand command, int argument, int option, string feature)
    {
        if (_playerContextHook is null || !_playerContextHook.QueueCommand(command, argument, option))
        {
            SetStatus($"无法排队执行{feature}；角色上下文可能已失效。", false);
            return;
        }

        SetStatus($"已排队{feature}。回到游戏停留约 1 秒后检查结果；本次操作只执行一次。", true);
        await Task.Delay(800);
        if (_playerContextHook?.WasCommandCompleted(command) == true)
        {
            SetStatus($"{feature}已由游戏原生接口执行。请在游戏内确认结果。", true);
        }
        else
        {
            SetStatus($"{feature}尚未收到游戏线程完成确认。请确认角色已加载、未切场景后重试。", false);
        }
    }

    private void WriteRuntimeFlag(long address, bool enabled, string feature)
    {
        if (_session!.WriteInt32(address, enabled ? 1 : 0))
        {
            SetStatus($"{feature}已{(enabled ? "开启" : "关闭")}。", true);
            return;
        }

        SetStatus($"写入{feature}失败；请重新定位角色。", false);
    }

    private void WriteHeroDebugFlag(PlayerRuntimeContext context, int flag, bool enabled, string feature)
    {
        var address = context.HeroComponent + 4;
        if (!_session!.TryReadInt32(address, out var existing))
        {
            SetStatus($"无法读取{feature}标志；请重新定位角色。", false);
            return;
        }

        var updated = enabled ? existing | flag : existing & ~flag;
        WriteValue(address, updated, feature);
    }

    private static bool TryMapRarity(string rarity, out int value)
    {
        value = rarity switch
        {
            "Common" => 0,
            "Magical" => 1,
            "Plagued" => 2,
            "Gold" => 3,
            _ => -1
        };
        return value >= 0;
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
        _itemSelectionHook?.Dispose();
        _playerContextHook?.Dispose();
        _itemCapture = null;
        _itemSelectionHook = null;
        _playerContextHook = null;
        _playerContext = null;
        _selectedItemEntity = null;
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
