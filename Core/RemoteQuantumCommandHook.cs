namespace NRftWManagerUI.Core;

/// <summary>Executes one queued rarity change from Quantum's simulation update thread.</summary>
internal sealed class RemoteQuantumCommandHook : IDisposable
{
    private const int FrameOffset = 0x800;
    private const int SelectedItemOffset = 0x808;
    private const int RarityOffset = 0x810;
    private const int CommandOffset = 0x814;
    private const int CompletedOffset = 0x818;
    private const int ResultOffset = 0x81C;

    private readonly GameSession _session;
    private readonly long _site;
    private readonly long _changeItemRarity;
    private readonly byte[] _replacedBytes;
    private IntPtr _trampoline;

    public RemoteQuantumCommandHook(GameSession session, long site, byte[] expectedEntry, long changeItemRarity)
    {
        _session = session;
        _site = site;
        _changeItemRarity = changeItemRarity;
        _replacedBytes = session.Read(site, expectedEntry.Length);
        if (!_replacedBytes.SequenceEqual(expectedEntry))
        {
            throw new InvalidOperationException("Quantum 更新入口与当前版本配置不匹配，已拒绝启用品质修改。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;

    public string Arm()
    {
        if (IsArmed)
        {
            return "品质命令入口已开启。";
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
        var skipCommandOffset = code.Count;
        code.AddRange(new byte[sizeof(int)]);

        AddStoreInt32RipRelative(code, root, CommandOffset, 0);
        AddLoadRipRelative(code, root, FrameOffset, new byte[] { 0x48, 0x8B, 0x0D });
        AddLoadRipRelative(code, root, SelectedItemOffset, new byte[] { 0x48, 0x8B, 0x15 });
        AddLoadRipRelative(code, root, RarityOffset, new byte[] { 0x44, 0x8B, 0x05 });
        code.AddRange(new byte[] { 0x41, 0xB9, 0x04, 0x00, 0x00, 0x00 }); // RarityEmber
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(_changeItemRarity));
        code.AddRange(new byte[] { 0xFF, 0xD0, 0x0F, 0xB6, 0xC0 });
        AddStoreRipRelative(code, root, ResultOffset, new byte[] { 0x89, 0x05 });
        AddStoreInt32RipRelative(code, root, CompletedOffset, 1);

        var restoreOffset = code.Count;
        var skipCommand = BitConverter.GetBytes(restoreOffset - (skipCommandOffset + sizeof(int)));
        for (var index = 0; index < skipCommand.Length; index++)
        {
            code[skipCommandOffset + index] = skipCommand[index];
        }
        code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28 });
        code.AddRange(new byte[] { 0x41, 0x5B, 0x41, 0x5A, 0x41, 0x59, 0x41, 0x58, 0x5A, 0x59, 0x58, 0x9D });

        code.AddRange(_replacedBytes);
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(checked((int)((_site + _replacedBytes.Length) - (root + code.Count + sizeof(int))))));

        if (!_session.Write(root, code.ToArray()))
        {
            Dispose();
            return "写入品质命令区失败；未修改游戏指令。";
        }
        _session.Flush(root, code.Count);

        var patch = new byte[_replacedBytes.Length];
        patch[0] = 0xE9;
        BitConverter.GetBytes(checked((int)(root - (_site + 5)))).CopyTo(patch, 1);
        Array.Fill(patch, (byte)0x90, 5, patch.Length - 5);
        if (!_session.Write(_site, patch))
        {
            Dispose();
            return "启用品质命令入口失败；原始指令未改动。";
        }
        _session.Flush(_site, patch.Length);
        return "品质命令入口已开启：命令只会在 Quantum 模拟线程执行一次。";
    }

    public bool QueueRarity(long selectedItem, int rarity)
    {
        if (!IsArmed || selectedItem == 0 || rarity is < 0 or > 3)
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
               _session.WriteInt32(root + CommandOffset, 1);
    }

    public bool TryReadCompletion(out bool completed, out bool succeeded)
    {
        completed = false;
        succeeded = false;
        if (!IsArmed || !_session.TryReadInt32(_trampoline.ToInt64() + CompletedOffset, out var completedValue))
        {
            return false;
        }

        completed = completedValue == 1;
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

    private static void AddStoreInt32RipRelative(List<byte> code, long root, int targetOffset, int value)
    {
        code.AddRange(new byte[] { 0xC7, 0x05 });
        code.AddRange(BitConverter.GetBytes(checked((int)((root + targetOffset) - (root + code.Count + sizeof(int) + sizeof(int))))));
        code.AddRange(BitConverter.GetBytes(value));
    }
}
