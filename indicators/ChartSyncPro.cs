#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Xml.Serialization;
using System.Windows;
using System.Windows.Input;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript
{
    public enum ChartSyncRole { Auto, Master, Slave }
}

namespace NinjaTrader.NinjaScript.Indicators
{
    public class ChartSyncPro : Indicator
    {
        // ── Per-instrument shared state (static = cross-chart) ─────────────
        private class SyncState
        {
            public double   Price;
            public DateTime Time;
            public bool     Active;
        }

        private static readonly Dictionary<string, SyncState> s_states = new Dictionary<string, SyncState>();
        private static readonly List<ChartSyncPro> s_instances = new List<ChartSyncPro>();
        private static readonly object s_lock = new object();

        // ── Per-instance fields ───────────────────────────────────────────
        private bool   isMaster;
        private bool   mouseHooked;
        private int    barPeriodMinutes;
        private string instrumentName;
        private SharpDX.Direct2D1.SolidColorBrush dxBrush;
        private SharpDX.Direct2D1.RenderTarget lastRenderTarget;

        #region OnStateChange
        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Syncs crosshair position between linked chart windows.";
                Name        = "ChartSyncPro";
                Calculate   = Calculate.OnEachTick;
                IsOverlay   = true;
                DisplayInDataBox = false;
                DrawOnPricePanel = true;
                IsAutoScale      = false;

                Role           = ChartSyncRole.Auto;
                CrosshairColor = System.Windows.Media.Brushes.Yellow;
                CrosshairWidth = 1;
                HighlightOpacity = 50;
            }
            else if (State == State.Historical)
            {
                barPeriodMinutes = GetBarPeriodMinutes();
                instrumentName   = Instrument.MasterInstrument.Name;

                lock (s_lock)
                {
                    s_instances.Add(this);
                    if (!s_states.ContainsKey(instrumentName))
                        s_states[instrumentName] = new SyncState();
                }

                ResolveRoles();
            }
            else if (State == State.Realtime)
            {
                ResolveRoles();
                HookMouse();
            }
            else if (State == State.Terminated)
            {
                UnhookMouse();

                lock (s_lock)
                {
                    s_instances.Remove(this);
                }

                ResolveRoles();

                if (dxBrush != null)
                {
                    dxBrush.Dispose();
                    dxBrush = null;
                }
            }
        }
        #endregion

        protected override void OnBarUpdate() { }

        #region Auto-role detection

        private int GetBarPeriodMinutes()
        {
            if (BarsPeriod == null) return 1;

            switch (BarsPeriod.BarsPeriodType)
            {
                case BarsPeriodType.Minute:  return BarsPeriod.Value;
                case BarsPeriodType.Day:     return BarsPeriod.Value * 1440;
                case BarsPeriodType.Week:    return BarsPeriod.Value * 10080;
                case BarsPeriodType.Month:   return BarsPeriod.Value * 43200;
                case BarsPeriodType.Year:    return BarsPeriod.Value * 525600;
                case BarsPeriodType.Second:  return Math.Max(1, BarsPeriod.Value / 60);
                case BarsPeriodType.Tick:    return 1; // smallest
                default:                     return BarsPeriod.Value;
            }
        }

        private static void ResolveRoles()
        {
            lock (s_lock)
            {
                if (s_instances.Count == 0) return;

                // Group by instrument and elect master per instrument
                var instruments = new Dictionary<string, List<ChartSyncPro>>();
                foreach (var inst in s_instances)
                {
                    if (string.IsNullOrEmpty(inst.instrumentName)) continue;
                    if (!instruments.ContainsKey(inst.instrumentName))
                        instruments[inst.instrumentName] = new List<ChartSyncPro>();
                    instruments[inst.instrumentName].Add(inst);
                }

                foreach (var kvp in instruments)
                {
                    var group = kvp.Value;
                    ChartSyncPro largest = null;
                    int maxMinutes = -1;

                    // Find explicit master or largest TF
                    foreach (var inst in group)
                    {
                        if (inst.Role == ChartSyncRole.Master)
                        {
                            largest = inst;
                            maxMinutes = int.MaxValue;
                            break;
                        }
                        if (inst.barPeriodMinutes > maxMinutes)
                        {
                            maxMinutes = inst.barPeriodMinutes;
                            largest    = inst;
                        }
                    }

                    foreach (var inst in group)
                    {
                        bool wasMaster = inst.isMaster;

                        if (inst.Role == ChartSyncRole.Auto)
                            inst.isMaster = (inst == largest);
                        else
                            inst.isMaster = (inst.Role == ChartSyncRole.Master);

                        // Re-hook mouse if role changed mid-session
                        if (inst.isMaster != wasMaster)
                        {
                            if (wasMaster)
                                inst.UnhookMouse();
                            if (inst.isMaster)
                                inst.HookMouse();
                        }
                    }
                }
            }
        }

        private void HookMouse()
        {
            if (mouseHooked || !isMaster || ChartPanel == null) return;
            ChartPanel.MouseMove  += Panel_MouseMove;
            ChartPanel.MouseLeave += Panel_MouseLeave;
            mouseHooked = true;
        }

        private void UnhookMouse()
        {
            if (!mouseHooked || ChartPanel == null) return;
            ChartPanel.MouseMove  -= Panel_MouseMove;
            ChartPanel.MouseLeave -= Panel_MouseLeave;
            mouseHooked = false;
        }

        #endregion

        #region Mouse handlers (Master only)
        private void Panel_MouseMove(object sender, MouseEventArgs e)
        {
            if (ChartControl == null || ChartPanel == null) return;

            System.Windows.Point pos = e.GetPosition(ChartPanel);

            double price = ChartPanel.MaxValue -
                ((pos.Y / ChartPanel.H) * (ChartPanel.MaxValue - ChartPanel.MinValue));

            int slotIdx = (int)ChartControl.GetSlotIndexByX((int)pos.X);
            if (slotIdx < 0) return;

            DateTime time = ChartControl.GetTimeBySlotIndex(slotIdx);

            lock (s_lock)
            {
                SyncState state;
                if (s_states.TryGetValue(instrumentName, out state))
                {
                    state.Price  = price;
                    state.Time   = time;
                    state.Active = true;
                }
            }

            // Refresh all charts for this instrument
            ForceRefresh();
        }

        private void Panel_MouseLeave(object sender, MouseEventArgs e)
        {
            lock (s_lock)
            {
                SyncState state;
                if (s_states.TryGetValue(instrumentName, out state))
                    state.Active = false;
            }
            ForceRefresh();
        }

        private void ForceRefresh()
        {
            // Invalidate ALL charts for this instrument so slaves repaint
            lock (s_lock)
            {
                foreach (var inst in s_instances)
                {
                    if (inst.instrumentName == instrumentName && inst.ChartControl != null)
                    {
                        inst.ChartControl.Dispatcher.InvokeAsync(() =>
                        {
                            inst.ChartControl.InvalidateVisual();
                        });
                    }
                }
            }
        }
        #endregion

        #region OnRender
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null) return;

            // Hook mouse on first render (ChartPanel guaranteed ready here)
            if (isMaster && !mouseHooked)
                HookMouse();

            if (isMaster) return;

            double   price;
            DateTime time;
            bool     active;

            lock (s_lock)
            {
                SyncState state;
                if (!s_states.TryGetValue(instrumentName, out state))
                    return;
                price  = state.Price;
                time   = state.Time;
                active = state.Active;
            }

            if (!active || price == 0) return;

            // Recreate brush if render target changed or first use
            if (dxBrush == null || dxBrush.IsDisposed || lastRenderTarget != RenderTarget)
            {
                if (dxBrush != null && !dxBrush.IsDisposed) dxBrush.Dispose();
                lastRenderTarget = RenderTarget;
            }
            if (dxBrush == null || dxBrush.IsDisposed)
            {
                var wpfColor = ((System.Windows.Media.SolidColorBrush)CrosshairColor).Color;
                float opacity = HighlightOpacity / 100f;
                dxBrush = new SharpDX.Direct2D1.SolidColorBrush(
                    RenderTarget,
                    new SharpDX.Color4(
                        wpfColor.R / 255f, wpfColor.G / 255f,
                        wpfColor.B / 255f, opacity));
            }

            // ── Highlight all bars matching the synced date ────────────
            DateTime syncDate = time.Date;
            int firstBar = -1;
            int lastBar  = -1;

            for (int i = ChartBars.FromIndex; i <= ChartBars.ToIndex; i++)
            {
                if (i < 0 || i >= Bars.Count) continue;
                if (Bars.GetTime(i).Date == syncDate)
                {
                    if (firstBar < 0) firstBar = i;
                    lastBar = i;
                }
            }

            if (firstBar < 0) return;  // date not visible on chart

            float xLeft  = chartControl.GetXByBarIndex(chartControl.BarsArray[0], firstBar);
            float xRight = chartControl.GetXByBarIndex(chartControl.BarsArray[0], lastBar);
            float halfBar = (float)chartControl.BarWidth / 2f + 2f;

            var rect = new SharpDX.RectangleF(
                xLeft - halfBar,
                ChartPanel.Y,
                (xRight - xLeft) + halfBar * 2f,
                ChartPanel.H);

            RenderTarget.FillRectangle(rect, dxBrush);
        }
        #endregion

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Role", GroupName = "ChartSyncPro", Order = 0,
                 Description = "Auto = largest TF becomes master. Override with Master/Slave.")]
        public ChartSyncRole Role { get; set; }

        [XmlIgnore]
        [Display(Name = "Highlight Color", GroupName = "ChartSyncPro", Order = 1)]
        public System.Windows.Media.Brush CrosshairColor { get; set; }

        [Browsable(false)]
        public string CrosshairColorSerializable
        {
            get { return Serialize.BrushToString(CrosshairColor); }
            set { CrosshairColor = Serialize.StringToBrush(value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Highlight Width", GroupName = "ChartSyncPro", Order = 2)]
        [Range(1, 5)]
        public int CrosshairWidth { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Highlight Opacity (%)", GroupName = "ChartSyncPro", Order = 3,
                 Description = "How opaque the bar highlight is on slave charts")]
        [Range(5, 80)]
        public int HighlightOpacity { get; set; }
        #endregion
    }
}

// NinjaScript generated code will be added by NinjaTrader on compile
