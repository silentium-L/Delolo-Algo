#nullable enable
using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Indicators
{
    [Indicator(IsOverlay = true, AccessRights = AccessRights.None, AutoRescale = false,
        TimeZone = TimeZones.UTC)]
    public class RegimeMaster : Indicator
    {
        #region Parameters

        [Parameter("Regime TimeFrame", DefaultValue = "Daily", Group = "General")]
        public TimeFrame RegimeTimeFrame { get; set; }

        [Parameter("Period", DefaultValue = 20, MinValue = 2, Group = "General")]
        public int Period { get; set; }

        [Parameter("Bullish Color", DefaultValue = "Green", Group = "Colors")]
        public Color BullishColor { get; set; }

        [Parameter("Bearish Color", DefaultValue = "Red", Group = "Colors")]
        public Color BearishColor { get; set; }

        [Parameter("Opacity (0-255)", DefaultValue = 30, MinValue = 0, MaxValue = 255, Group = "Colors")]
        public int Opacity { get; set; }

        #endregion

        #region Outputs

        [Output("Regime Signal", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries RegimeSignal { get; set; } = null!;

        #endregion

        #region Fields

        private Bars _mtfBars = null!;
        private SimpleMovingAverage _mtfSma = null!;

        // Tracks the current open box: name and starting bar index on the chart timeframe
        private string? _currentBoxName;
        private int _currentBoxStartIndex;
        private bool _currentIsBullish;

        // Cache last drawn box end-index to avoid redundant redraws
        private int _lastDrawnEndIndex = -1;

        #endregion

        #region Lifecycle

        protected override void Initialize()
        {
            _mtfBars = MarketData.GetBars(RegimeTimeFrame);

            // Ensure enough MTF history is loaded for the SMA calculation
            int required = Period * 2;
            while (_mtfBars.Count < required)
            {
                var result = _mtfBars.LoadMoreHistory();
                if (result == 0) break; // no more history available
            }

            _mtfSma = Indicators.SimpleMovingAverage(_mtfBars.ClosePrices, Period);
        }

        public override void Calculate(int index)
        {
            // Map the current chart bar to the corresponding MTF bar index
            int mtfIndex = _mtfBars.OpenTimes.GetIndexByTime(Bars.OpenTimes[index]);

            // Use the previous closed MTF bar to avoid repainting
            int signalMtfIndex = mtfIndex - 1;
            if (signalMtfIndex < Period) return;

            double mtfClose = _mtfBars.ClosePrices[signalMtfIndex];
            double smaValue = _mtfSma.Result[signalMtfIndex];
            if (double.IsNaN(smaValue)) return;

            bool isBullish = mtfClose > smaValue;
            RegimeSignal[index] = isBullish ? 1.0 : -1.0;

            string boxName = BuildBoxName(isBullish, _currentBoxStartIndex);

            if (_currentBoxName == null)
            {
                // First bar — start the initial box
                StartNewBox(isBullish, index);
                return;
            }

            if (isBullish != _currentIsBullish)
            {
                // Regime changed: finalise the old box at the previous bar and start a new one
                DrawBox(_currentBoxName, _currentIsBullish, _currentBoxStartIndex, index - 1);
                StartNewBox(isBullish, index);
                return;
            }

            // Same regime: extend the current box only if the end index changed
            if (_lastDrawnEndIndex != index)
            {
                DrawBox(_currentBoxName, _currentIsBullish, _currentBoxStartIndex, index);
                _lastDrawnEndIndex = index;
            }
        }

        #endregion

        #region Helpers

        private void StartNewBox(bool isBullish, int startIndex)
        {
            _currentIsBullish = isBullish;
            _currentBoxStartIndex = startIndex;
            _currentBoxName = BuildBoxName(isBullish, startIndex);
            _lastDrawnEndIndex = -1;

            DrawBox(_currentBoxName, isBullish, startIndex, startIndex);
            _lastDrawnEndIndex = startIndex;
        }

        private void DrawBox(string name, bool isBullish, int startIndex, int endIndex)
        {
            if (Chart == null) return; // backtest/optimizer context — no visual chart

            Color baseColor = isBullish ? BullishColor : BearishColor;
            Color fillColor = Color.FromArgb(Opacity, baseColor.R, baseColor.G, baseColor.B);

            // Use extreme price values so the box spans the full vertical chart area
            const double yTop    = 999_999.0;
            const double yBottom = 0.0;

            var rect = Chart.DrawRectangle(name, startIndex, yTop, endIndex, yBottom, fillColor);
            rect.IsFilled      = true;
            rect.IsInteractive = false;
        }

        private static string BuildBoxName(bool isBullish, int startIndex) =>
            $"regime_{(isBullish ? "bull" : "bear")}_{startIndex}";

        #endregion
    }
}
