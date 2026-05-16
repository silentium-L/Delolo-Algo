#nullable enable
using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Indicators
{
    [Indicator(IsOverlay = true, AccessRights = AccessRights.None, AutoRescale = false,
        TimeZone = TimeZones.UTC)]
    public class MultiTimeframeRsiCandles : Indicator
    {
        #region Parameters

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 2, Group = "RSI")]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Source", Group = "RSI")]
        public DataSeries RsiSource { get; set; } = null!;

        [Parameter("Overbought Level", DefaultValue = 70.0, MinValue = 50.0, MaxValue = 100.0, Group = "Levels")]
        public double OverboughtLevel { get; set; }

        [Parameter("Oversold Level", DefaultValue = 30.0, MinValue = 0.0, MaxValue = 50.0, Group = "Levels")]
        public double OversoldLevel { get; set; }

        [Parameter("TimeFrame 2", DefaultValue = "Hour", Group = "Timeframes")]
        public TimeFrame TimeFrame2 { get; set; } = null!;

        [Parameter("TimeFrame 3", DefaultValue = "Hour4", Group = "Timeframes")]
        public TimeFrame TimeFrame3 { get; set; } = null!;

        [Parameter("Color Bars", DefaultValue = true, Group = "Display")]
        public bool ColorBars { get; set; }

        #endregion

        #region Outputs

        [Output("RSI TF1", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries Rsi1Out { get; set; } = null!;

        [Output("RSI TF2", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries Rsi2Out { get; set; } = null!;

        [Output("RSI TF3", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries Rsi3Out { get; set; } = null!;

        /// <summary>Number of TFs (0..3) with RSI &lt;= OversoldLevel on the closed bar.</summary>
        [Output("Oversold Count", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries OversoldCountOut { get; set; } = null!;

        /// <summary>Number of TFs (0..3) with RSI &gt;= OverboughtLevel on the closed bar.</summary>
        [Output("Overbought Count", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries OverboughtCountOut { get; set; } = null!;

        #endregion

        #region Fields

        private RelativeStrengthIndex _rsiCurrent = null!;
        private RelativeStrengthIndex _rsiTf2     = null!;
        private RelativeStrengthIndex _rsiTf3     = null!;
        private Bars                  _mtfBars2   = null!;
        private Bars                  _mtfBars3   = null!;

        private const int WarmupBars = 100;

        #endregion

        #region Lifecycle

        protected override void Initialize()
        {
            _rsiCurrent = Indicators.RelativeStrengthIndex(RsiSource, RsiPeriod);

            _mtfBars2 = MarketData.GetBars(TimeFrame2);
            _mtfBars3 = MarketData.GetBars(TimeFrame3);

            while (_mtfBars2.Count < WarmupBars)
            {
                if (_mtfBars2.LoadMoreHistory() == 0) break;
            }

            while (_mtfBars3.Count < WarmupBars)
            {
                if (_mtfBars3.LoadMoreHistory() == 0) break;
            }

            _rsiTf2 = Indicators.RelativeStrengthIndex(_mtfBars2.ClosePrices, RsiPeriod);
            _rsiTf3 = Indicators.RelativeStrengthIndex(_mtfBars3.ClosePrices, RsiPeriod);
        }

        public override void Calculate(int index)
        {
            // Default to NaN/0 so consumers can detect warmup unambiguously
            Rsi1Out[index]            = double.NaN;
            Rsi2Out[index]            = double.NaN;
            Rsi3Out[index]            = double.NaN;
            OversoldCountOut[index]   = 0;
            OverboughtCountOut[index] = 0;

            double rsi1 = _rsiCurrent.Result[index];
            if (double.IsNaN(rsi1)) return;

            int tf2Idx = _mtfBars2.OpenTimes.GetIndexByTime(Bars.OpenTimes[index]);
            int tf3Idx = _mtfBars3.OpenTimes.GetIndexByTime(Bars.OpenTimes[index]);
            if (tf2Idx < 0 || tf3Idx < 0) return;

            double rsi2 = _rsiTf2.Result[tf2Idx];
            double rsi3 = _rsiTf3.Result[tf3Idx];
            if (double.IsNaN(rsi2) || double.IsNaN(rsi3)) return;

            Rsi1Out[index] = rsi1;
            Rsi2Out[index] = rsi2;
            Rsi3Out[index] = rsi3;

            int overboughtCount = 0;
            int oversoldCount   = 0;

            if      (rsi1 >= OverboughtLevel) overboughtCount++;
            else if (rsi1 <= OversoldLevel)   oversoldCount++;

            if      (rsi2 >= OverboughtLevel) overboughtCount++;
            else if (rsi2 <= OversoldLevel)   oversoldCount++;

            if      (rsi3 >= OverboughtLevel) overboughtCount++;
            else if (rsi3 <= OversoldLevel)   oversoldCount++;

            OversoldCountOut[index]   = oversoldCount;
            OverboughtCountOut[index] = overboughtCount;

            if (ColorBars && Chart != null)
            {
                if      (overboughtCount == 3) Chart.SetBarColor(index, Color.Red);
                else if (overboughtCount == 2) Chart.SetBarColor(index, Color.Orange);
                else if (overboughtCount == 1) Chart.SetBarColor(index, Color.Yellow);
                else if (oversoldCount   == 3) Chart.SetBarColor(index, Color.Green);
                else if (oversoldCount   == 2) Chart.SetBarColor(index, Color.Blue);
                else if (oversoldCount   == 1) Chart.SetBarColor(index, Color.Magenta);
            }
        }

        #endregion
    }
}
