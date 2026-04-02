#!/bin/bash
# Deploy .cs files to NinjaTrader 8 Custom directories

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
NT_CUSTOM="/mnt/c/Users/Jeremy/Documents/NinjaTrader 8/bin/Custom"

if [ ! -d "$NT_CUSTOM" ]; then
    echo "ERROR: NinjaTrader directory not found: $NT_CUSTOM"
    exit 1
fi

echo "Deploying from $REPO_ROOT to NinjaTrader..."

cp "$REPO_ROOT/drawing_tools/"*.cs "$NT_CUSTOM/DrawingTools/" && \
    echo "  DrawingTools: $(ls "$REPO_ROOT/drawing_tools/"*.cs | xargs -n1 basename | tr '\n' ' ')"

cp "$REPO_ROOT/strategies/"*.cs "$NT_CUSTOM/Strategies/" && \
    echo "  Strategies:   $(ls "$REPO_ROOT/strategies/"*.cs | xargs -n1 basename | tr '\n' ' ')"

if ls "$REPO_ROOT/indicators/"*.cs 1>/dev/null 2>&1; then
    cp "$REPO_ROOT/indicators/"*.cs "$NT_CUSTOM/Indicators/" && \
        echo "  Indicators:   $(ls "$REPO_ROOT/indicators/"*.cs | xargs -n1 basename | tr '\n' ' ')"
fi

echo "Done. Open NinjaTrader and compile (F5 in NinjaScript Editor)."
