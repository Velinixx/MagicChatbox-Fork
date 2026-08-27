using System;
using System.Threading.Tasks;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using Windows.Devices.Bluetooth;
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
    private System.Timers.Timer? _batteryTimer;
    private bool _disposed;

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
            Logging.WriteInfo($"C20 BLE: No watch found at {address}. Wake the watch (tap the screen) or pair it in Windows Settings → Bluetooth & devices, then retry.");
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
        }

        if (_hrCharacteristic == null)
        {
            Logging.WriteInfo("C20 BLE: The watch does not expose the heart rate service (0x180D / 0x2A37).");
            return false;
        }

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
            ulong result = 0;
            for (int i = 0; i < 6; i++)
                result |= (ulong)Convert.ToByte(parts[i], 16) << (8 * i);
            return result;
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}