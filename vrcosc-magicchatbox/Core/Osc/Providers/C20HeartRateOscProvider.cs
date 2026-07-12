using System;
using vrcosc_magicchatbox.Classes.Modules;
using vrcosc_magicchatbox.Core.Configuration;
using vrcosc_magicchatbox.Core.Services;
using vrcosc_magicchatbox.Core.State;

namespace vrcosc_magicchatbox.Core.Osc.Providers;

/// <summary>
/// Adapter: C20 BLE heart rate → OSC segment.
/// Wraps <see cref="C20HeartRateModule.GetHeartRateString"/>.
/// </summary>
public sealed class C20HeartRateOscProvider : IOscProvider
{
    private readonly Lazy<IModuleHost> _modules;
    private readonly IntegrationSettings _intgr;

    public C20HeartRateOscProvider(
        Lazy<IModuleHost> modules,
        ISettingsProvider<IntegrationSettings> intgrProvider)
    {
        _modules = modules;
        _intgr = intgrProvider.Value;
    }

    public string SortKey => "C20HeartRate";
    public string UiKey => "C20HeartRate";
    public int Priority => 41;

    public bool IsEnabledForCurrentMode(bool isVR)
        => _intgr.IntgrC20HeartRate
           && (isVR ? _intgr.IntgrC20HeartRate_VR : _intgr.IntgrC20HeartRate_DESKTOP);

    public OscSegment? TryBuild(OscBuildContext context)
    {
        string output = _modules.Value.C20HeartRate?.GetHeartRateString();
        if (string.IsNullOrEmpty(output)) return null;

        return new OscSegment { Text = output };
    }
}
