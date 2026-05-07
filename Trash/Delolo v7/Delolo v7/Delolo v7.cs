using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum MacroTrend { Bullish, Bearish, Flat }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class RazorTrendFollower : Robot
    {
        #region Parameter
        [Parameter("Risiko pro Trade (%)", DefaultValue = 1.0, MaxValue = 2.0, Group = "Risk & Reward")]
        public double RiskPerTrade { get; set; }

        [Parameter("Chance-Risiko-Verhältnis (CRV)", DefaultValue = 1.5, MinValue = 1.1, Group = "Risk & Reward")]
        public double RewardToRiskRatio { get; set; }

        [Parameter("Kommissions-Puffer (Pips)", DefaultValue = 0.8, Group = "Risk & Reward")]
        public double CommissionBufferPips { get; set; }

        [Parameter("Makro Timeframe (Trend)", DefaultValue = "Hour1", Group = "Filter")]
        public TimeFrame MacroTimeFrame { get; set; }

        [Parameter("Makro EMA", DefaultValue = 50, Group = "Filter")]
        public int MacroEmaPeriod { get; set; }

        [Parameter("Micro EMA Fast (Trigger)", DefaultValue = 9, Group = "Trigger")]
        public int MicroEmaFast { get; set; }

        [Parameter("Micro EMA Slow (Trigger)", DefaultValue = 21, Group = "Trigger")]
        public int MicroEmaSlow { get; set; }
        
        [Parameter("Debug Logs anzeigen", DefaultValue = true, Group = "Testing")]
        public bool ShowDebug { get; set; }
        #endregion

        #region Variablen
        private Bars _macroBars;
        private ExponentialMovingAverage _macroEma;
        private ExponentialMovingAverage _microEmaFast;
        private ExponentialMovingAverage _microEmaSlow;
        private AverageTrueRange _atr;
        private const string Label = "RazorTrendV3";
        #endregion

        protected override void OnStart()
        {
            try
            {
                // HIER IST DER FIX: Wir übergeben explizit 'SymbolName', damit cTrader genau das Symbol lädt, 
                // auf dem der Bot aktuell gestartet wurde (z.B. EURUSD.a), und nicht blind rät.
                _macroBars = MarketData.GetBars(MacroTimeFrame, SymbolName);
                _macroEma = Indicators.ExponentialMovingAverage(_macroBars.ClosePrices, MacroEmaPeriod);

                // Für den aktuellen Timeframe nutzt cTrader automatisch das richtige Symbol über Bars.ClosePrices
                _microEmaFast = Indicators.ExponentialMovingAverage(Bars.ClosePrices, MicroEmaFast);
                _microEmaSlow = Indicators.ExponentialMovingAverage(Bars.ClosePrices, MicroEmaSlow);
                _atr = Indicators.AverageTrueRange(14, MovingAverageType.Simple);
                
                Print($"Bot V3 gestartet auf Symbol: {SymbolName}. Multi-Timeframe dynamisch verknüpft.");
            }
            catch (Exception ex)
            {
                Print("Kritischer Fehler beim Starten (OnStart): " + ex.Message);
            }
        }

        protected override void OnBar()
        {
            try
            {
                if (_macroBars == null || _macroEma == null || _microEmaFast == null || _microEmaSlow == null || _atr == null)
                    return;

                if (Positions.FindAll(Label, SymbolName).Length > 0) return;

                if (double.IsNaN(_macroEma.Result.Last(1)) || double.IsNaN(_macroBars.ClosePrices.Last(1)))
                {
                    if (ShowDebug) Print("Warte auf genügend Historie im Makro-Chart...");
                    return; 
                }

                MacroTrend trend = GetMacroTrend();
                
                if (trend == MacroTrend.Flat) return;

                if (Bars.Count < MicroEmaSlow + 5) return;

                bool isCrossUp = _microEmaFast.Result.Last(1) > _microEmaSlow.Result.Last(1) && _microEmaFast.Result.Last(2) <= _microEmaSlow.Result.Last(2);
                bool isCrossDown = _microEmaFast.Result.Last(1) < _microEmaSlow.Result.Last(1) && _microEmaFast.Result.Last(2) >= _microEmaSlow.Result.Last(2);

                if (trend == MacroTrend.Bullish && isCrossUp)
                {
                    if (ShowDebug) Print($"SETUP GEFUNDEN ({SymbolName}): Trend UP, Cross UP");
                    ExecuteTrade(TradeType.Buy);
                }
                else if (trend == MacroTrend.Bearish && isCrossDown)
                {
                    if (ShowDebug) Print($"SETUP GEFUNDEN ({SymbolName}): Trend DOWN, Cross DOWN");
                    ExecuteTrade(TradeType.Sell);
                }
            }
            catch (Exception ex)
            {
                Print("Fehler in OnBar: " + ex.Message + " | StackTrace: " + ex.StackTrace);
            }
        }

        private MacroTrend GetMacroTrend()
        {
            double macroClose = _macroBars.ClosePrices.Last(1);
            double macroEma = _macroEma.Result.Last(1);

            if (macroClose > macroEma + (2 * Symbol.PipSize))
                return MacroTrend.Bullish;
            
            if (macroClose < macroEma - (2 * Symbol.PipSize))
                return MacroTrend.Bearish;

            return MacroTrend.Flat;
        }

        private void ExecuteTrade(TradeType tradeType)
        {
            double atrPips = _atr.Result.Last(1) / Symbol.PipSize;
            double slPips = Math.Round(atrPips * 1.5, 1);
            
            if (slPips < 5.0) slPips = 5.0; 

            double tpPips = Math.Round((slPips * RewardToRiskRatio) + CommissionBufferPips, 1);
            double volume = CalculateVolume(slPips);

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, Label, slPips, tpPips, "Trend V3");
            
            if (!result.IsSuccessful)
            {
                Print("ORDER FEHLER: ", result.Error);
            }
        }

        private double CalculateVolume(double slPips)
        {
            double riskAmount = Account.Equity * (RiskPerTrade / 100);
            double valuePerPip = Symbol.PipValue;
            if (valuePerPip == 0 || slPips == 0) return Symbol.VolumeInUnitsMin;
            double exactVolume = riskAmount / (slPips * valuePerPip);
            return Symbol.NormalizeVolumeInUnits(exactVolume, RoundingMode.Down);
        }
    }
}