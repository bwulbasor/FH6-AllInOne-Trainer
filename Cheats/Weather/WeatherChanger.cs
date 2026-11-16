using System;
using System.Diagnostics;
using System.Linq;
using FH6Mod.Cheats.RuntimeHook;

namespace FH6Mod.Cheats.Weather;

/// <summary>
/// Live weather intensity control. The weather entity exposes four one-instruction
/// getters (RE-verified, identical across v379/v382/v403):
///   GetRainIntensity       → movss xmm0,[rcx+0x16C]
///   GetEnvironmentWetness  → movss xmm0,[rcx+0x170]
///   GetAtmosphereIntensity → movss xmm0,[rcx+0x174]
///   GetWindIntensity       → movss xmm0,[rcx+0x178]
/// The engine hooks the rain getter to capture the entity pointer (the game calls
/// it every frame, so capture is immediate); this class then reads/writes the four
/// intensity floats directly — data writes only, no code patches beyond the capture.
/// </summary>
public sealed class WeatherChanger
{
    private readonly RuntimeHookEngine _engine;
    private ulong _weatherRootAddress;
    private int? _directBuildPid;
    private bool _directBuild;
    private ulong _savedManager;
    private int? _savedPid;
    private ulong[]? _savedAddresses;
    private byte[][]? _savedValues;
    private WeatherState? _lastDirectRead;
    private ulong _baselineScript;
    private int? _baselinePid;
    private byte[]? _baselineScriptPause;
    private byte[]? _savedClockSample;

    private const int OFF_RAIN = 0x16C;
    private const int OFF_WETNESS = 0x170;
    private const int OFF_ATMOSPHERE = 0x174;
    private const int OFF_WIND = 0x178;

    public WeatherChanger(RuntimeHookEngine engine) => _engine = engine;

    public sealed record WeatherState(float Rain, float Wetness, float Atmosphere, float Wind, float? Storm = null);

    /// <summary>
    /// Captured entity pointer validated: plausible address and at least one of the
    /// four intensity floats reads as a sane (small, non-NaN) value.
    /// </summary>
    private ulong? ValidEntity()
    {
        var captured = _engine.GetCapturedWeatherEntity();
        if (captured is not { } ptr || ptr < 0x10000 || ptr > 0x0000_7FFF_FFFF_FFFF)
            return null;
        try
        {
            var s = ReadState();
            if (s is null) return null;
            if (float.IsNaN(s.Rain) || Math.Abs(s.Rain) > 1000f) return null;
            return ptr;
        }
        catch { return null; }
    }

    public bool UsesDirectWeather
    {
        get
        {
            var pid = _engine.Pid;
            if (pid == null) return false;
            if (_directBuildPid == pid) return _directBuild;
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                _directBuild = process.MainModule?.FileVersionInfo.FileVersion == "6.483.970.0";
            }
            catch (Exception) { _directBuild = false; }
            _directBuildPid = pid;
            _weatherRootAddress = 0;
            return _directBuild;
        }
    }

    public bool IsResolved => UsesDirectWeather ? ResolveDirectManager() != 0 : ValidEntity() != null;

    public WeatherState? ReadState()
    {
        if (UsesDirectWeather)
        {
            var manager = ResolveDirectManager();
            if (manager == 0) return null;
            _lastDirectRead = new WeatherState(ReadFloat(manager + 0x50), ReadFloat(manager + 0x34),
                ReadFloat(manager + 0x18), ReadFloat(manager + 0xD4), ReadFloat(manager + 0xE4));
            return _lastDirectRead;
        }
        var captured = _engine.GetCapturedWeatherEntity();
        if (captured is not { } ptr) return null;
        return new WeatherState(
            ReadFloat(ptr + OFF_RAIN),
            ReadFloat(ptr + OFF_WETNESS),
            ReadFloat(ptr + OFF_ATMOSPHERE),
            ReadFloat(ptr + OFF_WIND));
    }

    public bool SetState(float rain, float wetness, float atmosphere, float wind, out string? error)
        => SetState(rain, wetness, atmosphere, wind, null, out error);

    public bool SetState(float rain, float wetness, float atmosphere, float wind, float? storm, out string? error)
    {
        error = null;
        if (UsesDirectWeather)
            return SetDirectWeather(rain, wetness, atmosphere, wind, storm, out error);
        if (storm.HasValue)
        {
            error = "Storm intensity is unavailable on this game build.";
            return false;
        }
        if (ValidEntity() is not { } ptr)
        {
            error = "Weather entity not captured yet. Press Read once more a second later (the game captures it while rendering).";
            return false;
        }
        WriteFloat(ptr + OFF_RAIN, Clamp(rain));
        WriteFloat(ptr + OFF_WETNESS, Clamp(wetness));
        WriteFloat(ptr + OFF_ATMOSPHERE, Clamp(atmosphere));
        WriteFloat(ptr + OFF_WIND, Clamp(wind));
        _engine.LogPublic($"Weather: set rain={rain:0.00} wetness={wetness:0.00} atmosphere={atmosphere:0.00} wind={wind:0.00} (entity 0x{ptr:X})");
        return true;
    }

    private static float Clamp(float v) => Math.Clamp(v, 0f, 1f);

    private float ReadFloat(ulong addr)
    {
        var b = _engine.ReadBytesPublic(addr, 4);
        return b.Length < 4 ? 0f : BitConverter.ToSingle(b, 0);
    }

    private void WriteFloat(ulong addr, float v) => _engine.WriteBytesPublic(addr, BitConverter.GetBytes(v));

    private ulong ResolveDirectManager()
    {
        if (!_engine.IsAttached) return 0;
        if (_weatherRootAddress == 0)
        {
            const string signature = "F3 0F 10 81 18 03 00 00 48 8B 0D ? ? ? ? 0F 43 C2 F3 0F 11 45 23 88 45 F7 0F 2F C1 41 0F B6 C1 0F 43 C2 33 D2 88 45 1F E8";
            var bytes = _engine.ReadBytesPublic(_engine.MainBase, _engine.MainSize);
            var matches = Pattern.FindAll(bytes, Pattern.Parse(signature), 2).ToArray();
            if (matches.Length != 1) return 0;
            var match = matches[0];
            var displacement = BitConverter.ToInt32(bytes, match + 11);
            var root = (long)_engine.MainBase + match + 15L + displacement;
            if (root < (long)_engine.MainBase || root + 8 > (long)_engine.MainBase + _engine.MainSize)
                return 0;
            _weatherRootAddress = (ulong)root;
        }
        var rootPtr = _engine.ReadUInt64Public(_weatherRootAddress);
        if (rootPtr < 0x10000) return 0;
        var world = _engine.ReadUInt64Public(rootPtr + 0x1D0);
        if (world < 0x10000) return 0;
        var game = _engine.ReadUInt64Public(world + 0x128);
        if (game < 0x10000) return 0;
        var manager = _engine.ReadUInt64Public(game + 0x58);
        if (manager < 0x10000 || manager > 0x0000_7FFF_FFFF_FFFF) return 0;
        var current = _engine.ReadBytesPublic(manager + 0x50, 4);
        return current.Length == 4 && float.IsFinite(BitConverter.ToSingle(current, 0)) ? manager : 0;
    }

    private bool SetDirectWeather(float rain, float wetness, float atmosphere, float wind, float? storm, out string? error)
    {
        error = null;
        var manager = ResolveDirectManager();
        var state = _lastDirectRead;
        if (manager == 0 || state == null)
        {
            error = "Weather manager could not be verified for this game build.";
            return false;
        }
        if (!float.IsFinite(rain) || !float.IsFinite(wetness) || !float.IsFinite(atmosphere) || !float.IsFinite(wind) || (storm.HasValue && !float.IsFinite(storm.Value)))
        {
            error = "Weather values must be finite numbers.";
            return false;
        }
        if (Math.Abs(wetness - state.Wetness) > 0.011f || Math.Abs(atmosphere - state.Atmosphere) > 0.011f || Math.Abs(wind - state.Wind) > 0.011f)
        {
            error = "On this build, rain and storm intensity are available. Press Read and leave wetness, atmosphere and wind unchanged.";
            return false;
        }
        var script = _engine.ReadUInt64Public(manager + 0x1B0);
        if (script < 0x10000 || script > 0x0000_7FFF_FFFF_FFFF || _engine.ReadBytesPublic(script, 1).Length != 1)
        {
            error = "The active weather script could not be verified.";
            return false;
        }
        if (!CompatibleScript(script)) { error = "The weather script changed. Restart the trainer before applying weather."; return false; }
        var addresses = new[] { manager + 0x120, manager + 0x11C, manager + 0x14C, manager + 0x50,
            script, manager + 0x138, manager + 0x134, manager + 0x150, manager + 0xE4 };
        var lengths = new[] { 4, 1, 1, 4, 1, 4, 1, 1, 4 };
        var original = addresses.Select((address, index) => _engine.ReadBytesPublic(address, lengths[index])).ToArray();
        if (original.Where((bytes, index) => bytes.Length != lengths[index]).Any())
        {
            error = "The weather state could not be read completely.";
            return false;
        }
        try
        {
            var intensity = BitConverter.GetBytes(Clamp(rain));
            _engine.WriteBytesPublic(addresses[0], intensity);
            _engine.WriteBytesPublic(addresses[1], [1]);
            _engine.WriteBytesPublic(addresses[2], [0]);
            _engine.WriteBytesPublic(addresses[3], intensity);
            _engine.WriteBytesPublic(addresses[4], [1]);
            if (storm.HasValue)
            {
                var stormIntensity = BitConverter.GetBytes(Clamp(storm.Value));
                _engine.WriteBytesPublic(addresses[5], stormIntensity);
                _engine.WriteBytesPublic(addresses[6], [1]);
                _engine.WriteBytesPublic(addresses[7], [0]);
                _engine.WriteBytesPublic(addresses[8], stormIntensity);
            }
            if (_savedManager != manager || _savedPid != _engine.Pid)
            {
                _savedManager = manager;
                _savedPid = _engine.Pid;
                _savedAddresses = addresses;
                _savedValues = original;
            }
            RememberScriptBaseline(script, original[4]);
            _engine.LogPublic($"Weather: set rain={rain:0.00}, storm={storm:0.00} using the build 483 weather manager.");
            return true;
        }
        catch (Exception ex)
        {
            for (var index = addresses.Length - 1; index >= 0; index--)
            {
                try { _engine.WriteBytesPublic(addresses[index], original[index]); }
                catch { }
            }
            error = $"Could not apply rain: {ex.Message}";
            return false;
        }
    }

    // On this build the weather script supplies the visual clock on every update.
    // Holding only the clock value causes flicker; holding the script's validated
    // time sample lets the game update the clock normally with a stable value.
    public bool SetTimeOfDay(float hours, out string? error)
    {
        error = null;
        if (!UsesDirectWeather) { error = "Time controls are unavailable on this game build."; return false; }
        if (!float.IsFinite(hours) || hours < 0 || hours >= 24) { error = "Invalid time of day."; return false; }
        try
        {
            var manager = ResolveDirectManager();
            if (manager == 0) { error = "The weather manager could not be found."; return false; }
            var b = _engine.MainBase;
            if (!_engine.ReadBytesPublic(b + 0x757d66, 8).AsSpan().SequenceEqual(new byte[] { 0xf3, 0x0f, 0x10, 0xaf, 0xb8, 0, 0, 0 }) ||
                !_engine.ReadBytesPublic(b + 0x436590, 6).AsSpan().SequenceEqual(new byte[] { 0x8b, 0x42, 4, 0x89, 0x41, 0x64 }) ||
                !_engine.ReadBytesPublic(b + 0x2041ab0, 8).AsSpan().SequenceEqual(new byte[] { 0x48, 0x8b, 0x81, 0x88, 3, 0, 0, 0xc3 }))
            { error = "The time-of-day signatures do not match this game build."; return false; }
            var script = _engine.ReadUInt64Public(manager + 0x1b0);
            if (script < 0x10000 || script > 0x00007fffffffffffUL || !CompatibleScript(script))
            { error = "The active weather script changed. Restart the trainer and try again."; return false; }
            var root = _engine.ReadUInt64Public(_weatherRootAddress);
            var world = _engine.ReadUInt64Public(root + 0x1d0);
            var game = _engine.ReadUInt64Public(world + 0x128);
            var clock = _engine.ReadUInt64Public(game + 0x388);
            if (clock < 0x10000 || clock > 0x00007fffffffffffUL) { error = "The game clock could not be verified."; return false; }
            var oldFlag = _engine.ReadBytesPublic(script, 1);
            var oldSample = _engine.ReadBytesPublic(script + 0xb8, 4);
            var oldClock = _engine.ReadBytesPublic(clock + 0x64, 4);
            if (oldFlag.Length != 1 || oldSample.Length != 4 || oldClock.Length != 4 ||
                !float.IsFinite(BitConverter.ToSingle(oldSample)) || !float.IsFinite(BitConverter.ToSingle(oldClock)))
            { error = "The game clock state could not be read."; return false; }
            try
            {
                var time = BitConverter.GetBytes(hours * 3600f);
                _engine.WriteBytesPublic(script, [1]);
                _engine.WriteBytesPublic(script + 0xb8, time);
                _engine.WriteBytesPublic(clock + 0x64, time);
                RememberScriptBaseline(script, oldFlag);
                _savedClockSample ??= oldSample;
                _engine.LogPublic($"Time of day set to {hours:0.##}:00.");
                return true;
            }
            catch
            {
                try { _engine.WriteBytesPublic(script + 0xb8, oldSample); } catch { }
                try { _engine.WriteBytesPublic(clock + 0x64, oldClock); } catch { }
                try { _engine.WriteBytesPublic(script, oldFlag); } catch { }
                throw;
            }
        }
        catch (Exception ex) { error = $"Could not set time of day: {ex.Message}"; return false; }
    }

    public bool RestoreTimeOfDay(out string? error)
    {
        error = null;
        if (_savedClockSample == null) return true;
        try
        {
            var manager = ResolveDirectManager();
            if (manager == 0 || _baselinePid != _engine.Pid || _engine.ReadUInt64Public(manager + 0x1b0) != _baselineScript)
            { error = "The game world changed; the previous clock state cannot be restored safely."; return false; }
            _engine.WriteBytesPublic(_baselineScript + 0xb8, _savedClockSample);
            if (_savedAddresses == null && _baselineScriptPause != null)
            {
                _engine.WriteBytesPublic(_baselineScript, _baselineScriptPause);
                _baselineScript = 0; _baselinePid = null; _baselineScriptPause = null;
            }
            _savedClockSample = null;
            return true;
        }
        catch (Exception ex) { error = $"Could not restore time of day: {ex.Message}"; return false; }
    }

    private bool CompatibleScript(ulong script) => _baselineScriptPause == null ||
        (_baselinePid == _engine.Pid && _baselineScript == script);

    private void RememberScriptBaseline(ulong script, byte[] pause)
    {
        if (_baselineScriptPause != null) return;
        _baselineScript = script; _baselinePid = _engine.Pid; _baselineScriptPause = pause;
    }

    public void Reset()
    {
        if (_engine.IsAttached && _baselinePid == _engine.Pid && _baselineScriptPause != null)
        {
            try
            {
                var manager = ResolveDirectManager();
                if (manager != 0 && _engine.ReadUInt64Public(manager + 0x1B0) == _baselineScript)
                {
                    if (_savedPid == _engine.Pid && _savedManager == manager && _savedAddresses != null && _savedValues != null)
                        for (var index = _savedAddresses.Length - 1; index >= 0; index--)
                            if (index != 4) TryRestore(_savedAddresses[index], _savedValues[index]);
                    if (_savedClockSample != null) TryRestore(_baselineScript + 0xb8, _savedClockSample);
                    TryRestore(_baselineScript, _baselineScriptPause);
                }
            }
            catch (Exception ex) { _engine.LogPublic($"Weather reset failed: {ex.Message}"); }
        }
        _savedManager = 0;
        _savedPid = null;
        _savedAddresses = null;
        _savedValues = null;
        _baselineScript = 0;
        _baselinePid = null;
        _baselineScriptPause = null;
        _savedClockSample = null;
        _lastDirectRead = null;
        _weatherRootAddress = 0;
        _directBuildPid = null;
    }

    private void TryRestore(ulong address, byte[] value)
    {
        try { _engine.WriteBytesPublic(address, value); }
        catch (Exception ex) { _engine.LogPublic($"Weather restore failed: {ex.Message}"); }
    }
}
