"""
Delolo Algo V6 - Backtest Auswertung
pip install pandas plotly kaleido
"""

import json, re, os, sys
from datetime import datetime
import pandas as pd

DATA_DIR      = r"C:\Users\ldenn\Documents\cAlgo\Sources\Robots\JSON_Data"
EVENTS_FILE   = os.path.join(DATA_DIR, "events.json")
LOG_FILE      = os.path.join(DATA_DIR, "log.txt")
OUTPUT_DIR    = DATA_DIR
START_BALANCE = 10_000.0

try:
    import plotly.graph_objects as go
    PLOTLY_OK = True
except ImportError:
    print("INFO: plotly nicht gefunden, keine Charts. pip install plotly kaleido")
    PLOTLY_OK = False

# ── 1. LADEN ──────────────────────────────────────────────────────────────────
with open(EVENTS_FILE, "r", encoding="utf-8") as fh:
    events = json.load(fh)
with open(LOG_FILE, "r", encoding="utf-8", errors="replace") as fh:
    log_text = fh.read()

# ── 2. EVENTS ─────────────────────────────────────────────────────────────────
df_ev = pd.DataFrame(events)
df_ev["time_dt"] = pd.to_datetime(df_ev["time"], unit="ms", utc=True)

CLOSE_EVENTS = ["Position geschlossen", "Stop-Loss-Zugriff", "Take-Profit-Zugriff"]
df_closed = df_ev[df_ev["event"].isin(CLOSE_EVENTS)].copy()
df_closed["grossProfit"] = pd.to_numeric(df_closed["grossProfit"], errors="coerce")
df_closed = df_closed[df_closed["balance"].notna() & df_closed["grossProfit"].notna()].copy()
df_open   = df_ev[df_ev["event"] == "Position erstellen"].copy()

rows = []
for idx in df_closed.index:
    cr  = df_closed.loc[idx]
    pid = cr["positionId"]
    subset = df_open[df_open["positionId"] == pid]
    exit_dt = cr["time_dt"]
    if len(subset) > 0:
        entry_dt = pd.Timestamp(subset["time_dt"].values[0], tz="UTC")
        dur_min  = (exit_dt - entry_dt).total_seconds() / 60
        ep       = subset["entryPrice"].values[0]
    else:
        entry_dt = None
        dur_min  = None
        ep       = cr["entryPrice"]
    sl_rows = df_ev[(df_ev["positionId"] == pid) & (df_ev["event"].str.startswith("Position geaendert"))]
    if len(sl_rows) == 0:
        sl_rows = df_ev[(df_ev["positionId"] == pid) & (df_ev["event"].str.contains("geändert|geaendert", na=False))]
    init_sl = sl_rows.iloc[0]["sl"] if len(sl_rows) > 0 else None
    rows.append({
        "positionId":   pid,
        "type":         cr["type"],
        "entryPrice":   ep,
        "closePrice":   cr["closePrice"],
        "grossProfit":  float(cr["grossProfit"]),
        "pips":         cr["pips"],
        "balance":      float(cr["balance"]),
        "quantity":     cr["quantity"],
        "sl":           init_sl,
        "tp":           cr["tp"],
        "close_event":  cr["event"],
        "entry_dt":     entry_dt,
        "exit_dt":      exit_dt,
        "duration_min": dur_min,
    })

df = pd.DataFrame(rows).reset_index(drop=True)
df["win"]        = df["grossProfit"] > 0
df["exit_dow"]   = df["exit_dt"].dt.day_name()
df["exit_month"] = df["exit_dt"].dt.to_period("M").astype(str)

# ── 3. LOG ────────────────────────────────────────────────────────────────────
trade_re = re.compile(
    r'(\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})\.\d+ \| Info \| TRADE #\d+ \w+ \| Score:(\d)/4 \| Pat:(.+)')
adx_re   = re.compile(r'ADX:([\d.]+) \| HTF:(\w+)')
lines    = log_text.split("\n")
df["score"] = None
df["pattern"] = None
df["adx"]     = None
df["htf"]     = None

for i, line in enumerate(lines):
    m = trade_re.search(line)
    if not m:
        continue
    dt_str, score, pat = m.group(1), m.group(2), m.group(3)
    dt_utc = pd.Timestamp(datetime.strptime(dt_str, "%d.%m.%Y %H:%M:%S"), tz="UTC")
    adx_val = htf_val = None
    for j in range(i + 1, min(i + 5, len(lines))):
        ma = adx_re.search(lines[j])
        if ma:
            adx_val  = float(ma.group(1))
            htf_val  = ma.group(2)
            break
    mask = df["entry_dt"].notna() & (abs(df["entry_dt"] - dt_utc) < pd.Timedelta("2min"))
    df.loc[mask, "score"]   = int(score)
    df.loc[mask, "pattern"] = pat.strip()
    df.loc[mask, "adx"]     = adx_val
    df.loc[mask, "htf"]     = htf_val

close_re   = re.compile(r'Trade Closed \[(WIN|LOSS)\].*?Reason:(\S+)')
df_reasons = pd.DataFrame([{"result": m.group(1), "reason": m.group(2)}
                            for m in close_re.finditer(log_text)])

# ── 4. METRIKEN ───────────────────────────────────────────────────────────────
n        = len(df)
n_win    = int(df["win"].sum())
n_loss   = n - n_win
wr       = n_win / n * 100 if n else 0
gp       = df[df["win"]]["grossProfit"].sum()
gl       = df[~df["win"]]["grossProfit"].sum()
net_pnl  = df["grossProfit"].sum()
pf       = abs(gp / gl)              if gl    != 0         else float("inf")
avg_win  = df[df["win"]]["grossProfit"].mean()  if n_win   else 0
avg_loss = df[~df["win"]]["grossProfit"].mean() if n_loss  else 0
rr       = abs(avg_win / avg_loss)   if avg_loss != 0      else float("inf")
expect   = (wr / 100 * avg_win) + ((1 - wr / 100) * avg_loss)

bal_arr   = df["balance"].dropna().values
end_bal   = bal_arr[-1] if len(bal_arr) else START_BALANCE
total_ret = (end_bal - START_BALANCE) / START_BALANCE * 100
peak = START_BALANCE
max_dd = max_dd_pct = 0.0
for b in bal_arr:
    if b > peak:
        peak = b
    dd  = peak - b
    ddp = dd / peak * 100
    if dd  > max_dd:     max_dd     = dd
    if ddp > max_dd_pct: max_dd_pct = ddp

sl_hits  = (df["close_event"] == "Stop-Loss-Zugriff").sum()
tp_hits  = (df["close_event"] == "Take-Profit-Zugriff").sum()
swap_cl  = (df["close_event"] == "Position geschlossen").sum()
avg_dur  = df["duration_min"].mean() if df["duration_min"].notna().any() else 0

# ── 5. KONSOLEN-REPORT ────────────────────────────────────────────────────────
SEP  = "=" * 60
SEP2 = "-" * 60

print()
print(SEP)
print("  DELOLO ALGO V6 – BACKTEST REPORT")
print(SEP)
print(f"  Zeitraum:          {df['exit_dt'].min().strftime('%d.%m.%Y')} – {df['exit_dt'].max().strftime('%d.%m.%Y')}")
print(f"  Startkapital:      {START_BALANCE:>10,.2f} EUR")
print(f"  Endkapital:        {end_bal:>10,.2f} EUR")
print(f"  Gesamtrendite:     {total_ret:>+9.2f}%")
print(SEP2)
print(f"  Trades gesamt:     {n:>6}")
print(f"  Wins:              {n_win:>6}  ({n_win/n*100:.1f}%)")
print(f"  Losses:            {n_loss:>6}  ({n_loss/n*100:.1f}%)")
print(f"  Win Rate:          {wr:>9.1f}%")
print(SEP2)
print(f"  Net P/L:           {net_pnl:>+9.2f} EUR")
print(f"  Profit Factor:     {pf:>9.2f}")
print(f"  Avg Win:           {avg_win:>+9.2f} EUR")
print(f"  Avg Loss:          {avg_loss:>+9.2f} EUR")
print(f"  RR-Ratio:          {rr:>9.2f}")
print(f"  Expectancy/Trade:  {expect:>+9.2f} EUR")
print(SEP2)
print(f"  Max Drawdown:      {max_dd:>+9.2f} EUR  ({max_dd_pct:.2f}%)")
print(f"  Avg Haltedauer:    {avg_dur:>6.0f} min")
print(SEP2)
print(f"  Stop-Loss Hits:    {sl_hits:>6}")
print(f"  Take-Profit Hits:  {tp_hits:>6}")
print(f"  Swap/Manual Close: {swap_cl:>6}")
print(SEP)

if df["score"].notna().any():
    print()
    print("  SCORE-PERFORMANCE:")
    print(f"  {'Score':<10} {'Trades':>6} {'Wins':>6} {'WR%':>6} {'Net P/L':>10}")
    print("  " + "-" * 42)
    for sc in sorted(df["score"].dropna().unique()):
        grp = df[df["score"] == sc]
        w   = grp["win"].sum()
        t   = len(grp)
        pl  = grp["grossProfit"].sum()
        print(f"  {str(sc)+'/4':<10} {t:>6} {w:>6} {w/t*100:>5.0f}% {pl:>+10.2f} EUR")
    print(SEP)

if df["pattern"].notna().any():
    print()
    print("  PATTERN-PERFORMANCE:")
    print(f"  {'Pattern':<24} {'Trades':>6} {'Wins':>6} {'WR%':>6} {'Net P/L':>10}")
    print("  " + "-" * 56)
    for pat in sorted(df["pattern"].dropna().unique()):
        grp = df[df["pattern"] == pat]
        w   = grp["win"].sum()
        t   = len(grp)
        pl  = grp["grossProfit"].sum()
        print(f"  {pat:<24} {t:>6} {w:>6} {w/t*100:>5.0f}% {pl:>+10.2f} EUR")
    print(SEP)

print()
print("  NET P/L NACH WOCHENTAG:")
DOW = ["Monday","Tuesday","Wednesday","Thursday","Friday"]
for day in DOW:
    grp = df[df["exit_dow"] == day]
    if len(grp) == 0:
        continue
    pl  = grp["grossProfit"].sum()
    bar = "#" * int(abs(pl) / 10)
    sig = "+" if pl >= 0 else "-"
    print(f"  {day:<12}  {pl:>+8.2f} EUR  {sig}{bar}")
print(SEP)

print()
print("  NET P/L NACH MONAT:")
for mon in sorted(df["exit_month"].unique()):
    grp = df[df["exit_month"] == mon]
    pl  = grp["grossProfit"].sum()
    bar = "#" * int(abs(pl) / 20)
    sig = "+" if pl >= 0 else "-"
    print(f"  {mon}   {pl:>+8.2f} EUR  {sig}{bar}")
print(SEP)

if len(df_reasons):
    print()
    print("  CLOSE-GRÜNDE:")
    for reason, grp in df_reasons.groupby("reason"):
        wins = (grp["result"] == "WIN").sum()
        loss = (grp["result"] == "LOSS").sum()
        print(f"  {reason:<28}  {len(grp):>3}x  (W:{wins} L:{loss})")
    print(SEP)

# ── 6. TRADE-LISTE ────────────────────────────────────────────────────────────
print()
print("  TRADE-LISTE (alle geschlossenen Trades):")
print(f"  {'#':<4} {'Datum':<12} {'Dir':<5} {'Pat':<22} {'Sc':<4} {'ADX':<6} {'Pips':>7} {'P/L':>8} {'Grund':<15} {'Bal':>10}")
print("  " + "-" * 100)
for i, row in df.iterrows():
    date_str = row["exit_dt"].strftime("%d.%m.%Y") if pd.notna(row["exit_dt"]) else "?"
    pat      = str(row["pattern"])[:21] if pd.notna(row["pattern"]) else "-"
    sc       = str(int(row["score"])) + "/4" if pd.notna(row["score"]) else "-"
    adx_s    = f"{row['adx']:.1f}" if pd.notna(row["adx"]) else "-"
    pips_s   = f"{row['pips']:+.1f}" if pd.notna(row["pips"]) else "-"
    result   = "WIN " if row["win"] else "LOSS"
    reason   = str(row["close_event"])[:14]
    print(f"  {i+1:<4} {date_str:<12} {str(row['type']):<5} {pat:<22} {sc:<4} {adx_s:<6} {pips_s:>7} {row['grossProfit']:>+8.2f} {reason:<15} {row['balance']:>10.2f}")

print(SEP)
print()

# ── 7. CSV EXPORT ─────────────────────────────────────────────────────────────
csv_path = os.path.join(OUTPUT_DIR, "backtest_trades.csv")
df.to_csv(csv_path, index=False, encoding="utf-8-sig")
print(f"  CSV gespeichert: {csv_path}")

# ── 8. CHARTS ─────────────────────────────────────────────────────────────────
if PLOTLY_OK:
    def save_chart(fig, name):
        path = os.path.join(OUTPUT_DIR, name + ".png")
        fig.write_image(path)
        print(f"  Chart:  {path}")

    # Equity
    bal_s = df["balance"].dropna().reset_index(drop=True)
    f1    = go.Figure()
    f1.add_trace(go.Scatter(x=list(range(1, len(bal_s)+1)), y=bal_s,
        mode="lines", fill="tozeroy", line=dict(width=2.5)))
    f1.update_layout(title={"text": f"Equity Curve – {total_ret:+.1f}% ({df['exit_dt'].min().strftime('%b %Y')} – {df['exit_dt'].max().strftime('%b %Y')})"})
    f1.update_xaxes(title_text="Trade #")
    f1.update_yaxes(title_text="Balance (EUR)")
    save_chart(f1, "equity_curve")

    # P/L pro Trade
    f2 = go.Figure()
    f2.add_trace(go.Bar(x=list(range(1, n+1)), y=df["grossProfit"],
        marker_color=["#2ecc71" if w else "#e74c3c" for w in df["win"]]))
    f2.add_hline(y=0, line_dash="dash", line_color="white", opacity=0.4)
    f2.update_layout(title={"text": f"P/L je Trade  WR {wr:.0f}%  PF {pf:.2f}"})
    f2.update_xaxes(title_text="Trade #")
    f2.update_yaxes(title_text="P/L (EUR)")
    save_chart(f2, "pnl_per_trade")

    # Pattern
    if df["pattern"].notna().any():
        pdf = df.groupby("pattern").agg(count=("grossProfit","count"),
                pnl=("grossProfit","sum"), wins=("win","sum")).reset_index()
        pdf["wr_pct"] = (pdf["wins"] / pdf["count"] * 100).round(1)
        f3 = go.Figure()
        f3.add_trace(go.Bar(x=pdf["pattern"], y=pdf["pnl"],
            marker_color=["#2ecc71" if p > 0 else "#e74c3c" for p in pdf["pnl"]]))
        f3.update_layout(title={"text": "Net P/L nach Pattern"})
        f3.update_xaxes(title_text="Pattern")
        f3.update_yaxes(title_text="Net P/L (EUR)")
        save_chart(f3, "pattern_pnl")

    # Score
    if df["score"].notna().any():
        sdf = df.groupby("score").agg(count=("grossProfit","count"),
                pnl=("grossProfit","sum"), wins=("win","sum")).reset_index()
        sdf["label"] = sdf["score"].astype(str) + "/4"
        f4 = go.Figure()
        f4.add_trace(go.Bar(x=sdf["label"], y=sdf["pnl"],
            marker_color=["#2ecc71" if p > 0 else "#e74c3c" for p in sdf["pnl"]]))
        f4.update_layout(title={"text": "Net P/L nach Signal-Score"})
        f4.update_xaxes(title_text="Score")
        f4.update_yaxes(title_text="Net P/L (EUR)")
        save_chart(f4, "score_pnl")

    # Wochentag
    DOW    = ["Monday","Tuesday","Wednesday","Thursday","Friday"]
    dow_df = df.groupby("exit_dow").agg(pnl=("grossProfit","sum")).reindex(DOW).fillna(0).reset_index()
    f5 = go.Figure()
    f5.add_trace(go.Bar(x=dow_df["exit_dow"], y=dow_df["pnl"],
        marker_color=["#2ecc71" if p > 0 else "#e74c3c" for p in dow_df["pnl"]]))
    f5.update_layout(title={"text": "Net P/L nach Wochentag"})
    f5.update_xaxes(title_text="Wochentag")
    f5.update_yaxes(title_text="Net P/L (EUR)")
    save_chart(f5, "dow_pnl")

    # Monatlich
    mon_df = df.groupby("exit_month").agg(pnl=("grossProfit","sum")).reset_index()
    f6 = go.Figure()
    f6.add_trace(go.Bar(x=mon_df["exit_month"], y=mon_df["pnl"],
        marker_color=["#2ecc71" if p > 0 else "#e74c3c" for p in mon_df["pnl"]]))
    f6.update_layout(title={"text": "Monatliche P/L"})
    f6.update_xaxes(title_text="Monat", tickangle=30)
    f6.update_yaxes(title_text="Net P/L (EUR)")
    save_chart(f6, "monthly_pnl")

    # Close Reasons
    if len(df_reasons):
        rdf = df_reasons.groupby("reason").size().reset_index(name="count")
        f7  = go.Figure(go.Pie(labels=rdf["reason"], values=rdf["count"], hole=0.3))
        f7.update_layout(title={"text": "Close-Gruende"},
                         uniformtext_minsize=13, uniformtext_mode="hide")
        save_chart(f7, "close_reasons")

print()
print("  Fertig.")
print(SEP)