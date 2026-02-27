using System;
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

        [Parameter("Max Trades Per Day", DefaultValue = 5, MinValue = 1, MaxValue = 100)]
        public int MaxDailyTrades { get; set; }

        [Parameter("Max Daily Loss (%)", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Max Spread (pips)", DefaultValue = 0.5, MinValue = 0.1, MaxValue = 10.0)]
        public double MaxSpreadPips { get; set; }

        [Parameter("Max Margin Usage (%)", DefaultValue = 25.0, MinValue = 10.0, MaxValue = 50.0)]
        public double MaxMarginUsagePercent { get; set; }

        [Parameter("== INDICATORS ==")]
        public string IndicatorLabel { get; set; }

        [Parameter("RSI Period", DefaultValue = 14, MinValue = 3, MaxValue = 50)]
        public int RsiPeriod { get; set; }

        [Parameter("RSI Overbought", DefaultValue = 70, MinValue = 50, MaxValue = 95)]
        public double RsiOverbought { get; set; }

        [Parameter("RSI Oversold", DefaultValue = 30, MinValue = 5, MaxValue = 50)]
        public double RsiOversold { get; set; }

        [Parameter("MACD Fast", DefaultValue = 12, MinValue = 2, MaxValue = 50)]
        public int MacdFast { get; set; }

        [Parameter("MACD Slow", DefaultValue = 26, MinValue = 3, MaxValue = 100)]
        public int MacdSlow { get; set; }

        [Parameter("MACD Signal", DefaultValue = 9, MinValue = 2, MaxValue = 50)]
        public int MacdSignal { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, MaxValue = 100)]
        public int AtrPeriod { get; set; }

        [Parameter("Supertrend Period", DefaultValue = 10, MinValue = 2, MaxValue = 50)]
        public int SupertrendPeriod { get; set; }

        [Parameter("Supertrend Multiplier", DefaultValue = 3.0, MinValue = 0.5, MaxValue = 10.0)]
        public double SupertrendMultiplier { get; set; }

        [Parameter("== TRADING HOURS (UTC) ==")]
        public string SessionLabel { get; set; }

        [Parameter("Start Hour", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartHour { get; set; }

        [Parameter("End Hour", DefaultValue = 20, MinValue = 0, MaxValue = 23)]
        public int EndHour { get; set; }

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

        [Parameter("Enable MACD Filter", DefaultValue = true)]
        public bool EnableMacdFilter { get; set; }

        [Parameter("Enable RSI Filter", DefaultValue = true)]
        public bool EnableRsiFilter { get; set; }

        [Parameter("Trailing Start (ATR)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 20.0)]
        public double TrailingStartMultiplier { get; set; }

        [Parameter("Trailing Distance (ATR)", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 20.0)]
        public double TrailingDistanceMultiplier { get; set; }

        // ==================== INDICATORS ====================

        private ExponentialMovingAverage ema21;
        private ExponentialMovingAverage ema50;
        private ExponentialMovingAverage ema200;

        private RelativeStrengthIndex rsi;
        private MacdHistogram macd;
        private AverageTrueRange atr;
        private Supertrend supertrend;

        // ==================== STATE ====================

        private const string BotLabel = "MultiScalper";

        private int tradesOpenedToday;
        private double dailyStartBalance;
        private double highestBalanceToday;
        private DateTime lastTradeDate;

        private long? managedPositionId;
        private bool tp1Hit;
        private bool tp2Hit;
        private bool trailingActive;

        // ==================== LIFECYCLE ====================

        protected override void OnStart()
        {
            Print("╔════════════════════════════════════════╗");
            Print("║      Multi-Indicator Scalper v2.5      ║");
            Print("║  + 4 Optional Features (Full Control)  ║");
            Print("╚════════════════════════════════════════╝");
            Print($"Account Balance: {Account.Balance:F2} {Account.Asset.Name}");
            Print($"Risk per Trade: {RiskPercent:F2}%");
            Print($"Max Trades/Day: {MaxDailyTrades}");
            Print($"Max Daily Loss: {MaxDailyLossPercent:F2}%");
            Print($"Max Spread: {MaxSpreadPips:F2} pips");
            Print($"Max Margin Usage: {MaxMarginUsagePercent:F1}%");
            Print($"Trading Hours: {StartHour:D2}:00 - {EndHour:D2}:00 UTC");
            Print($"Symbol: {SymbolName}");
            Print($"Timeframe: {TimeFrame}");
            Print($"Leverage: 1:{Account.PreciseLeverage:F0}");
            Print("─────────────────────────────────────────");
            Print($"Strategy Features:");
            Print($"  - Trailing Stop:  {(EnableTrailingStop ? "ENABLED" : "DISABLED")}");
            Print($"  - Partial TPs:    {(EnablePartialTPs ? "ENABLED" : "DISABLED")}");
            Print($"  - MACD Filter:    {(EnableMacdFilter ? "ENABLED" : "DISABLED")}");
            Print($"  - RSI Filter:     {(EnableRsiFilter ? "ENABLED" : "DISABLED")}");
            Print("─────────────────────────────────────────");

            ema21 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 21);
            ema50 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 50);
            ema200 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 200);

            rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            macd = Indicators.MacdHistogram(Bars.ClosePrices, MacdFast, MacdSlow, MacdSignal);
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            supertrend = Indicators.Supertrend(SupertrendPeriod, SupertrendMultiplier);

            ResetDailyTracking();

            Print("✓ Indicators initialized");
            Print("✓ Bot ready - Position Sizing: Conservative (66%)");
            Print("═════════════════════════════════════════");
        }

        protected override void OnBar()
        {
            if (Bars.Count < 210)
                return;

            HandleNewTradingDayIfNeeded();

            if (Account.Balance > highestBalanceToday)
                highestBalanceToday = Account.Balance;

            ManageOpenPositions();

            if (!ShouldTrade())
                return;

            CheckForEntrySignal();
        }

        protected override void OnTick()
        {
            ManageOpenPositions();
        }

        protected override void OnStop()
        {
            PrintDailySummary();

            Print("\n╔════════════════════════════════════════╗");
            Print("║           BOT STOPPED v2.5             ║");
            Print("╚════════════════════════════════════════╝");
            Print($"Final Balance: {Account.Balance:F2}");
            Print($"Trades Today: {tradesOpenedToday}");
            Print($"Strategy: Trail={EnableTrailingStop}, Partial={EnablePartialTPs}, MACD={EnableMacdFilter}, RSI={EnableRsiFilter}");
            Print("═════════════════════════════════════════\n");
        }

        // ==================== DAY / SESSION ====================

        private void HandleNewTradingDayIfNeeded()
        {
            if (Server.Time.Date == lastTradeDate)
                return;

            PrintDailySummary();
            ResetDailyTracking();
            Print($"\n═══ NEW TRADING DAY: {Server.Time.Date:yyyy-MM-dd} ═══");
        }

        private void ResetDailyTracking()
        {
            tradesOpenedToday = 0;
            dailyStartBalance = Account.Balance;
            highestBalanceToday = Account.Balance;
            lastTradeDate = Server.Time.Date;

            managedPositionId = null;
            tp1Hit = false;
            tp2Hit = false;
            trailingActive = false;
        }

        private bool IsInTradingSession()
        {
            int currentHour = Server.Time.Hour;

            if (StartHour < EndHour)
                return currentHour >= StartHour && currentHour < EndHour;

            return currentHour >= StartHour || currentHour < EndHour;
        }

        private double GetSpreadPips()
        {
            return Symbol.Spread / Symbol.PipSize;
        }

        private bool IsSpreadAcceptable()
        {
            return GetSpreadPips() <= MaxSpreadPips;
        }

        // ==================== TRADE GATING ====================

        private bool ShouldTrade()
        {
            if (Positions.FindAll(BotLabel, SymbolName).Length > 0)
                return false;

            if (tradesOpenedToday >= MaxDailyTrades)
                return false;

            double dailyPnL = Account.Balance - dailyStartBalance;
            double dailyLossLimit = dailyStartBalance * (MaxDailyLossPercent / 100.0);

            if (dailyPnL < -dailyLossLimit)
            {
                Print($"⚠ Daily loss limit reached: {dailyPnL:F2}. Pausing until tomorrow.");
                return false;
            }

            if (!IsInTradingSession())
                return false;

            if (!IsSpreadAcceptable())
            {
                Print($"⚠ Spread too high: {GetSpreadPips():F2} pips (Max {MaxSpreadPips:F2})");
                return false;
            }

            return true;
        }

        // ==================== ENTRY LOGIC (WITH OPTIONAL FILTERS) ====================

        private void CheckForEntrySignal()
        {
            double price = Bars.ClosePrices.LastValue;
            double atrValue = atr.Result.LastValue;

            double ema21Val = ema21.Result.LastValue;
            double ema50Val = ema50.Result.LastValue;
            double ema200Val = ema200.Result.LastValue;

            // RSI Filter (optional)
            bool longRsi = true;
            bool shortRsi = true;
            
            if (EnableRsiFilter)
            {
                double rsiValue = rsi.Result.LastValue;
                longRsi = rsiValue > 40 && rsiValue < RsiOverbought;
                shortRsi = rsiValue > RsiOversold && rsiValue < 60;
            }

            // MACD Filter (optional)
            bool longMacd = true;
            bool shortMacd = true;
            
            if (EnableMacdFilter)
            {
                double macdHist = macd.Histogram.LastValue;
                double macdHistPrev = macd.Histogram.Last(1);
                
                bool macdBullish = macdHist > 0;
                bool macdBearish = macdHist < 0;
                bool macdBullishCross = macdHist > 0 && macdHistPrev <= 0;
                bool macdBearishCross = macdHist < 0 && macdHistPrev >= 0;
                
                longMacd = macdBullish || macdBullishCross;
                shortMacd = macdBearish || macdBearishCross;
            }

            // Supertrend (ALWAYS ACTIVE - Core Filter)
            bool isBullishSupertrend = !double.IsNaN(supertrend.UpTrend.LastValue);
            bool isBearishSupertrend = !double.IsNaN(supertrend.DownTrend.LastValue);

            // Long conditions
            bool longTrend = price > ema200Val;
            bool longMomentum = ema21Val > ema50Val;
            bool longSupertrend = isBullishSupertrend;

            if (longTrend && longMomentum && longSupertrend && longRsi && longMacd)
            {
                ExecuteTrade(TradeType.Buy, atrValue);
                return;
            }

            // Short conditions
            bool shortTrend = price < ema200Val;
            bool shortMomentum = ema21Val < ema50Val;
            bool shortSupertrend = isBearishSupertrend;

            if (shortTrend && shortMomentum && shortSupertrend && shortRsi && shortMacd)
            {
                ExecuteTrade(TradeType.Sell, atrValue);
                return;
            }
        }

        // ==================== EXECUTION ====================

        private void ExecuteTrade(TradeType tradeType, double atrValue)
        {
            // Pre-Trade Margin Check
            double? currentMarginLevel = Account.MarginLevel;
            
            if (currentMarginLevel.HasValue && currentMarginLevel.Value < 200)
            {
                Print($"⚠ Margin Level too low: {currentMarginLevel.Value:F0}%. Trade aborted.");
                return;
            }

            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            double stopLossPips = (StopLossMultiplier * atrValue) / Symbol.PipSize;
            double takeProfitPips = (TakeProfit3Multiplier * atrValue) / Symbol.PipSize;

            if (stopLossPips <= 0 || takeProfitPips <= 0)
            {
                Print("Invalid SL/TP (<= 0). Skipping.");
                return;
            }

            double volumeInUnits = CalculateVolumeInUnitsFromRisk(riskAmount, stopLossPips);

            if (volumeInUnits < Symbol.VolumeInUnitsMin)
            {
                Print($"⚠ Position size too small: {volumeInUnits:F0} units (min {Symbol.VolumeInUnitsMin:F0}). Skipping.");
                return;
            }

            // Double-Check: Simuliere Margin-Level nach Trade
            double requiredMargin = Symbol.GetEstimatedMargin(tradeType, volumeInUnits);
            double currentMargin = Account.Margin;
            
            if (currentMargin + requiredMargin > 0)
            {
                double marginLevelAfterTrade = (Account.Equity / (currentMargin + requiredMargin)) * 100;
                
                if (marginLevelAfterTrade < 150)
                {
                    Print($"⚠ Trade would reduce Margin Level to {marginLevelAfterTrade:F0}%. Too risky, aborted.");
                    return;
                }
            }

            var result = ExecuteMarketOrder(
                tradeType,
                SymbolName,
                volumeInUnits,
                BotLabel,
                stopLossPips,
                takeProfitPips
            );

            if (!result.IsSuccessful)
            {
                Print($"✗ Trade failed: {result.Error}");
                return;
            }

            tradesOpenedToday++;

            managedPositionId = result.Position.Id;
            tp1Hit = false;
            tp2Hit = false;
            trailingActive = false;

            double lotsApprox = Symbol.VolumeInUnitsToQuantity(volumeInUnits);
            double? finalMarginLevel = Account.MarginLevel;
            string marginLevelDisplay = finalMarginLevel.HasValue ? $"{finalMarginLevel.Value:F0}%" : "N/A";
            
            // Build feature string
            string featuresInfo = "";
            if (EnablePartialTPs) featuresInfo += "Partial ";
            if (EnableTrailingStop) featuresInfo += "Trail ";
            if (!EnableMacdFilter) featuresInfo += "NoMACD ";
            if (!EnableRsiFilter) featuresInfo += "NoRSI ";
            if (string.IsNullOrEmpty(featuresInfo)) featuresInfo = "Simple ";

            Print($"\n┌─── TRADE OPENED #{tradesOpenedToday} ({featuresInfo.Trim()}) ───");
            Print($"│ Type: {tradeType}");
            Print($"│ Volume: {lotsApprox:F2} lots ({volumeInUnits:F0} units)");
            Print($"│ Entry: {result.Position.EntryPrice:F5}");
            Print($"│ SL: {stopLossPips:F1} pips  |  TP: {takeProfitPips:F1} pips");
            Print($"│ Risk:Reward = 1:{(takeProfitPips / stopLossPips):F2}");
            Print($"│ Risk: {riskAmount:F2} ({RiskPercent:F2}%)");
            Print($"│ Spread: {GetSpreadPips():F2} pips");
            Print($"│ Margin Used: {requiredMargin:F2} ({(requiredMargin/Account.Equity*100):F1}%)");
            Print($"│ Margin Level: {marginLevelDisplay}");
            Print($"│ Time: {Server.Time:HH:mm:ss}");
            Print($"└────────────────────────────────────");
        }

        // ==================== POSITION SIZING (HYBRID + MARGIN SAFE) ====================

        private double CalculateVolumeInUnitsFromRisk(double riskAmount, double stopLossPips)
        {
            // 1. Risk-basierte Berechnung
            double riskBasedVolume = riskAmount / (stopLossPips * Symbol.PipValue);
            
            // 2. Balance-basierte Mindestgröße (konservativ 66%)
            double balanceBasedVolume = CalculateMinimumVolumeFromBalance();
            
            // 3. Nimm das höhere
            double volumeInUnits = Math.Max(riskBasedVolume, balanceBasedVolume);
            
            // 4. Max-Risk Cap (niemals mehr als 3% riskieren)
            double maxRiskPercent = 3.0;
            double maxAllowedRisk = Account.Balance * (maxRiskPercent / 100.0);
            double maxVolumeForRisk = maxAllowedRisk / (stopLossPips * Symbol.PipValue);
            volumeInUnits = Math.Min(volumeInUnits, maxVolumeForRisk);
            
            // 5. Margin-Safe Check
            double maxVolumeForMargin = CalculateMaxVolumeFromMargin(MaxMarginUsagePercent);
            volumeInUnits = Math.Min(volumeInUnits, maxVolumeForMargin);
            
            // Validierung
            if (double.IsNaN(volumeInUnits) || double.IsInfinity(volumeInUnits) || volumeInUnits <= 0)
                return Symbol.VolumeInUnitsMin;
            
            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits, RoundingMode.Down);
            
            if (volumeInUnits < Symbol.VolumeInUnitsMin)
                volumeInUnits = Symbol.VolumeInUnitsMin;
            
            if (volumeInUnits > Symbol.VolumeInUnitsMax)
                volumeInUnits = Symbol.VolumeInUnitsMax;
            
            return volumeInUnits;
        }

        private double CalculateMinimumVolumeFromBalance()
        {
            double currentBalance = Account.Balance;
            
            // KONSERVATIVE Skalierung: 100€ = 0.02 Lots (66% vom Maximum)
            // Dann +0.01 Lots pro 75€ für langsameres Wachstum
            double baseBalance = 100.0;
            double baseLots = 0.02;
            double incrementPer = 75.0;
            double incrementLots = 0.01;
            
            double lots;
            
            if (currentBalance < baseBalance)
            {
                // Unter 100€: proportional reduzieren (min 0.01)
                lots = Math.Max(0.01, (currentBalance / baseBalance) * baseLots);
            }
            else
            {
                // Ab 100€: langsam skalieren
                double additionalBalance = currentBalance - baseBalance;
                double additionalLots = (additionalBalance / incrementPer) * incrementLots;
                lots = baseLots + additionalLots;
            }
            
            return Symbol.QuantityToVolumeInUnits(lots);
        }

        private double CalculateMaxVolumeFromMargin(double maxMarginUsagePercent)
        {
            double freeMargin = Account.FreeMargin;
            double allowedMargin = freeMargin * (maxMarginUsagePercent / 100.0);
            
            // Binäre Suche für optimales Volume
            double minVolume = Symbol.VolumeInUnitsMin;
            double maxVolume = Symbol.VolumeInUnitsMax;
            double optimalVolume = minVolume;
            
            for (int i = 0; i < 20; i++)
            {
                double testVolume = (minVolume + maxVolume) / 2.0;
                testVolume = Symbol.NormalizeVolumeInUnits(testVolume, RoundingMode.Down);
                
                double marginRequired = Symbol.GetEstimatedMargin(TradeType.Buy, testVolume);
                
                if (marginRequired <= allowedMargin)
                {
                    optimalVolume = testVolume;
                    minVolume = testVolume;
                }
                else
                {
                    maxVolume = testVolume;
                }
                
                if (Math.Abs(maxVolume - minVolume) < Symbol.VolumeInUnitsStep)
                    break;
            }
            
            return optimalVolume;
        }

        // ==================== POSITION MANAGEMENT ====================

        private void ManageOpenPositions()
        {
            var positions = Positions.FindAll(BotLabel, SymbolName);
            if (positions.Length == 0)
            {
                managedPositionId = null;
                tp1Hit = false;
                tp2Hit = false;
                trailingActive = false;
                return;
            }

            var position = positions[0];

            if (managedPositionId == null || managedPositionId.Value != position.Id)
            {
                managedPositionId = position.Id;
                tp1Hit = false;
                tp2Hit = false;
                trailingActive = false;
            }

            // Nur Partial Close wenn Feature enabled
            if (EnablePartialTPs)
            {
                CheckPartialClose(position);
            }

            // Nur Trailing wenn Feature enabled UND aktiviert
            if (EnableTrailingStop && trailingActive)
            {
                ApplyTrailingStop(position);
            }
        }

        private void CheckPartialClose(Position position)
        {
            double atrValue = atr.Result.LastValue;
            double profitPips = Math.Abs(position.Pips);

            // TP1
            double tp1Pips = (TakeProfit1Multiplier * atrValue) / Symbol.PipSize;
            if (!tp1Hit && profitPips >= tp1Pips)
            {
                bool closed = TryPartialClose(position, Tp1ClosePercent, "TP1", profitPips);
                tp1Hit = true;

                // Trailing nur aktivieren wenn Feature enabled
                if (closed && EnableTrailingStop)
                {
                    trailingActive = true;
                    Print("✓ Trailing stop activated (after TP1).");
                }
            }

            // TP2
            double tp2Pips = (TakeProfit2Multiplier * atrValue) / Symbol.PipSize;
            if (!tp2Hit && profitPips >= tp2Pips)
            {
                TryPartialClose(position, Tp2ClosePercent, "TP2", profitPips);
                tp2Hit = true;
            }
        }

        private bool TryPartialClose(Position position, int closePercent, string tag, double profitPips)
        {
            if (closePercent <= 0)
                return false;

            if (position.VolumeInUnits < Symbol.VolumeInUnitsMin * 2)
                return false;

            double desiredClose = position.VolumeInUnits * (closePercent / 100.0);
            double closeUnits = Symbol.NormalizeVolumeInUnits(desiredClose, RoundingMode.Down);

            if (closeUnits < Symbol.VolumeInUnitsMin)
                closeUnits = Symbol.VolumeInUnitsMin;

            if (closeUnits >= position.VolumeInUnits)
                return false;

            var closeResult = ClosePosition(position, closeUnits);
            if (!closeResult.IsSuccessful)
                return false;

            Print($"✓ {tag} hit: closed {closePercent}% at +{profitPips:F1} pips ({closeUnits:F0} units).");
            return true;
        }

        private void ApplyTrailingStop(Position position)
        {
            double atrValue = atr.Result.LastValue;
            double profitPips = Math.Abs(position.Pips);

            double trailingStartPips = (TrailingStartMultiplier * atrValue) / Symbol.PipSize;
            if (profitPips < trailingStartPips)
                return;

            double trailingDistancePrice = TrailingDistanceMultiplier * atrValue;

            double? currentSL = position.StopLoss;
            double newSL;

            if (position.TradeType == TradeType.Buy)
            {
                newSL = Symbol.Bid - trailingDistancePrice;

                if (currentSL == null || newSL > currentSL.Value)
                {
                    ModifyPosition(position, newSL, position.TakeProfit, ProtectionType.Absolute);
                }
            }
            else
            {
                newSL = Symbol.Ask + trailingDistancePrice;

                if (currentSL == null || newSL < currentSL.Value)
                {
                    ModifyPosition(position, newSL, position.TakeProfit, ProtectionType.Absolute);
                }
            }
        }

        // ==================== REPORTING ====================

        private void PrintDailySummary()
        {
            if (tradesOpenedToday == 0)
                return;

            double dailyPnL = Account.Balance - dailyStartBalance;
            double dailyPnLPercent = dailyStartBalance <= 0 ? 0 : (dailyPnL / dailyStartBalance) * 100.0;

            Print("\n╔════════════ DAILY SUMMARY ═════════════╗");
            Print($"║ Date: {lastTradeDate:yyyy-MM-dd}");
            Print($"║ Trades Opened: {tradesOpenedToday}");
            Print($"║ Start Balance: {dailyStartBalance:F2}");
            Print($"║ End Balance: {Account.Balance:F2}");
            Print($"║ Daily P/L: {dailyPnL:F2} ({dailyPnLPercent:F2}%)");
            Print($"║ Features: Trail={EnableTrailingStop}, Partial={EnablePartialTPs}");
            Print($"║           MACD={EnableMacdFilter}, RSI={EnableRsiFilter}");
            Print("╚════════════════════════════════════════╝\n");
        }
    }
}
