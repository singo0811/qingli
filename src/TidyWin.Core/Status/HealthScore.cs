namespace TidyWin.Core.Status;

public sealed record HealthInputs(
    double CpuPercent,
    double MemoryPercent,
    double SystemDiskUsedPercent,
    int? BatteryHealthPercent,
    double UptimeDays,
    bool RebootPending,
    int StartupEnabled = 0,
    bool OsUnsupported = false,
    int? UpdateStaleDays = null,
    bool DiskUnhealthy = false,
    int ProblemDevices = 0,
    bool NotActivated = false);

public enum HealthBand { Excellent, Good, Fair, Poor }

public sealed record Deduction(string Code, int Points);

public sealed record HealthReport(int Score, HealthBand Band, IReadOnlyList<Deduction> Deductions);

/// <summary>Every point lost has a code, so the UI can always say why.</summary>
public static class HealthScore
{
    public static HealthReport Assess(HealthInputs i)
    {
        var d = new List<Deduction>();

        Add(d, "memory.critical", i.MemoryPercent >= 92, 20);
        Add(d, "memory.high", i.MemoryPercent is >= 82 and < 92, 10);

        Add(d, "disk.critical", i.SystemDiskUsedPercent >= 95, 25);
        Add(d, "disk.high", i.SystemDiskUsedPercent is >= 90 and < 95, 15);
        Add(d, "disk.watch", i.SystemDiskUsedPercent is >= 80 and < 90, 5);

        Add(d, "cpu.sustained", i.CpuPercent >= 90, 10);

        Add(d, "battery.degraded", i.BatteryHealthPercent is < 60, 10);
        Add(d, "battery.aging", i.BatteryHealthPercent is >= 60 and < 80, 5);

        Add(d, "uptime.long", i.UptimeDays >= 30, 5);
        Add(d, "reboot.pending", i.RebootPending, 5);

        Add(d, "disk.unhealthy", i.DiskUnhealthy, 25);
        Add(d, "os.unsupported", i.OsUnsupported, 10);
        Add(d, "update.stale", i.UpdateStaleDays is >= 60, 5);
        Add(d, "startup.many", i.StartupEnabled >= 25, 3);
        Add(d, "devices.problem", i.ProblemDevices > 0, 5);
        Add(d, "license.missing", i.NotActivated, 3);

        var score = Math.Max(0, 100 - d.Sum(x => x.Points));
        return new HealthReport(score, BandFor(score), d);
    }

    public static HealthBand BandFor(int score) => score switch
    {
        >= 85 => HealthBand.Excellent,
        >= 65 => HealthBand.Good,
        >= 45 => HealthBand.Fair,
        _ => HealthBand.Poor,
    };

    private static void Add(List<Deduction> list, string code, bool condition, int points)
    {
        if (condition) list.Add(new Deduction(code, points));
    }
}
