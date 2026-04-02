#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class SwingDetector : Indicator
    {
        private Swing swing;
        private ATR   atr;

        // Track the last confirmed swing bar to avoid duplicates
        private int lastSwingHighBar;
        private int lastSwingLowBar;

        // Alternation: +1 = last was high, -1 = last was low, 0 = none
        private int lastSwingType;

        // Store recent major swings (alternating) for external access
        private List<SwingPoint> majorSwingHighs;
        private List<SwingPoint> majorSwingLows;
        private List<SwingPoint> majorSwings; // interleaved, alternating

        public struct SwingPoint
        {
            public double Price;
            public int    BarIndex;

            public SwingPoint(double price, int barIndex)
            {
                Price    = price;
                BarIndex = barIndex;
            }
        }

        #region Properties

        [Display(Name = "Swing Strength", GroupName = "Detection", Order = 1,
                 Description = "Bars required on each side to confirm a swing")]
        [Range(1, 50)]
        public int SwingStrength { get; set; }

        [Display(Name = "ATR Period", GroupName = "Filter", Order = 10,
                 Description = "ATR lookback period for minimum swing size")]
        [Range(5, 100)]
        public int ATRPeriod { get; set; }

        [Display(Name = "Min ATR Multiple", GroupName = "Filter", Order = 11,
                 Description = "Minimum swing size as multiple of ATR (0 = no filter)")]
        [Range(0, 10.0)]
        public double MinATRMultiple { get; set; }

        [Display(Name = "Min Swing Points", GroupName = "Filter", Order = 12,
                 Description = "Minimum swing size in points (0 = use ATR filter only)")]
        [Range(0, 10000)]
        public double MinSwingPoints { get; set; }

        [Display(Name = "Max Swings Tracked", GroupName = "Detection", Order = 2,
                 Description = "Number of recent major swings to keep")]
        [Range(1, 50)]
        public int MaxSwingsTracked { get; set; }

        [Display(Name = "Swing High Color", GroupName = "Visual", Order = 20)]
        [XmlIgnore]
        public Brush SwingHighColor { get; set; }

        [Browsable(false)]
        public string SwingHighColorSerializable
        {
            get { return Serialize.BrushToString(SwingHighColor); }
            set { SwingHighColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Swing Low Color", GroupName = "Visual", Order = 21)]
        [XmlIgnore]
        public Brush SwingLowColor { get; set; }

        [Browsable(false)]
        public string SwingLowColorSerializable
        {
            get { return Serialize.BrushToString(SwingLowColor); }
            set { SwingLowColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Marker Size", GroupName = "Visual", Order = 22,
                 Description = "Size of swing markers in ticks offset from high/low")]
        [Range(1, 20)]
        public int MarkerOffset { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "Swing Detector";
                Description      = "Identifies major swing highs and lows filtered by minimum size";
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;

                SwingStrength    = 5;
                ATRPeriod        = 14;
                MinATRMultiple   = 1.5;
                MinSwingPoints   = 0;
                MaxSwingsTracked = 20;
                SwingHighColor   = Brushes.Red;
                SwingLowColor    = Brushes.Lime;
                MarkerOffset     = 3;
            }
            else if (State == State.DataLoaded)
            {
                swing = Swing(SwingStrength);
                atr   = ATR(ATRPeriod);

                lastSwingHighBar = -1;
                lastSwingLowBar  = -1;
                lastSwingType    = 0;
                majorSwingHighs  = new List<SwingPoint>();
                majorSwingLows   = new List<SwingPoint>();
                majorSwings      = new List<SwingPoint>();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < SwingStrength + ATRPeriod)
                return;

            double currentATR = atr[0];
            double minSize    = MinSwingPoints > 0
                ? MinSwingPoints
                : currentATR * MinATRMultiple;

            // Check for new swing high (confirmed SwingStrength bars ago)
            int shBar = swing.SwingHighBar(0, 1, SwingStrength + 1);
            if (shBar >= 0 && (CurrentBar - shBar) != lastSwingHighBar)
            {
                double shPrice = High[shBar];
                int absBar     = CurrentBar - shBar;

                double nearestLow = FindNearestSwingLow(shBar);
                double swingSize  = nearestLow > 0 ? Math.Abs(shPrice - nearestLow) : double.MaxValue;

                if (swingSize >= minSize)
                {
                    if (lastSwingType == 1)
                    {
                        // Consecutive high — replace if this one is higher
                        if (majorSwingHighs.Count > 0 && shPrice > majorSwingHighs[majorSwingHighs.Count - 1].Price)
                        {
                            // Remove old marker
                            var old = majorSwingHighs[majorSwingHighs.Count - 1];
                            RemoveDrawObject("SH_" + old.BarIndex);
                            majorSwingHighs[majorSwingHighs.Count - 1] = new SwingPoint(shPrice, absBar);
                            majorSwings[majorSwings.Count - 1] = new SwingPoint(shPrice, absBar);
                        }
                        else
                        {
                            // New high is lower — skip it
                            lastSwingHighBar = absBar;
                            goto CheckSwingLow;
                        }
                    }
                    else
                    {
                        // Alternating — normal add
                        majorSwingHighs.Add(new SwingPoint(shPrice, absBar));
                        majorSwings.Add(new SwingPoint(shPrice, absBar));

                        if (majorSwingHighs.Count > MaxSwingsTracked)
                            majorSwingHighs.RemoveAt(0);
                        if (majorSwings.Count > MaxSwingsTracked * 2)
                            majorSwings.RemoveAt(0);
                    }

                    lastSwingHighBar = absBar;
                    lastSwingType    = 1;

                    NinjaTrader.NinjaScript.DrawingTools.Draw.TriangleDown(this,
                        "SH_" + absBar, false, shBar,
                        shPrice + MarkerOffset * TickSize,
                        SwingHighColor);
                }
            }

            CheckSwingLow:

            // Check for new swing low (confirmed SwingStrength bars ago)
            int slBar = swing.SwingLowBar(0, 1, SwingStrength + 1);
            if (slBar >= 0 && (CurrentBar - slBar) != lastSwingLowBar)
            {
                double slPrice = Low[slBar];
                int absBar     = CurrentBar - slBar;

                double nearestHigh = FindNearestSwingHigh(slBar);
                double swingSize   = nearestHigh > 0 ? Math.Abs(nearestHigh - slPrice) : double.MaxValue;

                if (swingSize >= minSize)
                {
                    if (lastSwingType == -1)
                    {
                        // Consecutive low — replace if this one is lower
                        if (majorSwingLows.Count > 0 && slPrice < majorSwingLows[majorSwingLows.Count - 1].Price)
                        {
                            var old = majorSwingLows[majorSwingLows.Count - 1];
                            RemoveDrawObject("SL_" + old.BarIndex);
                            majorSwingLows[majorSwingLows.Count - 1] = new SwingPoint(slPrice, absBar);
                            majorSwings[majorSwings.Count - 1] = new SwingPoint(slPrice, absBar);
                        }
                        else
                        {
                            lastSwingLowBar = absBar;
                            return;
                        }
                    }
                    else
                    {
                        majorSwingLows.Add(new SwingPoint(slPrice, absBar));
                        majorSwings.Add(new SwingPoint(slPrice, absBar));

                        if (majorSwingLows.Count > MaxSwingsTracked)
                            majorSwingLows.RemoveAt(0);
                        if (majorSwings.Count > MaxSwingsTracked * 2)
                            majorSwings.RemoveAt(0);
                    }

                    lastSwingLowBar = absBar;
                    lastSwingType   = -1;

                    NinjaTrader.NinjaScript.DrawingTools.Draw.TriangleUp(this,
                        "SL_" + absBar, false, slBar,
                        slPrice - MarkerOffset * TickSize,
                        SwingLowColor);
                }
            }
        }

        #region Helpers

        private double FindNearestSwingLow(int fromBarsAgo)
        {
            // Look for the most recent swing low before this swing high
            int slBar = swing.SwingLowBar(fromBarsAgo, 1, CurrentBar);
            if (slBar >= 0)
                return Low[fromBarsAgo + slBar];
            return 0;
        }

        private double FindNearestSwingHigh(int fromBarsAgo)
        {
            // Look for the most recent swing high before this swing low
            int shBar = swing.SwingHighBar(fromBarsAgo, 1, CurrentBar);
            if (shBar >= 0)
                return High[fromBarsAgo + shBar];
            return 0;
        }

        #endregion

        #region Public API (for DSB strategy)

        public SwingPoint? GetLastMajorSwingHigh()
        {
            if (majorSwingHighs.Count == 0) return null;
            return majorSwingHighs[majorSwingHighs.Count - 1];
        }

        public SwingPoint? GetLastMajorSwingLow()
        {
            if (majorSwingLows.Count == 0) return null;
            return majorSwingLows[majorSwingLows.Count - 1];
        }

        public SwingPoint? GetMajorSwingHigh(int instance)
        {
            int idx = majorSwingHighs.Count - instance;
            if (idx < 0) return null;
            return majorSwingHighs[idx];
        }

        public SwingPoint? GetMajorSwingLow(int instance)
        {
            int idx = majorSwingLows.Count - instance;
            if (idx < 0) return null;
            return majorSwingLows[idx];
        }

        public int MajorSwingHighCount { get { return majorSwingHighs.Count; } }
        public int MajorSwingLowCount  { get { return majorSwingLows.Count; } }

        #endregion
    }
}
