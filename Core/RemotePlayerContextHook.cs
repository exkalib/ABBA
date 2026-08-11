namespace NRftWManagerUI.Core;

/// <summary>
/// Captures the two raw values arriving at the verified player-update entry.
/// A separately requested inventory probe may call one verified game API once on the update thread.
/// </summary>
internal sealed class RemotePlayerContextHook : IDisposable
{
    private const int FrameOffset = 0x800;
    private const int HeroOffset = 0x808;
    private const int InventoryComponentOffset = 0x810;
    private const int HeroComponentOffset = 0x818;
    private const int HeroComponentSuccessOffset = 0x820;
    private const int InventoryProbeRequestOffset = 0x828;
    private const int InventoryProbeCompletedOffset = 0x82C;
    private const int StatsGroupOffset = 0x840;
    private const int StatsGroupSuccessOffset = 0x880;
    private const int CommandOffset = 0x900;
    private const int CommandArgumentOffset = 0x904;
    private const int CommandOptionOffset = 0x908;
    private const int SelectedItemOffset = 0x910;
    private const int CompletedCommandOffset = 0x918;

    private readonly GameSession _session;
    private readonly long _site;
    private readonly byte[] _replacedBytes;
    private readonly long _getInventoryComponent;
    private readonly long _getHeroComponent;
    private readonly long _getHeroStats;
    private readonly long _levelUp;
    private readonly long _changeItemRarity;
    private readonly long _addItemEnchantment;
    private readonly long _duplicateItem;
    private readonly long _addItemToInventory;
    private readonly long _repairItem;
    private readonly long _getItemData;
    private readonly long _createItem;
    private readonly long _setItemLevel;
    private readonly long _awardGloamseed;
    private readonly long _giveMaxStats;
    private readonly long _unlockFastTravel;
    private IntPtr _trampoline;

    public RemotePlayerContextHook(
        GameSession session,
        long site,
        byte[] expectedEntry,
        long getInventoryComponent,
        long getHeroComponent,
        long getHeroStats,
        long levelUp,
        long changeItemRarity,
        long addItemEnchantment,
        long duplicateItem,
        long addItemToInventory,
        long repairItem,
        long getItemData,
        long createItem,
        long setItemLevel,
        long awardGloamseed,
        long giveMaxStats,
        long unlockFastTravel)
    {
        _session = session;
        _site = site;
        _replacedBytes = session.Read(site, expectedEntry.Length);
        _getInventoryComponent = getInventoryComponent;
        _getHeroComponent = getHeroComponent;
        _getHeroStats = getHeroStats;
        _levelUp = levelUp;
        _changeItemRarity = changeItemRarity;
        _addItemEnchantment = addItemEnchantment;
        _duplicateItem = duplicateItem;
        _addItemToInventory = addItemToInventory;
        _repairItem = repairItem;
        _getItemData = getItemData;
        _createItem = createItem;
        _setItemLevel = setItemLevel;
        _awardGloamseed = awardGloamseed;
        _giveMaxStats = giveMaxStats;
        _unlockFastTravel = unlockFastTravel;

        if (!_replacedBytes.SequenceEqual(expectedEntry))
        {
            throw new InvalidOperationException("玩家更新入口与当前版本配置不匹配，已拒绝启用角色上下文定位。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;

    public string Arm()
    {
        if (IsArmed)
        {
            return "角色上下文定位已开启。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在玩家更新入口附近建立临时定位区；本次不会写入游戏。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();

        // Stage one intentionally captures only the two incoming values. MOV does not alter registers
        // or flags, and no game API is called from this trampoline.
        AddStoreRipRelative(code, trampolineAddress, FrameOffset, new byte[] { 0x4C, 0x89, 0x05 });
        AddStoreRipRelative(code, trampolineAddress, HeroOffset, new byte[] { 0x4C, 0x89, 0x0D });

        // Preserve flags and volatile registers. The inventory resolver is called once only when the
        // desktop app sets InventoryProbeRequestOffset; normal updates take the skip path.
        code.AddRange(new byte[] { 0x9C, 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53 });
        code.AddRange(new byte[] { 0x31, 0xC0 });
        AddLoadRipRelative(code, trampolineAddress, InventoryProbeRequestOffset, new byte[] { 0x87, 0x05 });
        code.AddRange(new byte[] { 0x85, 0xC0, 0x74, 0x00 });
        var skipInventoryProbeOffset = code.Count - 1;
        var inventoryProbeStart = code.Count;
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 });
        AddCallGetInventoryComponent(code, trampolineAddress);
        AddStoreInt32RipRelative(code, trampolineAddress, InventoryProbeCompletedOffset, 1);
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        code[skipInventoryProbeOffset] = checked((byte)(code.Count - inventoryProbeStart));
        code.AddRange(new byte[] { 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D });
        code.AddRange(_replacedBytes);
        code.Add(0xE9);
        var returnOffset = checked((int)((_site + _replacedBytes.Length) - (trampolineAddress + code.Count + sizeof(int))));
        code.AddRange(BitConverter.GetBytes(returnOffset));

        if (!_session.Write(trampolineAddress, code.ToArray()))
        {
            Dispose();
            return "写入角色上下文定位区失败；未修改游戏指令。";
        }
        _session.Flush(trampolineAddress, code.Count);

        var jumpOffset = checked((int)(trampolineAddress - (_site + 5)));
        var patch = new byte[_replacedBytes.Length];
        patch[0] = 0xE9;
        BitConverter.GetBytes(jumpOffset).CopyTo(patch, 1);
        for (var index = 5; index < patch.Length; index++)
        {
            patch[index] = 0x90;
        }

        if (!_session.Write(_site, patch))
        {
            Dispose();
            return "启用角色上下文定位失败；原始指令未改动。";
        }

        _session.Flush(_site, patch.Length);
        return "最小角色捕获已开启：默认只记录更新参数；背包函数仅在单独请求时执行一次。";
    }

    public bool TryReadRawCapture(out long firstArgument, out long secondArgument)
    {
        firstArgument = 0;
        secondArgument = 0;
        return IsArmed &&
               TryReadInt64(FrameOffset, out firstArgument) &&
               TryReadInt64(HeroOffset, out secondArgument) &&
               firstArgument != 0 && secondArgument != 0;
    }

    public bool QueueInventoryProbe()
    {
        if (!IsArmed || !TryReadRawCapture(out _, out _))
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        return _session.WriteInt64(root + InventoryComponentOffset, 0) &&
               _session.WriteInt32(root + InventoryProbeCompletedOffset, 0) &&
               _session.WriteInt32(root + InventoryProbeRequestOffset, 1);
    }

    public bool TryReadInventoryProbe(out long inventoryComponent, out bool completed, out bool pending)
    {
        inventoryComponent = 0;
        completed = false;
        pending = false;
        if (!IsArmed ||
            !TryReadInt64(InventoryComponentOffset, out inventoryComponent) ||
            !_session.TryReadInt32(_trampoline.ToInt64() + InventoryProbeCompletedOffset, out var completedValue) ||
            !_session.TryReadInt32(_trampoline.ToInt64() + InventoryProbeRequestOffset, out var requestValue))
        {
            return false;
        }

        completed = completedValue == 1;
        pending = requestValue == 1;
        return true;
    }

    public bool TryReadContext(out PlayerRuntimeContext context)
    {
        context = default;
        if (!IsArmed || !TryReadByte(StatsGroupSuccessOffset, out var statsSuccess) || statsSuccess == 0)
        {
            return false;
        }

        var data = _session.Read(_trampoline.ToInt64() + StatsGroupOffset, 64);
        if (data.Length != 64 || !TryReadInt64(FrameOffset, out var frame) || !TryReadInt64(HeroOffset, out var hero) ||
            !TryReadInt64(InventoryComponentOffset, out var inventory) || !TryReadInt64(HeroComponentOffset, out var heroComponent))
        {
            return false;
        }

        context = new PlayerRuntimeContext(
            frame,
            hero,
            inventory,
            heroComponent,
            BitConverter.ToInt64(data, 8),
            BitConverter.ToInt64(data, 16),
            BitConverter.ToInt64(data, 24),
            BitConverter.ToInt64(data, 32),
            BitConverter.ToInt64(data, 40),
            BitConverter.ToInt64(data, 56));

        return context.Frame != 0 && context.Hero != 0 && context.HasWallet && context.HasHero && context.HasStats;
    }

    public bool TryReadWalletAddress(out long address)
    {
        address = 0;
        if (!TryReadContext(out var context))
        {
            return false;
        }

        address = context.InventoryComponent + sizeof(int);
        return _session.TryReadInt32(address, out _);
    }

    public bool SetSelectedItem(long itemEntity)
    {
        return IsArmed && itemEntity != 0 && _session.WriteInt64(_trampoline.ToInt64() + SelectedItemOffset, itemEntity);
    }

    public bool QueueCommand(PlayerCommand command, int argument = 0, int option = 0)
    {
        if (!IsArmed || command == PlayerCommand.None)
        {
            return false;
        }

        var root = _trampoline.ToInt64();
        if (!_session.TryReadInt32(root + CommandOffset, out var pending) || pending != (int)PlayerCommand.None)
        {
            return false;
        }

        return _session.WriteInt32(root + CommandArgumentOffset, argument) &&
               _session.WriteInt32(root + CommandOptionOffset, option) &&
               _session.WriteInt32(root + CompletedCommandOffset, 0) &&
               _session.WriteInt32(root + CommandOffset, (int)command);
    }

    public bool WasCommandCompleted(PlayerCommand command)
    {
        return IsArmed && _session.TryReadInt32(_trampoline.ToInt64() + CompletedCommandOffset, out var completed) &&
               completed == (int)command;
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

    private void AddCallGetInventoryComponent(List<byte> code, long trampolineAddress)
    {
        AddLoadRipRelative(code, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x15 });
        // IL2CPP emits a hidden MethodInfo* argument in R8 for this static method. Every native
        // call site in the supported build passes null; do the same instead of forwarding the
        // intercepted function's unrelated R8 value.
        code.AddRange(new byte[] { 0x45, 0x33, 0xC0 });
        AddCallAbsolute(code, _getInventoryComponent);
        AddStoreRipRelative(code, trampolineAddress, InventoryComponentOffset, new byte[] { 0x48, 0x89, 0x05 });
    }

    private void AddCallGetHeroComponent(List<byte> code, long trampolineAddress)
    {
        AddLoadRipRelative(code, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLeaRipRelative(code, trampolineAddress, HeroComponentOffset, new byte[] { 0x4C, 0x8D, 0x05 });
        AddCallAbsolute(code, _getHeroComponent);
        AddStoreRipRelative(code, trampolineAddress, HeroComponentSuccessOffset, new byte[] { 0x88, 0x05 });
    }

    private void AddCallGetHeroStats(List<byte> code, long trampolineAddress)
    {
        AddLoadRipRelative(code, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLeaRipRelative(code, trampolineAddress, StatsGroupOffset, new byte[] { 0x4C, 0x8D, 0x05 });
        AddCallAbsolute(code, _getHeroStats);
        AddStoreRipRelative(code, trampolineAddress, StatsGroupSuccessOffset, new byte[] { 0x88, 0x05 });
    }

    private void AddQueuedCommands(List<byte> code, long trampolineAddress)
    {
        AddLoadRipRelative(code, trampolineAddress, CommandOffset, new byte[] { 0x44, 0x8B, 0x25 }); // mov r12d,[rip+command]
        AddStoreInt32RipRelative(code, trampolineAddress, CommandOffset, 0);

        AddConditionalCommand(code, trampolineAddress, PlayerCommand.LevelUp, BuildLevelUpCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.ChangeSelectedItemRarity, BuildChangeRarityCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.AddSelectedItemEnchantment, BuildAddEnchantmentCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.DuplicateSelectedItem, BuildDuplicateCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.RepairSelectedItem, BuildRepairCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.CreateSelectedItem, BuildCreateCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.SetSelectedItemLevel, BuildSetItemLevelCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.AwardGloamseed, BuildAwardGloamseedCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.GiveMaxStats, BuildGiveMaxStatsCommand);
        AddConditionalCommand(code, trampolineAddress, PlayerCommand.UnlockFastTravel, BuildUnlockFastTravelCommand);
    }

    private List<byte> BuildLevelUpCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(body, trampolineAddress, CommandArgumentOffset, new byte[] { 0x44, 0x8B, 0x05 });
        AddCallAbsolute(body, _levelUp);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.LevelUp);
        return body;
    }

    private List<byte> BuildChangeRarityCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(body, trampolineAddress, CommandArgumentOffset, new byte[] { 0x44, 0x8B, 0x05 });
        body.AddRange(new byte[] { 0x41, 0xB9, 0x04, 0x00, 0x00, 0x00 }); // ItemEnchantmentSource.RarityEmber
        AddCallAbsolute(body, _changeItemRarity);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.ChangeSelectedItemRarity);
        return body;
    }

    private List<byte> BuildAddEnchantmentCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(body, trampolineAddress, CommandOptionOffset, new byte[] { 0x44, 0x8B, 0x05 });
        AddCallAbsolute(body, _addItemEnchantment);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.AddSelectedItemEnchantment);
        return body;
    }

    private List<byte> BuildDuplicateCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(body, _duplicateItem);
        body.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x74, 0x00 }); // test rax,rax; jz after insertion when duplication failed
        var skipInsertOffset = body.Count - 1;
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x15 });
        body.AddRange(new byte[] { 0x4C, 0x8B, 0xC0, 0x41, 0xB9, 0x04, 0x00, 0x00, 0x00 }); // r8=duplicate; r9d=Cheat
        AddStoreInt64AtStack(body, 0x20, 0);
        AddCallAbsolute(body, _addItemToInventory);
        body[skipInsertOffset] = checked((byte)(body.Count - skipInsertOffset - 1));
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.DuplicateSelectedItem);
        return body;
    }

    private List<byte> BuildRepairCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(body, _repairItem);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.RepairSelectedItem);
        return body;
    }

    private List<byte> BuildCreateCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddCallAbsolute(body, _getItemData);
        body.AddRange(new byte[] { 0x48, 0x8B, 0xD0 }); // mov rdx,rax (HeroItemData)
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, CommandArgumentOffset, new byte[] { 0x44, 0x8B, 0x05 });
        AddLoadRipRelative(body, trampolineAddress, CommandOptionOffset, new byte[] { 0x44, 0x8B, 0x0D });
        AddStoreInt64AtStack(body, 0x20, 4); // NewInventoryItemSource.Cheat
        AddCallAbsolute(body, _createItem);
        body.AddRange(new byte[] { 0x48, 0x85, 0xC0, 0x74, 0x00 }); // test rax,rax; jz after insertion when creation failed
        var skipInsertOffset = body.Count - 1;
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x15 });
        body.AddRange(new byte[] { 0x4C, 0x8B, 0xC0, 0x41, 0xB9, 0x04, 0x00, 0x00, 0x00 });
        AddStoreInt64AtStack(body, 0x20, 0);
        AddCallAbsolute(body, _addItemToInventory);
        body[skipInsertOffset] = checked((byte)(body.Count - skipInsertOffset - 1));
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.CreateSelectedItem);
        return body;
    }

    private List<byte> BuildSetItemLevelCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(body, trampolineAddress, CommandArgumentOffset, new byte[] { 0x44, 0x8B, 0x05 });
        AddCallAbsolute(body, _setItemLevel);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.SetSelectedItemLevel);
        return body;
    }

    private List<byte> BuildAwardGloamseedCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, HeroComponentOffset, new byte[] { 0x48, 0x8B, 0x15 });
        body.AddRange(new byte[] { 0x8B, 0x52, 0x0C }); // mov edx,[rdx+0xC] RuntimeCharacterReference.Raw
        AddLoadRipRelative(body, trampolineAddress, CommandArgumentOffset, new byte[] { 0x44, 0x8B, 0x05 });
        AddCallAbsolute(body, _awardGloamseed);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.AwardGloamseed);
        return body;
    }

    private List<byte> BuildGiveMaxStatsCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(body, trampolineAddress, HeroOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(body, trampolineAddress, CommandArgumentOffset, new byte[] { 0x44, 0x8B, 0x05 });
        AddCallAbsolute(body, _giveMaxStats);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.GiveMaxStats);
        return body;
    }

    private List<byte> BuildUnlockFastTravelCommand(long trampolineAddress)
    {
        var body = new List<byte>();
        AddLoadRipRelative(body, trampolineAddress, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        body.AddRange(new byte[] { 0x31, 0xD2, 0x45, 0x31, 0xC0 }); // delay and duration FP values = 0
        AddCallAbsolute(body, _unlockFastTravel);
        AddStoreInt32RipRelative(body, trampolineAddress, CompletedCommandOffset, (int)PlayerCommand.UnlockFastTravel);
        return body;
    }

    private static void AddConditionalCommand(List<byte> code, long trampolineAddress, PlayerCommand command, Func<long, List<byte>> buildBody)
    {
        // Build once to choose the branch form. The second build uses the real code address so every
        // RIP-relative instruction points into the allocated page, not to a synthetic offset.
        var preview = buildBody(0);
        var branchLength = preview.Count <= sbyte.MaxValue ? 2 : 6;
        var body = buildBody(trampolineAddress + code.Count + 4 + branchLength);

        code.AddRange(new byte[] { 0x41, 0x83, 0xFC, (byte)command }); // cmp r12d, command
        if (body.Count <= sbyte.MaxValue)
        {
            code.AddRange(new byte[] { 0x75, (byte)body.Count }); // jne after body
        }
        else
        {
            code.AddRange(new byte[] { 0x0F, 0x85 });
            code.AddRange(BitConverter.GetBytes(body.Count));
        }

        code.AddRange(body);
    }

    private bool TryReadByte(int offset, out byte value)
    {
        var bytes = _session.Read(_trampoline.ToInt64() + offset, 1);
        value = bytes.Length == 1 ? bytes[0] : (byte)0;
        return bytes.Length == 1;
    }

    private bool TryReadInt64(int offset, out long value)
    {
        return _session.TryReadInt64(_trampoline.ToInt64() + offset, out value);
    }

    private static void AddCallAbsolute(List<byte> code, long address)
    {
        code.Add(0x48);
        code.Add(0xB8);
        code.AddRange(BitConverter.GetBytes(address));
        code.AddRange(new byte[] { 0xFF, 0xD0 });
    }

    private static void AddLoadRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode)
    {
        code.AddRange(opcode);
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + targetOffset) - nextInstruction))));
    }

    private static void AddLeaRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode)
    {
        AddLoadRipRelative(code, trampolineAddress, targetOffset, opcode);
    }

    private static void AddStoreRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode)
    {
        AddLoadRipRelative(code, trampolineAddress, targetOffset, opcode);
    }

    private static void AddStoreInt32RipRelative(List<byte> code, long trampolineAddress, int targetOffset, int value)
    {
        code.AddRange(new byte[] { 0xC7, 0x05 });
        // C7 /0 uses RIP after the complete instruction: opcode + disp32 + imm32.
        var nextInstruction = trampolineAddress + code.Count + sizeof(int) + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + targetOffset) - nextInstruction))));
        code.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddStoreInt64AtStack(List<byte> code, byte stackOffset, long value)
    {
        code.AddRange(new byte[] { 0x48, 0xC7, 0x44, 0x24, stackOffset });
        code.AddRange(BitConverter.GetBytes(checked((int)value)));
    }
}
