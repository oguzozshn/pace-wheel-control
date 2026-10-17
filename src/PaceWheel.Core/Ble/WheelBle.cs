using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PaceWheel.Core.Ble;

/// <summary>One device seen while scanning, merged over all its advertisements.</summary>
public sealed class BleSighting
{
    public required ulong Address { get; init; }
    public string? Name { get; set; }
    public short BestRssi { get; set; } = short.MinValue;
    public int Count { get; set; }
    public HashSet<Guid> ServiceUuids { get; } = new();
    public bool Connectable { get; set; }

    public string AddressText => FormatAddress(Address);

    public static string FormatAddress(ulong address) =>
        string.Join(":", BitConverter.GetBytes(address).Take(6).Reverse().Select(b => b.ToString("X2")));
}

public sealed record GattCharacteristicInfo(Guid Uuid, GattCharacteristicProperties Properties, IReadOnlyList<Guid> Descriptors);

public sealed record GattServiceInfo(Guid Uuid, IReadOnlyList<GattCharacteristicInfo> Characteristics);

/// <summary>
/// Read-only Bluetooth LE access to the wheel's configuration interface (service 0xFFE0, see
/// docs/protocol-research.md). Nothing here writes characteristics, descriptors or values.
/// </summary>
public static class WheelBle
{
    public const string WheelName = "FFB WHEEL PACE V987";

    public static readonly Guid ConfigService = Guid.Parse("0000ffe0-0000-1000-8000-00805f9b34fb");

    /// <summary>Listens to advertisements for <paramref name="duration"/>. Passive mode sends no scan requests.</summary>
    public static async Task<IReadOnlyList<BleSighting>> ScanAsync(TimeSpan duration, bool active, CancellationToken cancel)
    {
        var sightings = new Dictionary<ulong, BleSighting>();
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive,
        };
        watcher.Received += (_, e) =>
        {
            lock (sightings)
            {
                if (!sightings.TryGetValue(e.BluetoothAddress, out var s))
                {
                    s = new BleSighting { Address = e.BluetoothAddress };
                    sightings[e.BluetoothAddress] = s;
                }
                s.Count++;
                s.BestRssi = Math.Max(s.BestRssi, e.RawSignalStrengthInDBm);
                s.Connectable |= e.IsConnectable;
                if (!string.IsNullOrEmpty(e.Advertisement.LocalName))
                {
                    s.Name = e.Advertisement.LocalName;
                }
                foreach (var uuid in e.Advertisement.ServiceUuids)
                {
                    s.ServiceUuids.Add(uuid);
                }
            }
        };

        watcher.Start();
        try
        {
            await Task.Delay(duration, cancel).ContinueWith(_ => { });
        }
        finally
        {
            watcher.Stop();
        }

        lock (sightings)
        {
            return sightings.Values.OrderByDescending(s => s.BestRssi).ToList();
        }
    }

    /// <summary>
    /// Connects as a GATT client and lists services, characteristics (with their properties) and
    /// descriptors. Only attribute discovery happens: no reads, no writes, no notification subscription.
    /// </summary>
    public static async Task<IReadOnlyList<GattServiceInfo>> DiscoverAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException($"Device {BleSighting.FormatAddress(address)} not reachable.");

        var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"Service discovery failed: {servicesResult.Status}");
        }

        var services = new List<GattServiceInfo>();
        foreach (var service in servicesResult.Services)
        {
            using (service)
            {
                var characteristics = new List<GattCharacteristicInfo>();
                var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (chars.Status == GattCommunicationStatus.Success)
                {
                    foreach (var c in chars.Characteristics)
                    {
                        var descriptors = await c.GetDescriptorsAsync(BluetoothCacheMode.Uncached);
                        characteristics.Add(new GattCharacteristicInfo(
                            c.Uuid,
                            c.CharacteristicProperties,
                            descriptors.Status == GattCommunicationStatus.Success
                                ? descriptors.Descriptors.Select(d => d.Uuid).ToList()
                                : []));
                    }
                }
                services.Add(new GattServiceInfo(service.Uuid, characteristics));
            }
        }
        return services;
    }

    /// <summary>Short name for 16-bit Bluetooth base UUIDs (0000xxxx-0000-1000-8000-00805f9b34fb).</summary>
    public static string ShortUuid(Guid uuid)
    {
        string s = uuid.ToString();
        return s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) && s.StartsWith("0000")
            ? "0x" + s.Substring(4, 4).ToUpperInvariant()
            : s;
    }
}
