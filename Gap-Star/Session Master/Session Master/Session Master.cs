#nullable enable
using System;
using cAlgo.API;

namespace cAlgo.Indicators
{
    [Indicator(IsOverlay = true, AccessRights = AccessRights.None, AutoRescale = false,
               TimeZone = TimeZones.UTC)]
    public class SessionAndLiquidityLevelsUTC : Indicator
    {
        #region Parameters

        [Parameter("HK Start (UTC)", DefaultValue = "01:00", Group = "Hong Kong Session")]
        public string HkStartUTC { get; set; } = "01:00";

        [Parameter("HK End (UTC)", DefaultValue = "09:00", Group = "Hong Kong Session")]
        public string HkEndUTC { get; set; } = "09:00";

        [Parameter("London Start (UTC)", DefaultValue = "08:00", Group = "London Session")]
        public string LonStartUTC { get; set; } = "08:00";

        [Parameter("London End (UTC)", DefaultValue = "16:00", Group = "London Session")]
        public string LonEndUTC { get; set; } = "16:00";

        [Parameter("NY Start (UTC)", DefaultValue = "13:00", Group = "New York Session")]
        public string NyStartUTC { get; set; } = "13:00";

        [Parameter("NY End (UTC)", DefaultValue = "21:00", Group = "New York Session")]
        public string NyEndUTC { get; set; } = "21:00";

        [Parameter("Box Opacity", DefaultValue = 30, MinValue = 5, MaxValue = 100, Group = "Display")]
        public int Opacity { get; set; }

        [Parameter("Show Yesterday/Last Week Highs", DefaultValue = true, Group = "Display")]
        public bool ShowDailyWeeklyHighs { get; set; }

        #endregion

        #region Outputs

        /// <summary>0 = none, 1 = HK, 2 = London, 3 = NY. Priority: NY > London > HK.</summary>
        [Output("Current Session", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries CurrentSessionOut { get; set; } = null!;

        [Output("Session High", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries SessionHighOut { get; set; } = null!;

        [Output("Session Low", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries SessionLowOut { get; set; } = null!;

        #endregion

        #region Fields

        private TimeSpan _hkStart,  _hkEnd;
        private TimeSpan _lonStart, _lonEnd;
        private TimeSpan _nyStart,  _nyEnd;

        private Bars _dailyBars  = null!;
        private Bars _weeklyBars = null!;

        private readonly SessionState _hk     = new SessionState();
        private readonly SessionState _london = new SessionState();
        private readonly SessionState _ny     = new SessionState();

        // ARGB base colors (full opacity) – applied with user Opacity at draw time
        private static readonly Color HkBase     = Color.FromHex("#FF00AA00");
        private static readonly Color LondonBase = Color.FromHex("#FFCC0000");
        private static readonly Color NyBase     = Color.FromHex("#FF888888");

        // Yesterday / Last Week level colors
        private static readonly Color YestColor = Color.FromArgb(180, 210, 210, 210);
        private static readonly Color WeekColor = Color.FromArgb(180, 255, 215,   0);

        #endregion

        #region Lifecycle

        protected override void Initialize()
        {
            _hkStart  = TimeSpan.Parse(HkStartUTC);
            _hkEnd    = TimeSpan.Parse(HkEndUTC);
            _lonStart = TimeSpan.Parse(LonStartUTC);
            _lonEnd   = TimeSpan.Parse(LonEndUTC);
            _nyStart  = TimeSpan.Parse(NyStartUTC);
            _nyEnd    = TimeSpan.Parse(NyEndUTC);

            _dailyBars  = MarketData.GetBars(TimeFrame.Daily);
            _weeklyBars = MarketData.GetBars(TimeFrame.Weekly);
        }

        public override void Calculate(int index)
        {
            if (index < 1) return;

            var barTime   = Bars.OpenTimes[index];
            var timeOfDay = barTime.TimeOfDay;

            ProcessSession(index, barTime, timeOfDay, _hk,     _hkStart,  _hkEnd,  "HK",     HkBase);
            ProcessSession(index, barTime, timeOfDay, _london, _lonStart, _lonEnd, "London", LondonBase);
            ProcessSession(index, barTime, timeOfDay, _ny,     _nyStart,  _nyEnd,  "NY",     NyBase);

            if (_ny.InSession)
            {
                CurrentSessionOut[index] = 3;
                SessionHighOut[index]    = _ny.SessionHigh;
                SessionLowOut[index]     = _ny.SessionLow;
            }
            else if (_london.InSession)
            {
                CurrentSessionOut[index] = 2;
                SessionHighOut[index]    = _london.SessionHigh;
                SessionLowOut[index]     = _london.SessionLow;
            }
            else if (_hk.InSession)
            {
                CurrentSessionOut[index] = 1;
                SessionHighOut[index]    = _hk.SessionHigh;
                SessionLowOut[index]     = _hk.SessionLow;
            }
            else
            {
                CurrentSessionOut[index] = 0;
                SessionHighOut[index]    = double.NaN;
                SessionLowOut[index]     = double.NaN;
            }

            if (ShowDailyWeeklyHighs)
                DrawDailyWeeklyHighs(index, barTime);
        }

        #endregion

        #region Session State Machine

        private void ProcessSession(
            int index, DateTime barTime, TimeSpan timeOfDay,
            SessionState state, TimeSpan start, TimeSpan end,
            string name, Color baseColor)
        {
            bool nowInSession = IsTimeInSession(timeOfDay, start, end);

            if (!state.InSession && nowInSession)
            {
                // Close the previous session's extending lines at exact new-session start
                if (state.HighLine != null)
                {
                    state.HighLine.Time2 = barTime;
                    state.HighLine.Y2    = state.HighLine.Y1;
                }
                if (state.LowLine != null)
                {
                    state.LowLine.Time2 = barTime;
                    state.LowLine.Y2    = state.LowLine.Y1;
                }

                state.InSession   = true;
                state.StartIndex  = index;
                state.StartTime   = barTime;
                state.SessionHigh = Bars.HighPrices[index];
                state.SessionLow  = Bars.LowPrices[index];
                state.DateKey     = barTime.ToString("yyyyMMdd");
                state.HighLine    = null;
                state.LowLine     = null;
            }
            else if (state.InSession && nowInSession)
            {
                if (Bars.HighPrices[index] > state.SessionHigh)
                    state.SessionHigh = Bars.HighPrices[index];
                if (Bars.LowPrices[index] < state.SessionLow)
                    state.SessionLow = Bars.LowPrices[index];
            }
            else if (state.InSession && !nowInSession)
            {
                // Session over – finalise box on last session bar, extend lines to approx +1 day
                state.InSession = false;
                DrawSessionBox(index - 1, state, name, baseColor);

                try
                {
                    if (state.HighLine != null)
                        state.HighLine.Time2 = barTime.AddDays(1);
                    if (state.LowLine != null)
                        state.LowLine.Time2 = barTime.AddDays(1);
                }
                catch
                {
                    // Chart object may be invalidated between bars in non-visual backtests
                }

                // Drop references — next session re-creates its own lines
                state.HighLine = null;
                state.LowLine  = null;
                return;
            }

            if (state.InSession)
            {
                DrawSessionBox(index, state, name, baseColor);
                DrawSessionLines(barTime, state, name, baseColor);
            }
        }

        private void DrawSessionBox(int endIndex, SessionState state, string name, Color baseColor)
        {
            var fillColor = Color.FromArgb(Opacity, baseColor.R, baseColor.G, baseColor.B);
            var rect = Chart.DrawRectangle(
                $"Box_{name}_{state.DateKey}",
                state.StartIndex, state.SessionHigh,
                endIndex,         state.SessionLow,
                fillColor);
            rect.IsFilled      = true;
            rect.IsInteractive = false;
        }

        private void DrawSessionLines(DateTime barTime, SessionState state, string name, Color baseColor)
        {
            var lineColor = Color.FromArgb(200, baseColor.R, baseColor.G, baseColor.B);
            string dateKey = state.DateKey;

            var hl = Chart.DrawTrendLine(
                $"HighLine_{name}_{dateKey}",
                state.StartTime, state.SessionHigh,
                barTime,         state.SessionHigh,
                lineColor, 1, LineStyle.Solid);
            hl.ExtendToInfinity = false;
            hl.IsInteractive    = false;
            state.HighLine      = hl;

            var lowColor = Color.FromArgb(120, baseColor.R, baseColor.G, baseColor.B);
            var ll = Chart.DrawTrendLine(
                $"LowLine_{name}_{dateKey}",
                state.StartTime, state.SessionLow,
                barTime,         state.SessionLow,
                lowColor, 1, LineStyle.Solid);
            ll.ExtendToInfinity = false;
            ll.IsInteractive    = false;
            state.LowLine       = ll;
        }

        #endregion

        #region Yesterday & Last Week Highs / Lows

        private void DrawDailyWeeklyHighs(int index, DateTime barTime)
        {
            int dailyIdx = _dailyBars.OpenTimes.GetIndexByTime(barTime);
            if (dailyIdx > 0)
            {
                var    dayStart      = _dailyBars.OpenTimes[dailyIdx];
                double prevDayHigh   = _dailyBars.HighPrices[dailyIdx - 1];
                double prevDayLow    = _dailyBars.LowPrices[dailyIdx - 1];
                string dk            = dayStart.ToString("yyyyMMdd");

                DrawLevel($"YestHigh_{dk}", dayStart, barTime, prevDayHigh, YestColor, LineStyle.Solid);
                DrawLevel($"YestLow_{dk}",  dayStart, barTime, prevDayLow,  YestColor, LineStyle.Solid);
            }

            int weeklyIdx = _weeklyBars.OpenTimes.GetIndexByTime(barTime);
            if (weeklyIdx > 0)
            {
                var    weekStart     = _weeklyBars.OpenTimes[weeklyIdx];
                double prevWeekHigh  = _weeklyBars.HighPrices[weeklyIdx - 1];
                double prevWeekLow   = _weeklyBars.LowPrices[weeklyIdx - 1];
                string wk            = weekStart.ToString("yyyyMMdd");

                DrawLevel($"WeekHigh_{wk}", weekStart, barTime, prevWeekHigh, WeekColor, LineStyle.Solid);
                DrawLevel($"WeekLow_{wk}",  weekStart, barTime, prevWeekLow,  WeekColor, LineStyle.Solid);
            }
        }

        private void DrawLevel(string id, DateTime t1, DateTime t2, double price,
            Color color, LineStyle style)
        {
            var line = Chart.DrawTrendLine(id, t1, price, t2, price, color, 1, style);
            line.ExtendToInfinity = false;
            line.IsInteractive    = false;
        }

        #endregion

        #region Helpers

        // Handles sessions that wrap past midnight (e.g. 22:00 – 06:00).
        private static bool IsTimeInSession(TimeSpan time, TimeSpan start, TimeSpan end)
        {
            if (start <= end)
                return time >= start && time < end;
            return time >= start || time < end;
        }

        #endregion
    }

    internal sealed class SessionState
    {
        public bool            InSession   { get; set; }
        public int             StartIndex  { get; set; }
        public DateTime        StartTime   { get; set; }
        public double          SessionHigh { get; set; }
        public double          SessionLow  { get; set; }
        public string          DateKey     { get; set; } = string.Empty;
        public ChartTrendLine? HighLine    { get; set; }
        public ChartTrendLine? LowLine     { get; set; }
    }
}
