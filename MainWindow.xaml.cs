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
    private const string PlayerContextTailPattern = "56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string PlayerContextEntry = "48 89 5C 24 08";
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
    private long? _currencyAddress;
    private long? _itemQuantitySite;
    private long? _playerContextSite;
    private long? _itemSelectionSite;
    private long? _selectedItemEntity;
    private bool _stalePlayerContextHookDetected;
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
        AddLog("已连接游戏，正在检查当前版本并自动开启物品数量持续跟踪。");
        OnScanKnownSignatures(this, new RoutedEventArgs());
        if (_itemQuantitySite.HasValue)
        {
            OnStartItemCapture(this, new RoutedEventArgs());
        }
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
            _stalePlayerContextHookDetected = profileMatches.Count == 0 && HasStalePlayerContextHook();

            var summary = $"材料数量：{itemMatches.Count} 个匹配；角色入口：{profileMatches.Count} 个匹配；物品详情：{itemSelectionMatches.Count} 个匹配。";
            SignatureStateText.Text = _itemQuantitySite.HasValue && _playerContextSite.HasValue && _itemSelectionSite.HasValue ? "已验证" : "不匹配";
            SignatureDetailText.Text = summary;

            if (_itemQuantitySite.HasValue && _playerContextSite.HasValue && _itemSelectionSite.HasValue)
            {
                WriteStateText.Text = "可捕获";
                WriteStateDetailText.Text = "角色、钱包和物品均通过当前版本入口校验。";
                SetStatus($"{summary} 当前游戏版本可验证。钱包和角色字段无需先让数值变化。", true);
            }
            else if (_stalePlayerContextHookDetected)
            {
                WriteStateText.Text = "需重启游戏";
                WriteStateDetailText.Text = "检测到上次管理器异常退出后残留的角色捕获跳转。";
                SetStatus("检测到本程序上次留下的角色捕获跳转。请完全退出并重新启动游戏，再点击“连接并检查”。", false);
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

    private void OnKeepLastItemChanged(object sender, RoutedEventArgs e)
    {
        if (_session is { IsAttached: true } && _itemQuantitySite.HasValue)
        {
            OnStartItemCapture(this, new RoutedEventArgs());
        }
    }

    private void OnStopItemCapture(object sender, RoutedEventArgs e)
    {
        if (_itemCapture is null)
        {
            SetStatus("物品数量跟踪当前没有开启。", false);
            return;
        }

        _itemCapture.Dispose();
        _itemCapture = null;
        ItemCaptureText.Text = "持续跟踪已暂停；游戏已恢复原始数量处理逻辑。";
        SetStatus(ItemCaptureText.Text, true);
    }

    private void OnWriteItemQuantity(object sender, RoutedEventArgs e)
    {
        if (!RequireSession() || _itemCapture is null || !_itemCapture.TryReadCapturedAddress(out var address))
        {
            SetStatus("还没有捕获到最近变动的物品。请先在游戏里拾取、拆分、出售或使用目标物品 1 个。", false);
            return;
        }

        if (!_session!.TryReadInt32(address, out var current) || current is < 0 or > 999_999)
        {
            SetStatus("最近捕获的物品地址已经不可读或已失效。请让目标物品再变化一次。", false);
            return;
        }

        if (!TryParseInt32(ItemQuantityBox.Text, out var value) || value is < 1 or > 9_999)
        {
            SetStatus("请输入 1 至 9,999 的整数。", false);
            return;
        }

        WriteValue(address, value, "材料数量");
        if (_session.TryReadInt32(address, out var updated) && updated == value)
        {
            ItemCaptureText.Text = $"已修改最近变动物品：{current} → {updated}。跟踪保持开启，下一次数量变化会自动切换目标。";
        }
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

    private async void OnStartCurrencyCapture(object sender, RoutedEventArgs e)
    {
        if (!RequireKnownSite(_playerContextSite, "角色上下文"))
        {
            return;
        }

        _playerContextHook?.Dispose();
        _playerContext = null;
        _currencyAddress = null;
        InventoryProbeButton.IsEnabled = false;
        PlayerDiagnosticBox.Text = $"{DateTime.Now:HH:mm:ss}  开始新的角色捕获会话。";
        try
        {
            var activeProbe = new RemotePlayerContextHook(
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
            _playerContextHook = activeProbe;
            var result = activeProbe.Arm();
            CurrencyCaptureText.Text = result;
            SetStatus(CurrencyCaptureText.Text);
            if (!activeProbe.IsArmed)
            {
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(500);
                if (!ReferenceEquals(_playerContextHook, activeProbe))
                {
                    return;
                }

                if (activeProbe.TryReadRawCapture(out var firstArgument, out var secondArgument))
                {
                    CurrencyCaptureText.Text = $"捕获成功 · 参数 A：0x{firstArgument:X} · 参数 B：0x{secondArgument:X}";
                    AppendPlayerDiagnostic($"RawCapture: A=0x{firstArgument:X}, B=0x{secondArgument:X}");
                    InventoryProbeButton.IsEnabled = true;
                    SetStatus("角色参数捕获成功。现在可以单独测试一次背包组件解析。", true);
                    return;
                }
            }

            CurrencyCaptureText.Text = "10 秒内没有捕获到角色更新参数。请确认角色已加载后重新测试。";
            AppendPlayerDiagnostic("RawCapture: Timeout after 10 seconds.");
            SetStatus(CurrencyCaptureText.Text, false);
        }
        catch (Exception exception)
        {
            _playerContextHook = null;
            AppendPlayerDiagnostic($"RawCapture: Error={exception.Message}");
            SetStatus($"无法开始角色上下文定位：{exception.Message}", false);
        }
    }

    private async void OnProbeInventoryComponent(object sender, RoutedEventArgs e)
    {
        var activeProbe = _playerContextHook;
        if (activeProbe is null || !activeProbe.QueueInventoryProbe())
        {
            AppendPlayerDiagnostic("InventoryProbe: Request rejected because raw capture is unavailable.");
            SetStatus("尚未取得有效角色参数，无法测试背包组件。", false);
            return;
        }

        InventoryProbeButton.IsEnabled = false;
        AppendPlayerDiagnostic("InventoryProbe: Request=1, Completed=0, Result=0x0");
        SetStatus("已请求一次背包组件解析；正在等待游戏更新线程返回结果。");
        var requestConsumptionLogged = false;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(100);
            if (!ReferenceEquals(_playerContextHook, activeProbe))
            {
                return;
            }

            if (!activeProbe.TryReadInventoryProbe(out var inventoryComponent, out var completed, out var pending))
            {
                continue;
            }

            if (!pending && !completed && !requestConsumptionLogged)
            {
                requestConsumptionLogged = true;
                AppendPlayerDiagnostic("InventoryProbe: Request=0, Completed=0; call started, waiting for return.");
            }

            if (completed)
            {
                AppendPlayerDiagnostic($"InventoryProbe: Request=0, Completed=1, Result=0x{inventoryComponent:X}");
                if (inventoryComponent == 0)
                {
                    CurrencyCaptureText.Text = "背包函数已执行一次，但返回了空组件。入口线程正常，下一步需要校正函数参数。";
                    SetStatus(CurrencyCaptureText.Text, false);
                    return;
                }

                var currencyAddress = inventoryComponent + sizeof(int);
                var currencyText = _session!.TryReadInt32(currencyAddress, out var currencyBase)
                    ? currencyBase.ToString()
                    : "读取失败";
                _currencyAddress = currencyAddress;
                CurrencyCaptureText.Text = $"背包组件成功 · 组件：0x{inventoryComponent:X} · 货币基数：{currencyText}";
                SetStatus("一次性背包组件解析成功，游戏内部函数没有连续执行。", true);
                return;
            }
        }

        if (activeProbe.TryReadInventoryProbe(out var resultAfterTimeout, out var completedAfterTimeout, out var pendingAfterTimeout))
        {
            AppendPlayerDiagnostic($"InventoryProbe: Request={(pendingAfterTimeout ? 1 : 0)}, Completed={(completedAfterTimeout ? 1 : 0)}, Result=0x{resultAfterTimeout:X}");
            CurrencyCaptureText.Text = pendingAfterTimeout
                ? "背包测试请求未被游戏更新线程消费；捕获入口可能暂时没有运行。"
                : completedAfterTimeout
                    ? "背包函数已经执行，但返回值读取失败。"
                    : "背包测试请求已被消费，但完成标记未写回；需要检查调用返回路径。";
        }
        else
        {
            AppendPlayerDiagnostic("InventoryProbe: Status read failed.");
            CurrencyCaptureText.Text = "无法读取背包测试状态；当前捕获区可能已经失效。";
        }
        SetStatus(CurrencyCaptureText.Text, false);
    }

    private async void OnCopyPlayerDiagnostic(object sender, RoutedEventArgs e)
    {
        if (await TryCopyTextAsync(PlayerDiagnosticBox.Text))
        {
            SetStatus("角色诊断数据已复制到剪贴板。", true);
            return;
        }

        SetStatus("剪贴板正被其他程序占用，复制失败；诊断内容仍保留在文本框中，可选中后按 Ctrl+C。", false);
    }

    private void AppendPlayerDiagnostic(string message)
    {
        PlayerDiagnosticBox.AppendText($"{Environment.NewLine}{DateTime.Now:HH:mm:ss}  {message}");
        PlayerDiagnosticBox.ScrollToEnd();
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

    private async void OnCopyDetectorReport(object sender, RoutedEventArgs e)
    {
        if (_detector is null)
        {
            SetStatus("没有可复制的探测结果。", false);
            return;
        }

        if (await TryCopyTextAsync(_detector.BuildReport()))
        {
            SetStatus("候选地址已复制。把这段内容发给我，我会判断下一轮怎么继续缩小范围。", true);
            return;
        }

        SetStatus("剪贴板正被其他程序占用，候选地址复制失败；请稍后重试。", false);
    }

    private static async Task<bool> TryCopyTextAsync(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch (Exception) when (attempt < 4)
            {
                await Task.Delay(80);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
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
            if (feature == "角色上下文" && _stalePlayerContextHookDetected)
            {
                SetStatus("检测到上次异常退出残留的角色捕获跳转。请完全退出并重新启动游戏后再连接。", false);
                return false;
            }

            SetStatus($"{feature}特征码尚未唯一匹配。请先在概览页点击“检查已知定位”。", false);
            return false;
        }

        return true;
    }

    private bool HasStalePlayerContextHook()
    {
        var tailMatches = _session!.ScanGameAssembly(AobPattern.Parse(PlayerContextTailPattern));
        if (tailMatches.Count != 1)
        {
            return false;
        }

        var site = tailMatches[0] - 5;
        var entry = _session.Read(site, 5);
        if (entry.Length != 5 || entry[0] != 0xE9)
        {
            return false;
        }

        var trampoline = site + 5 + BitConverter.ToInt32(entry, 1);
        var marker = _session.Read(trampoline, 10);
        return marker.Length == 10 &&
               marker[0] == 0x4C && marker[1] == 0x89 && marker[2] == 0x05 &&
               marker[7] == 0x4C && marker[8] == 0x89 && marker[9] == 0x0D;
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
        _stalePlayerContextHookDetected = false;
        _currencyAddress = null;
        _itemQuantitySite = null;
        _playerContextSite = null;
        _itemSelectionSite = null;
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
