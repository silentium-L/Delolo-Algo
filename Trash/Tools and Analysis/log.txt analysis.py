#!/usr/bin/env python3
"""
log_summary.py  –  10-Fold Bot Log Analyser
Format: DD.MM.YYYY HH:MM:SS.mmm | Info | Message
Einfach in PyCharm starten – kein Kommandozeilenargument nötig.
"""

import os
import re
from datetime import datetime
from collections import defaultdict, Counter


# ─────────────────────────────────────────────────────────────────────────────
#  KONFIGURATION
# ─────────────────────────────────────────────────────────────────────────────

DATA_DIR = r"C:\Users\ldenn\Documents\cAlgo\Sources\Robots\JSON_Data"
LOG_FILE = os.path.join(DATA_DIR, "log.txt")

# ─────────────────────────────────────────────────────────────────────────────
#  Regex – Zeilenformat:  DD.MM.YYYY HH:MM:SS.mmm | Level | Message
# ─────────────────────────────────────────────────────────────────────────────

LINE_RE = re.compile(
    r"^(\d{2}\.\d{2}\.\d{4})"           # Datum  DD.MM.YYYY
    r"\s+(\d{2}:\d{2}:\d{2}(?:\.\d+)?)" # Zeit   HH:MM:SS[.mmm]
    r"\s*\|\s*\w+\s*\|\s*"              # | Level |
    r"(.+)$"                             # Message
)

# Score-Zeile:  Score [Buy]: EMA+BB+... = 3/21
SCORE_RE = re.compile(
    r"Score\s+\[(Buy|Sell)\].*?=\s*(\d+)/(\d+)"
)

# Modul-Detail: "  [OSC] dir=Buy rsi=... pts=2"
MODULE_RE = re.compile(
    r"\[(EMA|BB|ST|PA|FIB|OSC|SR)\]\s+dir=(Buy|Sell).*?\bpts=(\d+)"
)

# Daily reset:  "Daily reset Equity10000,00 EUR ..."
EQUITY_RE = re.compile(r"Equity([\d,\.]+)")

# Market tradable:  "Market tradable for Sell. ..."
TRADABLE_RE = re.compile(r"Market tradable for\s+(Buy|Sell)")

# ─────────────────────────────────────────────────────────────────────────────
#  Helpers
# ─────────────────────────────────────────────────────────────────────────────

def parse_timestamp(date_str: str, time_str: str) -> datetime | None:
    raw = f"{date_str} {time_str}"
    for fmt in ("%d.%m.%Y %H:%M:%S.%f", "%d.%m.%Y %H:%M:%S"):
        try:
            return datetime.strptime(raw, fmt)
        except ValueError:
            continue
    return None


def eu_float(value: str) -> float:
    return float(value.replace(",", "."))


def classify_rejection(message: str) -> str:
    if "HTF filter" in message:
        return "HTF filter"
    if "Session filter" in message:
        return "Session filter"
    if "Spread" in message and "MaxAllowedSpread" in message:
        return "Spread"
    if "Max open exposure" in message:
        return "Max open exposure"
    if "blocked on Friday" in message or "Friday" in message:
        return "Friday block"
    if "Daily drawdown" in message:
        return "Daily drawdown"
    return "Other"


def parse_line(raw: str) -> tuple[datetime, str] | None:
    m = LINE_RE.match(raw.rstrip("\n\r"))
    if not m:
        return None
    dt = parse_timestamp(m.group(1), m.group(2))
    if dt is None:
        return None
    return dt, m.group(3)


# ─────────────────────────────────────────────────────────────────────────────
#  Per-Tag-Struktur
# ─────────────────────────────────────────────────────────────────────────────

def new_day() -> dict:
    return {
        "start_equity": None,
        # Score-Sammlung pro Richtung: liste der (score, max) Tupel
        "scores": {"Buy": [], "Sell": []},
        # Modul-Punkte: {modul: {"Buy": [pts,...], "Sell": [pts,...]}}
        "module_pts": {m: {"Buy": [], "Sell": []} for m in
                       ("EMA", "BB", "ST", "PA", "FIB", "OSC", "SR")},
        "market_tradable": {"Buy": 0, "Sell": 0},
        "rejections_total": 0,
        "rejections_by_reason": Counter(),
    }


# ─────────────────────────────────────────────────────────────────────────────
#  Output-Helpers
# ─────────────────────────────────────────────────────────────────────────────

SEP  = "─" * 64
SEP2 = "═" * 64

def score_stats(lst: list) -> str:
    if not lst:
        return "count=0  avg=–"
    scores  = [s for s, _ in lst]
    maxvals = [mx for _, mx in lst]
    avg_s   = sum(scores) / len(scores)
    avg_m   = maxvals[0] if len(set(maxvals)) == 1 else sum(maxvals) / len(maxvals)
    return (f"count={len(lst)}  "
            f"avg={avg_s:.1f}/{avg_m:.0f}  "
            f"min={min(scores)}  max={max(scores)}")


# ─────────────────────────────────────────────────────────────────────────────
#  Main
# ─────────────────────────────────────────────────────────────────────────────

def main():
    path = LOG_FILE

    if not os.path.isfile(path):
        print(f"ERROR: Datei nicht gefunden: {path}")
        return

    days: dict[str, dict]   = defaultdict(new_day)
    global_rejections        = Counter()
    global_score_freq        = Counter()   # (dir, score, max) → count
    global_module_pts: dict  = {m: {"Buy": [], "Sell": []} for m in
                                ("EMA", "BB", "ST", "PA", "FIB", "OSC", "SR")}

    total_lines  = 0
    parsed_lines = 0
    first_dt: datetime | None = None
    last_dt:  datetime | None = None

    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for raw in fh:
            total_lines += 1
            result = parse_line(raw)
            if result is None:
                continue

            dt, msg = result
            parsed_lines += 1
            day_key = dt.strftime("%Y-%m-%d")

            if first_dt is None or dt < first_dt:
                first_dt = dt
            if last_dt is None or dt > last_dt:
                last_dt = dt

            day = days[day_key]

            # ── Daily reset → Equity ──────────────────────────────────────
            if msg.startswith("Daily reset"):
                m = EQUITY_RE.search(msg)
                if m:
                    try:
                        day["start_equity"] = eu_float(m.group(1))
                    except ValueError:
                        pass

            # ── Score-Zeile ───────────────────────────────────────────────
            elif msg.startswith("Score"):
                m = SCORE_RE.search(msg)
                if m:
                    direction  = m.group(1)
                    score      = int(m.group(2))
                    score_max  = int(m.group(3))
                    day["scores"][direction].append((score, score_max))
                    global_score_freq[(direction, score, score_max)] += 1

            # ── Modul-Detail-Zeile  [XXX] dir=... pts=N ──────────────────
            elif msg.lstrip().startswith("["):
                m = MODULE_RE.search(msg)
                if m:
                    mod, direction, pts = m.group(1), m.group(2), int(m.group(3))
                    day["module_pts"][mod][direction].append(pts)
                    global_module_pts[mod][direction].append(pts)

            # ── Market tradable ───────────────────────────────────────────
            elif msg.startswith("Market tradable for"):
                m = TRADABLE_RE.match(msg)
                if m:
                    day["market_tradable"][m.group(1)] += 1

            # ── Trade rejected ────────────────────────────────────────────
            elif msg.startswith("Trade rejected"):
                reason = classify_rejection(msg)
                day["rejections_total"]             += 1
                day["rejections_by_reason"][reason] += 1
                global_rejections[reason]           += 1

    # ── Header ───────────────────────────────────────────────────────────────
    print(SEP2)
    print("  10-FOLD BOT  –  LOG SUMMARY")
    print(SEP2)
    print(f"  Datei       : {os.path.abspath(path)}")
    print(f"  Zeilen ges. : {total_lines:,}  (geparst: {parsed_lines:,})")
    if first_dt and last_dt:
        print(f"  Zeitspanne  : {first_dt:%d.%m.%Y %H:%M:%S}  →  {last_dt:%d.%m.%Y %H:%M:%S}")
    print(f"  Handelstage : {len(days)}")
    print(SEP2)

    # ── Pro-Tag-Blöcke ────────────────────────────────────────────────────────
    for day_key in sorted(days.keys()):
        d = days[day_key]
        print(f"\n  TAG: {day_key}")
        print(SEP)

        if d["start_equity"] is not None:
            print(f"  Start-Equity     : {d['start_equity']:>12,.2f}")
        else:
            print("  Start-Equity     : – (keine Daily-reset-Zeile)")

        print("  Scores           :")
        for direction in ("Buy", "Sell"):
            print(f"    {direction:<4} → {score_stats(d['scores'][direction])}")

        has_module_data = any(
            d["module_pts"][mod][di]
            for mod in d["module_pts"]
            for di in ("Buy", "Sell")
        )
        if has_module_data:
            print("  Ø Punkte/Modul   :  (Buy / Sell)")
            for mod in ("EMA", "BB", "ST", "PA", "FIB", "OSC", "SR"):
                b_list = d["module_pts"][mod]["Buy"]
                s_list = d["module_pts"][mod]["Sell"]
                b_avg  = f"{sum(b_list)/len(b_list):.2f}" if b_list else "–"
                s_avg  = f"{sum(s_list)/len(s_list):.2f}" if s_list else "–"
                print(f"    [{mod:<3}]  Buy={b_avg:>5}  Sell={s_avg:>5}")

        mt = d["market_tradable"]
        print(f"  Market Tradable  : Buy={mt['Buy']}  Sell={mt['Sell']}")

        rej_total = d["rejections_total"]
        print(f"  Rejections       : {rej_total} total")
        if rej_total > 0:
            for reason, cnt in d["rejections_by_reason"].most_common():
                print(f"    {reason:<22} : {cnt}")

    # ── Global: Rejections ────────────────────────────────────────────────────
    print(f"\n{SEP2}")
    print("  GLOBAL REJECTIONS")
    print(SEP2)
    total_rej = sum(global_rejections.values())
    print(f"  Gesamt: {total_rej:,}")
    if total_rej > 0:
        for reason, cnt in global_rejections.most_common():
            pct = cnt / total_rej * 100
            print(f"  {reason:<22} : {cnt:>8,}  ({pct:5.1f}%)")

    # ── Global: Score-Frequenzen (Top 15) ─────────────────────────────────────
    print(f"\n{SEP2}")
    print("  GLOBAL SCORE-FREQUENZEN  (Top 15)")
    print(SEP2)
    if not global_score_freq:
        print("  Keine Score-Zeilen gefunden.")
    else:
        top_cnt = global_score_freq.most_common(1)[0][1]
        for (direction, score, score_max), cnt in global_score_freq.most_common(15):
            bar = "█" * min(cnt // max(1, top_cnt // 20), 20)
            print(f"  {direction:<4} {score:>2}/{score_max:<3} : {cnt:>8,}x  {bar}")

    # ── Global: Modul-Effizienz ───────────────────────────────────────────────
    print(f"\n{SEP2}")
    print("  GLOBAL Ø PUNKTE PRO MODUL")
    print(SEP2)
    print(f"  {'Modul':<6}  {'Ø Buy':>8}  {'Ø Sell':>8}  {'n (Buy)':>10}  {'n (Sell)':>10}")
    print(f"  {'─'*6}  {'─'*8}  {'─'*8}  {'─'*10}  {'─'*10}")
    for mod in ("EMA", "BB", "ST", "PA", "FIB", "OSC", "SR"):
        b = global_module_pts[mod]["Buy"]
        s = global_module_pts[mod]["Sell"]
        b_avg = f"{sum(b)/len(b):.3f}" if b else "–"
        s_avg = f"{sum(s)/len(s):.3f}" if s else "–"
        print(f"  [{mod:<3}]  {b_avg:>8}  {s_avg:>8}  {len(b):>10,}  {len(s):>10,}")

    print(f"\n{SEP2}\n")


if __name__ == "__main__":
    main()