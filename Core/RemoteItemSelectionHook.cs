namespace NRftWManagerUI.Core;

/// <summary>Records the selected ItemSlot entity and its view-side item asset.</summary>
internal sealed class RemoteItemSelectionHook : IDisposable
{
    private const int ItemOffset = 0x800;
    private const int ItemAssetOffset = 0x808;

    private readonly GameSession _session;
    private readonly long _site;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteItemSelectionHook(GameSession session, long site, byte[] expectedEntry)
    {
        _session = session;
        _site = site;
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
        code.AddRange(new byte[] { 0x48, 0x8B, 0x81, 0x38, 0x05, 0x00, 0x00 }); // mov rax,[rcx+0x538] HeroItemDataAsset
        AddStoreRipRelative(code, trampolineAddress, ItemAssetOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x81, 0x00, 0x01, 0x00, 0x00 }); // mov rax,[rcx+0x100] ItemRef.Entity
        // ItemOffset is the ready flag and must be published after ItemAssetOffset.
        AddStoreRipRelative(code, trampolineAddress, ItemOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.Add(0x58); // pop rax
        code.AddRange(_replacedBytes);
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

    public bool TryReadSelection(out long itemEntity, out long itemAsset)
    {
        itemEntity = 0;
        itemAsset = 0;
        if (!IsArmed)
        {
            return false;
        }

        _session.TryReadInt64(_trampoline.ToInt64() + ItemAssetOffset, out itemAsset);
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

}
