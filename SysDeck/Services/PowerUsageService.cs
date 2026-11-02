using System.Globalization;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

/// <summary>Réglages du calcul de consommation : prix du kWh et estimation de ce qu'aucun capteur ne mesure.</summary>
public sealed class PowerSettings : ObservableSettings
{
    private double _pricePerKwh = 0.1952;
    private bool _useOffPeak;
    private double _offPeakPricePerKwh = 0.1635;
    private double _offPeakStartHour = 22;
    private double _offPeakEndHour = 6;
    private double _cpuIdleWatts = 30;
    private double _cpuMaxWatts = 120;
    private double _otherWatts = 50;
    private double _monitorWatts;
    private double _psuEfficiency = 0.9;
    private double _gaugeMaxWatts = 600;

    /// <summary>Prix TTC du kWh (heures pleines quand l'option heures creuses est active).</summary>
    public double PricePerKwh { get => _pricePerKwh; set => Set(ref _pricePerKwh, value); }
    public bool UseOffPeak { get => _useOffPeak; set => Set(ref _useOffPeak, value); }
    public double OffPeakPricePerKwh { get => _offPeakPricePerKwh; set => Set(ref _offPeakPricePerKwh, value); }
    /// <summary>Début et fin des heures creuses, en heures décimales (22,5 = 22 h 30) ; la plage peut passer minuit.</summary>
    public double OffPeakStartHour { get => _offPeakStartHour; set => Set(ref _offPeakStartHour, value); }
    public double OffPeakEndHour { get => _offPeakEndHour; set => Set(ref _offPeakEndHour, value); }

    /// <summary>Estimation du processeur quand son capteur est bloqué (Intégrité de la mémoire) : interpolée selon la charge.</summary>
    public double CpuIdleWatts { get => _cpuIdleWatts; set => Set(ref _cpuIdleWatts, value); }
    public double CpuMaxWatts { get => _cpuMaxWatts; set => Set(ref _cpuMaxWatts, value); }
    /// <summary>Carte mère, RAM, SSD, ventilateurs, périphériques USB : aucun capteur, forfait fixe.</summary>
    public double OtherWatts { get => _otherWatts; set => Set(ref _otherWatts, value); }
    /// <summary>Écran(s), branchés sur la prise et pas sur l'alimentation du PC.</summary>
    public double MonitorWatts { get => _monitorWatts; set => Set(ref _monitorWatts, value); }
    /// <summary>Rendement de l'alimentation : la prise fournit plus que ce que consomment les composants.</summary>
    public double PsuEfficiency { get => _psuEfficiency; set => Set(ref _psuEfficiency, value); }
    public double GaugeMaxWatts { get => _gaugeMaxWatts; set => Set(ref _gaugeMaxWatts, value); }

    private DateTime? _firstPowerOnDate;
    private double _pastAverageWatts;

    /// <summary>Première mise en service du PC ; null = date d'installation de Windows.</summary>
    public DateTime? FirstPowerOnDate { get => _firstPowerOnDate; set => Set(ref _firstPowerOnDate, value); }
    /// <summary>Puissance moyenne appliquée aux heures d'avant SysDeck ; 0 = moyenne mesurée par SysDeck.</summary>
    public double PastAverageWatts { get => _pastAverageWatts; set => Set(ref _pastAverageWatts, value); }

    private double _dayStartHour = 6;
    private double _dayEndHour = 20;

    /// <summary>Heures d'utilisation : « jour » de DayStartHour à DayEndHour, « soir » le reste (passe minuit).</summary>
    public double DayStartHour { get => _dayStartHour; set => Set(ref _dayStartHour, value); }
    public double DayEndHour { get => _dayEndHour; set => Set(ref _dayEndHour, value); }

    public bool IsDayTime(DateTime localTime) => PowerOnHistory.IsDayHour(localTime.TimeOfDay.TotalHours, DayStartHour, DayEndHour);

    /// <summary>Prix moyen d'un kWh sur une journée : pondéré par la durée des heures creuses si l'option est active.</summary>
    public double AveragePrice()
    {
        if (!UseOffPeak || OffPeakStartHour == OffPeakEndHour) return PricePerKwh;
        var offPeakHours = (OffPeakEndHour - OffPeakStartHour + 24) % 24;
        return (offPeakHours * OffPeakPricePerKwh + (24 - offPeakHours) * PricePerKwh) / 24;
    }

    public bool IsOffPeak(DateTime localTime)
    {
        if (!UseOffPeak || OffPeakStartHour == OffPeakEndHour) return false;
        var hour = localTime.TimeOfDay.TotalHours;
        return OffPeakStartHour < OffPeakEndHour
            ? hour >= OffPeakStartHour && hour < OffPeakEndHour
            : hour >= OffPeakStartHour || hour < OffPeakEndHour;
    }

    public double PriceAt(DateTime localTime) => IsOffPeak(localTime) ? OffPeakPricePerKwh : PricePerKwh;
}

/// <summary>Puissance tirée à la prise, détaillée par composant.</summary>
public sealed record PowerReading(double TotalWatts, double CpuWatts, bool CpuEstimated, double? GpuWatts, double OtherWatts);

public sealed record PowerUsageSummary(
    PowerReading Reading,
    TimeSpan Uptime,
    double SessionKwh, double SessionCost,
    double TodayKwh, double TodayCost,
    double TodayDayHours, double TodayNightHours,
    double MonthKwh, double MonthCost,
    double? ProjectedMonthCost,
    bool OffPeakNow,
    PowerLifetime? Lifetime);

/// <summary>
/// Depuis la première mise en service : mesuré par SysDeck + passé estimé (heures d'allumage du journal
/// Windows, extrapolées avant le début du journal, × puissance moyenne × prix moyen actuel).
/// </summary>
public sealed record PowerLifetime(
    DateTime Since,
    double Hours, double Kwh, double Cost,
    double DayHours, double NightHours,
    double MeasuredHours, double LoggedHours, double ExtrapolatedHours,
    double PastAverageWatts,
    DateTime? LogFrom);

/// <summary>
/// Intègre la puissance dans le temps (kWh) et la convertit en euros au prix en vigueur à chaque
/// mesure. Les cumuls (session depuis l'allumage, jours) sont conservés dans power-usage.json.
/// </summary>
public sealed class PowerUsageService
{
    // Écart entre deux mesures au-delà duquel le PC était en veille : cette durée n'est pas comptée.
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(15);
    // Au lancement, on compte le temps écoulé depuis l'allumage (ou depuis la dernière mesure si SysDeck
    // a été relancé) à la puissance de la première mesure, mais seulement s'il est court : au-delà,
    // le PC a pu être en veille entre-temps.
    private static readonly TimeSpan MaxBackfill = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan PastLogRetryDelay = TimeSpan.FromDays(7);
    private const int KeptDays = 400;

    private readonly PowerSettings _settings;
    private PowerHistory _history;
    private DateTime? _lastSampleUtc;
    private DateTime _lastSaveUtc = DateTime.UtcNow;
    private readonly DateTime? _windowsInstallDate = PowerOnHistory.WindowsInstallDate();

    public PowerUsageService(PowerSettings settings)
    {
        _settings = settings;
        _history = JsonFile.Load(AppPaths.PowerUsageFile, () => new PowerHistory());
        _history.Days ??= new();

        // Anciennes versions du fichier : le suivi a commencé avec le premier jour enregistré.
        if (_history.TrackedSinceUtc is null)
        {
            _history.MeasuredKwh = _history.Days.Values.Sum(d => d.Kwh);
            _history.MeasuredCost = _history.Days.Values.Sum(d => d.Cost);
            _history.MeasuredSeconds = _history.Days.Values.Sum(d => d.Seconds);
            _history.TrackedSinceUtc = DateTime.UtcNow - TimeSpan.FromSeconds(_history.MeasuredSeconds);
        }

        // Fichiers d'avant la répartition jour/soir : les quelques heures déjà mesurées vont dans la tranche actuelle.
        if (_history.MeasuredDaySeconds + _history.MeasuredNightSeconds == 0 && _history.MeasuredSeconds > 0)
        {
            var isDay = settings.IsDayTime(DateTime.Now);
            if (isDay) _history.MeasuredDaySeconds = _history.MeasuredSeconds; else _history.MeasuredNightSeconds = _history.MeasuredSeconds;
            foreach (var d in _history.Days.Values.Where(d => d.DaySeconds + d.NightSeconds == 0))
                if (isDay) d.DaySeconds = d.Seconds; else d.NightSeconds = d.Seconds;
        }

        _ = LoadPastLogAsync();
    }

    /// <summary>Date utilisée comme première mise en service (réglage, sinon installation de Windows).</summary>
    public DateTime? FirstPowerOn => _settings.FirstPowerOnDate ?? _windowsInstallDate;

    /// <summary>
    /// Lu une seule fois puis figé dans le fichier : le journal Windows est circulaire et aura oublié
    /// ces mois-là dans quelque temps, alors que SysDeck mesure lui-même tout ce qui suit.
    /// </summary>
    private async Task LoadPastLogAsync()
    {
        if (_history.PastLog?.Periods is not null) return;
        // Une lecture ratée (journal inaccessible) n'est pas retentée à chaque lancement : elle coûte cher.
        if (_history.PastLogFailedUtc is { } failed && DateTime.UtcNow - failed < PastLogRetryDelay) return;
        var until = _history.TrackedSinceUtc!.Value;
        var log = await Task.Run(() => PowerOnHistory.ReadLog(until));
        if (log is null)
        {
            _history.PastLogFailedUtc = DateTime.UtcNow;
            Save();
            return;
        }
        _history.PastLogFailedUtc = null;
        _history.PastLog = log;
        Save();
    }

    // GetTickCount64 inclut la veille : c'est bien l'heure du démarrage de Windows.
    private static DateTime BootTimeUtc => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    public PowerReading Estimate(HardwareSnapshot snapshot)
    {
        var cpuEstimated = snapshot.CpuPower is null;
        var cpu = snapshot.CpuPower
                  ?? _settings.CpuIdleWatts + (_settings.CpuMaxWatts - _settings.CpuIdleWatts) * Math.Clamp((snapshot.CpuLoad ?? 0) / 100.0, 0, 1);
        // Sans carte dédiée, rien à ajouter : l'iGPU fait partie du processeur.
        double? gpu = snapshot.GpuPower;

        var components = cpu + (gpu ?? 0) + _settings.OtherWatts;
        var atWall = components / Math.Clamp(_settings.PsuEfficiency, 0.5, 1) + _settings.MonitorWatts;
        return new PowerReading(atWall, cpu, cpuEstimated, gpu, _settings.OtherWatts);
    }

    public PowerUsageSummary AddSample(PowerReading reading)
    {
        var nowUtc = DateTime.UtcNow;
        var seconds = _lastSampleUtc is { } last
            ? (nowUtc - last <= MaxSampleGap ? (nowUtc - last).TotalSeconds : 0)
            : StartSession(nowUtc);
        _lastSampleUtc = nowUtc;

        if (seconds > 0)
        {
            var local = DateTime.Now;
            var kwh = reading.TotalWatts * seconds / 3_600_000;
            var cost = kwh * _settings.PriceAt(local);

            _history.SessionKwh += kwh;
            _history.SessionCost += cost;
            _history.MeasuredKwh += kwh;
            _history.MeasuredCost += cost;
            _history.MeasuredSeconds += seconds;

            var isDay = _settings.IsDayTime(local);
            if (isDay) _history.MeasuredDaySeconds += seconds; else _history.MeasuredNightSeconds += seconds;

            var day = GetDay(local);
            day.Kwh += kwh;
            day.Cost += cost;
            day.Seconds += seconds;
            if (isDay) day.DaySeconds += seconds; else day.NightSeconds += seconds;
        }
        _history.LastSampleUtc = nowUtc;

        if (nowUtc - _lastSaveUtc >= SaveInterval)
            Save();

        return Summarize(reading);
    }

    /// <summary>Reprend la session si SysDeck est relancé sans redémarrage du PC ; renvoie les secondes à rattraper.</summary>
    private double StartSession(DateTime nowUtc)
    {
        var boot = BootTimeUtc;
        DateTime from;
        if (_history.SessionBootUtc is { } savedBoot && Math.Abs((savedBoot - boot).TotalMinutes) < 2)
        {
            from = _history.LastSampleUtc ?? nowUtc;
        }
        else
        {
            _history.SessionBootUtc = boot;
            _history.SessionKwh = 0;
            _history.SessionCost = 0;
            from = boot;
        }

        var gap = nowUtc - from;
        return gap > TimeSpan.Zero && gap <= MaxBackfill ? gap.TotalSeconds : 0;
    }

    private PowerDay GetDay(DateTime local)
    {
        var key = DayKey(local);
        if (!_history.Days.TryGetValue(key, out var day))
        {
            _history.Days[key] = day = new PowerDay();
            Prune();
        }
        return day;
    }

    private static string DayKey(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private void Prune()
    {
        var oldest = DayKey(DateTime.Today.AddDays(-KeptDays));
        foreach (var key in _history.Days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList())
            _history.Days.Remove(key);
    }

    private PowerUsageSummary Summarize(PowerReading reading)
    {
        var now = DateTime.Now;
        var today = _history.Days.GetValueOrDefault(DayKey(now)) ?? new PowerDay();
        var monthPrefix = now.ToString("yyyy-MM-", CultureInfo.InvariantCulture);
        var month = _history.Days.Where(d => d.Key.StartsWith(monthPrefix, StringComparison.Ordinal)).Select(d => d.Value).ToList();

        return new PowerUsageSummary(
            reading,
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            _history.SessionKwh, _history.SessionCost,
            today.Kwh, today.Cost,
            today.DaySeconds / 3600, today.NightSeconds / 3600,
            month.Sum(d => d.Kwh), month.Sum(d => d.Cost),
            ProjectMonthCost(now, today),
            _settings.IsOffPeak(now),
            ComputeLifetime());
    }

    public PowerLifetime? ComputeLifetime()
    {
        if (FirstPowerOn is not { } firstLocal || _history.TrackedSinceUtc is not { } trackedSince) return null;
        var firstUtc = firstLocal.ToUniversalTime();

        // Heures d'allumage avant SysDeck : journal Windows sur la période qu'il couvre, et avant lui
        // la même proportion de temps allumé (ex. 6 h/jour) jusqu'à la mise en service.
        double loggedDay = 0, loggedNight = 0, extrapolatedSeconds = 0;
        var log = _history.PastLog;
        if (log?.Periods is { } periods && log.UntilUtc > log.FromUtc)
        {
            // Chaque période d'allumage du journal, coupée à la mise en service et répartie jour / soir.
            foreach (var p in periods)
            {
                var from = p.FromUtc < firstUtc ? firstUtc : p.FromUtc;
                if (p.UntilUtc <= from) continue;
                var (d, n) = PowerOnHistory.SplitDayNight(from.ToLocalTime(), p.UntilUtc.ToLocalTime(), _settings.DayStartHour, _settings.DayEndHour);
                loggedDay += d;
                loggedNight += n;
            }

            if (firstUtc < log.FromUtc)
            {
                var onRatio = Math.Clamp(log.OnSeconds / (log.UntilUtc - log.FromUtc).TotalSeconds, 0, 1);
                extrapolatedSeconds = (log.FromUtc - firstUtc).TotalSeconds * onRatio;
            }
        }
        var loggedSeconds = loggedDay + loggedNight;
        // Avant le journal, même répartition jour / soir que dans le journal.
        var dayShare = loggedSeconds > 0 ? loggedDay / loggedSeconds : 0.5;

        var measuredWatts = _history.MeasuredSeconds >= 60 ? _history.MeasuredKwh * 3_600_000 / _history.MeasuredSeconds : 150;
        var pastWatts = _settings.PastAverageWatts > 0 ? _settings.PastAverageWatts : measuredWatts;
        var pastKwh = (loggedSeconds + extrapolatedSeconds) * pastWatts / 3_600_000;
        var pastCost = pastKwh * _settings.AveragePrice();

        return new PowerLifetime(
            firstLocal,
            (loggedSeconds + extrapolatedSeconds + _history.MeasuredSeconds) / 3600,
            pastKwh + _history.MeasuredKwh,
            pastCost + _history.MeasuredCost,
            (loggedDay + extrapolatedSeconds * dayShare + _history.MeasuredDaySeconds) / 3600,
            (loggedNight + extrapolatedSeconds * (1 - dayShare) + _history.MeasuredNightSeconds) / 3600,
            _history.MeasuredSeconds / 3600, loggedSeconds / 3600, extrapolatedSeconds / 3600,
            pastWatts,
            log?.FromUtc.ToLocalTime());
    }

    /// <summary>
    /// Coût moyen des 7 derniers jours complets × 30. Sans jour complet (premier jour d'utilisation),
    /// le coût d'aujourd'hui est extrapolé à 24 h depuis minuit.
    /// </summary>
    private double? ProjectMonthCost(DateTime now, PowerDay today)
    {
        var from = DayKey(now.AddDays(-7));
        var todayKey = DayKey(now);
        var pastDays = _history.Days
            .Where(d => string.CompareOrdinal(d.Key, from) >= 0 && string.CompareOrdinal(d.Key, todayKey) < 0)
            .Select(d => d.Value.Cost)
            .ToList();
        if (pastDays.Count > 0)
            return pastDays.Average() * 30;

        var sinceMidnight = now.TimeOfDay.TotalHours;
        if (today.Seconds < 600 || sinceMidnight < 1) return null;
        return today.Cost / sinceMidnight * 24 * 30;
    }

    public void Reset()
    {
        _history.Days.Clear();
        _history.SessionKwh = 0;
        _history.SessionCost = 0;
        // Le cumul depuis la mise en service est conservé : c'est l'historique de la machine, pas un compteur.
        Save();
    }

    /// <summary>Écriture atomique ; en cas d'échec, nouvelle tentative à la prochaine sauvegarde.</summary>
    public void Save()
    {
        _lastSaveUtc = DateTime.UtcNow;
        JsonFile.Save(AppPaths.PowerUsageFile, _history);
    }

    private sealed class PowerHistory
    {
        public DateTime? SessionBootUtc { get; set; }
        public DateTime? LastSampleUtc { get; set; }
        public double SessionKwh { get; set; }
        public double SessionCost { get; set; }
        /// <summary>Début des mesures de SysDeck ; tout ce qui précède vient du journal Windows ou est estimé.</summary>
        public DateTime? TrackedSinceUtc { get; set; }
        /// <summary>Cumul de toutes les mesures, jamais élagué (contrairement à <see cref="Days"/>).</summary>
        public double MeasuredKwh { get; set; }
        public double MeasuredCost { get; set; }
        public double MeasuredSeconds { get; set; }
        public double MeasuredDaySeconds { get; set; }
        public double MeasuredNightSeconds { get; set; }
        public PowerOnLog? PastLog { get; set; }
        /// <summary>Dernière lecture du journal Windows qui n'a rien donné ; évite de la relancer à chaque démarrage.</summary>
        public DateTime? PastLogFailedUtc { get; set; }
        /// <summary>Par jour local, clé "yyyy-MM-dd".</summary>
        public Dictionary<string, PowerDay> Days { get; set; } = new();
    }

    private sealed class PowerDay
    {
        public double Kwh { get; set; }
        public double Cost { get; set; }
        public double Seconds { get; set; }
        public double DaySeconds { get; set; }
        public double NightSeconds { get; set; }
    }
}
