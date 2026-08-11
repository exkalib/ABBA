namespace NRftWManagerUI.Core;

/// <summary>
/// Records the inventory component pointer that the game has already resolved inside
/// GetInventoryEntity. No game function is invoked by this hook.
/// </summary>
internal sealed class RemoteInventoryCallHook : IDisposable
{
    private const int ResultOffset = 0x800;
    private static readonly byte[] ExpectedEntry = { 0x48, 0x8B, 0x06, 0x48, 0x8B, 0x74, 0x24, 0x30 };

    private readonly GameSession _session;
    private readonly long _site;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteInventoryCallHook(GameSession session, long site)
    {
        _session = session;
        _site = site;
        _replacedBytes = session.Read(site, ExpectedEntry.Length);
        if (!_replacedBytes.SequenceEqual(ExpectedEntry))
        {
            throw new InvalidOperationException("背包组件返回点与当前版本不匹配，已拒绝安装旁路捕获。");
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
            return "无法在背包组件返回点附近建立旁路捕获区；游戏指令未改动。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();
        code.AddRange(new byte[] { 0x48, 0x89, 0x35 }); // mov [rip+result], rsi
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + ResultOffset) - nextInstruction))));
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
        return "背包旁路捕获已开启：请回游戏拾取、购买或制作一个物品。";
    }

    public bool TryReadInventoryComponent(out long component)
    {
        component = 0;
        return IsArmed &&
               _session.TryReadInt64(_trampoline.ToInt64() + ResultOffset, out component) &&
               component != 0;
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
}
