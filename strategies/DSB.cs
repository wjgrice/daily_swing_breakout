#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum EntryType
    {
        Limit,
        Market,
        StopLimit
    }

    public enum TradeDirection
    {
        Both,
        LongsOnly,
        ShortsOnly
    }

    public class DSB : Strategy
    {
        private enum TradeState { Idle, PendingEntry, InPosition }

        private TradeState          state;
        private RiskRewardTool      activeRRTool;

        // Locked-in values at entry time
        private double              lockedEntryPrice;
        private double              actualFillPrice;
        private double              lockedSLPrice;
        private bool                lockedIsLong;
        private double[]            lockedRRLevels;
        private int                 lockedContracts;
        private double              pointValue;

        // Orders
        private Order               entryOrder;
        private Order               stopOrder;
        private int                 sumFilled;

        // Trailing state
        private int                 highestPassedLevel;

        // P&L tracking
        private double              realizedPnL;

        // Auto-detection state
        private Swing               htfSwing;          // daily swings for entry signals
        private Swing               ltfSwing;          // chart-TF swings for SL placement
        private ATR                 htfATR;
        private double              lastSwingHighPrice;
        private double              lastSwingLowPrice;
        private int                 lastSwingHighAbsBar;
        private int                 lastSwingLowAbsBar;
        private bool                swingHighViolated;
        private bool                swingLowViolated;
        private int                 lastSignalBar;

        // Trend filter
        private LinRegSlope         trendLinReg;

        // HTF inside bar tracking
        private bool                htfPrevIsInside;
        private double              htfPrevMotherHigh;
        private double              htfPrevMotherLow;
        private bool                wasInsideBar;
        private double              lastMotherHigh;
        private double              lastMotherLow;
        private double              lastTradedMotherHigh;
        private double              lastTradedMotherLow;
        private double              lastTradedSwingPrice;

        #region Strategy Properties — Manual Mode

        [Display(Name = "RR Tool Tag", GroupName = "1. Manual Mode", Order = 10,
                 Description = "Tag of a specific RR Tool to use (empty = auto-detect)")]
        public string RRToolTag { get; set; }

        [Display(Name = "Entry Type", GroupName = "1. Manual Mode", Order = 20)]
        public EntryType EntryType { get; set; }

        [Display(Name = "Entry Offset Ticks", GroupName = "1. Manual Mode", Order = 21)]
        [Range(0, 20)]
        public int EntryOffsetTicks { get; set; }

        #endregion

        #region Strategy Properties — Auto Detection

        [Display(Name = "Auto Detect", GroupName = "2. Auto Detection", Order = 1,
                 Description = "Enable automatic entry detection (for backtesting)")]
        public bool AutoDetect { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trade Direction", GroupName = "2. Auto Detection", Order = 2)]
        public TradeDirection Direction { get; set; }

        [Display(Name = "Enable Swing Entries", GroupName = "2. Auto Detection", Order = 3)]
        public bool EnableSwingEntries { get; set; }

        [Display(Name = "Enable Inside Bar Entries", GroupName = "2. Auto Detection", Order = 3)]
        public bool EnableInsideBarEntries { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Swing Strength", GroupName = "2. Auto Detection", Order = 10,
                 Description = "HTF swing strength for entry detection")]
        [Range(1, 50)]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LTF Swing Strength", GroupName = "2. Auto Detection", Order = 11,
                 Description = "Chart-TF swing strength for SL placement")]
        [Range(1, 50)]
        public int LTFSwingStrength { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LTF Bar Type", GroupName = "2. Auto Detection", Order = 12,
                 Description = "Bar type for SL swing detection (Minute, Day, etc.)")]
        public BarsPeriodType LTFBarType { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "LTF Bar Value", GroupName = "2. Auto Detection", Order = 13,
                 Description = "Bar period for SL swing detection (e.g., 15 for 15min)")]
        [Range(1, 1440)]
        public int LTFBarValue { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min SL ATR", GroupName = "2. Auto Detection", Order = 14,
                 Description = "Min SL distance as daily ATR multiple (falls back to next swing if too close)")]
        [Range(0.1, 3.0)]
        public double MinSLATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max SL ATR", GroupName = "2. Auto Detection", Order = 13,
                 Description = "Max SL distance as daily ATR multiple (skip trade if too far)")]
        [Range(0.5, 10.0)]
        public double MaxSLATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR Period", GroupName = "2. Auto Detection", Order = 14)]
        [Range(5, 100)]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min Swing ATR", GroupName = "2. Auto Detection", Order = 15,
                 Description = "Min swing size as ATR multiple")]
        [Range(0, 10.0)]
        public double MinSwingATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Min Mother ATR", GroupName = "2. Auto Detection", Order = 16,
                 Description = "Mother bar must be >= this ATR multiple")]
        [Range(0, 5.0)]
        public double MinMotherATR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Risk %", GroupName = "2. Auto Detection", Order = 20,
                 Description = "% of equity to risk per auto trade")]
        [Range(0.1, 10.0)]
        public double RiskPercent { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Cooldown Bars", GroupName = "2. Auto Detection", Order = 21,
                 Description = "Min bars between auto signals")]
        [Range(1, 200)]
        public int CooldownBars { get; set; }

        [Display(Name = "Use Trend Filter", GroupName = "2. Auto Detection", Order = 30)]
        public bool UseTrendFilter { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trend LinReg Period", GroupName = "2. Auto Detection", Order = 31,
                 Description = "LinReg slope period on daily bars")]
        [Range(5, 100)]
        public int TrendLinRegPeriod { get; set; }

        #endregion

        #region Strategy Properties — Trailing Stop

        [NinjaScriptProperty]
        [Display(Name = "BE Trigger (R)", GroupName = "3. Trailing Stop", Order = 1,
                 Description = "R multiple that triggers breakeven")]
        [Range(0.1, 6.0)]
        public double BETriggerR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Start (R)", GroupName = "3. Trailing Stop", Order = 2,
                 Description = "R multiple where trailing begins")]
        [Range(0.5, 6.0)]
        public double TrailStartR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Step (R)", GroupName = "3. Trailing Stop", Order = 3,
                 Description = "R step between trail levels")]
        [Range(0.1, 3.0)]
        public double TrailStepR { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Trail Offset (R)", GroupName = "3. Trailing Stop", Order = 4,
                 Description = "SL trails this far behind current level")]
        [Range(0.1, 3.0)]
        public double TrailOffsetR { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                 = "DSB";
                Description          = "Daily Swing Breakout — manual RR Tool + auto detection";
                Calculate            = Calculate.OnBarClose;
                IsUnmanaged          = true;
                EntriesPerDirection  = 1;
                EntryHandling        = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                IsOverlay            = true;

                // Manual mode
                RRToolTag            = string.Empty;
                EntryType            = EntryType.Limit;
                EntryOffsetTicks     = 0;

                // Auto detection (off by default for live)
                AutoDetect           = true;
                Direction            = TradeDirection.Both;
                EnableSwingEntries   = true;
                EnableInsideBarEntries = true;
                SwingStrength        = 3;
                LTFSwingStrength     = 3;
                LTFBarType           = BarsPeriodType.Minute;
                LTFBarValue          = 15;
                MinSLATR             = 0.3;
                MaxSLATR             = 3.0;
                ATRPeriod            = 10;
                MinSwingATR          = 0.85;
                MinMotherATR         = 0.75;
                RiskPercent          = 4.0;
                CooldownBars         = 3;
                UseTrendFilter       = true;
                TrendLinRegPeriod    = 20;

                // Trailing stop defaults
                BETriggerR           = 0.5;
                TrailStartR          = 1.5;
                TrailStepR           = 1.0;
                TrailOffsetR         = 0.6;
            }
            else if (State == State.Configure)
            {
                if (AutoDetect)
                {
                    AddDataSeries(BarsPeriodType.Day, 1);    // BarsArray[1] — HTF for entries
                    AddDataSeries(LTFBarType, LTFBarValue);  // BarsArray[2] — LTF for SL swings
                }
            }
            else if (State == State.DataLoaded)
            {
                state          = TradeState.Idle;
                lockedRRLevels = new double[7];
                lastSignalBar  = -1;

                if (AutoDetect)
                {
                    // Daily swings for entry signal detection
                    htfSwing = Swing(BarsArray[1], SwingStrength);
                    htfATR   = ATR(BarsArray[1], ATRPeriod);

                    // LTF (15min) swings for SL placement
                    ltfSwing = Swing(BarsArray[2], LTFSwingStrength);

                    // Daily trend filter
                    if (UseTrendFilter)
                        trendLinReg = LinRegSlope(BarsArray[1], TrendLinRegPeriod);
                }
            }
        }

        protected override void OnBarUpdate()
        {
            // For auto-detect: process HTF inside bars when daily bar updates
            if (AutoDetect && BarsInProgress == 1)
            {
                UpdateHTFInsideBarState();
                return;
            }

            if (BarsInProgress != 0) return; // ignore LTF (2) and any other series
            if (CurrentBar < 1) return;

            switch (state)
            {
                case TradeState.Idle:
                    if (AutoDetect)
                        ScanForAutoSetup();
                    else
                        ScanForConfirmedRRTool();
                    break;

                case TradeState.PendingEntry:
                    HandlePendingEntry();
                    break;

                case TradeState.InPosition:
                    TrailSL();
                    break;
            }
        }

        #region Idle — Manual RR Tool scan

        private void ScanForConfirmedRRTool()
        {
            var rrTool = FindConfirmedRRTool();
            if (rrTool == null) return;

            int contracts = rrTool.GetContracts();
            if (contracts < 1) return;

            activeRRTool = rrTool;
            pointValue   = Instrument.MasterInstrument.PointValue;
            realizedPnL  = 0;
            highestPassedLevel = 0;
            LockLevels(rrTool, contracts);

            Print(string.Format("DSB: {0} {1} | {2} cts @ {3:F2} | SL {4:F2}",
                lockedIsLong ? "LONG" : "SHORT", Instrument.FullName,
                lockedContracts, lockedEntryPrice, lockedSLPrice));

            SubmitEntry();

            activeRRTool.TradeState      = RRTradeState.Pending;
            activeRRTool.CurrentSLPrice  = lockedSLPrice;
            activeRRTool.ActiveContracts = lockedContracts;

            state = TradeState.PendingEntry;
        }

        private RiskRewardTool FindConfirmedRRTool()
        {
            foreach (var drawObj in DrawObjects.ToList())
            {
                var rr = drawObj as RiskRewardTool;
                if (rr == null || rr.TradeState != RRTradeState.Confirmed) continue;
                if (!string.IsNullOrEmpty(RRToolTag) && rr.Tag != RRToolTag) continue;
                return rr;
            }
            return null;
        }

        private void LockLevels(RiskRewardTool rr, int contracts)
        {
            lockedEntryPrice = rr.GetEntryPrice();
            lockedSLPrice    = rr.GetSLPrice();
            lockedIsLong     = rr.IsLong();
            lockedContracts  = contracts;

            for (int r = 1; r <= 6; r++)
                lockedRRLevels[r] = rr.GetRRLevelPrice(r);

            sumFilled = 0;
        }

        #endregion

        #region Idle — Auto detection

        private void ScanForAutoSetup()
        {
            if (CurrentBars[0] < 20 || CurrentBars[1] < SwingStrength + ATRPeriod + 2 || CurrentBars[2] < LTFSwingStrength + 2) return;

            // Cooldown check
            if (lastSignalBar >= 0 && CurrentBar - lastSignalBar < CooldownBars)
                return;

            // Update HTF (daily) swing levels
            UpdateHTFSwingLevels();

            // Inside bar state updated in OnBarUpdate when BarsInProgress==1

            // Check for setups
            if (EnableSwingEntries)
                CheckSwingViolation();

            if (EnableInsideBarEntries)
                CheckInsideBarBreakout();
        }

        private void UpdateHTFSwingLevels()
        {
            // Check for new daily swing high
            int shBar = htfSwing.SwingHighBar(0, 1, SwingStrength + 1);
            if (shBar >= 0)
            {
                double shPrice = Highs[1][shBar];
                int absBar = CurrentBars[1] - shBar;

                if (absBar != lastSwingHighAbsBar)
                {
                    // ATR filter
                    double minSize = htfATR[0] * MinSwingATR;
                    int slBar = htfSwing.SwingLowBar(shBar, 1, Math.Max(1, CurrentBars[1] - shBar));
                    double nearestLow = slBar >= 0 && (shBar + slBar) <= CurrentBars[1] ? Lows[1][shBar + slBar] : 0;

                    if (nearestLow > 0 && Math.Abs(shPrice - nearestLow) >= minSize)
                    {
                        lastSwingHighPrice  = shPrice;
                        lastSwingHighAbsBar = absBar;
                        swingHighViolated   = false;
                        lastTradedSwingPrice = 0; // new swing — allow trading
                    }
                }
            }

            // Check for new daily swing low
            int slBarCheck = htfSwing.SwingLowBar(0, 1, SwingStrength + 1);
            if (slBarCheck >= 0)
            {
                double slPrice = Lows[1][slBarCheck];
                int absBar = CurrentBars[1] - slBarCheck;

                if (absBar != lastSwingLowAbsBar)
                {
                    double minSize = htfATR[0] * MinSwingATR;
                    int shBarCheck = htfSwing.SwingHighBar(slBarCheck, 1, Math.Max(1, CurrentBars[1] - slBarCheck));
                    double nearestHigh = shBarCheck >= 0 && (slBarCheck + shBarCheck) <= CurrentBars[1] ? Highs[1][slBarCheck + shBarCheck] : 0;

                    if (nearestHigh > 0 && Math.Abs(nearestHigh - slPrice) >= minSize)
                    {
                        lastSwingLowPrice  = slPrice;
                        lastSwingLowAbsBar = absBar;
                        swingLowViolated   = false;
                        lastTradedSwingPrice = 0; // new swing — allow trading
                    }
                }
            }
        }

        private void UpdateHTFInsideBarState()
        {
            // Inline daily inside bar detection on BarsArray[1]
            if (CurrentBars[1] < 2) return;

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
                htfPrevIsInside   = true;
                htfPrevMotherHigh = mHigh;
                htfPrevMotherLow  = mLow;

                // Only set wasInsideBar if this is a NEW mother bar we haven't traded
                if (mHigh != lastTradedMotherHigh || mLow != lastTradedMotherLow)
                {
                    wasInsideBar   = true;
                    lastMotherHigh = mHigh;
                    lastMotherLow  = mLow;
                }
            }
            else
            {
                htfPrevIsInside   = false;
                htfPrevMotherHigh = 0;
                htfPrevMotherLow  = 0;
                wasInsideBar      = false;
                // New daily bar broke out of inside bar — allow future inside bars to trade
                lastTradedMotherHigh = 0;
                lastTradedMotherLow  = 0;
            }
        }

        private double GetLTFSwingSL(bool isLong, double entryPrice)
        {
            if (CurrentBars[2] < LTFSwingStrength + 1) return 0;
            int lookBack = Math.Min(CurrentBars[2], 500);

            double dailyATR = CurrentBars[1] >= ATRPeriod ? htfATR[0] : 0;
            double minDist  = dailyATR * MinSLATR;
            double maxDist  = dailyATR * MaxSLATR;

            // Try swing instances 1 through 5, find first in valid range
            for (int instance = 1; instance <= 5; instance++)
            {
                double swingPrice = 0;

                if (isLong)
                {
                    int slBar = ltfSwing.SwingLowBar(0, instance, lookBack);
                    if (slBar >= 0) swingPrice = Lows[2][slBar];
                }
                else
                {
                    int shBar = ltfSwing.SwingHighBar(0, instance, lookBack);
                    if (shBar >= 0) swingPrice = Highs[2][shBar];
                }

                if (swingPrice <= 0) break;

                double dist = Math.Abs(entryPrice - swingPrice);

                if (dist >= minDist)
                {
                    if (dist <= maxDist)
                        return swingPrice;
                    else
                        return 0;
                }
            }

            return 0;
        }

        private bool IsTrendAllowed(bool isLong)
        {
            // Direction filter
            if (Direction == TradeDirection.LongsOnly && !isLong) return false;
            if (Direction == TradeDirection.ShortsOnly && isLong) return false;

            // LinReg trend filter
            if (!UseTrendFilter || trendLinReg == null) return true;
            if (CurrentBars[1] < TrendLinRegPeriod) return false;

            bool slopeUp = trendLinReg[0] > 0;
            return isLong ? slopeUp : !slopeUp;
        }

        private void CheckSwingViolation()
        {
            if (lastSwingHighPrice <= 0 || lastSwingLowPrice <= 0) return;

            // Price breaks ABOVE daily swing high → LONG
            if (!swingHighViolated && High[0] > lastSwingHighPrice)
            {
                swingHighViolated = true;
                if (lastSwingHighPrice == lastTradedSwingPrice) return;
                if (!IsTrendAllowed(true)) return;
                double sl = GetLTFSwingSL(true, lastSwingHighPrice);
                if (sl <= 0 || sl >= lastSwingHighPrice) return;
                lastTradedSwingPrice = lastSwingHighPrice;
                EmitAutoSignal(lastSwingHighPrice, sl, true, "Daily SH break");
            }

            // Price breaks BELOW daily swing low → SHORT
            if (!swingLowViolated && Low[0] < lastSwingLowPrice)
            {
                swingLowViolated = true;
                if (lastSwingLowPrice == lastTradedSwingPrice) return;
                if (!IsTrendAllowed(false)) return;
                double sl = GetLTFSwingSL(false, lastSwingLowPrice);
                if (sl <= 0 || sl <= lastSwingLowPrice) return;
                lastTradedSwingPrice = lastSwingLowPrice;
                EmitAutoSignal(lastSwingLowPrice, sl, false, "Daily SL break");
            }
        }

        private void CheckInsideBarBreakout()
        {
            if (!wasInsideBar || lastMotherHigh <= 0 || lastMotherLow <= 0) return;

            // Skip if we already traded this exact mother bar setup
            if (lastMotherHigh == lastTradedMotherHigh && lastMotherLow == lastTradedMotherLow)
                return;

            if (High[0] > lastMotherHigh)
            {
                wasInsideBar = false;
                lastTradedMotherHigh = lastMotherHigh;
                lastTradedMotherLow  = lastMotherLow;
                if (!IsTrendAllowed(true)) return;
                double sl = GetLTFSwingSL(true, lastMotherHigh);
                if (sl <= 0 || sl >= lastMotherHigh) sl = lastMotherLow;
                EmitAutoSignal(lastMotherHigh, sl, true, "Inside Bar UP");
            }
            else if (Low[0] < lastMotherLow)
            {
                wasInsideBar = false;
                lastTradedMotherHigh = lastMotherHigh;
                lastTradedMotherLow  = lastMotherLow;
                if (!IsTrendAllowed(false)) return;
                double sl = GetLTFSwingSL(false, lastMotherLow);
                if (sl <= 0 || sl <= lastMotherLow) sl = lastMotherHigh;
                EmitAutoSignal(lastMotherLow, sl, false, "Inside Bar DOWN");
            }
        }

        private void EmitAutoSignal(double entryPrice, double slPrice, bool isLong, string reason)
        {
            if (entryPrice <= 0 || slPrice <= 0) return;
            if (Math.Abs(entryPrice - slPrice) < TickSize) return;

            pointValue       = Instrument.MasterInstrument.PointValue;
            realizedPnL      = 0;
            highestPassedLevel = 0;
            lockedEntryPrice = entryPrice;
            lockedSLPrice    = slPrice;
            lockedIsLong     = isLong;

            // Position sizing
            double risk = Math.Abs(entryPrice - slPrice);
            double riskPerContract = risk * pointValue;
            if (riskPerContract <= 0) return;

            Account account = null;
            lock (Account.All)
                account = Account.All.FirstOrDefault();
            if (account == null) return;

            double equity = account.Get(AccountItem.CashValue, Currency.UsDollar);
            double riskAmount = equity * RiskPercent / 100.0;
            lockedContracts = (int)Math.Floor(riskAmount / riskPerContract);
            if (lockedContracts < 1) return;

            // Compute R:R levels
            double direction = entryPrice - slPrice;
            for (int r = 1; r <= 6; r++)
                lockedRRLevels[r] = entryPrice + direction * r;

            sumFilled = 0;

            double totalRisk = riskPerContract * lockedContracts;
            Print(string.Format("DSB [AUTO]: {0} | {1} {2} | {3} cts @ {4:F2} | SL {5:F2} | Risk ${6:N0}",
                reason, isLong ? "LONG" : "SHORT", Instrument.FullName,
                lockedContracts, entryPrice, slPrice, totalRisk));

            SubmitEntry();
            lastSignalBar = CurrentBar;
            state = TradeState.PendingEntry;
        }

        #endregion

        #region PendingEntry — update or cancel

        private void HandlePendingEntry()
        {
            // Manual mode: check if RR Tool was disarmed
            if (activeRRTool != null)
            {
                if (activeRRTool.TradeState == RRTradeState.Unarmed)
                {
                    if (entryOrder != null) CancelOrder(entryOrder);
                    Reset("DSB: RR Tool cancelled. Entry cancelled.");
                    return;
                }

                double newEntry = activeRRTool.GetEntryPrice();
                if (Math.Abs(newEntry - lockedEntryPrice) > TickSize
                    && entryOrder != null
                    && entryOrder.OrderState == OrderState.Working)
                {
                    int newContracts = activeRRTool.GetContracts();
                    LockLevels(activeRRTool, newContracts > 0 ? newContracts : lockedContracts);
                    CancelOrder(entryOrder);
                    SubmitEntry();

                    activeRRTool.CurrentSLPrice  = lockedSLPrice;
                    activeRRTool.ActiveContracts = lockedContracts;
                }
            }
            // Auto mode: nothing to update — entry is fire-and-forget
        }

        #endregion

        #region Entry submission

        private void SubmitEntry()
        {
            OrderAction action = lockedIsLong ? OrderAction.Buy : OrderAction.SellShort;
            double currentPrice = Close[0];

            bool isBreakout = lockedIsLong
                ? lockedEntryPrice > currentPrice
                : lockedEntryPrice < currentPrice;

            switch (EntryType)
            {
                case EntryType.Market:
                    entryOrder = SubmitOrderUnmanaged(0, action, OrderType.Market,
                        lockedContracts, 0, 0, string.Empty, "DSB_Entry");
                    break;

                case EntryType.Limit:
                    if (isBreakout)
                    {
                        entryOrder = SubmitOrderUnmanaged(0, action, OrderType.StopMarket,
                            lockedContracts, 0, lockedEntryPrice, string.Empty, "DSB_Entry");
                    }
                    else
                    {
                        entryOrder = SubmitOrderUnmanaged(0, action, OrderType.Limit,
                            lockedContracts, lockedEntryPrice, 0, string.Empty, "DSB_Entry");
                    }
                    break;

                case EntryType.StopLimit:
                    int sign     = lockedIsLong ? 1 : -1;
                    double limit = lockedEntryPrice + sign * EntryOffsetTicks * TickSize;
                    entryOrder = SubmitOrderUnmanaged(0, action, OrderType.StopLimit,
                        lockedContracts, limit, lockedEntryPrice, string.Empty, "DSB_Entry");
                    break;
            }
        }

        #endregion

        #region Execution handling

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (entryOrder != null && execution.Order == entryOrder)
            {
                if (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled
                    || (execution.Order.OrderState == OrderState.Cancelled && execution.Order.Filled > 0))
                {
                    sumFilled += execution.Quantity;

                    if (execution.Order.OrderState != OrderState.PartFilled
                        && sumFilled == execution.Order.Filled)
                    {
                        lockedContracts = execution.Order.Filled;
                        actualFillPrice = execution.Order.AverageFillPrice;
                        entryOrder = null;
                        sumFilled  = 0;

                        OrderAction slAction = lockedIsLong ? OrderAction.Sell : OrderAction.BuyToCover;
                        stopOrder = SubmitOrderUnmanaged(0, slAction, OrderType.StopMarket,
                            lockedContracts, 0, lockedSLPrice, string.Empty, "DSB_SL");

                        state = TradeState.InPosition;

                        if (activeRRTool != null)
                        {
                            activeRRTool.TradeState      = RRTradeState.Live;
                            activeRRTool.ActiveContracts = lockedContracts;
                            activeRRTool.CurrentSLPrice  = lockedSLPrice;
                        }

                        Print(string.Format("DSB: Filled {0} @ {1:F2}. SL at {2:F2}.",
                            lockedContracts, actualFillPrice, lockedSLPrice));
                    }
                }
                return;
            }

            if (stopOrder != null && execution.Order == stopOrder)
            {
                if (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled)
                {
                    double pnl = (price - actualFillPrice) * execution.Quantity * pointValue;
                    if (!lockedIsLong) pnl = -pnl;
                    realizedPnL += pnl;

                    if (execution.Order.OrderState == OrderState.Filled)
                    {
                        if (activeRRTool != null)
                        {
                            activeRRTool.TradeState      = RRTradeState.Closed;
                            activeRRTool.RealizedPnL     = realizedPnL;
                            activeRRTool.ActiveContracts = 0;
                        }

                        Reset(string.Format("DSB: SL hit at {0:F2}. P&L: ${1:N0}.",
                            price, realizedPnL));
                    }
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice,
            double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time,
            ErrorCode error, string comment)
        {
            if (order.Name == "DSB_Entry")
                entryOrder = order;
            else if (order.Name == "DSB_SL")
                stopOrder = order;

            if (order == entryOrder && (orderState == OrderState.Cancelled
                                     || orderState == OrderState.Rejected))
            {
                if (activeRRTool != null)
                    activeRRTool.TradeState = RRTradeState.Unarmed;
                Reset(string.Format("DSB: Entry {0}. {1}", orderState, comment));
            }
        }

        #endregion

        #region Trailing stop

        private void TrailSL()
        {
            if (stopOrder == null) return;
            if (stopOrder.OrderState != OrderState.Working
                && stopOrder.OrderState != OrderState.Accepted) return;

            double currentPrice = Close[0];
            double riskDist = Math.Abs(lockedEntryPrice - lockedSLPrice);
            if (riskDist <= 0) return;

            // How far has price moved in R multiples?
            double currentR = lockedIsLong
                ? (currentPrice - lockedEntryPrice) / riskDist
                : (lockedEntryPrice - currentPrice) / riskDist;

            if (currentR <= 0) return;

            // Determine what SL should be based on current R progress
            double newSLPrice = 0;
            string trailLabel = "";

            // Step 1: Breakeven trigger
            if (currentR >= BETriggerR && highestPassedLevel == 0)
            {
                newSLPrice = lockedEntryPrice + (lockedIsLong ? TickSize : -TickSize);
                highestPassedLevel = 1;
                trailLabel = string.Format("{0:F1}R → BE", BETriggerR);
            }

            // Step 2: Level-by-level trailing from TrailStartR onward
            if (currentR >= TrailStartR)
            {
                // How many trail steps past TrailStartR?
                int steps = (int)Math.Floor((currentR - TrailStartR) / TrailStepR);
                double trailLevel = TrailStartR + steps * TrailStepR;
                double slAtR = trailLevel - TrailOffsetR;

                if (slAtR > 0)
                {
                    double candidateSL = lockedIsLong
                        ? lockedEntryPrice + slAtR * riskDist
                        : lockedEntryPrice - slAtR * riskDist;

                    if (newSLPrice == 0 || (lockedIsLong ? candidateSL > newSLPrice : candidateSL < newSLPrice))
                    {
                        newSLPrice = candidateSL;
                        trailLabel = string.Format("{0:F1}R → SL at {1:F1}R", trailLevel, slAtR);
                    }
                }
            }

            if (newSLPrice == 0) return;

            // Only move forward
            bool isForward = lockedIsLong
                ? newSLPrice > stopOrder.StopPrice
                : newSLPrice < stopOrder.StopPrice;

            if (isForward)
            {
                ChangeOrder(stopOrder, lockedContracts, 0, newSLPrice);

                Print(string.Format("DSB: {0} | SL to {1:F2}", trailLabel, newSLPrice));

                if (activeRRTool != null)
                    activeRRTool.CurrentSLPrice = newSLPrice;
            }
        }

        #endregion

        #region Cleanup

        private void Reset(string message)
        {
            Print(message);
            state              = TradeState.Idle;
            activeRRTool       = null;
            entryOrder         = null;
            stopOrder          = null;
            sumFilled          = 0;
            highestPassedLevel = 0;
        }

        #endregion
    }
}
