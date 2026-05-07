// ═══════════════════════════════════════════════════════════════════════════════
//  TenFoldBot – State Persistence (partial class)
//  JSON-file persistence for DailyState, WeeklyState, TradeState.
//  Files live under %APPDATA%/cTrader/10FoldBot/.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Text.Json;
// cAlgo.API defines its own "File" type – alias System.IO.File to avoid ambiguity.
using File = System.IO.File;

namespace cAlgo.Robots
{
    public partial class TenFoldBot
    {
        // ─────────────────────────────────────────────────────────────────────
        //  LoadPersistedState – JSON File Based
        // ─────────────────────────────────────────────────────────────────────
        private void LoadPersistedState()
        {
            if (DisablePersistenceIO) return;
            _persistedTodayLoaded = false;
            string dateKey = Server.Time.ToString("yyyyMMdd");
            string filePath = GetStateFilePath(dateKey);

            try
            {
                if (!File.Exists(filePath))
                    return;

                string json = File.ReadAllText(filePath);
                var state = JsonSerializer.Deserialize<DailyState>(json);

                if (state != null && state.Date == dateKey)
                {
                    _dayStartEquity = state.DayStartEquity;
                    _tradesToday = state.TradesToday;
                    _consecutiveLosses = state.ConsecutiveLosses;
                    _cooldownEndTime = state.CooldownEndTime;
                    _rolloverCheckDoneToday = state.RolloverCheckDoneToday;
                    _persistedTodayLoaded = true;

                    Print("  [✓] Loaded persisted daily state: Equity={0:F2} {1}, Trades={2}, ConsecLoss={3}",
                        _dayStartEquity, Account.Asset.Name, _tradesToday, _consecutiveLosses);
                }
            }
            catch (Exception ex)
            {
                Print("  [!] LoadPersistedState error: {0}", ex.Message);
                _persistedTodayLoaded = false;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PersistDailyState – JSON File Based
        // ─────────────────────────────────────────────────────────────────────
        private void PersistDailyState()
        {
            if (DisablePersistenceIO) return;
            try
            {
                string dateKey = Server.Time.ToString("yyyyMMdd");
                var state = new DailyState
                {
                    Date = dateKey,
                    DayStartEquity = _dayStartEquity,
                    TradesToday = _tradesToday,
                    ConsecutiveLosses = _consecutiveLosses,
                    CooldownEndTime = _cooldownEndTime,
                    RolloverCheckDoneToday = _rolloverCheckDoneToday
                };

                string filePath = GetStateFilePath(dateKey);
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(state, options);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Print("  [!] PersistDailyState error: {0}", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PersistTradeState / LoadPersistedTradeState / DeletePersistedTradeState
        //  (v2.13.0 – P4)
        // ─────────────────────────────────────────────────────────────────────
        private void PersistTradeState()
        {
            if (DisablePersistenceIO) return;
            if (_currentTrade == null)
                return;

            try
            {
                string filePath = GetTradeStateFilePath();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_currentTrade, options);

                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Print("  [!] PersistTradeState error: {0}", ex.Message);
            }
        }

        private TradeState LoadPersistedTradeState()
        {
            if (DisablePersistenceIO) return null;
            try
            {
                string filePath = GetTradeStateFilePath();
                if (!File.Exists(filePath))
                    return null;

                string json = File.ReadAllText(filePath);
                var ts = JsonSerializer.Deserialize<TradeState>(json);
                return ts?.PositionId > 0 ? ts : null;
            }
            catch (Exception ex)
            {
                Print("  [!] LoadPersistedTradeState error: {0}", ex.Message);
                return null;
            }
        }

        private void DeletePersistedTradeState()
        {
            if (DisablePersistenceIO) return;
            try
            {
                string filePath = GetTradeStateFilePath();
                if (File.Exists(filePath))
                    File.Delete(filePath);
            }
            catch (Exception ex)
            {
                Print("  [!] DeletePersistedTradeState error: {0}", ex.Message);
            }
        }

        private void PersistWeeklyState()
        {
            if (DisablePersistenceIO) return;
            if (MaxWeeklyDrawdownPercent <= 0)
                return;

            try
            {
                string wKey = GetWeekMonday(Server.Time).ToString("yyyyMMdd");
                var state = new WeeklyState
                {
                    WeekStart = wKey,
                    WeekStartEquity = _weekStartEquity
                };

                string filePath = GetWeeklyStateFilePath(wKey);
                string directory = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(state, options);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Print("  [!] PersistWeeklyState error: {0}", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  State Persistence Helper Methods
        // ─────────────────────────────────────────────────────────────────────
        private string GetStateBaseDirectory()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string botDir = Path.Combine(appData, "cTrader", "10FoldBot");
            return botDir;
        }

        private string GetStateFilePath(string dateKey)
        {
            return Path.Combine(GetStateBaseDirectory(), $"DailyState_{dateKey}.json");
        }

        private string GetTradeStateFilePath()
        {
            return Path.Combine(GetStateBaseDirectory(), "TradeState.json");
        }

        private string GetWeeklyStateFilePath(string weekKey)
        {
            return Path.Combine(GetStateBaseDirectory(), $"WeeklyState_{weekKey}.json");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  CleanupOldStateFiles – prevents unbounded growth of the state dir.
        //  DailyState_*.json older than keepDays are removed.
        //  WeeklyState_*.json older than keepDays*2 are removed.
        // ─────────────────────────────────────────────────────────────────────
        private void CleanupOldStateFiles(int keepDays = 30)
        {
            if (DisablePersistenceIO) return;
            try
            {
                string dir = GetStateBaseDirectory();
                if (!Directory.Exists(dir))
                    return;

                DateTime dailyCutoff  = DateTime.UtcNow.AddDays(-keepDays);
                DateTime weeklyCutoff = DateTime.UtcNow.AddDays(-keepDays * 2);
                int removed = 0;

                foreach (string path in Directory.GetFiles(dir, "DailyState_*.json"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < dailyCutoff)
                        {
                            File.Delete(path);
                            removed++;
                        }
                    }
                    catch { }
                }

                foreach (string path in Directory.GetFiles(dir, "WeeklyState_*.json"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(path) < weeklyCutoff)
                        {
                            File.Delete(path);
                            removed++;
                        }
                    }
                    catch { }
                }

                if (removed > 0)
                    Print("  [✓] CleanupOldStateFiles: removed {0} stale file(s) from {1}", removed, dir);
            }
            catch (Exception ex)
            {
                Print("  [!] CleanupOldStateFiles error: {0}", ex.Message);
            }
        }
    }
}
