namespace NRftWManagerUI.Core;

internal sealed class RemoteWalletContextHook : IDisposable
{
    private const int FrameOffset = 0x800;
    private const int HeroOffset = 0x808;
    private const int InventoryComponentOffset = 0x810;

    private readonly GameSession _session;
    private readonly long _site;
    private readonly long _getInventoryComponent;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteWalletContextHook(GameSession session, long site, long getInventoryComponent, byte[] expectedEntry)
    {
        _session = session;
        _site = site;
        _getInventoryComponent = getInventoryComponent;
        _replacedBytes = session.Read(site, expectedEntry.Length);

        if (!_replacedBytes.SequenceEqual(expectedEntry))
        {
            throw new InvalidOperationException("玩家更新入口与当前版本配置不匹配，已拒绝启用自动钱包定位。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;

    public string Arm()
    {
        if (IsArmed)
        {
            return "自动钱包定位已开启。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在玩家更新入口附近建立临时定位区；本次不会写入游戏。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();

        // Preserve the original arguments. PlayerControllerView.OnUpdate receives Frame in R8
        // and the local hero EntityRef in R9 on Windows x64.
        code.AddRange(new byte[] { 0x50, 0x51, 0x52, 0x41, 0x50, 0x41, 0x51, 0x41, 0x52, 0x41, 0x53 });
        AddStoreRipRelative(code, trampolineAddress, FrameOffset, new byte[] { 0x4C, 0x89, 0x05 });
        AddStoreRipRelative(code, trampolineAddress, HeroOffset, new byte[] { 0x4C, 0x89, 0x0D });
        code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x20, 0x4C, 0x89, 0xC1, 0x4C, 0x89, 0xCA, 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(_getInventoryComponent));
        code.AddRange(new byte[] { 0xFF, 0xD0 });
        AddStoreRipRelative(code, trampolineAddress, InventoryComponentOffset, new byte[] { 0x48, 0x89, 0x05 });
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x20, 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58 });
        code.AddRange(_replacedBytes);
        code.Add(0xE9);
        var returnOffset = checked((int)((_site + _replacedBytes.Length) - (trampolineAddress + code.Count + sizeof(int))));
        code.AddRange(BitConverter.GetBytes(returnOffset));

        if (!_session.Write(trampolineAddress, code.ToArray()))
        {
            Dispose();
            return "写入自动钱包定位区失败；未修改游戏指令。";
        }

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
            return "启用自动钱包定位失败；原始指令未改动。";
        }

        _session.Flush(_site, patch.Length);
        return "自动钱包定位已开启。保持在已加载的角色画面约 1 秒；无需让货币变化。";
    }

    public bool TryReadWalletAddress(out long address)
    {
        address = 0;
        if (!IsArmed)
        {
            return false;
        }

        var componentBytes = _session.Read(_trampoline.ToInt64() + InventoryComponentOffset, sizeof(long));
        if (componentBytes.Length != sizeof(long))
        {
            return false;
        }

        var component = BitConverter.ToInt64(componentBytes);
        if (component <= 0)
        {
            return false;
        }

        address = component + sizeof(int);
        return _session.TryReadInt32(address, out _);
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
        var displacement = checked((int)((trampolineAddress + targetOffset) - nextInstruction));
        code.AddRange(BitConverter.GetBytes(displacement));
    }
}
