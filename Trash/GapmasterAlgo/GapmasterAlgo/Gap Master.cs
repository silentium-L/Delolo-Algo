#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.API.Indicators;

namespace cAlgo.Indicators
{
    // [Indicator] attribute intentionally omitted: this assembly carries [Robot] on
    // GapMasterAlgo, and cTrader.Automate allows only one algo type per DLL. GapRadar
    // still works as a callable indicator via Indicators.GetIndicator<GapRadar>() — the
    // runtime only needs the Indicator base class, not the attribute.
    public class GapRadar : Indicator
    {
        #region Constants

        private const int    LiveBarBoxExtension  = 5;
        private const int    DefaultProjectionBars = 50;
        private const double MaxProbability        = 0.99;

        #endregion

        #region Parameters

        [Parameter("Lookback Bars", DefaultValue = 5000, MinValue = 500, Group = "General")]
        public int Lookback { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 5, Group = "General")]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Type", DefaultValue = MovingAverageType.Simple, Group = "General")]
        public MovingAverageType AtrType { get; set; }

        [Parameter("Min Gap Size (ATR mult)", DefaultValue = 0.08, MinValue = 0.01, Step = 0.01, Group = "General")]
        public double MinGapAtr { get; set; }

        [Parameter("Fill Window (Bars)", DefaultValue = 200, MinValue = 10, Group = "General")]
        public int FillWindow { get; set; }

        [Parameter("Top N Projections", DefaultValue = 3, MinValue = 1, MaxValue = 10, Group = "Display")]
        public int TopN { get; set; }

        [Parameter("Show Filled Gaps", DefaultValue = true, Group = "Display")]
        public bool ShowFilled { get; set; }

        [Parameter("Show Weekend Gaps", DefaultValue = true, Group = "Gap Types")]
        public bool ShowWeekend { get; set; }

        [Parameter("Show Session Gaps", DefaultValue = true, Group = "Gap Types")]
        public bool ShowSession { get; set; }

        [Parameter("Show News Gaps", DefaultValue = true, Group = "Gap Types")]
        public bool ShowNews { get; set; }

        [Parameter("Show FVG", DefaultValue = true, Group = "Gap Types")]
        public bool ShowFvg { get; set; }

        [Parameter("Show Liquidity Gaps", DefaultValue = true, Group = "Gap Types")]
        public bool ShowLiquidity { get; set; }

        [Parameter("News Gap Trigger (ATR mult)", DefaultValue = 1.8, MinValue = 0.5, Step = 0.1, Group = "Detection")]
        public double NewsGapAtr { get; set; }

        [Parameter("Session Hour 1 (UTC)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int SessionHour1 { get; set; }

        [Parameter("Session Hour 2 (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int SessionHour2 { get; set; }

        [Parameter("Session Hour 3 (UTC)", DefaultValue = 13, MinValue = 0, MaxValue = 23, Group = "Session")]
        public int SessionHour3 { get; set; }

        [Parameter("Custom Session Hours (CSV UTC)", DefaultValue = "", Group = "Session")]
        public string CustomSessionHours { get; set; } = string.Empty;

        [Parameter("Distance Decay (ATR)", DefaultValue = 5.0, MinValue = 1.0, Step = 0.5, Group = "Probability")]
        public double DistanceDecay { get; set; }

        [Parameter("Age Decay (Bars)", DefaultValue = 500, MinValue = 50, Group = "Probability")]
        public int AgeDecay { get; set; }

        #endregion

        #region Outputs

        [Output("Nearest Gap Price", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries NearestGapPrice { get; set; } = null!;

        [Output("Nearest Gap Probability", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries NearestGapProb { get; set; } = null!;

        [Output("Nearest Gap Type", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries NearestGapType { get; set; } = null!;

        [Output("Active Gaps Above", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries GapsAbove { get; set; } = null!;

        [Output("Active Gaps Below", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries GapsBelow { get; set; } = null!;

        [Output("Is Confirmed", LineColor = "Transparent", PlotType = PlotType.Line)]
        public IndicatorDataSeries IsConfirmedSignal { get; set; } = null!;

        #endregion

        #region Public API

        public List<Gap> ActiveGaps { get; private set; } = new List<Gap>();
        public List<Gap> AllGaps    { get; private set; } = new List<Gap>();

        #endregion

        #region Fields

        private AverageTrueRange        _atr   = null!;
        private ExponentialMovingAverage _ema50 = null!;
        private FillStatistics          _stats  = null!;
        private bool _initialized       = false;
        private int  _tentativeBarIndex = -1;

        private double       _minGapAtrEffective;
        private int          _fillWindowEffective;
        private int          _projectionBars;
        private HashSet<int> _sessionHours = new HashSet<int>();

        private readonly HashSet<string> _renderedBoxIds        = new HashSet<string>();
        private readonly HashSet<string> _renderedProjIds       = new HashSet<string>();
        private readonly List<Gap>       _tentativeGaps         = new List<Gap>();
        private readonly List<Gap>       _tentativelyFilledGaps = new List<Gap>();

        private readonly Dictionary<string, (int endIdx, bool isFilled)> _boxCache
            = new Dictionary<string, (int, bool)>();

        private static readonly IReadOnlyDictionary<GapType, Color> TypeColors =
            new Dictionary<GapType, Color>
            {
                { GapType.Weekend,   Color.FromHex("#FFCC00FF") },
                { GapType.Session,   Color.FromHex("#FFFF8800") },
                { GapType.News,      Color.FromHex("#FFFF3333") },
                { GapType.FVG,       Color.FromHex("#FF3399FF") },
                { GapType.Liquidity, Color.FromHex("#FF888888") }
            };

        #endregion

        #region Lifecycle

        protected override void Initialize()
        {
            _atr   = Indicators.AverageTrueRange(AtrPeriod, AtrType);
            _ema50 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 50);
            _stats = new FillStatistics();

            _minGapAtrEffective  = MinGapAtr;
            _fillWindowEffective = FillWindow;

            if (Bars.TimeFrame == TimeFrame.Minute5)
                _projectionBars = 60;
            else if (Bars.TimeFrame == TimeFrame.Minute15)
                _projectionBars = 32;
            else
                _projectionBars = DefaultProjectionBars;

            _sessionHours = ParseCustomSessionHours(CustomSessionHours);
            if (_sessionHours.Count == 0)
                _sessionHours = new HashSet<int> { SessionHour1, SessionHour2, SessionHour3 };
        }

        private static HashSet<int> ParseCustomSessionHours(string csv)
        {
            var result = new HashSet<int>();
            if (string.IsNullOrWhiteSpace(csv)) return result;
            foreach (var part in csv.Split(','))
                if (int.TryParse(part.Trim(), out int h) && h >= 0 && h <= 23)
                    result.Add(h);
            return result;
        }

        public override void Calculate(int index)
        {
            NearestGapPrice[index]   = double.NaN;
            NearestGapProb[index]    = 0;
            NearestGapType[index]    = 0;
            GapsAbove[index]         = 0;
            GapsBelow[index]         = 0;
            IsConfirmedSignal[index] = IsLastBar ? 0 : 1;

            if (!_initialized && index >= AtrPeriod + 3)
            {
                BuildHistoricalStatistics(index);
                _initialized = true;
                RenderAllHistoricalGaps(index);
            }

            if (!_initialized || index < AtrPeriod + 3) return;

            if (IsLastBar && _tentativeBarIndex != index)
            {
                ConfirmTentativeGaps(_tentativeBarIndex);
                _tentativeBarIndex = index;
            }
            else if (!IsLastBar && _tentativeBarIndex >= 0)
            {
                ConfirmTentativeGaps(_tentativeBarIndex);
                _tentativeBarIndex = -1;
            }

            DetectGapsAtBar(index);
            UpdateGapFillStatus(index);
            RecalculateProbabilities(index);
            UpdateOutputs(index);

            if (index % 100 == 0)
                PruneAllGaps(index);

            RenderGapsDelta(index);
        }

        #endregion

        #region Historical Rendering

        private void RenderAllHistoricalGaps(int upToIndex)
        {
            foreach (var gap in AllGaps)
                if (!gap.IsFilled && !gap.IsAgedOut && !ActiveGaps.Contains(gap))
                    ActiveGaps.Add(gap);

            RecalculateProbabilities(upToIndex);

            foreach (var gap in AllGaps)
                DrawGapBox(gap, upToIndex);

            var topProjections = ActiveGaps
                .OrderByDescending(g => g.HitProbability)
                .Take(TopN)
                .ToList();

            int rank = 1;
            foreach (var proj in topProjections)
                DrawProjectionLine(proj, upToIndex, rank++);

            UpdateOutputs(upToIndex);

            Print($"[GapRadar] Init: {AllGaps.Count} total, {ActiveGaps.Count} active");
        }

        #endregion

        #region Tentative State

        private void ConfirmTentativeGaps(int barIndex)
        {
            if (barIndex < 0) return;

            for (int i = _tentativeGaps.Count - 1; i >= 0; i--)
            {
                var gap = _tentativeGaps[i];
                if (gap.CreatedBarIndex != barIndex) continue;

                if (IsGapStillValidAtClose(gap, barIndex))
                    gap.IsConfirmed = true;
                else
                {
                    AllGaps.Remove(gap);
                    ActiveGaps.Remove(gap);
                    _tentativelyFilledGaps.Remove(gap);
                    RemoveGapChartObjects(gap);
                }

                _tentativeGaps.RemoveAt(i);
            }

            for (int i = _tentativelyFilledGaps.Count - 1; i >= 0; i--)
            {
                var gap = _tentativelyFilledGaps[i];
                gap.IsFilledTentative = false;

                if (IsGapFilled(gap, barIndex))
                {
                    gap.IsFilled       = true;
                    gap.FilledBarIndex = barIndex;
                    _stats.AddSample(gap.Type, gap.SizeInAtr, true, barIndex - gap.CreatedBarIndex);
                    ActiveGaps.Remove(gap);
                }

                _tentativelyFilledGaps.RemoveAt(i);
            }
        }

        private bool IsGapStillValidAtClose(Gap gap, int barIndex)
        {
            if (barIndex < 0 || barIndex >= Bars.Count) return true;
            double mid = (gap.Top + gap.Bottom) / 2.0;
            return !(Bars.LowPrices[barIndex] <= mid && Bars.HighPrices[barIndex] >= mid);
        }

        private void RemoveGapChartObjects(Gap gap)
        {
            if (Chart == null) return;

            string boxId  = $"gap_{gap.Type}_{gap.CreatedBarIndex}";
            string projId = $"proj_{gap.Type}_{gap.CreatedBarIndex}";

            Chart.RemoveObject(boxId);
            Chart.RemoveObject(projId);
            Chart.RemoveObject(projId + "_lbl");

            _renderedBoxIds.Remove(boxId);
            _renderedProjIds.Remove(projId);
            _boxCache.Remove(boxId);
        }

        #endregion

        #region Historical Statistics

        private void BuildHistoricalStatistics(int upToIndex)
        {
            try
            {
                int startIdx = Math.Max(AtrPeriod + 3, upToIndex - Lookback);

                for (int i = startIdx; i <= upToIndex; i++)
                    DetectGapsAtBar(i, addToActive: true, isHistoricalScan: true);

                foreach (var gap in AllGaps)
                {
                    int checkUntil = Math.Min(gap.CreatedBarIndex + _fillWindowEffective, upToIndex);
                    for (int j = gap.CreatedBarIndex + 1; j <= checkUntil; j++)
                    {
                        if (j >= Bars.Count) break;
                        if (IsGapFilled(gap, j))
                        {
                            gap.FilledBarIndex = j;
                            gap.IsFilled       = true;
                            _stats.AddSample(gap.Type, gap.SizeInAtr, true, j - gap.CreatedBarIndex);
                            break;
                        }
                    }

                    if (!gap.IsFilled && upToIndex - gap.CreatedBarIndex >= _fillWindowEffective)
                    {
                        gap.IsAgedOut = true;
                        _stats.AddSample(gap.Type, gap.SizeInAtr, false, _fillWindowEffective);
                    }
                }

                _stats.Build();
            }
            catch (Exception ex)
            {
                Print($"[GapRadar] BuildHistoricalStatistics error: {ex.Message}");
            }
        }

        #endregion

        #region Gap Detection

        private void DetectGapsAtBar(int index, bool addToActive = true, bool isHistoricalScan = false)
        {
            if (index < 3 || index >= Bars.Count) return;

            double atrVal = _atr.Result[index];
            if (!double.IsFinite(atrVal) || atrVal <= 0) return;

            double minSize   = atrVal * _minGapAtrEffective;
            double open      = Bars.OpenPrices[index];
            double prevClose = Bars.ClosePrices[index - 1];
            double gapSize   = Math.Abs(open - prevClose);
            bool   isLiveBar = IsLastBar && !isHistoricalScan;

            bool isNews    = gapSize >= atrVal * NewsGapAtr;
            bool isWeekend = IsWeekendBoundary(index);
            bool isSession = IsSessionBoundary(index);

            if (ShowNews && isNews)
            {
                var gap = CreateAndAddGap(GapType.News, open, prevClose, gapSize, atrVal, index, addToActive, isLiveBar);
                if (gap != null) gap.IsSessionAligned = isSession;
            }
            else if (ShowWeekend && isWeekend && gapSize >= minSize)
                CreateAndAddGap(GapType.Weekend, open, prevClose, gapSize, atrVal, index, addToActive, isLiveBar);
            else if (ShowSession && isSession && gapSize >= minSize)
                CreateAndAddGap(GapType.Session, open, prevClose, gapSize, atrVal, index, addToActive, isLiveBar);
            else if (ShowLiquidity && IsLiquidityGap(index, gapSize, minSize))
                CreateAndAddGap(GapType.Liquidity, open, prevClose, gapSize, atrVal, index, addToActive, isLiveBar);

            if (ShowFvg && index >= 2)
                DetectFvg(index, atrVal, minSize, addToActive, isLiveBar);
        }

        private bool IsLiquidityGap(int index, double gapSize, double minSize)
        {
            if (gapSize < minSize * 1.5) return false;
            int prev = index - 1;
            if (prev < 0 || prev >= Bars.Count) return false;

            double range = Bars.HighPrices[prev] - Bars.LowPrices[prev];
            if (range <= 0) return false;

            double body = Math.Abs(Bars.ClosePrices[prev] - Bars.OpenPrices[prev]);
            return body < 0.3 * range;
        }

        private Gap? CreateAndAddGap(GapType type, double open, double prevClose,
            double gapSize, double atrVal, int index, bool addToActive, bool isLiveBar)
        {
            var gap = new Gap
            {
                Type            = type,
                Top             = Math.Max(open, prevClose),
                Bottom          = Math.Min(open, prevClose),
                CreatedBarIndex = index,
                CreatedTime     = Bars.OpenTimes[index],
                SizeInAtr       = gapSize / atrVal,
                Direction       = open > prevClose ? GapDirection.Up : GapDirection.Down,
                IsConfirmed     = !isLiveBar
            };
            return AddGap(gap, addToActive, isLiveBar) ? gap : null;
        }

        private void DetectFvg(int index, double atrVal, double minSize, bool addToActive, bool isLiveBar)
        {
            double bar0Low  = Bars.LowPrices[index];
            double bar0High = Bars.HighPrices[index];
            double bar2Low  = Bars.LowPrices[index - 2];
            double bar2High = Bars.HighPrices[index - 2];

            // FIX: midBarOverlap-Check entfernt — war auf M5 zu restriktiv
            // FIX: FVG nutzt halbe minSize als Schwellwert
            double fvgMinSize = minSize * 0.5;

            if (bar2Low > bar0High)
            {
                double fvgSize = bar2Low - bar0High;
                if (fvgSize >= fvgMinSize)
                    AddGap(new Gap
                    {
                        Type            = GapType.FVG,
                        Top             = bar2Low,
                        Bottom          = bar0High,
                        CreatedBarIndex = index,
                        CreatedTime     = Bars.OpenTimes[index],
                        SizeInAtr       = fvgSize / atrVal,
                        Direction       = GapDirection.Up,
                        IsConfirmed     = !isLiveBar
                    }, addToActive, isLiveBar);
            }
            else if (bar2High < bar0Low)
            {
                double fvgSize = bar0Low - bar2High;
                if (fvgSize >= fvgMinSize)
                    AddGap(new Gap
                    {
                        Type            = GapType.FVG,
                        Top             = bar0Low,
                        Bottom          = bar2High,
                        CreatedBarIndex = index,
                        CreatedTime     = Bars.OpenTimes[index],
                        SizeInAtr       = fvgSize / atrVal,
                        Direction       = GapDirection.Down,
                        IsConfirmed     = !isLiveBar
                    }, addToActive, isLiveBar);
            }
        }

        private bool AddGap(Gap gap, bool addToActive, bool isLiveBar)
        {
            if (AllGaps.Any(g => g.CreatedBarIndex == gap.CreatedBarIndex && g.Type == gap.Type))
                return false;

            AllGaps.Add(gap);
            if (addToActive) ActiveGaps.Add(gap);
            if (isLiveBar)   _tentativeGaps.Add(gap);
            return true;
        }

        private bool IsWeekendBoundary(int index)
        {
            if (index <= 0 || index >= Bars.Count) return false;
            var prev = Bars.OpenTimes[index - 1].DayOfWeek;
            var curr = Bars.OpenTimes[index].DayOfWeek;
            return prev == DayOfWeek.Friday && (curr == DayOfWeek.Sunday || curr == DayOfWeek.Monday);
        }

        private bool IsSessionBoundary(int index)
        {
            if (index <= 0 || index >= Bars.Count) return false;
            int prevHour = Bars.OpenTimes[index - 1].Hour;
            int currHour = Bars.OpenTimes[index].Hour;
            return prevHour != currHour && _sessionHours.Contains(currHour);
        }

        #endregion

        #region Fill Check

        private bool IsGapFilled(Gap gap, int atIndex)
        {
            if (atIndex < 0 || atIndex >= Bars.Count) return false;
            double mid = (gap.Top + gap.Bottom) / 2.0;
            return Bars.LowPrices[atIndex] <= mid && Bars.HighPrices[atIndex] >= mid;
        }

        private void UpdateGapFillStatus(int index)
        {
            for (int i = ActiveGaps.Count - 1; i >= 0; i--)
            {
                var gap = ActiveGaps[i];
                if (gap.IsFilled || gap.IsAgedOut) continue;

                if (index - gap.CreatedBarIndex >= _fillWindowEffective)
                {
                    gap.IsAgedOut = true;
                    _stats.AddSample(gap.Type, gap.SizeInAtr, false, _fillWindowEffective);
                    ActiveGaps.RemoveAt(i);
                    continue;
                }

                if (!IsGapFilled(gap, index)) continue;

                if (IsLastBar)
                {
                    if (!gap.IsFilledTentative)
                    {
                        gap.IsFilledTentative = true;
                        _tentativelyFilledGaps.Add(gap);
                    }
                }
                else
                {
                    gap.IsFilled       = true;
                    gap.FilledBarIndex = index;
                    _stats.AddSample(gap.Type, gap.SizeInAtr, true, index - gap.CreatedBarIndex);
                    ActiveGaps.RemoveAt(i);
                }
            }
        }

        #endregion

        #region Probability Engine

        private void RecalculateProbabilities(int index)
        {
            double atrVal = _atr.Result[index];
            if (!double.IsFinite(atrVal) || atrVal <= 0) return;

            double currentPrice = Bars.ClosePrices[index];
            double ema50Val     = _ema50.Result[index];
            bool   emaValid     = double.IsFinite(ema50Val)
                               && index >= 1
                               && double.IsFinite(_ema50.Result[index - 1]);

            foreach (var gap in ActiveGaps)
            {
                double baseRate   = _stats.GetFillRate(gap.Type, gap.SizeInAtr);
                double mid        = (gap.Top + gap.Bottom) / 2.0;
                double distFactor = Math.Exp(-Math.Abs(currentPrice - mid) / (atrVal * DistanceDecay));
                double ageFactor  = Math.Exp(-(double)(index - gap.CreatedBarIndex) / AgeDecay);

                // Penalise only when price has already moved past the gap (gap was skipped).
                // Un-filled gaps sit below (Up) or above (Down) current price by design.
                double directionFactor = 1.0;
                if (gap.Direction == GapDirection.Up   && currentPrice > gap.Top)    directionFactor = 0.7;
                if (gap.Direction == GapDirection.Down && currentPrice < gap.Bottom) directionFactor = 0.7;

                double trendFactor = 1.0;
                if (emaValid)
                {
                    bool emaRising = ema50Val > _ema50.Result[index - 1];
                    bool gapAbove  = mid > currentPrice;
                    trendFactor = (emaRising == gapAbove) ? 1.15 : 0.85;
                }

                gap.HitProbability = Math.Min(MaxProbability,
                    baseRate * distFactor * ageFactor * directionFactor * trendFactor);
            }
        }

        #endregion

        #region Pruning

        private void PruneAllGaps(int index)
        {
            int oldest   = index - Lookback;
            var toRemove = AllGaps
                .Where(g => g.CreatedBarIndex < oldest
                         && (g.IsFilled || g.IsAgedOut || index - g.CreatedBarIndex > _fillWindowEffective))
                .ToList();

            foreach (var gap in toRemove)
            {
                RemoveGapChartObjects(gap);
                AllGaps.Remove(gap);
                ActiveGaps.Remove(gap);
            }
        }

        #endregion

        #region Outputs

        private void UpdateOutputs(int index)
        {
            if (ActiveGaps.Count == 0) return;

            double currentPrice = Bars.ClosePrices[index];
            var    top          = ActiveGaps.OrderByDescending(g => g.HitProbability).First();

            NearestGapPrice[index] = (top.Top + top.Bottom) / 2.0;
            NearestGapProb[index]  = top.HitProbability;
            NearestGapType[index]  = (int)top.Type;
            GapsAbove[index]       = ActiveGaps.Count(g => (g.Top + g.Bottom) / 2.0 > currentPrice);
            GapsBelow[index]       = ActiveGaps.Count(g => (g.Top + g.Bottom) / 2.0 < currentPrice);
        }

        #endregion

        #region Rendering

        private void RenderGapsDelta(int index)
        {
            if (Chart == null) return;

            var topProjections = ActiveGaps
                .OrderByDescending(g => g.HitProbability)
                .Take(TopN)
                .ToList();

            var currentProjIds = new HashSet<string>(
                topProjections.Select(g => $"proj_{g.Type}_{g.CreatedBarIndex}"));

            foreach (var oldId in _renderedProjIds.ToList())
            {
                if (!currentProjIds.Contains(oldId))
                {
                    Chart.RemoveObject(oldId);
                    Chart.RemoveObject(oldId + "_lbl");
                    _renderedProjIds.Remove(oldId);
                }
            }

            foreach (var gap in AllGaps)
            {
                string boxId = $"gap_{gap.Type}_{gap.CreatedBarIndex}";

                if (gap.IsFilled && !ShowFilled)
                {
                    if (_renderedBoxIds.Contains(boxId))
                    {
                        Chart.RemoveObject(boxId);
                        _renderedBoxIds.Remove(boxId);
                        _boxCache.Remove(boxId);
                    }
                    continue;
                }

                DrawGapBox(gap, index);
            }

            int rank = 1;
            foreach (var proj in topProjections)
                DrawProjectionLine(proj, index, rank++);
        }

        private void DrawGapBox(Gap gap, int currentIndex)
        {
            if (Chart == null) return;

            string boxId  = $"gap_{gap.Type}_{gap.CreatedBarIndex}";
            int    endIdx = gap.IsFilled ? gap.FilledBarIndex : currentIndex + LiveBarBoxExtension;
            bool   filled = gap.IsFilled;

            if (_boxCache.TryGetValue(boxId, out var cached)
                && cached.endIdx == endIdx && cached.isFilled == filled)
                return;

            _boxCache[boxId] = (endIdx, filled);

            Color baseColor = TypeColors[gap.Type];
            Color color     = Color.FromArgb(filled ? 40 : 80, baseColor.R, baseColor.G, baseColor.B);

            var rect = Chart.DrawRectangle(boxId, gap.CreatedBarIndex, gap.Top, endIdx, gap.Bottom, color);
            rect.IsFilled      = true;
            rect.IsInteractive = false;

            _renderedBoxIds.Add(boxId);
        }

        private void DrawProjectionLine(Gap gap, int currentIndex, int rank)
        {
            if (Chart == null) return;

            string id        = $"proj_{gap.Type}_{gap.CreatedBarIndex}";
            double mid       = (gap.Top + gap.Bottom) / 2.0;
            Color  lineColor = ProbabilityToColor(gap.HitProbability);
            int    endBar    = currentIndex + _projectionBars;

            var line = Chart.DrawTrendLine(id, currentIndex, mid, endBar, mid, lineColor, 2, LineStyle.Solid);
            line.IsInteractive    = false;
            line.ExtendToInfinity = false;

            var text = Chart.DrawText(id + "_lbl",
                $"#{rank} {gap.Type} {gap.HitProbability:P0}", endBar, mid, lineColor);
            text.HorizontalAlignment = HorizontalAlignment.Right;
            text.VerticalAlignment   = VerticalAlignment.Center;
            text.FontSize            = 10;
            text.IsBold              = true;

            _renderedProjIds.Add(id);
        }

        private static Color ProbabilityToColor(double prob)
        {
            int r = prob < 0.5 ? 255 : (int)((1 - prob) * 2 * 255);
            int g = prob < 0.5 ? (int)(prob * 2 * 255) : 255;
            return Color.FromArgb(255, (byte)r, (byte)g, (byte)0);
        }

        #endregion
    }

    // ==================== Supporting Types ====================

    public enum GapType      { None = 0, Weekend = 1, Session = 2, News = 3, FVG = 4, Liquidity = 5 }
    public enum GapDirection { Up, Down }

    public sealed class Gap
    {
        public GapType      Type              { get; set; }
        public double       Top               { get; set; }
        public double       Bottom            { get; set; }
        public int          CreatedBarIndex   { get; set; }
        public DateTime     CreatedTime       { get; set; }
        public double       SizeInAtr         { get; set; }
        public GapDirection Direction         { get; set; }
        public bool         IsFilled          { get; set; }
        public bool         IsFilledTentative { get; set; }
        public bool         IsAgedOut         { get; set; }
        public bool         IsSessionAligned  { get; set; }
        public int          FilledBarIndex    { get; set; } = -1;
        public double       HitProbability    { get; set; }
        public bool         IsConfirmed       { get; set; }
    }

    public class FillStatistics
    {
        private const int    MinSamples  = 5;
        private const double DefaultRate = 0.5;

        private static readonly double[] Buckets = { 0.25, 0.5, 1.0, 2.0, 4.0, double.MaxValue };

        private readonly Dictionary<(GapType, int), (int hits, int total)> _data
            = new Dictionary<(GapType, int), (int hits, int total)>();

        public void AddSample(GapType type, double sizeInAtr, bool wasFilled, int barsToFill)
        {
            var key = (type, Bucket(sizeInAtr));
            if (!_data.TryGetValue(key, out var e)) e = (0, 0);
            _data[key] = (e.hits + (wasFilled ? 1 : 0), e.total + 1);
        }

        public double GetFillRate(GapType type, double sizeInAtr)
        {
            var key = (type, Bucket(sizeInAtr));
            if (_data.TryGetValue(key, out var e) && e.total >= MinSamples)
                return (double)e.hits / e.total;

            int hits = 0, total = 0;
            foreach (var kv in _data)
                if (kv.Key.Item1 == type) { hits += kv.Value.hits; total += kv.Value.total; }

            return total > 0 ? (double)hits / total : DefaultRate;
        }

        public void Build() { }

        private static int Bucket(double sizeInAtr)
        {
            for (int i = 0; i < Buckets.Length; i++)
                if (sizeInAtr <= Buckets[i]) return i;
            return Buckets.Length - 1;
        }
    }
}
