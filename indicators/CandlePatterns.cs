#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class CandlePatterns : Indicator
    {
        private Series<bool>   insideBarFlags;
        private Series<double> motherHighs;
        private Series<double> motherLows;
        private ATR atr;

        // MTF state
        private bool  isMTF;
        private bool  htfAlternate;
        private Brush htfBoundaryBrushFrozen;

        // HTF inside bar tracking (simple vars, not Series)
        private bool   htfPrevIsInside;
        private double htfPrevMotherHigh;
        private double htfPrevMotherLow;
        private ATR    htfATR;

        private enum BarRole { None, Mother, Inside }

        #region Properties

        [Display(Name = "Show Inside Bars", GroupName = "Inside Bars", Order = 1)]
        public bool ShowInsideBars { get; set; }

        [Display(Name = "Inside Bar Color", GroupName = "Inside Bars", Order = 2)]
        [XmlIgnore]
        public Brush InsideBarColor { get; set; }

        [Browsable(false)]
        public string InsideBarColorSerializable
        {
            get { return Serialize.BrushToString(InsideBarColor); }
            set { InsideBarColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Min Mother ATR", GroupName = "Inside Bars", Order = 3,
                 Description = "Mother bar range must be at least this multiple of ATR (0 = no filter)")]
        [Range(0, 5.0)]
        public double MinMotherATR { get; set; }

        [Display(Name = "ATR Period", GroupName = "Inside Bars", Order = 4)]
        [Range(5, 100)]
        public int ATRPeriod { get; set; }

        [Display(Name = "Mother Bar Color", GroupName = "Inside Bars", Order = 5)]
        [XmlIgnore]
        public Brush MotherBarColor { get; set; }

        [Browsable(false)]
        public string MotherBarColorSerializable
        {
            get { return Serialize.BrushToString(MotherBarColor); }
            set { MotherBarColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Show HTF Boundaries", GroupName = "Multi-Timeframe", Order = 8,
                 Description = "Alternating background shading to delineate HTF bars")]
        public bool ShowHTFBoundaries { get; set; }

        [Display(Name = "Boundary Opacity %", GroupName = "Multi-Timeframe", Order = 9,
                 Description = "Opacity of alternating background highlight")]
        [Range(3, 30)]
        public int BoundaryOpacity { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF Type", GroupName = "Multi-Timeframe", Order = 10,
                 Description = "Higher timeframe bar type (Day, Minute, Week)")]
        public BarsPeriodType HTFType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "HTF Value", GroupName = "Multi-Timeframe", Order = 11,
                 Description = "Higher timeframe period (1=Daily, 240=4hr)")]
        [Range(1, 10000)]
        public int HTFValue { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name              = "Candle Patterns";
                Description       = "Inside bar detection with multi-timeframe projection";
                Calculate         = Calculate.OnBarClose;
                IsOverlay         = true;
                DisplayInDataBox  = false;
                PaintPriceMarkers = false;

                ShowInsideBars    = true;
                InsideBarColor    = Brushes.Yellow;
                MotherBarColor    = Brushes.Purple;
                MinMotherATR      = 0.75;
                ATRPeriod         = 14;
                ShowHTFBoundaries = true;
                BoundaryOpacity   = 8;
                HTFType           = BarsPeriodType.Day;
                HTFValue          = 1;
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
                insideBarFlags = new Series<bool>(this);
                motherHighs    = new Series<double>(this);
                motherLows     = new Series<double>(this);
                atr            = ATR(ATRPeriod);

                if (isMTF)
                {
                    htfATR          = ATR(BarsArray[1], ATRPeriod);
                    htfPrevIsInside = false;
                    htfPrevMotherHigh = 0;
                    htfPrevMotherLow  = 0;
                    htfAlternate    = false;

                    byte alpha = (byte)(255 * BoundaryOpacity / 100);
                    htfBoundaryBrushFrozen = new SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(alpha, 255, 255, 255));
                    htfBoundaryBrushFrozen.Freeze();
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (isMTF)
            {
                if (BarsInProgress == 1)
                    OnHTFBar();
                else if (BarsInProgress == 0)
                    OnLTFBar();
            }
            else
            {
                if (BarsInProgress != 0) return;
                OnNativeBar();
            }
        }

        #region Native mode

        private void OnNativeBar()
        {
            if (CurrentBar < ATRPeriod + 1)
            {
                insideBarFlags[0] = false;
                motherHighs[0] = 0;
                motherLows[0]  = 0;
                return;
            }

            double mHigh = insideBarFlags[1] ? motherHighs[1] : Highs[0][1];
            double mLow  = insideBarFlags[1] ? motherLows[1]  : Lows[0][1];

            bool isIB = Highs[0][0] <= mHigh && Lows[0][0] >= mLow;

            if (isIB && MinMotherATR > 0 && !insideBarFlags[1])
            {
                if ((mHigh - mLow) < MinMotherATR * atr[0])
                    isIB = false;
            }

            insideBarFlags[0] = isIB;

            if (isIB)
            {
                motherHighs[0] = mHigh;
                motherLows[0]  = mLow;

                if (ShowInsideBars)
                {
                    BarBrushes[0]           = InsideBarColor;
                    CandleOutlineBrushes[0] = InsideBarColor;

                    if (!insideBarFlags[1])
                    {
                        BarBrushes[1]           = MotherBarColor;
                        CandleOutlineBrushes[1] = MotherBarColor;
                    }
                }
            }
            else
            {
                motherHighs[0] = 0;
                motherLows[0]  = 0;
            }
        }

        #endregion

        #region MTF — HTF bar detection

        private void OnHTFBar()
        {
            if (CurrentBars[1] < 2) return;

            // Alternating background for HTF bar boundaries
            htfAlternate = !htfAlternate;
            if (ShowHTFBoundaries && htfAlternate)
            {
                ColorLTFBackgroundInRange(Times[1][1], Times[1][0], htfBoundaryBrushFrozen);
            }

            if (CurrentBars[1] < ATRPeriod + 1) return;

            double mHigh = htfPrevIsInside ? htfPrevMotherHigh : Highs[1][1];
            double mLow  = htfPrevIsInside ? htfPrevMotherLow  : Lows[1][1];

            bool isIB = Highs[1][0] <= mHigh && Lows[1][0] >= mLow;

            if (isIB && MinMotherATR > 0 && !htfPrevIsInside)
            {
                if ((mHigh - mLow) < MinMotherATR * htfATR[0])
                    isIB = false;
            }

            if (isIB)
            {
                if (ShowInsideBars)
                {
                    // Color all LTF bars within this inside bar's time range
                    ColorLTFBarsInRange(Times[1][1], Times[1][0], InsideBarColor);

                    // Color mother bar's LTF bars (only the first inside bar triggers this)
                    if (CurrentBars[1] >= 3 && !htfPrevIsInside)
                        ColorLTFBarsInRange(Times[1][2], Times[1][1], MotherBarColor);
                }

                htfPrevIsInside    = true;
                htfPrevMotherHigh  = mHigh;
                htfPrevMotherLow   = mLow;
            }
            else
            {
                htfPrevIsInside   = false;
                htfPrevMotherHigh = 0;
                htfPrevMotherLow  = 0;
            }
        }

        private void ColorLTFBarsInRange(DateTime rangeStart, DateTime rangeEnd, Brush color)
        {
            for (int i = 0; i < CurrentBars[0]; i++)
            {
                DateTime t = Times[0][i];
                if (t > rangeEnd) continue;
                if (t <= rangeStart) break;

                BarBrushes[i]           = color;
                CandleOutlineBrushes[i] = color;
            }
        }

        private void ColorLTFBackgroundInRange(DateTime rangeStart, DateTime rangeEnd, Brush color)
        {
            for (int i = 0; i < CurrentBars[0]; i++)
            {
                DateTime t = Times[0][i];
                if (t > rangeEnd) continue;
                if (t <= rangeStart) break;

                BackBrushes[i] = color;
            }
        }

        #endregion

        #region MTF — LTF bar projection

        private void OnLTFBar()
        {
            // Coloring is handled retroactively in OnHTFBar
            // Just maintain the public API series
            insideBarFlags[0] = false;
            motherHighs[0]    = 0;
            motherLows[0]     = 0;
        }

        #endregion

        #region Helpers

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

        public bool IsInsideBar(int barsAgo)
        {
            if (barsAgo < 0 || barsAgo >= CurrentBar) return false;
            return insideBarFlags[barsAgo];
        }

        public double MotherBarHigh(int barsAgo)
        {
            if (barsAgo < 0 || barsAgo >= CurrentBar) return 0;
            return motherHighs[barsAgo];
        }

        public double MotherBarLow(int barsAgo)
        {
            if (barsAgo < 0 || barsAgo >= CurrentBar) return 0;
            return motherLows[barsAgo];
        }

        #endregion
    }
}
