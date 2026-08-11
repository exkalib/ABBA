namespace NRftWManagerUI.Core;

/// <summary>
/// Observes the return value of a native game call to GetInventoryComponent. It never invokes the
/// game API on its own; the original game call keeps its arguments, thread and timing.
/// </summary>
internal sealed class RemoteInventoryCallHook : IDisposable
{
    private const int ResultOffset = 0x800;
    private static readonly byte[] ExpectedCall = { 0xE8, 0x00, 0x3D, 0x00, 0x00 };

    private readonly GameSession _session;
    private readonly long _site;
    private readonly long _target;
    private readonly byte[] _originalCall;
    private IntPtr _trampoline;

    public RemoteInventoryCallHook(GameSession session, long site, long expectedTarget)
    {
        _session = session;
        _site = site;
        _target = expectedTarget;
        _originalCall = session.Read(site, ExpectedCall.Length);

        if (!_originalCall.SequenceEqual(ExpectedCall) ||
            site + ExpectedCall.Length + BitConverter.ToInt32(_originalCall, 1) != expectedTarget)
        {
            throw new InvalidOperationException("背包原生调用点与当前版本不匹配，已拒绝安装旁路捕获。");
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
            return "无法在背包原生调用点附近建立旁路捕获区；游戏指令未改动。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28 }); // preserve ABI stack alignment and shadow space
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(_target));
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        AddStoreResult(code, trampolineAddress);
        code.Add(0xC3);

        if (!_session.Write(trampolineAddress, code.ToArray()))
        {
            Dispose();
            return "写入背包旁路捕获区失败；游戏指令未改动。";
        }
        _session.Flush(trampolineAddress, code.Count);

        var replacement = new byte[] { 0xE8, 0, 0, 0, 0 };
        BitConverter.GetBytes(checked((int)(trampolineAddress - (_site + replacement.Length)))).CopyTo(replacement, 1);
        if (!_session.Write(_site, replacement))
        {
            Dispose();
            return "启用背包旁路捕获失败；原始调用未改动。";
        }

        _session.Flush(_site, replacement.Length);
        return "背包旁路捕获已开启：请回游戏拾取一个物品，或购买/制作一个物品。";
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

        _session.Write(_site, _originalCall);
        _session.Flush(_site, _originalCall.Length);
        _session.Free(_trampoline);
        _trampoline = IntPtr.Zero;
    }

    private static void AddStoreResult(List<byte> code, long trampolineAddress)
    {
        code.AddRange(new byte[] { 0x48, 0x89, 0x05 });
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + ResultOffset) - nextInstruction))));
    }
}
