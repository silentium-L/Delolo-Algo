using System;
using System.Linq;
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
        
        [Parameter("Max Trades Per Day", DefaultValue = 10, MinValue = 1, MaxValue = 100)]
        public int MaxDailyTrades { get; set; }
        
        [Parameter("Max Daily Loss (%)", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 20.0)]
        public double MaxDailyLossPercent { get; set; }
        
        [Parameter("== INDICATORS ==")]
        public string IndicatorLabel { get; set; }
        
        [Parameter("RSI Period", DefaultValue = 14, MinValue = 3, MaxValue = 21)]
        public int RsiPeriod { get; set; }
        
        [Parameter("RSI Overbought", DefaultValue = 70, MinValue = 60, MaxValue = 90)]
        public double RsiOverbought { get; set; }
        
        [Parameter("RSI Oversold", DefaultValue = 30, MinValue = 10, MaxValue = 40)]
        public double RsiOversold { get; set; }
        
        [Parameter("MACD Fast", DefaultValue = 12, MinValue = 3, MaxValue = 20)]
        public int MacdFast { get; set; }
        
        [Parameter("MACD Slow", DefaultValue = 26, MinValue = 10, MaxValue = 30)]
        public int MacdSlow { get; set; }
        
        [Parameter("MACD Signal", DefaultValue = 9, MinValue = 3, MaxValue = 15)]
        public int MacdSignal { get; set; }
        
        [Parameter("ATR Period", DefaultValue = 14, MinValue = 5, MaxValue = 20)]
        public int AtrPeriod { get; set; }
        
        [Parameter("Supertrend Period", DefaultValue = 10, MinValue = 5, MaxValue = 15)]
        public int SupertrendPeriod { get; set; }
        
        [Parameter("Supertrend Multiplier", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 5.0)]
        public double SupertrendMultiplier { get; set; }
        
        [Parameter("== TRADING HOURS (UTC) ==")]
        public string SessionLabel { get; set; }
        
        [Parameter("Start Hour", DefaultValue = 7, MinValue = 0, MaxValue = 23)]
        public int StartHour { get; set; }
        
        [Parameter("End Hour", DefaultValue = 22, MinValue = 0, MaxValue = 23)]
        public int EndHour { get; set; }
        
        [Parameter("== TARGETS ==")]
        public string TargetLabel { get; set; }
        
        [Parameter("Stop Loss (ATR)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 5.0)]
        public double StopLossMultiplier { get; set; }
        
        [Parameter("Take Profit 1 (ATR)", DefaultValue = 2.0, MinValue = 1.0, MaxValue = 10.0)]
        public double TakeProfit1Multiplier { get; set; }
        
        [Parameter("Take Profit 2 (ATR)", DefaultValue = 3.0, MinValue = 1.0, MaxValue = 10.0)]
        public double TakeProfit2Multiplier { get; set; }
        
        [Parameter("Take Profit 3 (ATR)", DefaultValue = 5.0, MinValue = 1.0, MaxValue = 10.0)]
        public double TakeProfit3Multiplier { get; set; }
        
        [Parameter("TP1 Close %", DefaultValue = 30, MinValue = 0, MaxValue = 100)]
        public int Tp1ClosePercent { get; set; }
        
        [Parameter("TP2 Close %", DefaultValue = 40, MinValue = 0, MaxValue = 100)]
        public int Tp2ClosePercent { get; set; }
        
        // ==================== INDICATORS ====================
        
        private ExponentialMovingAverage ema21;
        private ExponentialMovingAverage ema50;
        private ExponentialMovingAverage ema200;
        
        private RelativeStrengthIndex rsi;
        private MacdHistogram macd;
        private AverageTrueRange atr;
        private Supertrend supertrend;
        
        // ==================== VARIABLES ====================
        
        private int tradesOpenedToday;
        private double dailyStartBalance;
        private double highestBalanceToday;
        private DateTime lastTradeDate;
        private const string BotLabel = "MultiScalper";
        
        private bool tp1Hit = false;
        private bool tp2Hit = false;
        
        // ==================== INITIALIZATION ====================
        
        protected override void OnStart()
        {
            Print("╔════════════════════════════════════════╗");
            Print("║  Multi-Indicator Scalping Bot v1.1    ║");
            Print("║  Fixed Volume Bug + Simplified        ║");
            Print("╚════════════════════════════════════════╝");
            Print($"Account Balance: {Account.Balance:F2} {Account.Asset.Name}");
            Print($"Risk per Trade: {RiskPercent}%");
            Print($"Max Trades/Day: {MaxDailyTrades}");
            Print($"Trading Hours: {StartHour:D2}:00 - {EndHour:D2}:00 UTC");
            Print($"Symbol: {SymbolName}");
            Print($"Timeframe: {TimeFrame}");
            Print("─────────────────────────────────────────");
            
            // Initialize indicators (simplified - only what we need)
            ema21 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 21);
            ema50 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 50);
            ema200 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 200);
            
            rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, RsiPeriod);
            macd = Indicators.MacdHistogram(Bars.ClosePrices, MacdFast, MacdSlow, MacdSignal);
            atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);
            supertrend = Indicators.Supertrend(SupertrendPeriod, SupertrendMultiplier);
            
            // Initialize daily tracking
            dailyStartBalance = Account.Balance;
            highestBalanceToday = Account.Balance;
            lastTradeDate = Server.Time.Date;
            tradesOpenedToday = 0;
            
            Print("✓ All indicators initialized successfully");
            Print("✓ Bot is ready to trade");
            Print("═════════════════════════════════════════");
        }
        
        // ==================== MAIN LOGIC ====================
        
        protected override void OnBar()
        {
            // Reset daily counters
            if (Server.Time.Date != lastTradeDate)
            {
                PrintDailySummary();
                
                tradesOpenedToday = 0;
                dailyStartBalance = Account.Balance;
                highestBalanceToday = Account.Balance;
                lastTradeDate = Server.Time.Date;
                tp1Hit = false;
                tp2Hit = false;
                
                Print($"\n═══ NEW TRADING DAY: {Server.Time.Date:yyyy-MM-dd} ═══");
            }
            
            // Update highest balance
            if (Account.Balance > highestBalanceToday)
                highestBalanceToday = Account.Balance;
            
            // Manage existing positions first
            ManageOpenPositions();
            
            // Check if we should look for new trades
            if (!ShouldTrade())
                return;
            
            // Check for new entry signals
            CheckForEntrySignal();
        }
        
        // ==================== TRADING CONDITIONS ====================
        
        private bool ShouldTrade()
        {
            // Check if we have open positions (only 1 at a time)
            var positions = Positions.FindAll(BotLabel, SymbolName);
            if (positions.Length > 0)
                return false;
            
            // Check daily trade limit
            if (tradesOpenedToday >= MaxDailyTrades)
                return false;
            
            // Check daily loss limit
            double dailyPnL = Account.Balance - dailyStartBalance;
            double dailyLossLimit = dailyStartBalance * (MaxDailyLossPercent / 100);
            
            if (dailyPnL < -dailyLossLimit)
            {
                Print($"⚠ Daily loss limit reached: {dailyPnL:F2}. Pausing until tomorrow.");
                return false;
            }
            
            // Check trading session
            int currentHour = Server.Time.Hour;
            bool inSession = false;
            
            if (StartHour < EndHour)
                inSession = currentHour >= StartHour && currentHour < EndHour;
            else
                inSession = currentHour >= StartHour || currentHour < EndHour;
            
            return inSession;
        }
        
        // ==================== ENTRY SIGNAL (SIMPLIFIED) ====================
        
        private void CheckForEntrySignal()
        {
            double price = Bars.ClosePrices.LastValue;
            double atrValue = atr.Result.LastValue;
            
            // Simplified EMA check
            double ema21Val = ema21.Result.LastValue;
            double ema50Val = ema50.Result.LastValue;
            double ema200Val = ema200.Result.LastValue;
            
            // RSI
            double rsiValue = rsi.Result.LastValue;
            
            // MACD
            double macdValue = macd.Histogram.LastValue;
            double macdSignalValue = macd.Signal.LastValue;
            
            // Supertrend
            var supertrendUp = supertrend.UpTrend.LastValue;
            var supertrendDown = supertrend.DownTrend.LastValue;
            bool isBullishSupertrend = !double.IsNaN(supertrendUp);
            bool isBearishSupertrend = !double.IsNaN(supertrendDown);
            
            // ========== LONG SIGNAL (SIMPLIFIED - 4 CONDITIONS) ==========
            bool longTrend = price > ema200Val;                 // 1. Major trend up
            bool longMomentum = ema21Val > ema50Val;           // 2. Short-term momentum
            bool longSupertrend = isBullishSupertrend;         // 3. Supertrend confirmation
            bool longRsi = rsiValue > 40 && rsiValue < 80;     // 4. RSI not extreme
            
            if (longTrend && longMomentum && longSupertrend && longRsi)
            {
                ExecuteTrade(TradeType.Buy, atrValue);
                return;
            }
            
            // ========== SHORT SIGNAL (SIMPLIFIED - 4 CONDITIONS) ==========
            bool shortTrend = price < ema200Val;               // 1. Major trend down
            bool shortMomentum = ema21Val < ema50Val;         // 2. Short-term momentum
            bool shortSupertrend = isBearishSupertrend;       // 3. Supertrend confirmation
            bool shortRsi = rsiValue > 20 && rsiValue < 60;   // 4. RSI not extreme
            
            if (shortTrend && shortMomentum && shortSupertrend && shortRsi)
            {
                ExecuteTrade(TradeType.Sell, atrValue);
                return;
            }
        }
        
        // ==================== EXECUTE TRADE (FIXED!) ====================
        
        private void ExecuteTrade(TradeType tradeType, double atrValue)
        {
            double riskAmount = Account.Balance * (RiskPercent / 100);
            double stopLossPips = (StopLossMultiplier * atrValue) / Symbol.PipSize;
            
            double volumeInLots = CalculatePositionSize(riskAmount, stopLossPips);
            
            if (volumeInLots < Symbol.VolumeInUnitsMin / 1000)
            {
                Print($"⚠ Position size too small: {volumeInLots:F2} lots. Skipping.");
                return;
            }
            
            // Berechne TP (nutze TP3 als finalen Exit)
            double takeProfitPips = (TakeProfit3Multiplier * atrValue) / Symbol.PipSize;
            
            //  FIX: Korrekte Volume-Konvertierung (das war der Bug!)
            double volumeInUnits = riskAmount / (stopLossPips * Symbol.PipValue);
            volumeInLots = volumeInUnits / 1000;

            
            // Berechne Preise für Display
            double stopLossPrice;
            double takeProfitPrice;
            
            if (tradeType == TradeType.Buy)
            {
                stopLossPrice = Symbol.Bid - (StopLossMultiplier * atrValue);
                takeProfitPrice = Symbol.Ask + (TakeProfit3Multiplier * atrValue);
            }
            else
            {
                stopLossPrice = Symbol.Ask + (StopLossMultiplier * atrValue);
                takeProfitPrice = Symbol.Bid - (TakeProfit3Multiplier * atrValue);
            }
            
            // Execute Order mit korrektem Volume
            var result = ExecuteMarketOrder(
                tradeType, 
                SymbolName, 
                volumeInUnits,  // ✅ Direkt Units, keine weitere Konvertierung!
                BotLabel, 
                stopLossPips,
                takeProfitPips
            );
            
            if (result.IsSuccessful)
            {
                tradesOpenedToday++;
                tp1Hit = false;
                tp2Hit = false;
                
                Print($"\n┌─── TRADE OPENED #{tradesOpenedToday} ───");
                Print($"│ Type: {tradeType}");
                Print($"│ Volume: {volumeInLots:F2} lots ({volumeInUnits} units)");
                Print($"│ Entry: {result.Position.EntryPrice:F5}");
                Print($"│ Stop Loss: {stopLossPrice:F5} ({stopLossPips:F1} pips)");
                Print($"│ Take Profit: {takeProfitPrice:F5} ({takeProfitPips:F1} pips)");
                Print($"│ Risk:Reward = 1:{(takeProfitPips/stopLossPips):F2}");
                Print($"│ Risk: {riskAmount:F2} ({RiskPercent}%)");
                Print($"│ Time: {Server.Time:HH:mm:ss}");
                Print($"└────────────────────────────────────");
            }
            else
            {
                Print($"✗ Trade failed: {result.Error}");
                Print($"  Attempted volume: {volumeInLots:F2} lots ({volumeInUnits} units)");
            }
        }
        
        // ==================== POSITION SIZING (FIXED!) ====================
        
        private double CalculatePositionSize(double riskAmount, double stopLossPips)
        {
            // Basis-Berechnung basierend auf Risk
            double volumeInUnits = riskAmount / (stopLossPips * Symbol.PipValue);
            double volumeInLots = volumeInUnits / 1000;
            
            // Linear scaling: 100€ = 0.03 lots, +0.01 per 33€
            double baseBalance = 100;
            double currentBalance = Account.Balance;
            
            if (currentBalance >= baseBalance)
            {
                double additionalCapital = currentBalance - baseBalance;
                double additionalLots = (additionalCapital / 33) * 0.01;
                double baseLots = 0.03;
                
                volumeInLots = Math.Max(volumeInLots, baseLots + additionalLots);
            }
            
            // Runde auf 0.01 Lots (Micro Lot)
            volumeInLots = Math.Round(volumeInLots, 2);
            
            // Stelle sicher es ist innerhalb der Broker-Limits
            double minVolumeLots = Symbol.VolumeInUnitsMin / 1000.0;
            double maxVolumeLots = Symbol.VolumeInUnitsMax / 1000.0;
            
            volumeInLots = Math.Max(minVolumeLots, Math.Min(maxVolumeLots, volumeInLots));
            
            Print($"📊 Position Size: {volumeInLots:F3} lots (Risk: {riskAmount:F2}, SL: {stopLossPips:F1} pips)");
            
            return volumeInLots;
        }
        
        // ==================== MANAGE POSITIONS ====================
        
        private void ManageOpenPositions()
        {
            var positions = Positions.FindAll(BotLabel, SymbolName);
            
            foreach (var position in positions)
            {
                CheckPartialClose(position);
            }
        }
        
        private void CheckPartialClose(Position position)
        {
            double atrValue = atr.Result.LastValue;
            double profitPips = Math.Abs(position.Pips);
            
            // TP1
            double tp1Pips = (TakeProfit1Multiplier * atrValue) / Symbol.PipSize;
            if (!tp1Hit && profitPips >= tp1Pips && position.VolumeInUnits >= Symbol.VolumeInUnitsMin * 2)
            {
                double closeVolume = position.VolumeInUnits * (Tp1ClosePercent / 100.0);
                closeVolume = Math.Max(Symbol.VolumeInUnitsMin, Math.Round(closeVolume / Symbol.VolumeInUnitsStep) * Symbol.VolumeInUnitsStep);
                
                if (closeVolume < position.VolumeInUnits)
                {
                    var closeResult = ClosePosition(position, closeVolume);
                    if (closeResult.IsSuccessful)
                    {
                        tp1Hit = true;
                        Print($"✓ TP1 HIT: Closed {Tp1ClosePercent}% at +{profitPips:F1} pips");
                    }
                }
            }
            
            // TP2
            double tp2Pips = (TakeProfit2Multiplier * atrValue) / Symbol.PipSize;
            if (!tp2Hit && profitPips >= tp2Pips && position.VolumeInUnits >= Symbol.VolumeInUnitsMin * 2)
            {
                double closeVolume = position.VolumeInUnits * (Tp2ClosePercent / 100.0);
                closeVolume = Math.Max(Symbol.VolumeInUnitsMin, Math.Round(closeVolume / Symbol.VolumeInUnitsStep) * Symbol.VolumeInUnitsStep);
                
                if (closeVolume < position.VolumeInUnits)
                {
                    var closeResult = ClosePosition(position, closeVolume);
                    if (closeResult.IsSuccessful)
                    {
                        tp2Hit = true;
                        Print($"✓ TP2 HIT: Closed {Tp2ClosePercent}% at +{profitPips:F1} pips");
                    }
                }
            }
            
            // TP3 wird automatisch vom Broker geschlossen (ist im Order als TP gesetzt)
        }
        
        // ==================== REPORTING ====================
        
        private void PrintDailySummary()
        {
            if (tradesOpenedToday == 0)
                return;
            
            double dailyPnL = Account.Balance - dailyStartBalance;
            double dailyPnLPercent = (dailyPnL / dailyStartBalance) * 100;
            
            Print("\n╔════════════ DAILY SUMMARY ═════════════╗");
            Print($"║ Date: {lastTradeDate:yyyy-MM-dd}");
            Print($"║ Trades Opened: {tradesOpenedToday}");
            Print($"║ Start Balance: {dailyStartBalance:F2}");
            Print($"║ End Balance: {Account.Balance:F2}");
            Print($"║ Daily P/L: {dailyPnL:F2} ({dailyPnLPercent:F2}%)");
            Print($"╚════════════════════════════════════════╝\n");
        }
        
        protected override void OnStop()
        {
            PrintDailySummary();
            
            Print("\n╔════════════════════════════════════════╗");
            Print("║         BOT STOPPED                    ║");
            Print("╚════════════════════════════════════════╝");
            Print($"Final Balance: {Account.Balance:F2}");
            Print($"Total Trades Today: {tradesOpenedToday}");
            Print("═════════════════════════════════════════\n");
        }
    }
}
