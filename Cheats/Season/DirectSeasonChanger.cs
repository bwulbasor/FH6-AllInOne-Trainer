using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FH6Mod.Cheats.RuntimeHook;

namespace FH6Mod.Cheats.Season;

// Build 6.483.970.0 only. Objects are discovered anew in each game process;
// code and vtable slots are validated before they are used.
internal sealed class DirectSeasonChanger(RuntimeHookEngine engine)
{
    private const ulong ForceSeasonRva = 0xfbc8f0;
    private const ulong SeasonVtableRva = 0x65b7450;
    private const ulong CallbackVtableRva = 0x6a57458;
    private readonly object _gate = new();
    private int? _pid;
    private ulong _module, _callback, _dispatcher;
    private uint _thread;
    private bool _requestUncertain;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessIdOfThread(IntPtr thread);

    public void Reset() { lock (_gate) { _pid = null; _module = _callback = _dispatcher = 0; _thread = 0; _requestUncertain = false; } }

    public int GetCurrent()
    {
        try
        {
            var b = engine.MainBase;
            var code = engine.ReadBytesPublic(b + 0xfc0560, 14);
            if (code.Length != 14 || code[0] != 0x48 || code[1] != 0x8b || code[2] != 0x0d ||
                code[7] != 0x33 || code[8] != 0xd2 || code[9] != 0xe9) return -1;
            var global = unchecked((ulong)((long)b + 0xfc0567 + BitConverter.ToInt32(code, 3)));
            if (global < b || global + 8 > b + (ulong)engine.MainSize) return -1;
            var manager = engine.ReadUInt64Public(global);
            if (!Plausible(manager)) return -1;
            var season = engine.ReadInt32Public(manager + 0x670);
            return season is >= 0 and <= 3 ? season : -1;
        }
        catch { return -1; }
    }

    public bool SetSeason(int season, out string? error)
    {
        lock (_gate)
        {
            error = null;
            if (season is < 0 or > 3) { error = "Invalid season."; return false; }
            if (_requestUncertain) { error = "A previous request did not finish. Restart the game before retrying."; return false; }
            if (!Resolve(out error)) return false;
            if (GetCurrent() == season) return true;
            var success = new GameThreadInvoker(engine).Invoke(engine.MainBase + ForceSeasonRva, _module, _dispatcher, _thread, season, out error);
            if (!success && error?.Contains("did not return", StringComparison.OrdinalIgnoreCase) == true) _requestUncertain = true;
            if (!success) return false;
            for (var i = 0; i < 50; i++)
            {
                if (GetCurrent() == season)
                {
                    engine.LogPublic($"Season request returned and the game reported {SeasonChanger.SeasonName(season)}.");
                    return true;
                }
                Thread.Sleep(100);
            }
            error = "The season request returned, but the game did not report the requested season.";
            return false;
        }
    }

    private bool Resolve(out string? error)
    {
        error = null;
        var pid = engine.Pid;
        if (!engine.IsAttached || pid is null) { error = "The game is not attached."; return false; }
        if (_pid != pid) { _pid = pid; _module = _callback = _dispatcher = 0; _thread = 0; _requestUncertain = false; }
        var b = engine.MainBase;
        try
        {
            var expected = new byte[] { 0x83, 0xfa, 0x04, 0x74, 0x65, 0x48, 0x89, 0x5c, 0x24, 0x08, 0x57, 0x48, 0x83, 0xec, 0x20 };
            if (!engine.ReadBytesPublic(b + ForceSeasonRva, expected.Length).AsSpan().SequenceEqual(expected) ||
                engine.ReadUInt64Public(b + SeasonVtableRva + 0x90) != b + 0xfbcaf0 ||
                engine.ReadUInt64Public(b + CallbackVtableRva + 0x318) != b + 0x32e3950)
            { error = "The season signatures do not match this game build."; return false; }
            if (ValidObjects(pid.Value, b)) return true;
            _module = _callback = _dispatcher = 0; _thread = 0;
            var first = BitConverter.GetBytes(b + SeasonVtableRva);
            var second = BitConverter.GetBytes(b + CallbackVtableRva);
            var timer = Stopwatch.StartNew();
            ulong address = 0;
            var buffer = new byte[4 * 1024 * 1024];
            while (address < 0x00007fff00000000UL && timer.Elapsed < TimeSpan.FromMinutes(2))
            {
                if (!engine.IsAttached || engine.Pid != pid) { error = "The game changed during discovery."; return false; }
                if (Native.VirtualQueryEx(engine.HandlePublic, (UIntPtr)address, out var m,
                        (UIntPtr)Marshal.SizeOf<Native.MemoryBasicInformation64>()) == UIntPtr.Zero) break;
                var next = m.BaseAddress + m.RegionSize;
                if (next <= address) break;
                address = next;
                if (m.State != Native.MEM_COMMIT || m.Type != Native.MEM_PRIVATE || !Native.IsWritable(m.Protect)) continue;
                for (ulong offset = 0; offset < m.RegionSize; offset += (ulong)buffer.Length)
                {
                    if (timer.Elapsed >= TimeSpan.FromMinutes(2)) break;
                    var count = (int)Math.Min((ulong)buffer.Length, m.RegionSize - offset);
                    if (!Native.ReadProcessMemory(engine.HandlePublic, (IntPtr)(long)(m.BaseAddress + offset), buffer,
                            (UIntPtr)count, out var read) || (ulong)read != (ulong)count) continue;
                    var span = buffer.AsSpan(0, count);
                    if (_module == 0)
                    {
                        var index = span.IndexOf(first);
                        while (index >= 0)
                        {
                            var candidate = m.BaseAddress + offset + (ulong)index;
                            if ((candidate & 7) == 0) { _module = candidate; break; }
                            var remaining = span[(index + 1)..].IndexOf(first);
                            index = remaining < 0 ? -1 : index + 1 + remaining;
                        }
                    }
                    if (_callback == 0)
                    {
                        var index = span.IndexOf(second);
                        while (index >= 0)
                        {
                            var candidate = m.BaseAddress + offset + (ulong)index;
                            if ((candidate & 7) == 0 && candidate >= 8)
                            {
                                try
                                {
                                    var dispatcher = engine.ReadUInt64Public(candidate - 8);
                                    if (Plausible(dispatcher))
                                    {
                                        var thread = unchecked((uint)engine.ReadInt32Public(dispatcher + 0xdc));
                                        var handle = thread == 0 ? IntPtr.Zero : OpenThread(0x800, false, thread);
                                        try
                                        {
                                            if (handle != IntPtr.Zero && GetProcessIdOfThread(handle) == (uint)pid.Value)
                                            { _callback = candidate; _dispatcher = dispatcher; _thread = thread; break; }
                                        }
                                        finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
                                    }
                                }
                                catch { /* Not a live callback object. */ }
                            }
                            var remaining = span[(index + 1)..].IndexOf(second);
                            index = remaining < 0 ? -1 : index + 1 + remaining;
                        }
                    }
                    if (_module != 0 && _callback != 0 && ValidObjects(pid.Value, b)) return true;
                }
            }
            error = "Could not find the active season module and update thread. Enter offline free roam and try again.";
            return false;
        }
        catch (Exception ex) { error = $"Season discovery failed: {ex.Message}"; return false; }
    }

    private bool ValidObjects(int pid, ulong b)
    {
        if (!Plausible(_module) || !Plausible(_callback) || !Plausible(_dispatcher) || _thread == 0) return false;
        if (engine.ReadUInt64Public(_module) != b + SeasonVtableRva ||
            engine.ReadUInt64Public(_callback) != b + CallbackVtableRva ||
            engine.ReadUInt64Public(_callback - 8) != _dispatcher ||
            unchecked((uint)engine.ReadInt32Public(_dispatcher + 0xdc)) != _thread) return false;
        var thread = OpenThread(0x800, false, _thread);
        if (thread == IntPtr.Zero) return false;
        try { return GetProcessIdOfThread(thread) == (uint)pid; }
        finally { Native.CloseHandle(thread); }
    }

    private static bool Plausible(ulong address) => address >= 0x10000 && address <= 0x00007fffffffffffUL;
}
