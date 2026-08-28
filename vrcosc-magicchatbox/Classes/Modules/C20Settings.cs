using CommunityToolkit.Mvvm.ComponentModel;
using vrcosc_magicchatbox.Core.Configuration;

namespace vrcosc_magicchatbox.Classes.Modules;

public partial class C20Settings : ObservableObject
{
    [ObservableProperty] private int _tcpPort = 9876;
    [ObservableProperty] private bool _autoLaunchBridge = true;
    [ObservableProperty] private string _bridgePath = "hr_bridge.exe";
    [ObservableProperty] private bool _embeddedBle = false;
    [ObservableProperty] private string _watchAddress = "96:D6:AF:D0:2B:6E";
    [ObservableProperty] private bool _smoothHeartRate = false;
    [ObservableProperty] private int _smoothHeartRateTimeSpan = 4;
    [ObservableProperty] private bool _showBpmSuffix = false;
    [ObservableProperty] private string _heartRateIcon = "\u2764\uFE0F";
    [ObservableProperty] private string _heartRateFormat = "{icon} {hr} | {max} | {min}";
}
