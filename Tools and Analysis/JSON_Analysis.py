import json
from datetime import datetime
from collections import defaultdict, Counter

CLOSE_EVENTS = {"Position geschlossen", "Stop-Loss-Zugriff", "Take-Profit-Zugriff"}


def dt(ms):
    return datetime.fromtimestamp(ms/1000)


def month_key(ms):
    return dt(ms).strftime("%Y-%m")


def safe_float(x):
    return None if x is None else float(x)


def load_events(path):
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def aggregate_positions(events):
    # positionId -> dict
    pos = {}
    for e in events:
        pid = e.get("positionId")
        if pid is None:
            continue
        if pid not in pos:
            pos[pid] = {
                "pid": pid,
                "type": e.get("type"),
                "entryPrice": e.get("entryPrice"),
                "open_time": e.get("time"),
                "close_events": [],
                "pnl": 0.0,
                "last_balance": None,
                "last_equity": None,
                "sl_hits": 0,
                "tp_hits": 0,
                "close_hits": 0,
                "vol_change": 0,
                "sl_change": 0,
                "tp_change": 0,
            }

        # track last known times
        t = e.get("time")
        if t is not None:
            if pos[pid]["open_time"] is None or t < pos[pid]["open_time"]:
                pos[pid]["open_time"] = t

        # track modifications (proxy for trailing / partial closes)
        evname = e.get("event", "")
        if "Volumen" in evname:
            pos[pid]["vol_change"] += 1
        if evname == "Position geändert (SL)":
            pos[pid]["sl_change"] += 1
        if evname == "Position geändert (TP)":
            pos[pid]["tp_change"] += 1
        if evname == "Position geändert (SL, TP)":
            pos[pid]["sl_change"] += 1
            pos[pid]["tp_change"] += 1

        # equity / balance snapshots
        b = e.get("balance")
        eq = e.get("equity")
        if b is not None:
            pos[pid]["last_balance"] = float(b)
        if eq is not None:
            pos[pid]["last_equity"] = float(eq)

        # close events
        if evname in CLOSE_EVENTS and e.get("closePrice") is not None:
            gp = safe_float(e.get("grossProfit")) or 0.0
            pos[pid]["pnl"] += gp
            pos[pid]["close_events"].append(e)
            if evname == "Stop-Loss-Zugriff":
                pos[pid]["sl_hits"] += 1
            elif evname == "Take-Profit-Zugriff":
                pos[pid]["tp_hits"] += 1
            else:
                pos[pid]["close_hits"] += 1

    # finalize per-position close time as last close event time if exists
    out = []
    for pid, p in pos.items():
        if p["close_events"]:
            p["close_time"] = max(ev["time"] for ev in p["close_events"] if ev.get("time") is not None)
        else:
            p["close_time"] = None
        out.append(p)

    # keep only positions that have at least one close event (realized)
    out = [p for p in out if p["close_time"] is not None]
    out.sort(key=lambda x: x["close_time"])
    return out


def calc_stats(positions, start_balance=10000.0):
    trades = len(positions)
    wins = [p for p in positions if p["pnl"] > 0]
    losses = [p for p in positions if p["pnl"] <= 0]
    net = sum(p["pnl"] for p in positions)
    gross_win = sum(p["pnl"] for p in wins)
    gross_loss = abs(sum(p["pnl"] for p in losses))
    pf = (gross_win / gross_loss) if gross_loss > 0 else float("inf")
    wr = (len(wins) / trades * 100) if trades else 0.0
    avg_win = (gross_win / len(wins)) if wins else 0.0
    avg_loss = (-gross_loss / len(losses)) if losses else 0.0  # negative number
    rr = (avg_win / abs(avg_loss)) if avg_loss != 0 else 0.0
    breakeven_wr = (abs(avg_loss) / (avg_win + abs(avg_loss)) * 100) if (avg_win + abs(avg_loss)) > 0 else 0.0

    # drawdown from last_balance snapshots per closed position
    bals = [p["last_balance"] for p in positions if p.get("last_balance") is not None]
    peak = start_balance
    max_dd = 0.0
    for b in bals:
        if b > peak: peak = b
        dd = peak - b
        if dd > max_dd: max_dd = dd
    final_balance = bals[-1] if bals else start_balance

    return {
        "trades": trades,
        "wins": len(wins),
        "losses": len(losses),
        "wr": wr,
        "pf": pf,
        "net": net,
        "final_balance": final_balance,
        "max_dd": max_dd,
        "avg_win": avg_win,
        "avg_loss": avg_loss,
        "rr": rr,
        "breakeven_wr": breakeven_wr,
    }

def monthly_pnl(positions):
    m = defaultdict(float)
    for p in positions:
        m[month_key(p["close_time"])] += p["pnl"]
    return dict(sorted(m.items()))


def top_bottom_trades(positions, n=10):
    s = sorted(positions, key=lambda x: x["pnl"])
    worst = s[:n]
    best = s[-n:][::-1]
    return best, worst


def print_report(path):
    events = load_events(path)
    if not events:
        print("EMPTY FILE")
        return

    # Basic file info
    t0 = dt(events[0]["time"]) if events[0].get("time") else None
    t1 = dt(events[-1]["time"]) if events[-1].get("time") else None
    pids = [e.get("positionId") for e in events if e.get("positionId") is not None]
    pid_min = min(pids) if pids else None
    pid_max = max(pids) if pids else None

    # Event counts
    ev_counts = Counter(e.get("event","") for e in events)

    positions = aggregate_positions(events)
    stats = calc_stats(positions, start_balance=10000.0)
    mpnl = monthly_pnl(positions)
    best, worst = top_bottom_trades(positions, n=8)

    # Proxy diagnostics
    vol_changes = sum(p["vol_change"] for p in positions)
    sl_changes = sum(p["sl_change"] for p in positions)
    tp_changes = sum(p["tp_change"] for p in positions)
    sl_hits = sum(p["sl_hits"] for p in positions)
    tp_hits = sum(p["tp_hits"] for p in positions)

    print("="*70)
    print(f"FILE: {path}")
    print(f"Events: {len(events)} | PID range: {pid_min}-{pid_max} | Time: {t0} -> {t1}")
    print("-"*70)
    print("KEY STATS (per position, aggregated partial closes):")
    print(f"Trades: {stats['trades']} | Wins: {stats['wins']} | Losses: {stats['losses']} | WR: {stats['wr']:.1f}%")
    print(f"Net PnL: {stats['net']:.2f} | Final Bal: {stats['final_balance']:.2f} | MaxDD: {stats['max_dd']:.2f}")
    print(f"PF: {stats['pf']:.2f} | AvgWin: {stats['avg_win']:.2f} | AvgLoss: {stats['avg_loss']:.2f} | RR: {stats['rr']:.2f}")
    print(f"Breakeven WR (from avg win/loss): {stats['breakeven_wr']:.1f}%")
    print("-"*70)
    print("STRUCTURE / DIAGNOSTICS (proxies):")
    print(f"Total SL hits: {sl_hits} | Total TP hits: {tp_hits}")
    print(f"Total SL changes: {sl_changes} | TP changes: {tp_changes} | Volume changes: {vol_changes}")
    print("(Viele Volume changes => Partial TPs aktiv; viele SL changes => Trailing/BE aktiv)")
    print("-"*70)
    print("TOP 8 BEST TRADES (by net pnl per position):")
    for p in best:
        print(f"PID {p['pid']:>4}  pnl={p['pnl']:>8.2f}  sl_hits={p['sl_hits']} tp_hits={p['tp_hits']}  close={dt(p['close_time'])}")
    print("-"*70)
    print("TOP 8 WORST TRADES:")
    for p in worst:
        print(f"PID {p['pid']:>4}  pnl={p['pnl']:>8.2f}  sl_hits={p['sl_hits']} tp_hits={p['tp_hits']}  close={dt(p['close_time'])}")
    print("-"*70)
    print("MONTHLY PnL:")
    for k,v in mpnl.items():
        sign = "+" if v >= 0 else ""
        print(f"{k}: {sign}{v:.2f}")
    print("-"*70)
    print("EVENT COUNTS (top 12):")
    for name, c in ev_counts.most_common(12):
        print(f"{c:>6}  {name}")
    print("="*70)


if __name__ == "__main__":
    # Beispiel:
    # python analyze_events.py events.json
    import sys
    if len(sys.argv) < 2:
        print("Usage: python analyze_events.py <events.json>")
    else:
        print_report(sys.argv[1])
