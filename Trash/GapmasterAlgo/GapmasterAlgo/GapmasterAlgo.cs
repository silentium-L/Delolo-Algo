// ═══════════════════════════════════════════════════════════════════════════════
// GapMasterAlgo │ cTrader Gap-Fill Strategy (Single-File Build)
// Platform      │ cTrader / cAlgo  (Pepperstone Razor)
// Indicator     │ GapRadar (Gap Master.cs) — same assembly, plain Indicator subclass
//
// STRATEGIE-ÜBERSICHT
// ───────────────────
// Primär-TF : M15  (Entry-Signale)
// MTF-Layer : H1   (Gap-Kontext + Trendfilter)
//             H4   (Strukturfilter — kein Trade gegen H4-Trend)
//             D1   (Regime-Basis)
//
// SIGNAL-LOGIK (TryEnterGapMaster)
// ─────────────────────────────────
// 1. M15-GapRadar meldet aktives Gap mit Prob ≥ MinProbThreshold
// 2. Gap ist CONFIRMED (kein tentatives Live-Bar-Gap)
// 3. Gap-Typ-Filter: FVG / Weekend / Session / Liquidity (News default aus)
// 4. Gap liegt innerhalb MaxGapDistanceAtr vom aktuellen Kurs
// 5. H1-Confluence: Gap in gleicher Richtung → Trend-RRR statt MR-RRR
// 6. H4-EMA: harter Filter — verhindert Counter-Trend-Trades auf Tages-Ebene
// 7. HTF-EMA-Bias (H1): Soft-Filter (kombiniert mit H4)
// 8. Regime-Anpassung: HighVol → nur FVG/Weekend, LowVol → Prob +0.05
// ═══════════════════════════════════════════════════════════════════════════════

#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Globalization;
using System.Reflection;
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
        public int SessionBucket;  // 0=Asia(0-7), 1=London(7-13), 2=NY(13-20), 3=Off(20-24)

        public override bool Equals(object obj) => obj is SessionKey other && Equals(other);
        public bool Equals(SessionKey other) => DoW == other.DoW && SessionBucket == other.SessionBucket;
        public override int GetHashCode() => (int)DoW * 10 + SessionBucket;
    }

    internal struct SessionStats
    {
        public int TradeCount;
        public int Wins;
        public double PnLSum;
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
    }

    [Robot("GapMasterAlgo", AccessRights = AccessRights.None)]
    public class GapMasterAlgo : Robot
    {
        // ════════════════════════════════════════════════════════════════════
        //  CONSTANTS
        // ════════════════════════════════════════════════════════════════════
        private const double SPREAD_FLOOR_PIPS      = 1.0;
        private const double CHANDELIER_SL_CAP_PIPS = 2.0;
        private const int    TRADING_DAYS_PER_YEAR  = 252;

        private static readonly Dictionary<string, object> CANONICAL_DEFAULTS = new Dictionary<string, object>
        {
            { "RiskPerTradePct",              1.0  },
            { "MaxDailyDdPct",                5.0  },
            { "MaxWeeklyDdPct",               10.0 },
            { "MaxFloatingDailyDdPct",        6.0  },
            { "MaxTradesPerDay",              3    },
            { "ConsecLossCoolDownHours",      4.0  },
            { "EquityCurveTrailPct",          15.0 },
            { "MaxLossPerTradeUsd",           4.0  },
            { "RegimeRiskMultLowVol",         0.5  },
            { "RegimeRiskMultNormal",         1.2  },
            { "RegimeRiskMultHighVol",        0.8  },
            { "AtrPeriod",                    14   },
            { "AtrSlMultiplier",              1.8  },
            { "SessionStartHour",             7    },
            { "SessionEndHour",               20   },
            { "MaxMarginUtilizationPct",      30.0 },
            { "RegimeHysteresisBand",         0.05 },
            { "RrrTrend",                     2.5  },
            { "RrrMR",                        1.3  },
            { "RrrBreakout",                  2.0  },
            { "AtrRatioLow",                  0.80 },
            { "AtrRatioHigh",                 1.30 },
            { "ServerTimeIsUtc",              true },
        };

        // ════════════════════════════════════════════════════════════════════
        //  PARAMETERS
        // ════════════════════════════════════════════════════════════════════

        // ── 00 · Core ────────────────────────────────────────────────────────
        [Parameter("Bot Label", Group = "00 · Core", DefaultValue = "GapMasterAlgo")]
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
            DefaultValue = 0.5, MinValue = 0.25, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultLowVol { get; set; }

        [Parameter("Regime Risk Mult – Normal", Group = "00 · Core",
            DefaultValue = 1.2, MinValue = 0.5, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultNormal { get; set; }

        [Parameter("Regime Risk Mult – HighVol", Group = "00 · Core",
            DefaultValue = 0.8, MinValue = 0.25, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultHighVol { get; set; }

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

        [Parameter("Treat Server.Time as UTC (warns if delta > 30min)", Group = "02 · Time & Session",
            DefaultValue = true)]
        public bool ServerTimeIsUtc { get; set; }

        // ── 03 · HTF Regime Filter ───────────────────────────────────────────
        [Parameter("HTF TimeFrame", Group = "03 · HTF Regime", DefaultValue = "Hour1")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("HTF EMA Period", Group = "03 · HTF Regime",
            DefaultValue = 50, MinValue = 5, MaxValue = 500)]
        public int HtfEmaPeriod { get; set; }

        [Parameter("HTF Buffer (Pips, bias zone)", Group = "03 · HTF Regime",
            DefaultValue = 3.0, MinValue = 0.0, Step = 0.1)]
        public double HtfBufferPips { get; set; }

        // ── 04 · Volatility / Regime ─────────────────────────────────────────
        [Parameter("ATR Period (base)", Group = "04 · Volatility",
            DefaultValue = 14, MinValue = 3, MaxValue = 100)]
        public int AtrPeriod { get; set; }

        [Parameter("Daily ATR Ratio LOW (atr/median ≤ X = LowVol)", Group = "04 · Volatility",
            DefaultValue = 0.80, MinValue = 0.1, MaxValue = 2.0, Step = 0.05)]
        public double AtrRatioLow { get; set; }

        [Parameter("Daily ATR Ratio HIGH (atr/median ≥ X = HighVol)", Group = "04 · Volatility",
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

        [Parameter("Spread / ATR max ratio (dyn cap)", Group = "05 · Spread",
            DefaultValue = 0.15, MinValue = 0.01, MaxValue = 1.0, Step = 0.01)]
        public double SpreadAtrRatio { get; set; }

        // ── 09 · Stops & Targets ─────────────────────────────────────────────
        [Parameter("ATR SL Multiplier", Group = "09 · Stops & Targets",
            DefaultValue = 1.8, MinValue = 0.3, MaxValue = 6.0, Step = 0.1)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("Min SL Pips", Group = "09 · Stops & Targets",
            DefaultValue = 6.0, MinValue = 1.0, Step = 0.5)]
        public double MinSlPips { get; set; }

        [Parameter("Max SL Pips", Group = "09 · Stops & Targets",
            DefaultValue = 60.0, MinValue = 5.0, Step = 1.0)]
        public double MaxSlPips { get; set; }

        [Parameter("RRR – Trend Setup", Group = "09 · Stops & Targets",
            DefaultValue = 2.5, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double RrrTrend { get; set; }

        [Parameter("RRR – MR Setup", Group = "09 · Stops & Targets",
            DefaultValue = 1.3, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double RrrMR { get; set; }

        [Parameter("RRR – Breakout Setup", Group = "09 · Stops & Targets",
            DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double RrrBreakout { get; set; }

        // ── 10 · Trade Management ────────────────────────────────────────────
        [Parameter("Enable BreakEven", Group = "10 · Trade Mgmt", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("BreakEven Trigger (R)", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.1, Step = 0.1)]
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

        [Parameter("Enable Partial 2", Group = "10 · Trade Mgmt", DefaultValue = false)]
        public bool EnablePartial2 { get; set; }

        [Parameter("Partial 2 – Trigger (R)", Group = "10 · Trade Mgmt",
            DefaultValue = 2.0, MinValue = 0.5, Step = 0.1)]
        public double Partial2TriggerR { get; set; }

        [Parameter("Partial 2 – Close Fraction", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.05, MaxValue = 0.9, Step = 0.05)]
        public double Partial2Fraction { get; set; }

        [Parameter("Enable Chandelier Trail (after Partial1)", Group = "10 · Trade Mgmt", DefaultValue = false)]
        public bool EnableChandelier { get; set; }

        [Parameter("Chandelier ATR Multiplier", Group = "10 · Trade Mgmt",
            DefaultValue = 2.5, MinValue = 0.5, MaxValue = 10.0, Step = 0.1)]
        public double ChandelierAtrMult { get; set; }

        [Parameter("Max Hold Bars (0 = off)", Group = "10 · Trade Mgmt",
            DefaultValue = 24, MinValue = 0, MaxValue = 500)]
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
            DefaultValue = 3, MinValue = 1, MaxValue = 50)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Consec Losses – Cool-Down Hours (0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 4.0, MinValue = 0.0, MaxValue = 48.0, Step = 0.5)]
        public double ConsecLossCoolDownHours { get; set; }

        [Parameter("Consec Losses – Trigger", Group = "11 · Risk Gates",
            DefaultValue = 4, MinValue = 1, MaxValue = 10)]
        public int ConsecLossTrigger { get; set; }

        [Parameter("Persist Streak Counter Across Restarts", Group = "11 · Risk Gates", DefaultValue = false)]
        public bool PersistStreakCounter { get; set; }

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

        // ── 12 · Vol-Targeted Sizing ─────────────────────────────────────────
        [Parameter("Enable Vol-Targeted Sizing", Group = "12 · Sizing", DefaultValue = false)]
        public bool EnableVolTargetSizing { get; set; }

        [Parameter("Baseline ATR Pips (reference)", Group = "12 · Sizing",
            DefaultValue = 12.0, MinValue = 1.0, Step = 0.5)]
        public double VolTargetBaselineAtrPips { get; set; }

        [Parameter("Max Margin Utilization (%)", Group = "12 · Sizing",
            DefaultValue = 30.0, MinValue = 5.0, MaxValue = 95.0, Step = 5.0)]
        public double MaxMarginUtilizationPct { get; set; }

        // ── 13 · Logging ─────────────────────────────────────────────────────
        [Parameter("Verbose Logs", Group = "13 · Logging", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Persist Attribution JSON (Live only)", Group = "13 · Logging", DefaultValue = false)]
        public bool EnableAttributionPersistence { get; set; }

        [Parameter("Lock Parameters (warn on canonical drift)", Group = "13 · Logging", DefaultValue = false)]
        public bool LockParameters { get; set; }

        [Parameter("Export trade log CSV on Stop", Group = "13 · Logging", DefaultValue = false)]
        public bool ExportTradeLogCsv { get; set; }

        [Parameter("Parameter Set ID (for WF/version tracking)", Group = "13 · Logging", DefaultValue = "v1.0.0-mr-cons")]
        public string ParameterSetId { get; set; }

        // ── 20 · GapMaster ───────────────────────────────────────────────────
        [Parameter("Min Probability Threshold (M15)", Group = "20 · GapMaster",
            DefaultValue = 0.40, MinValue = 0.10, MaxValue = 0.90, Step = 0.01)]
        public double MinProbThreshold { get; set; }

        [Parameter("Min Probability Threshold (H1 standalone)", Group = "20 · GapMaster",
            DefaultValue = 0.45, MinValue = 0.10, MaxValue = 0.90, Step = 0.01)]
        public double MinProbH1Standalone { get; set; }

        [Parameter("Min RRR for Gap-TP (reject if gap too close)", Group = "20 · GapMaster",
            DefaultValue = 0.8, MinValue = 0.3, MaxValue = 3.0, Step = 0.1)]
        public double MinGapRrr { get; set; }

        [Parameter("Max Gap Distance (ATR mult)", Group = "20 · GapMaster",
            DefaultValue = 3.0, MinValue = 0.5, MaxValue = 10.0, Step = 0.25)]
        public double MaxGapDistanceAtr { get; set; }

        [Parameter("Max Gap TP Distance (ATR mult, >x → ATR fallback)", Group = "20 · GapMaster",
            DefaultValue = 5.0, MinValue = 1.0, MaxValue = 20.0, Step = 0.5)]
        public double MaxGapTpAtr { get; set; }

        [Parameter("Gap Distance Decay (ATR, higher = slower prob decay)", Group = "20 · GapMaster",
            DefaultValue = 15.0, MinValue = 1.0, MaxValue = 50.0, Step = 0.5)]
        public double GapDistanceDecay { get; set; }

        [Parameter("Trade FVG Gaps", Group = "20 · GapMaster", DefaultValue = true)]
        public bool TradeFvg { get; set; }

        [Parameter("Trade Weekend Gaps", Group = "20 · GapMaster", DefaultValue = true)]
        public bool TradeWeekend { get; set; }

        [Parameter("Trade Session Gaps", Group = "20 · GapMaster", DefaultValue = false)]
        public bool TradeSession { get; set; }

        [Parameter("Trade Liquidity Gaps", Group = "20 · GapMaster", DefaultValue = false)]
        public bool TradeLiquidity { get; set; }

        [Parameter("Trade News Gaps (risky!)", Group = "20 · GapMaster", DefaultValue = false)]
        public bool TradeNews { get; set; }

        [Parameter("Only Confirmed Gaps (no live-bar tentative)", Group = "20 · GapMaster", DefaultValue = true)]
        public bool OnlyConfirmed { get; set; }

        [Parameter("H4 EMA Period (structural filter)", Group = "20 · GapMaster",
            DefaultValue = 50, MinValue = 5, MaxValue = 200)]
        public int H4EmaPeriod { get; set; }

        [Parameter("H4 EMA Buffer (Pips)", Group = "20 · GapMaster",
            DefaultValue = 5.0, MinValue = 0.0, Step = 0.5)]
        public double H4BufferPips { get; set; }

        [Parameter("Enable H4 Hard Filter (block counter-trend)", Group = "20 · GapMaster", DefaultValue = true)]
        public bool EnableH4Filter { get; set; }

        [Parameter("Enable H1 Gap Confluence Layer", Group = "20 · GapMaster", DefaultValue = true)]
        public bool EnableH1Confluence { get; set; }

        [Parameter("Enable H1 Standalone Signals", Group = "20 · GapMaster", DefaultValue = false)]
        public bool EnableH1Standalone { get; set; }

        [Parameter("H1 GapRadar – Min Gap ATR mult", Group = "20 · GapMaster",
            DefaultValue = 0.08, MinValue = 0.01, MaxValue = 0.5, Step = 0.01)]
        public double H1GapMinAtr { get; set; }

        [Parameter("H1 GapRadar – Fill Window (Bars)", Group = "20 · GapMaster",
            DefaultValue = 80, MinValue = 10, MaxValue = 500)]
        public int H1FillWindow { get; set; }

        [Parameter("FVG: Place SL behind gap edge (tighter stop)", Group = "20 · GapMaster", DefaultValue = true)]
        public bool FvgEdgeSl { get; set; }

        [Parameter("FVG Edge SL Buffer (Pips beyond gap edge)", Group = "20 · GapMaster",
            DefaultValue = 1.0, MinValue = 0.0, MaxValue = 10.0, Step = 0.1)]
        public double FvgEdgeSlBuffer { get; set; }

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE STATE
        // ════════════════════════════════════════════════════════════════════
        private Bars             _htfBars;
        private Bars             _dailyBars;
        private MovingAverage    _htfEma;
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
        private DateTime _regimeChangeBar    = DateTime.MinValue;
        private double   _runHighWaterMarkBalance;

        private List<(DateTime center, int halfMinutes)> _newsBlackouts = new List<(DateTime, int)>();
        private List<string> _tradeLogRows                              = new List<string>();
        private TradeRegime  _tradeOpenRegime                           = TradeRegime.Normal;

        private Dictionary<DateTime, double> _dailyRSum        = new Dictionary<DateTime, double>();
        private Dictionary<DateTime, double> _dailyEquityClose = new Dictionary<DateTime, double>();

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

        // ── GapMaster strategy state ────────────────────────────────────────
        private cAlgo.Indicators.GapRadar _gapM15;
        private cAlgo.Indicators.GapRadar _gapH1;
        private Bars                      _h1Bars;
        private Bars                      _h4Bars;
        private MovingAverage             _h4Ema;

        // ════════════════════════════════════════════════════════════════════
        //  ON START
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            Print("╔══════════════════════════════════════════════╗");
            Print("║   GapMasterAlgo ({0,-22}) ║", ParameterSetId);
            Print("║   M15 primary │ H1 confluence │ H4 filter    ║");
            Print("╚══════════════════════════════════════════════╝");
            _startTime = Server.Time;

            if (!IsBacktesting)
            {
                double serverUtcDeltaMin = Math.Abs((Server.Time - DateTime.UtcNow).TotalMinutes);
                if (serverUtcDeltaMin > 30)
                    Print("WARNING: Server.Time deviates from system UTC by {0:F1}min — session/news gates may be off", serverUtcDeltaMin);
                if (!ServerTimeIsUtc)
                    Print("CRITICAL: ServerTimeIsUtc=false — all hour-based gates use server local time, NOT UTC.");
            }

            if (!ValidateParameters())
            {
                Print("CRITICAL: parameter validation failed — bot will idle.");
                return;
            }

            _atr           = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _atrChandelier = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);

            _htfBars = MarketData.GetBars(HtfTimeFrame, SymbolName);
            if (_htfBars == null)
                Print("WARNING: Failed to load {0} {1} bars — HTF EMA filter disabled (neutral bias).", HtfTimeFrame, SymbolName);
            else
            {
                WarmupBars(_htfBars, HtfEmaPeriod + 50, "HTF");
                _htfEma = Indicators.MovingAverage(_htfBars.ClosePrices, HtfEmaPeriod, MovingAverageType.Exponential);
            }

            _dailyBars = MarketData.GetBars(TimeFrame.Daily, SymbolName);
            if (_dailyBars == null)
                Print("WARNING: Failed to load Daily {0} bars — regime detection disabled (last regime held).", SymbolName);
            else
            {
                WarmupBars(_dailyBars, MedianLookbackDays + 10, "Daily");
                _dailyAtr = Indicators.AverageTrueRange(_dailyBars, AtrPeriod, MovingAverageType.WilderSmoothing);
            }

            // ── GapMaster strategy init (formerly OnStartStrategy hook) ─────
            _gapM15 = Indicators.GetIndicator<cAlgo.Indicators.GapRadar>(
                Bars,
                5000, 14, MovingAverageType.Simple,
                0.08, 200, 3, true,
                true, true, true, true, true,
                1.8, 0, 7, 13, "",
                GapDistanceDecay, 500
            );

            if (EnableH1Confluence || EnableH1Standalone)
            {
                _h1Bars = MarketData.GetBars(TimeFrame.Hour, SymbolName);
                if (_h1Bars == null)
                {
                    Print("WARNING: Failed to load H1 {0} bars — H1 confluence/standalone disabled.", SymbolName);
                    EnableH1Confluence = false;
                    EnableH1Standalone = false;
                }
                else
                {
                    WarmupBars(_h1Bars, 300, "H1");

                    _gapH1 = Indicators.GetIndicator<cAlgo.Indicators.GapRadar>(
                        _h1Bars,
                        1000, 14, MovingAverageType.Simple,
                        H1GapMinAtr, H1FillWindow, 3, true,
                        true, true, true, true, true,
                        1.8, 0, 7, 13, "",
                        GapDistanceDecay, 500
                    );
                }
            }

            if (EnableH4Filter)
            {
                _h4Bars = MarketData.GetBars(TimeFrame.Hour4, SymbolName);
                if (_h4Bars == null)
                {
                    Print("WARNING: Failed to load H4 {0} bars — H4 filter disabled.", SymbolName);
                    EnableH4Filter = false;
                }
                else
                {
                    WarmupBars(_h4Bars, H4EmaPeriod + 50, "H4");
                    _h4Ema = Indicators.MovingAverage(_h4Bars.ClosePrices, H4EmaPeriod, MovingAverageType.Exponential);
                }
            }

            Print("GapMaster init complete. H1-Confluence={0} H4-Filter={1}",
                EnableH1Confluence, EnableH4Filter);

            ParseNewsBlackout();
            if (PersistStreakCounter) LoadStreakFromStorage();

            _dayStartBalance         = Account.Balance;
            _weekStartBalance        = Account.Balance;
            _dayRealizedPnl          = 0;
            _weekRealizedPnl         = 0;
            _botDay                  = Server.Time.Date;
            _botWeek                 = GetWeekMonday(Server.Time);
            _runHighWaterMarkBalance = Account.Balance;

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            RecoverExistingPosition();

            Print("Symbol={0} TF={1} HTF={2} EMA={3} | Risk={4:F2}% base={5}",
                SymbolName, TimeFrame, HtfTimeFrame, HtfEmaPeriod, RiskPerTradePct, RiskBase);
            Print("Gates: DailyDD={0:F1}% WeeklyDD={1:F1}% FloatingDD={2:F1}% MaxTrades={3} HardLoss=${4:F0}",
                MaxDailyDdPct, MaxWeeklyDdPct, MaxFloatingDailyDdPct, MaxTradesPerDay, MaxLossPerTradeUsd);

            if (LockParameters) ValidateParameterLock();
        }

        protected override void OnStop()
        {
            Positions.Opened -= OnPositionOpened;
            Positions.Closed -= OnPositionClosed;

            if (_botDay != DateTime.MinValue)
                _dailyEquityClose[_botDay] = Account.Balance;

            TimeSpan runtime = Server.Time - _startTime;
            Print("╔══════════════════════════════════════════════╗");
            Print("║   GapMasterAlgo Stopped                      ║");
            Print("╚══════════════════════════════════════════════╝");
            Print("  Runtime    : {0:dd\\d\\ hh\\h\\ mm\\m}", runtime);
            Print("  Balance    : {0:F2} {1}", Account.Balance, Account.Asset.Name);
            Print("  Trades     : {0}", _totalTradesOpened);
            Print("  ParamSetId : {0}", ParameterSetId);

            EmitAttribution();
            PersistAttributionJson();
            ExportTradeLogCsvFile();
        }

        // ════════════════════════════════════════════════════════════════════
        //  ON BAR  —  signal evaluation gate, then GapMaster entry logic
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

                if (Bars.Count < AtrPeriod + 5) return;

                if (_currentTrade != null && _currentTrade.InitialSlPips > 0)
                {
                    var pos = Positions.FindById(_currentTrade.PositionId);
                    if (pos != null)
                    {
                        double favR    = ComputeBarMaxR(pos, _currentTrade.InitialSlPips);
                        double advR    = ComputeBarMinR(pos, _currentTrade.InitialSlPips);
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
                int htfBias        = GetHtfBias();

                TryEnterGapMaster(regime, htfBias);
            }
            catch (Exception ex)
            {
                Print("ERROR OnBar: {0} | {1}", ex.Message, ex.StackTrace);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  ON TICK  —  trade management
        // ════════════════════════════════════════════════════════════════════
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
        //  GAPMASTER STRATEGY  —  entry logic
        // ════════════════════════════════════════════════════════════════════
        private bool TryEnterGapMaster(TradeRegime regime, int htfBias)
        {
            if (_gapM15 == null) return false;
            if (Bars.Count < 20) return false;

            double currentClose = Bars.ClosePrices.Last(1);
            double atrPips      = GetAtrPips();
            if (atrPips <= 0) return false;

            int h4Bias = GetH4Bias();

            if (Verbose)
            {
                int activeCount = _gapM15.ActiveGaps?.Count ?? 0;
                double thresh   = GetEffectiveProbThreshold(regime, isH1: false);
                Print("GAP-SCAN: M15 active={0} regime={1} thresh={2:F2} h4={3} htf={4} price={5:F5} atr={6:F1}p",
                    activeCount, regime, thresh, h4Bias, htfBias, currentClose, atrPips);
            }

            var m15Signal = FindBestGapSignal(
                _gapM15, currentClose, atrPips, regime,
                GetEffectiveProbThreshold(regime, isH1: false));

            GapSignal h1Signal = null;
            if (_gapH1 != null && (EnableH1Confluence || EnableH1Standalone))
            {
                double h1Close   = _h1Bars.ClosePrices.Last(1);
                double h1AtrPips = GetH1AtrPips();
                h1Signal = FindBestGapSignal(
                    _gapH1, h1Close,
                    h1AtrPips > 0 ? h1AtrPips : atrPips,
                    regime,
                    GetEffectiveProbThreshold(regime, isH1: true));
            }

            bool triedEntry = false;

            if (m15Signal != null && h1Signal != null && m15Signal.Direction == h1Signal.Direction)
            {
                int dir = m15Signal.Direction;
                if (IsDirectionAllowed(dir, h4Bias, htfBias))
                {
                    string reason = $"M15+H1 confluence {m15Signal.GapType}/{h1Signal.GapType} " +
                                    $"prob={m15Signal.Prob:F2}+{h1Signal.Prob:F2}";
                    triedEntry = TryOpenGapTrade(
                        dir == 1 ? TradeType.Buy : TradeType.Sell,
                        m15Signal, "GapMTF", TradeSetupKind.Trend, RrrTrend, reason);
                }
            }

            if (!triedEntry && m15Signal != null)
            {
                int dir = m15Signal.Direction;
                if (IsDirectionAllowed(dir, h4Bias, htfBias))
                {
                    string reason = $"M15 solo {m15Signal.GapType} " +
                                    $"prob={m15Signal.Prob:F2} dist={m15Signal.DistAtr:F2}ATR";
                    triedEntry = TryOpenGapTrade(
                        dir == 1 ? TradeType.Buy : TradeType.Sell,
                        m15Signal, "GapM15", TradeSetupKind.MeanReversion, RrrMR, reason);
                }
            }

            if (!triedEntry && EnableH1Standalone && h1Signal != null
                && h1Signal.Prob >= MinProbH1Standalone && m15Signal == null)
            {
                int dir = h1Signal.Direction;
                if (IsDirectionAllowed(dir, h4Bias, htfBias))
                {
                    string reason = $"H1 standalone {h1Signal.GapType} prob={h1Signal.Prob:F2}";
                    triedEntry = TryOpenGapTrade(
                        dir == 1 ? TradeType.Buy : TradeType.Sell,
                        h1Signal, "GapH1", TradeSetupKind.MeanReversion, RrrMR, reason);
                }
            }

            return triedEntry;
        }

        private GapSignal FindBestGapSignal(
            cAlgo.Indicators.GapRadar radar,
            double currentPrice,
            double atrPips,
            TradeRegime regime,
            double probThreshold)
        {
            if (radar.ActiveGaps == null || radar.ActiveGaps.Count == 0)
                return null;

            double atrPrice = atrPips * Symbol.PipSize;
            if (atrPrice <= 0) return null;

            GapSignal best = null;

            foreach (var gap in radar.ActiveGaps)
            {
                if (OnlyConfirmed && !gap.IsConfirmed)
                {
                    if (Verbose) Print("  GAP skip bar={0} type={1}: not confirmed", gap.CreatedBarIndex, gap.Type);
                    continue;
                }
                if (!IsGapTypeAllowed(gap.Type, regime))
                {
                    if (Verbose) Print("  GAP skip bar={0} type={1}: type not allowed (regime={2})", gap.CreatedBarIndex, gap.Type, regime);
                    continue;
                }
                if (gap.HitProbability < probThreshold)
                {
                    if (Verbose) Print("  GAP skip bar={0} type={1}: prob={2:F3} < thresh={3:F3}", gap.CreatedBarIndex, gap.Type, gap.HitProbability, probThreshold);
                    continue;
                }

                double mid     = (gap.Top + gap.Bottom) / 2.0;
                double distAtr = Math.Abs(currentPrice - mid) / atrPrice;

                if (distAtr > MaxGapDistanceAtr)
                {
                    if (Verbose) Print("  GAP skip bar={0} type={1}: dist={2:F2}ATR > max={3:F2}", gap.CreatedBarIndex, gap.Type, distAtr, MaxGapDistanceAtr);
                    continue;
                }

                int direction = mid > currentPrice ? 1 : -1;

                if (best == null || gap.HitProbability > best.Prob)
                {
                    best = new GapSignal
                    {
                        GapMid      = mid,
                        GapTop      = gap.Top,
                        GapBottom   = gap.Bottom,
                        Prob        = gap.HitProbability,
                        GapType     = gap.Type.ToString(),
                        Direction   = direction,
                        DistAtr     = distAtr,
                        SizeInAtr   = gap.SizeInAtr,
                        IsConfirmed = gap.IsConfirmed
                    };
                }
            }

            return best;
        }

        private bool TryOpenGapTrade(
            TradeType tradeType,
            GapSignal signal,
            string edgeLabel,
            TradeSetupKind setup,
            double rrr,
            string reason)
        {
            double atrPips  = GetAtrPips();

            double slPips = Math.Round(atrPips * AtrSlMultiplier + CommissionBufferPips, 1);
            slPips = Math.Max(MinSlPips, Math.Min(MaxSlPips, slPips));

            if (FvgEdgeSl && signal.GapType == "FVG")
            {
                double edgePips = tradeType == TradeType.Buy
                    ? (Bars.ClosePrices.Last(1) - signal.GapBottom) / Symbol.PipSize + FvgEdgeSlBuffer
                    : (signal.GapTop - Bars.ClosePrices.Last(1)) / Symbol.PipSize + FvgEdgeSlBuffer;

                if (edgePips > 0 && edgePips < slPips)
                    slPips = Math.Max(MinSlPips, edgePips);
            }

            double distToMid = Math.Abs(signal.GapMid - Bars.ClosePrices.Last(1));
            double tpPips    = distToMid / Symbol.PipSize;

            if (tpPips < slPips * MinGapRrr)
            {
                if (Verbose)
                    Print("REJECT {0}: gap-TP too close ({1:F1}p < {2:F2}*SL={3:F1}p)",
                        edgeLabel, tpPips, MinGapRrr, slPips * MinGapRrr);
                return false;
            }

            bool useGapTp = tpPips <= atrPips * MaxGapTpAtr;
            double effectiveRrr;
            if (useGapTp)
            {
                tpPips = Math.Round(tpPips, 1);
                effectiveRrr = tpPips / slPips;
            }
            else
            {
                tpPips = Math.Round(slPips * rrr, 1);
                effectiveRrr = rrr;
                if (Verbose)
                    Print("INFO {0}: gap-TP capped, ATR fallback TP={1:F1}p", edgeLabel, tpPips);
            }

            bool opened = OpenTrade(tradeType, edgeLabel, setup, effectiveRrr,
                $"{reason} | GapMid={signal.GapMid:F5} TP={tpPips:F1}p SL={slPips:F1}p prob={signal.Prob:F2}");

            if (opened && useGapTp)
                OverrideTpToGapMidpoint(tradeType, signal.GapMid);

            return opened;
        }

        private void OverrideTpToGapMidpoint(TradeType tradeType, double gapMid)
        {
            var pos = Positions
                .Where(p => p.Label == BotLabel && p.SymbolName == SymbolName)
                .OrderByDescending(p => p.EntryTime)
                .FirstOrDefault();

            if (pos == null) return;

            bool tpValid = tradeType == TradeType.Buy
                ? gapMid > pos.EntryPrice
                : gapMid < pos.EntryPrice;

            if (!tpValid)
            {
                if (Verbose)
                    Print("WARN: Gap midpoint {0:F5} wrong side of entry {1:F5} — keeping ATR TP",
                        gapMid, pos.EntryPrice);
                return;
            }

            var result = ModifyPosition(pos, pos.StopLoss, gapMid);
            if (result.IsSuccessful && Verbose)
                Print("TP overridden to gap midpoint {0:F5}", gapMid);
            else if (!result.IsSuccessful)
                Print("WARN: TP override failed: {0}", result.Error);
        }

        private int GetH4Bias()
        {
            if (!EnableH4Filter || _h4Bars == null || _h4Ema == null) return 0;
            if (_h4Bars.Count < H4EmaPeriod + 5) return 0;

            double close = _h4Bars.ClosePrices.Last(1);
            double ema   = _h4Ema.Result.Last(1);
            if (double.IsNaN(close) || double.IsNaN(ema)) return 0;

            double buf = H4BufferPips * Symbol.PipSize;
            if (close > ema + buf) return 1;
            if (close < ema - buf) return -1;
            return 0;
        }

        private bool IsDirectionAllowed(int direction, int h4Bias, int htfBias)
        {
            if (EnableH4Filter)
            {
                if (direction == 1 && h4Bias == -1)
                {
                    if (Verbose) Print("REJECT: Long blocked by H4 bearish bias");
                    return false;
                }
                if (direction == -1 && h4Bias == 1)
                {
                    if (Verbose) Print("REJECT: Short blocked by H4 bullish bias");
                    return false;
                }
            }

            if (htfBias != 0 && htfBias != direction)
            {
                if (h4Bias != 0 && h4Bias != direction)
                {
                    if (Verbose)
                        Print("REJECT: Combined H4+HTF bias block (h4={0} htf={1} dir={2})",
                            h4Bias, htfBias, direction);
                    return false;
                }
            }

            return true;
        }

        private double GetEffectiveProbThreshold(TradeRegime regime, bool isH1)
        {
            double baseThresh = isH1 ? MinProbH1Standalone : MinProbThreshold;
            return regime switch
            {
                TradeRegime.LowVol  => baseThresh + 0.05,
                TradeRegime.HighVol => baseThresh - 0.03,
                _                   => baseThresh
            };
        }

        private bool IsGapTypeAllowed(cAlgo.Indicators.GapType type, TradeRegime regime)
        {
            if (regime == TradeRegime.HighVol)
            {
                bool allowed = type == cAlgo.Indicators.GapType.FVG
                            || type == cAlgo.Indicators.GapType.Weekend;
                if (!allowed) return false;
            }

            return type switch
            {
                cAlgo.Indicators.GapType.FVG       => TradeFvg,
                cAlgo.Indicators.GapType.Weekend   => TradeWeekend,
                cAlgo.Indicators.GapType.Session   => TradeSession,
                cAlgo.Indicators.GapType.Liquidity => TradeLiquidity,
                cAlgo.Indicators.GapType.News      => TradeNews,
                _                                  => false
            };
        }

        private double GetH1AtrPips()
        {
            if (_h1Bars == null || _h1Bars.Count < 20) return 0;
            double sumTr = 0;
            int n = Math.Min(14, _h1Bars.Count - 1);
            for (int i = 1; i <= n; i++)
            {
                double h  = _h1Bars.HighPrices.Last(i);
                double l  = _h1Bars.LowPrices.Last(i);
                double c  = _h1Bars.ClosePrices.Last(i + 1 < _h1Bars.Count ? i + 1 : i);
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - c), Math.Abs(l - c)));
                sumTr += tr;
            }
            return (sumTr / n) / Symbol.PipSize;
        }

        private sealed class GapSignal
        {
            public double GapMid      { get; set; }
            public double GapTop      { get; set; }
            public double GapBottom   { get; set; }
            public double Prob        { get; set; }
            public string GapType     { get; set; } = "";
            public int    Direction   { get; set; }
            public double DistAtr     { get; set; }
            public double SizeInAtr   { get; set; }
            public bool   IsConfirmed { get; set; }
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
            if (Partial2TriggerR <= Partial1TriggerR && EnablePartial2)
            {
                Print("CRITICAL: Partial2TriggerR ({0}) <= Partial1TriggerR ({1})",
                    Partial2TriggerR, Partial1TriggerR);
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
            return ok;
        }

        private void ValidateParameterLock()
        {
            Print("──── Parameter Lock Active ────");
            int divergences = 0;
            foreach (var canonical in CANONICAL_DEFAULTS)
            {
                object current = GetType().GetProperty(canonical.Key)?.GetValue(this);
                if (current != null && !current.Equals(canonical.Value))
                {
                    Print("DIVERGENCE: {0} = {1} (canonical: {2})", canonical.Key, current, canonical.Value);
                    divergences++;
                }
            }
            if (divergences == 0) Print("All canonical parameters match defaults.");
            else                  Print("WARNING: {0} parameter(s) diverge from canonical set.", divergences);
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
                _lastRegime      = newRegime;
                _regimeChangeBar = Bars.OpenTimes.Last(0);
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
    // ← Guard hinzufügen, analog zu GetH4Bias()
    if (_htfBars == null || _htfEma == null) return 0;
    if (_htfBars.Count < HtfEmaPeriod + 5) return 0;

    double close = _htfBars.ClosePrices.Last(1);
    double ema   = _htfEma.Result.Last(1);
    double buf   = HtfBufferPips * Symbol.PipSize;

    if (double.IsNaN(close) || double.IsNaN(ema)) return 0;

    if (close > ema + buf) return  1;
    if (close < ema - buf) return -1;
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
        private bool OpenTrade(TradeType dir, string edgeLabel, TradeSetupKind setup, double rrr, string reason)
        {
            double atrPips  = GetAtrPips();
            double slPips   = Math.Round(atrPips * AtrSlMultiplier + CommissionBufferPips, 1);
            if (slPips < MinSlPips) slPips = MinSlPips;
            if (slPips > MaxSlPips) slPips = MaxSlPips;
            double tpPips   = Math.Round(slPips * rrr, 1);

            double volume = CalculateVolume(slPips, dir);
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("REJECT: calculated volume below broker min. slPips={0:F1} vol={1:F0}", slPips, volume);
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
            };
            _totalTradesOpened++;
            _tradesToday++;

            Print("FILLED: {0} {1} vol={2:F0} entry={3:F5} slip={4:+0.0;-0.0;0.0}p SL={5:F1}p TP={6:F1}p RRR={7:F2} edge={8} setup={9} | {10}",
                dir, SymbolName, volume, pos.EntryPrice, entrySlippagePips, slPips, tpPips, rrr, edgeLabel, setup, reason);
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
                if (Verbose) Print("RegimeRiskScale: regime={0} mult={1:F2} riskPct {2:F2}->{3:F2}", _lastRegime, regimeMult, prev, riskPct);
            }

            double notionalScale = 1.0;
            if (EnableVolTargetSizing)
            {
                double atrPips = GetAtrPips();
                if (atrPips > 0)
                {
                    notionalScale = VolTargetBaselineAtrPips / atrPips;
                    if (notionalScale < 0.5) notionalScale = 0.5;
                    if (notionalScale > 2.0) notionalScale = 2.0;
                }
            }

            double riskAmount = baseAcct * (riskPct / 100.0);
            double pipValue   = Symbol.PipValue;
            if (pipValue <= 0 || slPips <= 0) return Symbol.VolumeInUnitsMin;
            double exact      = riskAmount / (slPips * pipValue);
            double normalized = Symbol.NormalizeVolumeInUnits(exact, RoundingMode.Down);

            if (EnableVolTargetSizing && Math.Abs(notionalScale - 1.0) > 0.001 && notionalScale > 0)
            {
                double oldVol = normalized;
                normalized = Symbol.NormalizeVolumeInUnits(normalized / notionalScale, RoundingMode.Down);
                if (normalized < Symbol.VolumeInUnitsMin) normalized = Symbol.VolumeInUnitsMin;
                if (Verbose) Print("VolTarget: scale={0:F3} vol {1:F0}->{2:F0}", notionalScale, oldVol, normalized);
            }

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
            double slPips = _currentTrade.InitialSlPips;
            if (slPips <= 0) return;
            double currentR = ComputeCurrentR(pos, slPips);

            if (EnablePartial1 && !_currentTrade.Partial1Done && currentR >= Partial1TriggerR)
            {
                ClosePartial(pos, Partial1Fraction, "Partial1");
                _currentTrade.Partial1Done = true;
            }
            if (EnablePartial2 && !_currentTrade.Partial2Done
                && _currentTrade.Partial1Done
                && currentR >= Partial2TriggerR)
            {
                ClosePartial(pos, Partial2Fraction, "Partial2");
                _currentTrade.Partial2Done = true;
            }
        }

        private void ManagePartialsAtBarClose(Position pos)
        {
            double slPips = _currentTrade.InitialSlPips;
            if (slPips <= 0) return;
            double barMaxR = ComputeBarMaxR(pos, slPips);

            if (EnablePartial1 && !_currentTrade.Partial1Done && barMaxR >= Partial1TriggerR)
            {
                ClosePartial(pos, Partial1Fraction, "Partial1@BarClose");
                _currentTrade.Partial1Done = true;
            }
            if (EnablePartial2 && !_currentTrade.Partial2Done
                && _currentTrade.Partial1Done
                && barMaxR >= Partial2TriggerR)
            {
                ClosePartial(pos, Partial2Fraction, "Partial2@BarClose");
                _currentTrade.Partial2Done = true;
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
            if (!EnableChandelier || !_currentTrade.Partial1Done) return;
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

            if (PersistStreakCounter) SaveStreakToStorage();

            Print("CLOSED: id={0} edge={1} setup={2} pnl={3:F2} slip_e={4:+0.0;-0.0;0.0}p slip_x={5:+0.0;-0.0;0.0}p MAE={6:F2}R MFE={7:F2}R consecLoss={8}",
                pos.Id, edgeLabel, _currentTrade.Setup, pnl,
                _currentTrade.EntrySlippage, _currentTrade.ExitSlippagePips,
                _currentTrade.MaeRMultiple, _currentTrade.MfeRMultiple,
                _consecutiveLosses);

            if (ExportTradeLogCsv)
            {
                SessionKey sk = _currentTrade.EntrySessionKey;
                _tradeLogRows.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss},{2},{3},{4},{5},{6:F5},{7:F5},{8:F1},{9:F2},{10:F0},{11:F2},{12:F4},{13:F2},{14:F2},{15:F4},{16:F4},{17},{18}",
                    _currentTrade.EntryTime, Server.Time, SymbolName,
                    edgeLabel, _currentTrade.Setup, pos.TradeType,
                    _currentTrade.EntryPrice, pos.EntryPrice,
                    _currentTrade.InitialSlPips, _currentTrade.RRRTarget,
                    _currentTrade.InitialVolumeUnits, pnl, rMultiple,
                    _currentTrade.EntrySlippage, _currentTrade.ExitSlippagePips,
                    _currentTrade.MaeRMultiple, _currentTrade.MfeRMultiple,
                    _tradeOpenRegime, SessionBucketName(sk.SessionBucket)));
            }

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
                    Print("DAILY DD HIT: realized={0:F2} ({1:F2}%) >= limit {2:F2}%", _dayRealizedPnl, ddPct, MaxDailyDdPct);
                }
            }
            if (MaxFloatingDailyDdPct > 0 && !_dailyDdBreached)
            {
                double floatingPct = (_dayStartBalance - Account.Equity) / Math.Max(_dayStartBalance, 1) * 100.0;
                if (floatingPct >= MaxFloatingDailyDdPct)
                {
                    _dailyDdBreached = true;
                    ClosePositionIfOpen("FloatingDDHit");
                    Print("FLOATING DAILY DD HIT: equity={0:F2} startBal={1:F2} dd={2:F2}% >= limit {3:F2}%",
                        Account.Equity, _dayStartBalance, floatingPct, MaxFloatingDailyDdPct);
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
                double         recoveredRrr       = RrrTrend;
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
                            recoveredRrr   = setup == TradeSetupKind.Trend         ? RrrTrend
                                           : setup == TradeSetupKind.MeanReversion ? RrrMR
                                           : setup == TradeSetupKind.Breakout      ? RrrBreakout
                                           : RrrTrend;
                            recoveredWithoutMd = false;
                        }
                    }
                }
                if (recoveredWithoutMd) Print("RECOVERY: comment parse failed for id={0} — edge stats excluded", p.Id);

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
                    RRRTarget                = recoveredRrr,
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
                };
                Print("RECOVERY: adopted id={0} {1} entry={2:F5} slPips={3:F1} edge={4} setup={5}",
                    p.Id, p.TradeType, p.EntryPrice, slPips, recoveredEdge, recoveredSetup);
                break;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  TIME / SESSION HELPERS
        // ════════════════════════════════════════════════════════════════════
        private DateTime NowUtc()
        {
            if (IsBacktesting) return Server.Time;
            return ServerTimeIsUtc ? Server.Time : Server.Time.ToUniversalTime();
        }

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
                var sortedR = _rMultiples.OrderBy(x => x).ToList();
                double meanR  = sortedR.Average();
                double medianR = sortedR.Count % 2 == 0
                    ? (sortedR[sortedR.Count / 2 - 1] + sortedR[sortedR.Count / 2]) / 2.0
                    : sortedR[sortedR.Count / 2];
                double p10 = sortedR[(int)(sortedR.Count * 0.10)];
                double p90 = sortedR[(int)(sortedR.Count * 0.90)];
                int    denomN = sortedR.Count < 30 ? sortedR.Count - 1 : sortedR.Count;
                double stdDev = denomN > 0 ? Math.Sqrt(sortedR.Sum(r => Math.Pow(r - meanR, 2)) / denomN) : 0;
                double sumDev = sortedR.Sum(r => Math.Pow(r - meanR, 3));
                double skew   = stdDev > 0 ? sumDev / (sortedR.Count * stdDev * stdDev * stdDev) : 0;

                Print("  n={0,3} mean={1:+0.00;-0.00;0.00} median={2:+0.00;-0.00;0.00} p10={3:+0.00;-0.00;0.00} p90={4:+0.00;-0.00;0.00}",
                    sortedR.Count, meanR, medianR, p10, p90);
                Print("  stddev={0:F2} skew={1:+0.00;-0.00;0.00} expectancy={2:+0.00;-0.00;0.00}R", stdDev, skew, meanR);

                Print("── Performance Metrics (R-based, daily annualised 252d) ──");
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

                if (_dailyRSum.Count < 30)
                {
                    Print("  Sharpe=N/A (insufficient sample: {0}d < 30)", _dailyRSum.Count);
                }
                else
                {
                    var dailyReturns  = _dailyRSum.Values.ToList();
                    double dailyMean  = dailyReturns.Average();
                    int    nDays      = dailyReturns.Count;
                    double dailyStd   = nDays > 1
                        ? Math.Sqrt(dailyReturns.Sum(r => Math.Pow(r - dailyMean, 2)) / (nDays - 1))
                        : 0;
                    double sharpe  = dailyStd > 0 ? dailyMean / dailyStd * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                    var negDaily   = dailyReturns.Where(r => r < 0).ToList();
                    double downDev = negDaily.Count > 0 ? Math.Sqrt(negDaily.Sum(r => r * r) / nDays) : 0;
                    double sortino = downDev > 0 ? dailyMean / downDev * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                    Print("  Sharpe={0:+0.00;-0.00;0.00} Sortino={1:+0.00;-0.00;0.00} MaxDD={2:F2}R PF={3:F2} | N={4}d",
                        sharpe, sortino, maxDdR, pf, nDays);
                }
                Print("  GrossWin={0:F2}R  GrossLoss={1:F2}R", grossWin, grossLoss);
            }

            if (_dailyEquityClose.Count >= 2)
            {
                var sorted = _dailyEquityClose.OrderBy(k => k.Key).ToList();
                var rets   = new List<double>();
                for (int i = 1; i < sorted.Count; i++)
                {
                    double prev = sorted[i - 1].Value;
                    double curr = sorted[i].Value;
                    if (prev > 0) rets.Add((curr - prev) / prev);
                }
                if (rets.Count >= 30)
                {
                    double mean    = rets.Average();
                    double var_    = rets.Sum(r => Math.Pow(r - mean, 2)) / (rets.Count - 1);
                    double std     = Math.Sqrt(var_);
                    double sharpe  = std > 0 ? mean / std * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                    var neg        = rets.Where(r => r < 0).ToList();
                    double downDev = neg.Count > 0 ? Math.Sqrt(neg.Sum(r => r * r) / rets.Count) : 0;
                    double sortino = downDev > 0 ? mean / downDev * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                    Print("── Equity-Returns ($) ───────────────────────────");
                    Print("  Eq-Sharpe={0:+0.00;-0.00;0.00} Eq-Sortino={1:+0.00;-0.00;0.00} | N={2}d", sharpe, sortino, rets.Count);
                }
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
            if (!IsLiveTradingMode())
            {
                if (Verbose) Print("Attribution persistence disabled: not Live mode");
                return;
            }
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine($"  \"bot\": \"{BotLabel}\",");
                sb.AppendLine($"  \"symbol\": \"{SymbolName}\",");
                sb.AppendLine($"  \"runtime\": \"{Server.Time - _startTime:dd\\\\d\\\\ hh\\\\h\\\\ mm\\\\m}\",");
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
                    double avgSlip = _edgeSlippageSampleCount[e] > 0 ? _edgeEntrySlippageSum[e] / _edgeSlippageSampleCount[e] : 0;
                    sb.Append($"    \"{e}\": {{\"n\": {n}, \"wins\": {w}, \"loss\": {l}, \"wr\": {wr:F4}, \"avg_pnl\": {avg:F2}, \"entry_slip\": {avgSlip:F2}}}");
                    sb.AppendLine(i < edges.Count - 1 ? "," : "");
                }
                sb.AppendLine("  }");
                sb.AppendLine("}");

                string filename = $"{BotLabel}.attribution.json";
                string filepath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cAlgo", "Robots", filename);
                System.IO.File.WriteAllText(filepath, sb.ToString(), Encoding.UTF8);
                Print($"Attribution saved: {filepath}");
            }
            catch (Exception ex)
            {
                Print($"ERROR persisting attribution: {ex.Message}");
            }
        }

        private bool IsLiveTradingMode()
        {
            try { return Account.IsLive; }
            catch { return false; }
        }

        private void ExportTradeLogCsvFile()
        {
            if (!ExportTradeLogCsv || _tradeLogRows.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("EntryTime,ExitTime,Symbol,Edge,Setup,Direction,EntryPrice,ExitPrice,SLPips,RRR,VolumeUnits,PnL,RMultiple,EntrySlipP,ExitSlipP,MAE_R,MFE_R,Regime,SessionBucket");
                foreach (string row in _tradeLogRows) sb.AppendLine(row);

                string filename = $"{BotLabel}.tradelog.csv";
                string filepath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "cAlgo", "Robots", filename);
                System.IO.File.WriteAllText(filepath, sb.ToString(), Encoding.UTF8);
                Print("Trade log CSV saved: {0} ({1} rows)", filepath, _tradeLogRows.Count);
            }
            catch (Exception ex)
            {
                Print("ERROR exporting trade log CSV: {0}", ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  STREAK PERSISTENCE
        // ════════════════════════════════════════════════════════════════════
        private string StreakKeyLosses   => $"GapMasterAlgo_ConsecLoss_{BotLabel}_{SymbolName}";
        private string StreakKeyCooldown => $"GapMasterAlgo_CooldownEnd_{BotLabel}_{SymbolName}";

        private void LoadStreakFromStorage()
        {
            try
            {
                string rawLosses   = LocalStorage.GetString(StreakKeyLosses);
                string rawCooldown = LocalStorage.GetString(StreakKeyCooldown);
                if (!string.IsNullOrEmpty(rawLosses) && int.TryParse(rawLosses, out int storedLosses))
                {
                    _consecutiveLosses = storedLosses;
                    if (Verbose) Print("StreakLoad: consecutiveLosses={0}", _consecutiveLosses);
                }
                if (!string.IsNullOrEmpty(rawCooldown) && DateTime.TryParse(rawCooldown, out DateTime storedCd))
                {
                    _cooldownEndTime = storedCd;
                    if (Verbose) Print("StreakLoad: cooldownEnd={0:yyyy-MM-dd HH:mm}", _cooldownEndTime);
                }
            }
            catch (Exception ex) { Print("StreakLoad error: {0}", ex.Message); }
        }

        private void SaveStreakToStorage()
        {
            try
            {
                LocalStorage.SetString(StreakKeyLosses,   _consecutiveLosses.ToString());
                LocalStorage.SetString(StreakKeyCooldown, _cooldownEndTime.ToString("o"));
            }
            catch (Exception ex) { Print("StreakSave error: {0}", ex.Message); }
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
