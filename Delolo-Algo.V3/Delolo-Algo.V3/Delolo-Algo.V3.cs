using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class MultiIndicatorScalper : Robot
    {
        // ==================== PARAMETERS ====================

        [Parameter("== RISK MANAGEMENT ==")]
        public string RiskLabel { get; set; }

        [Parameter("Risk Per Trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercent { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 5, MinValue = 1, MaxValue = 20)]
        public int MaxDailyTrades { get; set; }

        [Parameter("Max Daily Loss (%)", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 10.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Max Margin Usage (%)", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 50.0)]
        public double MaxMarginUsagePercent { get; set; }

        // ── Swap Protection ──────────────────────────────────

        [Parameter("== SWAP PROTECTION ==")]
        public string SwapLabel { get; set; }

        [Parameter("Enable Swap Protection", DefaultValue = true)]
        public bool EnableSwapProtection { get; set; }

        [Parameter("Hard Close Hour UTC", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int HardCloseHour { get; set; }

        [Parameter("Hard Close Minute", DefaultValue = 50, MinValue = 0, MaxValue = 59)]
        public int HardCloseMinute { get; set; }

        [Parameter("Hard Close: Losing Only", DefaultValue = false)]
        public bool HardCloseLosingOnly { get; set; }

        [Parameter("Enable Weekend Protection", DefaultValue = true)]
        public bool EnableWeekendProtection { get; set; }

        [Parameter("Weekend Close Hour UTC", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int WeekendCloseHour { get; set; }

        [Parameter("Weekend Close Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int WeekendCloseMinute { get; set; }

        [Parameter("Enable Triple Swap Guard (Wed)", DefaultValue = true)]
        public bool EnableTripleSwapGuard { get; set; }

        [Parameter("Triple Swap Close Hour UTC", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int TripleSwapCloseHour { get; set; }

        [Parameter("Triple Swap Close Minute", DefaultValue = 50, MinValue = 0, MaxValue = 59)]
        public int TripleSwapCloseMinute { get; set; }

        [Parameter("Enable Swap Threshold Check", DefaultValue = true)]
        public bool EnableSwapThreshold { get; set; }

        [Parameter("Swap Threshold (% of P/L)", DefaultValue = 20.0, MinValue = 1.0, MaxValue = 100.0)]
        public double SwapThresholdPercent { get; set; }

        [Parameter("Swap Check Hour UTC", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int SwapCheckHour { get; set; }

        [Parameter("Swap Check Minute", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int SwapCheckMinute { get; set; }

        // ── Score-Based Position Sizing ──────────────────────

        [Parameter("== SCORE-BASED SIZING ==")]
        public string ScoreSizingLabel { get; set; }

        [Parameter("Enable Score Sizing", DefaultValue = true)]
        public bool EnableScoreSizing { get; set; }

        [Parameter("Risk Multiplier Max Score", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 3.0)]
        public double RiskMultiplierMax { get; set; }

        [Parameter("Risk Multiplier Min Score", DefaultValue = 0.75, MinValue = 0.1, MaxValue = 1.0)]
        public double RiskMultiplierMin { get; set; }

        // ── Indicators ──────────────────────────────────────

        [Parameter("== INDICATORS ==")]
        public string IndicatorLabel { get; set; }

        [Parameter("EMA Fast Period", DefaultValue = 21, MinValue = 3, MaxValue = 100)]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Mid Period", DefaultValue = 50, MinValue = 5, MaxValue = 200)]
        public int EmaMidPeriod { get; set; }

        [Parameter("EMA Slow Period", DefaultValue = 200, MinValue = 50, MaxValue = 500)]
        public int EmaSlowPeriod { get; set; }

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 3, MaxValue = 50)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Overbought", DefaultValue = 70, MinValue = 50, MaxValue = 95)]
        public double RsiOverbought { get; set; }

        [Parameter("RSI Oversold", DefaultValue = 30, MinValue = 5, MaxValue = 50)]
        public double RsiOversold { get; set; }

        [Parameter("RSI Long Min", DefaultValue = 40, MinValue = 20, MaxValue = 60)]
        public double RsiLongMin { get; set; }

        [Parameter("RSI Short Max", DefaultValue = 60, MinValue = 40, MaxValue = 80)]
        public double RsiShortMax { get; set; }

        [Parameter("MACD Fast", DefaultValue = 12, MinValue = 2, MaxValue = 50)]
        public int MacdFast { get; set; }

        [Parameter("MACD Slow", DefaultValue = 26, MinValue = 3, MaxValue = 100)]
        public int MacdSlow { get; set; }

        [Parameter("MACD Signal", DefaultValue = 9, MinValue = 2, MaxValue = 50)]
        public int MacdSignal { get; set; }

        [Parameter("MACD Mode: 0=Cross 1=Histogram", DefaultValue = 0, MinValue = 0, MaxValue = 1)]
        public int MacdMode { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int AtrPeriod { get; set; }

        [Parameter("Supertrend Period", DefaultValue = 10, MinValue = 2, MaxValue = 50)]
        public int SupertrendPeriod { get; set; }

        [Parameter("Supertrend Multiplier", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 10.0)]
        public double SupertrendMultiplier { get; set; }

        // ── ADX Filter ───────────────────────────────────────

        [Parameter("== ADX MARKET FILTER ==")]
        public string AdxLabel { get; set; }

        [Parameter("Enable ADX Filter", DefaultValue = true)]
        public bool EnableAdxFilter { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, MaxValue = 50)]
        public int AdxPeriod { get; set; }

        [Parameter("ADX Min Trend", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0)]
        public double AdxMinTrend { get; set; }

        [Parameter("ADX Max Range (Block)", DefaultValue = 20.0, MinValue = 5.0, MaxValue = 40.0)]
        public double AdxMaxRange { get; set; }

        [Parameter("Score: ADX Bonus (0-2)", DefaultValue = 1, MinValue = 0, MaxValue = 2)]
        public int ScoreAdxTrend { get; set; }

        // ── News Filter (Phase 3) ────────────────────────────

        [Parameter("== NEWS FILTER ==")]
        public string NewsLabel { get; set; }

        [Parameter("Enable News Filter", DefaultValue = true)]
        public bool EnableNewsFilter { get; set; }

        [Parameter("News Block Minutes Before", DefaultValue = 30, MinValue = 5, MaxValue = 120)]
        public int NewsBlockBefore { get; set; }

        [Parameter("News Block Minutes After", DefaultValue = 30, MinValue = 5, MaxValue = 120)]
        public int NewsBlockAfter { get; set; }

        // Bis zu 5 tägliche News-Zeiten (UTC HH:MM) als Stunden-Parameter
        // Flexibler als ein String-Parameter, besser für Optimizer
        [Parameter("News Time 1 Hour UTC (-1=off)", DefaultValue = 8, MinValue = -1, MaxValue = 23)]
        public int NewsTime1Hour { get; set; }

        [Parameter("News Time 1 Minute", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int NewsTime1Minute { get; set; }

        [Parameter("News Time 2 Hour UTC (-1=off)", DefaultValue = 13, MinValue = -1, MaxValue = 23)]
        public int NewsTime2Hour { get; set; }

        [Parameter("News Time 2 Minute", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int NewsTime2Minute { get; set; }

        [Parameter("News Time 3 Hour UTC (-1=off)", DefaultValue = 15, MinValue = -1, MaxValue = 23)]
        public int NewsTime3Hour { get; set; }

        [Parameter("News Time 3 Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int NewsTime3Minute { get; set; }

        [Parameter("News Time 4 Hour UTC (-1=off)", DefaultValue = -1, MinValue = -1, MaxValue = 23)]
        public int NewsTime4Hour { get; set; }

        [Parameter("News Time 4 Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int NewsTime4Minute { get; set; }

        [Parameter("News Time 5 Hour UTC (-1=off)", DefaultValue = -1, MinValue = -1, MaxValue = 23)]
        public int NewsTime5Hour { get; set; }

        [Parameter("News Time 5 Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int NewsTime5Minute { get; set; }

        [Parameter("Close Positions on News", DefaultValue = false)]
        public bool ClosePositionsOnNews { get; set; }

        // ── Multi-Timeframe ──────────────────────────────────

        [Parameter("== MULTI-TIMEFRAME ==")]
        public string MtfLabel { get; set; }

        [Parameter("Enable MTF Filter", DefaultValue = true)]
        public bool EnableMtfFilter { get; set; }

        [Parameter("HTF Timeframe", DefaultValue = "Hour")]
        public TimeFrame HtfTimeFrame { get; set; }

        // ── Signal Scoring ───────────────────────────────────

        [Parameter("== SIGNAL SCORING ==")]
        public string ScoringLabel { get; set; }

        [Parameter("Min Score to Trade", DefaultValue = 5, MinValue = 1, MaxValue = 12)]
        public int MinScoreToTrade { get; set; }

        [Parameter("Enable Dynamic Score (×ADX)", DefaultValue = true)]
        public bool EnableDynamicScore { get; set; }

        [Parameter("Score: Supertrend (0-2)", DefaultValue = 2, MinValue = 0, MaxValue = 2)]
        public int ScoreSupertrend { get; set; }

        [Parameter("Score: EMA Trend (0-2)", DefaultValue = 2, MinValue = 0, MaxValue = 2)]
        public int ScoreEmaTrend { get; set; }

        [Parameter("Score: EMA Momentum (0-1)", DefaultValue = 1, MinValue = 0, MaxValue = 1)]
        public int ScoreEmaMomentum { get; set; }

        [Parameter("Score: RSI (0-1)", DefaultValue = 1, MinValue = 0, MaxValue = 1)]
        public int ScoreRsi { get; set; }

        [Parameter("Score: MACD (0-1)", DefaultValue = 1, MinValue = 0, MaxValue = 1)]
        public int ScoreMacd { get; set; }

        [Parameter("Score: MTF (0-2)", DefaultValue = 2, MinValue = 0, MaxValue = 2)]
        public int ScoreMtfConfirm { get; set; }

        [Parameter("Score: Candle Pattern (0-2)", DefaultValue = 2, MinValue = 0, MaxValue = 2)]
        public int ScoreCandlePattern { get; set; }

        // ── Candlestick Patterns ─────────────────────────────

        [Parameter("== CANDLESTICK PATTERNS ==")]
        public string CandleLabel { get; set; }

        [Parameter("Enable Candle Patterns", DefaultValue = true)]
        public bool EnableCandlePatterns { get; set; }

        [Parameter("Enable Engulfing", DefaultValue = true)]
        public bool EnableEngulfing { get; set; }

        [Parameter("Enable Pin Bar", DefaultValue = true)]
        public bool EnablePinBar { get; set; }

        [Parameter("Enable Inside Bar", DefaultValue = true)]
        public bool EnableInsideBar { get; set; }

        [Parameter("Enable Morning/Evening Star", DefaultValue = true)]
        public bool EnableStar { get; set; }

        [Parameter("Pin Bar Wick Ratio", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 10.0)]
        public double PinBarWickRatio { get; set; }

        [Parameter("Engulfing Body Ratio", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 5.0)]
        public double EngulfingBodyRatio { get; set; }

        [Parameter("Inside Bar Min Mother (ATR)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double InsideBarMinMotherSizeAtr { get; set; }

        // ── Trading Session ───────────────────────────────────

        [Parameter("== TRADING HOURS (UTC) ==")]
        public string SessionLabel { get; set; }

        [Parameter("Session Start Hour", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartHour { get; set; }

        [Parameter("Session Start Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int StartMinute { get; set; }

        [Parameter("Session End Hour", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int EndHour { get; set; }

        [Parameter("Session End Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int EndMinute { get; set; }

        // ── Targets ───────────────────────────────────────────

        [Parameter("== TARGETS ==")]
        public string TargetLabel { get; set; }

        [Parameter("Stop Loss (ATR)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double StopLossMultiplier { get; set; }

        [Parameter("Take Profit 1 (ATR)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 20.0)]
        public double TakeProfit1Multiplier { get; set; }

        [Parameter("Take Profit 2 (ATR)", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 20.0)]
        public double TakeProfit2Multiplier { get; set; }

        [Parameter("Take Profit 3 (ATR)", DefaultValue = 4.0, MinValue = 0.5, MaxValue = 50.0)]
        public double TakeProfit3Multiplier { get; set; }

        [Parameter("TP1 Close %", DefaultValue = 30, MinValue = 0, MaxValue = 100)]
        public int Tp1ClosePercent { get; set; }

        [Parameter("TP2 Close %", DefaultValue = 40, MinValue = 0, MaxValue = 100)]
        public int Tp2ClosePercent { get; set; }

        // ── Strategy Features ─────────────────────────────────

        [Parameter("== STRATEGY FEATURES ==")]
        public string FeaturesLabel { get; set; }

        [Parameter("Enable Trailing Stop", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Enable Partial TPs", DefaultValue = true)]
        public bool EnablePartialTPs { get; set; }

        [Parameter("Enable Break-Even", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("Break-Even Buffer (ATR)", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 1.0)]
        public double BreakEvenBufferAtr { get; set; }

        [Parameter("Enable MACD Filter", DefaultValue = true)]
        public bool EnableMacdFilter { get; set; }

        [Parameter("Enable RSI Filter", DefaultValue = true)]
        public bool EnableRsiFilter { get; set; }

        [Parameter("Enable Supertrend Filter", DefaultValue = true)]
        public bool EnableSupertrendFilter { get; set; }

        [Parameter("Enable EMA Trend Filter", DefaultValue = true)]
        public bool EnableEmaTrendFilter { get; set; }

        [Parameter("Enable EMA Momentum Filter", DefaultValue = true)]
        public bool EnableEmaMomentumFilter { get; set; }

        [Parameter("Trailing Start (ATR)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 20.0)]
        public double TrailingStartMultiplier { get; set; }

        [Parameter("Trailing Distance (ATR)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 20.0)]
        public double TrailingDistanceMultiplier { get; set; }

        // ==================== INDICATORS ====================

        private ExponentialMovingAverage  emaFast;
        private ExponentialMovingAverage  emaMid;
        private ExponentialMovingAverage  emaSlow;
        private RelativeStrengthIndex     rsi;
        private MacdHistogram             macd;
        private AverageTrueRange          atr;
        private Supertrend                supertrend;
        private DirectionalMovementSystem adx;

        private Bars                     htfBars;
        private ExponentialMovingAverage htfEma50;
        private ExponentialMovingAverage htfEma200;

        // HTF Supertrend manuell (kein 3-Arg Overload in cAlgo)
        private double[] htfStBand;
        private bool[]   htfStBull;
        private int      htfStCalcUpTo = -1;
        private const int HtfStHistory = 500;

        // HTF Supertrend Incremental Cache
        private double _htfPrevUpperBand = 0;
        private double _htfPrevLowerBand = 0;
        private bool   _htfPrevBull      = true;
        private int    _htfLastFullCalc  = -1;

        // ==================== STATE ====================

        private const string BotLabel = "MultiScalper";

        private int      tradesOpenedToday;
        private double   dailyStartBalance;
        private double   highestBalanceToday;
        private DateTime lastTradeDate;

        private long?  managedPositionId;
        private bool   tp1Hit;
        private bool   tp2Hit;
        private bool   trailingActive;
        private bool   breakEvenSet;
        private double entryPrice;

        private int    lastLongScore;
        private int    lastShortScore;
        private string lastCandlePattern;

        private bool swapThresholdCheckedToday;
        private bool hardCloseExecutedToday;

        // News-Zeiten gecacht
        private List<(int Hour, int Minute)> newsTimes;

        // ==================== TRADE JOURNAL ====================

        private int    totalTrades;
        private int    totalWins;
        private int    totalLosses;
        private double totalWinAmount;
        private double totalLossAmount;
        private double peakBalance;
        private double maxDrawdown;
        private double maxDrawdownPercent;

        // FIX Phase 3: Pro Position ID tracken (kein Reset-Timing Problem)
        private Dictionary<long, string> positionPatterns = new Dictionary<long, string>();
        private Dictionary<long, int>    positionScores   = new Dictionary<long, int>();

        private Dictionary<string, int>    patternWins   = new Dictionary<string, int>();
        private Dictionary<string, int>    patternLosses = new Dictionary<string, int>();
        private Dictionary<string, double> patternPnl    = new Dictionary<string, double>();
        private Dictionary<int, int>       scoreWins     = new Dictionary<int, int>();
        private Dictionary<int, int>       scoreLosses   = new Dictionary<int, int>();

        // ==================== LIFECYCLE ====================

        protected override void OnStart()
        {
            int maxScore = GetMaxPossibleScore();

            // Validierungen
            if (MinScoreToTrade > maxScore)
                Print($"⚠ MinScoreToTrade ({MinScoreToTrade}) > Max ({maxScore})!");
            if (EnableScoreSizing && RiskMultiplierMin > RiskMultiplierMax)
                Print("⚠ RiskMultiplierMin > RiskMultiplierMax!");
            if (EmaFastPeriod >= EmaMidPeriod)
                Print("⚠ EmaFastPeriod >= EmaMidPeriod!");
            if (EmaMidPeriod >= EmaSlowPeriod)
                Print("⚠ EmaMidPeriod >= EmaSlowPeriod!");

            // News-Zeiten aus Parametern aufbauen
            newsTimes = BuildNewsTimes();

            Print("╔════════════════════════════════════════╗");
            Print("║      Multi-Indicator Scalper v4.0      ║");
            Print("║     Phase 3 Final — Optimizer Ready    ║");
            Print("╚════════════════════════════════════════╝");
            Print($"Balance:         {Account.Balance:F2} {Account.Asset.Name}");
            Print($"Risk/Trade:      {RiskPercent:F2}%");
            Print($"Max Trades/Day:  {MaxDailyTrades}");
            Print($"Max Daily Loss:  {MaxDailyLossPercent:F2}%");
            Print($"Session (UTC):   {StartHour:D2}:{StartMinute:D2} - {EndHour:D2}:{EndMinute:D2}");
            Print($"LTF / HTF:       {TimeFrame} / {HtfTimeFrame}");
            Print($"Leverage:        1:{Account.PreciseLeverage:F0}");
            Print("─────────────────────────────────────────");
            Print($"EMAs:            {EmaFastPeriod}/{EmaMidPeriod}/{EmaSlowPeriod}");
            Print($"Supertrend:      {SupertrendPeriod} × {SupertrendMultiplier:F1}");
            Print($"MACD:            {MacdFast}/{MacdSlow}/{MacdSignal} Mode:{(MacdMode == 0 ? "Cross" : "Hist")}");
            Print($"RSI:             {RsiPeriod} OS:{RsiOversold} OB:{RsiOverbought}");
            Print($"ADX:             {(EnableAdxFilter ? $"ON P:{AdxPeriod} Trend>{AdxMinTrend} Range<{AdxMaxRange}" : "OFF")}");
            Print($"Dynamic Score:   {(EnableDynamicScore ? "ON (×ADX factor)" : "OFF")}");
            Print($"Score Min:       {MinScoreToTrade}/{maxScore}");
            Print($"Score Sizing:    {(EnableScoreSizing ? $"ON ({RiskMultiplierMin:F2}x-{RiskMultiplierMax:F2}x)" : "OFF")}");
            Print("─────────────────────────────────────────");
            Print($"Break-Even:      {(EnableBreakEven ? $"ON ({BreakEvenBufferAtr:F1}×ATR + min 2×Spread)" : "OFF")}");
            Print($"Trailing Stop:   {(EnableTrailingStop ? "ON" : "OFF")}");
            Print($"Partial TPs:     {(EnablePartialTPs ? $"ON TP1:{Tp1ClosePercent}% TP2:{Tp2ClosePercent}%" : "OFF")}");
            Print($"Swap Protect:    {(EnableSwapProtection ? "ON" : "OFF")}");
            Print("─────────────────────────────────────────");
            Print($"News Filter:     {(EnableNewsFilter ? $"ON ±{NewsBlockBefore}/{NewsBlockAfter}min" : "OFF")}");
            if (EnableNewsFilter && newsTimes.Count > 0)
                foreach (var t in newsTimes)
                    Print($"  News @ {t.Hour:D2}:{t.Minute:D2} UTC");
            Print("─────────────────────────────────────────");

            // LTF Indikatoren
            emaFast    = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaFastPeriod);
            emaMid     = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaMidPeriod);
            emaSlow    = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaSlowPeriod);
            rsi        = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            macd       = Indicators.MacdHistogram(Bars.ClosePrices, MacdFast, MacdSlow, MacdSignal);
            atr        = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            supertrend = Indicators.Supertrend(SupertrendPeriod, SupertrendMultiplier);
            adx        = Indicators.DirectionalMovementSystem(AdxPeriod);

            // HTF Indikatoren
            htfBars  = MarketData.GetBars(HtfTimeFrame);
            htfEma50 = Indicators.ExponentialMovingAverage(htfBars.ClosePrices, 50);
            htfEma200= Indicators.ExponentialMovingAverage(htfBars.ClosePrices, 200);

            htfStBand = new double[HtfStHistory];
            htfStBull = new bool[HtfStHistory];
            RecalculateHtfSupertrendFull();

            Positions.Closed += OnPositionClosed;

            peakBalance       = Account.Balance;
            lastCandlePattern = "None";
            swapThresholdCheckedToday = false;
            hardCloseExecutedToday    = false;

            ResetDailyTracking();

            Print("✓ LTF + HTF Indicators initialized");
            Print("✓ HTF Supertrend (manual incremental) ready");
            Print("✓ News Filter ready");
            Print("✓ Dynamic Score ready");
            Print("✓ Bot v4.0 ready");
            Print("═════════════════════════════════════════");
        }

        protected override void OnBar()
        {
            if (Bars.Count < Math.Max(EmaSlowPeriod, 210)) return;

            HandleNewTradingDayIfNeeded();

            if (Account.Balance > highestBalanceToday)
                highestBalanceToday = Account.Balance;

            // Drawdown Tracking
            if (Account.Balance > peakBalance) peakBalance = Account.Balance;
            double dd    = peakBalance - Account.Balance;
            double ddPct = peakBalance > 0 ? (dd / peakBalance) * 100.0 : 0;
            if (dd    > maxDrawdown)        maxDrawdown        = dd;
            if (ddPct > maxDrawdownPercent) maxDrawdownPercent = ddPct;

            if (EnableSwapProtection) RunSwapProtection();

            // News: bestehende Positionen schließen wenn konfiguriert
            if (EnableNewsFilter && ClosePositionsOnNews && IsNewsTime())
            {
                var openPos = Positions.FindAll(BotLabel, SymbolName);
                if (openPos.Length > 0)
                    CloseAllPositionsWithReason(openPos, "News Filter");
            }

            ManageOpenPositions();

            if (!ShouldTrade()) return;

            CheckForEntrySignal();
        }

        protected override void OnTick()
        {
            if (EnableSwapProtection) RunSwapProtection();
            ManageOpenPositions();
        }

        protected override void OnStop()
        {
            PrintDailySummary();
            PrintLifetimeStats();
            Print("\n╔════════════════════════════════════════╗");
            Print("║           BOT STOPPED v4.0             ║");
            Print("╚════════════════════════════════════════╝");
            Print($"Final Balance: {Account.Balance:F2}");
            Print("═════════════════════════════════════════\n");
        }

        // ==================== NEWS FILTER ====================

        private List<(int Hour, int Minute)> BuildNewsTimes()
        {
            var list = new List<(int, int)>();
            void Add(int h, int m) { if (h >= 0) list.Add((h, m)); }
            Add(NewsTime1Hour, NewsTime1Minute);
            Add(NewsTime2Hour, NewsTime2Minute);
            Add(NewsTime3Hour, NewsTime3Minute);
            Add(NewsTime4Hour, NewsTime4Minute);
            Add(NewsTime5Hour, NewsTime5Minute);
            return list;
        }

        /// <summary>
        /// Gibt true zurück wenn aktuelle Zeit innerhalb des
        /// News-Blocks liegt (Before / After Fenster um jede News-Zeit).
        /// </summary>
        private bool IsNewsTime()
        {
            if (!EnableNewsFilter || newsTimes.Count == 0) return false;

            var now = Server.Time;
            foreach (var news in newsTimes)
            {
                var newsTime  = new DateTime(now.Year, now.Month, now.Day, news.Hour, news.Minute, 0);
                var blockStart = newsTime.AddMinutes(-NewsBlockBefore);
                var blockEnd   = newsTime.AddMinutes(NewsBlockAfter);
                if (now >= blockStart && now <= blockEnd)
                    return true;
            }
            return false;
        }

        // ==================== HTF SUPERTREND ====================

        /// <summary>
        /// Phase 3: Vollständige Erstberechnung + inkrementelles Update.
        /// Nur bei neuen HTF-Kerzen wird die letzte Kerze neu berechnet
        /// — nicht die gesamte History.
        /// </summary>
        private void RecalculateHtfSupertrendFull()
        {
            int count = htfBars.Count;
            if (count < SupertrendPeriod + 2) return;

            int startIdx = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);

            double prevUpper = 0, prevLower = 0;
            bool   prevBull  = true;

            for (int i = startIdx; i < count - 1; i++)
            {
                double upper, lower;
                bool   bull;
                CalcHtfBar(i, prevUpper, prevLower, prevBull, out upper, out lower, out bull);

                int idx = i - startIdx;
                if (idx >= 0 && idx < HtfStHistory)
                {
                    htfStBand[idx] = bull ? lower : upper;
                    htfStBull[idx] = bull;
                }

                prevUpper = upper;
                prevLower = lower;
                prevBull  = bull;
            }

            // Cache letzten Zustand für inkrementelles Update
            _htfPrevUpperBand = prevUpper;
            _htfPrevLowerBand = prevLower;
            _htfPrevBull      = prevBull;
            _htfLastFullCalc  = count - 2;
            htfStCalcUpTo     = count - 2;
        }

        /// <summary>
        /// Phase 3: Inkrementelles Update — nur neue Kerze berechnen.
        /// Spart bis zu 499 Iterationen pro HTF-Bar gegenüber v3.5.
        /// </summary>
        private void UpdateHtfSupertrendIncremental()
        {
            int count    = htfBars.Count;
            int newIndex = count - 2; // Letzte bestätigte HTF-Kerze

            if (newIndex <= _htfLastFullCalc) return; // Nichts Neues

            double upper, lower;
            bool   bull;
            CalcHtfBar(newIndex, _htfPrevUpperBand, _htfPrevLowerBand, _htfPrevBull,
                       out upper, out lower, out bull);

            int startIdx = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);
            int idx      = newIndex - startIdx;
            if (idx >= 0 && idx < HtfStHistory)
            {
                htfStBand[idx] = bull ? lower : upper;
                htfStBull[idx] = bull;
            }

            _htfPrevUpperBand = upper;
            _htfPrevLowerBand = lower;
            _htfPrevBull      = bull;
            _htfLastFullCalc  = newIndex;
            htfStCalcUpTo     = newIndex;
        }

        private void CalcHtfBar(int i, double prevUpper, double prevLower, bool prevBull,
                                 out double upperBand, out double lowerBand, out bool bull)
        {
            // ATR (SMA)
            double atrSum = 0; int atrCount = 0;
            for (int j = i - SupertrendPeriod + 1; j <= i; j++)
            {
                if (j <= 0) continue;
                double tr = Math.Max(htfBars.HighPrices[j] - htfBars.LowPrices[j],
                            Math.Max(Math.Abs(htfBars.HighPrices[j] - htfBars.ClosePrices[j - 1]),
                                     Math.Abs(htfBars.LowPrices[j]  - htfBars.ClosePrices[j - 1])));
                atrSum += tr; atrCount++;
            }
            double atrVal = atrCount > 0 ? atrSum / atrCount : 0;

            double hl2  = (htfBars.HighPrices[i] + htfBars.LowPrices[i]) / 2.0;
            upperBand   = hl2 + SupertrendMultiplier * atrVal;
            lowerBand   = hl2 - SupertrendMultiplier * atrVal;

            // Band-Anpassung
            if (prevUpper > 0 || prevLower > 0)
            {
                double prevClose = htfBars.ClosePrices[i - 1];
                lowerBand = (lowerBand > prevLower || prevClose < prevLower) ? lowerBand : prevLower;
                upperBand = (upperBand < prevUpper || prevClose > prevUpper) ? upperBand : prevUpper;
            }

            // Trend-Richtung
            double close = htfBars.ClosePrices[i];
            if (prevUpper == 0 && prevLower == 0)
                bull = close > hl2;
            else if (prevBull)
                bull = close >= lowerBand;
            else
                bull = close >  upperBand;
        }

        private bool GetHtfSupertrendBullish()
        {
            int count = htfBars.Count;

            if (count - 2 != htfStCalcUpTo)
                UpdateHtfSupertrendIncremental();

            int startIdx = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);
            int idx      = (count - 2) - startIdx;

            if (idx < 0 || idx >= HtfStHistory) return false;
            return htfStBull[idx];
        }

        // ==================== TRADE JOURNAL ====================

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var    pos   = args.Position;
            double pnl   = pos.NetProfit;
            bool   isWin = pnl > 0;

            totalTrades++;
            if (isWin) { totalWins++;   totalWinAmount  += pnl; }
            else       { totalLosses++; totalLossAmount += Math.Abs(pnl); }

            // FIX Phase 3: Pattern/Score aus Position-Dictionary (kein Reset-Timing Problem)
            string pat = positionPatterns.ContainsKey(pos.Id) ? positionPatterns[pos.Id] : "None";
            int    sc  = positionScores.ContainsKey(pos.Id)   ? positionScores[pos.Id]   : 0;

            positionPatterns.Remove(pos.Id);
            positionScores.Remove(pos.Id);

            // Pattern Stats
            if (!patternWins.ContainsKey(pat))
            { patternWins[pat] = 0; patternLosses[pat] = 0; patternPnl[pat] = 0.0; }
            if (isWin) patternWins[pat]++; else patternLosses[pat]++;
            patternPnl[pat] += pnl;

            // Score Stats
            if (!scoreWins.ContainsKey(sc))   scoreWins[sc]   = 0;
            if (!scoreLosses.ContainsKey(sc))  scoreLosses[sc] = 0;
            if (isWin) scoreWins[sc]++; else scoreLosses[sc]++;

            double wr = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0;
            double pf = totalLossAmount > 0 ? totalWinAmount / totalLossAmount : 0;

            Print($"\n── Trade Closed [{(isWin ? "WIN ✓" : "LOSS ✗")}] ─────────────────");
            Print($"   P/L:           {pnl:F2}");
            Print($"   Pattern:       {pat}");
            Print($"   Score:         {sc}/{GetMaxPossibleScore()}");
            Print($"   Close Reason:  {args.Reason}");
            Print($"   Trades:        {totalTrades} (W:{totalWins} L:{totalLosses})");
            Print($"   Win Rate:      {wr:F1}%  |  PF: {pf:F2}");
            Print($"   Max Drawdown:  {maxDrawdown:F2} ({maxDrawdownPercent:F2}%)");
            Print($"──────────────────────────────────────────\n");
        }

        private void PrintLifetimeStats()
        {
            if (totalTrades == 0) return;

            double wr         = (double)totalWins / totalTrades * 100.0;
            double pf         = totalLossAmount > 0 ? totalWinAmount / totalLossAmount : 0;
            double avgWin     = totalWins   > 0 ? totalWinAmount  / totalWins   : 0;
            double avgLoss    = totalLosses > 0 ? totalLossAmount / totalLosses : 0;
            double expectancy = (wr / 100.0 * avgWin) - ((1.0 - wr / 100.0) * avgLoss);

            Print("\n╔══════════ LIFETIME STATISTICS ═════════╗");
            Print($"║ Total Trades:    {totalTrades}");
            Print($"║ Wins / Losses:   {totalWins} / {totalLosses}");
            Print($"║ Win Rate:        {wr:F1}%");
            Print($"║ Avg Win:         {avgWin:F2}");
            Print($"║ Avg Loss:        {avgLoss:F2}");
            Print($"║ Profit Factor:   {pf:F2}");
            Print($"║ Expectancy:      {expectancy:F2} per trade");
            Print($"║ Max Drawdown:    {maxDrawdown:F2} ({maxDrawdownPercent:F2}%)");
            Print("╠══════════ PATTERN PERFORMANCE ═════════╣");
            foreach (var kvp in patternPnl)
            {
                int    w   = patternWins.ContainsKey(kvp.Key)   ? patternWins[kvp.Key]   : 0;
                int    l   = patternLosses.ContainsKey(kvp.Key) ? patternLosses[kvp.Key] : 0;
                int    tot = w + l;
                double pwr = tot > 0 ? (double)w / tot * 100.0 : 0;
                Print($"║ {kvp.Key,-22} W:{w,2} L:{l,2} WR:{pwr,4:F0}% PnL:{kvp.Value:F2}");
            }
            Print("╠══════════ SCORE PERFORMANCE ═══════════╣");
            int maxSc = GetMaxPossibleScore();
            for (int s = MinScoreToTrade; s <= maxSc; s++)
            {
                int w   = scoreWins.ContainsKey(s)   ? scoreWins[s]   : 0;
                int l   = scoreLosses.ContainsKey(s) ? scoreLosses[s] : 0;
                int tot = w + l;
                if (tot == 0) continue;
                double swr = (double)w / tot * 100.0;
                Print($"║ Score {s,2}/{maxSc,-2}          W:{w,2} L:{l,2} WR:{swr,4:F0}%");
            }
            Print("╚════════════════════════════════════════╝\n");
        }

        // ==================== DAY / SESSION ====================

        private void HandleNewTradingDayIfNeeded()
        {
            if (Server.Time.Date == lastTradeDate) return;
            PrintDailySummary();
            ResetDailyTracking();
            Print($"\n═══ NEW TRADING DAY: {Server.Time.Date:yyyy-MM-dd} ═══");
        }

        private void ResetDailyTracking()
        {
            tradesOpenedToday         = 0;
            dailyStartBalance         = Account.Balance;
            highestBalanceToday       = Account.Balance;
            lastTradeDate             = Server.Time.Date;
            swapThresholdCheckedToday = false;
            hardCloseExecutedToday    = false;

            managedPositionId = null;
            tp1Hit            = false;
            tp2Hit            = false;
            trailingActive    = false;
            breakEvenSet      = false;
            lastCandlePattern = "None";
        }

        /// <summary>
        /// Phase 3: Session mit Minuten (statt nur Stunden) für
        /// präzisere Steuerung und bessere Optimizer-Granularität.
        /// </summary>
        private bool IsInTradingSession()
        {
            var now         = Server.Time;
            int nowMinutes  = now.Hour * 60 + now.Minute;
            int startMins   = StartHour * 60 + StartMinute;
            int endMins     = EndHour   * 60 + EndMinute;

            return startMins < endMins
                ? nowMinutes >= startMins && nowMinutes < endMins
                : nowMinutes >= startMins || nowMinutes < endMins;
        }

        private double GetSpreadPips() => Symbol.Spread / Symbol.PipSize;

        // ==================== SWAP PROTECTION ====================

        private void RunSwapProtection()
        {
            var now       = Server.Time;
            var positions = Positions.FindAll(BotLabel, SymbolName);
            if (positions.Length == 0) return;

            bool isFri = now.DayOfWeek == DayOfWeek.Friday;
            bool isWed = now.DayOfWeek == DayOfWeek.Wednesday;

            if (EnableSwapThreshold && !swapThresholdCheckedToday)
                if (IsTimeWindow(now, SwapCheckHour, SwapCheckMinute, 5))
                { swapThresholdCheckedToday = true; RunSwapThresholdCheck(positions); }

            if (EnableTripleSwapGuard && isWed)
                if (IsTimeWindow(now, TripleSwapCloseHour, TripleSwapCloseMinute, 5))
                { CloseAllPositionsWithReason(positions, "Triple Swap (3x Wed)"); return; }

            if (EnableWeekendProtection && isFri)
                if (IsTimeWindow(now, WeekendCloseHour, WeekendCloseMinute, 5))
                { CloseAllPositionsWithReason(positions, "Weekend Protection"); return; }

            if (!hardCloseExecutedToday)
                if (IsTimeWindow(now, HardCloseHour, HardCloseMinute, 5))
                {
                    hardCloseExecutedToday = true;
                    if (HardCloseLosingOnly) CloseLosingPositions(positions, "Hard Close (Losing)");
                    else CloseAllPositionsWithReason(positions, "Hard Close (Daily)");
                }
        }

        private bool IsTimeWindow(DateTime now, int hour, int minute, int windowMinutes)
        {
            var target = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
            return now >= target && now <= target.AddMinutes(windowMinutes);
        }

        private bool IsWithinMinutesBefore(DateTime now, int h, int m, int buffer)
        {
            var target = new DateTime(now.Year, now.Month, now.Day, h, m, 0);
            return now >= target.AddMinutes(-buffer) && now < target;
        }

        private void RunSwapThresholdCheck(Position[] positions)
        {
            Print("\n── Swap Threshold Check ──────────────────");
            foreach (var pos in positions)
            {
                double pnl  = pos.NetProfit;
                double swap = EstimateNextSwap(pos);
                if (pnl <= 0)
                { ClosePositionWithReason(pos, $"Swap: Losing ({pnl:F2})"); continue; }
                if (Math.Abs(swap) > 0)
                {
                    double ratio = (Math.Abs(swap) / pnl) * 100.0;
                    if (ratio >= SwapThresholdPercent)
                        ClosePositionWithReason(pos, $"Swap {ratio:F1}% of profit");
                    else
                        Print($"  ✓ Kept #{pos.Id}: Swap {ratio:F1}% < {SwapThresholdPercent:F0}%");
                }
            }
            Print("── Check complete ────────────────────────\n");
        }

        private double EstimateNextSwap(Position pos)
        {
            double lots  = Symbol.VolumeInUnitsToQuantity(pos.VolumeInUnits);
            double rate  = pos.TradeType == TradeType.Buy ? Symbol.SwapLong : Symbol.SwapShort;
            double mult  = Server.Time.DayOfWeek == DayOfWeek.Wednesday ? 3.0 : 1.0;
            return rate * lots * mult;
        }

        private void CloseAllPositionsWithReason(Position[] positions, string reason)
        {
            Print($"\n⚡ SWAP PROTECTION: {reason}");
            foreach (var p in positions) ClosePositionWithReason(p, reason);
        }

        private void CloseLosingPositions(Position[] positions, string reason)
        {
            Print($"\n⚡ SWAP PROTECTION: {reason}");
            foreach (var p in positions)
            {
                if (p.NetProfit < 0) ClosePositionWithReason(p, reason);
                else Print($"  ✓ Kept #{p.Id} (+{p.NetProfit:F2})");
            }
        }

        private void ClosePositionWithReason(Position pos, string reason)
        {
            double pnl = pos.NetProfit;
            var    res = ClosePosition(pos);
            if (res.IsSuccessful)
                Print($"  ✓ Closed #{pos.Id} {pos.TradeType} | P/L:{pnl:F2} | {reason}");
            else
                Print($"  ✗ Failed #{pos.Id}: {res.Error}");
        }

        // ==================== TRADE GATING ====================

        private bool ShouldTrade()
        {
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0) return false;
            if (tradesOpenedToday >= MaxDailyTrades) return false;

            double pnl   = Account.Balance - dailyStartBalance;
            double limit = dailyStartBalance * (MaxDailyLossPercent / 100.0);
            if (pnl < -limit) { Print("⚠ Daily loss limit."); return false; }

            if (!IsInTradingSession()) return false;

            double spread = GetSpreadPips();
            if (spread > MaxSpreadPips) { Print($"⚠ Spread: {spread:F2}"); return false; }

            if (EnableSwapProtection && IsSwapProtectionImminent()) return false;

            // News-Block
            if (EnableNewsFilter && IsNewsTime())
            {
                Print("⚠ News Block active: No entry.");
                return false;
            }

            // ADX Range-Filter
            if (EnableAdxFilter && adx.ADX[1] < AdxMaxRange)
            {
                Print($"⚠ ADX {adx.ADX[1]:F1} < {AdxMaxRange:F1}: Range.");
                return false;
            }

            return true;
        }

        private bool IsSwapProtectionImminent()
        {
            var now    = Server.Time;
            int buffer = 30;
            bool isFri = now.DayOfWeek == DayOfWeek.Friday;
            bool isWed = now.DayOfWeek == DayOfWeek.Wednesday;

            if (EnableWeekendProtection && isFri &&
                IsWithinMinutesBefore(now, WeekendCloseHour, WeekendCloseMinute, buffer))
            { Print($"⚠ No entry: Weekend Close <{buffer}min"); return true; }

            if (EnableTripleSwapGuard && isWed &&
                IsWithinMinutesBefore(now, TripleSwapCloseHour, TripleSwapCloseMinute, buffer))
            { Print($"⚠ No entry: Triple Swap <{buffer}min"); return true; }

            if (IsWithinMinutesBefore(now, HardCloseHour, HardCloseMinute, buffer))
            { Print($"⚠ No entry: Hard Close <{buffer}min"); return true; }

            return false;
        }

        // ==================== SCORE-BASED SIZING ====================

        private double CalculateRiskMultiplier(int score, int minScore, int maxScore)
        {
            if (!EnableScoreSizing) return 1.0;
            if (maxScore <= minScore) return RiskMultiplierMin;
            double t = Math.Max(0.0, Math.Min(1.0,
                (double)(score - minScore) / (maxScore - minScore)));
            return RiskMultiplierMin + t * (RiskMultiplierMax - RiskMultiplierMin);
        }

        // ==================== DYNAMIC SCORE ====================

        /// <summary>
        /// Phase 3: Dynamischer Score gewichtet mit ADX-Stärke.
        ///
        /// Formel:
        ///   adxFactor = Clamp(ADX / AdxMinTrend, 0.5, 2.0)
        ///   effectiveScore = rawScore × adxFactor
        ///
        /// Beispiele (AdxMinTrend = 25):
        ///   ADX = 50: factor = 2.0 → Score 7 × 2.0 = 14.0 (starker Trend)
        ///   ADX = 25: factor = 1.0 → Score 7 × 1.0 = 7.0  (normaler Trend)
        ///   ADX = 12: factor = 0.5 → Score 7 × 0.5 = 3.5  (schwacher Trend)
        /// </summary>
        private double ApplyDynamicScore(int rawScore)
        {
            if (!EnableDynamicScore || !EnableAdxFilter) return rawScore;

            double adxVal   = adx.ADX[1];
            double factor   = adxVal / AdxMinTrend;
            factor          = Math.Max(0.5, Math.Min(2.0, factor));
            return rawScore * factor;
        }

        // ==================== CANDLESTICK PATTERNS ====================

        private double CandleBody(int i)      => Math.Abs(Bars.ClosePrices[i] - Bars.OpenPrices[i]);
        private double CandleRange(int i)     => Bars.HighPrices[i] - Bars.LowPrices[i];
        private double UpperWick(int i)       => Bars.HighPrices[i] - Math.Max(Bars.OpenPrices[i], Bars.ClosePrices[i]);
        private double LowerWick(int i)       => Math.Min(Bars.OpenPrices[i], Bars.ClosePrices[i]) - Bars.LowPrices[i];
        private bool   IsBullishCandle(int i) => Bars.ClosePrices[i] > Bars.OpenPrices[i];
        private bool   IsBearishCandle(int i) => Bars.ClosePrices[i] < Bars.OpenPrices[i];

        private bool IsBullishEngulfing()
        {
            if (!EnableEngulfing) return false;
            if (!IsBearishCandle(2) || !IsBullishCandle(1)) return false;
            double pb = CandleBody(2); if (pb <= 0) return false;
            return Bars.ClosePrices[1] > Bars.OpenPrices[2]
                && Bars.OpenPrices[1]  < Bars.ClosePrices[2]
                && CandleBody(1) >= pb * EngulfingBodyRatio;
        }

        private bool IsBearishEngulfing()
        {
            if (!EnableEngulfing) return false;
            if (!IsBullishCandle(2) || !IsBearishCandle(1)) return false;
            double pb = CandleBody(2); if (pb <= 0) return false;
            return Bars.ClosePrices[1] < Bars.OpenPrices[2]
                && Bars.OpenPrices[1]  > Bars.ClosePrices[2]
                && CandleBody(1) >= pb * EngulfingBodyRatio;
        }

        private bool IsBullishPinBar()
        {
            if (!EnablePinBar) return false;
            double body = CandleBody(1); double range = CandleRange(1);
            if (range <= 0 || body <= 0) return false;
            return LowerWick(1) >= body * PinBarWickRatio
                && UpperWick(1) <= body * 0.5
                && body <= range * 0.35;
        }

        private bool IsBearishPinBar()
        {
            if (!EnablePinBar) return false;
            double body = CandleBody(1); double range = CandleRange(1);
            if (range <= 0 || body <= 0) return false;
            return UpperWick(1) >= body * PinBarWickRatio
                && LowerWick(1) <= body * 0.5
                && body <= range * 0.35;
        }

        private bool IsBullishInsideBar()
        {
            if (!EnableInsideBar) return false;
            if (CandleRange(2) < InsideBarMinMotherSizeAtr * atr.Result[2]) return false;
            return Bars.HighPrices[1] < Bars.HighPrices[2]
                && Bars.LowPrices[1]  > Bars.LowPrices[2]
                && IsBullishCandle(1);
        }

        private bool IsBearishInsideBar()
        {
            if (!EnableInsideBar) return false;
            if (CandleRange(2) < InsideBarMinMotherSizeAtr * atr.Result[2]) return false;
            return Bars.HighPrices[1] < Bars.HighPrices[2]
                && Bars.LowPrices[1]  > Bars.LowPrices[2]
                && IsBearishCandle(1);
        }

        private bool IsMorningStar()
        {
            if (!EnableStar) return false;
            if (CandleBody(1) <= 0 || CandleBody(3) <= 0) return false;
            double minSize = atr.Result[2] * 0.5;
            return IsBearishCandle(3) && CandleBody(3) >= minSize
                && CandleBody(2) <= CandleBody(3) * 0.3
                && IsBullishCandle(1) && CandleBody(1) >= minSize
                && Bars.ClosePrices[1] > (Bars.OpenPrices[3] + Bars.ClosePrices[3]) / 2.0;
        }

        private bool IsEveningStar()
        {
            if (!EnableStar) return false;
            if (CandleBody(1) <= 0 || CandleBody(3) <= 0) return false;
            double minSize = atr.Result[2] * 0.5;
            return IsBullishCandle(3) && CandleBody(3) >= minSize
                && CandleBody(2) <= CandleBody(3) * 0.3
                && IsBearishCandle(1) && CandleBody(1) >= minSize
                && Bars.ClosePrices[1] < (Bars.OpenPrices[3] + Bars.ClosePrices[3]) / 2.0;
        }

        private bool HasBullishCandlePattern(out string name)
        {
            name = "None";
            if (!EnableCandlePatterns) return false;
            if (IsBullishEngulfing()) { name = "Bullish Engulfing"; return true; }
            if (IsBullishPinBar())    { name = "Pin Bar (Hammer)";  return true; }
            if (IsBullishInsideBar()) { name = "Inside Bar (Bull)"; return true; }
            if (IsMorningStar())      { name = "Morning Star";      return true; }
            return false;
        }

        private bool HasBearishCandlePattern(out string name)
        {
            name = "None";
            if (!EnableCandlePatterns) return false;
            if (IsBearishEngulfing()) { name = "Bearish Engulfing"; return true; }
            if (IsBearishPinBar())    { name = "Shooting Star";     return true; }
            if (IsBearishInsideBar()) { name = "Inside Bar (Bear)"; return true; }
            if (IsEveningStar())      { name = "Evening Star";      return true; }
            return false;
        }

        // ==================== SIGNAL SCORING ====================

        private void CalculateSignalScores(out double longScore,  out double shortScore,
                                           out string longPat,    out string shortPat)
        {
            int rawLong = 0, rawShort = 0;
            longPat = "None"; shortPat = "None";

            double price = Bars.ClosePrices[1];

            // 1. Supertrend
            if (EnableSupertrendFilter)
            {
                if (!double.IsNaN(supertrend.UpTrend[1]))   rawLong  += ScoreSupertrend;
                if (!double.IsNaN(supertrend.DownTrend[1])) rawShort += ScoreSupertrend;
            }

            // 2. EMA Trend
            if (EnableEmaTrendFilter)
            {
                double eSlow = emaSlow.Result[1];
                if (price > eSlow) rawLong  += ScoreEmaTrend;
                if (price < eSlow) rawShort += ScoreEmaTrend;
            }

            // 3. EMA Momentum
            if (EnableEmaMomentumFilter)
            {
                double eFast = emaFast.Result[1]; double eMid = emaMid.Result[1];
                if (eFast > eMid) rawLong  += ScoreEmaMomentum;
                if (eFast < eMid) rawShort += ScoreEmaMomentum;
            }

            // 4. RSI
            if (EnableRsiFilter)
            {
                double r = rsi.Result[1];
                if (r > RsiLongMin  && r < RsiOverbought) rawLong  += ScoreRsi;
                if (r > RsiOversold && r < RsiShortMax)   rawShort += ScoreRsi;
            }

            // 5. MACD
            if (EnableMacdFilter)
            {
                double h = macd.Histogram[1]; double hp = macd.Histogram[2];
                if (MacdMode == 0) { if (h > 0 && hp <= 0) rawLong  += ScoreMacd;
                                     if (h < 0 && hp >= 0) rawShort += ScoreMacd; }
                else               { if (h > 0) rawLong  += ScoreMacd;
                                     if (h < 0) rawShort += ScoreMacd; }
            }

            // 6. MTF
            if (EnableMtfFilter)
            {
                if (IsMtfBullish()) rawLong  += ScoreMtfConfirm;
                if (IsMtfBearish()) rawShort += ScoreMtfConfirm;
            }

            // 7. Candle Patterns
            if (EnableCandlePatterns)
            {
                if (HasBullishCandlePattern(out longPat))  rawLong  += ScoreCandlePattern;
                if (HasBearishCandlePattern(out shortPat)) rawShort += ScoreCandlePattern;
            }

            // 8. ADX Bonus
            if (EnableAdxFilter && adx.ADX[1] >= AdxMinTrend)
            {
                if (adx.DIPlus[1]  > adx.DIMinus[1]) rawLong  += ScoreAdxTrend;
                if (adx.DIMinus[1] > adx.DIPlus[1])  rawShort += ScoreAdxTrend;
            }

            // Phase 3: Dynamischer Score (×ADX-Faktor)
            longScore  = ApplyDynamicScore(rawLong);
            shortScore = ApplyDynamicScore(rawShort);
        }

        private bool IsMtfBullish()
            => GetHtfSupertrendBullish()
            && htfEma50.Result[1] > htfEma200.Result[1];

        private bool IsMtfBearish()
            => !GetHtfSupertrendBullish()
            && htfEma50.Result[1] < htfEma200.Result[1];

        private int GetMaxPossibleScore()
            => ScoreSupertrend + ScoreEmaTrend + ScoreEmaMomentum
             + ScoreRsi + ScoreMacd + ScoreMtfConfirm + ScoreCandlePattern + ScoreAdxTrend;

        // ==================== ENTRY LOGIC ====================

        private void CheckForEntrySignal()
        {
            CalculateSignalScores(
                out double longScore,  out double shortScore,
                out string longPat,    out string shortPat);

            lastLongScore  = (int)longScore;
            lastShortScore = (int)shortScore;

            int    maxScore = GetMaxPossibleScore();
            double atrVal   = atr.Result[1];
            double minScore = EnableDynamicScore ? MinScoreToTrade : MinScoreToTrade;

            if (longScore >= minScore)
            {
                lastCandlePattern = longPat;
                int rawScore = (int)Math.Round(longScore);
                positionPatterns[0] = longPat;  // Temp: wird nach ExecuteMarketOrder mit echter ID ersetzt
                double mult = CalculateRiskMultiplier(rawScore, MinScoreToTrade, maxScore);
                ExecuteTrade(TradeType.Buy, atrVal, rawScore, longPat, mult);
                return;
            }

            if (shortScore >= minScore)
            {
                lastCandlePattern = shortPat;
                int rawScore = (int)Math.Round(shortScore);
                double mult = CalculateRiskMultiplier(rawScore, MinScoreToTrade, maxScore);
                ExecuteTrade(TradeType.Sell, atrVal, rawScore, shortPat, mult);
            }
        }

        // ==================== EXECUTION ====================

        private void ExecuteTrade(TradeType tradeType, double atrVal,
                                  int signalScore, string candlePattern,
                                  double riskMultiplier)
        {
            double? ml = Account.MarginLevel;
            if (ml.HasValue && ml.Value < 200) { Print($"⚠ ML: {ml.Value:F0}%"); return; }

            double effectiveRisk  = RiskPercent * riskMultiplier;
            double riskAmount     = Account.Balance * (effectiveRisk / 100.0);
            double stopLossPips   = (StopLossMultiplier * atrVal) / Symbol.PipSize;
            double takeProfitPips = (TakeProfit3Multiplier * atrVal) / Symbol.PipSize;

            if (stopLossPips <= 0 || takeProfitPips <= 0) { Print("Invalid SL/TP."); return; }

            double vol = CalculateVolumeInUnitsFromRisk(riskAmount, stopLossPips);
            if (vol < Symbol.VolumeInUnitsMin) { Print("⚠ Volume too small."); return; }

            double reqMargin = Symbol.GetEstimatedMargin(tradeType, vol);
            if (Account.Margin + reqMargin > 0)
            {
                double mlAfter = (Account.Equity / (Account.Margin + reqMargin)) * 100;
                if (mlAfter < 150) { Print($"⚠ ML after: {mlAfter:F0}%"); return; }
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, vol,
                BotLabel, stopLossPips, takeProfitPips);

            if (!result.IsSuccessful) { Print($"✗ {result.Error}"); return; }

            // FIX Phase 3: Pattern/Score per Position ID speichern
            long posId = result.Position.Id;
            positionPatterns[posId] = candlePattern;
            positionScores[posId]   = signalScore;

            tradesOpenedToday++;
            managedPositionId = posId;
            entryPrice        = result.Position.EntryPrice;
            tp1Hit            = false;
            tp2Hit            = false;
            trailingActive    = false;
            breakEvenSet      = false;

            int    maxScore = GetMaxPossibleScore();
            double lots     = Symbol.VolumeInUnitsToQuantity(vol);
            string scoreBar = BuildScoreBar(signalScore, maxScore);
            string patDisp  = candlePattern != "None" ? $"✓ {candlePattern}" : "─ None";
            double estSwap  = EstimateNextSwap(result.Position);
            double dynScore = ApplyDynamicScore(signalScore);

            Print($"\n┌─── TRADE OPENED #{tradesOpenedToday} ─────────────────");
            Print($"│ Type:       {tradeType}");
            Print($"│ Score:      {scoreBar} {signalScore}/{maxScore} (dyn:{dynScore:F1})");
            Print($"│ Pattern:    {patDisp}");
            Print($"│ ADX:        {adx.ADX[1]:F1} D+:{adx.DIPlus[1]:F1} D-:{adx.DIMinus[1]:F1}");
            Print($"│ HTF:        {(GetHtfSupertrendBullish() ? "▲ Bull" : "▼ Bear")} EMA:{(htfEma50.Result[1] > htfEma200.Result[1] ? "Bull" : "Bear")}");
            Print($"│ RiskMult:   {riskMultiplier:F2}x → {effectiveRisk:F2}%");
            Print($"│ Volume:     {lots:F2} lots");
            Print($"│ Entry:      {result.Position.EntryPrice:F5}");
            Print($"│ SL:         {stopLossPips:F1} pips  |  TP3: {takeProfitPips:F1} pips");
            Print($"│ R:R:        1:{(takeProfitPips / stopLossPips):F2}");
            Print($"│ Risk:       {riskAmount:F2} ({effectiveRisk:F2}%)");
            Print($"│ EstSwap:    {estSwap:F2}");
            Print($"│ News Block: {(IsNewsTime() ? "YES ⚠" : "No")}");
            Print($"│ Time:       {Server.Time:HH:mm:ss} UTC");
            Print($"└────────────────────────────────────────");
        }

        private string BuildScoreBar(int score, int maxScore)
        {
            int filled = maxScore > 0 ? (int)Math.Round((double)score / maxScore * 7) : 0;
            string bar = "[";
            for (int i = 0; i < 7; i++) bar += i < filled ? "█" : "░";
            return bar + "]";
        }

        // ==================== POSITION SIZING ====================

        private double CalculateVolumeInUnitsFromRisk(double riskAmount, double stopLossPips)
        {
            double vol = riskAmount / (stopLossPips * Symbol.PipValue);
            double cap = (Account.Balance * 0.03) / (stopLossPips * Symbol.PipValue);
            vol = Math.Min(vol, cap);
            vol = Math.Min(vol, CalculateMaxVolumeFromMargin(MaxMarginUsagePercent));

            if (double.IsNaN(vol) || double.IsInfinity(vol) || vol <= 0)
                return Symbol.VolumeInUnitsMin;

            vol = Symbol.NormalizeVolumeInUnits(vol, RoundingMode.Down);
            vol = Math.Max(vol, Symbol.VolumeInUnitsMin);
            vol = Math.Min(vol, Symbol.VolumeInUnitsMax);
            return vol;
        }

        private double CalculateMaxVolumeFromMargin(double maxPct)
        {
            double allowed = Account.FreeMargin * (maxPct / 100.0);
            double minVol  = Symbol.VolumeInUnitsMin;
            double maxVol  = Symbol.VolumeInUnitsMax;
            double optimal = minVol;
            for (int i = 0; i < 20; i++)
            {
                double test   = Symbol.NormalizeVolumeInUnits((minVol + maxVol) / 2.0, RoundingMode.Down);
                double margin = Symbol.GetEstimatedMargin(TradeType.Buy, test);
                if (margin <= allowed) { optimal = test; minVol = test; }
                else maxVol = test;
                if (Math.Abs(maxVol - minVol) < Symbol.VolumeInUnitsStep) break;
            }
            return optimal;
        }

        // ==================== POSITION MANAGEMENT ====================

        private void ManageOpenPositions()
        {
            var positions = Positions.FindAll(BotLabel, SymbolName);

            if (positions.Length == 0)
            {
                managedPositionId = null;
                tp1Hit = false; tp2Hit = false;
                trailingActive = false; breakEvenSet = false;
                return;
            }

            var pos = positions[0];

            if (managedPositionId == null || managedPositionId.Value != pos.Id)
            {
                managedPositionId = pos.Id;
                entryPrice        = pos.EntryPrice;
                tp1Hit = false; tp2Hit = false;
                trailingActive = false; breakEvenSet = false;
            }

            if (EnablePartialTPs)                     CheckPartialClose(pos);
            if (EnableBreakEven)                      CheckBreakEven(pos);
            if (EnableTrailingStop && trailingActive) ApplyTrailingStop(pos);
        }

        private void CheckPartialClose(Position pos)
        {
            double atrVal     = atr.Result[1];
            double profitPips = Math.Abs(pos.Pips);

            if (!tp1Hit && profitPips >= (TakeProfit1Multiplier * atrVal) / Symbol.PipSize)
            {
                bool closed = TryPartialClose(pos, Tp1ClosePercent, "TP1", profitPips);
                tp1Hit = true;
                if (closed && EnableTrailingStop)
                { trailingActive = true; Print("✓ Trailing Stop activated."); }
            }

            if (!tp2Hit && profitPips >= (TakeProfit2Multiplier * atrVal) / Symbol.PipSize)
            {
                TryPartialClose(pos, Tp2ClosePercent, "TP2", profitPips);
                tp2Hit = true;
            }
        }

        /// <summary>
        /// Phase 3 Fix: Break-Even mit Mindest-Buffer (2× Spread).
        /// Verhindert sofortigen Stop-Out wenn Buffer kleiner als Spread ist.
        /// </summary>
        private void CheckBreakEven(Position pos)
        {
            if (!tp1Hit || breakEvenSet) return;

            double atrBuffer  = BreakEvenBufferAtr * atr.Result[1];
            double minBuffer  = Symbol.Spread * 2.0;                    // FIX: mindestens 2× Spread
            double buffer     = Math.Max(atrBuffer, minBuffer);
            double? currentSL = pos.StopLoss;

            if (pos.TradeType == TradeType.Buy)
            {
                double beSL = entryPrice + buffer;
                if (currentSL == null || beSL > currentSL.Value)
                {
                    ModifyPosition(pos, beSL, pos.TakeProfit, ProtectionType.Absolute);
                    breakEvenSet = true;
                    Print($"✓ Break-Even: SL → {beSL:F5} (buf:{buffer / Symbol.PipSize:F1} pips)");
                }
            }
            else
            {
                double beSL = entryPrice - buffer;
                if (currentSL == null || beSL < currentSL.Value)
                {
                    ModifyPosition(pos, beSL, pos.TakeProfit, ProtectionType.Absolute);
                    breakEvenSet = true;
                    Print($"✓ Break-Even: SL → {beSL:F5} (buf:{buffer / Symbol.PipSize:F1} pips)");
                }
            }
        }

        private bool TryPartialClose(Position pos, int closePct, string tag, double profitPips)
        {
            if (closePct <= 0) return false;
            if (pos.VolumeInUnits < Symbol.VolumeInUnitsMin * 2) return false;
            double closeUnits = Symbol.NormalizeVolumeInUnits(
                pos.VolumeInUnits * (closePct / 100.0), RoundingMode.Down);
            if (closeUnits < Symbol.VolumeInUnitsMin) closeUnits = Symbol.VolumeInUnitsMin;
            if (closeUnits >= pos.VolumeInUnits) return false;
            var res = ClosePosition(pos, closeUnits);
            if (!res.IsSuccessful) return false;
            Print($"✓ {tag}: closed {closePct}% at +{profitPips:F1} pips.");
            return true;
        }

        private void ApplyTrailingStop(Position pos)
        {
            double atrVal     = atr.Result[1];
            double profitPips = Math.Abs(pos.Pips);
            if (profitPips < (TrailingStartMultiplier * atrVal) / Symbol.PipSize) return;

            double dist = TrailingDistanceMultiplier * atrVal;
            double? sl  = pos.StopLoss;

            if (pos.TradeType == TradeType.Buy)
            {
                double newSL = Symbol.Bid - dist;
                if (sl == null || newSL > sl.Value)
                    ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
            }
            else
            {
                double newSL = Symbol.Ask + dist;
                if (sl == null || newSL < sl.Value)
                    ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
            }
        }

        // ==================== REPORTING ====================

        private void PrintDailySummary()
        {
            if (tradesOpenedToday == 0) return;

            double pnl    = Account.Balance - dailyStartBalance;
            double pnlPct = dailyStartBalance <= 0 ? 0 : (pnl / dailyStartBalance) * 100.0;
            double wr     = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0;
            double pf     = totalLossAmount > 0 ? totalWinAmount / totalLossAmount : 0;

            Print("\n╔════════════ DAILY SUMMARY ═════════════╗");
            Print($"║ Date:          {lastTradeDate:yyyy-MM-dd}");
            Print($"║ Trades Today:  {tradesOpenedToday}");
            Print($"║ Daily P/L:     {pnl:F2} ({pnlPct:F2}%)");
            Print($"║ Total WR:      {wr:F1}%  |  PF: {pf:F2}");
            Print($"║ Max Drawdown:  {maxDrawdown:F2} ({maxDrawdownPercent:F2}%)");
            Print($"║ Dynamic Score: {(EnableDynamicScore ? "ON" : "OFF")}");
            Print($"║ News Filter:   {(EnableNewsFilter ? "ON" : "OFF")}");
            Print($"║ Last Scores:   Long={lastLongScore} Short={lastShortScore}");
            Print($"║ Last Pattern:  {lastCandlePattern}");
            Print("╚════════════════════════════════════════╝\n");
        }
    }
}
