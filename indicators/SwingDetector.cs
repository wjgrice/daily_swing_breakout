#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
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

        private int lastSwingHighBar;
        private int lastSwingLowBar;
        private int lastSwingType;

        private List<SwingPoint> majorSwingHighs;
        private List<SwingPoint> majorSwingLows;
        private List<SwingPoint> majorSwings;

        // Active swing levels (lines extending right until violated)
        private struct SwingLevel
        {
            public double Price;
            public int    StartAbsBar;  // absolute bar index where swing was detected
            public int    Type;         // +1 = high, -1 = low
            public string Tag;
            public bool   Violated;
        }
        private List<SwingLevel> activeLevels;

        // MTF state
        private bool  isMTF;
        private Swing htfSwing;
        private ATR   htfATR;
        private int   htfLastSwingHighBar;
        private int   htfLastSwingLowBar;
        private int   htfLastSwingType;

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

        [Display(Name = "Swing Strength", GroupName = "Detection", Order = 1)]
        [Range(1, 50)]
        public int SwingStrength { get; set; }

        [Display(Name = "ATR Period", GroupName = "Filter", Order = 10)]
        [Range(5, 100)]
        public int ATRPeriod { get; set; }

        [Display(Name = "Min ATR Multiple", GroupName = "Filter", Order = 11)]
        [Range(0, 10.0)]
        public double MinATRMultiple { get; set; }

        [Display(Name = "Min Swing Points", GroupName = "Filter", Order = 12)]
        [Range(0, 10000)]
        public double MinSwingPoints { get; set; }

        [Display(Name = "Max Swings Tracked", GroupName = "Detection", Order = 2)]
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

        [Display(Name = "Line Width", GroupName = "Visual", Order = 22)]
        [Range(1, 5)]
        public int LineWidth { get; set; }

        [Display(Name = "Line Dash Style", GroupName = "Visual", Order = 23)]
        public DashStyleHelper LineDash { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF Type", GroupName = "Multi-Timeframe", Order = 30)]
        public BarsPeriodType HTFType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF Value", GroupName = "Multi-Timeframe", Order = 31)]
        [Range(1, 10000)]
        public int HTFValue { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "Swing Detector";
                Description      = "Swing levels as lines extending until violated";
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
                LineWidth        = 1;
                LineDash         = DashStyleHelper.Dash;
                HTFType          = BarsPeriodType.Day;
                HTFValue         = 1;
            }
            else if (State == State.Configure)
            {
                int chartMin = BarPeriodToMinutes(BarsPeriod.BarsPeriodType, BarsPeriod.Value);
                int htfMin   = BarPeriodToMinutes(HTFType, HTFValue);
                isMTF = chartMin < htfMin;

                if (isMTF)
                    AddDataSeries(HTFType, HTFValue);
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
                activeLevels     = new List<SwingLevel>();

                if (isMTF)
                {
                    htfSwing = Swing(BarsArray[1], SwingStrength);
                    htfATR   = ATR(BarsArray[1], ATRPeriod);
                    htfLastSwingHighBar = -1;
                    htfLastSwingLowBar  = -1;
                    htfLastSwingType    = 0;
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (isMTF)
            {
                if (CurrentBars[0] < 1 || CurrentBars[1] < 1) return;

                if (BarsInProgress == 1)
                    OnHTFBarUpdate();
                else if (BarsInProgress == 0)
                    UpdateLevels();
            }
            else
            {
                if (BarsInProgress != 0) return;
                OnNativeBarUpdate();
                UpdateLevels();
            }
        }

        #region Level management — extend or violate

        private void UpdateLevels()
        {
            for (int i = activeLevels.Count - 1; i >= 0; i--)
            {
                var lv = activeLevels[i];
                if (lv.Violated) continue;

                int startBarsAgo = CurrentBars[0] - lv.StartAbsBar;
                if (startBarsAgo < 0) continue;

                // Check violation
                bool violated = lv.Type == 1
                    ? Highs[0][0] > lv.Price
                    : Lows[0][0] < lv.Price;

                if (violated)
                {
                    // Redraw line ending at this bar
                    lv.Violated = true;
                    activeLevels[i] = lv;

                    Brush color = lv.Type == 1 ? SwingHighColor : SwingLowColor;
                    NinjaTrader.NinjaScript.DrawingTools.Draw.Line(this,
                        lv.Tag, false, startBarsAgo, lv.Price, 0, lv.Price,
                        color, LineDash, LineWidth);
                }
                else
                {
                    // Extend line to current bar
                    Brush color = lv.Type == 1 ? SwingHighColor : SwingLowColor;
                    NinjaTrader.NinjaScript.DrawingTools.Draw.Line(this,
                        lv.Tag, false, startBarsAgo, lv.Price, 0, lv.Price,
                        color, LineDash, LineWidth);
                }
            }

            // Clean up old violated levels
            if (activeLevels.Count > MaxSwingsTracked * 4)
                activeLevels.RemoveAll(lv => lv.Violated);
        }

        private void AddSwingLevel(double price, int absBar, int type, string tag)
        {
            activeLevels.Add(new SwingLevel
            {
                Price       = price,
                StartAbsBar = absBar,
                Type        = type,
                Tag         = tag,
                Violated    = false
            });
        }

        #endregion

        #region Native mode

        private void OnNativeBarUpdate()
        {
            if (CurrentBar < SwingStrength + ATRPeriod)
                return;

            double currentATR = atr[0];
            double minSize    = MinSwingPoints > 0 ? MinSwingPoints : currentATR * MinATRMultiple;

            // Swing high
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
                        if (majorSwingHighs.Count > 0 && shPrice > majorSwingHighs[majorSwingHighs.Count - 1].Price)
                        {
                            var old = majorSwingHighs[majorSwingHighs.Count - 1];
                            RemoveDrawObject("SH_" + old.BarIndex);
                            RemoveOldLevel("SH_" + old.BarIndex);
                            majorSwingHighs[majorSwingHighs.Count - 1] = new SwingPoint(shPrice, absBar);
                            if (majorSwings.Count > 0)
                                majorSwings[majorSwings.Count - 1] = new SwingPoint(shPrice, absBar);
                        }
                        else
                        {
                            lastSwingHighBar = absBar;
                            goto CheckSwingLow;
                        }
                    }
                    else
                    {
                        majorSwingHighs.Add(new SwingPoint(shPrice, absBar));
                        majorSwings.Add(new SwingPoint(shPrice, absBar));
                        if (majorSwingHighs.Count > MaxSwingsTracked) majorSwingHighs.RemoveAt(0);
                        if (majorSwings.Count > MaxSwingsTracked * 2) majorSwings.RemoveAt(0);
                    }

                    lastSwingHighBar = absBar;
                    lastSwingType    = 1;

                    string tag = "SH_" + absBar;
                    AddSwingLevel(shPrice, absBar, 1, tag);
                }
            }

            CheckSwingLow:

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
                        if (majorSwingLows.Count > 0 && slPrice < majorSwingLows[majorSwingLows.Count - 1].Price)
                        {
                            var old = majorSwingLows[majorSwingLows.Count - 1];
                            RemoveDrawObject("SL_" + old.BarIndex);
                            RemoveOldLevel("SL_" + old.BarIndex);
                            majorSwingLows[majorSwingLows.Count - 1] = new SwingPoint(slPrice, absBar);
                            if (majorSwings.Count > 0)
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
                        if (majorSwingLows.Count > MaxSwingsTracked) majorSwingLows.RemoveAt(0);
                        if (majorSwings.Count > MaxSwingsTracked * 2) majorSwings.RemoveAt(0);
                    }

                    lastSwingLowBar = absBar;
                    lastSwingType   = -1;

                    string tag = "SL_" + absBar;
                    AddSwingLevel(slPrice, absBar, -1, tag);
                }
            }
        }

        private void RemoveOldLevel(string tag)
        {
            for (int i = activeLevels.Count - 1; i >= 0; i--)
            {
                if (activeLevels[i].Tag == tag)
                {
                    activeLevels.RemoveAt(i);
                    break;
                }
            }
        }

        #endregion

        #region MTF mode — HTF swing detection

        private void OnHTFBarUpdate()
        {
            if (CurrentBars[1] < SwingStrength + ATRPeriod + 2)
                return;

            double currentATR = htfATR[0];
            double minSize    = MinSwingPoints > 0 ? MinSwingPoints : currentATR * MinATRMultiple;

            // Swing high on HTF
            int shBar = htfSwing.SwingHighBar(0, 1, SwingStrength + 1);
            if (shBar >= 0 && (CurrentBars[1] - shBar) != htfLastSwingHighBar)
            {
                double shPrice = Highs[1][shBar];
                int absBar     = CurrentBars[1] - shBar;

                double nearestLow = FindNearestHTFSwingLow(shBar);
                double swingSize  = nearestLow > 0 ? Math.Abs(shPrice - nearestLow) : double.MaxValue;

                if (swingSize >= minSize)
                {
                    bool accepted = true;
                    if (htfLastSwingType == 1)
                    {
                        if (majorSwingHighs.Count > 0 && shPrice > majorSwingHighs[majorSwingHighs.Count - 1].Price)
                        {
                            var old = majorSwingHighs[majorSwingHighs.Count - 1];
                            RemoveDrawObject("SH_" + old.BarIndex);
                            RemoveOldLevel("SH_" + old.BarIndex);
                            majorSwingHighs[majorSwingHighs.Count - 1] = new SwingPoint(shPrice, absBar);
                            if (majorSwings.Count > 0)
                                majorSwings[majorSwings.Count - 1] = new SwingPoint(shPrice, absBar);
                        }
                        else accepted = false;
                    }
                    else
                    {
                        majorSwingHighs.Add(new SwingPoint(shPrice, absBar));
                        majorSwings.Add(new SwingPoint(shPrice, absBar));
                        if (majorSwingHighs.Count > MaxSwingsTracked) majorSwingHighs.RemoveAt(0);
                        if (majorSwings.Count > MaxSwingsTracked * 2) majorSwings.RemoveAt(0);
                    }

                    if (accepted)
                    {
                        htfLastSwingHighBar = absBar;
                        htfLastSwingType    = 1;

                        // Place level on LTF at the extreme bar
                        DateTime rangeEnd   = Times[1][shBar];
                        DateTime rangeStart = (shBar + 1 < CurrentBars[1]) ? Times[1][shBar + 1] : rangeEnd.AddDays(-1);

                        int ltfBar = FindExtremeLTFBar(rangeStart, rangeEnd, 1);
                        if (ltfBar >= 0)
                        {
                            string tag = "SH_" + ltfBar;
                            AddSwingLevel(shPrice, ltfBar, 1, tag);
                        }
                    }
                }
            }

            // Swing low on HTF
            int slBar = htfSwing.SwingLowBar(0, 1, SwingStrength + 1);
            if (slBar >= 0 && (CurrentBars[1] - slBar) != htfLastSwingLowBar)
            {
                double slPrice = Lows[1][slBar];
                int absBar     = CurrentBars[1] - slBar;

                double nearestHigh = FindNearestHTFSwingHigh(slBar);
                double swingSize   = nearestHigh > 0 ? Math.Abs(nearestHigh - slPrice) : double.MaxValue;

                if (swingSize >= minSize)
                {
                    bool accepted = true;
                    if (htfLastSwingType == -1)
                    {
                        if (majorSwingLows.Count > 0 && slPrice < majorSwingLows[majorSwingLows.Count - 1].Price)
                        {
                            var old = majorSwingLows[majorSwingLows.Count - 1];
                            RemoveDrawObject("SL_" + old.BarIndex);
                            RemoveOldLevel("SL_" + old.BarIndex);
                            majorSwingLows[majorSwingLows.Count - 1] = new SwingPoint(slPrice, absBar);
                            if (majorSwings.Count > 0)
                                majorSwings[majorSwings.Count - 1] = new SwingPoint(slPrice, absBar);
                        }
                        else accepted = false;
                    }
                    else
                    {
                        majorSwingLows.Add(new SwingPoint(slPrice, absBar));
                        majorSwings.Add(new SwingPoint(slPrice, absBar));
                        if (majorSwingLows.Count > MaxSwingsTracked) majorSwingLows.RemoveAt(0);
                        if (majorSwings.Count > MaxSwingsTracked * 2) majorSwings.RemoveAt(0);
                    }

                    if (accepted)
                    {
                        htfLastSwingLowBar = absBar;
                        htfLastSwingType   = -1;

                        DateTime rangeEnd   = Times[1][slBar];
                        DateTime rangeStart = (slBar + 1 < CurrentBars[1]) ? Times[1][slBar + 1] : rangeEnd.AddDays(-1);

                        int ltfBar = FindExtremeLTFBar(rangeStart, rangeEnd, -1);
                        if (ltfBar >= 0)
                        {
                            string tag = "SL_" + ltfBar;
                            AddSwingLevel(slPrice, ltfBar, -1, tag);
                        }
                    }
                }
            }
        }

        private int FindExtremeLTFBar(DateTime rangeStart, DateTime rangeEnd, int type)
        {
            int bestBar    = -1;
            double bestVal = type == 1 ? double.MinValue : double.MaxValue;

            for (int i = 0; i < CurrentBars[0]; i++)
            {
                DateTime t = Times[0][i];
                if (t > rangeEnd) continue;
                if (t <= rangeStart) break;

                if (type == 1 && Highs[0][i] > bestVal)
                {
                    bestVal = Highs[0][i];
                    bestBar = CurrentBars[0] - i; // convert to absolute
                }
                else if (type == -1 && Lows[0][i] < bestVal)
                {
                    bestVal = Lows[0][i];
                    bestBar = CurrentBars[0] - i;
                }
            }

            return bestBar;
        }

        #endregion

        #region Helpers

        private double FindNearestSwingLow(int fromBarsAgo)
        {
            int lookBack = CurrentBar - fromBarsAgo;
            if (lookBack <= 0) return 0;
            int slBar = swing.SwingLowBar(fromBarsAgo, 1, lookBack);
            if (slBar >= 0 && (fromBarsAgo + slBar) <= CurrentBar)
                return Low[fromBarsAgo + slBar];
            return 0;
        }

        private double FindNearestSwingHigh(int fromBarsAgo)
        {
            int lookBack = CurrentBar - fromBarsAgo;
            if (lookBack <= 0) return 0;
            int shBar = swing.SwingHighBar(fromBarsAgo, 1, lookBack);
            if (shBar >= 0 && (fromBarsAgo + shBar) <= CurrentBar)
                return High[fromBarsAgo + shBar];
            return 0;
        }

        private double FindNearestHTFSwingLow(int fromBarsAgo)
        {
            int lookBack = Math.Min(CurrentBars[1] - fromBarsAgo, CurrentBars[1]);
            if (lookBack <= 0) return 0;
            int slBar = htfSwing.SwingLowBar(fromBarsAgo, 1, lookBack);
            if (slBar >= 0 && (fromBarsAgo + slBar) <= CurrentBars[1])
                return Lows[1][fromBarsAgo + slBar];
            return 0;
        }

        private double FindNearestHTFSwingHigh(int fromBarsAgo)
        {
            int lookBack = Math.Min(CurrentBars[1] - fromBarsAgo, CurrentBars[1]);
            if (lookBack <= 0) return 0;
            int shBar = htfSwing.SwingHighBar(fromBarsAgo, 1, lookBack);
            if (shBar >= 0 && (fromBarsAgo + shBar) <= CurrentBars[1])
                return Highs[1][fromBarsAgo + shBar];
            return 0;
        }

        private int BarPeriodToMinutes(BarsPeriodType type, int value)
        {
            switch (type)
            {
                case BarsPeriodType.Minute:  return value;
                case BarsPeriodType.Day:     return value * 1440;
                case BarsPeriodType.Week:    return value * 10080;
                case BarsPeriodType.Month:   return value * 43200;
                case BarsPeriodType.Second:  return Math.Max(1, value / 60);
                case BarsPeriodType.Tick:    return 1;
                default:                     return value;
            }
        }

        #endregion

        #region Public API

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
