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

    /// <summary>
    /// Reads, without writing anything: every readable characteristic of Device Information
    /// (0x180A) and GAP (0x1800), plus the 0x2901 user description descriptors of the
    /// configuration service. The configuration characteristics' own values are not read.
    /// </summary>
    public static async Task<IReadOnlyList<(string What, byte[]? Value, string? Error)>> ReadInfoAsync(ulong address)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException($"Device {BleSighting.FormatAddress(address)} not reachable.");

        var results = new List<(string, byte[]?, string?)>();
        var servicesResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (servicesResult.Status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"Service discovery failed: {servicesResult.Status}");
        }

        foreach (var service in servicesResult.Services)
        {
            using (service)
            {
                bool infoService = service.Uuid == DeviceInformationService || service.Uuid == GenericAccessService;
                if (!infoService && service.Uuid != ConfigService)
                {
                    continue;
                }
                var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                if (chars.Status != GattCommunicationStatus.Success)
                {
                    results.Add(($"{ShortUuid(service.Uuid)}", null, chars.Status.ToString()));
                    continue;
                }
                foreach (var c in chars.Characteristics)
                {
                    if (infoService && c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
                    {
                        var read = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
                        results.Add(($"{ShortUuid(service.Uuid)}/{ShortUuid(c.Uuid)} {CharacteristicName(c.Uuid)}",
                            read.Status == GattCommunicationStatus.Success ? ToBytes(read.Value) : null,
                            read.Status == GattCommunicationStatus.Success ? null : read.Status.ToString()));
                    }
                    else if (service.Uuid == ConfigService)
                    {
                        var descriptors = await c.GetDescriptorsForUuidAsync(UserDescriptionDescriptor, BluetoothCacheMode.Uncached);
                        foreach (var d in descriptors.Descriptors)
                        {
                            var read = await d.ReadValueAsync(BluetoothCacheMode.Uncached);
                            results.Add(($"{ShortUuid(service.Uuid)}/{ShortUuid(c.Uuid)} user description (0x2901)",
                                read.Status == GattCommunicationStatus.Success ? ToBytes(read.Value) : null,
                                read.Status == GattCommunicationStatus.Success ? null : read.Status.ToString()));
                        }
                    }
                }
            }
        }
        return results;
    }

    /// <summary>The app writes its commands to 0xFFE1 (smali of the app's onServicesDiscovered).</summary>
    public static readonly Guid CommandCharacteristic = Guid.Parse("0000ffe1-0000-1000-8000-00805f9b34fb");

    /// <summary>The app enables notifications on 0xFFE2; responses arrive there.</summary>
    public static readonly Guid ResponseCharacteristic = Guid.Parse("0000ffe2-0000-1000-8000-00805f9b34fb");

    public static Task<IReadOnlyList<(string Source, byte[] Data)>> ExchangeAsync(ulong address, byte[] request, TimeSpan window, bool listenOnCommandCharacteristic = false) =>
        ExchangeAsync(address, [request], window, listenOnCommandCharacteristic);

    /// <summary>
    /// Writes the requests to 0xFFE1 one at a time over a single connection and collects the
    /// notifications on 0xFFE2. After each request it waits until a new notification arrives or
    /// <paramref name="window"/> passes, then 100 ms, like the app. Besides the requests it writes
    /// only 0xFFE2's CCCD: 01 00 at the start and 00 00 at the end, also on failure.
    /// Callers must only pass requests the user approved byte for byte.
    /// </summary>
    public static async Task<IReadOnlyList<(string Source, byte[] Data)>> ExchangeAsync(ulong address, IReadOnlyList<byte[]> requests, TimeSpan window, bool listenOnCommandCharacteristic = false)
    {
        using var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
            ?? throw new InvalidOperationException($"Device {BleSighting.FormatAddress(address)} not reachable.");

        var servicesResult = await device.GetGattServicesForUuidAsync(ConfigService, BluetoothCacheMode.Uncached);
        var service = servicesResult.Services.FirstOrDefault()
            ?? throw new InvalidOperationException($"Service 0xFFE0 not found ({servicesResult.Status}).");
        using (service)
        {
            var commandResult = await service.GetCharacteristicsForUuidAsync(CommandCharacteristic, BluetoothCacheMode.Uncached);
            var command = commandResult.Characteristics.FirstOrDefault()
                ?? throw new InvalidOperationException($"Characteristic 0xFFE1 not found ({commandResult.Status}).");
            var responseResult = await service.GetCharacteristicsForUuidAsync(ResponseCharacteristic, BluetoothCacheMode.Uncached);
            var characteristic = responseResult.Characteristics.FirstOrDefault()
                ?? throw new InvalidOperationException($"Characteristic 0xFFE2 not found ({responseResult.Status}).");

            var received = new List<(string, byte[])>();
            void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs e)
            {
                lock (received)
                {
                    received.Add((ShortUuid(sender.Uuid), ToBytes(e.CharacteristicValue)));
                }
            }

            characteristic.ValueChanged += OnValueChanged;
            // 0xFFE1 has Notify but no CCCD, so nothing can be written to subscribe; a handler is
            // only attached in case the stack delivers its notifications anyway.
            if (listenOnCommandCharacteristic)
            {
                command.ValueChanged += OnValueChanged;
            }
            try
            {
                var subscribe = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (subscribe != GattCommunicationStatus.Success)
                {
                    throw new InvalidOperationException($"Enabling notifications failed: {subscribe}");
                }

                foreach (var request in requests)
                {
                    int before;
                    lock (received)
                    {
                        before = received.Count;
                    }

                    var write = await command.WriteValueAsync(
                        Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(request),
                        GattWriteOption.WriteWithResponse);
                    if (write != GattCommunicationStatus.Success)
                    {
                        throw new InvalidOperationException($"Writing request {Convert.ToHexString(request)} failed: {write}");
                    }

                    var deadline = DateTime.UtcNow + window;
                    while (DateTime.UtcNow < deadline)
                    {
                        lock (received)
                        {
                            if (received.Count > before)
                            {
                                break;
                            }
                        }
                        await Task.Delay(20);
                    }
                    await Task.Delay(100);
                }
            }
            finally
            {
                characteristic.ValueChanged -= OnValueChanged;
                command.ValueChanged -= OnValueChanged;
                try
                {
                    await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None);
                }
                catch
                {
                    // Disconnecting also ends the subscription.
                }
            }

            lock (received)
            {
                return received.ToList();
            }
        }
    }

    private static readonly Guid DeviceInformationService = Guid.Parse("0000180a-0000-1000-8000-00805f9b34fb");
    private static readonly Guid GenericAccessService = Guid.Parse("00001800-0000-1000-8000-00805f9b34fb");
    private static readonly Guid UserDescriptionDescriptor = Guid.Parse("00002901-0000-1000-8000-00805f9b34fb");

    private static byte[] ToBytes(Windows.Storage.Streams.IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string CharacteristicName(Guid uuid) => ShortUuid(uuid) switch
    {
        "0x2A00" => "Device Name",
        "0x2A01" => "Appearance",
        "0x2A04" => "Peripheral Preferred Connection Parameters",
        "0x2AA6" => "Central Address Resolution",
        "0x2A23" => "System ID",
        "0x2A24" => "Model Number",
        "0x2A25" => "Serial Number",
        "0x2A26" => "Firmware Revision",
        "0x2A27" => "Hardware Revision",
        "0x2A28" => "Software Revision",
        "0x2A29" => "Manufacturer Name",
        "0x2A2A" => "IEEE 11073-20601 Regulatory Certification",
        "0x2A50" => "PnP ID",
        _ => "",
    };

    /// <summary>Short name for 16-bit Bluetooth base UUIDs (0000xxxx-0000-1000-8000-00805f9b34fb).</summary>
    public static string ShortUuid(Guid uuid)
    {
        string s = uuid.ToString();
        return s.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) && s.StartsWith("0000")
            ? "0x" + s.Substring(4, 4).ToUpperInvariant()
            : s;
    }
}
