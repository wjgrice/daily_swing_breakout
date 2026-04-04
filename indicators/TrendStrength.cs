#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TrendStrength : Indicator
    {
        private LinRegSlope linRegSlope;
        private ATR         atr;

        #region Properties

        [Display(Name = "Period", GroupName = "Parameters", Order = 1,
                 Description = "Lookback period for linear regression")]
        [Range(5, 200)]
        public int Period { get; set; }

        [Display(Name = "ATR Period", GroupName = "Parameters", Order = 2,
                 Description = "ATR period for normalizing the slope")]
        [Range(5, 100)]
        public int ATRPeriod { get; set; }

        [Display(Name = "Up Color", GroupName = "Visual", Order = 10)]
        [XmlIgnore]
        public Brush UpColor { get; set; }

        [Browsable(false)]
        public string UpColorSerializable
        {
            get { return Serialize.BrushToString(UpColor); }
            set { UpColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Down Color", GroupName = "Visual", Order = 11)]
        [XmlIgnore]
        public Brush DownColor { get; set; }

        [Browsable(false)]
        public string DownColorSerializable
        {
            get { return Serialize.BrushToString(DownColor); }
            set { DownColor = Serialize.StringToBrush(value); }
        }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "Trend Strength";
                Description      = "Normalized linear regression slope — shows trend direction and strength";
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = false;
                DisplayInDataBox = true;
                PaintPriceMarkers = false;

                Period           = 20;
                ATRPeriod        = 14;
                UpColor          = Brushes.Lime;
                DownColor        = Brushes.Red;

                AddPlot(new Stroke(Brushes.DodgerBlue, 2), PlotStyle.Line, "Strength");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), 0.75, "+0.75");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), 0.50, "+0.50");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), 0.25, "+0.25");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dot,  1), 0,    "Zero");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), -0.25, "-0.25");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), -0.50, "-0.50");
                AddLine(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), -0.75, "-0.75");
            }
            else if (State == State.DataLoaded)
            {
                linRegSlope = LinRegSlope(Period);
                atr         = ATR(ATRPeriod);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < Math.Max(Period, ATRPeriod)) return;

            double slope    = linRegSlope[0];
            double atrValue = atr[0];

            // Normalize: slope per bar / ATR → roughly -1 to +1 range
            double normalized = atrValue > 0 ? slope / atrValue : 0;

            // Clamp to -1..+1
            normalized = Math.Max(-1.0, Math.Min(1.0, normalized));

            Value[0] = normalized;

            // Color the plot
            PlotBrushes[0][0] = normalized >= 0 ? UpColor : DownColor;
        }

        #region Public API

        public double GetStrength(int barsAgo)
        {
            if (barsAgo < 0 || barsAgo >= CurrentBar) return 0;
            return Value[barsAgo];
        }

        #endregion
    }
}
