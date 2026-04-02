#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
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
        private double              lockedEntryPrice;   // RR Tool anchor price (order price)
        private double              actualFillPrice;    // real average fill price
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

        #region Strategy Properties

        [Display(Name = "RR Tool Tag", GroupName = "Integration", Order = 10,
                 Description = "Tag of a specific RR Tool to use (empty = auto-detect)")]
        public string RRToolTag { get; set; }

        [Display(Name = "Entry Type", GroupName = "Entry", Order = 20)]
        public EntryType EntryType { get; set; }

        [Display(Name = "Entry Offset Ticks", GroupName = "Entry", Order = 21,
                 Description = "Tick offset from entry price (for StopLimit)")]
        [Range(0, 20)]
        public int EntryOffsetTicks { get; set; }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name                 = "DSB";
                Description          = "Daily Swing Breakout — trailing stop on R:R levels";
                Calculate            = Calculate.OnEachTick;
                IsUnmanaged          = true;
                EntriesPerDirection  = 1;
                EntryHandling        = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;
                IsOverlay            = true;

                RRToolTag            = string.Empty;
                EntryType            = EntryType.Limit;
                EntryOffsetTicks     = 0;
            }
            else if (State == State.DataLoaded)
            {
                state          = TradeState.Idle;
                lockedRRLevels = new double[7];
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 1) return;

            switch (state)
            {
                case TradeState.Idle:
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

        #region Idle — scan for confirmed RR Tool

        private void ScanForConfirmedRRTool()
        {
            var rrTool = FindConfirmedRRTool();
            if (rrTool == null) return;

            int contracts = rrTool.GetContracts();
            if (contracts < 1)
            {
                Print("DSB: Confirmed RR Tool found but contracts < 1. Skipping.");
                return;
            }

            activeRRTool = rrTool;
            pointValue   = Instrument.MasterInstrument.PointValue;
            realizedPnL  = 0;
            highestPassedLevel = 0;
            LockLevels(rrTool, contracts);

            Print(string.Format("DSB: {0} {1} | {2} cts @ {3:F2} | SL {4:F2} | Risk ${5:N0} ({6:F1}%)",
                lockedIsLong ? "LONG" : "SHORT", Instrument.FullName,
                lockedContracts, lockedEntryPrice, lockedSLPrice,
                rrTool.GetRisk() * pointValue * lockedContracts,
                equity > 0 ? (rrTool.GetRisk() * pointValue * lockedContracts / equity * 100.0) : 0));

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

                if (!string.IsNullOrEmpty(RRToolTag) && rr.Tag != RRToolTag)
                    continue;

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

        #region PendingEntry — update or cancel

        private void HandlePendingEntry()
        {
            if (activeRRTool == null || activeRRTool.TradeState == RRTradeState.Unarmed)
            {
                if (entryOrder != null)
                    CancelOrder(entryOrder);
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

                Print(string.Format("DSB: Entry resubmitted at {0:F2}, {1} contracts",
                    lockedEntryPrice, lockedContracts));
            }
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
                        Print(string.Format("DSB: Breakout entry — StopMarket at {0:F2}", lockedEntryPrice));
                    }
                    else
                    {
                        entryOrder = SubmitOrderUnmanaged(0, action, OrderType.Limit,
                            lockedContracts, lockedEntryPrice, 0, string.Empty, "DSB_Entry");
                        Print(string.Format("DSB: Pullback entry — Limit at {0:F2}", lockedEntryPrice));
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
            // ── Entry fill ──────────────────────────────────────────────
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

                        // Submit SL only — no TPs, trailing handles exits
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

            // ── SL fill — accumulate P&L across partial fills ─────────
            if (stopOrder != null && execution.Order == stopOrder)
            {
                if (execution.Order.OrderState == OrderState.Filled
                    || execution.Order.OrderState == OrderState.PartFilled)
                {
                    double pnl = (price - actualFillPrice) * execution.Quantity * pointValue;
                    if (!lockedIsLong) pnl = -pnl;
                    realizedPnL += pnl;

                    // Only close trade when fully filled
                    if (execution.Order.OrderState == OrderState.Filled)
                    {
                        if (activeRRTool != null)
                        {
                            activeRRTool.TradeState      = RRTradeState.Closed;
                            activeRRTool.RealizedPnL     = realizedPnL;
                            activeRRTool.ActiveContracts = 0;
                        }

                        Reset(string.Format("DSB: SL hit at {0:F2}. P&L: ${1:N0}. Position closed.",
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
            if (stopOrder == null)
                return;

            if (stopOrder.OrderState != OrderState.Working
                && stopOrder.OrderState != OrderState.Accepted)
                return;

            double currentPrice = Close[0];
            int newHighest = highestPassedLevel;

            // Check each R:R level — has price passed it?
            for (int r = newHighest + 1; r <= 6; r++)
            {
                bool passed = lockedIsLong
                    ? currentPrice >= lockedRRLevels[r]
                    : currentPrice <= lockedRRLevels[r];

                if (!passed) break;
                newHighest = r;
            }

            if (newHighest <= highestPassedLevel)
                return;

            // Determine new SL price
            double newSLPrice;
            if (highestPassedLevel == 0 && newHighest >= 1)
            {
                newSLPrice = lockedEntryPrice + (lockedIsLong ? TickSize : -TickSize);
            }
            else
            {
                newSLPrice = lockedRRLevels[newHighest - 1];
            }

            // Only move forward
            bool isForward = lockedIsLong
                ? newSLPrice > stopOrder.StopPrice
                : newSLPrice < stopOrder.StopPrice;

            if (isForward)
            {
                ChangeOrder(stopOrder, lockedContracts, 0, newSLPrice);
                highestPassedLevel = newHighest;

                Print(string.Format("DSB: 1:{0} passed → SL to {1:F2}",
                    newHighest, newSLPrice));

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
