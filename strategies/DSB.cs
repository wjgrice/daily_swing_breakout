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
        private Swing               swingIndicator;
        private ATR                 atrIndicator;
        private double              lastSwingHighPrice;
        private double              lastSwingLowPrice;
        private int                 lastSwingHighBar;
        private int                 lastSwingLowBar;
        private bool                swingHighViolated;
        private bool                swingLowViolated;
        private bool                wasInsideBar;
        private double              lastMotherHigh;
        private double              lastMotherLow;
        private int                 lastSignalBar;

        // Inside bar tracking (inline)
        private bool                prevBarIsInside;
        private double              prevMotherHigh;
        private double              prevMotherLow;

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

        [Display(Name = "Enable Swing Entries", GroupName = "2. Auto Detection", Order = 2)]
        public bool EnableSwingEntries { get; set; }

        [Display(Name = "Enable Inside Bar Entries", GroupName = "2. Auto Detection", Order = 3)]
        public bool EnableInsideBarEntries { get; set; }

        [Display(Name = "Swing Strength", GroupName = "2. Auto Detection", Order = 10)]
        [Range(1, 50)]
        public int SwingStrength { get; set; }

        [Display(Name = "ATR Period", GroupName = "2. Auto Detection", Order = 11)]
        [Range(5, 100)]
        public int ATRPeriod { get; set; }

        [Display(Name = "Min Swing ATR", GroupName = "2. Auto Detection", Order = 12,
                 Description = "Minimum swing size as ATR multiple")]
        [Range(0, 10.0)]
        public double MinSwingATR { get; set; }

        [Display(Name = "Min Mother ATR", GroupName = "2. Auto Detection", Order = 13,
                 Description = "Mother bar must be >= this ATR multiple")]
        [Range(0, 5.0)]
        public double MinMotherATR { get; set; }

        [Display(Name = "Risk %", GroupName = "2. Auto Detection", Order = 20,
                 Description = "% of equity to risk per auto trade")]
        [Range(0.1, 10.0)]
        public double RiskPercent { get; set; }

        [Display(Name = "Cooldown Bars", GroupName = "2. Auto Detection", Order = 21,
                 Description = "Min bars between auto signals")]
        [Range(1, 200)]
        public int CooldownBars { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                 = "DSB";
                Description          = "Daily Swing Breakout — manual RR Tool + auto detection";
                Calculate            = Calculate.OnEachTick;
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
                AutoDetect           = false;
                EnableSwingEntries   = true;
                EnableInsideBarEntries = true;
                SwingStrength        = 5;
                ATRPeriod            = 14;
                MinSwingATR          = 1.5;
                MinMotherATR         = 0.75;
                RiskPercent          = 4.0;
                CooldownBars         = 5;
            }
            else if (State == State.DataLoaded)
            {
                state          = TradeState.Idle;
                lockedRRLevels = new double[7];
                lastSignalBar  = -1;

                if (AutoDetect)
                {
                    swingIndicator = Swing(SwingStrength);
                    atrIndicator   = ATR(ATRPeriod);
                }
            }
        }

        protected override void OnBarUpdate()
        {
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
            if (CurrentBar < SwingStrength + ATRPeriod + 2) return;

            // Cooldown check
            if (lastSignalBar >= 0 && CurrentBar - lastSignalBar < CooldownBars)
                return;

            // Update swing levels
            UpdateSwingLevels();

            // Detect inside bars inline
            UpdateInsideBarState();

            // Check for setups
            if (EnableSwingEntries)
                CheckSwingViolation();

            if (EnableInsideBarEntries)
                CheckInsideBarBreakout();
        }

        private void UpdateSwingLevels()
        {
            // Check for new swing high
            int shBar = swingIndicator.SwingHighBar(0, 1, SwingStrength + 1);
            if (shBar >= 0 && (CurrentBar - shBar) != lastSwingHighBar)
            {
                double shPrice = High[shBar];

                // ATR filter
                double minSize = atrIndicator[0] * MinSwingATR;
                double nearestLow = GetNearestSwingLow(shBar);
                if (nearestLow > 0 && Math.Abs(shPrice - nearestLow) >= minSize)
                {
                    lastSwingHighPrice = shPrice;
                    lastSwingHighBar   = CurrentBar - shBar;
                    swingHighViolated  = false;
                }
            }

            // Check for new swing low
            int slBar = swingIndicator.SwingLowBar(0, 1, SwingStrength + 1);
            if (slBar >= 0 && (CurrentBar - slBar) != lastSwingLowBar)
            {
                double slPrice = Low[slBar];

                double minSize = atrIndicator[0] * MinSwingATR;
                double nearestHigh = GetNearestSwingHigh(slBar);
                if (nearestHigh > 0 && Math.Abs(nearestHigh - slPrice) >= minSize)
                {
                    lastSwingLowPrice = slPrice;
                    lastSwingLowBar   = CurrentBar - slBar;
                    swingLowViolated  = false;
                }
            }
        }

        private double GetNearestSwingLow(int fromBarsAgo)
        {
            int lookBack = CurrentBar - fromBarsAgo;
            if (lookBack <= 0) return 0;
            int slBar = swingIndicator.SwingLowBar(fromBarsAgo, 1, lookBack);
            if (slBar >= 0 && (fromBarsAgo + slBar) <= CurrentBar)
                return Low[fromBarsAgo + slBar];
            return 0;
        }

        private double GetNearestSwingHigh(int fromBarsAgo)
        {
            int lookBack = CurrentBar - fromBarsAgo;
            if (lookBack <= 0) return 0;
            int shBar = swingIndicator.SwingHighBar(fromBarsAgo, 1, lookBack);
            if (shBar >= 0 && (fromBarsAgo + shBar) <= CurrentBar)
                return High[fromBarsAgo + shBar];
            return 0;
        }

        private void UpdateInsideBarState()
        {
            if (CurrentBar < 2) return;

            double mHigh = prevBarIsInside ? prevMotherHigh : High[1];
            double mLow  = prevBarIsInside ? prevMotherLow  : Low[1];

            bool isIB = High[0] <= mHigh && Low[0] >= mLow;

            // Mother bar ATR filter
            if (isIB && MinMotherATR > 0 && !prevBarIsInside)
            {
                if ((mHigh - mLow) < MinMotherATR * atrIndicator[0])
                    isIB = false;
            }

            if (isIB)
            {
                wasInsideBar    = true;
                lastMotherHigh  = mHigh;
                lastMotherLow   = mLow;
                prevBarIsInside = true;
                prevMotherHigh  = mHigh;
                prevMotherLow   = mLow;
            }
            else
            {
                prevBarIsInside = false;
                prevMotherHigh  = 0;
                prevMotherLow   = 0;
            }
        }

        private void CheckSwingViolation()
        {
            if (lastSwingHighPrice <= 0 || lastSwingLowPrice <= 0) return;

            // Price breaks ABOVE swing high → LONG
            if (!swingHighViolated && High[0] > lastSwingHighPrice)
            {
                swingHighViolated = true;
                EmitAutoSignal(lastSwingHighPrice, lastSwingLowPrice, true, "Swing High break");
            }

            // Price breaks BELOW swing low → SHORT
            if (!swingLowViolated && Low[0] < lastSwingLowPrice)
            {
                swingLowViolated = true;
                EmitAutoSignal(lastSwingLowPrice, lastSwingHighPrice, false, "Swing Low break");
            }
        }

        private void CheckInsideBarBreakout()
        {
            if (!wasInsideBar || lastMotherHigh <= 0 || lastMotherLow <= 0) return;

            // Only check on the bar AFTER the inside bar cluster ends
            if (prevBarIsInside) return;

            // Breakout above mother bar → LONG
            if (High[0] > lastMotherHigh)
            {
                double sl = lastMotherLow;
                if (lastSwingLowPrice > 0 && lastSwingLowPrice > sl)
                    sl = lastSwingLowPrice;
                EmitAutoSignal(lastMotherHigh, sl, true, "IB breakout UP");
                wasInsideBar = false;
            }
            // Breakout below mother bar → SHORT
            else if (Low[0] < lastMotherLow)
            {
                double sl = lastMotherHigh;
                if (lastSwingHighPrice > 0 && lastSwingHighPrice < sl)
                    sl = lastSwingHighPrice;
                EmitAutoSignal(lastMotherLow, sl, false, "IB breakout DOWN");
                wasInsideBar = false;
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
            int newHighest = highestPassedLevel;

            for (int r = newHighest + 1; r <= 6; r++)
            {
                bool passed = lockedIsLong
                    ? currentPrice >= lockedRRLevels[r]
                    : currentPrice <= lockedRRLevels[r];
                if (!passed) break;
                newHighest = r;
            }

            if (newHighest <= highestPassedLevel) return;

            double newSLPrice;
            if (highestPassedLevel == 0 && newHighest >= 1)
                newSLPrice = lockedEntryPrice + (lockedIsLong ? TickSize : -TickSize);
            else
                newSLPrice = lockedRRLevels[newHighest - 1];

            bool isForward = lockedIsLong
                ? newSLPrice > stopOrder.StopPrice
                : newSLPrice < stopOrder.StopPrice;

            if (isForward)
            {
                ChangeOrder(stopOrder, lockedContracts, 0, newSLPrice);
                highestPassedLevel = newHighest;

                Print(string.Format("DSB: 1:{0} passed → SL to {1:F2}", newHighest, newSLPrice));

                if (activeRRTool != null)
                {
                    activeRRTool.CurrentSLPrice = newSLPrice;
                    activeRRTool.FilledTPLevels |= (1 << newHighest);
                }
            }
            else
            {
                highestPassedLevel = newHighest;
                if (activeRRTool != null)
                    activeRRTool.FilledTPLevels |= (1 << newHighest);
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
