namespace NRftWManagerUI.Core;

/// <summary>Executes one queued item command from Quantum's simulation update thread.</summary>
internal sealed class RemoteQuantumCommandHook : IDisposable
{
    private const int RarityCommand = 1;
    private const int CreateFromTemplateCommand = 2;
    private const int AddGoldCommand = 3;
    private const int SetInfiniteStatCommand = 4;
    private const int InspectItemCommand = 5;
    private const int FrameOffset = 0x800;
    private const int SelectedItemOffset = 0x808;
    private const int RarityOffset = 0x810;
    private const int CommandOffset = 0x814;
    private const int CompletedOffset = 0x818;
    private const int ResultOffset = 0x81C;
    private const int CreatedItemOffset = 0x820;
    private const int InventoryOwnerOffset = 0x828;
    private const int ItemTemplateOffset = 0x830;
    private const int GoldValueOffset = 0x838;
    private const int StatKindOffset = 0x83C;
    private const int StatEnabledOffset = 0x840;
    private const int StatsGroupOffset = 0x848;
    private const int HeroInventoryPointerOffset = 0x888;
    private const int InspectedTemplateOffset = 0x890;
    private const int InspectedGuidOffset = 0x898;
    private const int InspectedPathOffset = 0x8A0;
    private const int InspectedItemTypeOffset = 0x8A8;
    private const int CreateTemplateGuidOffset = 0x8B0;
    private const int CreateTemplateRarityOffset = 0x8B8;
    private const int InspectedRarityOffset = 0x8BC;

    private readonly GameSession _session;
    private readonly long _site;
    private readonly long _changeItemRarity;
    private readonly long _getItemData;
    private readonly long _getItemRarity;
    private readonly long _createItem;
    private readonly long _getInventoryOwner;
    private readonly long _tryGetItemOwner;
    private readonly long _addItemToInventory;
    private readonly long _addGold;
    private readonly long _getHeroStats;
    private readonly long _setHealthInfinite;
    private readonly long _setResourceInfinite;
    private readonly long _tryGetHeroInventoryPointer;
    private readonly long _heroInventoryTypeInfo;
    private readonly long _resolveHeroItemData;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteQuantumCommandHook(
        GameSession session,
        long site,
        byte[] expectedEntry,
        long changeItemRarity,
        long getItemData,
        long getItemRarity,
        long createItem,
        long getInventoryOwner,
        long tryGetItemOwner,
        long addItemToInventory,
        long addGold,
        long getHeroStats,
        long setHealthInfinite,
        long setResourceInfinite,
        long tryGetHeroInventoryPointer,
        long heroInventoryTypeInfo,
        long resolveHeroItemData)
    {
        _session = session;
        _site = site;
        _changeItemRarity = changeItemRarity;
        _getItemData = getItemData;
        _getItemRarity = getItemRarity;
        _createItem = createItem;
        _getInventoryOwner = getInventoryOwner;
        _tryGetItemOwner = tryGetItemOwner;
        _addItemToInventory = addItemToInventory;
        _addGold = addGold;
        _getHeroStats = getHeroStats;
        _setHealthInfinite = setHealthInfinite;
        _setResourceInfinite = setResourceInfinite;
        _tryGetHeroInventoryPointer = tryGetHeroInventoryPointer;
        _heroInventoryTypeInfo = heroInventoryTypeInfo;
        _resolveHeroItemData = resolveHeroItemData;
        _replacedBytes = session.Read(site, expectedEntry.Length);
        if (!_replacedBytes.SequenceEqual(expectedEntry))
        {
            throw new InvalidOperationException("Quantum 更新入口与当前版本配置不匹配，已拒绝启用物品命令。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;

    public string Arm()
    {
        if (IsArmed)
        {
            return "物品命令入口已开启。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在 Quantum 更新入口附近建立命令区；本次不会写入游戏。";
        }

        var root = _trampoline.ToInt64();
        var code = new List<byte>();

        // Update(Frame f) is an instance method, so the simulation Frame arrives in RDX.
        AddStoreRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x89, 0x15 });

        // Preserve flags and all volatile integer registers before calling a game function.
        code.AddRange(new byte[] { 0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53 });
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });

        AddLoadRipRelative(code, root, CommandOffset, new byte[] { 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x85, 0xC0, 0x0F, 0x84 });
        var noCommandJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        code.AddRange(new byte[] { 0x83, 0xF8, RarityCommand, 0x0F, 0x84 });
        var rarityJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x83, 0xF8, CreateFromTemplateCommand, 0x0F, 0x84 });
        var createJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x83, 0xF8, AddGoldCommand, 0x0F, 0x84 });
        var setGoldJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x83, 0xF8, SetInfiniteStatCommand, 0x0F, 0x84 });
        var setInfiniteStatJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x83, 0xF8, InspectItemCommand, 0x0F, 0x84 });
        var inspectItemJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        AddStoreInt32RipRelative(code, root, CommandOffset, 0);
        var unknownCommandJump = AddNearJump(code);

        var rarityOffset = code.Count;
        AddStoreInt32RipRelative(code, root, CommandOffset, 0);
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(code, root, RarityOffset, new byte[] { 0x44, 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x41, 0xB9, 0x04, 0x00, 0x00, 0x00 }); // RarityEmber
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(_changeItemRarity));
        code.AddRange(new byte[] { 0xFF, 0xD0, 0x0F, 0xB6, 0xC0 });
        AddStoreRipRelative(code, root, ResultOffset, new byte[] { 0x89, 0x05 });
        AddStoreInt32RipRelative(code, root, CompletedOffset, RarityCommand);
        var rarityDoneJump = AddNearJump(code);

        var createOffset = code.Count;
        AddStoreInt32RipRelative(code, root, CommandOffset, 0);

        // Resolve the selected item's current owner before creating anything.
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(code, _getInventoryOwner);
        AddStoreRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x84 });
        var missingOwnerJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        // A persisted catalog entry resolves its static HeroItemData by GUID. A zero GUID keeps
        // the existing behavior and reads the template from the selected runtime item.
        AddLoadRipRelative(code, root, CreateTemplateGuidOffset, new byte[] { 0x48, 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x84 });
        var resolveSelectedTemplateJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        AddLeaRipRelative(code, root, CreateTemplateGuidOffset, new byte[] { 0x48, 0x8D, 0x0D });
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(code, _resolveHeroItemData);
        var templateResolvedJump = AddNearJump(code);

        var resolveSelectedTemplateOffset = code.Count;
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(code, _getItemData);
        var templateResolvedOffset = code.Count;
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x84 });
        var missingTemplateJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        AddStoreRipRelative(code, root, ItemTemplateOffset, new byte[] { 0x48, 0x89, 0x05 });

        // Persisted catalog templates carry the captured rarity; live templates read it now.
        AddLoadRipRelative(code, root, CreateTemplateGuidOffset, new byte[] { 0x48, 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x85 });
        var loadPersistedRarityJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(code, _getItemRarity);
        code.AddRange(new byte[] { 0x44, 0x8B, 0xC8 }); // r9d = current rarity
        var rarityResolvedJump = AddNearJump(code);

        var loadPersistedRarityOffset = code.Count;
        AddLoadRipRelative(code, root, CreateTemplateRarityOffset, new byte[] { 0x44, 0x8B, 0x0D });
        var rarityResolvedOffset = code.Count;

        // ItemsAPI.Create(Frame, HeroItemData, 1, currentRarity, Cheat) creates fresh persistent data.
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, ItemTemplateOffset, new byte[] { 0x48, 0x8B, 0x15 });
        code.AddRange(new byte[] { 0x41, 0xB8, 0x01, 0x00, 0x00, 0x00 }); // r8d = count 1
        code.AddRange(new byte[] { 0xC7, 0x44, 0x24, 0x20, 0x04, 0x00, 0x00, 0x00 }); // source = Cheat
        AddCallAbsolute(code, _createItem);
        AddStoreRipRelative(code, root, CreatedItemOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x84 });
        var createFailedJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(code, root, CreatedItemOffset, new byte[] { 0x4C, 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x41, 0xB9, 0x04, 0x00, 0x00, 0x00 }); // source = Cheat
        code.AddRange(new byte[] { 0xC6, 0x44, 0x24, 0x20, 0x00 }); // suppressNotification = false
        AddCallAbsolute(code, _addItemToInventory);
        code.AddRange(new byte[] { 0x0F, 0xB6, 0xC0 });
        AddStoreRipRelative(code, root, ResultOffset, new byte[] { 0x89, 0x05 });
        AddStoreInt32RipRelative(code, root, CompletedOffset, CreateFromTemplateCommand);
        var createDoneJump = AddNearJump(code);

        var createFailureOffset = code.Count;
        AddStoreInt32RipRelative(code, root, ResultOffset, 0);
        AddStoreInt32RipRelative(code, root, CompletedOffset, CreateFromTemplateCommand);
        var createFailureDoneJump = AddNearJump(code);

        var setGoldOffset = code.Count;
        AddStoreInt32RipRelative(code, root, CommandOffset, 0);

        // Resolve the current hero from any item in that hero's backpack.
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLeaRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x4C, 0x8D, 0x05 });
        AddCallAbsolute(code, _tryGetItemOwner);
        code.AddRange(new byte[] { 0x84, 0xC0, 0x0F, 0x84 });
        var missingGoldOwnerJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        // A zero-value native call initializes the generated HeroInventoryComponent type metadata
        // without changing state or raising events. Resolve the live component pointer afterwards.
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x48, 0x8B, 0x15 });
        code.AddRange(new byte[] { 0x45, 0x33, 0xC0 }); // r8d = 0
        code.AddRange(new byte[] { 0x41, 0xB9, 0x0E, 0x00, 0x00, 0x00 }); // source = Payload
        code.AddRange(new byte[] { 0xC6, 0x44, 0x24, 0x20, 0x01 }); // suppressNotification = true
        AddCallAbsolute(code, _addGold);

        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(_heroInventoryTypeInfo));
        code.AddRange(new byte[] { 0x48, 0x8B, 0x00, 0x48, 0x85, 0xC0, 0x0F, 0x84 });
        var missingInventoryTypeJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x48, 0x38, 0x4D, 0x85, 0xC9, 0x0F, 0x84 });
        var missingInventoryMethodTableJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x4D, 0x8B, 0x49, 0x08 });
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        code.AddRange(new byte[] { 0x48, 0x83, 0xC1, 0x60 });
        AddLoadRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLeaRipRelative(code, root, HeroInventoryPointerOffset, new byte[] { 0x4C, 0x8D, 0x05 });
        AddCallAbsolute(code, _tryGetHeroInventoryPointer);
        code.AddRange(new byte[] { 0x84, 0xC0, 0x0F, 0x84 });
        var missingHeroInventoryJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        AddLoadRipRelative(code, root, HeroInventoryPointerOffset, new byte[] { 0x48, 0x8B, 0x0D });
        code.AddRange(new byte[] { 0x48, 0x85, 0xC9, 0x0F, 0x84 });
        var nullHeroInventoryJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        AddLoadRipRelative(code, root, GoldValueOffset, new byte[] { 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x8B, 0x51, 0x04, 0x03, 0xD0, 0x0F, 0x88 });
        var goldOverflowJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x89, 0x51, 0x04 });
        AddStoreInt32RipRelative(code, root, ResultOffset, 1);
        AddStoreInt32RipRelative(code, root, CompletedOffset, AddGoldCommand);
        var setGoldDoneJump = AddNearJump(code);

        var setGoldFailureOffset = code.Count;
        AddStoreInt32RipRelative(code, root, ResultOffset, 0);
        AddStoreInt32RipRelative(code, root, CompletedOffset, AddGoldCommand);
        var setGoldFailureDoneJump = AddNearJump(code);

        var setInfiniteStatOffset = code.Count;
        AddStoreInt32RipRelative(code, root, CommandOffset, 0);

        // Resolve the hero from the selected backpack item, then obtain live stat pointers.
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLeaRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x4C, 0x8D, 0x05 });
        AddCallAbsolute(code, _tryGetItemOwner);
        code.AddRange(new byte[] { 0x84, 0xC0, 0x0F, 0x84 });
        var missingStatOwnerJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, InventoryOwnerOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLeaRipRelative(code, root, StatsGroupOffset, new byte[] { 0x4C, 0x8D, 0x05 });
        AddCallAbsolute(code, _getHeroStats);
        code.AddRange(new byte[] { 0x84, 0xC0, 0x0F, 0x84 });
        var missingStatsJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        AddLoadRipRelative(code, root, StatKindOffset, new byte[] { 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x85, 0xC0, 0x0F, 0x84 });
        var healthJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x83, 0xF8, 0x01, 0x0F, 0x84 });
        var staminaJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        code.AddRange(new byte[] { 0x83, 0xF8, 0x02, 0x0F, 0x84 });
        var focusJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        var invalidStatJump = AddNearJump(code);

        var healthOffset = code.Count;
        AddLoadRipRelative(code, root, StatsGroupOffset + 0x10, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, StatEnabledOffset, new byte[] { 0x8B, 0x15 });
        AddCallAbsolute(code, _setHealthInfinite);
        var healthDoneJump = AddNearJump(code);

        var staminaOffset = code.Count;
        AddLoadRipRelative(code, root, StatsGroupOffset + 0x18, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, StatEnabledOffset, new byte[] { 0x8B, 0x15 });
        AddCallAbsolute(code, _setResourceInfinite);
        var staminaDoneJump = AddNearJump(code);

        var focusOffset = code.Count;
        AddLoadRipRelative(code, root, StatsGroupOffset + 0x28, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, StatEnabledOffset, new byte[] { 0x8B, 0x15 });
        AddCallAbsolute(code, _setResourceInfinite);

        var setInfiniteStatSuccessOffset = code.Count;
        AddStoreInt32RipRelative(code, root, ResultOffset, 1);
        AddStoreInt32RipRelative(code, root, CompletedOffset, SetInfiniteStatCommand);
        var setInfiniteStatDoneJump = AddNearJump(code);

        var setInfiniteStatFailureOffset = code.Count;
        AddStoreInt32RipRelative(code, root, ResultOffset, 0);
        AddStoreInt32RipRelative(code, root, CompletedOffset, SetInfiniteStatCommand);
        var setInfiniteStatFailureDoneJump = AddNearJump(code);

        var inspectItemOffset = code.Count;
        AddStoreInt32RipRelative(code, root, CommandOffset, 0);
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(code, _getItemData);
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x0F, 0x84 });
        var inspectFailedJump = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        AddStoreRipRelative(code, root, InspectedTemplateOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x50, 0x18 });
        AddStoreRipRelative(code, root, InspectedGuidOffset, new byte[] { 0x48, 0x89, 0x15 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x50, 0x10 });
        AddStoreRipRelative(code, root, InspectedPathOffset, new byte[] { 0x48, 0x89, 0x15 });
        code.AddRange(new byte[] { 0x8B, 0x50, 0x20 });
        AddStoreRipRelative(code, root, InspectedItemTypeOffset, new byte[] { 0x89, 0x15 });
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(code, _getItemRarity);
        AddStoreRipRelative(code, root, InspectedRarityOffset, new byte[] { 0x89, 0x05 });
        AddStoreInt32RipRelative(code, root, ResultOffset, 1);
        AddStoreInt32RipRelative(code, root, CompletedOffset, InspectItemCommand);
        var inspectDoneJump = AddNearJump(code);

        var inspectFailureOffset = code.Count;
        AddStoreInt32RipRelative(code, root, ResultOffset, 0);
        AddStoreInt32RipRelative(code, root, CompletedOffset, InspectItemCommand);

        var restoreOffset = code.Count;
        PatchNearJump(code, noCommandJump, restoreOffset);
        PatchNearJump(code, rarityJump, rarityOffset);
        PatchNearJump(code, createJump, createOffset);
        PatchNearJump(code, resolveSelectedTemplateJump, resolveSelectedTemplateOffset);
        PatchNearJump(code, templateResolvedJump, templateResolvedOffset);
        PatchNearJump(code, loadPersistedRarityJump, loadPersistedRarityOffset);
        PatchNearJump(code, rarityResolvedJump, rarityResolvedOffset);
        PatchNearJump(code, setGoldJump, setGoldOffset);
        PatchNearJump(code, setInfiniteStatJump, setInfiniteStatOffset);
        PatchNearJump(code, inspectItemJump, inspectItemOffset);
        PatchNearJump(code, unknownCommandJump, restoreOffset);
        PatchNearJump(code, rarityDoneJump, restoreOffset);
        PatchNearJump(code, missingOwnerJump, createFailureOffset);
        PatchNearJump(code, missingTemplateJump, createFailureOffset);
        PatchNearJump(code, createFailedJump, createFailureOffset);
        PatchNearJump(code, createDoneJump, restoreOffset);
        PatchNearJump(code, createFailureDoneJump, restoreOffset);
        PatchNearJump(code, missingGoldOwnerJump, setGoldFailureOffset);
        PatchNearJump(code, setGoldDoneJump, restoreOffset);
        PatchNearJump(code, setGoldFailureDoneJump, restoreOffset);
        PatchNearJump(code, missingInventoryTypeJump, setGoldFailureOffset);
        PatchNearJump(code, missingInventoryMethodTableJump, setGoldFailureOffset);
        PatchNearJump(code, missingHeroInventoryJump, setGoldFailureOffset);
        PatchNearJump(code, nullHeroInventoryJump, setGoldFailureOffset);
        PatchNearJump(code, goldOverflowJump, setGoldFailureOffset);
        PatchNearJump(code, missingStatOwnerJump, setInfiniteStatFailureOffset);
        PatchNearJump(code, missingStatsJump, setInfiniteStatFailureOffset);
        PatchNearJump(code, healthJump, healthOffset);
        PatchNearJump(code, staminaJump, staminaOffset);
        PatchNearJump(code, focusJump, focusOffset);
        PatchNearJump(code, invalidStatJump, setInfiniteStatFailureOffset);
        PatchNearJump(code, healthDoneJump, setInfiniteStatSuccessOffset);
        PatchNearJump(code, staminaDoneJump, setInfiniteStatSuccessOffset);
        PatchNearJump(code, setInfiniteStatDoneJump, restoreOffset);
        PatchNearJump(code, setInfiniteStatFailureDoneJump, restoreOffset);
        PatchNearJump(code, inspectFailedJump, inspectFailureOffset);
        PatchNearJump(code, inspectDoneJump, restoreOffset);
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        code.AddRange(new byte[] { 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D });

        code.AddRange(_replacedBytes);
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(checked((int)((_site + _replacedBytes.Length) - (root + code.Count + sizeof(int))))));

        if (code.Count >= FrameOffset)
        {
            Dispose();
            return "物品命令代码超过安全区域；已拒绝写入游戏。";
        }

        if (!_session.Write(root, code.ToArray()))
        {
            Dispose();
            return "写入物品命令区失败；未修改游戏指令。";
        }
        _session.Flush(root, code.Count);

        var patch = new byte[_replacedBytes.Length];
        patch[0] = 0xE9;
        BitConverter.GetBytes(checked((int)(root - (_site + 5)))).CopyTo(patch, 1);
        Array.Fill(patch, (byte)0x90, 5, patch.Length - 5);
        if (!_session.Write(_site, patch))
        {
            Dispose();
            return "启用物品命令入口失败；原始指令未改动。";
        }
        _session.Flush(_site, patch.Length);
        return "物品命令入口已开启：命令只会在 Quantum 模拟线程执行一次。";
    }

    public bool QueueRarity(long selectedItem, int rarity)
    {
        if (!IsArmed || selectedItem == 0 || rarity is < 0 or > 2)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != 0)
        {
            return false;
        }

        return _session.WriteInt64(root + SelectedItemOffset, selectedItem) &&
               _session.WriteInt32(root + RarityOffset, rarity) &&
               _session.WriteInt32(root + CompletedOffset, 0) &&
               _session.WriteInt32(root + ResultOffset, 0) &&
               _session.WriteInt32(root + CommandOffset, RarityCommand);
    }

    public bool QueueCreateFromTemplate(long selectedItem)
    {
        if (!IsArmed || selectedItem == 0)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != 0)
        {
            return false;
        }

        return _session.WriteInt64(root + SelectedItemOffset, selectedItem) &&
               _session.WriteInt32(root + CompletedOffset, 0) &&
               _session.WriteInt32(root + ResultOffset, 0) &&
               _session.WriteInt64(root + CreatedItemOffset, 0) &&
               _session.WriteInt64(root + InventoryOwnerOffset, 0) &&
               _session.WriteInt64(root + ItemTemplateOffset, 0) &&
               _session.WriteInt64(root + CreateTemplateGuidOffset, 0) &&
               _session.WriteInt32(root + CreateTemplateRarityOffset, 0) &&
               _session.WriteInt32(root + CommandOffset, CreateFromTemplateCommand);
    }

    public bool QueueCreateFromGuid(long ownerItem, long templateGuid, int rarity)
    {
        if (!IsArmed || ownerItem == 0 || templateGuid == 0 || rarity is < 0 or > 3)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != 0)
        {
            return false;
        }

        return _session.WriteInt64(root + SelectedItemOffset, ownerItem) &&
               _session.WriteInt32(root + CompletedOffset, 0) &&
               _session.WriteInt32(root + ResultOffset, 0) &&
               _session.WriteInt64(root + CreatedItemOffset, 0) &&
               _session.WriteInt64(root + InventoryOwnerOffset, 0) &&
               _session.WriteInt64(root + ItemTemplateOffset, 0) &&
               _session.WriteInt64(root + CreateTemplateGuidOffset, templateGuid) &&
               _session.WriteInt32(root + CreateTemplateRarityOffset, rarity) &&
               _session.WriteInt32(root + CommandOffset, CreateFromTemplateCommand);
    }

    public bool TryReadRarityCompletion(out bool completed, out bool succeeded) =>
        TryReadCompletion(RarityCommand, out completed, out succeeded);

    public bool QueueAddGold(long selectedItem, int gold)
    {
        if (!IsArmed || selectedItem == 0 || gold <= 0)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != 0)
        {
            return false;
        }

        return _session.WriteInt64(root + SelectedItemOffset, selectedItem) &&
               _session.WriteInt32(root + GoldValueOffset, gold) &&
               _session.WriteInt32(root + CompletedOffset, 0) &&
               _session.WriteInt32(root + ResultOffset, 0) &&
               _session.WriteInt64(root + InventoryOwnerOffset, 0) &&
               _session.WriteInt32(root + CommandOffset, AddGoldCommand);
    }

    public bool TryReadGoldCompletion(out bool completed, out bool succeeded) =>
        TryReadCompletion(AddGoldCommand, out completed, out succeeded);

    public bool QueueInfiniteStat(long selectedItem, int statKind, bool enabled)
    {
        if (!IsArmed || selectedItem == 0 || statKind is < 0 or > 2)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != 0)
        {
            return false;
        }

        return _session.WriteInt64(root + SelectedItemOffset, selectedItem) &&
               _session.WriteInt32(root + StatKindOffset, statKind) &&
               _session.WriteInt32(root + StatEnabledOffset, enabled ? 1 : 0) &&
               _session.WriteInt32(root + CompletedOffset, 0) &&
               _session.WriteInt32(root + ResultOffset, 0) &&
               _session.WriteInt64(root + InventoryOwnerOffset, 0) &&
               _session.WriteInt32(root + CommandOffset, SetInfiniteStatCommand);
    }

    public bool TryReadInfiniteStatCompletion(out bool completed, out bool succeeded) =>
        TryReadCompletion(SetInfiniteStatCommand, out completed, out succeeded);

    public bool QueueItemInspection(long selectedItem)
    {
        if (!IsArmed || selectedItem == 0)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != 0)
        {
            return false;
        }

        return _session.WriteInt64(root + SelectedItemOffset, selectedItem) &&
               _session.WriteInt32(root + CompletedOffset, 0) &&
               _session.WriteInt32(root + ResultOffset, 0) &&
               _session.WriteInt64(root + InspectedTemplateOffset, 0) &&
               _session.WriteInt64(root + InspectedGuidOffset, 0) &&
               _session.WriteInt64(root + InspectedPathOffset, 0) &&
               _session.WriteInt32(root + InspectedItemTypeOffset, 0) &&
               _session.WriteInt32(root + InspectedRarityOffset, 0) &&
               _session.WriteInt32(root + CommandOffset, InspectItemCommand);
    }

    public bool TryReadItemInspectionCompletion(
        out bool completed,
        out bool succeeded,
        out long template,
        out long guid,
        out long path,
        out int itemType,
        out int rarity)
    {
        template = 0;
        guid = 0;
        path = 0;
        itemType = 0;
        rarity = 0;
        var root = _trampoline.ToInt64();
        return TryReadCompletion(InspectItemCommand, out completed, out succeeded) &&
               _session.TryReadInt64(root + InspectedTemplateOffset, out template) &&
               _session.TryReadInt64(root + InspectedGuidOffset, out guid) &&
               _session.TryReadInt64(root + InspectedPathOffset, out path) &&
               _session.TryReadInt32(root + InspectedItemTypeOffset, out itemType) &&
               _session.TryReadInt32(root + InspectedRarityOffset, out rarity);
    }

    public bool TryReadCreateCompletion(out bool completed, out bool succeeded, out long createdItem)
    {
        createdItem = 0;
        return TryReadCompletion(CreateFromTemplateCommand, out completed, out succeeded) &&
               _session.TryReadInt64(_trampoline.ToInt64() + CreatedItemOffset, out createdItem);
    }

    private bool TryReadCompletion(int expectedCommand, out bool completed, out bool succeeded)
    {
        completed = false;
        succeeded = false;
        if (!IsArmed || !_session.TryReadInt32(_trampoline.ToInt64() + CompletedOffset, out var completedValue))
        {
            return false;
        }

        completed = completedValue == expectedCommand;
        if (!_session.TryReadInt32(_trampoline.ToInt64() + ResultOffset, out var result))
        {
            return false;
        }

        succeeded = result != 0;
        return true;
    }

    public void Dispose()
    {
        if (_trampoline == IntPtr.Zero)
        {
            return;
        }

        _session.Write(_site, _replacedBytes);
        _session.Flush(_site, _replacedBytes.Length);
        _session.Free(_trampoline);
        _trampoline = IntPtr.Zero;
    }

    private static void AddLoadRipRelative(List<byte> code, long root, int targetOffset, byte[] opcode)
    {
        code.AddRange(opcode);
        code.AddRange(BitConverter.GetBytes(checked((int)((root + targetOffset) - (root + code.Count + sizeof(int))))));
    }

    private static void AddStoreRipRelative(List<byte> code, long root, int targetOffset, byte[] opcode) =>
        AddLoadRipRelative(code, root, targetOffset, opcode);

    private static void AddLeaRipRelative(List<byte> code, long root, int targetOffset, byte[] opcode) =>
        AddLoadRipRelative(code, root, targetOffset, opcode);

    private static void AddStoreInt32RipRelative(List<byte> code, long root, int targetOffset, int value)
    {
        code.AddRange(new byte[] { 0xC7, 0x05 });
        code.AddRange(BitConverter.GetBytes(checked((int)((root + targetOffset) - (root + code.Count + sizeof(int) + sizeof(int))))));
        code.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddCallAbsolute(List<byte> code, long address)
    {
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(address));
        code.AddRange(new byte[] { 0xFF, 0xD0 });
    }

    private static int AddNearJump(List<byte> code)
    {
        code.Add(0xE9);
        var displacementOffset = code.Count;
        code.AddRange(new byte[sizeof(int)]);
        return displacementOffset;
    }

    private static void PatchNearJump(List<byte> code, int displacementOffset, int targetOffset)
    {
        var displacement = BitConverter.GetBytes(targetOffset - (displacementOffset + sizeof(int)));
        for (var index = 0; index < displacement.Length; index++)
        {
            code[displacementOffset + index] = displacement[index];
        }
    }
}
