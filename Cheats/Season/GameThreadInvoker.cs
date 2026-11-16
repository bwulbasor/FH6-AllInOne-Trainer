using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FH6Mod.Cheats.RuntimeHook;

namespace FH6Mod.Cheats.Season;

// Executes a verified game function on the dispatcher producer thread. No game code
// is patched and no integrity checks are disabled. The caller must validate all sites.
internal sealed class GameThreadInvoker(RuntimeHookEngine engine)
{
    private const uint ContextAllXState = 0x0010005f;
    private const int RegionSize = 0x30000;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetProcessIdOfThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint SuspendThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeContext(IntPtr buffer, uint flags, out IntPtr context, ref uint length);
    [DllImport("kernel32.dll")] private static extern ulong GetEnabledXStateFeatures();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetXStateFeaturesMask(IntPtr context, ulong mask);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);

    public bool Invoke(ulong function, ulong instance, ulong dispatcher, uint threadId, int season, out string? error)
    {
        error = null;
        var process = engine.HandlePublic;
        var pid = engine.Pid;
        if (process == IntPtr.Zero || pid is null) { error = "Game is not attached."; return false; }
        var thread = OpenThread(0x81a, false, threadId);
        if (thread == IntPtr.Zero) { error = "Cannot open the game update thread."; return false; }

        IntPtr allocation = IntPtr.Zero, rawContext = IntPtr.Zero, context = IntPtr.Zero;
        var suspended = false;
        var redirected = false;
        var started = false;
        byte[]? savedContext = null;
        ulong alignedContext = 0;
        try
        {
            if (GetProcessIdOfThread(thread) != (uint)pid.Value) throw new InvalidOperationException("The game thread changed.");
            var restore = GetProcAddress(GetModuleHandle("kernel32.dll"), "RtlRestoreContext");
            if (restore == IntPtr.Zero) throw new InvalidOperationException("Windows context restore is unavailable.");
            var localRestore = new byte[16];
            Marshal.Copy(restore, localRestore, 0, localRestore.Length);
            var remoteRestore = engine.ReadBytesPublic((ulong)restore.ToInt64(), 16);
            if (!remoteRestore.AsSpan().SequenceEqual(localRestore)) throw new InvalidOperationException("Cannot verify Windows context restore in the game.");

            uint length = 0;
            InitializeContext(IntPtr.Zero, ContextAllXState, out _, ref length);
            if (length == 0 || length >= 0x7000) throw new InvalidOperationException("Unsupported processor context size.");
            rawContext = Marshal.AllocHGlobal(checked((int)length + 63));
            alignedContext = ((ulong)rawContext.ToInt64() + 63) & ~63UL;
            var initializedLength = length;
            if (!InitializeContext((IntPtr)(long)alignedContext, ContextAllXState, out context, ref initializedLength))
                throw new InvalidOperationException("Cannot initialize the processor context.");
            if (!SetXStateFeaturesMask(context, GetEnabledXStateFeatures() & ~3UL))
                throw new InvalidOperationException("Cannot preserve the processor's extended state.");

            allocation = Native.VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)RegionSize,
                Native.MEM_RESERVE | Native.MEM_COMMIT, Native.PAGE_READWRITE);
            if (allocation == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate game-thread call storage.");
            var remote = (ulong)allocation.ToInt64();
            var remoteContext = remote + 0x1000 + ((ulong)context.ToInt64() - alignedContext);
            var completion = remote + 0x8000;
            var asm = new List<byte>();
            void Bytes(params byte[] bytes) => asm.AddRange(bytes);
            void Imm(ulong value) => asm.AddRange(BitConverter.GetBytes(value));
            Bytes(0x48, 0x83, 0xec, 0x20, 0x48, 0xb8); Imm(completion);
            Bytes(0xc7, 0x00, 1, 0, 0, 0, 0x48, 0xb9); Imm(instance);
            Bytes(0xba); asm.AddRange(BitConverter.GetBytes(season));
            Bytes(0x48, 0xb8); Imm(function); Bytes(0xff, 0xd0, 0x48, 0xb8); Imm(completion);
            Bytes(0xc7, 0x00, 2, 0, 0, 0, 0x48, 0xb9); Imm(remoteContext);
            Bytes(0x31, 0xd2, 0x48, 0xb8); Imm((ulong)restore.ToInt64()); Bytes(0xff, 0xd0, 0xcc);
            engine.WriteBytesPublic(remote, asm.ToArray());
            if (!Native.VirtualProtectEx(process, allocation, (UIntPtr)0x1000, Native.PAGE_EXECUTE_READ, out _))
                throw new InvalidOperationException("Cannot prepare the game-thread call.");
            FlushInstructionCache(process, allocation, (UIntPtr)asm.Count);

            for (var attempt = 0; attempt < 1000; attempt++)
            {
                var previous = SuspendThread(thread);
                if (previous == uint.MaxValue) throw new InvalidOperationException("Cannot pause the game update thread.");
                suspended = true;
                if (previous != 0) throw new InvalidOperationException("The game update thread is already paused.");
                if (engine.Pid != pid) throw new InvalidOperationException("The game process changed.");
                if (engine.ReadUInt64Public(instance) != engine.MainBase + 0x65b7450 ||
                    unchecked((uint)engine.ReadInt32Public(dispatcher + 0xdc)) != threadId)
                    throw new InvalidOperationException("The season module or update thread changed.");
                if (engine.ReadUInt64Public(dispatcher + 0x50) != 0) break;
                if (ResumeThread(thread) == uint.MaxValue) throw new InvalidOperationException("Cannot resume the game update thread.");
                suspended = false;
                Thread.Sleep(2);
            }
            if (!suspended) throw new InvalidOperationException("The game did not expose an active event queue.");
            if (!Native.GetThreadContext(thread, context)) throw new InvalidOperationException("Cannot capture game thread context.");
            savedContext = new byte[initializedLength];
            Marshal.Copy((IntPtr)(long)alignedContext, savedContext, 0, savedContext.Length);
            engine.WriteBytesPublic(remote + 0x1000, savedContext);
            var rsp = (ulong)Marshal.ReadInt64(context, Native.OFF_CTX_RSP);
            if (rsp < 0x10000 || Native.VirtualQueryEx(process, (UIntPtr)(rsp - 0x10000), out var stackRegion,
                    (UIntPtr)Marshal.SizeOf<Native.MemoryBasicInformation64>()) == UIntPtr.Zero ||
                stackRegion.State != Native.MEM_COMMIT || !Native.IsReadable(stackRegion.Protect) ||
                stackRegion.BaseAddress + stackRegion.RegionSize < rsp)
                throw new InvalidOperationException("The game thread has insufficient committed stack space.");
            Marshal.WriteInt64(context, Native.OFF_CTX_RIP, (long)remote);
            Marshal.WriteInt64(context, Native.OFF_CTX_RSP, (long)((rsp - 0x200) & ~15UL));
            if (!Native.SetThreadContext(thread, context)) throw new InvalidOperationException("Cannot schedule the season request.");
            redirected = true;
            if (ResumeThread(thread) == uint.MaxValue) throw new InvalidOperationException("Cannot resume the game update thread.");
            suspended = false;
            started = true;
            redirected = false;
            for (var i = 0; i < 1500; i++)
            {
                if (!engine.IsAttached || engine.Pid != pid) throw new InvalidOperationException("The game closed during the season request.");
                var result = engine.ReadBytesPublic(completion, 4);
                if (result.Length == 4 && BitConverter.ToInt32(result) == 2)
                {
                    // Let the thread complete its context restore before another
                    // season request can be started. The remote storage is retained.
                    Thread.Sleep(100);
                    return true;
                }
                Thread.Sleep(10);
            }
            throw new InvalidOperationException("The season request did not return; do not retry until the game is restarted.");
        }
        catch (Exception ex) { error = ex.Message; return false; }
        finally
        {
            if (suspended)
            {
                if (redirected && savedContext is not null)
                {
                    Marshal.Copy(savedContext, 0, (IntPtr)(long)alignedContext, savedContext.Length);
                    Native.SetThreadContext(thread, context);
                }
                ResumeThread(thread);
            }
            // Once started, retain remote memory until process exit: Windows may still
            // be restoring the saved context after the completion flag is set.
            if (!started && allocation != IntPtr.Zero) Native.VirtualFreeEx(process, allocation, UIntPtr.Zero, Native.MEM_RELEASE);
            if (rawContext != IntPtr.Zero) Marshal.FreeHGlobal(rawContext);
            Native.CloseHandle(thread);
        }
    }
}
