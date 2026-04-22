// ═══════════════════════════════════════════════════════════════════════════════
//  TenFoldBot – Parameters (partial class)
//  All [Parameter(...)] properties grouped by config section.
// ═══════════════════════════════════════════════════════════════════════════════

using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class TenFoldBot
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

        [Parameter("Enable Volatility Regime Gate (adjusts MinReq by ATR vs 20d-median)",
            Group = "01b · Volatility & News", DefaultValue = false)]
        public bool EnableVolRegime { get; set; }

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

        // ── 01c · ADX Trend Strength Filter ──────────────────────────────────
        //  Gate (kein Score-Modul): blockiert Entries wenn ADX unter Schwelle
        //  (Chop-Market). Ergänzt Trend- und MR-Signale, ohne doppelt zu zählen.
        [Parameter("Enable ADX Trend Strength Filter",
            Group = "01c · ADX Trend Strength", DefaultValue = false)]
        public bool EnableAdxFilter { get; set; }

        [Parameter("ADX Period",
            Group = "01c · ADX Trend Strength", DefaultValue = 14, MinValue = 2, MaxValue = 200)]
        public int AdxPeriod { get; set; }

        [Parameter("Min ADX for new Entries (0–100)",
            Group = "01c · ADX Trend Strength", DefaultValue = 20.0, MinValue = 0.0, MaxValue = 100.0, Step = 1.0)]
        public double MinAdxValue { get; set; }

        [Parameter("Require DI Alignment (DI+>DI- for Long etc.)",
            Group = "01c · ADX Trend Strength", DefaultValue = true)]
        public bool RequireDiAlignment { get; set; }

        [Parameter("Enable ADX as Score Module (in addition to Gate)",
            Group = "01c · ADX Trend Strength", DefaultValue = false)]
        public bool EnableAdxScoreModule { get; set; }

        [Parameter("ADX Score Max Points (1–3)",
            Group = "01c · ADX Trend Strength", DefaultValue = 2, MinValue = 1, MaxValue = 3)]
        public int AdxScoreMaxWeight { get; set; }

        // ── 02 · Spread Protection ───────────────────────────────────────────
        [Parameter("Max Allowed Spread (Pips)",
            Group = "02 · Spread Protection", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1)]
        public double MaxAllowedSpread { get; set; }

        [Parameter("Enable Dynamic Spread Cap (ATR-relative)",
            Group = "02 · Spread Protection", DefaultValue = false)]
        public bool EnableDynamicSpreadCap { get; set; }

        [Parameter("Dynamic Spread Cap – ATR Ratio (spread <= ratio × ATR)",
            Group = "02 · Spread Protection", DefaultValue = 0.3, MinValue = 0.05, MaxValue = 1.0, Step = 0.05)]
        public double DynamicSpreadAtrRatio { get; set; }

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

        [Parameter("Fibo Use Legacy Range (max/min over full lookback)",
            Group = "09 · Module: Fibonacci", DefaultValue = false)]
        public bool FiboUseLegacyRange { get; set; }

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

        // ── 09b · Pivot Detection ────────────────────────────────────────────
        [Parameter("Pivot Left/Right Strength (bars required each side for a pivot, 1=standard)",
            Group = "09b · Pivot Detection", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int PivotLeftRightStrength { get; set; }

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

        // ── 11b · Module: MACD ───────────────────────────────────────────────
        //  3-Punkte-Scoring:
        //   (1) Context : MACD-Line vs Signal (bullish/bearish Basis)
        //   (2) Strength: Histogram wächst in Richtung (Momentum beschleunigt)
        //   (3) Timing  : Histogram hat gerade die Nulllinie in Richtung gekreuzt
        [Parameter("Enable MACD Module",
            Group = "11b · Module: MACD", DefaultValue = true)]
        public bool EnableMacdModule { get; set; }

        [Parameter("MACD Long Cycle",
            Group = "11b · Module: MACD", DefaultValue = 26, MinValue = 2, MaxValue = 200)]
        public int MacdLongCycle { get; set; }

        [Parameter("MACD Short Cycle",
            Group = "11b · Module: MACD", DefaultValue = 12, MinValue = 2, MaxValue = 200)]
        public int MacdShortCycle { get; set; }

        [Parameter("MACD Signal Periods",
            Group = "11b · Module: MACD", DefaultValue = 9, MinValue = 1, MaxValue = 100)]
        public int MacdSignalPeriods { get; set; }

        [Parameter("MACD Max Points (1–3)",
            Group = "11b · Module: MACD", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int MacdMaxWeight { get; set; }

        // ── 12 · Scoring & Consensus ─────────────────────────────────────────
        [Parameter("Consensus Ratio (0.30–1.00)",
            Group = "12 · Scoring & Consensus", DefaultValue = 0.70, MinValue = 0.30, MaxValue = 1.0, Step = 0.05)]
        public double ConsensusRatio { get; set; }

        [Parameter("Enable Verbose Score Logging",
            Group = "12 · Scoring & Consensus", DefaultValue = false)]
        public bool EnableVerboseScoreLogging { get; set; }

        [Parameter("Block On Conflicting Signals (both Long+Short qualify)",
            Group = "12 · Scoring & Consensus", DefaultValue = true)]
        public bool BlockOnConflictingSignals { get; set; }

        [Parameter("Enable Trade Attribution Log (print [TRADECSV] per closed trade)",
            Group = "12 · Scoring & Consensus", DefaultValue = false)]
        public bool EnableTradeAttributionLog { get; set; }

        [Parameter("Attribution Log File Path (empty = disable file output)",
            Group = "12 · Scoring & Consensus", DefaultValue = "")]
        public string AttributionLogFilePath { get; set; }

        [Parameter("Enable Session/DoW Attribution",
            Group = "12 · Scoring & Consensus", DefaultValue = false)]
        public bool EnableSessionAttribution { get; set; }

        // ── 12b · Category Caps (v2.12.0) ──────────────────────────────────────
        [Parameter("Scoring Preset (DecorrelatedDefault overrides caps on start)",
            Group = "12b · Category Caps", DefaultValue = ScoringPreset.Custom)]
        public ScoringPreset ScoringPresetMode { get; set; }

        [Parameter("Enable Category Score Caps",
            Group = "12b · Category Caps", DefaultValue = false)]
        public bool EnableCategoryCaps { get; set; }

        [Parameter("Trend Cap – EMA+ST+MACD max",
            Group = "12b · Category Caps", DefaultValue = 3, MinValue = 1, MaxValue = 9)]
        public int TrendCategoryCap { get; set; }

        [Parameter("MeanReversion Cap – BB+FIB+SR max",
            Group = "12b · Category Caps", DefaultValue = 3, MinValue = 1, MaxValue = 9)]
        public int MeanReversionCategoryCap { get; set; }

        [Parameter("Momentum Cap – OSC max",
            Group = "12b · Category Caps", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int MomentumCategoryCap { get; set; }

        [Parameter("PriceAction Cap – Patterns max",
            Group = "12b · Category Caps", DefaultValue = 3, MinValue = 1, MaxValue = 3)]
        public int PriceActionCategoryCap { get; set; }

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

        [Parameter("Consec Loss Size Reducer (1.0=off, 0.5-1.0 to shrink after losses)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 1.0, Step = 0.05)]
        public double ConsecLossSizeReducer { get; set; }

        [Parameter("Estimated Commission Pips (both sides, 0 = ignore)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 0.0, MinValue = 0.0, Step = 0.1)]
        public double EstimatedCommissionPips { get; set; }

        [Parameter("Include Commission In Risk Calc (adds EstCommPips to SL for unit sizing)",
            Group = "13 · Position Sizing & Risk", DefaultValue = false)]
        public bool IncludeCommissionInRisk { get; set; }

        [Parameter("Enable Vol Targeted Sizing",
            Group = "13 · Position Sizing & Risk", DefaultValue = false)]
        public bool EnableVolTargetedSizing { get; set; }

        [Parameter("Vol Target ATR Baseline (Pips)",
            Group = "13 · Position Sizing & Risk", DefaultValue = 20.0, MinValue = 1.0, Step = 0.5)]
        public double VolTargetAtrBaselinePips { get; set; }

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

        [Parameter("Reversal Exit Score Multiplier (1.0=default, 1.1-1.2 = fewer false exits)",
            Group = "19 · Exit Logic", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 3.0, Step = 0.05)]
        public double ReversalExitScoreMultiplier { get; set; }

        [Parameter("Reversal Exit Require Higher Than Entry Score",
            Group = "19 · Exit Logic", DefaultValue = false)]
        public bool ReversalExitRequireHigherThanEntry { get; set; }

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

        [Parameter("Enable Max Hold Time Exit",
            Group = "19 · Exit Logic", DefaultValue = false)]
        public bool EnableMaxHoldTime { get; set; }

        [Parameter("Max Hold Time (Hours)",
            Group = "19 · Exit Logic", DefaultValue = 48, MinValue = 1, MaxValue = 720)]
        public int MaxHoldTimeHours { get; set; }

        [Parameter("Enable R-Progress Time Stop",
            Group = "19 · Exit Logic", DefaultValue = false)]
        public bool EnableRProgressTimeStop { get; set; }

        [Parameter("R-Progress Window (Bars)",
            Group = "19 · Exit Logic", DefaultValue = 20, MinValue = 1, MaxValue = 500)]
        public int RProgressWindowBars { get; set; }

        [Parameter("Min R Progress",
            Group = "19 · Exit Logic", DefaultValue = 0.3, MinValue = 0.0, MaxValue = 10.0, Step = 0.1)]
        public double MinRProgress { get; set; }

        // ── 20 · Account Protection ──────────────────────────────────────────
        [Parameter("Max Daily Drawdown % (halts new entries)",
            Group = "20 · Account Protection", DefaultValue = 2.0, MinValue = 0.1, MaxValue = 100.0, Step = 0.1)]
        public double MaxDailyDrawdownPercent { get; set; }

        // P4: Kein klassisches Exposure-Konzept – summiert nur Floating Losses
        [Parameter("Max Floating Loss on Open Positions (% of Balance)",
            Group = "20 · Account Protection", DefaultValue = 4.0, MinValue = 0.1, MaxValue = 100.0, Step = 0.1)]
        public double MaxFloatingLossPercent { get; set; }

        [Parameter("Floating Loss Gate Mode (FloatingLossOnly | Gross Unrealised (abs sum of all P&L))",
            Group = "20 · Account Protection", DefaultValue = FloatingLossGateMode.FloatingLossOnly)]
        public FloatingLossGateMode FloatingLossMode { get; set; }

        [Parameter("Floating Loss Denominator (Balance = parity | Equity = tighter in drawdown)",
            Group = "20 · Account Protection", DefaultValue = FloatingLossDenom.Balance)]
        public FloatingLossDenom FloatingLossDenominator { get; set; }

        [Parameter("Max Trades per Day (0 = off)",
            Group = "20 · Account Protection", DefaultValue = 3, MinValue = 0)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Weekly Drawdown % (0 = off, resets Monday)",
            Group = "20 · Account Protection", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 100.0, Step = 0.5)]
        public double MaxWeeklyDrawdownPercent { get; set; }

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

        [Parameter("Dashboard Corner",
            Group = "21 · Dashboard", DefaultValue = DashboardCornerPosition.TopLeft)]
        public DashboardCornerPosition DashboardCorner { get; set; }
    }
}
