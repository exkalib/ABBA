namespace NRftWManagerUI.Core;

/// <summary>Records the selected ItemSlot entity and its current-language display name.</summary>
internal sealed class RemoteItemSelectionHook : IDisposable
{
    private const int ItemOffset = 0x800;
    private const int ItemAssetOffset = 0x808;
    private const int LocalizedNameOffset = 0x810;

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
        AddStoreRipRelative(code, trampolineAddress, LocalizedNameOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x81, 0x00, 0x01, 0x00, 0x00 }); // mov rax,[rcx+0x100] ItemRef.Entity
        AddStoreRipRelative(code, trampolineAddress, ItemOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x81, 0x38, 0x05, 0x00, 0x00 }); // mov rax,[rcx+0x538] HeroItemDataAsset
        AddStoreRipRelative(code, trampolineAddress, ItemAssetOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.Add(0x58); // pop rax
        code.AddRange(_replacedBytes);

        // The original prologue leaves RSP 16-byte aligned and reserves its own call frame.
        // Preserve all volatile integer and SIMD registers while asking the asset for the
        // already-localized name that the game UI itself displays.
        code.AddRange(new byte[] { 0x48, 0x81, 0xEC, 0xC0, 0x00, 0x00, 0x00 }); // sub rsp,C0
        AddSaveVolatileRegisters(code);
        AddLoadRipRelative(code, trampolineAddress, ItemAssetOffset, new byte[] { 0x48, 0x8B, 0x0D }); // mov rcx,[asset]
        code.AddRange(new byte[] { 0x48, 0x85, 0xC9 }); // test rcx,rcx
        var skipNameCall = code.Count;
        code.AddRange(new byte[] { 0x74, 0x00 }); // je short restore
        code.AddRange(new byte[] { 0x48, 0x31, 0xD2 }); // xor rdx,rdx (MethodInfo*)
        code.AddRange(new byte[] { 0x48, 0xB8 }); // mov rax,imm64
        code.AddRange(BitConverter.GetBytes(_getItemName));
        code.AddRange(new byte[] { 0xFF, 0xD0 }); // call rax
        AddStoreRipRelative(code, trampolineAddress, LocalizedNameOffset, new byte[] { 0x48, 0x89, 0x05 });
        code[skipNameCall + 1] = checked((byte)(code.Count - (skipNameCall + 2)));
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

    public bool TryReadSelection(out long itemEntity, out long localizedName)
    {
        itemEntity = 0;
        localizedName = 0;
        if (!IsArmed)
        {
            return false;
        }

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
        code.AddRange(new byte[] { 0x48, 0x89, 0x44, 0x24, 0x20 }); // [rsp+20],rax
        code.AddRange(new byte[] { 0x48, 0x89, 0x4C, 0x24, 0x28 }); // [rsp+28],rcx
        code.AddRange(new byte[] { 0x48, 0x89, 0x54, 0x24, 0x30 }); // [rsp+30],rdx
        code.AddRange(new byte[] { 0x4C, 0x89, 0x44, 0x24, 0x38 }); // [rsp+38],r8
        code.AddRange(new byte[] { 0x4C, 0x89, 0x4C, 0x24, 0x40 }); // [rsp+40],r9
        code.AddRange(new byte[] { 0x4C, 0x89, 0x54, 0x24, 0x48 }); // [rsp+48],r10
        code.AddRange(new byte[] { 0x4C, 0x89, 0x5C, 0x24, 0x50 }); // [rsp+50],r11
        AddSaveXmm(code, 0, 0x60);
        AddSaveXmm(code, 1, 0x70);
        AddSaveXmm(code, 2, 0x80);
        AddSaveXmm(code, 3, 0x90);
        AddSaveXmm(code, 4, 0xA0);
        AddSaveXmm(code, 5, 0xB0);
    }

    private static void AddRestoreVolatileRegisters(List<byte> code)
    {
        AddRestoreXmm(code, 0, 0x60);
        AddRestoreXmm(code, 1, 0x70);
        AddRestoreXmm(code, 2, 0x80);
        AddRestoreXmm(code, 3, 0x90);
        AddRestoreXmm(code, 4, 0xA0);
        AddRestoreXmm(code, 5, 0xB0);
        code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x20 }); // rax,[rsp+20]
        code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x28 }); // rcx,[rsp+28]
        code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x30 }); // rdx,[rsp+30]
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x38 }); // r8,[rsp+38]
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x40 }); // r9,[rsp+40]
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x54, 0x24, 0x48 }); // r10,[rsp+48]
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x5C, 0x24, 0x50 }); // r11,[rsp+50]
    }

    private static void AddSaveXmm(List<byte> code, int register, int offset) =>
        AddXmmStackMove(code, 0x7F, register, offset);

    private static void AddRestoreXmm(List<byte> code, int register, int offset) =>
        AddXmmStackMove(code, 0x6F, register, offset);

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
