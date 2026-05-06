// ═══════════════════════════════════════════════════════════════════════════════
//  Gap-Star  │  RSI + Fair-Value-Gap Strategy
//  Platform  │  cTrader / cAlgo
//  Edge      │  Mean-reversion entries from RSI extremes that target the
//              nearest unfilled Fair-Value Gap as profit objective.
//
//  Long  : RSI < OversoldLevel  → enter Buy, TP = nearest unfilled bullish FVG above
//  Short : RSI > OverboughtLevel → enter Sell, TP = nearest unfilled bearish FVG below
//
//  Built on Bot Template v1.2.0 infrastructure (risk gates, sizing,
//  trade management, attribution, recovery). Strategy-specific TP overrides
//  the RRR-based target — RRR is computed implicitly from the FVG distance
//  and gated by MinRrrFvg.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TradeRegime { LowVol, Normal, HighVol }
    public enum TradeSetupKind { Trend, MeanReversion, Breakout, Other }
    public enum RiskBaseKind { Balance, Equity }

    internal struct SessionKey : IEquatable<SessionKey>
    {
        public DayOfWeek DoW;
        public int SessionBucket;

        public override bool Equals(object obj) => obj is SessionKey other && Equals(other);
        public bool Equals(SessionKey other) => DoW == other.DoW && SessionBucket == other.SessionBucket;
        public override int GetHashCode() => (int)DoW * 10 + SessionBucket;
    }

    internal struct SessionStats
    {
        public int    TradeCount;
        public int    Wins;
        public double PnLSum;
    }

    internal sealed class FvgZone
    {
        public bool     IsBullish;
        public double   Top;
        public double   Bottom;
        public DateTime FormedAt;
        public int      FormedAtBarIndex;
        public bool     Filled;
        public bool     PartiallyTouched;
    }

    internal sealed class BotTradeState
    {
        public int             PositionId;
        public double          EntryPrice;
        public double          InitialSlPips;
        public double          InitialVolumeUnits;
        public double          RRRTarget;
        public bool            BreakEvenDone;
        public bool            Partial1Done;
        public bool            Partial2Done;
        public double          ChandelierStop;
        public DateTime        EntryTime;
        public string          EdgeLabel;
        public TradeSetupKind  Setup;
        public double          EntrySlippage;
        public double          ExitSlippagePips;
        public double          ChandelierPeakHigh;
        public double          ChandelierPeakLow;
        public DateTime        LastChandelierUpdateTime;
        public int             EntryBarIndex;
        public double          MaeRMultiple;
        public double          MfeRMultiple;
        public SessionKey      EntrySessionKey;
        public bool            RecoveredWithoutMetadata;
        public double          FvgTargetPrice;
    }

    [Robot("Gap-Star", AccessRights = AccessRights.None)]
    public class GapStar : Robot
    {
        // ════════════════════════════════════════════════════════════════════
        //  CONSTANTS
        // ════════════════════════════════════════════════════════════════════
        private const double SPREAD_FLOOR_PIPS      = 0.3;
        private const double CHANDELIER_SL_CAP_PIPS = 2.0;
        private const int    TRADING_DAYS_PER_YEAR  = 252;

        // ════════════════════════════════════════════════════════════════════
        //  PARAMETERS
        // ════════════════════════════════════════════════════════════════════

        // ── 00 · Core ────────────────────────────────────────────────────────
        [Parameter("Bot Label", Group = "00 · Core", DefaultValue = "GapStar")]
        public string BotLabel { get; set; }

        [Parameter("Risk Base", Group = "00 · Core", DefaultValue = RiskBaseKind.Balance)]
        public RiskBaseKind RiskBase { get; set; }

        [Parameter("Risk per Trade (%)", Group = "00 · Core",
            DefaultValue = 1.0, MinValue = 0.05, MaxValue = 3.0, Step = 0.05)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Commission Buffer (Pips, added to SL)", Group = "00 · Core",
            DefaultValue = 0.6, MinValue = 0.0, Step = 0.1)]
        public double CommissionBufferPips { get; set; }

        [Parameter("Entry Market Range Pips (0 = broker default)", Group = "00 · Core",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 20.0, Step = 0.1)]
        public double EntryMarketRangePips { get; set; }

        [Parameter("Regime Risk Mult – LowVol", Group = "00 · Core",
            DefaultValue = 0.7, MinValue = 0.25, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultLowVol { get; set; }

        [Parameter("Regime Risk Mult – Normal", Group = "00 · Core",
            DefaultValue = 1.0, MinValue = 0.5, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultNormal { get; set; }

        [Parameter("Regime Risk Mult – HighVol", Group = "00 · Core",
            DefaultValue = 0.5, MinValue = 0.25, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultHighVol { get; set; }

        // ── 01 · RSI Signal ──────────────────────────────────────────────────
        [Parameter("RSI Period", Group = "01 · RSI Signal",
            DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Oversold Level (Long trigger)", Group = "01 · RSI Signal",
            DefaultValue = 30.0, MinValue = 5.0, MaxValue = 49.0, Step = 1.0)]
        public double RsiOversoldLevel { get; set; }

        [Parameter("RSI Overbought Level (Short trigger)", Group = "01 · RSI Signal",
            DefaultValue = 70.0, MinValue = 51.0, MaxValue = 95.0, Step = 1.0)]
        public double RsiOverboughtLevel { get; set; }

        [Parameter("RSI Cross Confirmation (cross back from extreme)", Group = "01 · RSI Signal",
            DefaultValue = true)]
        public bool RsiRequireCrossBack { get; set; }

        [Parameter("Allow Long Entries", Group = "01 · RSI Signal", DefaultValue = true)]
        public bool AllowLongs { get; set; }

        [Parameter("Allow Short Entries", Group = "01 · RSI Signal", DefaultValue = true)]
        public bool AllowShorts { get; set; }

        // ── 02 · Time & Session ──────────────────────────────────────────────
        [Parameter("Session Start Hour (server UTC)", Group = "02 · Time & Session",
            DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session End Hour (server UTC)", Group = "02 · Time & Session",
            DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        [Parameter("Block New Trades on Fridays", Group = "02 · Time & Session", DefaultValue = true)]
        public bool BlockFriday { get; set; }

        [Parameter("Force Close on Weekend (Fri server hour)", Group = "02 · Time & Session",
            DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int WeekendCloseHour { get; set; }

        [Parameter("Treat Server.Time as UTC", Group = "02 · Time & Session", DefaultValue = true)]
        public bool ServerTimeIsUtc { get; set; }

        // ── 03 · HTF Bias Filter ─────────────────────────────────────────────
        [Parameter("Enable HTF Bias Filter (long only in uptrend / short in downtrend)",
            Group = "03 · HTF Bias", DefaultValue = false)]
        public bool EnableHtfBiasFilter { get; set; }

        [Parameter("HTF TimeFrame", Group = "03 · HTF Bias", DefaultValue = "Hour1")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("HTF EMA Period", Group = "03 · HTF Bias",
            DefaultValue = 50, MinValue = 5, MaxValue = 500)]
        public int HtfEmaPeriod { get; set; }

        [Parameter("HTF Buffer (Pips, neutral zone)", Group = "03 · HTF Bias",
            DefaultValue = 3.0, MinValue = 0.0, Step = 0.1)]
        public double HtfBufferPips { get; set; }

        // ── 04 · Volatility / Regime ─────────────────────────────────────────
        [Parameter("ATR Period", Group = "04 · Volatility",
            DefaultValue = 14, MinValue = 3, MaxValue = 100)]
        public int AtrPeriod { get; set; }

        [Parameter("Block Entries in HighVol Regime (default OFF — mean-reversion typically benefits from HighVol)",
            Group = "04 · Volatility", DefaultValue = false)]
        public bool BlockHighVol { get; set; }

        [Parameter("Daily ATR Ratio LOW", Group = "04 · Volatility",
            DefaultValue = 0.80, MinValue = 0.1, MaxValue = 2.0, Step = 0.05)]
        public double AtrRatioLow { get; set; }

        [Parameter("Daily ATR Ratio HIGH", Group = "04 · Volatility",
            DefaultValue = 1.30, MinValue = 0.5, MaxValue = 3.0, Step = 0.05)]
        public double AtrRatioHigh { get; set; }

        [Parameter("Regime Hysteresis Band", Group = "04 · Volatility",
            DefaultValue = 0.05, MinValue = 0.0, MaxValue = 0.5, Step = 0.01)]
        public double RegimeHysteresisBand { get; set; }

        [Parameter("Median Lookback (days)", Group = "04 · Volatility",
            DefaultValue = 20, MinValue = 5, MaxValue = 60)]
        public int MedianLookbackDays { get; set; }

        // ── 05 · Spread Protection ───────────────────────────────────────────
        [Parameter("Max Spread (Pips, hard cap)", Group = "05 · Spread",
            DefaultValue = 2.5, MinValue = 0.1, Step = 0.1)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Spread / ATR max ratio", Group = "05 · Spread",
            DefaultValue = 0.15, MinValue = 0.01, MaxValue = 1.0, Step = 0.01)]
        public double SpreadAtrRatio { get; set; }

        // ── 06 · FVG Detection ───────────────────────────────────────────────
        [Parameter("FVG Min Size (Pips)", Group = "06 · FVG",
            DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double FvgMinSizePips { get; set; }

        [Parameter("FVG Max Age (bars, 0 = no cap)", Group = "06 · FVG",
            DefaultValue = 200, MinValue = 0, MaxValue = 5000)]
        public int FvgMaxAgeBars { get; set; }

        [Parameter("FVG Max Distance (Pips, 0 = no cap)", Group = "06 · FVG",
            DefaultValue = 80.0, MinValue = 0.0, Step = 1.0)]
        public double FvgMaxDistancePips { get; set; }

        [Parameter("FVG Lookback Scan (bars on start, history seed)", Group = "06 · FVG",
            DefaultValue = 500, MinValue = 50, MaxValue = 5000)]
        public int FvgHistorySeedBars { get; set; }

        [Parameter("FVG Target Mode", Group = "06 · FVG", DefaultValue = FvgTargetMode.NearEdge)]
        public FvgTargetMode FvgTpMode { get; set; }

        // ── 07 · Stops & Min RRR ─────────────────────────────────────────────
        [Parameter("ATR SL Multiplier", Group = "07 · Stops",
            DefaultValue = 1.5, MinValue = 0.3, MaxValue = 6.0, Step = 0.1)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("Min SL Pips", Group = "07 · Stops",
            DefaultValue = 6.0, MinValue = 1.0, Step = 0.5)]
        public double MinSlPips { get; set; }

        [Parameter("Max SL Pips", Group = "07 · Stops",
            DefaultValue = 60.0, MinValue = 5.0, Step = 1.0)]
        public double MaxSlPips { get; set; }

        [Parameter("Min RRR (FVG TP / SL must be ≥ this)", Group = "07 · Stops",
            DefaultValue = 1.2, MinValue = 0.3, MaxValue = 10.0, Step = 0.1)]
        public double MinRrrFvg { get; set; }

        [Parameter("Fallback RRR (if no FVG found, 0 = no fallback / skip)", Group = "07 · Stops",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 10.0, Step = 0.1)]
        public double FallbackRrr { get; set; }

        // ── 10 · Trade Management ────────────────────────────────────────────
        [Parameter("Enable BreakEven", Group = "10 · Trade Mgmt", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("BreakEven Trigger (R)", Group = "10 · Trade Mgmt",
            DefaultValue = 1.0, MinValue = 0.1, Step = 0.1)]
        public double BeTriggerR { get; set; }

        [Parameter("BreakEven Offset (Pips)", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.0, Step = 0.1)]
        public double BeOffsetPips { get; set; }

        [Parameter("Enable Partial 1", Group = "10 · Trade Mgmt", DefaultValue = false)]
        public bool EnablePartial1 { get; set; }

        [Parameter("Partial 1 – Trigger (R)", Group = "10 · Trade Mgmt",
            DefaultValue = 1.0, MinValue = 0.2, Step = 0.1)]
        public double Partial1TriggerR { get; set; }

        [Parameter("Partial 1 – Close Fraction", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.05, MaxValue = 0.9, Step = 0.05)]
        public double Partial1Fraction { get; set; }

        [Parameter("Enable Chandelier Trail (after Partial1 / BE)", Group = "10 · Trade Mgmt",
            DefaultValue = false)]
        public bool EnableChandelier { get; set; }

        [Parameter("Chandelier ATR Multiplier", Group = "10 · Trade Mgmt",
            DefaultValue = 2.5, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double ChandelierAtrMult { get; set; }

        [Parameter("Max Hold Bars (0 = off)", Group = "10 · Trade Mgmt",
            DefaultValue = 0, MinValue = 0, MaxValue = 500)]
        public int MaxHoldBars { get; set; }

        // ── 11 · Risk Gates ──────────────────────────────────────────────────
        [Parameter("Max Daily Drawdown (%, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 5.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1)]
        public double MaxDailyDdPct { get; set; }

        [Parameter("Max Weekly Drawdown (%, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 10.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1)]
        public double MaxWeeklyDdPct { get; set; }

        [Parameter("Max Floating Daily DD (%, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 6.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1)]
        public double MaxFloatingDailyDdPct { get; set; }

        [Parameter("Max Trades per Day", Group = "11 · Risk Gates",
            DefaultValue = 6, MinValue = 1, MaxValue = 50)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Consec Losses – Cool-Down Hours (0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 4.0, MinValue = 0.0, MaxValue = 48.0, Step = 0.5)]
        public double ConsecLossCoolDownHours { get; set; }

        [Parameter("Consec Losses – Trigger", Group = "11 · Risk Gates",
            DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int ConsecLossTrigger { get; set; }

        [Parameter("News Blackout UTC (CSV: yyyy-MM-dd HH:mm±min;...)", Group = "11 · Risk Gates",
            DefaultValue = "")]
        public string NewsBlackoutCsv { get; set; }

        [Parameter("Force Flat During News Blackout", Group = "11 · Risk Gates", DefaultValue = false)]
        public bool ForceFlatInBlackout { get; set; }

        [Parameter("Equity Curve Trail Stop (% from HWM, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 15.0, MinValue = 0.0, MaxValue = 30.0, Step = 0.5)]
        public double EquityCurveTrailPct { get; set; }

        [Parameter("Per-Trade Hard Loss Cap ($, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 4.0, MinValue = 0.0, Step = 1.0)]
        public double MaxLossPerTradeUsd { get; set; }

        [Parameter("Re-Entry Cooldown Bars (after any close)", Group = "11 · Risk Gates",
            DefaultValue = 3, MinValue = 0, MaxValue = 100)]
        public int ReEntryCooldownBars { get; set; }

        // ── 12 · Sizing ──────────────────────────────────────────────────────
        [Parameter("Max Margin Utilization (%)", Group = "12 · Sizing",
            DefaultValue = 30.0, MinValue = 5.0, MaxValue = 95.0, Step = 5.0)]
        public double MaxMarginUtilizationPct { get; set; }

        // ── 13 · Logging ─────────────────────────────────────────────────────
        [Parameter("Verbose Logs", Group = "13 · Logging", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Persist Attribution JSON (Live only)", Group = "13 · Logging", DefaultValue = false)]
        public bool EnableAttributionPersistence { get; set; }

        [Parameter("Export trade log CSV on Stop", Group = "13 · Logging", DefaultValue = false)]
        public bool ExportTradeLogCsv { get; set; }

        [Parameter("Parameter Set ID", Group = "13 · Logging", DefaultValue = "v0.1.0")]
        public string ParameterSetId { get; set; }

        public enum FvgTargetMode { NearEdge, Midpoint, FarEdge }

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE STATE
        // ════════════════════════════════════════════════════════════════════
        private Bars             _htfBars;
        private Bars             _dailyBars;
        private MovingAverage    _htfEma;
        private RelativeStrengthIndex _rsi;
        private AverageTrueRange _atr;
        private AverageTrueRange _atrChandelier;
        private AverageTrueRange _dailyAtr;

        private BotTradeState _currentTrade;
        private DateTime _startTime;
        private DateTime _botDay      = DateTime.MinValue;
        private DateTime _botWeek     = DateTime.MinValue;
        private double   _dayStartBalance;
        private double   _weekStartBalance;
        private double   _dayRealizedPnl;
        private double   _weekRealizedPnl;
        private bool     _dailyDdBreached;
        private bool     _weeklyDdBreached;
        private int      _tradesToday;
        private int      _consecutiveLosses;
        private DateTime _cooldownEndTime    = DateTime.MinValue;
        private TradeRegime _lastRegime      = TradeRegime.Normal;
        private double   _runHighWaterMarkBalance;

        private List<(DateTime center, int halfMinutes)> _newsBlackouts = new List<(DateTime, int)>();
        private List<string> _tradeLogRows                              = new List<string>();
        private TradeRegime  _tradeOpenRegime                           = TradeRegime.Normal;
        private int          _lastTradeCloseBarIndex                    = -1;

        private Dictionary<DateTime, double> _dailyRSum        = new Dictionary<DateTime, double>();
        private Dictionary<DateTime, double> _dailyEquityClose = new Dictionary<DateTime, double>();

        // FVG state
        private List<FvgZone> _fvgs = new List<FvgZone>();
        private int _lastDetectedBarCount = -1;

        // Attribution
        private int _totalTradesOpened;
        private Dictionary<string, int>    _edgeWinCount            = new Dictionary<string, int>();
        private Dictionary<string, int>    _edgeLossCount           = new Dictionary<string, int>();
        private Dictionary<string, double> _edgePnlSum              = new Dictionary<string, double>();
        private Dictionary<string, double> _edgeEntrySlippageSum    = new Dictionary<string, double>();
        private Dictionary<string, int>    _edgeSlippageSampleCount = new Dictionary<string, int>();
        private Dictionary<string, double> _edgeExitSlippageSum     = new Dictionary<string, double>();
        private Dictionary<string, int>    _edgeExitSlippageCount   = new Dictionary<string, int>();
        private Dictionary<string, double> _edgeMaeSum              = new Dictionary<string, double>();
        private Dictionary<string, double> _edgeMfeSum              = new Dictionary<string, double>();
        private Dictionary<SessionKey, SessionStats> _sessionStats  = new Dictionary<SessionKey, SessionStats>();
        private List<double> _rMultiples = new List<double>();

        // ════════════════════════════════════════════════════════════════════
        //  ON START
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            Print("╔══════════════════════════════════════════════╗");
            Print("║   Gap-Star ({0,-28}) ║", ParameterSetId);
            Print("║   RSI + FVG Strategy                          ║");
            Print("╚══════════════════════════════════════════════╝");
            _startTime = Server.Time;

            double serverUtcDeltaMin = Math.Abs((Server.Time - DateTime.UtcNow).TotalMinutes);
            if (serverUtcDeltaMin > 30)
                Print("WARNING: Server.Time deviates from system UTC by {0:F1}min", serverUtcDeltaMin);
            if (!ServerTimeIsUtc)
                Print("CRITICAL: ServerTimeIsUtc=false — hour gates use server local time, NOT UTC.");

            if (!ValidateParameters())
            {
                Print("CRITICAL: parameter validation failed — bot will idle.");
                return;
            }

            _atr           = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _atrChandelier = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _rsi           = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);

            if (EnableHtfBiasFilter)
            {
                _htfBars = MarketData.GetBars(HtfTimeFrame, SymbolName);
                WarmupBars(_htfBars, HtfEmaPeriod + 50, "HTF");
                _htfEma = Indicators.MovingAverage(_htfBars.ClosePrices, HtfEmaPeriod, MovingAverageType.Exponential);
            }

            _dailyBars = MarketData.GetBars(TimeFrame.Daily, SymbolName);
            WarmupBars(_dailyBars, MedianLookbackDays + 10, "Daily");
            _dailyAtr = Indicators.AverageTrueRange(_dailyBars, AtrPeriod, MovingAverageType.WilderSmoothing);

            SeedFvgHistory();

            ParseNewsBlackout();

            _dayStartBalance         = Account.Balance;
            _weekStartBalance        = Account.Balance;
            _botDay                  = Server.Time.Date;
            _botWeek                 = GetWeekMonday(Server.Time);
            _runHighWaterMarkBalance = Account.Balance;

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            RecoverExistingPosition();

            Print("Symbol={0} TF={1} | RSI({2}) OS={3:F0}/OB={4:F0} | Risk={5:F2}% base={6}",
                SymbolName, TimeFrame, RsiPeriod, RsiOversoldLevel, RsiOverboughtLevel, RiskPerTradePct, RiskBase);
            Print("FVG: minSize={0:F1}p maxAge={1}b maxDist={2:F0}p mode={3} | seeded {4} zones",
                FvgMinSizePips, FvgMaxAgeBars, FvgMaxDistancePips, FvgTpMode, _fvgs.Count);
            Print("Gates: DailyDD={0:F1}% WeeklyDD={1:F1}% MaxTrades={2} HardLoss=${3:F0} | MinRRR={4:F2}",
                MaxDailyDdPct, MaxWeeklyDdPct, MaxTradesPerDay, MaxLossPerTradeUsd, MinRrrFvg);
        }

        protected override void OnStop()
        {
            Positions.Opened -= OnPositionOpened;
            Positions.Closed -= OnPositionClosed;

            if (_botDay != DateTime.MinValue)
                _dailyEquityClose[_botDay] = Account.Balance;

            TimeSpan runtime = Server.Time - _startTime;
            Print("╔══════════════════════════════════════════════╗");
            Print("║   Gap-Star Stopped                            ║");
            Print("╚══════════════════════════════════════════════╝");
            Print("  Runtime    : {0:dd\\d\\ hh\\h\\ mm\\m}", runtime);
            Print("  Balance    : {0:F2} {1}", Account.Balance, Account.Asset.Name);
            Print("  Trades     : {0}", _totalTradesOpened);
            Print("  FVGs total : {0} (filled: {1})", _fvgs.Count, _fvgs.Count(f => f.Filled));
            Print("  ParamSetId : {0}", ParameterSetId);

            EmitAttribution();
            PersistAttributionJson();
            ExportTradeLogCsvFile();
        }

        // ════════════════════════════════════════════════════════════════════
        //  ON BAR
        // ════════════════════════════════════════════════════════════════════
        protected override void OnBar()
        {
            try
            {
                RolloverDailyWeekly();

                if (WeekendClosePending())
                {
                    ClosePositionIfOpen("WeekendClose");
                    return;
                }

                if (ForceFlatInBlackout && _currentTrade != null)
                {
                    DateTime now = Server.Time;
                    foreach (var (center, halfMin) in _newsBlackouts)
                    {
                        if (Math.Abs((now - center).TotalMinutes) <= halfMin)
                        {
                            ClosePositionIfOpen($"NewsBlackout({center:HH:mm})");
                            return;
                        }
                    }
                }

                if (Bars.Count < Math.Max(AtrPeriod, RsiPeriod) + 5) return;

                // Update FVG state on every closed bar
                DetectNewFvgOnLastClosedBar();
                ScanFvgFills();
                PruneFvgs();

                // MAE/MFE update from bar high/low
                if (_currentTrade != null && _currentTrade.InitialSlPips > 0)
                {
                    var pos = Positions.FindById(_currentTrade.PositionId);
                    if (pos != null)
                    {
                        double favR = ComputeBarMaxR(pos, _currentTrade.InitialSlPips);
                        double advR = ComputeBarMinR(pos, _currentTrade.InitialSlPips);
                        if (favR > _currentTrade.MfeRMultiple) _currentTrade.MfeRMultiple = favR;
                        if (advR < _currentTrade.MaeRMultiple) _currentTrade.MaeRMultiple = advR;
                    }
                }

                if (_currentTrade != null)
                {
                    var pos = Positions.FindById(_currentTrade.PositionId);
                    if (pos != null) ManagePartialsAtBarClose(pos);
                    return;
                }

                if (!IsMarketTradable()) return;

                TradeRegime regime = ClassifyRegime();
                _tradeOpenRegime   = regime;

                if (BlockHighVol && regime == TradeRegime.HighVol)
                {
                    if (Verbose) Print("REJECT: HighVol regime block");
                    return;
                }

                int htfBias = EnableHtfBiasFilter ? GetHtfBias() : 0;

                TryEnterRsiFvg(regime, htfBias);
            }
            catch (Exception ex)
            {
                Print("ERROR OnBar: {0} | {1}", ex.Message, ex.StackTrace);
            }
        }

        protected override void OnTick()
        {
            if (_currentTrade == null) return;

            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null) { _currentTrade = null; return; }

            try
            {
                if (_currentTrade.InitialSlPips > 0)
                {
                    double curR = ComputeCurrentR(pos, _currentTrade.InitialSlPips);
                    if (curR > _currentTrade.MfeRMultiple) _currentTrade.MfeRMultiple = curR;
                    if (curR < _currentTrade.MaeRMultiple) _currentTrade.MaeRMultiple = curR;
                }

                ManageBreakEven(pos);
                ManagePartials(pos);
                ManageChandelier(pos);
                ManageMaxHold(pos);

                if (MaxLossPerTradeUsd > 0 && pos.NetProfit <= -MaxLossPerTradeUsd)
                    ClosePositionIfOpen("HardLossCap");
            }
            catch (Exception ex)
            {
                Print("ERROR OnTick: {0}", ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  STRATEGY  —  RSI + FVG
        // ════════════════════════════════════════════════════════════════════
        private bool TryEnterRsiFvg(TradeRegime regime, int htfBias)
        {
            double rsiNow  = _rsi.Result.Last(1);
            double rsiPrev = _rsi.Result.Last(2);
            if (double.IsNaN(rsiNow) || double.IsNaN(rsiPrev)) return false;

            bool oversoldSig = RsiRequireCrossBack
                ? (rsiPrev <= RsiOversoldLevel && rsiNow > RsiOversoldLevel)
                : (rsiNow <= RsiOversoldLevel);

            bool overboughtSig = RsiRequireCrossBack
                ? (rsiPrev >= RsiOverboughtLevel && rsiNow < RsiOverboughtLevel)
                : (rsiNow >= RsiOverboughtLevel);

            // Long path
            if (AllowLongs && oversoldSig)
            {
                if (EnableHtfBiasFilter && htfBias < 0)
                    return Reject($"HTF bias bearish (rsi={rsiNow:F1})");

                double entryRef = Symbol.Ask;
                FvgZone target  = FindNearestUnfilledFvg(true, entryRef);
                double tpPrice  = ResolveTpPrice(target, entryRef, true);

                return TryOpenWithFvgTarget(TradeType.Buy, entryRef, tpPrice, target,
                    $"RSI oversold rsi={rsiNow:F1} (prev={rsiPrev:F1})");
            }

            // Short path
            if (AllowShorts && overboughtSig)
            {
                if (EnableHtfBiasFilter && htfBias > 0)
                    return Reject($"HTF bias bullish (rsi={rsiNow:F1})");

                double entryRef = Symbol.Bid;
                FvgZone target  = FindNearestUnfilledFvg(false, entryRef);
                double tpPrice  = ResolveTpPrice(target, entryRef, false);

                return TryOpenWithFvgTarget(TradeType.Sell, entryRef, tpPrice, target,
                    $"RSI overbought rsi={rsiNow:F1} (prev={rsiPrev:F1})");
            }

            return false;
        }

        private double ResolveTpPrice(FvgZone z, double entryRef, bool longSide)
        {
            if (z == null) return double.NaN;
            switch (FvgTpMode)
            {
                case FvgTargetMode.NearEdge: return longSide ? z.Bottom : z.Top;
                case FvgTargetMode.FarEdge:  return longSide ? z.Top    : z.Bottom;
                case FvgTargetMode.Midpoint:
                default:                     return (z.Top + z.Bottom) / 2.0;
            }
        }

        private bool TryOpenWithFvgTarget(TradeType dir, double entryRef, double tpPrice,
                                          FvgZone target, string reason)
        {
            double atrPips = GetAtrPips();
            double slPips  = Math.Round(atrPips * AtrSlMultiplier + CommissionBufferPips, 1);
            if (slPips < MinSlPips) slPips = MinSlPips;
            if (slPips > MaxSlPips) slPips = MaxSlPips;

            double tpPips;
            string edgeLabel;
            if (target != null && !double.IsNaN(tpPrice))
            {
                tpPips = dir == TradeType.Buy
                    ? (tpPrice - entryRef) / Symbol.PipSize
                    : (entryRef - tpPrice) / Symbol.PipSize;

                if (tpPips <= 0)
                    return Reject($"FVG target on wrong side (tpPips={tpPips:F1})");

                double impliedRrr = tpPips / slPips;
                if (impliedRrr < MinRrrFvg)
                    return Reject($"RRR {impliedRrr:F2} < min {MinRrrFvg:F2} (tp={tpPips:F1}p sl={slPips:F1}p)");

                edgeLabel = "RsiFvg";
            }
            else
            {
                if (FallbackRrr <= 0)
                    return Reject("No FVG target & no fallback RRR");
                tpPips    = Math.Round(slPips * FallbackRrr, 1);
                edgeLabel = "RsiFallback";
            }

            double spreadPips = Symbol.Spread / Symbol.PipSize;
            double minTpPips  = Math.Max(1.0, spreadPips * 1.5);
            if (tpPips < minTpPips)
                return Reject($"TP {tpPips:F1}p < min {minTpPips:F1}p (spread={spreadPips:F2})");

            return OpenTradeRaw(dir, edgeLabel, TradeSetupKind.MeanReversion,
                slPips, tpPips, target != null ? tpPrice : double.NaN, reason);
        }

        // ════════════════════════════════════════════════════════════════════
        //  FVG DETECTION & MANAGEMENT
        // ════════════════════════════════════════════════════════════════════
        // 3-bar pattern (closed bars only):
        //   Bullish FVG: Low[i] > High[i-2]  → zone (High[i-2], Low[i])
        //   Bearish FVG: High[i] < Low[i-2]  → zone (High[i], Low[i-2])
        // The middle bar (i-1) "leaves" the gap between bars i-2 and i.
        private void DetectNewFvgOnLastClosedBar()
        {
            if (Bars.Count < 4) return;
            if (_lastDetectedBarCount == Bars.Count) return;
            _lastDetectedBarCount = Bars.Count;

            // Last(1) = most recently closed bar (= bar i in pattern)
            int idxI  = 1;
            int idxI2 = 3;

            double highI  = Bars.HighPrices.Last(idxI);
            double lowI   = Bars.LowPrices.Last(idxI);
            double highI2 = Bars.HighPrices.Last(idxI2);
            double lowI2  = Bars.LowPrices.Last(idxI2);
            DateTime tI   = Bars.OpenTimes.Last(idxI);

            double minSize = FvgMinSizePips * Symbol.PipSize;

            // Bullish FVG
            if (lowI > highI2 + minSize)
            {
                _fvgs.Add(new FvgZone
                {
                    IsBullish        = true,
                    Top              = lowI,
                    Bottom           = highI2,
                    FormedAt         = tI,
                    FormedAtBarIndex = Bars.Count - 1 - idxI,
                });
                if (Verbose) Print("FVG+ formed: [{0:F5} – {1:F5}] size={2:F1}p @ {3:HH:mm}",
                    highI2, lowI, (lowI - highI2) / Symbol.PipSize, tI);
            }

            // Bearish FVG
            if (highI < lowI2 - minSize)
            {
                _fvgs.Add(new FvgZone
                {
                    IsBullish        = false,
                    Top              = lowI2,
                    Bottom           = highI,
                    FormedAt         = tI,
                    FormedAtBarIndex = Bars.Count - 1 - idxI,
                });
                if (Verbose) Print("FVG- formed: [{0:F5} – {1:F5}] size={2:F1}p @ {3:HH:mm}",
                    highI, lowI2, (lowI2 - highI) / Symbol.PipSize, tI);
            }
        }

        // Seed FVGs from history at startup.
        private void SeedFvgHistory()
        {
            int scan = Math.Min(FvgHistorySeedBars, Bars.Count - 4);
            double minSize = FvgMinSizePips * Symbol.PipSize;
            for (int back = scan; back >= 1; back--)
            {
                int idxI  = back;
                int idxI2 = back + 2;
                if (idxI2 >= Bars.Count) continue;

                double highI  = Bars.HighPrices.Last(idxI);
                double lowI   = Bars.LowPrices.Last(idxI);
                double highI2 = Bars.HighPrices.Last(idxI2);
                double lowI2  = Bars.LowPrices.Last(idxI2);
                DateTime tI   = Bars.OpenTimes.Last(idxI);

                if (lowI > highI2 + minSize)
                {
                    _fvgs.Add(new FvgZone
                    {
                        IsBullish        = true,
                        Top              = lowI,
                        Bottom           = highI2,
                        FormedAt         = tI,
                        FormedAtBarIndex = Bars.Count - 1 - idxI,
                    });
                }
                if (highI < lowI2 - minSize)
                {
                    _fvgs.Add(new FvgZone
                    {
                        IsBullish        = false,
                        Top              = lowI2,
                        Bottom           = highI,
                        FormedAt         = tI,
                        FormedAtBarIndex = Bars.Count - 1 - idxI,
                    });
                }
            }
        // Full sweep: mark FVGs filled by any bar in the seeded range.
            for (int back = scan; back >= 1; back--)
            {
                double barHigh = Bars.HighPrices.Last(back);
                double barLow  = Bars.LowPrices.Last(back);
                int    barIdx  = Bars.Count - 1 - back;
                foreach (var z in _fvgs)
                {
                    if (z.Filled) continue;
                    if (z.FormedAtBarIndex >= barIdx) continue;
                    if (barLow <= z.Bottom && barHigh >= z.Top)
                        z.Filled = true;
                    else if (barLow <= z.Top && barHigh >= z.Bottom)
                        z.PartiallyTouched = true;
                }
            }
            if (Verbose) Print("SeedFvg: total={0} filled={1} unfilled={2}",
                _fvgs.Count, _fvgs.Count(z => z.Filled), _fvgs.Count(z => !z.Filled));
            _lastDetectedBarCount = Bars.Count;
        }

        // Mark FVGs as filled when price has fully traversed them.
        private void ScanFvgFills()
        {
            if (Bars.Count < 2) return;
            double lastHigh = Bars.HighPrices.Last(1);
            double lastLow  = Bars.LowPrices.Last(1);

            foreach (var z in _fvgs)
            {
                if (z.Filled) continue;
                if (z.FormedAtBarIndex >= Bars.Count - 1) continue; // own bar

                if (lastLow <= z.Bottom && lastHigh >= z.Top)
                {
                    z.Filled = true;
                }
                else if (lastLow <= z.Top && lastHigh >= z.Bottom)
                {
                    z.PartiallyTouched = true;
                }
            }
        }

        private void PruneFvgs()
        {
            int currentIdx = Bars.Count - 1;
            _fvgs.RemoveAll(z =>
                z.Filled ||
                (FvgMaxAgeBars > 0 && currentIdx - z.FormedAtBarIndex > FvgMaxAgeBars));
        }

        private FvgZone FindNearestUnfilledFvg(bool wantBullish, double refPrice)
        {
            FvgZone best = null;
            double bestDist = double.MaxValue;
            double maxDist = FvgMaxDistancePips > 0 ? FvgMaxDistancePips * Symbol.PipSize : double.MaxValue;

            foreach (var z in _fvgs)
            {
                if (z.Filled) continue;
                if (z.IsBullish != wantBullish) continue;

                // For longs we need a bullish FVG above price; for shorts a bearish FVG below.
                double dist;
                if (wantBullish)
                {
                    if (z.Bottom <= refPrice) continue;
                    dist = z.Bottom - refPrice;
                }
                else
                {
                    if (z.Top >= refPrice) continue;
                    dist = refPrice - z.Top;
                }

                if (dist > maxDist) continue;
                if (dist < bestDist) { bestDist = dist; best = z; }
            }
            return best;
        }

        // ════════════════════════════════════════════════════════════════════
        //  VALIDATION
        // ════════════════════════════════════════════════════════════════════
        private bool ValidateParameters()
        {
            bool ok = true;
            if (MaxDailyDdPct > 0 && RiskPerTradePct * 2 > MaxDailyDdPct)
            {
                double clamped = MaxDailyDdPct / 2.0;
                Print("WARNING: RiskPerTradePct*2 ({0:F2}%) > MaxDailyDdPct ({1:F2}%) — clamping to {2:F2}%",
                    RiskPerTradePct * 2, MaxDailyDdPct, clamped);
                RiskPerTradePct = clamped;
            }
            if (RsiOversoldLevel >= RsiOverboughtLevel)
            {
                Print("CRITICAL: RsiOversoldLevel ({0}) >= RsiOverboughtLevel ({1})",
                    RsiOversoldLevel, RsiOverboughtLevel);
                ok = false;
            }
            if (AtrRatioHigh <= AtrRatioLow)
            {
                Print("CRITICAL: AtrRatioHigh ({0:F2}) <= AtrRatioLow ({1:F2})", AtrRatioHigh, AtrRatioLow);
                ok = false;
            }
            if (MinSlPips >= MaxSlPips)
            {
                Print("CRITICAL: MinSlPips ({0}) >= MaxSlPips ({1})", MinSlPips, MaxSlPips);
                ok = false;
            }
            if (!AllowLongs && !AllowShorts)
            {
                Print("CRITICAL: both AllowLongs and AllowShorts disabled — bot will idle");
                ok = false;
            }
            return ok;
        }

        // ════════════════════════════════════════════════════════════════════
        //  GATES
        // ════════════════════════════════════════════════════════════════════
        private bool IsMarketTradable()
        {
            DateTime now    = Server.Time;
            DateTime nowUtc = NowUtc();

            if (_dailyDdBreached)                  return Reject("DailyDD hit");
            if (_weeklyDdBreached)                 return Reject("WeeklyDD hit");
            if (_tradesToday >= MaxTradesPerDay)   return Reject("MaxTradesPerDay");
            if (now < _cooldownEndTime)            return Reject("CoolDown");
            if (ReEntryCooldownBars > 0 && _lastTradeCloseBarIndex >= 0
                && (Bars.Count - 1 - _lastTradeCloseBarIndex) < ReEntryCooldownBars)
                return Reject($"ReEntryCooldown {Bars.Count - 1 - _lastTradeCloseBarIndex}/{ReEntryCooldownBars}b");

            foreach (var (center, halfMin) in _newsBlackouts)
                if (Math.Abs((now - center).TotalMinutes) <= halfMin)
                    return Reject($"NewsBlackout ({center:yyyy-MM-dd HH:mm} ±{halfMin}m)");

            int hour = nowUtc.Hour;
            if (hour < SessionStartHour || hour >= SessionEndHour) return Reject("OutsideSession");
            if (BlockFriday && nowUtc.DayOfWeek == DayOfWeek.Friday) return Reject("FridayBlock");

            double spreadPips = Symbol.Spread / Symbol.PipSize;
            double atrPips    = GetAtrPips();
            double dynCap     = Math.Min(MaxSpreadPips, Math.Max(SPREAD_FLOOR_PIPS, atrPips * SpreadAtrRatio));
            if (spreadPips > dynCap) return Reject($"SpreadGate {spreadPips:F2} > {dynCap:F2}");

            return true;
        }

        private bool Reject(string reason)
        {
            if (Verbose) Print("REJECT: {0}", reason);
            return false;
        }

        // ════════════════════════════════════════════════════════════════════
        //  REGIME CLASSIFIER
        // ════════════════════════════════════════════════════════════════════
        private TradeRegime ClassifyRegime()
        {
            if (_dailyBars == null || _dailyBars.Count < MedianLookbackDays + 2) return _lastRegime;
            if (_dailyAtr  == null)                                              return _lastRegime;

            double dailyAtrPrice = _dailyAtr.Result.Last(1);
            if (double.IsNaN(dailyAtrPrice) || dailyAtrPrice <= 0) return _lastRegime;

            double[] trs = new double[MedianLookbackDays];
            for (int i = 0; i < MedianLookbackDays; i++)
            {
                int idx = i + 1;
                if (idx + 1 >= _dailyBars.Count) return _lastRegime;
                double h  = _dailyBars.HighPrices.Last(idx);
                double l  = _dailyBars.LowPrices.Last(idx);
                double c1 = _dailyBars.ClosePrices.Last(idx + 1);
                trs[i] = Math.Max(h - l, Math.Max(Math.Abs(h - c1), Math.Abs(l - c1)));
            }
            Array.Sort(trs);

            int n = MedianLookbackDays;
            double median = (n % 2 == 0)
                ? (trs[n / 2 - 1] + trs[n / 2]) / 2.0
                : trs[n / 2];
            if (median <= 0) return _lastRegime;

            double ratio = dailyAtrPrice / median;
            TradeRegime newRegime = _lastRegime;
            switch (_lastRegime)
            {
                case TradeRegime.LowVol:
                    if (ratio > AtrRatioLow + RegimeHysteresisBand)
                        newRegime = ratio >= AtrRatioHigh ? TradeRegime.HighVol : TradeRegime.Normal;
                    break;
                case TradeRegime.HighVol:
                    if (ratio < AtrRatioHigh - RegimeHysteresisBand)
                        newRegime = ratio <= AtrRatioLow ? TradeRegime.LowVol : TradeRegime.Normal;
                    break;
                default:
                    if      (ratio <= AtrRatioLow)  newRegime = TradeRegime.LowVol;
                    else if (ratio >= AtrRatioHigh) newRegime = TradeRegime.HighVol;
                    break;
            }

            if (newRegime != _lastRegime)
            {
                _lastRegime = newRegime;
                if (Verbose) Print("Regime change: {0} (ratio={1:F3})", newRegime, ratio);
            }
            return _lastRegime;
        }

        private void WarmupBars(Bars bars, int needed, string label)
        {
            if (bars == null) return;
            int guard = 0;
            while (bars.Count < needed && guard++ < 20)
            {
                int loaded = bars.LoadMoreHistory();
                if (loaded <= 0) break;
            }
            if (Verbose) Print("Warmup {0}: {1} bars (target {2})", label, bars.Count, needed);
        }

        private int GetHtfBias()
        {
            if (_htfBars == null || _htfEma == null) return 0;
            double close = _htfBars.ClosePrices.Last(1);
            double ema   = _htfEma.Result.Last(1);
            double buf   = HtfBufferPips * Symbol.PipSize;
            if (double.IsNaN(close) || double.IsNaN(ema)) return 0;
            if (close > ema + buf)  return 1;
            if (close < ema - buf)  return -1;
            return 0;
        }

        private double GetAtrPips()
        {
            double atr = _atr.Result.Last(1);
            if (double.IsNaN(atr) || atr <= 0) return 10.0;
            return atr / Symbol.PipSize;
        }

        // ════════════════════════════════════════════════════════════════════
        //  ORDER EXECUTION
        // ════════════════════════════════════════════════════════════════════
        private bool OpenTradeRaw(TradeType dir, string edgeLabel, TradeSetupKind setup,
                                  double slPips, double tpPips, double fvgTargetPrice, string reason)
        {
            double volume = CalculateVolume(slPips, dir);
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("REJECT: vol below broker min. slPips={0:F1} vol={1:F0}", slPips, volume);
                return false;
            }

            double intendedPrice = dir == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            string metadata = $"|{edgeLabel}|{setup}";

            TradeResult result = EntryMarketRangePips > 0
                ? ExecuteMarketRangeOrder(dir, SymbolName, volume, EntryMarketRangePips, intendedPrice, BotLabel, slPips, tpPips, metadata)
                : ExecuteMarketOrder(dir, SymbolName, volume, BotLabel, slPips, tpPips, metadata);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("ORDER FAILED: {0}", result.Error);
                return false;
            }

            var pos = result.Position;
            double entrySlippagePips = dir == TradeType.Buy
                ? (pos.EntryPrice - intendedPrice) / Symbol.PipSize
                : (intendedPrice - pos.EntryPrice) / Symbol.PipSize;

            double rrr = slPips > 0 ? tpPips / slPips : 0;

            _currentTrade = new BotTradeState
            {
                PositionId               = pos.Id,
                EntryPrice               = pos.EntryPrice,
                InitialSlPips            = slPips,
                InitialVolumeUnits       = pos.VolumeInUnits,
                RRRTarget                = rrr,
                EntryTime                = Server.Time,
                EdgeLabel                = edgeLabel,
                Setup                    = setup,
                EntrySlippage            = entrySlippagePips,
                ChandelierStop           = dir == TradeType.Buy ? double.MinValue : double.MaxValue,
                ChandelierPeakHigh       = dir == TradeType.Buy ? pos.EntryPrice : double.MinValue,
                ChandelierPeakLow        = dir == TradeType.Sell ? pos.EntryPrice : double.MaxValue,
                LastChandelierUpdateTime = Server.Time,
                EntryBarIndex            = Bars.Count - 1,
                EntrySessionKey          = GetSessionKey(Server.Time),
                FvgTargetPrice           = fvgTargetPrice,
            };
            _totalTradesOpened++;
            _tradesToday++;

            Print("FILLED: {0} {1} vol={2:F0} entry={3:F5} slip={4:+0.0;-0.0;0.0}p SL={5:F1}p TP={6:F1}p RRR={7:F2} edge={8} | {9}",
                dir, SymbolName, volume, pos.EntryPrice, entrySlippagePips, slPips, tpPips, rrr, edgeLabel, reason);
            return true;
        }

        private double CalculateVolume(double slPips, TradeType dir)
        {
            double baseAcct = RiskBase == RiskBaseKind.Equity ? Account.Equity : Account.Balance;
            double riskPct  = RiskPerTradePct;

            double regimeMult = _lastRegime == TradeRegime.LowVol  ? RegimeRiskMultLowVol
                              : _lastRegime == TradeRegime.HighVol ? RegimeRiskMultHighVol
                              :                                       RegimeRiskMultNormal;
            if (Math.Abs(regimeMult - 1.0) > 0.001)
            {
                double prev = riskPct;
                riskPct *= regimeMult;
                if (Verbose) Print("RegimeRiskScale: regime={0} mult={1:F2} riskPct {2:F2}->{3:F2}",
                    _lastRegime, regimeMult, prev, riskPct);
            }

            double riskAmount = baseAcct * (riskPct / 100.0);
            double pipValue   = Symbol.PipValue;
            if (pipValue <= 0 || slPips <= 0) return Symbol.VolumeInUnitsMin;
            double exact      = riskAmount / (slPips * pipValue);
            double normalized = Symbol.NormalizeVolumeInUnits(exact, RoundingMode.Down);

            // Margin direct-solve cap
            double maxMarginRatio = MaxMarginUtilizationPct / 100.0;
            if (Account.Equity > 0)
            {
                double maxAllowedNew = maxMarginRatio * Account.Equity - Account.Margin;
                if (maxAllowedNew <= 0) return Symbol.VolumeInUnitsMin;
                double currentMargin = Symbol.GetEstimatedMargin(dir, normalized);
                if (currentMargin > maxAllowedNew && currentMargin > 0)
                {
                    double scale  = maxAllowedNew / currentMargin;
                    double oldVol = normalized;
                    normalized    = Symbol.NormalizeVolumeInUnits(normalized * scale, RoundingMode.Down);
                    if (normalized < Symbol.VolumeInUnitsMin) return Symbol.VolumeInUnitsMin;
                    if (Verbose) Print("MarginCap: util={0:P1} allowed={1:F2} cap={2:P1} vol {3:F0}->{4:F0}",
                        Account.Margin / Account.Equity, maxAllowedNew, maxMarginRatio, oldVol, normalized);
                }
            }
            return normalized;
        }

        // ════════════════════════════════════════════════════════════════════
        //  TRADE MANAGEMENT
        // ════════════════════════════════════════════════════════════════════
        private void ManageBreakEven(Position pos)
        {
            if (!EnableBreakEven || _currentTrade.BreakEvenDone) return;
            double slPips = _currentTrade.InitialSlPips;
            if (slPips <= 0) return;
            double currentR = ComputeCurrentR(pos, slPips);
            if (currentR < BeTriggerR) return;

            double newSl = pos.TradeType == TradeType.Buy
                ? pos.EntryPrice + BeOffsetPips * Symbol.PipSize
                : pos.EntryPrice - BeOffsetPips * Symbol.PipSize;

            if (pos.StopLoss.HasValue)
            {
                bool improves = pos.TradeType == TradeType.Buy
                    ? newSl > pos.StopLoss.Value
                    : newSl < pos.StopLoss.Value;
                if (!improves) { _currentTrade.BreakEvenDone = true; return; }
            }

            var r = ModifyPosition(pos, newSl, pos.TakeProfit);
            if (r.IsSuccessful)
            {
                _currentTrade.BreakEvenDone = true;
                if (Verbose) Print("BE set @ {0:F5} (R={1:F2})", newSl, currentR);
            }
        }

        private void ManagePartials(Position pos)
        {
            if (!EnablePartial1 || _currentTrade.Partial1Done) return;
            double slPips = _currentTrade.InitialSlPips;
            if (slPips <= 0) return;
            double currentR = ComputeCurrentR(pos, slPips);
            if (currentR >= Partial1TriggerR)
            {
                ClosePartial(pos, Partial1Fraction, "Partial1");
                _currentTrade.Partial1Done = true;
            }
        }

        private void ManagePartialsAtBarClose(Position pos)
        {
            if (!EnablePartial1 || _currentTrade.Partial1Done) return;
            double slPips = _currentTrade.InitialSlPips;
            if (slPips <= 0) return;
            double barMaxR = ComputeBarMaxR(pos, slPips);
            if (barMaxR >= Partial1TriggerR)
            {
                ClosePartial(pos, Partial1Fraction, "Partial1@BarClose");
                _currentTrade.Partial1Done = true;
            }
        }

        private void ClosePartial(Position pos, double fraction, string tag)
        {
            double volToClose = pos.VolumeInUnits * fraction;
            double normalized = Symbol.NormalizeVolumeInUnits(volToClose, RoundingMode.Down);
            if (normalized < Symbol.VolumeInUnitsMin) return;
            if (normalized >= pos.VolumeInUnits) normalized = pos.VolumeInUnits - Symbol.VolumeInUnitsStep;
            if (normalized < Symbol.VolumeInUnitsMin) return;

            double intendedExit = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            var r = ClosePosition(pos, normalized);
            if (r.IsSuccessful)
            {
                if (_currentTrade != null)
                {
                    double exitSlip = pos.TradeType == TradeType.Buy
                        ? (intendedExit - Symbol.Bid) / Symbol.PipSize
                        : (Symbol.Ask - intendedExit) / Symbol.PipSize;
                    _currentTrade.ExitSlippagePips += exitSlip;
                }
                if (Verbose) Print("{0}: closed {1:F0}u remaining {2:F0}u", tag, normalized, pos.VolumeInUnits - normalized);
            }
        }

        private void ManageChandelier(Position pos)
        {
            if (!EnableChandelier) return;
            if (!(_currentTrade.Partial1Done || _currentTrade.BreakEvenDone)) return;
            double atr = _atrChandelier.Result.Last(1);
            if (double.IsNaN(atr) || atr <= 0) return;

            DateTime lastBarTime = Bars.OpenTimes.Last(1);
            if (lastBarTime != _currentTrade.LastChandelierUpdateTime)
            {
                _currentTrade.LastChandelierUpdateTime = lastBarTime;
                if (pos.TradeType == TradeType.Buy)
                {
                    if (Bars.HighPrices.Last(1) > _currentTrade.ChandelierPeakHigh)
                        _currentTrade.ChandelierPeakHigh = Bars.HighPrices.Last(1);
                }
                else
                {
                    if (Bars.LowPrices.Last(1) < _currentTrade.ChandelierPeakLow)
                        _currentTrade.ChandelierPeakLow = Bars.LowPrices.Last(1);
                }
            }

            if (pos.TradeType == TradeType.Buy)
            {
                double candidate = _currentTrade.ChandelierPeakHigh - ChandelierAtrMult * atr;
                if (candidate > _currentTrade.ChandelierStop) _currentTrade.ChandelierStop = candidate;

                double desiredSl = _currentTrade.ChandelierStop;
                double capSl     = Symbol.Bid - CHANDELIER_SL_CAP_PIPS * Symbol.PipSize;
                if (desiredSl >= capSl) desiredSl = capSl;
                if (pos.StopLoss.HasValue && desiredSl <= pos.StopLoss.Value) return;
                if (!pos.StopLoss.HasValue || desiredSl > pos.StopLoss.Value)
                    ModifyPosition(pos, desiredSl, pos.TakeProfit);
            }
            else
            {
                double candidate = _currentTrade.ChandelierPeakLow + ChandelierAtrMult * atr;
                if (candidate < _currentTrade.ChandelierStop) _currentTrade.ChandelierStop = candidate;

                double desiredSl = _currentTrade.ChandelierStop;
                double capSl     = Symbol.Ask + CHANDELIER_SL_CAP_PIPS * Symbol.PipSize;
                if (desiredSl <= capSl) desiredSl = capSl;
                if (pos.StopLoss.HasValue && desiredSl >= pos.StopLoss.Value) return;
                if (!pos.StopLoss.HasValue || desiredSl < pos.StopLoss.Value)
                    ModifyPosition(pos, desiredSl, pos.TakeProfit);
            }
        }

        private void ManageMaxHold(Position pos)
        {
            if (MaxHoldBars <= 0) return;
            int barsHeld = Bars.Count - 1 - _currentTrade.EntryBarIndex;
            if (barsHeld >= MaxHoldBars) ClosePositionIfOpen("MaxHold");
        }

        private double ComputeCurrentR(Position pos, double initialSlPips)
        {
            if (initialSlPips <= 0) return 0;
            double currentPips = pos.TradeType == TradeType.Buy
                ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;
            return currentPips / initialSlPips;
        }

        private double ComputeBarMaxR(Position pos, double initialSlPips)
        {
            if (initialSlPips <= 0) return 0;
            double pips = pos.TradeType == TradeType.Buy
                ? (Bars.HighPrices.Last(1) - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Bars.LowPrices.Last(1)) / Symbol.PipSize;
            return pips / initialSlPips;
        }

        private double ComputeBarMinR(Position pos, double initialSlPips)
        {
            if (initialSlPips <= 0) return 0;
            double pips = pos.TradeType == TradeType.Buy
                ? (Bars.LowPrices.Last(1)  - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Bars.HighPrices.Last(1)) / Symbol.PipSize;
            return pips / initialSlPips;
        }

        // ════════════════════════════════════════════════════════════════════
        //  POSITION HOOKS
        // ════════════════════════════════════════════════════════════════════
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != BotLabel || pos.SymbolName != SymbolName) return;
            if (_currentTrade != null) return;
            RecoverExistingPosition();
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != BotLabel || pos.SymbolName != SymbolName) return;
            if (_currentTrade == null || _currentTrade.PositionId != pos.Id) { _currentTrade = null; return; }

            double pnl       = pos.NetProfit;
            string edgeLabel = _currentTrade.EdgeLabel ?? "Unknown";
            double rMultiple = _currentTrade.InitialSlPips > 0
                ? pnl / (Symbol.PipValue * _currentTrade.InitialSlPips * _currentTrade.InitialVolumeUnits)
                : 0;

            if (!_currentTrade.RecoveredWithoutMetadata)
            {
                _rMultiples.Add(rMultiple);
                DateTime tradeDay = _currentTrade.EntryTime.Date;
                if (!_dailyRSum.ContainsKey(tradeDay)) _dailyRSum[tradeDay] = 0;
                _dailyRSum[tradeDay] += rMultiple;
            }

            _dayRealizedPnl  += pnl;
            _weekRealizedPnl += pnl;

            if (_currentTrade.RecoveredWithoutMetadata)
            {
                Print("STATS-SKIP: recovered position {0} excluded from edge stats", pos.Id);
                if (pnl > 0)      _consecutiveLosses = 0;
                else if (pnl < 0) _consecutiveLosses++;
            }
            else
            {
                EnsureEdgeKeys(edgeLabel);
                if (pnl > 0)
                {
                    _edgeWinCount[edgeLabel]++;
                    _consecutiveLosses = 0;
                }
                else if (pnl < 0)
                {
                    _edgeLossCount[edgeLabel]++;
                    _consecutiveLosses++;
                    if (ConsecLossCoolDownHours > 0 && _consecutiveLosses >= ConsecLossTrigger)
                    {
                        _cooldownEndTime = Server.Time.AddHours(ConsecLossCoolDownHours);
                        Print("CoolDown engaged until {0:yyyy-MM-dd HH:mm} (consec losses={1})",
                            _cooldownEndTime, _consecutiveLosses);
                    }
                }
                _edgePnlSum[edgeLabel]              += pnl;
                _edgeEntrySlippageSum[edgeLabel]    += _currentTrade.EntrySlippage;
                _edgeSlippageSampleCount[edgeLabel] += 1;
                _edgeMaeSum[edgeLabel]              += _currentTrade.MaeRMultiple;
                _edgeMfeSum[edgeLabel]              += _currentTrade.MfeRMultiple;
                _edgeExitSlippageSum[edgeLabel]     += _currentTrade.ExitSlippagePips;
                _edgeExitSlippageCount[edgeLabel]   += 1;

                SessionKey skey = _currentTrade.EntrySessionKey;
                if (!_sessionStats.ContainsKey(skey))
                    _sessionStats[skey] = new SessionStats();
                SessionStats stat = _sessionStats[skey];
                stat.TradeCount++;
                if (pnl > 0) stat.Wins++;
                stat.PnLSum += pnl;
                _sessionStats[skey] = stat;
            }

            Print("CLOSED: id={0} edge={1} pnl={2:F2} R={3:+0.00;-0.00;0.00} slip_e={4:+0.0;-0.0;0.0}p slip_x={5:+0.0;-0.0;0.0}p MAE={6:F2}R MFE={7:F2}R consecLoss={8}",
                pos.Id, edgeLabel, pnl, rMultiple,
                _currentTrade.EntrySlippage, _currentTrade.ExitSlippagePips,
                _currentTrade.MaeRMultiple, _currentTrade.MfeRMultiple,
                _consecutiveLosses);

            if (ExportTradeLogCsv)
            {
                SessionKey sk = _currentTrade.EntrySessionKey;
                _tradeLogRows.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss},{2},{3},{4},{5},{6:F5},{7:F5},{8:F1},{9:F2},{10:F0},{11:F2},{12:F4},{13:F2},{14:F2},{15:F4},{16:F4},{17},{18},{19:F5}",
                    _currentTrade.EntryTime, Server.Time, SymbolName,
                    edgeLabel, _currentTrade.Setup, pos.TradeType,
                    _currentTrade.EntryPrice, pos.EntryPrice,
                    _currentTrade.InitialSlPips, _currentTrade.RRRTarget,
                    _currentTrade.InitialVolumeUnits, pnl, rMultiple,
                    _currentTrade.EntrySlippage, _currentTrade.ExitSlippagePips,
                    _currentTrade.MaeRMultiple, _currentTrade.MfeRMultiple,
                    _tradeOpenRegime, SessionBucketName(sk.SessionBucket),
                    _currentTrade.FvgTargetPrice));
            }

            _lastTradeCloseBarIndex = Bars.Count - 1;
            _currentTrade = null;
        }

        private void EnsureEdgeKeys(string edge)
        {
            if (_edgeWinCount.ContainsKey(edge)) return;
            _edgeWinCount[edge]            = 0;
            _edgeLossCount[edge]           = 0;
            _edgePnlSum[edge]              = 0;
            _edgeEntrySlippageSum[edge]    = 0;
            _edgeSlippageSampleCount[edge] = 0;
            _edgeExitSlippageSum[edge]     = 0;
            _edgeExitSlippageCount[edge]   = 0;
            _edgeMaeSum[edge]              = 0;
            _edgeMfeSum[edge]              = 0;
        }

        // ════════════════════════════════════════════════════════════════════
        //  DAILY / WEEKLY ROLLOVER + DD GATES
        // ════════════════════════════════════════════════════════════════════
        private void RolloverDailyWeekly()
        {
            DateTime today    = Server.Time.Date;
            DateTime thisWeek = GetWeekMonday(Server.Time);

            if (_botDay != today)
            {
                if (_botDay != DateTime.MinValue) _dailyEquityClose[_botDay] = Account.Balance;
                _botDay          = today;
                _dayStartBalance = Account.Balance;
                _dayRealizedPnl  = 0;
                _dailyDdBreached = false;
                _tradesToday     = 0;
                if (Verbose) Print("Daily rollover. StartBalance={0:F2}", _dayStartBalance);
            }
            if (_botWeek != thisWeek)
            {
                _botWeek          = thisWeek;
                _weekStartBalance = Account.Balance;
                _weekRealizedPnl  = 0;
                _weeklyDdBreached = false;
                if (Verbose) Print("Weekly rollover. StartBalance={0:F2}", _weekStartBalance);
            }

            if (MaxDailyDdPct > 0 && !_dailyDdBreached)
            {
                double ddPct = Math.Abs(_dayRealizedPnl) / Math.Max(_dayStartBalance, 1) * 100.0;
                if (_dayRealizedPnl < 0 && ddPct >= MaxDailyDdPct)
                {
                    _dailyDdBreached = true;
                    ClosePositionIfOpen("DailyDD");
                    Print("DAILY DD HIT: realized={0:F2} ({1:F2}%)", _dayRealizedPnl, ddPct);
                }
            }
            if (MaxFloatingDailyDdPct > 0 && !_dailyDdBreached)
            {
                double floatingPct = (_dayStartBalance - Account.Equity) / Math.Max(_dayStartBalance, 1) * 100.0;
                if (floatingPct >= MaxFloatingDailyDdPct)
                {
                    _dailyDdBreached = true;
                    ClosePositionIfOpen("FloatingDDHit");
                    Print("FLOATING DAILY DD HIT: equity={0:F2} dd={1:F2}%", Account.Equity, floatingPct);
                }
            }
            if (MaxWeeklyDdPct > 0 && !_weeklyDdBreached)
            {
                double ddPct = Math.Abs(_weekRealizedPnl) / Math.Max(_weekStartBalance, 1) * 100.0;
                if (_weekRealizedPnl < 0 && ddPct >= MaxWeeklyDdPct)
                {
                    _weeklyDdBreached = true;
                    ClosePositionIfOpen("WeeklyDD");
                    Print("WEEKLY DD HIT: realized={0:F2} ({1:F2}%)", _weekRealizedPnl, ddPct);
                }
            }
            if (EquityCurveTrailPct > 0 && !_dailyDdBreached && !_weeklyDdBreached)
            {
                if (Account.Balance > _runHighWaterMarkBalance) _runHighWaterMarkBalance = Account.Balance;
                double trailPct = (_runHighWaterMarkBalance - Account.Balance) / Math.Max(_runHighWaterMarkBalance, 1) * 100.0;
                if (trailPct >= EquityCurveTrailPct)
                {
                    _dailyDdBreached  = true;
                    _weeklyDdBreached = true;
                    ClosePositionIfOpen("EquityTrailStop");
                    Print("EQUITY CURVE TRAIL STOP: HWM={0:F2} bal={1:F2} dd={2:F2}% — bot halted",
                        _runHighWaterMarkBalance, Account.Balance, trailPct);
                }
            }
        }

        private bool WeekendClosePending()
        {
            DateTime t = NowUtc();
            return t.DayOfWeek == DayOfWeek.Friday
                && t.Hour >= WeekendCloseHour
                && _currentTrade != null;
        }

        private void ClosePositionIfOpen(string reason)
        {
            if (_currentTrade == null) return;
            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null) { _currentTrade = null; return; }

            double intendedExit = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            var r = ClosePosition(pos);
            if (r.IsSuccessful)
            {
                double exitSlip = pos.TradeType == TradeType.Buy
                    ? (intendedExit - Symbol.Bid) / Symbol.PipSize
                    : (Symbol.Ask - intendedExit) / Symbol.PipSize;
                if (_currentTrade != null) _currentTrade.ExitSlippagePips += exitSlip;
                Print("FORCE CLOSE [{0}]: id={1} pnl={2:F2}", reason, pos.Id, pos.NetProfit);
            }
        }

        private void RecoverExistingPosition()
        {
            foreach (var p in Positions)
            {
                if (p.SymbolName != SymbolName || p.Label != BotLabel) continue;

                double slPips = 0;
                if (p.StopLoss.HasValue)
                {
                    slPips = p.TradeType == TradeType.Buy
                        ? (p.EntryPrice - p.StopLoss.Value) / Symbol.PipSize
                        : (p.StopLoss.Value - p.EntryPrice) / Symbol.PipSize;
                    slPips = Math.Abs(slPips);
                }
                if (slPips <= 0.1) slPips = GetAtrPips() * AtrSlMultiplier;

                string         recoveredEdge      = "Recovered";
                TradeSetupKind recoveredSetup     = TradeSetupKind.Other;
                bool           recoveredWithoutMd = true;

                if (!string.IsNullOrEmpty(p.Comment))
                {
                    var parts = p.Comment.Split('|');
                    if (parts.Length >= 3)
                    {
                        bool setupOk = Enum.TryParse<TradeSetupKind>(parts[2], out var setup);
                        if (setupOk && !string.IsNullOrEmpty(parts[1]))
                        {
                            recoveredEdge = parts[1];
                            recoveredSetup = setup;
                            recoveredWithoutMd = false;
                        }
                    }
                }
                if (recoveredWithoutMd) Print("RECOVERY: comment parse failed for id={0}", p.Id);

                int recoveredEntryIdx = Bars.Count - 1;
                for (int i = Bars.Count - 1; i >= 0; i--)
                {
                    if (Bars.OpenTimes[i] <= p.EntryTime) { recoveredEntryIdx = i; break; }
                }

                _currentTrade = new BotTradeState
                {
                    PositionId               = p.Id,
                    EntryPrice               = p.EntryPrice,
                    InitialSlPips            = slPips,
                    InitialVolumeUnits       = p.VolumeInUnits,
                    RRRTarget                = MinRrrFvg,
                    BreakEvenDone            = p.StopLoss.HasValue && (
                        p.TradeType == TradeType.Buy
                            ? p.StopLoss.Value >= p.EntryPrice - 0.1 * Symbol.PipSize
                            : p.StopLoss.Value <= p.EntryPrice + 0.1 * Symbol.PipSize),
                    Partial1Done             = true,
                    Partial2Done             = true,
                    ChandelierStop           = p.TradeType == TradeType.Buy ? double.MinValue : double.MaxValue,
                    EntryTime                = p.EntryTime,
                    EdgeLabel                = recoveredEdge,
                    Setup                    = recoveredSetup,
                    ChandelierPeakHigh       = p.TradeType == TradeType.Buy ? p.EntryPrice : double.MinValue,
                    ChandelierPeakLow        = p.TradeType == TradeType.Sell ? p.EntryPrice : double.MaxValue,
                    LastChandelierUpdateTime = Server.Time,
                    EntryBarIndex            = recoveredEntryIdx,
                    EntrySessionKey          = GetSessionKey(p.EntryTime),
                    RecoveredWithoutMetadata = recoveredWithoutMd,
                    FvgTargetPrice           = double.NaN,
                };
                Print("RECOVERY: adopted id={0} {1} entry={2:F5} slPips={3:F1} edge={4}",
                    p.Id, p.TradeType, p.EntryPrice, slPips, recoveredEdge);
                break;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  TIME / SESSION HELPERS
        // ════════════════════════════════════════════════════════════════════
        private DateTime NowUtc() => ServerTimeIsUtc ? Server.Time : Server.Time.ToUniversalTime();

        private static DateTime GetWeekMonday(DateTime d)
        {
            int back = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return d.Date.AddDays(-back);
        }

        private SessionKey GetSessionKey(DateTime time)
        {
            int hour   = time.Hour;
            int bucket = hour < 7 ? 0 : (hour < 13 ? 1 : (hour < 20 ? 2 : 3));
            return new SessionKey { DoW = time.DayOfWeek, SessionBucket = bucket };
        }

        private string SessionBucketName(int bucket) => bucket switch
        {
            0 => "Asia(0-7)",
            1 => "London(7-13)",
            2 => "NY(13-20)",
            3 => "Off(20-24)",
            _ => "Unknown"
        };

        // ════════════════════════════════════════════════════════════════════
        //  ATTRIBUTION
        // ════════════════════════════════════════════════════════════════════
        private void EmitAttribution()
        {
            Print("── Edge Attribution ─────────────────────────────");
            foreach (var kvp in _edgeWinCount)
            {
                string e = kvp.Key;
                int w = kvp.Value;
                int l = _edgeLossCount[e];
                int n = w + l;
                if (n == 0) continue;
                double wr            = (double)w / n;
                double avg           = _edgePnlSum[e] / n;
                double avgEntrySlip  = _edgeSlippageSampleCount[e] > 0 ? _edgeEntrySlippageSum[e] / _edgeSlippageSampleCount[e] : 0;
                Print("  {0,-12} n={1,3} wr={2:P1} avgPnL={3:+0.00;-0.00;0.00} entry_slip={4:+0.0;-0.0;0.0}p",
                    e, n, wr, avg, avgEntrySlip);
            }

            Print("── Session/DoW Matrix ───────────────────────────");
            foreach (var entry in _sessionStats.OrderBy(kvp => (kvp.Key.DoW, kvp.Key.SessionBucket)))
            {
                SessionKey   key  = entry.Key;
                SessionStats stat = entry.Value;
                double wr  = stat.TradeCount > 0 ? (double)stat.Wins / stat.TradeCount : 0;
                double avg = stat.TradeCount > 0 ? stat.PnLSum / stat.TradeCount : 0;
                Print("  {0,-10} {1,-12} n={2,2} wr={3:P0} avgPnL={4:+0.00;-0.00;0.00}",
                    key.DoW, SessionBucketName(key.SessionBucket), stat.TradeCount, wr, avg);
            }

            if (_rMultiples.Count > 0)
            {
                Print("── R-Multiple Distribution ──────────────────────");
                var sortedR    = _rMultiples.OrderBy(x => x).ToList();
                double meanR   = sortedR.Average();
                double medianR = sortedR.Count % 2 == 0
                    ? (sortedR[sortedR.Count / 2 - 1] + sortedR[sortedR.Count / 2]) / 2.0
                    : sortedR[sortedR.Count / 2];
                double p10 = sortedR[(int)(sortedR.Count * 0.10)];
                double p90 = sortedR[(int)(sortedR.Count * 0.90)];
                int    denomN = sortedR.Count < 30 ? sortedR.Count - 1 : sortedR.Count;
                double stdDev = denomN > 0 ? Math.Sqrt(sortedR.Sum(r => Math.Pow(r - meanR, 2)) / denomN) : 0;

                Print("  n={0,3} mean={1:+0.00;-0.00;0.00} median={2:+0.00;-0.00;0.00} p10={3:+0.00;-0.00;0.00} p90={4:+0.00;-0.00;0.00}",
                    sortedR.Count, meanR, medianR, p10, p90);
                Print("  stddev={0:F2} expectancy={1:+0.00;-0.00;0.00}R", stdDev, meanR);

                double peak = 0, cumR = 0, maxDdR = 0;
                foreach (double r in _rMultiples)
                {
                    cumR += r;
                    if (cumR > peak) peak = cumR;
                    double dd = peak - cumR;
                    if (dd > maxDdR) maxDdR = dd;
                }
                double grossWin  = _rMultiples.Where(r => r > 0).Sum();
                double grossLoss = Math.Abs(_rMultiples.Where(r => r < 0).Sum());
                double pf        = grossLoss > 0 ? grossWin / grossLoss : double.PositiveInfinity;

                if (_dailyRSum.Count >= 30)
                {
                    var dailyReturns  = _dailyRSum.Values.ToList();
                    double dailyMean  = dailyReturns.Average();
                    int    nDays      = dailyReturns.Count;
                    double dailyStd   = nDays > 1
                        ? Math.Sqrt(dailyReturns.Sum(r => Math.Pow(r - dailyMean, 2)) / (nDays - 1))
                        : 0;
                    double sharpe = dailyStd > 0 ? dailyMean / dailyStd * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                    Print("  Sharpe={0:+0.00;-0.00;0.00} MaxDD={1:F2}R PF={2:F2} | N={3}d", sharpe, maxDdR, pf, nDays);
                }
                else
                {
                    Print("  Sharpe=N/A (sample {0}d < 30) MaxDD={1:F2}R PF={2:F2}", _dailyRSum.Count, maxDdR, pf);
                }
                Print("  GrossWin={0:F2}R  GrossLoss={1:F2}R", grossWin, grossLoss);
            }

            Print("── MAE / MFE per Edge ───────────────────────────");
            foreach (var kvp in _edgeWinCount)
            {
                string e = kvp.Key;
                int total = _edgeWinCount[e] + _edgeLossCount[e];
                if (total == 0) continue;
                double avgMae      = _edgeMaeSum[e] / total;
                double avgMfe      = _edgeMfeSum[e] / total;
                double avgExitSlip = _edgeExitSlippageCount[e] > 0 ? _edgeExitSlippageSum[e] / _edgeExitSlippageCount[e] : 0;
                Print("  {0,-12} avgMAE={1:+0.00;-0.00;0.00}R avgMFE={2:+0.00;-0.00;0.00}R exit_slip={3:+0.0;-0.0;0.0}p",
                    e, avgMae, avgMfe, avgExitSlip);
            }
        }

        private void PersistAttributionJson()
        {
            if (!EnableAttributionPersistence) return;
            try { if (!Account.IsLive) return; } catch { return; }
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"bot\": \"{BotLabel}\",");
                sb.AppendLine($"  \"symbol\": \"{SymbolName}\",");
                sb.AppendLine($"  \"trades_opened\": {_totalTradesOpened},");
                sb.AppendLine("  \"edges\": {");
                var edges = _edgeWinCount.Keys.ToList();
                for (int i = 0; i < edges.Count; i++)
                {
                    string e = edges[i];
                    int w = _edgeWinCount[e], l = _edgeLossCount[e], n = w + l;
                    if (n == 0) continue;
                    double wr  = (double)w / n;
                    double avg = _edgePnlSum[e] / n;
                    sb.Append($"    \"{e}\": {{\"n\": {n}, \"wins\": {w}, \"loss\": {l}, \"wr\": {wr:F4}, \"avg_pnl\": {avg:F2}}}");
                    sb.AppendLine(i < edges.Count - 1 ? "," : "");
                }
                sb.AppendLine("  }");
                sb.AppendLine("}");

                string filename = $"{BotLabel}.attribution.json";
                string filepath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "cAlgo", "Robots", filename);
                System.IO.File.WriteAllText(filepath, sb.ToString(), Encoding.UTF8);
                Print($"Attribution saved: {filepath}");
            }
            catch (Exception ex)
            {
                Print($"ERROR persisting attribution: {ex.Message}");
            }
        }

        private void ExportTradeLogCsvFile()
        {
            if (!ExportTradeLogCsv || _tradeLogRows.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("EntryTime,ExitTime,Symbol,Edge,Setup,Direction,EntryPrice,ExitPrice,SLPips,RRR,VolumeUnits,PnL,RMultiple,EntrySlipP,ExitSlipP,MAE_R,MFE_R,Regime,SessionBucket,FvgTargetPrice");
                foreach (string row in _tradeLogRows) sb.AppendLine(row);

                string filename = $"{BotLabel}.tradelog.csv";
                string filepath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "cAlgo", "Robots", filename);
                System.IO.File.WriteAllText(filepath, sb.ToString(), Encoding.UTF8);
                Print("Trade log CSV saved: {0} ({1} rows)", filepath, _tradeLogRows.Count);
            }
            catch (Exception ex)
            {
                Print("ERROR exporting trade log CSV: {0}", ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  NEWS BLACKOUT PARSER
        // ════════════════════════════════════════════════════════════════════
        private void ParseNewsBlackout()
        {
            _newsBlackouts.Clear();
            if (string.IsNullOrWhiteSpace(NewsBlackoutCsv)) return;
            foreach (string entry in NewsBlackoutCsv.Split(';'))
            {
                string s = entry.Trim();
                if (string.IsNullOrEmpty(s)) continue;
                try
                {
                    int plusIdx = s.IndexOf('±');
                    if (plusIdx < 0) plusIdx = s.IndexOf('+', 10);
                    if (plusIdx < 0) { Print("NewsBlackout: no ± in '{0}', skipping", s); continue; }

                    string dtPart  = s.Substring(0, plusIdx).Trim();
                    string minPart = s.Substring(plusIdx + 1).Trim();

                    if (!DateTime.TryParse(dtPart, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime center))
                    { Print("NewsBlackout: bad datetime '{0}'", dtPart); continue; }
                    if (!int.TryParse(minPart, out int halfMin))
                    { Print("NewsBlackout: bad minutes '{0}'", minPart); continue; }

                    _newsBlackouts.Add((center, halfMin));
                    if (Verbose) Print("NewsBlackout registered: {0:yyyy-MM-dd HH:mm} ±{1}m", center, halfMin);
                }
                catch (Exception ex) { Print("NewsBlackout parse error '{0}': {1}", entry, ex.Message); }
            }
            Print("NewsBlackout: {0} window(s) loaded", _newsBlackouts.Count);
        }
    }
}
