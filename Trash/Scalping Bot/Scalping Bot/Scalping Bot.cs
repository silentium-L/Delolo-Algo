// =============================================================
//  EURUSD FVG + Volume Spike + EMA Trend cBot  v2.0
//  Platform : cTrader (cAlgo API)
//  Timeframe: M15 (HTF: H1 Trend, Daily Struktur)
//  Changes v2.0:
//    [1] Daily High/Low Struktur-Filter (HTF-Kontext)
//    [2] Tick-Volume Hinweis + relativer Spike-Filter verbessert
//    [3] CSV Trade-Journal (Print-basiert, strukturiert)
//    [4] Max Open Positions Parameter (statt hartem Limit=1)
// =============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.FullAccess)]
    public class FVGVolumeEMABot : Robot
    {
        // ── ENTRY PARAMETER ──────────────────────────────────────
        [Parameter("EMA Fast Period", Group = "Entry - EMA Trend", DefaultValue = 50)]
        public int EmaFastPeriod { get; set; }

        [Parameter("EMA Slow Period", Group = "Entry - EMA Trend", DefaultValue = 200)]
        public int EmaSlowPeriod { get; set; }

        [Parameter("Volume Multiplier (%)", Group = "Entry - Signal Candle", DefaultValue = 150)]
        public double VolumeMultiplierPercent { get; set; }

        [Parameter("Volume Average Period", Group = "Entry - Signal Candle", DefaultValue = 20)]
        public int VolumeAvgPeriod { get; set; }

        // [2] Zweiter Volume-Schwellwert: Spike muss AUCH im H1-Kontext erhöht sein
        [Parameter("H1 Volume Confirmation (%)", Group = "Entry - Signal Candle", DefaultValue = 120)]
        public double H1VolumeConfirmPercent { get; set; }

        [Parameter("Min Body Ratio (%)", Group = "Entry - Signal Candle", DefaultValue = 60)]
        public double MinBodyRatioPercent { get; set; }

        [Parameter("FVG Expiry Bars (M15)", Group = "Entry - FVG", DefaultValue = 10)]
        public int FvgExpiryBars { get; set; }

        // ── DAILY STRUKTUR FILTER ─────────────────────────────────
        // [1] Daily High/Low Struktur
        [Parameter("Daily Structure Filter", Group = "Daily Structure", DefaultValue = true)]
        public bool DailyStructureFilter { get; set; }

        [Parameter("Daily Structure Buffer (Pips)", Group = "Daily Structure", DefaultValue = 5.0)]
        public double DailyStructureBufferPips { get; set; }

        [Parameter("Daily Structure Lookback (Days)", Group = "Daily Structure", DefaultValue = 5)]
        public int DailyStructureLookback { get; set; }

        // ── STOP LOSS ─────────────────────────────────────────────
        [Parameter("ATR Period", Group = "Stop Loss", DefaultValue = 14)]
        public int AtrPeriod { get; set; }

        [Parameter("ATR Buffer Multiplier", Group = "Stop Loss", DefaultValue = 0.25)]
        public double AtrBufferMultiplier { get; set; }

        // ── TAKE PROFIT ───────────────────────────────────────────
        [Parameter("TP1 RR Multiplier", Group = "Take Profit", DefaultValue = 1.0)]
        public double Tp1RR { get; set; }

        [Parameter("TP1 Close Percent (%)", Group = "Take Profit", DefaultValue = 33)]
        public double Tp1ClosePercent { get; set; }

        [Parameter("TP2 RR Multiplier", Group = "Take Profit", DefaultValue = 2.0)]
        public double Tp2RR { get; set; }

        [Parameter("TP2 Close Percent (%)", Group = "Take Profit", DefaultValue = 33)]
        public double Tp2ClosePercent { get; set; }

        [Parameter("TP3 RR Multiplier", Group = "Take Profit", DefaultValue = 3.0)]
        public double Tp3RR { get; set; }

        [Parameter("TP3 Close Percent (%)", Group = "Take Profit", DefaultValue = 34)]
        public double Tp3ClosePercent { get; set; }

        [Parameter("Break Even Trigger", Group = "Take Profit", DefaultValue = BreakEvenTriggerOption.TP1)]
        public BreakEvenTriggerOption BreakEvenTrigger { get; set; }

        [Parameter("Trailing Stop After TP2", Group = "Take Profit", DefaultValue = true)]
        public bool TrailingAfterLastTp { get; set; }

        // ── RISK & SIZING ─────────────────────────────────────────
        [Parameter("Risk Per Trade (%)", Group = "Risk Management", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5.0)]
        public double RiskPerTradePercent { get; set; }

        [Parameter("Min Lot Size", Group = "Risk Management", DefaultValue = 0.01)]
        public double MinLotSize { get; set; }

        [Parameter("Max Lot Size", Group = "Risk Management", DefaultValue = 5.0)]
        public double MaxLotSize { get; set; }

        [Parameter("Commission Per Lot (EUR)", Group = "Risk Management", DefaultValue = 5.20)]
        public double CommissionPerLot { get; set; }

        // [4] Max Open Positions konfigurierbar
        [Parameter("Max Open Positions", Group = "Risk Management", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
        public int MaxOpenPositions { get; set; }

        // ── CIRCUIT BREAKER ───────────────────────────────────────
        [Parameter("Max Daily Loss (%)", Group = "Circuit Breaker", DefaultValue = 2.0)]
        public double MaxDailyLossPercent { get; set; }

        [Parameter("Max Drawdown (%)", Group = "Circuit Breaker", DefaultValue = 8.0)]
        public double MaxDrawdownPercent { get; set; }

        [Parameter("Max Consecutive Losses", Group = "Circuit Breaker", DefaultValue = 3)]
        public int MaxConsecutiveLosses { get; set; }

        // ── SESSION ───────────────────────────────────────────────
        [Parameter("London Session Start (UTC Hour)", Group = "Session Filter", DefaultValue = 7)]
        public int LondonStartHour { get; set; }

        [Parameter("London Session End (UTC Hour)", Group = "Session Filter", DefaultValue = 11)]
        public int LondonEndHour { get; set; }

        [Parameter("NY Session Start (UTC Hour)", Group = "Session Filter", DefaultValue = 13)]
        public int NyStartHour { get; set; }

        [Parameter("NY Session End (UTC Hour)", Group = "Session Filter", DefaultValue = 16)]
        public int NyEndHour { get; set; }

        [Parameter("No Trades Friday After (UTC Hour)", Group = "Session Filter", DefaultValue = 19)]
        public int FridayCutoffHour { get; set; }

        // ── SWAP PROTECTION ───────────────────────────────────────
        [Parameter("Negative Swap Protection", Group = "Swap Protection", DefaultValue = true)]
        public bool NegativeSwapProtect { get; set; }

        [Parameter("Swap Close Time (UTC Hour)", Group = "Swap Protection", DefaultValue = 21)]
        public int SwapCloseHour { get; set; }

        [Parameter("Swap Close Time (UTC Minute)", Group = "Swap Protection", DefaultValue = 45)]
        public int SwapCloseMinute { get; set; }

        // ── QUALITY FILTER ────────────────────────────────────────
        [Parameter("Max Spread (Pips)", Group = "Quality Filter", DefaultValue = 1.0)]
        public double MaxSpreadPips { get; set; }

        // ── TRADE JOURNAL ─────────────────────────────────────────
        // [3] CSV Trade-Journal
        [Parameter("Enable Trade Journal", Group = "Trade Journal", DefaultValue = true)]
        public bool EnableTradeJournal { get; set; }

        // ── PRIVATE FIELDS ────────────────────────────────────────
        private Bars _h1Bars;
        private Bars _dailyBars;
        private MovingAverage _emaFast;
        private MovingAverage _emaSlow;
        private AverageTrueRange _atr;

        private readonly List<FvgZone> _activeFvgs = new List<FvgZone>();
        private readonly HashSet<long> _tp1ClosedPositions = new HashSet<long>();
        private readonly HashSet<long> _tp2ClosedPositions = new HashSet<long>();
        private readonly HashSet<long> _tp3ClosedPositions = new HashSet<long>();
        private readonly HashSet<long> _breakEvenAppliedPositions = new HashSet<long>();
        private readonly HashSet<long> _trailingActivePositions = new HashSet<long>();

        // [3] Trade-Journal: Entry-Daten zwischenspeichern
        private readonly Dictionary<long, TradeJournalEntry> _journalEntries = new Dictionary<long, TradeJournalEntry>();

        private double _dailyStartEquity;
        private double _peakEquity;
        private int _consecutiveLosses;
        private bool _botHalted;
        private DateTime _lastDayChecked;

        // Swap-Check Flag
        private bool _swapCheckedThisHour;
        private int _lastSwapCheckHour = -1;

        private const string BotLabel = "FVGVolumeEMABot";

        // ── LIFECYCLE ─────────────────────────────────────────────
        protected override void OnStart()
        {
            ValidateParameters();

            _h1Bars    = MarketData.GetBars(TimeFrame.Hour);
            _dailyBars = MarketData.GetBars(TimeFrame.Daily);  // [1]
            _emaFast   = Indicators.MovingAverage(_h1Bars.ClosePrices, EmaFastPeriod, MovingAverageType.Exponential);
            _emaSlow   = Indicators.MovingAverage(_h1Bars.ClosePrices, EmaSlowPeriod, MovingAverageType.Exponential);
            _atr       = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Simple);

            _dailyStartEquity = Account.Equity;
            _peakEquity       = Account.Equity;
            _lastDayChecked   = Server.Time.Date;

            Positions.Closed += OnPositionClosed;

            // [3] Journal-Header
            if (EnableTradeJournal)
                PrintJournal("JOURNAL_HEADER",
                    "Timestamp;PositionId;Symbol;Direction;Lots;EntryPrice;StopLoss;SLPips;" +
                    "FvgHigh;FvgLow;FvgSizePips;H1Bias;DailyHigh;DailyLow;Session;" +
                    "Spread;ATR;ExitReason;GrossProfit;NetProfit;RR_Realized");

            Print("FVGVolumeEMABot v2.0 started. Risk: {0}% | MaxPositions: {1}", RiskPerTradePercent, MaxOpenPositions);
        }

        protected override void OnBar()
        {
            ResetDailyEquityIfNewDay();

            if (_botHalted)
            {
                Print("Bot halted by circuit breaker.");
                return;
            }

            if (NegativeSwapProtect)
                CheckSwapProtection();

            ManageOpenPositions();
            ExpireFvgZones();
            DetectFvgFromSignalCandle();
            CheckFvgEntries();
        }

        protected override void OnTick()
        {
            if (_botHalted) return;
            ManageOpenPositions();
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
        }

        // ── CIRCUIT BREAKER ───────────────────────────────────────
        private void ResetDailyEquityIfNewDay()
        {
            var today = Server.Time.Date;
            if (today != _lastDayChecked)
            {
                _dailyStartEquity   = Account.Equity;
                _lastDayChecked     = today;
                _consecutiveLosses  = 0;
                _botHalted          = false;
                _swapCheckedThisHour = false;
                _lastSwapCheckHour  = -1;
                Print("New day - Daily equity reset to {0}. Circuit breaker reset.", _dailyStartEquity);
            }

            _peakEquity = Math.Max(_peakEquity, Account.Equity);

            var dailyLossPct = (_dailyStartEquity - Account.Equity) / _dailyStartEquity * 100.0;
            var drawdownPct  = (_peakEquity - Account.Equity) / _peakEquity * 100.0;

            if (dailyLossPct >= MaxDailyLossPercent)
            {
                Print("CIRCUIT BREAKER: Daily loss {0:F2}% reached.", MaxDailyLossPercent);
                _botHalted = true;
            }
            if (drawdownPct >= MaxDrawdownPercent)
            {
                Print("CIRCUIT BREAKER: Max drawdown {0:F2}% reached.", MaxDrawdownPercent);
                _botHalted = true;
            }
            if (_consecutiveLosses >= MaxConsecutiveLosses)
            {
                Print("CIRCUIT BREAKER: {0} consecutive losses.", MaxConsecutiveLosses);
                _botHalted = true;
            }
        }

        // ── SESSION GUARD ─────────────────────────────────────────
        private bool IsWithinTradingSession()
        {
            var now  = Server.Time;
            var hour = now.Hour;

            if (now.DayOfWeek == DayOfWeek.Friday && hour >= FridayCutoffHour)
                return false;

            bool inLondon = hour >= LondonStartHour && hour < LondonEndHour;
            bool inNY     = hour >= NyStartHour     && hour < NyEndHour;

            return inLondon || inNY;
        }

        private string GetCurrentSession()
        {
            var hour = Server.Time.Hour;
            if (hour >= LondonStartHour && hour < LondonEndHour) return "London";
            if (hour >= NyStartHour     && hour < NyEndHour)     return "NewYork";
            return "OffSession";
        }

        // ── SWAP PROTECTION ───────────────────────────────────────
        private void CheckSwapProtection()
        {
            var now = Server.Time;
            if (now.Hour != SwapCloseHour || now.Minute < SwapCloseMinute) return;
            if (_lastSwapCheckHour == now.Hour && _swapCheckedThisHour) return;

            _swapCheckedThisHour = true;
            _lastSwapCheckHour   = now.Hour;

            foreach (var position in Positions.Where(p => p.Label == BotLabel))
            {
                double swapRate = position.TradeType == TradeType.Buy
                    ? Symbol.SwapLong : Symbol.SwapShort;

                if (swapRate < 0)
                {
                    Print("Swap protection: Closing {0} position {1} (swap: {2:F5})",
                        position.TradeType, position.Id, swapRate);
                    ClosePosition(position);
                }
            }
        }

        // ── EMA TREND BIAS ────────────────────────────────────────
        private TrendBias GetH1TrendBias()
        {
            int lastIdx = _h1Bars.Count - 2;
            if (lastIdx < EmaSlowPeriod) return TrendBias.Neutral;

            double price   = _h1Bars.ClosePrices[lastIdx];
            double emaFast = _emaFast.Result[lastIdx];
            double emaSlow = _emaSlow.Result[lastIdx];

            if (price > emaFast && price > emaSlow && emaFast > emaSlow) return TrendBias.Bullish;
            if (price < emaFast && price < emaSlow && emaFast < emaSlow) return TrendBias.Bearish;
            return TrendBias.Neutral;
        }

        // ── [1] DAILY STRUKTUR FILTER ─────────────────────────────
        /// <summary>
        /// Gibt das relevante Daily High und Low der letzten N Tage zurueck.
        /// Longs werden blockiert wenn Preis zu nah an Daily High ist (Widerstand).
        /// Shorts werden blockiert wenn Preis zu nah an Daily Low ist (Support).
        /// </summary>
        private bool IsDailyStructureAllowed(TradeType direction, double currentPrice)
        {
            if (!DailyStructureFilter) return true;

            int count = _dailyBars.Count;
            if (count < DailyStructureLookback + 2) return true;

            double dailyHigh = double.MinValue;
            double dailyLow  = double.MaxValue;

            // Letzte N geschlossene Daily Bars (count-2 bis count-2-lookback)
            int startIdx = count - 2 - DailyStructureLookback;
            int endIdx   = count - 2;
            for (int i = startIdx; i <= endIdx; i++)
            {
                if (i < 0) continue;
                dailyHigh = Math.Max(dailyHigh, _dailyBars.HighPrices[i]);
                dailyLow  = Math.Min(dailyLow,  _dailyBars.LowPrices[i]);
            }

            double bufferPrice = DailyStructureBufferPips * Symbol.PipSize;

            if (direction == TradeType.Buy)
            {
                // Long blockieren wenn Preis zu nah an Daily High (innerhalb Buffer)
                if (dailyHigh - currentPrice < bufferPrice)
                {
                    Print("Daily Structure: Long blocked. Price {0:F5} within {1} pips of Daily High {2:F5}",
                        currentPrice, DailyStructureBufferPips, dailyHigh);
                    return false;
                }
            }
            else
            {
                // Short blockieren wenn Preis zu nah an Daily Low
                if (currentPrice - dailyLow < bufferPrice)
                {
                    Print("Daily Structure: Short blocked. Price {0:F5} within {1} pips of Daily Low {2:F5}",
                        currentPrice, DailyStructureBufferPips, dailyLow);
                    return false;
                }
            }

            return true;
        }

        private (double High, double Low) GetDailyStructureLevels()
        {
            int count = _dailyBars.Count;
            if (count < DailyStructureLookback + 2) return (0, 0);

            double dailyHigh = double.MinValue;
            double dailyLow  = double.MaxValue;
            int startIdx = count - 2 - DailyStructureLookback;
            int endIdx   = count - 2;
            for (int i = startIdx; i <= endIdx; i++)
            {
                if (i < 0) continue;
                dailyHigh = Math.Max(dailyHigh, _dailyBars.HighPrices[i]);
                dailyLow  = Math.Min(dailyLow,  _dailyBars.LowPrices[i]);
            }
            return (dailyHigh, dailyLow);
        }

        // ── [2] H1 VOLUME CONFIRMATION ────────────────────────────
        /// <summary>
        /// Prueft ob die aktuelle H1-Bar ebenfalls einen erhoehten Tick-Volume-Spike zeigt.
        /// Kompensiert die Limitation von Tick-Volume als Proxy fuer echtes Volumen:
        /// Wenn beide Timeframes (M15 + H1) erhoehte Aktivitaet zeigen, steigt die
        /// Wahrscheinlichkeit eines echten institutionellen Impulses.
        /// </summary>
        private bool IsH1VolumeConfirmed()
        {
            if (H1VolumeConfirmPercent <= 0) return true;

            int h1LastIdx = _h1Bars.Count - 2;
            if (h1LastIdx < VolumeAvgPeriod) return true;

            double h1AvgVol = 0;
            int count = 0;
            for (int i = h1LastIdx - VolumeAvgPeriod; i < h1LastIdx; i++)
            {
                if (i < 0) continue;
                h1AvgVol += _h1Bars.TickVolumes[i];
                count++;
            }
            if (count == 0) return true;
            h1AvgVol /= count;

            double h1CurrentVol   = _h1Bars.TickVolumes[h1LastIdx];
            double h1Threshold    = h1AvgVol * (H1VolumeConfirmPercent / 100.0);

            bool confirmed = h1CurrentVol >= h1Threshold;
            if (!confirmed)
                Print("H1 Volume not confirmed: {0:F0} < {1:F0} (threshold)", h1CurrentVol, h1Threshold);

            return confirmed;
        }

        // ── FVG DETECTION ─────────────────────────────────────────
        private void DetectFvgFromSignalCandle()
        {
            int count = Bars.Count;
            if (count < 5) return;

            // 3-Bar-Muster: alle Bars geschlossen
            int candle3Idx = count - 2; // nach Signal-Kerze
            int signalIdx  = count - 3; // Signal-Kerze
            int candle1Idx = count - 4; // vor Signal-Kerze

            var signalBar = Bars[signalIdx];
            var bias = GetH1TrendBias();
            if (bias == TrendBias.Neutral) return;

            // M15 Volume Spike
            double avgVolume = GetAverageVolume(signalIdx, VolumeAvgPeriod);
            if (avgVolume <= 0) return;
            if (Bars.TickVolumes[signalIdx] < avgVolume * (VolumeMultiplierPercent / 100.0)) return;

            // [2] H1 Volume Bestätigung
            if (!IsH1VolumeConfirmed()) return;

            // Body ratio
            double totalRange = Math.Abs(signalBar.High - signalBar.Low);
            if (totalRange < Symbol.PipSize) return;
            double bodyRatio = Math.Abs(signalBar.Close - signalBar.Open) / totalRange;
            if (bodyRatio < MinBodyRatioPercent / 100.0) return;

            bool isBullishCandle = signalBar.Close > signalBar.Open;
            if (bias == TrendBias.Bullish && !isBullishCandle) return;
            if (bias == TrendBias.Bearish && isBullishCandle) return;

            var candle1 = Bars[candle1Idx];
            var candle3 = Bars[candle3Idx];

            double fvgHigh, fvgLow;
            if (isBullishCandle)
            {
                fvgLow  = candle1.High;
                fvgHigh = candle3.Low;
                if (fvgHigh <= fvgLow) return;
            }
            else
            {
                fvgHigh = candle1.Low;
                fvgLow  = candle3.High;
                if (fvgLow >= fvgHigh) return;
            }

            // Duplikat-Check
            bool alreadyExists = _activeFvgs.Any(f =>
                Math.Abs(f.FvgHigh - fvgHigh) < Symbol.PipSize &&
                Math.Abs(f.FvgLow  - fvgLow)  < Symbol.PipSize);
            if (alreadyExists) return;

            var fvg = new FvgZone
            {
                FvgHigh          = fvgHigh,
                FvgLow           = fvgLow,
                Direction        = isBullishCandle ? TradeType.Buy : TradeType.Sell,
                CreatedAtBarIndex = count - 1,
                IsActive         = true,
                H1Bias           = bias.ToString(),
                SignalVolume     = Bars.TickVolumes[signalIdx],
                AvgVolume        = avgVolume
            };

            _activeFvgs.Add(fvg);
            Print("FVG detected: {0} | High={1:F5} Low={2:F5} | VolSpike={3:F0}/{4:F0}",
                fvg.Direction, fvgHigh, fvgLow, fvg.SignalVolume, avgVolume);
        }

        private double GetAverageVolume(int endIndex, int period)
        {
            double sum = 0; int count = 0;
            for (int i = endIndex - period; i < endIndex; i++)
            {
                if (i < 0) continue;
                sum += Bars.TickVolumes[i]; count++;
            }
            return count > 0 ? sum / count : 0;
        }

        // ── FVG EXPIRY ────────────────────────────────────────────
        private void ExpireFvgZones()
        {
            int currentBar = Bars.Count - 1;
            foreach (var fvg in _activeFvgs.Where(f => f.IsActive))
            {
                if (currentBar - fvg.CreatedAtBarIndex > FvgExpiryBars)
                {
                    fvg.IsActive = false;
                    Print("FVG expired at bar {0}", currentBar);
                }
            }
            _activeFvgs.RemoveAll(f => !f.IsActive);
        }

        // ── ENTRY LOGIC ───────────────────────────────────────────
        private void CheckFvgEntries()
        {
            if (!IsWithinTradingSession()) return;

            // [4] Konfigurierbare Max-Positions statt hartem Limit=1
            if (Positions.Count(p => p.Label == BotLabel) >= MaxOpenPositions) return;

            double currentSpread = Symbol.Spread / Symbol.PipSize;
            if (currentSpread > MaxSpreadPips)
            {
                Print("Spread too high: {0:F1} pips", currentSpread);
                return;
            }

            double currentPrice = Symbol.Bid;
            var bias = GetH1TrendBias();
            if (bias == TrendBias.Neutral) return;

            foreach (var fvg in _activeFvgs.Where(f => f.IsActive))
            {
                if (currentPrice < fvg.FvgLow || currentPrice > fvg.FvgHigh) continue;

                int lastIdx  = Bars.Count - 2;
                var lastBar  = Bars[lastIdx];
                bool closeInsideFvg = lastBar.Close >= fvg.FvgLow && lastBar.Close <= fvg.FvgHigh;
                if (!closeInsideFvg) continue;

                bool confirmBull = fvg.Direction == TradeType.Buy  && lastBar.Close > lastBar.Open;
                bool confirmBear = fvg.Direction == TradeType.Sell && lastBar.Close < lastBar.Open;
                if (!confirmBull && !confirmBear) continue;

                if (fvg.Direction == TradeType.Buy  && bias != TrendBias.Bullish) continue;
                if (fvg.Direction == TradeType.Sell && bias != TrendBias.Bearish) continue;

                // Invalidierung
                if (fvg.Direction == TradeType.Buy  && lastBar.Close < fvg.FvgLow)  { fvg.IsActive = false; continue; }
                if (fvg.Direction == TradeType.Sell && lastBar.Close > fvg.FvgHigh) { fvg.IsActive = false; continue; }

                // [1] Daily Struktur prüfen
                if (!IsDailyStructureAllowed(fvg.Direction, currentPrice)) continue;

                double atrValue = _atr.Result.LastValue;
                double slPrice  = CalculateStopLoss(fvg, atrValue);
                double slPips   = Math.Abs(currentPrice - slPrice) / Symbol.PipSize;
                if (slPips < 1.0) continue;

                double lots = CalculateLotSize(slPips);
                if (lots < MinLotSize) continue;

                ExecuteEntry(fvg.Direction, lots, slPrice, slPips, fvg);
                fvg.IsActive = false;

                // Nach einem Entry prüfen ob Max-Positions erreicht
                if (Positions.Count(p => p.Label == BotLabel) >= MaxOpenPositions) break;
            }
        }

        private double CalculateStopLoss(FvgZone fvg, double atrValue)
        {
            double buffer = atrValue * AtrBufferMultiplier;
            return fvg.Direction == TradeType.Buy
                ? fvg.FvgLow - buffer
                : fvg.FvgHigh + buffer;
        }

        private double CalculateLotSize(double slPips)
        {
            double riskBudget           = Account.Equity * (RiskPerTradePercent / 100.0);
            double riskAfterCommission  = riskBudget - CommissionPerLot;
            if (riskAfterCommission <= 0) return 0;

            double volumeInUnits = riskAfterCommission / (slPips * Symbol.PipValue);
            volumeInUnits = Symbol.NormalizeVolumeInUnits(volumeInUnits);

            double lots = Symbol.VolumeInUnitsToQuantity(volumeInUnits);
            return Math.Max(MinLotSize, Math.Min(MaxLotSize, lots));
        }

        private void ExecuteEntry(TradeType direction, double lots, double slPrice, double slPips, FvgZone fvg)
        {
            double volumeInUnits = Symbol.QuantityToVolumeInUnits(lots);
            var result = ExecuteMarketOrder(direction, Symbol.Name, volumeInUnits, BotLabel, slPips, null);

            if (result.IsSuccessful)
            {
                var (dHigh, dLow) = GetDailyStructureLevels();
                double fvgSizePips = (fvg.FvgHigh - fvg.FvgLow) / Symbol.PipSize;
                double spreadPips  = Symbol.Spread / Symbol.PipSize;

                Print("Entry executed: {0} {1} lots | SL={2:F5} ({3:F1} pips) | FVG={4:F5}-{5:F5}",
                    direction, lots, slPrice, slPips, fvg.FvgLow, fvg.FvgHigh);

                // [3] Journal-Entry zwischenspeichern
                if (EnableTradeJournal)
                {
                    _journalEntries[result.Position.Id] = new TradeJournalEntry
                    {
                        Timestamp    = Server.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                        PositionId   = result.Position.Id,
                        Direction    = direction.ToString(),
                        Lots         = lots,
                        EntryPrice   = result.Position.EntryPrice,
                        StopLoss     = slPrice,
                        SLPips       = slPips,
                        FvgHigh      = fvg.FvgHigh,
                        FvgLow       = fvg.FvgLow,
                        FvgSizePips  = fvgSizePips,
                        H1Bias       = fvg.H1Bias,
                        DailyHigh    = dHigh,
                        DailyLow     = dLow,
                        Session      = GetCurrentSession(),
                        Spread       = spreadPips,
                        ATR          = _atr.Result.LastValue
                    };
                }
            }
            else
            {
                Print("Entry failed: {0}", result.Error);
            }
        }

        // ── POSITION MANAGEMENT ───────────────────────────────────
        private void ManageOpenPositions()
        {
            foreach (var position in Positions.Where(p => p.Label == BotLabel).ToList())
            {
                double entryPrice = position.EntryPrice;
                double slDistance = Math.Abs(entryPrice - (position.StopLoss ?? entryPrice));
                if (slDistance <= 0) continue;

                bool   isBuy         = position.TradeType == TradeType.Buy;
                double currentPrice  = isBuy ? Symbol.Bid : Symbol.Ask;

                // TP1
                if (!_tp1ClosedPositions.Contains(position.Id))
                {
                    double tp1Price = isBuy
                        ? entryPrice + slDistance * Tp1RR
                        : entryPrice - slDistance * Tp1RR;

                    if (isBuy ? currentPrice >= tp1Price : currentPrice <= tp1Price)
                    {
                        _tp1ClosedPositions.Add(position.Id);
                        PartialClose(position, Tp1ClosePercent / 100.0);
                        Print("TP1 hit for position {0}", position.Id);
                        if (BreakEvenTrigger == BreakEvenTriggerOption.TP1) ApplyBreakEven(position);
                    }
                }
                // TP2
                else if (!_tp2ClosedPositions.Contains(position.Id))
                {
                    double tp2Price = isBuy
                        ? entryPrice + slDistance * Tp2RR
                        : entryPrice - slDistance * Tp2RR;

                    if (isBuy ? currentPrice >= tp2Price : currentPrice <= tp2Price)
                    {
                        _tp2ClosedPositions.Add(position.Id);
                        PartialClose(position, Tp2ClosePercent / 100.0);
                        Print("TP2 hit for position {0}", position.Id);
                        if (BreakEvenTrigger == BreakEvenTriggerOption.TP2) ApplyBreakEven(position);
                        if (TrailingAfterLastTp && !_trailingActivePositions.Contains(position.Id))
                            _trailingActivePositions.Add(position.Id);
                    }
                }
                // TP3 + Trailing
                else if (!_tp3ClosedPositions.Contains(position.Id))
                {
                    double tp3Price = isBuy
                        ? entryPrice + slDistance * Tp3RR
                        : entryPrice - slDistance * Tp3RR;

                    if (isBuy ? currentPrice >= tp3Price : currentPrice <= tp3Price)
                    {
                        _tp3ClosedPositions.Add(position.Id);
                        ClosePosition(position);
                        Print("TP3 hit for position {0}", position.Id);
                        continue;
                    }

                    if (TrailingAfterLastTp && _trailingActivePositions.Contains(position.Id))
                        ApplyTrailingStop(position);

                    if (BreakEvenTrigger == BreakEvenTriggerOption.TP3)
                        ApplyBreakEven(position);
                }
            }
        }

        private void PartialClose(Position position, double percent)
        {
            double volumeToClose = Symbol.NormalizeVolumeInUnits(position.VolumeInUnits * percent);
            double volumeRemaining = position.VolumeInUnits - volumeToClose;

            if (volumeRemaining < Symbol.VolumeInUnitsMin || volumeToClose < Symbol.VolumeInUnitsMin)
            {
                Print("PartialClose skipped for {0}: volume check failed (close={1}, remaining={2}, min={3})",
                    position.Id, volumeToClose, volumeRemaining, Symbol.VolumeInUnitsMin);
                return;
            }

            ClosePosition(position, volumeToClose);
        }

        private void ApplyBreakEven(Position position)
        {
            if (_breakEvenAppliedPositions.Contains(position.Id)) return;

            double positionLots    = Symbol.VolumeInUnitsToQuantity(position.VolumeInUnits);
            double totalCommission = CommissionPerLot * positionLots;
            double commissionPips  = totalCommission / (position.VolumeInUnits * Symbol.PipValue);

            double breakEvenPrice = position.TradeType == TradeType.Buy
                ? position.EntryPrice + commissionPips * Symbol.PipSize
                : position.EntryPrice - commissionPips * Symbol.PipSize;

            ModifyPosition(position, breakEvenPrice, position.TakeProfit, ProtectionType.Absolute);
            _breakEvenAppliedPositions.Add(position.Id);
            Print("Break-even applied to position {0} at {1:F5}", position.Id, breakEvenPrice);
        }

        private void ApplyTrailingStop(Position position)
        {
            bool   isBuy        = position.TradeType == TradeType.Buy;
            double currentPrice = isBuy ? Symbol.Bid : Symbol.Ask;
            double atrValue     = _atr.Result.LastValue;
            double newSl        = isBuy ? currentPrice - atrValue : currentPrice + atrValue;
            double currentSl    = position.StopLoss ?? 0;
            bool   shouldMove   = isBuy ? newSl > currentSl : (currentSl == 0 || newSl < currentSl);

            if (shouldMove) ModifyPosition(position, newSl, position.TakeProfit, ProtectionType.Absolute);
        }

        // ── POSITION CLOSED EVENT ─────────────────────────────────
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;
            if (position.Label != BotLabel) return;

            if (position.GrossProfit < 0) _consecutiveLosses++;
            else _consecutiveLosses = 0;

            // [3] Trade-Journal beim Schließen vervollständigen und ausgeben
            if (EnableTradeJournal && _journalEntries.TryGetValue(position.Id, out var entry))
            {
                double slDistance  = Math.Abs(entry.EntryPrice - entry.StopLoss);
                double profitPips  = position.GrossProfit >= 0
                    ? Math.Abs(position.EntryPrice - (position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask)) / Symbol.PipSize
                    : -Math.Abs(position.EntryPrice - (position.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask)) / Symbol.PipSize;
                double rrRealized  = slDistance > 0 ? (position.GrossProfit / (entry.SLPips * Symbol.PipValue * Symbol.QuantityToVolumeInUnits(entry.Lots))) : 0;

                string exitReason = args.Reason.ToString();

                PrintJournal("JOURNAL_TRADE",
                    string.Format("{0};{1};{2};{3};{4:F2};{5:F5};{6:F5};{7:F1};{8:F5};{9:F5};{10:F1};{11};{12:F5};{13:F5};{14};{15:F2};{16:F5};{17};{18:F2};{19:F2};{20:F2}",
                        entry.Timestamp, entry.PositionId, Symbol.Name, entry.Direction,
                        entry.Lots, entry.EntryPrice, entry.StopLoss, entry.SLPips,
                        entry.FvgHigh, entry.FvgLow, entry.FvgSizePips,
                        entry.H1Bias, entry.DailyHigh, entry.DailyLow,
                        entry.Session, entry.Spread, entry.ATR,
                        exitReason, position.GrossProfit, position.NetProfit, rrRealized));

                _journalEntries.Remove(position.Id);
            }

            // Tracking bereinigen
            _tp1ClosedPositions.Remove(position.Id);
            _tp2ClosedPositions.Remove(position.Id);
            _tp3ClosedPositions.Remove(position.Id);
            _breakEvenAppliedPositions.Remove(position.Id);
            _trailingActivePositions.Remove(position.Id);

            Print("Position {0} closed | PnL: {1:F2} | Consecutive losses: {2}",
                position.Id, position.GrossProfit, _consecutiveLosses);

            if (_consecutiveLosses >= MaxConsecutiveLosses)
            {
                Print("CIRCUIT BREAKER: {0} consecutive losses. Halting until next day.", MaxConsecutiveLosses);
                _botHalted = true;
            }
        }

        // ── [3] JOURNAL HELPER ────────────────────────────────────
        private void PrintJournal(string tag, string csvLine)
        {
            Print("[{0}] {1}", tag, csvLine);
        }

        // ── VALIDATION ────────────────────────────────────────────
        private void ValidateParameters()
        {
            double totalPct = Tp1ClosePercent + Tp2ClosePercent + Tp3ClosePercent;
            if (Math.Abs(totalPct - 100.0) > 0.1)
                Print("WARNING: TP Close percentages sum to {0}%, not 100%.", totalPct);

            if (RiskPerTradePercent > 5.0)
                Print("WARNING: Risk per trade {0}% exceeds 5%.", RiskPerTradePercent);

            if (EmaFastPeriod >= EmaSlowPeriod)
                Print("WARNING: EMA Fast ({0}) >= EMA Slow ({1}). Trend logic incorrect.", EmaFastPeriod, EmaSlowPeriod);

            if (MaxOpenPositions > 1)
                Print("INFO: MaxOpenPositions={0}. Total risk exposure up to {1:F1}% simultaneously.",
                    MaxOpenPositions, MaxOpenPositions * RiskPerTradePercent);
        }
    }

    // ── SUPPORTING TYPES ──────────────────────────────────────────
    public class FvgZone
    {
        public double    FvgHigh           { get; set; }
        public double    FvgLow            { get; set; }
        public TradeType Direction         { get; set; }
        public int       CreatedAtBarIndex { get; set; }
        public bool      IsActive          { get; set; }
        public string    H1Bias            { get; set; }
        public double    SignalVolume      { get; set; }
        public double    AvgVolume         { get; set; }
    }

    // [3] Journal-Daten pro Trade
    public class TradeJournalEntry
    {
        public string Timestamp   { get; set; }
        public long   PositionId  { get; set; }
        public string Direction   { get; set; }
        public double Lots        { get; set; }
        public double EntryPrice  { get; set; }
        public double StopLoss    { get; set; }
        public double SLPips      { get; set; }
        public double FvgHigh     { get; set; }
        public double FvgLow      { get; set; }
        public double FvgSizePips { get; set; }
        public string H1Bias      { get; set; }
        public double DailyHigh   { get; set; }
        public double DailyLow    { get; set; }
        public string Session     { get; set; }
        public double Spread      { get; set; }
        public double ATR         { get; set; }
    }

    public enum TrendBias          { Bullish, Bearish, Neutral }
    public enum BreakEvenTriggerOption { TP1, TP2, TP3 }
}
