---
name: Bot Template Section Map
description: Line-range index of every major section in Bot Template.cs — read by offset, not by full file load
type: reference
scope: Bot Template / cTrader strategies
---

# Bot Template — Section Map

`Bot Template/Bot Template.cs` is **1665 lines**. Reading the whole file costs ~25k tokens. **Don't.** This map gives you the line range for every meaningful section so you can use `Read offset:N limit:M`.

## Where to add strategy code

**One place only:** `TryEnterStrategy(regime, htfBias)` at lines **600–605**. It's a `protected virtual bool` returning `false` by default. Replace the body with your signal logic and call `OpenTrade(...)` to enter.

If your strategy needs custom indicators (RSI, Bollinger, custom-period EMA, etc.), initialize them in the `OnStartStrategy()` override at line **608** — also a virtual no-op by default.

## Top-level layout

| Lines | Section | Read when |
|-------|---------|-----------|
| 1–28   | File header + usings + namespace | Never (boilerplate) |
| 23–25  | Public enums: `TradeRegime`, `TradeSetupKind`, `RiskBaseKind` | Adding a new setup kind |
| 30–46  | Internal structs: `SessionKey`, `SessionStats` | Almost never |
| 47–71  | `BotTradeState` class — per-trade in-memory state | Adding a tracked field per trade |
| 73–78  | `[Robot]` declaration + class opening | Never |

## Sections inside the `BotTemplate` class

All line numbers are 1-based and approximate (±2 lines after edits). The `═══` banners mark each section.

| Lines | Banner | Content | Read when |
|-------|--------|---------|-----------|
| 78–112  | CONSTANTS | Magic numbers + `CANONICAL_DEFAULTS` dict | Tuning canonical defaults |
| 114–349 | PARAMETERS | All ~59 `[Parameter]` properties, grouped by `00 · Core` … `13 · Logging` | Adding/removing a parameter |
| 352–397 | PRIVATE STATE | All instance fields (indicators, counters, dicts) | Adding tracked state |
| 400–453 | ON START | Initialization, indicator setup, warmup, recovery hookup | Adding new indicators or startup logic |
| 455–476 | ON STOP | Final attribution emit + JSON/CSV persistence | Almost never |
| 482–545 | ON BAR | Per-bar gate flow → strategy hook | Changing the gate ordering |
| 549–578 | ON TICK | Trade management on every tick (BE, partials, chandelier, MaxHold, hard-loss) | Adding tick-time management |
| **580–608** | **STRATEGY HOOK** | `TryEnterStrategy` (virtual, default returns false) + `OnStartStrategy` | **Implementing your strategy** |
| 610–656 | VALIDATION | `ValidateParameters()` + `ValidateParameterLock()` | Adding param invariants |
| 659–691 | GATES | `IsMarketTradable()` + `Reject()` (DD/session/spread/news) | Loosening or tightening a gate |
| 694–779 | REGIME CLASSIFIER | Daily ATR vs N-day median with asymmetric hysteresis + `WarmupBars` + `GetHtfBias` + `GetAtrPips` | Tweaking regime logic |
| 782–903 | ORDER EXECUTION | `OpenTrade()` + `CalculateVolume()` (sizing + margin direct-solve) | Sizing changes only |
| 906–1079 | TRADE MANAGEMENT | BE / Partials / Chandelier / MaxHold + R helpers | Tweaking trade-mgmt rules |
| 1082–1198 | POSITION HOOKS | `OnPositionOpened` / `OnPositionClosed` + `EnsureEdgeKeys` | Adding per-trade tracking |
| 1201–1374 | ROLLOVER + DD GATES | Daily/weekly rollover, DailyDD/WeeklyDD/Floating/EquityTrail, force-close, recovery | Adding a new gate |
| 1377–1402 | TIME / SESSION HELPERS | `NowUtc()`, `GetWeekMonday()`, `GetSessionKey()`, `SessionBucketName()` | Changing session bucketing |
| 1404–1593 | ATTRIBUTION | `EmitAttribution()` (R-stats, Sharpe sqrt(252), Sortino, MAE/MFE, session matrix) + `PersistAttributionJson` + `ExportTradeLogCsvFile` | Adding a new metric |
| 1596–1629 | STREAK PERSISTENCE | LocalStorage save/load for consec-loss streak | Almost never |
| 1632–1664 | NEWS BLACKOUT PARSER | CSV parser for news windows | Changing the news format |

## Parameter groups (within PARAMETERS section, lines 114–349)

| Lines | Group | Param count |
|-------|-------|-------------|
| 117–146 | 00 · Core (BotLabel, Risk, Regime risk mults) | 7 |
| 148–166 | 02 · Time & Session | 5 |
| 168–178 | 03 · HTF Regime | 3 |
| 180–199 | 04 · Volatility / Regime | 5 |
| 201–208 | 05 · Spread | 2 |
| 210–233 | 09 · Stops & Targets | 6 |
| 235–278 | 10 · Trade Management (BE / Partials / Chandelier) | 12 |
| 280–321 | 11 · Risk Gates (DD / Cool-down / News / Equity-Trail / Hard-Loss) | 11 |
| 323–333 | 12 · Vol-Targeted Sizing | 3 |
| 335–349 | 13 · Logging (Verbose / Lock / CSV / SetId) | 5 |

**Removed vs Clover Algo v1.2.0:** Groups `01 · Edges` (3 toggles), `06 · ORB` (11), `07 · VWAP-MR` (8), `08 · Momentum` (4), and per-setup ATR-mults (3) are gone — strategy code lives in `TryEnterStrategy`, not in parameters.

## Quick lookups by symptom

| You need to… | Read these lines |
|---|---|
| Implement a strategy entry | 580–608 (hook) + 782–820 (`OpenTrade` signature) |
| Add a tracked metric per trade | 47–71 (`BotTradeState`) + 1082–1198 (close hook) + 1404–1593 (emit) |
| Change BE / partial trigger logic | 906–1079 |
| Tighten a gate (e.g. spread) | 659–691 |
| Change sizing | 822–903 (`CalculateVolume`) |
| Fix a daily-DD edge case | 1201–1300 |
| Add a parameter | 114–349 (PARAMETERS) + 78–112 if it's a canonical default |
| Audit Sharpe / Sortino math | 1404–1593 |

## Build & verify

```powershell
dotnet build "Bot Template/Bot Template.sln"
```

5-second feedback loop. Strongly preferred over re-reading the file. The build emits the same 4 cTrader-API deprecation warnings as Clover Algo — they're not regressions, they're inherited from the cAlgo SDK surface.

## Don't read

- `bin/`, `obj/` — compiled artifacts.
- `Bot Template.algo` (when generated) — packaged binary, opaque.
- `Bot Template.csproj` — 8 lines, never changes.
- `Bot Template.sln` — 21 lines, never changes.
