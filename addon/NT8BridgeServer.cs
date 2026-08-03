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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;   // windows: Win32 window inventory (NT is multi-UI-threaded)
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;           // windows: WindowInteropHelper -> HWND
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
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
        // connection name -> last drop classification ("inadvertent"|"user"|"connected"),
        // written by the ConnectionStatusUpdate event (fires on arbitrary threads) so it
        // MUST be a ConcurrentDictionary; read by the poller thread (connections handler).
        private readonly ConcurrentDictionary<string, string> _dropClass = new ConcurrentDictionary<string, string>();

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
                    try { Connection.ConnectionStatusUpdate -= OnConnStatus; } catch { }
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
            // Classify every connection status change at the moment it happens (named
            // handler so Terminated can unsubscribe it — no leak across reload cycles).
            // ConnectionLost / error-disconnect = inadvertent (auto-reconnect eligible);
            // clean Disconnected / UserAbort = the user parked it (never auto-reconnect).
            try { Connection.ConnectionStatusUpdate += OnConnStatus; } catch (Exception ex) { LogSafe("conn subscribe: " + ex.Message); }
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
                else if (kind == "reload")
                    WriteResult("reload_" + id + ".json", RunCompileCore(id, true));
                else if (kind == "windows")
                    WriteResult("windows_" + id + ".json", RunWindows(id));
                else if (kind == "backtest")
                    RunBacktest(id, ParseParams(text));
                else if (kind == "account")
                    WriteResult("account_" + id + ".json", RunAccountState(id, ExtractJsonString(text, "account")));
                else if (kind == "flatten")
                    WriteResult("flatten_" + id + ".json", RunFlatten(id, ExtractJsonString(text, "account"), ExtractJsonString(text, "instrument")));
                else if (kind == "connections")
                    WriteResult("connections_" + id + ".json", RunConnections(id));
                else if (kind == "reconnect")
                    WriteResult("reconnect_" + id + ".json", RunReconnect(id, ExtractJsonString(text, "connection")));
                else if (kind == "peek")
                    WriteResult("peek_" + id + ".json", RunPeek(id));
                else if (kind == "probe")
                    WriteResult("probe_" + id + ".json", RunProbe(id));
                else if (kind == "configure")
                    WriteResult("configure_" + id + ".json", RunConfigure(id, text));
                else if (kind == "feedhealth")
                    WriteResult("feedhealth_" + id + ".json", RunFeedHealth(id, ExtractJsonString(text, "instruments")));
                else if (kind == "performance")
                    WriteResult("perf_" + id + ".json", RunPerformance(id,
                        ExtractJsonString(text, "account"), ExtractJsonString(text, "from"),
                        ExtractJsonString(text, "to"), ExtractJsonString(text, "instrument")));
                else if (kind == "perfwindow")
                    WriteResult("perfwindow_" + id + ".json", RunPerfWindow(id, ExtractJsonString(text, "account"),
                        ExtractJsonString(text, "generate") == "true",
                        ExtractJsonString(text, "from"), ExtractJsonString(text, "to")));
                else if (kind == "chartseries")
                    WriteResult("chartseries_" + id + ".json", RunChartSeries(id, text));
                else if (kind == "marketReplayDump")
                    WriteResult("marketReplayDump_" + id + ".json", RunMarketReplayDump(id, text));
                else if (kind == "marketReplayDownload")
                    WriteResult("marketReplayDownload_" + id + ".json", RunMarketReplayDownload(id, text));
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
            if (kind == "flatten") return "flatten_";
            if (kind == "backtest") return "backtest_";
            if (kind == "connections") return "connections_";
            if (kind == "reconnect") return "reconnect_";
            if (kind == "peek") return "peek_";
            if (kind == "probe") return "probe_";
            if (kind == "configure") return "configure_";
            if (kind == "feedhealth") return "feedhealth_";
            if (kind == "performance") return "perf_";
            if (kind == "perfwindow") return "perfwindow_";
            if (kind == "chartseries") return "chartseries_";
            if (kind == "reload") return "reload_";
            if (kind == "windows") return "windows_";
            if (kind == "marketReplayDump") return "marketReplayDump_";
            if (kind == "marketReplayDownload") return "marketReplayDownload_";
            return "compile_";
        }

        private string RunCompile(string id)
        {
            return RunCompileCore(id, false);
        }

        // ── compile vs reload ────────────────────────────────────────────────────────────────────
        //  `checkCompileOnly` is the ONLY difference, and it is the difference between "does this
        //  code build" and "is this code now RUNNING".
        //
        //    compile  (checkCompileOnly = TRUE)  — validates the tree and emits nothing. Fast, and it
        //             disturbs nothing: no indicator reloads, no strategy restarts, no chart flicker.
        //             This is what you want in an edit loop.
        //    reload   (checkCompileOnly = FALSE) — a real build. NinjaTrader swaps in the new
        //             NinjaScript assembly exactly as the Editor's F5 does, so new TYPES appear in the
        //             pickers and existing indicators reload. This is the step that used to require a
        //             human pressing F5.
        //
        //  ⚠ reload is DISRUPTIVE by nature: it restarts indicators and can interrupt a running
        //  strategy, and on this suite it also orphans bars-type instances (they are NOT recreated by
        //  a reload or a chart reload — only by an NT restart). Never fire it into a live bake.
        //  Kept as a SEPARATE command rather than a flag on compile so it can never happen by accident.
        private string RunCompileCore(string id, bool reload)
        {
            Type compilerType = Type.GetType("NinjaTrader.Code.Compiler, NinjaTrader.Core");
            if (compilerType == null) throw new Exception("NinjaTrader.Code.Compiler not found");
            MethodInfo compile = compilerType.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static);
            if (compile == null) throw new Exception("Compiler.Compile(...) not found");

            var empty = new List<string>();
            // checkCompileOnly, debugBuild=false, filesToIgnore=[], filesInTmp=[]
            object emit = compile.Invoke(null, new object[] { !reload, false, empty, empty });

            // Report the reload HONESTLY: only a non-check build that actually succeeded swapped the
            // assembly. Previously this field was hardcoded false, which made a real reload
            // indistinguishable from a check — the caller could never tell whether its code was live.
            bool ok = EmitSucceeded(emit);
            return BuildResultJson(id, emit, reload && ok);
        }

        // ═════════════════════════════════════════════════════════════════════════════════════════
        //  windows — an inventory of NinjaTrader's top-level windows.
        //
        //  ⚠ WIN32 ONLY, AND THIS IS NOT A STYLE CHOICE. NinjaTrader runs EACH WINDOW ON ITS OWN
        //  DISPATCHER THREAD, so reading `w.Left` / `.ActualWidth` / `.IsVisible` / `.WindowState`
        //  from this poller thread throws `InvalidOperationException: The calling thread cannot access
        //  this object because a different thread owns it` — on every window, every time. A handler
        //  written against WPF properties returns an empty list and looks like "NT has no windows".
        //  GetWindowRect / IsWindowVisible / IsIconic / IsZoomed are thread-agnostic; use those.
        //
        //  `Globals.AllWindows` is only touched to collect HWNDs (cheap, no geometry), and even that
        //  is snapshotted first because the collection mutates as windows open and close.
        // ═════════════════════════════════════════════════════════════════════════════════════════
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out BridgeRect r);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);

        [StructLayout(LayoutKind.Sequential)]
        private struct BridgeRect { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        private string RunWindows(string id)
        {
            //  ⚠ ENUMERATED VIA WIN32, NOT VIA Globals.AllWindows.
            //
            //  The first version of this walked `Globals.AllWindows` and called
            //  `new WindowInteropHelper(w).Handle` to get each HWND. That THROWS: WindowInteropHelper
            //  reads the Window's HwndSource, which is thread-affine like every other WPF member, so
            //  from the poller thread it raises "The calling thread cannot access this object because
            //  a different thread owns it" for EVERY window and the handler returns an empty list —
            //  a confident `status:"ok", count:0` that looks like NinjaTrader has no windows.
            //
            //  You cannot even obtain the HANDLE off-thread. So do not start from WPF objects at all:
            //  EnumWindows filtered by our own process id is completely thread-agnostic, needs no
            //  dispatcher marshalling (which could deadlock against a busy window thread), and also
            //  catches top-level windows that never appear in Globals.AllWindows.
            var rows = new List<string>();
            try
            {
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                // Held in a local so the GC cannot collect the delegate while EnumWindows runs.
                EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        uint pid;
                        GetWindowThreadProcessId(h, out pid);
                        if (pid != self || !IsWindow(h)) return true;

                        var title = new StringBuilder(400);
                        GetWindowText(h, title, title.Capacity);
                        var cls = new StringBuilder(200);
                        GetClassName(h, cls, cls.Capacity);

                        // Untitled HWNDs are message-only//tooltip/layered helpers, not real windows.
                        if (title.Length == 0) return true;

                        BridgeRect r;
                        bool got = GetWindowRect(h, out r);
                        rows.Add("{\"hwnd\":" + h.ToInt64().ToString(InvCi) +
                                 ",\"title\":" + JsonStr(title.ToString()) +
                                 ",\"class\":" + JsonStr(cls.ToString()) +
                                 ",\"visible\":" + (IsWindowVisible(h) ? "true" : "false") +
                                 ",\"minimized\":" + (IsIconic(h) ? "true" : "false") +
                                 ",\"maximized\":" + (IsZoomed(h) ? "true" : "false") +
                                 ",\"left\":" + (got ? r.Left : 0).ToString(InvCi) +
                                 ",\"top\":" + (got ? r.Top : 0).ToString(InvCi) +
                                 ",\"width\":" + (got ? r.Right - r.Left : 0).ToString(InvCi) +
                                 ",\"height\":" + (got ? r.Bottom - r.Top : 0).ToString(InvCi) + "}");
                    }
                    catch (Exception ex) { LogSafe("RunWindows row: " + ex.Message); }
                    return true;
                };
                EnumWindows(cb, IntPtr.Zero);
                GC.KeepAlive(cb);
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"message\":" + JsonStr(ex.Message) + "}";
            }
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"count\":" + rows.Count.ToString(InvCi) +
                   ",\"windows\":[" + string.Join(",", rows.ToArray()) + "]}";
        }

        private static bool EmitSucceeded(object emit)
        {
            try
            {
                if (emit == null) return true;               // no result object == nothing to complain about
                PropertyInfo succ = emit.GetType().GetProperty("Success");
                return succ == null || (bool)succ.GetValue(emit, null);
            }
            catch { return false; }
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

        // ===== Live chart data-series control (chartseries) =====
        // Thin handler: parse the trigger, then delegate find-chart + resolve + OnDataSeriesChanged to the
        // shared ChartDataSeriesSwitcher (the ChartScheduler reuses the same engine). The switcher is
        // invoked by REFLECTION on purpose: the offline precheck compiles ONE .cs against the prebuilt
        // Custom.dll, so a direct type reference to the sibling (not yet in that DLL) would be CS0246; at
        // runtime both files compile into NinjaTrader.Custom.dll and Type.GetType resolves it.
        // Reproduces NRDToCSV's full-depth export (mode 2) server-side: the exact MarketReplay engine,
        // no window. Writes the raw L1+L2 dump straight to outPath, returns the row count. Instrument
        // resolution matches NRDToCSV (InstrumentList.GetInstruments) so output is byte-faithful.
        // NOTE: DumpMarketDepth runs inline under the poller gate, so a dump SERIALIZES the bridge for
        // its duration (other trigger kinds wait). One-day dumps are quick and the Python client's
        // per-date timeout governs; a pathologically hung dump would block the bridge (heartbeat keeps
        // beating, so the watchdog would not restart) — acceptable for the batch-export use case.
        private string RunMarketReplayDump(string id, string text)
        {
            try
            {
                string instrName = ExtractJsonString(text, "instrument");
                string dateStr   = ExtractJsonString(text, "date");
                string outPath   = ExtractJsonString(text, "outPath");
                string mode      = ExtractJsonString(text, "mode");
                if (string.IsNullOrEmpty(mode)) mode = "depth";
                if (mode != "depth")
                    return CsErr(id, "only mode=depth is supported in this build (got '" + mode + "')");
                if (string.IsNullOrEmpty(instrName) || string.IsNullOrEmpty(dateStr) || string.IsNullOrEmpty(outPath))
                    return CsErr(id, "marketReplayDump requires instrument, date (YYYYMMDD), outPath");

                DateTime day;
                if (!DateTime.TryParseExact(dateStr, "yyyyMMdd",
                        InvCi, System.Globalization.DateTimeStyles.None, out day))
                    return CsErr(id, "bad date (expected YYYYMMDD): " + dateStr);

                Instrument inst = null;
                try
                {
                    var matches = InstrumentList.GetInstruments(instrName);
                    if (matches != null && matches.Count > 1)
                        return CsErr(id, "ambiguous instrument name: " + instrName);
                    if (matches != null && matches.Count == 1)
                        inst = matches[0];
                }
                catch { }
                if (inst == null) return CsErr(id, "instrument not found: " + instrName);

                try { string dir = System.IO.Path.GetDirectoryName(outPath); if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir); } catch { }

                MarketReplay.DumpMarketDepth(inst, day, day, outPath);

                long rows = 0;
                try { using (var sr = new System.IO.StreamReader(outPath)) { while (sr.ReadLine() != null) rows++; } } catch { }

                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"rows\":" + rows.ToString(InvCi)
                     + ",\"outPath\":" + JsonStr(outPath) + "}";
            }
            catch (Exception ex) { return CsErr(id, "marketReplayDump: " + ex.Message); }
        }

        // Download ONE day of MarketReplay data for an instrument, then return. Uses the connection's
        // HDS client's RequestMarketReplay(instrument, dateEst, Action<ErrorCode,string,object>, IProgress,
        // state) via reflection — the same path the community MultidayReplayDownloader uses. The call is
        // async under the hood; we block the poller on a ManualResetEvent until the callback fires, so a
        // download SERIALIZES the bridge for its duration (like marketReplayDump; the heartbeat runs off a
        // separate beat so the watchdog won't restart). Writes db/replay/<FullName>/<yyyyMMdd>.nrd.
        // The Python client loops dates + skips existing; this handler always attempts the requested date.
        private string RunMarketReplayDownload(string id, string text)
        {
            try
            {
                string instrName = ExtractJsonString(text, "instrument");
                string dateStr   = ExtractJsonString(text, "date");
                if (string.IsNullOrEmpty(instrName) || string.IsNullOrEmpty(dateStr))
                    return CsErr(id, "marketReplayDownload requires instrument, date (YYYYMMDD)");

                DateTime day;
                if (!DateTime.TryParseExact(dateStr, "yyyyMMdd", InvCi,
                        System.Globalization.DateTimeStyles.None, out day))
                    return CsErr(id, "bad date (expected YYYYMMDD): " + dateStr);

                Instrument inst = null;
                try
                {
                    var matches = InstrumentList.GetInstruments(instrName);
                    if (matches != null && matches.Count > 1)
                        return CsErr(id, "ambiguous instrument name: " + instrName);
                    if (matches != null && matches.Count == 1)
                        inst = matches[0];
                }
                catch { }
                if (inst == null) return CsErr(id, "instrument not found: " + instrName);

                object hdsClient; MethodInfo requestMethod;
                ResolveMarketReplayRequester(out hdsClient, out requestMethod);
                if (hdsClient == null || requestMethod == null)
                    return CsErr(id, "no MarketReplay download service — need an active data connection "
                                   + "(Tradovate/Continuum) logged in");

                // Heavy MNQ days (~500 MB) take 300-460s; the wait is client-configurable via
                // "timeoutSec" (histget passes its --timeout) so a slow day isn't false-reported as a
                // timeout while NT8 finishes the download in the background. Default 600s.
                double waitSec = 600;
                string toStr = ExtractJsonString(text, "timeoutSec");
                if (!string.IsNullOrEmpty(toStr))
                {
                    double t;
                    if (double.TryParse(toStr, System.Globalization.NumberStyles.Any, InvCi, out t) && t > 0)
                        waitSec = t;
                }

                ErrorCode ec = ErrorCode.NoError; string emsg = null;
                using (var done = new ManualResetEventSlim(false))
                {
                    Action<ErrorCode, string, object> cb = (code, msg, state) =>
                    { ec = code; emsg = msg; try { done.Set(); } catch { } };
                    // (Instrument, DateTime dateEst[ET], callback, IProgress, object state)
                    requestMethod.Invoke(hdsClient, new object[] { inst, day, cb, null, null });
                    if (!done.Wait(TimeSpan.FromSeconds(waitSec)))
                        return CsErr(id, "download timed out (" + waitSec.ToString("0", InvCi)
                                       + "s) for " + dateStr);
                }
                if (ec != ErrorCode.NoError)
                    return CsErr(id, "download " + ec.ToString()
                                   + (string.IsNullOrEmpty(emsg) ? "" : ": " + emsg) + " (" + dateStr + ")");

                string nrdPath = System.IO.Path.Combine(Globals.UserDataDir, "db", "replay",
                                                        inst.FullName, dateStr + ".nrd");
                bool exists = System.IO.File.Exists(nrdPath);
                long bytes = 0;
                try { if (exists) bytes = new System.IO.FileInfo(nrdPath).Length; } catch { }
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"date\":" + JsonStr(dateStr)
                     + ",\"instrument\":" + JsonStr(inst.FullName) + ",\"nrd\":" + JsonStr(nrdPath)
                     + ",\"exists\":" + (exists ? "true" : "false") + ",\"bytes\":" + bytes.ToString(InvCi) + "}";
            }
            catch (Exception ex)
            {
                Exception e = ex.InnerException ?? ex;
                return CsErr(id, "marketReplayDownload: " + e.Message);
            }
        }

        // Reflection: find a connection's HDS client (HistoricalDataClient or Adapter) that exposes
        // RequestMarketReplay. Mirrors the community MultidayReplayDownloader's discovery: try
        // Connection.ClientConnection (HistoricalDataClient, else Adapter), then scan Connection.Connections.
        private void ResolveMarketReplayRequester(out object hdsClient, out MethodInfo requestMethod)
        {
            hdsClient = null; requestMethod = null;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                if (Connection.ClientConnection != null)
                {
                    var prop = Connection.ClientConnection.GetType().GetProperty("HistoricalDataClient", F);
                    if (prop != null) hdsClient = prop.GetValue(Connection.ClientConnection);
                    if (hdsClient == null)
                    {
                        var adapterProp = Connection.ClientConnection.GetType()
                            .GetProperty("Adapter", BindingFlags.Public | BindingFlags.Instance);
                        if (adapterProp != null)
                        {
                            var adapter = adapterProp.GetValue(Connection.ClientConnection);
                            if (adapter != null)
                            {
                                var m = adapter.GetType().GetMethod("RequestMarketReplay", F);
                                if (m != null) { hdsClient = adapter; requestMethod = m; }
                            }
                        }
                    }
                }
                if (hdsClient == null && Connection.Connections != null)
                {
                    foreach (Connection conn in Connection.Connections)
                    {
                        if (conn == null) continue;
                        var prop = conn.GetType().GetProperty("HistoricalDataClient", F);
                        if (prop != null)
                        {
                            var client = prop.GetValue(conn);
                            if (client != null) { hdsClient = client; break; }
                        }
                    }
                }
                if (hdsClient != null && requestMethod == null)
                    requestMethod = hdsClient.GetType().GetMethod("RequestMarketReplay", F);
            }
            catch { }
        }

        // Trigger schema (Task 3): {id, kind:"chartseries", target:{mode:"active|instrument|title", value?},
        //   dataseries:{instrument?, barsPeriodType?, barsPeriodValue?, barsPeriodValue2?, baseBarsPeriodValue?}, force}.
        // PARSE SCOPING: target.mode's VALUE can be "instrument"/"title", so slice each nested object and
        // parse its keys from that slice; force is the lone top-level bool (JsonBoolFlag).
        private string RunChartSeries(string id, string triggerJson)
        {
            string targetObj = ExtractJsonObject(triggerJson, "target");
            string dataObj = ExtractJsonObject(triggerJson, "dataseries");

            string mode = ExtractJsonString(targetObj, "mode");
            if (string.IsNullOrEmpty(mode)) mode = "active";
            string targetValue = ExtractJsonString(targetObj, "value");
            string newInstrName = ExtractJsonString(dataObj, "instrument");
            string btype = ExtractJsonString(dataObj, "barsPeriodType");
            bool force = JsonBoolFlag(triggerJson, "force");

            // barsPeriodType may be an enum NAME ("Minute") or a numeric custom id ("12345"/"2018").
            int typeId = -1;   // -1 => keep the chart's current bar type
            if (!string.IsNullOrEmpty(btype))
            {
                int parsed;
                if (int.TryParse(btype, out parsed)) typeId = parsed;
                else
                {
                    try { typeId = (int)(BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), btype, true); }
                    catch { return CsErr(id, "unknown barsPeriodType: " + btype); }
                }
            }
            object v  = ParseNullableInt(ExtractJsonString(dataObj, "barsPeriodValue"));
            object v2 = ParseNullableInt(ExtractJsonString(dataObj, "barsPeriodValue2"));
            object bv = ParseNullableInt(ExtractJsonString(dataObj, "baseBarsPeriodValue"));

            // Delegate to the shared switcher (reflection -- see note above). title -> by tab name;
            // active -> empty tab name (switcher targets the active/sole chart); instrument -> by the
            // chart's current instrument.
            object sr;
            if (mode == "instrument")
                sr = InvokeSwitcher("SwitchByInstrument", new object[] { targetValue, newInstrName, typeId, v, v2, bv, null, force });
            else
                sr = InvokeSwitcher("Switch", new object[] { mode == "title" ? targetValue : "", newInstrName, typeId, v, v2, bv, null, force });

            if (sr == null) return CsErr(id, "ChartDataSeriesSwitcher unavailable (not compiled into Custom.dll?)");

            string status = SrStr(sr, "Status");      // ok | blocked | missing | error
            string message = SrStr(sr, "Message");
            bool have = status == "ok" || status == "blocked";
            string jsonStatus = status == "ok" ? "ok" : (status == "blocked" ? "blocked" : "error");

            if (!have)
                return "{\"id\":" + JsonStr(id) + ",\"status\":" + JsonStr(jsonStatus) + ",\"matched\":0"
                     + ",\"chart\":null,\"message\":" + JsonStr(message)
                     + ",\"errors\":[{\"code\":\"BRIDGE\",\"message\":" + JsonStr(message) + "}]}";

            string before = "{\"instrument\":" + JsonStr(SrStr(sr, "BeforeInstrument")) + ",\"bars\":" + JsonStr(SrStr(sr, "BeforeBars")) + "}";
            string after  = "{\"instrument\":" + JsonStr(SrStr(sr, "AfterInstrument"))  + ",\"bars\":" + JsonStr(SrStr(sr, "AfterBars"))  + "}";
            return "{\"id\":" + JsonStr(id) + ",\"status\":" + JsonStr(jsonStatus) + ",\"matched\":1,\"chart\":{\"title\":"
                 + JsonStr(SrStr(sr, "Title")) + ",\"before\":" + before + ",\"after\":" + after + "},\"message\":"
                 + JsonStr(message) + ",\"errors\":[]}";
        }

        // Invoke a static method on the sibling ChartDataSeriesSwitcher by reflection (same assembly at
        // runtime). Returns the SwitchResult object, or null if the type/method is unavailable.
        private object InvokeSwitcher(string method, object[] args)
        {
            try
            {
                Type t = Type.GetType("NinjaTrader.NinjaScript.AddOns.ChartDataSeriesSwitcher");
                if (t == null) return null;
                MethodInfo m = t.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
                if (m == null) return null;
                return m.Invoke(null, args);
            }
            catch (Exception ex) { LogSafe("InvokeSwitcher " + method + ": " + ex.Message); return null; }
        }

        // read a public string property off the reflected SwitchResult.
        private static string SrStr(object sr, string name)
        {
            try { PropertyInfo p = sr.GetType().GetProperty(name); object v = p != null ? p.GetValue(sr, null) : null; return v != null ? v.ToString() : ""; }
            catch { return ""; }
        }

        // "5" -> boxed int 5 ; "" / non-numeric -> null (boxed for the int? reflection parameter).
        private static object ParseNullableInt(string s)
        {
            int x;
            return (!string.IsNullOrEmpty(s) && int.TryParse(s, out x)) ? (object)x : null;
        }

        private string CsErr(string id, string msg)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"matched\":0,\"chart\":null,\"message\":" + JsonStr(msg)
                 + ",\"errors\":[{\"code\":\"BRIDGE\",\"message\":" + JsonStr(msg) + "}]}";
        }

        // true only if the JSON carries the KEY  "<key>": true  (ExtractJsonString reads only quoted
        // values, so a bare boolean must be parsed here). Matches the "<key>" occurrence immediately
        // followed by ':' -- so a string VALUE that happens to equal the key (e.g. --on-title "force")
        // cannot trip it.
        private static bool JsonBoolFlag(string json, string key)
        {
            if (json == null) return false;
            string pat = "\"" + key + "\"";
            int from = 0;
            while (true)
            {
                int i = json.IndexOf(pat, from, StringComparison.Ordinal);
                if (i < 0) return false;
                int j = i + pat.Length;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j < json.Length && json[j] == ':')   // KEY occurrence (followed by a colon)
                {
                    j++;
                    while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    return json.Length - j >= 4 && json.Substring(j, 4) == "true";
                }
                from = i + pat.Length;   // a string VALUE equal to the key -> keep scanning for the real key
            }
        }

        private static SystemPerformance LatestPerf(StrategyAnalyzerTabControl tab)
        {
            if (tab == null || tab.Results == null || tab.Results.Count == 0) return null;
            StrategyAnalyzerGridEntry e = tab.Results[tab.Results.Count - 1];
            return e != null ? e.Results : null;
        }

        // Read-only: capture the SA tab's latest completed result WITHOUT firing a new Run,
        // PLUS a read-back of the strategy template's current NinjaScriptProperty inputs (so the
        // caller can verify param injection actually took — a wrong-params run looks valid otherwise).
        private string RunPeek(string id)
        {
            Window saWin = FindStrategyAnalyzerWindow();
            if (saWin == null) return BtErr(id, "no Strategy Analyzer window open");
            try
            {
                return (string)saWin.Dispatcher.Invoke(new Func<string>(delegate
                {
                    try
                    {
                        StrategyAnalyzerViewModel vm = saWin.DataContext as StrategyAnalyzerViewModel;
                        StrategyAnalyzerTabControl tab = vm != null ? vm.SelectedTab : null;
                        if (tab == null) return BtErr(id, "no active SA tab");
                        StrategyBase strat = (tab.TabStrategyProperties != null) ? tab.TabStrategyProperties.StrategyTemplate : null;
                        string prm = ParamReadback(strat);
                        SystemPerformance perf = LatestPerf(tab);
                        if (perf == null)
                            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"metrics\":null,\"note\":"
                                + JsonStr("no completed result in the SA grid (run one first)") + ",\"params\":" + prm + "}";
                        string bt = BuildBacktestJson(id, perf, strat != null ? strat.Name : "");
                        if (bt.EndsWith("}")) bt = bt.Substring(0, bt.Length - 1) + ",\"params\":" + prm + "}";
                        return bt;
                    }
                    catch (Exception ex) { return BtErr(id, "peek read: " + ex.Message); }
                }));
            }
            catch (Exception ex) { return BtErr(id, "peek: " + ex.Message); }
        }

        // Read every public read/write property carrying a NinjaScriptProperty attribute (the
        // strategy's user inputs) -> {name: "value"}. Generic across strategies.
        private string ParamReadback(StrategyBase strat)
        {
            if (strat == null) return "null";
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (PropertyInfo p in strat.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                try
                {
                    if (!p.CanRead || !p.CanWrite) continue;
                    bool isInput = false;
                    foreach (object a in p.GetCustomAttributes(false))
                        if (a.GetType().Name.IndexOf("NinjaScriptProperty", StringComparison.Ordinal) >= 0) { isInput = true; break; }
                    if (!isInput) continue;
                    object v = p.GetValue(strat, null);
                    if (!first) sb.Append(",");
                    sb.Append(JsonStr(p.Name)).Append(":").Append(JsonStr(v != null ? v.ToString() : "null"));
                    first = false;
                }
                catch { }
            }
            sb.Append("}");
            return sb.ToString();
        }

        // Probe: reflect on tab + tab.TabStrategyProperties + StrategyTemplate. Emits each
        // public read/write property with name, type, and current value (string repr). Used
        // to DISCOVER property names before writing a `configure` config — NT8's SA tab
        // members are partly obfuscated so naive guessing wastes cycles. Read-only.
        private string RunProbe(string id)
        {
            Window saWin = FindStrategyAnalyzerWindow();
            if (saWin == null) return BtErr(id, "no Strategy Analyzer window open");
            try
            {
                return (string)saWin.Dispatcher.Invoke(new Func<string>(delegate
                {
                    try
                    {
                        StrategyAnalyzerViewModel vm = saWin.DataContext as StrategyAnalyzerViewModel;
                        StrategyAnalyzerTabControl tab = vm != null ? vm.SelectedTab : null;
                        if (tab == null) return BtErr(id, "no active SA tab");
                        object tsp = tab.TabStrategyProperties;
                        object strat = (tsp != null) ? tab.TabStrategyProperties.StrategyTemplate : null;
                        var sb = new StringBuilder("{\"id\":").Append(JsonStr(id));
                        sb.Append(",\"status\":\"ok\",\"tab\":");
                        DumpProps(sb, tab);
                        sb.Append(",\"tabStrategyProperties\":");
                        DumpProps(sb, tsp);
                        sb.Append(",\"strategyTemplate\":");
                        DumpProps(sb, strat);
                        sb.Append("}");
                        return sb.ToString();
                    }
                    catch (Exception ex) { return BtErr(id, "probe: " + ex.Message); }
                }));
            }
            catch (Exception ex) { return BtErr(id, "probe outer: " + ex.Message); }
        }

        private static void DumpProps(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            sb.Append("{\"_type\":").Append(JsonStr(o.GetType().FullName)).Append(",\"properties\":[");
            bool first = true;
            foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string val, repr;
                try
                {
                    object v = p.CanRead ? p.GetValue(o, null) : null;
                    val = v != null ? v.ToString() : "null";
                    repr = "ok";
                }
                catch (Exception ex) { val = ex.Message; repr = "throw"; }
                if (!first) sb.Append(",");
                sb.Append("{\"name\":").Append(JsonStr(p.Name))
                  .Append(",\"type\":").Append(JsonStr(p.PropertyType.FullName))
                  .Append(",\"canWrite\":").Append(p.CanWrite ? "true" : "false")
                  .Append(",\"value\":").Append(JsonStr(val))
                  .Append(",\"repr\":").Append(JsonStr(repr))
                  .Append("}");
                first = false;
            }
            sb.Append("]}");
        }

        // Configure: write each key in the trigger's "params" map to whichever of
        // (tab, tab.TabStrategyProperties, strategyTemplate) has a matching writable
        // property. Type-aware: Instrument is created via Instrument.GetInstrument(name);
        // DateTime/TimeSpan/Enum parsed explicitly; ints/doubles via Convert.ChangeType.
        // Returns a per-key result list so the caller can verify what landed where.
        // (Intended replacement for the manual "open SA tab + click everything" step
        //  before each backtest sweep.)
        private string RunConfigure(string id, string triggerJson)
        {
            Window saWin = FindStrategyAnalyzerWindow();
            if (saWin == null) return BtErr(id, "no Strategy Analyzer window open");
            Dictionary<string, string> prms = ParseParams(triggerJson);
            try
            {
                return (string)saWin.Dispatcher.Invoke(new Func<string>(delegate
                {
                    try
                    {
                        StrategyAnalyzerViewModel vm = saWin.DataContext as StrategyAnalyzerViewModel;
                        StrategyAnalyzerTabControl tab = vm != null ? vm.SelectedTab : null;
                        if (tab == null) return BtErr(id, "no active SA tab");
                        object tsp = tab.TabStrategyProperties;
                        object strat = (tsp != null) ? tab.TabStrategyProperties.StrategyTemplate : null;
                        object[] targets = new object[] { tab, tsp, strat };
                        string[] targetNames = new string[] { "tab", "tabStrategyProperties", "strategyTemplate" };
                        var sb = new StringBuilder("{\"id\":").Append(JsonStr(id))
                            .Append(",\"status\":\"ok\",\"applied\":[");
                        bool first = true;
                        foreach (var kv in prms)
                        {
                            string outcome = TrySetOnTargets(targets, targetNames, kv.Key, kv.Value);
                            if (!first) sb.Append(",");
                            sb.Append(outcome);
                            first = false;
                        }
                        sb.Append("]}");
                        return sb.ToString();
                    }
                    catch (Exception ex) { return BtErr(id, "configure: " + ex.Message); }
                }));
            }
            catch (Exception ex) { return BtErr(id, "configure outer: " + ex.Message); }
        }

        // Walk targets in order; first one with a writable property of matching name wins.
        // Returns one JSON object per key: {"key":..,"target":..,"status":"set|skip|error",..}
        private static string TrySetOnTargets(object[] targets, string[] names, string key, string token)
        {
            for (int ti = 0; ti < targets.Length; ti++)
            {
                if (targets[ti] == null) continue;
                PropertyInfo p = targets[ti].GetType().GetProperty(key, BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) continue;
                try
                {
                    object v = ConvertConfigToken(token, p.PropertyType);
                    p.SetValue(targets[ti], v, null);
                    return "{\"key\":" + JsonStr(key) + ",\"target\":" + JsonStr(names[ti])
                         + ",\"status\":\"set\",\"to\":" + JsonStr(v != null ? v.ToString() : "null") + "}";
                }
                catch (Exception ex)
                {
                    return "{\"key\":" + JsonStr(key) + ",\"target\":" + JsonStr(names[ti])
                         + ",\"status\":\"error\",\"message\":" + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}";
                }
            }
            return "{\"key\":" + JsonStr(key) + ",\"status\":\"skip\",\"message\":\"no writable property of this name on tab/tabStrategyProperties/strategyTemplate\"}";
        }

        // Type-aware token -> value conversion. Extends ConvertToken with Instrument
        // (factory lookup by FullName), DateTime (ISO yyyy-MM-dd or yyyy-MM-ddTHH:mm:ss),
        // and falls back to ConvertToken for primitive/enum/string/TimeSpan.
        private static object ConvertConfigToken(string token, Type t)
        {
            string raw = token == null ? null : token.Trim();
            if (raw != null && raw.Length >= 2 && raw[0] == '"' && raw[raw.Length - 1] == '"')
                raw = raw.Substring(1, raw.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            Type nt = Nullable.GetUnderlyingType(t) ?? t;
            if (nt == typeof(Instrument)) return Instrument.GetInstrument(raw);
            if (nt == typeof(DateTime))   return DateTime.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
            if (nt == typeof(BarsPeriod))
            {
                // "Second:1" / "Minute:1" / "Tick:1" — type:value[:value2] form. The optional third
                // part sets Value2 for two-parameter bar types (e.g. a custom add-on type like
                // NinzaRenko: "12345:64:16" = type 12345, brick 64 / reversal 16). A numeric type
                // token parses straight through Enum.Parse to (BarsPeriodType)value, so custom add-on
                // bar types (which register an int BarsPeriodType, not a named enum member) work too.
                string[] parts = raw.Split(':');
                BarsPeriodType bpType = (BarsPeriodType)Enum.Parse(typeof(BarsPeriodType), parts[0], true);
                int val = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                BarsPeriod bp = new BarsPeriod { BarsPeriodType = bpType, Value = val };
                if (parts.Length > 2) bp.Value2 = int.Parse(parts[2]);
                return bp;
            }
            return ConvertToken(token, t);
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
            // TimeSpan is NOT IConvertible (Convert.ChangeType throws) -- common NT8 input type
            // for session Start/End times. Parse "HH:mm[:ss]" explicitly. (Session-based
            // strategies expose StartTime/EndTime as TimeSpan; without this they silently skip-inject.)
            if (nt == typeof(TimeSpan)) return TimeSpan.Parse(raw, System.Globalization.CultureInfo.InvariantCulture);
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
        // completed run, then write its SystemPerformance. Caps at ~45 min
        // (tick-resolution intrabar fills over long ranges take far longer than 5 min).
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
                    else if (ticks[0] >= 1350)
                    {
                        WriteResult("backtest_" + id + ".json",
                            BtErr(id, "no completed result within ~45 min (run may still be going, or produced no new Results entry)"));
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
                // Per-day absolute stop levels, recovered from the backtest's StopMarket
                // orders (e.g. EventFadeNFP's "EFStop"). The stop appears in the order
                // history whether it FILLED (stop exit) or was CANCELLED (time exit), so
                // this gives a stop LEVEL for time-exit days too -- the parity comparator
                // checks the stop as an order LEVEL, and on a time-exit day it is absent
                // from the exit execution. Built once per run; defensive (empty on any
                // API hiccup -> stops fall back to the exit order's StopPrice, else null).
                Dictionary<DateTime, double> stopByDate = BuildStopByDate(perf);

                int cap = nTrades < 5000 ? nTrades : 5000;
                for (int i = 0; i < cap; i++)
                {
                    Trade t = all[i];
                    if (i > 0) sb.Append(",");
                    string entry = (t.Entry != null) ? t.Entry.Time.ToString("o") : "";
                    string exit  = (t.Exit  != null) ? t.Exit.Time.ToString("o")  : "";
                    // Times are the NT chart wall-clock (ET per the runbook), tz-naive --
                    // matching the original entryTime; the comparator attaches ET.
                    sb.Append("{\"pnl\":").Append(Num(t.ProfitCurrency))
                      .Append(",\"marketPosition\":").Append(JsonStr(SafeStr(delegate { return t.Entry.MarketPosition.ToString(); })))
                      .Append(",\"entryTime\":").Append(JsonStr(entry))
                      .Append(",\"exitTime\":").Append(JsonStr(exit))
                      .Append(",\"entryPrice\":").Append(SafeNum(delegate { return t.Entry.Price; }))
                      .Append(",\"exitPrice\":").Append(SafeNum(delegate { return t.Exit.Price; }))
                      .Append(",\"exitName\":").Append(JsonStr(SafeStr(delegate { return t.Exit.Order.Name; })));
                    double stp = ResolveStop(t, stopByDate);
                    if (!double.IsNaN(stp))
                        sb.Append(",\"stopPrice\":").Append(Num(stp));
                    sb.Append("}");
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Build {date -> absolute stop price} from the backtest's StopMarket orders. The
        // strategy attaches an absolute StopMarket to each entry; it is in the order
        // history whether it FILLED (stop exit) or was CANCELLED (time exit), so this
        // recovers the stop LEVEL for every trading day, including time-exit days where the
        // stop is absent from the exit execution. One trade/day for the event strategies,
        // so date-keying is unambiguous. Defensive: any API/reflection hiccup degrades to an
        // empty map (callers then fall back to the exit order's StopPrice on stop days).
        private static Dictionary<DateTime, double> BuildStopByDate(SystemPerformance perf)
        {
            var map = new Dictionary<DateTime, double>();
            try
            {
                IEnumerator<Order> orders = perf.GetBacktestOrdersReverse();
                if (orders == null) return map;
                try
                {
                    while (orders.MoveNext())
                    {
                        Order o = orders.Current;
                        try
                        {
                            if (o == null || o.OrderType != OrderType.StopMarket) continue;
                            double sp = o.StopPrice;
                            if (double.IsNaN(sp) || sp <= 0) continue;
                            map[o.Time.Date] = sp;   // 1 stop order/day for the event strategies
                        }
                        catch { }
                    }
                }
                finally { try { orders.Dispose(); } catch { } }
            }
            catch { }
            return map;
        }

        // Resolve a trade's absolute stop level: prefer the per-day StopMarket map (covers
        // time-exit days); fall back to the exit order's StopPrice when the exit itself was
        // the stop; else NaN (the field is then omitted -> the comparator sees it absent).
        private static double ResolveStop(Trade t, Dictionary<DateTime, double> stopByDate)
        {
            try
            {
                if (t.Entry != null && stopByDate != null)
                {
                    double s;
                    if (stopByDate.TryGetValue(t.Entry.Time.Date, out s))
                        return s;
                }
                if (t.Exit != null && t.Exit.Order != null
                    && t.Exit.Order.OrderType == OrderType.StopMarket)
                {
                    double sp = t.Exit.Order.StopPrice;
                    if (!double.IsNaN(sp) && sp > 0)
                        return sp;
                }
            }
            catch { }
            return double.NaN;
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

        private string BuildResultJson(string id, object emit) { return BuildResultJson(id, emit, false); }

        private string BuildResultJson(string id, object emit, bool assemblyReloaded)
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
                   string.Join(",", errors.ToArray()) + "],\"assemblyReloaded\":" +
                   ((assemblyReloaded && status == "ok") ? "true" : "false") + "}";
        }

        // --- Account state: independent, out-of-band read of NT8's own Account
        // objects (open positions, working orders, realized/unrealized P&L, recent
        // completed trades). This is a SEPARATE channel from whatever automation feeds
        // your strategy: when an upstream position substream stalls (e.g. NT8 stops
        // pushing updates and a winning trade is booked as a $0 placeholder),
        // this still reads NT8's truth. Read-only — never submits/cancels/flattens.
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

        // --- Force-close: flatten an account's position(s) and cancel its working
        // orders. Automated strategies do not always reliably close a stranded/naked
        // position; this is the independent kill switch the `watch` watchdog calls when it
        // sees an unprotected position. Account name is REQUIRED (refuses to
        // flatten everything). Acts AFTER releasing the snapshot locks — Lesson
        // #159: never call Account.Cancel/Flatten while holding a collection lock.
        private string RunFlatten(string id, string accountFilter, string instrumentFilter)
        {
            try
            {
                if (string.IsNullOrEmpty(accountFilter))
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"flattened\":[],\"errors\":[{" +
                           "\"code\":\"BRIDGE\",\"message\":\"flatten requires an account name\"}]}";

                Account acct = null;
                try { lock (Account.All) { foreach (Account a in Account.All) { if (a.Name == accountFilter) { acct = a; break; } } } } catch { }
                if (acct == null)
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"flattened\":[],\"errors\":[{" +
                           "\"code\":\"BRIDGE\",\"message\":" + JsonStr("account not found: " + accountFilter) + "}]}";

                var instrs = new List<Instrument>();
                var flattened = new List<string>();
                try {
                    lock (acct.Positions) {
                        foreach (Position p in acct.Positions) {
                            string mp = SafeStr(delegate { return p.MarketPosition.ToString(); });
                            if (mp == "Flat" || mp == "") continue;
                            string fn = SafeStr(delegate { return p.Instrument.FullName; });
                            if (!string.IsNullOrEmpty(instrumentFilter) && fn != instrumentFilter) continue;
                            instrs.Add(p.Instrument);
                            flattened.Add(fn + " " + mp + " qty=" + SafeInt(delegate { return p.Quantity; }).ToString(InvCi));
                        }
                    }
                } catch { }

                var ordersToCancel = new List<Order>();
                try {
                    lock (acct.Orders) {
                        foreach (Order o in acct.Orders) {
                            if (!IsWorkingState(SafeStr(delegate { return o.OrderState.ToString(); }))) continue;
                            string fn = SafeStr(delegate { return o.Instrument.FullName; });
                            if (!string.IsNullOrEmpty(instrumentFilter) && fn != instrumentFilter) continue;
                            ordersToCancel.Add(o);
                        }
                    }
                } catch { }

                // act AFTER the locks are released (Lesson #159)
                int cancelled = 0;
                if (ordersToCancel.Count > 0) {
                    try { acct.Cancel(ordersToCancel); cancelled = ordersToCancel.Count; }
                    catch (Exception ex) { LogSafe("flatten cancel: " + ex.Message); }
                }
                bool flattenCalled = false;
                if (instrs.Count > 0) {
                    try { acct.Flatten(instrs); flattenCalled = true; }
                    catch (Exception ex) { LogSafe("flatten: " + ex.Message); }
                }
                LogSafe("FLATTEN account=" + accountFilter + " instrument='" + instrumentFilter +
                        "' positions=" + instrs.Count + " ordersCancelled=" + cancelled);

                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(JsonStr(id))
                  .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"account\":").Append(JsonStr(accountFilter))
                  .Append(",\"flattenCalled\":").Append(flattenCalled ? "true" : "false")
                  .Append(",\"ordersCancelled\":").Append(cancelled.ToString(InvCi))
                  .Append(",\"flattened\":[");
                for (int i = 0; i < flattened.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(flattened[i])); }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"flattened\":[],\"errors\":[{" +
                       "\"code\":\"BRIDGE\",\"message\":" + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        // --- Account trade-performance: round-trip trades + headline metrics over a date
        // range, sourced from NT8's own trade DB (Execution.DbGet) and paired by
        // SystemPerformance.Calculate -- the engine behind the native Trade Performance
        // window. Read-only. The DB query is start-padded so a round-trip opened just before
        // `from` still pairs; metrics are computed over the EXIT-filtered set so the pad
        // never pollutes them (see BuildPerfJson).
        private const int PerfStartPadDays = 2;

        private string RunPerformance(string id, string account, string fromStr, string toStr, string instrument)
        {
            try
            {
                if (string.IsNullOrEmpty(account))
                    return PerfErr(id, "performance requires an account name");

                Account acct = null;
                try { lock (Account.All) { foreach (Account a in Account.All) { if (a.Name == account) { acct = a; break; } } } } catch { }
                if (acct == null)
                    return PerfErr(id, "account not found: " + account);

                // ET date bounds. Default: today 00:00 -> now. An explicit `to` -> end-of-day.
                DateTime now = DateTime.Now;   // NT8 process clock is exchange/ET per the runbook
                DateTime from = ParsePerfDate(fromStr, new DateTime(now.Year, now.Month, now.Day, 0, 0, 0));
                DateTime to = ParsePerfDate(toStr, now);
                if (!string.IsNullOrEmpty(toStr)) to = to.Date.AddDays(1).AddSeconds(-1);
                DateTime queryFrom = from.AddDays(-PerfStartPadDays);

                var warnings = new List<string>();
                string source = "db";
                var execs = new List<Execution>();
                var seen = new HashSet<string>();

                // 1) DB executions over the padded window.
                //    Execution.DbGet rejects bounds whose Kind is Local: NT8's DB time zone is
                //    the exchange zone, not the machine's, so a Local DateTime trips an internal
                //    TimeZoneInfo conversion ("...the source time zone must be TimeZoneInfo.Local").
                //    When no --to is supplied, `to` defaults to DateTime.Now (Local) and DbGet
                //    throws -> we silently lost the DB and fell back to the 3-day memory window
                //    (every intraday/EOD pull omits --to). Pass Unspecified wall-clock bounds,
                //    exactly as NT8's own Trade Performance window does. `queryFrom` is already
                //    Unspecified (ParsePerfDate); normalize both for safety.
                DateTime dbFrom = DateTime.SpecifyKind(queryFrom, DateTimeKind.Unspecified);
                DateTime dbTo = DateTime.SpecifyKind(to, DateTimeKind.Unspecified);
                try
                {
                    var dbExecs = Execution.DbGet(acct, dbFrom, dbTo);
                    if (dbExecs != null)
                        foreach (Execution e in dbExecs)
                        {
                            if (!ExecInScope(e, queryFrom, to, instrument)) continue;
                            string k = SafeStr(delegate { return e.ExecutionId; });
                            execs.Add(e);
                            if (k.Length > 0) seen.Add(k);
                        }
                }
                catch (Exception ex)
                {
                    LogSafe("perf DbGet: " + ex.Message);
                    source = "memory";
                    warnings.Add("DB history unavailable; limited to ~3-day in-memory window");
                }

                // 2) Union the in-memory executions (freshest intraday fills), deduped by id.
                try
                {
                    lock (acct.Executions)
                    {
                        foreach (Execution e in acct.Executions)
                        {
                            if (!ExecInScope(e, queryFrom, to, instrument)) continue;
                            string k = SafeStr(delegate { return e.ExecutionId; });
                            if (k.Length > 0 && seen.Contains(k)) continue;
                            execs.Add(e);
                            if (k.Length > 0) seen.Add(k);
                        }
                    }
                }
                catch (Exception ex) { LogSafe("perf mem-union: " + ex.Message); }

                // 2b) Commission is a curve-ball for live/funded accounts: the raw executions
                //     from Execution.DbGet carry Commission==0 because NT8 NEVER persists the
                //     per-fill commission -- the native Trade Performance window recomputes it at
                //     display time by applying the account's assigned Commission TEMPLATE
                //     (acct.Commission, a Cbi.Commission whose public GetWithMinimum(instrument,
                //     quantity) yields the per-side charge). Pull the template + the server's
                //     running commission/fee totals so BuildPerfJson can reconstruct per-trade
                //     commission exactly as the window does (and cross-check against the server).
                Commission commTemplate = null;
                try { commTemplate = acct.Commission; } catch (Exception ex) { LogSafe("perf commTemplate: " + ex.Message); }
                double svrComm = double.NaN, svrFee = double.NaN;
                try { svrComm = acct.Get(AccountItem.Commission, acct.Denomination); } catch { }
                try { svrFee = acct.Get(AccountItem.Fee, acct.Denomination); } catch { }

                // 3) Pair round-trips + 4) emit (filtering trades to exit in [from,to]).
                SystemPerformance perf = SystemPerformance.Calculate(execs);
                return BuildPerfJson(id, perf, account, from, to, source, warnings, commTemplate, svrComm, svrFee);
            }
            catch (Exception ex)
            {
                return PerfErr(id, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // An execution is in scope if its time is within the (padded) window and it matches
        // the optional instrument filter. Defensive: a read failure excludes it.
        private static bool ExecInScope(Execution e, DateTime lo, DateTime hi, string instrument)
        {
            try
            {
                if (e == null) return false;
                DateTime t = e.Time;
                if (t < lo || t > hi) return false;
                if (!string.IsNullOrEmpty(instrument)
                    && SafeStr(delegate { return e.Instrument.FullName; }) != instrument) return false;
                return true;
            }
            catch { return false; }
        }

        private static DateTime ParsePerfDate(string s, DateTime fallback)
        {
            if (string.IsNullOrEmpty(s)) return fallback;
            DateTime d;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", InvCi,
                    System.Globalization.DateTimeStyles.None, out d)) return d;
            if (DateTime.TryParse(s, InvCi, System.Globalization.DateTimeStyles.None, out d)) return d;
            return fallback;
        }

        private static string PerfErr(string id, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"ts\":" +
                   JsonStr(DateTime.UtcNow.ToString("o")) +
                   ",\"metrics\":{\"totalTrades\":0},\"trades\":[],\"warnings\":[],\"errors\":[{" +
                   "\"code\":\"BRIDGE\",\"message\":" + JsonStr(message) + "}]}";
        }

        // Sibling of BuildBacktestJson (NOT a refactor): metrics are computed here from the
        // EXIT-filtered trade set (pad-safe), and per-trade quantity/commission are added.
        // The full scorecard (win rate, avg win/loss, equity, drawdown) is derived client-side
        // by nt8bridge.report.compute_stats from `trades`, so we emit only headline metrics.
        private string BuildPerfJson(string id, SystemPerformance perf, string account,
                                     DateTime from, DateTime to, string source, List<string> warnings,
                                     Commission commTemplate, double svrComm, double svrFee)
        {
            var trades = new List<Trade>();
            TradeCollection all = (perf != null) ? perf.AllTrades : null;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    Trade t = all[i];
                    if (t == null || t.Exit == null) continue;
                    DateTime xt;
                    try { xt = t.Exit.Time; } catch { continue; }
                    if (xt >= from && xt <= to) trades.Add(t);
                }
            }

            int n = trades.Count;
            // Per-trade commission, resolved once so the metric total and the per-trade emission
            // agree. Preference: (1) the execution's stored commission if the server already
            // stamped it (fresh live fills carry it); (2) else reconstruct from the account's
            // Commission TEMPLATE the same way the native window does -- entry-side + exit-side
            // GetWithMinimum(instrument, legQty). Zero-commission accounts with no template stay 0.
            double[] comm = new double[n];
            double[] grossArr = new double[n];
            int nStored = 0, nTemplate = 0, nNoComm = 0;
            double net = 0, gp = 0, gl = 0, cum = 0, peak = 0, maxDd = 0, totalComm = 0;
            for (int i = 0; i < n; i++)
            {
                Trade t = trades[i];
                double pnl = 0; try { pnl = t.ProfitCurrency; } catch { }
                net += pnl;
                if (pnl > 0) gp += pnl; else if (pnl < 0) gl += pnl;
                cum += pnl;
                if (cum > peak) peak = cum;
                double dd = cum - peak;
                if (dd < maxDd) maxDd = dd;

                double stored = 0; try { stored = t.Entry.Commission + t.Exit.Commission; } catch { }
                double c = stored;
                if (stored != 0) nStored++;
                else if (commTemplate != null)
                {
                    try
                    {
                        c = commTemplate.GetWithMinimum(t.Entry.Instrument, t.Entry.Quantity)
                          + commTemplate.GetWithMinimum(t.Exit.Instrument, t.Exit.Quantity);
                    }
                    catch { c = 0; }
                    if (c != 0) nTemplate++; else nNoComm++;
                }
                else nNoComm++;
                comm[i] = c;
                totalComm += c;

                // TRUE pre-commission gross per trade (v3.1.4, referee realignment):
                // Trade.ProfitCurrency is net of commission stamped ON the fills, so add back
                // exactly that — prorated per matched pair the same way the journal addon does
                // (legComm × pairQty ÷ legFillQty; SystemPerformance emits one Trade per matched
                // pair, and sibling pairs share legs). NOT comm[i]: that is stored-or-TEMPLATE
                // and unprorated, and template commission never touches ProfitCurrency. Also
                // distinct from metrics.grossProfit below, which is the sum of WINNERS.
                double rawProrated = 0;
                try
                {
                    double pairQty = t.Quantity;
                    if (pairQty > 0)
                    {
                        double eq = t.Entry.Quantity, xq = t.Exit.Quantity;
                        // Commission AND Fee (v3.1.5): ProfitCurrency is net of BOTH charges when
                        // a provider stamps them on fills (proven on a remote prop capture: gross
                        // short by exactly the fee total). Zero-charge fills are unchanged. Fee
                        // via reflection (donor pattern) — reads 0 where the build lacks it.
                        if (eq > 0) rawProrated += (t.Entry.Commission + ToD(GetPropAny(t.Entry, "Fee"))) * (pairQty / eq);
                        if (xq > 0) rawProrated += (t.Exit.Commission + ToD(GetPropAny(t.Exit, "Fee"))) * (pairQty / xq);
                    }
                }
                catch { }
                grossArr[i] = pnl + rawProrated;
            }
            double pf = (gl < 0) ? (gp / (-gl)) : 0.0;
            double netAfter = net - totalComm;
            string commSource = (nStored > 0 && nTemplate > 0) ? "mixed"
                              : (nStored > 0) ? "stored"
                              : (nTemplate > 0) ? "template" : "none";
            string templateName = SafeStr(delegate { return commTemplate != null ? commTemplate.Name : ""; });

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id)).Append(",\"status\":\"ok\",\"ts\":")
              .Append(JsonStr(DateTime.UtcNow.ToString("o")))
              .Append(",\"account\":").Append(JsonStr(account))
              .Append(",\"source\":").Append(JsonStr(source))
              .Append(",\"from\":").Append(JsonStr(from.ToString("o")))
              .Append(",\"to\":").Append(JsonStr(to.ToString("o")))
              .Append(",\"metrics\":{\"totalTrades\":").Append(n.ToString(InvCi))
              .Append(",\"netProfit\":").Append(Num(net))
              .Append(",\"grossProfit\":").Append(Num(gp))
              .Append(",\"grossLoss\":").Append(Num(gl))
              .Append(",\"profitFactor\":").Append(Num(pf))
              .Append(",\"maxDrawdown\":").Append(Num(maxDd))
              .Append(",\"commission\":").Append(Num(totalComm))
              .Append(",\"netAfterCommission\":").Append(Num(netAfter))
              .Append("},\"commissionInfo\":{\"template\":").Append(JsonStr(templateName))
              .Append(",\"source\":").Append(JsonStr(commSource))
              .Append(",\"total\":").Append(Num(totalComm))
              .Append(",\"tradesFromStored\":").Append(nStored.ToString(InvCi))
              .Append(",\"tradesFromTemplate\":").Append(nTemplate.ToString(InvCi))
              .Append(",\"tradesNoCommission\":").Append(nNoComm.ToString(InvCi))
              .Append(",\"serverCommissionTotal\":").Append(double.IsNaN(svrComm) ? "null" : Num(svrComm))
              .Append(",\"serverFeeTotal\":").Append(double.IsNaN(svrFee) ? "null" : Num(svrFee))
              .Append("},\"trades\":[");
            int cap = n < 5000 ? n : 5000;
            for (int i = 0; i < cap; i++)
            {
                Trade t = trades[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"pnl\":").Append(SafeNum(delegate { return t.ProfitCurrency; }))
                  // grossProfit = pnl + prorated raw fill commission (see grossArr above) — the
                  // uniform pre-commission basis the journal's grossPnl also carries (v3.1.4);
                  // reconcile_verified.py's gross gate sums THIS, never pnl, so stored-comm
                  // accounts (Sim fills stamped by an active template) reconcile exactly.
                  .Append(",\"grossProfit\":").Append(Num(grossArr[i]))
                  .Append(",\"marketPosition\":").Append(JsonStr(SafeStr(delegate { return t.Entry.MarketPosition.ToString(); })))
                  .Append(",\"entryTime\":").Append(JsonStr(SafeStr(delegate { return t.Entry.Time.ToString("o"); })))
                  .Append(",\"exitTime\":").Append(JsonStr(SafeStr(delegate { return t.Exit.Time.ToString("o"); })))
                  .Append(",\"entryPrice\":").Append(SafeNum(delegate { return t.Entry.Price; }))
                  .Append(",\"exitPrice\":").Append(SafeNum(delegate { return t.Exit.Price; }))
                  .Append(",\"exitName\":").Append(JsonStr(SafeStr(delegate { return t.Exit.Order.Name; })))
                  // quantity is the entry-leg value (exact for single-fill round-trips). commission
                  // is stored-or-template-reconstructed (see loop above): entry-side + exit-side.
                  .Append(",\"quantity\":").Append(SafeInt(delegate { return t.Entry.Quantity; }).ToString(InvCi))
                  .Append(",\"commission\":").Append(Num(comm[i]))
                  .Append("}");
            }
            sb.Append("],\"warnings\":[");
            for (int i = 0; i < warnings.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(warnings[i])); }
            sb.Append("],\"errors\":[]}");
            return sb.ToString();
        }

        // ===== Read the OPEN Trade Performance window (perfwindow) =====
        // The curve-ball: for live/funded (e.g. prop) accounts NT8 does NOT persist per-fill
        // commission -- Execution.DbGet returns 0 and no local Commission template exists, so the
        // `performance` verb above can only see gross P&L. But when NT8's native Trade Performance
        // window DISPLAYS the account it fetches the broker's cash history
        // (NinjaTrader.Cbi.TradingServices.CashHistoryLogItem: a CashChangeType + a Delta amount)
        // and holds it in the tab's TradePerformanceReportViewModel: public TotalFeesAll/Long/Short
        // plus FeesByExecution (execId -> cash-history items). This reads that live in-memory copy
        // -- exactly what the window shows -- with no server round-trip of our own. Requires the
        // window to be open on the account so its fees are calculated; read-only, on the window's
        // OWN dispatcher thread (NT8 windows are per-thread -- see the chartseries lesson).
        private string RunPerfWindow(string id, string account, bool generate, string fromStr, string toStr)
        {
            try
            {
                var wins = FindTradePerformanceWindows();
                bool autoOpened = false;
                if (wins.Count == 0)
                {
                    // A read needs an existing window. --generate is meant to be hands-off, so when none
                    // is open we SPAWN one (NT8-native: construct the NTWindow on a pooled UI-thread
                    // dispatcher + Show, wait for it to register in Globals.AllWindows), then drive it.
                    // NOTE: the target account must be CONNECTED -- a fresh window's Generate pulls from
                    // connected accounts and builds its account-filter list from those executions, so a
                    // disconnected account never appears.
                    if (!generate)
                        return PerfWinErr(id, "no Trade Performance window open -- open a Trade Performance tab, "
                            + "select account '" + account + "' + the date range, and let it display before running this "
                            + "(or pass --generate to open + build it automatically)");
                    string openErr = OpenTradePerformanceWindow(10000);
                    if (openErr != null) return PerfWinErr(id, "auto-open failed: " + openErr);
                    autoOpened = true;
                    System.Threading.Thread.Sleep(1500);   // let the fresh window start populating
                    wins = FindTradePerformanceWindows();
                    if (wins.Count == 0) return PerfWinErr(id, "auto-opened a Trade Performance window but it did not register");
                }

                // Optionally DRIVE the window: set the account filter + date range and fire the
                // report's own GenerateReport() (the Generate button's async method), then wait for
                // it to finish -- so the pull is hands-off, no manual Generate click. A FRESH window
                // (auto-opened) can need a couple of generates before the connected account's data +
                // filter list are fully populated, so we loop generate+read until the account's data
                // appears (or a bounded timeout).
                List<string> frags;
                if (generate)
                {
                    frags = new List<string>();
                    int genWait = 0;
                    while (true)
                    {
                        string genErr = DrivePerfGenerate(wins, account, fromStr, toStr, 180000);
                        if (genErr != null && !autoOpened && genWait == 0)
                            return PerfWinErr(id, genErr);   // an already-open window failing to generate is a hard error
                        frags = CollectPerfReports(wins, account);
                        if (frags.Count > 0) break;
                        if (genWait >= 30000) break;         // give up after ~30s of empty re-generates
                        System.Threading.Thread.Sleep(2000); genWait += 2000;
                        wins = FindTradePerformanceWindows();
                    }
                }
                else
                {
                    frags = CollectPerfReports(wins, account);
                }
                if (frags.Count == 0)
                    return PerfWinErr(id, string.IsNullOrEmpty(account)
                        ? "Trade Performance window(s) open but no report tab could be read"
                        : "Trade Performance window(s) open but none is showing account '" + account + "' (check the account selector in the window)");

                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(JsonStr(id)).Append(",\"status\":\"ok\",\"ts\":")
                  .Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"autoOpened\":").Append(autoOpened ? "true" : "false")
                  .Append(",\"reportCount\":").Append(frags.Count.ToString(InvCi))
                  .Append(",\"reports\":[");
                for (int i = 0; i < frags.Count; i++) { if (i > 0) sb.Append(","); sb.Append(frags[i]); }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return PerfWinErr(id, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Standalone Trade Performance windows (NOT the Strategy Analyzer, whose embedded report
        // runs in IsStrategyAnalyzerMode).
        private List<Window> FindTradePerformanceWindows()
        {
            var wins = new List<Window>();
            var all = Globals.AllWindows;
            var snap = new List<Window>();
            try { if (all != null) for (int i = 0; i < all.Count; i++) snap.Add(all[i]); } catch { }
            foreach (Window w in snap)
            {
                if (w == null) continue;
                string fn = null; try { fn = w.GetType().FullName; } catch { }
                if (fn != null && fn.EndsWith(".TradePerformance.TradePerformance", StringComparison.Ordinal))
                    wins.Add(w);
            }
            return wins;
        }

        // Spawn a Trade Performance window the NT8-native way: construct the NTWindow on one of NT8's
        // pooled UI-thread dispatchers (Globals.RandomDispatcher -- a real STA UI thread with a running
        // Dispatcher, where NT8's own windows live) and Show() it, then wait for it to register in
        // Globals.AllWindows. Returns null on success, else an error string.
        private string OpenTradePerformanceWindow(int timeoutMs)
        {
            try
            {
                Type t = Type.GetType("NinjaTrader.Gui.TradePerformance.TradePerformance, NinjaTrader.Gui");
                if (t == null) return "TradePerformance window type not found";
                System.Windows.Threading.Dispatcher disp = Globals.RandomDispatcher ?? Globals.MainThreadDispatcher;
                if (disp == null) return "no NT8 UI dispatcher available";
                disp.Invoke(new Action(delegate
                {
                    try
                    {
                        object w = Activator.CreateInstance(t);
                        MethodInfo show = t.GetMethod("Show", Type.EmptyTypes);
                        if (show != null) show.Invoke(w, null);
                    }
                    catch (Exception ex) { LogSafe("perfwindow open-inner: " + ex.Message); }
                }));
                int waited = 0;
                while (FindTradePerformanceWindows().Count == 0 && waited < timeoutMs) { System.Threading.Thread.Sleep(100); waited += 100; }
                return FindTradePerformanceWindows().Count > 0 ? null : "opened window did not appear in AllWindows within " + (timeoutMs / 1000) + "s";
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
        }

        // Drive the window headless: on the first report tab that carries the target account, set the
        // account filter (only it selected) + date range, fire the VM's public GenerateReport() (the
        // async method behind the Generate button -- SAFE to invoke on the window's own thread, it
        // awaits internally), then wait off-thread for its Task. Returns null on success, else an
        // error string. The per-account breakdown isolates the account afterward regardless of how
        // NT8 ends up applying the filter.
        private string DrivePerfGenerate(List<Window> wins, string account, string fromStr, string toStr, int timeoutMs)
        {
            // Configure + fire on the first report tab, then wait for the Task. (The caller loops
            // generate+read to give a fresh window time to populate connected-account data.)
            object task = null;
            foreach (Window w in wins)
            {
                try
                {
                    object t = w.Dispatcher.Invoke(new Func<object>(delegate { return ConfigureAndGenerate(w, account, fromStr, toStr); }));
                    if (t != null) { task = t; break; }
                }
                catch (Exception ex) { LogSafe("perfwindow gen disp: " + ex.Message); }
            }
            if (task == null)
                return "could not start Generate (no report tab found)";

            var tsk = task as System.Threading.Tasks.Task;
            if (tsk != null)
            {
                int waited = 0;
                while (!tsk.IsCompleted && waited < timeoutMs) { System.Threading.Thread.Sleep(100); waited += 100; }
                if (tsk.IsFaulted)
                    return "Generate faulted: " + (tsk.Exception != null ? tsk.Exception.GetBaseException().Message : "unknown");
                if (!tsk.IsCompleted)
                    return "Generate timed out after " + (timeoutMs / 1000) + "s (server fetch may be slow / lookback capped)";
                // let the async fee calculation settle before we read (best effort, bounded).
                System.Threading.Thread.Sleep(500);
            }
            return null;
        }

        // Runs on the window's dispatcher. Finds the first report tab (matching `account` if given),
        // sets its account selection + dates, and returns the Task from GenerateReport() (or null if
        // no suitable tab). Never blocks on the async work -- returns the Task to the caller to await.
        private object ConfigureAndGenerate(Window w, string account, string fromStr, string toStr)
        {
            try
            {
                object tabControl = GetFieldAny(w, "tabControl");
                var items = GetPropAny(tabControl, "Items") as System.Collections.IEnumerable;
                if (items == null) return null;
                foreach (object item in items)
                {
                    object report = ResolvePerfReport(item);
                    if (report == null) continue;
                    object vm = GetFieldAny(report, "report");
                    if (vm == null) continue;

                    // A freshly auto-opened window loads its account list lazily; force it so the
                    // target account can appear + be selected (idempotent, best-effort).
                    try
                    {
                        MethodInfo la = report.GetType().GetMethod("LoadAccounts", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (la != null && la.GetParameters().Length == 0) la.Invoke(report, null);
                    }
                    catch (Exception ex) { LogSafe("perfwindow loadaccts: " + ex.Message); }

                    // Account filter: best-effort select ONLY the target (others off). Do NOT gate on
                    // the account being present -- a fresh window's account list is empty until its
                    // first Generate builds it from the pulled executions, so gating would deadlock.
                    // We generate regardless (the default generate covers connected accounts); the
                    // per-account breakdown isolates the target from whatever the report pulls.
                    if (!string.IsNullOrEmpty(account))
                    {
                        var availEnum = GetPropAny(vm, "AvailableAccounts") as System.Collections.IEnumerable;
                        if (availEnum != null)
                            foreach (object filt in availEnum)
                            {
                                string fn = SafeStr(delegate { return (string)GetPropAny(filt, "Filter"); });
                                if (string.IsNullOrEmpty(fn)) { object inst = GetPropAny(filt, "Instance"); fn = SafeStr(delegate { return (string)GetPropAny(inst, "Name"); }); }
                                SetPropAny(filt, "IsSelected", fn == account);
                            }
                    }

                    // Date range (optional): StartDate/EndDate carry the date, StartTime/EndTime the time.
                    if (!string.IsNullOrEmpty(fromStr))
                    {
                        DateTime f = ParsePerfDate(fromStr, DateTime.MinValue);
                        if (f != DateTime.MinValue) { SetPropAny(vm, "StartDate", f.Date); SetPropAny(vm, "StartTime", f.Date); }
                    }
                    if (!string.IsNullOrEmpty(toStr))
                    {
                        DateTime tt = ParsePerfDate(toStr, DateTime.MinValue);
                        if (tt != DateTime.MinValue) { SetPropAny(vm, "EndDate", tt.Date); SetPropAny(vm, "EndTime", tt.Date.AddDays(1).AddMinutes(-1)); }
                    }

                    // Fire the no-arg GenerateReport() overload (there are also (entry) and (strategy,mode)).
                    MethodInfo gen = null;
                    for (Type ty = vm.GetType(); ty != null && gen == null; ty = ty.BaseType)
                        gen = ty.GetMethod("GenerateReport", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (gen == null) return null;
                    object task = gen.Invoke(vm, null);
                    return task ?? (object)"started";
                }
            }
            catch (Exception ex) { LogSafe("perfwindow configure: " + ex.Message); }
            return null;
        }

        // Read every window's report tabs (each on its own dispatcher), collecting the JSON fragments.
        private List<string> CollectPerfReports(List<Window> wins, string account)
        {
            var frags = new List<string>();
            foreach (Window w in wins)
            {
                try
                {
                    var got = (List<string>)w.Dispatcher.Invoke(new Func<List<string>>(delegate { return ReadPerfReports(w, account); }));
                    if (got != null) frags.AddRange(got);
                }
                catch (Exception ex) { LogSafe("perfwindow disp: " + ex.Message); }
            }
            return frags;
        }

        // Walk one Trade Performance window's tab control, reading every report tab that matches the
        // account filter (empty = all). Runs on the window's own dispatcher thread.
        private List<string> ReadPerfReports(Window w, string accountFilter)
        {
            var outp = new List<string>();
            try
            {
                object tabControl = GetFieldAny(w, "tabControl");
                var items = GetPropAny(tabControl, "Items") as System.Collections.IEnumerable;
                if (items == null) return outp;
                foreach (object item in items)
                {
                    object report = ResolvePerfReport(item);
                    if (report == null) continue;
                    string frag = ReadOnePerfReport(report, accountFilter);
                    if (frag != null) outp.Add(frag);
                }
            }
            catch (Exception ex) { LogSafe("perfwindow tabs: " + ex.Message); }
            return outp;
        }

        // A tab item is (or wraps) a NinjaTrader.Gui.TradePerformance.TradePerformanceReport.
        private static object ResolvePerfReport(object item)
        {
            if (item == null) return null;
            string tn = null; try { tn = item.GetType().FullName; } catch { }
            if (tn != null && tn.EndsWith(".TradePerformanceReport", StringComparison.Ordinal)) return item;
            object content = GetPropAny(item, "Content");
            if (content != null)
            {
                string cn = null; try { cn = content.GetType().FullName; } catch { }
                if (cn != null && cn.EndsWith(".TradePerformanceReport", StringComparison.Ordinal)) return content;
            }
            return null;
        }

        // Pull commission/fees out of one report tab's view model. Returns null if the account
        // filter does not match this tab.
        private string ReadOnePerfReport(object report, string accountFilter)
        {
            try
            {
                object vm = GetFieldAny(report, "report");   // TradePerformanceReportViewModel
                if (vm == null) return null;

                // The report supports MULTI-account selection, so report.account is usually null;
                // the real filter is the VM's AvailableAccounts (each a TradePerformanceFilter with
                // Instance=Account, IsSelected, Filter=display name). Collect the selected ones and
                // match --name against them (or against the single report.account when present).
                object acct = GetFieldAny(report, "account");
                string acctName = acct != null ? SafeStr(delegate { return (string)GetPropAny(acct, "Name"); }) : "";
                var selAccts = new List<string>();
                object avail = GetPropAny(vm, "AvailableAccounts");
                var availEnum = avail as System.Collections.IEnumerable;
                if (availEnum != null)
                    foreach (object filt in availEnum)
                    {
                        bool sel = false; try { object b = GetPropAny(filt, "IsSelected"); sel = b is bool && (bool)b; } catch { }
                        if (!sel) continue;
                        string fn = SafeStr(delegate { return (string)GetPropAny(filt, "Filter"); });
                        if (string.IsNullOrEmpty(fn))
                        {
                            object inst = GetPropAny(filt, "Instance");
                            fn = SafeStr(delegate { return (string)GetPropAny(inst, "Name"); });
                        }
                        if (!string.IsNullOrEmpty(fn)) selAccts.Add(fn);
                    }
                // NOTE: we do NOT reject the tab here on the selected-accounts list. A requested
                // account may hold data in a tab without being "selected" (esp. a freshly auto-opened
                // window). We compute the per-account accumulators below and, for an account request,
                // skip the tab only if it truly has no data for that account (see the override block).

                bool feesCalc = false; try { object b = GetPropAny(vm, "areFeesCalculated") ?? GetFieldAny(vm, "areFeesCalculated"); feesCalc = b is bool && (bool)b; } catch { }
                object totalAllObj = GetPropAny(vm, "TotalFeesAll");
                double totalAll = ToD(totalAllObj);
                // Partial-rename guard: areFeesCalculated said fees ARE calculated, but TotalFeesAll did
                // not resolve (member renamed on this NT8 build). Do NOT present the resulting 0 as a real
                // total -- flip the trust flag off and attach a note so `feesCalculated` stays reliable.
                string feeReadNote = null;
                if (feesCalc && totalAllObj == null) { feesCalc = false; feeReadNote = "TotalFeesAll unreadable (NT8 build mismatch?)"; }
                double totalLong = ToD(GetPropAny(vm, "TotalFeesLong"));
                double totalShort = ToD(GetPropAny(vm, "TotalFeesShort"));
                string from = FmtRange(GetFieldAny(vm, "sDate"), GetFieldAny(vm, "sTime"));
                string to = FmtRange(GetFieldAny(vm, "eDate"), GetFieldAny(vm, "eTime"));

                // Per-category breakdown from FeesByExecution: group the cash-history items by their
                // CashChangeType (e.g. "Commission", "Exchange Fee", ...) and sum the Delta.
                var byType = new Dictionary<string, double>();
                var byCnt = new Dictionary<string, int>();
                int execsWithFees = 0, itemCount = 0;
                double sumDelta = 0;
                object fbx = GetPropAny(vm, "FeesByExecution");
                var dict = fbx as System.Collections.IDictionary;
                if (dict != null)
                {
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        var list = de.Value as System.Collections.IEnumerable;
                        if (list == null) continue;
                        bool any = false;
                        foreach (object it in list)
                        {
                            any = true; itemCount++;
                            string ct = SafeStr(delegate { return (string)GetPropAny(it, "CashChangeType"); });
                            if (string.IsNullOrEmpty(ct)) ct = "(unknown)";
                            double delta = ToD(GetPropAny(it, "Delta"));
                            if (!byType.ContainsKey(ct)) { byType[ct] = 0; byCnt[ct] = 0; }
                            byType[ct] += delta; byCnt[ct] += 1; sumDelta += delta;
                        }
                        if (any) execsWithFees++;
                    }
                }

                // PnL off the window's own SystemPerformance.AllTrades: overall net/gross plus a
                // per-account split (Trade.Entry.Account.Name) so a single prop account is isolated.
                int nTrades = -1;
                double net = 0, gp = 0, gl = 0;
                var acctNet = new Dictionary<string, double>();
                var acctGp = new Dictionary<string, double>();
                var acctGl = new Dictionary<string, double>();
                var acctTr = new Dictionary<string, int>();
                try
                {
                    object perf = GetFieldAny(vm, "performance");
                    object at = GetPropAny(perf, "AllTrades");
                    var te = at as System.Collections.IEnumerable;
                    if (te != null)
                    {
                        nTrades = 0;
                        foreach (object t in te)
                        {
                            nTrades++;
                            double pnl = ToD(GetPropAny(t, "ProfitCurrency"));
                            net += pnl; if (pnl > 0) gp += pnl; else if (pnl < 0) gl += pnl;
                            string an = SafeStr(delegate { object en = GetPropAny(t, "Entry"); object a = GetPropAny(en, "Account"); return (string)GetPropAny(a, "Name"); });
                            if (string.IsNullOrEmpty(an)) an = "(unknown)";
                            if (!acctNet.ContainsKey(an)) { acctNet[an] = 0; acctGp[an] = 0; acctGl[an] = 0; acctTr[an] = 0; }
                            acctNet[an] += pnl; acctTr[an] += 1;
                            if (pnl > 0) acctGp[an] += pnl; else if (pnl < 0) acctGl[an] += pnl;
                        }
                    }
                }
                catch (Exception ex) { LogSafe("perfwindow trades: " + ex.Message); }

                // Displayed executions carry commission/fee on Execution.Commission/.Fee. Sum overall +
                // per account, and map executionId -> account so the server cash-history fees
                // (FeesByExecution, the "Total Fees" path) can be attributed per account too.
                int execCount = 0; double execComm = 0, execFee = 0;
                var acctCnt = new Dictionary<string, int>();
                var acctComm = new Dictionary<string, double>();
                var acctFee = new Dictionary<string, double>();
                var execAcct = new Dictionary<long, string>();
                object execs = GetFieldAny(vm, "executions");
                var execEnum = execs as System.Collections.IEnumerable;
                if (execEnum != null)
                    foreach (object e in execEnum)
                    {
                        execCount++;
                        double ec = ToD(GetPropAny(e, "Commission"));
                        double ef = ToD(GetPropAny(e, "Fee"));
                        execComm += ec; execFee += ef;
                        string an = SafeStr(delegate { object a = GetPropAny(e, "Account"); return (string)GetPropAny(a, "Name"); });
                        if (string.IsNullOrEmpty(an)) an = "(unknown)";
                        if (!acctCnt.ContainsKey(an)) { acctCnt[an] = 0; acctComm[an] = 0; acctFee[an] = 0; }
                        acctCnt[an] += 1; acctComm[an] += ec; acctFee[an] += ef;
                        try { object ido = GetPropAny(e, "Id"); if (ido != null) execAcct[Convert.ToInt64(ido)] = an; } catch { }
                    }

                // Server cash-history fees (the "Total Fees" line) attributed per account via executionId.
                var acctCash = new Dictionary<string, double>();
                if (dict != null)
                    foreach (System.Collections.DictionaryEntry de in dict)
                    {
                        long eid; try { eid = Convert.ToInt64(de.Key); } catch { continue; }
                        string an = execAcct.ContainsKey(eid) ? execAcct[eid] : "(unknown)";
                        var list = de.Value as System.Collections.IEnumerable;
                        if (list == null) continue;
                        foreach (object it in list)
                        {
                            if (!acctCash.ContainsKey(an)) acctCash[an] = 0;
                            acctCash[an] += ToD(GetPropAny(it, "Delta"));
                        }
                    }

                // The window won't actually RESTRICT the report to the ticked account (NT8 leaves the
                // account filter effectively off), so the raw totals are the all-accounts aggregate.
                // When a single account is requested, override the headline with THAT account's own
                // slice so `perfwindow --name X` reports X's numbers; accountBreakdown still lists all.
                string scope = "allDisplayed";
                if (!string.IsNullOrEmpty(accountFilter))
                {
                    // this tab holds no data for the requested account -> not the right tab.
                    if (!acctNet.ContainsKey(accountFilter) && !acctCnt.ContainsKey(accountFilter)) return null;
                    scope = "account";
                    net = acctNet.ContainsKey(accountFilter) ? acctNet[accountFilter] : 0;
                    gp = acctGp.ContainsKey(accountFilter) ? acctGp[accountFilter] : 0;
                    gl = acctGl.ContainsKey(accountFilter) ? acctGl[accountFilter] : 0;
                    nTrades = acctTr.ContainsKey(accountFilter) ? acctTr[accountFilter] : 0;
                    execCount = acctCnt.ContainsKey(accountFilter) ? acctCnt[accountFilter] : 0;
                    execComm = acctComm.ContainsKey(accountFilter) ? acctComm[accountFilter] : 0;
                    execFee = acctFee.ContainsKey(accountFilter) ? acctFee[accountFilter] : 0;
                }

                // Report only what NT8/the broker actually SHOW: netProfit (NT8 "Total net profit" =
                // the Tradovate Current-Balance change -- PRE-cost) + the separately-listed commission
                // and fees as their own lines. No derived net-of-cost figure (the broker balance does
                // not subtract these; no NT8 screen shows the subtraction). Commission rides on
                // Execution.Commission; fees via server cash-history (acctCash) or Execution.Fee.
                double headFee = string.IsNullOrEmpty(accountFilter)
                    ? (totalAll != 0 ? totalAll : execFee)
                    : (acctCash.ContainsKey(accountFilter) && acctCash[accountFilter] != 0 ? acctCash[accountFilter] : execFee);
                double feesCost = Math.Abs(headFee);
                double commCost = Math.Abs(execComm);

                var sb = new StringBuilder();
                sb.Append("{\"account\":").Append(JsonStr(acctName))
                  .Append(",\"selectedAccounts\":[");
                for (int i = 0; i < selAccts.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(selAccts[i])); }
                sb.Append("]")
                  .Append(",\"scope\":").Append(JsonStr(scope))
                  .Append(",\"from\":").Append(JsonStr(from))
                  .Append(",\"to\":").Append(JsonStr(to))
                  .Append(",\"feesCalculated\":").Append(feesCalc ? "true" : "false")
                  .Append(",\"feeReadNote\":").Append(feeReadNote == null ? "null" : JsonStr(feeReadNote))
                  .Append(",\"totalFees\":").Append(Num(totalAll))
                  .Append(",\"totalFeesLong\":").Append(Num(totalLong))
                  .Append(",\"totalFeesShort\":").Append(Num(totalShort))
                  .Append(",\"feeItemSum\":").Append(Num(sumDelta))
                  .Append(",\"executionsWithFees\":").Append(execsWithFees.ToString(InvCi))
                  .Append(",\"feeItemCount\":").Append(itemCount.ToString(InvCi))
                  .Append(",\"executionCount\":").Append(execCount.ToString(InvCi))
                  .Append(",\"execCommissionSum\":").Append(Num(execComm))
                  .Append(",\"execFeeSum\":").Append(Num(execFee))
                  .Append(",\"trades\":").Append(nTrades < 0 ? "null" : nTrades.ToString(InvCi))
                  .Append(",\"netProfit\":").Append(Num(net))
                  .Append(",\"grossProfit\":").Append(Num(gp))
                  .Append(",\"grossLoss\":").Append(Num(gl))
                  .Append(",\"commission\":").Append(Num(commCost))
                  .Append(",\"fees\":").Append(Num(feesCost))
                  .Append(",\"feeCategories\":[");
                bool first = true;
                foreach (var kv in byType)
                {
                    if (!first) sb.Append(","); first = false;
                    sb.Append("{\"type\":").Append(JsonStr(kv.Key))
                      .Append(",\"total\":").Append(Num(kv.Value))
                      .Append(",\"count\":").Append(byCnt[kv.Key].ToString(InvCi))
                      .Append("}");
                }
                sb.Append("],\"accountBreakdown\":[");
                // Union of accounts seen in executions and in trades (usually identical).
                var names = new List<string>();
                foreach (var k in acctCnt.Keys) if (!names.Contains(k)) names.Add(k);
                foreach (var k in acctNet.Keys) if (!names.Contains(k)) names.Add(k);
                first = true;
                foreach (string name in names)
                {
                    if (!first) sb.Append(","); first = false;
                    int ex = acctCnt.ContainsKey(name) ? acctCnt[name] : 0;
                    double cm = acctComm.ContainsKey(name) ? acctComm[name] : 0;
                    // prefer the server cash-history fee (the "Total Fees" line) attributed to this
                    // account; fall back to the execution-level Fee.
                    double fe = (acctCash.ContainsKey(name) && acctCash[name] != 0) ? acctCash[name]
                              : (acctFee.ContainsKey(name) ? acctFee[name] : 0);
                    double pn = acctNet.ContainsKey(name) ? acctNet[name] : 0;
                    int tr = acctTr.ContainsKey(name) ? acctTr[name] : 0;
                    sb.Append("{\"account\":").Append(JsonStr(name))
                      .Append(",\"trades\":").Append(tr.ToString(InvCi))
                      .Append(",\"executions\":").Append(ex.ToString(InvCi))
                      .Append(",\"netProfit\":").Append(Num(pn))
                      .Append(",\"commission\":").Append(Num(Math.Abs(cm)))
                      .Append(",\"fee\":").Append(Num(Math.Abs(fe)))
                      .Append("}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex) { LogSafe("perfwindow report: " + ex.Message); return null; }
        }

        private static double ToD(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToDouble(o, InvCi); } catch { return 0; }
        }

        // Combine a date-part + time-part DateTime (the VM stores them split) into an ISO string.
        private static string FmtRange(object datePart, object timePart)
        {
            try
            {
                if (!(datePart is DateTime)) return "";
                DateTime d = (DateTime)datePart;
                if (timePart is DateTime) d = d.Date + ((DateTime)timePart).TimeOfDay;
                return d.ToString("o");
            }
            catch { return ""; }
        }

        // Reflection helpers that walk the base-type chain and read public+non-public members
        // (the VM/report expose some data as private fields, some as public props).
        private static object GetFieldAny(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type ty = o.GetType(); ty != null; ty = ty.BaseType)
                {
                    FieldInfo f = ty.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (f != null) return f.GetValue(o);
                }
            }
            catch { }
            return null;
        }

        private static object GetPropAny(object o, string name)
        {
            if (o == null) return null;
            try
            {
                for (Type ty = o.GetType(); ty != null; ty = ty.BaseType)
                {
                    PropertyInfo p = ty.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanRead) return p.GetValue(o, null);
                }
            }
            catch { }
            return null;
        }

        private static void SetPropAny(object o, string name, object val)
        {
            if (o == null) return;
            try
            {
                for (Type ty = o.GetType(); ty != null; ty = ty.BaseType)
                {
                    PropertyInfo p = ty.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanWrite) { p.SetValue(o, val, null); return; }
                }
            }
            catch { }
        }

        private string PerfWinErr(string id, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"ts\":" +
                   JsonStr(DateTime.UtcNow.ToString("o")) +
                   ",\"reportCount\":0,\"reports\":[],\"errors\":[{" +
                   "\"code\":\"BRIDGE\",\"message\":" + JsonStr(message) + "}]}";
        }

        // ConnectionStatusUpdate handler (named so Terminated can unsubscribe — no leak).
        // Records, per connection name, WHY it last left Connected: "inadvertent"
        // (ConnectionLost or an error-disconnect — eligible for auto-reconnect), "user"
        // (clean Disconnect or UserAbort — parked, NEVER auto-reconnected), or "connected"
        // (recovered). Fires on arbitrary NT8 threads -> only touches the
        // ConcurrentDictionary + logs; never blocks.
        private void OnConnStatus(object sender, ConnectionStatusEventArgs e)
        {
            try
            {
                if (e == null || e.Connection == null || e.Connection.Options == null) return;
                string name = e.Connection.Options.Name;
                if (string.IsNullOrEmpty(name)) return;
                ConnectionStatus s = e.Status;
                if (s == ConnectionStatus.Connected)
                    _dropClass[name] = "connected";
                else if (s == ConnectionStatus.ConnectionLost)
                    _dropClass[name] = "inadvertent";
                else if (s == ConnectionStatus.Disconnected)
                    _dropClass[name] = (e.Error == ErrorCode.NoError || e.Error == ErrorCode.UserAbort) ? "user" : "inadvertent";
                // Connecting / Disconnecting: transient — leave the prior classification.
                LogSafe("conn status: " + name + " -> " + s + " (err=" + e.Error + ")");
            }
            catch (Exception ex) { LogSafe("OnConnStatus: " + ex.Message); }
        }

        // Read every configured connection's live status + whether it dropped
        // INADVERTENTLY (drop class "inadvertent" and not currently connected). The Python
        // guardian keys on inadvertentlyDropped; a connection the user parked reads false.
        private string RunConnections(string id)
        {
            try
            {
                var live = new List<Connection>();
                try { lock (Connection.Connections) { foreach (Connection c in Connection.Connections) live.Add(c); } } catch { }
                var cfg = new List<ConnectOptions>();
                try { lock (Globals.ConnectOptions) { foreach (ConnectOptions o in Globals.ConnectOptions) cfg.Add(o); } } catch { }

                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(JsonStr(id))
                  .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"connections\":[");
                bool first = true;
                foreach (ConnectOptions o in cfg)
                {
                    string nm = SafeStr(delegate { return o.Name; });
                    string liveStatus = LiveStatusOf(live, nm);
                    bool connected = liveStatus == "Connected";
                    string cls; if (!_dropClass.TryGetValue(nm, out cls)) cls = "";
                    bool inadvertent = cls == "inadvertent" && !connected;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"name\":").Append(JsonStr(nm))
                      .Append(",\"status\":").Append(JsonStr(liveStatus))
                      .Append(",\"connected\":").Append(connected ? "true" : "false")
                      .Append(",\"inadvertentlyDropped\":").Append(inadvertent ? "true" : "false")
                      .Append("}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"connections\":[],\"errors\":[{" +
                       "\"code\":\"BRIDGE\",\"message\":" + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        // --- Feed health: per-instrument last-tick AGE, so a FROZEN-but-connected feed
        // (NT8 reports the connection up while no ticks flow) is detectable. connwatch only
        // heals NT8-flagged DROPS; a dark feed never flags a drop (a chart can freeze
        // mid-session while the live market keeps moving, and the connections read still
        // calls it connected the whole time). Age is computed HERE from a
        // single clock (Now - MarketData.Last.Time) so there is no client/server skew or tz
        // drift. Read-only; every access guarded so a bad instrument degrades to null fields.
        private string RunFeedHealth(string id, string instrumentsCsv)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(JsonStr(id))
                  .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"feeds\":[");
                string[] names = (instrumentsCsv ?? "").Split(',');
                bool first = true;
                foreach (string rawName in names)
                {
                    string name = rawName == null ? "" : rawName.Trim();
                    if (name.Length == 0) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"instrument\":").Append(JsonStr(name));
                    try
                    {
                        Instrument instr = Instrument.GetInstrument(name);
                        bool haveLast = instr != null && instr.MarketData != null && instr.MarketData.Last != null;
                        if (haveLast)
                        {
                            var last = instr.MarketData.Last;
                            DateTime t = last.Time;
                            double ageMs = (DateTime.Now - t).TotalMilliseconds;
                            if (ageMs < 0) ageMs = 0;   // a future-stamped tick reads as fresh, not negative
                            sb.Append(",\"lastPrice\":").Append(Num(last.Price))
                              .Append(",\"lastTickTime\":").Append(JsonStr(t.ToUniversalTime().ToString("o")))
                              .Append(",\"ageMs\":").Append(((long)ageMs).ToString(InvCi));
                        }
                        else
                        {
                            sb.Append(",\"lastPrice\":null,\"lastTickTime\":\"\",\"ageMs\":null");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSafe("feedhealth '" + name + "': " + ex.Message);
                        sb.Append(",\"lastPrice\":null,\"lastTickTime\":\"\",\"ageMs\":null");
                    }
                    sb.Append("}");
                }
                sb.Append("]}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"ts\":" +
                       JsonStr(DateTime.UtcNow.ToString("o")) + ",\"feeds\":[],\"errors\":[{" +
                       "\"code\":\"BRIDGE\",\"message\":" + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        // Reconnect a configured connection by name via Connection.Connect(savedOptions),
        // marshalled to the UI dispatcher (the spike-proven safe path). UNCONDITIONAL —
        // an explicit reconnect is an operator override; the inadvertent-only POLICY lives
        // in the Python guardian, not here. No-op (reports wasConnected) if already up.
        private string RunReconnect(string id, string nameArg)
        {
            try
            {
                if (string.IsNullOrEmpty(nameArg))
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{" +
                           "\"code\":\"BRIDGE\",\"message\":\"reconnect requires a connection name\"}]}";

                var live = new List<Connection>();
                try { lock (Connection.Connections) { foreach (Connection c in Connection.Connections) live.Add(c); } } catch { }
                bool wasConnected = LiveStatusOf(live, nameArg) == "Connected";

                ConnectOptions opt = null;
                var cfg = new List<ConnectOptions>();
                try { lock (Globals.ConnectOptions) { foreach (ConnectOptions o in Globals.ConnectOptions) cfg.Add(o); } } catch { }
                foreach (ConnectOptions o in cfg)
                    if (SafeStr(delegate { return o.Name; }) == nameArg) { opt = o; break; }
                if (opt == null)
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{" +
                           "\"code\":\"BRIDGE\",\"message\":" + JsonStr("connection not configured: " + nameArg) + "}]}";

                bool attempted = false, threw = false, timedOut = false; string err = "";
                if (!wasConnected)
                {
                    Exception cap = null;
                    bool ran = false;
                    ConnectOptions o2 = opt;
                    Action act = delegate { ran = true; try { Connection.Connect(o2); } catch (Exception ex) { cap = ex; } };
                    // Bounded UI marshal (MED-2): if the UI dispatcher is wedged, Invoke returns after
                    // the timeout instead of blocking the bridge poller thread — which holds _gate —
                    // indefinitely and starving every other command, including the flatten kill-switch.
                    // A truly hung UI thread is then caught separately by the heartbeat watchdog
                    // (TryBeat stops rewriting heartbeat.json -> stale mtime -> `nt8bridge watchdog`).
                    try
                    {
                        var disp = Globals.MainThreadDispatcher;
                        if (disp != null)
                            disp.Invoke(System.Windows.Threading.DispatcherPriority.Send, TimeSpan.FromSeconds(10), act);
                        else
                            act();
                    }
                    catch (Exception ex) { cap = ex; }
                    timedOut = !ran && cap == null;
                    attempted = true; threw = cap != null;
                    if (threw) err = cap.GetType().Name + ": " + cap.Message;
                    else if (timedOut) err = "TimeoutException: UI dispatcher did not run Connect within 10s";
                    LogSafe("reconnect: " + nameArg + " attempted threw=" + threw + " timedOut=" + timedOut + ((threw || timedOut) ? " " + err : ""));
                    if (ran) System.Threading.Thread.Sleep(1500);   // connect kicked off -> let the async connect begin
                }

                var live2 = new List<Connection>();
                try { lock (Connection.Connections) { foreach (Connection c in Connection.Connections) live2.Add(c); } } catch { }
                string statusAfter = LiveStatusOf(live2, nameArg);

                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"ts\":" + JsonStr(DateTime.UtcNow.ToString("o")) +
                       ",\"name\":" + JsonStr(nameArg) +
                       ",\"wasConnected\":" + (wasConnected ? "true" : "false") +
                       ",\"connectAttempted\":" + (attempted ? "true" : "false") +
                       ",\"connectThrew\":" + (threw ? "true" : "false") +
                       ",\"connectTimedOut\":" + (timedOut ? "true" : "false") +
                       ",\"connectError\":" + JsonStr(err) +
                       ",\"statusAfter\":" + JsonStr(statusAfter) + "}";
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{" +
                       "\"code\":\"BRIDGE\",\"message\":" + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        // live ConnectionStatus for a configured connection name, or "(none)" if not active.
        private static string LiveStatusOf(List<Connection> live, string name)
        {
            foreach (Connection c in live)
                if (SafeStr(delegate { return c.Options != null ? c.Options.Name : ""; }) == name)
                    return SafeStr(delegate { return c.Status.ToString(); });
            return "(none)";
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

        // Return the "{...}" object substring for  "key":{...}  via a balanced-brace scan from the
        // first '{' after the key (string-aware so braces inside quoted values do not miscount).
        // Returns "" when the key is absent or its value is not an object. Scoping a flat key read
        // (e.g. "instrument") to the right nested slice prevents collisions with a same-named value
        // elsewhere in the trigger. Dependency-free (no Newtonsoft).
        private static string ExtractJsonObject(string json, string key)
        {
            if (json == null) return "";
            string pat = "\"" + key + "\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return "";
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return "";
            int open = c + 1;
            while (open < json.Length && char.IsWhiteSpace(json[open])) open++;
            if (open >= json.Length || json[open] != '{') return "";  // value is not an object
            int depth = 0;
            bool inStr = false;
            for (int p = open; p < json.Length; p++)
            {
                char ch = json[p];
                if (inStr)
                {
                    if (ch == '\\') { p++; continue; }   // skip the escaped char
                    if (ch == '"') inStr = false;
                }
                else if (ch == '"') inStr = true;
                else if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0) return json.Substring(open, p - open + 1);
                }
            }
            return "";  // unbalanced
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
