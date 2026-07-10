using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using vrcosc_magicchatbox.ViewModels;

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

    public static string GetFullSettingsPath()
    {
        return System.IO.Path.Combine(ViewModel.Instance.DataPath, SettingsFileName);
    }

    public static C20Settings LoadSettings()
    {
        var settingsPath = GetFullSettingsPath();
        if (System.IO.File.Exists(settingsPath))
        {
            try
            {
                var json = System.IO.File.ReadAllText(settingsPath);
                return JsonConvert.DeserializeObject<C20Settings>(json) ?? new C20Settings();
            }
            catch { }
        }
        return new C20Settings();
    }

    public void SaveSettings()
    {
        try
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            System.IO.File.WriteAllText(GetFullSettingsPath(), json);
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Error saving settings: {ex.Message}");
        }
    }
}

public partial class C20HeartRateModule : ObservableObject
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

    [ObservableProperty]
    private C20Settings settings;

    [ObservableProperty]
    private bool deviceConnected;

    [ObservableProperty]
    private int heartRate;

    public C20HeartRateModule()
    {
        Settings = C20Settings.LoadSettings();
        
        _dataTimer = new System.Timers.Timer
        {
            AutoReset = true,
            Interval = 1000
        };
        _dataTimer.Elapsed += (_, _) =>
        {
            if (Application.Current != null)
                Application.Current.Dispatcher.Invoke(ProcessData);
        };
        
        CheckMonitoringConditions();
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
        return ViewModel.Instance.IntgrC20HeartRate && ViewModel.Instance.IsVRRunning && ViewModel.Instance.IntgrC20HeartRate_VR ||
               ViewModel.Instance.IntgrC20HeartRate && !ViewModel.Instance.IsVRRunning && ViewModel.Instance.IntgrC20HeartRate_DESKTOP ||
               ViewModel.Instance.IntgrC20HeartRate_OSC;
    }

    public bool IsRelevantPropertyChange(string propertyName)
    {
        return propertyName == nameof(ViewModel.Instance.IntgrC20HeartRate) ||
               propertyName == nameof(ViewModel.Instance.IsVRRunning) ||
               propertyName == nameof(ViewModel.Instance.IntgrC20HeartRate_VR) ||
               propertyName == nameof(ViewModel.Instance.IntgrC20HeartRate_DESKTOP) ||
               propertyName == nameof(ViewModel.Instance.IntgrC20HeartRate_OSC);
    }

    public void PropertyChangedHandler(object sender, PropertyChangedEventArgs e)
    {
        if (IsRelevantPropertyChange(e.PropertyName))
        {
            CheckMonitoringConditions();
        }
    }

    public void OnApplicationClosing()
    {
        Settings.SaveSettings();
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
            Application.Current.Dispatcher.Invoke(() => DeviceConnected = false);
            
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
            
            Application.Current.Dispatcher.Invoke(() => DeviceConnected = true);
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
                // uint16 format
                ushort val = reader.ReadUInt16();
                bpm = val;
            }
            else
            {
                // uint8 format
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
        
        // Check if device is still connected
        if (_device == null || _device.ConnectionStatus != BluetoothConnectionStatus.Connected)
        {
            Application.Current.Dispatcher.Invoke(() => DeviceConnected = false);
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
                Application.Current.Dispatcher.Invoke(() => HeartRate = hr);
            }
            
            if (ViewModel.Instance.IntgrC20HeartRate_OSC && hr > 0)
            {
                float hrPercent = hr / 255f;
                float fullHRPercent = (hr / 127.5f) - 1f;
                
                OSCSender.SendOscParam("/avatar/parameters/C20_isHRConnected", DeviceConnected);
                OSCSender.SendOscParam("/avatar/parameters/C20_HR", hr);
                OSCSender.SendOscParam("/avatar/parameters/C20_HRPercent", hrPercent);
                OSCSender.SendOscParam("/avatar/parameters/C20_FullHRPercent", fullHRPercent);
                
                // Also send to standard HR params for compatibility
                OSCSender.SendOscParam("/avatar/parameters/isHRConnected", DeviceConnected);
                OSCSender.SendOscParam("/avatar/parameters/HR", hr);
                OSCSender.SendOscParam("/avatar/parameters/HRPercent", hrPercent);
                
                // Digit split for legacy avatars
                int ones = hr % 10;
                int tens = (hr / 10) % 10;
                int hundreds = hr / 100;
                OSCSender.SendOscParam("/avatar/parameters/onesHR", ones);
                OSCSender.SendOscParam("/avatar/parameters/tensHR", tens);
                OSCSender.SendOscParam("/avatar/parameters/hundredsHR", hundreds);
            }
        }
    }

    private void StopMonitoring()
    {
        _dataTimer.Stop();
        
        if (_hrCharacteristic != null)
        {
            try
            {
                _hrCharacteristic.ValueChanged -= OnHRValueChanged;
            }
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
        
        Application.Current.Dispatcher.Invoke(() => DeviceConnected = false);
        _isMonitoringStarted = false;
        Logging.WriteInfo("C20: Disconnected");
    }
}
