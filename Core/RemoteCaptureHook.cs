namespace NRftWManagerUI.Core;

internal enum CaptureRegister
{
    Rbx,
    R13
}

internal sealed class RemoteCaptureHook : IDisposable
{
    private readonly GameSession _session;
    private readonly long _site;
    private readonly byte[] _replacedBytes;
    private readonly CaptureRegister _register;
    private readonly int _capturedAddressAdjustment;
    private readonly bool _preventZeroQuantity;
    private IntPtr _trampoline;
    private int _captureOffset;

    public RemoteCaptureHook(GameSession session, long site, int patchLength, CaptureRegister register, int capturedAddressAdjustment, bool preventZeroQuantity = false)
    {
        _session = session;
        _site = site;
        _register = register;
        _capturedAddressAdjustment = capturedAddressAdjustment;
        _preventZeroQuantity = preventZeroQuantity;
        _replacedBytes = session.Read(site, patchLength);

        if (_replacedBytes.Length != patchLength)
        {
            throw new InvalidOperationException("无法读取待验证的原始指令。游戏可能正在退出，或此版本不兼容。");
        }

        if (_preventZeroQuantity &&
            (_register != CaptureRegister.Rbx || !_replacedBytes.SequenceEqual(new byte[] { 0x89, 0x3B, 0x0F, 0x94, 0xC0 })))
        {
            throw new InvalidOperationException("物品数量原始指令不匹配，已拒绝启用最后一个保留功能。");
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;
    public bool PreventsZeroQuantity => _preventZeroQuantity;

    public string Arm()
    {
        if (IsArmed)
        {
            return "捕获已开启。请回游戏让目标数值变化一次。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在特征码附近建立临时捕获区。本次不会写入游戏；请重启游戏后再试。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();
        code.AddRange(RegisterMoveToRipRelative(_register, trampolineAddress, out _captureOffset));
        if (_preventZeroQuantity)
        {
            // cmp edi,0; jne +5; mov edi,1; mov [rbx],edi; test edi,edi; sete al
            code.AddRange(new byte[] { 0x83, 0xFF, 0x00, 0x75, 0x05, 0xBF, 0x01, 0x00, 0x00, 0x00 });
            code.AddRange(new byte[] { 0x89, 0x3B, 0x85, 0xFF, 0x0F, 0x94, 0xC0 });
        }
        else
        {
            code.AddRange(_replacedBytes);
        }
        code.Add(0xE9);
        var returnOffset = checked((int)((_site + _replacedBytes.Length) - (trampolineAddress + code.Count + sizeof(int))));
        code.AddRange(BitConverter.GetBytes(returnOffset));

        if (!_session.Write(trampolineAddress, code.ToArray()))
        {
            Dispose();
            return "写入临时捕获区失败；未修改游戏指令。";
        }

        var jmpOffset = checked((int)(trampolineAddress - (_site + 5)));
        var patch = new byte[_replacedBytes.Length];
        patch[0] = 0xE9;
        BitConverter.GetBytes(jmpOffset).CopyTo(patch, 1);
        for (var index = 5; index < patch.Length; index++)
        {
            patch[index] = 0x90;
        }

        if (!_session.Write(_site, patch))
        {
            Dispose();
            return "启用临时捕获失败；原始指令未改动。";
        }

        _session.Flush(_site, patch.Length);
        return _preventZeroQuantity
            ? "捕获已开启：最后 1 个将保留。回游戏卖出或使用目标材料一次，再点击“读取捕获结果”。"
            : "捕获已开启：回游戏让目标数值变化一次，然后点击“读取捕获结果”。";
    }

    public bool TryReadCapturedAddress(out long address)
    {
        address = 0;
        if (!IsArmed)
        {
            return false;
        }

        var bytes = _session.Read(_trampoline.ToInt64() + _captureOffset, sizeof(long));
        if (bytes.Length != sizeof(long))
        {
            return false;
        }

        address = BitConverter.ToInt64(bytes) + _capturedAddressAdjustment;
        return address != _capturedAddressAdjustment;
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

    private static byte[] RegisterMoveToRipRelative(CaptureRegister register, long trampolineAddress, out int dataOffset)
    {
        var opcode = register switch
        {
            CaptureRegister.Rbx => new byte[] { 0x48, 0x89, 0x1D },
            CaptureRegister.R13 => new byte[] { 0x4C, 0x89, 0x2D },
            _ => throw new ArgumentOutOfRangeException(nameof(register))
        };

        // The capture value lives after the code. Reserve it at a fixed location in this page.
        dataOffset = 0x800;
        var displacement = checked((int)((trampolineAddress + dataOffset) - (trampolineAddress + 7)));
        return opcode.Concat(BitConverter.GetBytes(displacement)).ToArray();
    }
}
