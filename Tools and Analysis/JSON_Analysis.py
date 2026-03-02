from __future__ import annotations

import argparse
import json
import os
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, Dict, Iterable, List, Optional, Tuple
from collections import Counter, defaultdict


# -----------------------------
# Parsing helpers
# -----------------------------
def _to_int(v: Any) -> Optional[int]:
    if v is None:
        return None
    try:
        return int(v)
    except (TypeError, ValueError):
        return None


def _to_float(v: Any) -> Optional[float]:
    if v is None:
        return None
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def _to_str(v: Any) -> Optional[str]:
    if v is None:
        return None
    s = str(v)
    s = s.strip()
    return s if s else None


def _to_dt_utc_from_ms(ms: Any) -> Optional[datetime]:
    if ms is None:
        return None
    try:
        # cTrader-like logs often store ms since epoch
        return datetime.fromtimestamp(float(ms) / 1000.0, tz=timezone.utc)
    except (TypeError, ValueError, OSError):
        return None


def _to_dt_utc_from_iso(s: Any) -> Optional[datetime]:
    if s is None:
        return None
    try:
        st = str(s).strip()
        if not st:
            return None
        # Support "Z" suffix
        if st.endswith("Z"):
            st = st[:-1] + "+00:00"
        dt = datetime.fromisoformat(st)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def _fmt_dt(dt: Optional[datetime]) -> str:
    if not dt:
        return ""
    return dt.astimezone(timezone.utc).strftime("%Y-%m-%d %H:%M:%S.%f")


def _event_key(raw: Dict[str, Any]) -> str:
    # unify typical keys
    for k in ("event", "eventName", "name", "type", "message"):
        if k in raw and raw[k] is not None:
            s = _to_str(raw[k])
            if s:
                return s
    return "UNKNOWN"


def _contains_any(s: str, needles: Iterable[str]) -> bool:
    sl = s.lower()
    return any(n.lower() in sl for n in needles)


# -----------------------------
# Normalization
# -----------------------------
def normalize_event(raw: Dict[str, Any]) -> Dict[str, Any]:
    # Supports multiple possible schemas (camelCase/snake_case)
    def g(*keys: str) -> Any:
        for k in keys:
            if k in raw:
                return raw[k]
        return None

    # time: prefer ms fields; fallback to ISO string
    dt = _to_dt_utc_from_ms(g("time", "timestamp", "timeMs", "time_ms", "ts"))
    if dt is None:
        dt = _to_dt_utc_from_iso(g("timeIso", "time_iso", "datetime", "dateTime", "date_time"))

    return {
        "raw": raw,
        "event": _event_key(raw),
        "time": dt,
        "position_id": _to_int(g("positionId", "position_id", "positionID", "pid", "PID")),
        "order_id": _to_int(g("orderId", "order_id")),
        "serial": _to_int(g("serial")),
        "side": _to_str(g("side", "type", "tradeType", "trade_type")),

        # prices
        "entry_price": _to_float(g("entryPrice", "entry_price", "openPrice", "open_price")),
        "close_price": _to_float(g("closePrice", "close_price")),

        # risk/targets
        "sl": _to_float(g("sl", "stopLoss", "stop_loss")),
        "tp": _to_float(g("tp", "takeProfit", "take_profit")),

        # size
        "volume": _to_int(g("volume", "volumeInUnits", "volume_in_units", "quantityUnits", "quantity_units")),
        "quantity": _to_float(g("quantity", "lots", "lot")),

        # PnL fields (net first, then gross)
        "net_profit": _to_float(g("netProfit", "net_profit", "netPnl", "net_pnl", "profit", "pnl", "pl", "PnL")),
        "gross_profit": _to_float(g("grossProfit", "gross_profit")),

        # account
        "balance": _to_float(g("balance", "accountBalance", "account_balance")),
        "equity": _to_float(g("equity", "accountEquity", "account_equity")),
        "pips": _to_float(g("pips")),
    }


# -----------------------------
# Trade aggregation per PID
# -----------------------------
@dataclass
class PosAgg:
    pid: int
    pnl: float = 0.0
    pnl_events: int = 0

    sl_hits: int = 0
    tp_hits: int = 0

    sl_changes: int = 0
    tp_changes: int = 0
    vol_changes: int = 0

    last_sl: Optional[float] = None
    last_tp: Optional[float] = None
    last_vol: Optional[int] = None

    open_time: Optional[datetime] = None
    close_time: Optional[datetime] = None  # last "closing-ish" time

    close_event: Optional[str] = None

    # store a few last-known fields (optional diagnostics)
    last_balance: Optional[float] = None
    last_equity: Optional[float] = None


def is_sl_hit(event_name: str) -> bool:
    return _contains_any(event_name, ["stop-loss-zugriff", "stop loss", "stop-loss"])


def is_tp_hit(event_name: str) -> bool:
    return _contains_any(event_name, ["take-profit-zugriff", "take profit", "take-profit"])


def is_position_created(event_name: str) -> bool:
    return _contains_any(event_name, ["position erstellen", "position created", "created"])


def is_position_closed_like(event_name: str) -> bool:
    # include explicit closes + SL/TP hits
    return (
        _contains_any(event_name, ["position geschlossen", "position closed", "closed"])
        or is_sl_hit(event_name)
        or is_tp_hit(event_name)
    )


def realized_pnl_from_event(e: Dict[str, Any]) -> Optional[float]:
    # Prefer net profit; fallback to gross
    if e["net_profit"] is not None:
        return float(e["net_profit"])
    if e["gross_profit"] is not None:
        return float(e["gross_profit"])
    return None


def aggregate(events: List[Dict[str, Any]]) -> Tuple[
    Dict[int, PosAgg],
    List[Tuple[datetime, float]],  # (time, balance) series
    Counter,  # event counts
    int, int, Optional[datetime], Optional[datetime]
]:
    by_pid: Dict[int, PosAgg] = {}
    event_counts: Counter = Counter()
    balance_series: List[Tuple[datetime, float]] = []

    pids: List[int] = []
    t_min: Optional[datetime] = None
    t_max: Optional[datetime] = None

    # sort by time (keep unknown times at end but stable)
    def sort_key(x: Dict[str, Any]) -> Tuple[int, float]:
        dt = x["time"]
        if dt is None:
            return (1, 0.0)
        return (0, dt.timestamp())

    events_sorted = sorted(events, key=sort_key)

    for e in events_sorted:
        name = e["event"] or "UNKNOWN"
        event_counts[name] += 1

        dt = e["time"]
        if dt is not None:
            if t_min is None or dt < t_min:
                t_min = dt
            if t_max is None or dt > t_max:
                t_max = dt

        pid = e["position_id"]
        if pid is not None:
            pids.append(pid)
            if pid not in by_pid:
                by_pid[pid] = PosAgg(pid=pid)
            agg = by_pid[pid]

            # open_time heuristic
            if is_position_created(name) and dt is not None:
                if agg.open_time is None or dt < agg.open_time:
                    agg.open_time = dt
            elif agg.open_time is None and dt is not None:
                agg.open_time = dt

            # SL/TP hit counters (per event; in your sample this is ~1 per PID)
            if is_sl_hit(name):
                agg.sl_hits += 1
                if dt is not None:
                    agg.close_time = dt
                    agg.close_event = name
            if is_tp_hit(name):
                agg.tp_hits += 1
                if dt is not None:
                    agg.close_time = dt
                    agg.close_event = name

            # Update "close_time" on explicit close
            if is_position_closed_like(name) and dt is not None:
                if agg.close_time is None or dt > agg.close_time:
                    agg.close_time = dt
                    agg.close_event = name

            # Change counters as proxies (count actual value changes, not event-name counts)
            if e["sl"] is not None:
                if agg.last_sl is None:
                    agg.last_sl = e["sl"]
                else:
                    if float(e["sl"]) != float(agg.last_sl):
                        agg.sl_changes += 1
                        agg.last_sl = e["sl"]

            if e["tp"] is not None:
                if agg.last_tp is None:
                    agg.last_tp = e["tp"]
                else:
                    if float(e["tp"]) != float(agg.last_tp):
                        agg.tp_changes += 1
                        agg.last_tp = e["tp"]

            if e["volume"] is not None:
                if agg.last_vol is None:
                    agg.last_vol = e["volume"]
                else:
                    if int(e["volume"]) != int(agg.last_vol):
                        agg.vol_changes += 1
                        agg.last_vol = e["volume"]

            # Realized pnl aggregation (captures partial closes if your exporter emits pnl on those events)
            rp = realized_pnl_from_event(e)
            if rp is not None:
                agg.pnl += rp
                agg.pnl_events += 1
                if dt is not None:
                    # treat as close-ish moment (partial closes included)
                    if agg.close_time is None or dt > agg.close_time:
                        agg.close_time = dt
                        agg.close_event = name

            # last known account values (optional)
            if e["balance"] is not None:
                agg.last_balance = e["balance"]
            if e["equity"] is not None:
                agg.last_equity = e["equity"]

        # global balance series for DD + final balance
        if dt is not None and e["balance"] is not None:
            balance_series.append((dt, float(e["balance"])))

    pid_min = min(pids) if pids else 0
    pid_max = max(pids) if pids else 0
    return by_pid, balance_series, event_counts, pid_min, pid_max, t_min, t_max


# -----------------------------
# Stats
# -----------------------------
def compute_max_dd(balance_series: List[Tuple[datetime, float]]) -> float:
    if not balance_series:
        return 0.0
    # series assumed chronological
    peak = balance_series[0][1]
    max_dd = 0.0
    for _, bal in balance_series:
        if bal > peak:
            peak = bal
        dd = peak - bal
        if dd > max_dd:
            max_dd = dd
    return max_dd


def profit_factor(pnls: List[float]) -> float:
    gains = sum(x for x in pnls if x > 0)
    losses = sum(-x for x in pnls if x < 0)
    if losses <= 0:
        return float("inf") if gains > 0 else 0.0
    return gains / losses


def avg_win_loss(pnls: List[float]) -> Tuple[float, float]:
    wins = [x for x in pnls if x > 0]
    losses = [x for x in pnls if x < 0]
    avg_win = sum(wins) / len(wins) if wins else 0.0
    avg_loss = sum(losses) / len(losses) if losses else 0.0  # negative
    return avg_win, avg_loss


def breakeven_wr_from_avgs(avg_win: float, avg_loss: float) -> float:
    # avg_loss should be negative
    if avg_win <= 0 or avg_loss >= 0:
        return 0.0
    L = abs(avg_loss)
    return (L / (avg_win + L)) * 100.0


# -----------------------------
# Reporting
# -----------------------------
def print_header(events_count: int, pid_min: int, pid_max: int, t_min: Optional[datetime], t_max: Optional[datetime]) -> None:
    tmin_s = _fmt_dt(t_min)
    tmax_s = _fmt_dt(t_max)
    print(f"Events: {events_count} | PID range: {pid_min}-{pid_max} | Time: {tmin_s} -> {tmax_s}")


def print_key_stats(pos_aggs: Dict[int, PosAgg], balance_series: List[Tuple[datetime, float]]) -> None:
    # count "trades" as positions with any close_time OR any pnl event
    positions = list(pos_aggs.values())
    traded = [p for p in positions if (p.close_time is not None) or (p.pnl_events > 0)]
    pnls = [p.pnl for p in traded]

    trades = len(traded)
    wins = sum(1 for x in pnls if x > 0)
    losses = sum(1 for x in pnls if x < 0)
    wr = (wins / trades * 100.0) if trades else 0.0
    net = sum(pnls) if pnls else 0.0

    final_bal = balance_series[-1][1] if balance_series else None
    maxdd = compute_max_dd(balance_series)

    pf = profit_factor(pnls)
    avgw, avgl = avg_win_loss(pnls)
    rr = (avgw / abs(avgl)) if avgl < 0 else 0.0
    be_wr = breakeven_wr_from_avgs(avgw, avgl)

    print("KEY STATS (per position, aggregated partial closes):")
    print(f"Trades: {trades} | Wins: {wins} | Losses: {losses} | WR: {wr:.1f}%")
    if final_bal is None:
        print(f"Net PnL: {net:.2f} | Final Bal: n/a | MaxDD: {maxdd:.2f}")
    else:
        print(f"Net PnL: {net:.2f} | Final Bal: {final_bal:.2f} | MaxDD: {maxdd:.2f}")
    if pf == float("inf"):
        pf_s = "inf"
    else:
        pf_s = f"{pf:.2f}"
    print(f"PF: {pf_s} | AvgWin: {avgw:.2f} | AvgLoss: {avgl:.2f} | RR: {rr:.2f}")
    print(f"Breakeven WR (from avg win/loss): {be_wr:.1f}%")


def print_diagnostics(pos_aggs: Dict[int, PosAgg]) -> None:
    positions = list(pos_aggs.values())
    total_sl_hits = sum(p.sl_hits for p in positions)
    total_tp_hits = sum(p.tp_hits for p in positions)
    total_sl_changes = sum(p.sl_changes for p in positions)
    total_tp_changes = sum(p.tp_changes for p in positions)
    total_vol_changes = sum(p.vol_changes for p in positions)

    print("STRUCTURE / DIAGNOSTICS (proxies):")
    print(f"Total SL hits: {total_sl_hits} | Total TP hits: {total_tp_hits}")
    print(f"Total SL changes: {total_sl_changes} | TP changes: {total_tp_changes} | Volume changes: {total_vol_changes}")
    print("(Viele Volume changes => Partial TPs aktiv; viele SL changes => Trailing/BE aktiv)")


def print_top_trades(pos_aggs: Dict[int, PosAgg], top_n: int = 8) -> None:
    traded = [p for p in pos_aggs.values() if (p.close_time is not None) or (p.pnl_events > 0)]
    traded.sort(key=lambda p: p.pnl, reverse=True)

    print(f"TOP {top_n} BEST TRADES (by net pnl per position):")
    for p in traded[:top_n]:
        print(f"PID {p.pid} pnl= {p.pnl:>6.2f} sl_hits={p.sl_hits} tp_hits={p.tp_hits} close={_fmt_dt(p.close_time)}")

    print(f"TOP {top_n} WORST TRADES:")
    for p in traded[-top_n:]:
        print(f"PID {p.pid} pnl= {p.pnl:>6.2f} sl_hits={p.sl_hits} tp_hits={p.tp_hits} close={_fmt_dt(p.close_time)}")


def print_monthly_pnl(pos_aggs: Dict[int, PosAgg]) -> None:
    monthly: Dict[str, float] = defaultdict(float)
    for p in pos_aggs.values():
        if p.close_time is None:
            continue
        key = p.close_time.strftime("%Y-%m")
        monthly[key] += p.pnl

    print("MONTHLY PnL:")
    for k in sorted(monthly.keys()):
        v = monthly[k]
        sign = "+" if v >= 0 else ""
        print(f"{k}: {sign}{v:.2f}")


def print_event_counts(event_counts: Counter, top_n: int = 12) -> None:
    print(f"EVENT COUNTS (top {top_n}):")
    for name, c in event_counts.most_common(top_n):
        print(f"{c} {name}")


# -----------------------------
# IO
# -----------------------------
def read_events(path: str) -> List[Dict[str, Any]]:
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)

    if isinstance(data, dict) and "events" in data and isinstance(data["events"], list):
        return data["events"]
    if isinstance(data, list):
        return data

    raise ValueError("Unerwartetes JSON-Format: erwartet Liste oder {events:[...]}.")


def build_default_input_path() -> str:
    # Guess your folder layout:
    # .../Robots/Tools and Analysis/JSON_Analysis.py
    # .../Robots/JSON_events.json
    script_dir = os.path.dirname(os.path.abspath(__file__))
    robots_dir = os.path.dirname(script_dir)
    return os.path.join(robots_dir, "JSON_Data", "events.json")


def main() -> int:
    ap = argparse.ArgumentParser(description="Analyze cTrader JSON events (per position, partial closes aggregated).")
    ap.add_argument("input", nargs="?", default=build_default_input_path(), help="Path to events.json")
    ap.add_argument("--top", type=int, default=8, help="Top N best/worst positions")
    ap.add_argument("--top-events", type=int, default=12, help="Top N event names")
    ap.add_argument("--no-monthly", action="store_true", help="Disable monthly pnl output")
    ap.add_argument("--no-events", action="store_true", help="Disable event count output")
    args = ap.parse_args()

    input_path = os.path.abspath(args.input)
    raw_events = read_events(input_path)
    norm = [normalize_event(x) for x in raw_events]

    pos_aggs, balance_series, event_counts, pid_min, pid_max, t_min, t_max = aggregate(norm)

    print_header(events_count=len(norm), pid_min=pid_min, pid_max=pid_max, t_min=t_min, t_max=t_max)
    print("-" * 70)
    print_key_stats(pos_aggs, balance_series)
    print("-" * 70)
    print_diagnostics(pos_aggs)
    print("-" * 70)
    print_top_trades(pos_aggs, top_n=max(1, args.top))
    print("-" * 70)
    if not args.no_monthly:
        print_monthly_pnl(pos_aggs)
        print("-" * 70)
    if not args.no_events:
        print_event_counts(event_counts, top_n=max(1, args.top_events))
        print("=" * 70)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
