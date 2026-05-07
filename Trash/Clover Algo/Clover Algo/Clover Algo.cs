// ═══════════════════════════════════════════════════════════════════════════════
//  Clover Algo  │  Multi-Edge Intraday cBot
//  Platform     │  cTrader
//  Version      │  1.2.0
//  Edges        │  (1) Opening Range Breakout + Volatility Compression (NR7/Squeeze)
//                  (2) VWAP Mean-Reversion in Low-ADX (range) regimes
//                  (3) Momentum Continuation aligned with HTF regime
//                  Regime Switch: Daily ATR/Median(20d) picks which edge is "armed".

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
    public enum CloverEdge { ORB, VwapMR, MomoCont }
    public enum CloverRegime { LowVol, Normal, HighVol }
    public enum CloverSetup { Trend, MeanReversion, Breakout }
    public enum CloverRiskBase { Balance, Equity }
    public enum VolTargetMode { AtrBased, StdDevBased }
    public enum VolTargetScaleMode { RiskPctScale, NotionalScale }

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

    internal sealed class CloverTradeState
    {
        public int        PositionId;
        public double     EntryPrice;
        public double     InitialSlPips;
        public double     InitialVolumeUnits;
        public double     RRRTarget;
        public bool       BreakEvenDone;
        public bool       Partial1Done;
        public bool       Partial2Done;
        public double     ChandelierStop;           // long: highest(high) - k*ATR ; short: lowest(low) + k*ATR
        public DateTime   EntryTime;
        public CloverEdge Edge;
        public CloverSetup Setup;
        public double     EntrySlippage;            // pips: intended vs actual entry price
        public double     ExitSlippagePips;         // pips: accumulated exit slippage (partials + force-close)
        public double     ChandelierPeakHigh;       // cached peak high since entry (long)
        public double     ChandelierPeakLow;        // cached peak low since entry (short)
        public DateTime   LastChandelierUpdateTime;
        public int        EntryBarIndex;            // Bars.Count - 1 at entry; for MaxHold O(1)
        public double     MaeRMultiple;             // most adverse R reached during trade (negative = against)
        public double     MfeRMultiple;             // most favorable R reached during trade
        public SessionKey EntrySessionKey;          // session frozen at entry time for accurate attribution
        public bool       RecoveredWithoutMetadata; // true → comment parse failed at recovery; excluded from edge stats
    }

    [Robot("Clover Algo", AccessRights = AccessRights.None)]
    public class CloverAlgo : Robot
    {
        // ════════════════════════════════════════════════════════════════════
        //  CONSTANTS
        // ════════════════════════════════════════════════════════════════════
        private const double MOMO_PULLBACK_TOLERANCE_RATIO = 0.25;
        private const double SPREAD_FLOOR_PIPS             = 0.3;
        private const double CHANDELIER_SL_CAP_PIPS        = 2.0;
        private const int    TRADING_DAYS_PER_YEAR         = 252;

        private static readonly Dictionary<string, object> CANONICAL_DEFAULTS = new Dictionary<string, object>
        {
            // v1.2.0 aggressive-but-calculated defaults (was v1.1.2 conservative)
            { "RiskPerTradePct",              1.0  },  // was 0.5
            { "MaxDailyDdPct",                5.0  },  // was 3.0
            { "MaxWeeklyDdPct",               10.0 },  // was 6.0
            { "MaxFloatingDailyDdPct",        6.0  },  // was 0.0 (off)
            { "MaxTradesPerDay",              6    },  // was 4
            { "ConsecLossCoolDownHours",      4.0  },  // was 0.0 (off)
            { "EquityCurveTrailPct",          15.0 },  // was 0.0 (off)
            { "MaxLossPerTradeUsd",           4.0  },  // was 0.0 (off); 2% hard-cap on $200 acct
            { "RegimeRiskMultLowVol",         0.5  },  // was 1.0
            { "RegimeRiskMultNormal",         1.2  },  // was 1.0
            { "RegimeRiskMultHighVol",        0.8  },  // was 1.0
            // Structural defaults (unchanged from v1.1.2)
            { "AtrPeriod",                    14   },
            { "AtrSlMultiplier",              1.8  },
            { "SessionStartHour",             7    },
            { "SessionEndHour",               20   },
            { "MaxMarginUtilizationPct",      30.0 },
            { "RegimeHysteresisBand",         0.05 },
            { "RrrTrend",                     2.5  },
            { "RrrMR",                        1.3  },
            { "RrrBreakout",                  2.0  },
            { "MomoAdxMin",                   22.0 },
            { "VwapMrAdxMax",                 22.0 },
            { "VwapMrMinDevAtr",              1.2  },
            { "HtfEmaPeriod",                 50   },
            { "OrbDurationMinutes",           60   },
            { "AtrRatioLow",                  0.80 },
            { "AtrRatioHigh",                 1.30 },
            { "OrbRangeLookbackDays",         60   },
            { "OrbRangePercentileMax",        30.0 },
            { "OrbRequireVolumeExpansion",    false },
            { "OrbVolumeAvgBars",             20   },
            { "OrbVolumeMultiplier",          1.3  },
            { "VwapMrUseLimitOrder",          false },
            { "VwapMrLimitOffsetPips",        0.5  },
            { "VwapMrLimitTimeoutBars",       3    },
            { "ExportTradeLogCsv",            false },
            { "ServerTimeIsUtc",              true  },
            { "VolTargetScaleMode",           VolTargetScaleMode.RiskPctScale },
            { "OrbBackfillHistoryOnStart",    true  },
        };

        // ════════════════════════════════════════════════════════════════════
        //  PARAMETERS
        // ════════════════════════════════════════════════════════════════════

        // ── 00 · Core ────────────────────────────────────────────────────────
        [Parameter("Bot Label", Group = "00 · Core", DefaultValue = "CloverAlgo")]
        public string BotLabel { get; set; }

        [Parameter("Risk Base", Group = "00 · Core", DefaultValue = CloverRiskBase.Balance)]
        public CloverRiskBase RiskBase { get; set; }

        [Parameter("Risk per Trade (%)", Group = "00 · Core",
            DefaultValue = 1.0, MinValue = 0.05, MaxValue = 3.0, Step = 0.05)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Commission Buffer (Pips, added to SL)", Group = "00 · Core",
            DefaultValue = 0.6, MinValue = 0.0, Step = 0.1)]
        public double CommissionBufferPips { get; set; }

        [Parameter("Entry Market Range Pips (0 = broker default, caps max slippage)", Group = "00 · Core",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 20.0, Step = 0.1)]
        public double EntryMarketRangePips { get; set; }

        [Parameter("Regime Risk Mult – LowVol (1.0 = no change)", Group = "00 · Core",
            DefaultValue = 0.5, MinValue = 0.25, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultLowVol { get; set; }

        [Parameter("Regime Risk Mult – Normal (1.0 = no change)", Group = "00 · Core",
            DefaultValue = 1.2, MinValue = 0.5, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultNormal { get; set; }

        [Parameter("Regime Risk Mult – HighVol (1.0 = no change)", Group = "00 · Core",
            DefaultValue = 0.8, MinValue = 0.25, MaxValue = 1.5, Step = 0.05)]
        public double RegimeRiskMultHighVol { get; set; }

        // ── 01 · Edges (enable/disable) ──────────────────────────────────────
        [Parameter("Enable ORB (Opening Range Breakout)", Group = "01 · Edges", DefaultValue = true)]
        public bool EnableOrb { get; set; }

        [Parameter("Enable VWAP Mean-Reversion", Group = "01 · Edges", DefaultValue = true)]
        public bool EnableVwapMR { get; set; }

        [Parameter("Enable Momentum Continuation", Group = "01 · Edges", DefaultValue = true)]
        public bool EnableMomoCont { get; set; }

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

        [Parameter("Treat Server.Time as UTC (observational only – warns if deviation > 30min)", Group = "02 · Time & Session",
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

        [Parameter("Regime Hysteresis Band (avoid flicker)", Group = "04 · Volatility",
            DefaultValue = 0.05, MinValue = 0.0, MaxValue = 0.5, Step = 0.01)]
        public double RegimeHysteresisBand { get; set; }

        [Parameter("Skip First Bar After Regime Change", Group = "04 · Volatility", DefaultValue = false)]
        public bool SkipBarAfterRegimeChange { get; set; }

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

        // ── 06 · ORB Params ──────────────────────────────────────────────────
        [Parameter("ORB Start Hour (server UTC)", Group = "06 · ORB",
            DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int OrbStartHour { get; set; }

        [Parameter("ORB Duration Minutes", Group = "06 · ORB",
            DefaultValue = 60, MinValue = 5, MaxValue = 240)]
        public int OrbDurationMinutes { get; set; }

        [Parameter("ORB Breakout Buffer (Pips beyond range)", Group = "06 · ORB",
            DefaultValue = 0.5, MinValue = 0.0, Step = 0.1)]
        public double OrbBufferPips { get; set; }

        [Parameter("ORB require NR7 squeeze (range < last 7d)", Group = "06 · ORB", DefaultValue = true)]
        public bool OrbRequireNr7 { get; set; }

        [Parameter("ORB NR7 Strict Warmup (block until 7 ORB ranges captured)", Group = "06 · ORB", DefaultValue = false)]
        public bool OrbNr7StrictWarmup { get; set; }

        [Parameter("ORB Backfill Range History on Start (pre-fill from historical bars, default ON)", Group = "06 · ORB", DefaultValue = true)]
        public bool OrbBackfillHistoryOnStart { get; set; }

        [Parameter("ORB entry cutoff hour (no entries after)", Group = "06 · ORB",
            DefaultValue = 14, MinValue = 1, MaxValue = 23)]
        public int OrbEntryCutoffHour { get; set; }

        [Parameter("ORB Range Lookback Days (volatility percentile history)", Group = "06 · ORB",
            DefaultValue = 60, MinValue = 7, MaxValue = 252)]
        public int OrbRangeLookbackDays { get; set; }

        [Parameter("ORB Range Percentile Threshold (entry if range ≤ X-th pct of history)", Group = "06 · ORB",
            DefaultValue = 30.0, MinValue = 5.0, MaxValue = 50.0, Step = 1.0)]
        public double OrbRangePercentileMax { get; set; }

        [Parameter("ORB require volume expansion (TickVol > avg × multiplier)", Group = "06 · ORB",
            DefaultValue = false)]
        public bool OrbRequireVolumeExpansion { get; set; }

        [Parameter("ORB volume expansion – avg lookback bars", Group = "06 · ORB",
            DefaultValue = 20, MinValue = 5, MaxValue = 100)]
        public int OrbVolumeAvgBars { get; set; }

        [Parameter("ORB volume expansion – multiplier vs avg", Group = "06 · ORB",
            DefaultValue = 1.3, MinValue = 1.0, MaxValue = 3.0, Step = 0.1)]
        public double OrbVolumeMultiplier { get; set; }

        // ── 07 · VWAP MR Params ──────────────────────────────────────────────
        [Parameter("VWAP: ADX max for MR regime (trade only if ADX <= X)", Group = "07 · VWAP MR",
            DefaultValue = 22.0, MinValue = 5.0, MaxValue = 40.0, Step = 0.5)]
        public double VwapMrAdxMax { get; set; }

        [Parameter("VWAP: min deviation (ATR multiples)", Group = "07 · VWAP MR",
            DefaultValue = 1.2, MinValue = 0.2, MaxValue = 5.0, Step = 0.1)]
        public double VwapMrMinDevAtr { get; set; }

        [Parameter("VWAP: require RSI extreme (<30 long / >70 short)", Group = "07 · VWAP MR",
            DefaultValue = true)]
        public bool VwapMrRequireRsi { get; set; }

        [Parameter("VWAP: RSI Period", Group = "07 · VWAP MR",
            DefaultValue = 14, MinValue = 2, MaxValue = 50)]
        public int VwapMrRsiPeriod { get; set; }

        [Parameter("VWAP: Daily Anchor Hour UTC (0 = midnight reset)", Group = "07 · VWAP MR",
            DefaultValue = 0, MinValue = 0, MaxValue = 23)]
        public int VwapAnchorHourUtc { get; set; }

        [Parameter("VWAP MR use Limit order (passive entry)", Group = "07 · VWAP MR",
            DefaultValue = false)]
        public bool VwapMrUseLimitOrder { get; set; }

        [Parameter("VWAP MR Limit offset pips (entry vs current price)", Group = "07 · VWAP MR",
            DefaultValue = 0.5, MinValue = 0.0, MaxValue = 5.0, Step = 0.1)]
        public double VwapMrLimitOffsetPips { get; set; }

        [Parameter("VWAP MR Limit timeout bars (cancel if not filled)", Group = "07 · VWAP MR",
            DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int VwapMrLimitTimeoutBars { get; set; }

        // ── 08 · Momentum Cont. Params ───────────────────────────────────────
        [Parameter("Momo: EMA Fast", Group = "08 · Momentum",
            DefaultValue = 12, MinValue = 2, MaxValue = 100)]
        public int MomoEmaFast { get; set; }

        [Parameter("Momo: EMA Slow", Group = "08 · Momentum",
            DefaultValue = 26, MinValue = 5, MaxValue = 200)]
        public int MomoEmaSlow { get; set; }

        [Parameter("Momo: ADX min (trade only if ADX >= X)", Group = "08 · Momentum",
            DefaultValue = 22.0, MinValue = 10.0, MaxValue = 50.0, Step = 0.5)]
        public double MomoAdxMin { get; set; }

        [Parameter("Momo: require pullback to fast EMA", Group = "08 · Momentum", DefaultValue = true)]
        public bool MomoRequirePullback { get; set; }

        // ── 09 · Stops & Targets ─────────────────────────────────────────────
        [Parameter("ATR SL Multiplier", Group = "09 · Stops & Targets",
            DefaultValue = 1.8, MinValue = 0.3, MaxValue = 6.0, Step = 0.1)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("ATR SL Mult – Trend (0 = use default)", Group = "09 · Stops & Targets",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 6.0, Step = 0.1)]
        public double AtrSlMultTrend { get; set; }

        [Parameter("ATR SL Mult – MR (0 = use default)", Group = "09 · Stops & Targets",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 6.0, Step = 0.1)]
        public double AtrSlMultMR { get; set; }

        [Parameter("ATR SL Mult – Breakout (0 = use default)", Group = "09 · Stops & Targets",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 6.0, Step = 0.1)]
        public double AtrSlMultBreakout { get; set; }

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

        [Parameter("BreakEven Trigger (R multiple)", Group = "10 · Trade Mgmt",
            DefaultValue = 1.0, MinValue = 0.1, Step = 0.1)]
        public double BeTriggerR { get; set; }

        [Parameter("BreakEven Offset (Pips beyond entry)", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.0, Step = 0.1)]
        public double BeOffsetPips { get; set; }

        [Parameter("Enable Partial 1 (50% at R=1)", Group = "10 · Trade Mgmt", DefaultValue = true)]
        public bool EnablePartial1 { get; set; }

        [Parameter("Partial 1 – Trigger (R)", Group = "10 · Trade Mgmt",
            DefaultValue = 1.0, MinValue = 0.2, Step = 0.1)]
        public double Partial1TriggerR { get; set; }

        [Parameter("Partial 1 – Close Fraction (0-1)", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.05, MaxValue = 0.9, Step = 0.05)]
        public double Partial1Fraction { get; set; }

        [Parameter("Enable Partial 2 (25% at R=2)", Group = "10 · Trade Mgmt", DefaultValue = true)]
        public bool EnablePartial2 { get; set; }

        [Parameter("Partial 2 – Trigger (R)", Group = "10 · Trade Mgmt",
            DefaultValue = 2.0, MinValue = 0.5, Step = 0.1)]
        public double Partial2TriggerR { get; set; }

        [Parameter("Partial 2 – Close Fraction (0-1)", Group = "10 · Trade Mgmt",
            DefaultValue = 0.5, MinValue = 0.05, MaxValue = 0.9, Step = 0.05)]
        public double Partial2Fraction { get; set; }

        [Parameter("Enable Chandelier Trail (after Partial1)", Group = "10 · Trade Mgmt", DefaultValue = true)]
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

        [Parameter("Max Floating Daily DD (%, 0 = off, force-closes open trade)", Group = "11 · Risk Gates",
            DefaultValue = 6.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1)]
        public double MaxFloatingDailyDdPct { get; set; }

        [Parameter("Max Trades per Day", Group = "11 · Risk Gates",
            DefaultValue = 6, MinValue = 1, MaxValue = 50)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Consec Losses – Cool-Down (hours, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 4.0, MinValue = 0.0, MaxValue = 48.0, Step = 0.5)]
        public double ConsecLossCoolDownHours { get; set; }

        [Parameter("Consec Losses – Trigger", Group = "11 · Risk Gates",
            DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int ConsecLossTrigger { get; set; }

        [Parameter("Consec Loss – Size Reducer (0.5–1.0, <1 = smaller)", Group = "11 · Risk Gates",
            DefaultValue = 0.7, MinValue = 0.3, MaxValue = 1.0, Step = 0.05)]
        public double ConsecLossSizeReducer { get; set; }

        [Parameter("Persist Streak Counter Across Restarts (LocalStorage)", Group = "11 · Risk Gates", DefaultValue = false)]
        public bool PersistStreakCounter { get; set; }

        [Parameter("News Blackout UTC (CSV: yyyy-MM-dd HH:mm±min, e.g. 2026-05-07 12:30±30)", Group = "11 · Risk Gates",
            DefaultValue = "")]
        public string NewsBlackoutCsv { get; set; }

        [Parameter("Force Flat During News Blackout (close open trade)", Group = "11 · Risk Gates", DefaultValue = false)]
        public bool ForceFlatInBlackout { get; set; }

        [Parameter("Equity Curve Trail Stop (% from session HWM, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 15.0, MinValue = 0.0, MaxValue = 30.0, Step = 0.5)]
        public double EquityCurveTrailPct { get; set; }

        [Parameter("Per-Trade Hard Loss Cap ($, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 4.0, MinValue = 0.0, Step = 1.0)]
        public double MaxLossPerTradeUsd { get; set; }

        // ── 12 · Vol-Targeted Sizing ─────────────────────────────────────────
        [Parameter("Enable Vol-Targeted Sizing", Group = "12 · Sizing", DefaultValue = false)]
        public bool EnableVolTargetSizing { get; set; }

        [Parameter("Vol Target Mode", Group = "12 · Sizing", DefaultValue = VolTargetMode.AtrBased)]
        public VolTargetMode VolTargetMode { get; set; }

        [Parameter("Vol Target Scale Mode (RiskPctScale=legacy double-scales; NotionalScale=institutional fix)", Group = "12 · Sizing", DefaultValue = VolTargetScaleMode.RiskPctScale)]
        public VolTargetScaleMode VolTargetScaleMode { get; set; }

        [Parameter("Baseline ATR Pips (reference, ATR-based mode)", Group = "12 · Sizing",
            DefaultValue = 12.0, MinValue = 1.0, Step = 0.5)]
        public double VolTargetBaselineAtrPips { get; set; }

        [Parameter("Target Vol % (StdDev-based mode)", Group = "12 · Sizing",
            DefaultValue = 1.5, MinValue = 0.1, MaxValue = 10.0, Step = 0.1)]
        public double VolTargetStdDevPct { get; set; }

        [Parameter("Vol Lookback Days (StdDev calculation)", Group = "12 · Sizing",
            DefaultValue = 20, MinValue = 5, MaxValue = 60)]
        public int VolLookbackDays { get; set; }

        [Parameter("Max Margin Utilization (%)", Group = "12 · Sizing",
            DefaultValue = 30.0, MinValue = 5.0, MaxValue = 95.0, Step = 5.0)]
        public double MaxMarginUtilizationPct { get; set; }

        // ── 13 · Logging ─────────────────────────────────────────────────────
        [Parameter("Verbose Logs", Group = "13 · Logging", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Enable Attribution Summary on Stop", Group = "13 · Logging", DefaultValue = true)]
        public bool EnableAttribution { get; set; }

        [Parameter("Enable Attribution Persistence (JSON, Live only)", Group = "13 · Logging", DefaultValue = false)]
        public bool EnableAttributionPersistence { get; set; }

        [Parameter("Lock Parameters (prevent live changes)", Group = "13 · Logging", DefaultValue = false)]
        public bool LockParameters { get; set; }

        [Parameter("Export trade log CSV on Stop", Group = "13 · Logging", DefaultValue = false)]
        public bool ExportTradeLogCsv { get; set; }

        [Parameter("Parameter Set ID (WF compatibility)", Group = "13 · Logging", DefaultValue = "v1.2.0")]
        public string ParameterSetId { get; set; }

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE STATE
        // ════════════════════════════════════════════════════════════════════
        private Bars _htfBars;
        private Bars _dailyBars;
        private MovingAverage _htfEma;
        private AverageTrueRange _atr;
        private AverageTrueRange _atrChandelier;
        private AverageTrueRange _dailyAtr;          // daily-TF ATR for regime ratio (M15 ATR would be ~10× too small)
        private MovingAverage _emaFast;
        private MovingAverage _emaSlow;
        private RelativeStrengthIndex _rsi;
        private DirectionalMovementSystem _dms;

        private CloverTradeState _currentTrade;
        private DateTime _startTime;
        private DateTime _botDay = DateTime.MinValue;
        private DateTime _botWeek = DateTime.MinValue;
        private double _dayStartBalance;
        private double _weekStartBalance;
        private double _dayRealizedPnl;
        private double _weekRealizedPnl;
        private bool _dailyDdBreached;
        private bool _weeklyDdBreached;
        private int _tradesToday;
        private int _consecutiveLosses;
        private DateTime _cooldownEndTime = DateTime.MinValue;
        private CloverRegime _lastRegime = CloverRegime.Normal;
        private DateTime _regimeChangeBar = DateTime.MinValue;

        // ORB session state (reset per day)
        private DateTime _orbDate = DateTime.MinValue;
        private double _orbHigh;
        private double _orbLow;
        private bool _orbReady;
        private bool _orbLongTaken;
        private bool _orbShortTaken;
        // LinkedList of ORB intraday ranges (capped at OrbRangeLookbackDays) for percentile filter.
        private readonly LinkedList<double> _orbRangeHistory = new LinkedList<double>();

        // VWAP incremental
        private DateTime _vwapDate = DateTime.MinValue;
        private double _vwapSumPv;
        private double _vwapSumV;
        private double _cachedVwap;
        private DateTime _lastVwapBarTime = DateTime.MinValue;

        // News blackout windows: (centerUtc, halfWindowMinutes)
        private List<(DateTime center, int halfMinutes)> _newsBlackouts = new List<(DateTime, int)>();

        private PendingOrder _pendingVwapMrLimit;
        private int _pendingLimitPlacedBarIndex;
        private (CloverEdge edge, CloverSetup setup, double rrr, double slPips, DateTime placedAt)? _pendingVwapMrLimitMeta;

        private List<string> _tradeLogRows = new List<string>();
        private CloverRegime _tradeOpenRegime = CloverRegime.Normal;
        private double _runHighWaterMarkBalance;

        private Dictionary<DateTime, double> _dailyRSum       = new Dictionary<DateTime, double>();
        private Dictionary<DateTime, double> _dailyEquityClose = new Dictionary<DateTime, double>();

        // Tracking
        private int _totalTradesOpened;
        private Dictionary<CloverEdge, int>    _edgeWinCount              = new Dictionary<CloverEdge, int>();
        private Dictionary<CloverEdge, int>    _edgeLossCount             = new Dictionary<CloverEdge, int>();
        private Dictionary<CloverEdge, double> _edgePnlSum                = new Dictionary<CloverEdge, double>();
        private Dictionary<CloverEdge, double> _edgeEntrySlippageSum      = new Dictionary<CloverEdge, double>();
        private Dictionary<CloverEdge, int>    _edgeSlippageSampleCount   = new Dictionary<CloverEdge, int>();
        private Dictionary<CloverEdge, double> _edgeExitSlippageSum       = new Dictionary<CloverEdge, double>();
        private Dictionary<CloverEdge, int>    _edgeExitSlippageCount     = new Dictionary<CloverEdge, int>();
        private Dictionary<CloverEdge, double> _edgeMaeSum                = new Dictionary<CloverEdge, double>();
        private Dictionary<CloverEdge, double> _edgeMfeSum                = new Dictionary<CloverEdge, double>();
        private Dictionary<SessionKey, SessionStats> _sessionStats        = new Dictionary<SessionKey, SessionStats>();
        private List<double> _rMultiples = new List<double>();

        // ════════════════════════════════════════════════════════════════════
        //  ON START
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            Print("╔══════════════════════════════════════════════╗");
            Print("║   Clover Algo v1.2.0 - Starting               ║");
            Print("║   ParameterSetId: {0,-25} ║", ParameterSetId);
            Print("╚══════════════════════════════════════════════╝");
            _startTime = Server.Time;

            if (ParameterSetId == "v1.2.0")
                Print("Clover v1.2.0 starting | Aggressive-Calculated defaults: Risk=1.0%, DailyDD=5%, WeeklyDD=10%, FloatingDD=6%, MaxTrades=6, ConsecCool=4h, EqTrail=15%, HardLoss=$4, RegimeMults=0.5/1.2/0.8");

            double serverUtcDeltaMin = Math.Abs((Server.Time - DateTime.UtcNow).TotalMinutes);
            if (serverUtcDeltaMin > 30)
                Print("WARNING: Server.Time deviates from system UTC by {0:F1}min — session/news gates may be off", serverUtcDeltaMin);
            if (!ServerTimeIsUtc)
                Print("CRITICAL: ServerTimeIsUtc=false — all hour-based session/news gates use server local time, NOT UTC. Review session hours.");

            if (!ValidateParameters())
            {
                Print("CRITICAL: parameter validation failed — bot will idle.");
                return;
            }

            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _atrChandelier = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _emaFast = Indicators.MovingAverage(Bars.ClosePrices, MomoEmaFast, MovingAverageType.Exponential);
            _emaSlow = Indicators.MovingAverage(Bars.ClosePrices, MomoEmaSlow, MovingAverageType.Exponential);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, VwapMrRsiPeriod);
            _dms = Indicators.DirectionalMovementSystem(14);

            _htfBars = MarketData.GetBars(HtfTimeFrame, SymbolName);
            WarmupBars(_htfBars, HtfEmaPeriod + 50, "HTF");
            _htfEma = Indicators.MovingAverage(_htfBars.ClosePrices, HtfEmaPeriod, MovingAverageType.Exponential);

            _dailyBars = MarketData.GetBars(TimeFrame.Daily, SymbolName);
            int dailyNeeded = Math.Max(MedianLookbackDays, VolLookbackDays) + 10;
            WarmupBars(_dailyBars, dailyNeeded, "Daily");

            _dailyAtr = Indicators.AverageTrueRange(_dailyBars, AtrPeriod, MovingAverageType.WilderSmoothing);

            foreach (CloverEdge e in Enum.GetValues(typeof(CloverEdge)))
            {
                _edgeWinCount[e]            = 0;
                _edgeLossCount[e]           = 0;
                _edgePnlSum[e]              = 0;
                _edgeEntrySlippageSum[e]    = 0;
                _edgeSlippageSampleCount[e] = 0;
                _edgeExitSlippageSum[e]     = 0;
                _edgeExitSlippageCount[e]   = 0;
                _edgeMaeSum[e]              = 0;
                _edgeMfeSum[e]              = 0;
            }

            BackfillOrbRangeHistory();
            BackfillVwapState();
            ParseNewsBlackout();

            if (PersistStreakCounter)
                LoadStreakFromStorage();

            _dayStartBalance = Account.Balance;
            _weekStartBalance = Account.Balance;
            _dayRealizedPnl = 0;
            _weekRealizedPnl = 0;
            _botDay = Server.Time.Date;
            _botWeek = GetWeekMonday(Server.Time);
            _runHighWaterMarkBalance = Account.Balance;

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            RecoverExistingPosition();

            Print("Symbol={0} TF={1} | HTF={2} EMA={3} | Risk={4:F2}% base={5}",
                SymbolName, TimeFrame, HtfTimeFrame, HtfEmaPeriod, RiskPerTradePct, RiskBase);
            Print("Edges: ORB={0} VWAP-MR={1} Momo={2} | DailyDD={3:F1}% WeeklyDD={4:F1}% MaxTrades={5}",
                EnableOrb ? "on" : "off",
                EnableVwapMR ? "on" : "off",
                EnableMomoCont ? "on" : "off",
                MaxDailyDdPct, MaxWeeklyDdPct, MaxTradesPerDay);
            Print("DD mode: realized PnL (closed trades only, no floating equity bleed)");
            Print("ParameterSetId: {0} | LockParameters: {1}", ParameterSetId, LockParameters);

            if (LockParameters)
            {
                ValidateParameterLock();
            }
        }

        protected override void OnStop()
        {
            Positions.Opened -= OnPositionOpened;
            Positions.Closed -= OnPositionClosed;

            if (_botDay != DateTime.MinValue)
                _dailyEquityClose[_botDay] = Account.Balance;
            TimeSpan runtime = Server.Time - _startTime;
            Print("╔══════════════════════════════════════════════╗");
            Print("║   Clover Algo v1.2.0 (ParameterSetId: {0})    ║", ParameterSetId);
            Print("║   Stopped                                      ║");
            Print("╚══════════════════════════════════════════════╝");
            Print("  Runtime    : {0:dd\\d\\ hh\\h\\ mm\\m}", runtime);
            Print("  Balance    : {0:F2} {1}", Account.Balance, Account.Asset.Name);
            Print("  Trades     : {0}", _totalTradesOpened);
            Print("  ParameterSetId: {0}", ParameterSetId);

            if (EnableAttribution)
            {
                Print("── Edge Attribution ─────────────────────────────");
                foreach (CloverEdge e in Enum.GetValues(typeof(CloverEdge)))
                {
                    int w = _edgeWinCount[e];
                    int l = _edgeLossCount[e];
                    int n = w + l;
                    if (n == 0) continue;
                    double wr = (double)w / n;
                    double avg = _edgePnlSum[e] / n;
                    double avgEntrySlip = _edgeSlippageSampleCount[e] > 0 ? _edgeEntrySlippageSum[e] / _edgeSlippageSampleCount[e] : 0;
                    Print("  {0,-10} n={1,3} wr={2:P1} avgPnL={3:+0.00;-0.00;0.00} entry_slip={4:+0.0;-0.0;0.0}p",
                        e, n, wr, avg, avgEntrySlip);
                }

                Print("── Session/DoW Matrix ───────────────────────────");
                var sortedSessions = _sessionStats.OrderBy(kvp => (kvp.Key.DoW, kvp.Key.SessionBucket)).ToList();
                foreach (var entry in sortedSessions)
                {
                    SessionKey key = entry.Key;
                    SessionStats stat = entry.Value;
                    double wr = stat.TradeCount > 0 ? (double)stat.Wins / stat.TradeCount : 0;
                    double avg = stat.TradeCount > 0 ? stat.PnLSum / stat.TradeCount : 0;
                    Print("  {0,-10} {1,-10} n={2,2} wr={3:P0} avgPnL={4:+0.00;-0.00;0.00}",
                        key.DoW, SessionBucketName(key.SessionBucket), stat.TradeCount, wr, avg);
                }

                if (_rMultiples.Count > 0)
                {
                    Print("── R-Multiple Distribution ──────────────────────");
                    var sortedR = _rMultiples.OrderBy(x => x).ToList();
                    double meanR = sortedR.Average();
                    double medianR = sortedR.Count % 2 == 0
                        ? (sortedR[sortedR.Count / 2 - 1] + sortedR[sortedR.Count / 2]) / 2.0
                        : sortedR[sortedR.Count / 2];
                    double p10 = sortedR[(int)(sortedR.Count * 0.10)];
                    double p90 = sortedR[(int)(sortedR.Count * 0.90)];
                    double sumDev = sortedR.Sum(r => Math.Pow(r - meanR, 3));
                    // bias-correct stddev denominator for small samples (Bessel's correction)
                    int denomN = sortedR.Count < 30 ? (sortedR.Count - 1) : sortedR.Count;
                    double stdDev = denomN > 0
                        ? Math.Sqrt(sortedR.Sum(r => Math.Pow(r - meanR, 2)) / denomN)
                        : 0;
                    double skewness = stdDev > 0 ? sumDev / (sortedR.Count * stdDev * stdDev * stdDev) : 0;
                    double expectancy = meanR;

                    Print("  n={0,3} mean={1:+0.00;-0.00;0.00} median={2:+0.00;-0.00;0.00} p10={3:+0.00;-0.00;0.00} p90={4:+0.00;-0.00;0.00}",
                        sortedR.Count, meanR, medianR, p10, p90);
                    Print("  stddev={0:F2} skew={1:+0.00;-0.00;0.00} expectancy={2:+0.00;-0.00;0.00}R",
                        stdDev, skewness, expectancy);

                    Print("BREAKING v1.1.2: Sharpe annualisation fixed (sqrt(N)→sqrt(252))");
                    Print("── Performance Metrics (R-based, daily annualisation) ─────");
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
                    double pf = grossLoss > 0 ? grossWin / grossLoss : double.PositiveInfinity;

                    if (_dailyRSum.Count < 30)
                    {
                        Print("  Sharpe=N/A (insufficient sample: {0} trading days < 30)", _dailyRSum.Count);
                        Print("  Sortino=N/A");
                    }
                    else
                    {
                        var dailyReturns = _dailyRSum.Values.ToList();
                        double dailyMean  = dailyReturns.Average();
                        int    nDays      = dailyReturns.Count;
                        double dailyStdDev = nDays > 1
                            ? Math.Sqrt(dailyReturns.Sum(r => Math.Pow(r - dailyMean, 2)) / (nDays - 1))
                            : 0;
                        double sharpe  = dailyStdDev > 0 ? dailyMean / dailyStdDev * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;

                        var negDaily   = dailyReturns.Where(r => r < 0).ToList();
                        double downDev = negDaily.Count > 0
                            ? Math.Sqrt(negDaily.Sum(r => r * r) / nDays)
                            : 0;
                        double sortino = downDev > 0 ? dailyMean / downDev * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;

                        Print("  Sharpe={0:+0.00;-0.00;0.00} (annualised, 252d) | sample N={1}d",
                            sharpe, nDays);
                        Print("  Sortino={0:+0.00;-0.00;0.00}  MaxDD={1:F2}R  ProfitFactor={2:F2}",
                            sortino, maxDdR, pf);
                    }

                    Print("  GrossWin={0:F2}R  GrossLoss={1:F2}R  Expectancy={2:+0.00;-0.00;0.00}R",
                        grossWin, grossLoss, expectancy);
                }

                Print("── Performance (Equity-Returns, $ daily series) ─────");
                if (_dailyEquityClose.Count >= 2)
                {
                    var sortedEqDays = _dailyEquityClose.OrderBy(k => k.Key).ToList();
                    var eqReturns    = new List<double>();
                    for (int i = 1; i < sortedEqDays.Count; i++)
                    {
                        double prevBal = sortedEqDays[i - 1].Value;
                        double currBal = sortedEqDays[i].Value;
                        if (prevBal > 0) eqReturns.Add((currBal - prevBal) / prevBal);
                    }
                    int eqN = eqReturns.Count;
                    if (eqN < 30)
                    {
                        Print("  Equity-Sharpe=N/A  Equity-Sortino=N/A  (only {0} days < 30)", eqN);
                    }
                    else
                    {
                        double eqMean    = eqReturns.Average();
                        double eqVar     = eqReturns.Sum(r => Math.Pow(r - eqMean, 2)) / (eqN - 1);
                        double eqStd     = Math.Sqrt(eqVar);
                        double eqSharpe  = eqStd > 0 ? eqMean / eqStd * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                        var    negEq     = eqReturns.Where(r => r < 0).ToList();
                        double eqDownDev = negEq.Count > 0 ? Math.Sqrt(negEq.Sum(r => r * r) / eqN) : 0;
                        double eqSortino = eqDownDev > 0 ? eqMean / eqDownDev * Math.Sqrt(TRADING_DAYS_PER_YEAR) : 0;
                        Print("  Equity-Sharpe={0:+0.00;-0.00;0.00} (annualised, 252d) | sample N={1}d",
                            eqSharpe, eqN);
                        Print("  Equity-Sortino={0:+0.00;-0.00;0.00}", eqSortino);
                    }
                }
                else
                {
                    Print("  Equity-Sharpe=N/A  (no daily equity history — run lasted < 2 days)");
                }

                Print("── MAE / MFE per Edge ───────────────────────────");
                foreach (CloverEdge e in Enum.GetValues(typeof(CloverEdge)))
                {
                    int total = _edgeWinCount[e] + _edgeLossCount[e];
                    if (total == 0) continue;
                    double avgMae = _edgeMaeSum[e] / total;
                    double avgMfe = _edgeMfeSum[e] / total;
                    double avgExitSlip = _edgeExitSlippageCount[e] > 0
                        ? _edgeExitSlippageSum[e] / _edgeExitSlippageCount[e] : 0;
                    Print("  {0,-10} avgMAE={1:+0.00;-0.00;0.00}R  avgMFE={2:+0.00;-0.00;0.00}R  exit_slip={3:+0.0;-0.0;0.0}p",
                        e, avgMae, avgMfe, avgExitSlip);
                }

                PersistAttributionJson();
                ExportTradeLogCsvFile();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  ON BAR – signal evaluation
        // ════════════════════════════════════════════════════════════════════
        protected override void OnBar()
        {
            try
            {
                RolloverDailyWeekly();

                // Force close on Friday close window (weekend flat).
                if (WeekendClosePending())
                {
                    ClosePositionIfOpen("WeekendClose");
                    return;
                }

                if (ForceFlatInBlackout && _currentTrade != null)
                {
                    DateTime now2 = Server.Time;
                    foreach (var (center, halfMin) in _newsBlackouts)
                    {
                        if (Math.Abs((now2 - center).TotalMinutes) <= halfMin)
                        {
                            ClosePositionIfOpen($"NewsBlackout({center:HH:mm})");
                            return;
                        }
                    }
                }

                if (Bars.Count < Math.Max(MomoEmaSlow, AtrPeriod) + 5) return;

                UpdateOrbState();
                CancelStaleLimitOrder();

                // Update MAE/MFE from bar H/L — catches gaps missed by OnTick in sparse-tick backtests
                if (_currentTrade != null && _currentTrade.InitialSlPips > 0)
                {
                    var mfmPos = Positions.FindById(_currentTrade.PositionId);
                    if (mfmPos != null)
                    {
                        double barFavorR   = ComputeBarMaxR(mfmPos, _currentTrade.InitialSlPips);
                        double barAdverseR = ComputeBarMinR(mfmPos, _currentTrade.InitialSlPips);
                        if (barFavorR   > _currentTrade.MfeRMultiple) _currentTrade.MfeRMultiple = barFavorR;
                        if (barAdverseR < _currentTrade.MaeRMultiple) _currentTrade.MaeRMultiple = barAdverseR;
                    }
                }

                if (_currentTrade != null) return;
                if (!IsMarketTradable()) return;

                CloverRegime regime = ClassifyRegime();
                _tradeOpenRegime = regime;
                int htfBias = GetHtfBias();

                DateTime lastBarTime = Bars.OpenTimes.Last(1);
                if (SkipBarAfterRegimeChange && lastBarTime == _regimeChangeBar) return;

                if (EnableOrb && TryOrbEntry(regime, htfBias)) return;
                if (EnableMomoCont && TryMomoEntry(regime, htfBias)) return;
                if (EnableVwapMR && TryVwapMrEntry(regime, htfBias)) return;

                if (_currentTrade != null)
                {
                    var pos = Positions.FindById(_currentTrade.PositionId);
                    if (pos != null)
                        ManagePartialsAtBarClose(pos);
                }
            }
            catch (Exception ex)
            {
                Print("ERROR OnBar: {0} | {1}", ex.Message, ex.StackTrace);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  ON TICK – trade management
        // ════════════════════════════════════════════════════════════════════
        protected override void OnTick()
        {
            if (_currentTrade == null) return;

            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null)
            {
                _currentTrade = null;
                return;
            }

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
                Print("ERROR OnTick mgmt: {0}", ex.Message);
            }
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
                Print("WARNING: RiskPerTradePct*2 ({0:F2}%) > MaxDailyDdPct ({1:F2}%) — clamping risk to {2:F2}%.",
                    RiskPerTradePct * 2, MaxDailyDdPct, clamped);
                RiskPerTradePct = clamped;
            }
            if (MomoEmaFast >= MomoEmaSlow)
            {
                Print("CRITICAL: MomoEmaFast ({0}) >= MomoEmaSlow ({1})", MomoEmaFast, MomoEmaSlow);
                ok = false;
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
                string paramName = canonical.Key;
                object canonicalValue = canonical.Value;
                object currentValue = GetPropertyValue(paramName);

                if (currentValue != null && !currentValue.Equals(canonicalValue))
                {
                    Print("DIVERGENCE: {0} = {1} (canonical: {2})", paramName, currentValue, canonicalValue);
                    divergences++;
                }
            }
            if (divergences == 0)
                Print("All critical parameters match canonical defaults.");
            else
                Print("WARNING: {0} parameter(s) diverged from canonical set. Manual tuning detected.", divergences);
        }

        private object GetPropertyValue(string propertyName)
        {
            var prop = this.GetType().GetProperty(propertyName);
            return prop?.GetValue(this);
        }

        // ════════════════════════════════════════════════════════════════════
        //  GATES
        // ════════════════════════════════════════════════════════════════════
        private bool IsMarketTradable()
        {
            DateTime now    = Server.Time;
            DateTime nowUtc = NowUtc();

            if (_dailyDdBreached) return Reject("DailyDD hit");
            if (_weeklyDdBreached) return Reject("WeeklyDD hit");
            if (_tradesToday >= MaxTradesPerDay) return Reject("MaxTradesPerDay");
            if (now < _cooldownEndTime) return Reject("CoolDown");

            foreach (var (center, halfMin) in _newsBlackouts)
            {
                if (Math.Abs((now - center).TotalMinutes) <= halfMin)
                    return Reject($"NewsBlackout ({center:yyyy-MM-dd HH:mm} ±{halfMin}m)");
            }

            int hour = nowUtc.Hour;
            if (hour < SessionStartHour || hour >= SessionEndHour) return Reject("OutsideSession");

            if (BlockFriday && nowUtc.DayOfWeek == DayOfWeek.Friday) return Reject("FridayBlock");

            // Spread gate: hard cap + dynamic ATR-scaled cap
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            double atrPips = GetAtrPips();
            double dynCap = Math.Min(MaxSpreadPips, Math.Max(SPREAD_FLOOR_PIPS, atrPips * SpreadAtrRatio));
            if (spreadPips > dynCap) return Reject($"SpreadGate {spreadPips:F2} > {dynCap:F2}");

            return true;
        }

        private bool Reject(string reason)
        {
            if (Verbose) Print("REJECT: {0}", reason);
            return false;
        }

        private CloverRegime ClassifyRegime()
        {
            if (_dailyBars == null || _dailyBars.Count < MedianLookbackDays + 2)
                return _lastRegime;
            if (_dailyAtr == null) return _lastRegime;

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
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - c1), Math.Abs(l - c1)));
                trs[i] = tr;
            }
            Array.Sort(trs);

            int n = MedianLookbackDays;
            double median = (n % 2 == 0)
                ? (trs[n / 2 - 1] + trs[n / 2]) / 2.0
                : trs[n / 2];
            if (median <= 0) return _lastRegime;

            double ratio = dailyAtrPrice / median;

            // Asymmetric hysteresis: exit threshold differs per current regime to prevent boundary flicker
            CloverRegime newRegime = _lastRegime;
            switch (_lastRegime)
            {
                case CloverRegime.LowVol:
                    if (ratio > AtrRatioLow + RegimeHysteresisBand)
                        newRegime = ratio >= AtrRatioHigh ? CloverRegime.HighVol : CloverRegime.Normal;
                    break;
                case CloverRegime.HighVol:
                    if (ratio < AtrRatioHigh - RegimeHysteresisBand)
                        newRegime = ratio <= AtrRatioLow ? CloverRegime.LowVol : CloverRegime.Normal;
                    break;
                case CloverRegime.Normal:
                default:
                    if (ratio <= AtrRatioLow)
                        newRegime = CloverRegime.LowVol;
                    else if (ratio >= AtrRatioHigh)
                        newRegime = CloverRegime.HighVol;
                    break;
            }

            if (newRegime != _lastRegime)
            {
                _lastRegime = newRegime;
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
            double close = _htfBars.ClosePrices.Last(1);
            double ema = _htfEma.Result.Last(1);
            double buf = HtfBufferPips * Symbol.PipSize;
            if (double.IsNaN(close) || double.IsNaN(ema)) return 0;
            if (close > ema + buf) return 1;
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
        //  EDGE #1 — OPENING RANGE BREAKOUT (+NR7 squeeze)
        // ════════════════════════════════════════════════════════════════════
        private void UpdateOrbState()
        {
            DateTime now   = NowUtc();
            DateTime today = now.Date;

            if (_orbDate != today)
            {
                _orbDate = today;
                _orbHigh = double.MinValue;
                _orbLow = double.MaxValue;
                _orbReady = false;
                _orbLongTaken = false;
                _orbShortTaken = false;
            }

            DateTime orbStart = today.AddHours(OrbStartHour);
            DateTime orbEnd = orbStart.AddMinutes(OrbDurationMinutes);

            DateTime lastBarOpen = Bars.OpenTimes.Last(1);
            if (lastBarOpen >= orbStart && lastBarOpen < orbEnd)
            {
                double h = Bars.HighPrices.Last(1);
                double l = Bars.LowPrices.Last(1);
                if (h > _orbHigh) _orbHigh = h;
                if (l < _orbLow) _orbLow = l;
            }
            else if (lastBarOpen >= orbEnd && !_orbReady && _orbHigh > double.MinValue)
            {
                _orbReady = true;
                double orbRange = _orbHigh - _orbLow;
                if (orbRange > 0)
                {
                    _orbRangeHistory.AddLast(orbRange);
                    while (_orbRangeHistory.Count > OrbRangeLookbackDays)
                        _orbRangeHistory.RemoveFirst();
                }
                if (Verbose)
                    Print("ORB ready: H={0:F5} L={1:F5} range={2:F1}p (vol-pct history={3}/{4})",
                        _orbHigh, _orbLow, (_orbHigh - _orbLow) / Symbol.PipSize, _orbRangeHistory.Count, OrbRangeLookbackDays);
            }
        }

        private bool TryOrbEntry(CloverRegime regime, int htfBias)
        {
            if (!_orbReady) return false;
            if (NowUtc().Hour >= OrbEntryCutoffHour) return false;

            if (regime == CloverRegime.LowVol) return false;
            if (OrbRequireNr7 && !IsNr7Squeeze()) return false;

            double bufferPx = OrbBufferPips * Symbol.PipSize;
            double lastClose = Bars.ClosePrices.Last(1);
            double lastHigh = Bars.HighPrices.Last(1);
            double lastLow = Bars.LowPrices.Last(1);

            if (OrbRequireVolumeExpansion && !IsVolumeExpanded())
            {
                if (Verbose) Print("ORB: volume expansion not confirmed — skipping entry");
                return false;
            }

            if (!_orbLongTaken && htfBias >= 0
                && lastClose > _orbHigh + bufferPx
                && lastHigh > _orbHigh)
            {
                _orbLongTaken = true;
                return OpenTrade(TradeType.Buy, CloverEdge.ORB, CloverSetup.Breakout, RrrBreakout,
                    $"ORB Long | H={_orbHigh:F5} close={lastClose:F5}");
            }

            if (!_orbShortTaken && htfBias <= 0
                && lastClose < _orbLow - bufferPx
                && lastLow < _orbLow)
            {
                _orbShortTaken = true;
                return OpenTrade(TradeType.Sell, CloverEdge.ORB, CloverSetup.Breakout, RrrBreakout,
                    $"ORB Short | L={_orbLow:F5} close={lastClose:F5}");
            }
            return false;
        }

        private bool IsNr7Squeeze() => IsLowRangePercentile();

        private bool IsLowRangePercentile()
        {
            double todayRange = _orbHigh - _orbLow;
            if (todayRange <= 0) return false;

            int warmupFloor = 7;
            if (_orbRangeHistory.Count < warmupFloor)
            {
                if (OrbNr7StrictWarmup)
                {
                    if (Verbose) Print("VolPct: warmup {0}/{1} strict=true — blocking entry", _orbRangeHistory.Count, warmupFloor);
                    return false;
                }
                return true;
            }

            var sorted = _orbRangeHistory.OrderBy(x => x).ToList();
            int n = sorted.Count;
            double pctIdx = (OrbRangePercentileMax / 100.0) * (n - 1);
            int lo = (int)pctIdx;
            int hi = Math.Min(lo + 1, n - 1);
            double threshold = sorted[lo] + (pctIdx - lo) * (sorted[hi] - sorted[lo]);

            bool result = todayRange <= threshold;
            if (Verbose) Print("VolPct: range={0:F1}p threshold={1:F1}p ({2:F0}th pct of {3} sessions) → {4}",
                todayRange / Symbol.PipSize, threshold / Symbol.PipSize, OrbRangePercentileMax, n, result ? "PASS" : "FAIL");
            return result;
        }

        // ════════════════════════════════════════════════════════════════════
        //  EDGE #2 — VWAP MEAN-REVERSION (ADX-gated, dev>= X*ATR)
        // ════════════════════════════════════════════════════════════════════
        private bool TryVwapMrEntry(CloverRegime regime, int htfBias)
        {
            if (regime == CloverRegime.HighVol) return false;

            UpdateVwap();
            if (_cachedVwap <= 0) return false;

            double adx = _dms.ADX.Last(1);
            if (double.IsNaN(adx) || adx > VwapMrAdxMax) return false;

            double close = Bars.ClosePrices.Last(1);
            double atr = _atr.Result.Last(1);
            if (double.IsNaN(atr) || atr <= 0) return false;
            double devAtrMultiples = (close - _cachedVwap) / atr;

            double rsi = _rsi.Result.Last(1);

            if (devAtrMultiples <= -VwapMrMinDevAtr && htfBias >= 0)
            {
                if (VwapMrRequireRsi && rsi >= 30) return false;
                if (VwapMrUseLimitOrder)
                    return PlaceVwapMrLimit(TradeType.Buy, $"VwapMR Long Limit | dev={devAtrMultiples:F2}ATR rsi={rsi:F1}");
                return OpenTrade(TradeType.Buy, CloverEdge.VwapMR, CloverSetup.MeanReversion, RrrMR,
                    $"VwapMR Long | dev={devAtrMultiples:F2}ATR rsi={rsi:F1} adx={adx:F1}");
            }
            if (devAtrMultiples >= VwapMrMinDevAtr && htfBias <= 0)
            {
                if (VwapMrRequireRsi && rsi <= 70) return false;
                if (VwapMrUseLimitOrder)
                    return PlaceVwapMrLimit(TradeType.Sell, $"VwapMR Short Limit | dev={devAtrMultiples:F2}ATR rsi={rsi:F1}");
                return OpenTrade(TradeType.Sell, CloverEdge.VwapMR, CloverSetup.MeanReversion, RrrMR,
                    $"VwapMR Short | dev={devAtrMultiples:F2}ATR rsi={rsi:F1} adx={adx:F1}");
            }
            return false;
        }

        private void UpdateVwap()
        {
            DateTime now          = Server.Time;
            DateTime anchorToday  = now.Date.AddHours(VwapAnchorHourUtc);
            DateTime sessionStart = now >= anchorToday ? anchorToday : anchorToday.AddDays(-1);

            if (_vwapDate != sessionStart)
            {
                _vwapDate = sessionStart;
                _vwapSumPv = 0;
                _vwapSumV = 0;
                _lastVwapBarTime = DateTime.MinValue;
            }

            DateTime lastBarOpen = Bars.OpenTimes.Last(1);
            if (lastBarOpen == _lastVwapBarTime) return;
            if (lastBarOpen < sessionStart) return;

            double tp = (Bars.HighPrices.Last(1) + Bars.LowPrices.Last(1) + Bars.ClosePrices.Last(1)) / 3.0;
            double vol = Math.Max(Bars.TickVolumes.Last(1), 1);
            _vwapSumPv += tp * vol;
            _vwapSumV += vol;
            _lastVwapBarTime = lastBarOpen;
            if (_vwapSumV > 0) _cachedVwap = _vwapSumPv / _vwapSumV;
        }

        // ════════════════════════════════════════════════════════════════════
        //  EDGE #3 — MOMENTUM CONTINUATION (trend + pullback + ADX)
        // ════════════════════════════════════════════════════════════════════
        private bool TryMomoEntry(CloverRegime regime, int htfBias)
        {
            if (regime == CloverRegime.LowVol) return false;
            if (htfBias == 0) return false;

            double fastNow = _emaFast.Result.Last(1);
            double slowNow = _emaSlow.Result.Last(1);
            double fastPrev = _emaFast.Result.Last(2);
            double closeNow = Bars.ClosePrices.Last(1);
            double lowPrev = Bars.LowPrices.Last(1);
            double highPrev = Bars.HighPrices.Last(1);
            double adx = _dms.ADX.Last(1);
            double diPlus = _dms.DIPlus.Last(1);
            double diMinus = _dms.DIMinus.Last(1);

            if (double.IsNaN(adx) || adx < MomoAdxMin) return false;

            if (htfBias > 0)
            {
                bool trendOk = fastNow > slowNow && fastNow > fastPrev && closeNow > slowNow;
                bool diOk = diPlus > diMinus;
                bool pullbackOk = !MomoRequirePullback
                    || (lowPrev <= fastNow + MOMO_PULLBACK_TOLERANCE_RATIO * Math.Abs(fastNow - slowNow)
                        && closeNow > fastNow);
                if (trendOk && diOk && pullbackOk)
                    return OpenTrade(TradeType.Buy, CloverEdge.MomoCont, CloverSetup.Trend, RrrTrend,
                        $"Momo Long | adx={adx:F1} DI+={diPlus:F1} DI-={diMinus:F1}");
            }
            else if (htfBias < 0)
            {
                bool trendOk = fastNow < slowNow && fastNow < fastPrev && closeNow < slowNow;
                bool diOk = diMinus > diPlus;
                bool pullbackOk = !MomoRequirePullback
                    || (highPrev >= fastNow - MOMO_PULLBACK_TOLERANCE_RATIO * Math.Abs(fastNow - slowNow)
                        && closeNow < fastNow);
                if (trendOk && diOk && pullbackOk)
                    return OpenTrade(TradeType.Sell, CloverEdge.MomoCont, CloverSetup.Trend, RrrTrend,
                        $"Momo Short | adx={adx:F1} DI+={diPlus:F1} DI-={diMinus:F1}");
            }
            return false;
        }

        // ════════════════════════════════════════════════════════════════════
        //  ORDER EXECUTION
        // ════════════════════════════════════════════════════════════════════
        private bool OpenTrade(TradeType dir, CloverEdge edge, CloverSetup setup, double rrr, string reason)
        {
            double atrPips = GetAtrPips();
            double slMult = setup == CloverSetup.Trend ? AtrSlMultTrend
                          : setup == CloverSetup.MeanReversion ? AtrSlMultMR
                          : AtrSlMultBreakout;
            if (slMult <= 0) slMult = AtrSlMultiplier;

            double slPips = Math.Round(atrPips * slMult + CommissionBufferPips, 1);
            if (slPips < MinSlPips) slPips = MinSlPips;
            if (slPips > MaxSlPips) slPips = MaxSlPips;
            double tpPips = Math.Round(slPips * rrr, 1);

            double volume = CalculateVolume(slPips, dir);
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("REJECT: calculated volume below broker min. slPips={0:F1} vol={1:F0}", slPips, volume);
                return false;
            }

            double intendedPrice = dir == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            string metadata = $"|{edge}|{setup}";

            TradeResult result;
            if (EntryMarketRangePips > 0)
                result = ExecuteMarketRangeOrder(dir, SymbolName, volume, EntryMarketRangePips, intendedPrice, BotLabel, slPips, tpPips, metadata);
            else
                result = ExecuteMarketOrder(dir, SymbolName, volume, BotLabel, slPips, tpPips, metadata);

            if (!result.IsSuccessful || result.Position == null)
            {
                Print("ORDER FAILED: {0}", result.Error);
                return false;
            }

            var pos = result.Position;
            double entrySlippagePips = dir == TradeType.Buy
                ? (pos.EntryPrice - intendedPrice) / Symbol.PipSize
                : (intendedPrice - pos.EntryPrice) / Symbol.PipSize;

            _currentTrade = new CloverTradeState
            {
                PositionId               = pos.Id,
                EntryPrice               = pos.EntryPrice,
                InitialSlPips            = slPips,
                InitialVolumeUnits       = pos.VolumeInUnits,
                RRRTarget                = rrr,
                BreakEvenDone            = false,
                Partial1Done             = false,
                Partial2Done             = false,
                ChandelierStop           = dir == TradeType.Buy ? double.MinValue : double.MaxValue,
                EntryTime                = Server.Time,
                Edge                     = edge,
                Setup                    = setup,
                EntrySlippage            = entrySlippagePips,
                ExitSlippagePips         = 0,
                ChandelierPeakHigh       = dir == TradeType.Buy ? pos.EntryPrice : double.MinValue,
                ChandelierPeakLow        = dir == TradeType.Sell ? pos.EntryPrice : double.MaxValue,
                LastChandelierUpdateTime = Server.Time,
                EntryBarIndex            = Bars.Count - 1,
                MaeRMultiple             = 0,
                MfeRMultiple             = 0,
                EntrySessionKey          = GetSessionKey(Server.Time),
            };
            _totalTradesOpened++;
            _tradesToday++;

            Print("FILLED: {0} {1} vol={2:F0} entry={3:F5} slip={4:+0.0;-0.0;0.0}p SL={5:F1}p TP={6:F1}p RRR={7:F2} edge={8} setup={9} | {10}",
                dir, SymbolName, volume, pos.EntryPrice, entrySlippagePips, slPips, tpPips, rrr, edge, setup, reason);

            return true;
        }

        private double CalculateVolume(double slPips, TradeType dir)
        {
            double baseAcct = RiskBase == CloverRiskBase.Equity ? Account.Equity : Account.Balance;
            double riskPct = RiskPerTradePct;

            if (_consecutiveLosses >= ConsecLossTrigger)
                riskPct *= ConsecLossSizeReducer;

            double regimeMult = _lastRegime == CloverRegime.LowVol  ? RegimeRiskMultLowVol
                              : _lastRegime == CloverRegime.HighVol ? RegimeRiskMultHighVol
                              : RegimeRiskMultNormal;
            if (Math.Abs(regimeMult - 1.0) > 0.001)
            {
                double prevRisk = riskPct;
                riskPct *= regimeMult;
                if (Verbose) Print("RegimeRiskScale: regime={0} mult={1:F2} riskPct {2:F2}->{3:F2}", _lastRegime, regimeMult, prevRisk, riskPct);
            }

            // RiskPctScale (legacy) double-scales with slPips at high ATR; NotionalScale applies after normalization
            double volTargetNotionalScale = 1.0;
            if (EnableVolTargetSizing)
            {
                double scale = 1.0;
                if (VolTargetMode == VolTargetMode.AtrBased)
                {
                    double atrPips = GetAtrPips();
                    if (atrPips > 0)
                        scale = VolTargetBaselineAtrPips / atrPips;
                }
                else if (VolTargetMode == VolTargetMode.StdDevBased)
                {
                    double realizedVol = CalculateRealizedVol();
                    if (realizedVol > 0)
                        scale = (VolTargetStdDevPct / 100.0) / realizedVol;
                }
                if (scale < 0.5) scale = 0.5;
                if (scale > 2.0) scale = 2.0;

                if (VolTargetScaleMode == VolTargetScaleMode.RiskPctScale)
                    riskPct *= scale;
                else
                    volTargetNotionalScale = scale;
            }

            double riskAmount = baseAcct * (riskPct / 100.0);
            double pipValue = Symbol.PipValue;
            if (pipValue <= 0 || slPips <= 0) return Symbol.VolumeInUnitsMin;
            double exact = riskAmount / (slPips * pipValue);
            double normalized = Symbol.NormalizeVolumeInUnits(exact, RoundingMode.Down);

            if (EnableVolTargetSizing && VolTargetScaleMode == VolTargetScaleMode.NotionalScale
                && Math.Abs(volTargetNotionalScale - 1.0) > 0.001 && volTargetNotionalScale > 0)
            {
                double oldVol = normalized;
                double scaledVol = normalized / volTargetNotionalScale;
                normalized = Symbol.NormalizeVolumeInUnits(scaledVol, RoundingMode.Down);
                if (normalized < Symbol.VolumeInUnitsMin) normalized = Symbol.VolumeInUnitsMin;
                if (Verbose) Print("VolTarget[NotionalScale]: scale={0:F3} vol {1:F0}->{2:F0}", volTargetNotionalScale, oldVol, normalized);
            }

            // Margin cap: direct ratio solve — guaranteed to satisfy cap in one step.
            double maxMarginUtilRatio = MaxMarginUtilizationPct / 100.0;
            if (Account.Equity > 0)
            {
                double maxAllowedNewMargin = maxMarginUtilRatio * Account.Equity - Account.Margin;
                if (maxAllowedNewMargin <= 0) return Symbol.VolumeInUnitsMin;
                double currentVolMargin = Symbol.GetEstimatedMargin(dir, normalized);
                if (currentVolMargin > maxAllowedNewMargin && currentVolMargin > 0)
                {
                    double scale = maxAllowedNewMargin / currentVolMargin;
                    double oldVol = normalized;
                    normalized = Symbol.NormalizeVolumeInUnits(normalized * scale, RoundingMode.Down);
                    if (normalized < Symbol.VolumeInUnitsMin) return Symbol.VolumeInUnitsMin;
                    if (Verbose) Print("MarginCap: util={0:P1} allowedNewMargin={1:F2} cap={2:P1} scaled vol {3:F0}->{4:F0}",
                        Account.Margin / Account.Equity, maxAllowedNewMargin, maxMarginUtilRatio, oldVol, normalized);
                }
            }

            return normalized;
        }

        private double CalculateRealizedVol()
        {
            if (_dailyBars == null || _dailyBars.Count < VolLookbackDays + 1)
                return 0;

            double sumLogReturns = 0;
            double sumLogReturns2 = 0;
            int count = 0;

            for (int i = 1; i <= VolLookbackDays && i < _dailyBars.Count; i++)
            {
                double c = _dailyBars.ClosePrices.Last(i);
                double cPrev = _dailyBars.ClosePrices.Last(i + 1);
                if (cPrev <= 0) continue;
                double logReturn = Math.Log(c / cPrev);
                sumLogReturns += logReturn;
                sumLogReturns2 += logReturn * logReturn;
                count++;
            }
            if (count <= 1) return 0;

            double meanReturn = sumLogReturns / count;
            double variance = (sumLogReturns2 / count) - (meanReturn * meanReturn);
            if (variance < 0) variance = 0;
            double stdDev = Math.Sqrt(variance);
            return stdDev;
        }

        // ════════════════════════════════════════════════════════════════════
        //  ORB RANGE HISTORY BACKFILL
        // ════════════════════════════════════════════════════════════════════
        private void BackfillOrbRangeHistory()
        {
            if (!OrbBackfillHistoryOnStart) return;

            int daysToCheck = OrbRangeLookbackDays + 5;
            DateTime today  = Bars.OpenTimes.Last(1).Date;
            DateTime cutoff = today.AddDays(-daysToCheck);

            var dailyOrbHigh = new Dictionary<DateTime, double>();
            var dailyOrbLow  = new Dictionary<DateTime, double>();

            for (int i = 0; i < Bars.Count - 1; i++)   // exclude current forming bar
            {
                DateTime barOpenTime = Bars.OpenTimes[i];
                DateTime barDate     = barOpenTime.Date;
                if (barDate < cutoff) continue;
                if (barDate == today) continue;   // today accumulated live by UpdateOrbState
                if (barDate.DayOfWeek == DayOfWeek.Saturday || barDate.DayOfWeek == DayOfWeek.Sunday) continue;

                DateTime orbStart = barDate.AddHours(OrbStartHour);
                DateTime orbEnd   = orbStart.AddMinutes(OrbDurationMinutes);
                if (barOpenTime < orbStart || barOpenTime >= orbEnd) continue;

                double h = Bars.HighPrices[i];
                double l = Bars.LowPrices[i];
                if (!dailyOrbHigh.ContainsKey(barDate))
                {
                    dailyOrbHigh[barDate] = h;
                    dailyOrbLow[barDate]  = l;
                }
                else
                {
                    if (h > dailyOrbHigh[barDate]) dailyOrbHigh[barDate] = h;
                    if (l < dailyOrbLow[barDate])  dailyOrbLow[barDate]  = l;
                }
            }

            int sessionsAdded = 0;
            foreach (var date in dailyOrbHigh.Keys.OrderBy(d => d))
            {
                double range = dailyOrbHigh[date] - dailyOrbLow[date];
                if (range > 0)
                {
                    _orbRangeHistory.AddLast(range);
                    while (_orbRangeHistory.Count > OrbRangeLookbackDays)
                        _orbRangeHistory.RemoveFirst();
                    sessionsAdded++;
                }
            }
            Print("ORB-history backfilled: {0} sessions over {1} days", sessionsAdded, daysToCheck);
        }

        // ════════════════════════════════════════════════════════════════════
        //  VWAP SAME-SESSION BACKFILL
        // ════════════════════════════════════════════════════════════════════
        private void BackfillVwapState()
        {
            DateTime now         = Server.Time;
            DateTime anchorToday = now.Date.AddHours(VwapAnchorHourUtc);
            DateTime anchorPrev  = anchorToday.AddDays(-1);
            DateTime sessionStart = now >= anchorToday ? anchorToday : anchorPrev;

            _vwapDate         = sessionStart;
            _vwapSumPv        = 0;
            _vwapSumV         = 0;
            _cachedVwap       = 0;
            _lastVwapBarTime  = DateTime.MinValue;

            DateTime lastClosedBarTime = Bars.OpenTimes.Last(1);
            int barsCounted = 0;

            for (int i = 0; i < Bars.Count; i++)
            {
                DateTime barOpenTime = Bars.OpenTimes[i];
                if (barOpenTime < sessionStart) continue;
                if (barOpenTime > lastClosedBarTime) break;

                double tp  = (Bars.HighPrices[i] + Bars.LowPrices[i] + Bars.ClosePrices[i]) / 3.0;
                double vol = Math.Max(Bars.TickVolumes[i], 1);
                _vwapSumPv      += tp * vol;
                _vwapSumV       += vol;
                _lastVwapBarTime = barOpenTime;
                barsCounted++;
            }

            if (_vwapSumV > 0) _cachedVwap = _vwapSumPv / _vwapSumV;
            Print("VWAP backfilled from {0:HH:mm}: {1} bars cumulated, vwap={2:F5}", sessionStart, barsCounted, _cachedVwap);
        }

        // ════════════════════════════════════════════════════════════════════
        //  TRADE MANAGEMENT — BE / Partials / Chandelier / Time-Stop
        // ════════════════════════════════════════════════════════════════════
        private void ManageBreakEven(Position pos)
        {
            if (!EnableBreakEven || _currentTrade.BreakEvenDone) return;
            double slPips = _currentTrade.InitialSlPips;
            if (slPips <= 0) return;
            double currentR = ComputeCurrentR(pos, slPips);
            if (currentR < BeTriggerR) return;

            double newSlPrice = pos.TradeType == TradeType.Buy
                ? pos.EntryPrice + BeOffsetPips * Symbol.PipSize
                : pos.EntryPrice - BeOffsetPips * Symbol.PipSize;

            if (pos.StopLoss.HasValue)
            {
                bool improves = pos.TradeType == TradeType.Buy
                    ? newSlPrice > pos.StopLoss.Value
                    : newSlPrice < pos.StopLoss.Value;
                if (!improves) { _currentTrade.BreakEvenDone = true; return; }
            }

            var r = ModifyPosition(pos, newSlPrice, pos.TakeProfit);
            if (r.IsSuccessful)
            {
                _currentTrade.BreakEvenDone = true;
                if (Verbose) Print("BE set @ {0:F5} (R={1:F2})", newSlPrice, currentR);
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

            double intendedExitPrice = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

            var r = ClosePosition(pos, normalized);
            if (r.IsSuccessful)
            {
                if (_currentTrade != null)
                {
                    double exitSlip = pos.TradeType == TradeType.Buy
                        ? (intendedExitPrice - Symbol.Bid) / Symbol.PipSize
                        : (Symbol.Ask - intendedExitPrice) / Symbol.PipSize;
                    _currentTrade.ExitSlippagePips += exitSlip;
                }
                if (Verbose)
                    Print("{0}: closed {1:F0}u remaining {2:F0}u", tag, normalized, pos.VolumeInUnits - normalized);
            }
        }

        private void ManageChandelier(Position pos)
        {
            if (!EnableChandelier) return;
            if (!_currentTrade.Partial1Done) return;
            double atr = _atrChandelier.Result.Last(1);
            if (double.IsNaN(atr) || atr <= 0) return;

            DateTime lastBarTime = Bars.OpenTimes.Last(1);
            if (lastBarTime != _currentTrade.LastChandelierUpdateTime)
            {
                _currentTrade.LastChandelierUpdateTime = lastBarTime;
                if (pos.TradeType == TradeType.Buy)
                {
                    if (Bars.HighPrices.Last(1) > _currentTrade.ChandelierPeakHigh)
                    {
                        _currentTrade.ChandelierPeakHigh = Bars.HighPrices.Last(1);
                        if (Verbose) Print("Chandelier: Buy peak updated to {0:F5}", _currentTrade.ChandelierPeakHigh);
                    }
                }
                else
                {
                    if (Bars.LowPrices.Last(1) < _currentTrade.ChandelierPeakLow)
                    {
                        _currentTrade.ChandelierPeakLow = Bars.LowPrices.Last(1);
                        if (Verbose) Print("Chandelier: Sell peak updated to {0:F5}", _currentTrade.ChandelierPeakLow);
                    }
                }
            }

            if (pos.TradeType == TradeType.Buy)
            {
                double candidate = _currentTrade.ChandelierPeakHigh - ChandelierAtrMult * atr;
                if (candidate > _currentTrade.ChandelierStop)
                    _currentTrade.ChandelierStop = candidate;

                double desiredSl = _currentTrade.ChandelierStop;
                // Clamp: cap prevents SL update from touching price (keeps trail advancing)
                double capSl = Symbol.Bid - CHANDELIER_SL_CAP_PIPS * Symbol.PipSize;
                if (desiredSl >= capSl) desiredSl = capSl;
                if (pos.StopLoss.HasValue && desiredSl <= pos.StopLoss.Value) return;
                if (!pos.StopLoss.HasValue || desiredSl > pos.StopLoss.Value)
                    ModifyPosition(pos, desiredSl, pos.TakeProfit);
            }
            else
            {
                double candidate = _currentTrade.ChandelierPeakLow + ChandelierAtrMult * atr;
                if (candidate < _currentTrade.ChandelierStop)
                    _currentTrade.ChandelierStop = candidate;

                double desiredSl = _currentTrade.ChandelierStop;
                double capSl = Symbol.Ask + CHANDELIER_SL_CAP_PIPS * Symbol.PipSize;
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
            if (barsHeld >= MaxHoldBars)
            {
                ClosePositionIfOpen("MaxHold");
            }
        }

        private double ComputeCurrentR(Position pos, double initialSlPips)
        {
            if (initialSlPips <= 0) return 0;
            double currentPipsInFavor = pos.TradeType == TradeType.Buy
                ? (Symbol.Bid - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Symbol.Ask) / Symbol.PipSize;
            return currentPipsInFavor / initialSlPips;
        }

        private double ComputeBarMaxR(Position pos, double initialSlPips)
        {
            if (initialSlPips <= 0) return 0;
            double barMaxPips = pos.TradeType == TradeType.Buy
                ? (Bars.HighPrices.Last(1) - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Bars.LowPrices.Last(1)) / Symbol.PipSize;
            return barMaxPips / initialSlPips;
        }

        private double ComputeBarMinR(Position pos, double initialSlPips)
        {
            if (initialSlPips <= 0) return 0;
            double barMinPips = pos.TradeType == TradeType.Buy
                ? (Bars.LowPrices.Last(1)  - pos.EntryPrice) / Symbol.PipSize
                : (pos.EntryPrice - Bars.HighPrices.Last(1)) / Symbol.PipSize;
            return barMinPips / initialSlPips;
        }

        // ════════════════════════════════════════════════════════════════════
        //  POSITION OPENED HOOK
        // ════════════════════════════════════════════════════════════════════
        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != BotLabel || pos.SymbolName != SymbolName) return;
            if (_currentTrade != null) return;

            if (_pendingVwapMrLimitMeta.HasValue
                && !string.IsNullOrEmpty(pos.Comment)
                && pos.Comment.Contains("|VwapMR|"))
            {
                var meta = _pendingVwapMrLimitMeta.Value;
                _currentTrade = new CloverTradeState
                {
                    PositionId               = pos.Id,
                    EntryPrice               = pos.EntryPrice,
                    InitialSlPips            = meta.slPips,
                    InitialVolumeUnits       = pos.VolumeInUnits,
                    RRRTarget                = meta.rrr,
                    BreakEvenDone            = false,
                    Partial1Done             = false,
                    Partial2Done             = false,
                    ChandelierStop           = pos.TradeType == TradeType.Buy ? double.MinValue : double.MaxValue,
                    EntryTime                = pos.EntryTime,
                    Edge                     = meta.edge,
                    Setup                    = meta.setup,
                    EntrySlippage            = 0,  // limit fills at limit price, no slippage
                    ExitSlippagePips         = 0,
                    ChandelierPeakHigh       = pos.TradeType == TradeType.Buy ? pos.EntryPrice : double.MinValue,
                    ChandelierPeakLow        = pos.TradeType == TradeType.Sell ? pos.EntryPrice : double.MaxValue,
                    LastChandelierUpdateTime = Server.Time,
                    EntryBarIndex            = Bars.Count - 1,
                    MaeRMultiple             = 0,
                    MfeRMultiple             = 0,
                    EntrySessionKey          = GetSessionKey(pos.EntryTime),
                    RecoveredWithoutMetadata = false,
                };
                _pendingVwapMrLimitMeta = null;
                _pendingVwapMrLimit     = null;
                _totalTradesOpened++;
                _tradesToday++;
                Print("LIMIT FILLED: id={0} entry={1:F5} (slip=0 by limit) edge={2} setup={3}",
                    pos.Id, pos.EntryPrice, meta.edge, meta.setup);
            }
            else
            {
                // Unknown position opened (manual or unmatched) — delegate to recovery path
                RecoverExistingPosition();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  POSITION CLOSED HOOK
        // ════════════════════════════════════════════════════════════════════
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (pos.Label != BotLabel || pos.SymbolName != SymbolName) return;
            if (_currentTrade == null || _currentTrade.PositionId != pos.Id)
            {
                _currentTrade = null;
                return;
            }

            double pnl = pos.NetProfit;
            CloverEdge edge = _currentTrade.Edge;
            // Use InitialVolumeUnits to avoid inflated R on partialed trades
            double rMultiple = _currentTrade.InitialSlPips > 0 ? pnl / (Symbol.PipValue * _currentTrade.InitialSlPips * _currentTrade.InitialVolumeUnits) : 0;
            if (!_currentTrade.RecoveredWithoutMetadata)
            {
                _rMultiples.Add(rMultiple);
                DateTime tradeDay = _currentTrade.EntryTime.Date;
                if (!_dailyRSum.ContainsKey(tradeDay)) _dailyRSum[tradeDay] = 0;
                _dailyRSum[tradeDay] += rMultiple;
            }

            _dayRealizedPnl += pnl;
            _weekRealizedPnl += pnl;

            if (_currentTrade.RecoveredWithoutMetadata)
            {
                Print("STATS-SKIP: recovered position {0} excluded from edge stats", pos.Id);
                // still update consecutive loss counter for risk gates
                if (pnl > 0) _consecutiveLosses = 0;
                else if (pnl < 0) { _consecutiveLosses++; }
            }
            else
            {
                if (pnl > 0)
                {
                    _edgeWinCount[edge]++;
                    _consecutiveLosses = 0;
                }
                else if (pnl < 0)
                {
                    _edgeLossCount[edge]++;
                    _consecutiveLosses++;
                    if (ConsecLossCoolDownHours > 0 && _consecutiveLosses >= ConsecLossTrigger)
                    {
                        _cooldownEndTime = Server.Time.AddHours(ConsecLossCoolDownHours);
                        Print("CoolDown engaged until {0:yyyy-MM-dd HH:mm} (consec losses={1})",
                            _cooldownEndTime, _consecutiveLosses);
                    }
                }
                _edgePnlSum[edge] += pnl;
                _edgeEntrySlippageSum[edge] += _currentTrade.EntrySlippage;
                _edgeSlippageSampleCount[edge]++;

                SessionKey skey = _currentTrade.EntrySessionKey;
                if (!_sessionStats.ContainsKey(skey))
                    _sessionStats[skey] = new SessionStats { TradeCount = 0, Wins = 0, PnLSum = 0 };
                SessionStats stat = _sessionStats[skey];
                stat.TradeCount++;
                if (pnl > 0) stat.Wins++;
                stat.PnLSum += pnl;
                _sessionStats[skey] = stat;

                _edgeMaeSum[edge] += _currentTrade.MaeRMultiple;
                _edgeMfeSum[edge] += _currentTrade.MfeRMultiple;
                _edgeExitSlippageSum[edge]   += _currentTrade.ExitSlippagePips;
                _edgeExitSlippageCount[edge] += 1;
            }

            if (PersistStreakCounter)
                SaveStreakToStorage();

            Print("CLOSED: id={0} edge={1} setup={2} pnl={3:F2} slip_entry={4:+0.0;-0.0;0.0}p slip_exit={5:+0.0;-0.0;0.0}p MAE={6:F2}R MFE={7:F2}R dayReal={8:F2} consecLoss={9}",
                pos.Id, edge, _currentTrade.Setup, pnl,
                _currentTrade.EntrySlippage, _currentTrade.ExitSlippagePips,
                _currentTrade.MaeRMultiple, _currentTrade.MfeRMultiple,
                _dayRealizedPnl, _consecutiveLosses);

            if (ExportTradeLogCsv)
            {
                SessionKey sk = _currentTrade.EntrySessionKey;
                _tradeLogRows.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0:yyyy-MM-dd HH:mm:ss},{1:yyyy-MM-dd HH:mm:ss},{2},{3},{4},{5},{6:F5},{7:F5},{8:F1},{9:F2},{10:F0},{11:F2},{12:F4},{13:F2},{14:F2},{15:F4},{16:F4},{17},{18}",
                    _currentTrade.EntryTime, Server.Time, SymbolName,
                    edge, _currentTrade.Setup, pos.TradeType,
                    _currentTrade.EntryPrice, pos.EntryPrice,
                    _currentTrade.InitialSlPips, _currentTrade.RRRTarget,
                    _currentTrade.InitialVolumeUnits, pnl, rMultiple,
                    _currentTrade.EntrySlippage, _currentTrade.ExitSlippagePips,
                    _currentTrade.MaeRMultiple, _currentTrade.MfeRMultiple,
                    _tradeOpenRegime, SessionBucketName(sk.SessionBucket)));
            }

            _currentTrade = null;
        }

        // ════════════════════════════════════════════════════════════════════
        //  DAILY / WEEKLY ROLLOVER + DD GATES
        // ════════════════════════════════════════════════════════════════════
        private void RolloverDailyWeekly()
        {
            DateTime today = Server.Time.Date;
            DateTime thisWeek = GetWeekMonday(Server.Time);

            if (_botDay != today)
            {
                if (_botDay != DateTime.MinValue)
                    _dailyEquityClose[_botDay] = Account.Balance;

                _botDay = today;
                _dayStartBalance = Account.Balance;
                _dayRealizedPnl = 0;
                _dailyDdBreached = false;
                _tradesToday = 0;
                if (Verbose) Print("Daily rollover. StartBalance={0:F2}", _dayStartBalance);
            }
            if (_botWeek != thisWeek)
            {
                _botWeek = thisWeek;
                _weekStartBalance = Account.Balance;
                _weekRealizedPnl = 0;
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
                double floatingDdPct = (_dayStartBalance - Account.Equity) / Math.Max(_dayStartBalance, 1) * 100.0;
                if (floatingDdPct >= MaxFloatingDailyDdPct)
                {
                    _dailyDdBreached = true;
                    ClosePositionIfOpen("FloatingDDHit");
                    Print("FLOATING DAILY DD HIT: equity={0:F2} startBal={1:F2} dd={2:F2}% >= limit {3:F2}%",
                        Account.Equity, _dayStartBalance, floatingDdPct, MaxFloatingDailyDdPct);
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
                if (Account.Balance > _runHighWaterMarkBalance)
                    _runHighWaterMarkBalance = Account.Balance;

                double trailDdPct = (_runHighWaterMarkBalance - Account.Balance) / Math.Max(_runHighWaterMarkBalance, 1) * 100.0;
                if (trailDdPct >= EquityCurveTrailPct)
                {
                    _dailyDdBreached = true;
                    _weeklyDdBreached = true;
                    ClosePositionIfOpen("EquityTrailStop");
                    Print("EQUITY CURVE TRAIL STOP: HWM={0:F2} balance={1:F2} dd={2:F2}% >= limit {3:F2}% — bot halted for session",
                        _runHighWaterMarkBalance, Account.Balance, trailDdPct, EquityCurveTrailPct);
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

            double intendedExitPrice = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

            var r = ClosePosition(pos);
            if (r.IsSuccessful)
            {
                double exitSlip = pos.TradeType == TradeType.Buy
                    ? (intendedExitPrice - Symbol.Bid) / Symbol.PipSize
                    : (Symbol.Ask - intendedExitPrice) / Symbol.PipSize;
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

                CloverEdge recoveredEdge = CloverEdge.MomoCont;
                CloverSetup recoveredSetup = CloverSetup.Trend;
                double recoveredRrr = RrrTrend;
                bool recoveredWithoutMeta = true;

                if (!string.IsNullOrEmpty(p.Comment))
                {
                    var parts = p.Comment.Split('|');
                    if (parts.Length >= 3)
                    {
                        bool edgeOk = Enum.TryParse<CloverEdge>(parts[1], out var edge);
                        bool setupOk = Enum.TryParse<CloverSetup>(parts[2], out var setup);
                        if (edgeOk && setupOk)
                        {
                            recoveredEdge = edge;
                            recoveredSetup = setup;
                            recoveredRrr = setup == CloverSetup.Trend ? RrrTrend
                                         : setup == CloverSetup.MeanReversion ? RrrMR
                                         : RrrBreakout;
                            recoveredWithoutMeta = false;
                        }
                    }
                }
                if (recoveredWithoutMeta)
                    Print("RECOVERY: comment parse failed for id={0} — edge stats will be excluded", p.Id);

                int recoveredEntryBarIdx = Bars.Count - 1;
                for (int i = Bars.Count - 1; i >= 0; i--)
                {
                    if (Bars.OpenTimes[i] <= p.EntryTime) { recoveredEntryBarIdx = i; break; }
                }
                if (Verbose) Print("RECOVERY EntryBar resolved to {0} ({1:yyyy-MM-dd HH:mm})",
                    recoveredEntryBarIdx, Bars.OpenTimes[recoveredEntryBarIdx]);

                _currentTrade = new CloverTradeState
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
                    Edge                     = recoveredEdge,
                    Setup                    = recoveredSetup,
                    ExitSlippagePips         = 0,
                    ChandelierPeakHigh       = p.TradeType == TradeType.Buy ? p.EntryPrice : double.MinValue,
                    ChandelierPeakLow        = p.TradeType == TradeType.Sell ? p.EntryPrice : double.MaxValue,
                    LastChandelierUpdateTime = Server.Time,
                    EntryBarIndex            = recoveredEntryBarIdx,
                    MaeRMultiple             = 0,
                    MfeRMultiple             = 0,
                    EntrySessionKey          = GetSessionKey(p.EntryTime),
                    RecoveredWithoutMetadata = recoveredWithoutMeta,
                };
                Print("RECOVERY: adopted open position id={0} {1} entry={2:F5} slPips={3:F1} edge={4} setup={5}",
                    p.Id, p.TradeType, p.EntryPrice, slPips, recoveredEdge, recoveredSetup);
                break;
            }
        }

        // Brokers may serve local-time Server.Time — always convert to UTC for session gates
        private DateTime NowUtc() => ServerTimeIsUtc ? Server.Time : Server.Time.ToUniversalTime();

        private static DateTime GetWeekMonday(DateTime d)
        {
            int back = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return d.Date.AddDays(-back);
        }

        private SessionKey GetSessionKey(DateTime time)
        {
            int hour = time.Hour;
            int bucket = hour < 7 ? 0 : (hour < 13 ? 1 : (hour < 20 ? 2 : 3));
            return new SessionKey { DoW = time.DayOfWeek, SessionBucket = bucket };
        }

        private string SessionBucketName(int bucket)
        {
            return bucket switch
            {
                0 => "Asia(0-7)",
                1 => "London(7-13)",
                2 => "NY(13-20)",
                3 => "Off(20-24)",
                _ => "Unknown"
            };
        }

        private void PersistAttributionJson()
        {
            if (!EnableAttributionPersistence) return;
            if (!IsLiveTradingMode())
            {
                if (Verbose) Print("Attribution persistence disabled: not running in Live mode");
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
                sb.AppendLine("  \"edge_attribution\": {");

                var edges = Enum.GetValues(typeof(CloverEdge)).Cast<CloverEdge>().ToList();
                for (int i = 0; i < edges.Count; i++)
                {
                    CloverEdge e = edges[i];
                    int w = _edgeWinCount[e];
                    int l = _edgeLossCount[e];
                    int n = w + l;
                    if (n == 0) continue;
                    double wr = (double)w / n;
                    double avg = _edgePnlSum[e] / n;
                    double avgSlip = _edgeSlippageSampleCount[e] > 0 ? _edgeEntrySlippageSum[e] / _edgeSlippageSampleCount[e] : 0;

                    sb.Append($"    \"{e}\": {{\"n\": {n}, \"wins\": {w}, \"loss\": {l}, \"wr\": {wr:F4}, \"avg_pnl\": {avg:F2}, \"entry_slip_pips\": {avgSlip:F2}}}");
                    if (i < edges.Count - 1) sb.AppendLine(",");
                    else sb.AppendLine();
                }

                sb.AppendLine("  },");
                sb.AppendLine("  \"session_stats\": {");

                var sortedSessions = _sessionStats.OrderBy(kvp => (kvp.Key.DoW, kvp.Key.SessionBucket)).ToList();
                for (int i = 0; i < sortedSessions.Count; i++)
                {
                    var entry = sortedSessions[i];
                    SessionKey key = entry.Key;
                    SessionStats stat = entry.Value;
                    double wr = stat.TradeCount > 0 ? (double)stat.Wins / stat.TradeCount : 0;
                    double avg = stat.TradeCount > 0 ? stat.PnLSum / stat.TradeCount : 0;

                    sb.Append($"    \"{key.DoW}_{SessionBucketName(key.SessionBucket)}\": {{\"n\": {stat.TradeCount}, \"wins\": {stat.Wins}, \"wr\": {wr:F4}, \"avg_pnl\": {avg:F2}}}");
                    if (i < sortedSessions.Count - 1) sb.AppendLine(",");
                    else sb.AppendLine();
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

        // ════════════════════════════════════════════════════════════════════
        //  STREAK PERSISTENCE (LocalStorage)
        // ════════════════════════════════════════════════════════════════════
        private string StreakKeyLosses   => $"CloverAlgo_ConsecLoss_{BotLabel}_{SymbolName}";
        private string StreakKeyCooldown => $"CloverAlgo_CooldownEnd_{BotLabel}_{SymbolName}";

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
                if (!string.IsNullOrEmpty(rawCooldown) && DateTime.TryParse(rawCooldown, out DateTime storedCooldown))
                {
                    _cooldownEndTime = storedCooldown;
                    if (Verbose) Print("StreakLoad: cooldownEnd={0:yyyy-MM-dd HH:mm}", _cooldownEndTime);
                }
            }
            catch (Exception ex)
            {
                Print("StreakLoad error: {0}", ex.Message);
            }
        }

        private void SaveStreakToStorage()
        {
            try
            {
                LocalStorage.SetString(StreakKeyLosses,   _consecutiveLosses.ToString());
                LocalStorage.SetString(StreakKeyCooldown, _cooldownEndTime.ToString("o"));
            }
            catch (Exception ex)
            {
                Print("StreakSave error: {0}", ex.Message);
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
                    if (plusIdx < 0) plusIdx = s.IndexOf('+', 10);  // fallback: +minutes after datetime
                    if (plusIdx < 0) { Print("NewsBlackout parse: no ± in '{0}', skipping", s); continue; }

                    string dtPart  = s.Substring(0, plusIdx).Trim();
                    string minPart = s.Substring(plusIdx + 1).Trim();

                    if (!DateTime.TryParse(dtPart, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime center))
                    { Print("NewsBlackout parse: bad datetime '{0}', skipping", dtPart); continue; }
                    if (!int.TryParse(minPart, out int halfMin))
                    { Print("NewsBlackout parse: bad minutes '{0}', skipping", minPart); continue; }

                    _newsBlackouts.Add((center, halfMin));
                    if (Verbose) Print("NewsBlackout registered: {0:yyyy-MM-dd HH:mm} ±{1}m", center, halfMin);
                }
                catch (Exception ex)
                {
                    Print("NewsBlackout parse error on '{0}': {1}", entry, ex.Message);
                }
            }
            Print("NewsBlackout: {0} window(s) loaded", _newsBlackouts.Count);
        }

        // ════════════════════════════════════════════════════════════════════
        //  ORB VOLUME CONFIRMATION
        // ════════════════════════════════════════════════════════════════════
        private bool IsVolumeExpanded()
        {
            int avail = Math.Min(OrbVolumeAvgBars, Bars.Count - 2);
            if (avail <= 0) return true;  // not enough history → don't block
            double sum = 0;
            for (int i = 2; i <= avail + 1; i++)
                sum += Bars.TickVolumes.Last(i);
            double avg = sum / avail;
            double current = Bars.TickVolumes.Last(1);
            bool expanded = current >= avg * OrbVolumeMultiplier;
            if (Verbose) Print("VolExp: tickVol={0:F0} avg={1:F0} mult={2:F1} → {3}", current, avg, OrbVolumeMultiplier, expanded ? "PASS" : "FAIL");
            return expanded;
        }

        // ════════════════════════════════════════════════════════════════════
        //  VWAP-MR LIMIT ORDER HELPER
        // ════════════════════════════════════════════════════════════════════
        private bool PlaceVwapMrLimit(TradeType dir, string reason)
        {
            if (_pendingVwapMrLimit != null) return false;  // already a pending order

            double atrPips = GetAtrPips();
            double slPips  = Math.Round(atrPips * AtrSlMultiplier + CommissionBufferPips, 1);
            if (slPips < MinSlPips) slPips = MinSlPips;
            if (slPips > MaxSlPips) slPips = MaxSlPips;
            double tpPips  = Math.Round(slPips * RrrMR, 1);
            double volume  = CalculateVolume(slPips, dir);
            if (volume < Symbol.VolumeInUnitsMin) return false;

            double offsetPrice = VwapMrLimitOffsetPips * Symbol.PipSize;
            double limitPrice  = dir == TradeType.Buy
                ? Symbol.Ask - offsetPrice
                : Symbol.Bid + offsetPrice;

            string metadata = $"|{CloverEdge.VwapMR}|{CloverSetup.MeanReversion}";
            double slAbsolute = dir == TradeType.Buy
                ? limitPrice - slPips * Symbol.PipSize
                : limitPrice + slPips * Symbol.PipSize;
            double tpAbsolute = dir == TradeType.Buy
                ? limitPrice + tpPips * Symbol.PipSize
                : limitPrice - tpPips * Symbol.PipSize;
            var result = PlaceLimitOrder(dir, SymbolName, volume, limitPrice, BotLabel, slAbsolute, tpAbsolute, null, metadata);
            if (result.IsSuccessful && result.PendingOrder != null)
            {
                _pendingVwapMrLimit         = result.PendingOrder;
                _pendingLimitPlacedBarIndex = Bars.Count - 1;
                _pendingVwapMrLimitMeta     = (CloverEdge.VwapMR, CloverSetup.MeanReversion, RrrMR, slPips, Server.Time);
                Print("LIMIT PLACED: {0} {1} @ {2:F5} sl={3:F1}p tp={4:F1}p | {5}", dir, SymbolName, limitPrice, slPips, tpPips, reason);
                return true;
            }
            Print("LIMIT FAILED: {0}", result.Error);
            return false;
        }

        private void CancelStaleLimitOrder()
        {
            if (_pendingVwapMrLimit == null) return;

            // Check if the order was already filled or cancelled externally
            bool stillPending = false;
            foreach (var po in PendingOrders)
            {
                if (po.Id == _pendingVwapMrLimit.Id) { stillPending = true; break; }
            }
            if (!stillPending)
            {
                _pendingVwapMrLimit     = null;
                _pendingVwapMrLimitMeta = null;
                return;
            }

            int barsElapsed = Bars.Count - 1 - _pendingLimitPlacedBarIndex;
            if (barsElapsed >= VwapMrLimitTimeoutBars)
            {
                CancelPendingOrder(_pendingVwapMrLimit);
                Print("LIMIT CANCELLED: timeout after {0} bars", barsElapsed);
                _pendingVwapMrLimit     = null;
                _pendingVwapMrLimitMeta = null;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  CSV TRADE LOG EXPORT
        // ════════════════════════════════════════════════════════════════════
        private void ExportTradeLogCsvFile()
        {
            if (!ExportTradeLogCsv || _tradeLogRows.Count == 0) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("EntryTime,ExitTime,Symbol,Edge,Setup,Direction,EntryPrice,ExitPrice,SLPips,RRR,VolumeUnits,PnL,RMultiple,EntrySlipP,ExitSlipP,MAE_R,MFE_R,Regime,SessionBucket");
                foreach (string row in _tradeLogRows)
                    sb.AppendLine(row);

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

    }
}
