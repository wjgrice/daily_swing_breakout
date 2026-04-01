# Money Management Strategy — Spec v1.0

**Date**: 2026-04-01
**Status**: Draft
**Target**: NinjaTrader 8 Strategy (new repo)

---

## 1. Overview

A NinjaTrader 8 Strategy that manages the full lifecycle of a trade: entry, partial exits, stop-loss trailing, and breakeven management. It reads setup levels from the **RR Tool** (DrawingTool) already placed on the chart, then executes and manages orders according to a configurable exit mode.

### Architecture

```
RR Tool (DrawingTool)          Money Management (Strategy)
  - Visual R:R levels    --->    - Reads RR Tool anchors from DrawObjects
  - Position sizing info         - Places entry order
  - No order logic               - Manages exits per configurable mode
                                 - Trails SL on partial fills
                                 - Reports P&L per trade

Future:
  Swing Detector (Indicator)  --->  Auto Trader (Strategy)
    - Identifies setups              - Combines swing detection + MM
    - Places RR Tool                 - Fully automated
```

### Why a Strategy (not a DrawingTool button)

- Strategies have `OnExecutionUpdate`, `OnOrderUpdate`, `OnBarUpdate` — the full lifecycle hooks needed for partial exits and SL trailing
- Strategies can use the **unmanaged order approach** for full control over multiple simultaneous orders
- Strategies run continuously and can react to price changes (trail SL, move to breakeven)
- DrawingTools are visual-only; embedding order logic fights the framework

---

## 2. Strategy Properties

### Account & Risk (mirrors RR Tool)

| Property | Type | Default | Range | Description |
|----------|------|---------|-------|-------------|
| `AccountName` | string (dropdown) | Sim101 | Account.All | Trading account |
| `RiskPercent` | double | 1.0 | 0.1 - 10.0 | % of equity to risk per trade |

### Exit Mode

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ExitMode` | enum | HalfAndTrail | How to manage exits (see Section 3) |
| `MaxTargetRR` | int | 3 | Maximum R:R level to target (1-6) |
| `BreakevenTriggerRR` | double | 1.0 | R:R level that triggers SL move to breakeven |
| `TrailSLToLevel` | bool | true | Trail SL to most recent filled TP level |

### RR Tool Integration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `RRToolTag` | string | "" | Tag of the RR Tool to read (empty = first found) |
| `AutoDetect` | bool | true | Auto-detect the most recent RR Tool on chart |

### Entry

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EntryType` | enum | Limit | Limit, Market, or StopLimit |
| `EntryOffsetTicks` | int | 0 | Offset from RR Tool entry price (for StopLimit) |

---

## 3. Exit Modes

All exit modes are configurable via the `ExitMode` enum property.

### 3.1 HalfAndTrail (default)

```
Entry fills (N contracts)
  |
  +-- At 1:1 R:R --> Sell N/2 contracts, move SL to breakeven (entry price)
  |
  +-- At MaxTargetRR --> Sell remaining N/2 contracts
  |
  +-- SL hit at any point --> Close all remaining
```

**SL Management**:
- Initial: SL price from RR Tool anchor 0
- After 1:1 partial fill: Move SL to entry price (breakeven)
- Runner rides to MaxTargetRR or SL

### 3.2 ThirdScale

```
Entry fills (N contracts)
  |
  +-- At 1:1 R:R --> Sell N/3, move SL to breakeven
  |
  +-- At 1:2 R:R --> Sell N/3, move SL to 1:1 level
  |
  +-- At 1:3 R:R --> Sell N/3 (or remaining)
  |
  +-- SL hit at any point --> Close all remaining
```

**SL Management**:
- Initial: SL price from RR Tool
- After 1:1 fill: SL → entry price
- After 1:2 fill: SL → 1:1 price
- After 1:3 fill: Position closed

### 3.3 FullAtTarget

```
Entry fills (N contracts)
  |
  +-- At MaxTargetRR --> Sell all N contracts
  |
  +-- SL hit --> Close all
```

Simple bracket: entry + SL + single TP at `MaxTargetRR`. No partial exits, no trailing.

### 3.4 Runner

```
Entry fills (N contracts)
  |
  +-- At 1:1 R:R --> Sell N/2, move SL to breakeven
  |
  +-- At each subsequent R:R level --> Trail SL to previous level
  |
  +-- No fixed target — runner rides until SL is hit
```

**SL Management**:
- Initial: SL price from RR Tool
- After 1:1: SL → breakeven
- After 1:2: SL → 1:1
- After 1:3: SL → 1:2
- ...continues until SL is hit (no MaxTargetRR cap)

### 3.5 Custom (future)

User-defined rules via a configuration object or script. Deferred to v2.

---

## 4. Position Lifecycle

### State Machine

```
Idle
  |
  v
[User enables strategy + RR Tool detected on chart]
  |
  v
PendingEntry ---- Entry Limit/Market submitted
  |
  v
InPosition ------- Entry filled, SL + TP(s) submitted
  |                   |
  |                   +-- OnExecutionUpdate: partial fill → adjust SL, submit next TP
  |                   +-- OnBarUpdate: check trail conditions
  |
  v
Closing ---------- Final TP hit or SL hit
  |
  v
Idle (ready for next RR Tool)
```

### Key State Transitions

| From | Event | To | Action |
|------|-------|----|--------|
| Idle | RR Tool detected + strategy enabled | PendingEntry | Submit entry order |
| PendingEntry | Entry filled | InPosition | Submit initial SL + first TP per exit mode |
| InPosition | TP partial fill | InPosition | Trail SL, submit next TP |
| InPosition | SL hit | Idle | Position closed, log trade |
| InPosition | Final TP hit | Idle | Position closed, log trade |
| PendingEntry | Entry cancelled/rejected | Idle | Log error |

---

## 5. SL Trailing Rules

### Breakeven Move

When price reaches `BreakevenTriggerRR` (default 1:1):
1. Cancel existing SL order
2. Submit new SL at entry price (or entry + 1 tick for guaranteed profit)
3. Log: "SL moved to breakeven"

### Level-by-Level Trail

When `TrailSLToLevel = true` and a partial TP fills:
1. Determine which R:R level just filled (e.g., 1:2)
2. Cancel existing SL
3. Submit new SL at the **previous** R:R level (e.g., 1:1)
4. Use `ChangeOrder()` if the SL order is still working (more efficient than cancel+resubmit)

### Trail Implementation

```csharp
// In OnExecutionUpdate, after a TP partial fill:
if (TrailSLToLevel && lastFilledLevel > 0)
{
    double newSLPrice = GetRRLevelPrice(lastFilledLevel - 1);
    ChangeOrder(stopOrder, stopOrder.Quantity, 0, newSLPrice);
}
```

---

## 6. RR Tool Integration

### Reading Anchors from DrawObjects

The strategy reads the RR Tool's anchor prices from the chart's `DrawObjects` collection:

```csharp
// In OnBarUpdate or OnStateChange(State.Realtime):
foreach (var drawObj in DrawObjects)
{
    if (drawObj is RiskRewardTool rrTool)
    {
        var anchors = rrTool.Anchors.ToArray();
        double slPrice    = anchors[0].Price;   // Anchor 0 = SL
        double entryPrice = anchors[1].Price;   // Anchor 1 = Entry
        double direction  = entryPrice - slPrice;
        bool isLong       = direction > 0;

        // Compute R:R level prices
        for (int r = 1; r <= 6; r++)
            rrLevels[r] = entryPrice + direction * r;
    }
}
```

### Tag Matching

- If `RRToolTag` is set, only read the RR Tool with that specific tag
- If `AutoDetect = true`, use the most recently placed RR Tool
- If multiple RR Tools exist, use the one closest to current price

### Live Anchor Updates

The RR Tool anchors can be dragged while the strategy is running. The strategy should re-read anchor prices on each `OnBarUpdate` to detect changes. If anchors move while in position:
- **Entry not yet filled**: Cancel and resubmit at new price
- **Already in position**: Do NOT change existing orders (anchors locked at entry time)

---

## 7. NinjaTrader API Surface

### Unmanaged Order Approach (recommended)

The strategy should use the **unmanaged approach** for full control over multiple simultaneous exit orders at different R:R levels.

```csharp
protected override void OnStateChange()
{
    if (State == State.SetDefaults)
    {
        IsUnmanaged = true;  // Required for unmanaged approach
    }
}
```

### Key Methods

| Method | Purpose |
|--------|---------|
| `SubmitOrderUnmanaged(...)` | Submit entry, SL, and TP orders |
| `ChangeOrder(order, qty, limitPrice, stopPrice)` | Modify SL price for trailing |
| `CancelOrder(order)` | Cancel a working order |
| `OnExecutionUpdate(...)` | React to fills — submit bracket, trail SL |
| `OnOrderUpdate(...)` | Track order state changes |

### OnExecutionUpdate (preferred over OnOrderUpdate for fills)

Per NinjaTrader docs: "If your strategy logic needs to be driven by order fills, you must use OnExecutionUpdate() instead of OnOrderUpdate()."

```csharp
protected override void OnExecutionUpdate(Execution execution, string executionId,
    double price, int quantity, MarketPosition marketPosition,
    string orderId, DateTime time)
{
    if (entryOrder != null && entryOrder == execution.Order)
    {
        // Entry filled — submit SL + TP(s)
        sumFilled += execution.Quantity;

        if (execution.Order.OrderState == OrderState.Filled
            && sumFilled == execution.Order.Filled)
        {
            SubmitBracketOrders(execution.Order.Filled);
            entryOrder = null;
            sumFilled = 0;
        }
    }

    // Handle TP fills — trail SL
    if (tp1Order != null && tp1Order == execution.Order
        && execution.Order.OrderState == OrderState.Filled)
    {
        TrailSLToBreakeven();
        SubmitNextTP();
    }
}
```

### ChangeOrder for SL Trailing

```csharp
// Move SL to breakeven (entry price)
if (stopOrder != null && stopOrder.OrderState == OrderState.Working)
    ChangeOrder(stopOrder, remainingQty, 0, entryFillPrice);

// Trail SL to previous R:R level
if (stopOrder != null && stopOrder.OrderState == OrderState.Working)
    ChangeOrder(stopOrder, remainingQty, 0, rrLevels[lastFilledLevel - 1]);
```

---

## 8. Order Quantity Splitting

For partial exits, the total contract quantity must be split across TP levels.

### Splitting Rules

| Exit Mode | Contracts | TP1 Qty | TP2 Qty | TP3 Qty |
|-----------|-----------|---------|---------|---------|
| HalfAndTrail | N | floor(N/2) | N - floor(N/2) | — |
| ThirdScale | N | floor(N/3) | floor(N/3) | N - 2*floor(N/3) |
| FullAtTarget | N | N | — | — |
| Runner | N | floor(N/2) | 0 (trail only) | — |

**Remainder rule**: Last tranche gets any remainder from integer division.

**Minimum contracts**: If N < required splits (e.g., 1 contract for ThirdScale), fall back to FullAtTarget.

---

## 9. Future: Automation Hook

### Swing Detector Integration (v2)

The Money Management Strategy will expose a public method or event that an upstream Swing Detector can call to initiate a trade:

```csharp
// Called by Swing Detector indicator or auto-trader strategy
public void InitiateTrade(double entryPrice, double slPrice, bool isLong)
{
    // Programmatically set the levels (bypass RR Tool reading)
    this.entryPrice = entryPrice;
    this.slPrice = slPrice;
    this.isLong = isLong;
    ComputeRRLevels();
    SubmitEntry();
}
```

Alternatively, the Swing Detector could place an RR Tool on the chart, and the MM Strategy auto-detects it — keeping the visual feedback.

### Auto Trader Strategy (v3)

Combines swing detection + money management in a single strategy:
1. Scans for swing setups (ICT patterns, FVG, OB, sweep+reversal)
2. Calculates entry/SL from structure
3. Applies MM rules
4. Fully automated, no manual intervention

---

## 10. Configuration Matrix

All configurable parameters with defaults:

| Parameter | Type | Default | Group |
|-----------|------|---------|-------|
| AccountName | string | Sim101 | Account |
| RiskPercent | double | 1.0% | Account |
| ExitMode | enum | HalfAndTrail | Exit Rules |
| MaxTargetRR | int | 3 | Exit Rules |
| BreakevenTriggerRR | double | 1.0 | Exit Rules |
| TrailSLToLevel | bool | true | Exit Rules |
| RRToolTag | string | "" | Integration |
| AutoDetect | bool | true | Integration |
| EntryType | enum | Limit | Entry |
| EntryOffsetTicks | int | 0 | Entry |

---

## 11. File Structure (new repo)

```
money-management/
├── README.md
├── strategies/
│   └── MoneyManagement.cs        -- Main strategy file
├── tests/
│   └── ... (NinjaTrader replay/sim tests)
└── docs/
    └── spec_money_management_v1.md  -- This spec
```

---

## 12. Verification Plan

### Phase 1: Basic Bracket (FullAtTarget mode)
1. Place RR Tool on ES chart (Sim101)
2. Enable MM Strategy
3. Verify entry Limit order placed at RR Tool entry price
4. Verify SL + TP submitted on entry fill
5. Verify position closes on SL or TP hit

### Phase 2: Partial Exits (HalfAndTrail mode)
1. Place RR Tool, enable strategy
2. Verify N/2 contracts close at 1:1
3. Verify SL moves to breakeven after 1:1 fill
4. Verify remaining N/2 close at MaxTargetRR or SL

### Phase 3: ThirdScale mode
1. Verify 3-tranche exits at 1:1, 1:2, 1:3
2. Verify SL trails: breakeven → 1:1 → 1:2

### Phase 4: Runner mode
1. Verify half exits at 1:1
2. Verify SL trails level-by-level indefinitely
3. Verify runner closes only on SL hit

### Phase 5: Live Anchor Updates
1. Drag RR Tool anchors while strategy is in PendingEntry
2. Verify entry order updates to new price
3. Drag anchors while InPosition — verify orders do NOT change

### Phase 6: Edge Cases
- 1 contract with ThirdScale → falls back to FullAtTarget
- Account disconnected → error handling
- RR Tool deleted while in position → position remains, strategy goes to error state
- Multiple RR Tools on chart → correct one selected
