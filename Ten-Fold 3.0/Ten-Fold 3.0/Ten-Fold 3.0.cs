// ═══════════════════════════════════════════════════════════════════════════════
//  10-Fold Bot  │  Multi-Strategy Scoring cBot
//  Platform     │  cTrader (Pepperstone Razor Account)
//  Architecture │  Modular Scoring Engine – Pullback / Mean Reversion
//  Version      │  3.1.8 (Backtest-Mode Persistence Flag)
// ═══════════════════════════════════════════════════════════════════════════════
//  CHANGELOG
//  ──────────────────────────────────────────────────────────────────────────────
//  v3.1.5  P0-1: ValidateCriticalParameters clamps MaxRiskPercent to
//          MaxDailyDrawdownPercent/2 when MaxRiskPercent*2 > MaxDailyDrawdownPercent.
//  v3.1.6  P0-2: IncludeCommissionInRisk adds EstCommPips to slPips in CalculateSlPips.
//          P0-3: ScoringPreset enum; DecorrelatedDefault preset forces Caps=true, all=3.
//          P1-1: ReversalExitRequireHigherThanEntry raises threshold to EntryTotalScore.
//          P1-2: EnableRProgressTimeStop exits when currentR < MinRProgress after N bars.
//          P1-3: EnableVolTargetedSizing scales risk by Baseline/ATR (clamped 0.5–2.0).
//          P1-4: AttributionLogFilePath appends CSV to file; header auto-written.
//  v3.1.7  P2-1: EnableSessionAttribution – Session×DoW win-rate/avgPnL matrix in OnStop.
//          GetSessionBucket (Asia/London/Overlap/NY) + GetDowBucket helpers.
//  v3.1.8  P2-2: DisablePersistenceIO (default=false) skips all File I/O in Persistence.cs.


using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
// cAlgo.API defines its own "File" type – alias System.IO.File to avoid ambiguity.
using File = System.IO.File;

namespace cAlgo.Robots
{
    public enum SlMethod   { AtrBased, SwingHighLow, FixedPips }
    public enum TpMethod   { Rrr, AtrMultiplier, NextSwingExtreme, Runner, IntervalLot }
    public enum TrailingType { None, Chandelier, FastEma }
    public enum EmaTrailFilter { StrictClose, DoubleClose }
    public enum IntervalBasis { Pips, AtrMultiple }

    // Basis für Risikoberechnung
    public enum RiskBase { Balance, Equity }

    // Floating Loss Gate Mode für IsMarketTradable
    // NOTE (v3.0.0 breaking rename): "NetUnrealised" was a misnomer – it sums
    // Math.Abs(P&L) across all positions, i.e. a Gross exposure metric. Renamed
    // accordingly. Users who relied on NetUnrealised must switch to GrossUnrealised.
    public enum FloatingLossGateMode { FloatingLossOnly, GrossUnrealised }

    public enum DashboardCornerPosition { TopLeft, TopRight, BottomLeft, BottomRight }

    // v3.1.2 – Denominator for the floating-loss gate
    public enum FloatingLossDenom { Balance, Equity }

    // v3.1.6 – Scoring preset modes
    public enum ScoringPreset { Custom, DecorrelatedDefault }

    internal class TimeWindow
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End   { get; set; }
    }

    internal class TradeState
    {
        public int    PositionId           { get; set; }
        public double EntryPrice           { get; set; }
        public double InitialSlPips        { get; set; }
        public double InitialVolume        { get; set; }
        public bool   BreakEvenDone        { get; set; }
        public bool   Partial1Done         { get; set; }
        public bool   Partial2Done         { get; set; }
        public bool   Partial3Done         { get; set; }
        public double ChandelierStopLong   { get; set; }
        public double ChandelierStopShort  { get; set; }
        public int    ConsecutiveEmaCloses { get; set; }

        // v2.8.0 – Interval-Lot-TP State
        public int    IntervalsTriggered   { get; set; }
        public double IntervalAtrAtEntry   { get; set; }  // ATR zum Zeitpunkt des Entries (falls AtrMultiple-Mode)

        // v2.12.0 – Trade entry time for max-hold-time-exit
        public DateTime EntryTime          { get; set; }

        // v2.13.0 – Trade Attribution Log (P2)
        public int[]   EntryModuleScores { get; set; }  // [EMA,BB,ST,PA,FIB,OSC,SR,MACD,ADX]
        public int     EntryTotalScore   { get; set; }
        public double  EntrySpreadPips   { get; set; }
        public double  EntryAtrPips      { get; set; }
        public string  EntryHtfRegime    { get; set; }  // BULL/BEAR/NA
        public double  EntryAdxValue     { get; set; }
    }

    internal class DailyState
    {
        public string Date                  { get; set; }
        public double DayStartEquity        { get; set; }
        public int    TradesToday           { get; set; }
        public int    ConsecutiveLosses     { get; set; }
        public DateTime CooldownEndTime     { get; set; }
        public bool   RolloverCheckDoneToday { get; set; }
    }

    internal class WeeklyState
    {
        public string WeekStart             { get; set; }
        public double WeekStartEquity       { get; set; }
    }


    // ─────────────────────────────────────────────────────────────────────────
    //  PivotPoint – zentraler Baustein für Fibo, S/R und SL-Swing-Erkennung
    // ─────────────────────────────────────────────────────────────────────────
    internal sealed class PivotPoint
    {
        public double Price  { get; }
        public int    Index  { get; }   // Bars.Last(Index) zur Zeit der Erkennung
        public bool   IsHigh { get; }

        public PivotPoint(double price, int index, bool isHigh)
        {
            Price  = price;
            Index  = index;
            IsHigh = isHigh;
        }
    }

    [Robot("10-Fold Bot", AccessRights = AccessRights.None)]
    public partial class TenFoldBot : Robot
    {

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE FIELDS – Indicators
        // ════════════════════════════════════════════════════════════════════

        private MovingAverage          _emaFast;
        private MovingAverage          _emaSlow;
        private BollingerBands         _bollingerBands;
        private RelativeStrengthIndex  _rsi;
        private StochasticOscillator   _stochastic;
        private AverageTrueRange       _atrSl;
        private AverageTrueRange       _atrChandelier;
        private MovingAverage          _trailingEma;
        private Bars                   _htfBars;
        private MovingAverage          _htfEma;
        private AverageTrueRange       _atrSupertrend;
        private MacdHistogram          _macd;
        private DirectionalMovementSystem _dms;

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE FIELDS – State
        // ════════════════════════════════════════════════════════════════════

        private int      _maxPossibleScore;
        private int      _minRequiredScore;
        private double   _dayStartEquity;
        private bool     _dailyDrawdownBreached;
        private DateTime _lastDailyResetDate;
        private bool     _weekendCloseFired;
        private bool     _rolloverCheckDoneToday;
        private bool     _botInStandby;

        private TradeState _currentTrade;
        private int      _tradesToday;
        private int      _consecutiveLosses;
        private DateTime _cooldownEndTime = DateTime.MinValue;

        // v2.13.0 – P3: Weekly DD Cap
        private double   _weekStartEquity        = 0;
        private bool     _weeklyDrawdownBreached = false;
        private DateTime _lastWeeklyResetDate    = DateTime.MinValue;
        private bool     _persistedTodayLoaded;
        private List<TimeWindow> _parsedNewsWindows = new List<TimeWindow>();
        private Bars     _dailyBars;


        // P2: OnStop Tracking
        private DateTime _startTime;
        private int      _totalTradesOpened = 0;

        // v2.13.0 – P5: Rejection-Log-Throttle – max 1x per bar per reason
        private readonly Dictionary<string, DateTime> _lastRejectionPrint = new Dictionary<string, DateTime>();

        // v2.13.0 – P2: Trade Attribution Tracking
        private double[] _attrSumScoresWin  = new double[9];
        private double[] _attrSumScoresLoss = new double[9];
        private int      _attrCountWin      = 0;
        private int      _attrCountLoss     = 0;

        // v3.1.3 – Module Edge per Score-Bucket (9 modules × scores 0–3)
        private double[,] _attrScoreWinByBucket  = new double[9, 4];
        private double[,] _attrScoreLossByBucket = new double[9, 4];

        // v3.1.7 – Session × DoW Attribution (5 weekdays × 4 sessions)
        private double[,] _sessionWinCount  = new double[5, 4];
        private double[,] _sessionLossCount = new double[5, 4];
        private double[,] _sessionPnlSum    = new double[5, 4];

        // VWAP Cache – einmal pro Bar in OnBar() berechnet
        private double   _cachedVwap      = 0;
        private int      _cachedLongScore  = 0;
        private int      _cachedShortScore = 0;

        // Supertrend Rolling-State
        private double _stFinalUpperBand = double.MaxValue;
        private double _stFinalLowerBand = double.MinValue;
        private int    _stTrend          = 1;   // 1 = bullish, -1 = bearish
        private bool   _stFlippedThisBar = false;
        private bool   _stInitialized    = false;

        // v2.9.0 – Performance Caches
        // Dashboard throttling: Tick-Aufrufe rendern nur alle N Millisekunden neu.
        private DateTime _lastDashboardUpdate = DateTime.MinValue;
        private const int DashboardThrottleMs = 1000;

        // Reversal-Exit Counter-Score: pro Bar berechnen, nicht pro Tick.
        private DateTime _lastCounterScoreBarTime = DateTime.MinValue;
        private int      _cachedCounterScore      = 0;
        private bool     _cachedCounterTradable   = false;

        // v2.12.0 – Pivot-Point-Cache: GetRecentPivots reduziert von 4×/OnBar auf 1×
        private DateTime _pivotCacheBarTime = DateTime.MinValue;
        private readonly Dictionary<int, List<PivotPoint>> _pivotCache = new Dictionary<int, List<PivotPoint>>();

        // v2.12.0 – Module-Score-Cache: LogScoreBreakdown liest aus Cache statt neu zu rechnen
        private int[] _cachedLongModuleScores  = new int[9]; // EMA,BB,ST,PA,FIB,OSC,SR,MACD,ADX
        private int[] _cachedShortModuleScores = new int[9];

        // v2.12.0 – VWAP incremental: statt O(480) Loop wird O(1) pro Bar
        private double   _vwapSumTpVol    = 0;
        private double   _vwapSumVol      = 0;
        private DateTime _vwapLastBarTime = DateTime.MinValue;
        private DateTime _vwapLastDate    = DateTime.MinValue;

        private const string BotLabel = "10-Fold Bot";

        // ════════════════════════════════════════════════════════════════════
        //  OnStart
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            Print("╔══════════════════════════════════════════════╗");
            Print("║   10-Fold Bot  v3.1.8  │  Starting           ║");
            Print("╚══════════════════════════════════════════════╝");
            _startTime = Server.Time;
            Print("Symbol={0} | TF={1} | Balance={2:F2} {3}",
                SymbolName, TimeFrame, Account.Balance, Account.Asset.Name);

            ValidateCriticalParameters();
            ValidateParameters();
            ApplyScoringPreset();

            Positions.Closed += OnPositionClosed;

            if (EnableVolatilityFilter || EnableVolRegime)
                _dailyBars = MarketData.GetBars(TimeFrame.Daily);

            if (EnableNewsBlocker)
                ParseNewsWindows();

            if (!_botInStandby)
            {
                InitializeIndicators();
                CalculateScoreThresholds();
                RecoverExistingPosition();
            }

            LoadPersistedState();
            CleanupOldStateFiles();
            ResetDailyState(isOnStartCall: true);
            ResetWeeklyState(isOnStartCall: true);

            if (ShowDashboard)
                InitializeDashboard();

            Print("Init complete. Standby={0} | MaxScore={1} | MinRequired={2} ({3:P0})",
                _botInStandby, _maxPossibleScore, _minRequiredScore, ConsensusRatio);

            // v3.0.0 – parity marker: lets backtest diffs confirm that the
            // partial-class-split build produces the same module set and score
            // thresholds as the pre-3.0 monolithic build.
            string modulesList = string.Format(
                "EMA={0} BB={1} ST={2} PA={3} FIB={4} OSC={5} SR={6} MACD={7} ADX={8}",
                EnableEmaModule        ? "on" : "off",
                EnableBbModule         ? "on" : "off",
                EnableSupertrendModule ? "on" : "off",
                EnablePatternsModule   ? "on" : "off",
                EnableFiboModule       ? "on" : "off",
                EnableOscModule        ? "on" : "off",
                EnableSrModule         ? "on" : "off",
                EnableMacdModule       ? "on" : "off",
                EnableAdxScoreModule   ? "on" : "off");
            Print("BUILD: v3.1.8 | Modules={0} | MaxScore={1} | MinReq={2}",
                modulesList, _maxPossibleScore, _minRequiredScore);

            if (DisablePersistenceIO)
                Print("BACKTEST MODE: All Persistence I/O disabled. No JSON files will be read or written this session.");

            Print("Dashboard: {0} | Corner: {1}", ShowDashboard ? "ON" : "OFF", DashboardCorner);
            Print("Risk range: {0:F2}% – {1:F2}% | SL: {2} | TP: {3}",
                MinRiskPercent, MaxRiskPercent, StopLossMethod, TakeProfitMethod);
            Print("Trailing: {0} | BE: {1} (trigger {2:F1}R +{3:F1}p offset)",
                TrailingStopType, EnableBreakEven ? "ON" : "OFF", BeRMultiple, BeOffsetPips);
            Print("VerboseScoreLogging: {0}", EnableVerboseScoreLogging ? "ON" : "OFF");
            Print("SessionAttribution: {0}", EnableSessionAttribution ? "ON" : "OFF");

            // v2.8.0 – Interval-Lot-TP Config-Log
            if (TakeProfitMethod == TpMethod.IntervalLot)
            {
                string basisStr = IntervalTpBasis == IntervalBasis.Pips
                    ? string.Format("{0:F1} Pips", IntervalPips)
                    : string.Format("{0:F2}x ATR", IntervalAtrMultiple);
                Print("IntervalLot-TP: every {0} close {1:F2} Lots | Runner-Min: {2:F2}L | MaxIntervals: {3}",
                    basisStr, LotsPerInterval, MinRunnerLots, MaxIntervals == 0 ? "unlimited" : MaxIntervals.ToString());
            }
        }


        // ════════════════════════════════════════════════════════════════════
        //  OnStop – Abschluss-Log beim Beenden des Bots
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
            TimeSpan runtime = Server.Time - _startTime;
            Print("╔══════════════════════════════════════════════╗");
            Print("║   10-Fold Bot  v3.1.8  │  Stopped            ║");
            Print("╚══════════════════════════════════════════════╝");
            Print("  Runtime      : {0:dd\\d\\ hh\\h\\ mm\\m\\ ss\\s}",  runtime);
            Print("  Balance      : {0:F2} {1}", Account.Balance, Account.Asset.Name);
            Print("  Equity       : {0:F2} {1}", Account.Equity,  Account.Asset.Name);
            Print("  Trades opened: {0}", _totalTradesOpened);
            Print("  Daily DD     : {0}",
                _dailyDrawdownBreached ? "LIMIT REACHED today" : "Within limits");
            Print("  INFO: Der Daily-DD-Zähler beginnt beim nächsten Start neu. " +
                  "Falls Restart mitten am Tag: DD-Zählung startet ab OnStart-Equity, " +
                  "nicht ab Tagesbeginn-Equity.");
            if (_currentTrade != null)
                Print("  WARNUNG: Trade Id={0} ist noch offen – SL/TP gelten weiterhin.",
                    _currentTrade.PositionId);

            if (EnableTradeAttributionLog && (_attrCountWin + _attrCountLoss) > 0)
            {
                string[] names = { "EMA", "BB", "ST", "PA", "FIB", "OSC", "SR", "MACD", "ADX" };
                Print("── Trade Attribution Stats ──────────────────────────");
                Print("  Winners: {0}  Losers: {1}", _attrCountWin, _attrCountLoss);
                Print("  Module   Avg(win)  Avg(loss)  Delta");
                for (int i = 0; i < 9; i++)
                {
                    double aw = _attrCountWin  > 0 ? _attrSumScoresWin[i]  / _attrCountWin  : 0;
                    double al = _attrCountLoss > 0 ? _attrSumScoresLoss[i] / _attrCountLoss : 0;
                    Print("  {0,-6}   {1:F2}      {2:F2}       {3:+0.00;-0.00;0.00}",
                        names[i], aw, al, aw - al);
                }

                // v3.1.3 – Module Edge per Score-Bucket
                Print("── Module Edge per Score-Bucket ──────────────────────");
                for (int m = 0; m < 9; m++)
                {
                    for (int b = 0; b <= 3; b++)
                    {
                        double w  = _attrScoreWinByBucket[m, b];
                        double l  = _attrScoreLossByBucket[m, b];
                        double wr = (w + l) > 0 ? w / (w + l) : 0;
                        if ((w + l) >= 5)
                            Print("  {0,-6} score={1} n={2,3} winRate={3:P1}",
                                names[m], b, (int)(w + l), wr);
                    }
                }
            }

            // v3.1.7 – Session × DoW Attribution
            if (EnableSessionAttribution)
            {
                string[] dowNames = { "Mon", "Tue", "Wed", "Thu", "Fri" };
                string[] sesNames = { "Asia", "London", "Overlap", "NY" };
                Print("── Session × Day-of-Week Edge ────────────────────────");
                Print("  Day    Session    n    winRate   avgPnL");
                for (int d = 0; d < 5; d++)
                {
                    for (int s = 0; s < 4; s++)
                    {
                        double w = _sessionWinCount[d, s];
                        double l = _sessionLossCount[d, s];
                        double n = w + l;
                        if (n < 3) continue;
                        double wr  = w / n;
                        double avg = _sessionPnlSum[d, s] / n;
                        Print("  {0,-4}   {1,-7}  {2,3}   {3:P1}    {4:+0.00;-0.00;0.00}",
                            dowNames[d], sesNames[s], (int)n, wr, avg);
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ValidateCriticalParameters – hard-fail validation
        //  Anything that would yield pathological behaviour forces Standby
        //  instead of being downgraded to a WARNING.
        // ─────────────────────────────────────────────────────────────────────
        private void ValidateCriticalParameters()
        {
            if (!(MaxRiskPercent > 0) || !(MinRiskPercent > 0))
            {
                _botInStandby = true;
                Print("CRITICAL: Min/MaxRiskPercent must both be > 0 (got Min={0:F4}, Max={1:F4}) – Bot entering Standby.",
                    MinRiskPercent, MaxRiskPercent);
            }

            if (MaxDailyDrawdownPercent > 0 && MaxRiskPercent * 2 > MaxDailyDrawdownPercent)
            {
                double clamped = MaxDailyDrawdownPercent / 2.0;
                Print("WARNING: MaxRiskPercent ({0:F4}%) * 2 > MaxDailyDrawdownPercent ({1:F4}%) – clamping MaxRiskPercent to {2:F4}%.",
                    MaxRiskPercent, MaxDailyDrawdownPercent, clamped);
                MaxRiskPercent = clamped;
            }

            if (EnablePartial2 && Partial2TriggerR <= Partial1TriggerR)
            {
                _botInStandby = true;
                Print("CRITICAL: Partial2TriggerR ({0:F2}R) <= Partial1TriggerR ({1:F2}R) – Bot entering Standby.",
                    Partial2TriggerR, Partial1TriggerR);
            }

            if (EnablePartial3 && Partial3TriggerR <= Partial2TriggerR)
            {
                _botInStandby = true;
                Print("CRITICAL: Partial3TriggerR ({0:F2}R) <= Partial2TriggerR ({1:F2}R) – Bot entering Standby.",
                    Partial3TriggerR, Partial2TriggerR);
            }

            if (EnableMacdModule && MacdShortCycle >= MacdLongCycle)
            {
                _botInStandby = true;
                Print("CRITICAL: MacdShortCycle ({0}) >= MacdLongCycle ({1}) – Bot entering Standby.",
                    MacdShortCycle, MacdLongCycle);
            }
        }

        //  ValidateParameters
        // ─────────────────────────────────────────────────────────────────────
        private void ValidateParameters()
        {
            if (MinRiskPercent > MaxRiskPercent)
            {
                Print("WARNING: MinRiskPercent > MaxRiskPercent – swapping automatically.");
                double tmp = MinRiskPercent;
                MinRiskPercent = MaxRiskPercent;
                MaxRiskPercent = tmp;
            }

            if (EnableEmaModule && EmaFastPeriod >= EmaSlowPeriod)
                Print("WARNING: EMA Fast ({0}) >= Slow ({1}) – signals may be inverted.", EmaFastPeriod, EmaSlowPeriod);

            if (EnablePartial2 && Partial2TriggerR <= Partial1TriggerR)
                Print("WARNING: Partial2 trigger ({0}R) should be > Partial1 ({1}R).", Partial2TriggerR, Partial1TriggerR);

            if (EnablePartial3 && Partial3TriggerR <= Partial2TriggerR)
                Print("WARNING: Partial3 trigger ({0}R) should be > Partial2 ({1}R).", Partial3TriggerR, Partial2TriggerR);

            // P6: Performance-Warnungen für große Lookbacks
            if (EnableFiboModule && FiboSwingLookback > 200)
                Print("WARNING: FiboSwingLookback={0} ist sehr groß und kann auf kleinen Timeframes die Performance belasten.", FiboSwingLookback);

            if (EnablePatternsModule && PatternLookback > 10)
                Print("WARNING: PatternLookback={0} > 10 bringt selten zusätzlichen Mehrwert und verlangsamt OnBar.", PatternLookback);

            if (EnableFiboModule && TimeFrame < TimeFrame.Hour && FiboSwingLookback > 100)
                Print("WARNING: Kleiner TF + großer FiboSwingLookback={0} – erwäge einen Wert unter 50.", FiboSwingLookback);

            if (EnableVolatilityFilter && AdrPeriod < 1)
                Print("WARNING: AdrPeriod sollte >= 1 sein.");

            if (EnableNewsBlocker && string.IsNullOrWhiteSpace(NewsTimeWindows))
                Print("WARNING: EnableNewsBlocker=true aber NewsTimeWindows ist leer.");

            // v2.8.0 – Validation für Interval-Lot-TP
            if (TakeProfitMethod == TpMethod.IntervalLot)
            {
                double brokerMinLots = Symbol.VolumeInUnitsMin / Symbol.LotSize;

                if (LotsPerInterval < brokerMinLots)
                    Print("WARNING: LotsPerInterval ({0:F2}) < Broker-Min ({1:F2}) – kann zu gescheiterten Teilschließungen führen.",
                        LotsPerInterval, brokerMinLots);

                if (MinRunnerLots > 0 && MinRunnerLots < brokerMinLots)
                    Print("WARNING: MinRunnerLots ({0:F2}) < Broker-Min ({1:F2}) – setze entweder 0 oder >= Broker-Min.",
                        MinRunnerLots, brokerMinLots);

                if (IntervalTpBasis == IntervalBasis.Pips && IntervalPips < 1.0)
                    Print("WARNING: IntervalPips={0:F2} ist sehr klein – kann zu Over-Trading führen.", IntervalPips);

                if (IntervalTpBasis == IntervalBasis.AtrMultiple && IntervalAtrMultiple < 0.1)
                    Print("WARNING: IntervalAtrMultiple={0:F2} ist sehr klein.", IntervalAtrMultiple);

                if (EnablePartial1 || EnablePartial2 || EnablePartial3)
                    Print("INFO: IntervalLot-Modus aktiv – klassische Partial-Closes (Sektion 17) werden ignoriert.");
            }

            // v2.8.0 – Warnung: EMA- und Supertrend-Modul sind beide Trend-Following.
            // Aktiviert zusammen → Trend-Signale werden doppelt gewichtet.
            if (EnableEmaModule && EnableSupertrendModule)
                Print("INFO: EMA- und Supertrend-Modul messen beide Trend → können redundant sein. " +
                      "Evtl. nur eines aktivieren oder Max-Weights reduzieren.");

            // v2.11.0 – MACD/ADX-Validation
            if (EnableMacdModule && MacdShortCycle >= MacdLongCycle)
                Print("WARNING: MACD Short ({0}) >= Long ({1}) – MACD-Linie wird invertiert.",
                    MacdShortCycle, MacdLongCycle);

            if (EnableMacdModule && (EnableEmaModule || EnableSupertrendModule))
                Print("INFO: MACD + EMA/Supertrend aktiv → Trend/Momentum wird mehrfach gewichtet. " +
                      "Für Pullback/MR evtl. MacdMaxWeight reduzieren.");

            if (EnableAdxFilter && MinAdxValue > 40.0)
                Print("WARNING: MinAdxValue={0:F1} ist sehr hoch – blockt ggf. sehr viele Entries.", MinAdxValue);

            if (ReversalExitScoreMultiplier < 1.0)
                Print("INFO: ReversalExitScoreMultiplier={0:F2} < 1.0 – erleichtert Reversal-Exits.", ReversalExitScoreMultiplier);

            if (!FiboUseLegacyRange)
                Print("INFO: FiboUseLegacyRange=false – nutzt FindLastImpulseSwing (last coherent swing).");

            if (ConsecLossSizeReducer < 1.0)
                Print("INFO: ConsecLossSizeReducer={0:F2} – Anti-Martingale Sizing aktiv.", ConsecLossSizeReducer);

            if (MaxWeeklyDrawdownPercent > 0 && MaxWeeklyDrawdownPercent < MaxDailyDrawdownPercent)
                Print("WARNING: MaxWeeklyDrawdownPercent ({0:F1}%) < MaxDailyDrawdownPercent ({1:F1}%) – wöchentlicher Cap ist enger als täglicher.",
                    MaxWeeklyDrawdownPercent, MaxDailyDrawdownPercent);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ApplyScoringPreset – overwrites cap params when preset != Custom
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyScoringPreset()
        {
            if (ScoringPresetMode == ScoringPreset.DecorrelatedDefault)
            {
                EnableCategoryCaps        = true;
                TrendCategoryCap          = 3;
                MeanReversionCategoryCap  = 3;
                MomentumCategoryCap       = 3;
                PriceActionCategoryCap    = 3;
                Print("Preset active: Decorrelated (Trend/MR/Mom/PA capped at 3 each)");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  InitializeIndicators
        // ─────────────────────────────────────────────────────────────────────
        private void InitializeIndicators()
        {
            if (EnableEmaModule)
            {
                _emaFast = Indicators.MovingAverage(Bars.ClosePrices, EmaFastPeriod, MovingAverageType.Exponential);
                _emaSlow = Indicators.MovingAverage(Bars.ClosePrices, EmaSlowPeriod, MovingAverageType.Exponential);
                Print("  [✓] EMA  Fast={0} Slow={1}", EmaFastPeriod, EmaSlowPeriod);
            }

            if (EnableBbModule)
            {
                _bollingerBands = Indicators.BollingerBands(Bars.ClosePrices, BbPeriod, BbStdDev, MovingAverageType.Simple);
                Print("  [✓] BB  Period={0} StdDev={1}", BbPeriod, BbStdDev);
            }

            if (EnableSupertrendModule)
            {
                _atrSupertrend = Indicators.AverageTrueRange(SupertrendAtrPeriod, MovingAverageType.WilderSmoothing);
                WarmUpSupertrendState();
                Print("  [✓] Supertrend ATR  Period={0} Factor={1}", SupertrendAtrPeriod, SupertrendFactor);
            }

            if (EnableOscModule || EnableRsiPanicExit)
            {
                _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
                Print("  [✓] RSI  Period={0}", RsiPeriod);
            }

            if (EnableOscModule)
            {
                _stochastic = Indicators.StochasticOscillator(StochKPeriod, StochKSlowing, StochDPeriod, MovingAverageType.Simple);
                Print("  [✓] Stochastic  K={0} Slow={1} D={2}", StochKPeriod, StochKSlowing, StochDPeriod);
            }

            _atrSl = Indicators.AverageTrueRange(AtrSlPeriod, MovingAverageType.WilderSmoothing);
            Print("  [✓] ATR-SL  Period={0}", AtrSlPeriod);

            if (TrailingStopType == TrailingType.Chandelier)
            {
                _atrChandelier = Indicators.AverageTrueRange(ChandelierAtrPeriod, MovingAverageType.WilderSmoothing);
                Print("  [✓] Chandelier ATR  Period={0} x{1}", ChandelierAtrPeriod, ChandelierAtrMultiplier);
            }
            else if (TrailingStopType == TrailingType.FastEma)
            {
                _trailingEma = Indicators.MovingAverage(Bars.ClosePrices, TrailingEmaPeriod, MovingAverageType.Exponential);
                Print("  [✓] Fast-EMA Trail  Period={0} Filter={1}", TrailingEmaPeriod, EmaTrailingFilter);
            }

            if (EnableHtfFilter || EnableHtfBreakExit)
            {
                _htfBars = MarketData.GetBars(HtfTimeFrame);
                _htfEma  = Indicators.MovingAverage(_htfBars.ClosePrices, HtfEmaPeriod, MovingAverageType.Exponential);
                Print("  [✓] HTF  TF={0} EMA={1}", HtfTimeFrame, HtfEmaPeriod);
            }

            if (EnableMacdModule)
            {
                _macd = Indicators.MacdHistogram(Bars.ClosePrices, MacdLongCycle, MacdShortCycle, MacdSignalPeriods);
                Print("  [✓] MACD  Long={0} Short={1} Signal={2}", MacdLongCycle, MacdShortCycle, MacdSignalPeriods);
            }

            if (EnableAdxFilter || EnableAdxScoreModule)
            {
                _dms = Indicators.DirectionalMovementSystem(AdxPeriod);
                string role = "";
                if (EnableAdxFilter && EnableAdxScoreModule) role = "Gate+Module";
                else if (EnableAdxFilter) role = "Gate";
                else role = "Module";
                Print("  [✓] ADX   Period={0} Role={1}", AdxPeriod, role);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  WarmUpSupertrendState (v2.12.0)
        //  Initialisiert Supertrend-State aus historischen Bars statt kaltem Start.
        //  Verhindert falsche Trend-Richtung nach Bot-Restart.
        // ─────────────────────────────────────────────────────────────────────
        private void WarmUpSupertrendState()
        {
            if (_atrSupertrend == null) return;
            int warmupBars = Math.Min(SupertrendAtrPeriod * 5, Bars.Count - 2);

            for (int i = warmupBars; i >= 1; i--)
            {
                double atr = _atrSupertrend.Result.Last(i);
                if (double.IsNaN(atr) || atr <= 0) continue;

                double hl2      = (Bars.HighPrices.Last(i) + Bars.LowPrices.Last(i)) / 2.0;
                double rawUpper = hl2 + SupertrendFactor * atr;
                double rawLower = hl2 - SupertrendFactor * atr;
                double closeNow = Bars.ClosePrices.Last(i);
                double prevClose = (i + 1 < Bars.Count) ? Bars.ClosePrices.Last(i + 1) : closeNow;

                double newUpper, newLower;
                if (!_stInitialized)
                {
                    newUpper = rawUpper;
                    newLower = rawLower;
                    _stTrend = closeNow > hl2 ? 1 : -1;
                    _stInitialized = true;
                }
                else
                {
                    newUpper = prevClose > _stFinalUpperBand ? rawUpper : Math.Min(rawUpper, _stFinalUpperBand);
                    newLower = prevClose < _stFinalLowerBand ? rawLower : Math.Max(rawLower, _stFinalLowerBand);
                }

                if (_stTrend == -1 && closeNow > newUpper)     _stTrend = 1;
                else if (_stTrend == 1  && closeNow < newLower) _stTrend = -1;

                _stFinalUpperBand = newUpper;
                _stFinalLowerBand = newLower;
            }

            if (_stInitialized)
                Print("  [✓] Supertrend warm-up: {0} bars, trend={1}", warmupBars, _stTrend == 1 ? "Bullish" : "Bearish");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RecoverExistingPosition (v2.9.0)
        //  Findet bei Bot-Start eine bereits offene Position mit passendem Label
        //  und rekonstruiert den TradeState konservativ, damit Trailing/Exits
        //  weiterlaufen. Partials werden als "done" markiert (kein Re-Trigger).
        // ─────────────────────────────────────────────────────────────────────
        private void RecoverExistingPosition()
        {
            Position found = null;
            foreach (var p in Positions)
            {
                if (p.SymbolName == SymbolName && p.Label == BotLabel)
                {
                    found = p;
                    break;
                }
            }

            if (found == null) return;

            bool   isLong     = found.TradeType == TradeType.Buy;
            double entry      = found.EntryPrice;
            double slPips     = 0;

            if (found.StopLoss.HasValue)
            {
                slPips = isLong
                    ? (entry - found.StopLoss.Value) / Symbol.PipSize
                    : (found.StopLoss.Value - entry) / Symbol.PipSize;
                slPips = Math.Abs(slPips);
            }

            // Fallback: Falls kein SL gesetzt oder SL = Entry (BE), dann ATR als Basis.
            if (slPips <= 0.1 && _atrSl != null)
            {
                double atr = _atrSl.Result.LastValue;
                if (!double.IsNaN(atr) && atr > 0)
                    slPips = (atr / Symbol.PipSize) * AtrSlMultiplier;
            }
            if (slPips <= 0.1) slPips = FixedSlPips;

            // BreakEven-Erkennung: SL bereits auf oder jenseits Entry → BE als erledigt markieren.
            bool beDone = false;
            if (found.StopLoss.HasValue)
            {
                beDone = isLong
                    ? found.StopLoss.Value >= entry - 0.1 * Symbol.PipSize
                    : found.StopLoss.Value <= entry + 0.1 * Symbol.PipSize;
            }

            double atrAtEntry = 0;
            if (TakeProfitMethod == TpMethod.IntervalLot
                && IntervalTpBasis == IntervalBasis.AtrMultiple
                && _atrSl != null)
            {
                atrAtEntry = _atrSl.Result.Last(1); // closed bar – no repaint
                if (double.IsNaN(atrAtEntry)) atrAtEntry = 0;
            }

            // Intervals-Rekonstruktion: Aus aktueller Preisbewegung schätzen.
            int intervalsTriggered = 0;
            if (TakeProfitMethod == TpMethod.IntervalLot)
            {
                double currentMove = isLong ? Symbol.Bid - entry : entry - Symbol.Ask;
                double intervalPx  = IntervalTpBasis == IntervalBasis.Pips
                    ? IntervalPips * Symbol.PipSize
                    : atrAtEntry * IntervalAtrMultiple;
                if (intervalPx > 0 && currentMove > 0)
                    intervalsTriggered = (int)Math.Floor(currentMove / intervalPx);
            }

            // v2.13.0 – P4: use persisted TradeState if position ID matches
            TradeState persisted = LoadPersistedTradeState();
            bool usePersisted = persisted != null && persisted.PositionId == found.Id;

            _currentTrade = new TradeState
            {
                PositionId           = found.Id,
                EntryPrice           = usePersisted && persisted.EntryPrice > 0 ? persisted.EntryPrice : entry,
                InitialSlPips        = usePersisted && persisted.InitialSlPips > 0 ? persisted.InitialSlPips : slPips,
                InitialVolume        = usePersisted && persisted.InitialVolume > 0 ? persisted.InitialVolume : found.VolumeInUnits,
                BreakEvenDone        = usePersisted ? persisted.BreakEvenDone : beDone,
                Partial1Done         = usePersisted ? persisted.Partial1Done : true,   // conservative if no persisted state
                Partial2Done         = usePersisted ? persisted.Partial2Done : true,
                Partial3Done         = usePersisted ? persisted.Partial3Done : true,
                ChandelierStopLong   = isLong  && found.StopLoss.HasValue ? found.StopLoss.Value : 0,
                ChandelierStopShort  = !isLong && found.StopLoss.HasValue ? found.StopLoss.Value : double.MaxValue,
                ConsecutiveEmaCloses = 0,
                IntervalsTriggered   = usePersisted ? persisted.IntervalsTriggered : intervalsTriggered,
                IntervalAtrAtEntry   = usePersisted && persisted.IntervalAtrAtEntry > 0 ? persisted.IntervalAtrAtEntry : atrAtEntry,
                EntryTime            = usePersisted && persisted.EntryTime != DateTime.MinValue ? persisted.EntryTime : found.EntryTime
            };

            if (usePersisted)
                Print("RECOVERY: Position Id={0} {1} – using PERSISTED state. " +
                      "BE={2} P1={3} P2={4} P3={5} Intervals={6} Entry={7:F5} SL={8:F1}p",
                    found.Id, found.TradeType,
                    _currentTrade.BreakEvenDone, _currentTrade.Partial1Done,
                    _currentTrade.Partial2Done, _currentTrade.Partial3Done,
                    _currentTrade.IntervalsTriggered, _currentTrade.EntryPrice, _currentTrade.InitialSlPips);
            else
                Print("RECOVERY: Adopted open position Id={0} {1} Entry={2:F5} | " +
                      "No persisted state – Partials set Done=true (conservative). InitialVolume={3:F0}u. " +
                      "Trailing/BE/Intervals only. EntryTime={4:yyyy-MM-dd HH:mm:ss}",
                    found.Id, found.TradeType, entry, found.VolumeInUnits, found.EntryTime);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CalculateScoreThresholds
        // ─────────────────────────────────────────────────────────────────────
        private void CalculateScoreThresholds()
        {
            _maxPossibleScore = 0;
            if (EnableEmaModule)        _maxPossibleScore += EmaMaxWeight;
            if (EnableBbModule)         _maxPossibleScore += BbMaxWeight;
            if (EnableSupertrendModule) _maxPossibleScore += SupertrendMaxWeight;
            if (EnablePatternsModule)   _maxPossibleScore += PatternsMaxWeight;
            if (EnableFiboModule)       _maxPossibleScore += FiboMaxWeight;
            if (EnableOscModule)        _maxPossibleScore += OscMaxWeight;
            if (EnableSrModule)         _maxPossibleScore += SrMaxWeight;
            if (EnableMacdModule)       _maxPossibleScore += MacdMaxWeight;
            if (EnableAdxScoreModule)   _maxPossibleScore += AdxScoreMaxWeight;

            // v2.12.0 – Category Caps: recompute max if enabled
            if (EnableCategoryCaps)
            {
                int trendMax = (EnableEmaModule ? EmaMaxWeight : 0) +
                               (EnableSupertrendModule ? SupertrendMaxWeight : 0) +
                               (EnableMacdModule ? MacdMaxWeight : 0);
                int mrMax    = (EnableBbModule ? BbMaxWeight : 0) +
                               (EnableFiboModule ? FiboMaxWeight : 0) +
                               (EnableSrModule ? SrMaxWeight : 0);
                int momMax   = EnableOscModule ? OscMaxWeight : 0;
                int paMax    = EnablePatternsModule ? PatternsMaxWeight : 0;

                _maxPossibleScore = Math.Min(trendMax, TrendCategoryCap) +
                                    Math.Min(mrMax,    MeanReversionCategoryCap) +
                                    Math.Min(momMax,   MomentumCategoryCap) +
                                    Math.Min(paMax,    PriceActionCategoryCap);
            }

            if (_maxPossibleScore == 0)
            {
                Print("CRITICAL: Zero modules enabled – permanent Standby.");
                _botInStandby = true;
                return;
            }

            _minRequiredScore = (int)Math.Ceiling(_maxPossibleScore * ConsensusRatio);
            string capInfo = EnableCategoryCaps ? string.Format(" [Caps: T={0} MR={1} M={2} PA={3}]",
                TrendCategoryCap, MeanReversionCategoryCap, MomentumCategoryCap, PriceActionCategoryCap) : "";
            Print("  Thresholds: Max={0} MinRequired={1} ({2:P0}){3}", _maxPossibleScore, _minRequiredScore, ConsensusRatio, capInfo);
        }


        // ─────────────────────────────────────────────────────────────────────
        //  GetWeekMonday – returns Monday date of the week containing d
        // ─────────────────────────────────────────────────────────────────────
        private static DateTime GetWeekMonday(DateTime d)
        {
            int back = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return d.Date.AddDays(-back);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ResetWeeklyState (v2.13.0 – P3)
        // ─────────────────────────────────────────────────────────────────────
        private void ResetWeeklyState(bool isOnStartCall = false)
        {
            bool loaded = false;
            if (isOnStartCall && MaxWeeklyDrawdownPercent > 0)
            {
                string wKey = GetWeekMonday(Server.Time).ToString("yyyyMMdd");
                string filePath = GetWeeklyStateFilePath(wKey);

                try
                {
                    if (File.Exists(filePath))
                    {
                        string json = File.ReadAllText(filePath);
                        var state = JsonSerializer.Deserialize<WeeklyState>(json);
                        if (state != null && state.WeekStart == wKey)
                        {
                            _weekStartEquity = state.WeekStartEquity;
                            loaded = true;
                            Print("  [✓] Loaded persisted weekly equity: {0:F2} {1}", _weekStartEquity, Account.Asset.Name);
                        }
                    }
                }
                catch { }
            }

            if (!loaded)
                _weekStartEquity = Account.Equity;

            _weeklyDrawdownBreached = false;
            _lastWeeklyResetDate = Server.Time;
            PersistWeeklyState();
        }



        // ─────────────────────────────────────────────────────────────────────
        //  ResetDailyState
        // ─────────────────────────────────────────────────────────────────────
        private void ResetDailyState(bool isOnStartCall = false)
        {
            bool isFirstReset = (_lastDailyResetDate == DateTime.MinValue);
            if (isFirstReset && !isOnStartCall)
                Print("INFO: Daily-DD-Zähler startet neu. Falls der Bot mitten am Tag gestartet wurde, " +
                      "beginnt die DD-Berechnung ab jetzt.");

            if (!(isOnStartCall && _persistedTodayLoaded))
                _dayStartEquity = Account.Equity;
            if (!(isOnStartCall && _persistedTodayLoaded))
                _tradesToday = 0;

            _dailyDrawdownBreached  = false;
            // Preserve the persisted rollover flag across same-day restarts.
            if (!(isOnStartCall && _persistedTodayLoaded))
                _rolloverCheckDoneToday = false;
            _weekendCloseFired      = false;
            _lastDailyResetDate     = Server.Time;

            PersistDailyState();

            if (_currentTrade == null)
                Print("  Daily reset  Equity={0:F2} {1}  Date={2:yyyy-MM-dd} (no open trade)",
                    _dayStartEquity, Account.Asset.Name, Server.Time);
            else
                Print("  Daily reset  Equity={0:F2} {1}  Date={2:yyyy-MM-dd} (trade Id={3} still open)",
                    _dayStartEquity, Account.Asset.Name, Server.Time, _currentTrade.PositionId);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Dashboard
        // ─────────────────────────────────────────────────────────────────────
        private void ParseNewsWindows()
        {
            _parsedNewsWindows = new List<TimeWindow>();

            if (string.IsNullOrWhiteSpace(NewsTimeWindows))
            {
                Print("ERROR: EnableNewsBlocker=true aber NewsTimeWindows ist leer. Bot entering Standby.");
                _botInStandby = true;
                return;
            }

            var endOfDay = new TimeSpan(23, 59, 59);
            var midnight = TimeSpan.Zero;

            var blocks = NewsTimeWindows.Split(',');
            foreach (var rawBlock in blocks)
            {
                var block = rawBlock.Trim();
                if (string.IsNullOrWhiteSpace(block)) continue;

                // Nur am LETZTEN Bindestrich aufsplitten, damit "23:30-00:30" korrekt trennt
                int dashIdx = block.LastIndexOf('-');
                if (dashIdx < 1)
                {
                    Print("ERROR: NewsTimeWindows Parsing fehlgeschlagen bei '{0}'. Erwartet HH:mm-HH:mm. Bot entering Standby.", block);
                    _botInStandby = true;
                    return;
                }

                string startStr = block.Substring(0, dashIdx).Trim();
                string endStr   = block.Substring(dashIdx + 1).Trim();

                TimeSpan tStart, tEnd;
                if (!TimeSpan.TryParse(startStr, out tStart) || !TimeSpan.TryParse(endStr, out tEnd))
                {
                    Print("ERROR: NewsTimeWindows Parsing fehlgeschlagen bei '{0}'. Erwartet HH:mm-HH:mm. Bot entering Standby.", block);
                    _botInStandby = true;
                    return;
                }

                if (tEnd > tStart)
                {
                    // Normales Fenster – gleicher Tag
                    _parsedNewsWindows.Add(new TimeWindow { Start = tStart, End = tEnd });
                }
                else
                {
                    // Nacht-übergreifendes Fenster (z.B. 23:30–00:30) → zwei Teilfenster
                    Print("  [i] News-Fenster '{0}' überschreitet Mitternacht – wird in zwei Teilfenster aufgesplittet.", block);
                    _parsedNewsWindows.Add(new TimeWindow { Start = tStart, End = endOfDay });
                    _parsedNewsWindows.Add(new TimeWindow { Start = midnight, End = tEnd });
                }
            }

            Print("  [✓] News blocker windows parsed: {0} (inkl. etwaiger Midnight-Splits)", _parsedNewsWindows.Count);
        }

        private bool IsInsideNewsWindow(TimeSpan now, out TimeWindow activeWindow)
        {
            foreach (var window in _parsedNewsWindows)
            {
                if (now >= window.Start && now <= window.End)
                {
                    activeWindow = window;
                    return true;
                }
            }

            activeWindow = null;
            return false;
        }

        private double CalculateAdrPips()
        {
            if (_dailyBars == null)
                _dailyBars = MarketData.GetBars(TimeFrame.Daily);

            if (_dailyBars == null || _dailyBars.Count < AdrPeriod + 2)
                return -1;

            double sum = 0;
            int count = 0;
            for (int i = 1; i <= AdrPeriod && i < _dailyBars.Count; i++)
            {
                double range = (_dailyBars.HighPrices.Last(i) - _dailyBars.LowPrices.Last(i)) / Symbol.PipSize;
                if (range > 0 && !double.IsNaN(range))
                {
                    sum += range;
                    count++;
                }
            }

            return count > 0 ? sum / count : -1;
        }

        private double CalculateCurrentDayRangePips()
        {
            if (_dailyBars == null)
                _dailyBars = MarketData.GetBars(TimeFrame.Daily);

            if (_dailyBars == null || _dailyBars.Count < 1)
                return -1;

            double range = (_dailyBars.HighPrices.LastValue - _dailyBars.LowPrices.LastValue) / Symbol.PipSize;
            return !double.IsNaN(range) ? range : -1;
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            if (args == null || args.Position == null)
                return;

            if (args.Position.SymbolName != SymbolName)
                return;

            if (args.Position.Label != BotLabel)
                return;

            if (args.Position.NetProfit < 0)
                _consecutiveLosses++;
            else
                _consecutiveLosses = 0;

            if (MaxConsecutiveLosses > 0 && _consecutiveLosses >= MaxConsecutiveLosses)
            {
                _cooldownEndTime = Server.Time.AddMinutes(CooldownMinutesAfterLossStreak);
                Print("ACCOUNT PROTECTION: {0} Verluste in Folge. Cooldown aktiviert bis {1:HH:mm}.",
                    _consecutiveLosses, _cooldownEndTime);
                _consecutiveLosses = 0;
            }

            // v2.13.0 – P4: delete persisted TradeState so a fresh trade starts clean
            if (_currentTrade != null && args.Position.Id == _currentTrade.PositionId)
                DeletePersistedTradeState();

            // v2.13.0 – P2: Trade Attribution CSV log
            if (EnableTradeAttributionLog
                && _currentTrade != null
                && args.Position.Id == _currentTrade.PositionId
                && _currentTrade.EntryModuleScores != null)
            {
                var   cp     = args.Position;
                bool  isLong = cp.TradeType == TradeType.Buy;
                double pnlPips = cp.NetProfit / (Symbol.PipValue * cp.VolumeInUnits);
                double exitPrice = isLong
                    ? (cp.EntryPrice + pnlPips * Symbol.PipSize)
                    : (cp.EntryPrice - pnlPips * Symbol.PipSize);
                int[] s = _currentTrade.EntryModuleScores;
                string csvLine = string.Format("{0},{1},{2:F5},{3:F5},{4:F2},{5:F2},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15},{16:F2},{17:F4},{18},{19:F1}",
                    cp.EntryTime.ToString("O"), cp.TradeType,
                    cp.EntryPrice, exitPrice, pnlPips, cp.NetProfit,
                    s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7], s[8],
                    _currentTrade.EntryTotalScore,
                    _currentTrade.EntrySpreadPips,
                    _currentTrade.EntryAtrPips,
                    _currentTrade.EntryHtfRegime,
                    _currentTrade.EntryAdxValue);
                Print("[TRADECSV] {0}", csvLine);

                if (EnableTradeAttributionLog && !string.IsNullOrEmpty(AttributionLogFilePath))
                {
                    try
                    {
                        const string header = "EntryTime,Direction,EntryPrice,ExitPrice,PnlPips,NetProfit,EMA,BB,ST,PA,FIB,OSC,SR,MACD,ADX,TotalScore,SpreadPips,AtrPips,HtfRegime,AdxValue";
                        if (!File.Exists(AttributionLogFilePath))
                            File.WriteAllText(AttributionLogFilePath, header + Environment.NewLine);
                        File.AppendAllText(AttributionLogFilePath, csvLine + Environment.NewLine);
                    }
                    catch (Exception ex)
                    {
                        Print("WARNING: Attribution file write failed ({0}): {1}", AttributionLogFilePath, ex.Message);
                    }
                }

                bool isWinner = cp.NetProfit >= 0;
                if (isWinner)
                {
                    _attrCountWin++;
                    for (int i = 0; i < 9; i++) _attrSumScoresWin[i] += s[i];
                }
                else
                {
                    _attrCountLoss++;
                    for (int i = 0; i < 9; i++) _attrSumScoresLoss[i] += s[i];
                }

                for (int i = 0; i < 9; i++)
                {
                    int bucket = Math.Min(3, s[i]);
                    if (isWinner) _attrScoreWinByBucket[i, bucket]++;
                    else          _attrScoreLossByBucket[i, bucket]++;
                }
            }

            // v3.1.7 – Session × DoW Attribution
            if (EnableSessionAttribution
                && _currentTrade != null
                && args.Position.Id == _currentTrade.PositionId)
            {
                DateTime entryT = _currentTrade.EntryTime != DateTime.MinValue
                    ? _currentTrade.EntryTime
                    : args.Position.EntryTime;
                if (_currentTrade.EntryTime == DateTime.MinValue)
                    Print("WARNING SessionAttribution: EntryTime missing – using position.EntryTime as fallback.");
                int dow = GetDowBucket(entryT);
                int ses = GetSessionBucket(entryT);
                if (dow >= 0)
                {
                    if (args.Position.NetProfit >= 0) _sessionWinCount[dow, ses]++;
                    else                              _sessionLossCount[dow, ses]++;
                    _sessionPnlSum[dow, ses] += args.Position.NetProfit;
                }
            }

            // State-Null-Konsistenz: closed trade → clear in-memory state so stale
            // references cannot leak into the next entry cycle.
            if (_currentTrade != null && args.Position.Id == _currentTrade.PositionId)
                _currentTrade = null;

            PersistDailyState();
        }

        private const string DashboardKey = "10FoldDashboard";

        private void InitializeDashboard()
        {
            UpdateDashboard();
            Print("  [✓] Dashboard initialized at corner={0}", DashboardCorner);
        }

        private void UpdateDashboard()
        {
            if (!ShowDashboard) return;

            var (vAlign, hAlign) = GetDashboardAlignment();
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;

            string htfStatus = "N/A";
            if ((EnableHtfFilter || EnableHtfBreakExit) && _htfBars != null && _htfEma != null)
            {
                double htfClose = _htfBars.ClosePrices.Last(1);
                double htfEma   = _htfEma.Result.Last(1); // closed bar – no repaint
                htfStatus = htfClose > htfEma ? "▲ BULL" : "▼ BEAR";
            }

            double openRiskPct = 0;
            if (_currentTrade != null)
            {
                var pos = Positions.FindById(_currentTrade.PositionId);
                if (pos != null && pos.StopLoss.HasValue)
                {
                    double slDist = Math.Abs(
                        (pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask)
                        - pos.StopLoss.Value);
                    double slPips    = slDist / Symbol.PipSize;
                    double riskMoney = slPips * Symbol.PipValue * pos.VolumeInUnits;
                    openRiskPct = Account.Balance > 0 ? (riskMoney / Account.Balance) * 100.0 : 0;
                }
            }

            double dayDdPct = _dayStartEquity > 0
                ? ((_dayStartEquity - Account.Equity) / _dayStartEquity) * 100.0 : 0;
            dayDdPct = Math.Max(0, dayDdPct);

            string botStatus;
            if (_botInStandby)               botStatus = "⛔ STANDBY (config error)";
            else if (_dailyDrawdownBreached) botStatus = "⛔ DD LIMIT HIT";
            else if (_currentTrade != null)  botStatus = "● IN TRADE";
            else                             botStatus = "◌ SCANNING";

            // Score aus OnBar-Cache – kein Neuberechnen im Tick-Pfad
            string scoreStr = "–";
            if (!_botInStandby && _maxPossibleScore > 0)
            {
                scoreStr = string.Format("L:{0} S:{1} (max {2})",
                    _cachedLongScore, _cachedShortScore, _maxPossibleScore);
            }

            // v2.8.0 – Interval-Lot-Status für Dashboard
            string intervalStr = "";
            if (TakeProfitMethod == TpMethod.IntervalLot && _currentTrade != null)
            {
                string maxStr = MaxIntervals == 0 ? "∞" : MaxIntervals.ToString();
                intervalStr = string.Format("  Intervals: {0}/{1} done",
                    _currentTrade.IntervalsTriggered, maxStr);
            }

            string nl   = "\n";
            string line = "─────────────────────────────";
            string text =
                "╔═══════════════════════════╗"  + nl +
                "║  10-FOLD BOT  v3.1.8      ║"  + nl +
                "╚═══════════════════════════╝"  + nl +
                string.Format("  Status   : {0}", botStatus)              + nl +
                line                                                        + nl +
                string.Format("  Symbol   : {0}", SymbolName)              + nl +
                string.Format("  Timeframe: {0}", TimeFrame)               + nl +
                string.Format("  Spread   : {0:F1}p  (max {1:F1}p)", spreadPips, MaxAllowedSpread) + nl +
                string.Format("  HTF Trend: {0}  [{1}]", htfStatus, HtfTimeFrame)                  + nl +
                line                                                        + nl +
                string.Format("  Score    : {0}", scoreStr)                + nl +
                string.Format("  FloatLoss: {0:F2}%  (max {1:F1}%, of {2})",
                    openRiskPct, MaxFloatingLossPercent,
                    FloatingLossDenominator == FloatingLossDenom.Equity ? "Equity" : "Balance") + nl +
                string.Format("  Day DD   : {0:F2}%  (max {1:F1}%)", dayDdPct, MaxDailyDrawdownPercent)   + nl +
                (string.IsNullOrEmpty(intervalStr) ? "" : intervalStr + nl) +
                line                                                        + nl +
                string.Format("  Balance  : {0:F2} {1}", Account.Balance, Account.Asset.Name) + nl +
                string.Format("  Equity   : {0:F2} {1}", Account.Equity,  Account.Asset.Name);

            Chart.DrawStaticText(DashboardKey, text, vAlign, hAlign, Chart.ColorSettings.ForegroundColor);
        }

        private (VerticalAlignment, HorizontalAlignment) GetDashboardAlignment()
        {
            switch (DashboardCorner)
            {
                case DashboardCornerPosition.TopRight:    return (VerticalAlignment.Top,    HorizontalAlignment.Right);
                case DashboardCornerPosition.BottomLeft:  return (VerticalAlignment.Bottom, HorizontalAlignment.Left);
                case DashboardCornerPosition.BottomRight: return (VerticalAlignment.Bottom, HorizontalAlignment.Right);
                case DashboardCornerPosition.TopLeft:
                default:                                  return (VerticalAlignment.Top,    HorizontalAlignment.Left);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  OnBar  – Market Filter + Entry Scoring
        // ════════════════════════════════════════════════════════════════════
        protected override void OnBar()
        {
            if (Server.Time.Date > _lastDailyResetDate.Date)
                ResetDailyState();

            DateTime thisMonday = GetWeekMonday(Server.Time);
            if (thisMonday > _lastWeeklyResetDate.Date)
                ResetWeeklyState();

            if (_botInStandby)          { Print("OnBar: Bot in Standby – skipped."); return; }
            if (_dailyDrawdownBreached) { Print("OnBar: Daily drawdown breached – no new entries."); return; }
            if (MaxWeeklyDrawdownPercent > 0 && _weeklyDrawdownBreached)
                { Print("OnBar: Weekly drawdown breached – no new entries until Monday."); return; }
            if (_currentTrade != null)
            {
                PersistTradeState(); // v2.13.0 – P4: keep state current (once per bar)
                return;
            }

            if (EnableSupertrendModule) UpdateSupertrendState();

            // VWAP incremental (v2.12.0): einmal pro Bar, kein O(480) Loop
            UpdateVwapIncremental();

            bool longTradable  = IsMarketTradable(TradeType.Buy);
            bool shortTradable = IsMarketTradable(TradeType.Sell);

            // Scores berechnen und cachen (Dashboard nutzt Cache, kein Tick-Spam)
            _cachedLongScore  = longTradable  ? CalculateEntryScore(TradeType.Buy)  : 0;
            _cachedShortScore = shortTradable ? CalculateEntryScore(TradeType.Sell) : 0;

            int adjustedMinReq = _minRequiredScore;
            if (EnableVolRegime)
            {
                double regime = CalculateVolRegime();
                if (regime < 0.7)
                    adjustedMinReq = (int)Math.Ceiling(_maxPossibleScore * (ConsensusRatio + 0.05));
                else if (regime > 1.4)
                    adjustedMinReq = (int)Math.Ceiling(_maxPossibleScore * (ConsensusRatio - 0.05));
                Print("VolRegime: {0:F2} → MinReq={1}", regime, adjustedMinReq);
            }

            bool longQualifies  = _cachedLongScore  >= adjustedMinReq;
            bool shortQualifies = _cachedShortScore >= adjustedMinReq;

            TradeType? selectedDir   = null;
            int        selectedScore = 0;

            if (longQualifies && shortQualifies)
            {
                if (BlockOnConflictingSignals)
                    Print("OnBar: Both Long ({0}) and Short ({1}) qualify – conflicting signal blocked.",
                        _cachedLongScore, _cachedShortScore);
                else if (_cachedLongScore > _cachedShortScore)
                    { selectedDir = TradeType.Buy;  selectedScore = _cachedLongScore; }
                else if (_cachedShortScore > _cachedLongScore)
                    { selectedDir = TradeType.Sell; selectedScore = _cachedShortScore; }
                else
                    Print("OnBar: Both qualify with equal scores ({0}) – no trade (tie).", _cachedLongScore);
            }
            else if (longQualifies)
                { selectedDir = TradeType.Buy;  selectedScore = _cachedLongScore; }
            else if (shortQualifies)
                { selectedDir = TradeType.Sell; selectedScore = _cachedShortScore; }

            if (selectedDir.HasValue)
            {
                double risk = CalculateRiskPercent(selectedScore);
                Print("Entry candidate {0}: Score={1}/{2}  Risk={3:F2}%  – calling TryOpenTrade",
                    selectedDir.Value, selectedScore, _maxPossibleScore, risk);
                if (EnableVerboseScoreLogging)
                    LogScoreBreakdown(selectedDir.Value, selectedScore);
                TryOpenTrade(selectedDir.Value, selectedScore);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  OnTick  – Trade Management + Exits
        // ════════════════════════════════════════════════════════════════════
        protected override void OnTick()
        {
            if (_botInStandby) return;

            CheckDailyDrawdown();

            // v2.9.0 – Dashboard-Throttle: max 1x/Sek., um Chart-Rendering-Overhead pro Tick zu vermeiden.
            if (ShowDashboard &&
                (Server.Time - _lastDashboardUpdate).TotalMilliseconds >= DashboardThrottleMs)
            {
                UpdateDashboard();
                _lastDashboardUpdate = Server.Time;
            }

            if (_dailyDrawdownBreached) return;
            if (_currentTrade == null)  return;

            ManageOpenTrade();
            CheckExitConditions();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CheckDailyDrawdown
        // ─────────────────────────────────────────────────────────────────────
        private void CheckDailyDrawdown()
        {
            if (!_dailyDrawdownBreached)
            {
                double equityDrop = _dayStartEquity - Account.Equity;
                double dropPct    = _dayStartEquity > 0 ? (equityDrop / _dayStartEquity) * 100.0 : 0;

                if (dropPct >= MaxDailyDrawdownPercent)
                {
                    _dailyDrawdownBreached = true;
                    Print("ACCOUNT PROTECTION: Daily drawdown {0:F2}% >= limit {1:F1}%. " +
                          "No new entries for the rest of today. StartEquity={2:F2} Current={3:F2}",
                          dropPct, MaxDailyDrawdownPercent, _dayStartEquity, Account.Equity);
                }
            }

            if (MaxWeeklyDrawdownPercent > 0 && !_weeklyDrawdownBreached)
            {
                double weekDrop = _weekStartEquity - Account.Equity;
                double weekPct  = _weekStartEquity > 0 ? (weekDrop / _weekStartEquity) * 100.0 : 0;
                if (weekPct >= MaxWeeklyDrawdownPercent)
                {
                    _weeklyDrawdownBreached = true;
                    Print("ACCOUNT PROTECTION: Weekly drawdown {0:F2}% >= limit {1:F1}%. " +
                          "No new entries until Monday. WeekStartEquity={2:F2} Current={3:F2}",
                          weekPct, MaxWeeklyDrawdownPercent, _weekStartEquity, Account.Equity);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  MARKET FILTER
        // ════════════════════════════════════════════════════════════════════
        private bool IsMarketTradable(TradeType direction, bool logRejections = true)
        {
            if (EnableTimeFilter)
            {
                TimeSpan now   = Server.Time.TimeOfDay;
                TimeSpan start = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
                TimeSpan end   = new TimeSpan(SessionEndHour,   SessionEndMinute,   0);

                if (now < start || now >= end)
                {
                    if (logRejections && ShouldLogRejection("SessionFilter"))
                        Print("Trade rejected [{0}]: Session filter. ServerTime={1:HH:mm} not in {2:hh\\:mm}–{3:hh\\:mm}.",
                            direction, Server.Time, start, end);
                    return false;
                }
            }

            if (BlockFridayNewTrades && Server.Time.DayOfWeek == DayOfWeek.Friday)
            {
                TimeSpan weekendCloseTime = new TimeSpan(WeekendCloseHour, WeekendCloseMinute, 0);
                if (Server.Time.TimeOfDay >= weekendCloseTime)
                {
                    if (logRejections && ShouldLogRejection("FridayBlock"))
                        Print("Trade rejected [{0}]: New trades blocked on Friday after {1:hh\\:mm}.",
                            direction, weekendCloseTime);
                    return false;
                }
            }

            if (Server.Time < _cooldownEndTime)
            {
                if (logRejections && ShouldLogRejection("Cooldown"))
                    Print("Trade rejected [{0}]: Cooldown aktiv bis {1:HH:mm}.",
                        direction, _cooldownEndTime);
                return false;
            }

            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
            {
                if (logRejections && ShouldLogRejection("MaxTrades"))
                    Print("Trade rejected [{0}]: Max Trades pro Tag ({1}) erreicht.",
                        direction, MaxTradesPerDay);
                return false;
            }

            if (EnableNewsBlocker)
            {
                TimeWindow activeWindow;
                if (IsInsideNewsWindow(Server.Time.TimeOfDay, out activeWindow))
                {
                    if (logRejections && ShouldLogRejection("News"))
                        Print("Trade rejected [{0}]: News-Fenster ({1:hh\\:mm}-{2:hh\\:mm}).",
                            direction, activeWindow.Start, activeWindow.End);
                    return false;
                }
            }

            if (EnableVolatilityFilter)
            {
                double adrPips = CalculateAdrPips();
                double currentDayRangePips = CalculateCurrentDayRangePips();
                if (adrPips > 0 && currentDayRangePips > adrPips * MaxAdrRatio)
                {
                    if (logRejections && ShouldLogRejection("Volatility"))
                        Print("Trade rejected [{0}]: Volatility Filter. Heutige Range ({1:F1}p) ueberschreitet ADR-Limit ({2:F1}p * {3:F1}).",
                            direction, currentDayRangePips, adrPips, MaxAdrRatio);
                    return false;
                }
            }

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxAllowedSpread)
            {
                if (logRejections && ShouldLogRejection("Spread"))
                    Print("Trade rejected [{0}]: Spread {1:F2} > MaxAllowedSpread {2:F1} pips.",
                        direction, spreadPips, MaxAllowedSpread);
                return false;
            }

            if (EnableDynamicSpreadCap && _atrSl != null)
            {
                double atrPips = _atrSl.Result.Last(1) / Symbol.PipSize;
                if (!double.IsNaN(atrPips) && atrPips > 0)
                {
                    double dynCap = Math.Min(MaxAllowedSpread, atrPips * DynamicSpreadAtrRatio);
                    if (spreadPips > dynCap)
                    {
                        if (logRejections && ShouldLogRejection("DynSpread"))
                            Print("Trade rejected [{0}]: DynSpread {1:F2}p > dyn-cap {2:F2}p (ATR×{3:F2}).",
                                direction, spreadPips, dynCap, DynamicSpreadAtrRatio);
                        return false;
                    }
                }
            }

            if (EnableHtfFilter && _htfBars != null && _htfEma != null)
            {
                double htfClose = _htfBars.ClosePrices.Last(1);
                double htfEma   = _htfEma.Result.Last(1); // closed bar – no repaint

                if (direction == TradeType.Buy && htfClose < htfEma)
                {
                    if (logRejections && ShouldLogRejection("HTFLong"))
                        Print("Trade rejected [Long]: HTF filter. HTF close {0:F5} < HTF EMA {1:F5}.", htfClose, htfEma);
                    return false;
                }
                if (direction == TradeType.Sell && htfClose > htfEma)
                {
                    if (logRejections && ShouldLogRejection("HTFShort"))
                        Print("Trade rejected [Short]: HTF filter. HTF close {0:F5} > HTF EMA {1:F5}.", htfClose, htfEma);
                    return false;
                }
            }

            // FloatingLossMode:
            //   FloatingLossOnly  – only positions with NetProfit < 0 contribute
            //   GrossUnrealised   – absolute P&L of every open position (v3.0.0 rename of NetUnrealised)
            double totalUnrealised = 0;
            foreach (var pos in Positions)
            {
                if (pos.SymbolName == SymbolName)
                {
                    double contrib = FloatingLossMode == FloatingLossGateMode.FloatingLossOnly
                        ? (pos.NetProfit < 0 ? Math.Abs(pos.NetProfit) : 0)
                        : Math.Abs(pos.NetProfit);
                    totalUnrealised += contrib;
                }
            }
            double denom = FloatingLossDenominator == FloatingLossDenom.Equity
                ? Account.Equity : Account.Balance;
            double exposurePct = denom > 0 ? (totalUnrealised / denom) * 100.0 : 0;
            if (exposurePct >= MaxFloatingLossPercent)
            {
                if (logRejections && ShouldLogRejection("FloatingLoss"))
                    Print("Trade rejected [{0}]: Max floating loss {1:F2}% >= limit {2:F1}%.",
                        direction, exposurePct, MaxFloatingLossPercent);
                return false;
            }

            if (EnableAdxFilter && _dms != null)
            {
                double adxVal   = _dms.ADX.LastValue;
                double diPlus   = _dms.DIPlus.LastValue;
                double diMinus  = _dms.DIMinus.LastValue;

                if (double.IsNaN(adxVal))
                {
                    if (logRejections && ShouldLogRejection("ADXInvalid"))
                        Print("Trade rejected [{0}]: ADX value invalid (NaN).", direction);
                    return false;
                }

                if (adxVal < MinAdxValue)
                {
                    if (logRejections && ShouldLogRejection("ADXValue"))
                        Print("Trade rejected [{0}]: ADX {1:F1} < MinAdxValue {2:F1} (chop filter).",
                            direction, adxVal, MinAdxValue);
                    return false;
                }

                if (RequireDiAlignment && !double.IsNaN(diPlus) && !double.IsNaN(diMinus))
                {
                    bool aligned = direction == TradeType.Buy ? diPlus > diMinus : diMinus > diPlus;
                    if (!aligned)
                    {
                        if (logRejections && ShouldLogRejection("DIAlignment"))
                            Print("Trade rejected [{0}]: DI not aligned (DI+={1:F1} DI-={2:F1}).",
                                direction, diPlus, diMinus);
                        return false;
                    }
                }
            }

            if (logRejections)
                Print("Market tradable for {0}. Spread={1:F2}p  FloatingLoss={2:F2}%", direction, spreadPips, exposurePct);
            return true;
        }


        #region Risk & Position Sizing
        // ════════════════════════════════════════════════════════════════════
        //  RISK SCALING
        // ════════════════════════════════════════════════════════════════════
        // P5: Liefert die konfigurierte Risikobasis (Balance = stabiler, Equity = dynamischer)
        private double RiskBaseAmount => RiskBaseMode == RiskBase.Equity
            ? Account.Equity : Account.Balance;

        private double CalculateRiskPercent(int score)
        {
            if (_maxPossibleScore == _minRequiredScore)
                return MaxRiskPercent;

            double ratio = (double)(score - _minRequiredScore)
                         / (_maxPossibleScore - _minRequiredScore);

            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
            double risk = MinRiskPercent + (MaxRiskPercent - MinRiskPercent) * ratio;

            // P3: Anti-Martingale – reduce size after consecutive losses
            if (ConsecLossSizeReducer < 1.0 && _consecutiveLosses >= 1)
            {
                risk *= Math.Pow(ConsecLossSizeReducer, _consecutiveLosses);
                // Floor at 50 % of MinRiskPercent to avoid effectively zero risk on long losing streaks.
                risk = Math.Max(risk, MinRiskPercent * 0.5);
            }

            if (EnableVolTargetedSizing && _atrSl != null && VolTargetAtrBaselinePips > 0)
            {
                double atrPips = _atrSl.Result.Last(1) / Symbol.PipSize;
                if (atrPips > 0 && !double.IsNaN(atrPips))
                {
                    double scale = VolTargetAtrBaselinePips / atrPips;
                    scale = Math.Max(0.5, Math.Min(2.0, scale));
                    risk *= scale;
                }
            }

            return risk;
        }

        // ════════════════════════════════════════════════════════════════════
        //  STOP LOSS CALCULATOR
        // ════════════════════════════════════════════════════════════════════
        private double CalculateSlPips(TradeType direction)
        {
            double slPips = 0;

            switch (StopLossMethod)
            {
                case SlMethod.FixedPips:
                    slPips = FixedSlPips + SlBufferPips;
                    break;

                case SlMethod.AtrBased:
                    double atr = _atrSl.Result.Last(1); // closed bar – no repaint
                    if (double.IsNaN(atr) || atr <= 0)
                    {
                        Print("CalculateSlPips: ATR value invalid ({0:F6}). Skipping trade.", atr);
                        return -1;
                    }
                    slPips = (atr / Symbol.PipSize) * AtrSlMultiplier + SlBufferPips;
                    break;

                case SlMethod.SwingHighLow:
                    double entryRef   = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
                    double swingLevel = FindSwingLevel(direction, SwingSlLookback);
                    if (swingLevel < 0)
                    {
                        Print("CalculateSlPips: No swing found in {0} bars – falling back to ATR.", SwingSlLookback);
                        double atrFb = _atrSl.Result.Last(1);
                        slPips = (atrFb / Symbol.PipSize) * AtrSlMultiplier + SlBufferPips;
                    }
                    else
                    {
                        slPips = direction == TradeType.Buy
                            ? (entryRef - swingLevel) / Symbol.PipSize + SlBufferPips
                            : (swingLevel - entryRef) / Symbol.PipSize + SlBufferPips;
                    }
                    break;
            }

            if (IncludeCommissionInRisk && EstimatedCommissionPips > 0)
                slPips += EstimatedCommissionPips;

            if (slPips <= 0 || double.IsNaN(slPips))
            {
                Print("CalculateSlPips: Result invalid ({0:F1} pips). Skipping trade.", slPips);
                return -1;
            }

            return slPips;
        }

        private double FindSwingLevel(TradeType direction, int lookback)
        {
            var pivots = GetRecentPivots(Math.Min(lookback, Bars.Count - 3));

            foreach (var p in pivots)
            {
                if (direction == TradeType.Buy  && !p.IsHigh) return p.Price;
                if (direction == TradeType.Sell &&  p.IsHigh) return p.Price;
            }

            return -1;
        }

        // ════════════════════════════════════════════════════════════════════
        //  TAKE PROFIT CALCULATOR
        // ════════════════════════════════════════════════════════════════════
        private double CalculateTpPips(TradeType direction, double slPips)
        {
            switch (TakeProfitMethod)
            {
                case TpMethod.Rrr:
                    return slPips * RrrTarget;

                case TpMethod.AtrMultiplier:
                    double atr = _atrSl.Result.Last(1); // closed bar – no repaint
                    if (double.IsNaN(atr) || atr <= 0)
                    {
                        Print("CalculateTpPips: ATR invalid – falling back to RRR.");
                        return slPips * RrrTarget;
                    }
                    return (atr / Symbol.PipSize) * AtrTpMultiplier;

                case TpMethod.NextSwingExtreme:
                    TradeType opp   = direction == TradeType.Buy ? TradeType.Sell : TradeType.Buy;
                    double swingTgt = FindSwingLevel(opp, SwingTpLookback);
                    if (swingTgt < 0)
                    {
                        Print("CalculateTpPips: No opposing swing found – falling back to RRR.");
                        return slPips * RrrTarget;
                    }
                    double entry   = direction == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
                    double tpSwing = direction == TradeType.Buy
                        ? (swingTgt - entry) / Symbol.PipSize
                        : (entry - swingTgt) / Symbol.PipSize;
                    return tpSwing > slPips ? tpSwing : slPips * RrrTarget;

                case TpMethod.Runner:
                    return 0;

                case TpMethod.IntervalLot:
                    // Kein fester TP – Position wird in Intervallen durch TryIntervalLotClose geschlossen.
                    return 0;
            }

            return slPips * RrrTarget;
        }

        #endregion // Risk & Position Sizing

        #region Trade Management
        // ════════════════════════════════════════════════════════════════════
        //  TRADE EXECUTION
        // ════════════════════════════════════════════════════════════════════
        private void TryOpenTrade(TradeType direction, int score)
        {
            if (_currentTrade != null)
            {
                Print("TryOpenTrade: Position already open (Id={0}) – skipping.", _currentTrade.PositionId);
                return;
            }
            if (_botInStandby || _dailyDrawdownBreached)
            {
                Print("TryOpenTrade: Standby or DrawdownBreached – skipping.");
                return;
            }

            double slPips = CalculateSlPips(direction);
            if (slPips < 0) return;

            double tpPips      = CalculateTpPips(direction, slPips);
            double riskPercent = CalculateRiskPercent(score);
            if (riskPercent <= 0)
            {
                Print("TryOpenTrade: RiskPercent={0:F3} <= 0 – skipping.", riskPercent);
                return;
            }

            // P5: Risikobasis je nach RiskBaseMode (Balance/Equity)
            double riskMoney = RiskBaseAmount * (riskPercent / 100.0);
            if (riskMoney <= 0) { Print("TryOpenTrade: RiskMoney={0:F2} <= 0 – skipping.", riskMoney); return; }

            double slValuePerUnit = slPips * Symbol.PipValue;
            if (slValuePerUnit <= 0 || double.IsNaN(slValuePerUnit))
            {
                Print("TryOpenTrade: SlValuePerUnit invalid ({0:F6}) – skipping.", slValuePerUnit);
                return;
            }

            double rawUnits = riskMoney / slValuePerUnit;
            if (double.IsNaN(rawUnits) || rawUnits <= 0)
            {
                Print("TryOpenTrade: RawUnits={0:F2} invalid – skipping.", rawUnits);
                return;
            }

            double volumeInUnits = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);
            double minUnits      = Symbol.VolumeInUnitsMin;
            double maxUnits      = Symbol.VolumeInUnitsMax;

            if (volumeInUnits < minUnits)
            {
                Print("TryOpenTrade [{0}]: Volume {1:F0} units < Broker-Min ({2:F0} units = {3:F2} lots). Skipping.",
                    direction, volumeInUnits, minUnits, minUnits / Symbol.LotSize);
                return;
            }
            if (volumeInUnits > maxUnits)
            {
                Print("TryOpenTrade [{0}]: Volume clamped to Broker-Max ({1:F0} units = {2:F2} lots).",
                    direction, maxUnits, maxUnits / Symbol.LotSize);
                volumeInUnits = maxUnits;
            }

            Print("TryOpenTrade: Dir={0} | Score={1}/{2} | Risk={3:F2}% ({4:F2} {5}) | " +
                  "SL={6:F1}p | TP={7} | Vol={8:F0}u ({9:F2} lots)",
                direction, score, _maxPossibleScore,
                riskPercent, riskMoney, Account.Asset.Name,
                slPips,
                tpPips > 0 ? tpPips.ToString("F1") + "p" : "Runner (no TP)",
                volumeInUnits, volumeInUnits / Symbol.LotSize);

            var result = ExecuteMarketOrder(
                direction, SymbolName, volumeInUnits, BotLabel,
                slPips, tpPips > 0 ? (double?)tpPips : null);

            if (!result.IsSuccessful)
            {
                Print("TryOpenTrade: Order FAILED! Error={0}", result.Error);
                return;
            }

            // ATR bei Entry einfrieren, falls IntervalLot im ATR-Mode arbeitet
            double atrAtEntry = 0;
            if (TakeProfitMethod == TpMethod.IntervalLot
                && IntervalTpBasis == IntervalBasis.AtrMultiple
                && _atrSl != null)
            {
                atrAtEntry = _atrSl.Result.Last(1); // closed bar – no repaint
                if (double.IsNaN(atrAtEntry) || atrAtEntry <= 0)
                {
                    Print("TryOpenTrade: IntervalLot/ATR mode requested but ATR invalid ({0:F6}) – skipping.", atrAtEntry);
                    ClosePosition(result.Position);
                    return;
                }
            }

            _currentTrade = new TradeState
            {
                PositionId           = result.Position.Id,
                EntryPrice           = result.Position.EntryPrice,
                InitialSlPips        = slPips,
                InitialVolume        = volumeInUnits,
                BreakEvenDone        = false,
                Partial1Done         = false,
                Partial2Done         = false,
                Partial3Done         = false,
                ChandelierStopLong   = 0,
                ChandelierStopShort  = double.MaxValue,
                ConsecutiveEmaCloses = 0,
                IntervalsTriggered   = 0,
                IntervalAtrAtEntry   = atrAtEntry,
                EntryTime            = result.Position.EntryTime  // v2.12.0: für Max-Hold-Time-Exit
            };

            _totalTradesOpened++;
            _tradesToday++;
            PersistDailyState();
            PersistTradeState(); // v2.13.0 – P4: persist trade state for restart recovery

            // v2.13.0 – P2: capture entry attribution data for CSV log
            if (EnableTradeAttributionLog)
            {
                int[]  mScores = direction == TradeType.Buy
                    ? (int[])_cachedLongModuleScores.Clone()
                    : (int[])_cachedShortModuleScores.Clone();
                double entrySpread = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
                double entryAtr    = _atrSl != null && !double.IsNaN(_atrSl.Result.Last(1))
                    ? _atrSl.Result.Last(1) / Symbol.PipSize : 0;
                string htfRegime = "NA";
                if (_htfBars != null && _htfEma != null)
                    htfRegime = _htfBars.ClosePrices.Last(1) > _htfEma.Result.Last(1) ? "BULL" : "BEAR";
                double adxEntry = _dms != null && !double.IsNaN(_dms.ADX.Last(1)) ? _dms.ADX.Last(1) : 0;

                _currentTrade.EntryModuleScores = mScores;
                _currentTrade.EntryTotalScore   = score;
                _currentTrade.EntrySpreadPips   = entrySpread;
                _currentTrade.EntryAtrPips      = entryAtr;
                _currentTrade.EntryHtfRegime    = htfRegime;
                _currentTrade.EntryAdxValue     = adxEntry;
            }

            // v2.8.0: Enhanced Trade Logging – alle Modul-Scores & R:R im Eröffnungslog
            string tpLabel;
            if (TakeProfitMethod == TpMethod.IntervalLot)
            {
                tpLabel = IntervalTpBasis == IntervalBasis.Pips
                    ? string.Format("IntervalLot ({0:F1}p / {1:F2}L)", IntervalPips, LotsPerInterval)
                    : string.Format("IntervalLot ({0:F2}xATR / {1:F2}L)", IntervalAtrMultiple, LotsPerInterval);
            }
            else
            {
                tpLabel = tpPips > 0 ? tpPips.ToString("F1") + "p" : "Runner";
            }

            double rrr         = (tpPips > 0 && slPips > 0) ? tpPips / slPips : 0;
            double entrySpreadP = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            double commPips    = EstimatedCommissionPips; // set EstimatedCommissionPips > 0 to include commission in eff. R:R
            double effRrr = (tpPips > 0 && slPips > 0)
                ? (tpPips - entrySpreadP - commPips) / (slPips + entrySpreadP + commPips)
                : 0;
            Print("TryOpenTrade: FILLED ✓ | Id={0} | Entry={1:F5} | SL={2:F1}p | TP={3} | R:R={4:F2} (eff={5:F2}) | Vol={6:F0}u ({7:F2}L)",
                _currentTrade.PositionId, _currentTrade.EntryPrice, _currentTrade.InitialSlPips,
                tpLabel, rrr, effRrr, _currentTrade.InitialVolume, _currentTrade.InitialVolume / Symbol.LotSize);

            // Modul-Score-Breakdown für bessere Backtest-Analyse
            if (EnableVerboseScoreLogging)
                LogScoreBreakdown(direction, score);
        }

        // ════════════════════════════════════════════════════════════════════
        //  TRADE MANAGEMENT
        // ════════════════════════════════════════════════════════════════════
        private void ManageOpenTrade()
        {
            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null)
            {
                Print("ManageOpenTrade: Position {0} no longer exists – clearing state.", _currentTrade.PositionId);
                _currentTrade = null;
                return;
            }

            double slPips  = _currentTrade.InitialSlPips;
            double entry   = _currentTrade.EntryPrice;
            bool   isLong  = pos.TradeType == TradeType.Buy;

            double currentPriceDiff = isLong ? Symbol.Bid - entry : entry - Symbol.Ask;
            double currentR         = slPips > 0 ? currentPriceDiff / (slPips * Symbol.PipSize) : 0;

            // ── a) Break-Even ─────────────────────────────────────────────────
            if (EnableBreakEven && !_currentTrade.BreakEvenDone && currentR >= BeRMultiple)
            {
                double newSlPrice = isLong
                    ? entry + BeOffsetPips * Symbol.PipSize
                    : entry - BeOffsetPips * Symbol.PipSize;

                bool shouldMove = isLong
                    ? (pos.StopLoss == null || newSlPrice > pos.StopLoss.Value)
                    : (pos.StopLoss == null || newSlPrice < pos.StopLoss.Value);

                if (shouldMove)
                {
                    var beResult = ModifyPosition(pos, newSlPrice, pos.TakeProfit);
                    if (beResult.IsSuccessful)
                    {
                        _currentTrade.BreakEvenDone = true;
                        Print("BreakEven triggered at {0:F1}R. SL moved to {1:F5} (+{2:F1}p offset).",
                            currentR, newSlPrice, BeOffsetPips);
                    }
                    else
                        Print("BreakEven: ModifyPosition failed. Error={0}", beResult.Error);
                }
            }

            // ── b) Partial Closes (klassisches 3-Level-System) ────────────────
            //  Nur aktiv, wenn NICHT der Interval-Lot-Modus gewählt wurde.
            if (TakeProfitMethod != TpMethod.IntervalLot
                && _currentTrade.InitialVolume >= MinVolumeForPartials * Symbol.LotSize)
            {
                _currentTrade.Partial1Done = TryPartialClose(
                    pos, 1, EnablePartial1, Partial1TriggerR, Partial1Percent, _currentTrade.Partial1Done, currentR);
                _currentTrade.Partial2Done = TryPartialClose(
                    pos, 2, EnablePartial2, Partial2TriggerR, Partial2Percent, _currentTrade.Partial2Done, currentR);
                _currentTrade.Partial3Done = TryPartialClose(
                    pos, 3, EnablePartial3, Partial3TriggerR, Partial3Percent, _currentTrade.Partial3Done, currentR);
            }

            // ── b') Interval-Lot Take Profit (v2.8.0) ─────────────────────────
            //  Systematisches Schließen fester Lot-Mengen in gleichmäßigen Intervallen.
            if (TakeProfitMethod == TpMethod.IntervalLot)
                TryIntervalLotClose(pos, isLong, entry);

            // ── c) Trailing Stop ──────────────────────────────────────────────
            if (TrailingStopType == TrailingType.Chandelier)
                ApplyChandelierTrail(pos, isLong);
            else if (TrailingStopType == TrailingType.FastEma)
                ApplyFastEmaTrail(pos, isLong);
        }

        private bool TryPartialClose(Position pos, int level, bool enabled,
            double triggerR, double percent, bool alreadyDone, double currentR)
        {
            if (!enabled || alreadyDone) return alreadyDone;
            if (currentR < triggerR)     return false;

            double closeUnits = Symbol.NormalizeVolumeInUnits(
                pos.VolumeInUnits * (percent / 100.0), RoundingMode.Down);

            // v2.12.0 – Split into two cases: full-close if volume-depleted, skip if invalid
            if (closeUnits <= 0)
            {
                Print("Partial{0}: closeUnits <= 0 at {1:F2}R – marking as done.", level, currentR);
                return true;
            }

            if (closeUnits >= pos.VolumeInUnits)
            {
                // v2.12.0 – Full-close when volume exhausted (instead of silent skip)
                var fullResult = ClosePosition(pos);
                if (fullResult.IsSuccessful)
                    Print("Partial{0}: Full-close at {1:F2}R (volume depleted). PnL={2:F2} {3}",
                        level, currentR, fullResult.Position?.NetProfit ?? 0, Account.Asset.Name);
                else
                    Print("Partial{0}: Full-close failed. Error={1}", level, fullResult.Error);
                return true;
            }

            var result = ClosePosition(pos, closeUnits);
            if (result.IsSuccessful)
            {
                Print("Partial{0} closed {1:F0} units ({2:F1}%) at {3:F1}R.", level, closeUnits, percent, currentR);
                return true;
            }
            else
            {
                Print("Partial{0}: ClosePosition failed. Error={1}", level, result.Error);
                return false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  TryIntervalLotClose (v2.8.0)
        //  Schließt feste Lot-Mengen in regelmäßigen Intervallen
        //  (entweder in Pips oder in ATR-Vielfachen).
        // ─────────────────────────────────────────────────────────────────────
        private void TryIntervalLotClose(Position pos, bool isLong, double entry)
        {
            // Intervall-Distanz in Preis-Einheiten berechnen
            double intervalPrice;
            if (IntervalTpBasis == IntervalBasis.Pips)
            {
                intervalPrice = IntervalPips * Symbol.PipSize;
            }
            else
            {
                // ATR-Multiple Modus: ATR bei Entry eingefroren (konsistente Intervalle)
                double atr = _currentTrade.IntervalAtrAtEntry;
                if (atr <= 0 || double.IsNaN(atr))
                {
                    Print("IntervalLot: Invalid ATR-at-Entry ({0:F6}) – cannot calculate interval.", atr);
                    return;
                }
                intervalPrice = atr * IntervalAtrMultiple;
            }

            if (intervalPrice <= 0) return;

            // Aktuellen Profit in Preis-Einheiten ermitteln
            double currentPriceMove = isLong ? Symbol.Bid - entry : entry - Symbol.Ask;
            if (currentPriceMove <= 0) return;   // Position nicht im Profit

            // Wie viele Intervalle wurden erreicht?
            int intervalsReached = (int)Math.Floor(currentPriceMove / intervalPrice);
            if (intervalsReached <= _currentTrade.IntervalsTriggered) return;

            // Max-Intervalle-Check
            if (MaxIntervals > 0 && _currentTrade.IntervalsTriggered >= MaxIntervals)
                return;

            // Wie viele Intervalle müssen JETZT abgearbeitet werden?
            int intervalsToProcess = intervalsReached - _currentTrade.IntervalsTriggered;
            if (MaxIntervals > 0)
                intervalsToProcess = Math.Min(intervalsToProcess, MaxIntervals - _currentTrade.IntervalsTriggered);

            double lotsPerIntervalUnits  = LotsPerInterval * Symbol.LotSize;
            double minRunnerUnits        = MinRunnerLots   * Symbol.LotSize;
            double totalUnitsToClose     = lotsPerIntervalUnits * intervalsToProcess;

            // Runner-Schutz: Nie unter MinRunnerLots schließen
            double maxCloseableUnits = pos.VolumeInUnits - minRunnerUnits;
            if (maxCloseableUnits <= 0)
            {
                Print("IntervalLot: Position bereits auf MinRunnerLots ({0:F2}) – keine weitere Schließung.",
                    MinRunnerLots);
                _currentTrade.IntervalsTriggered = intervalsReached;
                return;
            }

            if (totalUnitsToClose > maxCloseableUnits)
            {
                Print("IntervalLot: Gewünschte Schließung ({0:F0}u) > verfügbar ({1:F0}u) – clamp auf Runner-Schutz.",
                    totalUnitsToClose, maxCloseableUnits);
                totalUnitsToClose = maxCloseableUnits;
            }

            double normalizedClose = Symbol.NormalizeVolumeInUnits(totalUnitsToClose, RoundingMode.Down);
            if (normalizedClose <= 0)
            {
                Print("IntervalLot: Normalisiertes Volumen <= 0 – skip.");
                _currentTrade.IntervalsTriggered = intervalsReached;
                return;
            }

            // Falls Schließen komplette Position wäre, nutze ClosePosition ohne Volumen
            if (normalizedClose >= pos.VolumeInUnits)
            {
                var fullResult = ClosePosition(pos);
                if (fullResult.IsSuccessful)
                {
                    Print("IntervalLot FULL-CLOSE: Interval {0} erreicht, Position komplett geschlossen. PnL={1:F2} {2}",
                        intervalsReached, fullResult.Position?.NetProfit ?? 0, Account.Asset.Name);
                    _currentTrade = null;
                }
                else
                    Print("IntervalLot: Full-Close failed. Error={0}", fullResult.Error);
                return;
            }

            var result = ClosePosition(pos, normalizedClose);
            if (result.IsSuccessful)
            {
                _currentTrade.IntervalsTriggered = intervalsReached;
                double moveUnits = IntervalTpBasis == IntervalBasis.Pips
                    ? currentPriceMove / Symbol.PipSize
                    : currentPriceMove / _currentTrade.IntervalAtrAtEntry;
                string unitLabel = IntervalTpBasis == IntervalBasis.Pips ? "p" : "xATR";
                Print("IntervalLot: Interval #{0} erreicht ({1:F2}{2}). Closed {3:F0}u ({4:F2} lots). Remaining: {5:F0}u",
                    intervalsReached, moveUnits, unitLabel, normalizedClose,
                    normalizedClose / Symbol.LotSize, pos.VolumeInUnits - normalizedClose);
            }
            else
                Print("IntervalLot: ClosePosition failed. Error={0}", result.Error);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Chandelier Trail
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyChandelierTrail(Position pos, bool isLong)
        {
            if (_atrChandelier == null) return;

            double atr = _atrChandelier.Result.Last(1); // closed bar – no repaint
            if (double.IsNaN(atr) || atr <= 0) return;

            double newSl;
            if (isLong)
            {
                double chandelierLevel = Bars.HighPrices.Last(1) - ChandelierAtrMultiplier * atr;
                _currentTrade.ChandelierStopLong = Math.Max(_currentTrade.ChandelierStopLong, chandelierLevel);
                newSl = _currentTrade.ChandelierStopLong;

                if (pos.StopLoss.HasValue && (newSl - pos.StopLoss.Value) / Symbol.PipSize < MinPipsToModifySl) return;
                if (pos.StopLoss.HasValue && newSl <= pos.StopLoss.Value) return;
            }
            else
            {
                double chandelierLevel = Bars.LowPrices.Last(1) + ChandelierAtrMultiplier * atr;
                _currentTrade.ChandelierStopShort = Math.Min(_currentTrade.ChandelierStopShort, chandelierLevel);
                newSl = _currentTrade.ChandelierStopShort;

                if (pos.StopLoss.HasValue && (pos.StopLoss.Value - newSl) / Symbol.PipSize < MinPipsToModifySl) return;
                if (pos.StopLoss.HasValue && newSl >= pos.StopLoss.Value) return;
            }

            var result = ModifyPosition(pos, newSl, pos.TakeProfit);
            if (!result.IsSuccessful)
                Print("ChandelierTrail: ModifyPosition failed. Error={0}", result.Error);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Fast EMA Trail
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyFastEmaTrail(Position pos, bool isLong)
        {
            if (_trailingEma == null) return;

            double emaValue = _trailingEma.Result.Last(1); // closed bar – no repaint
            if (double.IsNaN(emaValue)) return;

            double lastClose    = Bars.ClosePrices.Last(1);
            bool   closedBeyond = isLong ? lastClose < emaValue : lastClose > emaValue;

            if (closedBeyond)
                _currentTrade.ConsecutiveEmaCloses++;
            else
            {
                _currentTrade.ConsecutiveEmaCloses = 0;
                return;
            }

            int requiredCloses = EmaTrailingFilter == EmaTrailFilter.DoubleClose ? 2 : 1;
            if (_currentTrade.ConsecutiveEmaCloses < requiredCloses) return;

            Print("FastEMA trail exit: {0} consecutive close(s) beyond EMA ({1:F5}). Closing.",
                _currentTrade.ConsecutiveEmaCloses, emaValue);

            var result = ClosePosition(pos);
            if (result.IsSuccessful)
                _currentTrade = null;
            else
                Print("FastEMA trail: ClosePosition failed. Error={0}", result.Error);
        }

        // ════════════════════════════════════════════════════════════════════
        //  EXIT CONDITIONS
        // ════════════════════════════════════════════════════════════════════
        private void CheckExitConditions()
        {
            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null) { _currentTrade = null; return; }

            bool isLong = pos.TradeType == TradeType.Buy;

            // ── a) Weekend Protection ─────────────────────────────────────────
            if (EnableWeekendClose && !_weekendCloseFired)
            {
                bool isFriday  = Server.Time.DayOfWeek == DayOfWeek.Friday;
                bool pastClose = Server.Time.TimeOfDay >= new TimeSpan(WeekendCloseHour, WeekendCloseMinute, 0);

                if (isFriday && pastClose)
                {
                    Print("WeekendClose: Friday {0:HH:mm} >= {1:D2}:{2:D2}. Closing all positions.",
                        Server.Time, WeekendCloseHour, WeekendCloseMinute);
                    ForceCloseCurrentTrade("WeekendClose");
                    _weekendCloseFired = true;
                    return;
                }
            }

            // ── b) HTF Trend Break Exit ───────────────────────────────────────
            if (EnableHtfBreakExit && _htfBars != null && _htfEma != null)
            {
                double htfClose  = _htfBars.ClosePrices.Last(1);
                double htfEma    = _htfEma.Result.Last(1); // closed bar – no repaint
                bool   htfBroken = isLong ? htfClose < htfEma : htfClose > htfEma;

                if (htfBroken)
                {
                    Print("HTF Trend Break exit [{0}]: HTF close {1:F5} vs EMA {2:F5}.",
                        isLong ? "Long" : "Short", htfClose, htfEma);
                    ForceCloseCurrentTrade("HTFBreak");
                    return;
                }
            }

            // ── c) RSI Panic Exit ─────────────────────────────────────────────
            if (EnableRsiPanicExit && _rsi != null)
            {
                double rsiValue   = _rsi.Result.Last(1); // closed bar – no repaint
                bool   panicLong  = isLong  && rsiValue > RsiPanicLong;
                bool   panicShort = !isLong && rsiValue < RsiPanicShort;

                if (panicLong || panicShort)
                {
                    Print("RSI Panic exit [{0}]: RSI={1:F1} breached panic level {2:F1}.",
                        isLong ? "Long" : "Short", rsiValue, isLong ? RsiPanicLong : RsiPanicShort);
                    ForceCloseCurrentTrade("RSIPanic");
                    return;
                }
            }

            // ── d) Max Hold Time Exit (v2.12.0) ───────────────────────────────
            if (EnableMaxHoldTime && _currentTrade.EntryTime != DateTime.MinValue)
            {
                double elapsed = (Server.Time - _currentTrade.EntryTime).TotalHours;
                if (elapsed >= MaxHoldTimeHours)
                {
                    Print("MaxHoldTime exit: {0:F1}h elapsed >= limit {1}h. Closing.", elapsed, MaxHoldTimeHours);
                    ForceCloseCurrentTrade("MaxHoldTime");
                    return;
                }
            }

            // ── d2) R-Progress Time Stop ──────────────────────────────────────
            if (EnableRProgressTimeStop && _currentTrade != null && _currentTrade.EntryTime != DateTime.MinValue)
            {
                double barSeconds = Bars.Count >= 2
                    ? (Bars.OpenTimes.Last(0) - Bars.OpenTimes.Last(1)).TotalSeconds
                    : 60;
                double elapsedBars = barSeconds > 0
                    ? (Server.Time - _currentTrade.EntryTime).TotalSeconds / barSeconds
                    : 0;
                if (elapsedBars >= RProgressWindowBars)
                {
                    double slPipsNow  = _currentTrade.InitialSlPips;
                    double currentR   = slPipsNow > 0
                        ? (isLong
                            ? (pos.CurrentPrice - _currentTrade.EntryPrice) / Symbol.PipSize / slPipsNow
                            : (_currentTrade.EntryPrice - pos.CurrentPrice) / Symbol.PipSize / slPipsNow)
                        : 0;
                    if (currentR < MinRProgress)
                    {
                        Print("RProgressStall: {0:F1} bars elapsed, currentR={1:F2} < minR={2:F2}. Closing.",
                            elapsedBars, currentR, MinRProgress);
                        ForceCloseCurrentTrade("RProgressStall");
                        return;
                    }
                }
            }

            // ── e) Swap / Rollover Evasion ────────────────────────────────────
            if (EnableSwapEvasion && !_rolloverCheckDoneToday)
            {
                TimeSpan rolloverTime = new TimeSpan(RolloverHour, RolloverMinute, 0);
                if (Server.Time.TimeOfDay >= rolloverTime)
                {
                    _rolloverCheckDoneToday = true;
                    bool swapIsNegative   = pos.Swap < 0;
                    bool htfBrokenForSwap = false;

                    if (_htfBars != null && _htfEma != null)
                    {
                        double htfClose = _htfBars.ClosePrices.Last(1);
                        double htfEma   = _htfEma.Result.Last(1); // closed bar – no repaint
                        htfBrokenForSwap = isLong ? htfClose < htfEma : htfClose > htfEma;
                    }

                    if (swapIsNegative && htfBrokenForSwap)
                    {
                        Print("SwapEvasion: Negative swap ({0:F2}) + HTF trend broken. Closing.", pos.Swap);
                        ForceCloseCurrentTrade("SwapEvasion");
                        return;
                    }
                    else
                        Print("SwapEvasion: Rollover window. Swap={0:F2} HTFBreak={1} – keeping open.",
                            pos.Swap, htfBrokenForSwap);
                }
            }

            // ── e) Reversal Exit ──────────────────────────────────────────────
            // v2.9.0 – Counter-Score wird pro Bar gecacht, nicht pro Tick neu berechnet.
            // Scores ändern sich erst mit geschlossenem Bar; Tick-Polling wäre reine CPU-Verschwendung.
            if (EnableReversalExit)
            {
                DateTime curBarTime = Bars.OpenTimes.LastValue;
                if (curBarTime != _lastCounterScoreBarTime)
                {
                    TradeType counter = isLong ? TradeType.Sell : TradeType.Buy;
                    _cachedCounterTradable   = IsMarketTradable(counter, logRejections: false);
                    _cachedCounterScore      = _cachedCounterTradable
                        ? CalculateEntryScore(counter, logVerbose: false) : 0;
                    _lastCounterScoreBarTime = curBarTime;
                }

                int reversalThreshold = (int)Math.Ceiling(_minRequiredScore * ReversalExitScoreMultiplier);
                if (ReversalExitRequireHigherThanEntry && _currentTrade != null)
                    reversalThreshold = Math.Max(reversalThreshold, _currentTrade.EntryTotalScore);
                if (_cachedCounterScore >= reversalThreshold)
                {
                    Print("Reversal exit [{0}]: Counter-direction score {1}/{2} >= {3} (threshold). Closing.",
                        isLong ? "Long" : "Short", _cachedCounterScore, _maxPossibleScore, reversalThreshold);
                    ForceCloseCurrentTrade("Reversal");
                    return;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ForceCloseCurrentTrade
        // ─────────────────────────────────────────────────────────────────────
        private void ForceCloseCurrentTrade(string reason)
        {
            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null)
            {
                Print("ForceClose ({0}): Position already gone.", reason);
                _currentTrade = null;
                return;
            }

            var result = ClosePosition(pos);
            if (result.IsSuccessful)
            {
                Print("ForceClose ({0}): Position {1} closed. PnL={2:F2} {3}",
                    reason, pos.Id, result.Position?.NetProfit ?? 0, Account.Asset.Name);
                _currentTrade = null;
            }
            else
                Print("ForceClose ({0}): ClosePosition failed! Error={1}", reason, result.Error);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Session / DoW helpers (v3.1.7)
        // ─────────────────────────────────────────────────────────────────────
        private int GetSessionBucket(DateTime t)
        {
            int h = t.Hour;
            if (h < 8)  return 0;
            if (h < 13) return 1;
            if (h < 17) return 2;
            return 3;
        }

        private int GetDowBucket(DateTime t)
        {
            int dow = (int)t.DayOfWeek; // Sun=0, Mon=1, … Sat=6
            if (dow == 0 || dow == 6) return -1;
            return dow - 1; // Mon=0 … Fri=4
        }

        #endregion // Trade Management

        // ════════════════════════════════════════════════════════════════════
        //  VERBOSE LOGGING HELPERS
        // ════════════════════════════════════════════════════════════════════

        // Returns true and records bar-time only on first call per reason per bar.
        private bool ShouldLogRejection(string reason)
        {
            DateTime barTime = Bars.OpenTimes.Last(1);
            DateTime last;
            if (_lastRejectionPrint.TryGetValue(reason, out last) && last >= barTime)
                return false;
            _lastRejectionPrint[reason] = barTime;
            return true;
        }

        private void LogMarketFilterSummary(TradeType direction, bool tradable)
        {
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            string htfStr = "N/A";
            if (_htfBars != null && _htfEma != null)
            {
                double htfClose = _htfBars.ClosePrices.Last(1);
                double htfEma   = _htfEma.Result.Last(1); // closed bar – no repaint
                htfStr = string.Format("{0:F5} vs EMA {1:F5} → {2}",
                    htfClose, htfEma, htfClose > htfEma ? "BULL" : "BEAR");
            }
            Print("[FilterSummary {0}] Tradable={1} | Time={2:HH:mm} | Spread={3:F2}p | HTF={4} | DDBreached={5}",
                direction, tradable, Server.Time, spreadPips, htfStr, _dailyDrawdownBreached);
        }


        private void LogTradeState()
        {
            if (_currentTrade == null) { Print("[TradeState] No open trade."); return; }

            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null) { Print("[TradeState] Position {0} not found.", _currentTrade.PositionId); return; }

            bool   isLong  = pos.TradeType == TradeType.Buy;
            double slPips  = _currentTrade.InitialSlPips;
            double diff    = isLong ? Symbol.Bid - _currentTrade.EntryPrice
                                    : _currentTrade.EntryPrice - Symbol.Ask;
            double currentR = slPips > 0 ? diff / (slPips * Symbol.PipSize) : 0;

            Print("[TradeState] Id={0} | Dir={1} | Entry={2:F5} | SL={3:F1}p | Vol={4:F0}u " +
                  "| CurrentR={5:F2} | BE={6} | P1={7} P2={8} P3={9} | EmaCloses={10}",
                _currentTrade.PositionId,
                pos.TradeType,
                _currentTrade.EntryPrice,
                slPips,
                pos.VolumeInUnits,
                currentR,
                _currentTrade.BreakEvenDone,
                _currentTrade.Partial1Done,
                _currentTrade.Partial2Done,
                _currentTrade.Partial3Done,
                _currentTrade.ConsecutiveEmaCloses);
        }


    } // end class TenFoldBot
} // end namespace
