using System;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.Indicators;

namespace cAlgo.Robots;

[Robot(AccessRights = AccessRights.None, AddIndicators = true)]
public class GapStar : Robot
{
    #region Parameters

    [Parameter("Regime TimeFrame", DefaultValue = "Daily", Group = "Regime")]
    public TimeFrame RegimeTimeFrame { get; set; }

    [Parameter("Regime Period", DefaultValue = 20, MinValue = 2, Group = "Regime")]
    public int RegimePeriod { get; set; }

    [Parameter("Log Regime Signals", DefaultValue = false, Group = "Regime")]
    public bool LogRegimeSignals { get; set; }

    [Parameter("Log Session Signals", DefaultValue = false, Group = "Session")]
    public bool LogSessionSignals { get; set; }

    [Parameter("RSI Period", DefaultValue = 14, MinValue = 2, Group = "RSI")]
    public int RsiPeriod { get; set; }

    [Parameter("RSI Source", Group = "RSI")]
    public DataSeries RsiSource { get; set; } = null!;

    [Parameter("RSI Overbought", DefaultValue = 70.0, MinValue = 50.0, MaxValue = 100.0, Group = "RSI")]
    public double RsiOverbought { get; set; }

    [Parameter("RSI Oversold", DefaultValue = 30.0, MinValue = 0.0, MaxValue = 50.0, Group = "RSI")]
    public double RsiOversold { get; set; }

    [Parameter("RSI TF2", DefaultValue = "Hour", Group = "RSI")]
    public TimeFrame RsiTimeFrame2 { get; set; }

    [Parameter("RSI TF3", DefaultValue = "Hour4", Group = "RSI")]
    public TimeFrame RsiTimeFrame3 { get; set; }

    [Parameter("Log RSI Signals", DefaultValue = false, Group = "RSI")]
    public bool LogRsiSignals { get; set; }

    [Parameter("Gap Lookback Bars", DefaultValue = 5000, MinValue = 500, Group = "Gap")]
    public int GapLookback { get; set; }

    [Parameter("Gap ATR Period", DefaultValue = 14, MinValue = 5, Group = "Gap")]
    public int GapAtrPeriod { get; set; }

    [Parameter("Gap Min Size (ATR)", DefaultValue = 0.08, MinValue = 0.01, Step = 0.01, Group = "Gap")]
    public double GapMinAtr { get; set; }

    [Parameter("Gap Fill Window (Bars)", DefaultValue = 200, MinValue = 10, Group = "Gap")]
    public int GapFillWindow { get; set; }

    [Parameter("Log Gap Signals", DefaultValue = false, Group = "Gap")]
    public bool LogGapSignals { get; set; }

    [Parameter("Sweep ATR Period", DefaultValue = 14, MinValue = 2, Group = "Sweep Setup")]
    public int SweepAtrPeriod { get; set; }

    [Parameter("Sweep Min Wick (ATR mult)", DefaultValue = 0.3, MinValue = 0.0, MaxValue = 3.0, Step = 0.05, Group = "Sweep Setup")]
    public double SweepMinWickAtrMult { get; set; }

    [Parameter("Sweep Min RSI Confluence", DefaultValue = 1, MinValue = 0, MaxValue = 3, Group = "Sweep Setup")]
    public int SweepMinRsiConfluence { get; set; }

    [Parameter("Log Sweep Setups", DefaultValue = true, Group = "Sweep Setup")]
    public bool LogSweepSetups { get; set; }

    [Parameter("Risk per Trade (%)", DefaultValue = 0.5, MinValue = 0.05, MaxValue = 5.0, Step = 0.05, Group = "Trading")]
    public double RiskPercent { get; set; }

    [Parameter("ATR SL Buffer (mult)", DefaultValue = 0.3, MinValue = 0.0, MaxValue = 3.0, Step = 0.05, Group = "Trading")]
    public double AtrSlBufferMult { get; set; }

    [Parameter("Min RRR", DefaultValue = 1.5, MinValue = 0.5, MaxValue = 10.0, Step = 0.1, Group = "Trading")]
    public double MinRrr { get; set; }

    [Parameter("Allow ATR TP Fallback", DefaultValue = false, Group = "Trading")]
    public bool AllowAtrTpFallback { get; set; }

    [Parameter("TP ATR Fallback (mult)", DefaultValue = 2.0, MinValue = 0.5, MaxValue = 10.0, Step = 0.1, Group = "Trading")]
    public double TpAtrFallbackMult { get; set; }

    [Parameter("Bot Label", DefaultValue = "GapStar", Group = "Trading")]
    public string BotLabel { get; set; } = "GapStar";

    [Parameter("BE Trigger (R)", DefaultValue = 1.0, MinValue = 0.0, MaxValue = 5.0, Step = 0.1, Group = "Trade Management")]
    public double BeTriggerR { get; set; }

    [Parameter("Chandelier ATR (mult, 0=off)", DefaultValue = 3.0, MinValue = 0.0, MaxValue = 20.0, Step = 0.1, Group = "Trade Management")]
    public double ChandelierAtrMult { get; set; }

    [Parameter("Max Hold Bars (0=off)", DefaultValue = 50, MinValue = 0, MaxValue = 5000, Group = "Trade Management")]
    public int MaxHoldBars { get; set; }

    [Parameter("Reentry Cooldown Bars", DefaultValue = 5, MinValue = 0, MaxValue = 1000, Group = "Trade Management")]
    public int ReentryCooldownBars { get; set; }

    [Parameter("Daily Loss Cap (%, 0=off)", DefaultValue = 2.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1, Group = "Risk Caps")]
    public double DailyLossCapPct { get; set; }

    [Parameter("Weekly Loss Cap (%, 0=off)", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1, Group = "Risk Caps")]
    public double WeeklyLossCapPct { get; set; }

    [Parameter("Floating DD Cap (%, 0=off)", DefaultValue = 5.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1, Group = "Risk Caps")]
    public double FloatingDdCapPct { get; set; }

    [Parameter("Hard Loss Cap (%, 0=off)", DefaultValue = 10.0, MinValue = 0.0, MaxValue = 90.0, Step = 0.1, Group = "Risk Caps")]
    public double HardLossCapPct { get; set; }

    [Parameter("Equity Trail (%, 0=off)", DefaultValue = 0.0, MinValue = 0.0, MaxValue = 50.0, Step = 0.1, Group = "Risk Caps")]
    public double EquityTrailPct { get; set; }

    [Parameter("Trade Sessions (CSV: 1=HK,2=LON,3=NY)", DefaultValue = "1,2,3", Group = "Risk Caps")]
    public string TradeSessionsCsv { get; set; } = "1,2,3";

    [Parameter("Enable Trade Log CSV", DefaultValue = false, Group = "Logging")]
    public bool EnableTradeLogCsv { get; set; }

    [Parameter("Log Trades", DefaultValue = true, Group = "Logging")]
    public bool LogTrades { get; set; }

    [Parameter("Log Risk Caps", DefaultValue = true, Group = "Logging")]
    public bool LogRiskCaps { get; set; }

    #endregion

    #region Fields

    private RegimeMaster _regime = null!;
    private SessionAndLiquidityLevelsUTC _session = null!;
    private MultiTimeframeRsiCandles _rsi = null!;
    private GapRadar _gap = null!;
    private AverageTrueRange _atr = null!;

    private double _initialBalance;
    private double _peakEquity;
    private bool   _killSwitch;
    private string _killReason = "";

    private DateTime _dailyAnchorDate  = DateTime.MinValue;
    private DateTime _weeklyAnchorDate = DateTime.MinValue;
    private double   _dailyAnchorBalance;
    private double   _weeklyAnchorBalance;

    private int _lastCloseBarIndex = int.MinValue;

    private readonly System.Collections.Generic.HashSet<int> _allowedSessions = new();

    private readonly System.Collections.Generic.Dictionary<int, OpenTradeState> _openStates = new();
    private readonly System.Collections.Generic.List<double> _rMultiples = new();

    private string _tradeLogPath = "";

    #endregion

    #region Open Trade State

    private sealed class OpenTradeState
    {
        public int    EntryBarIndex;
        public double EntryPrice;
        public double InitialStopLoss;
        public double InitialTakeProfit;
        public double InitialRiskPrice;
        public double InitialRiskMoney;
        public bool   BeMoved;
        public double ChandelierAnchor;
        public SetupDir Dir;
        public DateTime EntryTime;
    }

    #endregion

    #region Setup Types

    private enum SetupDir { None, Bull, Bear }

    private readonly struct SweepSetup
    {
        public readonly SetupDir Dir;
        public readonly double SweepLevel;
        public readonly double WickSize;
        public readonly double Rsi1;
        public readonly int Confluence;
        public readonly double Atr;

        public SweepSetup(SetupDir dir, double sweepLevel, double wickSize, double rsi1, int confluence, double atr)
        {
            Dir = dir;
            SweepLevel = sweepLevel;
            WickSize = wickSize;
            Rsi1 = rsi1;
            Confluence = confluence;
            Atr = atr;
        }

        public static SweepSetup None => default;
    }

    #endregion

    #region Lifecycle

    protected override void OnStart()
    {
        _regime = Indicators.GetIndicator<RegimeMaster>(
            RegimeTimeFrame,
            RegimePeriod,
            Color.Green,
            Color.Red,
            30
        );

        _session = Indicators.GetIndicator<SessionAndLiquidityLevelsUTC>(
            "01:00", "09:00",
            "08:00", "16:00",
            "13:00", "21:00",
            30,
            true
        );

        _rsi = Indicators.GetIndicator<MultiTimeframeRsiCandles>(
            RsiPeriod,
            RsiSource,
            RsiOverbought,
            RsiOversold,
            RsiTimeFrame2,
            RsiTimeFrame3,
            false  // ColorBars off — bot context, keine Chart-Färbung
        );

        _gap = Indicators.GetIndicator<GapRadar>(
            GapLookback,            // Lookback
            GapAtrPeriod,           // ATR Period
            MovingAverageType.Simple,
            GapMinAtr,              // Min Gap Size (ATR)
            GapFillWindow,          // Fill Window
            0.5,                    // Fill Threshold (Mitigation %)
            3,                      // Top N Projections
            true,                   // Show Filled
            true,                   // Show Labels
            true,                   // Show Weekend
            true,                   // Show Session
            true,                   // Show News
            true,                   // Show FVG
            true,                   // Show Liquidity
            1.8,                    // News Gap ATR mult
            0,                      // Session Hour 1
            7,                      // Session Hour 2
            13,                     // Session Hour 3
            "",                     // Custom Session Hours CSV
            5.0,                    // Distance Decay (ATR)
            500,                    // Age Decay (Bars)
            0.5                     // Mitigation Boost
        );

        _atr = Indicators.AverageTrueRange(SweepAtrPeriod, MovingAverageType.Simple);

        ParseAllowedSessions(TradeSessionsCsv);

        _initialBalance = Account.Balance;
        _peakEquity     = Account.Equity;
        ResetDailyAnchor(Server.Time);
        ResetWeeklyAnchor(Server.Time);

        Positions.Closed += OnPositionsClosed;

        if (EnableTradeLogCsv && RunningMode == RunningMode.RealTime)
            InitializeTradeLog();

        RecoverOpenPositions();

        Print($"[GapStar] RegimeMaster geladen — TF={RegimeTimeFrame}, Period={RegimePeriod}");
        Print("[GapStar] SessionMaster geladen — HK 01-09, LON 08-16, NY 13-21 UTC");
        Print($"[GapStar] RsiMaster geladen — Period={RsiPeriod}, TF2={RsiTimeFrame2}, TF3={RsiTimeFrame3}, OB={RsiOverbought}, OS={RsiOversold}");
        Print($"[GapStar] GapRadar geladen — Lookback={GapLookback}, AtrPeriod={GapAtrPeriod}, MinAtr={GapMinAtr}, FillWindow={GapFillWindow}");
        Print($"[GapStar] Sweep Setup — AtrPeriod={SweepAtrPeriod}, MinWickAtr={SweepMinWickAtrMult}, MinConfluence={SweepMinRsiConfluence}/3");
        Print($"[GapStar] Trading — Risk={RiskPercent:F2}% AtrSlBuf={AtrSlBufferMult:F2} MinRRR={MinRrr:F2} ATR-TP-Fallback={AllowAtrTpFallback}");
        Print($"[GapStar] Trade Mgmt — BE@{BeTriggerR:F2}R Chandelier={ChandelierAtrMult:F2}*ATR MaxHold={MaxHoldBars} Cooldown={ReentryCooldownBars}");
        Print($"[GapStar] Risk Caps — Daily={DailyLossCapPct:F1}% Weekly={WeeklyLossCapPct:F1}% FloatingDD={FloatingDdCapPct:F1}% Hard={HardLossCapPct:F1}% EquityTrail={EquityTrailPct:F1}% Sessions=[{string.Join(",", _allowedSessions)}]");
    }

    protected override void OnBar()
    {
        int i = Bars.Count - 2; // letzter abgeschlossener Bar
        if (i < 0) return;

        try
        {
            ProcessBar(i);
        }
        catch (Exception ex)
        {
            Print($"[GapStar] OnBar EXCEPTION at bar {i} ({Bars.OpenTimes[i]:yyyy-MM-dd HH:mm}): {ex.GetType().Name} — {ex.Message}");
            Print($"[GapStar] Stack: {ex.StackTrace}");
            throw;
        }
    }

    private void ProcessBar(int i)
    {
        double signal = _regime.RegimeSignal[i];

        if (double.IsNaN(signal))
        {
            if (LogRegimeSignals)
                Print($"[Regime] Bar {i} ({Bars.OpenTimes[i]:yyyy-MM-dd HH:mm}) — Warmup, kein Signal");
        }
        else if (LogRegimeSignals)
        {
            string label = signal > 0.0 ? "BULLISH" : "BEARISH";
            Print($"[Regime] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | Signal={signal:F1} → {label}");
        }

        if (LogSessionSignals)
        {
            double sessCode = _session.CurrentSessionOut[i];
            double sHigh    = _session.SessionHighOut[i];
            double sLow     = _session.SessionLowOut[i];

            string sessName = sessCode switch
            {
                3 => "NY",
                2 => "LONDON",
                1 => "HK",
                _ => "NONE"
            };

            if (sessCode > 0 && !double.IsNaN(sHigh) && !double.IsNaN(sLow))
                Print($"[Session] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | {sessName} | H={sHigh:F5} L={sLow:F5}");
            else if (sessCode == 0)
                Print($"[Session] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | NONE");
        }

        if (LogRsiSignals)
        {
            double rsi1 = _rsi.Rsi1Out[i];
            double rsi2 = _rsi.Rsi2Out[i];
            double rsi3 = _rsi.Rsi3Out[i];
            double os   = _rsi.OversoldCountOut[i];
            double ob   = _rsi.OverboughtCountOut[i];

            if (double.IsNaN(rsi1) || double.IsNaN(rsi2) || double.IsNaN(rsi3))
                Print($"[RSI] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | Warmup");
            else
                Print($"[RSI] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | TF1={rsi1:F1} TF2={rsi2:F1} TF3={rsi3:F1} | OS={os:F0}/3 OB={ob:F0}/3");
        }

        if (LogGapSignals)
        {
            double nearestPrice = _gap.NearestGapPrice[i];
            double nearestProb  = _gap.NearestGapProb[i];
            double nearestType  = _gap.NearestGapType[i];
            double above        = _gap.GapsAbove[i];
            double below        = _gap.GapsBelow[i];
            double fvgAbove     = _gap.NearestFvgAbove[i];
            double fvgBelow     = _gap.NearestFvgBelow[i];

            string typeName = ((int)nearestType) switch
            {
                1 => "Weekend",
                2 => "Session",
                3 => "News",
                4 => "FVG",
                5 => "Liquidity",
                _ => "None"
            };

            string nearestStr = double.IsNaN(nearestPrice)
                ? "none"
                : $"{typeName}@{nearestPrice:F5} p={nearestProb:P0}";
            string fvgAboveStr = double.IsNaN(fvgAbove) ? "—"     : $"{fvgAbove:F5}";
            string fvgBelowStr = double.IsNaN(fvgBelow) ? "—"     : $"{fvgBelow:F5}";

            Print($"[Gap] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | nearest={nearestStr} | active above={above:F0} below={below:F0} | FVG↑={fvgAboveStr} FVG↓={fvgBelowStr}");
        }

        UpdateRiskCaps(i);
        ManageOpenPositions(i);

        if (_killSwitch) return;

        var setup = DetectSweep(i);
        if (setup.Dir == SetupDir.None) return;

        if (LogSweepSetups)
        {
            string side    = setup.Dir == SetupDir.Bull ? "BULL (Sweep↓→Long)" : "BEAR (Sweep↑→Short)";
            double wickAtr = setup.Atr > 0.0 ? setup.WickSize / setup.Atr : 0.0;
            Print($"[Sweep] Bar {i} | {Bars.OpenTimes[i]:yyyy-MM-dd HH:mm} | {side} | level={setup.SweepLevel:F5} wick={setup.WickSize:F5} ({wickAtr:F2}*ATR) | rsi1={setup.Rsi1:F1} confluence={setup.Confluence}/3");
        }

        TryEnter(setup, i);
    }

    private SweepSetup DetectSweep(int i)
    {
        if (i < 1) return SweepSetup.None;

        double sessNow  = _session.CurrentSessionOut[i];
        double sessPrev = _session.CurrentSessionOut[i - 1];
        if (sessPrev <= 0.0) return SweepSetup.None;
        if (sessNow != sessPrev) return SweepSetup.None;

        double sHighPrev = _session.SessionHighOut[i - 1];
        double sLowPrev  = _session.SessionLowOut[i - 1];
        if (double.IsNaN(sHighPrev) || double.IsNaN(sLowPrev)) return SweepSetup.None;

        double atr = _atr.Result[i];
        if (double.IsNaN(atr) || atr <= 0.0) return SweepSetup.None;

        double rsi1 = _rsi.Rsi1Out[i];
        if (double.IsNaN(rsi1)) return SweepSetup.None;

        double obCount = _rsi.OverboughtCountOut[i];
        double osCount = _rsi.OversoldCountOut[i];

        double high  = Bars.HighPrices[i];
        double low   = Bars.LowPrices[i];
        double close = Bars.ClosePrices[i];

        double minWick = SweepMinWickAtrMult * atr;

        if (high > sHighPrev && close < sHighPrev)
        {
            double wick = high - sHighPrev;
            if (wick >= minWick && rsi1 >= RsiOverbought && obCount >= SweepMinRsiConfluence)
                return new SweepSetup(SetupDir.Bear, sHighPrev, wick, rsi1, (int)obCount, atr);
        }

        if (low < sLowPrev && close > sLowPrev)
        {
            double wick = sLowPrev - low;
            if (wick >= minWick && rsi1 <= RsiOversold && osCount >= SweepMinRsiConfluence)
                return new SweepSetup(SetupDir.Bull, sLowPrev, wick, rsi1, (int)osCount, atr);
        }

        return SweepSetup.None;
    }

    protected override void OnStop()
    {
        WriteRMultipleSummary();
        Print("[GapStar] Bot gestoppt.");
    }

    #endregion

    #region Entry Engine

    private void TryEnter(SweepSetup setup, int signalBar)
    {
        if (HasOwnedPosition()) return;

        if (_lastCloseBarIndex != int.MinValue)
        {
            int sinceClose = signalBar - _lastCloseBarIndex;
            if (sinceClose < ReentryCooldownBars)
            {
                if (LogTrades) Print($"[Entry] Skip — Cooldown active ({sinceClose}/{ReentryCooldownBars} bars since last close)");
                return;
            }
        }

        int sessCode = (int)_session.CurrentSessionOut[signalBar];
        if (_allowedSessions.Count > 0 && !_allowedSessions.Contains(sessCode))
        {
            if (LogTrades) Print($"[Entry] Skip — session {sessCode} not in allow-list");
            return;
        }

        bool isLong = setup.Dir == SetupDir.Bull;
        TradeType tradeType = isLong ? TradeType.Buy : TradeType.Sell;

        double entryPrice = isLong ? Symbol.Ask : Symbol.Bid;
        double atrBuf     = AtrSlBufferMult * setup.Atr;

        double stopPrice;
        if (isLong)
        {
            double low = Bars.LowPrices[signalBar];
            stopPrice = Math.Min(low, setup.SweepLevel) - atrBuf;
        }
        else
        {
            double high = Bars.HighPrices[signalBar];
            stopPrice = Math.Max(high, setup.SweepLevel) + atrBuf;
        }

        double slDistance = Math.Abs(entryPrice - stopPrice);
        if (slDistance < Symbol.PipSize)
        {
            if (LogTrades) Print($"[Entry] Skip — SL distance < 1 pip ({slDistance:F5})");
            return;
        }

        double tpPrice = ResolveTakeProfit(signalBar, isLong, entryPrice, slDistance);
        if (double.IsNaN(tpPrice))
        {
            if (LogTrades) Print($"[Entry] Skip — no FVG target and ATR fallback off");
            return;
        }

        double rewardDistance = Math.Abs(tpPrice - entryPrice);
        double rrr = rewardDistance / slDistance;
        if (rrr < MinRrr)
        {
            if (LogTrades) Print($"[Entry] Skip — RRR {rrr:F2} < MinRrr {MinRrr:F2} (entry={entryPrice:F5} sl={stopPrice:F5} tp={tpPrice:F5})");
            return;
        }

        double slPips = Math.Round(slDistance / Symbol.PipSize, 1);
        double tpPips = Math.Round(rewardDistance / Symbol.PipSize, 1);

        double volume = ComputeVolume(slDistance);
        if (volume <= 0.0)
        {
            if (LogTrades) Print($"[Entry] Skip — computed volume 0 (Risk%={RiskPercent} slPips={slPips})");
            return;
        }

        var result = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel, slPips, tpPips);

        if (result == null || !result.IsSuccessful || result.Position == null)
        {
            string err = result?.Error?.ToString() ?? "null result";
            Print($"[Entry] FAILED — {err} (vol={volume} sl={slPips}p tp={tpPips}p)");
            return;
        }

        var pos = result.Position;
        double riskMoney = Account.Balance * (RiskPercent / 100.0);
        var state = new OpenTradeState
        {
            EntryBarIndex     = signalBar,
            EntryPrice        = pos.EntryPrice,
            InitialStopLoss   = stopPrice,
            InitialTakeProfit = tpPrice,
            InitialRiskPrice  = slDistance,
            InitialRiskMoney  = riskMoney,
            BeMoved           = false,
            ChandelierAnchor  = pos.EntryPrice,
            Dir               = setup.Dir,
            EntryTime         = pos.EntryTime
        };
        _openStates[pos.Id] = state;

        if (LogTrades)
            Print($"[Entry] {(isLong ? "LONG " : "SHORT")} #{pos.Id} | vol={volume} | entry={pos.EntryPrice:F5} sl={stopPrice:F5} ({slPips:F1}p) tp={tpPrice:F5} ({tpPips:F1}p) | RRR={rrr:F2} | session={sessCode} | rsi1={setup.Rsi1:F1} conf={setup.Confluence}/3");
    }

    private double ResolveTakeProfit(int i, bool isLong, double entryPrice, double slDistance)
    {
        double fvg = isLong ? _gap.NearestFvgAbove[i] : _gap.NearestFvgBelow[i];
        bool fvgValid = !double.IsNaN(fvg) && (isLong ? fvg > entryPrice : fvg < entryPrice);
        if (fvgValid) return fvg;

        if (!AllowAtrTpFallback) return double.NaN;

        double atr  = _atr.Result[i];
        if (double.IsNaN(atr) || atr <= 0.0) return double.NaN;

        double dist = TpAtrFallbackMult * atr;
        return isLong ? entryPrice + dist : entryPrice - dist;
    }

    private double ComputeVolume(double slDistance)
    {
        double slPips = slDistance / Symbol.PipSize;
        if (slPips <= 0.0) return 0.0;

        double riskAmount = Account.Balance * (RiskPercent / 100.0);
        double pipValue   = Symbol.PipValue;
        if (pipValue <= 0.0) return 0.0;

        double rawVolume = riskAmount / (slPips * pipValue);
        double normalized = Symbol.NormalizeVolumeInUnits(rawVolume, RoundingMode.Down);

        if (normalized < Symbol.VolumeInUnitsMin) return 0.0;
        if (normalized > Symbol.VolumeInUnitsMax) normalized = Symbol.VolumeInUnitsMax;

        return normalized;
    }

    #endregion

    #region Trade Management

    private void ManageOpenPositions(int i)
    {
        foreach (var pos in Positions)
        {
            if (!IsOwned(pos)) continue;
            if (!_openStates.TryGetValue(pos.Id, out var state)) continue;

            int barsHeld = i - state.EntryBarIndex;

            if (MaxHoldBars > 0 && barsHeld >= MaxHoldBars)
            {
                if (LogTrades) Print($"[Mgmt] MaxHold reached #{pos.Id} ({barsHeld} bars) — closing");
                ClosePosition(pos);
                continue;
            }

            UpdateBeMove(pos, state, i);
            UpdateChandelier(pos, state, i);
        }
    }

    private void UpdateBeMove(Position pos, OpenTradeState state, int i)
    {
        if (state.BeMoved || BeTriggerR <= 0.0) return;
        if (state.InitialRiskPrice <= 0.0) return;

        double currentPrice = state.Dir == SetupDir.Bull ? Symbol.Bid : Symbol.Ask;
        double moveR = state.Dir == SetupDir.Bull
            ? (currentPrice - state.EntryPrice) / state.InitialRiskPrice
            : (state.EntryPrice - currentPrice) / state.InitialRiskPrice;

        if (moveR < BeTriggerR) return;

        double newSl = state.EntryPrice;
        bool needsMove = pos.StopLoss is null
            || (state.Dir == SetupDir.Bull  && pos.StopLoss.Value < newSl)
            || (state.Dir == SetupDir.Bear && pos.StopLoss.Value > newSl);

        if (!needsMove)
        {
            state.BeMoved = true;
            return;
        }

        var res = ModifyPosition(pos, newSl, pos.TakeProfit);
        if (res.IsSuccessful)
        {
            state.BeMoved = true;
            if (LogTrades) Print($"[Mgmt] BE move #{pos.Id} → SL={newSl:F5} (after {moveR:F2}R)");
        }
    }

    private void UpdateChandelier(Position pos, OpenTradeState state, int i)
    {
        if (ChandelierAtrMult <= 0.0) return;
        if (!state.BeMoved) return;

        double atr = _atr.Result[i];
        if (double.IsNaN(atr) || atr <= 0.0) return;

        if (state.Dir == SetupDir.Bull)
        {
            state.ChandelierAnchor = Math.Max(state.ChandelierAnchor, Bars.HighPrices[i]);
            double candidate = state.ChandelierAnchor - ChandelierAtrMult * atr;
            if (pos.StopLoss is null || candidate > pos.StopLoss.Value)
            {
                var res = ModifyPosition(pos, candidate, pos.TakeProfit);
                if (res.IsSuccessful && LogTrades)
                    Print($"[Mgmt] Chandelier #{pos.Id} → SL={candidate:F5} (anchor={state.ChandelierAnchor:F5})");
            }
        }
        else
        {
            state.ChandelierAnchor = Math.Min(state.ChandelierAnchor == 0.0 ? double.MaxValue : state.ChandelierAnchor, Bars.LowPrices[i]);
            double candidate = state.ChandelierAnchor + ChandelierAtrMult * atr;
            if (pos.StopLoss is null || candidate < pos.StopLoss.Value)
            {
                var res = ModifyPosition(pos, candidate, pos.TakeProfit);
                if (res.IsSuccessful && LogTrades)
                    Print($"[Mgmt] Chandelier #{pos.Id} → SL={candidate:F5} (anchor={state.ChandelierAnchor:F5})");
            }
        }
    }

    #endregion

    #region Risk Caps

    private void UpdateRiskCaps(int i)
    {
        DateTime now = Server.Time;
        if (now.Date != _dailyAnchorDate) ResetDailyAnchor(now);
        if (StartOfWeek(now.Date) != _weeklyAnchorDate) ResetWeeklyAnchor(now);

        double equity = Account.Equity;
        if (equity > _peakEquity) _peakEquity = equity;

        if (HardLossCapPct > 0.0)
        {
            double floor = _initialBalance * (1.0 - HardLossCapPct / 100.0);
            if (equity <= floor) Trip("HardLossCap", $"equity={equity:F2} <= floor={floor:F2}");
        }

        if (EquityTrailPct > 0.0)
        {
            double trail = _peakEquity * (1.0 - EquityTrailPct / 100.0);
            if (equity <= trail) Trip("EquityTrail", $"equity={equity:F2} <= trail={trail:F2} (peak={_peakEquity:F2})");
        }

        if (FloatingDdCapPct > 0.0)
        {
            double maxDd = _peakEquity * (FloatingDdCapPct / 100.0);
            if (_peakEquity - equity >= maxDd) Trip("FloatingDD", $"DD={_peakEquity - equity:F2} >= cap={maxDd:F2}");
        }

        if (DailyLossCapPct > 0.0)
        {
            double dailyPnL = Account.Balance - _dailyAnchorBalance;
            double cap = -_dailyAnchorBalance * (DailyLossCapPct / 100.0);
            if (dailyPnL <= cap) Trip("DailyLossCap", $"dayPnL={dailyPnL:F2} <= cap={cap:F2}");
        }

        if (WeeklyLossCapPct > 0.0)
        {
            double weeklyPnL = Account.Balance - _weeklyAnchorBalance;
            double cap = -_weeklyAnchorBalance * (WeeklyLossCapPct / 100.0);
            if (weeklyPnL <= cap) Trip("WeeklyLossCap", $"weekPnL={weeklyPnL:F2} <= cap={cap:F2}");
        }

        if (_killSwitch && HasOwnedPosition())
            CloseAllOwned("KillSwitch");
    }

    private void Trip(string reason, string detail)
    {
        if (_killSwitch) return;
        _killSwitch = true;
        _killReason = reason;
        if (LogRiskCaps) Print($"[RiskCap] TRIP — {reason} | {detail}");
    }

    private void ResetDailyAnchor(DateTime now)
    {
        _dailyAnchorDate    = now.Date;
        _dailyAnchorBalance = Account.Balance;
    }

    private void ResetWeeklyAnchor(DateTime now)
    {
        _weeklyAnchorDate    = StartOfWeek(now.Date);
        _weeklyAnchorBalance = Account.Balance;
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff).Date;
    }

    private void ParseAllowedSessions(string csv)
    {
        _allowedSessions.Clear();
        if (string.IsNullOrWhiteSpace(csv)) return;
        foreach (var raw in csv.Split(','))
        {
            if (int.TryParse(raw.Trim(), out int code) && code >= 1 && code <= 3)
                _allowedSessions.Add(code);
        }
    }

    #endregion

    #region Position Tracking

    private void OnPositionsClosed(PositionClosedEventArgs args)
    {
        var pos = args.Position;
        if (!IsOwned(pos)) return;

        _lastCloseBarIndex = Bars.Count - 1;

        double r = double.NaN;
        if (_openStates.TryGetValue(pos.Id, out var state) && state.InitialRiskMoney > 0.0)
        {
            r = pos.NetProfit / state.InitialRiskMoney;
            _rMultiples.Add(r);
        }

        _openStates.Remove(pos.Id);

        if (LogTrades)
            Print($"[Close] #{pos.Id} {pos.TradeType} | net={pos.NetProfit:F2} | R={(double.IsNaN(r) ? "?" : r.ToString("F2"))} | reason={args.Reason}");

        if (EnableTradeLogCsv && RunningMode == RunningMode.RealTime)
            AppendTradeLog(pos, r);
    }

    private bool IsOwned(Position pos)
        => string.Equals(pos.Label, BotLabel, StringComparison.Ordinal)
        && string.Equals(pos.SymbolName, SymbolName, StringComparison.Ordinal);

    private bool HasOwnedPosition()
    {
        foreach (var p in Positions)
            if (IsOwned(p)) return true;
        return false;
    }

    private void CloseAllOwned(string reason)
    {
        foreach (var p in Positions)
        {
            if (!IsOwned(p)) continue;
            ClosePosition(p);
            if (LogTrades) Print($"[Close] forced #{p.Id} — {reason}");
        }
    }

    private void RecoverOpenPositions()
    {
        foreach (var pos in Positions)
        {
            if (!IsOwned(pos)) continue;
            if (_openStates.ContainsKey(pos.Id)) continue;

            int entryBar = FindBarIndexForTime(pos.EntryTime);
            double initialRisk = pos.StopLoss is null ? 0.0 : Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
            double initialRiskMoney = 0.0;
            if (initialRisk > 0.0 && Symbol.PipSize > 0.0 && Symbol.PipValue > 0.0)
                initialRiskMoney = (initialRisk / Symbol.PipSize) * Symbol.PipValue * pos.VolumeInUnits;

            _openStates[pos.Id] = new OpenTradeState
            {
                EntryBarIndex     = entryBar,
                EntryPrice        = pos.EntryPrice,
                InitialStopLoss   = pos.StopLoss ?? double.NaN,
                InitialTakeProfit = pos.TakeProfit ?? double.NaN,
                InitialRiskPrice  = initialRisk,
                InitialRiskMoney  = initialRiskMoney,
                BeMoved           = false,
                ChandelierAnchor  = pos.EntryPrice,
                Dir               = pos.TradeType == TradeType.Buy ? SetupDir.Bull : SetupDir.Bear,
                EntryTime         = pos.EntryTime
            };

            Print($"[Recovery] re-attached #{pos.Id} {pos.TradeType} entry={pos.EntryPrice:F5} sl={pos.StopLoss} tp={pos.TakeProfit}");
        }
    }

    private int FindBarIndexForTime(DateTime t)
    {
        for (int i = Bars.Count - 1; i >= 0; i--)
            if (Bars.OpenTimes[i] <= t) return i;
        return 0;
    }

    #endregion

    #region Logging

    private void InitializeTradeLog()
    {
        try
        {
            string dir = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "cAlgo", "GapStarLogs");
            System.IO.Directory.CreateDirectory(dir);
            _tradeLogPath = System.IO.Path.Combine(dir, $"trades_{SymbolName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv");
            System.IO.File.WriteAllText(_tradeLogPath, "Id,Symbol,Side,EntryTime,ClosingTime,EntryPrice,SL,TP,Volume,NetProfit,RMultiple\n");
        }
        catch (Exception ex)
        {
            Print($"[TradeLog] init failed: {ex.Message}");
            _tradeLogPath = "";
        }
    }

    private void AppendTradeLog(Position pos, double r)
    {
        if (string.IsNullOrEmpty(_tradeLogPath)) return;
        try
        {
            string line = string.Join(",",
                pos.Id,
                pos.SymbolName,
                pos.TradeType,
                pos.EntryTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                Server.Time.ToString("yyyy-MM-ddTHH:mm:ss"),
                pos.EntryPrice.ToString("F5"),
                (pos.StopLoss ?? 0.0).ToString("F5"),
                (pos.TakeProfit ?? 0.0).ToString("F5"),
                pos.VolumeInUnits.ToString("F0"),
                pos.NetProfit.ToString("F2"),
                double.IsNaN(r) ? "" : r.ToString("F4"));
            System.IO.File.AppendAllText(_tradeLogPath, line + "\n");
        }
        catch (Exception ex)
        {
            Print($"[TradeLog] append failed: {ex.Message}");
        }
    }

    private void WriteRMultipleSummary()
    {
        if (_rMultiples.Count == 0)
        {
            Print("[Summary] no closed trades");
            return;
        }

        int n = _rMultiples.Count;
        double sum = 0.0, sumSq = 0.0;
        int wins = 0, losses = 0;
        double bestR = double.NegativeInfinity, worstR = double.PositiveInfinity;
        foreach (var r in _rMultiples)
        {
            sum   += r;
            sumSq += r * r;
            if (r > 0.0) wins++; else losses++;
            if (r > bestR)  bestR  = r;
            if (r < worstR) worstR = r;
        }
        double mean = sum / n;
        double var  = (sumSq / n) - mean * mean;
        double sd   = var > 0.0 ? Math.Sqrt(var) : 0.0;
        double sharpe = sd > 0.0 ? mean / sd * Math.Sqrt(252.0) : 0.0;
        double winRate = (double)wins / n;
        double expectancy = mean;

        Print($"[Summary] trades={n} wins={wins} losses={losses} winrate={winRate:P1}");
        Print($"[Summary] expectancy(R)={expectancy:F3} sd(R)={sd:F3} sharpe(252)={sharpe:F2} bestR={bestR:F2} worstR={worstR:F2}");

        if (_killSwitch) Print($"[Summary] killSwitch tripped: {_killReason}");
    }

    #endregion
}
