using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace NRftWManagerUI.Core;

internal sealed class GameSession : IDisposable
{
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessAccess = ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    private IntPtr _handle;

    public bool IsAttached => _handle != IntPtr.Zero;
    public string ProcessName { get; private set; } = string.Empty;
    public int ProcessId { get; private set; }
    public long GameAssemblyBase { get; private set; }
    public int GameAssemblySize { get; private set; }
    public string GameAssemblyPath { get; private set; } = string.Empty;

    public string Attach()
    {
        Dispose();

        var candidates = Process.GetProcesses()
            .Where(process => process.ProcessName.Contains("NoRestForTheWicked", StringComparison.OrdinalIgnoreCase))
            .OrderBy(process => process.Id)
            .ToArray();

        if (candidates.Length == 0)
        {
            return "未找到 No Rest for the Wicked 游戏进程。请先进入游戏并加载存档。";
        }

        using var process = candidates[0];
        var handle = OpenProcess(ProcessAccess, false, process.Id);
        if (handle == IntPtr.Zero)
        {
            return "无法取得游戏进程读写权限。请用管理员身份运行本程序，且确认游戏没有以更高权限启动。";
        }

        ProcessModule? assembly = null;
        try
        {
            assembly = process.Modules.Cast<ProcessModule>()
                .FirstOrDefault(module => string.Equals(module.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Win32Exception)
        {
            CloseHandle(handle);
            return "无法读取游戏模块。请以管理员身份重新运行本程序。";
        }

        if (assembly is null)
        {
            CloseHandle(handle);
            return "已找到游戏进程，但尚未加载 GameAssembly.dll。请进入实际游戏画面后再试。";
        }

        _handle = handle;
        ProcessName = process.ProcessName;
        ProcessId = process.Id;
        GameAssemblyBase = assembly.BaseAddress.ToInt64();
        GameAssemblySize = assembly.ModuleMemorySize;
        GameAssemblyPath = assembly.FileName;
        return $"已连接 {ProcessName}（PID {ProcessId}），GameAssembly.dll：0x{GameAssemblyBase:X}";
    }

    public bool HasGameAssemblyHash(string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(GameAssemblyPath) || !File.Exists(GameAssemblyPath))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(GameAssemblyPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(stream));
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public IReadOnlyList<long> ScanGameAssembly(AobPattern pattern)
    {
        EnsureAttached();

        const int chunkSize = 1024 * 1024;
        var matches = new List<long>();
        var carry = Array.Empty<byte>();
        var offset = 0L;

        while (offset < GameAssemblySize)
        {
            var wanted = (int)Math.Min(chunkSize, GameAssemblySize - offset);
            var current = Read(GameAssemblyBase + offset, wanted);
            if (current.Length == 0)
            {
                offset += wanted;
                carry = Array.Empty<byte>();
                continue;
            }

            var buffer = new byte[carry.Length + current.Length];
            Buffer.BlockCopy(carry, 0, buffer, 0, carry.Length);
            Buffer.BlockCopy(current, 0, buffer, carry.Length, current.Length);

            var firstAddress = GameAssemblyBase + offset - carry.Length;
            for (var index = 0; index <= buffer.Length - pattern.Bytes.Length; index++)
            {
                if (pattern.IsMatch(buffer, index))
                {
                    matches.Add(firstAddress + index);
                }
            }

            var carryLength = Math.Min(pattern.Bytes.Length - 1, buffer.Length);
            carry = buffer[^carryLength..];
            offset += wanted;
        }

        return matches;
    }

    public IReadOnlyList<long> FindInt32(int value, int maximumResults = 200000)
    {
        EnsureAttached();
        var results = new List<long>();
        var address = 0L;
        var infoSize = Marshal.SizeOf<MemoryBasicInformation>();

        while (VirtualQueryEx(_handle, new IntPtr(address), out var info, (nuint)infoSize) != 0)
        {
            var regionBase = info.BaseAddress.ToInt64();
            var regionSize = (long)info.RegionSize;
            var next = regionBase + Math.Max(regionSize, 0x1000);

            if (CanRead(info) && regionSize > 0)
            {
                ScanRegionForInt32(regionBase, regionSize, value, results, maximumResults);
                if (results.Count >= maximumResults)
                {
                    break;
                }
            }

            if (next <= address || next < 0)
            {
                break;
            }

            address = next;
        }

        return results;
    }

    public IReadOnlyList<long> FilterInt32(IEnumerable<long> addresses, int expectedValue)
    {
        EnsureAttached();
        var result = new List<long>();
        foreach (var address in addresses)
        {
            if (TryReadInt32(address, out var value) && value == expectedValue)
            {
                result.Add(address);
            }
        }

        return result;
    }

    public bool TryReadInt32(long address, out int value)
    {
        var bytes = Read(address, sizeof(int));
        if (bytes.Length != sizeof(int))
        {
            value = default;
            return false;
        }

        value = BitConverter.ToInt32(bytes);
        return true;
    }

    public bool TryReadInt64(long address, out long value)
    {
        var bytes = Read(address, sizeof(long));
        if (bytes.Length != sizeof(long))
        {
            value = default;
            return false;
        }

        value = BitConverter.ToInt64(bytes);
        return true;
    }

    public bool TryReadIl2CppString(long address, out string value)
    {
        value = string.Empty;
        if (address == 0 || !TryReadInt32(address + 0x10, out var length) || length is < 0 or > 1024)
        {
            return false;
        }

        if (length == 0)
        {
            return true;
        }

        var bytes = Read(address + 0x14, checked(length * sizeof(char)));
        if (bytes.Length != length * sizeof(char))
        {
            return false;
        }

        value = Encoding.Unicode.GetString(bytes);
        return true;
    }

    public bool WriteInt32(long address, int value)
    {
        return Write(address, BitConverter.GetBytes(value));
    }

    public bool WriteInt64(long address, long value)
    {
        return Write(address, BitConverter.GetBytes(value));
    }

    public byte[] Read(long address, int length)
    {
        EnsureAttached();
        if (length <= 0)
        {
            return Array.Empty<byte>();
        }

        var buffer = new byte[length];
        return ReadProcessMemory(_handle, new IntPtr(address), buffer, buffer.Length, out var read)
            ? buffer[..(int)read]
            : Array.Empty<byte>();
    }

    public bool Write(long address, byte[] bytes)
    {
        EnsureAttached();
        var target = new IntPtr(address);
        if (!VirtualProtectEx(_handle, target, (nuint)bytes.Length, 0x40, out var oldProtect))
        {
            return false;
        }

        try
        {
            return WriteProcessMemory(_handle, target, bytes, bytes.Length, out var written)
                && written.ToInt64() == bytes.Length;
        }
        finally
        {
            VirtualProtectEx(_handle, target, (nuint)bytes.Length, oldProtect, out _);
        }
    }

    public IntPtr AllocateNear(long address, int size)
    {
        EnsureAttached();
        const long maximumRelativeJump = int.MaxValue - 0x10000L;
        const long step = 0x100000;

        for (var delta = step; delta < maximumRelativeJump; delta += step)
        {
            foreach (var candidate in new[] { address + delta, address - delta })
            {
                if (candidate <= 0)
                {
                    continue;
                }

                var allocated = VirtualAllocEx(_handle, new IntPtr(candidate), (nuint)size, 0x3000, 0x40);
                if (allocated != IntPtr.Zero && IsRelativeJumpReachable(address, allocated.ToInt64()))
                {
                    return allocated;
                }

                if (allocated != IntPtr.Zero)
                {
                    VirtualFreeEx(_handle, allocated, 0, 0x8000);
                }
            }
        }

        return IntPtr.Zero;
    }

    public void Free(IntPtr address)
    {
        if (IsAttached && address != IntPtr.Zero)
        {
            VirtualFreeEx(_handle, address, 0, 0x8000);
        }
    }

    public void Flush(long address, int size)
    {
        if (IsAttached)
        {
            FlushInstructionCache(_handle, new IntPtr(address), (nuint)size);
        }
    }

    public static bool IsRelativeJumpReachable(long source, long destination)
    {
        var offset = destination - (source + 5);
        return offset is >= int.MinValue and <= int.MaxValue;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        ProcessName = string.Empty;
        ProcessId = 0;
        GameAssemblyBase = 0;
        GameAssemblySize = 0;
        GameAssemblyPath = string.Empty;
    }

    private void ScanRegionForInt32(long regionBase, long regionSize, int value, List<long> results, int maximumResults)
    {
        const int chunkSize = 1024 * 1024;
        var target = BitConverter.GetBytes(value);
        var offset = 0L;
        var carry = Array.Empty<byte>();

        while (offset < regionSize && results.Count < maximumResults)
        {
            var length = (int)Math.Min(chunkSize, regionSize - offset);
            var current = Read(regionBase + offset, length);
            if (current.Length == 0)
            {
                offset += length;
                carry = Array.Empty<byte>();
                continue;
            }

            var buffer = new byte[carry.Length + current.Length];
            Buffer.BlockCopy(carry, 0, buffer, 0, carry.Length);
            Buffer.BlockCopy(current, 0, buffer, carry.Length, current.Length);
            var firstAddress = regionBase + offset - carry.Length;

            for (var index = 0; index <= buffer.Length - target.Length; index++)
            {
                if (buffer[index] == target[0] && buffer[index + 1] == target[1] &&
                    buffer[index + 2] == target[2] && buffer[index + 3] == target[3])
                {
                    results.Add(firstAddress + index);
                    if (results.Count >= maximumResults)
                    {
                        return;
                    }
                }
            }

            carry = buffer[^Math.Min(3, buffer.Length)..];
            offset += length;
        }
    }

    private static bool CanRead(MemoryBasicInformation info)
    {
        return info.State == MemCommit &&
               (info.Protect & PageNoAccess) == 0 &&
               (info.Protect & PageGuard) == 0;
    }

    private void EnsureAttached()
    {
        if (!IsAttached)
        {
            throw new InvalidOperationException("尚未连接游戏进程。");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public ushort Padding;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, [Out] byte[] buffer, int size, out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);
}
