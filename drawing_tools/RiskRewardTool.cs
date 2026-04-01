#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using SharpDX;
using SharpDX.Direct2D1;
using SharpDX.DirectWrite;
#endregion

namespace NinjaTrader.NinjaScript.DrawingTools
{
    public class AccountNameConverter : TypeConverter
    {
        public override bool GetStandardValuesSupported(ITypeDescriptorContext context) { return true; }
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) { return false; }

        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
        {
            var names = new List<string>();
            try
            {
                lock (Account.All)
                {
                    foreach (Account acct in Account.All)
                        names.Add(acct.Name);
                }
            }
            catch { }
            if (names.Count == 0) names.Add("Sim101");
            return new StandardValuesCollection(names);
        }
    }

    public class RiskRewardTool : FibonacciRetracements
    {
        // SharpDX resources
        private SharpDX.Direct2D1.SolidColorBrush riskFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush rewardFillBrush;
        private SharpDX.Direct2D1.SolidColorBrush labelBrush;
        private SharpDX.Direct2D1.SolidColorBrush entryBrush;
        private SharpDX.Direct2D1.SolidColorBrush entryArmedBrush;
        private TextFormat labelFormat;
        private TextFormat armedLabelFormat;

        [Display(Name = "Armed", GroupName = "RR Tool", Order = 1,
                 Description = "When checked, DSB strategy will trade this setup")]
        public bool Armed { get; set; }

        [Display(Name = "Risk Color", GroupName = "RR Tool", Order = 10)]
        [XmlIgnore]
        public System.Windows.Media.Brush RiskColor { get; set; }

        [Browsable(false)]
        public string RiskColorSerializable
        {
            get { return Serialize.BrushToString(RiskColor); }
            set { RiskColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Reward Color", GroupName = "RR Tool", Order = 11)]
        [XmlIgnore]
        public System.Windows.Media.Brush RewardColor { get; set; }

        [Browsable(false)]
        public string RewardColorSerializable
        {
            get { return Serialize.BrushToString(RewardColor); }
            set { RewardColor = Serialize.StringToBrush(value); }
        }

        [Display(Name = "Risk Opacity %", GroupName = "RR Tool", Order = 12)]
        [Range(5, 50)]
        public int RiskOpacity { get; set; }

        [Display(Name = "Reward Opacity %", GroupName = "RR Tool", Order = 13)]
        [Range(5, 50)]
        public int RewardOpacity { get; set; }

        [Display(Name = "Account Name", GroupName = "Position Sizing", Order = 20,
                 Description = "Select the trading account for position sizing")]
        [TypeConverter(typeof(AccountNameConverter))]
        public string AccountName { get; set; }

        [Display(Name = "Risk %", GroupName = "Position Sizing", Order = 21,
                 Description = "Percentage of account equity to risk per trade")]
        [Range(0.1, 10.0)]
        public double RiskPercent { get; set; }

        protected override void OnStateChange()
        {
            base.OnStateChange();

            if (State == State.SetDefaults)
            {
                Name         = "RR Tool";
                Description  = "Risk:Reward tool — anchor 1 = Stop Loss, anchor 2 = Entry, extensions show R:R levels";

                Armed        = false;
                RiskColor    = System.Windows.Media.Brushes.IndianRed;
                RewardColor  = System.Windows.Media.Brushes.MediumSeaGreen;
                RiskOpacity  = 15;
                RewardOpacity = 10;
                AccountName  = "Sim101";
                RiskPercent  = 1.0;

                PriceLevels.Clear();

                var hidden = System.Windows.Media.Brushes.Transparent;
                PriceLevels.Add(new PriceLevel(0,    hidden, 0));  // SL
                PriceLevels.Add(new PriceLevel(100,  hidden, 0));  // Entry
                PriceLevels.Add(new PriceLevel(200,  hidden, 0));  // 1:1
                PriceLevels.Add(new PriceLevel(300,  hidden, 0));  // 1:2
                PriceLevels.Add(new PriceLevel(400,  hidden, 0));  // 1:3
                PriceLevels.Add(new PriceLevel(500,  hidden, 0));  // 1:4
                PriceLevels.Add(new PriceLevel(600,  hidden, 0));  // 1:5
                PriceLevels.Add(new PriceLevel(700,  hidden, 0));  // 1:6

                IsExtendedLinesLeft  = false;
                IsExtendedLinesRight = false;
            }
            else if (State == State.Terminated)
            {
                DisposeDX();
            }
        }

        private void DisposeDX()
        {
            if (riskFillBrush != null)    { riskFillBrush.Dispose(); riskFillBrush = null; }
            if (rewardFillBrush != null)  { rewardFillBrush.Dispose(); rewardFillBrush = null; }
            if (labelBrush != null)       { labelBrush.Dispose(); labelBrush = null; }
            if (entryBrush != null)       { entryBrush.Dispose(); entryBrush = null; }
            if (entryArmedBrush != null)  { entryArmedBrush.Dispose(); entryArmedBrush = null; }
            if (labelFormat != null)      { labelFormat.Dispose(); labelFormat = null; }
            if (armedLabelFormat != null)  { armedLabelFormat.Dispose(); armedLabelFormat = null; }
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDX();

            if (RenderTarget != null)
            {
                var rc = ((System.Windows.Media.SolidColorBrush)RiskColor).Color;
                var gc = ((System.Windows.Media.SolidColorBrush)RewardColor).Color;

                riskFillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new Color4(rc.R / 255f, rc.G / 255f, rc.B / 255f, RiskOpacity / 100f));
                rewardFillBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new Color4(gc.R / 255f, gc.G / 255f, gc.B / 255f, RewardOpacity / 100f));
                labelBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new Color4(1f, 1f, 1f, 0.85f));
                entryBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new Color4(1f, 1f, 0f, 0.6f));
                entryArmedBrush = new SharpDX.Direct2D1.SolidColorBrush(RenderTarget,
                    new Color4(1f, 1f, 0f, 1f));

                var factory = NinjaTrader.Core.Globals.DirectWriteFactory;
                labelFormat = new TextFormat(factory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Normal,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 11f);
                armedLabelFormat = new TextFormat(factory, "Arial",
                    SharpDX.DirectWrite.FontWeight.Bold,
                    SharpDX.DirectWrite.FontStyle.Normal,
                    SharpDX.DirectWrite.FontStretch.Normal, 12f);
            }
        }

        public override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            base.OnRender(chartControl, chartScale);

            if (RenderTarget == null || Anchors == null) return;

            var anchorList = Anchors.ToArray();
            if (anchorList.Length < 2) return;

            double price0 = anchorList[0].Price;  // SL
            double price1 = anchorList[1].Price;  // Entry

            double risk = Math.Abs(price1 - price0);
            if (risk <= 0) return;
            if (riskFillBrush == null) return;

            double direction = price1 - price0;

            float x1 = (float)anchorList[0].GetPoint(chartControl, ChartPanel, chartScale).X;
            float x2 = (float)anchorList[1].GetPoint(chartControl, ChartPanel, chartScale).X;
            float xLeft  = Math.Min(x1, x2);
            float xRight = Math.Max(x1, x2);
            if (xRight - xLeft < 40) xRight = xLeft + 40;

            float ySL    = chartScale.GetYByValue(price0);
            float yEntry = chartScale.GetYByValue(price1);

            // ── Shade risk zone (SL → Entry) ────────────────────────────
            float top    = Math.Min(ySL, yEntry);
            float bottom = Math.Max(ySL, yEntry);
            RenderTarget.FillRectangle(
                new RectangleF(xLeft, top, xRight - xLeft, bottom - top),
                riskFillBrush);

            // ── Draw SL and Entry lines ──────────────────────────────────
            var activeEntryBrush = Armed ? entryArmedBrush : entryBrush;
            float entryLineWidth = Armed ? 3f : 2f;

            RenderTarget.DrawLine(
                new Vector2(xLeft, ySL), new Vector2(xRight, ySL),
                riskFillBrush, 2f);
            RenderTarget.DrawLine(
                new Vector2(xLeft, yEntry), new Vector2(xRight, yEntry),
                activeEntryBrush, entryLineWidth);

            // ── Custom labels ───────────────────────────────────────────
            float labelX = xRight + 8;

            DrawRRLabel(string.Format("SL  ({0:F2})  -1R", price0),
                labelX, ySL - 8, labelBrush);

            string entryLabel = Armed
                ? string.Format("ARMED  Entry  ({0:F2})", price1)
                : string.Format("Entry  ({0:F2})", price1);
            var entryLabelFormat = Armed ? armedLabelFormat : labelFormat;
            DrawRRLabel(entryLabel, labelX, yEntry - 8, activeEntryBrush, entryLabelFormat);

            // ── Position sizing from account ────────────────────────────
            string sizingLabel = "";
            try
            {
                var chartBars = GetAttachedToChartBars();
                if (chartBars != null && chartBars.Bars != null && chartBars.Bars.Instrument != null)
                {
                    double pointValue = chartBars.Bars.Instrument.MasterInstrument.PointValue;
                    double riskPerContract = risk * pointValue;

                    if (riskPerContract > 0 && !string.IsNullOrEmpty(AccountName))
                    {
                        var account = Account.All.FirstOrDefault(a => a.Name == AccountName);
                        if (account != null)
                        {
                            double equity = account.Get(AccountItem.CashValue, Currency.UsDollar);
                            double riskAmount = equity * RiskPercent / 100.0;
                            int contracts = (int)Math.Floor(riskAmount / riskPerContract);
                            double actualRisk = contracts * riskPerContract;
                            double actualPct = equity > 0 ? (actualRisk / equity * 100.0) : 0;

                            if (contracts >= 1)
                                sizingLabel = string.Format("{0} contracts  |  Risk: ${1:F0} ({2:F2}%)  |  Equity: ${3:F0}",
                                    contracts, actualRisk, actualPct, equity);
                            else
                                sizingLabel = string.Format("< 1 contract  |  Need ${0:F0}, have ${1:F0} at {2}%",
                                    riskPerContract, riskAmount, RiskPercent);
                        }
                        else
                        {
                            sizingLabel = string.Format("Account '{0}' not found", AccountName);
                        }
                    }
                }
            }
            catch { sizingLabel = ""; }

            if (!string.IsNullOrEmpty(sizingLabel))
            {
                bool slIsBelow = ySL > yEntry;
                float sizingY = slIsBelow ? ySL + 4 : ySL - 20;
                DrawRRLabel(sizingLabel, labelX, sizingY, activeEntryBrush);
            }

            // ── Shade each R:R level + labels ───────────────────────────
            float yPrev = yEntry;
            for (int r = 1; r <= 6; r++)
            {
                double targetPrice = price1 + direction * r;
                float yTarget = chartScale.GetYByValue(targetPrice);

                float sTop = Math.Min(yPrev, yTarget);
                float sBot = Math.Max(yPrev, yTarget);
                RenderTarget.FillRectangle(
                    new RectangleF(xLeft, sTop, xRight - xLeft, sBot - sTop),
                    rewardFillBrush);

                RenderTarget.DrawLine(
                    new Vector2(xLeft, yTarget), new Vector2(xRight, yTarget),
                    labelBrush, 0.5f);

                DrawRRLabel(string.Format("1:{0}  ({1:F2})  +{0}R", r, targetPrice),
                    labelX, yTarget - 8, labelBrush);

                yPrev = yTarget;
            }
        }

        private void DrawRRLabel(string text, float x, float y,
            SharpDX.Direct2D1.Brush brush, TextFormat format = null)
        {
            var fmt = format ?? labelFormat;
            if (fmt == null || RenderTarget == null) return;
            var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                text, fmt, 350, 18);
            RenderTarget.DrawTextLayout(new Vector2(x, y), layout, brush);
            layout.Dispose();
        }

        #region Public helpers (called by DSB strategy)

        public double GetSLPrice()
        {
            var anchors = Anchors.ToArray();
            return anchors.Length >= 2 ? anchors[0].Price : 0;
        }

        public double GetEntryPrice()
        {
            var anchors = Anchors.ToArray();
            return anchors.Length >= 2 ? anchors[1].Price : 0;
        }

        public bool IsLong()
        {
            return GetEntryPrice() > GetSLPrice();
        }

        public double GetRisk()
        {
            return Math.Abs(GetEntryPrice() - GetSLPrice());
        }

        public double GetRRLevelPrice(int level)
        {
            double sl    = GetSLPrice();
            double entry = GetEntryPrice();
            double dir   = entry - sl;
            return entry + dir * level;
        }

        public int GetContracts()
        {
            double risk = GetRisk();
            if (risk <= 0 || string.IsNullOrEmpty(AccountName)) return 0;

            var chartBars = GetAttachedToChartBars();
            if (chartBars == null || chartBars.Bars == null || chartBars.Bars.Instrument == null)
                return 0;

            double pointValue      = chartBars.Bars.Instrument.MasterInstrument.PointValue;
            double riskPerContract = risk * pointValue;
            if (riskPerContract <= 0) return 0;

            Account account;
            lock (Account.All)
                account = Account.All.FirstOrDefault(a => a.Name == AccountName);
            if (account == null) return 0;

            double equity     = account.Get(AccountItem.CashValue, Currency.UsDollar);
            double riskAmount = equity * RiskPercent / 100.0;
            return (int)Math.Floor(riskAmount / riskPerContract);
        }

        #endregion

        #region Static Draw (for programmatic placement by strategy)

        public static RiskRewardTool Draw(NinjaScriptBase owner, string tag,
            int startBarsAgo, double slPrice, int endBarsAgo, double entryPrice,
            bool armed)
        {
            var tool = DrawingTool.DrawToToggleObject<RiskRewardTool>(owner, tag,
                false, startBarsAgo, slPrice, endBarsAgo, entryPrice);
            if (tool != null)
                tool.Armed = armed;
            return tool;
        }

        #endregion
    }
}
