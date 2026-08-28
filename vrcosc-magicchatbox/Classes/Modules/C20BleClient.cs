using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace vrcosc_magicchatbox.Classes.Modules;

public sealed class C20BleClient : IDisposable
{
    public event Action<int>? HeartRateReceived;
    public event Action<bool>? ConnectionChanged;
    public event Action<int>? BatteryRead;

    public bool IsConnected { get; private set; }

    private BluetoothLEDevice? _device;
    private GattSession? _session;
    private GattCharacteristic? _hrCharacteristic;
    private GattCharacteristic? _batteryCharacteristic;
    private GattCharacteristic? _fee2Characteristic;
    private System.Timers.Timer? _batteryTimer;
    private CancellationTokenSource? _exerciseCts;
    private bool _hasHrReading;
    private bool _disposed;

    private static readonly Guid FeeAServiceGuid = new Guid("0000feea-0000-1000-8000-00805f9b34fb");
    private static readonly Guid Fee2CharGuid = new Guid("0000fee2-0000-1000-8000-00805f9b34fb");

    public async Task<bool> StartAsync(string address)
    {
        Stop();

        var watchAddress = ParseAddress(address);
        if (watchAddress == 0)
        {
            Logging.WriteInfo($"C20 BLE: Watch MAC '{address}' is not a valid address (expected 6 colon-separated hex pairs).");
            return false;
        }

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(watchAddress);
        if (device == null)
        {
            Logging.WriteInfo($"C20 BLE: No watch in the BLE cache at {address} — actively scanning for its advertisements for a few seconds...");
            device = await ScanForWatchAsync(watchAddress);
        }

        if (device == null)
        {
            Logging.WriteInfo("C20 BLE: Still could not find the watch even with a scan. Close the bridge app if it's running, wake the watch (keep the screen on), then retry.");
            return false;
        }

        Logging.WriteInfo($"C20 BLE: Watch found ({device.Name}). Connecting...");
        _device = device;

        var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        if (session != null)
        {
            session.MaintainConnection = true;
            session.SessionStatusChanged += OnSessionStatusChanged;
            _session = session;
        }

        var services = await device.GetGattServicesAsync();
        if (services.Status != GattCommunicationStatus.Success)
        {
            Logging.WriteInfo($"C20 BLE: Could not read the watch services (status {services.Status}). Bluetooth access may be blocked — check Windows Settings → Privacy → Bluetooth.");
            return false;
        }

        foreach (var service in services.Services)
        {
            if (service.Uuid == GattServiceUuids.HeartRate)
            {
                var hrChars = await service.GetCharacteristicsAsync();
                if (hrChars.Status != GattCommunicationStatus.Success)
                    continue;

                foreach (var c in hrChars.Characteristics)
                {
                    if (c.Uuid != GattCharacteristicUuids.HeartRateMeasurement)
                        continue;

                    _hrCharacteristic = c;
                    c.ValueChanged += OnHrValueChanged;
                    var status = await c.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);
                    if (status != GattCommunicationStatus.Success)
                    {
                        Logging.WriteInfo($"C20 BLE: Could not subscribe to heart rate notifications (status {status}).");
                        return false;
                    }

                    Logging.WriteInfo("C20 BLE: Subscribed to heart rate notifications (0x2A37).");
                }
            }
            else if (service.Uuid == GattServiceUuids.Battery)
            {
                var batteryChars = await service.GetCharacteristicsAsync();
                if (batteryChars.Status != GattCommunicationStatus.Success)
                    continue;

                foreach (var c in batteryChars.Characteristics)
                {
                    if (c.Uuid == GattCharacteristicUuids.BatteryLevel)
                        _batteryCharacteristic = c;
                }
            }
            else if (service.Uuid == FeeAServiceGuid)
            {
                var feeaChars = await service.GetCharacteristicsAsync();
                if (feeaChars.Status != GattCommunicationStatus.Success)
                    continue;

                foreach (var c in feeaChars.Characteristics)
                {
                    if (c.Uuid == Fee2CharGuid)
                        _fee2Characteristic = c;
                }
            }
        }

        if (_hrCharacteristic == null)
        {
            Logging.WriteInfo("C20 BLE: The watch does not expose the heart rate service (0x180D / 0x2A37).");
            return false;
        }

        _hasHrReading = false;
        _ = StartExerciseModeLoopAsync();
        IsConnected = true;
        ConnectionChanged?.Invoke(true);

        ReadBattery();
        _batteryTimer = new System.Timers.Timer
        {
            AutoReset = true,
            Interval = 30000
        };
        _batteryTimer.Elapsed += (_, _) => ReadBattery();
        _batteryTimer.Start();

        return true;
    }

    public void Stop()
    {
        if (_batteryTimer != null)
        {
            _batteryTimer.Stop();
            _batteryTimer.Dispose();
            _batteryTimer = null;
        }

        if (_exerciseCts != null)
        {
            _exerciseCts.Cancel();
            _exerciseCts.Dispose();
            _exerciseCts = null;
        }

        if (_hrCharacteristic != null)
        {
            try { _hrCharacteristic.ValueChanged -= OnHrValueChanged; }
            catch { }
            _hrCharacteristic = null;
        }

        _batteryCharacteristic = null;

        if (_session != null)
        {
            try { _session.SessionStatusChanged -= OnSessionStatusChanged; }
            catch { }
            _session.Dispose();
            _session = null;
        }

        if (_device != null)
        {
            try { _device.Dispose(); }
            catch { }
            _device = null;
        }

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false);
        }
    }

    private async Task StartExerciseModeLoopAsync()
    {
        if (_fee2Characteristic == null)
        {
            Logging.WriteInfo("C20 BLE: Watch has no 0xFEE2 write characteristic — exercise mode must be started manually on the watch.");
            return;
        }

        _exerciseCts?.Cancel();
        _exerciseCts = new CancellationTokenSource();
        var ct = _exerciseCts.Token;

        int attempts = 0;
        while (!_hasHrReading && !ct.IsCancellationRequested)
        {
            await WritePacketAsync(0x68, new byte[] { 0x00 });
            await WritePacketAsync(0x1F, new byte[] { 0x06 });

            if (attempts == 0)
                Logging.WriteInfo("C20 BLE: Sent exercise-mode start (0x68 0x00, 0x1F 0x06) to the watch.");
            else if (attempts % 3 == 0)
                Logging.WriteInfo("C20 BLE: Still waiting for HR — keeping the watch awake so it doesn't sleep-disconnect.");

            attempts++;

            try
            {
                await Task.Delay(10000, ct);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private async Task WritePacketAsync(byte cmd, byte[] payload)
    {
        if (_fee2Characteristic == null) return;

        try
        {
            var writer = new DataWriter();
            writer.WriteBytes(new byte[] { 0xFE, 0xEA, 0x10, (byte)(payload.Length + 2), cmd });
            if (payload.Length > 0)
                writer.WriteBytes(payload);
            await _fee2Characteristic.WriteValueAsync(writer.DetachBuffer(), GattWriteOption.WriteWithoutResponse);
        }
        catch
        {
        }
    }

    private void OnSessionStatusChanged(GattSession sender, GattSessionStatusChangedEventArgs args)
    {
        bool connected = args.Status == GattSessionStatus.Active;
        if (connected != IsConnected)
        {
            IsConnected = connected;
            ConnectionChanged?.Invoke(connected);
        }
    }

    private void OnHrValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        var bytes = ReadBytes(args.CharacteristicValue);
        if (bytes.Length < 2)
            return;

        int hr;
        if ((bytes[0] & 0x01) == 0)
        {
            hr = bytes[1];
        }
        else
        {
            if (bytes.Length < 3)
                return;
            hr = bytes[1] | (bytes[2] << 8);
        }

        HeartRateReceived?.Invoke(hr);
        _hasHrReading = true;
    }

    private static async Task<BluetoothLEDevice?> ScanForWatchAsync(ulong watchAddress)
    {
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        var completion = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new HashSet<string>();
        int totalSeen = 0;

        watcher.Received += (_, eventArgs) =>
        {
            var name = eventArgs.Advertisement.LocalName ?? string.Empty;
            var mac = $"{eventArgs.BluetoothAddress:X12}";

            lock (seen)
            {
                totalSeen++;
                if (seen.Count < 6)
                    seen.Add(name.Length > 0 ? $"{name} ({mac})" : mac);
            }

            bool isMatch = eventArgs.BluetoothAddress == watchAddress ||
                           name.Contains("C20", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("C 20", StringComparison.OrdinalIgnoreCase);
            if (isMatch && !completion.Task.IsCompleted)
                completion.TrySetResult(eventArgs.BluetoothAddress);
        };

        Logging.WriteInfo($"C20 BLE: Scanning for watch (0x{watchAddress:X12})...");
        watcher.Start();
        try
        {
            var finished = await Task.WhenAny(completion.Task, Task.Delay(12000));
            if (finished == completion.Task)
            {
                var address = await completion.Task;
                Logging.WriteInfo("C20 BLE: Watch advertisement matched!");
                return await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            }

            string samples;
            lock (seen)
            {
                samples = seen.Count == 0 ? "none" : string.Join(", ", seen);
            }
            Logging.WriteInfo($"C20 BLE: Scan saw {totalSeen} nearby device(s), no C 20. Samples: {samples}");
            return null;
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20 BLE: Scan failed: {ex.Message}");
            return null;
        }
        finally
        {
            watcher.Stop();
        }
    }

    private async void ReadBattery()
    {
        var characteristic = _batteryCharacteristic;
        if (characteristic == null)
            return;

        try
        {
            var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                var bytes = ReadBytes(result.Value);
                if (bytes.Length > 0)
                    BatteryRead?.Invoke(bytes[0]);
            }
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Battery read failed: {ex.Message}");
        }
    }

    private static byte[] ReadBytes(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static ulong ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return 0;

        var parts = address.Split(':', '-');
        if (parts.Length != 6)
            return 0;

        try
        {
            var hex = string.Concat(parts);
            if (ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var result) && result > 0)
                return result;
        }
        catch
        {
        }

        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}