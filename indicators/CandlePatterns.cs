#region Using declarations
using System;
using System.Collections.Generic;
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
    public class CandlePatterns : Indicator
    {
        // Track inside bar status per bar for public API
        private Series<bool> insideBarFlags;
        private Series<double> motherHighs;
        private Series<double> motherLows;
        private ATR atr;

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

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name              = "Candle Patterns";
                Description       = "Highlights candle patterns — inside bars, with more patterns to come";
                Calculate         = Calculate.OnBarClose;
                IsOverlay         = true;
                DisplayInDataBox  = false;
                PaintPriceMarkers = false;

                ShowInsideBars    = true;
                InsideBarColor    = Brushes.Yellow;
                MotherBarColor    = Brushes.Purple;
                MinMotherATR      = 0.75;
                ATRPeriod         = 14;
            }
            else if (State == State.DataLoaded)
            {
                insideBarFlags = new Series<bool>(this);
                motherHighs    = new Series<double>(this);
                motherLows     = new Series<double>(this);
                atr            = ATR(ATRPeriod);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < ATRPeriod + 1)
            {
                insideBarFlags[0] = false;
                motherHighs[0]    = 0;
                motherLows[0]     = 0;
                return;
            }

            // Determine the active mother bar range
            double mHigh = insideBarFlags[1] ? motherHighs[1] : High[1];
            double mLow  = insideBarFlags[1] ? motherLows[1]  : Low[1];

            // Inside bar = range contained within the MOTHER bar
            bool isIB = High[0] <= mHigh && Low[0] >= mLow;

            // Filter: mother bar must be significant (>= MinMotherATR × ATR)
            if (isIB && MinMotherATR > 0 && !insideBarFlags[1])
            {
                double motherRange = mHigh - mLow;
                if (motherRange < MinMotherATR * atr[0])
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

                    // Color the mother bar (only the true mother, not if previous was also inside)
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

        #region Public API (for DSB strategy)

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
