using SysDeck.Services;
using Xunit;

namespace SysDeck.Tests;

public class AlertTests
{
    private static (AlertMonitor Monitor, AlertSettings Settings) Create()
    {
        var settings = new AlertSettings
        {
            CpuEnabled = true, CpuTemperature = 85,
            GpuEnabled = true, GpuTemperature = 80,
            MouseEnabled = true, MousePercent = 20,
            ClaudeSessionEnabled = true, ClaudeSessionPercent = 80,
            ClaudeWeekEnabled = true, ClaudeWeekPercent = 90
        };
        return (new AlertMonitor(settings), settings);
    }

    [Fact]
    public void Temperature_NeedsSeveralReadings_ThenFiresOnce()
    {
        var (monitor, _) = Create();
        for (var i = 1; i < AlertMonitor.TemperatureReadings; i++)
            Assert.Empty(monitor.CheckTemperatures(90, null));

        var alert = Assert.Single(monitor.CheckTemperatures(90, null));
        Assert.Equal(AlertKind.CpuTemperature, alert.Kind);
        Assert.Equal("90 °C", alert.Value);

        for (var i = 0; i < 20; i++) Assert.Empty(monitor.CheckTemperatures(92, null));
    }

    [Fact]
    public void Temperature_BriefSpike_Ignored()
    {
        var (monitor, _) = Create();
        Assert.Empty(monitor.CheckTemperatures(95, null));
        Assert.Empty(monitor.CheckTemperatures(60, null));
        Assert.Empty(monitor.CheckTemperatures(95, null));
    }

    [Fact]
    public void Temperature_RearmsOnlyWellBelowThreshold()
    {
        var (monitor, _) = Create();
        void Hot() { for (var i = 0; i < AlertMonitor.TemperatureReadings; i++) monitor.CheckTemperatures(null, 85); }

        Hot();
        monitor.CheckTemperatures(null, 78); // 2 °C sous le seuil : pas assez pour réarmer
        Assert.Empty(Enumerable.Range(0, AlertMonitor.TemperatureReadings).SelectMany(_ => monitor.CheckTemperatures(null, 85)));

        monitor.CheckTemperatures(null, 70); // nettement redescendue
        Assert.Single(Enumerable.Range(0, AlertMonitor.TemperatureReadings).SelectMany(_ => monitor.CheckTemperatures(null, 85)));
    }

    [Fact]
    public void Disabled_NeverFires()
    {
        var (monitor, settings) = Create();
        settings.CpuEnabled = false;
        settings.MouseEnabled = false;
        for (var i = 0; i < 10; i++) Assert.Empty(monitor.CheckTemperatures(100, null));
        Assert.Null(monitor.CheckMouse(1, false));
    }

    [Fact]
    public void Mouse_FiresAtThreshold_NotWhileCharging_RearmsAfterCharge()
    {
        var (monitor, _) = Create();
        Assert.Null(monitor.CheckMouse(25, false));
        Assert.Null(monitor.CheckMouse(15, true));
        Assert.NotNull(monitor.CheckMouse(20, false));
        Assert.Null(monitor.CheckMouse(18, false));

        monitor.CheckMouse(18, true); // branchée : réarmée
        Assert.NotNull(monitor.CheckMouse(17, false));
    }

    [Fact]
    public void Mouse_VeryLow_IsCritical()
        => Assert.True(Create().Monitor.CheckMouse(5, false)!.Critical);

    [Fact]
    public void Claude_SessionAndWeek_Independent()
    {
        var (monitor, _) = Create();
        Assert.Empty(monitor.CheckClaude(50, 50));
        var a = Assert.Single(monitor.CheckClaude(81, 50));
        Assert.Equal(AlertKind.ClaudeSession, a.Kind);
        var b = Assert.Single(monitor.CheckClaude(85, 92));
        Assert.Equal(AlertKind.ClaudeWeek, b.Kind);

        // Nouvelle session (quota remis à zéro) : réarmée, prévient à nouveau au prochain dépassement.
        Assert.Empty(monitor.CheckClaude(3, 92));
        Assert.Single(monitor.CheckClaude(80, 92));
    }

    [Fact]
    public void Claude_UnknownValues_KeepState()
    {
        var (monitor, _) = Create();
        Assert.Single(monitor.CheckClaude(90, null));
        Assert.Empty(monitor.CheckClaude(null, null));
        Assert.Empty(monitor.CheckClaude(90, null));
    }

    [Fact]
    public void Sample_ExistsForEveryKind()
    {
        var settings = new AlertSettings();
        foreach (var kind in Enum.GetValues<AlertKind>())
            Assert.False(string.IsNullOrWhiteSpace(AlertMonitor.Sample(kind, settings).Title));
    }
}
