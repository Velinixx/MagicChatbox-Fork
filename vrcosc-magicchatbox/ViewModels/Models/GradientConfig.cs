using System.Collections.Generic;

namespace vrcosc_magicchatbox.ViewModels.Models
{
    public class GradientConfig
    {
        public string type { get; set; } = "linear";
        public int angle { get; set; } = 0;
        public List<GradientStop> stops { get; set; } = new List<GradientStop>
        {
            new GradientStop { color = "#3B3054", position = 0 },
            new GradientStop { color = "#240E55", position = 100 }
        };
    }

    public class GradientStop
    {
        public string color { get; set; } = "#000000";
        public int position { get; set; } = 0;
    }
}
