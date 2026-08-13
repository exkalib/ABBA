namespace NRftWManagerUI.Core;

/// <summary>Records the selected ItemSlot entity, view asset, and resolved display name.</summary>
internal sealed class RemoteItemSelectionHook : IDisposable
{
    private const int ItemOffset = 0x800;
    private const int ItemAssetOffset = 0x808;
    private const int LocalizedNameOffset = 0x810;
    private const int PendingItemOffset = 0x818;
    private const int ItemSlotOffset = 0x820;

    private readonly GameSession _session;
    private readonly long _site;
    private readonly long _getItemName;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteItemSelectionHook(GameSession session, long site, long getItemName, byte[] expectedEntry)
    {
        _session = session;
        _site = site;
        _getItemName = getItemName;
        _replacedBytes = session.Read(site, expectedEntry.Length);
        if (!_replacedBytes.SequenceEqual(expectedEntry))
        {
            throw new InvalidOperationException("物品详情入口与当前版本配置不匹配，已拒绝启用物品选择捕获。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;

    public string Arm()
    {
        if (IsArmed)
        {
            return "物品选择捕获已开启。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在物品详情入口附近建立捕获区；本次不会写入游戏。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();
        code.Add(0x50); // push rax
        code.AddRange(new byte[] { 0x48, 0x31, 0xC0 }); // xor rax,rax
        AddStoreRipRelative(code, trampolineAddress, ItemOffset, new byte[] { 0x48, 0x89, 0x05 });
        AddStoreRipRelative(code, trampolineAddress, LocalizedNameOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x89, 0xC8 }); // mov rax,rcx
        AddStoreRipRelative(code, trampolineAddress, ItemSlotOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x41, 0x20 }); // mov rax,[rcx+0x20] ItemSlotVisual
        code.AddRange(new byte[] { 0x48, 0x85, 0xC0 }); // test rax,rax
        var skipVisualAssetLoad = code.Count;
        code.AddRange(new byte[] { 0x74, 0x07 }); // je short store-null
        code.AddRange(new byte[] { 0x48, 0x8B, 0x80, 0x88, 0x01, 0x00, 0x00 }); // mov rax,[rax+0x188] m_slotPopulateData.ItemDataAsset
        code[skipVisualAssetLoad + 1] = checked((byte)(code.Count - (skipVisualAssetLoad + 2)));
        AddStoreRipRelative(code, trampolineAddress, ItemAssetOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x81, 0x00, 0x01, 0x00, 0x00 }); // mov rax,[rcx+0x100] ItemRef.Entity
        AddStoreRipRelative(code, trampolineAddress, PendingItemOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.Add(0x58); // pop rax
        code.AddRange(_replacedBytes);

        // Resolve through the same game function used by the inventory UI. It follows
        // ItemNameMsgRef when the legacy ItemNameMsg field is empty.
        code.AddRange(new byte[] { 0x48, 0x81, 0xEC, 0xC0, 0x00, 0x00, 0x00 }); // sub rsp,C0
        AddSaveVolatileRegisters(code);
        AddLoadRipRelative(code, trampolineAddress, ItemAssetOffset, new byte[] { 0x48, 0x8B, 0x0D }); // mov rcx,[asset]
        code.AddRange(new byte[] { 0x48, 0x85, 0xC9 }); // test rcx,rcx
        var skipNameCall = code.Count;
        code.AddRange(new byte[] { 0x74, 0x00 }); // je short publish
        code.AddRange(new byte[] { 0x48, 0x31, 0xD2 }); // xor rdx,rdx (MethodInfo*)
        code.AddRange(new byte[] { 0x48, 0xB8 }); // mov rax,imm64
        code.AddRange(BitConverter.GetBytes(_getItemName));
        code.AddRange(new byte[] { 0xFF, 0xD0 }); // call rax
        AddStoreRipRelative(code, trampolineAddress, LocalizedNameOffset, new byte[] { 0x48, 0x89, 0x05 });
        code[skipNameCall + 1] = checked((byte)(code.Count - (skipNameCall + 2)));

        // ItemOffset is the ready flag. Publish it only after the name result is final.
        AddLoadRipRelative(code, trampolineAddress, PendingItemOffset, new byte[] { 0x48, 0x8B, 0x05 });
        AddStoreRipRelative(code, trampolineAddress, ItemOffset, new byte[] { 0x48, 0x89, 0x05 });
        AddRestoreVolatileRegisters(code);
        code.AddRange(new byte[] { 0x48, 0x81, 0xC4, 0xC0, 0x00, 0x00, 0x00 }); // add rsp,C0
        code.Add(0xE9);
        var returnOffset = checked((int)((_site + _replacedBytes.Length) - (trampolineAddress + code.Count + sizeof(int))));
        code.AddRange(BitConverter.GetBytes(returnOffset));

        if (!_session.Write(trampolineAddress, code.ToArray()))
        {
            Dispose();
            return "写入物品选择捕获区失败；未修改游戏指令。";
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
            return "启用物品选择捕获失败；原始指令未改动。";
        }

        _session.Flush(_site, patch.Length);
        return "物品选择捕获已开启。回游戏在背包中点击目标物品，地址会自动更新。";
    }

    public bool TryReadSelection(out long itemEntity, out long itemSlot, out long itemAsset, out long localizedName)
    {
        itemEntity = 0;
        itemSlot = 0;
        itemAsset = 0;
        localizedName = 0;
        if (!IsArmed)
        {
            return false;
        }

        _session.TryReadInt64(_trampoline.ToInt64() + ItemSlotOffset, out itemSlot);
        _session.TryReadInt64(_trampoline.ToInt64() + ItemAssetOffset, out itemAsset);
        _session.TryReadInt64(_trampoline.ToInt64() + LocalizedNameOffset, out localizedName);
        return _session.TryReadInt64(_trampoline.ToInt64() + ItemOffset, out itemEntity) && itemEntity != 0;
    }

    public void ClearSelection()
    {
        if (!IsArmed)
        {
            return;
        }

        _session.WriteInt64(_trampoline.ToInt64() + ItemOffset, 0);
        _session.WriteInt64(_trampoline.ToInt64() + ItemAssetOffset, 0);
        _session.WriteInt64(_trampoline.ToInt64() + LocalizedNameOffset, 0);
        _session.WriteInt64(_trampoline.ToInt64() + PendingItemOffset, 0);
        _session.WriteInt64(_trampoline.ToInt64() + ItemSlotOffset, 0);
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

    private static void AddStoreRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode)
    {
        code.AddRange(opcode);
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + targetOffset) - nextInstruction))));
    }

    private static void AddLoadRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode)
    {
        code.AddRange(opcode);
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + targetOffset) - nextInstruction))));
    }

    private static void AddSaveVolatileRegisters(List<byte> code)
    {
        code.AddRange(new byte[] { 0x48, 0x89, 0x44, 0x24, 0x20 });
        code.AddRange(new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x28 });
        code.AddRange(new byte[] { 0x48, 0x89, 0x54, 0x24, 0x30 });
        code.AddRange(new byte[] { 0x4C, 0x89, 0x44, 0x24, 0x38 });
        code.AddRange(new byte[] { 0x4C, 0x89, 0x4C, 0x24, 0x40 });
        code.AddRange(new byte[] { 0x4C, 0x89, 0x54, 0x24, 0x48 });
        code.AddRange(new byte[] { 0x4C, 0x89, 0x5C, 0x24, 0x50 });
        AddXmmStackMove(code, 0x7F, 0, 0x60);
        AddXmmStackMove(code, 0x7F, 1, 0x70);
        AddXmmStackMove(code, 0x7F, 2, 0x80);
        AddXmmStackMove(code, 0x7F, 3, 0x90);
        AddXmmStackMove(code, 0x7F, 4, 0xA0);
        AddXmmStackMove(code, 0x7F, 5, 0xB0);
    }

    private static void AddRestoreVolatileRegisters(List<byte> code)
    {
        AddXmmStackMove(code, 0x6F, 0, 0x60);
        AddXmmStackMove(code, 0x6F, 1, 0x70);
        AddXmmStackMove(code, 0x6F, 2, 0x80);
        AddXmmStackMove(code, 0x6F, 3, 0x90);
        AddXmmStackMove(code, 0x6F, 4, 0xA0);
        AddXmmStackMove(code, 0x6F, 5, 0xB0);
        code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x20 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x28 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x30 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x38 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x40 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x54, 0x24, 0x48 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x5C, 0x24, 0x50 });
    }

    private static void AddXmmStackMove(List<byte> code, byte operation, int register, int offset)
    {
        code.AddRange(new byte[] { 0xF3, 0x0F, operation });
        if (offset <= sbyte.MaxValue)
        {
            code.AddRange(new byte[] { (byte)(0x44 | (register << 3)), 0x24, (byte)offset });
            return;
        }

        code.AddRange(new byte[] { (byte)(0x84 | (register << 3)), 0x24 });
        code.AddRange(BitConverter.GetBytes(offset));
    }

}
