// ═══════════════════════════════════════════════════════════════════════════════
//  TenFoldBot – Scoring Engine (partial class)
//  CalculateEntryScore dispatcher, 9 Score* modules, pivot helpers, VWAP.
//  Each Score* module returns 0–3 points capped by its *MaxWeight parameter.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using cAlgo.API;

namespace cAlgo.Robots
{
    public partial class TenFoldBot
    {
        #region Scoring Engine
        // ════════════════════════════════════════════════════════════════════
        //  ENTRY SCORING DISPATCHER
        //  logVerbose = true  → volle Prints (OnBar / echte Entry-Entscheidung)
        //  logVerbose = false → kein Print    (Dashboard/OnTick – Anti-Spam)
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Aggregates module scores for <paramref name="direction"/>. Applies optional
        /// category caps, writes per-module scores into the directional cache, and
        /// returns the total.
        /// </summary>
        /// <param name="direction">Buy or Sell — scoring is directional.</param>
        /// <param name="logVerbose">
        /// When true, modules may print their breakdowns (OnBar path). Set to false
        /// for Dashboard / OnTick reads to avoid log spam.
        /// </param>
        private int CalculateEntryScore(TradeType direction, bool logVerbose = true)
        {
            int score = 0;
            int[] cache = direction == TradeType.Buy ? _cachedLongModuleScores : _cachedShortModuleScores;

            cache[0] = EnableEmaModule        ? ScoreEma(direction, logVerbose) : 0;
            score += cache[0];
            cache[1] = EnableBbModule         ? ScoreBollingerBands(direction, logVerbose) : 0;
            score += cache[1];
            cache[2] = EnableSupertrendModule ? ScoreSupertrend(direction, logVerbose) : 0;
            score += cache[2];
            cache[3] = EnablePatternsModule   ? ScorePatterns(direction, logVerbose) : 0;
            score += cache[3];
            cache[4] = EnableFiboModule       ? ScoreFibonacci(direction, logVerbose) : 0;
            score += cache[4];
            cache[5] = EnableOscModule        ? ScoreOscillators(direction, logVerbose) : 0;
            score += cache[5];
            cache[6] = EnableSrModule         ? ScoreSupportResistance(direction, logVerbose) : 0;
            score += cache[6];
            cache[7] = EnableMacdModule       ? ScoreMacd(direction, logVerbose) : 0;
            score += cache[7];
            cache[8] = EnableAdxScoreModule && _dms != null ? ScoreAdx(direction, logVerbose) : 0;
            score += cache[8];

            // v2.12.0 – Apply category caps if enabled
            if (EnableCategoryCaps)
            {
                int trendRaw = cache[0] + cache[2] + cache[7]; // EMA + ST + MACD
                int mrRaw    = cache[1] + cache[4] + cache[6]; // BB + FIB + SR
                int momRaw   = cache[5];                        // OSC
                int paRaw    = cache[3];                        // PA

                int trendCapped = Math.Min(trendRaw, TrendCategoryCap);
                int mrCapped    = Math.Min(mrRaw,    MeanReversionCategoryCap);
                int momCapped   = Math.Min(momRaw,   MomentumCategoryCap);
                int paCapped    = Math.Min(paRaw,    PriceActionCategoryCap);

                score = trendCapped + mrCapped + momCapped + paCapped;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("Score [{0}]: EMA+BB+ST+PA+FIB+OSC+SR+MACD = {1}/{2}", direction, score, _maxPossibleScore);

            return score;
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: EMA
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// EMA module — 3-point scoring on closed-bar EMA values:
        ///  (1) Context:  close above/below slow EMA,
        ///  (2) Strength: fast EMA above/below slow and rising/falling,
        ///  (3) Timing:   pullback touched fast EMA (within 5% buffer) and recovered.
        /// Result capped at <see cref="EmaMaxWeight"/>.
        /// </summary>
        private int ScoreEma(TradeType direction, bool logVerbose = true)
        {
            int pts = 0;

            double fastNow  = _emaFast.Result.Last(1);  // repaint-safe: closed bar only
            double slowNow  = _emaSlow.Result.Last(1);  // repaint-safe: closed bar only
            double fastPrev = _emaFast.Result.Last(2);  // bar before fastNow (closed)
            double closeNow = Bars.ClosePrices.Last(1); // repaint-safe: closed bar only
            double lowPrev  = Bars.LowPrices.Last(1);
            double highPrev = Bars.HighPrices.Last(1);

            double emaBuffer = Math.Max(Math.Abs(fastNow - slowNow) * 0.05, 0.1 * Symbol.PipSize);

            if (direction == TradeType.Buy)
            {
                if (closeNow > slowNow)                                     pts++;
                if (fastNow > slowNow && fastNow > fastPrev)                pts++;
                if (lowPrev <= fastNow + emaBuffer && closeNow > fastNow)   pts++;
            }
            else
            {
                if (closeNow < slowNow)                                     pts++;
                if (fastNow < slowNow && fastNow < fastPrev)                pts++;
                if (highPrev >= fastNow - emaBuffer && closeNow < fastNow)  pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [EMA] dir={0} fast={1:F5} slow={2:F5} close={3:F5} pts={4}",
                    direction, fastNow, slowNow, closeNow, pts);

            return Math.Min(pts, EmaMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Bollinger Bands
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Bollinger Bands module — 3-point mean-reversion scoring:
        ///  (1) Context:  close on the favoured side of the middle band,
        ///  (2) Strength: prior bar touched the opposite band (deep pullback),
        ///  (3) Timing:   close re-entered the band (bounce confirmation).
        /// Result capped at <see cref="BbMaxWeight"/>.
        /// </summary>
        private int ScoreBollingerBands(TradeType direction, bool logVerbose = true)
        {
            int pts = 0;

            double upperBand  = _bollingerBands.Top.Last(1);     // repaint-safe: closed bar only
            double lowerBand  = _bollingerBands.Bottom.Last(1);  // repaint-safe: closed bar only
            double middleBand = _bollingerBands.Main.Last(1);    // repaint-safe: closed bar only
            double closeNow   = Bars.ClosePrices.Last(1);        // repaint-safe: closed bar only
            double closePrev  = Bars.ClosePrices.Last(2);        // bar before closeNow (closed)
            double lowPrev    = Bars.LowPrices.Last(1);
            double highPrev   = Bars.HighPrices.Last(1);

            if (direction == TradeType.Buy)
            {
                // (1) Kontext: Kurs ist unterhalb der BB-Mittellinie (untere Haelfte)
                //     Kompatibel mit EMA-Pullbacks – kein Band-Touch erforderlich
                if (closeNow < middleBand) pts++;

                // (2) Staerke: vorheriger Bar hat die untere Band beruehrt (starker Pullback)
                if (lowPrev <= lowerBand) pts++;

                // (3) Timing: Bounce-Bestaetigung – Close kehrt von unterhalb der Band zurueck
                if (closePrev <= lowerBand && closeNow > lowerBand) pts++;
            }
            else
            {
                // (1) Kontext: Kurs ist oberhalb der BB-Mittellinie
                if (closeNow > middleBand) pts++;

                // (2) Staerke: vorheriger Bar hat die obere Band beruehrt
                if (highPrev >= upperBand) pts++;

                // (3) Timing: Bounce-Bestaetigung von oberhalb zurueck
                if (closePrev >= upperBand && closeNow < upperBand) pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [BB] dir={0} upper={1:F5} lower={2:F5} close={3:F5} pts={4}",
                    direction, upperBand, lowerBand, closeNow, pts);

            return Math.Min(pts, BbMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  UpdateSupertrendState  – einmal pro Bar in OnBar() aufrufen
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Advances the rolling Supertrend state for the latest closed bar.
        /// Must be called exactly once per bar in <c>OnBar</c>. Sets
        /// <c>_stFlippedThisBar</c> when the trend direction flips.
        /// </summary>
        private void UpdateSupertrendState()
        {
            if (_atrSupertrend == null || Bars.Count < SupertrendAtrPeriod + 2) return;

            int    i   = 1;
            double atr = _atrSupertrend.Result.Last(i);
            if (double.IsNaN(atr) || atr <= 0) return;

            double hl2      = (Bars.HighPrices.Last(i) + Bars.LowPrices.Last(i)) / 2.0;
            double rawUpper = hl2 + SupertrendFactor * atr;
            double rawLower = hl2 - SupertrendFactor * atr;
            double prevClose = Bars.ClosePrices.Last(2);

            double newUpper, newLower;
            if (!_stInitialized)
            {
                newUpper       = rawUpper;
                newLower       = rawLower;
                _stTrend       = Bars.ClosePrices.Last(i) > hl2 ? 1 : -1;
                _stInitialized = true;
            }
            else
            {
                newUpper = prevClose > _stFinalUpperBand ? rawUpper : Math.Min(rawUpper, _stFinalUpperBand);
                newLower = prevClose < _stFinalLowerBand ? rawLower : Math.Max(rawLower, _stFinalLowerBand);
            }

            double closeNow  = Bars.ClosePrices.Last(i);
            int    prevTrend = _stTrend;

            if (_stTrend == -1 && closeNow > newUpper)      _stTrend = 1;
            else if (_stTrend == 1 && closeNow < newLower)  _stTrend = -1;

            _stFlippedThisBar = (_stTrend != prevTrend);
            _stFinalUpperBand = newUpper;
            _stFinalLowerBand = newLower;
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Supertrend
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Supertrend module — 3-point trend scoring from rolling state:
        ///  (1) Context:  Supertrend direction matches <paramref name="direction"/>,
        ///  (2) Strength: close beyond the opposite band by ≥ 0.5 × ATR,
        ///  (3) Timing:   trend flipped on this bar (fresh signal).
        /// Relies on state updated by <see cref="UpdateSupertrendState"/>.
        /// </summary>
        private int ScoreSupertrend(TradeType direction, bool logVerbose = true)
        {
            if (!_stInitialized) return 0;

            int    pts      = 0;
            double closeNow = Bars.ClosePrices.Last(1);  // repaint-safe: closed bar only
            double atrNow   = (_atrSupertrend != null && !double.IsNaN(_atrSupertrend.Result.Last(1)))
                              ? _atrSupertrend.Result.Last(1) : 0;  // repaint-safe: closed bar only

            if (direction == TradeType.Buy)
            {
                if (_stTrend == 1)                                                                       pts++;
                if (_stTrend == 1 && atrNow > 0 && closeNow > _stFinalLowerBand + atrNow * 0.5)        pts++;
                if (_stFlippedThisBar && _stTrend == 1)                                                 pts++;
            }
            else
            {
                if (_stTrend == -1)                                                                      pts++;
                if (_stTrend == -1 && atrNow > 0 && closeNow < _stFinalUpperBand - atrNow * 0.5)       pts++;
                if (_stFlippedThisBar && _stTrend == -1)                                                pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [ST] dir={0} trend={1} flip={2} upperBand={3:F5} lowerBand={4:F5} pts={5}",
                    direction, _stTrend, _stFlippedThisBar, _stFinalUpperBand, _stFinalLowerBand, pts);

            return Math.Min(pts, SupertrendMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Price Action Patterns
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Price-action module — scans the last <see cref="PatternLookback"/> bars
        /// for pin-bars, engulfing patterns and inside-bars. Each detected pattern
        /// contributes one point. Result capped at <see cref="PatternsMaxWeight"/>.
        /// </summary>
        private int ScorePatterns(TradeType direction, bool logVerbose = true)
        {
            int lb = Math.Min(PatternLookback, Bars.Count - 3);
            if (lb < 1) return 0;

            bool hasPinBar    = false;
            bool hasEngulfing = false;
            bool hasInsideBar = false;

            for (int i = 1; i <= lb; i++)
            {
                double open  = Bars.OpenPrices.Last(i);
                double high  = Bars.HighPrices.Last(i);
                double low   = Bars.LowPrices.Last(i);
                double close = Bars.ClosePrices.Last(i);
                double range = high - low;
                if (range <= Symbol.PipSize * 0.5) continue;

                double body      = Math.Abs(close - open);
                double upperWick = high - Math.Max(open, close);
                double lowerWick = Math.Min(open, close) - low;

                if (!hasPinBar && body <= range * 0.25)
                {
                    if (direction == TradeType.Buy  && lowerWick >= range * 0.60) hasPinBar = true;
                    if (direction == TradeType.Sell && upperWick >= range * 0.60) hasPinBar = true;
                }

                if (!hasEngulfing && i + 1 < Bars.Count)
                {
                    double prevOpen  = Bars.OpenPrices.Last(i + 1);
                    double prevClose = Bars.ClosePrices.Last(i + 1);

                    if (direction == TradeType.Buy
                        && close > open
                        && open  <= Math.Min(prevOpen, prevClose)
                        && close >= Math.Max(prevOpen, prevClose))
                        hasEngulfing = true;

                    if (direction == TradeType.Sell
                        && close < open
                        && open  >= Math.Max(prevOpen, prevClose)
                        && close <= Math.Min(prevOpen, prevClose))
                        hasEngulfing = true;
                }

                if (!hasInsideBar && i + 1 < Bars.Count)
                {
                    double prevHigh = Bars.HighPrices.Last(i + 1);
                    double prevLow  = Bars.LowPrices.Last(i + 1);
                    if (high <= prevHigh && low >= prevLow) hasInsideBar = true;
                }

                if (hasPinBar && hasEngulfing && hasInsideBar) break;
            }

            int pts = 0;
            if (hasInsideBar)  pts++;  // (1) Context
            if (hasEngulfing)  pts++;  // (2) Strength
            if (hasPinBar)     pts++;  // (3) Timing

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [PA] dir={0} pinBar={1} engulfing={2} insideBar={3} pts={4}",
                    direction, hasPinBar, hasEngulfing, hasInsideBar, pts);

            return Math.Min(pts, PatternsMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Fibonacci Retracement
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Fibonacci retracement module — identifies a swing (impulse or legacy range)
        /// and checks whether the current close sits inside the 38.2 / 50 / 61.8
        /// retracement zone within <see cref="FiboTolerancePercent"/>. Deeper zones
        /// score more.
        /// </summary>
        private int ScoreFibonacci(TradeType direction, bool logVerbose = true)
        {
            int lb = Math.Min(FiboSwingLookback, Bars.Count - 3);
            if (lb < 5) return 0;

            var pivots = GetRecentPivots(lb);
            if (pivots.Count < 2) return 0;

            double swingHigh = double.MinValue;
            double swingLow  = double.MaxValue;

            if (!FiboUseLegacyRange)
            {
                var (h, l) = FindLastImpulseSwing(direction, pivots);
                swingHigh = h;
                swingLow  = l;
            }

            // Legacy range OR fallback when FindLastImpulseSwing found no valid pair
            if (swingHigh == double.MinValue || swingLow == double.MaxValue)
            {
                foreach (var p in pivots)
                {
                    if (p.IsHigh  && p.Price > swingHigh) swingHigh = p.Price;
                    if (!p.IsHigh && p.Price < swingLow)  swingLow  = p.Price;
                }
            }

            // Last-resort fallback: no pivots at all
            if (swingHigh == double.MinValue || swingLow == double.MaxValue)
            {
                for (int i = 1; i <= lb; i++)
                {
                    if (Bars.HighPrices.Last(i) > swingHigh) swingHigh = Bars.HighPrices.Last(i);
                    if (Bars.LowPrices.Last(i)  < swingLow)  swingLow  = Bars.LowPrices.Last(i);
                }
            }

            double swingRange = swingHigh - swingLow;
            if (swingRange < Symbol.PipSize * 5) return 0;

            double tol   = swingRange * (FiboTolerancePercent / 100.0);
            double price = Bars.ClosePrices.Last(1); // repaint-safe: closed bar only

            double[] levels;
            if (direction == TradeType.Buy)
            {
                levels = new[]
                {
                    swingHigh - swingRange * 0.382,
                    swingHigh - swingRange * 0.500,
                    swingHigh - swingRange * 0.618,
                };
            }
            else
            {
                levels = new[]
                {
                    swingLow + swingRange * 0.382,
                    swingLow + swingRange * 0.500,
                    swingLow + swingRange * 0.618,
                };
            }

            bool near382 = Math.Abs(price - levels[0]) <= tol;
            bool near500 = Math.Abs(price - levels[1]) <= tol;
            bool near618 = Math.Abs(price - levels[2]) <= tol;

            int pts = 0;
            if (near382 || near500 || near618) pts++;  // (1) Context
            if (near618 || near500)            pts++;  // (2) Strength
            if (near618)                       pts++;  // (3) Timing

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [FIB] dir={0} hi={1:F5} lo={2:F5} 382={3:F5} 500={4:F5} 618={5:F5} near={6}/{7}/{8} pts={9}",
                    direction, swingHigh, swingLow, levels[0], levels[1], levels[2],
                    near382, near500, near618, pts);

            return Math.Min(pts, FiboMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Oscillators (RSI + Stochastic)
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// Oscillators module — combines RSI context / extreme zone and a Stochastic
        /// %K/%D cross for 3-point scoring. Cross detection uses closed-bar values.
        /// </summary>
        private int ScoreOscillators(TradeType direction, bool logVerbose = true)
        {
            if (_rsi == null && _stochastic == null) return 0;

            double rsiNow = _rsi        != null ? _rsi.Result.Last(1)            : 50.0;  // repaint-safe
            double kNow   = _stochastic != null ? _stochastic.PercentK.Last(1)   : 50.0;  // repaint-safe
            double dNow   = _stochastic != null ? _stochastic.PercentD.Last(1)   : 50.0;  // repaint-safe
            double kPrev  = _stochastic != null ? _stochastic.PercentK.Last(2)   : 50.0;  // bar before kNow
            double dPrev  = _stochastic != null ? _stochastic.PercentD.Last(2)   : 50.0;  // bar before dNow

            int pts = 0;

            if (direction == TradeType.Buy)
            {
                // (1) Kontext: RSI unter 50 (bearishes Momentum, kein Extremwert noetig)
                //     Kompatibel mit EMA-Pullbacks bei RSI 40-49
                if (rsiNow < 50.0)                      pts++;
                // (2) Staerke: echter Oversold-Bereich
                if (rsiNow < RsiOversold)               pts++;
                // (3) Timing: Stochastik K/D bullisher Kreuzung
                if (kPrev <= dPrev && kNow > dNow)      pts++;
            }
            else
            {
                // (1) Kontext: RSI ueber 50 (bullishes Momentum, kein Extremwert noetig)
                if (rsiNow > 50.0)                      pts++;
                // (2) Staerke: echter Overbought-Bereich
                if (rsiNow > RsiOverbought)             pts++;
                // (3) Timing: Stochastik K/D bearisher Kreuzung
                if (kPrev >= dPrev && kNow < dNow)      pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [OSC] dir={0} rsi={1:F1}(os={2}/ob={3}) stochK={4:F1} stochD={5:F1} kCross={6} pts={7}",
                    direction, rsiNow, RsiOversold, RsiOverbought,
                    kNow, dNow,
                    direction == TradeType.Buy ? (kPrev <= dPrev && kNow > dNow) : (kPrev >= dPrev && kNow < dNow),
                    pts);

            return Math.Min(pts, OscMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: Support / Resistance + VWAP
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// S/R + VWAP module — 3-point scoring combining pivot proximity, pivot
        /// clustering and VWAP alignment. Uses <c>_cachedVwap</c> produced by
        /// <see cref="UpdateVwapIncremental"/>.
        /// </summary>
        private int ScoreSupportResistance(TradeType direction, bool logVerbose = true)
        {
            double price    = Bars.ClosePrices.Last(1); // repaint-safe: closed bar only
            double tolPrice = SrZoneTolerance * Symbol.PipSize;

            double vwap     = _cachedVwap;
            bool   nearVwap = vwap > 0 && Math.Abs(price - vwap) <= tolPrice * 2.0;

            int  lb             = Math.Min(50, Bars.Count - 3);
            var  pivots         = GetRecentPivots(lb);
            bool nearSupport    = false;
            bool nearResistance = false;
            int  srClusterCount = 0;

            foreach (var p in pivots)
            {
                if (!p.IsHigh && Math.Abs(price - p.Price) <= tolPrice)
                {
                    nearSupport = true;
                    srClusterCount++;
                }
                if (p.IsHigh && Math.Abs(price - p.Price) <= tolPrice)
                {
                    nearResistance = true;
                    srClusterCount++;
                }
            }

            int pts = 0;
            if (direction == TradeType.Buy)
            {
                if (nearSupport)             pts++;
                if (srClusterCount >= 2)     pts++;
                if (nearVwap && nearSupport) pts++;
            }
            else
            {
                if (nearResistance)               pts++;
                if (srClusterCount >= 2)          pts++;
                if (nearVwap && nearResistance)   pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [SR] dir={0} nearSup={1} nearRes={2} cluster={3} vwap={4:F5} nearVwap={5} pts={6}",
                    direction, nearSupport, nearResistance, srClusterCount, vwap, nearVwap, pts);

            return Math.Min(pts, SrMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: MACD
        //   (1) Context : MACD-Line vs Signal (bullish/bearish Basis)
        //   (2) Strength: Histogram wächst in Richtung (Momentum beschleunigt)
        //   (3) Timing  : Histogram hat gerade die Nulllinie in Richtung gekreuzt
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// MACD module — 3-point momentum scoring on closed bars:
        ///  (1) Context:  MACD-line vs signal,
        ///  (2) Strength: histogram expansion in the signal direction,
        ///  (3) Timing:   MACD-line zero-cross this bar.
        /// </summary>
        private int ScoreMacd(TradeType direction, bool logVerbose = true)
        {
            if (_macd == null) return 0;

            double histNow  = _macd.Histogram.Last(1);  // repaint-safe: closed bar only
            double histPrev = _macd.Histogram.Last(2);  // bar before histNow (closed)
            double sigNow   = _macd.Signal.Last(1);     // repaint-safe: closed bar only
            double sig2     = _macd.Signal.Last(2);     // bar before sigNow (closed)

            if (double.IsNaN(histNow) || double.IsNaN(histPrev) || double.IsNaN(sigNow) || double.IsNaN(sig2))
                return 0;

            // MACD-Line rekonstruiert aus Histogram + Signal (Standard-Formel)
            double macdNow  = histNow  + sigNow;
            double macdPrev = histPrev + sig2;

            int pts = 0;

            if (direction == TradeType.Buy)
            {
                if (macdNow > sigNow)                               pts++; // (1) Context
                if (histNow > histPrev)                             pts++; // (2) Strength
                if (macdPrev <= 0 && macdNow > 0)                   pts++; // (3) Timing (Zero-Cross up)
            }
            else
            {
                if (macdNow < sigNow)                               pts++;
                if (histNow < histPrev)                             pts++;
                if (macdPrev >= 0 && macdNow < 0)                   pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [MACD] dir={0} macd={1:F6} sig={2:F6} hist={3:F6} (prev={4:F6}) pts={5}",
                    direction, macdNow, sigNow, histNow, histPrev, pts);

            return Math.Min(pts, MacdMaxWeight);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SCORING MODULE: ADX (v2.12.0)
        //  (1) ADX >= MinAdxValue: basic trend present
        //  (2) ADX >= 25: strong trend zone
        //  (3) DI+ > DI- (long) / DI- > DI+ (short): direction confirmation
        // ════════════════════════════════════════════════════════════════════
        /// <summary>
        /// ADX scoring module — 3-point trend-strength scoring:
        ///  (1) Context:  ADX ≥ <see cref="MinAdxValue"/> (trend exists),
        ///  (2) Strength: ADX ≥ 25 (strong trend zone),
        ///  (3) Timing:   DI± aligned with <paramref name="direction"/>.
        /// Only active when <see cref="EnableAdxScoreModule"/> is set. Distinct
        /// from the ADX gate in <c>IsMarketTradable</c>.
        /// </summary>
        private int ScoreAdx(TradeType direction, bool logVerbose = true)
        {
            if (_dms == null) return 0;

            double adxVal  = _dms.ADX.Last(1);  // repaint-safe: closed bar only
            double diPlus  = _dms.DIPlus.Last(1);  // repaint-safe: closed bar only
            double diMinus = _dms.DIMinus.Last(1); // repaint-safe: closed bar only

            if (double.IsNaN(adxVal)) return 0;

            int pts = 0;

            // (1) ADX above minimum (basic trend present)
            if (adxVal >= MinAdxValue) pts++;

            // (2) ADX in "strong trend" zone (above 25 typical threshold)
            if (adxVal >= 25.0) pts++;

            // (3) DI alignment matches direction
            if (!double.IsNaN(diPlus) && !double.IsNaN(diMinus))
            {
                bool aligned = direction == TradeType.Buy ? diPlus > diMinus : diMinus > diPlus;
                if (aligned) pts++;
            }

            if (EnableVerboseScoreLogging && logVerbose)
                Print("  [ADX] dir={0} adx={1:F1} DI+={2:F1} DI-={3:F1} pts={4}",
                    direction, adxVal, diPlus, diMinus, pts);

            return Math.Min(pts, AdxScoreMaxWeight);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ComputeDailyVwap
        // ─────────────────────────────────────────────────────────────────────
        private double ComputeDailyVwap()
        {
            double   sumTpVol    = 0;
            double   sumVol      = 0;
            DateTime today       = Server.Time.Date;
            int      maxLookback = Math.Min(Bars.Count - 1, 1440);

            for (int i = 0; i < maxLookback; i++)
            {
                if (Bars.OpenTimes.Last(i).Date < today) break;

                double tp  = (Bars.HighPrices.Last(i) + Bars.LowPrices.Last(i) + Bars.ClosePrices.Last(i)) / 3.0;
                double vol = Bars.TickVolumes.Last(i);
                if (vol <= 0) vol = 1;

                sumTpVol += tp * vol;
                sumVol   += vol;
            }

            return sumVol > 0 ? sumTpVol / sumVol : 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UpdateVwapIncremental (v2.12.0)
        //  Incremental VWAP statt O(480) Loop pro Bar: full rescan nur bei Tagswechsel,
        //  dann nur +1 Bar pro Tick. Called from OnBar(), caches result in _cachedVwap.
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>
        /// Updates <c>_cachedVwap</c> incrementally. Full rescan on day rollover,
        /// O(1) add for new bars within the same day.
        /// </summary>
        private void UpdateVwapIncremental()
        {
            if (!EnableSrModule) return;

            DateTime barTime = Bars.OpenTimes.Last(1); // closed bar time
            DateTime barDate = barTime.Date;

            if (barDate != _vwapLastDate)
            {
                // New day: full rescan of all today's closed bars
                _vwapSumTpVol = 0;
                _vwapSumVol   = 0;
                _vwapLastDate = barDate;
                _vwapLastBarTime = DateTime.MinValue;

                int maxLb = Math.Min(Bars.Count - 2, 1440);
                for (int i = maxLb; i >= 1; i--)
                {
                    if (Bars.OpenTimes.Last(i).Date != barDate) continue;
                    double tp  = (Bars.HighPrices.Last(i) + Bars.LowPrices.Last(i) + Bars.ClosePrices.Last(i)) / 3.0;
                    double vol = Math.Max(Bars.TickVolumes.Last(i), 1);
                    _vwapSumTpVol += tp * vol;
                    _vwapSumVol   += vol;
                }
                _vwapLastBarTime = barTime;
            }
            else if (barTime != _vwapLastBarTime)
            {
                // Same day, new bar: incrementally add Last(1)
                double tp  = (Bars.HighPrices.Last(1) + Bars.LowPrices.Last(1) + Bars.ClosePrices.Last(1)) / 3.0;
                double vol = Math.Max(Bars.TickVolumes.Last(1), 1);
                _vwapSumTpVol += tp * vol;
                _vwapSumVol   += vol;
                _vwapLastBarTime = barTime;
            }

            _cachedVwap = _vwapSumVol > 0 ? _vwapSumTpVol / _vwapSumVol : 0;
        }
        #endregion // Scoring Engine

        // ─────────────────────────────────────────────────────────────────────
        //  GetRecentPivots – zentrale Pivot-Erkennung (ersetzt redundante Loops
        //  in ScoreFibonacci, ScoreSupportResistance und FindSwingLevel)
        //
        //  leftRightStrength: Wie viele Bars links UND rechts must der Pivot
        //                     ein lokales Extremum sein (Standard = 1).
        // ─────────────────────────────────────────────────────────────────────
        // v2.12.0 – Pivot-Cache-Wrapper: reduziert redundante Berechnungen innerhalb einer Bar
        private List<PivotPoint> GetRecentPivots(int lookbackBars, int leftRightStrength = 1)
        {
            DateTime barTime = Bars.OpenTimes.LastValue;
            if (barTime != _pivotCacheBarTime)
            {
                _pivotCache.Clear();
                _pivotCacheBarTime = barTime;
            }

            int cacheKey = lookbackBars * 10 + leftRightStrength;
            List<PivotPoint> cached;
            if (!_pivotCache.TryGetValue(cacheKey, out cached))
            {
                cached = ComputePivots(lookbackBars, leftRightStrength);
                _pivotCache[cacheKey] = cached;
            }
            return cached;
        }

        private List<PivotPoint> ComputePivots(int lookbackBars, int leftRightStrength = 1)
        {
            var result = new List<PivotPoint>();
            int safe   = Math.Min(lookbackBars, Bars.Count - leftRightStrength - 2);

            for (int i = leftRightStrength + 1; i <= safe; i++)
            {
                double h = Bars.HighPrices.Last(i);
                bool isPivotHigh = true;
                bool isPivotLow  = true;
                double l = Bars.LowPrices.Last(i);

                for (int offset = 1; offset <= leftRightStrength; offset++)
                {
                    if (h <= Bars.HighPrices.Last(i - offset) || h <= Bars.HighPrices.Last(i + offset))
                        isPivotHigh = false;
                    if (l >= Bars.LowPrices.Last(i - offset)  || l >= Bars.LowPrices.Last(i + offset))
                        isPivotLow = false;
                }

                if (isPivotHigh) result.Add(new PivotPoint(h, i, isHigh: true));
                if (isPivotLow)  result.Add(new PivotPoint(l, i, isHigh: false));
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  FindLastImpulseSwing
        //  Finds the most recent coherent impulse move instead of max/min over
        //  the entire lookback (which spans multiple unrelated swings).
        //  LONG : last down-impulse (pivot-high → subsequent pivot-low)
        //  SHORT: last up-impulse  (pivot-low  → subsequent pivot-high)
        //  Returns (swingHigh, swingLow); (MinValue, MaxValue) on failure.
        // ─────────────────────────────────────────────────────────────────────
        private (double high, double low) FindLastImpulseSwing(TradeType direction, List<PivotPoint> pivots)
        {
            if (direction == TradeType.Buy)
            {
                PivotPoint lastLow = null;
                foreach (var p in pivots)
                    if (!p.IsHigh && (lastLow == null || p.Index < lastLow.Index))
                        lastLow = p;
                if (lastLow == null) return (double.MinValue, double.MaxValue);

                PivotPoint priorHigh = null;
                foreach (var p in pivots)
                    if (p.IsHigh && p.Index > lastLow.Index
                        && (priorHigh == null || p.Index < priorHigh.Index))
                        priorHigh = p;
                if (priorHigh == null) return (double.MinValue, double.MaxValue);

                return (priorHigh.Price, lastLow.Price);
            }
            else
            {
                PivotPoint lastHigh = null;
                foreach (var p in pivots)
                    if (p.IsHigh && (lastHigh == null || p.Index < lastHigh.Index))
                        lastHigh = p;
                if (lastHigh == null) return (double.MinValue, double.MaxValue);

                PivotPoint priorLow = null;
                foreach (var p in pivots)
                    if (!p.IsHigh && p.Index > lastHigh.Index
                        && (priorLow == null || p.Index < priorLow.Index))
                        priorLow = p;
                if (priorLow == null) return (double.MinValue, double.MaxValue);

                return (lastHigh.Price, priorLow.Price);
            }
        }

        /// <summary>
        /// Vollständige Score-Aufschlüsselung per Modul.
        /// logVerbose: false auf alle internen Modul-Aufrufe, damit
        /// kein Kaskaden-Spam entsteht. Der eigene Print ist immer aktiv.
        /// </summary>
        private void LogScoreBreakdown(TradeType direction, int totalScore)
        {
            int[] cache = direction == TradeType.Buy ? _cachedLongModuleScores : _cachedShortModuleScores;
            Print("[ScoreBreakdown {0}] Total={1}/{2} (min={3}) | " +
                  "EMA={4} BB={5} ST={6} PA={7} FIB={8} OSC={9} SR={10} MACD={11} ADX={12}",
                direction, totalScore, _maxPossibleScore, _minRequiredScore,
                EnableEmaModule        ? cache[0].ToString() : "off",
                EnableBbModule         ? cache[1].ToString() : "off",
                EnableSupertrendModule ? cache[2].ToString() : "off",
                EnablePatternsModule   ? cache[3].ToString() : "off",
                EnableFiboModule       ? cache[4].ToString() : "off",
                EnableOscModule        ? cache[5].ToString() : "off",
                EnableSrModule         ? cache[6].ToString() : "off",
                EnableMacdModule       ? cache[7].ToString() : "off",
                EnableAdxScoreModule   ? cache[8].ToString() : "off");
        }
    }
}
