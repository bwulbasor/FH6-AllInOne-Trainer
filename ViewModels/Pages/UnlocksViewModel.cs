using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FH6Mod.Cheats.RuntimeHook;
using FH6Mod.Cheats.Season;
using FH6Mod.Cheats.Sql;
using FH6Mod.Services;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace FH6Mod.ViewModels.Pages;

public partial class UnlocksViewModel : PageViewModelBase
{
    private readonly CheatService _cheats;
    private readonly GameProcessService _game;
    private readonly LogService _log;

    public override string PageTitle => "Unlocks";
    public override string PageSubtitle => "All cheats in one place.";
    public override MaterialIconKind PageIcon => MaterialIconKind.LockOpenVariantOutline;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty] private string? _logText;
    [ObservableProperty] private bool _canToggle;

    // --- Profile values ---
    [ObservableProperty] private bool _isCreditsOn;
    [ObservableProperty] private string _creditsAmountText = "999999999";

    [ObservableProperty] private bool _isWheelspinsOn;
    [ObservableProperty] private string _wheelspinsAmountText = "999";

    [ObservableProperty] private bool _isSuperWheelspinsOn;
    [ObservableProperty] private string _superWheelspinsAmountText = "999";

    [ObservableProperty] private bool _isSkillPointsOn;
    [ObservableProperty] private string _skillPointsAmountText = "999999";

    [ObservableProperty] private bool _isSellPayoutOn;
    [ObservableProperty] private string _sellPayoutText = "5";

    // --- Drift & Skills ---
    [ObservableProperty] private bool _isDriftMultiOn;
    [ObservableProperty] private string _driftMultiText = "10";
    [ObservableProperty] private bool _isNoSkillBreakOn;
    [ObservableProperty] private bool _isFreezeAIOn;

    // --- Time of Day ---
    [ObservableProperty] private bool _isTimeOfDayOn;
    [ObservableProperty] private string _timeOfDayText = "12.0";

    // --- Mission Timer ---
    [ObservableProperty] private bool _isMissionTimerOn;
    [ObservableProperty] private string _missionTimerText = "0";

    // --- Season ---
    [ObservableProperty] private int _selectedSeason;
    [ObservableProperty] private string _currentSeasonText = "Unknown";
    [ObservableProperty] private bool _seasonAvailable;

    // --- Weather ---
    [ObservableProperty] private string _currentWeatherText = "Press Read to capture.";
    [ObservableProperty] private string _rainText = "0.00";
    [ObservableProperty] private string _wetnessText = "0.00";
    [ObservableProperty] private string _atmosphereText = "0.00";
    [ObservableProperty] private string _windText = "0.00";
    [ObservableProperty] private string _stormText = "0.00";
    [ObservableProperty] private bool _stormAvailable;

    // --- XP ---
    [ObservableProperty] private string _xpAmountText = "100000";

    // --- Instant Rewards ---
    [ObservableProperty] private string _grantAmountText = "100";
    [ObservableProperty] private string _currentRewardsText = "Current counts appear after first use.";

    public UnlocksViewModel()
        : this(App.Services.GetRequiredService<CheatService>(),
               App.Services.GetRequiredService<GameProcessService>(),
               App.Services.GetRequiredService<LogService>()) { }

    public UnlocksViewModel(CheatService cheats, GameProcessService game, LogService log)
    {
        _cheats = cheats;
        _game = game;
        _log = log;
        _game.StatusChanged += OnGameStatusChanged;
        _log.Changed += OnLogChanged;
        CanToggle = _game.IsAttached;
        if (CanToggle) RefreshSeason();
    }

    private void OnGameStatusChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CanToggle = _game.IsAttached;
            if (!CanToggle)
            {
                StatusMessage = "FH6 is not running — start the game first.";
                SeasonAvailable = false;
                CurrentSeasonText = "Unknown";
                CurrentRewardsText = "Current counts appear after first use.";
            }
            else
            {
                RefreshSeason();
                RefreshRewards();
            }
        });
    }

    private void OnLogChanged()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LogText = _log.GetTail(60);
        });
    }

    private static int Parse(string s, int fallback)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static int ParseFloatAsIntBits(string s, float fallback)
    {
        if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || f <= 0)
            f = fallback;
        return BitConverter.ToInt32(BitConverter.GetBytes(f), 0);
    }

    private void Toggle(RuntimeProfileFeature f, bool target, int value, string nameLabel)
    {
        var ok = _cheats.Apply(f, value, target);
        SetStatus(ok, ok
            ? (target ? $"{nameLabel} ON." : $"{nameLabel} OFF.")
            : _cheats.LastError);
    }

    private void ApplyValue(RuntimeProfileFeature f, int value, string nameLabel)
    {
        var ok = _cheats.UpdateValue(f, value);
        SetStatus(ok, ok ? $"{nameLabel} updated." : _cheats.LastError);
    }

    private void SetStatus(bool ok, string? msg)
    {
        StatusIsError = !ok;
        StatusMessage = msg;
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            await System.Threading.Tasks.Task.Delay(5000);
            StatusMessage = null;
        });
    }

    [RelayCommand] private void ClearLog() => _log.Clear();

    // ===== Quick Start =====
    [RelayCommand]
    private void QuickStart()
    {
        var cr = 999_999_999;
        var ok1 = _cheats.Apply(RuntimeProfileFeature.Credits, cr, true);
        IsCreditsOn = _cheats.IsActive(RuntimeProfileFeature.Credits);
        var ok2 = _cheats.RunSql(SqlFeature.FreeCarPrices);
        var ok3 = _cheats.RunSql(SqlFeature.AutoshowUnlock);
        var ok4 = _cheats.RunSql(SqlFeature.InstallFlags);
        var ok5 = _cheats.RunSql(SqlFeature.AddAllCars);

        var allOk = ok1 && ok2 && ok3 && ok4 && ok5;
        StatusIsError = !allOk;
        StatusMessage = allOk
            ? "Quick Start done — 999M credits, all cars free & unlocked."
            : "Partially applied. Check Database tab.";
    }

    // ===== Max All =====
    [RelayCommand]
    private void MaxAll()
    {
        var cr = 999_999_999;
        var ws = 999;
        var sws = 999;
        var sp = 999_999;

        CreditsAmountText = cr.ToString();
        WheelspinsAmountText = ws.ToString();
        SuperWheelspinsAmountText = sws.ToString();
        SkillPointsAmountText = sp.ToString();

        var ok1 = _cheats.Apply(RuntimeProfileFeature.Credits, cr, true);
        IsCreditsOn = _cheats.IsActive(RuntimeProfileFeature.Credits);
        var ok2 = _cheats.Apply(RuntimeProfileFeature.Wheelspins, ws, true);
        IsWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.Wheelspins);
        var ok3 = _cheats.Apply(RuntimeProfileFeature.SuperWheelspins, sws, true);
        IsSuperWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.SuperWheelspins);
        var ok4 = _cheats.Apply(RuntimeProfileFeature.SkillPoints, sp, true);
        IsSkillPointsOn = _cheats.IsActive(RuntimeProfileFeature.SkillPoints);

        var allOk = ok1 && ok2 && ok3 && ok4;
        SetStatus(allOk, allOk
            ? "Max All applied — all profile values maxed."
            : _cheats.LastError);
    }

    // ===== Credits =====
    [RelayCommand] private void ToggleCredits()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.Credits);
        Toggle(RuntimeProfileFeature.Credits, on, Parse(CreditsAmountText, 1_000_000), "Credits");
        IsCreditsOn = _cheats.IsActive(RuntimeProfileFeature.Credits);
    }
    [RelayCommand] private void ApplyCredits()
        => ApplyValue(RuntimeProfileFeature.Credits, Parse(CreditsAmountText, 1_000_000), "Credits");
    [RelayCommand] private void SetCredits(string? amount) { if (amount is not null) { CreditsAmountText = amount; if (IsCreditsOn) ApplyCredits(); } }

    // ===== Wheelspins =====
    [RelayCommand] private void ToggleWheelspins()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.Wheelspins);
        Toggle(RuntimeProfileFeature.Wheelspins, on, Parse(WheelspinsAmountText, 100), "Wheelspins");
        IsWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.Wheelspins);
    }
    [RelayCommand] private void ApplyWheelspins()
        => ApplyValue(RuntimeProfileFeature.Wheelspins, Parse(WheelspinsAmountText, 100), "Wheelspins");
    [RelayCommand] private void SetWheelspins(string? a) { if (a is not null) { WheelspinsAmountText = a; if (IsWheelspinsOn) ApplyWheelspins(); } }

    // ===== Super Wheelspins =====
    [RelayCommand] private void ToggleSuperWheelspins()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.SuperWheelspins);
        Toggle(RuntimeProfileFeature.SuperWheelspins, on, Parse(SuperWheelspinsAmountText, 100), "Super Wheelspins");
        IsSuperWheelspinsOn = _cheats.IsActive(RuntimeProfileFeature.SuperWheelspins);
    }
    [RelayCommand] private void ApplySuperWheelspins()
        => ApplyValue(RuntimeProfileFeature.SuperWheelspins, Parse(SuperWheelspinsAmountText, 100), "Super Wheelspins");
    [RelayCommand] private void SetSuperWheelspins(string? a) { if (a is not null) { SuperWheelspinsAmountText = a; if (IsSuperWheelspinsOn) ApplySuperWheelspins(); } }

    // ===== Skill Points =====
    [RelayCommand] private void ToggleSkillPoints()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.SkillPoints);
        Toggle(RuntimeProfileFeature.SkillPoints, on, Parse(SkillPointsAmountText, 10_000), "Skill Points");
        IsSkillPointsOn = _cheats.IsActive(RuntimeProfileFeature.SkillPoints);
    }
    [RelayCommand] private void ApplySkillPoints()
        => ApplyValue(RuntimeProfileFeature.SkillPoints, Parse(SkillPointsAmountText, 10_000), "Skill Points");
    [RelayCommand] private void SetSkillPoints(string? a) { if (a is not null) { SkillPointsAmountText = a; if (IsSkillPointsOn) ApplySkillPoints(); } }

    // ===== Sell Payout =====
    [RelayCommand] private void ToggleSellPayout()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.SellFactor);
        Toggle(RuntimeProfileFeature.SellFactor, on, Parse(SellPayoutText, 5), "Sell Payout x");
        IsSellPayoutOn = _cheats.IsActive(RuntimeProfileFeature.SellFactor);
    }
    [RelayCommand] private void ApplySellPayout()
        => ApplyValue(RuntimeProfileFeature.SellFactor, Parse(SellPayoutText, 5), "Sell Payout x");

    // ===== Drift Score Multiplier =====
    [RelayCommand] private void ToggleDriftMulti()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.DriftScoreMultiplier);
        Toggle(RuntimeProfileFeature.DriftScoreMultiplier, on, ParseFloatAsIntBits(DriftMultiText, 10f), "Drift Score x");
        IsDriftMultiOn = _cheats.IsActive(RuntimeProfileFeature.DriftScoreMultiplier);
    }
    [RelayCommand] private void ApplyDriftMulti()
        => ApplyValue(RuntimeProfileFeature.DriftScoreMultiplier, ParseFloatAsIntBits(DriftMultiText, 10f), "Drift Score x");

    // ===== No Skill Break =====
    [RelayCommand] private void ToggleNoSkillBreak()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.NoSkillBreak);
        Toggle(RuntimeProfileFeature.NoSkillBreak, on, 0, "No Skill Break");
        IsNoSkillBreakOn = _cheats.IsActive(RuntimeProfileFeature.NoSkillBreak);
    }

    // ===== Freeze AI =====
    [RelayCommand] private void ToggleFreezeAI()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.FreezeAI);
        Toggle(RuntimeProfileFeature.FreezeAI, on, 0, "Freeze AI");
        IsFreezeAIOn = _cheats.IsActive(RuntimeProfileFeature.FreezeAI);
    }

    // ===== Time of Day =====
    [RelayCommand] private void ToggleTimeOfDay()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.TimeOfDay);
        Toggle(RuntimeProfileFeature.TimeOfDay, on, ParseFloatAsIntBits(TimeOfDayText, 12f), "Time of Day");
        IsTimeOfDayOn = _cheats.IsActive(RuntimeProfileFeature.TimeOfDay);
    }
    [RelayCommand] private void ApplyTimeOfDay()
        => ApplyValue(RuntimeProfileFeature.TimeOfDay, ParseFloatAsIntBits(TimeOfDayText, 12f), "Time of Day");
    [RelayCommand] private void SetTimeOfDay(string? a) { if (a is not null) { TimeOfDayText = a; if (IsTimeOfDayOn) ApplyTimeOfDay(); } }

    // ===== Mission Timer =====
    [RelayCommand] private void ToggleMissionTimer()
    {
        var on = !_cheats.IsActive(RuntimeProfileFeature.MissionTimerTick);
        Toggle(RuntimeProfileFeature.MissionTimerTick, on, ParseFloatAsIntBits(MissionTimerText, 0f), "Mission Timer");
        IsMissionTimerOn = _cheats.IsActive(RuntimeProfileFeature.MissionTimerTick);
    }
    [RelayCommand] private void ApplyMissionTimer()
        => ApplyValue(RuntimeProfileFeature.MissionTimerTick, ParseFloatAsIntBits(MissionTimerText, 0f), "Mission Timer");

    // ===== Season =====
    [RelayCommand]
    private async System.Threading.Tasks.Task SetSeasonToSpring() { await SetSeasonValue(0, "Spring"); }
    [RelayCommand]
    private async System.Threading.Tasks.Task SetSeasonToSummer() { await SetSeasonValue(1, "Summer"); }
    [RelayCommand]
    private async System.Threading.Tasks.Task SetSeasonToAutumn() { await SetSeasonValue(2, "Autumn"); }
    [RelayCommand]
    private async System.Threading.Tasks.Task SetSeasonToWinter() { await SetSeasonValue(3, "Winter"); }

    private async System.Threading.Tasks.Task SetSeasonValue(int season, string label)
    {
        SetStatus(true, $"Requesting {label}; locating the game state may take a moment.");
        var result = await System.Threading.Tasks.Task.Run(() =>
        {
            var ok = _cheats.SetSeason(season, out var err);
            return (ok, err);
        });
        SetStatus(result.ok, result.ok ? $"Season set to {label}. Fast-travel if the scenery has not refreshed." : result.err);
        if (result.ok) RefreshSeason();
    }

    private void RefreshSeason()
    {
        var s = _cheats.GetCurrentSeason();
        if (s is >= 0 and <= 3)
        {
            CurrentSeasonText = SeasonChanger.SeasonName(s);
            SeasonAvailable = true;
        }
        else
        {
            CurrentSeasonText = "Not loaded";
            SeasonAvailable = false;
        }
    }

    [RelayCommand]
    private void RefreshSeasonStatus()
    {
        if (!_game.IsAttached) { CurrentSeasonText = "Not loaded"; SeasonAvailable = false; return; }
        RefreshSeason();
    }

    // ===== Weather =====
    private static float ParseF(string s, float fallback)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    [RelayCommand]
    private void ReadWeather()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        var s = _cheats.ReadWeather();
        if (s is null)
        {
            CurrentWeatherText = "Not captured yet — the game must render one frame. Press Read again.";
            SetStatus(false, "Weather entity not captured yet.");
            return;
        }
        CurrentWeatherText = $"Rain {s.Rain:0.00} · Wetness {s.Wetness:0.00} · Atmosphere {s.Atmosphere:0.00} · Wind {s.Wind:0.00}";
        if (s.Storm.HasValue) CurrentWeatherText += $" · Storm {s.Storm.Value:0.00}";
        RainText = s.Rain.ToString("0.00", CultureInfo.InvariantCulture);
        WetnessText = s.Wetness.ToString("0.00", CultureInfo.InvariantCulture);
        AtmosphereText = s.Atmosphere.ToString("0.00", CultureInfo.InvariantCulture);
        WindText = s.Wind.ToString("0.00", CultureInfo.InvariantCulture);
        StormAvailable = s.Storm.HasValue;
        if (s.Storm.HasValue) StormText = s.Storm.Value.ToString("0.00", CultureInfo.InvariantCulture);
        SetStatus(true, "Weather state read.");
    }

    [RelayCommand]
    private void ApplyWeather()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        var ok = _cheats.SetWeather(
            ParseF(RainText, 0f), ParseF(WetnessText, 0f),
            ParseF(AtmosphereText, 0f), ParseF(WindText, 0f),
            StormAvailable ? ParseF(StormText, 0f) : null, out var err);
        SetStatus(ok, ok ? "Weather applied." : err);
        if (ok) ReadWeather();
    }

    [RelayCommand] private void SetDay() => SetClock(12, "Day");
    [RelayCommand] private void SetEvening() => SetClock(18, "Evening");
    [RelayCommand] private void SetNight() => SetClock(20, "Night");

    private void SetClock(float hour, string label)
    {
        var ok = _cheats.SetTimeOfDay(hour, out var error);
        SetStatus(ok, ok ? $"{label} selected." : error);
    }

    [RelayCommand]
    private void RestoreClock()
    {
        var ok = _cheats.RestoreTimeOfDay(out var error);
        SetStatus(ok, ok ? "Scripted time restored. Active weather controls may still hold the weather script." : error);
    }

    // ===== XP grant =====
    [RelayCommand]
    private void GrantXp()
    {
        if (!CanToggle) { SetStatus(false, "FH6 is not running."); return; }
        if (!uint.TryParse(XpAmountText, out var amount) || amount == 0)
        {
            SetStatus(false, "Enter a valid XP amount.");
            return;
        }
        var ok = _cheats.GrantXp(amount, out var err);
        SetStatus(ok, ok ? $"Granted {amount} XP — level-ups and notifications follow the game's own path." : err);
    }

    // ===== Instant reward grants (call the game's grant function — no scanning) =====
    [RelayCommand]
    private void GrantWheelspins()
    {
        var v = Parse(GrantAmountText, 100);
        var ok = _cheats.GrantWheelspins(v);
        SetStatus(ok, ok ? $"Granted {v} wheelspins." : _cheats.LastError);
        RefreshRewards();
    }

    [RelayCommand]
    private void GrantSuperWheelspins()
    {
        var v = Parse(GrantAmountText, 100);
        var ok = _cheats.GrantSuperWheelspins(v);
        SetStatus(ok, ok ? $"Granted {v} super wheelspins." : _cheats.LastError);
        RefreshRewards();
    }

    private void RefreshRewards()
    {
        var r = _cheats.GetCurrentRewards();
        CurrentRewardsText = r is null
            ? "Current counts appear after first use."
            : $"Current: {r.Value.Wheelspins} wheelspins · {r.Value.SuperWheelspins} super";
    }
}
