// NT8BridgeServer.cs — NT8 Bridge AddOn (Part 2 v1: in-process compile handler)
//
// Watches  <UserDataDir>\NT8Bridge\trigger\  for {kind:"compile"} request files,
// calls NinjaTrader.Code.Compiler.Compile(...) via reflection, and writes the
// structured Roslyn error diagnostics to  result\compile_<id>.json.
//
// One-time install: copy into  Documents\NinjaTrader 8\bin\Custom\AddOns\,
// then compile in the NinjaScript editor (F5). On load it writes a heartbeat
// so the Python side can tell "AddOn not running" from "compile failed".
//
// Backtest (Strategy Analyzer) handling is Part 3 — not in this file.
// Anti-lockup: every override try/catch-wrapped, timer disposed on Terminated,
// wall-clock polling (no per-tick work), re-entrancy guarded.
#region Using declarations
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Gui.NinjaScript.StrategyAnalyzer;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class NT8BridgeServer : AddOnBase
    {
        private readonly object _gate = new object();
        private Timer _poller;
        private string _triggerDir;
        private string _resultDir;
        private volatile bool _beatInFlight;

        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults)
                {
                    Name = "NT8BridgeServer";
                }
                else if (State == State.Configure)
                {
                    Init();
                }
                else if (State == State.Terminated)
                {
                    try { if (_poller != null) _poller.Dispose(); } catch { }
                    _poller = null;
                }
            }
            catch (Exception ex) { LogSafe("OnStateChange: " + ex.Message); }
        }

        private void Init()
        {
            string root = Path.Combine(Globals.UserDataDir, "NT8Bridge");
            _triggerDir = Path.Combine(root, "trigger");
            _resultDir = Path.Combine(root, "result");
            Directory.CreateDirectory(_triggerDir);
            Directory.CreateDirectory(_resultDir);
            // Diagnostic heartbeat: proves the AddOn loaded and the poller started.
            WriteResult("heartbeat.json",
                "{\"server\":\"NT8BridgeServer\",\"started\":" + JsonStr(Globals.Now.ToString("o")) + "}");
            // Poll once per second on wall-clock time; never tick-time.
            _poller = new Timer(delegate { Poll(); }, null, 1000, 1000);
        }

        private void Poll()
        {
            TryBeat();   // UI-liveness heartbeat (runs regardless of the compile gate)
            // Re-entrancy guard: skip this tick if the previous compile is still running.
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                if (_triggerDir == null) return;
                foreach (string file in Directory.GetFiles(_triggerDir, "*.json"))
                    HandleTrigger(file);
            }
            catch (Exception ex) { LogSafe("Poll: " + ex.Message); }
            finally { Monitor.Exit(_gate); }
        }

        // UI-thread liveness beat: rewrite heartbeat.json from the MAIN UI dispatcher
        // each tick (single in-flight, per the dispatcher-throttle rule). If the UI
        // thread hangs, the file stops updating and `nt8bridge watchdog` detects the
        // stale mtime and restarts NT8. A timer-thread write would falsely stay fresh.
        private void TryBeat()
        {
            try
            {
                if (_beatInFlight) return;
                var disp = Globals.MainThreadDispatcher;
                if (disp == null) return;
                _beatInFlight = true;
                disp.BeginInvoke(new Action(delegate
                {
                    try
                    {
                        WriteResult("heartbeat.json",
                            "{\"server\":\"NT8BridgeServer\",\"beat\":" + JsonStr(DateTime.UtcNow.ToString("o")) + "}");
                    }
                    catch { }
                    finally { _beatInFlight = false; }
                }));
            }
            catch (Exception ex) { _beatInFlight = false; LogSafe("TryBeat: " + ex.Message); }
        }

        private void HandleTrigger(string file)
        {
            string id = null;
            string kind = null;
            try
            {
                string text = File.ReadAllText(file);
                try { File.Delete(file); } catch { }   // consume the trigger
                id = ExtractJsonString(text, "id");
                kind = ExtractJsonString(text, "kind");
                if (kind == "compile")
                    WriteResult("compile_" + id + ".json", RunCompile(id));
                else if (kind == "backtest")
                    RunBacktest(id, ParseParams(text));
                else if (kind == "account")
                    WriteResult("account_" + id + ".json", RunAccountState(id, ExtractJsonString(text, "account")));
            }
            catch (Exception ex)
            {
                LogSafe("HandleTrigger: " + ex.Message);
                if (id != null)
                    // route the fallback error to the file the caller polls (compile_/backtest_/account_).
                    WriteResult(ResultPrefix(kind) + id + ".json",
                        "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"file\":\"\",\"line\":0," +
                        "\"code\":\"BRIDGE\",\"message\":" + JsonStr(ex.Message) + "}],\"assemblyReloaded\":false}");
            }
        }

        // result-file prefix per request kind, so a dispatch-level failure lands in the
        // file the caller is polling rather than always compile_.
        private static string ResultPrefix(string kind)
        {
            if (kind == "account") return "account_";
            if (kind == "backtest") return "backtest_";
            return "compile_";
        }

        private string RunCompile(string id)
        {
            Type compilerType = Type.GetType("NinjaTrader.Code.Compiler, NinjaTrader.Core");
            if (compilerType == null) throw new Exception("NinjaTrader.Code.Compiler not found");
            MethodInfo compile = compilerType.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static);
            if (compile == null) throw new Exception("Compiler.Compile(...) not found");

            var empty = new List<string>();
            // checkCompileOnly=true, debugBuild=false, filesToIgnore=[], filesInTmp=[]
            object emit = compile.Invoke(null, new object[] { true, false, empty, empty });
            return BuildResultJson(id, emit);
        }

        // --- Part 3: run a backtest by firing the SA's RunCommand (the exact
        // RoutedCommand the Run button uses — routes through OnRun, runs on a
        // background thread, SAFE), then polling the tab's Results for the
        // completed run and reading its SystemPerformance. The user configures
        // the SA tab (strategy + instrument + dates); the bridge clicks Run.
        // (Calling StrategyRunner.RunStrategyAsync directly hangs the UI — do not.) ---
        private Window FindStrategyAnalyzerWindow()
        {
            var all = Globals.AllWindows;
            if (all == null) return null;
            var snap = new List<Window>();
            try { for (int i = 0; i < all.Count; i++) snap.Add(all[i]); }
            catch { }
            foreach (Window w in snap)
                if (w != null && w.GetType().FullName.IndexOf("StrategyAnalyzer", StringComparison.Ordinal) >= 0)
                    return w;
            return null;
        }

        private static SystemPerformance LatestPerf(StrategyAnalyzerTabControl tab)
        {
            if (tab == null || tab.Results == null || tab.Results.Count == 0) return null;
            StrategyAnalyzerGridEntry e = tab.Results[tab.Results.Count - 1];
            return e != null ? e.Results : null;
        }

        private void RunBacktest(string id, Dictionary<string, string> prms)
        {
            Window saWin = FindStrategyAnalyzerWindow();
            if (saWin == null)
            {
                WriteResult("backtest_" + id + ".json", BtErr(id, "no Strategy Analyzer window open"));
                return;
            }
            saWin.Dispatcher.BeginInvoke(new Action(delegate
            {
                try
                {
                    StrategyAnalyzerViewModel vm = saWin.DataContext as StrategyAnalyzerViewModel;
                    if (vm == null) { WriteResult("backtest_" + id + ".json", BtErr(id, "SA DataContext is not a StrategyAnalyzerViewModel")); return; }
                    StrategyAnalyzerTabControl tab = vm.SelectedTab;
                    if (tab == null) { WriteResult("backtest_" + id + ".json", BtErr(id, "no active SA tab")); return; }

                    // Inject config.json params onto the configured template before running.
                    if (prms != null && prms.Count > 0 && tab.TabStrategyProperties != null && tab.TabStrategyProperties.StrategyTemplate != null)
                        InjectParams(tab.TabStrategyProperties.StrategyTemplate, prms);

                    // Capture the current latest result's SystemPerformance so the poll can
                    // detect a NEW one. The SA REPLACES the result on a re-run (Results.Count
                    // stays the same), so a count-based check misses every run after the first.
                    SystemPerformance prevPerf = LatestPerf(tab);

                    RoutedCommand rc = StrategyAnalyzerViewModel.RunCommand as RoutedCommand;
                    if (rc == null) { WriteResult("backtest_" + id + ".json", BtErr(id, "RunCommand is not a RoutedCommand")); return; }
                    if (!rc.CanExecute(null, saWin)) { WriteResult("backtest_" + id + ".json", BtErr(id, "RunCommand.CanExecute=false — configure the SA tab (strategy + instrument + dates) first")); return; }

                    // Button-equivalent: routes through OnRun on a background thread (safe).
                    LogSafe("backtest: executing RunCommand (had prior result=" + (prevPerf != null) + ")");
                    rc.Execute(null, saWin);
                    StartResultPoll(id, saWin, prevPerf);
                }
                catch (Exception ex)
                {
                    LogSafe("RunBacktest: " + ex.Message);
                    WriteResult("backtest_" + id + ".json", BtErr(id, ex.GetType().Name + ": " + ex.Message));
                }
            }));
        }

        // Set config.json params onto the configured strategy template by property
        // name (reflection). Values are converted to each property's type
        // (int/double/bool/enum/string). Unknown or read-only props are skipped.
        private void InjectParams(StrategyBase strat, Dictionary<string, string> prms)
        {
            foreach (var kv in prms)
            {
                try
                {
                    PropertyInfo p = strat.GetType().GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                    if (p == null || !p.CanWrite) { LogSafe("param skip (no writable prop '" + kv.Key + "')"); continue; }
                    p.SetValue(strat, ConvertToken(kv.Value, p.PropertyType), null);
                    LogSafe("param set: " + kv.Key + "=" + kv.Value);
                }
                catch (Exception ex) { LogSafe("param set failed '" + kv.Key + "': " + ex.Message); }
            }
        }

        private static object ConvertToken(string token, Type t)
        {
            token = token.Trim();
            bool quoted = token.Length >= 2 && token[0] == '"' && token[token.Length - 1] == '"';
            string raw = quoted
                ? token.Substring(1, token.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\")
                : token;
            Type nt = Nullable.GetUnderlyingType(t) ?? t;
            if (nt.IsEnum) return Enum.Parse(nt, raw, true);
            if (nt == typeof(bool)) return bool.Parse(raw);
            if (nt == typeof(string)) return raw;
            return Convert.ChangeType(raw, nt, System.Globalization.CultureInfo.InvariantCulture);
        }

        // Minimal flat-JSON parser for the trigger's "params" object -> name->rawToken
        // (no JSON-lib dependency, so the AddOn stays offline-precheckable).
        private static Dictionary<string, string> ParseParams(string json)
        {
            var d = new Dictionary<string, string>();
            if (json == null) return d;
            int pi = json.IndexOf("\"params\"", StringComparison.Ordinal);
            if (pi < 0) return d;
            int open = json.IndexOf('{', pi);
            if (open < 0) return d;
            int depth = 0, close = -1; bool inStr = false, esc = false;
            for (int i = open; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) { close = i; break; } }
            }
            if (close < 0) return d;
            string body = json.Substring(open + 1, close - open - 1);
            var pairs = new List<string>();
            int start = 0; depth = 0; inStr = false; esc = false;
            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
                else if (c == ',' && depth == 0) { pairs.Add(body.Substring(start, i - start)); start = i + 1; }
            }
            pairs.Add(body.Substring(start));
            foreach (string pair in pairs)
            {
                int colon = TopLevelColon(pair);
                if (colon < 0) continue;
                string key = pair.Substring(0, colon).Trim();
                if (key.Length >= 2 && key[0] == '"' && key[key.Length - 1] == '"') key = key.Substring(1, key.Length - 2);
                string val = pair.Substring(colon + 1).Trim();
                if (key.Length > 0) d[key] = val;
            }
            return d;
        }

        private static int TopLevelColon(string s)
        {
            bool inStr = false, esc = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (esc) esc = false; else if (c == '\\') esc = true; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == ':') return i;
            }
            return -1;
        }

        // Poll the SA tab's Results (wall-clock, off-thread, read-only) for a new
        // completed run, then write its SystemPerformance. Caps at ~5 min.
        private void StartResultPoll(string id, Window saWin, SystemPerformance prevPerf)
        {
            int[] ticks = { 0 };
            Timer[] tref = new Timer[1];
            tref[0] = new Timer(delegate
            {
                try
                {
                    ticks[0]++;
                    string json = (string)saWin.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        try
                        {
                            StrategyAnalyzerViewModel vm = saWin.DataContext as StrategyAnalyzerViewModel;
                            StrategyAnalyzerTabControl tab = vm != null ? vm.SelectedTab : null;
                            if (tab == null || tab.Results == null || tab.Results.Count == 0) return null;
                            StrategyAnalyzerGridEntry entry = tab.Results[tab.Results.Count - 1];
                            if (entry == null || entry.Results == null) return null;        // running / not populated
                            if (ReferenceEquals(entry.Results, prevPerf)) return null;       // still the previous result
                            return BuildBacktestJson(id, entry.Results, entry.StrategyName); // a NEW SystemPerformance
                        }
                        catch { return null; }
                    }));
                    if (json != null)
                    {
                        WriteResult("backtest_" + id + ".json", json);
                        LogSafe("backtest: result captured for " + id);
                        try { tref[0].Dispose(); } catch { }
                    }
                    else if (ticks[0] >= 150)
                    {
                        WriteResult("backtest_" + id + ".json",
                            BtErr(id, "no completed result within ~5 min (run may still be going, or produced no new Results entry)"));
                        try { tref[0].Dispose(); } catch { }
                    }
                }
                catch (Exception ex) { LogSafe("ResultPoll: " + ex.Message); }
            }, null, 2000, 2000);
        }

        private string BuildBacktestJson(string id, SystemPerformance perf, string stratName)
        {
            if (perf == null) return BtErr(id, "no SystemPerformance");
            TradeCollection all = perf.AllTrades;
            int nTrades = all != null ? all.Count : 0;

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id)).Append(",\"status\":\"ok\",\"strategy\":")
              .Append(JsonStr(stratName != null ? stratName : "")).Append(",\"metrics\":{");
            sb.Append("\"totalTrades\":").Append(nTrades);
            if (all != null && all.TradesPerformance != null)
            {
                TradesPerformance tp = all.TradesPerformance;
                sb.Append(",\"profitFactor\":").Append(Num(tp.ProfitFactor));
                if (tp.Currency != null)
                {
                    sb.Append(",\"netProfit\":").Append(Num(tp.Currency.CumProfit));
                    sb.Append(",\"maxDrawdown\":").Append(Num(tp.Currency.Drawdown));
                }
            }
            sb.Append("},\"trades\":[");
            if (all != null)
            {
                int cap = nTrades < 5000 ? nTrades : 5000;
                for (int i = 0; i < cap; i++)
                {
                    Trade t = all[i];
                    if (i > 0) sb.Append(",");
                    string entry = (t.Entry != null) ? t.Entry.Time.ToString("o") : "";
                    sb.Append("{\"pnl\":").Append(Num(t.ProfitCurrency))
                      .Append(",\"entryTime\":").Append(JsonStr(entry)).Append("}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
            return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private string BtErr(string id, string msg)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"message\":" + JsonStr(msg) + "}]}";
        }

        private string BuildResultJson(string id, object emit)
        {
            bool success = true;
            var errors = new List<string>();
            if (emit != null)
            {
                Type t = emit.GetType();
                PropertyInfo succ = t.GetProperty("Success");
                if (succ != null) success = (bool)succ.GetValue(emit, null);

                PropertyInfo diagProp = t.GetProperty("Diagnostics");
                IEnumerable diags = diagProp != null ? diagProp.GetValue(emit, null) as IEnumerable : null;
                if (diags != null)
                {
                    foreach (object d in diags)
                    {
                        Type dt = d.GetType();
                        object sev = GetProp(dt, d, "Severity");
                        if (sev == null || sev.ToString() != "Error") continue;

                        string code = AsStr(GetProp(dt, d, "Id"));
                        // Roslyn's Diagnostic.GetMessage takes an optional IFormatProvider,
                        // so there is no zero-arg overload — find GetMessage with 0 or 1 param.
                        string msg = "";
                        foreach (MethodInfo mi in dt.GetMethods())
                        {
                            if (mi.Name != "GetMessage") continue;
                            int np = mi.GetParameters().Length;
                            if (np == 0) { msg = AsStr(mi.Invoke(d, null)); break; }
                            if (np == 1) { msg = AsStr(mi.Invoke(d, new object[] { null })); break; }
                        }

                        string filePath = ""; int line = 0;
                        object loc = GetProp(dt, d, "Location");
                        if (loc != null)
                        {
                            MethodInfo span = loc.GetType().GetMethod("GetLineSpan", Type.EmptyTypes);
                            object ls = span != null ? span.Invoke(loc, null) : null;
                            if (ls != null)
                            {
                                filePath = AsStr(GetProp(ls.GetType(), ls, "Path"));
                                object start = GetProp(ls.GetType(), ls, "StartLinePosition");
                                if (start != null)
                                {
                                    object ln = GetProp(start.GetType(), start, "Line");
                                    if (ln is int) line = (int)ln + 1; // Roslyn line is 0-based
                                }
                            }
                        }
                        errors.Add("{\"file\":" + JsonStr(filePath) + ",\"line\":" + line +
                                   ",\"code\":" + JsonStr(code) + ",\"message\":" + JsonStr(msg) + "}");
                    }
                }
            }
            string status = (success && errors.Count == 0) ? "ok" : "error";
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"" + status + "\",\"errors\":[" +
                   string.Join(",", errors.ToArray()) + "],\"assemblyReloaded\":false}";
        }

        // --- Account state: independent, out-of-band read of NT8's own Account
        // objects (open positions, working orders, realized/unrealized P&L, recent
        // completed trades). This is a SEPARATE channel from any status/position feed
        // a strategy publishes: when such a feed stalls (e.g. a closed trade booked as
        // a $0 placeholder because position updates stopped), this still reads NT8's
        // truth. Read-only — never submits/cancels/flattens.
        // Every field access is wrapped so an API mismatch degrades to a null field
        // rather than throwing; the whole body is guarded so a bad call returns a
        // structured {status:"error"} instead of crashing the AddOn loop.
        private string RunAccountState(string id, string accountFilter)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(JsonStr(id))
                  .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"accounts\":[");

                var accts = new List<Account>();
                try { lock (Account.All) { foreach (Account a in Account.All) accts.Add(a); } } catch { }

                bool firstAcct = true;
                foreach (Account acct in accts)
                {
                    string name = SafeStr(delegate { return acct.Name; });
                    if (!string.IsNullOrEmpty(accountFilter) && name != accountFilter) continue;
                    if (!firstAcct) sb.Append(",");
                    firstAcct = false;

                    sb.Append("{\"name\":").Append(JsonStr(name));
                    sb.Append(",\"realizedPnl\":").Append(AcctNum(acct, AccountItem.RealizedProfitLoss));
                    sb.Append(",\"unrealizedPnl\":").Append(AcctNum(acct, AccountItem.UnrealizedProfitLoss));

                    // open positions (only non-flat = live exposure)
                    sb.Append(",\"positions\":[");
                    var positions = new List<Position>();
                    try { lock (acct.Positions) { foreach (Position p in acct.Positions) positions.Add(p); } } catch { }
                    bool firstPos = true;
                    foreach (Position p in positions)
                    {
                        string mp = SafeStr(delegate { return p.MarketPosition.ToString(); });
                        if (mp == "Flat" || mp == "") continue;
                        if (!firstPos) sb.Append(",");
                        firstPos = false;
                        sb.Append("{\"instrument\":").Append(JsonStr(SafeStr(delegate { return p.Instrument.FullName; })))
                          .Append(",\"marketPosition\":").Append(JsonStr(mp))
                          .Append(",\"quantity\":").Append(SafeInt(delegate { return p.Quantity; }).ToString(InvCi))
                          .Append(",\"avgPrice\":").Append(SafeNum(delegate { return p.AveragePrice; }))
                          .Append(",\"unrealizedPnl\":").Append(PosUnrealized(p))
                          .Append("}");
                    }
                    sb.Append("]");

                    // working orders (stops/targets/entries still live at NT8)
                    sb.Append(",\"workingOrders\":[");
                    var orders = new List<Order>();
                    try { lock (acct.Orders) { foreach (Order o in acct.Orders) orders.Add(o); } } catch { }
                    bool firstOrd = true;
                    foreach (Order o in orders)
                    {
                        string st = SafeStr(delegate { return o.OrderState.ToString(); });
                        if (!IsWorkingState(st)) continue;
                        if (!firstOrd) sb.Append(",");
                        firstOrd = false;
                        sb.Append("{\"instrument\":").Append(JsonStr(SafeStr(delegate { return o.Instrument.FullName; })))
                          .Append(",\"action\":").Append(JsonStr(SafeStr(delegate { return o.OrderAction.ToString(); })))
                          .Append(",\"type\":").Append(JsonStr(SafeStr(delegate { return o.OrderType.ToString(); })))
                          .Append(",\"quantity\":").Append(SafeInt(delegate { return o.Quantity; }).ToString(InvCi))
                          .Append(",\"limitPrice\":").Append(SafeNum(delegate { return o.LimitPrice; }))
                          .Append(",\"stopPrice\":").Append(SafeNum(delegate { return o.StopPrice; }))
                          .Append(",\"name\":").Append(JsonStr(SafeStr(delegate { return o.Name; })))
                          .Append(",\"state\":").Append(JsonStr(st))
                          .Append("}");
                    }
                    sb.Append("]");

                    // recent raw fills (entry + exit executions — the authoritative "what
                    // actually filled"; account-level realizedPnl above is the session total).
                    sb.Append(",\"recentExecutions\":[");
                    var execs = new List<Execution>();
                    try { lock (acct.Executions) { foreach (Execution e in acct.Executions) execs.Add(e); } } catch { }
                    int from = execs.Count > RecentExecutionsMax ? execs.Count - RecentExecutionsMax : 0;
                    bool firstEx = true;
                    for (int xi = from; xi < execs.Count; xi++)
                    {
                        Execution e = execs[xi];
                        if (!firstEx) sb.Append(",");
                        firstEx = false;
                        sb.Append("{\"instrument\":").Append(JsonStr(SafeStr(delegate { return e.Instrument.FullName; })))
                          .Append(",\"marketPosition\":").Append(JsonStr(SafeStr(delegate { return e.MarketPosition.ToString(); })))
                          .Append(",\"quantity\":").Append(SafeInt(delegate { return e.Quantity; }).ToString(InvCi))
                          .Append(",\"price\":").Append(SafeNum(delegate { return e.Price; }))
                          .Append(",\"time\":").Append(JsonStr(SafeStr(delegate { return e.Time.ToUniversalTime().ToString("o"); })))
                          .Append(",\"commission\":").Append(SafeNum(delegate { return e.Commission; }))
                          .Append(",\"orderName\":").Append(JsonStr(SafeStr(delegate { return e.Order.Name; })))
                          .Append("}");
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"ts\":" +
                       JsonStr(DateTime.UtcNow.ToString("o")) + ",\"accounts\":[],\"errors\":[{" +
                       "\"code\":\"BRIDGE\",\"message\":" + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        private static readonly System.Globalization.CultureInfo InvCi =
            System.Globalization.CultureInfo.InvariantCulture;

        // how many most-recent executions to include per account
        private const int RecentExecutionsMax = 12;

        private static bool IsWorkingState(string s)
        {
            return s == "Working" || s == "Accepted" || s == "Submitted" || s == "PartFilled"
                || s == "TriggerPending" || s == "ChangeSubmitted" || s == "ChangePending"
                || s == "CancelSubmitted";
        }

        private static string AcctNum(Account a, AccountItem item)
        {
            try { return Num(a.Get(item, Currency.UsDollar)); }
            catch { return "null"; }
        }

        private static string PosUnrealized(Position p)
        {
            try { return Num(p.GetUnrealizedProfitLoss(PerformanceUnit.Currency, p.Instrument.MarketData.Last.Price)); }
            catch { return "null"; }
        }

        private static string SafeStr(Func<string> f) { try { string s = f(); return s == null ? "" : s; } catch { return ""; } }
        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        // like Num(...) on the getter, but a read FAILURE degrades to JSON null (not a misleading 0).
        private static string SafeNum(Func<double> f) { try { return Num(f()); } catch { return "null"; } }

        private static object GetProp(Type t, object o, string name)
        {
            PropertyInfo p = t.GetProperty(name);
            return p != null ? p.GetValue(o, null) : null;
        }

        private static string AsStr(object o) { return o == null ? "" : o.ToString(); }

        private void WriteResult(string name, string json)
        {
            try
            {
                string dst = Path.Combine(_resultDir, name);
                string tmp = dst + ".tmp";
                File.WriteAllText(tmp, json);
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);   // atomic rename so Python never reads a partial file
            }
            catch (Exception ex) { LogSafe("WriteResult: " + ex.Message); }
        }

        private static string JsonStr(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append("\"").ToString();
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (json == null) return null;
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + pat.Length);
            if (i < 0) return null;
            int q1 = json.IndexOf('"', i + 1);
            if (q1 < 0) return null;
            int q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1);
        }

        private void LogSafe(string msg)
        {
            try
            {
                string dir = _resultDir != null ? _resultDir : Path.GetTempPath();
                File.AppendAllText(Path.Combine(dir, "bridge.log"),
                    DateTime.UtcNow.ToString("o") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
