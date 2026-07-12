using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using vrcosc_magicchatbox.Core.Configuration;
using vrcosc_magicchatbox.Core.State;
using vrcosc_magicchatbox.Services;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace vrcosc_magicchatbox.Classes.Modules;

public partial class C20Settings : ObservableObject
{
    private const string SettingsFileName = "C20ModuleSettings.json";

    [ObservableProperty]
    private string bleAddress = "96:D6:AF:D0:2B:6E";

    [ObservableProperty]
    private int smoothHeartRateTimeSpan = 4;

    [ObservableProperty]
    private bool smoothHeartRate = true;

    [ObservableProperty]
    private string heartRateIcon = "❤️";

    [ObservableProperty]
    private bool showBpmSuffix = false;

    public static string GetFullSettingsPath(string dataPath)
    {
        return Path.Combine(dataPath, SettingsFileName);
    }

    public static C20Settings LoadSettings(string dataPath)
    {
        var settingsPath = GetFullSettingsPath(dataPath);
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = File.ReadAllText(settingsPath);
                return JsonConvert.DeserializeObject<C20Settings>(json) ?? new C20Settings();
            }
            catch { }
        }
        return new C20Settings();
    }

    public void SaveSettings(string dataPath)
    {
        try
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            var dir = Path.GetDirectoryName(GetFullSettingsPath(dataPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(GetFullSettingsPath(dataPath), json);
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Error saving settings: {ex.Message}");
        }
    }
}

public partial class C20HeartRateModule : ObservableObject, IModule
{
    private const ushort C20_ADDRESS_PART1 = 0xD696;
    private const ushort C20_ADDRESS_PART2 = 0xAFD0;
    private const ushort C20_ADDRESS_PART3 = 0x2B6E;
    private static readonly ulong C20_BLE_ADDRESS = ((ulong)C20_ADDRESS_PART1 << 32) | ((ulong)C20_ADDRESS_PART2 << 16) | C20_ADDRESS_PART3;

    private static readonly Guid HR_SERVICE_UUID = new Guid("0000180d-0000-1000-8000-00805f9b34fb");
    private static readonly Guid HR_MEASUREMENT_UUID = new Guid("00002a37-0000-1000-8000-00805f9b34fb");

    private BluetoothLEDevice _device;
    private GattCharacteristic _hrCharacteristic;
    private CancellationTokenSource _cts;
    private bool _isMonitoringStarted;
    private readonly Queue<int> _heartRateHistory = new();
    private readonly object _hrLock = new();
    private int _latestHR;
    private DateTime _lastHRUpdate = DateTime.MinValue;
    private System.Timers.Timer _dataTimer;
    private bool _disposed;

    private readonly IAppState _appState;
    private readonly IOscSender _oscSender;
    private readonly IntegrationSettings _integrationSettings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IEnvironmentService _env;

    [ObservableProperty]
    private C20Settings settings;

    [ObservableProperty]
    private bool deviceConnected;

    [ObservableProperty]
    private int heartRate;

    public string Name => "C20HeartRate";
    public bool IsEnabled { get; set; } = true;
    public bool IsRunning => _isMonitoringStarted;

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StartAsync(CancellationToken ct = default)
    {
        CheckMonitoringConditions();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        StopMonitoring();
        await Task.CompletedTask;
    }

    public void SaveSettings() => Settings.SaveSettings(_env.DataPath);

    public C20HeartRateModule(
        IAppState appState,
        IOscSender oscSender,
        IntegrationSettings integrationSettings,
        IUiDispatcher dispatcher,
        IEnvironmentService env)
    {
        _appState = appState;
        _oscSender = oscSender;
        _integrationSettings = integrationSettings;
        _dispatcher = dispatcher;
        _env = env;
        Settings = C20Settings.LoadSettings(_env.DataPath);

        _dataTimer = new System.Timers.Timer
        {
            AutoReset = true,
            Interval = 1000
        };
        _dataTimer.Elapsed += (_, _) => ProcessData();
    }

    public string GetHeartRateString()
    {
        lock (_hrLock)
        {
            if (HeartRate <= 0 || !DeviceConnected)
                return string.Empty;

            int hr = HeartRate;
            string icon = Settings.HeartRateIcon;
            string bpm = Settings.ShowBpmSuffix ? " bpm" : "";
            return $"{icon} {hr}{bpm}";
        }
    }

    public bool ShouldStartMonitoring()
    {
        return _integrationSettings.IntgrC20HeartRate && _appState.IsVRRunning && _integrationSettings.IntgrC20HeartRate_VR ||
               _integrationSettings.IntgrC20HeartRate && !_appState.IsVRRunning && _integrationSettings.IntgrC20HeartRate_DESKTOP ||
               _integrationSettings.IntgrC20HeartRate_OSC;
    }

    public void PropertyChangedHandler(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAppState.IsVRRunning) ||
            e.PropertyName == nameof(IntegrationSettings.IntgrC20HeartRate) ||
            e.PropertyName == nameof(IntegrationSettings.IntgrC20HeartRate_VR) ||
            e.PropertyName == nameof(IntegrationSettings.IntgrC20HeartRate_DESKTOP) ||
            e.PropertyName == nameof(IntegrationSettings.IntgrC20HeartRate_OSC))
        {
            CheckMonitoringConditions();
        }
    }

    public void OnApplicationClosing()
    {
        Settings.SaveSettings(_env.DataPath);
        StopMonitoring();
    }

    private void CheckMonitoringConditions()
    {
        if (ShouldStartMonitoring() && !_isMonitoringStarted)
        {
            _ = StartMonitoringAsync();
        }
        else if (!ShouldStartMonitoring())
        {
            StopMonitoring();
        }
    }

    private async Task StartMonitoringAsync()
    {
        if (_isMonitoringStarted) return;
        _isMonitoringStarted = true;

        try
        {
            _cts = new CancellationTokenSource();
            await _dispatcher.InvokeAsync(() => DeviceConnected = false);

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(C20_BLE_ADDRESS);
            if (_device == null)
            {
                Logging.WriteInfo("C20: Device not found. Make sure the watch is nearby and awake.");
                _isMonitoringStarted = false;
                return;
            }

            var servicesResult = await _device.GetGattServicesAsync();
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                Logging.WriteInfo($"C20: Failed to get services: {servicesResult.Status}");
                _isMonitoringStarted = false;
                return;
            }

            var hrService = servicesResult.Services.FirstOrDefault(s => s.Uuid == HR_SERVICE_UUID);
            if (hrService == null)
            {
                Logging.WriteInfo("C20: Heart rate service not found");
                _isMonitoringStarted = false;
                return;
            }

            var charsResult = await hrService.GetCharacteristicsAsync();
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                Logging.WriteInfo($"C20: Failed to get characteristics: {charsResult.Status}");
                _isMonitoringStarted = false;
                return;
            }

            _hrCharacteristic = charsResult.Characteristics.FirstOrDefault(c => c.Uuid == HR_MEASUREMENT_UUID);
            if (_hrCharacteristic == null)
            {
                Logging.WriteInfo("C20: HR measurement characteristic not found");
                _isMonitoringStarted = false;
                return;
            }

            _hrCharacteristic.ValueChanged += OnHRValueChanged;
            var notifyResult = await _hrCharacteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (notifyResult != GattCommunicationStatus.Success)
            {
                Logging.WriteInfo($"C20: Failed to subscribe to notifications: {notifyResult}");
                _isMonitoringStarted = false;
                return;
            }

            await _dispatcher.InvokeAsync(() => DeviceConnected = true);
            Logging.WriteInfo("C20: Connected and subscribed to HR notifications!");
            _dataTimer.Start();
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Connection error: {ex.Message}");
            _isMonitoringStarted = false;
        }
    }

    private void OnHRValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            var reader = DataReader.FromBuffer(args.CharacteristicValue);
            byte flags = reader.ReadByte();
            int bpm;

            if ((flags & 0x01) != 0)
            {
                ushort val = reader.ReadUInt16();
                bpm = val;
            }
            else
            {
                bpm = reader.ReadByte();
            }

            if (bpm < 20 || bpm > 250) return;

            lock (_hrLock)
            {
                _latestHR = bpm;
                _lastHRUpdate = DateTime.Now;
                _heartRateHistory.Enqueue(bpm);
                while (_heartRateHistory.Count > Settings.SmoothHeartRateTimeSpan)
                    _heartRateHistory.Dequeue();
            }
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Error parsing HR: {ex.Message}");
        }
    }

    private void ProcessData()
    {
        if (!ShouldStartMonitoring())
        {
            StopMonitoring();
            return;
        }

        if (_device == null || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            _dispatcher.Invoke(() => DeviceConnected = false);
            Logging.WriteInfo("C20: Device disconnected");
            _isMonitoringStarted = false;
            _ = StartMonitoringAsync();
            return;
        }

        lock (_hrLock)
        {
            int hr;
            if (Settings.SmoothHeartRate && _heartRateHistory.Count > 0)
            {
                hr = (int)_heartRateHistory.Average();
            }
            else
            {
                hr = _latestHR;
            }

            if (hr > 0 && HeartRate != hr)
            {
                _dispatcher.Invoke(() => HeartRate = hr);
            }

            if (_integrationSettings.IntgrC20HeartRate_OSC && hr > 0)
            {
                float hrPercent = hr / 255f;
                float fullHRPercent = (hr / 127.5f) - 1f;

                _oscSender.SendOscParam("/avatar/parameters/C20_isHRConnected", DeviceConnected);
                _oscSender.SendOscParam("/avatar/parameters/C20_HR", hr);
                _oscSender.SendOscParam("/avatar/parameters/C20_HRPercent", hrPercent);
                _oscSender.SendOscParam("/avatar/parameters/C20_FullHRPercent", fullHRPercent);

                _oscSender.SendOscParam("/avatar/parameters/isHRConnected", DeviceConnected);
                _oscSender.SendOscParam("/avatar/parameters/HR", hr);
                _oscSender.SendOscParam("/avatar/parameters/HRPercent", hrPercent);

                int ones = hr % 10;
                int tens = (hr / 10) % 10;
                int hundreds = hr / 100;
                _oscSender.SendOscParam("/avatar/parameters/onesHR", ones);
                _oscSender.SendOscParam("/avatar/parameters/tensHR", tens);
                _oscSender.SendOscParam("/avatar/parameters/hundredsHR", hundreds);
            }
        }
    }

    private void StopMonitoring()
    {
        _dataTimer.Stop();

        if (_hrCharacteristic != null)
        {
            try { _hrCharacteristic.ValueChanged -= OnHRValueChanged; }
            catch { }
            _hrCharacteristic = null;
        }

        if (_device != null)
        {
            try { _device.Dispose(); }
            catch { }
            _device = null;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _dispatcher.Invoke(() => DeviceConnected = false);
        _isMonitoringStarted = false;
        Logging.WriteInfo("C20: Disconnected");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMonitoring();
        _dataTimer?.Dispose();
    }
}
