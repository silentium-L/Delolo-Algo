// ═══════════════════════════════════════════════════════════════════════════════
//  10-Fold Bot  │  Multi-Strategy Scoring cBot
//  Platform     │  cTrader (Pepperstone Razor Account)
//  Architecture │  Modular Scoring Engine – Pullback / Mean Reversion
//  Version      │  2.10.0 (Tuned Defaults: Risk-Profile optimized, earlier BE, tighter protection)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum SlMethod   { AtrBased, SwingHighLow, FixedPips }
    public enum TpMethod   { Rrr, AtrMultiplier, NextSwingExtreme, Runner, IntervalLot }
    public enum TrailingType { None, Chandelier, FastEma }
    public enum EmaTrailFilter { StrictClose, DoubleClose }
    public enum IntervalBasis { Pips, AtrMultiple }

    // Basis für Risikoberechnung
    public enum RiskBase { Balance, Equity }

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
    public class TenFoldBot : Robot
    {
        // ════════════════════════════════════════════════════════════════════
        //  PARAMETERS
        // ════════════════════════════════════════════════════════════════════

        // ── 01 · Time & Day Filter ───────────────────────────────────────────
        [Parameter("Enable Time Filter",
            Group = "01 · Time & Day Filter", DefaultValue = true)]
        public bool EnableTimeFilter { get; set; }

        [Parameter("Session Start Hour (Server Time, 0–23)",
            Group = "01 · Time & Day Filter", DefaultValue = 8, MinValue = 0, MaxValue = 23)]
        public int SessionStartHour { get; set; }

        [Parameter("Session Start Minute (0–59)",
            Group = "01 · Time & Day Filter", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int SessionStartMinute { get; set; }

        [Parameter("Session End Hour (Server Time, 0–23)",
            Group = "01 · Time & Day Filter", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int SessionEndHour { get; set; }

        [Parameter("Session End Minute (0–59)",
            Group = "01 · Time & Day Filter", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int SessionEndMinute { get; set; }

        [Parameter("Block NEW Trades on Fridays",
            Group = "01 · Time & Day Filter", DefaultValue = true)]
        public bool BlockFridayNewTrades { get; set; }

        // ── 01b · Volatility & News ───────────────────────────────────────
        [Parameter("Enable Volatility Filter",
            Group = "01b · Volatility & News", DefaultValue = false)]
        public bool EnableVolatilityFilter { get; set; }

        [Parameter("ADR Period",
            Group = "01b · Volatility & News", DefaultValue = 14, MinValue = 1, MaxValue = 100)]
        public int AdrPeriod { get; set; }

        [Parameter("Max ADR Ratio",
            Group = "01b · Volatility & News", DefaultValue = 1.5, MinValue = 1.0, Step = 0.1)]
        public double MaxAdrRatio { get; set; }

        [Parameter("Enable News Blocker",
            Group = "01b · Volatility & News", DefaultValue = false)]
        public bool EnableNewsBlocker { get; set; }

        [Parameter("News Time Windows",
            Group = "01b · Volatility & News", DefaultValue = "14:15-14:45, 15:45-16:15")]
        public string NewsTimeWindows { get; set; }

        // ── 02 · Spread Protection ───────────────────────────────────────────
        [Parameter("Max Allowed Spread (Pips)",
            Group = "02 · Spread Protection", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double MaxAllowedSpread { get; set; }

        // ── 03 · HTF Regime Filter ───────────────────────────────────────────
        [Parameter("Enable HTF Trend Filter",
            Group = "03 · HTF Regime Filter", DefaultValue = true)]
        public bool EnableHtfFilter { get; set; }

        [Parameter("HTF Timeframe",
            Group = "03 · HTF Regime Filter", DefaultValue = "Hour4")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("HTF EMA Period",
            Group = "03 · HTF Regime Filter", DefaultValue = 50, MinValue = 5, MaxValue = 500)]
        public int HtfEmaPeriod { get; set; }

        // ── 04 · Swap Evasion ────────────────────────────────────────────────
        [Parameter("Enable Swap Evasion Logic",
            Group = "04 · Swap Evasion", DefaultValue = true)]
        public bool EnableSwapEvasion { get; set; }

        [Parameter("Rollover Check Hour (Server Time)",
            Group = "04 · Swap Evasion", DefaultValue = 23, MinValue = 0, MaxValue = 23)]
        public int RolloverHour { get; set; }

        [Parameter("Rollover Check Minute",
            Group = "04 · Swap Evasion", DefaultValue = 50, MinValue = 0, MaxValue = 59)]
        public int RolloverMinute { get; set; }

        // ── 05 · Module: EMA ─────────────────────────────────────────────────
        [Parameter("Enable EMA Module",
            Group = "05 · Module: EMA", DefaultValue = true)]
        public bool EnableEmaModule { get; set; }

        [Parameter("EMA Fast Period",
            Group = "05 · Module: EMA", DefaultValue = 20, MinValue = 2, MaxValue = 500)]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period",
            Group = "05 · Module: EMA", DefaultValue = 50, MinValue = 5, MaxValue = 500)]
        public int EmaSlowPeriod { get; set; }

        [Parameter("EMA Max Points (1–3)",
            Group = "05 · Module: EMA", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int EmaMaxWeight { get; set; }

        // ── 06 · Module: Bollinger Bands ─────────────────────────────────────
        [Parameter("Enable Bollinger Bands Module",
            Group = "06 · Module: Bollinger Bands", DefaultValue = true)]
        public bool EnableBbModule { get; set; }

        [Parameter("BB Period",
            Group = "06 · Module: Bollinger Bands", DefaultValue = 20, MinValue = 5, MaxValue = 500)]
        public int BbPeriod { get; set; }

        [Parameter("BB Standard Deviations",
            Group = "06 · Module: Bollinger Bands", DefaultValue = 2.0, MinValue = 0.5, Step = 0.1)]
        public double BbStdDev { get; set; }

        [Parameter("BB Max Points (1–3)",
            Group = "06 · Module: Bollinger Bands", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int BbMaxWeight { get; set; }

        // ── 07 · Module: Supertrend ──────────────────────────────────────────
        [Parameter("Enable Supertrend Module",
            Group = "07 · Module: Supertrend", DefaultValue = true)]
        public bool EnableSupertrendModule { get; set; }

        [Parameter("Supertrend ATR Period",
            Group = "07 · Module: Supertrend", DefaultValue = 10, MinValue = 2, MaxValue = 200)]
        public int SupertrendAtrPeriod { get; set; }

        [Parameter("Supertrend Multiplier Factor",
            Group = "07 · Module: Supertrend", DefaultValue = 3.0, MinValue = 0.5, Step = 0.1)]
        public double SupertrendFactor { get; set; }

        [Parameter("Supertrend Max Points (1–3)",
            Group = "07 · Module: Supertrend", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int SupertrendMaxWeight { get; set; }

        // ── 08 · Module: Price Action ────────────────────────────────────────
        [Parameter("Enable Price Action Patterns Module",
            Group = "08 · Module: Price Action", DefaultValue = true)]
        public bool EnablePatternsModule { get; set; }

        [Parameter("Pattern Lookback (Bars)",
            Group = "08 · Module: Price Action", DefaultValue = 3, MinValue = 1, MaxValue = 20)]
        public int PatternLookback { get; set; }

        [Parameter("Patterns Max Points (1–3)",
            Group = "08 · Module: Price Action", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int PatternsMaxWeight { get; set; }

        // ── 09 · Module: Fibonacci ───────────────────────────────────────────
        [Parameter("Enable Fibonacci Module",
            Group = "09 · Module: Fibonacci", DefaultValue = true)]
        public bool EnableFiboModule { get; set; }

        [Parameter("Fibonacci Swing Lookback (Bars)",
            Group = "09 · Module: Fibonacci", DefaultValue = 100, MinValue = 10, MaxValue = 500)]
        public int FiboSwingLookback { get; set; }

        [Parameter("Fibonacci Tolerance Zone (% of swing)",
            Group = "09 · Module: Fibonacci", DefaultValue = 2.0, MinValue = 0.5, Step = 0.5)]
        public double FiboTolerancePercent { get; set; }

        [Parameter("Fibonacci Max Points (1–3)",
            Group = "09 · Module: Fibonacci", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int FiboMaxWeight { get; set; }

        // ── 10 · Module: Oscillators ─────────────────────────────────────────
        [Parameter("Enable Oscillators Module",
            Group = "10 · Module: Oscillators", DefaultValue = true)]
        public bool EnableOscModule { get; set; }

        [Parameter("RSI Period",
            Group = "10 · Module: Oscillators", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Oversold Level (Long signal)",
            Group = "10 · Module: Oscillators", DefaultValue = 35.0, MinValue = 10.0, MaxValue = 49.0)]
        public double RsiOversold { get; set; }

        [Parameter("RSI Overbought Level (Short signal)",
            Group = "10 · Module: Oscillators", DefaultValue = 65.0, MinValue = 51.0, MaxValue = 90.0)]
        public double RsiOverbought { get; set; }

        [Parameter("Stochastic %K Period",
            Group = "10 · Module: Oscillators", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int StochKPeriod { get; set; }

        [Parameter("Stochastic %K Slowing",
            Group = "10 · Module: Oscillators", DefaultValue = 3, MinValue = 1, MaxValue = 20)]
        public int StochKSlowing { get; set; }

        [Parameter("Stochastic %D Period",
            Group = "10 · Module: Oscillators", DefaultValue = 3, MinValue = 1, MaxValue = 20)]
        public int StochDPeriod { get; set; }

        [Parameter("Oscillators Max Points (1–3)",
            Group = "10 · Module: Oscillators", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int OscMaxWeight { get; set; }

        // ── 11 · Module: S/R + VWAP ──────────────────────────────────────────
        [Parameter("Enable S/R + VWAP Module",
            Group = "11 · Module: S/R + VWAP", DefaultValue = true)]
        public bool EnableSrModule { get; set; }

        [Parameter("S/R Zone Tolerance (Pips)",
            Group = "11 · Module: S/R + VWAP", DefaultValue = 5.0, MinValue = 0.5, Step = 0.5)]
        public double SrZoneTolerance { get; set; }

        [Parameter("S/R Max Points (1–3)",
            Group = "11 · Module: S/R + VWAP", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int SrMaxWeight { get; set; }

        // ── 12 · Scoring & Consensus ─────────────────────────────────────────
        [Parameter("Consensus Ratio (0.30–1.00)",
            Group = "12 · Scoring & Consensus", DefaultValue = 0.70, MinValue = 0.30, MaxValue = 1.0, Step = 0.05)]
        public double ConsensusRatio { get; set; }

        [Parameter("Enable Verbose Score Logging",
            Group = "12 · Scoring & Consensus", DefaultValue = false)]
        public bool EnableVerboseScoreLogging { get; set; }

        // ── 13 · Position Sizing & Risk ──────────────────────────────────────
        [Parameter("Min Risk % of Balance (at Min-Score)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double MinRiskPercent { get; set; }

        [Parameter("Max Risk % of Balance (at Max-Score)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 1.5, MinValue = 0.05, MaxValue = 10.0, Step = 0.05)]
        public double MaxRiskPercent { get; set; }

        // P5: Balance = stabiler bei Drawdowns; Equity = dynamischer, reagiert auf offene P&L
        [Parameter("Risk Base",
            Group = "13 · Position Sizing & Risk", DefaultValue = RiskBase.Balance)]
        public RiskBase RiskBaseMode { get; set; }

        [Parameter("Min Lot Size (Hard Floor)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double MinLotSize { get; set; }

        [Parameter("Max Lot Size (Hard Cap)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 5.0, MinValue = 0.01, Step = 0.01)]
        public double MaxLotSize { get; set; }

        // ── 14 · Stop Loss ───────────────────────────────────────────────────
        [Parameter("SL Calculation Method",
            Group = "14 · Stop Loss", DefaultValue = SlMethod.AtrBased)]
        public SlMethod StopLossMethod { get; set; }

        [Parameter("ATR SL Period",
            Group = "14 · Stop Loss", DefaultValue = 14, MinValue = 2, MaxValue = 200)]
        public int AtrSlPeriod { get; set; }

        [Parameter("ATR SL Multiplier",
            Group = "14 · Stop Loss", DefaultValue = 2.0, MinValue = 0.25, Step = 0.25)]
        public double AtrSlMultiplier { get; set; }

        [Parameter("Swing High/Low Lookback (Bars)",
            Group = "14 · Stop Loss", DefaultValue = 20, MinValue = 3, MaxValue = 200)]
        public int SwingSlLookback { get; set; }

        [Parameter("Fixed SL Distance (Pips)",
            Group = "14 · Stop Loss", DefaultValue = 20.0, MinValue = 1.0, Step = 1.0)]
        public double FixedSlPips { get; set; }

        [Parameter("SL Buffer Pips (added to calculated SL)",
            Group = "14 · Stop Loss", DefaultValue = 1.0, MinValue = 0.0, Step = 0.5)]
        public double SlBufferPips { get; set; }

        // ── 15 · Take Profit ─────────────────────────────────────────────────
        [Parameter("TP Calculation Method",
            Group = "15 · Take Profit", DefaultValue = TpMethod.Rrr)]
        public TpMethod TakeProfitMethod { get; set; }

        [Parameter("RRR Target (e.g. 2.0 = 1:2 R:R)",
            Group = "15 · Take Profit", DefaultValue = 2.5, MinValue = 0.5, Step = 0.25)]
        public double RrrTarget { get; set; }

        [Parameter("ATR TP Multiplier",
            Group = "15 · Take Profit", DefaultValue = 3.0, MinValue = 0.5, Step = 0.25)]
        public double AtrTpMultiplier { get; set; }

        [Parameter("Next Swing Lookback (Bars)",
            Group = "15 · Take Profit", DefaultValue = 30, MinValue = 5, MaxValue = 500)]
        public int SwingTpLookback { get; set; }

        // ── 15b · Interval Lot Take Profit (v2.8.0) ──────────────────────────
        //  Schließt systematisch feste Lot-Mengen in gleichmäßigen Intervallen.
        //  Aktiv wenn TakeProfitMethod = IntervalLot gewählt ist.
        //  Beispiel: IntervalPips=10, LotsPerInterval=0.01
        //    → Bei +10p wird 0.01 Lot geschlossen
        //    → Bei +20p wird nochmal 0.01 Lot geschlossen
        //    → Usw. bis Position ≤ MinRunnerLots
        [Parameter("Interval Basis (Pips or ATR-Multiple)",
            Group = "15b · Interval Lot TP", DefaultValue = IntervalBasis.Pips)]
        public IntervalBasis IntervalTpBasis { get; set; }

        [Parameter("Interval Distance (Pips) – only if Basis=Pips",
            Group = "15b · Interval Lot TP", DefaultValue = 10.0, MinValue = 1.0, Step = 0.5)]
        public double IntervalPips { get; set; }

        [Parameter("Interval Distance (ATR-Multiple) – only if Basis=AtrMultiple",
            Group = "15b · Interval Lot TP", DefaultValue = 0.5, MinValue = 0.1, Step = 0.1)]
        public double IntervalAtrMultiple { get; set; }

        [Parameter("Lots to Close per Interval",
            Group = "15b · Interval Lot TP", DefaultValue = 0.01, MinValue = 0.01, Step = 0.01)]
        public double LotsPerInterval { get; set; }

        [Parameter("Min Runner Lots (keep running, 0 = close all)",
            Group = "15b · Interval Lot TP", DefaultValue = 0.01, MinValue = 0.0, Step = 0.01)]
        public double MinRunnerLots { get; set; }

        [Parameter("Max Intervals (0 = unlimited)",
            Group = "15b · Interval Lot TP", DefaultValue = 0, MinValue = 0, MaxValue = 100)]
        public int MaxIntervals { get; set; }

        // ── 16 · Break-Even Engine ───────────────────────────────────────────
        [Parameter("Enable Break-Even",
            Group = "16 · Break-Even Engine", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("Break-Even Trigger (R-Multiple)",
            Group = "16 · Break-Even Engine", DefaultValue = 0.7, MinValue = 0.1, Step = 0.1)]
        public double BeRMultiple { get; set; }

        [Parameter("Break-Even Offset (Pips beyond Entry)",
            Group = "16 · Break-Even Engine", DefaultValue = 1.0, MinValue = 0.0, Step = 0.1)]
        public double BeOffsetPips { get; set; }

        // ── 17 · Partial Close ───────────────────────────────────────────────
        [Parameter("Enable Partial Close Level 1",
            Group = "17 · Partial Close", DefaultValue = true)]
        public bool EnablePartial1 { get; set; }

        [Parameter("Level 1 – Profit Trigger (R-Multiple)",
            Group = "17 · Partial Close", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1)]
        public double Partial1TriggerR { get; set; }

        [Parameter("Level 1 – Close % of Position",
            Group = "17 · Partial Close", DefaultValue = 33.0, MinValue = 1.0, MaxValue = 99.0, Step = 1.0)]
        public double Partial1Percent { get; set; }

        [Parameter("Enable Partial Close Level 2",
            Group = "17 · Partial Close", DefaultValue = true)]
        public bool EnablePartial2 { get; set; }

        [Parameter("Level 2 – Profit Trigger (R-Multiple)",
            Group = "17 · Partial Close", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double Partial2TriggerR { get; set; }

        [Parameter("Level 2 – Close % of Position",
            Group = "17 · Partial Close", DefaultValue = 33.0, MinValue = 1.0, MaxValue = 99.0, Step = 1.0)]
        public double Partial2Percent { get; set; }

        [Parameter("Enable Partial Close Level 3",
            Group = "17 · Partial Close", DefaultValue = false)]
        public bool EnablePartial3 { get; set; }

        [Parameter("Level 3 – Profit Trigger (R-Multiple)",
            Group = "17 · Partial Close", DefaultValue = 3.0, MinValue = 0.1, Step = 0.1)]
        public double Partial3TriggerR { get; set; }

        [Parameter("Level 3 – Close % of Position",
            Group = "17 · Partial Close", DefaultValue = 33.0, MinValue = 1.0, MaxValue = 99.0, Step = 1.0)]
        public double Partial3Percent { get; set; }

        [Parameter("Min Initial Volume for Partials to Activate (Lots)",
            Group = "17 · Partial Close", DefaultValue = 0.03, MinValue = 0.01, Step = 0.01)]
        public double MinVolumeForPartials { get; set; }

        // ── 18 · Trailing Stop ───────────────────────────────────────────────
        [Parameter("Trailing Stop Algorithm",
            Group = "18 · Trailing Stop", DefaultValue = TrailingType.Chandelier)]
        public TrailingType TrailingStopType { get; set; }

        [Parameter("Chandelier ATR Period",
            Group = "18 · Trailing Stop", DefaultValue = 22, MinValue = 2, MaxValue = 200)]
        public int ChandelierAtrPeriod { get; set; }

        [Parameter("Chandelier ATR Multiplier",
            Group = "18 · Trailing Stop", DefaultValue = 2.5, MinValue = 0.5, Step = 0.25)]
        public double ChandelierAtrMultiplier { get; set; }

        [Parameter("Fast EMA Trailing Period",
            Group = "18 · Trailing Stop", DefaultValue = 34, MinValue = 2, MaxValue = 500)]
        public int TrailingEmaPeriod { get; set; }

        [Parameter("Fast EMA Trail Filter Mode",
            Group = "18 · Trailing Stop", DefaultValue = EmaTrailFilter.StrictClose)]
        public EmaTrailFilter EmaTrailingFilter { get; set; }

        [Parameter("Min SL Move to Send API Request (Pips) – Anti-Spam",
            Group = "18 · Trailing Stop", DefaultValue = 1.0, MinValue = 0.1, Step = 0.1)]
        public double MinPipsToModifySl { get; set; }

        // ── 19 · Exit Logic ──────────────────────────────────────────────────
        [Parameter("Enable Reversal Exit",
            Group = "19 · Exit Logic", DefaultValue = true)]
        public bool EnableReversalExit { get; set; }

        [Parameter("Enable Weekend Protection (Friday close)",
            Group = "19 · Exit Logic", DefaultValue = true)]
        public bool EnableWeekendClose { get; set; }

        [Parameter("Weekend Close – Friday Hour (Server Time)",
            Group = "19 · Exit Logic", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int WeekendCloseHour { get; set; }

        [Parameter("Weekend Close – Friday Minute",
            Group = "19 · Exit Logic", DefaultValue = 55, MinValue = 0, MaxValue = 59)]
        public int WeekendCloseMinute { get; set; }

        [Parameter("Enable HTF Trend Break Exit",
            Group = "19 · Exit Logic", DefaultValue = true)]
        public bool EnableHtfBreakExit { get; set; }

        [Parameter("Enable RSI Panic Exit",
            Group = "19 · Exit Logic", DefaultValue = true)]
        public bool EnableRsiPanicExit { get; set; }

        [Parameter("RSI Panic Level – Long (exit if RSI exceeds)",
            Group = "19 · Exit Logic", DefaultValue = 85.0, MinValue = 60.0, MaxValue = 99.0)]
        public double RsiPanicLong { get; set; }

        [Parameter("RSI Panic Level – Short (exit if RSI falls below)",
            Group = "19 · Exit Logic", DefaultValue = 15.0, MinValue = 1.0, MaxValue = 40.0)]
        public double RsiPanicShort { get; set; }

        // ── 20 · Account Protection ──────────────────────────────────────────
        [Parameter("Max Daily Drawdown % (halts new entries)",
            Group = "20 · Account Protection", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 100.0, Step = 0.1)]
        public double MaxDailyDrawdownPercent { get; set; }

        // P4: Kein klassisches Exposure-Konzept – summiert nur Floating Losses
        [Parameter("Max Floating Loss on Open Positions (% of Balance)",
            Group = "20 · Account Protection", DefaultValue = 4.0, MinValue = 0.1, MaxValue = 100.0, Step = 0.1)]
        public double MaxFloatingLossPercent { get; set; }

        [Parameter("Max Trades per Day (0 = off)",
            Group = "20 · Account Protection", DefaultValue = 3, MinValue = 0)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Consecutive Losses (0 = off)",
            Group = "20 · Account Protection", DefaultValue = 2, MinValue = 0)]
        public int MaxConsecutiveLosses { get; set; }

        [Parameter("Cooldown Minutes after Loss Streak",
            Group = "20 · Account Protection", DefaultValue = 120, MinValue = 1)]
        public int CooldownMinutesAfterLossStreak { get; set; }

        // ── 21 · Dashboard ───────────────────────────────────────────────────
        [Parameter("Show On-Chart Dashboard",
            Group = "21 · Dashboard", DefaultValue = true)]
        public bool ShowDashboard { get; set; }

        [Parameter("Dashboard Corner (TopLeft / TopRight / BottomLeft / BottomRight)",
            Group = "21 · Dashboard", DefaultValue = "TopLeft")]
        public string DashboardCorner { get; set; }

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
        private List<TimeWindow> _parsedNewsWindows = new List<TimeWindow>();
        private Bars     _dailyBars;


        // P2: OnStop Tracking
        private DateTime _startTime;
        private int      _totalTradesOpened = 0;

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

        private const string BotLabel = "10-Fold Bot";

        // ════════════════════════════════════════════════════════════════════
        //  OnStart
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            Print("╔══════════════════════════════════════════════╗");
            Print("║   10-Fold Bot  v2.10.0 │  Starting           ║");
            Print("╚══════════════════════════════════════════════╝");
            _startTime = Server.Time;
            Print("Symbol={0} | TF={1} | Balance={2:F2} {3}",
                SymbolName, TimeFrame, Account.Balance, Account.Asset.Name);

            ValidateParameters();

            Positions.Closed += OnPositionClosed;

            if (EnableVolatilityFilter)
                _dailyBars = MarketData.GetBars(TimeFrame.Daily);

            if (EnableNewsBlocker)
                ParseNewsWindows();

            if (!_botInStandby)
            {
                InitializeIndicators();
                CalculateScoreThresholds();
                RecoverExistingPosition();
            }

            ResetDailyState();

            if (ShowDashboard)
                InitializeDashboard();

            Print("Init complete. Standby={0} | MaxScore={1} | MinRequired={2} ({3:P0})",
                _botInStandby, _maxPossibleScore, _minRequiredScore, ConsensusRatio);
            Print("Dashboard: {0} | Corner: {1}", ShowDashboard ? "ON" : "OFF", DashboardCorner);
            Print("Risk range: {0:F2}% – {1:F2}% | SL: {2} | TP: {3}",
                MinRiskPercent, MaxRiskPercent, StopLossMethod, TakeProfitMethod);
            Print("Trailing: {0} | BE: {1} (trigger {2:F1}R +{3:F1}p offset)",
                TrailingStopType, EnableBreakEven ? "ON" : "OFF", BeRMultiple, BeOffsetPips);
            Print("VerboseScoreLogging: {0}", EnableVerboseScoreLogging ? "ON" : "OFF");

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
            Print("║   10-Fold Bot  v2.10.0 │  Stopped            ║");
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
        }

        // ─────────────────────────────────────────────────────────────────────
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

            if (MinLotSize > MaxLotSize)
            {
                Print("ERROR: MinLotSize ({0}) > MaxLotSize ({1}). Bot entering Standby!", MinLotSize, MaxLotSize);
                _botInStandby = true;
                return;
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
                if (LotsPerInterval < MinLotSize)
                    Print("WARNING: LotsPerInterval ({0:F2}) < MinLotSize ({1:F2}) – kann zu gescheiterten Teilschließungen führen.",
                        LotsPerInterval, MinLotSize);

                if (MinRunnerLots > 0 && MinRunnerLots < MinLotSize)
                    Print("WARNING: MinRunnerLots ({0:F2}) < MinLotSize ({1:F2}) – setze entweder 0 oder >= MinLotSize.",
                        MinRunnerLots, MinLotSize);

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
                atrAtEntry = _atrSl.Result.LastValue;
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

            _currentTrade = new TradeState
            {
                PositionId           = found.Id,
                EntryPrice           = entry,
                InitialSlPips        = slPips,
                InitialVolume        = found.VolumeInUnits,   // Original unbekannt – current als Proxy
                BreakEvenDone        = beDone,
                Partial1Done         = true,                  // Konservativ: kein Re-Trigger nach Restart
                Partial2Done         = true,
                Partial3Done         = true,
                ChandelierStopLong   = isLong  && found.StopLoss.HasValue ? found.StopLoss.Value : 0,
                ChandelierStopShort  = !isLong && found.StopLoss.HasValue ? found.StopLoss.Value : double.MaxValue,
                ConsecutiveEmaCloses = 0,
                IntervalsTriggered   = intervalsTriggered,
                IntervalAtrAtEntry   = atrAtEntry
            };

            Print("RECOVERY: Adopted open position Id={0} {1} Entry={2:F5} SL={3:F1}p Vol={4:F0}u | " +
                  "BE-done={5} Partials→done (no retrigger) Intervals={6}",
                found.Id, found.TradeType, entry, slPips, found.VolumeInUnits,
                beDone, intervalsTriggered);
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

            if (_maxPossibleScore == 0)
            {
                Print("CRITICAL: Zero modules enabled – permanent Standby.");
                _botInStandby = true;
                return;
            }

            _minRequiredScore = (int)Math.Ceiling(_maxPossibleScore * ConsensusRatio);
            Print("  Thresholds: Max={0} MinRequired={1} ({2:P0})", _maxPossibleScore, _minRequiredScore, ConsensusRatio);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ResetDailyState
        // ─────────────────────────────────────────────────────────────────────
        private void ResetDailyState()
        {
            // P7: Beim allerersten Reset nach OnStart einen Hinweis ausgeben
            bool isFirstReset = (_lastDailyResetDate == DateTime.MinValue);
            if (isFirstReset)
                Print("INFO: Daily-DD-Zähler startet neu. Falls der Bot mitten am Tag gestartet wurde, " +
                      "beginnt die DD-Berechnung ab jetzt (keine Persistenz über Restarts).");

            _dayStartEquity         = Account.Equity;
            _dailyDrawdownBreached  = false;
            _rolloverCheckDoneToday = false;
            _weekendCloseFired      = false;
            _tradesToday            = 0;
            _lastDailyResetDate     = Server.Time;
            // _currentTrade wird NICHT gecleart – ein laufender Trade
            // überlebt den Tageswechsel. Nur tagesgebundene Flags resetten.
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
                double htfEma   = _htfEma.Result.LastValue;
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
                int best = Math.Max(_cachedLongScore, _cachedShortScore);
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
                "║  10-FOLD BOT  v2.8.0      ║"  + nl +
                "╚═══════════════════════════╝"  + nl +
                string.Format("  Status   : {0}", botStatus)              + nl +
                line                                                        + nl +
                string.Format("  Symbol   : {0}", SymbolName)              + nl +
                string.Format("  Timeframe: {0}", TimeFrame)               + nl +
                string.Format("  Spread   : {0:F1}p  (max {1:F1}p)", spreadPips, MaxAllowedSpread) + nl +
                string.Format("  HTF Trend: {0}  [{1}]", htfStatus, HtfTimeFrame)                  + nl +
                line                                                        + nl +
                string.Format("  Score    : {0}", scoreStr)                + nl +
                string.Format("  FloatLoss: {0:F2}%  (max {1:F1}%)", openRiskPct, MaxFloatingLossPercent) + nl +
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
                case "TopRight":    return (VerticalAlignment.Top,    HorizontalAlignment.Right);
                case "BottomLeft":  return (VerticalAlignment.Bottom, HorizontalAlignment.Left);
                case "BottomRight": return (VerticalAlignment.Bottom, HorizontalAlignment.Right);
                default:            return (VerticalAlignment.Top,    HorizontalAlignment.Left);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  OnBar  – Market Filter + Entry Scoring
        // ════════════════════════════════════════════════════════════════════
        protected override void OnBar()
        {
            if (Server.Time.Date > _lastDailyResetDate.Date)
                ResetDailyState();

            if (_botInStandby)          { Print("OnBar: Bot in Standby – skipped."); return; }
            if (_dailyDrawdownBreached) { Print("OnBar: Daily drawdown breached – no new entries."); return; }
            if (_currentTrade != null)  return;

            if (EnableSupertrendModule) UpdateSupertrendState();

            // VWAP einmal pro Bar cachen (nicht jeden Tick neu berechnen)
            if (EnableSrModule) _cachedVwap = ComputeDailyVwap();

            bool longTradable  = IsMarketTradable(TradeType.Buy);
            bool shortTradable = IsMarketTradable(TradeType.Sell);

            // Scores berechnen und cachen (Dashboard nutzt Cache, kein Tick-Spam)
            _cachedLongScore  = longTradable  ? CalculateEntryScore(TradeType.Buy)  : 0;
            _cachedShortScore = shortTradable ? CalculateEntryScore(TradeType.Sell) : 0;

            if (_cachedLongScore >= _minRequiredScore)
            {
                double risk = CalculateRiskPercent(_cachedLongScore);
                Print("Entry candidate LONG: Score={0}/{1}  Risk={2:F2}%  – calling TryOpenTrade",
                    _cachedLongScore, _maxPossibleScore, risk);
                // P3: Score-Breakdown nur bei Entry-Kandidat und VerboseLogging
                if (EnableVerboseScoreLogging)
                    LogScoreBreakdown(TradeType.Buy, _cachedLongScore);
                TryOpenTrade(TradeType.Buy, _cachedLongScore);
            }
            else if (_cachedShortScore >= _minRequiredScore)
            {
                double risk = CalculateRiskPercent(_cachedShortScore);
                Print("Entry candidate SHORT: Score={0}/{1}  Risk={2:F2}%  – calling TryOpenTrade",
                    _cachedShortScore, _maxPossibleScore, risk);
                // P3: Score-Breakdown nur bei Entry-Kandidat und VerboseLogging
                if (EnableVerboseScoreLogging)
                    LogScoreBreakdown(TradeType.Sell, _cachedShortScore);
                TryOpenTrade(TradeType.Sell, _cachedShortScore);
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
            if (_dailyDrawdownBreached) return;

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
                    if (logRejections) Print("Trade rejected [{0}]: Session filter. ServerTime={1:HH:mm} not in {2:hh\\:mm}–{3:hh\\:mm}.",
                        direction, Server.Time, start, end);
                    return false;
                }
            }

            if (BlockFridayNewTrades && Server.Time.DayOfWeek == DayOfWeek.Friday)
            {
                TimeSpan weekendCloseTime = new TimeSpan(WeekendCloseHour, WeekendCloseMinute, 0);
                if (Server.Time.TimeOfDay >= weekendCloseTime)
                {
                    if (logRejections) Print("Trade rejected [{0}]: New trades blocked on Friday after {1:hh\\:mm}.",
                        direction, weekendCloseTime);
                    return false;
                }
            }

            if (Server.Time < _cooldownEndTime)
            {
                if (logRejections) Print("Trade rejected [{0}]: Cooldown aktiv bis {1:HH:mm}.",
                    direction, _cooldownEndTime);
                return false;
            }

            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
            {
                if (logRejections) Print("Trade rejected [{0}]: Max Trades pro Tag ({1}) erreicht.",
                    direction, MaxTradesPerDay);
                return false;
            }

            if (EnableNewsBlocker)
            {
                TimeWindow activeWindow;
                if (IsInsideNewsWindow(Server.Time.TimeOfDay, out activeWindow))
                {
                    if (logRejections) Print("Trade rejected [{0}]: News-Fenster ({1:hh\\:mm}-{2:hh\\:mm}).",
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
                    if (logRejections) Print("Trade rejected [{0}]: Volatility Filter. Heutige Range ({1:F1}p) ueberschreitet ADR-Limit ({2:F1}p * {3:F1}).",
                        direction, currentDayRangePips, adrPips, MaxAdrRatio);
                    return false;
                }
            }

            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            if (spreadPips > MaxAllowedSpread)
            {
                if (logRejections) Print("Trade rejected [{0}]: Spread {1:F2} > MaxAllowedSpread {2:F1} pips.",
                    direction, spreadPips, MaxAllowedSpread);
                return false;
            }

            if (EnableHtfFilter && _htfBars != null && _htfEma != null)
            {
                double htfClose = _htfBars.ClosePrices.Last(1);
                double htfEma   = _htfEma.Result.LastValue;

                if (direction == TradeType.Buy && htfClose < htfEma)
                {
                    if (logRejections) Print("Trade rejected [Long]: HTF filter. HTF close {0:F5} < HTF EMA {1:F5}.", htfClose, htfEma);
                    return false;
                }
                if (direction == TradeType.Sell && htfClose > htfEma)
                {
                    if (logRejections) Print("Trade rejected [Short]: HTF filter. HTF close {0:F5} > HTF EMA {1:F5}.", htfClose, htfEma);
                    return false;
                }
            }

            // Summiert nur negative NetProfit-Werte (Floating Losses), kein klassisches Notional-Exposure
            double totalUnrealised = 0;
            foreach (var pos in Positions)
            {
                if (pos.SymbolName == SymbolName)
                    totalUnrealised += pos.NetProfit < 0 ? Math.Abs(pos.NetProfit) : 0;
            }
            // v2.9.0 – Guard gegen Division durch 0 bei leerem/unentfinierten Account
            double exposurePct = Account.Balance > 0
                ? (totalUnrealised / Account.Balance) * 100.0 : 0;
            if (exposurePct >= MaxFloatingLossPercent)
            {
                if (logRejections) Print("Trade rejected [{0}]: Max floating loss {1:F2}% >= limit {2:F1}%.",
                    direction, exposurePct, MaxFloatingLossPercent);
                return false;
            }

            if (logRejections)
                Print("Market tradable for {0}. Spread={1:F2}p  FloatingLoss={2:F2}%", direction, spreadPips, exposurePct);
            return true;
        }

        #region Scoring Engine
        // ════════════════════════════════════════════════════════════════════
        //  ENTRY SCORING DISPATCHER
        //  logVerbose = true  → volle Prints (OnBar / echte Entry-Entscheidung)
        //  logVerbose = false → kein Print    (Dashboard/OnTick – Anti-Spam)
        // ════════════════════════════════════════════════════════════════════
        private int CalculateEntryScore(TradeType direction, bool logVerbose = true)
        {
            int score = 0;

            if (EnableEmaModule)        score += ScoreEma(direction, logVerbose);
            if (EnableBbModule)         score += ScoreBollingerBands(direction, logVerbose);
            if (EnableSupertrendModule) score += ScoreSupertrend(direction, logVerbose);
            if (EnablePatternsModule)   score += ScorePatterns(direction, logVerbose);
            if (EnableFiboModule)       score += ScoreFibonacci(direction, logVerbose);
            if (EnableOscModule)        score += ScoreOscillators(direction, logVerbose);
            if (EnableSrModule)         score += ScoreSupportResistance(direction, logVerbose);

            if (EnableVerboseScoreLogging && logVerbose)
                Print("Score [{0}]: EMA+BB+ST+PA+FIB+OSC+SR = {1}/{2}", direction, score, _maxPossibleScore);

            return score;
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: EMA
        // ════════════════════════════════════════════════════════════════════
        private int ScoreEma(TradeType direction, bool logVerbose = true)
        {
            int pts = 0;

            double fastNow  = _emaFast.Result.LastValue;
            double slowNow  = _emaSlow.Result.LastValue;
            double fastPrev = _emaFast.Result.Last(2); // vorheriger Bar (war fälschlich Last(3))
            double closeNow = Bars.ClosePrices.LastValue;
            double lowPrev  = Bars.LowPrices.Last(1);
            double highPrev = Bars.HighPrices.Last(1);

            double emaBuffer = Math.Max(Math.Abs(fastNow - slowNow) * 0.05, 0.1 * Symbol.PipSize);

            if (direction == TradeType.Buy)
            {
                if (closeNow > slowNow)                                     pts++;
                if (fastNow > slowNow && fastNow > fastPrev)                pts++;
                if (lowPrev <= fastNow + emaBuffer && closeNow > fastNow)   pts++;
            }
            else
            {
                if (closeNow < slowNow)                                     pts++;
                if (fastNow < slowNow && fastNow < fastPrev)                pts++;
                if (highPrev >= fastNow - emaBuffer && closeNow < fastNow)  pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [EMA] dir={0} fast={1:F5} slow={2:F5} close={3:F5} pts={4}",
                    direction, fastNow, slowNow, closeNow, pts);

            return Math.Min(pts, EmaMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Bollinger Bands
        // ════════════════════════════════════════════════════════════════════
        private int ScoreBollingerBands(TradeType direction, bool logVerbose = true)
        {
            int pts = 0;

            double upperBand  = _bollingerBands.Top.LastValue;
            double lowerBand  = _bollingerBands.Bottom.LastValue;
            double middleBand = _bollingerBands.Main.LastValue;
            double closeNow   = Bars.ClosePrices.LastValue;
            double closePrev  = Bars.ClosePrices.Last(1);
            double lowPrev    = Bars.LowPrices.Last(1);
            double highPrev   = Bars.HighPrices.Last(1);

            if (direction == TradeType.Buy)
            {
                // (1) Kontext: Kurs ist unterhalb der BB-Mittellinie (untere Haelfte)
                //     Kompatibel mit EMA-Pullbacks – kein Band-Touch erforderlich
                if (closeNow < middleBand) pts++;

                // (2) Staerke: vorheriger Bar hat die untere Band beruehrt (starker Pullback)
                if (lowPrev <= lowerBand) pts++;

                // (3) Timing: Bounce-Bestaetigung – Close kehrt von unterhalb der Band zurueck
                if (closePrev <= lowerBand && closeNow > lowerBand) pts++;
            }
            else
            {
                // (1) Kontext: Kurs ist oberhalb der BB-Mittellinie (obere Haelfte)
                if (closeNow > middleBand) pts++;

                // (2) Staerke: vorheriger Bar hat die obere Band beruehrt
                if (highPrev >= upperBand) pts++;

                // (3) Timing: Bounce-Bestaetigung von oberhalb zurueck
                if (closePrev >= upperBand && closeNow < upperBand) pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [BB] dir={0} upper={1:F5} lower={2:F5} close={3:F5} pts={4}",
                    direction, upperBand, lowerBand, closeNow, pts);

            return Math.Min(pts, BbMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  UpdateSupertrendState  – einmal pro Bar in OnBar() aufrufen
        // ════════════════════════════════════════════════════════════════════
        private void UpdateSupertrendState()
        {
            if (_atrSupertrend == null || Bars.Count < SupertrendAtrPeriod + 2) return;

            int    i   = 1;
            double atr = _atrSupertrend.Result.Last(i);
            if (double.IsNaN(atr) || atr <= 0) return;

            double hl2      = (Bars.HighPrices.Last(i) + Bars.LowPrices.Last(i)) / 2.0;
            double rawUpper = hl2 + SupertrendFactor * atr;
            double rawLower = hl2 - SupertrendFactor * atr;
            double prevClose = Bars.ClosePrices.Last(2);

            double newUpper, newLower;
            if (!_stInitialized)
            {
                newUpper       = rawUpper;
                newLower       = rawLower;
                _stTrend       = Bars.ClosePrices.Last(i) > hl2 ? 1 : -1;
                _stInitialized = true;
            }
            else
            {
                newUpper = prevClose > _stFinalUpperBand ? rawUpper : Math.Min(rawUpper, _stFinalUpperBand);
                newLower = prevClose < _stFinalLowerBand ? rawLower : Math.Max(rawLower, _stFinalLowerBand);
            }

            double closeNow  = Bars.ClosePrices.Last(i);
            int    prevTrend = _stTrend;

            if (_stTrend == -1 && closeNow > newUpper)      _stTrend = 1;
            else if (_stTrend == 1 && closeNow < newLower)  _stTrend = -1;

            _stFlippedThisBar = (_stTrend != prevTrend);
            _stFinalUpperBand = newUpper;
            _stFinalLowerBand = newLower;
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Supertrend
        // ════════════════════════════════════════════════════════════════════
        private int ScoreSupertrend(TradeType direction, bool logVerbose = true)
        {
            if (!_stInitialized) return 0;

            int    pts      = 0;
            double closeNow = Bars.ClosePrices.LastValue;
            double atrNow   = (_atrSupertrend != null && !double.IsNaN(_atrSupertrend.Result.LastValue))
                              ? _atrSupertrend.Result.LastValue : 0;

            if (direction == TradeType.Buy)
            {
                if (_stTrend == 1)                                                                       pts++;
                if (_stTrend == 1 && atrNow > 0 && closeNow > _stFinalLowerBand + atrNow * 0.5)        pts++;
                if (_stFlippedThisBar && _stTrend == 1)                                                 pts++;
            }
            else
            {
                if (_stTrend == -1)                                                                      pts++;
                if (_stTrend == -1 && atrNow > 0 && closeNow < _stFinalUpperBand - atrNow * 0.5)       pts++;
                if (_stFlippedThisBar && _stTrend == -1)                                                pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [ST] dir={0} trend={1} flip={2} upperBand={3:F5} lowerBand={4:F5} pts={5}",
                    direction, _stTrend, _stFlippedThisBar, _stFinalUpperBand, _stFinalLowerBand, pts);

            return Math.Min(pts, SupertrendMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Price Action Patterns
        // ════════════════════════════════════════════════════════════════════
        private int ScorePatterns(TradeType direction, bool logVerbose = true)
        {
            int lb = Math.Min(PatternLookback, Bars.Count - 3);
            if (lb < 1) return 0;

            bool hasPinBar    = false;
            bool hasEngulfing = false;
            bool hasInsideBar = false;

            for (int i = 1; i <= lb; i++)
            {
                double open  = Bars.OpenPrices.Last(i);
                double high  = Bars.HighPrices.Last(i);
                double low   = Bars.LowPrices.Last(i);
                double close = Bars.ClosePrices.Last(i);
                double range = high - low;
                if (range <= Symbol.PipSize * 0.5) continue;

                double body      = Math.Abs(close - open);
                double upperWick = high - Math.Max(open, close);
                double lowerWick = Math.Min(open, close) - low;

                if (!hasPinBar && body <= range * 0.25)
                {
                    if (direction == TradeType.Buy  && lowerWick >= range * 0.60) hasPinBar = true;
                    if (direction == TradeType.Sell && upperWick >= range * 0.60) hasPinBar = true;
                }

                if (!hasEngulfing && i + 1 <= lb + 1)
                {
                    double prevOpen  = Bars.OpenPrices.Last(i + 1);
                    double prevClose = Bars.ClosePrices.Last(i + 1);

                    if (direction == TradeType.Buy
                        && close > open
                        && open  <= Math.Min(prevOpen, prevClose)
                        && close >= Math.Max(prevOpen, prevClose))
                        hasEngulfing = true;

                    if (direction == TradeType.Sell
                        && close < open
                        && open  >= Math.Max(prevOpen, prevClose)
                        && close <= Math.Min(prevOpen, prevClose))
                        hasEngulfing = true;
                }

                if (!hasInsideBar && i + 1 <= lb + 1)
                {
                    double prevHigh = Bars.HighPrices.Last(i + 1);
                    double prevLow  = Bars.LowPrices.Last(i + 1);
                    if (high <= prevHigh && low >= prevLow) hasInsideBar = true;
                }

                if (hasPinBar && hasEngulfing && hasInsideBar) break;
            }

            int pts = 0;
            if (hasInsideBar)  pts++;  // (1) Context
            if (hasEngulfing)  pts++;  // (2) Strength
            if (hasPinBar)     pts++;  // (3) Timing

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [PA] dir={0} pinBar={1} engulfing={2} insideBar={3} pts={4}",
                    direction, hasPinBar, hasEngulfing, hasInsideBar, pts);

            return Math.Min(pts, PatternsMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Fibonacci Retracement
        // ════════════════════════════════════════════════════════════════════
        private int ScoreFibonacci(TradeType direction, bool logVerbose = true)
        {
            int lb = Math.Min(FiboSwingLookback, Bars.Count - 3);
            if (lb < 5) return 0;

            var pivots = GetRecentPivots(lb);
            if (pivots.Count < 2) return 0;

            double swingHigh = double.MinValue;
            double swingLow  = double.MaxValue;

            foreach (var p in pivots)
            {
                if (p.IsHigh  && p.Price > swingHigh) swingHigh = p.Price;
                if (!p.IsHigh && p.Price < swingLow)  swingLow  = p.Price;
            }

            // Fallback: kein Pivot-Paar gefunden
            if (swingHigh == double.MinValue || swingLow == double.MaxValue)
            {
                for (int i = 1; i <= lb; i++)
                {
                    if (Bars.HighPrices.Last(i) > swingHigh) swingHigh = Bars.HighPrices.Last(i);
                    if (Bars.LowPrices.Last(i)  < swingLow)  swingLow  = Bars.LowPrices.Last(i);
                }
            }

            double swingRange = swingHigh - swingLow;
            if (swingRange < Symbol.PipSize * 5) return 0;

            double tol   = swingRange * (FiboTolerancePercent / 100.0);
            double price = Bars.ClosePrices.LastValue;

            double[] levels;
            if (direction == TradeType.Buy)
            {
                levels = new[]
                {
                    swingHigh - swingRange * 0.382,
                    swingHigh - swingRange * 0.500,
                    swingHigh - swingRange * 0.618,
                };
            }
            else
            {
                levels = new[]
                {
                    swingLow + swingRange * 0.382,
                    swingLow + swingRange * 0.500,
                    swingLow + swingRange * 0.618,
                };
            }

            bool near382 = Math.Abs(price - levels[0]) <= tol;
            bool near500 = Math.Abs(price - levels[1]) <= tol;
            bool near618 = Math.Abs(price - levels[2]) <= tol;

            int pts = 0;
            if (near382 || near500 || near618) pts++;  // (1) Context
            if (near618 || near500)            pts++;  // (2) Strength
            if (near618)                       pts++;  // (3) Timing

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [FIB] dir={0} hi={1:F5} lo={2:F5} 382={3:F5} 500={4:F5} 618={5:F5} near={6}/{7}/{8} pts={9}",
                    direction, swingHigh, swingLow, levels[0], levels[1], levels[2],
                    near382, near500, near618, pts);

            return Math.Min(pts, FiboMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Oscillators (RSI + Stochastic)
        // ════════════════════════════════════════════════════════════════════
        private int ScoreOscillators(TradeType direction, bool logVerbose = true)
        {
            if (_rsi == null && _stochastic == null) return 0;

            double rsiNow = _rsi        != null ? _rsi.Result.LastValue          : 50.0;
            double kNow   = _stochastic != null ? _stochastic.PercentK.LastValue : 50.0;
            double dNow   = _stochastic != null ? _stochastic.PercentD.LastValue : 50.0;
            double kPrev  = _stochastic != null ? _stochastic.PercentK.Last(1)   : 50.0;
            double dPrev  = _stochastic != null ? _stochastic.PercentD.Last(1)   : 50.0;

            int pts = 0;

            if (direction == TradeType.Buy)
            {
                // (1) Kontext: RSI unter 50 (bearishes Momentum, kein Extremwert noetig)
                //     Kompatibel mit EMA-Pullbacks bei RSI 40-49
                if (rsiNow < 50.0)                      pts++;
                // (2) Staerke: echter Oversold-Bereich
                if (rsiNow < RsiOversold)               pts++;
                // (3) Timing: Stochastik K/D bullisher Kreuzung
                if (kPrev <= dPrev && kNow > dNow)      pts++;
            }
            else
            {
                // (1) Kontext: RSI ueber 50 (bullishes Momentum, kein Extremwert noetig)
                if (rsiNow > 50.0)                      pts++;
                // (2) Staerke: echter Overbought-Bereich
                if (rsiNow > RsiOverbought)             pts++;
                // (3) Timing: Stochastik K/D bearisher Kreuzung
                if (kPrev >= dPrev && kNow < dNow)      pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [OSC] dir={0} rsi={1:F1}(os={2}/ob={3}) stochK={4:F1} stochD={5:F1} kCross={6} pts={7}",
                    direction, rsiNow, RsiOversold, RsiOverbought,
                    kNow, dNow,
                    direction == TradeType.Buy ? (kPrev <= dPrev && kNow > dNow) : (kPrev >= dPrev && kNow < dNow),
                    pts);

            return Math.Min(pts, OscMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Support / Resistance + VWAP
        // ════════════════════════════════════════════════════════════════════
        private int ScoreSupportResistance(TradeType direction, bool logVerbose = true)
        {
            double price    = Bars.ClosePrices.LastValue;
            double tolPrice = SrZoneTolerance * Symbol.PipSize;

            double vwap     = _cachedVwap;
            bool   nearVwap = vwap > 0 && Math.Abs(price - vwap) <= tolPrice * 2.0;

            int  lb             = Math.Min(50, Bars.Count - 3);
            var  pivots         = GetRecentPivots(lb);
            bool nearSupport    = false;
            bool nearResistance = false;
            int  srClusterCount = 0;

            foreach (var p in pivots)
            {
                if (!p.IsHigh && Math.Abs(price - p.Price) <= tolPrice)
                {
                    nearSupport = true;
                    srClusterCount++;
                }
                if (p.IsHigh && Math.Abs(price - p.Price) <= tolPrice)
                {
                    nearResistance = true;
                    srClusterCount++;
                }
            }

            int pts = 0;
            if (direction == TradeType.Buy)
            {
                if (nearSupport)             pts++;
                if (srClusterCount >= 2)     pts++;
                if (nearVwap && nearSupport) pts++;
            }
            else
            {
                if (nearResistance)               pts++;
                if (srClusterCount >= 2)          pts++;
                if (nearVwap && nearResistance)   pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [SR] dir={0} nearSup={1} nearRes={2} cluster={3} vwap={4:F5} nearVwap={5} pts={6}",
                    direction, nearSupport, nearResistance, srClusterCount, vwap, nearVwap, pts);

            return Math.Min(pts, SrMaxWeight);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ComputeDailyVwap
        // ─────────────────────────────────────────────────────────────────────
        private double ComputeDailyVwap()
        {
            double   sumTpVol    = 0;
            double   sumVol      = 0;
            DateTime today       = Server.Time.Date;
            int      maxLookback = Math.Min(Bars.Count - 1, 1440);

            for (int i = 0; i < maxLookback; i++)
            {
                if (Bars.OpenTimes.Last(i).Date < today) break;

                double tp  = (Bars.HighPrices.Last(i) + Bars.LowPrices.Last(i) + Bars.ClosePrices.Last(i)) / 3.0;
                double vol = Bars.TickVolumes.Last(i);
                if (vol <= 0) vol = 1;

                sumTpVol += tp * vol;
                sumVol   += vol;
            }

            return sumVol > 0 ? sumTpVol / sumVol : 0;
        }

        #endregion // Scoring Engine

        #region Risk & Position Sizing
        // ════════════════════════════════════════════════════════════════════
        //  RISK SCALING
        // ════════════════════════════════════════════════════════════════════
        // P5: Liefert die konfigurierte Risikobasis (Balance = stabiler, Equity = dynamischer)
        private double RiskBaseAmount => RiskBaseMode == RiskBase.Equity
            ? Account.Equity : Account.Balance;

        // ─────────────────────────────────────────────────────────────────────
        //  GetRecentPivots – zentrale Pivot-Erkennung (ersetzt redundante Loops
        //  in ScoreFibonacci, ScoreSupportResistance und FindSwingLevel)
        //
        //  leftRightStrength: Wie viele Bars links UND rechts must der Pivot
        //                     ein lokales Extremum sein (Standard = 1).
        // ─────────────────────────────────────────────────────────────────────
        private List<PivotPoint> GetRecentPivots(int lookbackBars, int leftRightStrength = 1)
        {
            var result = new List<PivotPoint>();
            int safe   = Math.Min(lookbackBars, Bars.Count - leftRightStrength - 2);

            for (int i = leftRightStrength + 1; i <= safe; i++)
            {
                double h = Bars.HighPrices.Last(i);
                bool isPivotHigh = true;
                bool isPivotLow  = true;
                double l = Bars.LowPrices.Last(i);

                for (int offset = 1; offset <= leftRightStrength; offset++)
                {
                    if (h <= Bars.HighPrices.Last(i - offset) || h <= Bars.HighPrices.Last(i + offset))
                        isPivotHigh = false;
                    if (l >= Bars.LowPrices.Last(i - offset)  || l >= Bars.LowPrices.Last(i + offset))
                        isPivotLow = false;
                }

                if (isPivotHigh) result.Add(new PivotPoint(h, i, isHigh: true));
                if (isPivotLow)  result.Add(new PivotPoint(l, i, isHigh: false));
            }

            return result;
        }
        private double CalculateRiskPercent(int score)
        {
            if (_maxPossibleScore == _minRequiredScore)
                return MaxRiskPercent;

            double ratio = (double)(score - _minRequiredScore)
                         / (_maxPossibleScore - _minRequiredScore);

            ratio = Math.Max(0.0, Math.Min(1.0, ratio));
            return MinRiskPercent + (MaxRiskPercent - MinRiskPercent) * ratio;
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
                    double atr = _atrSl.Result.LastValue;
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
                        double atrFb = _atrSl.Result.LastValue;
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
                    double atr = _atrSl.Result.LastValue;
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
            double minUnits      = MinLotSize * Symbol.LotSize;
            double maxUnits      = MaxLotSize * Symbol.LotSize;

            if (volumeInUnits < minUnits)
            {
                Print("TryOpenTrade [{0}]: Volume {1:F0} units < MinLotSize ({2:F0} units). Skipping.",
                    direction, volumeInUnits, minUnits);
                return;
            }
            if (volumeInUnits > maxUnits)
            {
                Print("TryOpenTrade [{0}]: Volume clamped to MaxLotSize.", direction);
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
                atrAtEntry = _atrSl.Result.LastValue;
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
                IntervalAtrAtEntry   = atrAtEntry
            };

            _totalTradesOpened++;
            _tradesToday++;

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

            double rrr = (tpPips > 0 && slPips > 0) ? tpPips / slPips : 0;
            Print("TryOpenTrade: FILLED ✓ | Id={0} | Entry={1:F5} | SL={2:F1}p | TP={3} | R:R={4:F2} | Vol={5:F0}u ({6:F2}L)",
                _currentTrade.PositionId, _currentTrade.EntryPrice, _currentTrade.InitialSlPips,
                tpLabel, rrr, _currentTrade.InitialVolume, _currentTrade.InitialVolume / Symbol.LotSize);

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

            // v2.9.0 – Bei invalidem Volumen Level als "done" markieren, um Tick-Spam zu vermeiden.
            // Grund: War bisher false → Print-Flut pro Tick, solange Trigger-R überschritten ist.
            if (closeUnits <= 0 || closeUnits >= pos.VolumeInUnits)
            {
                Print("Partial{0}: Volume calculation invalid ({1:F0} units) at {2:F2}R – marking as done to prevent retry.",
                    level, closeUnits, currentR);
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

            double atr = _atrChandelier.Result.LastValue;
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

            double emaValue = _trailingEma.Result.LastValue;
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
                double htfEma    = _htfEma.Result.LastValue;
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
                double rsiValue   = _rsi.Result.LastValue;
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

            // ── d) Swap / Rollover Evasion ────────────────────────────────────
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
                        double htfEma   = _htfEma.Result.LastValue;
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

                if (_cachedCounterScore >= _minRequiredScore)
                {
                    Print("Reversal exit [{0}]: Counter-direction score {1}/{2} >= {3}. Closing.",
                        isLong ? "Long" : "Short", _cachedCounterScore, _maxPossibleScore, _minRequiredScore);
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

        #endregion // Trade Management

        // ════════════════════════════════════════════════════════════════════
        //  VERBOSE LOGGING HELPERS
        // ════════════════════════════════════════════════════════════════════

        private void LogMarketFilterSummary(TradeType direction, bool tradable)
        {
            double spreadPips = (Symbol.Ask - Symbol.Bid) / Symbol.PipSize;
            string htfStr = "N/A";
            if (_htfBars != null && _htfEma != null)
            {
                double htfClose = _htfBars.ClosePrices.Last(1);
                double htfEma   = _htfEma.Result.LastValue;
                htfStr = string.Format("{0:F5} vs EMA {1:F5} → {2}",
                    htfClose, htfEma, htfClose > htfEma ? "BULL" : "BEAR");
            }
            Print("[FilterSummary {0}] Tradable={1} | Time={2:HH:mm} | Spread={3:F2}p | HTF={4} | DDBreached={5}",
                direction, tradable, Server.Time, spreadPips, htfStr, _dailyDrawdownBreached);
        }

        /// <summary>
        /// Vollständige Score-Aufschlüsselung per Modul.
        /// logVerbose: false auf alle internen Modul-Aufrufe, damit
        /// kein Kaskaden-Spam entsteht. Der eigene Print ist immer aktiv.
        /// </summary>
        private void LogScoreBreakdown(TradeType direction, int totalScore)
        {
            Print("[ScoreBreakdown {0}] Total={1}/{2} (min={3}) | " +
                  "EMA={4} BB={5} ST={6} PA={7} FIB={8} OSC={9} SR={10}",
                direction, totalScore, _maxPossibleScore, _minRequiredScore,
                EnableEmaModule        ? ScoreEma(direction,               false).ToString() : "off",
                EnableBbModule         ? ScoreBollingerBands(direction,    false).ToString() : "off",
                EnableSupertrendModule ? ScoreSupertrend(direction,        false).ToString() : "off",
                EnablePatternsModule   ? ScorePatterns(direction,          false).ToString() : "off",
                EnableFiboModule       ? ScoreFibonacci(direction,         false).ToString() : "off",
                EnableOscModule        ? ScoreOscillators(direction,       false).ToString() : "off",
                EnableSrModule         ? ScoreSupportResistance(direction, false).ToString() : "off");
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
