using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using vrcosc_magicchatbox.Core.Configuration;
using vrcosc_magicchatbox.Core.Services;
using vrcosc_magicchatbox.Core.State;
using vrcosc_magicchatbox.Services;

namespace vrcosc_magicchatbox.Classes.Modules;

public partial class C20HeartRateModule : ObservableObject, IModule
{
    private TcpClient? _tcpClient;
    private StreamReader? _tcpReader;
    private Process? _bridgeProcess;
    private CancellationTokenSource? _cts;
    private bool _isMonitoringStarted;
    private readonly Queue<int> _heartRateHistory = new();
    private readonly object _hrLock = new();
    private int _latestHR;
    private DateTime _lastHRUpdate = DateTime.MinValue;
    private System.Timers.Timer? _dataTimer;
    private bool _disposed;

    private readonly IAppState _appState;
    private readonly IOscSender _oscSender;
    private readonly IntegrationSettings _integrationSettings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISettingsProvider<C20Settings> _settingsProvider;

    public C20Settings Settings => _settingsProvider.Value;

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

    public void SaveSettings() => _settingsProvider.FlushPendingSave();

    public C20HeartRateModule(
        IAppState appState,
        IOscSender oscSender,
        IntegrationSettings integrationSettings,
        IUiDispatcher dispatcher,
        ISettingsProvider<C20Settings> settingsProvider)
    {
        _appState = appState;
        _oscSender = oscSender;
        _integrationSettings = integrationSettings;
        _dispatcher = dispatcher;
        _settingsProvider = settingsProvider;

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
            string result = $"{icon} {hr}{bpm}";

            if (Settings.SmoothHeartRate && _heartRateHistory.Count > 0)
            {
                int avg = (int)_heartRateHistory.Average();
                if (avg > 0 && avg != hr)
                    result = $"{icon} {avg}{bpm}";
            }

            return result;
        }
    }

    public bool ShouldStartMonitoring()
    {
        return _integrationSettings.IntgrC20HeartRate && _appState.IsVRRunning && _integrationSettings.IntgrC20HeartRate_VR ||
               _integrationSettings.IntgrC20HeartRate && !_appState.IsVRRunning && _integrationSettings.IntgrC20HeartRate_DESKTOP ||
               _integrationSettings.IntgrC20HeartRate_OSC;
    }

    public void PropertyChangedHandler(object? sender, PropertyChangedEventArgs e)
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

    private void LaunchBridge()
    {
        if (_bridgeProcess != null && !_bridgeProcess.HasExited) return;
        if (!Settings.AutoLaunchBridge) return;

        var path = Settings.BridgePath;
        if (!Path.IsPathRooted(path))
            path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);

        if (!File.Exists(path))
        {
            Logging.WriteInfo($"C20: hr_bridge not found at {path} — start it manually");
            return;
        }

        try
        {
            _bridgeProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
            };
            _bridgeProcess.Start();
            Logging.WriteInfo("C20: Launched hr_bridge process");
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Failed to launch hr_bridge: {ex.Message}");
        }
    }

    private void CheckMonitoringConditions()
    {
        if (ShouldStartMonitoring())
        {
            if (!_isMonitoringStarted)
                _ = StartMonitoringAsync();
        }
        else
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

            LaunchBridge();

            for (var i = 0; i < 10; i++)
            {
                try
                {
                    _tcpClient = new TcpClient();
                    await _tcpClient.ConnectAsync("127.0.0.1", Settings.TcpPort);
                    break;
                }
                catch
                {
                    await Task.Delay(500);
                }
            }

            if (_tcpClient == null || !_tcpClient.Connected)
            {
                Logging.WriteInfo($"C20: Could not connect to hr_bridge on port {Settings.TcpPort}. Make sure it's running.");
                _isMonitoringStarted = false;
                return;
            }

            _tcpReader = new StreamReader(_tcpClient.GetStream());

            await _dispatcher.InvokeAsync(() => DeviceConnected = true);
            Logging.WriteInfo($"C20: Connected to hr_bridge on port {Settings.TcpPort}!");
            _dataTimer?.Start();

            _ = ReadTcpLoopAsync();
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: Connection error: {ex.Message}");
            _isMonitoringStarted = false;
        }
    }

    private async Task ReadTcpLoopAsync()
    {
        try
        {
            while (_tcpClient?.Connected == true)
            {
                var line = await _tcpReader!.ReadLineAsync();
                if (line == null) break;

                var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(line);
                if (data == null) continue;

                if (data.TryGetValue("connected", out var connectedObj))
                {
                    bool connected = Convert.ToBoolean(connectedObj);
                    await _dispatcher.InvokeAsync(() => DeviceConnected = connected);
                }

                if (data.TryGetValue("bpm", out var bpmObj))
                {
                    int bpm = Convert.ToInt32(bpmObj);
                    if (bpm > 0)
                    {
                        lock (_hrLock)
                        {
                            _latestHR = bpm;
                            _lastHRUpdate = DateTime.Now;
                            _heartRateHistory.Enqueue(bpm);
                            while (_heartRateHistory.Count > Settings.SmoothHeartRateTimeSpan)
                                _heartRateHistory.Dequeue();
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.WriteInfo($"C20: TCP read error: {ex.Message}");
        }

        _dispatcher.Invoke(() => DeviceConnected = false);
        Logging.WriteInfo("C20: Disconnected from hr_bridge");
        _isMonitoringStarted = false;
    }

    private void ProcessData()
    {
        if (!ShouldStartMonitoring())
        {
            StopMonitoring();
            return;
        }

        if (_tcpClient == null || !_tcpClient.Connected)
        {
            _dispatcher.Invoke(() => DeviceConnected = false);
            _isMonitoringStarted = false;
            _ = StartMonitoringAsync();
            return;
        }

        lock (_hrLock)
        {
            int hr;
            if (Settings.SmoothHeartRate && _heartRateHistory.Count > 0)
                hr = (int)_heartRateHistory.Average();
            else
                hr = _latestHR;

            if (hr > 0 && HeartRate != hr)
                _dispatcher.Invoke(() => HeartRate = hr);

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
                _oscSender.SendOscParam("/avatar/parameters/FullHRPercent", fullHRPercent);
                _oscSender.SendOscParam("/avatar/parameters/HRFloat", hr / 220f);

                int ones = hr % 10;
                int tens = (hr / 10) % 10;
                int hundreds = hr / 100;
                _oscSender.SendOscParam("/avatar/parameters/onesHR", ones);
                _oscSender.SendOscParam("/avatar/parameters/tensHR", tens);
                _oscSender.SendOscParam("/avatar/parameters/hundredsHR", hundreds);

                _oscSender.SendOscParam("/avatar/parameters/HRMin", hr);
                _oscSender.SendOscParam("/avatar/parameters/HRMax", hr);
            }
        }
    }

    private void StopMonitoring()
    {
        _dataTimer?.Stop();

        if (_tcpReader != null)
        {
            try { _tcpReader.Close(); }
            catch { }
            _tcpReader = null;
        }

        if (_tcpClient != null)
        {
            try { _tcpClient.Close(); }
            catch { }
            _tcpClient = null;
        }

        if (_bridgeProcess != null && !_bridgeProcess.HasExited)
        {
            try
            {
                _bridgeProcess.Kill();
                _bridgeProcess.WaitForExit(3000);
            }
            catch { }
            _bridgeProcess.Dispose();
            _bridgeProcess = null;
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
