#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class CandleFinder : Indicator
    {
        private static DateTime s_targetTime  = DateTime.MinValue;
        private static string   s_instrument  = string.Empty;
        private static bool     s_jumpPending = false;
        private static readonly object s_lock = new object();
        private static readonly List<CandleFinder> s_instances = new List<CandleFinder>();

        private bool       isMaster;
        private bool       mouseHooked;
        private int        barPeriodMinutes;
        private MethodInfo scrollToTimeMethod;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name             = "Candle Finder";
                Description      = "Click an HTF bar to scroll the LTF chart to that date";
                Calculate        = Calculate.OnBarClose;
                IsOverlay        = true;
                DisplayInDataBox = false;
                PaintPriceMarkers = false;
                IsAutoScale      = false;
            }
            else if (State == State.Historical)
            {
                barPeriodMinutes = GetBarPeriodMinutes();
                lock (s_lock) { s_instances.Add(this); }
            }
            else if (State == State.Terminated)
            {
                UnhookMouse();
                lock (s_lock) { s_instances.Remove(this); }
            }
        }

        protected override void OnBarUpdate() { }

        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null) return;

            isMaster = barPeriodMinutes >= 1440;

            if (isMaster && !mouseHooked)
                HookMouse();

            // Cache the ScrollToTime method via reflection
            if (!isMaster && scrollToTimeMethod == null)
            {
                scrollToTimeMethod = chartControl.GetType().GetMethod("ScrollToTime",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (scrollToTimeMethod != null)
                    Print("CandleFinder: Found ScrollToTime method");
                else
                    Print("CandleFinder: ScrollToTime method NOT found");
            }

            if (!isMaster)
            {
                DateTime target;
                string instrument;
                bool pending;

                lock (s_lock)
                {
                    target     = s_targetTime;
                    instrument = s_instrument;
                    pending    = s_jumpPending;
                }

                if (pending && instrument == Instrument.MasterInstrument.Name
                    && target > DateTime.MinValue)
                {
                    lock (s_lock) { s_jumpPending = false; }

                    var cc = chartControl;
                    var t  = target;
                    // Get half the visible time span to center the target
                    TimeSpan halfVisible = TimeSpan.Zero;
                    if (ChartBars != null && ChartBars.FromIndex >= 0 && ChartBars.ToIndex > ChartBars.FromIndex)
                    {
                        DateTime fromTime = Bars.GetTime(ChartBars.FromIndex);
                        DateTime toTime   = Bars.GetTime(ChartBars.ToIndex);
                        halfVisible = TimeSpan.FromTicks((toTime.Ticks - fromTime.Ticks) / 2);
                    }
                    var centerTime = t - halfVisible;

                    cc.Dispatcher.BeginInvoke(
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                        new Action(() =>
                        {
                            try
                            {
                                if (scrollToTimeMethod != null)
                                {
                                    // ScrollToTime(DateTime, bool right)
                                    // right=true puts the time at the right edge
                                    // So we pass centerTime (target minus half visible) to put target in center
                                    scrollToTimeMethod.Invoke(cc, new object[] { centerTime, false });
                                }
                            }
                            catch (Exception ex)
                            {
                                Print("CandleFinder ERROR: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                            }
                        }));
                }
            }
        }

        #region Helpers

        private int GetBarPeriodMinutes()
        {
            if (BarsPeriod == null) return 1;
            switch (BarsPeriod.BarsPeriodType)
            {
                case BarsPeriodType.Minute:  return BarsPeriod.Value;
                case BarsPeriodType.Day:     return BarsPeriod.Value * 1440;
                case BarsPeriodType.Week:    return BarsPeriod.Value * 10080;
                case BarsPeriodType.Month:   return BarsPeriod.Value * 43200;
                case BarsPeriodType.Second:  return Math.Max(1, BarsPeriod.Value / 60);
                case BarsPeriodType.Tick:    return 1;
                default:                     return BarsPeriod.Value;
            }
        }

        #endregion

        #region Mouse handling

        private void HookMouse()
        {
            if (mouseHooked || ChartPanel == null) return;
            ChartPanel.MouseLeftButtonDown += Panel_MouseDown;
            mouseHooked = true;
        }

        private void UnhookMouse()
        {
            if (!mouseHooked || ChartPanel == null) return;
            ChartPanel.MouseLeftButtonDown -= Panel_MouseDown;
            mouseHooked = false;
        }

        private void Panel_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (ChartControl == null) return;

            // Only respond to Shift+Click
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                return;

            Point pos = e.GetPosition(ChartPanel);
            int slotIdx = (int)ChartControl.GetSlotIndexByX((int)pos.X);
            if (slotIdx < 0) return;

            DateTime clickTime = ChartControl.GetTimeBySlotIndex(slotIdx);

            lock (s_lock)
            {
                s_targetTime  = clickTime;
                s_instrument  = Instrument.MasterInstrument.Name;
                s_jumpPending = true;

                foreach (var inst in s_instances)
                {
                    if (inst != this && inst.ChartControl != null)
                    {
                        var cc = inst.ChartControl;
                        cc.Dispatcher.InvokeAsync(() => cc.InvalidateVisual());
                    }
                }
            }
        }

        #endregion
    }
}
