// ═══════════════════════════════════════════════════════════════════════════════
//  Clover Algo  │  Multi-Edge Intraday cBot
//  Platform     │  cTrader
//  Version      │  1.0.0
//  Edges        │  (1) Opening Range Breakout + Volatility Compression (NR7/Squeeze)
//                  (2) VWAP Mean-Reversion in Low-ADX (range) regimes
//                  (3) Momentum Continuation aligned with HTF regime
//                  Regime Switch: ATR/Median(20d) picks which edge is "armed".
//  Design notes │  - Closed-bar signals only (Last(1)) — no repaint.
//                  - Single-position model per symbol (one at a time).
//                  - Hard risk gates: Daily/Weekly DD, Spread×ATR, Consec-Loss sizing.
//                  - Partials + Chandelier trail + BE lock, Setup-Adaptive RRR.
//                  - All new/experimental modules default OFF for parity testing.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum CloverEdge { ORB, VwapMR, MomoCont }
    public enum CloverRegime { LowVol, Normal, HighVol }
    public enum CloverSetup { Trend, MeanReversion, Breakout }
    public enum CloverRiskBase { Balance, Equity }

    internal sealed class CloverTradeState
    {
        public int     PositionId;
        public double  EntryPrice;
        public double  InitialSlPips;
        public double  InitialVolumeUnits;
        public double  RRRTarget;
        public bool    BreakEvenDone;
        public bool    Partial1Done;
        public bool    Partial2Done;
        public double  ChandelierStop;      // long: highest(high) - k*ATR ; short: lowest(low) + k*ATR
        public DateTime EntryTime;
        public CloverEdge Edge;
        public CloverSetup Setup;
        public double  EntrySlippage;       // pips: intended vs actual entry price
        public double  ExitSlippage;        // pips: intended vs actual exit price
    }

    [Robot("Clover Algo", AccessRights = AccessRights.None)]
    public class CloverAlgo : Robot
    {
        // ════════════════════════════════════════════════════════════════════
        //  PARAMETERS
        // ════════════════════════════════════════════════════════════════════

        // ── 00 · Core ────────────────────────────────────────────────────────
        [Parameter("Bot Label", Group = "00 · Core", DefaultValue = "CloverAlgo")]
        public string BotLabel { get; set; }

        [Parameter("Risk Base", Group = "00 · Core", DefaultValue = CloverRiskBase.Balance)]
        public CloverRiskBase RiskBase { get; set; }

        [Parameter("Risk per Trade (%)", Group = "00 · Core",
            DefaultValue = 0.5, MinValue = 0.05, MaxValue = 2.0, Step = 0.05)]
        public double RiskPerTradePct { get; set; }

        [Parameter("Commission Buffer (Pips, added to SL)", Group = "00 · Core",
            DefaultValue = 0.6, MinValue = 0.0, Step = 0.1)]
        public double CommissionBufferPips { get; set; }

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

        [Parameter("ORB entry cutoff hour (no entries after)", Group = "06 · ORB",
            DefaultValue = 14, MinValue = 1, MaxValue = 23)]
        public int OrbEntryCutoffHour { get; set; }

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
            DefaultValue = 3.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1)]
        public double MaxDailyDdPct { get; set; }

        [Parameter("Max Weekly Drawdown (%, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 6.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1)]
        public double MaxWeeklyDdPct { get; set; }

        [Parameter("Max Trades per Day", Group = "11 · Risk Gates",
            DefaultValue = 4, MinValue = 1, MaxValue = 50)]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Consec Losses – Cool-Down (hours, 0 = off)", Group = "11 · Risk Gates",
            DefaultValue = 0.0, MinValue = 0.0, MaxValue = 48.0, Step = 0.5)]
        public double ConsecLossCoolDownHours { get; set; }

        [Parameter("Consec Losses – Trigger", Group = "11 · Risk Gates",
            DefaultValue = 3, MinValue = 1, MaxValue = 10)]
        public int ConsecLossTrigger { get; set; }

        [Parameter("Consec Loss – Size Reducer (0.5–1.0, <1 = smaller)", Group = "11 · Risk Gates",
            DefaultValue = 0.7, MinValue = 0.3, MaxValue = 1.0, Step = 0.05)]
        public double ConsecLossSizeReducer { get; set; }

        // ── 12 · Vol-Targeted Sizing ─────────────────────────────────────────
        [Parameter("Enable Vol-Targeted Sizing", Group = "12 · Sizing", DefaultValue = false)]
        public bool EnableVolTargetSizing { get; set; }

        [Parameter("Baseline ATR Pips (reference)", Group = "12 · Sizing",
            DefaultValue = 12.0, MinValue = 1.0, Step = 0.5)]
        public double VolTargetBaselineAtrPips { get; set; }

        // ── 13 · Logging ─────────────────────────────────────────────────────
        [Parameter("Verbose Logs", Group = "13 · Logging", DefaultValue = true)]
        public bool Verbose { get; set; }

        [Parameter("Enable Attribution Summary on Stop", Group = "13 · Logging", DefaultValue = true)]
        public bool EnableAttribution { get; set; }

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE STATE
        // ════════════════════════════════════════════════════════════════════
        private Bars _htfBars;
        private Bars _dailyBars;
        private MovingAverage _htfEma;
        private AverageTrueRange _atr;
        private AverageTrueRange _atrChandelier;
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

        // ORB session state (reset per day)
        private DateTime _orbDate = DateTime.MinValue;
        private double _orbHigh;
        private double _orbLow;
        private bool _orbReady;
        private bool _orbLongTaken;
        private bool _orbShortTaken;

        // VWAP incremental
        private DateTime _vwapDate = DateTime.MinValue;
        private double _vwapSumPv;
        private double _vwapSumV;
        private double _cachedVwap;
        private DateTime _lastVwapBarTime = DateTime.MinValue;

        // Tracking
        private int _totalTradesOpened;
        private Dictionary<CloverEdge, int> _edgeWinCount = new Dictionary<CloverEdge, int>();
        private Dictionary<CloverEdge, int> _edgeLossCount = new Dictionary<CloverEdge, int>();
        private Dictionary<CloverEdge, double> _edgePnlSum = new Dictionary<CloverEdge, double>();

        // ════════════════════════════════════════════════════════════════════
        //  ON START
        // ════════════════════════════════════════════════════════════════════
        protected override void OnStart()
        {
            Print("╔══════════════════════════════════════════════╗");
            Print("║   Clover Algo  v1.0.0   │  Starting          ║");
            Print("╚══════════════════════════════════════════════╝");
            _startTime = Server.Time;

            if (!ValidateParameters())
            {
                Print("CRITICAL: parameter validation failed — bot will idle.");
                return;
            }

            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _atrChandelier = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.WilderSmoothing);
            _emaFast = Indicators.MovingAverage(Bars.ClosePrices, MomoEmaFast, MovingAverageType.Exponential);
            _emaSlow = Indicators.MovingAverage(Bars.ClosePrices, MomoEmaSlow, MovingAverageType.Exponential);
            _rsi = Indicators.RelativeStrengthIndex(Bars.ClosePrices, 14);
            _dms = Indicators.DirectionalMovementSystem(14);

            _htfBars = MarketData.GetBars(HtfTimeFrame, SymbolName);
            _htfEma = Indicators.MovingAverage(_htfBars.ClosePrices, HtfEmaPeriod, MovingAverageType.Exponential);

            _dailyBars = MarketData.GetBars(TimeFrame.Daily, SymbolName);

            foreach (CloverEdge e in Enum.GetValues(typeof(CloverEdge)))
            {
                _edgeWinCount[e] = 0;
                _edgeLossCount[e] = 0;
                _edgePnlSum[e] = 0;
            }

            _dayStartBalance = Account.Balance;
            _weekStartBalance = Account.Balance;
            _dayRealizedPnl = 0;
            _weekRealizedPnl = 0;
            _botDay = Server.Time.Date;
            _botWeek = GetWeekMonday(Server.Time);

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
        }

        protected override void OnStop()
        {
            Positions.Closed -= OnPositionClosed;
            TimeSpan runtime = Server.Time - _startTime;
            Print("╔══════════════════════════════════════════════╗");
            Print("║   Clover Algo v1.0.0  │  Stopped             ║");
            Print("╚══════════════════════════════════════════════╝");
            Print("  Runtime    : {0:dd\\d\\ hh\\h\\ mm\\m}", runtime);
            Print("  Balance    : {0:F2} {1}", Account.Balance, Account.Asset.Name);
            Print("  Trades     : {0}", _totalTradesOpened);

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
                    Print("  {0,-10} n={1,3} wr={2:P1} avgPnL={3:+0.00;-0.00;0.00}", e, n, wr, avg);
                }
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

                // Need enough history for every indicator.
                if (Bars.Count < Math.Max(MomoEmaSlow, AtrPeriod) + 5) return;
                if (_htfBars.Count < HtfEmaPeriod + 2) return;

                // Update ORB window state once per bar.
                UpdateOrbState();

                // If an open position exists, no new entries — just management.
                if (_currentTrade != null) return;

                // Hard gates.
                if (!IsMarketTradable()) return;

                // Regime classification.
                CloverRegime regime = ClassifyRegime();
                int htfBias = GetHtfBias(); // +1 bull, -1 bear, 0 flat

                // Try edges by priority: ORB > Momo > VwapMR
                // (Breakouts & trend have earlier asymmetric payoff; MR is fallback.)
                if (EnableOrb && TryOrbEntry(regime, htfBias)) return;
                if (EnableMomoCont && TryMomoEntry(regime, htfBias)) return;
                if (EnableVwapMR && TryVwapMrEntry(regime, htfBias)) return;

                // Manage partials at bar-close level (gap protection).
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
                ManageBreakEven(pos);
                ManagePartials(pos);
                ManageChandelier(pos);
                ManageMaxHold(pos);
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

        // ════════════════════════════════════════════════════════════════════
        //  GATES
        // ════════════════════════════════════════════════════════════════════
        private bool IsMarketTradable()
        {
            DateTime now = Server.Time;

            if (_dailyDdBreached) return Reject("DailyDD hit");
            if (_weeklyDdBreached) return Reject("WeeklyDD hit");
            if (_tradesToday >= MaxTradesPerDay) return Reject("MaxTradesPerDay");
            if (now < _cooldownEndTime) return Reject("CoolDown");

            // Session window
            int hour = now.Hour;
            if (hour < SessionStartHour || hour >= SessionEndHour) return Reject("OutsideSession");

            // Friday block
            if (BlockFriday && now.DayOfWeek == DayOfWeek.Friday) return Reject("FridayBlock");

            // Spread gate: hard cap + dynamic ATR-scaled cap
            double spreadPips = Symbol.Spread / Symbol.PipSize;
            double atrPips = GetAtrPips();
            double dynCap = Math.Min(MaxSpreadPips, Math.Max(0.3, atrPips * SpreadAtrRatio));
            if (spreadPips > dynCap) return Reject($"SpreadGate {spreadPips:F2} > {dynCap:F2}");

            return true;
        }

        private bool Reject(string reason)
        {
            if (Verbose) Print("REJECT: {0}", reason);
            return false;
        }

        // Regime: ATR(today) / Median(last N daily ATR proxies)
        private CloverRegime ClassifyRegime()
        {
            if (_dailyBars == null || _dailyBars.Count < MedianLookbackDays + 2)
                return CloverRegime.Normal;

            double[] trs = new double[MedianLookbackDays];
            for (int i = 0; i < MedianLookbackDays; i++)
            {
                int idx = i + 1;
                if (idx + 1 >= _dailyBars.Count) return CloverRegime.Normal;
                double h = _dailyBars.HighPrices.Last(idx);
                double l = _dailyBars.LowPrices.Last(idx);
                double c1 = _dailyBars.ClosePrices.Last(idx + 1);
                double tr = Math.Max(h - l, Math.Max(Math.Abs(h - c1), Math.Abs(l - c1)));
                trs[i] = tr;
            }
            Array.Sort(trs);
            double median = trs[MedianLookbackDays / 2];
            if (median <= 0) return CloverRegime.Normal;

            double currAtrPips = GetAtrPips();
            double currAtrPrice = currAtrPips * Symbol.PipSize;
            double ratio = currAtrPrice / median;

            if (ratio <= AtrRatioLow) return CloverRegime.LowVol;
            if (ratio >= AtrRatioHigh) return CloverRegime.HighVol;
            return CloverRegime.Normal;
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
            DateTime now = Server.Time;
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

            // Accumulate range using the last closed bar.
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
                if (Verbose)
                    Print("ORB ready: H={0:F5} L={1:F5} range={2:F1}p",
                        _orbHigh, _orbLow, (_orbHigh - _orbLow) / Symbol.PipSize);
            }
        }

        private bool TryOrbEntry(CloverRegime regime, int htfBias)
        {
            if (!_orbReady) return false;
            DateTime now = Server.Time;
            if (now.Hour >= OrbEntryCutoffHour) return false;

            // ORB works best in Normal/High-Vol regimes, poorly in LowVol chop.
            if (regime == CloverRegime.LowVol) return false;

            // NR7 filter: today's ORB range must be narrower than the last 7 daily ranges.
            if (OrbRequireNr7 && !IsNr7Squeeze()) return false;

            double bufferPx = OrbBufferPips * Symbol.PipSize;
            double lastClose = Bars.ClosePrices.Last(1);
            double lastHigh = Bars.HighPrices.Last(1);
            double lastLow = Bars.LowPrices.Last(1);

            // Long breakout: close above ORB high + buffer, HTF bias not bearish.
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

        private bool IsNr7Squeeze()
        {
            if (_dailyBars == null || _dailyBars.Count < 8) return true;
            double todayRange = _orbHigh - _orbLow;
            if (todayRange <= 0) return false;
            for (int i = 1; i <= 7; i++)
            {
                double h = _dailyBars.HighPrices.Last(i);
                double l = _dailyBars.LowPrices.Last(i);
                if ((h - l) <= todayRange) return false;
            }
            return true;
        }

        // ════════════════════════════════════════════════════════════════════
        //  EDGE #2 — VWAP MEAN-REVERSION (ADX-gated, dev>= X*ATR)
        // ════════════════════════════════════════════════════════════════════
        private bool TryVwapMrEntry(CloverRegime regime, int htfBias)
        {
            // MR works best in LowVol / Normal range regimes.
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

            // Long: below VWAP by >= VwapMrMinDevAtr, optional RSI extreme, bullish/flat HTF bias.
            if (devAtrMultiples <= -VwapMrMinDevAtr && htfBias >= 0)
            {
                if (VwapMrRequireRsi && rsi >= 30) return false;
                return OpenTrade(TradeType.Buy, CloverEdge.VwapMR, CloverSetup.MeanReversion, RrrMR,
                    $"VwapMR Long | dev={devAtrMultiples:F2}ATR rsi={rsi:F1} adx={adx:F1}");
            }
            if (devAtrMultiples >= VwapMrMinDevAtr && htfBias <= 0)
            {
                if (VwapMrRequireRsi && rsi <= 70) return false;
                return OpenTrade(TradeType.Sell, CloverEdge.VwapMR, CloverSetup.MeanReversion, RrrMR,
                    $"VwapMR Short | dev={devAtrMultiples:F2}ATR rsi={rsi:F1} adx={adx:F1}");
            }
            return false;
        }

        private void UpdateVwap()
        {
            DateTime now = Server.Time;
            DateTime today = now.Date;
            if (_vwapDate != today)
            {
                _vwapDate = today;
                _vwapSumPv = 0;
                _vwapSumV = 0;
                _lastVwapBarTime = DateTime.MinValue;
            }

            DateTime lastBarOpen = Bars.OpenTimes.Last(1);
            if (lastBarOpen == _lastVwapBarTime) return;   // already incorporated
            if (lastBarOpen.Date != today) return;          // last closed bar belongs to a previous session

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
            // Momo works best in Normal/HighVol trending regimes.
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
                    || (lowPrev <= fastNow + 0.25 * Math.Abs(fastNow - slowNow)
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
                    || (highPrev >= fastNow - 0.25 * Math.Abs(fastNow - slowNow)
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
            double slPips = Math.Round(atrPips * AtrSlMultiplier + CommissionBufferPips, 1);
            if (slPips < MinSlPips) slPips = MinSlPips;
            if (slPips > MaxSlPips) slPips = MaxSlPips;
            double tpPips = Math.Round(slPips * rrr, 1);

            double volume = CalculateVolume(slPips);
            if (volume < Symbol.VolumeInUnitsMin)
            {
                Print("REJECT: calculated volume below broker min. slPips={0:F1} vol={1:F0}", slPips, volume);
                return false;
            }

            var result = ExecuteMarketOrder(dir, SymbolName, volume, BotLabel, slPips, tpPips, reason);
            if (!result.IsSuccessful || result.Position == null)
            {
                Print("ORDER FAILED: {0}", result.Error);
                return false;
            }

            var pos = result.Position;
            _currentTrade = new CloverTradeState
            {
                PositionId = pos.Id,
                EntryPrice = pos.EntryPrice,
                InitialSlPips = slPips,
                InitialVolumeUnits = pos.VolumeInUnits,
                RRRTarget = rrr,
                BreakEvenDone = false,
                Partial1Done = false,
                Partial2Done = false,
                ChandelierStop = dir == TradeType.Buy ? double.MinValue : double.MaxValue,
                EntryTime = Server.Time,
                Edge = edge,
                Setup = setup
            };
            _totalTradesOpened++;
            _tradesToday++;

            Print("FILLED: {0} {1} vol={2:F0} entry={3:F5} SL={4:F1}p TP={5:F1}p RRR={6:F2} edge={7} setup={8} | {9}",
                dir, SymbolName, volume, pos.EntryPrice, slPips, tpPips, rrr, edge, setup, reason);
            return true;
        }

        private double CalculateVolume(double slPips)
        {
            double baseAcct = RiskBase == CloverRiskBase.Equity ? Account.Equity : Account.Balance;
            double riskPct = RiskPerTradePct;

            // Anti-Martingale size reducer on losing streak.
            if (_consecutiveLosses >= ConsecLossTrigger)
                riskPct *= ConsecLossSizeReducer;

            // Vol-targeted sizing: scale by Baseline/ATR (bounded 0.5-2.0).
            if (EnableVolTargetSizing)
            {
                double atrPips = GetAtrPips();
                if (atrPips > 0)
                {
                    double scale = VolTargetBaselineAtrPips / atrPips;
                    if (scale < 0.5) scale = 0.5;
                    if (scale > 2.0) scale = 2.0;
                    riskPct *= scale;
                }
            }

            double riskAmount = baseAcct * (riskPct / 100.0);
            double pipValue = Symbol.PipValue;
            if (pipValue <= 0 || slPips <= 0) return Symbol.VolumeInUnitsMin;
            double exact = riskAmount / (slPips * pipValue);
            return Symbol.NormalizeVolumeInUnits(exact, RoundingMode.Down);
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
            var r = ClosePosition(pos, normalized);
            if (r.IsSuccessful && Verbose)
                Print("{0}: closed {1:F0}u remaining {2:F0}u", tag, normalized, pos.VolumeInUnits - normalized);
        }

        private void ManageChandelier(Position pos)
        {
            if (!EnableChandelier) return;
            if (!_currentTrade.Partial1Done) return;  // wait until first partial locks R>=0
            double atr = _atrChandelier.Result.Last(1);
            if (double.IsNaN(atr) || atr <= 0) return;

            if (pos.TradeType == TradeType.Buy)
            {
                double highSinceEntry = GetHighSince(_currentTrade.EntryTime);
                double candidate = highSinceEntry - ChandelierAtrMult * atr;
                if (candidate > _currentTrade.ChandelierStop)
                    _currentTrade.ChandelierStop = candidate;

                double desiredSl = _currentTrade.ChandelierStop;
                if (!pos.StopLoss.HasValue || desiredSl > pos.StopLoss.Value)
                {
                    // never loosen beyond current SL
                    if (pos.StopLoss.HasValue && desiredSl <= pos.StopLoss.Value) return;
                    // cap: don't pull SL beyond current price
                    if (desiredSl >= Symbol.Bid - 2 * Symbol.PipSize) return;
                    ModifyPosition(pos, desiredSl, pos.TakeProfit);
                }
            }
            else
            {
                double lowSinceEntry = GetLowSince(_currentTrade.EntryTime);
                double candidate = lowSinceEntry + ChandelierAtrMult * atr;
                if (candidate < _currentTrade.ChandelierStop)
                    _currentTrade.ChandelierStop = candidate;

                double desiredSl = _currentTrade.ChandelierStop;
                if (!pos.StopLoss.HasValue || desiredSl < pos.StopLoss.Value)
                {
                    if (pos.StopLoss.HasValue && desiredSl >= pos.StopLoss.Value) return;
                    if (desiredSl <= Symbol.Ask + 2 * Symbol.PipSize) return;
                    ModifyPosition(pos, desiredSl, pos.TakeProfit);
                }
            }
        }

        private void ManageMaxHold(Position pos)
        {
            if (MaxHoldBars <= 0) return;
            int barsHeld = 0;
            DateTime entry = _currentTrade.EntryTime;
            // count closed bars since entry
            for (int i = 1; i < Bars.Count; i++)
            {
                if (Bars.OpenTimes.Last(i) < entry) break;
                barsHeld++;
            }
            if (barsHeld >= MaxHoldBars)
            {
                ClosePositionIfOpen("MaxHold");
            }
        }

        private double GetHighSince(DateTime since)
        {
            double h = double.MinValue;
            for (int i = 1; i < Bars.Count; i++)
            {
                if (Bars.OpenTimes.Last(i) < since) break;
                if (Bars.HighPrices.Last(i) > h) h = Bars.HighPrices.Last(i);
            }
            if (h == double.MinValue) h = Bars.HighPrices.Last(1);
            return h;
        }

        private double GetLowSince(DateTime since)
        {
            double l = double.MaxValue;
            for (int i = 1; i < Bars.Count; i++)
            {
                if (Bars.OpenTimes.Last(i) < since) break;
                if (Bars.LowPrices.Last(i) < l) l = Bars.LowPrices.Last(i);
            }
            if (l == double.MaxValue) l = Bars.LowPrices.Last(1);
            return l;
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
            _dayRealizedPnl += pnl;
            _weekRealizedPnl += pnl;

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

            Print("CLOSED: id={0} edge={1} setup={2} pnl={3:F2} dayRealPnL={4:F2} consecLoss={5}",
                pos.Id, edge, _currentTrade.Setup, pnl, _dayRealizedPnl, _consecutiveLosses);
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
        }

        private bool WeekendClosePending()
        {
            return Server.Time.DayOfWeek == DayOfWeek.Friday
                && Server.Time.Hour >= WeekendCloseHour
                && _currentTrade != null;
        }

        private void ClosePositionIfOpen(string reason)
        {
            if (_currentTrade == null) return;
            var pos = Positions.FindById(_currentTrade.PositionId);
            if (pos == null) { _currentTrade = null; return; }
            var r = ClosePosition(pos);
            if (r.IsSuccessful)
                Print("FORCE CLOSE [{0}]: id={1} pnl={2:F2}", reason, pos.Id, pos.NetProfit);
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

                _currentTrade = new CloverTradeState
                {
                    PositionId = p.Id,
                    EntryPrice = p.EntryPrice,
                    InitialSlPips = slPips,
                    InitialVolumeUnits = p.VolumeInUnits,
                    RRRTarget = RrrTrend,
                    BreakEvenDone = p.StopLoss.HasValue && (
                        p.TradeType == TradeType.Buy
                            ? p.StopLoss.Value >= p.EntryPrice - 0.1 * Symbol.PipSize
                            : p.StopLoss.Value <= p.EntryPrice + 0.1 * Symbol.PipSize),
                    Partial1Done = true,   // conservative: skip partials after recovery
                    Partial2Done = true,
                    ChandelierStop = p.TradeType == TradeType.Buy ? double.MinValue : double.MaxValue,
                    EntryTime = p.EntryTime,
                    Edge = CloverEdge.MomoCont,
                    Setup = CloverSetup.Trend
                };
                Print("RECOVERY: adopted open position id={0} {1} entry={2:F5} slPips={3:F1}",
                    p.Id, p.TradeType, p.EntryPrice, slPips);
                break;
            }
        }

        private static DateTime GetWeekMonday(DateTime d)
        {
            int back = ((int)d.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
            return d.Date.AddDays(-back);
        }
    }
}
