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
using System.Windows.Input;
#endregion

namespace NinjaTrader.NinjaScript.DrawingTools
{
    public enum RRTradeState
    {
        Unarmed,
        Preview,
        Confirmed,
        Pending,
        Live,
        Closed
    }

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
        private SharpDX.Direct2D1.SolidColorBrush entryActiveBrush;
        private SharpDX.Direct2D1.SolidColorBrush trailSLBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnUnarmedBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnConfirmBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnPendingBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnLiveBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnClosedBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnProfitBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnLossBgBrush;
        private SharpDX.Direct2D1.SolidColorBrush btnTextBrush;
        private SharpDX.Direct2D1.SolidColorBrush dimBrush;
        private SharpDX.Direct2D1.SolidColorBrush filledCheckBrush;
        private TextFormat labelFormat;
        private TextFormat boldFormat;
        private TextFormat btnTextFormat;
        private TextFormat infoFormat;

        // ARM button hit-test rect (screen coords, updated each render)
        private RectangleF armButtonRect;

        #region Properties

        [Display(Name = "Trade State", GroupName = "RR Tool", Order = 1)]
        public RRTradeState TradeState { get; set; }

        // Backward compat — DSB scans for Armed == true (Confirmed state)
        [Browsable(false)]
        public bool Armed { get { return TradeState == RRTradeState.Confirmed; } }

        // Status properties written by DSB strategy
        [Browsable(false)]
        public double CurrentSLPrice { get; set; }

        [Browsable(false)]
        public int FilledTPCount { get; set; }

        [Browsable(false)]
        public double RealizedPnL { get; set; }

        [Browsable(false)]
        public int ActiveContracts { get; set; }

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

        #endregion

        protected override void OnStateChange()
        {
            base.OnStateChange();

            if (State == State.SetDefaults)
            {
                Name         = "RR Tool";
                Description  = "Risk:Reward tool — anchor 1 = Stop Loss, anchor 2 = Entry, extensions show R:R levels";

                TradeState   = RRTradeState.Unarmed;
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

        #region DX resource management

        private void DisposeDX()
        {
            var brushes = new IDisposable[] {
                riskFillBrush, rewardFillBrush, labelBrush, entryBrush, entryActiveBrush,
                trailSLBrush, btnUnarmedBgBrush, btnConfirmBgBrush, btnPendingBgBrush,
                btnLiveBgBrush, btnClosedBgBrush, btnProfitBgBrush, btnLossBgBrush,
                btnTextBrush, dimBrush, filledCheckBrush,
                labelFormat, boldFormat, btnTextFormat, infoFormat
            };
            foreach (var b in brushes)
                if (b != null) b.Dispose();

            riskFillBrush = null; rewardFillBrush = null; labelBrush = null;
            entryBrush = null; entryActiveBrush = null; trailSLBrush = null;
            btnUnarmedBgBrush = null; btnConfirmBgBrush = null; btnPendingBgBrush = null;
            btnLiveBgBrush = null; btnClosedBgBrush = null; btnProfitBgBrush = null;
            btnLossBgBrush = null; btnTextBrush = null; dimBrush = null;
            filledCheckBrush = null;
            labelFormat = null; boldFormat = null; btnTextFormat = null; infoFormat = null;
        }

        public override void OnRenderTargetChanged()
        {
            DisposeDX();

            if (RenderTarget != null)
            {
                var rc = ((System.Windows.Media.SolidColorBrush)RiskColor).Color;
                var gc = ((System.Windows.Media.SolidColorBrush)RewardColor).Color;

                riskFillBrush     = MakeBrush(rc.R / 255f, rc.G / 255f, rc.B / 255f, RiskOpacity / 100f);
                rewardFillBrush   = MakeBrush(gc.R / 255f, gc.G / 255f, gc.B / 255f, RewardOpacity / 100f);
                labelBrush        = MakeBrush(1f, 1f, 1f, 0.85f);
                entryBrush        = MakeBrush(1f, 1f, 0f, 0.6f);
                entryActiveBrush  = MakeBrush(1f, 1f, 0f, 1f);
                trailSLBrush      = MakeBrush(0f, 0.8f, 1f, 0.9f);     // cyan
                btnUnarmedBgBrush = MakeBrush(1f, 1f, 1f, 0.15f);
                btnConfirmBgBrush = MakeBrush(1f, 1f, 0f, 0.3f);       // yellow tint
                btnPendingBgBrush = MakeBrush(0.2f, 0.5f, 1f, 0.85f);  // blue
                btnLiveBgBrush    = MakeBrush(0f, 0.8f, 0f, 0.85f);    // green
                btnClosedBgBrush  = MakeBrush(0.4f, 0.4f, 0.4f, 0.6f); // gray
                btnProfitBgBrush  = MakeBrush(0f, 0.7f, 0f, 0.85f);    // green
                btnLossBgBrush    = MakeBrush(0.8f, 0.15f, 0.15f, 0.85f); // red
                btnTextBrush      = MakeBrush(1f, 1f, 1f, 0.95f);
                dimBrush          = MakeBrush(1f, 1f, 1f, 0.3f);
                filledCheckBrush  = MakeBrush(0f, 1f, 0f, 0.9f);

                var factory = NinjaTrader.Core.Globals.DirectWriteFactory;
                labelFormat   = MakeTextFormat(factory, SharpDX.DirectWrite.FontWeight.Normal, 11f);
                boldFormat    = MakeTextFormat(factory, SharpDX.DirectWrite.FontWeight.Bold, 12f);
                btnTextFormat = MakeTextFormat(factory, SharpDX.DirectWrite.FontWeight.Bold, 10f);
                infoFormat    = MakeTextFormat(factory, SharpDX.DirectWrite.FontWeight.Normal, 10f);
            }
        }

        private SharpDX.Direct2D1.SolidColorBrush MakeBrush(float r, float g, float b, float a)
        {
            return new SharpDX.Direct2D1.SolidColorBrush(RenderTarget, new Color4(r, g, b, a));
        }

        private TextFormat MakeTextFormat(SharpDX.DirectWrite.Factory factory,
            SharpDX.DirectWrite.FontWeight weight, float size)
        {
            return new TextFormat(factory, "Arial", weight,
                SharpDX.DirectWrite.FontStyle.Normal,
                SharpDX.DirectWrite.FontStretch.Normal, size);
        }

        #endregion

        #region Rendering

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
            bool isActive = TradeState != RRTradeState.Unarmed;
            bool isClosed = TradeState == RRTradeState.Closed;

            float x1 = (float)anchorList[0].GetPoint(chartControl, ChartPanel, chartScale).X;
            float x2 = (float)anchorList[1].GetPoint(chartControl, ChartPanel, chartScale).X;
            float xLeft  = Math.Min(x1, x2);
            float xRight = Math.Max(x1, x2);
            if (xRight - xLeft < 40) xRight = xLeft + 40;

            float ySL    = chartScale.GetYByValue(price0);
            float yEntry = chartScale.GetYByValue(price1);
            float labelX = xRight + 8;

            // ── Shade risk zone (SL → Entry) ────────────────────────────
            float top    = Math.Min(ySL, yEntry);
            float bottom = Math.Max(ySL, yEntry);
            RenderTarget.FillRectangle(
                new RectangleF(xLeft, top, xRight - xLeft, bottom - top),
                isClosed ? dimBrush : riskFillBrush);

            // ── SL and Entry lines ──────────────────────────────────────
            var activeEntryBrush = isActive ? entryActiveBrush : entryBrush;
            float entryLineWidth = isActive ? 3f : 2f;

            RenderTarget.DrawLine(
                new Vector2(xLeft, ySL), new Vector2(xRight, ySL),
                isClosed ? dimBrush : riskFillBrush, 2f);
            RenderTarget.DrawLine(
                new Vector2(xLeft, yEntry), new Vector2(xRight, yEntry),
                isClosed ? dimBrush : activeEntryBrush, entryLineWidth);

            // ── Labels ──────────────────────────────────────────────────
            var slLabelBrush   = isClosed ? dimBrush : labelBrush;
            var entryLabelBrush = isClosed ? dimBrush : activeEntryBrush;

            DrawRRLabel(string.Format("SL  ({0:F2})  -1R", price0),
                labelX, ySL - 8, slLabelBrush);
            DrawRRLabel(string.Format("Entry  ({0:F2})", price1),
                labelX, yEntry - 8, entryLabelBrush);

            // ── Trailing SL line (Live state) ───────────────────────────
            if (TradeState == RRTradeState.Live && CurrentSLPrice > 0)
            {
                float yTrailSL = chartScale.GetYByValue(CurrentSLPrice);
                RenderTarget.DrawLine(
                    new Vector2(xLeft, yTrailSL), new Vector2(xRight, yTrailSL),
                    trailSLBrush, 2f);
                DrawRRLabel(string.Format("SL  ({0:F2})", CurrentSLPrice),
                    labelX, yTrailSL - 8, trailSLBrush);
            }

            // ── Button ──────────────────────────────────────────────────
            RenderButton(labelX, yEntry);

            // ── Preview info line ───────────────────────────────────────
            if (TradeState == RRTradeState.Preview || TradeState == RRTradeState.Confirmed)
            {
                int cts = GetContracts();
                string info = string.Format("{0} contracts  |  Levels 1R-6R", cts);
                float infoY = yEntry + (direction > 0 ? -24 : 12);
                DrawRRLabel(info, labelX, infoY, entryActiveBrush, infoFormat);
            }

            // ── Position sizing (Unarmed/Preview only) ──────────────────
            if (TradeState == RRTradeState.Unarmed || TradeState == RRTradeState.Preview)
                RenderSizingLabel(risk, labelX, ySL, yEntry, activeEntryBrush);

            // ── R:R level zones + labels ────────────────────────────────
            float yPrev = yEntry;
            for (int r = 1; r <= 6; r++)
            {
                double targetPrice = price1 + direction * r;
                float yTarget = chartScale.GetYByValue(targetPrice);

                float sTop = Math.Min(yPrev, yTarget);
                float sBot = Math.Max(yPrev, yTarget);

                var zoneBrush = isClosed ? dimBrush : rewardFillBrush;
                RenderTarget.FillRectangle(
                    new RectangleF(xLeft, sTop, xRight - xLeft, sBot - sTop),
                    zoneBrush);

                var lineBrush = isClosed ? dimBrush : labelBrush;
                RenderTarget.DrawLine(
                    new Vector2(xLeft, yTarget), new Vector2(xRight, yTarget),
                    lineBrush, 0.5f);

                // Label with filled indicator
                bool filled = r <= FilledTPCount;
                string lvlLabel = filled
                    ? string.Format("1:{0}  ({1:F2})  +{0}R  FILLED", r, targetPrice)
                    : string.Format("1:{0}  ({1:F2})  +{0}R", r, targetPrice);

                var lvlBrush = filled ? filledCheckBrush : (isClosed ? dimBrush : labelBrush);
                DrawRRLabel(lvlLabel, labelX, yTarget - 8, lvlBrush);

                yPrev = yTarget;
            }
        }

        private void RenderButton(float labelX, float yEntry)
        {
            string btnText;
            SharpDX.Direct2D1.SolidColorBrush bgBrush;
            SharpDX.Direct2D1.SolidColorBrush borderBrush;
            float btnW;

            switch (TradeState)
            {
                case RRTradeState.Unarmed:
                    btnText = "ARM";    bgBrush = btnUnarmedBgBrush; borderBrush = entryBrush; btnW = 46; break;
                case RRTradeState.Preview:
                    btnText = "CONFIRM"; bgBrush = btnConfirmBgBrush; borderBrush = entryActiveBrush; btnW = 76; break;
                case RRTradeState.Confirmed:
                    btnText = "WAITING"; bgBrush = btnConfirmBgBrush; borderBrush = entryActiveBrush; btnW = 72; break;
                case RRTradeState.Pending:
                    btnText = "PENDING"; bgBrush = btnPendingBgBrush; borderBrush = btnPendingBgBrush; btnW = 76; break;
                case RRTradeState.Live:
                    btnText = string.Format("LIVE ({0})", ActiveContracts);
                    bgBrush = btnLiveBgBrush; borderBrush = btnLiveBgBrush; btnW = 80; break;
                case RRTradeState.Closed:
                    btnText = string.Format("{0}{1:F0}", RealizedPnL >= 0 ? "+$" : "-$", Math.Abs(RealizedPnL));
                    bgBrush = RealizedPnL >= 0 ? btnProfitBgBrush : btnLossBgBrush;
                    borderBrush = bgBrush; btnW = 80; break;
                default:
                    btnText = "ARM";    bgBrush = btnUnarmedBgBrush; borderBrush = entryBrush; btnW = 46; break;
            }

            float btnX = labelX + 160;
            float btnY = yEntry - 12;
            float btnH = 20;
            armButtonRect = new RectangleF(btnX, btnY, btnW, btnH);

            var rounded = new RoundedRectangle { Rect = armButtonRect, RadiusX = 3, RadiusY = 3 };
            RenderTarget.FillRoundedRectangle(rounded, bgBrush);
            RenderTarget.DrawRoundedRectangle(rounded, borderBrush, 1f);

            if (btnTextFormat != null)
            {
                var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                    btnText, btnTextFormat, btnW, btnH);
                layout.TextAlignment     = SharpDX.DirectWrite.TextAlignment.Center;
                layout.ParagraphAlignment = SharpDX.DirectWrite.ParagraphAlignment.Center;
                RenderTarget.DrawTextLayout(new Vector2(btnX, btnY), layout, btnTextBrush);
                layout.Dispose();
            }
        }

        private void RenderSizingLabel(double risk, float labelX, float ySL, float yEntry,
            SharpDX.Direct2D1.Brush brush)
        {
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
                            sizingLabel = string.Format("Account '{0}' not found", AccountName);
                    }
                }
            }
            catch { sizingLabel = ""; }

            if (!string.IsNullOrEmpty(sizingLabel))
            {
                bool slIsBelow = ySL > yEntry;
                float sizingY = slIsBelow ? ySL + 4 : ySL - 20;
                DrawRRLabel(sizingLabel, labelX, sizingY, brush);
            }
        }

        private void DrawRRLabel(string text, float x, float y,
            SharpDX.Direct2D1.Brush brush, TextFormat format = null)
        {
            var fmt = format ?? labelFormat;
            if (fmt == null || RenderTarget == null) return;
            var layout = new TextLayout(NinjaTrader.Core.Globals.DirectWriteFactory,
                text, fmt, 400, 18);
            RenderTarget.DrawTextLayout(new Vector2(x, y), layout, brush);
            layout.Dispose();
        }

        #endregion

        #region Mouse interaction

        public override void OnMouseDown(ChartControl chartControl, ChartPanel chartPanel,
            ChartScale chartScale, ChartAnchor dataPoint)
        {
            if (armButtonRect.Width > 0)
            {
                System.Windows.Point clickPt = dataPoint.GetPoint(chartControl, chartPanel, chartScale);
                if (armButtonRect.Contains((float)clickPt.X, (float)clickPt.Y))
                {
                    switch (TradeState)
                    {
                        case RRTradeState.Unarmed:
                            TradeState = RRTradeState.Preview;
                            break;
                        case RRTradeState.Preview:
                            TradeState = RRTradeState.Confirmed;
                            break;
                        case RRTradeState.Confirmed:
                        case RRTradeState.Pending:
                            TradeState = RRTradeState.Unarmed; // cancel
                            break;
                        case RRTradeState.Live:
                            break; // no-op while in position
                        case RRTradeState.Closed:
                            TradeState = RRTradeState.Unarmed; // reset
                            FilledTPCount  = 0;
                            ActiveContracts = 0;
                            RealizedPnL    = 0;
                            CurrentSLPrice = 0;
                            break;
                    }
                    chartControl.InvalidateVisual();
                    return;
                }
            }

            base.OnMouseDown(chartControl, chartPanel, chartScale, dataPoint);
        }

        public override Cursor GetCursor(ChartControl chartControl, ChartPanel chartPanel,
            ChartScale chartScale, System.Windows.Point point)
        {
            if (armButtonRect.Width > 0
                && armButtonRect.Contains((float)point.X, (float)point.Y)
                && TradeState != RRTradeState.Live)
                return Cursors.Hand;

            return base.GetCursor(chartControl, chartPanel, chartScale, point);
        }

        #endregion

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
    }
}
