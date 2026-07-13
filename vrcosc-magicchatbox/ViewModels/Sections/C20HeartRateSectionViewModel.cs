using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using vrcosc_magicchatbox.Classes.Modules;
using vrcosc_magicchatbox.Core.Configuration;
using vrcosc_magicchatbox.Core.Services;

namespace vrcosc_magicchatbox.ViewModels.Sections;

/// <summary>
/// Section ViewModel for C20 Heart Rate integration options.
/// </summary>
public partial class C20HeartRateSectionViewModel : ObservableObject
{
    private readonly Lazy<IModuleHost> _moduleHost;

    public AppSettings AppSettings { get; }
    public C20Settings ModuleSettings => _moduleHost.Value.C20HeartRate?.Settings;

    public string HasDevice
    {
        get
        {
            var module = _moduleHost.Value.C20HeartRate;
            if (module == null) return "No module";
            return module.DeviceConnected ? "Connected" : "Disconnected";
        }
    }

    public string HeartRate
    {
        get
        {
            var module = _moduleHost.Value.C20HeartRate;
            if (module == null) return "--";
            return module.HeartRate > 0 ? module.HeartRate.ToString() : "--";
        }
    }

    public C20HeartRateSectionViewModel(
        Lazy<IModuleHost> moduleHost,
        ISettingsProvider<AppSettings> appSettingsProvider)
    {
        _moduleHost = moduleHost;
        AppSettings = appSettingsProvider.Value;

        if (_moduleHost.Value.C20HeartRate != null)
        {
            _moduleHost.Value.C20HeartRate.PropertyChanged += OnModulePropertyChanged;
        }
    }

    private void OnModulePropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(C20HeartRateModule.DeviceConnected) or nameof(C20HeartRateModule.HeartRate))
        {
            OnPropertyChanged(nameof(HasDevice));
            OnPropertyChanged(nameof(HeartRate));
        }
    }
}
