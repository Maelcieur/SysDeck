using HidSharp;
using SysDeck.Infrastructure;

namespace SysDeck.Services;

public sealed record MouseBatteryInfo(int Percent, bool Charging);

/// <summary>
/// Reads battery level from Logitech wireless mice (G502 X PLUS and other HID++ 2.0 devices
/// exposing the UNIFIED_BATTERY feature, 0x1004) through the receiver's vendor HID channel.
/// Reverse-engineered from Logitech's undocumented HID++ 2.0 protocol (same approach as Solaar).
/// </summary>
public sealed class LogitechBatteryService
{
    private const int LogitechVendorId = 0x046D;

    private byte? _deviceIndex;
    private byte? _batteryFeatureIndex;
    private string? _receiverPath;

    public MouseBatteryInfo? TryReadBattery()
    {
        try
        {
            var receiver = FindReceiver();
            if (receiver is null)
                return null;

            using var stream = receiver.Open();
            stream.ReadTimeout = 700;
            stream.WriteTimeout = 500;

            if (_deviceIndex is null || _batteryFeatureIndex is null || _receiverPath != receiver.DevicePath)
            {
                _receiverPath = receiver.DevicePath;
                if (!TryResolveDevice(stream, out var idx, out var featureIndex))
                {
                    _deviceIndex = null;
                    _batteryFeatureIndex = null;
                    return null;
                }
                _deviceIndex = idx;
                _batteryFeatureIndex = featureIndex;
            }

            return QueryStatus(stream, _deviceIndex.Value, _batteryFeatureIndex.Value);
        }
        catch (Exception ex)
        {
            // Souris éteinte, récepteur débranché... : fréquent et répétitif, donc journalisé une seule fois.
            Log.WarnOnce("logitech:read", "Lecture de la batterie Logitech impossible", ex);
            _deviceIndex = null;
            _batteryFeatureIndex = null;
            return null;
        }
    }

    private static HidDevice? FindReceiver()
    {
        foreach (var device in DeviceList.Local.GetHidDevices(LogitechVendorId))
        {
            try
            {
                var descriptor = device.GetReportDescriptor();
                bool isHidPpLongChannel = descriptor.DeviceItems
                    .Any(item => item.Usages.GetAllValues().Contains(0xFF000002));

                if (isHidPpLongChannel)
                    return device;
            }
            catch (Exception ex)
            {
                // some HID collections don't expose a parseable report descriptor; skip them
                Log.WarnOnce("logitech:descriptor:" + device.DevicePath, "Descripteur HID illisible, collection ignorée", ex);
            }
        }
        return null;
    }

    private static bool TryResolveDevice(HidStream stream, out byte deviceIndex, out byte featureIndex)
    {
        for (byte idx = 1; idx <= 6; idx++)
        {
            var request = BuildReport(idx, featureIndex: 0x00, function: 0x00, swId: 0x0A, p0: 0x10, p1: 0x04);
            stream.Write(request);

            var response = ReadMatching(stream, idx, expectedFeatureIndex: 0x00, function: 0x00, swId: 0x0A, timeoutMs: 700);
            if (response is not null && response[4] != 0)
            {
                deviceIndex = idx;
                featureIndex = response[4];
                return true;
            }
        }

        deviceIndex = 0;
        featureIndex = 0;
        return false;
    }

    private static MouseBatteryInfo? QueryStatus(HidStream stream, byte deviceIndex, byte featureIndex)
    {
        var request = BuildReport(deviceIndex, featureIndex, function: 0x01, swId: 0x0B);
        stream.Write(request);

        var response = ReadMatching(stream, deviceIndex, featureIndex, function: 0x01, swId: 0x0B, timeoutMs: 1000);
        if (response is null)
            return null;

        int percent = response[4];
        byte chargingStatus = response[6];
        bool charging = chargingStatus is 1 or 2 or 3;

        return new MouseBatteryInfo(percent, charging);
    }

    private static byte[] BuildReport(byte deviceIndex, byte featureIndex, byte function, byte swId, byte p0 = 0, byte p1 = 0, byte p2 = 0)
    {
        var report = new byte[20];
        report[0] = 0x11; // long HID++ report
        report[1] = deviceIndex;
        report[2] = featureIndex;
        report[3] = (byte)((function & 0x0F) << 4 | (swId & 0x0F));
        report[4] = p0;
        report[5] = p1;
        report[6] = p2;
        return report;
    }

    private static byte[]? ReadMatching(HidStream stream, byte deviceIndex, byte expectedFeatureIndex, byte function, byte swId, int timeoutMs)
    {
        byte expectedByte3 = (byte)((function & 0x0F) << 4 | (swId & 0x0F));
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            byte[] buffer = new byte[20];
            int read;
            try
            {
                read = stream.Read(buffer);
            }
            catch (TimeoutException)
            {
                continue;
            }

            if (read < 7 || buffer[1] != deviceIndex)
                continue; // notification or reply for a different device index

            // Erreur HID++ 1.0 : 0x8F puis l'index demandé. Erreur HID++ 2.0 : 0xFF, index demandé, fonction/swId, code.
            bool isError10 = buffer[2] == 0x8F && buffer[3] == expectedFeatureIndex;
            bool isError20 = buffer[2] == 0xFF && buffer[3] == expectedFeatureIndex && buffer[4] == expectedByte3;
            if (isError10 || isError20)
            {
                Log.WarnOnce($"logitech:hiderror:{expectedFeatureIndex}:{buffer[2]:X2}",
                    $"Erreur HID++ {(isError20 ? "2.0" : "1.0")} (fonction {function}, code {buffer[5]})");
                return null;
            }

            if (buffer[2] == expectedFeatureIndex && buffer[3] == expectedByte3)
                return buffer;

            // anything else: a spontaneous notification unrelated to our request, keep reading
        }

        return null;
    }
}
