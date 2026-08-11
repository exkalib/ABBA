namespace NRftWManagerUI.Core;

/// <summary>
/// Resolves the inventory component on GetInventoryEntity's direct-inventory return path. Unlike
/// the rejected view-thread probe, this runs on the game's own Quantum thread with the Frame and
/// EntityRef already validated by GetInventoryEntity.
/// </summary>
internal sealed class RemoteInventoryCallHook : IDisposable
{
    private const int ResultOffset = 0x800;
    private const int ObservedOffset = 0x808;
    private static readonly byte[] ExpectedEntry = { 0x48, 0x8B, 0x74, 0x24, 0x30, 0x48, 0x8B, 0xC3 };

    private readonly GameSession _session;
    private readonly long _site;
    private readonly long _getInventoryComponent;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteInventoryCallHook(GameSession session, long site, long getInventoryComponent)
    {
        _session = session;
        _site = site;
        _getInventoryComponent = getInventoryComponent;
        _replacedBytes = session.Read(site, ExpectedEntry.Length);
        if (!_replacedBytes.SequenceEqual(ExpectedEntry))
        {
            throw new InvalidOperationException("背包快速返回点与当前版本不匹配，已拒绝安装旁路捕获。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;

    public string Arm()
    {
        if (IsArmed)
        {
            return "背包旁路捕获已开启。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在背包快速返回点附近建立旁路捕获区；游戏指令未改动。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();

        // At this native return path RDI is the valid Quantum Frame and RBX is an EntityRef that
        // GetInventoryEntity has already confirmed owns InventoryComponent. The function's 0x20
        // stack allocation supplies the required Windows x64 shadow space.
        code.AddRange(new byte[] { 0x48, 0x8B, 0xCF }); // mov rcx,rdi
        code.AddRange(new byte[] { 0x48, 0x8B, 0xD3 }); // mov rdx,rbx
        code.AddRange(new byte[] { 0x45, 0x33, 0xC0 }); // xor r8d,r8d (MethodInfo*)
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(_getInventoryComponent));
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        AddStoreResult(code, trampolineAddress);
        AddStoreObserved(code, trampolineAddress);

        // Restore the original epilogue instructions. In particular, mov rax,rbx preserves
        // GetInventoryEntity's original EntityRef return value for the caller.
        code.AddRange(_replacedBytes);
        code.Add(0xE9);
        var returnOffset = checked((int)((_site + _replacedBytes.Length) - (trampolineAddress + code.Count + sizeof(int))));
        code.AddRange(BitConverter.GetBytes(returnOffset));

        if (!_session.Write(trampolineAddress, code.ToArray()))
        {
            Dispose();
            return "写入背包旁路捕获区失败；游戏指令未改动。";
        }
        _session.Flush(trampolineAddress, code.Count);

        var replacement = new byte[_replacedBytes.Length];
        replacement[0] = 0xE9;
        BitConverter.GetBytes(checked((int)(trampolineAddress - (_site + 5)))).CopyTo(replacement, 1);
        for (var index = 5; index < replacement.Length; index++)
        {
            replacement[index] = 0x90;
        }

        if (!_session.Write(_site, replacement))
        {
            Dispose();
            return "启用背包旁路捕获失败；原始指令未改动。";
        }

        _session.Flush(_site, replacement.Length);
        return "背包旁路捕获已开启：请回游戏购买、拾取或制作一个物品。";
    }

    public bool TryReadInventoryComponent(out long component, out bool observed)
    {
        component = 0;
        observed = false;
        if (!IsArmed ||
            !_session.TryReadInt64(_trampoline.ToInt64() + ResultOffset, out component) ||
            !_session.TryReadInt32(_trampoline.ToInt64() + ObservedOffset, out var observedValue))
        {
            return false;
        }

        observed = observedValue == 1;
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

    private static void AddStoreResult(List<byte> code, long trampolineAddress)
    {
        code.AddRange(new byte[] { 0x48, 0x89, 0x05 });
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + ResultOffset) - nextInstruction))));
    }

    private static void AddStoreObserved(List<byte> code, long trampolineAddress)
    {
        code.AddRange(new byte[] { 0xC7, 0x05 });
        var nextInstruction = trampolineAddress + code.Count + sizeof(int) + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + ObservedOffset) - nextInstruction))));
        code.AddRange(BitConverter.GetBytes(1));
    }
}
