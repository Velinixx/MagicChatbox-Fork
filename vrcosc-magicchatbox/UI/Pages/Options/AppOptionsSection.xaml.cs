using Newtonsoft.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using vrcosc_magicchatbox.Classes.Modules;
using vrcosc_magicchatbox.ViewModels;
using vrcosc_magicchatbox.ViewModels.Models;
using vrcosc_magicchatbox.ViewModels.Sections;

namespace vrcosc_magicchatbox.UI.Pages.Options;

public partial class AppOptionsSection : UserControl
{
    public AppOptionsSection()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncThemeUI();
    }

    private AppSettings AppSettings => ((AppOptionsSectionViewModel)DataContext).AppSettings;

    private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out int theme))
        {
            AppSettings.SelectedTheme = theme;
            CustomThemePanel.Visibility = theme == 4 ? Visibility.Visible : Visibility.Collapsed;
            UpdateActiveGradientSwatch();
        }
    }

    private void OpenGradientEditorBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Dialogs.GradientEditorDialog
        {
            InitialJson = AppSettings.GradientConfigJson,
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
        UpdateActiveGradientSwatch();
    }

    private void UpdateActiveGradientSwatch()
    {
        try
        {
            if (AppSettings.SelectedTheme == 4)
            {
                string json = AppSettings.GradientConfigJson;
                if (!string.IsNullOrEmpty(json))
                {
                    var grad = JsonConvert.DeserializeObject<GradientConfig>(json);
                    if (grad != null)
                        ActiveGradientSwatch.Background = Dialogs.GradientEditorDialog.BuildGradientBrush(grad);
                    else
                        ActiveGradientSwatch.Background = new SolidColorBrush(Colors.Gray);
                }
            }
        }
        catch { }
    }

    public void SyncThemeUI()
    {
        int theme = AppSettings.SelectedTheme;
        var radios = new[] { DarkRadio, LightRadio, MidnightRadio, OriginalRadio, CustomThemeRadio };
        if (theme >= 0 && theme < radios.Length)
            radios[theme].IsChecked = true;
        CustomThemePanel.Visibility = theme == 4 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActiveGradientSwatch();
    }
}
