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
    private readonly long _tryGetItemOwner;
    private readonly long _preCaptureSite;
    private byte[] _preCaptureReplacedBytes = Array.Empty<byte>();
    private IntPtr _trampoline;
    private int _captureOffset;
    private int _itemEntityOffset;
    private int _ownerOffset;
    private int _ownerResolvedOffset;

    public RemoteCaptureHook(
        GameSession session,
        long site,
        int patchLength,
        CaptureRegister register,
        int capturedAddressAdjustment,
        bool preventZeroQuantity = false,
        long tryGetItemOwner = 0)
    {
        _session = session;
        _site = site;
        _register = register;
        _capturedAddressAdjustment = capturedAddressAdjustment;
        _preventZeroQuantity = preventZeroQuantity;
        _tryGetItemOwner = tryGetItemOwner;
        _preCaptureSite = site - 0x40;
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

        if (_preventZeroQuantity && _tryGetItemOwner == 0)
        {
            throw new InvalidOperationException("缺少物品归属解析函数，已拒绝启用最后一个保留功能。");
        }

        if (_preventZeroQuantity)
        {
            _preCaptureReplacedBytes = session.Read(_preCaptureSite, 9);
            if (!_preCaptureReplacedBytes.SequenceEqual(new byte[] { 0x48, 0x8D, 0x4E, 0x60, 0x4C, 0x8D, 0x44, 0x24, 0x40 }))
            {
                throw new InvalidOperationException("物品数量前置上下文不匹配，已拒绝启用最后一个保留功能。");
            }
        }
    }

    public bool IsArmed => _trampoline != IntPtr.Zero;
    public bool PreventsZeroQuantity => _preventZeroQuantity;

    public string Arm()
    {
        if (IsArmed)
        {
            return "持续跟踪已开启。最近一次发生数量变化的物品会自动成为写入目标。";
        }

        _trampoline = _session.AllocateNear(_site, 0x1000);
        if (_trampoline == IntPtr.Zero)
        {
            return "无法在特征码附近建立临时捕获区。本次不会写入游戏；请重启游戏后再试。";
        }

        var trampolineAddress = _trampoline.ToInt64();
        var code = new List<byte>();
        if (_preventZeroQuantity)
        {
            _itemEntityOffset = 0x808;
            _ownerOffset = 0x810;
            _ownerResolvedOffset = 0x818;

            var preCaptureAddress = trampolineAddress + 0x300;
            var preCapture = new List<byte>();
            AddStoreRipRelative(preCapture, preCaptureAddress, _itemEntityOffset, new byte[] { 0x48, 0x89, 0x15 }); // mov [itemEntity],rdx
            preCapture.AddRange(_preCaptureReplacedBytes);
            preCapture.Add(0xE9);
            preCapture.AddRange(BitConverter.GetBytes(checked((int)((_preCaptureSite + _preCaptureReplacedBytes.Length) - (preCaptureAddress + preCapture.Count + sizeof(int))))));

            if (!_session.Write(preCaptureAddress, preCapture.ToArray()))
            {
                Dispose();
                return "写入物品归属捕获区失败；未修改游戏指令。";
            }
            _session.Flush(preCaptureAddress, preCapture.Count);
        }

        code.AddRange(RegisterMoveToRipRelative(_register, trampolineAddress, out _captureOffset));
        if (_preventZeroQuantity)
        {
            // Normal quantity changes only need address capture. Resolving ownership on every
            // pickup/use made this hot path fragile; ownership matters only for a zero write.
            code.AddRange(new byte[] { 0x85, 0xFF }); // test edi,edi
            var nonZeroQuantityJump = code.Count;
            code.AddRange(new byte[] { 0x0F, 0x85, 0x00, 0x00, 0x00, 0x00 }); // jne original

            AddStoreInt32RipRelative(code, trampolineAddress, _ownerResolvedOffset, 0);
            code.AddRange(new byte[] { 0x48, 0x81, 0xEC, 0xC0, 0x00, 0x00, 0x00 }); // sub rsp,C0
            AddSaveVolatileRegisters(code);
            code.AddRange(new byte[] { 0x48, 0x89, 0xF1 }); // mov rcx,rsi (Frame)
            AddLoadRipRelative(code, trampolineAddress, _itemEntityOffset, new byte[] { 0x48, 0x8B, 0x15 }); // mov rdx,[itemEntity]
            code.AddRange(new byte[] { 0x48, 0x85, 0xD2 }); // test rdx,rdx
            var skipOwnerCall = code.Count;
            code.AddRange(new byte[] { 0x74, 0x00 });
            AddLeaRipRelative(code, trampolineAddress, _ownerOffset, new byte[] { 0x4C, 0x8D, 0x05 }); // lea r8,[owner]
            AddCallAbsolute(code, _tryGetItemOwner);
            code.AddRange(new byte[] { 0x0F, 0xB6, 0xC0 }); // movzx eax,al
            AddStoreRipRelative(code, trampolineAddress, _ownerResolvedOffset, new byte[] { 0x89, 0x05 }); // mov [ownerResolved],eax
            code[skipOwnerCall + 1] = checked((byte)(code.Count - (skipOwnerCall + 2)));
            AddRestoreVolatileRegisters(code);
            code.AddRange(new byte[] { 0x48, 0x81, 0xC4, 0xC0, 0x00, 0x00, 0x00 }); // add rsp,C0

            AddLoadRipRelative(code, trampolineAddress, _ownerResolvedOffset, new byte[] { 0x8B, 0x05 }); // mov eax,[ownerResolved]
            code.AddRange(new byte[] { 0x85, 0xC0 }); // test eax,eax
            code.AddRange(new byte[] { 0x74, 0x0A }); // je original
            code.AddRange(new byte[] { 0x83, 0xFF, 0x00, 0x75, 0x05, 0xBF, 0x01, 0x00, 0x00, 0x00 }); // clamp backpack zero writes
            var originalInstructionOffset = code.Count;
            code.AddRange(new byte[] { 0x89, 0x3B, 0x85, 0xFF, 0x0F, 0x94, 0xC0 });
            var nonZeroQuantityOffset = BitConverter.GetBytes(originalInstructionOffset - (nonZeroQuantityJump + 6));
            for (var index = 0; index < nonZeroQuantityOffset.Length; index++)
            {
                code[nonZeroQuantityJump + 2 + index] = nonZeroQuantityOffset[index];
            }
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
        _session.Flush(trampolineAddress, code.Count);

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

        if (_preventZeroQuantity)
        {
            var preCapturePatch = new byte[_preCaptureReplacedBytes.Length];
            preCapturePatch[0] = 0xE9;
            BitConverter.GetBytes(checked((int)((trampolineAddress + 0x300) - (_preCaptureSite + 5)))).CopyTo(preCapturePatch, 1);
            for (var index = 5; index < preCapturePatch.Length; index++)
            {
                preCapturePatch[index] = 0x90;
            }

            if (!_session.Write(_preCaptureSite, preCapturePatch))
            {
                Dispose();
                return "启用物品归属捕获失败；原始指令未改动。";
            }

            _session.Flush(_preCaptureSite, preCapturePatch.Length);
        }

        _session.Flush(_site, patch.Length);
        return _preventZeroQuantity
            ? "持续跟踪已开启：只对角色背包内物品保留最后 1 个。"
            : "持续跟踪已开启：让目标物品数量变化一次后可直接写入。";
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

        if (_preCaptureReplacedBytes.Length > 0)
        {
            _session.Write(_preCaptureSite, _preCaptureReplacedBytes);
            _session.Flush(_preCaptureSite, _preCaptureReplacedBytes.Length);
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

    private static void AddLoadRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode)
    {
        code.AddRange(opcode);
        var nextInstruction = trampolineAddress + code.Count + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + targetOffset) - nextInstruction))));
    }

    private static void AddStoreRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode) =>
        AddLoadRipRelative(code, trampolineAddress, targetOffset, opcode);

    private static void AddLeaRipRelative(List<byte> code, long trampolineAddress, int targetOffset, byte[] opcode) =>
        AddLoadRipRelative(code, trampolineAddress, targetOffset, opcode);

    private static void AddStoreInt32RipRelative(List<byte> code, long trampolineAddress, int targetOffset, int value)
    {
        code.AddRange(new byte[] { 0xC7, 0x05 });
        var nextInstruction = trampolineAddress + code.Count + sizeof(int) + sizeof(int);
        code.AddRange(BitConverter.GetBytes(checked((int)((trampolineAddress + targetOffset) - nextInstruction))));
        code.AddRange(BitConverter.GetBytes(value));
    }

    private static void AddCallAbsolute(List<byte> code, long address)
    {
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(address));
        code.AddRange(new byte[] { 0xFF, 0xD0 });
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
    }

    private static void AddRestoreVolatileRegisters(List<byte> code)
    {
        code.AddRange(new byte[] { 0x48, 0x8B, 0x44, 0x24, 0x20 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x4C, 0x24, 0x28 });
        code.AddRange(new byte[] { 0x48, 0x8B, 0x54, 0x24, 0x30 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x44, 0x24, 0x38 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x4C, 0x24, 0x40 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x54, 0x24, 0x48 });
        code.AddRange(new byte[] { 0x4C, 0x8B, 0x5C, 0x24, 0x50 });
    }
}
