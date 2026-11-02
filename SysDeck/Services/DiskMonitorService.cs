using System.IO;

namespace SysDeck.Services;

public sealed record DriveUsage(string Name, float UsedGb, float TotalGb, float LoadPercent);

public sealed class DiskMonitorService
{
    public IReadOnlyList<DriveUsage> Read()
    {
        var drives = new List<DriveUsage>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                continue;

            var totalGb = drive.TotalSize / 1_073_741_824f;
            var freeGb = drive.TotalFreeSpace / 1_073_741_824f;
            var usedGb = totalGb - freeGb;
            var load = totalGb > 0 ? usedGb / totalGb * 100f : 0f;

            drives.Add(new DriveUsage(drive.Name.TrimEnd('\\'), usedGb, totalGb, load));
        }

        return drives;
    }
}
