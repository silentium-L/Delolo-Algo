using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================
    //  Multi-Indicator Scalper v4.8
    //  Fixes vs v4.7:
    //  [FIX-A] MTF Hard Block: kein Long gegen HTF Bear
    //  [FIX-B] MTF Hard Block: kein Short gegen HTF Bull
    //  [FIX-C] SwapThreshold DefaultValue 20 -> 50
    //  [FIX-D] RunSwapThresholdCheck: Profitable Positionen
    //          werden nicht mehr wegen Swap geschlossen
    //  Alle Fixes aus v4.1-v4.7 enthalten
    // ============================================================
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

        // [FIX-C] DefaultValue 20.0 -> 50.0
        [Parameter("Swap Threshold (% of P/L)", DefaultValue = 50.0, MinValue = 1.0, MaxValue = 100.0)]
        public double SwapThresholdPercent { get; set; }

        [Parameter("Swap Check Hour UTC", DefaultValue = 21, MinValue = 0, MaxValue = 23)]
        public int SwapCheckHour { get; set; }

        [Parameter("Swap Check Minute", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
        public int SwapCheckMinute { get; set; }

        [Parameter("== SCORE-BASED SIZING ==")]
        public string ScoreSizingLabel { get; set; }

        [Parameter("Enable Score Sizing", DefaultValue = true)]
        public bool EnableScoreSizing { get; set; }

        [Parameter("Risk Multiplier Max Score", DefaultValue = 1.5, MinValue = 1.0, MaxValue = 3.0)]
        public double RiskMultiplierMax { get; set; }

        [Parameter("Risk Multiplier Min Score", DefaultValue = 0.75, MinValue = 0.1, MaxValue = 1.0)]
        public double RiskMultiplierMin { get; set; }

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

        [Parameter("== ADX MARKET FILTER ==")]
        public string AdxLabel { get; set; }

        [Parameter("Enable ADX Filter", DefaultValue = true)]
        public bool EnableAdxFilter { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, MaxValue = 50)]
        public int AdxPeriod { get; set; }

        [Parameter("ADX Min Trend", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0)]
        public double AdxMinTrend { get; set; }

        [Parameter("ADX Max Range (Block)", DefaultValue = 25.0, MinValue = 5.0, MaxValue = 40.0)]
        public double AdxMaxRange { get; set; }

        [Parameter("Score: ADX Bonus (0-2)", DefaultValue = 1, MinValue = 0, MaxValue = 2)]
        public int ScoreAdxTrend { get; set; }

        [Parameter("== MOMENTUM FILTER ==")]
        public string MomentumLabel { get; set; }

        [Parameter("Enable Momentum Filter", DefaultValue = true)]
        public bool EnableMomentumFilter { get; set; }

        [Parameter("ATR Momentum Ratio", DefaultValue = 0.6, MinValue = 0.2, MaxValue = 2.0)]
        public double MinAtrMomentumRatio { get; set; }

        [Parameter("ATR Avg Lookback Bars", DefaultValue = 10, MinValue = 3, MaxValue = 30)]
        public int AtrAvgLookback { get; set; }

        [Parameter("Max Bars Since Local Swing", DefaultValue = 10, MinValue = 2, MaxValue = 40)]
        public int MaxBarsSinceSwing { get; set; }

        [Parameter("Swing Neighbors (Confirmation)", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int SwingNeighbors { get; set; }

        [Parameter("Require Bar Confirmation", DefaultValue = true)]
        public bool RequireBarConfirmation { get; set; }

        [Parameter("Confirmation Bars", DefaultValue = 1, MinValue = 1, MaxValue = 3)]
        public int ConfirmationBars { get; set; }

        [Parameter("== NEWS FILTER ==")]
        public string NewsLabel { get; set; }

        [Parameter("Enable News Filter", DefaultValue = true)]
        public bool EnableNewsFilter { get; set; }

        [Parameter("News Block Minutes Before", DefaultValue = 30, MinValue = 5, MaxValue = 120)]
        public int NewsBlockBefore { get; set; }

        [Parameter("News Block Minutes After", DefaultValue = 30, MinValue = 5, MaxValue = 120)]
        public int NewsBlockAfter { get; set; }

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

        [Parameter("== MULTI-TIMEFRAME ==")]
        public string MtfLabel { get; set; }

        [Parameter("Enable MTF Filter", DefaultValue = true)]
        public bool EnableMtfFilter { get; set; }

        [Parameter("HTF Timeframe", DefaultValue = "Hour")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("== SIGNAL SCORING ==")]
        public string ScoringLabel { get; set; }

        [Parameter("Min Score to Trade", DefaultValue = 5, MinValue = 1, MaxValue = 12)]
        public int MinScoreToTrade { get; set; }

        [Parameter("Enable Dynamic Score (xADX)", DefaultValue = true)]
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

        // ==================== INDICATOR INSTANCES ====================

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

        private double[]  htfStBand;
        private bool[]    htfStBull;
        private int       htfStCalcUpTo     = -1;
        private const int HtfStHistory      = 500;
        private double    _htfPrevUpperBand = 0;
        private double    _htfPrevLowerBand = 0;
        private bool      _htfPrevBull      = true;
        private int       _htfLastFullCalc  = -1;
        private double    _htfRmaAtr        = 0;

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

        private double _tp1Price;
        private double _tp2Price;
        private double _slDistance;

        private int    lastLongScore;
        private int    lastShortScore;
        private string lastCandlePattern;

        private bool swapThresholdCheckedToday;
        private bool hardCloseExecutedToday;

        private DateTime _lastTickManage = DateTime.MinValue;

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

        private Dictionary<long, string>   positionPatterns = new Dictionary<long, string>();
        private Dictionary<long, int>      positionScores   = new Dictionary<long, int>();
        private Dictionary<string, int>    patternWins      = new Dictionary<string, int>();
        private Dictionary<string, int>    patternLosses    = new Dictionary<string, int>();
        private Dictionary<string, double> patternPnl       = new Dictionary<string, double>();
        private Dictionary<int, int>       scoreWins        = new Dictionary<int, int>();
        private Dictionary<int, int>       scoreLosses      = new Dictionary<int, int>();

        // ==================== LIFECYCLE ====================

        protected override void OnStart()
        {
            int maxScore = GetMaxPossibleScore();
            if (MinScoreToTrade > maxScore)
                Print($"WARNING: MinScoreToTrade ({MinScoreToTrade}) > Max ({maxScore})!");
            if (EnableScoreSizing && RiskMultiplierMin > RiskMultiplierMax)
                Print("WARNING: RiskMultiplierMin > RiskMultiplierMax!");
            if (EmaFastPeriod >= EmaMidPeriod)
                Print("WARNING: EmaFastPeriod >= EmaMidPeriod!");
            if (EmaMidPeriod >= EmaSlowPeriod)
                Print("WARNING: EmaMidPeriod >= EmaSlowPeriod!");
            if (EnableAdxFilter && AdxMaxRange > AdxMinTrend)
                Print($"WARNING: AdxMaxRange ({AdxMaxRange}) > AdxMinTrend ({AdxMinTrend})!");

            newsTimes = BuildNewsTimes();

            Print("=== Multi-Indicator Scalper v4.8 ===");
            Print($"Balance: {Account.Balance:F2} {Account.Asset.Name}");
            Print($"Session UTC: {StartHour:D2}:{StartMinute:D2} - {EndHour:D2}:{EndMinute:D2}");
            Print($"Score Min: {MinScoreToTrade}/{maxScore}");
            Print($"SL: {StopLossMultiplier}x ATR | TP3: {TakeProfit3Multiplier}x ATR");
            Print($"Momentum: {(EnableMomentumFilter ? $"ON ATR:{MinAtrMomentumRatio:F2} Swing:{MaxBarsSinceSwing} Nbrs:{SwingNeighbors}" : "OFF")}");

            emaFast    = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaFastPeriod);
            emaMid     = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaMidPeriod);
            emaSlow    = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaSlowPeriod);
            rsi        = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            macd       = Indicators.MacdHistogram(Bars.ClosePrices, MacdFast, MacdSlow, MacdSignal);
            atr        = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            supertrend = Indicators.Supertrend(SupertrendPeriod, SupertrendMultiplier);
            adx        = Indicators.DirectionalMovementSystem(AdxPeriod);

            htfBars   = MarketData.GetBars(HtfTimeFrame);
            htfEma50  = Indicators.ExponentialMovingAverage(htfBars.ClosePrices, 50);
            htfEma200 = Indicators.ExponentialMovingAverage(htfBars.ClosePrices, 200);

            htfStBand = new double[HtfStHistory];
            htfStBull = new bool[HtfStHistory];
            RecalculateHtfSupertrendFull();

            Positions.Closed += OnPositionClosed;

            peakBalance               = Account.Balance;
            lastCandlePattern         = "None";
            swapThresholdCheckedToday = false;
            hardCloseExecutedToday    = false;

            ResetDailyTracking();
            Print("Bot v4.8 ready.");
        }

        protected override void OnBar()
        {
            // [FIX-9] Zentraler Warm-up-Guard + explizite NaN-Checks
            int warmup = Math.Max(EmaSlowPeriod,
                         Math.Max(AtrPeriod + 5,
                         Math.Max(RsiPeriod + 5,
                         Math.Max(MacdSlow + MacdSignal + 5,
                         Math.Max(AdxPeriod + 5,
                                  SupertrendPeriod + 5)))));
            if (Bars.Count < warmup) return;

            if (double.IsNaN(atr.Result.Last(1)) || atr.Result.Last(1) <= 0) return;
            if (double.IsNaN(adx.ADX.Last(1)))        return;
            if (double.IsNaN(rsi.Result.Last(1)))      return;
            if (double.IsNaN(emaSlow.Result.Last(1)))  return;

            HandleNewTradingDayIfNeeded();

            if (Account.Balance > highestBalanceToday) highestBalanceToday = Account.Balance;
            if (Account.Balance > peakBalance)         peakBalance         = Account.Balance;

            double dd    = peakBalance - Account.Balance;
            double ddPct = peakBalance > 0 ? (dd / peakBalance) * 100.0 : 0;
            if (dd    > maxDrawdown)        maxDrawdown        = dd;
            if (ddPct > maxDrawdownPercent) maxDrawdownPercent = ddPct;

            if (EnableSwapProtection) RunSwapProtection();

            bool isNewsNow = EnableNewsFilter && IsNewsTime();
            if (isNewsNow && ClosePositionsOnNews)
            {
                var openPos = Positions.FindAll(BotLabel, SymbolName);
                if (openPos.Length > 0) CloseAllPositionsWithReason(openPos, "News Filter");
            }

            ManageOpenPositionsFull();

            if (!ShouldTrade(isNewsNow)) return;
            CheckForEntrySignal();
        }

        protected override void OnTick()
        {
            if (EnableSwapProtection) RunSwapProtection();

            if ((Server.Time - _lastTickManage).TotalSeconds < 1.0) return;
            _lastTickManage = Server.Time;

            ManageOpenPositionsTickOnly();
        }

        protected override void OnStop()
        {
            PrintDailySummary();
            PrintLifetimeStats();
            Print($"Bot v4.8 stopped. Final Balance: {Account.Balance:F2}");
        }

        protected override void OnError(Error error)
        {
            Print($"OnError: {error.Code}");
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

        private bool IsNewsTime()
        {
            if (!EnableNewsFilter || newsTimes.Count == 0) return false;
            var now = Server.Time;
            foreach (var news in newsTimes)
            {
                var newsTime   = new DateTime(now.Year, now.Month, now.Day, news.Hour, news.Minute, 0);
                var blockStart = newsTime.AddMinutes(-NewsBlockBefore);
                var blockEnd   = newsTime.AddMinutes(NewsBlockAfter);
                if (now >= blockStart && now <= blockEnd) return true;
            }
            return false;
        }

        // ==================== HTF SUPERTREND (Wilder RMA) ====================

        private void RecalculateHtfSupertrendFull()
        {
            int count = htfBars.Count;
            if (count < SupertrendPeriod + 2) return;

            int    startIdx = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);
            double prevUpper = 0, prevLower = 0;
            bool   prevBull  = true;
            double rmaAtr    = 0;

            double sumTr = 0;
            for (int i = startIdx - SupertrendPeriod + 1; i <= startIdx; i++)
            {
                if (i <= 0) continue;
                sumTr += CalcTrHtf(i);
            }
            rmaAtr = sumTr / SupertrendPeriod;

            for (int i = startIdx; i < count - 1; i++)
            {
                double tr = CalcTrHtf(i);
                rmaAtr = ((rmaAtr * (SupertrendPeriod - 1)) + tr) / SupertrendPeriod;

                CalcHtfBar(i, prevUpper, prevLower, prevBull, rmaAtr,
                           out double upper, out double lower, out bool bull);

                int idx = i - startIdx;
                if (idx >= 0 && idx < HtfStHistory)
                {
                    htfStBand[idx] = bull ? lower : upper;
                    htfStBull[idx] = bull;
                }
                prevUpper = upper; prevLower = lower; prevBull = bull;
            }
            _htfRmaAtr        = rmaAtr;
            _htfPrevUpperBand = prevUpper;
            _htfPrevLowerBand = prevLower;
            _htfPrevBull      = prevBull;
            _htfLastFullCalc  = count - 2;
            htfStCalcUpTo     = count - 2;
        }

        private void UpdateHtfSupertrendIncremental()
        {
            int count    = htfBars.Count;
            int newIndex = count - 2;
            if (newIndex <= _htfLastFullCalc) return;

            double tr  = CalcTrHtf(newIndex);
            _htfRmaAtr = ((_htfRmaAtr * (SupertrendPeriod - 1)) + tr) / SupertrendPeriod;

            CalcHtfBar(newIndex, _htfPrevUpperBand, _htfPrevLowerBand, _htfPrevBull, _htfRmaAtr,
                       out double upper, out double lower, out bool bull);

            int startIdx = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);
            int idx      = newIndex - startIdx;
            if (idx < 0 || idx >= HtfStHistory) { Print($"HTF ST idx {idx} out of range."); return; }

            htfStBand[idx]    = bull ? lower : upper;
            htfStBull[idx]    = bull;
            _htfPrevUpperBand = upper;
            _htfPrevLowerBand = lower;
            _htfPrevBull      = bull;
            _htfLastFullCalc  = newIndex;
            htfStCalcUpTo     = newIndex;
        }

        private double CalcTrHtf(int i)
        {
            if (i <= 0) return htfBars.HighPrices[i] - htfBars.LowPrices[i];
            return Math.Max(htfBars.HighPrices[i] - htfBars.LowPrices[i],
                   Math.Max(Math.Abs(htfBars.HighPrices[i] - htfBars.ClosePrices[i - 1]),
                            Math.Abs(htfBars.LowPrices[i]  - htfBars.ClosePrices[i - 1])));
        }

        private void CalcHtfBar(int i, double prevUpper, double prevLower, bool prevBull, double atrVal,
                                 out double upperBand, out double lowerBand, out bool bull)
        {
            double hl2 = (htfBars.HighPrices[i] + htfBars.LowPrices[i]) / 2.0;
            upperBand  = hl2 + SupertrendMultiplier * atrVal;
            lowerBand  = hl2 - SupertrendMultiplier * atrVal;

            if (prevUpper > 0 || prevLower > 0)
            {
                double prevClose = htfBars.ClosePrices[i - 1];
                lowerBand = (lowerBand > prevLower || prevClose < prevLower) ? lowerBand : prevLower;
                upperBand = (upperBand < prevUpper || prevClose > prevUpper) ? upperBand : prevUpper;
            }

            double close = htfBars.ClosePrices[i];
            if      (prevUpper == 0 && prevLower == 0) bull = close > hl2;
            else if (prevBull)                         bull = close >= lowerBand;
            else                                       bull = close >  upperBand;
        }

        private bool GetHtfSupertrendBullish()
        {
            int count = htfBars.Count;
            if (count - 2 != htfStCalcUpTo) UpdateHtfSupertrendIncremental();
            int startIdx = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);
            int idx      = (count - 2) - startIdx;
            if (idx < 0 || idx >= HtfStHistory) return false;
            return htfStBull[idx];
        }

        // ==================== MOMENTUM FILTER ====================

        private bool IsMomentumActive(TradeType type, out string rejectReason)
        {
            rejectReason = string.Empty;
            if (!EnableMomentumFilter) return true;

            // ATR-Momentum Check
            int safeAvgBars = Math.Min(AtrAvgLookback, Bars.Count - 2);
            if (safeAvgBars >= 1)
            {
                double atrSum = 0;
                for (int i = 1; i <= safeAvgBars; i++)
                    atrSum += atr.Result.Last(i);
                double avgAtr     = atrSum / safeAvgBars;
                double currentAtr = atr.Result.Last(1);
                double threshold  = avgAtr * MinAtrMomentumRatio;
                if (currentAtr < threshold)
                {
                    rejectReason = $"ATR {currentAtr:F5} < {threshold:F5} (Ratio:{MinAtrMomentumRatio:F2})";
                    return false;
                }
            }

            int minStartI   = SwingNeighbors + 1;
            int searchLimit = Math.Min(MaxBarsSinceSwing * 4, Bars.Count - SwingNeighbors - 2);

            if (type == TradeType.Sell)
            {
                int localSwingBar = -1;
                for (int i = minStartI; i <= searchLimit; i++)
                {
                    bool isLocalHigh = true;
                    for (int k = 1; k <= SwingNeighbors; k++)
                    {
                        if (Bars.HighPrices[i] <= Bars.HighPrices[i - k] ||
                            Bars.HighPrices[i] <= Bars.HighPrices[i + k])
                        { isLocalHigh = false; break; }
                    }
                    if (isLocalHigh) { localSwingBar = i; break; }
                }
                if (localSwingBar < 0 || localSwingBar > MaxBarsSinceSwing)
                {
                    rejectReason = $"Kein lok. Swing High <= {MaxBarsSinceSwing} Bars (Bar {localSwingBar})";
                    return false;
                }
            }
            else
            {
                int localSwingBar = -1;
                for (int i = minStartI; i <= searchLimit; i++)
                {
                    bool isLocalLow = true;
                    for (int k = 1; k <= SwingNeighbors; k++)
                    {
                        if (Bars.LowPrices[i] >= Bars.LowPrices[i - k] ||
                            Bars.LowPrices[i] >= Bars.LowPrices[i + k])
                        { isLocalLow = false; break; }
                    }
                    if (isLocalLow) { localSwingBar = i; break; }
                }
                if (localSwingBar < 0 || localSwingBar > MaxBarsSinceSwing)
                {
                    rejectReason = $"Kein lok. Swing Low <= {MaxBarsSinceSwing} Bars (Bar {localSwingBar})";
                    return false;
                }
            }

            if (RequireBarConfirmation)
            {
                int safeConfBars = Math.Min(ConfirmationBars, Bars.Count - 2);
                for (int i = 1; i <= safeConfBars; i++)
                {
                    bool confirms = type == TradeType.Buy
                        ? Bars.ClosePrices.Last(i) > Bars.OpenPrices.Last(i)
                        : Bars.ClosePrices.Last(i) < Bars.OpenPrices.Last(i);
                    if (!confirms)
                    {
                        rejectReason = $"Keine Bar-Bestaetigung (Bar {i}, {type})";
                        return false;
                    }
                }
            }

            return true;
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

            string pat = positionPatterns.ContainsKey(pos.Id) ? positionPatterns[pos.Id] : "None";
            int    sc  = positionScores.ContainsKey(pos.Id)   ? positionScores[pos.Id]   : 0;
            positionPatterns.Remove(pos.Id);
            positionScores.Remove(pos.Id);

            if (!patternWins.ContainsKey(pat))
            { patternWins[pat] = 0; patternLosses[pat] = 0; patternPnl[pat] = 0.0; }
            if (isWin) patternWins[pat]++; else patternLosses[pat]++;
            patternPnl[pat] += pnl;

            if (!scoreWins.ContainsKey(sc))   scoreWins[sc]   = 0;
            if (!scoreLosses.ContainsKey(sc))  scoreLosses[sc] = 0;
            if (isWin) scoreWins[sc]++; else scoreLosses[sc]++;

            double wr = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0;
            double pf = totalLossAmount > 0 ? totalWinAmount / totalLossAmount : 0;
            Print($"Trade Closed [{(isWin ? "WIN" : "LOSS")}] P/L:{pnl:F2} Pat:{pat} Score:{sc} WR:{wr:F1}% PF:{pf:F2} Reason:{args.Reason}");
        }

        private void PrintLifetimeStats()
        {
            if (totalTrades == 0) return;
            double wr         = (double)totalWins / totalTrades * 100.0;
            double pf         = totalLossAmount > 0 ? totalWinAmount / totalLossAmount : 0;
            double avgWin     = totalWins   > 0 ? totalWinAmount  / totalWins   : 0;
            double avgLoss    = totalLosses > 0 ? totalLossAmount / totalLosses : 0;
            double expectancy = (wr / 100.0 * avgWin) - ((1.0 - wr / 100.0) * avgLoss);
            Print("=== LIFETIME STATISTICS ===");
            Print($"Trades:{totalTrades} W:{totalWins} L:{totalLosses} WR:{wr:F1}% PF:{pf:F2} Expect:{expectancy:F2}");
            Print($"AvgWin:{avgWin:F2} AvgLoss:{avgLoss:F2} MaxDD:{maxDrawdown:F2} ({maxDrawdownPercent:F2}%)");
            Print("=== PATTERN PERFORMANCE ===");
            foreach (var kvp in patternPnl)
            {
                int    w   = patternWins.ContainsKey(kvp.Key)   ? patternWins[kvp.Key]   : 0;
                int    l   = patternLosses.ContainsKey(kvp.Key) ? patternLosses[kvp.Key] : 0;
                int    tot = w + l;
                double pwr = tot > 0 ? (double)w / tot * 100.0 : 0;
                Print($"  {kvp.Key}: W{w} L{l} WR{pwr:F0}% PnL{kvp.Value:F2}");
            }
            Print("=== SCORE PERFORMANCE ===");
            int maxSc = GetMaxPossibleScore();
            for (int s = MinScoreToTrade; s <= maxSc; s++)
            {
                int w   = scoreWins.ContainsKey(s)   ? scoreWins[s]   : 0;
                int l   = scoreLosses.ContainsKey(s) ? scoreLosses[s] : 0;
                int tot = w + l;
                if (tot == 0) continue;
                double swr = (double)w / tot * 100.0;
                Print($"  Score {s}/{maxSc}: W{w} L{l} WR{swr:F0}%");
            }
        }

        // ==================== DAY / SESSION ====================

        private void HandleNewTradingDayIfNeeded()
        {
            if (Server.Time.Date == lastTradeDate) return;
            PrintDailySummary();
            ResetDailyTracking();
            Print($"NEW TRADING DAY: {Server.Time.Date:yyyy-MM-dd}");
        }

        private void ResetDailyTracking()
        {
            tradesOpenedToday         = 0;
            dailyStartBalance         = Account.Balance;
            highestBalanceToday       = Account.Balance;
            lastTradeDate             = Server.Time.Date;
            swapThresholdCheckedToday = false;
            hardCloseExecutedToday    = false;

            bool hasOpenPosition = Positions.FindAll(BotLabel, SymbolName).Length > 0;
            if (!hasOpenPosition)
            {
                managedPositionId = null;
                tp1Hit = false; tp2Hit = false;
                trailingActive = false; breakEvenSet = false;
                _tp1Price = 0; _tp2Price = 0; _slDistance = 0;
            }
            lastCandlePattern = "None";
        }

        private bool IsInTradingSession()
        {
            var now        = Server.Time;
            int nowMinutes = now.Hour * 60 + now.Minute;
            int startMins  = StartHour  * 60 + StartMinute;
            int endMins    = EndHour    * 60 + EndMinute;
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

        // [FIX-D] Profitable Positionen werden nicht mehr wegen Swap geschlossen
        private void RunSwapThresholdCheck(Position[] positions)
        {
            foreach (var pos in positions)
            {
                double pnl  = pos.NetProfit;
                double swap = EstimateNextSwap(pos);

                // Verlustpositionen immer schliessen (Swap verschlimmert es)
                if (pnl < 0)
                {
                    ClosePositionWithReason(pos, $"Swap: Losing ({pnl:F2})");
                    continue;
                }

                // Gewinnposition: nur schliessen wenn Swap-Anteil ueber Schwellwert
                if (pnl > 0 && Math.Abs(swap) > 0)
                {
                    double ratio = (Math.Abs(swap) / pnl) * 100.0;
                    if (ratio >= SwapThresholdPercent)
                        ClosePositionWithReason(pos, $"Swap {ratio:F1}% of profit (threshold: {SwapThresholdPercent}%)");
                }
            }
        }

        private double EstimateNextSwap(Position pos)
        {
            double lots = Symbol.VolumeInUnitsToQuantity(pos.VolumeInUnits);
            double rate = pos.TradeType == TradeType.Buy ? Symbol.SwapLong : Symbol.SwapShort;
            double mult = Server.Time.DayOfWeek == DayOfWeek.Wednesday ? 3.0 : 1.0;
            return rate * lots * mult;
        }

        private void CloseAllPositionsWithReason(Position[] positions, string reason)
        { foreach (var p in positions) ClosePositionWithReason(p, reason); }

        private void CloseLosingPositions(Position[] positions, string reason)
        { foreach (var p in positions) if (p.NetProfit < 0) ClosePositionWithReason(p, reason); }

        private void ClosePositionWithReason(Position pos, string reason)
        {
            double pnl = pos.NetProfit;
            var    res = ClosePosition(pos);
            if (res.IsSuccessful)
                Print($"Closed #{pos.Id} {pos.TradeType} P/L:{pnl:F2} Reason:{reason}");
            else
                Print($"Failed to close #{pos.Id}: {res.Error}");
        }

        // ==================== TRADE GATING ====================

        private bool ShouldTrade(bool isNewsNow)
        {
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0) return false;
            if (tradesOpenedToday >= MaxDailyTrades) return false;

            double pnl   = Account.Balance - dailyStartBalance;
            double limit = dailyStartBalance * (MaxDailyLossPercent / 100.0);
            if (pnl < -limit) { Print("Daily loss limit reached."); return false; }

            if (!IsInTradingSession()) return false;

            double spread = GetSpreadPips();
            if (spread > MaxSpreadPips) { Print($"Spread too high: {spread:F2}"); return false; }

            if (EnableSwapProtection && IsSwapProtectionImminent()) return false;
            if (isNewsNow) { Print("News Block active."); return false; }

            if (EnableAdxFilter && adx.ADX.Last(1) < AdxMaxRange)
            {
                Print($"ADX {adx.ADX.Last(1):F1} < {AdxMaxRange:F1}: Range market, skip.");
                return false;
            }
            return true;
        }

        private bool IsSwapProtectionImminent()
        {
            var  now    = Server.Time;
            int  buffer = 30;
            bool isFri  = now.DayOfWeek == DayOfWeek.Friday;
            bool isWed  = now.DayOfWeek == DayOfWeek.Wednesday;

            if (EnableWeekendProtection && isFri &&
                IsWithinMinutesBefore(now, WeekendCloseHour, WeekendCloseMinute, buffer))
            { Print("No entry: Weekend Close imminent."); return true; }

            if (EnableTripleSwapGuard && isWed &&
                IsWithinMinutesBefore(now, TripleSwapCloseHour, TripleSwapCloseMinute, buffer))
            { Print("No entry: Triple Swap imminent."); return true; }

            if (IsWithinMinutesBefore(now, HardCloseHour, HardCloseMinute, buffer))
            { Print("No entry: Hard Close imminent."); return true; }

            return false;
        }

        // ==================== SCORING ====================

        private double CalculateRiskMultiplier(int score, int minScore, int maxScore)
        {
            if (!EnableScoreSizing) return 1.0;
            if (maxScore <= minScore) return RiskMultiplierMin;
            double t = Math.Max(0.0, Math.Min(1.0, (double)(score - minScore) / (maxScore - minScore)));
            return RiskMultiplierMin + t * (RiskMultiplierMax - RiskMultiplierMin);
        }

        private double ApplyDynamicScore(int rawScore)
        {
            if (!EnableDynamicScore || !EnableAdxFilter) return rawScore;
            double factor = Math.Max(0.5, Math.Min(2.0, adx.ADX.Last(1) / AdxMinTrend));
            return rawScore * factor;
        }

        // ==================== CANDLESTICK PATTERNS ====================

        // [FIX-9] Alle Bar-Zugriffe auf .Last(i)
        private double CandleBody(int i)      => Math.Abs(Bars.ClosePrices.Last(i) - Bars.OpenPrices.Last(i));
        private double CandleRange(int i)     => Bars.HighPrices.Last(i) - Bars.LowPrices.Last(i);
        private double UpperWick(int i)       => Bars.HighPrices.Last(i) - Math.Max(Bars.OpenPrices.Last(i), Bars.ClosePrices.Last(i));
        private double LowerWick(int i)       => Math.Min(Bars.OpenPrices.Last(i), Bars.ClosePrices.Last(i)) - Bars.LowPrices.Last(i);
        private bool   IsBullishCandle(int i) => Bars.ClosePrices.Last(i) > Bars.OpenPrices.Last(i);
        private bool   IsBearishCandle(int i) => Bars.ClosePrices.Last(i) < Bars.OpenPrices.Last(i);

        private bool IsBullishEngulfing()
        {
            if (!EnableEngulfing || !IsBearishCandle(2) || !IsBullishCandle(1)) return false;
            double pb = CandleBody(2); if (pb <= 0) return false;
            return Bars.ClosePrices.Last(1) > Bars.OpenPrices.Last(2)
                && Bars.OpenPrices.Last(1)  < Bars.ClosePrices.Last(2)
                && CandleBody(1) >= pb * EngulfingBodyRatio;
        }

        private bool IsBearishEngulfing()
        {
            if (!EnableEngulfing || !IsBullishCandle(2) || !IsBearishCandle(1)) return false;
            double pb = CandleBody(2); if (pb <= 0) return false;
            return Bars.ClosePrices.Last(1) < Bars.OpenPrices.Last(2)
                && Bars.OpenPrices.Last(1)  > Bars.ClosePrices.Last(2)
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
            if (CandleRange(2) < InsideBarMinMotherSizeAtr * atr.Result.Last(2)) return false;
            return Bars.HighPrices.Last(1) < Bars.HighPrices.Last(2)
                && Bars.LowPrices.Last(1)  > Bars.LowPrices.Last(2)
                && IsBullishCandle(1);
        }

        private bool IsBearishInsideBar()
        {
            if (!EnableInsideBar) return false;
            if (CandleRange(2) < InsideBarMinMotherSizeAtr * atr.Result.Last(2)) return false;
            return Bars.HighPrices.Last(1) < Bars.HighPrices.Last(2)
                && Bars.LowPrices.Last(1)  > Bars.LowPrices.Last(2)
                && IsBearishCandle(1);
        }

        private bool IsMorningStar()
        {
            if (!EnableStar || CandleBody(1) <= 0 || CandleBody(3) <= 0) return false;
            double minSize = atr.Result.Last(2) * 0.5;
            return IsBearishCandle(3) && CandleBody(3) >= minSize
                && CandleBody(2) <= CandleBody(3) * 0.3
                && IsBullishCandle(1) && CandleBody(1) >= minSize
                && Bars.ClosePrices.Last(1) > (Bars.OpenPrices.Last(3) + Bars.ClosePrices.Last(3)) / 2.0;
        }

        private bool IsEveningStar()
        {
            if (!EnableStar || CandleBody(1) <= 0 || CandleBody(3) <= 0) return false;
            double minSize = atr.Result.Last(2) * 0.5;
            return IsBullishCandle(3) && CandleBody(3) >= minSize
                && CandleBody(2) <= CandleBody(3) * 0.3
                && IsBearishCandle(1) && CandleBody(1) >= minSize
                && Bars.ClosePrices.Last(1) < (Bars.OpenPrices.Last(3) + Bars.ClosePrices.Last(3)) / 2.0;
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

        private void CalculateSignalScores(out double longScore, out double shortScore,
                                           out string longPat,   out string shortPat)
        {
            int rawLong = 0, rawShort = 0;
            longPat = "None"; shortPat = "None";
            double price = Bars.ClosePrices.Last(1);

            if (EnableSupertrendFilter)
            {
                if (!double.IsNaN(supertrend.UpTrend.Last(1)))   rawLong  += ScoreSupertrend;
                if (!double.IsNaN(supertrend.DownTrend.Last(1))) rawShort += ScoreSupertrend;
            }

            if (EnableEmaTrendFilter)
            {
                double eSlow = emaSlow.Result.Last(1);
                if (price > eSlow) rawLong  += ScoreEmaTrend;
                if (price < eSlow) rawShort += ScoreEmaTrend;
            }

            if (EnableEmaMomentumFilter)
            {
                double eFast = emaFast.Result.Last(1);
                double eMid  = emaMid.Result.Last(1);
                if (eFast > eMid) rawLong  += ScoreEmaMomentum;
                if (eFast < eMid) rawShort += ScoreEmaMomentum;
            }

            if (EnableRsiFilter)
            {
                double r = rsi.Result.Last(1);
                if (r > RsiLongMin  && r < RsiOverbought) rawLong  += ScoreRsi;
                if (r > RsiOversold && r < RsiShortMax)   rawShort += ScoreRsi;
            }

            if (EnableMacdFilter)
            {
                double h  = macd.Histogram.Last(1);
                double hp = macd.Histogram.Last(2);
                if (MacdMode == 0)
                {
                    if (h > 0 && hp <= 0) rawLong  += ScoreMacd;
                    if (h < 0 && hp >= 0) rawShort += ScoreMacd;
                }
                else
                {
                    if (h > 0) rawLong  += ScoreMacd;
                    if (h < 0) rawShort += ScoreMacd;
                }
            }

            if (EnableMtfFilter)
            {
                if (IsMtfBullish()) rawLong  += ScoreMtfConfirm;
                if (IsMtfBearish()) rawShort += ScoreMtfConfirm;
            }

            if (EnableCandlePatterns)
            {
                if (HasBullishCandlePattern(out longPat))  rawLong  += ScoreCandlePattern;
                if (HasBearishCandlePattern(out shortPat)) rawShort += ScoreCandlePattern;
            }

            if (EnableAdxFilter && adx.ADX.Last(1) >= AdxMinTrend)
            {
                if (adx.DIPlus.Last(1)  > adx.DIMinus.Last(1)) rawLong  += ScoreAdxTrend;
                if (adx.DIMinus.Last(1) > adx.DIPlus.Last(1))  rawShort += ScoreAdxTrend;
            }

            longScore  = ApplyDynamicScore(rawLong);
            shortScore = ApplyDynamicScore(rawShort);
        }

        private bool IsMtfBullish()
            => GetHtfSupertrendBullish() && htfEma50.Result.Last(1) > htfEma200.Result.Last(1);

        private bool IsMtfBearish()
            => !GetHtfSupertrendBullish() && htfEma50.Result.Last(1) < htfEma200.Result.Last(1);

        private int GetMaxPossibleScore()
            => ScoreSupertrend + ScoreEmaTrend + ScoreEmaMomentum
             + ScoreRsi + ScoreMacd + ScoreMtfConfirm + ScoreCandlePattern + ScoreAdxTrend;

        // ==================== ENTRY LOGIC ====================

        private void CheckForEntrySignal()
        {
            CalculateSignalScores(out double longScore, out double shortScore,
                                  out string longPat,   out string shortPat);
            lastLongScore  = (int)longScore;
            lastShortScore = (int)shortScore;

            int    maxScore = GetMaxPossibleScore();
            double atrVal   = atr.Result.Last(1);

            if (longScore >= MinScoreToTrade)
            {
                // [FIX-A] Harter MTF-Block: kein Long gegen HTF Bear
                if (EnableMtfFilter && IsMtfBearish())
                { Print("Long REJECTED: HTFBear - MTF Hard Block"); return; }

                if (!IsMomentumActive(TradeType.Buy, out string reason))
                { Print($"Long Momentum REJECTED: {reason}"); return; }
                lastCandlePattern = longPat;
                int    rawScore = (int)Math.Round(longScore);
                double mult     = CalculateRiskMultiplier(rawScore, MinScoreToTrade, maxScore);
                ExecuteTrade(TradeType.Buy, atrVal, rawScore, longPat, mult);
                return;
            }

            if (shortScore >= MinScoreToTrade)
            {
                // [FIX-B] Harter MTF-Block: kein Short gegen HTF Bull
                if (EnableMtfFilter && IsMtfBullish())
                { Print("Short REJECTED: HTFBull - MTF Hard Block"); return; }

                if (!IsMomentumActive(TradeType.Sell, out string reason))
                { Print($"Short Momentum REJECTED: {reason}"); return; }
                lastCandlePattern = shortPat;
                int    rawScore = (int)Math.Round(shortScore);
                double mult     = CalculateRiskMultiplier(rawScore, MinScoreToTrade, maxScore);
                ExecuteTrade(TradeType.Sell, atrVal, rawScore, shortPat, mult);
            }
        }

        // ==================== EXECUTION ====================

        private void ExecuteTrade(TradeType tradeType, double atrVal,
                                  int signalScore, string candlePattern, double riskMultiplier)
        {
            if (double.IsNaN(atrVal) || atrVal <= 0)
            { Print($"ExecuteTrade ABORTED: ATR invalid ({atrVal})"); return; }

            double? ml = Account.MarginLevel;
            if (ml.HasValue && ml.Value < 200)
            { Print($"Margin Level too low: {ml.Value:F0}%"); return; }

            double effectiveRisk = RiskPercent * riskMultiplier;
            double riskAmount    = Account.Balance * (effectiveRisk / 100.0);

            double slDist  = StopLossMultiplier    * atrVal;
            double tp1Dist = TakeProfit1Multiplier * atrVal;
            double tp2Dist = TakeProfit2Multiplier * atrVal;
            double tp3Dist = TakeProfit3Multiplier * atrVal;

            double currentPrice = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            double slPrice, tp3Price, tp1Price, tp2Price;

            if (tradeType == TradeType.Buy)
            {
                slPrice  = currentPrice - slDist;
                tp1Price = currentPrice + tp1Dist;
                tp2Price = currentPrice + tp2Dist;
                tp3Price = currentPrice + tp3Dist;
            }
            else
            {
                slPrice  = currentPrice + slDist;
                tp1Price = currentPrice - tp1Dist;
                tp2Price = currentPrice - tp2Dist;
                tp3Price = currentPrice - tp3Dist;
            }

            slPrice  = Math.Round(slPrice,  Symbol.Digits);
            tp3Price = Math.Round(tp3Price, Symbol.Digits);
            tp1Price = Math.Round(tp1Price, Symbol.Digits);
            tp2Price = Math.Round(tp2Price, Symbol.Digits);

            double stopLossPips = slDist / Symbol.PipSize;

            // [FIX-7] Symbol.MinStopLossDistance ist double, kein double?
            double minDist = Symbol.MinStopLossDistance;
            if (stopLossPips < minDist)
            { Print($"SL {stopLossPips:F1}p < MinStopDist {minDist:F1}p. Trade aborted."); return; }

            if (stopLossPips <= 0) { Print("SL <= 0, aborted."); return; }
            if (tp3Dist      <= 0) { Print("TP3 dist <= 0, aborted."); return; }

            // [FIX-8] tradeType korrekt durchgereicht
            double vol = CalculateVolumeInUnitsFromRisk(riskAmount, stopLossPips, tradeType);
            if (vol < Symbol.VolumeInUnitsMin) { Print("Volume too small."); return; }

            double reqMargin = Symbol.GetEstimatedMargin(tradeType, vol);
            if (Account.Margin + reqMargin > 0)
            {
                double mlAfter = (Account.Equity / (Account.Margin + reqMargin)) * 100;
                if (mlAfter < 150) { Print($"ML after trade too low: {mlAfter:F0}%"); return; }
            }

            var result = ExecuteMarketOrder(tradeType, SymbolName, vol, BotLabel);
            if (!result.IsSuccessful) { Print($"Order failed: {result.Error}"); return; }

            var pos = result.Position;

            var modResult = ModifyPosition(pos, slPrice, tp3Price, ProtectionType.Absolute);
            if (!modResult.IsSuccessful)
                Print($"WARNING: SL/TP set FAILED: {modResult.Error} | SL:{slPrice:F5} TP:{tp3Price:F5}");
            else
                Print($"SL/TP OK: SL={slPrice:F5} ({stopLossPips:F1}p) TP3={tp3Price:F5} RR=1:{(tp3Dist/slDist):F2}");

            _tp1Price   = tp1Price;
            _tp2Price   = tp2Price;
            _slDistance = slDist;

            long posId = pos.Id;
            positionPatterns[posId] = candlePattern;
            positionScores[posId]   = signalScore;

            tradesOpenedToday++;
            managedPositionId = posId;
            entryPrice        = pos.EntryPrice;
            tp1Hit = false; tp2Hit = false;
            trailingActive = false; breakEvenSet = false;

            double lots    = Symbol.VolumeInUnitsToQuantity(vol);
            double estSwap = EstimateNextSwap(pos);

            Print($"TRADE #{tradesOpenedToday} {tradeType} Score:{signalScore}/{GetMaxPossibleScore()} Pat:{candlePattern}");
            Print($"  Entry:{pos.EntryPrice:F5} SL:{slPrice:F5} TP1:{tp1Price:F5} TP2:{tp2Price:F5} TP3:{tp3Price:F5}");
            Print($"  Risk:{riskAmount:F2} ({effectiveRisk:F2}%) Lots:{lots:F2} ATR:{atrVal:F5} EstSwap:{estSwap:F2}");
            Print($"  ADX:{adx.ADX.Last(1):F1} HTF:{(GetHtfSupertrendBullish() ? "Bull" : "Bear")} {Server.Time:HH:mm:ss}UTC");
        }

        // ==================== POSITION SIZING ====================

        private double CalculateVolumeInUnitsFromRisk(double riskAmount, double stopLossPips, TradeType tradeType)
        {
            double vol = riskAmount / (stopLossPips * Symbol.PipValue);
            double cap = (Account.Balance * 0.03) / (stopLossPips * Symbol.PipValue);
            vol = Math.Min(vol, cap);
            vol = Math.Min(vol, CalculateMaxVolumeFromMargin(MaxMarginUsagePercent, tradeType));

            if (double.IsNaN(vol) || double.IsInfinity(vol) || vol <= 0)
                return Symbol.VolumeInUnitsMin;

            vol = Symbol.NormalizeVolumeInUnits(vol, RoundingMode.Down);
            vol = Math.Max(vol, Symbol.VolumeInUnitsMin);
            vol = Math.Min(vol, Symbol.VolumeInUnitsMax);
            return vol;
        }

        private double CalculateMaxVolumeFromMargin(double maxPct, TradeType tradeType)
        {
            double allowed = Account.FreeMargin * (maxPct / 100.0);
            double minVol  = Symbol.VolumeInUnitsMin;
            double maxVol  = Symbol.VolumeInUnitsMax;
            double optimal = minVol;
            for (int i = 0; i < 20; i++)
            {
                double test   = Symbol.NormalizeVolumeInUnits((minVol + maxVol) / 2.0, RoundingMode.Down);
                double margin = Symbol.GetEstimatedMargin(tradeType, test);
                if (margin <= allowed) { optimal = test; minVol = test; }
                else maxVol = test;
                if (Math.Abs(maxVol - minVol) < Symbol.VolumeInUnitsStep) break;
            }
            return optimal;
        }

        // ==================== POSITION MANAGEMENT ====================

        private void ManageOpenPositionsFull()
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

        private void ManageOpenPositionsTickOnly()
        {
            if (!EnableTrailingStop || !trailingActive) return;
            var positions = Positions.FindAll(BotLabel, SymbolName);
            if (positions.Length == 0) return;
            ApplyTrailingStop(positions[0]);
        }

        private void CheckPartialClose(Position pos)
        {
            if (_tp1Price <= 0 || _tp2Price <= 0) return;

            if (!tp1Hit)
            {
                bool tp1Reached = pos.TradeType == TradeType.Buy
                    ? Symbol.Bid >= _tp1Price
                    : Symbol.Ask <= _tp1Price;

                if (tp1Reached)
                {
                    TryPartialClose(pos, Tp1ClosePercent, "TP1");
                    tp1Hit = true;
                    if (EnableTrailingStop)
                    { trailingActive = true; Print("Trailing Stop activated after TP1."); }
                }
            }

            if (!tp2Hit)
            {
                bool tp2Reached = pos.TradeType == TradeType.Buy
                    ? Symbol.Bid >= _tp2Price
                    : Symbol.Ask <= _tp2Price;

                if (tp2Reached)
                {
                    TryPartialClose(pos, Tp2ClosePercent, "TP2");
                    tp2Hit = true;
                }
            }
        }

        // [FIX-5] Break-Even entkoppelt von tp1Hit - prueft direkt den Preis
        private void CheckBreakEven(Position pos)
        {
            if (breakEvenSet) return;

            bool tp1PriceReached = _tp1Price > 0 && (
                pos.TradeType == TradeType.Buy
                    ? Symbol.Bid >= _tp1Price
                    : Symbol.Ask <= _tp1Price
            );

            if (!tp1PriceReached) return;

            double atrBuffer  = BreakEvenBufferAtr * atr.Result.Last(1);
            double minBuffer  = Symbol.Spread * 2.0;
            double buffer     = Math.Max(atrBuffer, minBuffer);
            double? currentSL = pos.StopLoss;

            if (pos.TradeType == TradeType.Buy)
            {
                double beSL = Math.Round(entryPrice + buffer, Symbol.Digits);
                if (currentSL == null || beSL > currentSL.Value)
                {
                    var r = ModifyPosition(pos, beSL, pos.TakeProfit, ProtectionType.Absolute);
                    if (r.IsSuccessful) { breakEvenSet = true; Print($"Break-Even set: SL->{beSL:F5}"); }
                    else Print($"Break-Even FAILED: {r.Error}");
                }
            }
            else
            {
                double beSL = Math.Round(entryPrice - buffer, Symbol.Digits);
                if (currentSL == null || beSL < currentSL.Value)
                {
                    var r = ModifyPosition(pos, beSL, pos.TakeProfit, ProtectionType.Absolute);
                    if (r.IsSuccessful) { breakEvenSet = true; Print($"Break-Even set: SL->{beSL:F5}"); }
                    else Print($"Break-Even FAILED: {r.Error}");
                }
            }
        }

        private bool TryPartialClose(Position pos, int closePct, string tag)
        {
            if (closePct <= 0) return false;
            if (pos.VolumeInUnits < Symbol.VolumeInUnitsMin * 2) return false;
            double closeUnits = Symbol.NormalizeVolumeInUnits(
                pos.VolumeInUnits * (closePct / 100.0), RoundingMode.Down);
            if (closeUnits < Symbol.VolumeInUnitsMin) closeUnits = Symbol.VolumeInUnitsMin;
            if (closeUnits >= pos.VolumeInUnits) return false;
            var res = ClosePosition(pos, closeUnits);
            if (!res.IsSuccessful) { Print($"{tag} partial close FAILED: {res.Error}"); return false; }
            Print($"{tag}: closed {closePct}% at {pos.Pips:F1} pips.");
            return true;
        }

        private void ApplyTrailingStop(Position pos)
        {
            double atrVal     = atr.Result.Last(1);
            double profitPips = Math.Abs(pos.Pips);
            if (profitPips < (TrailingStartMultiplier * atrVal) / Symbol.PipSize) return;

            double dist = TrailingDistanceMultiplier * atrVal;
            double? sl  = pos.StopLoss;

            if (pos.TradeType == TradeType.Buy)
            {
                double newSL = Math.Round(Symbol.Bid - dist, Symbol.Digits);
                if (sl == null || newSL > sl.Value)
                    ModifyPosition(pos, newSL, pos.TakeProfit, ProtectionType.Absolute);
            }
            else
            {
                double newSL = Math.Round(Symbol.Ask + dist, Symbol.Digits);
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
            Print($"=== DAILY SUMMARY {lastTradeDate:yyyy-MM-dd} ===");
            Print($"Trades:{tradesOpenedToday} PnL:{pnl:F2} ({pnlPct:F2}%) WR:{wr:F1}% PF:{pf:F2}");
            Print($"MaxDD:{maxDrawdown:F2} ({maxDrawdownPercent:F2}%) Scores L:{lastLongScore} S:{lastShortScore}");
        }
    }
}