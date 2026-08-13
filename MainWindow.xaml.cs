using System;
using System.Linq;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NRftWManagerUI.Core;

namespace NRftWManagerUI;

public partial class MainWindow : Window
{
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        DragMove();
    }

    private void OnMinimizeWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private const string ItemQuantityPattern = "89 3B 0F 94 C0 EB ??";
    private const string PlayerContextPattern = "48 89 5C 24 08 56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string PlayerContextTailPattern = "56 57 41 56 48 83 EC 70 0F 29 74 24 60 49 8B D9 49 8B F0 4C 8B F2 48 8B F9";
    private const string PlayerContextEntry = "48 89 5C 24 08";
    private const string ItemSelectionPattern = "40 53 55 57 48 81 EC 70 04 00 00 80 3D 71 6D 57 03 00 48 8B DA 48 8B F9";
    private const string ItemSelectionEntry = "40 53 55 57 48 81 EC 70 04 00 00";
    private const string CurrentGameAssemblySha256 = "5B00EE90833B1BE2EA73E01CB83E710E09E86199D12AC769BE3C0E82ADD8B4BB";
    private const int GetInventoryComponentRva = 0x5D7F970;
    private const int InventoryComponentNaturalCallRva = 0x5D7F8B5;
    private const int GetHeroComponentRva = 0x5B32560;
    private const int GetHeroStatsRva = 0x5E02EC0;
    private const int LevelUpRva = 0x5DF50C0;
    private const int ChangeItemRarityRva = 0x5D66CF0;
    private const int AddItemEnchantmentRva = 0x5D6B250;
    private const int DuplicateItemRva = 0x5D884D0;
    private const int AddItemToInventoryRva = 0x5D792F0;
    private const int RepairItemRva = 0x5D617E0;
    private const int GetItemDataRva = 0x5D874F0;
    private const int GetItemRarityRva = 0x5D88620;
    private const int CreateItemRva = 0x5D863B0;
    private const int GetInventoryOwnerRva = 0x5D7A090;
    private const int TryGetItemOwnerRva = 0x5D7A150;
    private const int AddGoldRva = 0x5D79CF0;
    private const int SetHealthInfiniteRva = 0x5DE6050;
    private const int SetResourceInfiniteRva = 0x591A770;
    private const int TryGetHeroInventoryPointerRva = 0x61E20;
    private const int HeroInventoryTypeInfoRva = 0xBFDD5F8;
    private const int ResolveHeroItemDataRva = 0x5F5F270;
    private const int GetLocalizedItemNameRva = 0x8FB07C0;
    private const int SetItemLevelRva = 0x5D87920;
    private const int AwardGloamseedRva = 0x5F4BE60;
    private const int GiveMaxStatsRva = 0x5E00240;
    private const int UnlockFastTravelRva = 0x5F48E60;
    private const int QuantumEntitySystemUpdateRva = 0x5C29310;
    private const string QuantumEntitySystemUpdateEntry = "48 8B C4 48 89 58 08";

    private GameSession? _session;
    private RemoteCaptureHook? _itemCapture;
    private RemotePlayerContextHook? _playerContextHook;
    private RemoteInventoryCallHook? _inventoryCallHook;
    private RemoteItemSelectionHook? _itemSelectionHook;
    private RemoteQuantumCommandHook? _quantumCommandHook;
    private ValueChangeDetector? _detector;
    private PlayerRuntimeContext? _playerContext;
    private long? _currencyAddress;
    private long? _itemQuantitySite;
    private long? _playerContextSite;
    private long? _itemSelectionSite;
    private long? _selectedItemEntity;
    private bool _stalePlayerContextHookDetected;
    private bool _isConnecting;
    private bool _isClosing;
    private InstalledGame? _selectedGame;
    private string _selectedCurrency = "Gold";
    private readonly List<CapturedItemTemplate> _capturedItemTemplates = new();
    private readonly List<LocalGameItem> _localGameItems = new();
    private string _selectedIconCatalogCategory = "全部";
    private readonly HashSet<long> _pendingTemplateCaptures = new();
    private static readonly string ItemCatalogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NRftWManagerUI",
        "item-catalog.json");

    internal sealed class CapturedItemTemplate
    {
        public long Guid { get; set; }
        public int ItemType { get; set; }
        public int Rarity { get; set; }
        public string Path { get; set; } = string.Empty;
        public string LocalizedName { get; set; } = string.Empty;
        public string CustomName { get; set; } = string.Empty;
        [JsonIgnore] public long Entity { get; set; }
        [JsonIgnore] public long Template { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CustomName))
                {
                    return CustomName.Trim();
                }

                if (!string.IsNullOrWhiteSpace(LocalizedName))
                {
                    return LocalizedName.Trim();
                }

                var name = Path.Replace('\\', '/').Split('/').LastOrDefault();
                return string.IsNullOrWhiteSpace(name) ? $"物品 0x{Guid:X}" : name;
            }
        }

        [JsonIgnore]
        public string Metadata => $"类型 {ItemType}  ·  品质 {Rarity}  ·  {Guid:X16}";

        [JsonIgnore]
        public string Category => CategorizeItem(ItemType, Path, DisplayName);

        [JsonIgnore]
        public string IconPath => "Assets/riftctrl-icon.png";

        [JsonIgnore]
        public string PreviewDescription => string.IsNullOrWhiteSpace(Path)
            ? "等待资源导入器解析描述与图标。"
            : Path;

        public override string ToString() => $"{DisplayName}  · 类型 {ItemType}  · 品质 {Rarity}  · GUID 0x{Guid:X16}";
    }

    internal sealed class CapturedItemCategoryHeader
    {
        public required string Category { get; init; }
        public required int Count { get; init; }
        public string DisplayName => $"{Category}  ·  {Count}";
    }

    private sealed class IconCatalogEntry
    {
        public CapturedItemTemplate? Template { get; init; }
        public LocalGameItem? LocalItem { get; init; }
        public string DisplayName => Template?.DisplayName ?? LocalItem?.DisplayName ?? string.Empty;
        public string Category => Template?.Category ?? LocalItem?.Category ?? "其他";
        public string IconPath => Template?.IconPath ?? LocalItem?.IconPath ?? "Assets/riftctrl-icon.png";
        public string Metadata => Template?.Metadata ?? LocalItem?.Metadata ?? string.Empty;
        public string PreviewDescription => Template?.PreviewDescription ?? LocalItem?.Description ?? string.Empty;
        public string SearchText => $"{DisplayName} {Category} {Metadata} {PreviewDescription}";
        public bool CanGenerate => Template is not null;
        public string AvailabilityText => CanGenerate ? "可生成" : "待解析 GUID";
    }

    private sealed class IconCatalogCategoryFilter
    {
        public required string Name { get; init; }
        public required string Icon { get; init; }
        public required int Count { get; init; }
        public string DisplayName => $"{Icon} {Name}";
        public string CountText => Count.ToString();
    }

    public MainWindow()
    {
        InitializeComponent();
        LoadItemCatalog();
        Loaded += async (_, _) => await RefreshGameLibraryAsync();
        Closed += (_, _) =>
        {
            _isClosing = true;
            Disconnect();
        };
        AddLog("界面启动：尚未连接游戏。不会安装或加载任何游戏模组。");
    }

    private async void OnRefreshGameLibrary(object sender, RoutedEventArgs e) => await RefreshGameLibraryAsync();

    private async Task RefreshGameLibraryAsync()
    {
        var previousGame = _selectedGame;
        RefreshGameLibraryButton.IsEnabled = false;
        GameSelector.IsEnabled = false;
        GameLibrarySummaryText.Text = "正在扫描 Steam 与 Epic…";

        try
        {
            var games = await Task.Run(InstalledGameScanner.Scan);
            if (_isClosing)
            {
                return;
            }

            GameSelector.ItemsSource = games;
            GameLibrarySummaryText.Text = $"已发现 {games.Count(game => game.IsInstalled)} 个已安装游戏";
            GameSelector.SelectedItem = games.FirstOrDefault(game => previousGame is not null &&
                                                                     game.AppId == previousGame.AppId &&
                                                                     game.Platform == previousGame.Platform)
                                        ?? games.FirstOrDefault(game => game.IsSupported && game.IsInstalled)
                                        ?? games.FirstOrDefault(game => game.IsSupported)
                                        ?? games.FirstOrDefault();
        }
        catch (Exception exception)
        {
            GameLibrarySummaryText.Text = "游戏库扫描失败";
            UnsupportedGameNameText.Text = "无法读取游戏库";
            UnsupportedGameDetailText.Text = exception.Message;
            GameSupportBadgeText.Text = "扫描失败";
            SetStatus($"Steam / Epic 游戏库扫描失败：{exception.Message}", false);
        }
        finally
        {
            if (!_isClosing)
            {
                GameSelector.IsEnabled = true;
                RefreshGameLibraryButton.IsEnabled = true;
            }
        }
    }

    private void OnGameSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameSelector.SelectedItem is not InstalledGame game)
        {
            return;
        }

        var changedGame = _selectedGame is null ||
                          _selectedGame.AppId != game.AppId ||
                          _selectedGame.Platform != game.Platform;
        if (changedGame && _session is { IsAttached: true })
        {
            Disconnect();
        }

        _selectedGame = game;
        ConnectButton.IsEnabled = !_isConnecting && game.IsSupported;
        OperationSurface.Visibility = game.IsSupported ? Visibility.Visible : Visibility.Collapsed;
        UnsupportedGamePanel.Visibility = game.IsSupported ? Visibility.Collapsed : Visibility.Visible;

        if (game.IsSupported)
        {
            SetStatus(game.IsInstalled
                ? $"已选择 {game.Name} · {game.Platform}"
                : "已选择 No Rest for the Wicked；未从启动器检测到安装，运行游戏后仍可尝试连接。", true);
            if (game.IsInstalled)
            {
                _ = ScanLocalItemsAsync(game);
            }
            return;
        }

        UnsupportedGameNameText.Text = game.Name;
        UnsupportedGameDetailText.Text = $"已通过 {game.Platform} 检测到安装 · 当前版本尚未适配";
        GameSupportBadgeText.Text = "暂未支持";
        SetStatus($"已选择 {game.Name}，当前仅支持 No Rest for the Wicked。", false);
    }

    private async void OnConnectGame(object sender, RoutedEventArgs e)
    {
        if (_isConnecting)
        {
            return;
        }

        if (_selectedGame?.IsSupported != true)
        {
            SetStatus("请先选择一个已支持的游戏。", false);
            return;
        }

        if (_session is { IsAttached: true })
        {
            Disconnect();
            SetConnectionBusy(false);
            SelectedItemText.Text = "尚未选择物品";
            ItemCaptureText.Text = "连接游戏后自动跟踪最近一次数量变化";
            SetStatus("已断开游戏，并恢复所有临时捕获入口。", true);
            return;
        }

        Disconnect();
        SetConnectionBusy(true, "正在查找游戏进程…");
        var session = new GameSession();

        try
        {
            var result = await Task.Run(session.Attach);
            if (_isClosing)
            {
                session.Dispose();
                return;
            }
            if (!session.IsAttached)
            {
                session.Dispose();
                SetStatus(result);
                return;
            }

            ConnectionProgressText.Text = "正在验证版本与功能入口…";
            var scanResult = await Task.Run(() =>
            {
                var itemMatches = session.ScanGameAssembly(AobPattern.Parse(ItemQuantityPattern));
                var itemSelectionMatches = session.HasGameAssemblyHash(CurrentGameAssemblySha256)
                    ? session.ScanGameAssembly(AobPattern.Parse(ItemSelectionPattern))
                    : Array.Empty<long>();
                return (ItemMatches: itemMatches, ItemSelectionMatches: itemSelectionMatches);
            });
            if (_isClosing)
            {
                session.Dispose();
                return;
            }

            _session = session;
            _itemQuantitySite = scanResult.ItemMatches.Count == 1 ? scanResult.ItemMatches[0] : null;
            _playerContextSite = null;
            _itemSelectionSite = scanResult.ItemSelectionMatches.Count == 1 ? scanResult.ItemSelectionMatches[0] : null;
            _stalePlayerContextHookDetected = false;

            RuntimeStateText.Text = "已连接";
            RuntimeDetailText.Text = result;
            ConnectionText.Text = "已连接";
            ConnectionText.Foreground = (Brush)FindResource("NodeBrush");
            ConnectionIndicator.Fill = (Brush)FindResource("NodeBrush");

            var summary = $"数量入口：{scanResult.ItemMatches.Count} 个匹配；物品选择：{scanResult.ItemSelectionMatches.Count} 个匹配。";
            var verified = _itemQuantitySite.HasValue && _itemSelectionSite.HasValue;
            SignatureStateText.Text = verified ? "已验证" : "不匹配";
            SignatureDetailText.Text = summary;
            WriteStateText.Text = verified ? "可捕获" : "已锁定";
            WriteStateDetailText.Text = verified
                ? "数量与物品入口均通过当前版本校验。"
                : "游戏更新或文件不匹配；所有写入功能保持禁用。";

            if (!verified)
            {
                SetStatus($"{summary} 未通过当前版本的唯一匹配检查，因此所有功能保持禁用。", false);
                return;
            }

            ConnectionProgressText.Text = "正在启动自动捕获…";
            SetStatus($"{summary} 当前版本功能可用。", true);
            OnStartItemCapture(this, new RoutedEventArgs());
            OnStartItemSelection(this, new RoutedEventArgs());
            StartQuantumCommandHook();
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(_session, session))
            {
                session.Dispose();
            }
            else
            {
                Disconnect();
            }
            SetStatus($"连接或扫描失败：{exception.Message}", false);
        }
        finally
        {
            if (!_isClosing)
            {
                SetConnectionBusy(false);
            }
        }
    }

    private void SetConnectionBusy(bool busy, string progress = "")
    {
        _isConnecting = busy;
        ConnectButton.IsEnabled = !busy && _selectedGame?.IsSupported == true;
        GameSelector.IsEnabled = !busy;
        RefreshGameLibraryButton.IsEnabled = !busy;
        ConnectSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectButtonLabel.Text = busy
            ? "正在验证"
            : _session is { IsAttached: true }
                ? "断开连接"
                : "连接游戏";
        ConnectionOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateConnectedFeatureAvailability(!busy);
        if (busy)
        {
            ConnectionText.Text = "连接中";
            ConnectionText.Foreground = (Brush)FindResource("NodeBrush");
            ConnectionIndicator.Fill = (Brush)FindResource("NodeBrush");
            ConnectionProgressText.Text = progress;
            SetStatus(progress);
        }
        else if (_session is not { IsAttached: true })
        {
            ConnectionText.Text = "未连接";
            ConnectionText.Foreground = (Brush)FindResource("WarningBrush");
            ConnectionIndicator.Fill = (Brush)FindResource("WarningBrush");
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
                KeepLastItemCheck.IsChecked == true,
                _session!.GameAssemblyBase + TryGetItemOwnerRva);
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
        if (sender is Button { Tag: string currency } selectedButton)
        {
            _selectedCurrency = currency;
            foreach (var button in new[] { CurrencyCopperButton, CurrencySilverButton, CurrencyGoldButton })
            {
                button.Background = (Brush)FindResource("PanelAltBrush");
                button.BorderBrush = (Brush)FindResource("BorderBrush");
                button.Foreground = (Brush)FindResource("TextBrush");
            }
            selectedButton.Background = (Brush)FindResource("AccentBrush");
            selectedButton.BorderBrush = (Brush)FindResource("AccentBrush");
            selectedButton.Foreground = Brushes.White;
            CurrencyCaptureText.Text = $"已选择{GetCurrencyDisplayName(currency)}";
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
            var session = _session!;
            var activeProbe = new RemotePlayerContextHook(
                session,
                _playerContextSite!.Value,
                AobPattern.Parse(PlayerContextEntry).Bytes.Select(value => value ?? throw new InvalidOperationException("角色入口签名不能包含通配符。")).ToArray(),
                session.GameAssemblyBase + GetInventoryComponentRva,
                session.GameAssemblyBase + GetHeroComponentRva,
                session.GameAssemblyBase + GetHeroStatsRva,
                session.GameAssemblyBase + LevelUpRva,
                session.GameAssemblyBase + ChangeItemRarityRva,
                session.GameAssemblyBase + AddItemEnchantmentRva,
                session.GameAssemblyBase + DuplicateItemRva,
                session.GameAssemblyBase + AddItemToInventoryRva,
                session.GameAssemblyBase + RepairItemRva,
                session.GameAssemblyBase + GetItemDataRva,
                session.GameAssemblyBase + CreateItemRva,
                session.GameAssemblyBase + SetItemLevelRva,
                session.GameAssemblyBase + AwardGloamseedRva,
                session.GameAssemblyBase + GiveMaxStatsRva,
                session.GameAssemblyBase + UnlockFastTravelRva);
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
                    SetStatus("角色参数捕获成功。现在可以开启背包旁路捕获。", true);
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
        if (!RequireSession())
        {
            return;
        }

        _inventoryCallHook?.Dispose();
        InventoryProbeButton.IsEnabled = false;
        try
        {
            var activeProbe = new RemoteInventoryCallHook(
                _session!,
                _session!.GameAssemblyBase + InventoryComponentNaturalCallRva,
                _session.GameAssemblyBase + GetInventoryComponentRva);
            _inventoryCallHook = activeProbe;
            var result = activeProbe.Arm();
            AppendPlayerDiagnostic("InventoryObserve: armed at GetInventoryEntity direct-inventory return path.");
            SetStatus(result);
            if (!activeProbe.IsArmed)
            {
                _inventoryCallHook = null;
                InventoryProbeButton.IsEnabled = true;
                return;
            }

            for (var attempt = 0; attempt < 600; attempt++)
            {
                await Task.Delay(100);
                if (!ReferenceEquals(_inventoryCallHook, activeProbe))
                {
                    return;
                }

                if (!activeProbe.TryReadInventoryComponent(out var inventoryComponent, out var observed) || !observed)
                {
                    continue;
                }

                if (inventoryComponent == 0)
                {
                    activeProbe.Dispose();
                    _inventoryCallHook = null;
                    AppendPlayerDiagnostic("InventoryObserve: direct path observed, component result was 0x0.");
                    SetStatus("已命中背包快速返回路径，但组件解析返回空值。诊断已保留。", false);
                    return;
                }

                var currencyAddress = inventoryComponent + sizeof(int);
                var currencyText = _session!.TryReadInt32(currencyAddress, out var currencyBase)
                    ? currencyBase.ToString()
                    : "读取失败";
                _currencyAddress = currencyAddress;
                CurrencyCaptureText.Text = $"背包组件成功 · 组件：0x{inventoryComponent:X} · 货币基数：{currencyText}";
                AppendPlayerDiagnostic($"InventoryObserve: Result=0x{inventoryComponent:X}, CurrencyBase={currencyText}");
                activeProbe.Dispose();
                _inventoryCallHook = null;
                SetStatus("已从游戏自身的正常调用中旁路捕获背包组件。", true);
                return;
            }

            activeProbe.Dispose();
            _inventoryCallHook = null;
            InventoryProbeButton.IsEnabled = true;
            AppendPlayerDiagnostic("InventoryObserve: no native call observed within 60 seconds.");
            SetStatus("60 秒内没有观察到背包原生调用。请重新开启后拾取、购买或制作一个物品。", false);
        }
        catch (Exception exception)
        {
            _inventoryCallHook?.Dispose();
            _inventoryCallHook = null;
            InventoryProbeButton.IsEnabled = true;
            AppendPlayerDiagnostic($"InventoryObserve: Error={exception.Message}");
            SetStatus($"无法开启背包旁路捕获：{exception.Message}", false);
        }
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
        CurrencyCaptureText.Text = $"{GetCurrencyDisplayName(_selectedCurrency)}已就绪 · 当前值 {valueText}";
        SetStatus(CurrencyCaptureText.Text, true);
    }

    private async void OnWriteCurrency(object sender, RoutedEventArgs e)
    {
        if (!RequireSession() || _selectedItemEntity is not { } selectedItem || selectedItem == 0)
        {
            SetStatus("请先在游戏背包里点击任意一件属于当前角色的物品，再写入货币。", false);
            return;
        }

        if (!TryParseInt32(CurrencyValueBox.Text, out var value) || value is < 1 or > 9_999_999)
        {
            SetStatus("增加数量请输入 1 至 9,999,999 的整数。", false);
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
            SetStatus($"{GetCurrencyDisplayName(_selectedCurrency)}数值过大，换算后超出游戏允许范围。", false);
            return;
        }

        var internalValue = value * multiplier;
        if (_quantumCommandHook is null || !_quantumCommandHook.QueueAddGold(selectedItem, internalValue))
        {
            SetStatus("原生货币命令无法排队；请重新连接游戏后重试。", false);
            return;
        }

        SetStatus($"正在增加{GetCurrencyDisplayName(_selectedCurrency)} {value:N0}。", true);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            if (_quantumCommandHook?.TryReadGoldCompletion(out var completed, out var succeeded) != true || !completed)
            {
                continue;
            }

            CurrencyCaptureText.Text = succeeded
                ? $"已增加{GetCurrencyDisplayName(_selectedCurrency)} {value:N0}；若未立即刷新，重新打开背包即可。"
                : "游戏无法从所选物品解析当前角色；请点击角色背包内的物品后重试。";
            SetStatus(CurrencyCaptureText.Text, succeeded);
            return;
        }

        SetStatus("2 秒内没有收到货币命令完成确认；未重复执行。", false);
    }

    private async void OnToggleInfiniteStat(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string feature, IsChecked: bool enabled } checkBox)
        {
            return;
        }

        var statKind = feature switch
        {
            "health" => 0,
            "stamina" => 1,
            "focus" => 2,
            _ => -1
        };
        var featureName = feature switch
        {
            "health" => "无限生命",
            "stamina" => "无限体力",
            "focus" => "无限专注",
            _ => "无限状态"
        };

        if (!RequireSession() || statKind < 0 || _selectedItemEntity is not { } selectedItem || selectedItem == 0)
        {
            checkBox.IsChecked = !enabled;
            SetStatus($"开启{featureName}前，请先在角色背包里点击任意一件物品。", false);
            return;
        }

        checkBox.IsEnabled = false;
        if (_quantumCommandHook is null || !_quantumCommandHook.QueueInfiniteStat(selectedItem, statKind, enabled))
        {
            checkBox.IsChecked = !enabled;
            checkBox.IsEnabled = true;
            SetStatus($"{featureName}命令无法排队；请重新连接游戏后重试。", false);
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            if (_quantumCommandHook?.TryReadInfiniteStatCompletion(out var completed, out var succeeded) != true || !completed)
            {
                continue;
            }

            checkBox.IsEnabled = true;
            if (!succeeded)
            {
                checkBox.IsChecked = !enabled;
                SetStatus($"无法从所选物品解析角色的{featureName}组件。请点击角色背包内的物品后重试。", false);
                return;
            }

            SetStatus($"{featureName}已{(enabled ? "开启" : "关闭")}。", true);
            return;
        }

        checkBox.IsChecked = !enabled;
        checkBox.IsEnabled = true;
        SetStatus($"2 秒内没有收到{featureName}命令完成确认；未重复执行。", false);
    }

    private async void OnStartItemSelection(object sender, RoutedEventArgs e)
    {
        if (!RequireKnownSite(_itemSelectionSite, "物品详情"))
        {
            return;
        }

        _itemSelectionHook?.Dispose();
        _selectedItemEntity = null;
        SelectedItemHistoryList.Items.Clear();
        ItemNameDiagnosticBox.Text = "尚无物品名称诊断。";
        try
        {
            _itemSelectionHook = new RemoteItemSelectionHook(
                _session!,
                _itemSelectionSite!.Value,
                _session!.GameAssemblyBase + GetLocalizedItemNameRva,
                AobPattern.Parse(ItemSelectionEntry).Bytes.Select(value => value ?? throw new InvalidOperationException("物品详情入口签名不能包含通配符。")).ToArray());
            var result = _itemSelectionHook.Arm();
            SelectedItemText.Text = "等待选择物品";
            SetStatus(result);
            var activeHook = _itemSelectionHook;
            for (var attempt = 0; attempt < 18_000 && ReferenceEquals(_itemSelectionHook, activeHook); attempt++)
            {
                await Task.Delay(100);
                if (!activeHook.TryReadSelection(out var itemEntity, out var itemSlot, out var itemAsset, out var localizedNameAddress) || itemEntity == _selectedItemEntity)
                {
                    continue;
                }

                var localizedName = ReadResolvedItemName(localizedNameAddress);
                AppendItemNameDiagnostic(itemEntity, itemSlot, itemAsset, localizedNameAddress, localizedName);
                _selectedItemEntity = itemEntity;
                activeHook.ClearSelection();
                RecordSelectedItem(itemEntity, localizedName);
                SelectedItemText.Text = string.IsNullOrWhiteSpace(localizedName) ? "未识别物品名称" : localizedName;
                SetStatus(string.IsNullOrWhiteSpace(localizedName)
                    ? $"名称解析失败：物品 0x{itemEntity:X}，资源 0x{itemAsset:X}，名称指针 0x{localizedNameAddress:X}。"
                    : $"已选中 {localizedName}。", !string.IsNullOrWhiteSpace(localizedName));
            }
        }
        catch (Exception exception)
        {
            _itemSelectionHook = null;
            SetStatus($"无法开始物品选择捕获：{exception.Message}", false);
        }
    }

    private void OnReadItemSelection(object sender, RoutedEventArgs e)
    {
        if (_itemSelectionHook is null || !_itemSelectionHook.TryReadSelection(out var itemEntity, out var itemSlot, out var itemAsset, out var localizedNameAddress))
        {
            SetStatus("尚未捕获物品。请先开启物品选择捕获，再回游戏打开背包并点击目标物品详情。", false);
            return;
        }

        var localizedName = ReadResolvedItemName(localizedNameAddress);
        AppendItemNameDiagnostic(itemEntity, itemSlot, itemAsset, localizedNameAddress, localizedName);
        _selectedItemEntity = itemEntity;
        _itemSelectionHook.ClearSelection();
        RecordSelectedItem(itemEntity, localizedName);
        SelectedItemText.Text = string.IsNullOrWhiteSpace(localizedName) ? "未识别物品名称" : localizedName;
        SetStatus(string.IsNullOrWhiteSpace(localizedName)
            ? $"名称解析失败：物品 0x{itemEntity:X}，资源 0x{itemAsset:X}，名称指针 0x{localizedNameAddress:X}。"
            : $"已选中 {localizedName}。", !string.IsNullOrWhiteSpace(localizedName));
    }

    private string ReadResolvedItemName(long localizedNameAddress)
    {
        return _session?.TryReadIl2CppString(localizedNameAddress, out var value) == true
            ? value.Trim()
            : string.Empty;
    }

    private void AppendItemNameDiagnostic(long itemEntity, long itemSlot, long capturedAsset, long nameAddress, string resolvedName)
    {
        var delayedAsset = 0L;
        var directMessage = 0L;
        var simplifiedChinese = 0L;
        var referenceId = 0L;
        if (_session is { IsAttached: true } && itemSlot != 0)
        {
            if (_session.TryReadInt64(itemSlot + 0x20, out var itemSlotVisual) && itemSlotVisual != 0)
            {
                _session.TryReadInt64(itemSlotVisual + 0x188, out delayedAsset);
            }
            var asset = delayedAsset != 0 ? delayedAsset : capturedAsset;
            if (asset != 0)
            {
                _session.TryReadInt64(asset + 0x50, out directMessage);
                _session.TryReadInt64(asset + 0x98, out referenceId);
                if (directMessage != 0)
                {
                    _session.TryReadInt64(directMessage + 0x58, out simplifiedChinese);
                }
            }
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff}\tEntity=0x{itemEntity:X16}\tSlot=0x{itemSlot:X}\tAssetAtHook=0x{capturedAsset:X}\tAssetDelayed=0x{delayedAsset:X}\tNamePtr=0x{nameAddress:X}\tDirectMsg=0x{directMessage:X}\tZhCN=0x{simplifiedChinese:X}\tNameRefId=0x{referenceId:X}\tText={resolvedName}";
        if (ItemNameDiagnosticBox.Text == "尚无物品名称诊断。")
        {
            ItemNameDiagnosticBox.Clear();
        }
        ItemNameDiagnosticBox.AppendText((ItemNameDiagnosticBox.Text.Length == 0 ? string.Empty : Environment.NewLine) + line);
        ItemNameDiagnosticBox.ScrollToEnd();
    }

    private async void OnCopyItemNameDiagnostic(object sender, RoutedEventArgs e)
    {
        if (await TryCopyTextAsync(ItemNameDiagnosticBox.Text))
        {
            SetStatus("物品名称诊断已复制。直接把文字粘贴给我即可。", true);
            return;
        }

        SetStatus("剪贴板正被占用；诊断仍保留在文本框中，可以点进去按 Ctrl+A、Ctrl+C。", false);
    }

    private void RecordSelectedItem(long itemEntity, string localizedName)
    {
        var nameSuffix = string.IsNullOrWhiteSpace(localizedName) ? string.Empty : $"   {localizedName}";
        SelectedItemHistoryList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss.fff}   0x{itemEntity:X16}{nameSuffix}");
        while (SelectedItemHistoryList.Items.Count > 5)
        {
            SelectedItemHistoryList.Items.RemoveAt(SelectedItemHistoryList.Items.Count - 1);
        }

        _ = CaptureItemTemplateAsync(itemEntity, localizedName);
    }

    private async Task CaptureItemTemplateAsync(long itemEntity, string localizedName)
    {
        var existingEntity = _capturedItemTemplates.FirstOrDefault(item => item.Entity == itemEntity);
        if (existingEntity is not null)
        {
            if (!string.IsNullOrWhiteSpace(localizedName) && existingEntity.LocalizedName != localizedName)
            {
                existingEntity.LocalizedName = localizedName;
                SaveItemCatalog();
                RefreshCapturedItemList();
            }
            return;
        }

        if (!_pendingTemplateCaptures.Add(itemEntity))
        {
            return;
        }

        try
        {
            var queued = false;
            for (var attempt = 0; attempt < 10 && !queued; attempt++)
            {
                queued = _quantumCommandHook?.QueueItemInspection(itemEntity) == true;
                if (!queued)
                {
                    await Task.Delay(100);
                }
            }

            if (!queued)
            {
                return;
            }

            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                if (_quantumCommandHook?.TryReadItemInspectionCompletion(
                        out var completed,
                        out var succeeded,
                        out var template,
                        out var guid,
                        out var pathAddress,
                        out var itemType,
                        out var rarity) != true || !completed)
                {
                    continue;
                }

                if (!succeeded || template == 0 || guid == 0)
                {
                    return;
                }

                var path = _session?.TryReadIl2CppString(pathAddress, out var resolvedPath) == true
                    ? resolvedPath
                    : string.Empty;
                var existing = _capturedItemTemplates.FirstOrDefault(item => item.Guid == guid);
                if (existing is null)
                {
                    existing = new CapturedItemTemplate { Guid = guid };
                    _capturedItemTemplates.Add(existing);
                }

                existing.Entity = itemEntity;
                existing.Template = template;
                existing.ItemType = itemType;
                existing.Rarity = rarity;
                if (!string.IsNullOrWhiteSpace(localizedName))
                {
                    existing.LocalizedName = localizedName;
                }
                if (!string.IsNullOrWhiteSpace(path))
                {
                    existing.Path = path;
                }
                SaveItemCatalog();
                RefreshCapturedItemList();
                RefreshIconCatalogList();
                if (CapturedItemCatalogList.Items.Contains(existing))
                {
                    CapturedItemCatalogList.SelectedItem = existing;
                    CapturedItemCatalogList.ScrollIntoView(existing);
                }
                return;
            }
        }
        finally
        {
            _pendingTemplateCaptures.Remove(itemEntity);
        }
    }

    private void OnFilterCapturedItems(object sender, TextChangedEventArgs e) => RefreshCapturedItemList();

    private void OnFilterIconCatalogItems(object sender, TextChangedEventArgs e) => RefreshIconCatalogList();

    private void OnIconCatalogCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconCatalogCategoryList?.SelectedItem is IconCatalogCategoryFilter category)
        {
            _selectedIconCatalogCategory = category.Name;
            RefreshIconCatalogList();
        }
    }

    private void UpdateConnectedFeatureAvailability(bool allowInteraction)
    {
        var connected = allowInteraction &&
                        _session is { IsAttached: true } &&
                        _itemQuantitySite.HasValue &&
                        _itemSelectionSite.HasValue;

        GeneralTab.IsEnabled = connected;
        ItemEditTab.IsEnabled = connected;
        CapturedCatalogTab.IsEnabled = connected;
        IconCatalogTab.IsEnabled = allowInteraction;
        ScanLocalItemsButton.IsEnabled = allowInteraction;
    }

    private async void OnScanLocalItems(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is not { IsInstalled: true } game || string.IsNullOrWhiteSpace(game.InstallPath))
        {
            SetStatus("请先选择一个已安装游戏。", false);
            return;
        }

        ScanLocalItemsButton.IsEnabled = false;
        IconCatalogSummaryText.Text = "正在扫描当前游戏资源…";
        try
        {
            await ScanLocalItemsAsync(game);
        }
        catch (IOException exception)
        {
            SetStatus($"扫描当前游戏资源失败：{exception.Message}", false);
        }
        catch (UnauthorizedAccessException exception)
        {
            SetStatus($"没有权限读取游戏资源：{exception.Message}", false);
        }
        finally
        {
            ScanLocalItemsButton.IsEnabled = true;
        }
    }

    private async Task ScanLocalItemsAsync(InstalledGame game)
    {
        if (string.IsNullOrWhiteSpace(game.InstallPath))
        {
            return;
        }

        IconCatalogSummaryText.Text = "正在扫描当前游戏资源…";
        var items = await Task.Run(() => LocalGameItemScanner.Scan(game.InstallPath));
        if (!ReferenceEquals(_selectedGame, game) && (_selectedGame?.AppId != game.AppId || _selectedGame?.Platform != game.Platform))
        {
            return;
        }

        _localGameItems.Clear();
        _localGameItems.AddRange(items);
        RefreshIconCatalogList();
        SetStatus($"已从当前游戏资源扫描到 {_localGameItems.Count} 个候选物品。", _localGameItems.Count > 0);
    }

    private void RefreshCapturedItemList()
    {
        if (CapturedItemCatalogList is null)
        {
            return;
        }

        var search = CapturedItemSearchBox?.Text.Trim() ?? string.Empty;
        CapturedItemCatalogList.Items.Clear();
        var visibleItems = _capturedItemTemplates.Where(item =>
                     search.Length == 0 ||
                     item.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     item.Path.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                     item.Guid.ToString("X").Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var groups = visibleItems
            .GroupBy(item => item.Category)
            .OrderBy(group => GetCategoryOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        foreach (var group in groups)
        {
            var items = group.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
            CapturedItemCatalogList.Items.Add(new CapturedItemCategoryHeader { Category = group.Key, Count = items.Length });
            foreach (var item in items)
            {
                CapturedItemCatalogList.Items.Add(item);
            }
        }
        CapturedItemCatalogSummary.Text = search.Length == 0
            ? $"{_capturedItemTemplates.Count} 个物品 · {groups.Length} 类"
            : $"找到 {visibleItems.Length} 个 · {groups.Length} 类";

        RefreshIconCatalogList();
    }

    private void RefreshIconCatalogList()
    {
        if (IconCatalogList is null || IconCatalogCategoryList is null)
        {
            return;
        }

        var selectedName = (IconCatalogList.SelectedItem as IconCatalogEntry)?.DisplayName;
        var search = IconCatalogSearchBox?.Text.Trim() ?? string.Empty;
        var captured = _capturedItemTemplates.Select(item => new IconCatalogEntry { Template = item });
        var capturedKeys = _capturedItemTemplates
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var local = _localGameItems
            .Where(item => !capturedKeys.Contains(item.Key))
            .Select(item => new IconCatalogEntry { LocalItem = item });

        var allItems = captured.Concat(local).ToArray();
        RefreshIconCatalogCategories(allItems);

        var visibleItems = allItems
            .Where(item => _selectedIconCatalogCategory == "全部" || item.Category == _selectedIconCatalogCategory)
            .Where(item => search.Length == 0 || item.SearchText.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => GetCategoryOrder(item.Category))
            .ThenByDescending(item => item.CanGenerate)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        IconCatalogList.Items.Clear();
        foreach (var item in visibleItems)
        {
            IconCatalogList.Items.Add(item);
        }

        IconCatalogSummaryText.Text = $"{visibleItems.Length} 个 · 名称 {_localGameItems.Count} · 图标待解析 · 可生成 {_capturedItemTemplates.Count}";

        if (!string.IsNullOrWhiteSpace(selectedName))
        {
            IconCatalogList.SelectedItem = visibleItems.FirstOrDefault(item => item.DisplayName == selectedName);
        }
    }

    private void RefreshIconCatalogCategories(IReadOnlyCollection<IconCatalogEntry> allItems)
    {
        var categories = allItems
            .GroupBy(item => item.Category)
            .OrderBy(group => GetCategoryOrder(group.Key))
            .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new IconCatalogCategoryFilter
            {
                Name = group.Key,
                Icon = GetCategoryIcon(group.Key),
                Count = group.Count()
            })
            .ToList();

        categories.Insert(0, new IconCatalogCategoryFilter
        {
            Name = "全部",
            Icon = "✦",
            Count = allItems.Count
        });

        if (!categories.Any(category => category.Name == _selectedIconCatalogCategory))
        {
            _selectedIconCatalogCategory = "全部";
        }

        var previousHandler = new SelectionChangedEventHandler(OnIconCatalogCategoryChanged);
        IconCatalogCategoryList.SelectionChanged -= previousHandler;
        IconCatalogCategoryList.Items.Clear();
        foreach (var category in categories)
        {
            IconCatalogCategoryList.Items.Add(category);
        }

        IconCatalogCategoryList.SelectedItem = categories.FirstOrDefault(category => category.Name == _selectedIconCatalogCategory)
                                               ?? categories.FirstOrDefault();
        IconCatalogCategoryList.SelectionChanged += previousHandler;
    }

    private static string CategorizeItem(int itemType, string path, string displayName)
    {
        var text = $"{path} {displayName}".Replace('\\', '/');
        if (ContainsAny(text, "Weapon", "Weapons", "Sword", "Axe", "Bow", "Dagger", "Staff", "Spear", "Mace", "Hammer", "Wand", "双手", "单手", "巨棒", "剑", "斧", "弓", "杖", "矛"))
        {
            return "武器";
        }

        if (ContainsAny(text, "Armor", "Armour", "Helmet", "Chest", "Glove", "Pants", "Boot", "Shield", "Head", "防具", "头盔", "胸甲", "手套", "裤", "靴", "盾"))
        {
            return "防具";
        }

        if (ContainsAny(text, "Ring", "Amulet", "Jewelry", "Accessory", "Trinket", "戒指", "项链", "护符", "饰品"))
        {
            return "饰品";
        }

        if (ContainsAny(text, "Potion", "Food", "Consumable", "Bomb", "Elixir", "药", "食物", "消耗", "炸弹"))
        {
            return "消耗品";
        }

        if (ContainsAny(text, "Material", "Resource", "Ingredient", "Craft", "Ore", "Hide", "Herb", "Wood", "材料", "资源", "矿", "皮", "草", "木"))
        {
            return "材料";
        }

        if (ContainsAny(text, "Key", "Quest", "Token", "钥匙", "任务"))
        {
            return "任务/钥匙";
        }

        return itemType switch
        {
            1 or 2 or 3 => "武器",
            4 or 5 or 6 => "防具",
            7 or 8 => "饰品",
            9 or 10 => "消耗品",
            11 or 12 => "材料",
            _ => "其他"
        };
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static int GetCategoryOrder(string category) => category switch
    {
        "武器" => 0,
        "防具" => 1,
        "饰品" => 2,
        "消耗品" => 3,
        "材料" => 4,
        "任务/钥匙" => 5,
        _ => 9
    };

    private static string GetCategoryIcon(string category) => category switch
    {
        "武器" => "⚔",
        "防具" => "◈",
        "饰品" => "◇",
        "消耗品" => "✚",
        "材料" => "◆",
        "任务/钥匙" => "⌑",
        "图纸/配方" => "▧",
        _ => "•"
    };

    private void LoadItemCatalog()
    {
        try
        {
            if (File.Exists(ItemCatalogPath))
            {
                var savedItems = JsonSerializer.Deserialize<List<CapturedItemTemplate>>(File.ReadAllText(ItemCatalogPath));
                if (savedItems is not null)
                {
                    _capturedItemTemplates.AddRange(savedItems.Where(item => item.Guid != 0));
                }
            }
        }
        catch (JsonException)
        {
            AddLog("物品目录文件格式无效；本次未加载，原文件没有被覆盖。");
        }
        catch (IOException exception)
        {
            AddLog($"读取物品目录失败：{exception.Message}");
        }

        RefreshCapturedItemList();
    }

    private void SaveItemCatalog()
    {
        try
        {
            var directory = Path.GetDirectoryName(ItemCatalogPath)!;
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(
                _capturedItemTemplates.OrderBy(item => item.DisplayName).ToArray(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ItemCatalogPath, json);
        }
        catch (IOException exception)
        {
            SetStatus($"保存物品目录失败：{exception.Message}", false);
        }
        catch (UnauthorizedAccessException exception)
        {
            SetStatus($"没有权限保存物品目录：{exception.Message}", false);
        }
    }

    private void OnCreateFromCapturedTemplate(object sender, RoutedEventArgs e)
    {
        if (CapturedItemCatalogList.SelectedItem is not CapturedItemTemplate item)
        {
            SetStatus("请先在物品模板列表中选择一项。", false);
            return;
        }

        if (_selectedItemEntity is not { } ownerItem || ownerItem == 0)
        {
            SetStatus("目录已保存。生成前请在当前角色背包里点击任意一件物品，用于识别接收角色。", false);
            return;
        }

        if (!TryParseInt32(CatalogCreateCountBox.Text, out var count) || count is < 1 or > 99)
        {
            SetStatus("生成数量请输入 1 至 99。", false);
            return;
        }

        CreateFromTemplate(ownerItem, item.DisplayName, item.Guid, item.Rarity, count);
    }

    private void OnCapturedItemSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CapturedItemCatalogList.SelectedItem is CapturedItemCategoryHeader)
        {
            CapturedItemCatalogList.SelectedItem = null;
            return;
        }

        if (CapturedItemCatalogList.SelectedItem is CapturedItemTemplate item)
        {
            CapturedItemNameBox.Text = item.CustomName;
        }
    }

    private void OnIconCatalogSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IconCatalogList.SelectedItem is not IconCatalogEntry entry)
        {
            IconCatalogCreateButton.IsEnabled = false;
            return;
        }

        IconCatalogCreateButton.IsEnabled = entry.CanGenerate;
        if (entry.Template is { } item)
        {
            CapturedItemCatalogList.SelectedItem = item;
            CapturedItemNameBox.Text = item.CustomName;
        }
    }

    private void OnCreateFromIconCatalog(object sender, RoutedEventArgs e)
    {
        if (IconCatalogList.SelectedItem is not IconCatalogEntry { Template: { } item })
        {
            SetStatus("这个本地扫描项还没有解析出可生成 GUID。请先选择已捕获模板，或等资源 GUID 解析接入。", false);
            return;
        }

        if (_selectedItemEntity is not { } ownerItem || ownerItem == 0)
        {
            SetStatus("生成前请在当前角色背包里点击任意一件物品，用于识别接收角色。", false);
            return;
        }

        if (!TryParseInt32(IconCatalogCreateCountBox.Text, out var count) || count is < 1 or > 99)
        {
            SetStatus("生成数量请输入 1 至 99。", false);
            return;
        }

        CreateFromTemplate(ownerItem, item.DisplayName, item.Guid, item.Rarity, count);
    }

    private void OnSaveCapturedItemName(object sender, RoutedEventArgs e)
    {
        var item = CapturedItemCatalogList.SelectedItem as CapturedItemTemplate;
        if (item is null && _selectedItemEntity is { } selectedEntity)
        {
            item = _capturedItemTemplates.LastOrDefault(candidate => candidate.Entity == selectedEntity);
        }

        if (item is null)
        {
            SetStatus("请先在游戏里点击物品，等待它出现在模板列表后再保存名称。", false);
            return;
        }

        var customName = CapturedItemNameBox.Text.Trim();
        if (customName.Length > 80)
        {
            SetStatus("物品名称最多 80 个字符。", false);
            return;
        }

        item.CustomName = customName;
        SaveItemCatalog();
        RefreshCapturedItemList();
        CapturedItemCatalogList.SelectedItem = item;
        SetStatus(string.IsNullOrWhiteSpace(customName)
            ? "已清除手工名称，恢复显示内部资源名称。"
            : $"已将模板名称保存为“{customName}”。", true);
    }

    private void OnQueueRarity(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string rarity } || !TryMapRarity(rarity, out var value) ||
            _selectedItemEntity is not { } selectedItem || selectedItem == 0)
        {
            SetStatus("请先在游戏背包里点击要修改的物品。", false);
            return;
        }

        QueueRarityChange(selectedItem, value, rarity);
    }

    private void StartQuantumCommandHook()
    {
        _quantumCommandHook?.Dispose();
        _quantumCommandHook = null;
        if (_session is not { IsAttached: true } || !_session.HasGameAssemblyHash(CurrentGameAssemblySha256))
        {
            return;
        }

        try
        {
            var expected = AobPattern.Parse(QuantumEntitySystemUpdateEntry).Bytes
                .Select(value => value ?? throw new InvalidOperationException("Quantum 入口签名不能包含通配符。"))
                .ToArray();
            var hook = new RemoteQuantumCommandHook(
                _session,
                _session.GameAssemblyBase + QuantumEntitySystemUpdateRva,
                expected,
                _session.GameAssemblyBase + ChangeItemRarityRva,
                _session.GameAssemblyBase + GetItemDataRva,
                _session.GameAssemblyBase + GetItemRarityRva,
                _session.GameAssemblyBase + CreateItemRva,
                _session.GameAssemblyBase + GetInventoryOwnerRva,
                _session.GameAssemblyBase + TryGetItemOwnerRva,
                _session.GameAssemblyBase + AddItemToInventoryRva,
                _session.GameAssemblyBase + AddGoldRva,
                _session.GameAssemblyBase + GetHeroStatsRva,
                _session.GameAssemblyBase + SetHealthInfiniteRva,
                _session.GameAssemblyBase + SetResourceInfiniteRva,
                _session.GameAssemblyBase + TryGetHeroInventoryPointerRva,
                _session.GameAssemblyBase + HeroInventoryTypeInfoRva,
                _session.GameAssemblyBase + ResolveHeroItemDataRva);
            var result = hook.Arm();
            if (hook.IsArmed)
            {
                _quantumCommandHook = hook;
                SetStatus(result, true);
            }
            else
            {
                hook.Dispose();
                SetStatus(result, false);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"无法启用品质命令入口：{exception.Message}", false);
        }
    }

    private async void QueueRarityChange(long selectedItem, int rarityValue, string rarityName)
    {
        if (_quantumCommandHook is null || !_quantumCommandHook.QueueRarity(selectedItem, rarityValue))
        {
            SetStatus("品质命令无法排队；请重新点击“连接并检查”后重试。", false);
            return;
        }

        SetStatus($"已排队把 0x{selectedItem:X} 改为 {rarityName}，等待游戏模拟线程执行。", true);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100);
            if (_quantumCommandHook?.TryReadRarityCompletion(out var completed, out var succeeded) != true || !completed)
            {
                continue;
            }

            SetStatus(succeeded
                ? $"{rarityName} 品质已由游戏原生接口执行。请关闭再打开物品详情确认。"
                : $"游戏拒绝把当前物品改为 {rarityName}；该物品或品质转换可能不受支持。", succeeded);
            return;
        }

        SetStatus("2 秒内没有收到品质命令完成确认；未重复执行，请保持角色在游戏中后重试。", false);
    }

    private void OnCreateFromSelectedTemplate(object sender, RoutedEventArgs e)
    {
        if (_selectedItemEntity is not { } selectedItem || selectedItem == 0)
        {
            SetStatus("请先在游戏背包里点击要作为模板的物品。", false);
            return;
        }

        CreateFromTemplate(selectedItem, $"0x{selectedItem:X}", 0, 0);
    }

    private async void CreateFromTemplate(long ownerItem, string templateName, long templateGuid, int rarity, int count = 1)
    {
        var createdCount = 0;
        for (var index = 0; index < count; index++)
        {
            var queued = templateGuid == 0
                ? _quantumCommandHook?.QueueCreateFromTemplate(ownerItem) == true
                : _quantumCommandHook?.QueueCreateFromGuid(ownerItem, templateGuid, rarity) == true;
            if (!queued)
            {
                SetStatus($"已生成 {createdCount}/{count} 件；下一条命令无法排队，请稍后重试。", false);
                return;
            }

            SetStatus($"正在生成 {templateName}：{index + 1}/{count}…", true);
            var commandCompleted = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100);
                if (_quantumCommandHook?.TryReadCreateCompletion(out var completed, out var succeeded, out _) != true || !completed)
                {
                    continue;
                }

                commandCompleted = true;
                if (!succeeded)
                {
                    SetStatus($"已生成 {createdCount}/{count} 件；游戏拒绝继续生成，没有重复执行失败命令。", false);
                    return;
                }
                createdCount++;
                break;
            }

            if (!commandCompleted)
            {
                SetStatus($"已生成 {createdCount}/{count} 件；等待第 {index + 1} 件超时，未重复执行。", false);
                return;
            }
        }

        SetStatus($"已按“{templateName}”生成 {createdCount} 件并加入当前角色背包。", true);
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

    private static string GetCurrencyDisplayName(string currency) => currency switch
    {
        "Copper" => "铜币",
        "Silver" => "银币",
        _ => "金币"
    };

    private void Disconnect()
    {
        _itemCapture?.Dispose();
        _itemSelectionHook?.Dispose();
        _quantumCommandHook?.Dispose();
        _inventoryCallHook?.Dispose();
        _playerContextHook?.Dispose();
        _itemCapture = null;
        _itemSelectionHook = null;
        _quantumCommandHook = null;
        _inventoryCallHook = null;
        _playerContextHook = null;
        _playerContext = null;
        _selectedItemEntity = null;
        _pendingTemplateCaptures.Clear();
        _stalePlayerContextHookDetected = false;
        _currencyAddress = null;
        CurrencyCaptureText.Text = "选择币种并输入增加数量";
        _itemQuantitySite = null;
        _playerContextSite = null;
        _itemSelectionSite = null;
        _session?.Dispose();
        _session = null;
        UpdateConnectedFeatureAvailability(true);
        ConnectionText.Text = "未连接";
        ConnectionText.Foreground = (Brush)FindResource("WarningBrush");
        ConnectionIndicator.Fill = (Brush)FindResource("WarningBrush");
        ConnectButtonLabel.Text = "连接游戏";
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
