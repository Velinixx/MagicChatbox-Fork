using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using vrcosc_magicchatbox.ViewModels;
using GradientStopModel = vrcosc_magicchatbox.ViewModels.Models.GradientStop;

namespace vrcosc_magicchatbox.UI.Dialogs
{
    public partial class GradientEditorDialog : Window
    {
        private GradientConfig _gradient;
        private bool _isUpdatingUI = false;

        public string InitialJson { get; set; }

        public GradientEditorDialog()
        {
            InitializeComponent();

            string json = InitialJson ?? GetViewModel()?.AppSettingsInstance.GradientConfigJson;
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var result = JsonConvert.DeserializeObject<GradientConfig>(json);
                    _gradient = result ?? GetDefaultGradient();
                }
                catch { _gradient = GetDefaultGradient(); }
            }
            else
            {
                _gradient = GetDefaultGradient();
            }

            LoadGradientIntoUI();
            RenderPreview();
        }

        private static GradientConfig GetDefaultGradient()
        {
            return new GradientConfig
            {
                type = "linear",
                angle = 0,
                stops = new List<GradientStopModel>
                {
                    new GradientStopModel { color = "#3B3054", position = 0 },
                    new GradientStopModel { color = "#240E55", position = 100 }
                }
            };
        }

        public static GradientConfig GetDefaultGradientStatic() => GetDefaultGradient();

        private void LoadGradientIntoUI()
        {
            _isUpdatingUI = true;

            if (_gradient.type == "radial")
            {
                RadioRadial.IsChecked = true;
                AnglePanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                RadioLinear.IsChecked = true;
                AnglePanel.Visibility = Visibility.Visible;
            }

            AngleSlider.Value = _gradient.angle;
            AngleValue.Text = _gradient.angle.ToString();

            RebuildStopsUI();

            _isUpdatingUI = false;
        }

        private void RebuildStopsUI()
        {
            StopsPanel.Children.Clear();

            var sorted = _gradient.stops.OrderBy(s => s.position).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                int index = i;
                var stop = sorted[i];

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 4)
                };

                var swatchBtn = new Button
                {
                    Width = 36,
                    Height = 24,
                    Margin = new Thickness(0, 0, 6, 0),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "Click to change color"
                };
                try { swatchBtn.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stop.color)); }
                catch { swatchBtn.Background = new SolidColorBrush(Colors.Gray); }

                int capturedIndex = index;

                swatchBtn.Click += (s, e) => PickColor(capturedIndex);

                var posLabel = new TextBlock
                {
                    Width = 30,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Albert Sans Thin"),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x7D, 0x73, 0x97)),
                    Text = stop.position.ToString()
                };

                var posSlider = new Slider
                {
                    Width = 120,
                    Height = 20,
                    Minimum = 0,
                    Maximum = 100,
                    Value = stop.position,
                    IsSnapToTickEnabled = true,
                    TickFrequency = 1,
                    Style = Application.Current.TryFindResource("CustomSlider") as Style,
                    Margin = new Thickness(4, 0, 4, 0)
                };

                int capturedIdx = index;
                posSlider.ValueChanged += (s, e) =>
                {
                    if (_isUpdatingUI) return;
                    int idx = capturedIdx;
                    if (idx < _gradient.stops.Count)
                    {
                        _gradient.stops[idx].position = (int)e.NewValue;
                        posLabel.Text = ((int)e.NewValue).ToString();
                        RenderPreview();
                        UpdateAngleGroupVisibility();
                    }
                };

                var deleteBtn = new Button
                {
                    Width = 24,
                    Height = 24,
                    Content = "×",
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Colors.White),
                    Background = new SolidColorBrush(Color.FromRgb(0xA3, 0x22, 0x22)),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                var deleteIdx = index;
                deleteBtn.Click += (s, e) =>
                {
                    if (_gradient.stops.Count <= 2)
                    {
                        MessageBox.Show("Need at least 2 color stops.", "Gradient Editor", MessageBoxButton.OK, MessageBoxImage.Information);
                        return;
                    }
                    _gradient.stops.RemoveAt(deleteIdx);
                    RebuildStopsUI();
                    RenderPreview();
                    UpdateAngleGroupVisibility();
                };

                row.Children.Add(swatchBtn);
                row.Children.Add(posSlider);
                row.Children.Add(posLabel);
                row.Children.Add(deleteBtn);

                StopsPanel.Children.Add(row);
            }
        }

        private void PickColor(int stopIndex)
        {
            if (stopIndex < 0 || stopIndex >= _gradient.stops.Count) return;

            var stop = _gradient.stops[stopIndex];

            var colorDialog = new System.Windows.Forms.ColorDialog
            {
                Color = System.Drawing.ColorTranslator.FromHtml(stop.color),
                FullOpen = true
            };

            if (colorDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var c = colorDialog.Color;
                stop.color = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                RebuildStopsUI();
                RenderPreview();
                UpdateAngleGroupVisibility();
            }
        }

        private void RenderPreview()
        {
            try
            {
                PreviewRect.Fill = BuildGradientBrush(_gradient);
            }
            catch { }
        }

        public static Brush BuildGradientBrush(GradientConfig grad)
        {
            var sorted = grad.stops.OrderBy(s => s.position).ToList();
            if (sorted.Count == 0)
                return new SolidColorBrush(Colors.Gray);

            if (sorted.Count == 1)
            {
                try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(sorted[0].color)); }
                catch { return new SolidColorBrush(Colors.Gray); }
            }

            if (grad.type == "radial")
            {
                var brush = new RadialGradientBrush
                {
                    Center = new Point(0.5, 0.5),
                    GradientOrigin = new Point(0.5, 0.5),
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    SpreadMethod = GradientSpreadMethod.Pad
                };

                foreach (var stop in sorted)
                {
                    var offset = stop.position / 100.0;
                    try
                    {
                        var color = (Color)ColorConverter.ConvertFromString(stop.color);
                        brush.GradientStops.Add(new GradientStop(color, offset));
                    }
                    catch { }
                }

                return brush;
            }

            {
                double angleRad = grad.angle * Math.PI / 180.0;
                double ex = Math.Sin(angleRad) * 0.5;
                double ey = -Math.Cos(angleRad) * 0.5;

                var brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5 - ex, 0.5 - ey),
                    EndPoint = new Point(0.5 + ex, 0.5 + ey),
                    SpreadMethod = GradientSpreadMethod.Pad
                };

                foreach (var stop in sorted)
                {
                    var offset = stop.position / 100.0;
                    try
                    {
                        var color = (Color)ColorConverter.ConvertFromString(stop.color);
                        brush.GradientStops.Add(new GradientStop(color, offset));
                    }
                    catch { }
                }

                return brush;
            }
        }

        public static GradientConfig ReadGradientFromViewModel(ViewModel vm = null)
        {
            vm ??= GetViewModelFromActiveWindow();
            string json = vm?.AppSettingsInstance.GradientConfigJson;
            if (!string.IsNullOrEmpty(json))
            {
                try { return JsonConvert.DeserializeObject<GradientConfig>(json) ?? GetDefaultGradient(); }
                catch { }
            }
            return GetDefaultGradient();
        }

        private void UpdateAngleGroupVisibility()
        {
            AnglePanel.Visibility = _gradient.type == "linear" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RadioType_Checked(object sender, RoutedEventArgs e)
        {
            if (_isUpdatingUI) return;
            _gradient.type = RadioRadial.IsChecked == true ? "radial" : "linear";
            UpdateAngleGroupVisibility();
            RenderPreview();
        }

        private void AngleSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUI) return;
            _gradient.angle = (int)e.NewValue;
            AngleValue.Text = ((int)e.NewValue).ToString();
            RenderPreview();
        }

        private void AddStop_Click(object sender, RoutedEventArgs e)
        {
            _gradient.stops.Add(new GradientStopModel
            {
                color = "#7B7195",
                position = 50
            });
            RebuildStopsUI();
            RenderPreview();
            UpdateAngleGroupVisibility();
        }

        private void PreviewBtn_Click(object sender, RoutedEventArgs e)
        {
            RenderPreview();
        }

        private void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            _gradient = GetDefaultGradient();
            LoadGradientIntoUI();
            RenderPreview();
            UpdateAngleGroupVisibility();
        }

        private ViewModel GetViewModel()
        {
            if (Owner?.DataContext is ViewModel vm)
                return vm;
            return GetViewModelFromActiveWindow();
        }

        private static ViewModel GetViewModelFromActiveWindow()
        {
            foreach (Window w in Application.Current.Windows)
                if (w.DataContext is ViewModel vm)
                    return vm;
            return null;
        }

        private void ApplyBtn_Click(object sender, RoutedEventArgs e)
        {
            var vm = GetViewModel();
            if (vm != null)
            {
                vm.AppSettingsInstance.GradientConfigJson = JsonConvert.SerializeObject(_gradient);
                vm.AppSettingsInstance.SelectedTheme = 4; // Custom
                vm.RefreshWindowBackground();
            }
            DialogResult = true;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
