# Indicators — Mapping & Output Contract

## Summary

| Indicator       | Class                        | Problem                        | Fix                                    |
|-----------------|------------------------------|--------------------------------|----------------------------------------|
| Gap Master      | `GapRadar`                   | OK — hat Outputs + ActiveGaps  | NearestFvgAbove/Below hinzufügen       |
| RSI Master      | `MultiTimeframeRsiCandles`   | Nur Balkenfärbung, keine Series | Rsi1/2/3 + OversoldCount/OverboughtCount hinzufügen |
| Regime Master   | `RegimeMaster`               | Nur Boxen, keine Series         | RegimeSignal (1/-1) hinzufügen         |
| Session Master  | `SessionAndLiquidityLevelsUTC` | Nur visuell, keine Series     | CurrentSession + SessionHigh/Low hinzufügen |

---

## Gap Master (`GapRadar`)

**Outputs (bot-relevant):**
- `NearestGapPrice` — Mid des Gaps mit höchster Trefferwahrscheinlichkeit
- `NearestGapProb`  — Wahrscheinlichkeit (0–1)
- `NearestGapType`  — Enum-Wert (4 = FVG)
- `GapsAbove` / `GapsBelow` — Anzahl aktiver Gaps je Seite
- `NearestFvgAbove` — Mid des nächsten aktiven FVG über Preis → TP für Longs
- `NearestFvgBelow` — Mid des nächsten aktiven FVG unter Preis → TP für Shorts

**Public API:** `ActiveGaps` (List\<Gap>) — bot kann direkt filtern

**FVG Direktions-Logik:**
- Bullish FVG (`bar2High < bar0Low`): Gap unter aktuellem Preis, `FillsFromAbove=true` (Preis fällt rein)
- Bearish FVG (`bar2Low > bar0High`): Gap über aktuellem Preis, `FillsFromAbove=false` (Preis steigt rein)
- Für Long-TP: NearestFvgAbove (bearish FVG über Preis)
- Für Short-TP: NearestFvgBelow (bullish FVG unter Preis)

---

## RSI Master (`MultiTimeframeRsiCandles`)

**Outputs (neu):**
- `Rsi1Out` — RSI aktueller TF
- `Rsi2Out` — RSI TF2
- `Rsi3Out` — RSI TF3
- `OversoldCountOut`   — Anzahl TFs mit RSI ≤ OversoldLevel (0–3)
- `OverboughtCountOut` — Anzahl TFs mit RSI ≥ OverboughtLevel (0–3)

**Bot-Logik:** Entry wenn OversoldCount ≥ 2 (Long) / OverboughtCount ≥ 2 (Short)

---

## Regime Master (`RegimeMaster`)

**Output (neu):**
- `RegimeSignal` — 1.0 = bullish (Close > SMA), -1.0 = bearish, NaN = Warmup

**Bot-Logik:** Long nur wenn RegimeSignal ≥ 0 (oder ungefiltered für Mean-Rev), Short nur wenn RegimeSignal ≤ 0

---

## Session Master (`SessionAndLiquidityLevelsUTC`)

**Outputs (neu):**
- `CurrentSessionOut` — 0=keine, 1=HK, 2=London, 3=NY
- `SessionHighOut`    — Aktuelles Session-Hoch (NaN wenn keine Session)
- `SessionLowOut`     — Aktuelles Session-Tief (NaN wenn keine Session)

**Bot-Logik:** Trading-Fenster = London (2) + NY (3), HK nur optional

---

## Bot-Signal-Aggregation (Entwurf)

```
Long-Entry:
  OversoldCount >= 2
  NearestFvgAbove vorhanden (TP-Ziel)
  RRR >= MinRrr
  Session = London oder NY (optional)

Short-Entry:
  OverboughtCount >= 2
  NearestFvgBelow vorhanden (TP-Ziel)
  RRR >= MinRrr
  Session = London oder NY (optional)
```
