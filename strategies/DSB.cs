#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public enum ExitMode
    {
        HalfAndTrail,
        ThirdScale,
        FullAtTarget,
        Runner
    }

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
        private double              lockedSLPrice;
        private bool                lockedIsLong;
        private double[]            lockedRRLevels;
        private int                 lockedContracts;
        private double              pointValue;

        // Orders
        private Order               entryOrder;
        private Order               stopOrder;
        private List<Order>         tpOrders;
        private int                 sumFilled;
        private int                 lastFilledLevel;

        // P&L tracking
        private double              realizedPnL;

        #region Strategy Properties

        [Display(Name = "Exit Mode", GroupName = "Exit Rules", Order = 1)]
        public ExitMode ExitMode { get; set; }

        [Display(Name = "Max Target R:R", GroupName = "Exit Rules", Order = 2,
                 Description = "Maximum R:R level to target (1-6)")]
        [Range(1, 6)]
        public int MaxTargetRR { get; set; }

        [Display(Name = "Breakeven Trigger R:R", GroupName = "Exit Rules", Order = 3,
                 Description = "R:R level that triggers SL move to breakeven")]
        [Range(0.5, 6.0)]
        public double BreakevenTriggerRR { get; set; }

        [Display(Name = "Trail SL to Level", GroupName = "Exit Rules", Order = 4,
                 Description = "Trail SL to most recently filled TP level")]
        public bool TrailSLToLevel { get; set; }

        [Display(Name = "RR Tool Tag", GroupName = "Integration", Order = 10,
                 Description = "Tag of a specific RR Tool to use (empty = auto-detect armed tool)")]
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
                Description          = "Daily Swing Breakout — reads RR Tool, manages entry/exit/trailing";
                Calculate            = Calculate.OnBarClose;
                IsUnmanaged          = true;
                EntriesPerDirection  = 1;
                EntryHandling        = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = false;

                ExitMode             = ExitMode.HalfAndTrail;
                MaxTargetRR          = 3;
                BreakevenTriggerRR   = 1.0;
                TrailSLToLevel       = true;
                RRToolTag            = string.Empty;
                EntryType            = EntryType.Limit;
                EntryOffsetTicks     = 0;
            }
            else if (State == State.DataLoaded)
            {
                state          = TradeState.Idle;
                tpOrders       = new List<Order>();
                lockedRRLevels = new double[7]; // index 0 unused, 1-6 = R:R levels
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
            LockLevels(rrTool, contracts);
            SubmitEntry();

            // Update RR Tool visual state
            activeRRTool.TradeState      = RRTradeState.Pending;
            activeRRTool.CurrentSLPrice  = lockedSLPrice;
            activeRRTool.ActiveContracts = lockedContracts;

            state = TradeState.PendingEntry;
            Print(string.Format("DSB: Confirmed. {0} {1} contracts at {2:F2}, SL {3:F2}",
                lockedIsLong ? "BUY" : "SELL", lockedContracts, lockedEntryPrice, lockedSLPrice));
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

            sumFilled       = 0;
            lastFilledLevel = 0;
        }

        #endregion

        #region PendingEntry — update or cancel

        private void HandlePendingEntry()
        {
            // User clicked cancel (tool went back to Unarmed)
            if (activeRRTool == null || activeRRTool.TradeState == RRTradeState.Unarmed)
            {
                if (entryOrder != null)
                    CancelOrder(entryOrder);

                Reset("DSB: RR Tool cancelled. Entry cancelled.");
                return;
            }

            // Re-read anchors in case user dragged them while still pending
            double newEntry = activeRRTool.GetEntryPrice();
            if (Math.Abs(newEntry - lockedEntryPrice) > TickSize
                && entryOrder != null
                && entryOrder.OrderState == OrderState.Working)
            {
                int newContracts = activeRRTool.GetContracts();
                LockLevels(activeRRTool, newContracts > 0 ? newContracts : lockedContracts);

                double limitPrice = 0;
                double stopPrice  = 0;

                if (EntryType == EntryType.Limit)
                    limitPrice = lockedEntryPrice;
                else if (EntryType == EntryType.StopLimit)
                {
                    int sign   = lockedIsLong ? 1 : -1;
                    stopPrice  = lockedEntryPrice;
                    limitPrice = lockedEntryPrice + sign * EntryOffsetTicks * TickSize;
                }

                ChangeOrder(entryOrder, lockedContracts, limitPrice, stopPrice);

                activeRRTool.CurrentSLPrice  = lockedSLPrice;
                activeRRTool.ActiveContracts = lockedContracts;

                Print(string.Format("DSB: Entry updated to {0:F2}, {1} contracts", lockedEntryPrice, lockedContracts));
            }
        }

        #endregion

        #region Entry submission

        private void SubmitEntry()
        {
            OrderAction action = lockedIsLong ? OrderAction.Buy : OrderAction.SellShort;

            switch (EntryType)
            {
                case EntryType.Market:
                    entryOrder = SubmitOrderUnmanaged(0, action, OrderType.Market,
                        lockedContracts, 0, 0, string.Empty, "DSB_Entry");
                    break;

                case EntryType.Limit:
                    entryOrder = SubmitOrderUnmanaged(0, action, OrderType.Limit,
                        lockedContracts, lockedEntryPrice, 0, string.Empty, "DSB_Entry");
                    break;

                case EntryType.StopLimit:
                    int sign      = lockedIsLong ? 1 : -1;
                    double limit  = lockedEntryPrice + sign * EntryOffsetTicks * TickSize;
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
                        entryOrder = null;
                        sumFilled  = 0;

                        SubmitBracketOrders();
                        state = TradeState.InPosition;

                        // Update RR Tool visual
                        if (activeRRTool != null)
                        {
                            activeRRTool.TradeState      = RRTradeState.Live;
                            activeRRTool.ActiveContracts = lockedContracts;
                            activeRRTool.CurrentSLPrice  = lockedSLPrice;
                        }

                        Print(string.Format("DSB: Entry filled. {0} contracts at {1:F2}. Bracket placed.",
                            lockedContracts, price));
                    }
                }
                return;
            }

            // ── SL fill ─────────────────────────────────────────────────
            if (stopOrder != null && execution.Order == stopOrder
                && stopOrder.OrderState == OrderState.Filled)
            {
                // Calculate P&L for SL fill
                double slPnl = (price - lockedEntryPrice) * execution.Quantity * pointValue;
                if (!lockedIsLong) slPnl = -slPnl;
                realizedPnL += slPnl;

                CancelRemainingTPs();

                if (activeRRTool != null)
                {
                    activeRRTool.TradeState      = RRTradeState.Closed;
                    activeRRTool.RealizedPnL     = realizedPnL;
                    activeRRTool.ActiveContracts = 0;
                }

                Reset(string.Format("DSB: SL hit at {0:F2}. P&L: ${1:F0}. Position closed.",
                    price, realizedPnL));
                return;
            }

            // ── TP fills ────────────────────────────────────────────────
            for (int i = tpOrders.Count - 1; i >= 0; i--)
            {
                if (tpOrders[i] != null && execution.Order == tpOrders[i]
                    && tpOrders[i].OrderState == OrderState.Filled)
                {
                    lastFilledLevel++;
                    tpOrders.RemoveAt(i);

                    // Calculate P&L for this TP fill
                    double tpPnl = (price - lockedEntryPrice) * execution.Quantity * pointValue;
                    if (!lockedIsLong) tpPnl = -tpPnl;
                    realizedPnL += tpPnl;

                    Print(string.Format("DSB: TP {0}R filled at {1:F2}. P&L so far: ${2:F0}",
                        lastFilledLevel, price, realizedPnL));

                    TrailSLAfterFill();

                    // Update RR Tool visual
                    if (activeRRTool != null)
                    {
                        activeRRTool.FilledTPCount  = lastFilledLevel;
                        activeRRTool.ActiveContracts = GetRemainingQty();
                        activeRRTool.RealizedPnL    = realizedPnL;
                    }

                    if (marketPosition == MarketPosition.Flat)
                    {
                        CancelRemainingTPs();

                        if (activeRRTool != null)
                        {
                            activeRRTool.TradeState      = RRTradeState.Closed;
                            activeRRTool.ActiveContracts = 0;
                        }

                        Reset(string.Format("DSB: Final TP hit. P&L: ${0:F0}. Position closed.",
                            realizedPnL));
                    }

                    return;
                }
            }
        }

        protected override void OnOrderUpdate(Order order, double limitPrice,
            double stopPrice, int quantity, int filled,
            double averageFillPrice, OrderState orderState, DateTime time,
            ErrorCode error, string comment)
        {
            // Track order references by signal name
            if (order.Name == "DSB_Entry")
                entryOrder = order;
            else if (order.Name == "DSB_SL")
                stopOrder = order;
            else if (order.Name.StartsWith("DSB_TP"))
            {
                if (!tpOrders.Contains(order))
                    tpOrders.Add(order);
            }

            // Handle entry cancellation/rejection
            if (order == entryOrder && (orderState == OrderState.Cancelled
                                     || orderState == OrderState.Rejected))
            {
                if (activeRRTool != null)
                    activeRRTool.TradeState = RRTradeState.Unarmed;

                Reset(string.Format("DSB: Entry {0}. {1}", orderState, comment));
            }
        }

        #endregion

        #region Bracket orders

        private void SubmitBracketOrders()
        {
            tpOrders.Clear();

            OrderAction slAction = lockedIsLong ? OrderAction.Sell : OrderAction.BuyToCover;
            stopOrder = SubmitOrderUnmanaged(0, slAction, OrderType.StopMarket,
                lockedContracts, 0, lockedSLPrice, string.Empty, "DSB_SL");

            int[] splits = ComputeSplits();
            OrderAction tpAction = lockedIsLong ? OrderAction.Sell : OrderAction.BuyToCover;

            for (int i = 0; i < splits.Length; i++)
            {
                if (splits[i] <= 0) continue;

                int level = GetTPLevel(i);
                double tpPrice = lockedRRLevels[level];

                var tp = SubmitOrderUnmanaged(0, tpAction, OrderType.Limit,
                    splits[i], tpPrice, 0, string.Empty,
                    string.Format("DSB_TP{0}", level));
                tpOrders.Add(tp);
            }
        }

        private int[] ComputeSplits()
        {
            int n = lockedContracts;

            switch (ExitMode)
            {
                case ExitMode.HalfAndTrail:
                    if (n < 2) return new int[] { n };
                    int half = n / 2;
                    return new int[] { half, n - half };

                case ExitMode.ThirdScale:
                    if (n < 3) return new int[] { n };
                    int third = n / 3;
                    return new int[] { third, third, n - 2 * third };

                case ExitMode.FullAtTarget:
                    return new int[] { n };

                case ExitMode.Runner:
                    if (n < 2) return new int[] { n };
                    int h = n / 2;
                    return new int[] { h };

                default:
                    return new int[] { n };
            }
        }

        private int GetTPLevel(int splitIndex)
        {
            switch (ExitMode)
            {
                case ExitMode.HalfAndTrail:
                    return splitIndex == 0 ? 1 : Math.Min(MaxTargetRR, 6);

                case ExitMode.ThirdScale:
                    return Math.Min(splitIndex + 1, 6);

                case ExitMode.FullAtTarget:
                    return Math.Min(MaxTargetRR, 6);

                case ExitMode.Runner:
                    return 1;

                default:
                    return Math.Min(MaxTargetRR, 6);
            }
        }

        #endregion

        #region SL trailing

        private void TrailSLAfterFill()
        {
            if (stopOrder == null || stopOrder.OrderState != OrderState.Working)
                return;

            double newSLPrice;
            int remainingQty = GetRemainingQty();

            if (lastFilledLevel == 1)
            {
                newSLPrice = lockedEntryPrice + (lockedIsLong ? TickSize : -TickSize);
                Print(string.Format("DSB: SL moved to breakeven {0:F2}", newSLPrice));
            }
            else if (TrailSLToLevel && lastFilledLevel > 1)
            {
                newSLPrice = lockedRRLevels[lastFilledLevel - 1];
                Print(string.Format("DSB: SL trailed to {0}R = {1:F2}", lastFilledLevel - 1, newSLPrice));
            }
            else
            {
                return;
            }

            ChangeOrder(stopOrder, remainingQty, 0, newSLPrice);

            // Update RR Tool visual
            if (activeRRTool != null)
                activeRRTool.CurrentSLPrice = newSLPrice;
        }

        private int GetRemainingQty()
        {
            return Math.Max(1, lockedContracts - GetTPFilledQty());
        }

        private int GetTPFilledQty()
        {
            int remainingTPQty = 0;
            foreach (var tp in tpOrders)
                if (tp != null) remainingTPQty += tp.Quantity;

            return lockedContracts - remainingTPQty;
        }

        #endregion

        #region Cleanup

        private void CancelRemainingTPs()
        {
            foreach (var tp in tpOrders)
            {
                if (tp != null && tp.OrderState == OrderState.Working)
                    CancelOrder(tp);
            }
            tpOrders.Clear();
        }

        private void Reset(string message)
        {
            Print(message);
            state           = TradeState.Idle;
            activeRRTool    = null;
            entryOrder      = null;
            stopOrder       = null;
            tpOrders.Clear();
            sumFilled       = 0;
            lastFilledLevel = 0;
        }

        #endregion
    }
}
