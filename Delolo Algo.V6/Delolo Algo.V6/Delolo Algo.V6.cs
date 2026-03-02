using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    // ============================================================
    //  Delolo Algo V6 – Hypothese A: Pullback-in-Trend
    //
    //  Logik-Überblick:
    //  GATE   : H4-Supertrend + H4-EMA50>200 + ADX>25 + Session + Spread + News
    //  SCORE  : LTF Supertrend | RSI-Pullback-Erholung | EMA-Momentum | ATR-Momentum
    //  TRIGGER: Engulfing ODER Pin Bar (Pflicht)
    //  EXITS  : Partial TP1/TP2/TP3, Break-Even, Trailing Stop
    // ============================================================
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class DeloloAlgoV6 : Robot
    {
        // ==================== PARAMETERS ====================

        [Parameter("== RISK MANAGEMENT ==")]
        public string RiskLabel { get; set; }

        [Parameter("Risk Per Trade (%)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPercent { get; set; }

        [Parameter("Max Trades Per Day", DefaultValue = 5, MinValue = 1, MaxValue = 20)]
        public int MaxDailyTrades { get; set; }

        [Parameter("Max Trades Per Direction Per Day", DefaultValue = 1, MinValue = 1, MaxValue = 5)]
        public int MaxTradesPerDirectionPerDay { get; set; }

        [Parameter("Max Daily Loss (%)", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 0.3, MinValue = 0.1, MaxValue = 5.0)]
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

        [Parameter("Swap Protection Entry Buffer (min)", DefaultValue = 30, MinValue = 5, MaxValue = 120)]
        public int SwapProtectionBuffer { get; set; }

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

        // ==================== GATE: ADX ====================

        [Parameter("== GATE: ADX ==")]
        public string AdxLabel { get; set; }

        [Parameter("ADX Period", DefaultValue = 14, MinValue = 2, MaxValue = 50)]
        public int AdxPeriod { get; set; }

        [Parameter("ADX Min Trend (Gate)", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 60.0)]
        public double AdxMinTrend { get; set; }

        [Parameter("ADX Max Trend (Overheat Block)", DefaultValue = 50.0, MinValue = 25.0, MaxValue = 80.0)]
        public double AdxMaxTrend { get; set; }

        // ==================== GATE: HTF ====================

        [Parameter("== GATE: HTF (H4) ==")]
        public string HtfLabel { get; set; }

        [Parameter("HTF Timeframe", DefaultValue = "Hour4")]
        public TimeFrame HtfTimeFrame { get; set; }

        [Parameter("Supertrend Period", DefaultValue = 10, MinValue = 2, MaxValue = 50)]
        public int SupertrendPeriod { get; set; }

        [Parameter("Supertrend Multiplier", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 10.0)]
        public double SupertrendMultiplier { get; set; }

        [Parameter("Enable W1 Supertrend Filter", DefaultValue = true)]
        public bool EnableW1Filter { get; set; }

        [Parameter("W1 Supertrend Period", DefaultValue = 10, MinValue = 2, MaxValue = 50)]
        public int W1StPeriod { get; set; }

        [Parameter("W1 Supertrend Multiplier", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double W1StMultiplier { get; set; }

        // ==================== SCORE: INDIKATOREN ====================

        [Parameter("== SCORE: INDICATORS ==")]
        public string ScoreLabel { get; set; }

        [Parameter("Min Score to Trade (max=4)", DefaultValue = 3, MinValue = 1, MaxValue = 4)]
        public int MinScoreToTrade { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int AtrPeriod { get; set; }

        [Parameter("EMA Fast Period", DefaultValue = 21, MinValue = 3, MaxValue = 100)]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Mid Period", DefaultValue = 50, MinValue = 5, MaxValue = 200)]
        public int EmaMidPeriod { get; set; }

        [Parameter("EMA Slow Period (Gate)", DefaultValue = 200, MinValue = 50, MaxValue = 500)]
        public int EmaSlowPeriod { get; set; }

        [Parameter("LTF Supertrend Period", DefaultValue = 10, MinValue = 2, MaxValue = 50)]
        public int LtfStPeriod { get; set; }

        [Parameter("LTF Supertrend Multiplier", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0)]
        public double LtfStMultiplier { get; set; }

        // RSI – Pullback-Erholung
        [Parameter("RSI Period", DefaultValue = 14, MinValue = 3, MaxValue = 50)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Pullback Level Long (war darunter)", DefaultValue = 42, MinValue = 20, MaxValue = 55)]
        public double RsiPullbackLong { get; set; }

        [Parameter("RSI Recovery Level Long (jetzt darüber)", DefaultValue = 48, MinValue = 30, MaxValue = 60)]
        public double RsiRecoveryLong { get; set; }

        [Parameter("RSI Pullback Level Short (war darüber)", DefaultValue = 58, MinValue = 45, MaxValue = 80)]
        public double RsiPullbackShort { get; set; }

        [Parameter("RSI Recovery Level Short (jetzt darunter)", DefaultValue = 52, MinValue = 40, MaxValue = 70)]
        public double RsiRecoveryShort { get; set; }

        [Parameter("RSI Lookback Bars (war überdehnt)", DefaultValue = 5, MinValue = 1, MaxValue = 20)]
        public int RsiLookback { get; set; }

        // ATR-Momentum
        [Parameter("ATR Momentum Ratio", DefaultValue = 1.0, MinValue = 0.3, MaxValue = 3.0)]
        public double AtrMomentumRatio { get; set; }

        [Parameter("ATR Avg Lookback Bars", DefaultValue = 10, MinValue = 3, MaxValue = 30)]
        public int AtrAvgLookback { get; set; }

        // ==================== TRIGGER: CANDLESTICK ====================

        [Parameter("== TRIGGER: CANDLE PATTERNS ==")]
        public string CandleLabel { get; set; }

        [Parameter("Enable Bullish Engulfing (Long)", DefaultValue = false)]
        public bool EnableBullishEngulfing { get; set; }

        [Parameter("Enable Bearish Engulfing (Short)", DefaultValue = true)]
        public bool EnableBearishEngulfing { get; set; }

        [Parameter("Bearish Engulfing Min Score", DefaultValue = 3, MinValue = 1, MaxValue = 4)]
        public int BearishEngulfingMinScore { get; set; }

        [Parameter("Enable Pin Bar (Hammer / Long)", DefaultValue = true)]
        public bool EnablePinBarLong { get; set; }

        [Parameter("Enable Shooting Star (Short)", DefaultValue = false)]
        public bool EnableShootingStar { get; set; }

        [Parameter("Engulfing Body Ratio", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 5.0)]
        public double EngulfingBodyRatio { get; set; }

        [Parameter("Pin Bar Wick Ratio", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 10.0)]
        public double PinBarWickRatio { get; set; }

        // ==================== TRADING HOURS ====================

        [Parameter("== TRADING HOURS (UTC) ==")]
        public string SessionLabel { get; set; }

        [Parameter("Session Start Hour", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartHour { get; set; }

        [Parameter("Session Start Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int StartMinute { get; set; }

        [Parameter("Session End Hour", DefaultValue = 17, MinValue = 0, MaxValue = 23)]
        public int EndHour { get; set; }

        [Parameter("Session End Minute", DefaultValue = 0, MinValue = 0, MaxValue = 59)]
        public int EndMinute { get; set; }

        [Parameter("Block Wednesday Entries", DefaultValue = true)]
        public bool BlockWednesdayEntries { get; set; }

        // ==================== TARGETS ====================

        [Parameter("== TARGETS ==")]
        public string TargetLabel { get; set; }

        [Parameter("Stop Loss (ATR)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 10.0)]
        public double StopLossMultiplier { get; set; }

        [Parameter("Take Profit 1 (ATR)", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 20.0)]
        public double TakeProfit1Multiplier { get; set; }

        [Parameter("Take Profit 2 (ATR)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 20.0)]
        public double TakeProfit2Multiplier { get; set; }

        [Parameter("Take Profit 3 (ATR)", DefaultValue = 2.5, MinValue = 0.5, MaxValue = 50.0)]
        public double TakeProfit3Multiplier { get; set; }

        [Parameter("TP1 Close %", DefaultValue = 30, MinValue = 0, MaxValue = 100)]
        public int Tp1ClosePercent { get; set; }

        [Parameter("TP2 Close %", DefaultValue = 40, MinValue = 0, MaxValue = 100)]
        public int Tp2ClosePercent { get; set; }

        // ==================== STRATEGY FEATURES ====================

        [Parameter("== STRATEGY FEATURES ==")]
        public string FeaturesLabel { get; set; }

        [Parameter("Enable Trailing Stop", DefaultValue = true)]
        public bool EnableTrailingStop { get; set; }

        [Parameter("Enable Partial TPs", DefaultValue = true)]
        public bool EnablePartialTPs { get; set; }

        [Parameter("Enable Break-Even", DefaultValue = true)]
        public bool EnableBreakEven { get; set; }

        [Parameter("Break-Even Trigger (ATR)", DefaultValue = 0.75, MinValue = 0.1, MaxValue = 5.0)]
        public double BreakEvenTriggerAtr { get; set; }

        [Parameter("Break-Even Buffer (ATR)", DefaultValue = 0.1, MinValue = 0.0, MaxValue = 1.0)]
        public double BreakEvenBufferAtr { get; set; }

        [Parameter("Trailing Start (ATR)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 20.0)]
        public double TrailingStartMultiplier { get; set; }

        [Parameter("Trailing Distance (ATR)", DefaultValue = 1.0, MinValue = 0.5, MaxValue = 20.0)]
        public double TrailingDistanceMultiplier { get; set; }

        // ==================== NEWS FILTER ====================

        [Parameter("== NEWS FILTER ==")]
        public string NewsLabel { get; set; }

        [Parameter("Enable News Filter", DefaultValue = true)]
        public bool EnableNewsFilter { get; set; }

        [Parameter("News Block Minutes Before", DefaultValue = 45, MinValue = 5, MaxValue = 120)]
        public int NewsBlockBefore { get; set; }

        [Parameter("News Block Minutes After", DefaultValue = 60, MinValue = 5, MaxValue = 120)]
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

        // ==================== INDICATOR INSTANCES ====================

        private ExponentialMovingAverage emaFast;
        private ExponentialMovingAverage emaMid;
        private ExponentialMovingAverage emaSlow;
        private RelativeStrengthIndex    rsi;
        private AverageTrueRange         atr;
        private Supertrend               ltfSupertrend;
        private DirectionalMovementSystem adx;

        // HTF-Daten
        private Bars                     htfBars;
        private ExponentialMovingAverage htfEma50;
        private ExponentialMovingAverage htfEma200;

        // HTF Supertrend – manuell mit Wilder RMA (cAlgo API bietet keine HTF-Indikatoren)
        private double[]  htfStBand;
        private bool[]    htfStBull;
        private int       htfStCalcUpTo     = -1;
        private const int HtfStHistory      = 500;
        private double    _htfPrevUpperBand = 0;
        private double    _htfPrevLowerBand = 0;
        private bool      _htfPrevBull      = true;
        private int       _htfLastFullCalc  = -1;
        private double    _htfRmaAtr        = 0;

        // W1 Supertrend – gleiche manuelle Implementierung für Weekly Timeframe
        private Bars      w1Bars;
        private double[]  w1StBand;
        private bool[]    w1StBull;
        private int       w1StCalcUpTo      = -1;
        private const int W1StHistory       = 500;
        private double    _w1PrevUpperBand  = 0;
        private double    _w1PrevLowerBand  = 0;
        private bool      _w1PrevBull       = true;
        private int       _w1LastFullCalc   = -1;
        private double    _w1RmaAtr         = 0;

        // ==================== STATE ====================

        private const string BotLabel = "DeloloV6";

        private int      tradesOpenedToday;
        private int      buyTradesToday;
        private int      sellTradesToday;
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

        private int    lastScore;
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
            if (EmaFastPeriod >= EmaMidPeriod)
                Print("WARNING: EmaFastPeriod >= EmaMidPeriod!");
            if (EmaMidPeriod >= EmaSlowPeriod)
                Print("WARNING: EmaMidPeriod >= EmaSlowPeriod!");
            if (TakeProfit1Multiplier < StopLossMultiplier)
                Print($"WARNING: TP1 ({TakeProfit1Multiplier}x) < SL ({StopLossMultiplier}x) – RR < 1:1 beim Erst-Close!");
            if (TakeProfit2Multiplier < StopLossMultiplier)
                Print($"WARNING: TP2 ({TakeProfit2Multiplier}x) < SL ({StopLossMultiplier}x) – RR < 1:1 beim Zweit-Close!");
            if (Tp1ClosePercent + Tp2ClosePercent > 100)
                Print($"WARNING: TP1% ({Tp1ClosePercent}) + TP2% ({Tp2ClosePercent}) > 100!");
            if (RsiRecoveryLong <= RsiPullbackLong)
                Print($"WARNING: RSI Recovery Long ({RsiRecoveryLong}) <= Pullback Long ({RsiPullbackLong})!");
            if (RsiRecoveryShort >= RsiPullbackShort)
                Print($"WARNING: RSI Recovery Short ({RsiRecoveryShort}) >= Pullback Short ({RsiPullbackShort})!");
            if (AdxMaxTrend <= AdxMinTrend)
                Print($"WARNING: AdxMaxTrend ({AdxMaxTrend}) <= AdxMinTrend ({AdxMinTrend})!");
            if (!EnableBullishEngulfing && !EnableBearishEngulfing && !EnablePinBarLong && !EnableShootingStar)
                Print("WARNING: Alle Trigger-Patterns deaktiviert – kein Trade möglich!");

            newsTimes = BuildNewsTimes();

            emaFast       = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaFastPeriod);
            emaMid        = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaMidPeriod);
            emaSlow       = Indicators.ExponentialMovingAverage(Bars.ClosePrices, EmaSlowPeriod);
            rsi           = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            atr           = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            ltfSupertrend = Indicators.Supertrend(LtfStPeriod, LtfStMultiplier);
            adx           = Indicators.DirectionalMovementSystem(AdxPeriod);

            htfBars   = MarketData.GetBars(HtfTimeFrame);
            if (EnableW1Filter)
            {
                w1Bars    = MarketData.GetBars(TimeFrame.Weekly);
                w1StBand  = new double[W1StHistory];
                w1StBull  = new bool[W1StHistory];
                RecalculateW1SupertrendFull();
            }
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

            Print("=== Delolo Algo V6 – Hypothese A [Fix: Patterns+W1+BE+DirLimit+ScoreEng] ===");
            Print($"Balance: {Account.Balance:F2} {Account.Asset.Name}");
            Print($"Session UTC: {StartHour:D2}:{StartMinute:D2} – {EndHour:D2}:{EndMinute:D2}");
            Print($"Gate: ADX {AdxMinTrend}–{AdxMaxTrend} | HTF: {HtfTimeFrame} | Spread < {MaxSpreadPips}p");
            Print($"Score Min: {MinScoreToTrade}/4 | SL: {StopLossMultiplier}x ATR | TP3: {TakeProfit3Multiplier}x ATR");
            Print("Ready.");
        }

        protected override void OnBar()
        {
            int warmup = Math.Max(EmaSlowPeriod,
                         Math.Max(AtrPeriod + AtrAvgLookback + 2,
                         Math.Max(RsiPeriod + RsiLookback + 2,
                         Math.Max(AdxPeriod + 5,
                                  LtfStPeriod + 5))));
            if (Bars.Count < warmup) return;

            if (double.IsNaN(atr.Result.Last(1))    || atr.Result.Last(1) <= 0) return;
            if (double.IsNaN(adx.ADX.Last(1)))       return;
            if (double.IsNaN(rsi.Result.Last(1)))     return;
            if (double.IsNaN(emaSlow.Result.Last(1))) return;

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
            Print($"Delolo Algo V6 stopped. Final Balance: {Account.Balance:F2}");
        }

        protected override void OnError(Error error) => Print($"OnError: {error.Code}");

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

                // Mitternacht-Überlauf: Event von gestern könnte heute noch blockieren
                var blockEndYesterday = newsTime.AddDays(-1).AddMinutes(NewsBlockAfter);
                if (now <= blockEndYesterday) return true;
            }
            return false;
        }

        // ==================== HTF SUPERTREND (Wilder RMA) ====================

        private void RecalculateHtfSupertrendFull()
        {
            int count = htfBars.Count;
            if (count < SupertrendPeriod + 2) return;

            int    startIdx  = Math.Max(SupertrendPeriod + 1, count - HtfStHistory);
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

        // ==================== GATE CHECKS ====================


        // ==================== W1 SUPERTREND ====================

        private void RecalculateW1SupertrendFull()
        {
            int count = w1Bars.Count;
            if (count < W1StPeriod + 2) return;

            int    startIdx  = Math.Max(W1StPeriod + 1, count - W1StHistory);
            double prevUpper = 0, prevLower = 0;
            bool   prevBull  = true;
            double rmaAtr    = 0;

            double sumTr = 0;
            for (int i = startIdx - W1StPeriod + 1; i <= startIdx; i++)
            {
                if (i <= 0) continue;
                sumTr += CalcTrW1(i);
            }
            rmaAtr = sumTr / W1StPeriod;

            for (int i = startIdx; i < count - 1; i++)
            {
                double tr = CalcTrW1(i);
                rmaAtr = ((rmaAtr * (W1StPeriod - 1)) + tr) / W1StPeriod;
                CalcW1Bar(i, prevUpper, prevLower, prevBull, rmaAtr,
                          out double upper, out double lower, out bool bull);
                int idx = i - startIdx;
                if (idx >= 0 && idx < W1StHistory)
                {
                    w1StBand[idx] = bull ? lower : upper;
                    w1StBull[idx] = bull;
                }
                prevUpper = upper; prevLower = lower; prevBull = bull;
            }
            _w1RmaAtr        = rmaAtr;
            _w1PrevUpperBand = prevUpper;
            _w1PrevLowerBand = prevLower;
            _w1PrevBull      = prevBull;
            _w1LastFullCalc  = count - 2;
            w1StCalcUpTo     = count - 2;
        }

        private void UpdateW1SupertrendIncremental()
        {
            int count    = w1Bars.Count;
            int newIndex = count - 2;
            if (newIndex <= _w1LastFullCalc) return;

            double tr  = CalcTrW1(newIndex);
            _w1RmaAtr  = ((_w1RmaAtr * (W1StPeriod - 1)) + tr) / W1StPeriod;
            CalcW1Bar(newIndex, _w1PrevUpperBand, _w1PrevLowerBand, _w1PrevBull, _w1RmaAtr,
                      out double upper, out double lower, out bool bull);

            int startIdx = Math.Max(W1StPeriod + 1, count - W1StHistory);
            int idx      = newIndex - startIdx;
            if (idx < 0 || idx >= W1StHistory) { Print($"W1 ST idx {idx} out of range."); return; }

            w1StBand[idx]    = bull ? lower : upper;
            w1StBull[idx]    = bull;
            _w1PrevUpperBand = upper;
            _w1PrevLowerBand = lower;
            _w1PrevBull      = bull;
            _w1LastFullCalc  = newIndex;
            w1StCalcUpTo     = newIndex;
        }

        private double CalcTrW1(int i)
        {
            if (i <= 0) return w1Bars.HighPrices[i] - w1Bars.LowPrices[i];
            return Math.Max(w1Bars.HighPrices[i] - w1Bars.LowPrices[i],
                   Math.Max(Math.Abs(w1Bars.HighPrices[i] - w1Bars.ClosePrices[i - 1]),
                            Math.Abs(w1Bars.LowPrices[i]  - w1Bars.ClosePrices[i - 1])));
        }

        private void CalcW1Bar(int i, double prevUpper, double prevLower, bool prevBull, double atrVal,
                                out double upperBand, out double lowerBand, out bool bull)
        {
            double hl2 = (w1Bars.HighPrices[i] + w1Bars.LowPrices[i]) / 2.0;
            upperBand  = hl2 + W1StMultiplier * atrVal;
            lowerBand  = hl2 - W1StMultiplier * atrVal;

            if (prevUpper > 0 || prevLower > 0)
            {
                double prevClose = w1Bars.ClosePrices[i - 1];
                lowerBand = (lowerBand > prevLower || prevClose < prevLower) ? lowerBand : prevLower;
                upperBand = (upperBand < prevUpper || prevClose > prevUpper) ? upperBand : prevUpper;
            }

            double close = w1Bars.ClosePrices[i];
            if      (prevUpper == 0 && prevLower == 0) bull = close > hl2;
            else if (prevBull)                         bull = close >= lowerBand;
            else                                       bull = close >  upperBand;
        }

        private bool GetW1SupertrendBullish()
        {
            if (!EnableW1Filter || w1Bars == null) return true;
            int count = w1Bars.Count;
            if (count - 2 != w1StCalcUpTo) UpdateW1SupertrendIncremental();
            int startIdx = Math.Max(W1StPeriod + 1, count - W1StHistory);
            int idx      = (count - 2) - startIdx;
            if (idx < 0 || idx >= W1StHistory) return false;
            return w1StBull[idx];
        }

        private bool GetW1SupertrendBearish()
        {
            if (!EnableW1Filter || w1Bars == null) return true;
            return !GetW1SupertrendBullish();
        }

        private bool IsHtfBullish()
        {
            bool h4Bull = GetHtfSupertrendBullish() && htfEma50.Result.Last(1) > htfEma200.Result.Last(1);
            return h4Bull && GetW1SupertrendBullish();
        }

        private bool IsHtfBearish() =>
            !GetHtfSupertrendBullish() && htfEma50.Result.Last(1) < htfEma200.Result.Last(1);

        private bool IsAdxValid() =>
            adx.ADX.Last(1) >= AdxMinTrend && adx.ADX.Last(1) <= AdxMaxTrend;

        // Preis muss auf der richtigen Seite der EMA200 sein (LTF Trendgate)
        private bool IsPriceAboveEma200() => Bars.ClosePrices.Last(1) > emaSlow.Result.Last(1);
        private bool IsPriceBelowEma200() => Bars.ClosePrices.Last(1) < emaSlow.Result.Last(1);

        // ==================== SCORING – 4 unkorrelierte Komponenten ====================

        // Score 1: LTF Supertrend bestätigt Trendrichtung (1 Punkt)
        private bool ScoreLtfSupertrendLong()  => !double.IsNaN(ltfSupertrend.UpTrend.Last(1));
        private bool ScoreLtfSupertrendShort() => !double.IsNaN(ltfSupertrend.DownTrend.Last(1));

        // Score 2: RSI-Pullback-Erholung – war überdehnt, dreht jetzt zurück (1 Punkt)
        // Long: RSI war in den letzten N Bars unter RsiPullbackLong, jetzt über RsiRecoveryLong
        private bool ScoreRsiRecoveryLong()
        {
            double currentRsi = rsi.Result.Last(1);
            if (currentRsi <= RsiRecoveryLong) return false;
            int lookback = Math.Min(RsiLookback, Bars.Count - 2);
            for (int i = 1; i <= lookback; i++)
                if (rsi.Result.Last(i) < RsiPullbackLong) return true;
            return false;
        }

        // Short: RSI war in den letzten N Bars über RsiPullbackShort, jetzt unter RsiRecoveryShort
        private bool ScoreRsiRecoveryShort()
        {
            double currentRsi = rsi.Result.Last(1);
            if (currentRsi >= RsiRecoveryShort) return false;
            int lookback = Math.Min(RsiLookback, Bars.Count - 2);
            for (int i = 1; i <= lookback; i++)
                if (rsi.Result.Last(i) > RsiPullbackShort) return true;
            return false;
        }

        // Score 3: EMA-Momentum – EMA21 > EMA50 zeigt kurzfristiges Trendmomentum (1 Punkt)
        private bool ScoreEmaMomentumLong()  => emaFast.Result.Last(1) > emaMid.Result.Last(1);
        private bool ScoreEmaMomentumShort() => emaFast.Result.Last(1) < emaMid.Result.Last(1);

        // Score 4: ATR-Momentum – aktueller ATR über Durchschnitt, Markt bewegt sich (1 Punkt)
        private bool ScoreAtrMomentum()
        {
            int safeBars = Math.Min(AtrAvgLookback, Bars.Count - 2);
            if (safeBars < 1) return false;
            double sum = 0;
            for (int i = 1; i <= safeBars; i++) sum += atr.Result.Last(i);
            double avgAtr = sum / safeBars;
            return atr.Result.Last(1) >= avgAtr * AtrMomentumRatio;
        }

        private int CalcScore(bool isLong)
        {
            int score = 0;
            if (isLong)
            {
                if (ScoreLtfSupertrendLong())  score++;
                if (ScoreRsiRecoveryLong())     score++;
                if (ScoreEmaMomentumLong())     score++;
                if (ScoreAtrMomentum())         score++;
            }
            else
            {
                if (ScoreLtfSupertrendShort()) score++;
                if (ScoreRsiRecoveryShort())   score++;
                if (ScoreEmaMomentumShort())   score++;
                if (ScoreAtrMomentum())        score++;
            }
            return score;
        }

        // ==================== TRIGGER: CANDLESTICK PATTERNS ====================

        private double CandleBody(int i)      => Math.Abs(Bars.ClosePrices.Last(i) - Bars.OpenPrices.Last(i));
        private double CandleRange(int i)     => Bars.HighPrices.Last(i) - Bars.LowPrices.Last(i);
        private double UpperWick(int i)       => Bars.HighPrices.Last(i) - Math.Max(Bars.OpenPrices.Last(i), Bars.ClosePrices.Last(i));
        private double LowerWick(int i)       => Math.Min(Bars.OpenPrices.Last(i), Bars.ClosePrices.Last(i)) - Bars.LowPrices.Last(i);
        private bool   IsBullishCandle(int i) => Bars.ClosePrices.Last(i) > Bars.OpenPrices.Last(i);
        private bool   IsBearishCandle(int i) => Bars.ClosePrices.Last(i) < Bars.OpenPrices.Last(i);

        private bool IsBullishEngulfing()
        {
            if (!EnableBullishEngulfing || !IsBearishCandle(2) || !IsBullishCandle(1)) return false;
            double pb = CandleBody(2); if (pb <= 0) return false;
            return Bars.ClosePrices.Last(1) > Bars.OpenPrices.Last(2)
                && Bars.OpenPrices.Last(1)  < Bars.ClosePrices.Last(2)
                && CandleBody(1) >= pb * EngulfingBodyRatio;
        }

        private bool IsBearishEngulfing()
        {
            if (!EnableBearishEngulfing || !IsBullishCandle(2) || !IsBearishCandle(1)) return false;
            double pb = CandleBody(2); if (pb <= 0) return false;
            return Bars.ClosePrices.Last(1) < Bars.OpenPrices.Last(2)
                && Bars.OpenPrices.Last(1)  > Bars.ClosePrices.Last(2)
                && CandleBody(1) >= pb * EngulfingBodyRatio;
        }

        private bool IsBullishPinBar()
        {
            if (!EnablePinBarLong) return false;
            double body = CandleBody(1); double range = CandleRange(1);
            if (range <= 0 || body <= 0) return false;
            return LowerWick(1) >= body * PinBarWickRatio
                && UpperWick(1) <= body * 0.5
                && body <= range * 0.35;
        }

        private bool IsBearishPinBar()
        {
            if (!EnableShootingStar) return false;
            double body = CandleBody(1); double range = CandleRange(1);
            if (range <= 0 || body <= 0) return false;
            return UpperWick(1) >= body * PinBarWickRatio
                && LowerWick(1) <= body * 0.5
                && body <= range * 0.35;
        }

        private bool HasBullishTrigger(out string name)
        {
            name = "None";
            if (IsBullishEngulfing()) { name = "Bullish Engulfing"; return true; }
            if (IsBullishPinBar())    { name = "Pin Bar (Hammer)";  return true; }
            return false;
        }

        private bool HasBearishTrigger(out string name, bool engulfingAllowed = true)
        {
            name = "None";
            if (engulfingAllowed && IsBearishEngulfing()) { name = "Bearish Engulfing"; return true; }
            if (IsBearishPinBar())    { name = "Shooting Star";     return true; }
            return false;
        }

        // ==================== ENTRY LOGIC ====================

        private void CheckForEntrySignal()
        {
            double atrVal = atr.Result.Last(1);

            // Long-Setup prüfen
            if (IsHtfBullish() && IsPriceAboveEma200() && IsAdxValid())
            {
                int score = CalcScore(isLong: true);
                if (score >= MinScoreToTrade && buyTradesToday < MaxTradesPerDirectionPerDay && HasBullishTrigger(out string pat))
                {
                    lastScore         = score;
                    lastCandlePattern = pat;
                    double mult = CalculateRiskMultiplier(score);
                    ExecuteTrade(TradeType.Buy, atrVal, score, pat, mult);
                    return;
                }
            }

            // Short-Setup prüfen
            if (IsHtfBearish() && IsPriceBelowEma200() && IsAdxValid())
            {
                int score = CalcScore(isLong: false);
                bool beMinOk = score >= BearishEngulfingMinScore;
                if (score >= MinScoreToTrade && sellTradesToday < MaxTradesPerDirectionPerDay && HasBearishTrigger(out string pat, beMinOk))
                {
                    lastScore         = score;
                    lastCandlePattern = pat;
                    double mult = CalculateRiskMultiplier(score);
                    ExecuteTrade(TradeType.Sell, atrVal, score, pat, mult);
                }
            }
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
            if (BlockWednesdayEntries && Server.Time.DayOfWeek == DayOfWeek.Wednesday)
            { Print("Wednesday Entry Block active."); return false; }

            double spread = GetSpreadPips();
            if (spread > MaxSpreadPips) { Print($"Spread too high: {spread:F2}p"); return false; }

            if (EnableSwapProtection && IsSwapProtectionImminent()) return false;
            if (isNewsNow) { Print("News Block active."); return false; }

            return true;
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

        private bool IsSwapProtectionImminent()
        {
            var  now    = Server.Time;
            int  buffer = SwapProtectionBuffer;
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
            foreach (var pos in positions)
            {
                double pnl  = pos.NetProfit;
                double swap = pos.Swap;
                if (swap >= 0) continue;
                if (Math.Abs(pnl) < 0.01) continue;
                double swapCost = Math.Abs(swap);
                double ratio = pnl < 0
                    ? (swapCost / Math.Abs(pnl)) * 100.0
                    : (swapCost / pnl) * 100.0;
                if (ratio >= SwapThresholdPercent)
                    ClosePositionWithReason(pos, $"Swap {ratio:F1}% of P/L [{swap:F2}]");
            }
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

        // ==================== SIZING ====================

        private double CalculateRiskMultiplier(int score)
        {
            if (!EnableScoreSizing) return 1.0;
            if (MinScoreToTrade >= 4) return RiskMultiplierMax;
            double t = Math.Max(0.0, Math.Min(1.0,
                (double)(score - MinScoreToTrade) / (4 - MinScoreToTrade)));
            return RiskMultiplierMin + t * (RiskMultiplierMax - RiskMultiplierMin);
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
            double minDist      = Symbol.MinStopLossDistance;
            if (stopLossPips < minDist)
            { Print($"SL {stopLossPips:F1}p < MinStopDist {minDist:F1}p. Trade aborted."); return; }
            if (tp3Dist <= 0) { Print("TP3 dist <= 0, aborted."); return; }

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

            var pos       = result.Position;
            var modResult = ModifyPosition(pos, slPrice, tp3Price, ProtectionType.Absolute);
            if (!modResult.IsSuccessful)
                Print($"WARNING: SL/TP set FAILED: {modResult.Error} | SL:{slPrice:F5} TP:{tp3Price:F5}");
            else
                Print($"SL/TP OK: SL={slPrice:F5} ({stopLossPips:F1}p) TP3={tp3Price:F5} RR=1:{(tp3Dist/slDist):F2}");

            _tp1Price = tp1Price;
            _tp2Price = tp2Price;

            positionPatterns[pos.Id] = candlePattern;
            positionScores[pos.Id]   = signalScore;

            tradesOpenedToday++;
            if (tradeType == TradeType.Buy) buyTradesToday++;
            else                          sellTradesToday++;
            managedPositionId = pos.Id;
            entryPrice        = pos.EntryPrice;
            tp1Hit         = false;
            tp2Hit         = false;
            trailingActive = !EnablePartialTPs && EnableTrailingStop;
            breakEvenSet   = false;

            double lots = Symbol.VolumeInUnitsToQuantity(vol);
            Print($"TRADE #{tradesOpenedToday} {tradeType} | Score:{signalScore}/4 | Pat:{candlePattern}");
            Print($"  Entry:{pos.EntryPrice:F5} SL:{slPrice:F5} TP1:{tp1Price:F5} TP2:{tp2Price:F5} TP3:{tp3Price:F5}");
            Print($"  Risk:{riskAmount:F2} ({effectiveRisk:F2}%) | Lots:{lots:F2} | ATR:{atrVal:F5}");
            Print($"  ADX:{adx.ADX.Last(1):F1} | HTF:{(IsHtfBullish() ? "Bull" : "Bear")} | {Server.Time:HH:mm}UTC");
        }

        // ==================== POSITION SIZING ====================

        private double CalculateVolumeInUnitsFromRisk(double riskAmount, double stopLossPips, TradeType tradeType)
        {
            double vol = riskAmount / (stopLossPips * Symbol.PipValue);
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
            double step    = Symbol.VolumeInUnitsStep;
            double minVol  = Symbol.VolumeInUnitsMin;
            double maxVol  = Symbol.VolumeInUnitsMax;
            double optimal = minVol;

            // Binäre Suche für robuste Margin-Berechnung
            for (int i = 0; i < 30; i++)
            {
                if (maxVol - minVol < step) break;
                double mid    = Symbol.NormalizeVolumeInUnits((minVol + maxVol) / 2.0, RoundingMode.Down);
                double margin = Symbol.GetEstimatedMargin(tradeType, mid);
                if (margin <= allowed) { optimal = mid; minVol = mid + step; }
                else                   { maxVol  = mid - step; }
                if (minVol > maxVol) break;
            }
            return Math.Max(optimal, Symbol.VolumeInUnitsMin);
        }

        // ==================== POSITION MANAGEMENT ====================

        private void ManageOpenPositionsFull()
        {
            var positions = Positions.FindAll(BotLabel, SymbolName);
            if (positions.Length == 0) return;

            foreach (var pos in positions)
            {
                if (pos == null) continue;
                bool isPrimary = managedPositionId.HasValue && managedPositionId.Value == pos.Id;

                if (isPrimary)
                {
                    if (EnablePartialTPs)  CheckPartialClose(pos);
                    if (EnableBreakEven)   CheckBreakEven(pos);
                    bool trailReady = trailingActive || !EnablePartialTPs;
                    if (EnableTrailingStop && trailReady) ApplyTrailingStop(pos);
                }
                else
                {
                    if (EnableTrailingStop) ApplyTrailingStop(pos);
                }
            }
        }

        private void ManageOpenPositionsTickOnly()
        {
            if (!EnableTrailingStop) return;
            var positions = Positions.FindAll(BotLabel, SymbolName);
            if (positions.Length == 0) return;
            foreach (var pos in positions) ApplyTrailingStop(pos);
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
                    if (EnableTrailingStop) { trailingActive = true; Print("Trailing activated after TP1."); }
                }
            }

            if (!tp2Hit)
            {
                bool tp2Reached = pos.TradeType == TradeType.Buy
                    ? Symbol.Bid >= _tp2Price
                    : Symbol.Ask <= _tp2Price;

                if (tp2Reached) { TryPartialClose(pos, Tp2ClosePercent, "TP2"); tp2Hit = true; }
            }
        }

        private void CheckBreakEven(Position pos)
        {
            if (breakEvenSet) return;
            double atrTriggerDist = BreakEvenTriggerAtr * atr.Result.Last(1);
            bool triggerReached =
                pos.TradeType == TradeType.Buy
                    ? Symbol.Bid >= entryPrice + atrTriggerDist
                    : Symbol.Ask <= entryPrice - atrTriggerDist;
            if (!triggerReached) return;

            double atrBuffer = BreakEvenBufferAtr * atr.Result.Last(1);
            double minBuffer = Symbol.Spread * 2.0;
            double buffer    = Math.Max(atrBuffer, minBuffer);
            double? currentSL = pos.StopLoss;

            if (pos.TradeType == TradeType.Buy)
            {
                double beSL = Math.Round(entryPrice + buffer, Symbol.Digits);
                if (currentSL == null || beSL > currentSL.Value)
                {
                    var r = ModifyPosition(pos, beSL, pos.TakeProfit, ProtectionType.Absolute);
                    if (r.IsSuccessful) { breakEvenSet = true; Print($"Break-Even set: SL→{beSL:F5}"); }
                    else Print($"Break-Even FAILED: {r.Error}");
                }
            }
            else
            {
                double beSL = Math.Round(entryPrice - buffer, Symbol.Digits);
                if (currentSL == null || beSL < currentSL.Value)
                {
                    var r = ModifyPosition(pos, beSL, pos.TakeProfit, ProtectionType.Absolute);
                    if (r.IsSuccessful) { breakEvenSet = true; Print($"Break-Even set: SL→{beSL:F5}"); }
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
            if (pos.Pips <= 0) return;
            double atrVal     = atr.Result.Last(1);
            double profitPips = pos.Pips;
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

            if (!scoreWins.ContainsKey(sc))   scoreWins[sc]  = 0;
            if (!scoreLosses.ContainsKey(sc)) scoreLosses[sc] = 0;
            if (isWin) scoreWins[sc]++; else scoreLosses[sc]++;

            double wr = totalTrades > 0 ? (double)totalWins / totalTrades * 100.0 : 0;
            double pf = totalLossAmount > 0 ? totalWinAmount / totalLossAmount : 0;
            Print($"Trade Closed [{(isWin ? "WIN" : "LOSS")}] P/L:{pnl:F2} Pat:{pat} Score:{sc}/4 WR:{wr:F1}% PF:{pf:F2} Reason:{args.Reason}");

            // State-Reset wenn die verwaltete Position geschlossen wurde
            if (managedPositionId.HasValue && managedPositionId.Value == pos.Id)
            {
                managedPositionId = null;
                tp1Hit         = false;
                tp2Hit         = false;
                trailingActive = false;
                breakEvenSet   = false;
                _tp1Price      = 0;
                _tp2Price      = 0;
                entryPrice     = 0;
                Print($"State reset after position #{pos.Id} closed ({args.Reason}).");
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
            dailyStartBalance         = Account.Balance;
            highestBalanceToday       = Account.Balance;
            lastTradeDate             = Server.Time.Date;
            swapThresholdCheckedToday = false;
            hardCloseExecutedToday    = false;

            bool hasOpenPosition = Positions.FindAll(BotLabel, SymbolName).Length > 0;
            if (!hasOpenPosition)
            {
                tradesOpenedToday = 0;
                buyTradesToday    = 0;
                sellTradesToday   = 0;
                managedPositionId = null;
                tp1Hit = false; tp2Hit = false;
                trailingActive = false; breakEvenSet = false;
                _tp1Price = 0; _tp2Price = 0;
            }
            else
            {
                // Overnight-Position offen: Zähler auf 1 damit MaxDailyTrades korrekt bleibt
                tradesOpenedToday = 1;
                Print("Overnight position on new day: tradesOpenedToday set to 1.");
            }
            lastCandlePattern = "None";
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
            Print($"MaxDD:{maxDrawdown:F2} ({maxDrawdownPercent:F2}%) | Last Score:{lastScore}/4 | Pat:{lastCandlePattern}");
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
            for (int s = MinScoreToTrade; s <= 4; s++)
            {
                int w   = scoreWins.ContainsKey(s)   ? scoreWins[s]   : 0;
                int l   = scoreLosses.ContainsKey(s) ? scoreLosses[s] : 0;
                int tot = w + l;
                if (tot == 0) continue;
                double swr = (double)w / tot * 100.0;
                Print($"  Score {s}/4: W{w} L{l} WR{swr:F0}%");
            }
        }
    }
}
