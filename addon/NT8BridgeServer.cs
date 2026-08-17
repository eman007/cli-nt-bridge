// NT8BridgeServer.cs — NT8 Bridge AddOn (in-process request handler)  ·  nt8bridge 1.7.0
//
// Watches  <UserDataDir>\NT8Bridge\trigger\  for {kind:"..."} request files and writes each answer
// to  result\<kind>_<id>.json. The Python client (`nt8bridge`) is the only intended caller.
//
// One-time install: copy into  Documents\NinjaTrader 8\bin\Custom\AddOns\, then compile in the
// NinjaScript editor (F5). On load it writes a heartbeat so the client can tell "AddOn not running"
// from "the request failed".
//
// KINDS HANDLED
//   build/deploy : compile · reload
//   read-only    : windows · playback · ntstatus · workspace · screenshot · peek · probe ·
//                  connections · feedhealth · performance · perfwindow · chartseries ·
//                  marketReplayDump · marketReplayDownload
//   mutating     : backtest · configure · flatten · reconnect · layout ·
//                  log · dialog · strategy · playbackctl · chart
//
// CHANGELOG
//   1.7.0  Added log · dialog · strategy · playbackctl · chart. Fixed ExtractJsonString, which
//          matched a quoted VALUE as if it were a key ("kind":"chart" satisfied a lookup for
//          "chart"), silently turning a filter into the wrong field's value.
//   1.6.0  layout (capture/apply window placement).
//   1.5.0  playback · ntstatus · workspace · screenshot.
//
// THREE RULES EVERY MUTATING HANDLER FOLLOWS — each bought with real lost time:
//   1. Refuse an ambiguous match; never resolve it by taking the first.
//   2. Require explicit confirmation before arming an order source.
//   3. VERIFY THE OUTCOME. A call that resolves while changing nothing must never read as success.
//
// ⚠ NT IS MULTI-UI-THREADED: every window owns its own dispatcher. Touching a WPF member from the
//   poller thread throws on every window, every time — which returns an empty list that looks like
//   a real answer. Win32 is thread-agnostic and is preferred wherever it can do the job; otherwise
//   marshal to the owning dispatcher with a BOUNDED wait and report a timeout as a fact.
//
// ⚠ NO SENTINEL REFERENCES IN THIS FILE, BY DESIGN. It ships in an open-source repo. Anything
//   project-specific (which log matters, which strategy to arm) is supplied by the CALLER.
//
// Anti-lockup: every override try/catch-wrapped, timer disposed on Terminated, wall-clock polling
// (no per-tick work), re-entrancy guarded.
#region Using declarations
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;             // log: culture-invariant timestamp parsing
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;   // log: server-side filtering (the payload ceiling is ~12 KB)
using System.Runtime.InteropServices;   // windows: Win32 window inventory (NT is multi-UI-threaded)
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;           // windows: WindowInteropHelper -> HWND
using System.Xml.Linq;                  // chart --apply-template: hand NT the XML it already uses
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
                else if (kind == "order")
                    WriteResult("order_" + id + ".json", RunOrder(id, text));
                else if (kind == "connections")
                    WriteResult("connections_" + id + ".json", RunConnections(id, text));
                else if (kind == "reconnect")
                    WriteResult("reconnect_" + id + ".json", RunReconnect(id, ExtractJsonString(text, "connection")));
                else if (kind == "playback")
                    WriteResult("playback_" + id + ".json", RunPlayback(id, ExtractJsonString(text, "instrument")));
                else if (kind == "ntstatus")
                    WriteResult("ntstatus_" + id + ".json", RunNtStatus(id));
                else if (kind == "workspace")
                    WriteResult("workspace_" + id + ".json", RunWorkspace(id));
                else if (kind == "screenshot")
                    WriteResult("screenshot_" + id + ".json", RunScreenshot(id, text));
                else if (kind == "layout")
                    WriteResult("layout_" + id + ".json", RunLayout(id, text));
                else if (kind == "log")
                    WriteResult("log_" + id + ".json", RunLog(id, text));
                else if (kind == "dialog")
                    WriteResult("dialog_" + id + ".json", RunDialog(id, text));
                else if (kind == "strategy")
                    WriteResult("strategy_" + id + ".json", RunStrategy(id, text));
                else if (kind == "playbackctl")
                    WriteResult("playbackctl_" + id + ".json", RunPlaybackCtl(id, text));
                else if (kind == "chart")
                    WriteResult("chart_" + id + ".json", RunChart(id, text));
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
            if (kind == "playback") return "playback_";
            if (kind == "ntstatus") return "ntstatus_";
            if (kind == "workspace") return "workspace_";
            if (kind == "screenshot") return "screenshot_";
            if (kind == "layout") return "layout_";
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
            if (kind == "log") return "log_";
            if (kind == "dialog") return "dialog_";
            if (kind == "strategy") return "strategy_";
            if (kind == "playbackctl") return "playbackctl_";
            if (kind == "chart") return "chart_";
            return "compile_";
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        //  log — read a log file from INSIDE NinjaTrader, filtered AT THE SOURCE
        //
        //  WHY THE FILTERING HAPPENS HERE AND NOT AT THE CLIENT
        //    The remote transport this fleet drives encodes its payload UTF-16LE then base64, so the
        //    usable command size is roughly 12 KB — while the files worth reading run to tens of
        //    megabytes. Shipping a whole log to be searched at the far end is not slow, it is
        //    impossible. Only matches travel.
        //
        //  AND THE FILE IS OPEN
        //    NinjaTrader holds its own logs open for writing. A reader that omits
        //    FileShare.ReadWrite | FileShare.Delete throws IOException on the live file — which is the
        //    only file anyone ever wants to read. That flag is the whole reason this is not `type`.
        //
        //  ⭐ ABSENCE IS NOT EVIDENCE
        //    A file that does not exist answers `status:"error"`, never `ok` with zero matches. "No
        //    faults found" and "the log this box writes lives somewhere else" must not render alike:
        //    a fault that only fires when nobody is watching is the worst kind to leave unread, and a
        //    check that reports clean for a path that was never there would guarantee it stays unread.
        //
        //  NOT SENTINEL-AWARE, DELIBERATELY
        //    This handler knows nothing about which logs matter — the CALLER supplies the path. That
        //    keeps every Sentinel reference out of this file, which is a standing rule for it.
        private string RunLog(string id, string text)
        {
            string file = ExtractJsonString(text, "file");
            string grep = ExtractJsonString(text, "grep");
            bool ignoreCase = ExtractJsonString(text, "ignoreCase") == "true";

            double sinceMin;
            if (!double.TryParse(ExtractJsonString(text, "sinceMin"), NumberStyles.Any,
                                 CultureInfo.InvariantCulture, out sinceMin) || sinceMin < 0) sinceMin = 0;
            int tail;
            if (!int.TryParse(ExtractJsonString(text, "tail"), out tail) || tail <= 0) tail = 200;
            long maxBytes;
            if (!long.TryParse(ExtractJsonString(text, "maxBytes"), out maxBytes) || maxBytes <= 0)
                maxBytes = 8L * 1024 * 1024;
            int maxLineChars;
            if (!int.TryParse(ExtractJsonString(text, "maxLineChars"), out maxLineChars) || maxLineChars <= 0)
                maxLineChars = 2000;

            if (string.IsNullOrEmpty(file))
                return LogError(id, "NOFILE", "file is required");

            string path = file;
            try
            {
                // A relative path resolves under the NT8 user data dir, which is where NT's own
                // trace\ and log\ live. Absolute paths are honoured as given.
                if (!Path.IsPathRooted(path)) path = Path.Combine(Globals.UserDataDir, path);
                path = Path.GetFullPath(path);
            }
            catch (Exception ex) { return LogError(id, "BADPATH", ex.Message); }

            Regex rx = null;
            if (!string.IsNullOrEmpty(grep))
            {
                try
                {
                    rx = new Regex(grep, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                }
                catch (Exception ex)
                {
                    // A malformed pattern must FAIL. Falling back to "match everything" or "match
                    // nothing" would both render as an answer, and one of them reads as all-clear.
                    return LogError(id, "BADREGEX", "pattern did not compile: " + ex.Message);
                }
            }

            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                // A directory reports Exists=false through FileInfo, so "trace" (the folder) and a
                // genuinely absent path would give the same message. They are different mistakes.
                if (Directory.Exists(path))
                    return LogError(id, "ISDIR", "that is a directory, not a file: " + path
                                                 + " — name the file inside it");
                return LogError(id, "NOTFOUND", "no such file: " + path);
            }

            long fileLen = 0, windowStart = 0;
            int scanned = 0, stamped = 0, matched = 0;
            bool truncatedFromStart = false;
            var keptText = new List<string>();
            var keptLine = new List<int>();
            DateTime cutoff = DateTime.Now.AddMinutes(-sinceMin);

            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    fileLen = fs.Length;
                    if (fileLen > maxBytes)
                    {
                        windowStart = fileLen - maxBytes;
                        fs.Seek(windowStart, SeekOrigin.Begin);
                        truncatedFromStart = true;
                    }
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        // The seek lands mid-line; that fragment would be a fake first record.
                        if (truncatedFromStart) sr.ReadLine();
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            scanned++;
                            DateTime ts;
                            bool hasTs = TryParseLogStamp(line, out ts);
                            if (hasTs) stamped++;
                            // A line with no stamp is kept: it is almost always a continuation of the
                            // stamped line above it (a stack trace), and dropping those turns an
                            // exception into its own first line.
                            if (sinceMin > 0 && hasTs && ts < cutoff) continue;
                            if (rx != null && !rx.IsMatch(line)) continue;
                            matched++;
                            if (line.Length > maxLineChars)
                                line = line.Substring(0, maxLineChars) + "…[truncated]";
                            keptText.Add(line);
                            keptLine.Add(scanned);
                            if (keptText.Count > tail) { keptText.RemoveAt(0); keptLine.RemoveAt(0); }
                        }
                    }
                }
            }
            catch (Exception ex) { return LogError(id, "READ", ex.Message); }

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(Globals.Now.ToString("o")))
              .Append(",\"file\":").Append(JsonStr(path))
              .Append(",\"exists\":true")
              .Append(",\"sizeBytes\":").Append(fileLen.ToString(CultureInfo.InvariantCulture))
              .Append(",\"modifiedUtc\":").Append(JsonStr(fi.LastWriteTimeUtc.ToString("o")))
              .Append(",\"scannedLines\":").Append(scanned.ToString(CultureInfo.InvariantCulture))
              .Append(",\"matched\":").Append(matched.ToString(CultureInfo.InvariantCulture))
              .Append(",\"returned\":").Append(keptText.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"tail\":").Append(tail.ToString(CultureInfo.InvariantCulture))
              .Append(",\"truncatedFromStart\":").Append(truncatedFromStart ? "true" : "false")
              .Append(",\"windowStartByte\":").Append(windowStart.ToString(CultureInfo.InvariantCulture))
              // Line numbers count from the start of the SCANNED WINDOW when the file was too big to
              // read whole. Saying which is cheaper than a reader guessing wrong.
              .Append(",\"lineNumbersFrom\":").Append(JsonStr(truncatedFromStart ? "window" : "file"))
              .Append(",\"timeFilter\":{\"sinceMin\":")
              .Append(sinceMin.ToString(CultureInfo.InvariantCulture))
              .Append(",\"applied\":").Append(sinceMin > 0 && stamped > 0 ? "true" : "false")
              .Append(",\"stampedLines\":").Append(stamped.ToString(CultureInfo.InvariantCulture));
            if (sinceMin > 0 && stamped == 0)
                // Report the no-op rather than let an unfiltered result pass for a filtered one.
                sb.Append(",\"note\":\"no line carried a parseable timestamp — --since was NOT applied\"");
            sb.Append("}").Append(",\"lines\":[");
            for (int i = 0; i < keptText.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(keptLine[i].ToString(CultureInfo.InvariantCulture))
                  .Append(",\"text\":").Append(JsonStr(keptText[i])).Append("}");
            }
            sb.Append("],\"errors\":[]}");
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        //  dialog — see and dismiss the modal that is blocking a headless box
        //
        //  WHY THIS EXISTS
        //    A modal dialog on an unattended machine stops everything and announces nothing. NT's Auto
        //    Rollover prompt sat on one node for days offering to roll the contract out from under a
        //    holdout window, and the only way to see it was an interactive session — which is itself
        //    barred during a bake, because an RDP teardown puts NT into a UCEERR render-death spiral.
        //    `windows` could already SEE such a window. It could not answer it.
        //
        //  RUNNING IN-PROCESS IS WHAT MAKES IT POSSIBLE
        //    UI automation only works in the interactive session; SSH lands in session 0, which owns no
        //    desktop. This handler runs inside NinjaTrader, so it is always already in session 1.
        //
        //  ⛔ IT WILL NOT GUESS
        //    `dismiss` requires an explicit dialog AND an explicit button, and refuses when either match
        //    is ambiguous. There is no "click the default" — the default on a rollover prompt is the
        //    answer that spends your holdout.
        //
        //  ⭐ VERIFY THE OUTCOME, NEVER THE CALL
        //    Every failure this project keeps paying for is a call that RESOLVED while doing nothing.
        //    A posted click proves only that a message was queued, so the dialog is re-probed afterwards
        //    and `dismissed` reports whether the window actually went away.
        private string RunDialog(string id, string text)
        {
            string action = ExtractJsonString(text, "action");
            string titleQ = ExtractJsonString(text, "title");
            string buttonQ = ExtractJsonString(text, "button");
            string hwndS = ExtractJsonString(text, "hwnd");
            int waitMs;
            if (!int.TryParse(ExtractJsonString(text, "waitMs"), out waitMs) || waitMs <= 0) waitMs = 5000;
            if (string.IsNullOrEmpty(action)) action = "list";

            List<BridgeDialog> found;
            try { found = FindDialogs(ExtractJsonString(text, "scope") == "all"); }
            catch (Exception ex) { return DialogError(id, action, "SCAN", ex.Message); }

            if (action == "list")
                return DialogList(id, found, null);

            // `close` is the honest fallback for a window whose buttons cannot be resolved — the
            // WPF visual-tree walk found NO buttons on a real `Error` box whose OK was plainly
            // visible in a screenshot. WM_CLOSE is what the title-bar X sends, and the outcome is
            // verified the same way: the window has to actually go away.
            if (action == "close")
            {
                long wantH;
                IntPtr th = IntPtr.Zero;
                if (!string.IsNullOrEmpty(hwndS) && long.TryParse(hwndS, out wantH))
                    th = new IntPtr(wantH);
                else if (!string.IsNullOrEmpty(titleQ))
                {
                    var m2 = new List<BridgeDialog>();
                    foreach (var d in found)
                        if (d.Title != null && d.Title.IndexOf(titleQ, StringComparison.OrdinalIgnoreCase) >= 0)
                            m2.Add(d);
                    if (m2.Count == 0) return DialogError(id, action, "NOMATCH", "no window matching '" + titleQ + "'");
                    if (m2.Count > 1) return DialogList(id, m2, "AMBIGUOUS: " + m2.Count + " windows match — nothing was closed.");
                    th = m2[0].Hwnd;
                }
                if (th == IntPtr.Zero)
                    return DialogError(id, action, "NOTARGET", "close requires --title or --hwnd");
                PostMessage(th, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                bool gone2 = false; int w2 = 0;
                while (w2 < waitMs) { if (!IsWindow(th) || !IsWindowVisible(th)) { gone2 = true; break; } Thread.Sleep(100); w2 += 100; }
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"action\":\"close\",\"dialogs\":[]"
                     + ",\"hwnd\":" + th.ToInt64().ToString(CultureInfo.InvariantCulture)
                     + ",\"dismissed\":" + (gone2 ? "true" : "false")
                     + ",\"clickedVia\":\"WM_CLOSE\""
                     + ",\"verdict\":" + JsonStr(gone2 ? "window closed"
                            : "WM_CLOSE POSTED BUT THE WINDOW IS STILL UP after " + w2 + "ms")
                     + ",\"errors\":[]}";
            }

            if (action != "dismiss")
                return DialogError(id, action, "BADACTION", "action must be 'list', 'dismiss' or 'close'");

            // ---- dismiss: resolve exactly one dialog ----
            if (string.IsNullOrEmpty(titleQ) && string.IsNullOrEmpty(hwndS))
                return DialogError(id, action, "NOTARGET",
                                   "dismiss requires --title or --hwnd; it will not pick one for you");
            if (string.IsNullOrEmpty(buttonQ))
                return DialogError(id, action, "NOBUTTON",
                                   "dismiss requires --button; there is no default answer");

            var matches = new List<BridgeDialog>();
            long wanted;
            if (!string.IsNullOrEmpty(hwndS) && long.TryParse(hwndS, out wanted))
            {
                foreach (var d in found) if (d.Hwnd.ToInt64() == wanted) matches.Add(d);
            }
            else
            {
                foreach (var d in found)
                    if (d.Title != null && d.Title.IndexOf(titleQ, StringComparison.OrdinalIgnoreCase) >= 0)
                        matches.Add(d);
            }
            if (matches.Count == 0)
                return DialogError(id, action, "NOMATCH", "no dialog matching '"
                                   + (titleQ ?? hwndS) + "' — nothing was clicked");
            if (matches.Count > 1)
                // Ambiguity is refused, not resolved by picking the first. Clicking the wrong dialog
                // is unrecoverable in a way that returning an error never is.
                return DialogList(id, matches, "AMBIGUOUS: " + matches.Count
                                  + " dialogs match — narrow --title or pass --hwnd. Nothing was clicked.");

            BridgeDialog dlg = matches[0];
            var btnMatches = new List<BridgeButton>();
            foreach (var b in dlg.Buttons)
                if (b.Text != null && b.Text.IndexOf(buttonQ, StringComparison.OrdinalIgnoreCase) >= 0)
                    btnMatches.Add(b);
            if (btnMatches.Count == 0)
                return DialogList(id, matches, "no button matching '" + buttonQ
                                  + "' on that dialog — nothing was clicked");
            if (btnMatches.Count > 1)
                return DialogList(id, matches, "AMBIGUOUS: " + btnMatches.Count
                                  + " buttons match '" + buttonQ + "' — nothing was clicked");

            BridgeButton btn = btnMatches[0];
            string how;
            try { how = ClickButton(dlg, btn); }
            catch (Exception ex) { return DialogError(id, action, "CLICK", ex.Message); }

            // ---- verify the OUTCOME ----
            bool gone = false;
            int waited = 0;
            while (waited < waitMs)
            {
                if (!IsWindow(dlg.Hwnd) || !IsWindowVisible(dlg.Hwnd)) { gone = true; break; }
                Thread.Sleep(100);
                waited += 100;
            }

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(Globals.Now.ToString("o")))
              .Append(",\"action\":\"dismiss\"")
              .Append(",\"dialog\":").Append(JsonStr(dlg.Title))
              .Append(",\"hwnd\":").Append(dlg.Hwnd.ToInt64().ToString(CultureInfo.InvariantCulture))
              .Append(",\"button\":").Append(JsonStr(btn.Text))
              .Append(",\"clickedVia\":").Append(JsonStr(how))
              .Append(",\"dismissed\":").Append(gone ? "true" : "false")
              .Append(",\"waitedMs\":").Append(waited.ToString(CultureInfo.InvariantCulture))
              .Append(",\"verdict\":").Append(JsonStr(gone
                    ? "dialog is gone"
                    : "CLICK POSTED BUT THE DIALOG IS STILL UP after " + waited
                      + "ms — treat this as NOT dismissed"))
              .Append(",\"errors\":[]}");
            return sb.ToString();
        }

        private class BridgeButton
        {
            public IntPtr Hwnd;            // native button; IntPtr.Zero for a WPF one
            public string Text;
            public object WpfButton;       // ButtonBase, resolved on its own dispatcher
            public System.Windows.Threading.Dispatcher Dispatcher;
        }

        private class BridgeDialog
        {
            public IntPtr Hwnd;
            public IntPtr Owner;
            public string Title;
            public string ClassName;
            public bool Modal;
            public string ButtonSource = "none";
            public List<BridgeButton> Buttons = new List<BridgeButton>();
        }

        private List<BridgeDialog> FindDialogs(bool scopeAll)
        {
            var outp = new List<BridgeDialog>();
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var candidates = new List<BridgeDialog>();
            EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
            {
                try
                {
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    if (pid != self || !IsWindow(h) || !IsWindowVisible(h)) return true;
                    var cn = new StringBuilder(256); GetClassName(h, cn, cn.Capacity);
                    var tb = new StringBuilder(400); GetWindowText(h, tb, tb.Capacity);
                    IntPtr owner = GetWindow(h, GW_OWNER);
                    bool modal = owner != IntPtr.Zero && !IsWindowEnabled(owner);
                    string cls = cn.ToString();
                    // Two ways to be a dialog: Windows' own dialog class, or an owned window that has
                    // disabled its owner (which is what a modal WPF window does).
                    // ⚠ BUT A NON-MODAL WINDOW CAN STILL BE BLOCKING YOUR DAY. A sentry was sitting
                    //   with an `Error` box and an `Auto Rollover Notification` open, and the
                    //   modal-only scan reported "no dialogs" on all six boxes — a clean bill of
                    //   health over a machine with two unanswered prompts on screen. `scope=all`
                    //   widens to every visible top-level window of the process.
                    if (scopeAll)
                    {
                        if (string.IsNullOrEmpty(tb.ToString())) return true;   // untitled = chrome
                    }
                    else if (!modal && cls != "#32770") return true;
                    candidates.Add(new BridgeDialog
                    {
                        Hwnd = h, Owner = owner, Title = tb.ToString(), ClassName = cls, Modal = modal,
                    });
                }
                catch { }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            foreach (var d in candidates)
            {
                CollectNativeButtons(d);
                if (d.Buttons.Count == 0) CollectWpfButtons(d);
                outp.Add(d);
            }
            return outp;
        }

        // Native path: a Win32 dialog's buttons are real child HWNDs of class "Button".
        private void CollectNativeButtons(BridgeDialog d)
        {
            EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
            {
                try
                {
                    var cn = new StringBuilder(64); GetClassName(h, cn, cn.Capacity);
                    if (cn.ToString() != "Button") return true;
                    var tb = new StringBuilder(256); GetWindowText(h, tb, tb.Capacity);
                    string t = tb.ToString();
                    if (t.Length == 0) return true;
                    // Strip the accelerator marker so a caller matches "Yes", not "&Yes".
                    d.Buttons.Add(new BridgeButton { Hwnd = h, Text = t.Replace("&", "") });
                }
                catch { }
                return true;
            };
            try { EnumChildWindows(d.Hwnd, cb, IntPtr.Zero); } catch { }
            GC.KeepAlive(cb);
            if (d.Buttons.Count > 0) d.ButtonSource = "win32";
        }

        // WPF path: a WPF window renders its whole tree into ONE HWND, so there are no child button
        // handles to find. The tree must be walked on the dispatcher that owns it — NT is
        // multi-UI-threaded, and touching another thread's visual tree throws every time.
        //
        // The owning dispatcher is discovered by asking each known window's dispatcher whether
        // HwndSource.FromHwnd resolves there; exactly one will answer. That works even for a dialog
        // that never appears in AllWindows, which a transient modal often does not.
        private void CollectWpfButtons(BridgeDialog d)
        {
            var seen = new List<System.Windows.Threading.Dispatcher>();
            try
            {
                var all = Globals.AllWindows;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var w = all[i];
                        if (w == null) continue;
                        var disp = w.Dispatcher;
                        if (disp != null && !seen.Contains(disp)) seen.Add(disp);
                    }
            }
            catch { }

            foreach (var disp in seen)
            {
                try
                {
                    IntPtr target = d.Hwnd;
                    var acc = new List<BridgeButton>();
                    var op = disp.BeginInvoke(new Func<bool>(delegate
                    {
                        var src = HwndSource.FromHwnd(target);
                        if (src == null) return false;
                        var root = src.RootVisual as DependencyObject;
                        if (root == null) return false;
                        WalkForButtons(root, acc);
                        return true;
                    }));
                    // Bounded: a busy UI thread must never hold the poller hostage.
                    if (op.Wait(TimeSpan.FromSeconds(3))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed) continue;
                    if (!(op.Result is bool) || !(bool)op.Result) continue;
                    foreach (var b in acc) { b.Dispatcher = disp; d.Buttons.Add(b); }
                    d.ButtonSource = "wpf";
                    return;
                }
                catch { }
            }
        }

        private static void WalkForButtons(DependencyObject node, List<BridgeButton> acc)
        {
            if (node == null || acc.Count > 64) return;
            var bb = node as System.Windows.Controls.Primitives.ButtonBase;
            if (bb != null)
            {
                // ⚠ A TYPE NAME IS NOT A LABEL. An icon button's Content is a Shape, and
                // Content.ToString() dutifully returns "System.Windows.Shapes.Path" — which then
                // appears in the listing as if it were a caption you could click by name. The
                // "About NinjaTrader" dialog produced three of them alongside its real OK button.
                // Inventing a matchable label for an unlabelled control is worse than admitting
                // there is none: a caller could match it, and would be clicking blind.
                string label = null;
                try
                {
                    label = bb.Content as string;
                    if (string.IsNullOrEmpty(label))
                    {
                        object at = System.Windows.Automation.AutomationProperties.GetName(bb);
                        label = at as string;
                    }
                    if (string.IsNullOrEmpty(label) && bb.ToolTip is string) label = (string)bb.ToolTip;
                    if (string.IsNullOrEmpty(label)) label = bb.Name;
                    if (!string.IsNullOrEmpty(label)
                        && label.IndexOf("System.Windows.", StringComparison.Ordinal) >= 0)
                        label = null;   // a rendered ToString(), not a caption
                }
                catch { }
                if (!string.IsNullOrEmpty(label))
                    acc.Add(new BridgeButton { Hwnd = IntPtr.Zero, Text = label.Replace("_", ""), WpfButton = bb });
                else
                    // Counted, never named. "3 unlabelled controls" is a true statement; a list of
                    // type names is not.
                    acc.Add(new BridgeButton { Hwnd = IntPtr.Zero, Text = null, WpfButton = bb });
            }
            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); } catch { return; }
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = null;
                try { child = System.Windows.Media.VisualTreeHelper.GetChild(node, i); } catch { }
                WalkForButtons(child, acc);
            }
        }

        private string ClickButton(BridgeDialog d, BridgeButton b)
        {
            if (b.Hwnd != IntPtr.Zero)
            {
                // POST, not SEND: a send blocks this poller thread until the other UI thread services
                // it, and that thread is by definition sitting in a modal message loop.
                PostMessage(b.Hwnd, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                return "win32 BM_CLICK";
            }
            if (b.WpfButton != null && b.Dispatcher != null)
            {
                string how = "wpf";
                var op = b.Dispatcher.BeginInvoke(new Func<string>(delegate
                {
                    var bb = b.WpfButton as System.Windows.Controls.Primitives.ButtonBase;
                    if (bb == null) return "wpf (button vanished)";
                    // A Command, when present, is what the button actually does — raising Click alone
                    // would fire handlers and skip the command, which on some dialogs does nothing at all.
                    if (bb.Command != null && bb.Command.CanExecute(bb.CommandParameter))
                    {
                        bb.Command.Execute(bb.CommandParameter);
                        return "wpf ICommand";
                    }
                    bb.RaiseEvent(new RoutedEventArgs(
                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent, bb));
                    return "wpf Click event";
                }));
                if (op.Wait(TimeSpan.FromSeconds(5))
                    == System.Windows.Threading.DispatcherOperationStatus.Completed)
                    how = op.Result as string ?? how;
                return how;
            }
            throw new InvalidOperationException("button has neither an HWND nor a WPF element");
        }

        private string DialogList(string id, List<BridgeDialog> dialogs, string note)
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":").Append(note == null ? "\"ok\"" : "\"error\"")
              .Append(",\"ts\":").Append(JsonStr(Globals.Now.ToString("o")))
              .Append(",\"action\":\"list\",\"dismissed\":false")
              .Append(",\"count\":").Append(dialogs.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"dialogs\":[");
            for (int i = 0; i < dialogs.Count; i++)
            {
                var d = dialogs[i];
                if (i > 0) sb.Append(",");
                sb.Append("{\"hwnd\":").Append(d.Hwnd.ToInt64().ToString(CultureInfo.InvariantCulture))
                  .Append(",\"title\":").Append(JsonStr(d.Title))
                  .Append(",\"class\":").Append(JsonStr(d.ClassName))
                  .Append(",\"modal\":").Append(d.Modal ? "true" : "false")
                  .Append(",\"buttonSource\":").Append(JsonStr(d.ButtonSource))
                  .Append(",\"buttons\":[");
                int named = 0, unlabelled = 0;
                for (int j = 0; j < d.Buttons.Count; j++)
                {
                    if (d.Buttons[j].Text == null) { unlabelled++; continue; }
                    if (named > 0) sb.Append(",");
                    named++;
                    sb.Append(JsonStr(d.Buttons[j].Text));
                }
                // Reported so a dialog whose real button is unlabelled does not read as "no buttons",
                // which would look like a dialog that cannot be answered at all.
                sb.Append("],\"unlabelledButtons\":").Append(unlabelled.ToString(CultureInfo.InvariantCulture))
                  .Append("}");
            }
            sb.Append("]");
            if (note != null)
                sb.Append(",\"errors\":[{\"file\":\"\",\"line\":0,\"code\":\"REFUSED\",\"message\":")
                  .Append(JsonStr(note)).Append("}]}");
            else
                sb.Append(",\"errors\":[]}");
            return sb.ToString();
        }

        private static string DialogError(string id, string action, string code, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"action\":" + JsonStr(action)
                 + ",\"dismissed\":false,\"dialogs\":[],\"count\":0,"
                 + "\"errors\":[{\"file\":\"\",\"line\":0,\"code\":" + JsonStr(code)
                 + ",\"message\":" + JsonStr(message) + "}]}";
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        //  strategy — list / enable / disable / add, on a CHART
        //
        //  WHY THIS EXISTS
        //    A workspace file does NOT contain its strategy. It stores an integer handle; the type and
        //    every parameter live in db\NinjaTrader.sqlite, a file that also holds Accounts, Orders and
        //    Positions and so must never be copied between boxes. Staging an identical cell on a second
        //    machine therefore ended in a human re-adding the strategy by hand — six workers, six hands.
        //    Anything that needs a GUI click per box cannot run a matrix, and that is the whole reason
        //    the fleet exists.
        //
        //    Twice in one day a Playback toggle silently DISABLED a chart strategy and a replay ran for
        //    half an hour producing nothing. `workspace` can already see that; it could not fix it.
        //
        //  ⛔ EVERY MUTATION IS EXPLICIT AND CONFIRMED
        //    An ambiguous match is refused, never resolved by taking the first. `enable` and `add` start
        //    an ORDER SOURCE, so they additionally require confirm — `disable` does not, because the safe
        //    direction should never be the harder one to reach.
        //
        //  ⭐ REFLECT, THEN VERIFY THE OUTCOME
        //    These members are not a published contract and can move between NT builds. So the state is
        //    re-read from the object AFTER the call and the response reports before/after. A SetState
        //    that resolved and changed nothing reads as failure here, which is the only way this is safe
        //    to run unattended. `list` also reports which members actually resolved, so a future break
        //    is diagnosable from one read-only command instead of by guessing.
        private string RunStrategy(string id, string text)
        {
            string action = ExtractJsonString(text, "action");
            if (string.IsNullOrEmpty(action)) action = "list";
            string chartQ = ExtractJsonString(text, "chart");
            string nameQ = ExtractJsonString(text, "name");
            string typeQ = ExtractJsonString(text, "type");
            bool confirm = ExtractJsonString(text, "confirm") == "true";
            var notes = new List<string>();

            if (action == "add")
                return StrategyAdd(id, chartQ, typeQ, ParseParams(text), confirm,
                                   ExtractJsonString(text, "force") == "true", notes);

            List<StratRef> found;
            try { found = FindStrategies(chartQ, nameQ, notes); }
            catch (Exception ex) { return StrategyError(id, action, "SCAN", ex.Message); }

            if (action == "list")
                return StrategyList(id, action, found, notes, null);

            if (action != "enable" && action != "disable")
                return StrategyError(id, action, "BADACTION", "action must be list, enable, disable or add");

            if (string.IsNullOrEmpty(nameQ))
                return StrategyError(id, action, "NOTARGET",
                                     action + " requires --name; it will not pick a strategy for you");
            if (found.Count == 0)
                return StrategyList(id, action, found, notes,
                                    "no strategy matching '" + nameQ + "' — nothing was changed");
            // Refusing ambiguity is right, but it must not become a dead end: a chart CAN legitimately
            // hold two instances of the same type (a duplicate add leaves exactly that), and then no
            // name or chart filter can separate them. --index makes the choice explicit and visible
            // rather than reintroducing "just take the first".
            int pick;
            bool hasPick = int.TryParse(ExtractJsonString(text, "index"), out pick);
            if (found.Count > 1 && hasPick && pick >= 0 && pick < found.Count)
            {
                var one = new List<StratRef>(); one.Add(found[pick]);
                notes.Add("--index " + pick + " selected 1 of " + found.Count + " matches");
                found = one;
            }
            else if (found.Count > 1)
                return StrategyList(id, action, found, notes,
                                    "AMBIGUOUS: " + found.Count + " strategies match — narrow --name or "
                                    + "--chart, or pass --index 0.." + (found.Count - 1)
                                    + ". Nothing was changed.");
            if (action == "enable" && !confirm)
                // Arming an automated order source unattended is not a thing to do on a substring match.
                return StrategyList(id, action, found, notes,
                                    "enable starts an ORDER SOURCE and requires --confirm. Nothing was changed.");

            StratRef sr = found[0];
            string before = sr.State, after = null, how = null;
            bool wantEnable = action == "enable";
            bool wantOn = wantEnable;
            string mech = ExtractJsonString(text, "mechanism");
            if (string.IsNullOrEmpty(mech)) mech = "auto";

            // ⛔ StrategyEnable IS A TOGGLE, NOT A SETTER — measured, and it cost a live strategy.
            //    Invoking it with `true` on a Finalized strategy enabled it (asynchronously, which
            //    is why the first attempt looked like a no-op). Invoking it with `true` AGAIN on the
            //    now-Realtime strategy turned it back OFF. So the boolean does not mean what its
            //    position suggests, and calling it when the strategy is already in the desired state
            //    does the opposite of what was asked.
            //    ⇒ Read first, and if we are already there, do NOTHING. "Already enabled" must never
            //      be implemented as "enable again".
            bool beforeLive = before == "Active" || before == "Realtime" || before == "Historical";
            // ⚠ REMOVE IS NOT DISABLE. The already-in-state shortcut is right for a toggle-ish
            //   enable/disable, but it must not block a REMOVAL: a stopped strategy is still
            //   attached to the chart, and "already disabled, nothing to do" left two inert
            //   duplicates sitting there with no way to clear them.
            if (beforeLive == wantEnable && mech != "remove")
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"ts\":"
                     + JsonStr(Globals.Now.ToString("o")) + ",\"action\":" + JsonStr(action)
                     + ",\"chart\":" + JsonStr(sr.ChartTitle)
                     + ",\"strategy\":" + JsonStr(sr.Name) + ",\"type\":" + JsonStr(sr.Type)
                     + ",\"stateBefore\":" + JsonStr(before) + ",\"stateAfter\":" + JsonStr(before)
                     + ",\"via\":\"(no call made)\",\"succeeded\":true,\"changed\":false,\"waitedMs\":0"
                     + ",\"verdict\":" + JsonStr("already " + action + "d (" + before
                           + ") — nothing was called, because the chart's enable is a TOGGLE and "
                           + "calling it here would have reversed it")
                     + ",\"strategies\":[],\"notes\":[],\"errors\":[]}";
            try
            {
                var op = sr.Win.Dispatcher.BeginInvoke(new Func<string>(delegate
                {
                    // Prefer the chart's own path; SetState is the fallback for a build that lacks it,
                    // and is known NOT to work for enable — so if the fallback is what ran, the
                    // outcome check below is what will say so.
                    string via = ChartStrategyEnable(sr.Cc, sr.Strat, wantOn, mech, notes);
                    if (via != null) return via;
                    notes.Add("ChartControl.StrategyEnable unavailable — falling back to SetState, "
                              + "which does not drive a Finalized strategy back to Active");
                    return SetNsState(sr.Strat, wantOn ? State.Active : State.Terminated);
                }));
                if (op.Wait(TimeSpan.FromSeconds(20))
                    != System.Windows.Threading.DispatcherOperationStatus.Completed)
                    return StrategyError(id, action, "TIMEOUT",
                                         "the chart's UI thread did not answer within 20s — state unknown, "
                                         + "re-read with `strategy list` before assuming anything");
                how = op.Result as string;
            }
            catch (Exception ex) { return StrategyError(id, action, "SETSTATE", Explain(ex)); }

            // ⭐⭐ POLL, DO NOT SNAPSHOT — the same root cause as the replay seek, met a second time.
            //    StrategyEnable takes an Action callback as its fourth argument, which is the tell:
            //    it is ASYNCHRONOUS. Reading the state immediately after invoking it returned
            //    `Finalized` and looked exactly like "the call did nothing" — the identical
            //    misreading that once cost a day on the transport. The state is re-read until it
            //    reaches the target or the budget runs out, and the elapsed time is reported so a
            //    slow transition is distinguishable from a dead one.
            int settleMs;
            if (!int.TryParse(ExtractJsonString(text, "settleMs"), out settleMs) || settleMs <= 0)
                settleMs = 20000;
            int waitedMs = 0;
            bool live;
            while (true)
            {
                after = ReadNsState(sr);
                live = after == "Active" || after == "Realtime" || after == "Historical"
                    || after == "Transition";
                if (live == wantEnable) break;      // reached the target state
                if (waitedMs >= settleMs) break;
                Thread.Sleep(250);
                waitedMs += 250;
            }
            // ⭐⭐ A STATE OBSERVED ONCE IS NOT A STATE CHANGE — and this is a false green I shipped
            //    and then caught by driving it. StrategyEnable(false) really does produce
            //    `Finalized`... for a moment. The strategy then comes back to Realtime on its own,
            //    because the call re-applies the strategy rather than setting a flag. A single
            //    sample of the target state therefore reports a durable disable that never happened,
            //    which is exactly the class of lie every other check here exists to prevent.
            //    ⇒ The target must HOLD. Re-read after a settle window and believe the second answer.
            //    ⚠ AND THE WINDOW HAS TO BE LONG ENOUGH. A 4-SECOND HOLD PASSED, AND THE STRATEGY
            //      CAME BACK 10 SECONDS LATER. A hold that is shorter than the revert is just a
            //      slower way to print the same false green, so this WATCHES throughout the window
            //      and fails the instant the state leaves the target rather than sampling its end.
            int holdMs;
            if (!int.TryParse(ExtractJsonString(text, "holdMs"), out holdMs) || holdMs < 0)
                holdMs = 15000;
            bool reverted = false;
            if (holdMs > 0)
            {
                int slept = 0;
                while (slept < holdMs)
                {
                    Thread.Sleep(500); slept += 500;
                    string h = ReadNsState(sr);
                    bool hLive = h == "Active" || h == "Realtime" || h == "Historical";
                    if (hLive != wantEnable) { reverted = true; after = h; break; }
                    after = h;
                }
                waitedMs += slept;
            }

            // ⭐ THE LADDER. `auto` climbs only as far as it must, and each rung must HOLD before it
            //    is accepted — so the answer is measured on this machine rather than assumed. The
            //    rungs ascend in violence: flag < flag+refresh < state machine < remove from chart.
            //    `remove` is last because it is not a disable at all and cannot be undone from here.
            if (reverted && mech == "auto")
            {
                string[] ladder = wantEnable
                    ? new[] { "flag-refresh", "setstate" }
                    : new[] { "flag-refresh", "setstate", "remove" };
                foreach (string rung in ladder)
                {
                    try
                    {
                        string rungName = rung;
                        var op2 = sr.Win.Dispatcher.BeginInvoke(new Func<string>(delegate
                        {
                            object bars2 = StrategyChartBars(sr.Strat, sr.Cc);
                            return ApplyStrategyMechanism(sr.Cc, sr.Strat, bars2, wantEnable,
                                                          rungName, notes);
                        }));
                        if (op2.Wait(TimeSpan.FromSeconds(20))
                            != System.Windows.Threading.DispatcherOperationStatus.Completed)
                        { notes.Add("rung '" + rung + "' did not answer in 20s"); continue; }
                        how = (how ?? "") + " -> [" + rung + "] " + (op2.Result as string ?? "");

                        int slept2 = 0;
                        reverted = false;
                        while (slept2 < holdMs)
                        {
                            Thread.Sleep(500); slept2 += 500;
                            string h = ReadNsState(sr);
                            // "Absent" means the strategy is off the chart entirely — for a disable
                            // that is a success, and a decisive one.
                            bool hLive = h == "Active" || h == "Realtime" || h == "Historical";
                            if (hLive != wantEnable) { reverted = true; after = h; break; }
                            after = h;
                        }
                        waitedMs += slept2;
                        if (!reverted) { notes.Add("rung '" + rung + "' HELD for " + slept2 + "ms"); break; }
                        notes.Add("rung '" + rung + "' did not hold");
                    }
                    catch (Exception ex) { notes.Add("rung '" + rung + "': " + Explain(ex)); }
                }
            }

            // `Transition` is on the way somewhere; it counts as live for "is it running" but is
            // reported verbatim so a caller sees it did not finish settling.
            live = after == "Active" || after == "Realtime" || after == "Historical";
            bool ok = (action == "enable" ? live : !live) && !reverted;
            // ⚠ A REMOVAL IS PROVED BY THE COUNT, NOT BY THE STATE. Two instances left at
            //   `Configure` were never bound to the ChartBars, so RemoveStrategyForChartBars had
            //   nothing to remove — and the state check happily read "not live" and called it a
            //   success while both were still sitting on the chart. Count it.
            int remaining = -1;
            if (mech == "remove")
            {
                var after2 = FindStrategies(chartQ, nameQ, notes);
                remaining = after2.Count;
                ok = remaining < found.Count || remaining == 0;
                if (!ok)
                    notes.Add("RemoveStrategyForChartBars ran but " + remaining
                              + " strategies of that name are still on the chart — instances that "
                              + "never bound to a ChartBars cannot be removed this way");
            }
            // `changed` and `succeeded` are DIFFERENT questions and conflating them lies in both
            // directions: disabling an already-stopped strategy moved nothing yet succeeded, while a
            // SetState that resolved and left the state untouched moved nothing and FAILED. Found by
            // running this against a chart whose three strategies were all already Finalized.
            bool moved = !string.Equals(before, after, StringComparison.Ordinal);

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(Globals.Now.ToString("o")))
              .Append(",\"action\":").Append(JsonStr(action))
              .Append(",\"chart\":").Append(JsonStr(sr.ChartTitle))
              .Append(",\"strategy\":").Append(JsonStr(sr.Name))
              .Append(",\"type\":").Append(JsonStr(sr.Type))
              .Append(",\"stateBefore\":").Append(JsonStr(before))
              .Append(",\"stateAfter\":").Append(JsonStr(after))
              .Append(",\"via\":").Append(JsonStr(how))
              .Append(",\"succeeded\":").Append(ok ? "true" : "false")
              .Append(",\"changed\":").Append(moved ? "true" : "false")
              .Append(",\"waitedMs\":").Append(waitedMs.ToString(InvCi))
              .Append(",\"reverted\":").Append(reverted ? "true" : "false")
              .Append(",\"mechanism\":").Append(JsonStr(mech))
              .Append(",\"remaining\":").Append(remaining.ToString(InvCi))
              .Append(",\"verdict\":").Append(JsonStr(
                    reverted ? "IT DID NOT HOLD — the state reached the target and then went back to "
                             + after + " after " + holdMs + "ms. The chart re-applies the strategy "
                             + "rather than setting a flag, so this is NOT a durable " + action + "."
                  : ok && moved ? action + "d — state moved " + before + " -> " + after
                  : ok && !moved ? "already " + action + "d (" + after + ") — nothing to do"
                  : "THE CALL RESOLVED BUT THE STATE IS " + (after ?? "unreadable")
                    + " — treat this as NOT " + action + "d"))
              .Append(",\"strategies\":[],\"notes\":[");
            for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(notes[i])); }
            sb.Append("],\"errors\":[]}");
            return sb.ToString();
        }

        private class StratRef
        {
            public string ChartTitle;
            public Window Win;
            public object Cc;
            public object Strat;
            public string Name;
            public string Type;
            public string State;
        }

        // Walk chart windows on THEIR OWN dispatchers. NT is multi-UI-threaded: reading a chart's
        // members from the poller thread throws on every window, every time.
        private List<StratRef> FindStrategies(string chartQ, string nameQ, List<string> notes)
        {
            var outp = new List<StratRef>();
            var snap = new List<Window>();
            try
            {
                var all = Globals.AllWindows;
                if (all != null) for (int i = 0; i < all.Count; i++) snap.Add(all[i]);
            }
            catch (Exception ex) { notes.Add("AllWindows snapshot: " + ex.Message); }

            foreach (Window w in snap)
            {
                if (w == null) continue;
                string tn;
                try { tn = w.GetType().FullName; } catch { continue; }
                if (tn == null || tn.IndexOf("Chart", StringComparison.Ordinal) < 0) continue;
                if (tn.IndexOf("ChartTrader", StringComparison.Ordinal) >= 0) continue;

                Window win = w;
                var acc = new List<StratRef>();
                try
                {
                    var op = win.Dispatcher.BeginInvoke(new Func<bool>(delegate
                    {
                        string title = null;
                        try { title = win.Title; } catch { }
                        if (!string.IsNullOrEmpty(chartQ)
                            && (title == null
                                || title.IndexOf(chartQ, StringComparison.OrdinalIgnoreCase) < 0))
                            return true;
                        object cc = FirstMember(win, new[] { "ActiveChartControl", "ChartControl" });
                        if (cc == null) return false;
                        var list = FirstMember(cc, new[] { "Strategies" }) as IEnumerable;
                        if (list == null) return false;
                        foreach (object s in list)
                        {
                            if (s == null) continue;
                            string nm = null;
                            try { nm = Convert.ToString(FirstMember(s, new[] { "Name" })); } catch { }
                            string ty = SafeTypeName(s);
                            // A Sentinel tool blanks its own Name at DataLoaded (the on-chart label IS
                            // the Name), so matching on name alone finds nothing for most of this suite.
                            if (string.IsNullOrEmpty(nm)) nm = ty;
                            if (!string.IsNullOrEmpty(nameQ)
                                && (nm == null || nm.IndexOf(nameQ, StringComparison.OrdinalIgnoreCase) < 0)
                                && (ty == null || ty.IndexOf(nameQ, StringComparison.OrdinalIgnoreCase) < 0))
                                continue;
                            string st = null;
                            try
                            {
                                object v = FirstMember(s, new[] { "State" });
                                st = v != null ? Convert.ToString(v) : null;
                            }
                            catch { }
                            acc.Add(new StratRef
                            {
                                ChartTitle = title, Win = win, Cc = cc, Strat = s,
                                Name = nm, Type = ty, State = st,
                            });
                        }
                        return true;
                    }));
                    if (op.Wait(TimeSpan.FromSeconds(5))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed)
                    {
                        // A chart we could not read is a FACT worth returning, not a silent omission:
                        // otherwise "no strategies" and "one window was busy" render identically.
                        notes.Add("chart window did not answer within 5s: " + tn);
                        continue;
                    }
                }
                catch (Exception ex) { notes.Add("chart read (" + tn + "): " + ex.Message); }
                outp.AddRange(acc);
            }
            return outp;
        }

        // ⭐ THE CHART OWNS THE TRANSITION, AND IT HAS A NAME.
        //    `SetState(Active)` on a Finalized chart strategy resolves and changes nothing — proven
        //    on a live box, where the command correctly reported its own failure. `chart --api` then
        //    named the real path: ChartControl.StrategyEnable(StrategyRenderBase, ChartBars, bool,
        //    Action). Discovery answered in one command what guessing had not.
        // ⭐ PREFER THE STRATEGY'S OWN ChartBars. A chart can host several bars series, and the
        //    strategy already knows which one it is attached to. Picking a series off the
        //    ChartControl is a guess that happens to be right on a single-series chart — exactly
        //    the kind of assumption that works until the day it silently targets the wrong data.
        private static object StrategyChartBars(object strat, object cc)
        {
            try
            {
                object own = FirstMember(strat, new[] { "ChartBars" });
                if (own != null && own.GetType().Name.IndexOf("ChartBars", StringComparison.Ordinal) >= 0)
                    return own;
            }
            catch { }
            return FindChartBars(cc);
        }

        // ⚠ Type.GetMethod(name, Type.EmptyTypes) BINDS PUBLIC-ONLY BY DEFAULT, and almost every
        //   useful member of NT's ChartControl is internal. `RefreshStrategies() did not resolve`
        //   was this and nothing else — the member was there the whole time, and the `--api` dump
        //   could see it only because that code passed NonPublic explicitly. Any reflection lookup
        //   here must say NonPublic or it is quietly asking a different question.
        //   ⚠ AND `GetMethod(name, flags, binder, types, ...)` STILL MISSED IT, while enumerating
        //     GetMethods with the same flags found it immediately — inherited non-public members do
        //     not resolve the same way through the binder overload. So this ENUMERATES, which is the
        //     lookup that was already proven to work by the `--api` dump sitting a few lines away.
        //     Believe the method that produced evidence, not the one that ought to work.
        // A do-nothing delegate matching whatever Action/Action<T> shape the member wants, so the
        // platform can invoke its completion callback safely instead of hitting a null.
        // ⭐ MethodInfo.Invoke WRAPS whatever the target threw in a TargetInvocationException whose
        //   own Message is the content-free "Exception has been thrown by the target of an
        //   invocation." Reporting that is reporting nothing — and it is exactly what NinjaTrader
        //   put on screen in an Error dialog on a sentry, from our own reflection call. Unwrap it.
        private static string Explain(Exception ex)
        {
            if (ex == null) return "";
            Exception root = ex;
            while (root is TargetInvocationException && root.InnerException != null)
                root = root.InnerException;
            return root.GetType().Name + ": " + root.Message;
        }

        private static void NoOp0() { }
        private static void NoOp1<T>(T ignored) { }

        private static object NoOpDelegate(Type delegateType, List<string> notes)
        {
            try
            {
                if (delegateType == null || !typeof(Delegate).IsAssignableFrom(delegateType)) return null;
                MethodInfo target;
                if (delegateType.IsGenericType)
                {
                    Type[] args = delegateType.GetGenericArguments();
                    if (args.Length != 1) return null;
                    target = typeof(NT8BridgeServer)
                        .GetMethod("NoOp1", BindingFlags.NonPublic | BindingFlags.Static)
                        .MakeGenericMethod(args);
                }
                else
                {
                    target = typeof(NT8BridgeServer)
                        .GetMethod("NoOp0", BindingFlags.NonPublic | BindingFlags.Static);
                }
                return Delegate.CreateDelegate(delegateType, target);
            }
            catch (Exception ex) { notes.Add("no-op callback: " + ex.Message); return null; }
        }

        private static MethodInfo NoArgMethod(Type t, string name)
        {
            try
            {
                for (Type cur = t; cur != null; cur = cur.BaseType)
                    foreach (MethodInfo m in cur.GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                 | BindingFlags.DeclaredOnly))
                        if (m.Name == name && m.GetParameters().Length == 0) return m;
            }
            catch { }
            return null;
        }

        private static object FindChartBars(object cc)
        {
            object v = FirstMember(cc, new[] { "ChartBars" });
            if (v != null && v.GetType().Name.IndexOf("ChartBars", StringComparison.Ordinal) >= 0)
                return v;
            var arr = FirstMember(cc, new[] { "BarsArray" }) as IEnumerable;
            if (arr != null) foreach (object o in arr) if (o != null) return o;
            try
            {
                foreach (PropertyInfo p in cc.GetType().GetProperties(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (p.PropertyType.Name.IndexOf("ChartBars", StringComparison.Ordinal) < 0) continue;
                    object x = null;
                    try { x = p.GetValue(cc, null); } catch { }
                    if (x != null) return x;
                }
            }
            catch { }
            return null;
        }

        private static string ChartStrategyEnable(object cc, object strat, bool enable, string mech, List<string> notes)
        {
            if (cc == null || strat == null) return null;
            MethodInfo mi = null;
            try
            {
                foreach (MethodInfo m in cc.GetType().GetMethods(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != "StrategyEnable") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 4 && ps[2].ParameterType == typeof(bool)) { mi = m; break; }
                }
            }
            catch (Exception ex) { notes.Add("StrategyEnable lookup: " + ex.Message); }
            if (mi == null) return null;
            object bars = StrategyChartBars(strat, cc);
            if (bars == null) { notes.Add("StrategyEnable found but no ChartBars resolved"); return null; }
            return ApplyStrategyMechanism(cc, strat, bars, enable, mech, notes);
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════
        //  A MECHANISM LADDER, NOT A GUESS
        //
        //  Disabling a chart strategy resisted four separate "fixes", each of which looked complete
        //  and none of which held. NT's own log finally showed why: the disable lands, and NT logs
        //  `Enabling ... On starting a real-time strategy` 600 ms later. So the argument is with the
        //  chart, not with the state machine, and which lever actually wins is an EMPIRICAL question
        //  about this build.
        //
        //  ⇒ Rather than hard-code another guess, every known lever is named and selectable, `auto`
        //    walks them in ascending order of violence, and each rung is VERIFIED to hold before the
        //    ladder stops. The response reports which rung won — so the answer is measured on the
        //    machine it has to work on, and stays measurable when a future build moves it.
        private static string ApplyStrategyMechanism(object cc, object strat, object bars,
                                                     bool enable, string mech, List<string> notes)
        {
            bool didFlag = false;
            if (mech == "auto" || mech == "flag" || mech == "flag-refresh" || mech == "enable-call")
            {
                try
                {
                    PropertyInfo pe = strat.GetType().GetProperty("IsEnabled",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (pe != null && pe.CanWrite) { pe.SetValue(strat, enable, null); didFlag = true; }
                    else notes.Add("IsEnabled is not writable on this build");
                }
                catch (Exception ex) { notes.Add("IsEnabled set: " + ex.Message); }
            }
            string via = didFlag ? "IsEnabled=" + (enable ? "true" : "false") : "";

            if (mech == "flag") return via.Length > 0 ? via : "(no lever available)";

            if (mech == "flag-refresh")
            {
                try
                {
                    MethodInfo rs = NoArgMethod(cc.GetType(), "RefreshStrategies");
                    if (rs != null) { rs.Invoke(cc, null); return via + " + RefreshStrategies()"; }
                    notes.Add("RefreshStrategies() did not resolve");
                }
                catch (Exception ex) { notes.Add("RefreshStrategies: " + Explain(ex)); }
                return via;
            }

            if (mech == "setstate")
                return SetNsState(strat, enable ? State.Active : State.Terminated);

            if (mech == "remove")
            {
                try
                {
                    MethodInfo rm = null;
                    foreach (MethodInfo m in cc.GetType().GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name != "RemoveStrategyForChartBars") continue;
                        if (m.GetParameters().Length == 1) { rm = m; break; }
                    }
                    if (rm == null) notes.Add("RemoveStrategyForChartBars did not resolve — trying the collection");
                    if (rm != null) rm.Invoke(cc, new object[] { bars });
                    // ⭐ AND THE COLLECTION'S OWN Remove, because RemoveStrategyForChartBars only
                    //   removes what is BOUND to those bars. An instance added programmatically
                    //   never binds, so it survived that call entirely — two inert duplicates sat on
                    //   a chart with no way to clear them, while the command reported success.
                    string extra = "";
                    try
                    {
                        object coll = FirstMember(cc, new[] { "Strategies" });
                        if (coll != null)
                        {
                            MethodInfo rem2 = coll.GetType().GetMethod("Remove");
                            if (rem2 != null)
                            {
                                object r = rem2.Invoke(coll, new object[] { strat });
                                extra = " + Strategies.Remove()=" + (r == null ? "void" : r.ToString());
                            }
                        }
                    }
                    catch (Exception ex) { notes.Add("Strategies.Remove: " + Explain(ex)); }
                    // Removal is not a disable: the strategy leaves the chart entirely. It is the
                    // last rung precisely because it is not reversible from here.
                    return via + " + RemoveStrategyForChartBars()" + extra;
                }
                catch (Exception ex) { notes.Add("RemoveStrategyForChartBars: " + Explain(ex)); return via; }
            }

            MethodInfo mi2 = null;
            foreach (MethodInfo m in cc.GetType().GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name != "StrategyEnable") continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length == 4 && ps[2].ParameterType == typeof(bool)) { mi2 = m; break; }
            }
            if (mi2 == null) return via;
            // ⭐⭐ DO NOT PASS null FOR THE CALLBACK.
            //    NT's log named the mechanism and it took two readings to hear it:
            //        Disabling NinjaScript strategy 'SentinelKeel_v0_1_0/397135297'
            //        Enabling  ... On starting a real-time strategy ... MaxRestarts=4 in 5 minutes
            //    `MaxRestarts` is NT's AUTO-RESTART for a strategy that terminates ABNORMALLY. The
            //    disable was landing correctly and then NT was resurrecting it, because a null
            //    Action invoked by the platform throws inside the teardown and the teardown looks
            //    like a crash. A real no-op delegate makes the disable an orderly one.
            object cb = NoOpDelegate(mi2.GetParameters()[3].ParameterType, notes);
            mi2.Invoke(cc, new object[] { strat, bars, enable, cb });
            return (via.Length > 0 ? via + " + " : "")
                 + "StrategyEnable(" + (enable ? "true" : "false") + ", "
                 + (cb == null ? "null" : "no-op callback") + ")";
        }


        private static string SetNsState(object ns, State target)
        {
            Type t = ns.GetType();
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo mi = null;
            try { mi = t.GetMethod("SetState", bf, null, new[] { typeof(State) }, null); }
            catch { }
            if (mi == null)
                throw new MissingMethodException("SetState(State) not found on " + t.FullName
                                                 + " — the NinjaScript state API moved; run "
                                                 + "`strategy list` to see what resolves");
            mi.Invoke(ns, new object[] { target });
            return "SetState(" + target + ")";
        }

        // ⭐⭐ RE-QUERY THE COLLECTION; DO NOT TRUST A CAPTURED REFERENCE.
        //    This is the subtlest false green of the lot, and it survived three rounds of "fixes".
        //    ChartControl.StrategyEnable does not flip a flag on the object you handed it — it
        //    TERMINATES that instance and the chart re-applies a NEW one. So a verifier holding the
        //    original reference watches a corpse: it reads `Finalized` and holds there forever,
        //    while the chart's actual strategy is a different object sitting at `Realtime`.
        //    A 45-second hold "confirmed" a disable that had not happened, and only an independent
        //    `strategy list` — which re-enumerates the collection — disagreed.
        //    ⇒ Identity here is the (type, chart) pair, never the pointer.
        private string ReadNsState(StratRef sr)
        {
            try
            {
                var op = sr.Win.Dispatcher.BeginInvoke(new Func<string>(delegate
                {
                    object cc = sr.Cc ?? FirstMember(sr.Win, new[] { "ActiveChartControl", "ChartControl" });
                    var list = cc != null ? FirstMember(cc, new[] { "Strategies" }) as IEnumerable : null;
                    if (list != null)
                    {
                        object fallback = null;
                        foreach (object o in list)
                        {
                            if (o == null) continue;
                            if (ReferenceEquals(o, sr.Strat))
                            {
                                // Same object still in the collection: authoritative.
                                object sv = FirstMember(o, new[] { "State" });
                                return sv != null ? Convert.ToString(sv) : null;
                            }
                            if (fallback == null && SafeTypeName(o) == sr.Type) fallback = o;
                        }
                        if (fallback != null)
                        {
                            object sv = FirstMember(fallback, new[] { "State" });
                            return sv != null ? Convert.ToString(sv) : null;
                        }
                        // Nothing of that type on the chart any more — genuinely gone, which is a
                        // different answer from "terminated" and must not read as one.
                        return "Absent";
                    }
                    object v = FirstMember(sr.Strat, new[] { "State" });
                    return v != null ? Convert.ToString(v) : null;
                }));
                if (op.Wait(TimeSpan.FromSeconds(10))
                    == System.Windows.Threading.DispatcherOperationStatus.Completed)
                    return op.Result as string;
            }
            catch { }
            return null;
        }

        private string StrategyAdd(string id, string chartQ, string typeQ,
                                   Dictionary<string, string> prms, bool confirm, bool force,
                                   List<string> notes)
        {
            if (string.IsNullOrEmpty(typeQ))
                return StrategyError(id, "add", "NOTYPE", "add requires --type");
            if (!confirm)
                return StrategyError(id, "add", "NOCONFIRM",
                                     "add attaches an ORDER SOURCE to a live chart and requires --confirm");
            // ⛔ MEASURED, NOT ASSUMED: an Activator-created strategy added to the collection never
            //    binds to a ChartBars. It sits inert at SetDefaults/Configure, it cannot be started,
            //    RemoveStrategyForChartBars cannot clear it (only the collection's own Remove can),
            //    and NinjaTrader raises a TargetInvocationException that lands as an `Error` box on
            //    the screen of an unattended machine. Attaching a strategy is a UI operation on this
            //    build. This refuses by default rather than leaving that trap armed.
            if (!force)
                return StrategyError(id, "add", "UNSAFE",
                                     "programmatic attach does not work on this build: the instance "
                                     + "never binds to a ChartBars, stays inert, and NT raises an "
                                     + "Error dialog. Add the strategy from the chart UI. Pass "
                                     + "--force to attempt it anyway.");

            Type st = null;
            try
            {
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = null;
                    try { an = a.GetName().Name; } catch { continue; }
                    if (an == null || an.IndexOf("NinjaTrader", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    Type[] types;
                    try { types = a.GetTypes(); } catch { continue; }
                    foreach (Type t in types)
                    {
                        if (t.IsAbstract || !typeof(StrategyBase).IsAssignableFrom(t)) continue;
                        if (string.Equals(t.Name, typeQ, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(t.FullName, typeQ, StringComparison.OrdinalIgnoreCase))
                        { st = t; break; }
                    }
                    if (st != null) break;
                }
            }
            catch (Exception ex) { return StrategyError(id, "add", "TYPESCAN", ex.Message); }
            if (st == null)
                return StrategyError(id, "add", "NOTYPEFOUND",
                                     "no StrategyBase subclass named '" + typeQ + "' is loaded — check "
                                     + "the class name (not the display Name) and that it compiled");

            // Same dispatcher-safe collection the chart handler uses: reading Window.Title from the
            // poller thread throws and used to be swallowed into a silent mis-filter.
            List<Window> targets = CollectChartWindows(chartQ, notes);

            if (targets.Count == 0)
                return StrategyError(id, "add", "NOCHART",
                                     "no chart matching '" + (chartQ ?? "(any)") + "'");
            if (targets.Count > 1)
                return StrategyError(id, "add", "AMBIGUOUS",
                                     targets.Count + " charts match — narrow --chart. Nothing was added.");

            Window target = targets[0];
            string applied = null, resultState = null, chartTitle = null;
            try
            {
                var op = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                {
                    try { chartTitle = target.Title; } catch { }
                    object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                    if (cc == null) return "no ChartControl on that window";
                    object coll = FirstMember(cc, new[] { "Strategies" });
                    if (coll == null) return "ChartControl has no Strategies collection";

                    object inst = Activator.CreateInstance(st);
                    foreach (var kv in prms)
                    {
                        try
                        {
                            PropertyInfo p = st.GetProperty(kv.Key,
                                BindingFlags.Public | BindingFlags.Instance);
                            if (p == null || !p.CanWrite) { notes.Add("param skipped (not writable): " + kv.Key); continue; }
                            p.SetValue(inst, ConvertToken(kv.Value, p.PropertyType), null);
                        }
                        catch (Exception ex) { notes.Add("param '" + kv.Key + "': " + ex.Message); }
                    }

                    // ⛔ ChartControl.ApplyStrategy WAS TRIED HERE AND MUST NOT BE. It is the
                    //    member the chart uses itself, so it looked like the principled answer —
                    //    but invoked from outside it blocked the chart's UI thread past 30 s and
                    //    raised TWO .NET assertion dialogs whose default button is Abort=Quit. On
                    //    an unattended box that is one stray click from killing NinjaTrader.
                    //    Left documented rather than deleted, so nobody re-derives it.
                    MethodInfo add = coll.GetType().GetMethod("Add");
                    if (add == null) return "the Strategies collection exposes no Add";
                    add.Invoke(coll, new object[] { inst });

                    // ⭐ Adding leaves the strategy at `Configure` — attached and inert. The chart
                    //    owns the rest of the transition, and `StrategyEnable` is the member that
                    //    performs it: measured across many attempts, it ALWAYS ends enabled, which
                    //    made it useless as a disable and makes it exactly right here.
                    //    The strategy's own ChartBars is used, so a multi-series chart cannot be
                    //    silently wired to the wrong data.
                    object bars = StrategyChartBars(inst, cc);
                    if (bars == null) bars = FindChartBars(cc);
                    string via2 = ApplyStrategyMechanism(cc, inst, bars, true, "enable-call", notes);

                    object sv = FirstMember(inst, new[] { "State" });
                    resultState = sv != null ? Convert.ToString(sv) : null;
                    return "Strategies.Add + " + via2;
                }));
                if (op.Wait(TimeSpan.FromSeconds(30))
                    != System.Windows.Threading.DispatcherOperationStatus.Completed)
                    return StrategyError(id, "add", "TIMEOUT",
                                         "the chart's UI thread did not answer within 30s — the strategy "
                                         + "may or may not be attached; check with `strategy list`");
                applied = op.Result as string;
            }
            catch (Exception ex) { return StrategyError(id, "add", "ADD", Explain(ex)); }

            // The transition is asynchronous — the same lesson as the seek and the enable. Poll the
            // chart's LIVE collection (never the instance we just handed over, which the chart may
            // replace) until a strategy of this type is running, or the budget expires.
            int addWait = 0;
            while (addWait < 30000)
            {
                bool found = false;
                try
                {
                    var opq = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                    {
                        object cc2 = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                        var l2 = cc2 != null ? FirstMember(cc2, new[] { "Strategies" }) as IEnumerable : null;
                        if (l2 == null) return null;
                        foreach (object o in l2)
                        {
                            if (o == null || o.GetType() != st) continue;
                            object v = FirstMember(o, new[] { "State" });
                            return v != null ? Convert.ToString(v) : null;
                        }
                        return "Absent";
                    }));
                    if (opq.Wait(TimeSpan.FromSeconds(10))
                        == System.Windows.Threading.DispatcherOperationStatus.Completed)
                    {
                        string s2 = opq.Result as string;
                        if (s2 != null) resultState = s2;
                        found = s2 == "Active" || s2 == "Realtime" || s2 == "Historical";
                    }
                }
                catch { }
                if (found) break;
                Thread.Sleep(500); addWait += 500;
            }
            notes.Add("waited " + addWait + "ms for the chart to bring it up");

            bool live = resultState == "Active" || resultState == "Realtime" || resultState == "Historical";
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(Globals.Now.ToString("o")))
              .Append(",\"action\":\"add\",\"chart\":").Append(JsonStr(chartTitle))
              .Append(",\"type\":").Append(JsonStr(st.FullName))
              .Append(",\"paramsRequested\":").Append(prms.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"via\":").Append(JsonStr(applied))
              .Append(",\"stateAfter\":").Append(JsonStr(resultState))
              .Append(",\"succeeded\":").Append(live ? "true" : "false")
              .Append(",\"changed\":").Append(live ? "true" : "false")
              .Append(",\"verdict\":").Append(JsonStr(live
                    ? "attached and running (" + resultState + ")"
                    : "ATTACHED BUT NOT RUNNING — state is " + (resultState ?? "unreadable")
                      + "; verify with `strategy list` before trusting a run"))
              .Append(",\"strategies\":[],\"notes\":[");
            for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(notes[i])); }
            sb.Append("],\"errors\":[]}");
            return sb.ToString();
        }

        private string StrategyList(string id, string action, List<StratRef> found,
                                    List<string> notes, string refusal)
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":").Append(refusal == null ? "\"ok\"" : "\"error\"")
              .Append(",\"ts\":").Append(JsonStr(Globals.Now.ToString("o")))
              .Append(",\"action\":").Append(JsonStr(action))
              .Append(",\"changed\":false")
              .Append(",\"count\":").Append(found.Count.ToString(CultureInfo.InvariantCulture))
              .Append(",\"strategies\":[");
            for (int i = 0; i < found.Count; i++)
            {
                var s = found[i];
                if (i > 0) sb.Append(",");
                bool live = s.State == "Active" || s.State == "Realtime" || s.State == "Historical";
                sb.Append("{\"chart\":").Append(JsonStr(s.ChartTitle))
                  .Append(",\"name\":").Append(JsonStr(s.Name))
                  .Append(",\"type\":").Append(JsonStr(s.Type))
                  .Append(",\"state\":").Append(JsonStr(s.State))
                  .Append(",\"enabled\":").Append(live ? "true" : "false").Append("}");
            }
            sb.Append("],\"notes\":[");
            for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(notes[i])); }
            sb.Append("]");
            if (refusal != null)
                sb.Append(",\"errors\":[{\"file\":\"\",\"line\":0,\"code\":\"REFUSED\",\"message\":")
                  .Append(JsonStr(refusal)).Append("}]}");
            else
                sb.Append(",\"errors\":[]}");
            return sb.ToString();
        }

        private static string StrategyError(string id, string action, string code, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"action\":" + JsonStr(action)
                 + ",\"changed\":false,\"count\":0,\"strategies\":[],\"notes\":[],"
                 + "\"errors\":[{\"file\":\"\",\"line\":0,\"code\":" + JsonStr(code)
                 + ",\"message\":" + JsonStr(message) + "}]}";
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        //  playbackctl — move the replay transport: seek, speed, and the connection range
        //
        //  ⭐⭐ THE SEEK EXISTS BECAUSE OF ONE ROOT CAUSE, AND IT WAS OURS
        //    A whole day was lost to a seek that "did not work". Every failing attempt reported back in
        //    57 ms; every succeeding one took 5–7 SECONDS. Reset is ASYNCHRONOUS and walks the clock
        //    toward the target progressively — so reading the position immediately, declaring
        //    OFF-TARGET and aborting is what froze the seek mid-flight. The clock's own history was the
        //    proof and it sat in a log the whole time, each attempt advancing and stopping:
        //        04-20 00:00:00 -> 04-21 03:06:14 -> 04-21 03:51:01
        //    always toward the target, never reaching it.
        //
        //    ⇒ THIS HANDLER POLLS THE CLOCK UNTIL IT SETTLES BEFORE RENDERING ANY VERDICT, and returns
        //      the whole trajectory it saw. The fleet currently runs a WORKAROUND for this
        //      (seekTolMin = 0 + seekPauseFirst) in which TWO settings were changed at once, so the
        //      cause was never established. The trajectory below is what establishes it.
        //
        //  ⚠ THE CONNECTION RANGE IS THE VERSION-FRAGILE PART
        //    ConnectOptions carries Start/End under OBFUSCATED names, which is exactly why programmatic
        //    connect was rejected once before. They are therefore located BY TYPE (DateTime properties)
        //    rather than by name, every write is READ BACK, and a write that cannot be verified is
        //    reported as a failure rather than assumed to have worked.
        private string RunPlaybackCtl(string id, string text)
        {
            string action = ExtractJsonString(text, "action");
            if (string.IsNullOrEmpty(action)) action = "api";
            bool confirm = ExtractJsonString(text, "confirm") == "true";

            Type pb;
            PropertyInfo piNowEst, piSpeed;
            try
            {
                pb = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                if (pb == null)
                    return PbError(id, action, "NOTRANSPORT",
                                   "PlaybackAdapter did not resolve — this NT build moved it");
                piNowEst = pb.GetProperty("NowEst", BFStatic);
                piSpeed = pb.GetProperty("PlaybackSpeed", BFStatic);
            }
            catch (Exception ex) { return PbError(id, action, "REFLECT", ex.Message); }

            if (action == "api")
                return PbApi(id, pb);

            if (action == "speed")
            {
                // ⚠ ZERO IS A REAL SPEED, not a missing argument. A parked transport reads 0, so
                // rejecting it makes the command unable to restore the state it just changed —
                // found while putting a test box back exactly as it was found.
                int want;
                if (!int.TryParse(ExtractJsonString(text, "speed"), out want) || want < 0)
                    return PbError(id, action, "BADSPEED", "speed must be a non-negative integer");
                if (piSpeed == null || !piSpeed.CanWrite)
                    return PbError(id, action, "NOSPEED", "PlaybackSpeed is not writable on this build");
                object before = null, after = null;
                try { before = piSpeed.GetValue(null); } catch { }
                try { piSpeed.SetValue(null, want); }
                catch (Exception ex) { return PbError(id, action, "SETSPEED", ex.Message); }
                try { after = piSpeed.GetValue(null); } catch { }
                bool ok = after is int && (int)after == want;
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"action\":\"speed\""
                     + ",\"requested\":" + want.ToString(InvCi)
                     + ",\"speedBefore\":" + (before is int ? ((int)before).ToString(InvCi) : "null")
                     + ",\"speedAfter\":" + (after is int ? ((int)after).ToString(InvCi) : "null")
                     + ",\"succeeded\":" + (ok ? "true" : "false")
                     + ",\"verdict\":" + JsonStr(ok ? "speed is now " + want
                            : "THE WRITE RESOLVED BUT THE SPEED READS BACK "
                              + (after == null ? "unreadable" : after.ToString()))
                     + ",\"errors\":[]}";
            }

            if (action == "range")
                return PbSetRange(id, text, confirm);

            if (action != "seek")
                return PbError(id, action, "BADACTION", "action must be api, seek, speed or range");

            // ---------------------------------------------------------------- seek
            DateTime target;
            string toS = ExtractJsonString(text, "to");
            if (string.IsNullOrEmpty(toS) ||
                !DateTime.TryParse(toS, InvCi, DateTimeStyles.None, out target))
                return PbError(id, action, "BADTARGET",
                               "seek requires --to as a parseable date-time");

            int settleMs;
            if (!int.TryParse(ExtractJsonString(text, "settleMs"), out settleMs) || settleMs <= 0)
                settleMs = 1500;
            int timeoutMs;
            if (!int.TryParse(ExtractJsonString(text, "timeoutMs"), out timeoutMs) || timeoutMs <= 0)
                timeoutMs = 60000;
            int sampleMs = 250;

            MethodInfo seek = FindSeekMethod(pb);
            PropertyInfo seekProp = seek == null ? WritableStaticDate(pb, "NowEst") : null;
            if (seek == null && seekProp == null)
                return PbError(id, action, "NOSEEK",
                               "neither a Reset/Seek(DateTime) method nor a writable NowEst on "
                               + "PlaybackAdapter — run `playbackctl --api` to see what this build has");

            // ⛔ RANGE-CHECK BEFORE SEEKING — found by driving it on a real transport.
            //    Writing NowEst sets the clock and validates NOTHING. A seek to 2026-05-01 against a
            //    range loaded 04-19..04-24 reported `succeeded: true, offset 0` — the clock really was
            //    where it was asked to go, and there is no data there. A bake started from that
            //    position produces nothing while every check reads green: a success-shaped nothing,
            //    the same family as a transport reading 2099-12-01 that passed a "ready" probe.
            //    So this fails CLOSED, and --force is the only way past it.
            bool force = ExtractJsonString(text, "force") == "true";
            DateTime rFrom = default(DateTime), rTo = default(DateTime);
            bool haveRange = false;
            try
            {
                PropertyInfo pf = pb.GetProperty("FromEst", BFStatic);
                PropertyInfo pt = pb.GetProperty("ToEst", BFStatic);
                if (pf != null && pt != null)
                {
                    object vf = pf.GetValue(null, null), vt = pt.GetValue(null, null);
                    if (vf is DateTime && vt is DateTime)
                    {
                        rFrom = (DateTime)vf; rTo = (DateTime)vt;
                        haveRange = rFrom > new DateTime(1900, 1, 1) && rTo > rFrom;
                    }
                }
            }
            catch (Exception ex) { LogSafe("seek range read: " + ex.Message); }

            if (haveRange && (target < rFrom || target > rTo) && !force)
                return PbError(id, action, "OUTOFRANGE",
                               "target " + target.ToString("o") + " is OUTSIDE the loaded replay range "
                               + rFrom.ToString("o") + " .. " + rTo.ToString("o")
                               + " — the clock would move there and find no data, which reads as a "
                               + "successful seek and produces an empty run. Pass --force if you mean it.");

            DateTime? start = ReadClock(piNowEst);
            string invoked;
            try
            {
                if (seek != null) { seek.Invoke(null, new object[] { target }); invoked = seek.Name + "(DateTime)"; }
                else { seekProp.SetValue(null, target, null); invoked = "NowEst = target"; }
            }
            catch (Exception ex) { return PbError(id, action, "SEEK", Explain(ex)); }

            // ---- poll until the clock SETTLES. Never judge a moving clock. ----
            var traj = new List<DateTime>();
            DateTime? last = null;
            int stableMs = 0, waited = 0;
            bool reached = false;
            while (waited < timeoutMs)
            {
                Thread.Sleep(sampleMs);
                waited += sampleMs;
                DateTime? now = ReadClock(piNowEst);
                if (!now.HasValue) continue;
                if (traj.Count == 0 || traj[traj.Count - 1] != now.Value)
                {
                    if (traj.Count < 200) traj.Add(now.Value);
                }
                if (last.HasValue && now.Value == last.Value) stableMs += sampleMs;
                else stableMs = 0;
                last = now;
                // Landing exactly on target is a finish; otherwise wait for the clock to stop moving.
                if (Math.Abs((now.Value - target).TotalSeconds) < 1.0) { reached = true; break; }
                if (stableMs >= settleMs) break;
            }

            DateTime? landed = ReadClock(piNowEst);
            double offsetSec = landed.HasValue ? (landed.Value - target).TotalSeconds : double.NaN;
            bool onTarget = reached || (landed.HasValue && Math.Abs(offsetSec) < 60.0);

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"action\":\"seek\"")
              .Append(",\"target\":").Append(JsonStr(target.ToString("o")))
              .Append(",\"clockBefore\":").Append(start.HasValue ? JsonStr(start.Value.ToString("o")) : "null")
              .Append(",\"landedAt\":").Append(landed.HasValue ? JsonStr(landed.Value.ToString("o")) : "null")
              .Append(",\"offsetSec\":").Append(double.IsNaN(offsetSec) ? "null" : offsetSec.ToString("0.##", InvCi))
              .Append(",\"via\":").Append(JsonStr(invoked))
              // The loaded range travels with every seek result: "landed on target" means nothing
              // without it, which is the whole reason the range check above exists.
              .Append(",\"rangeFrom\":").Append(haveRange ? JsonStr(rFrom.ToString("o")) : "null")
              .Append(",\"rangeTo\":").Append(haveRange ? JsonStr(rTo.ToString("o")) : "null")
              .Append(",\"inRange\":").Append(!haveRange ? "null"
                    : ((target >= rFrom && target <= rTo) ? "true" : "false"))
              .Append(",\"forced\":").Append(force ? "true" : "false")
              .Append(",\"settledAfterMs\":").Append(waited.ToString(InvCi))
              .Append(",\"settleMs\":").Append(settleMs.ToString(InvCi))
              .Append(",\"timedOut\":").Append(waited >= timeoutMs ? "true" : "false")
              .Append(",\"succeeded\":").Append(onTarget ? "true" : "false")
              // ⭐ The trajectory is the evidence. A seek that walks toward the target and stops short
              // looks IDENTICAL to a seek that never moved if you only report the final position —
              // and that indistinguishability is precisely what cost a day.
              .Append(",\"trajectory\":[");
            for (int i = 0; i < traj.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(JsonStr(traj[i].ToString("o")));
            }
            sb.Append("],\"samples\":").Append(traj.Count.ToString(InvCi))
              .Append(",\"verdict\":").Append(JsonStr(
                    onTarget ? "landed within a minute of target after " + waited + "ms"
                  : waited >= timeoutMs
                        ? "TIMED OUT after " + waited + "ms still " + offsetSec.ToString("0", InvCi)
                          + "s from target — the clock was still moving when we stopped watching"
                        : "SETTLED SHORT: stopped " + offsetSec.ToString("0", InvCi)
                          + "s from target and stayed there for " + settleMs + "ms"))
              .Append(",\"errors\":[]}");
            return sb.ToString();
        }

        // ⭐ FOUND BY RUNNING `--api` RATHER THAN BY ASSUMING: this build exposes NO Reset(DateTime)
        // seek method at all. What it has is a WRITABLE STATIC PROPERTY, `NowEst` — setting the clock
        // IS the seek. The method names were the guess; the property is the fact. Both paths are kept
        // (a method wins if a build has one) and the response reports which was used, so the day a
        // build changes this it says so instead of silently doing nothing.
        private static MethodInfo FindSeekMethod(Type pb)
        {
            string[] names = { "Reset", "Seek", "SeekTo", "SetPlaybackTime" };
            foreach (string n in names)
            {
                try
                {
                    MethodInfo mi = pb.GetMethod(n, BFStatic, null, new[] { typeof(DateTime) }, null);
                    if (mi != null) return mi;
                }
                catch { }
            }
            return null;
        }

        private static PropertyInfo WritableStaticDate(Type pb, string name)
        {
            try
            {
                PropertyInfo p = pb.GetProperty(name, BFStatic);
                if (p != null && p.PropertyType == typeof(DateTime) && p.CanWrite) return p;
            }
            catch { }
            return null;
        }

        // Read-only discovery. When one of these members moves in an NT build, this is what turns
        // "the seek broke" into "SetPlaybackTime is gone and Reset now takes two arguments".
        private string PbApi(string id, Type pb)
        {
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"action\":\"api\",\"succeeded\":true")
              .Append(",\"transport\":").Append(JsonStr(pb.FullName))
              .Append(",\"seekMethods\":[");
            bool first = true;
            try
            {
                foreach (MethodInfo mi in pb.GetMethods(BFStatic))
                {
                    ParameterInfo[] ps;
                    try { ps = mi.GetParameters(); } catch { continue; }
                    if (ps.Length != 1 || ps[0].ParameterType != typeof(DateTime)) continue;
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append(JsonStr(mi.Name + "(DateTime)"));
                }
            }
            catch { }
            // The transport's own writable DateTime statics. On the build this was written against
            // these — NowEst / FromEst / ToEst — are the real seek and range, and no Reset(DateTime)
            // exists at all. Reporting them by name is what makes the next build's change legible.
            sb.Append("],\"writableDateProperties\":[");
            first = true;
            try
            {
                foreach (PropertyInfo p in pb.GetProperties(BFStatic))
                {
                    if (p.PropertyType != typeof(DateTime) || !p.CanWrite) continue;
                    object v = null;
                    try { v = p.CanRead ? p.GetValue(null, null) : null; } catch { }
                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"name\":").Append(JsonStr(p.Name))
                      .Append(",\"value\":").Append(v is DateTime ? JsonStr(((DateTime)v).ToString("o")) : "null")
                      .Append("}");
                }
            }
            catch { }
            sb.Append("],\"connectOptionsDateTimes\":[");
            first = true;
            try
            {
                var conn = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                object c = conn != null ? conn.GetValue(null) : null;
                object opts = c != null ? FirstMember(c, new[] { "Options", "ConnectOptions" }) : null;
                if (opts != null)
                {
                    foreach (PropertyInfo p in opts.GetType().GetProperties(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (p.PropertyType != typeof(DateTime)) continue;
                        object v = null;
                        try { v = p.GetValue(opts, null); } catch { }
                        if (!first) sb.Append(",");
                        first = false;
                        // The NAMES here are obfuscated on purpose by NT; the TYPE is not, which is why
                        // this locates them by type and reports the value so a human can tell which
                        // is Start and which is End.
                        sb.Append("{\"name\":").Append(JsonStr(p.Name))
                          .Append(",\"canWrite\":").Append(p.CanWrite ? "true" : "false")
                          .Append(",\"value\":").Append(v is DateTime ? JsonStr(((DateTime)v).ToString("o")) : "null")
                          .Append("}");
                    }
                }
            }
            catch (Exception ex) { LogSafe("PbApi options: " + ex.Message); }
            sb.Append("],\"errors\":[]}");
            return sb.ToString();
        }

        private string PbSetRange(string id, string text, bool confirm)
        {
            string fromS = ExtractJsonString(text, "start");
            string toS = ExtractJsonString(text, "end");
            DateTime from, to;
            if (string.IsNullOrEmpty(fromS) || !DateTime.TryParse(fromS, InvCi, DateTimeStyles.None, out from)
                || string.IsNullOrEmpty(toS) || !DateTime.TryParse(toS, InvCi, DateTimeStyles.None, out to))
                return PbError(id, "range", "BADRANGE", "range requires --start and --end as date-times");
            if (to <= from)
                return PbError(id, "range", "BADRANGE", "--end must be after --start");
            if (!confirm)
                return PbError(id, "range", "NOCONFIRM",
                               "changing the replay range re-points every bake on this box and requires --confirm");

            // ⭐ `--api` on a live box showed the range is NOT hidden behind the obfuscated
            // ConnectOptions members after all: PlaybackAdapter exposes writable statics named
            // FromEst / ToEst. Prefer those. The by-type ConnectOptions hunt is kept only as a
            // fallback for a build that lacks them, because that was the documented shape once.
            Type pbT;
            try { pbT = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false); }
            catch (Exception ex) { return PbError(id, "range", "REFLECT", ex.Message); }

            object host = null;
            PropertyInfo pStart = null, pEnd = null;
            if (pbT != null)
            {
                pStart = WritableStaticDate(pbT, "FromEst");
                pEnd = WritableStaticDate(pbT, "ToEst");
            }
            if (pStart == null || pEnd == null)
            {
                object opts = null;
                try
                {
                    var conn = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                    object c = conn != null ? conn.GetValue(null) : null;
                    opts = c != null ? FirstMember(c, new[] { "Options", "ConnectOptions" }) : null;
                }
                catch (Exception ex) { return PbError(id, "range", "REFLECT", ex.Message); }
                if (opts == null)
                    return PbError(id, "range", "NORANGE",
                                   "no FromEst/ToEst on PlaybackAdapter and no playback connection "
                                   + "options to fall back to — run `playbackctl --api`");
                // Located by TYPE: those names are obfuscated and a name match would break on the
                // next build without saying why.
                var dts = new List<PropertyInfo>();
                foreach (PropertyInfo p in opts.GetType().GetProperties(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    if (p.PropertyType == typeof(DateTime) && p.CanWrite && p.CanRead) dts.Add(p);
                if (dts.Count != 2)
                    return PbError(id, "range", "AMBIGUOUS",
                                   "expected exactly 2 writable DateTime properties on ConnectOptions, "
                                   + "found " + dts.Count + " — run `playbackctl --api`");
                // Order by their CURRENT values; reflection order is not guaranteed but the values
                // are self-describing.
                pStart = dts[0]; pEnd = dts[1];
                try
                {
                    var v0 = (DateTime)dts[0].GetValue(opts, null);
                    var v1 = (DateTime)dts[1].GetValue(opts, null);
                    if (v1 < v0) { pStart = dts[1]; pEnd = dts[0]; }
                }
                catch { }
                host = opts;
            }

            DateTime b0 = default(DateTime), b1 = default(DateTime);
            try { b0 = (DateTime)pStart.GetValue(host, null); b1 = (DateTime)pEnd.GetValue(host, null); } catch { }
            try
            {
                pStart.SetValue(host, from, null);
                pEnd.SetValue(host, to, null);
            }
            catch (Exception ex) { return PbError(id, "range", "WRITE", Explain(ex)); }

            DateTime a0 = default(DateTime), a1 = default(DateTime);
            bool readBack = true;
            try { a0 = (DateTime)pStart.GetValue(host, null); a1 = (DateTime)pEnd.GetValue(host, null); }
            catch { readBack = false; }
            bool ok = readBack && a0 == from && a1 == to;

            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"action\":\"range\""
                 + ",\"startProperty\":" + JsonStr(pStart.Name)
                 + ",\"endProperty\":" + JsonStr(pEnd.Name)
                 + ",\"startBefore\":" + JsonStr(b0.ToString("o"))
                 + ",\"endBefore\":" + JsonStr(b1.ToString("o"))
                 + ",\"startAfter\":" + (readBack ? JsonStr(a0.ToString("o")) : "null")
                 + ",\"endAfter\":" + (readBack ? JsonStr(a1.ToString("o")) : "null")
                 + ",\"succeeded\":" + (ok ? "true" : "false")
                 + ",\"verdict\":" + JsonStr(ok
                        ? "range set — RECONNECT Playback for it to take effect, then re-read with `playback`"
                        : "THE WRITE RESOLVED BUT THE RANGE DID NOT READ BACK — treat it as unchanged")
                 + ",\"errors\":[]}";
        }

        private static string PbError(string id, string action, string code, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"action\":" + JsonStr(action)
                 + ",\"succeeded\":false,\"errors\":[{\"file\":\"\",\"line\":0,\"code\":" + JsonStr(code)
                 + ",\"message\":" + JsonStr(message) + "}]}";
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        //  chart — list charts, and attach/remove INDICATORS on them
        //
        //  WHY
        //    `strategy add` alone does not reproduce a cell. A Sentinel strategy reads its context from
        //    sensor indicators that must be ON THE SAME CHART — a chart-derived sensor computes from its
        //    own chart's bars, so an off-chart one votes on a chart it cannot see. Staging a cell on a
        //    second box therefore means placing indicators too, and that was the other half of the
        //    per-box hand-work.
        //
        //  ⛔ WHAT THIS DELIBERATELY DOES NOT DO: CREATE A CHART WINDOW.
        //    Constructing a chart window means building a WPF Window on another UI thread and wiring it
        //    into the workspace, on a platform that is multi-UI-threaded and hosts live order routing.
        //    The risk is real and the value is not: `layout` already moves windows across machines, and
        //    a workspace file already carries the charts themselves. What a workspace CANNOT carry is
        //    the strategy and the indicator set — which is exactly what this and `strategy add` supply.
        //    Closing IS offered, behind --confirm, because it is bounded and reversible by reopening.
        private string RunChart(string id, string text)
        {
            string action = ExtractJsonString(text, "action");
            if (string.IsNullOrEmpty(action)) action = "list";
            string chartQ = ExtractJsonString(text, "chart");
            string typeQ = ExtractJsonString(text, "type");
            string nameQ = ExtractJsonString(text, "name");
            bool confirm = ExtractJsonString(text, "confirm") == "true";
            var prms = ParseParams(text);
            var notes = new List<string>();

            if (action != "list" && action != "addIndicator" && action != "removeIndicator"
                && action != "close" && action != "api" && action != "applyTemplate"
                && action != "dataWindow")
                return ChartError(id, action, "BADACTION",
                                  "action must be list, api, addIndicator, removeIndicator, "
                                  + "applyTemplate, dataWindow or close");

            List<Window> targets = CollectChartWindows(chartQ, notes);

            if (action == "list")
                return ChartListing(id, targets, notes, null);

            // Read-only member discovery. `playbackctl --api` refuted the assumption its seek was
            // written against within a minute of existing; the same question here is "what does this
            // build actually offer for bringing a newly added indicator to life?"
            if (action == "api")
            {
                if (targets.Count == 0)
                    return ChartError(id, action, "NOCHART", "no chart to inspect");
                Window w0 = targets[0];
                string dump = null;
                try
                {
                    var op = w0.Dispatcher.BeginInvoke(new Func<string>(delegate
                    {
                        object cc = FirstMember(w0, new[] { "ActiveChartControl", "ChartControl" });
                        if (cc == null) return "{\"chartControl\":null}";
                        var b = new StringBuilder();
                        b.Append("{\"chartControl\":").Append(JsonStr(cc.GetType().FullName))
                         .Append(",\"noArgMethods\":[");
                        bool f = true;
                        foreach (MethodInfo mi in cc.GetType().GetMethods(
                                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (mi.GetParameters().Length != 0) continue;
                            if (mi.Name.StartsWith("get_", StringComparison.Ordinal)) continue;
                            if (!f) b.Append(","); f = false;
                            b.Append(JsonStr(mi.Name));
                        }
                        b.Append("],\"indicatorMethods\":[");
                        f = true;
                        foreach (MethodInfo mi in cc.GetType().GetMethods(
                                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (mi.Name.IndexOf("Indicator", StringComparison.OrdinalIgnoreCase) < 0
                                && mi.Name.IndexOf("Strategy", StringComparison.OrdinalIgnoreCase) < 0
                                && mi.Name.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) < 0
                                && mi.Name.IndexOf("Refresh", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var ps = mi.GetParameters();
                            var sig = new StringBuilder(mi.Name).Append("(");
                            for (int k = 0; k < ps.Length; k++)
                            { if (k > 0) sig.Append(","); sig.Append(ps[k].ParameterType.Name); }
                            sig.Append(")");
                            if (!f) b.Append(","); f = false;
                            b.Append(JsonStr(sig.ToString()));
                        }
                        // The strategy object itself: what can be READ and, crucially, WRITTEN.
                        // A settable enable-ish property would beat any method call, because a
                        // property write has no async re-apply to race.
                        b.Append("],\"strategyMembers\":[");
                        f = true;
                        try
                        {
                            var slist = FirstMember(cc, new[] { "Strategies" }) as IEnumerable;
                            object s0 = null;
                            if (slist != null) foreach (object o in slist) { if (o != null) { s0 = o; break; } }
                            if (s0 != null)
                            {
                                b.Append(JsonStr("TYPE=" + s0.GetType().FullName)); f = false;
                                foreach (PropertyInfo pi in s0.GetType().GetProperties(
                                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    string pn = pi.Name;
                                    if (pn.IndexOf("Enab", StringComparison.OrdinalIgnoreCase) < 0
                                        && pn.IndexOf("State", StringComparison.OrdinalIgnoreCase) < 0
                                        && pn.IndexOf("Active", StringComparison.OrdinalIgnoreCase) < 0
                                        && pn.IndexOf("ChartBars", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    string val = "?";
                                    try { object v = pi.CanRead ? pi.GetValue(s0, null) : null; val = v == null ? "null" : Convert.ToString(v); }
                                    catch { val = "(threw)"; }
                                    if (!f) b.Append(","); f = false;
                                    b.Append(JsonStr(pn + " : " + pi.PropertyType.Name
                                                     + (pi.CanWrite ? " [RW] = " : " [RO] = ") + val));
                                }
                            }
                        }
                        catch (Exception ex) { if (!f) b.Append(","); b.Append(JsonStr("ERR " + ex.Message)); }

                        b.Append("],\"chartBars\":");
                        try
                        {
                            object cb = FindChartBars(cc);
                            b.Append(JsonStr(cb == null ? null : cb.GetType().FullName));
                        }
                        catch { b.Append("null"); }

                        // ⭐ THE DATA WINDOW, MEASURED RATHER THAN GUESSED.
                        // A chart template serialises DaysBack / RangeType / From / To, so those
                        // property names are known. "Break at EOD" appears in NO template on this
                        // machine, which means the template is NOT where it lives — and a setter
                        // written against a guessed name would resolve to nothing and report
                        // success, the exact failure this command family exists to refuse. So dump
                        // what BarsProperties and Bars actually expose and write the setter against
                        // the answer. Read-only: names, types and writability only.
                        b.Append(",\"barsProperties\":");
                        b.Append(DumpMembers(SafeProp(FindChartBars(cc), "Properties"),
                                             new[] { "DaysBack", "RangeType", "From", "To", "BarsBack",
                                                     "MonthsBack", "Reset", "EOD", "Session", "Break" }));
                        b.Append(",\"bars\":");
                        b.Append(DumpMembers(SafeProp(FindChartBars(cc), "Bars"),
                                             new[] { "Reset", "EOD", "Session", "Break", "TradingDay",
                                                     "Count", "IsResetOnNewTradingDay" }));

                        // ROUND 2 (2026-08-09). Round 1 proved Bars.IsResetOnNewTradingDay is READ-ONLY
                        // and that BarsProperties has no EOD member at all, so "Break at EOD" is set
                        // somewhere else entirely. These three answer the three remaining unknowns:
                        //   barsPeriod        -> where Break-at-EOD actually lives (if anywhere settable)
                        //   chartBarsMethods  -> the reload that makes a historical rebuild happen
                        //   connectionApi     -> how to CONNECT (connections.py is read-only today)
                        // Unfiltered on BarsPeriod deliberately: the round-1 filter is exactly what would
                        // hide a field named something we did not think to guess.
                        b.Append(",\"barsPeriod\":");
                        b.Append(DumpMembers(FirstMember(cc, new[] { "BarsPeriod" }), null));

                        b.Append(",\"chartBarsMethods\":[");
                        f = true;
                        try
                        {
                            object cb2 = FindChartBars(cc);
                            if (cb2 != null)
                                foreach (MethodInfo mi in cb2.GetType().GetMethods(
                                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    if (mi.Name.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) < 0
                                        && mi.Name.IndexOf("Refresh", StringComparison.OrdinalIgnoreCase) < 0
                                        && mi.Name.IndexOf("Load", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    var ps = mi.GetParameters();
                                    var sig = new StringBuilder(mi.Name).Append("(");
                                    for (int k = 0; k < ps.Length; k++)
                                    { if (k > 0) sig.Append(","); sig.Append(ps[k].ParameterType.Name); }
                                    sig.Append(")");
                                    if (!f) b.Append(","); f = false;
                                    b.Append(JsonStr(sig.ToString()));
                                }
                        }
                        catch (Exception ex) { if (!f) b.Append(","); b.Append(JsonStr("ERR " + ex.Message)); }

                        b.Append("],\"connectionApi\":[");
                        f = true;
                        try
                        {
                            Type ct = typeof(NinjaTrader.Cbi.Connection);
                            foreach (MethodInfo mi in ct.GetMethods(
                                         BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance))
                            {
                                if (mi.Name.IndexOf("Connect", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                var ps = mi.GetParameters();
                                var sig = new StringBuilder(mi.IsStatic ? "static " : "").Append(mi.Name).Append("(");
                                for (int k = 0; k < ps.Length; k++)
                                { if (k > 0) sig.Append(","); sig.Append(ps[k].ParameterType.Name); }
                                sig.Append(")");
                                if (!f) b.Append(","); f = false;
                                b.Append(JsonStr(sig.ToString()));
                            }
                            // Where the configured (not yet connected) options live.
                            foreach (PropertyInfo pi in ct.GetProperties(
                                         BindingFlags.Public | BindingFlags.NonPublic
                                       | BindingFlags.Static | BindingFlags.Instance))
                            {
                                if (pi.Name.IndexOf("Option", StringComparison.OrdinalIgnoreCase) < 0
                                    && pi.Name.IndexOf("Status", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                if (!f) b.Append(","); f = false;
                                b.Append(JsonStr("PROP " + pi.Name + " : " + pi.PropertyType.Name));
                            }

                            // ⭐ WHERE DOES THE REPLAY RANGE REALLY LIVE?
                            // Setting FromEst/ToEst on the TRANSPORT half-works: ToEst sticks, FromEst
                            // snaps back to 2025-12-26 across two independent runs (identical to the
                            // second). The obvious reading is that NT rebuilds the transport from the
                            // CONNECTION's own options on connect, so the transport write is a value
                            // with a shorter life than the thing that overwrites it. Dump the Playback
                            // connection's ConnectOptions and find the field that actually owns it —
                            // rather than guessing a fourth property name.
                            foreach (ConnectOptions co in Globals.ConnectOptions)
                            {
                                string nm = SafeStr(delegate { return co.Name; });
                                if (nm == null || nm.IndexOf("Playback", StringComparison.OrdinalIgnoreCase) < 0)
                                    continue;
                                // UNFILTERED on purpose. A name filter can only find fields someone
                                // already guessed — the same mistake that hid "Break at EOD" until
                                // BarsPeriod was dumped whole.
                                if (!f) b.Append(","); f = false;
                                b.Append(JsonStr("PLAYBACKOPT TYPE=" + co.GetType().FullName));
                                foreach (PropertyInfo pi in co.GetType().GetProperties(
                                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    string pn = pi.Name;
                                    string val;
                                    try { object v = pi.CanRead ? pi.GetValue(co, null) : null; val = v == null ? "null" : Convert.ToString(v); }
                                    catch { val = "(threw)"; }
                                    if (!f) b.Append(","); f = false;
                                    b.Append(JsonStr("PLAYBACKOPT " + pn + " : " + pi.PropertyType.Name
                                                     + (pi.CanWrite ? " [RW] = " : " [RO] = ") + val));
                                }
                            }
                        }
                        catch (Exception ex) { if (!f) b.Append(","); b.Append(JsonStr("ERR " + ex.Message)); }
                        b.Append("]");
                        b.Append(",\"_r2\":true");

                        b.Append(",\"windowMethods\":[");
                        f = true;
                        foreach (MethodInfo mi in w0.GetType().GetMethods(
                                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            if (mi.Name.IndexOf("Indicator", StringComparison.OrdinalIgnoreCase) < 0
                                && mi.Name.IndexOf("Reload", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            var ps = mi.GetParameters();
                            var sig = new StringBuilder(mi.Name).Append("(");
                            for (int k = 0; k < ps.Length; k++)
                            { if (k > 0) sig.Append(","); sig.Append(ps[k].ParameterType.Name); }
                            sig.Append(")");
                            if (!f) b.Append(","); f = false;
                            b.Append(JsonStr(sig.ToString()));
                        }
                        b.Append("]}");
                        return b.ToString();
                    }));
                    if (op.Wait(TimeSpan.FromSeconds(15))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed)
                        return ChartError(id, action, "TIMEOUT", "the chart did not answer within 15s");
                    dump = op.Result as string;
                }
                catch (Exception ex) { return ChartError(id, action, "API", ex.Message); }
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"action\":\"api\",\"succeeded\":true"
                     + ",\"api\":" + (dump ?? "null") + ",\"charts\":[],\"notes\":[],\"errors\":[]}";
            }

            if (targets.Count == 0)
                return ChartError(id, action, "NOCHART",
                                  "no chart matching '" + (chartQ ?? "(any)") + "'");
            if (targets.Count > 1)
                return ChartListing(id, targets, notes,
                                    "AMBIGUOUS: " + targets.Count
                                    + " charts match — narrow --chart. Nothing was changed.");
            Window target = targets[0];

            if (action == "close")
            {
                if (!confirm)
                    return ChartError(id, action, "NOCONFIRM", "close requires --confirm");
                string title = null;
                try
                {
                    var op = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                    {
                        string t = null;
                        try { t = target.Title; } catch { }
                        target.Close();
                        return t;
                    }));
                    if (op.Wait(TimeSpan.FromSeconds(20))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed)
                        return ChartError(id, action, "TIMEOUT", "the chart did not answer within 20s");
                    title = op.Result as string;
                }
                catch (Exception ex) { return ChartError(id, action, "CLOSE", ex.Message); }
                // Verify: a Close() that resolved is not a window that went away.
                bool gone = true;
                try
                {
                    var all = Globals.AllWindows;
                    if (all != null)
                        for (int i = 0; i < all.Count; i++) if (ReferenceEquals(all[i], target)) gone = false;
                }
                catch { }
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"action\":\"close\""
                     + ",\"chart\":" + JsonStr(title)
                     + ",\"succeeded\":" + (gone ? "true" : "false")
                     + ",\"verdict\":" + JsonStr(gone ? "chart closed"
                            : "Close() RESOLVED BUT THE WINDOW IS STILL REGISTERED — treat as not closed")
                     + ",\"charts\":[],\"notes\":[],\"errors\":[]}";
            }

            // ---- dataWindow ----
            //
            // HOW MUCH HISTORY the chart loads: RangeType / DaysBack / From / To / BarsBack /
            // MonthsBack. `chart --api` measured all six as [RW] on BarsProperties (on two separate
            // builds), and measured that ChartBars exposes NO reload of its own — so the apply is
            // `Chart.OnDataSeriesChanged`, the same member ChartDataSeriesSwitcher already uses.
            //
            // ⚠ Setting the properties is NOT enough on its own: they describe the window, and the
            // chart only acts on them when the series is re-applied. Every field is therefore READ
            // BACK after the reload and reported before/after, so a value that silently failed to
            // stick is visible instead of assumed.
            if (action == "dataWindow")
            {
                string rangeType = ExtractJsonString(text, "rangeType");
                string daysBack  = ExtractJsonString(text, "daysBack");
                string barsBack  = ExtractJsonString(text, "barsBack");
                string monthsBk  = ExtractJsonString(text, "monthsBack");
                string fromS     = ExtractJsonString(text, "from");
                string toS       = ExtractJsonString(text, "to");
                if (string.IsNullOrEmpty(rangeType) && string.IsNullOrEmpty(daysBack)
                    && string.IsNullOrEmpty(barsBack) && string.IsNullOrEmpty(monthsBk)
                    && string.IsNullOrEmpty(fromS) && string.IsNullOrEmpty(toS))
                    return ChartError(id, action, "NOFIELDS",
                                      "dataWindow needs at least one of --range-type / --days-back / "
                                      + "--bars-back / --months-back / --from / --to");

                string wTitle = null, wBefore = null, wAfter = null;
                var wNotes = new List<string>();
                try
                {
                    var op = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                    {
                        try { wTitle = target.Title; } catch { }
                        object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                        if (cc == null) return "no ChartControl on that window";
                        object cb = FindChartBars(cc);
                        object props = SafeProp(cb, "Properties");
                        if (props == null) return "ChartBars exposes no Properties (BarsProperties)";
                        wBefore = DescribeWindow(props);

                        if (!string.IsNullOrEmpty(rangeType))
                        {
                            // ⭐ AN ENUM ERROR THAT DOES NOT NAME THE ALTERNATIVES IS A DEAD END.
                            //    Enum.Parse says only "Requested value 'X' was not found", which leaves
                            //    the caller guessing at a closed set the process already knows. Listing
                            //    the legal values turns one failed call into the answer.
                            PropertyInfo p = null;
                            try { p = props.GetType().GetProperty("RangeType"); } catch { }
                            if (p == null) wNotes.Add("rangeType: no RangeType property on this build");
                            else
                            {
                                string legal = "";
                                try { legal = string.Join("|", Enum.GetNames(p.PropertyType)); } catch { }
                                try { p.SetValue(props, Enum.Parse(p.PropertyType, rangeType, true), null); }
                                catch { wNotes.Add("rangeType: '" + rangeType + "' is not valid — this build accepts: " + legal); }
                            }
                        }
                        SetIntIfGiven(props, "DaysBack",   daysBack, wNotes);
                        SetIntIfGiven(props, "BarsBack",   barsBack, wNotes);
                        SetIntIfGiven(props, "MonthsBack", monthsBk, wNotes);
                        SetDateIfGiven(props, "From", fromS, wNotes);
                        SetDateIfGiven(props, "To",   toS,   wNotes);

                        // Re-apply the series so the chart acts on the new window.
                        try
                        {
                            object instr = cc.GetType().GetProperty("Instrument").GetValue(cc, null);
                            object bp = cc.GetType().GetProperty("BarsPeriod").GetValue(cc, null);
                            MethodInfo apply = target.GetType().GetMethod("OnDataSeriesChanged",
                                BindingFlags.NonPublic | BindingFlags.Instance);
                            if (apply == null) return "Chart.OnDataSeriesChanged not found on "
                                                    + target.GetType().FullName;
                            apply.Invoke(target, new object[] { cc, instr, bp, false, false, true, null });
                        }
                        catch (Exception ex) { return "OnDataSeriesChanged: " + Explain(ex); }
                        return null;
                    }));
                    if (op.Wait(TimeSpan.FromSeconds(60))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed)
                        return ChartError(id, action, "TIMEOUT",
                                          "the chart's UI thread did not answer within 60s");
                    string err = op.Result as string;
                    if (err != null) return ChartError(id, action, "APPLY", err);
                }
                catch (Exception ex) { return ChartError(id, action, "APPLY", Explain(ex)); }

                // Let the reload settle before reading back — the bars load asynchronously, and a
                // read taken mid-load describes a chart that no longer exists a second later.
                DateTime wdl = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < wdl)
                {
                    bool loading = true;
                    try
                    {
                        var opL = target.Dispatcher.BeginInvoke(new Func<bool>(delegate
                        {
                            object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                            object v = SafeProp(cc, "IsBarsLoading");
                            return v is bool ? (bool)v : false;
                        }));
                        if (opL.Wait(TimeSpan.FromSeconds(10))
                            == System.Windows.Threading.DispatcherOperationStatus.Completed)
                            loading = (bool)opL.Result;
                        else loading = false;
                    }
                    catch { loading = false; }
                    if (!loading) break;
                    try { Thread.Sleep(250); } catch { }
                }
                try
                {
                    var opR = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                    {
                        object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                        return DescribeWindow(SafeProp(FindChartBars(cc), "Properties"));
                    }));
                    if (opR.Wait(TimeSpan.FromSeconds(20))
                        == System.Windows.Threading.DispatcherOperationStatus.Completed)
                        wAfter = opR.Result as string;
                }
                catch { }

                // ⛔ A REJECTED FIELD IS A FAILURE, NOT A FOOTNOTE.
                //    The first version of this returned succeeded=true with the rejection tucked into
                //    `notes`, so `--from not-a-date --days-back 7` applied the 7, dropped the From,
                //    and reported success. A script would never have noticed. If ANY field the caller
                //    asked for did not apply, this fails — the note explains which, the flag decides.
                bool wChanged = wBefore != null && wAfter != null && wBefore != wAfter;
                bool wRejected = wNotes.Count > 0;
                bool wOk = wAfter != null && !wRejected;
                var sbW = new StringBuilder();
                sbW.Append("{\"id\":").Append(JsonStr(id))
                   .Append(",\"status\":\"ok\",\"action\":\"dataWindow\"")
                   .Append(",\"chart\":").Append(JsonStr(wTitle))
                   .Append(",\"before\":").Append(JsonStr(wBefore))
                   .Append(",\"after\":").Append(JsonStr(wAfter))
                   .Append(",\"changed\":").Append(wChanged ? "true" : "false")
                   .Append(",\"rejectedFields\":").Append(wNotes.Count.ToString(InvCi))
                   .Append(",\"succeeded\":").Append(wOk ? "true" : "false")
                   .Append(",\"verdict\":").Append(JsonStr(
                        wAfter == null ? "could not read the window back — treat as NOT applied"
                      : wRejected ? "PARTIALLY APPLIED — " + wNotes.Count + " requested field(s) were "
                                    + "REJECTED and are unchanged; the rest took (" + wAfter
                                    + "). Treat as NOT applied and fix the input."
                      : wChanged ? "data window applied — " + wBefore + "  ->  " + wAfter
                                 : "THE CALL RESOLVED BUT THE WINDOW IS UNCHANGED (" + wAfter
                                   + ") — the values did not stick"))
                   .Append(",\"notes\":[");
                for (int i = 0; i < wNotes.Count; i++) { if (i > 0) sbW.Append(","); sbW.Append(JsonStr(wNotes[i])); }
                sbW.Append("],\"charts\":[],\"errors\":[]}");
                return sbW.ToString();
            }

            // ---- applyTemplate ----
            //
            // WHY: `addIndicator` attaches an indicator but CANNOT bring it to life — measured, and
            // said plainly in this file's own verdict text: Indicators.Add + SetState(Active) +
            // RefreshIndicators(true,true) + RefreshAllBars() leaves it at `Configure` while its
            // neighbours sit at Realtime, and the only advice we could offer was "re-add it from the
            // UI". That is the whole reason a chart cell still needed hands.
            //
            // A chart template already contains the answer: NT serialises its indicators, WITH every
            // parameter, as <Indicator Name="fully.qualified.Type" ...> — and `chart --api` shows this
            // build exposes BOTH `TemplateLoadIndicators(XElement)` and `LoadIndicatorsFromXml(XElement)`.
            // So instead of poking the collection, hand NT the XML it wrote and let its own loader run
            // the state machine. That is the path Apply-Template uses from the UI, and it demonstrably
            // produces RUNNING indicators.
            //
            // ⚠ WHICH ELEMENT EACH METHOD WANTS IS NOT DOCUMENTED, so this does not guess: it TRIES
            // the candidates in order and reports which one actually moved the count (`via`). An
            // attempt that resolves and changes nothing is not success.
            if (action == "applyTemplate")
            {
                string path = ExtractJsonString(text, "path");
                if (string.IsNullOrEmpty(path))
                    return ChartError(id, action, "NOPATH",
                                      "applyTemplate requires --template (a chart template .xml path "
                                      + "ON THE NT MACHINE)");
                if (!File.Exists(path))
                    return ChartError(id, action, "NOFILE", "no such template file: " + path);

                XElement root;
                try { root = XElement.Load(path); }
                catch (Exception ex)
                { return ChartError(id, action, "BADXML", "could not parse " + path + ": " + ex.Message); }

                // The <Indicators> node, wherever it sits in the document.
                XElement indsEl = null;
                try
                {
                    if (root.Name.LocalName == "Indicators") indsEl = root;
                    else foreach (XElement d in root.Descendants())
                        if (d.Name.LocalName == "Indicators") { indsEl = d; break; }
                }
                catch { }
                int declared = 0;
                try { if (indsEl != null) foreach (XElement c in indsEl.Elements()) declared++; }
                catch { }
                if (indsEl == null || declared == 0)
                    return ChartError(id, action, "NOINDICATORS",
                                      "the template declares no <Indicators> — nothing to apply ("
                                      + path + ")");

                string chartTitle2 = null, via2 = null, statesAfter = null;
                int before2 = 0, after2 = 0;
                var tried = new List<string>();
                try
                {
                    XElement rootC = root, indsC = indsEl;
                    var op = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                    {
                        try { chartTitle2 = target.Title; } catch { }
                        object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                        if (cc == null) return "no ChartControl on that window";
                        object coll = FirstMember(cc, new[] { "Indicators" });
                        var en = coll as IEnumerable;
                        if (en != null) foreach (object o in en) if (o != null) before2++;

                        // (method, element) candidates, most-principled first.
                        string[] names = { "TemplateLoadIndicators", "LoadIndicatorsFromXml" };
                        XElement[] els = { rootC, indsC };
                        foreach (string mn in names)
                        {
                            MethodInfo mi = null;
                            foreach (MethodInfo cand in cc.GetType().GetMethods(
                                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                            {
                                if (cand.Name != mn) continue;
                                var ps = cand.GetParameters();
                                if (ps.Length == 1 && ps[0].ParameterType == typeof(XElement)) { mi = cand; break; }
                            }
                            if (mi == null) { tried.Add(mn + ": not on this build"); continue; }
                            foreach (XElement el in els)
                            {
                                if (el == null) continue;
                                string label = mn + "(" + el.Name.LocalName + ")";
                                try { mi.Invoke(cc, new object[] { new XElement(el) }); }
                                catch (Exception ex) { tried.Add(label + ": " + Explain(ex)); continue; }
                                int n = 0;
                                var en2 = FirstMember(cc, new[] { "Indicators" }) as IEnumerable;
                                if (en2 != null) foreach (object o in en2) if (o != null) n++;
                                if (n > before2) { after2 = n; return label; }
                                tried.Add(label + ": resolved but count stayed " + n);
                            }
                        }
                        var en3 = FirstMember(cc, new[] { "Indicators" }) as IEnumerable;
                        after2 = 0; if (en3 != null) foreach (object o in en3) if (o != null) after2++;
                        return null;
                    }));
                    if (op.Wait(TimeSpan.FromSeconds(60))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed)
                        return ChartError(id, action, "TIMEOUT",
                                          "the chart's UI thread did not answer within 60s — verify "
                                          + "with `chart --list` before assuming anything");
                    via2 = op.Result as string;
                }
                catch (Exception ex) { return ChartError(id, action, "APPLY", Explain(ex)); }

                // ⭐⭐ LOADED IS NOT RUNNING — measured, twice now, one state apart.
                // `addIndicator` leaves an indicator at `Configure`; TemplateLoadIndicators leaves it
                // at `SetDefaults`. In both cases the objects are in the collection and NT has simply
                // not driven them through the rest of the state machine. `chart --api` named the
                // no-arg members that plausibly do, so try them IN ORDER and stop at the first that
                // actually produces running indicators — reporting which one it was, because the
                // answer is the useful artefact here, not the fact that something eventually worked.
                bool moved = after2 > before2;
                var ladder = new List<string>();
                if (moved)
                {
                    // ⛔ `RefreshAllBars` IS A TRAP HERE — MEASURED, NOT SUSPECTED. In the ladder that
                    //    found this, ApplyNinjaScripts reached 2 running and RefreshIndicators 3, and
                    //    then RefreshAllBars knocked every one of them back to 0. It is deliberately
                    //    NOT in this sequence. (Note: `addIndicator` above still calls it as a
                    //    fallback — that is suspect for the same reason and wants re-testing.)
                    foreach (string sn in new[] { "ApplyNinjaScripts", "RefreshIndicators2" })
                    {
                        string stepName = sn;
                        string how;
                        try
                        {
                            var opA = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                            {
                                object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                                if (cc == null) return "no ChartControl";
                                try
                                {
                                    if (stepName == "RefreshIndicators2")
                                    {
                                        MethodInfo ri = cc.GetType().GetMethod("RefreshIndicators",
                                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                            null, new[] { typeof(bool), typeof(bool) }, null);
                                        if (ri == null) return "not on this build";
                                        ri.Invoke(cc, new object[] { true, true });
                                    }
                                    else
                                    {
                                        MethodInfo m = NoArgMethod(cc.GetType(), stepName);
                                        if (m == null) return "not on this build";
                                        m.Invoke(cc, null);
                                    }
                                }
                                catch (Exception ex) { return "threw: " + Explain(ex); }
                                return "invoked";
                            }));
                            how = opA.Wait(TimeSpan.FromSeconds(45))
                                    == System.Windows.Threading.DispatcherOperationStatus.Completed
                                  ? (opA.Result as string) : "timed out";
                        }
                        catch (Exception ex) { how = "dispatcher failed: " + ex.Message; }
                        ladder.Add(sn + ": " + how);
                    }

                    // ⭐ ACTIVATION IS ASYNCHRONOUS. An indicator climbs
                    //   SetDefaults -> Configure -> DataLoaded -> Historical -> Realtime as the chart
                    //   loads bars, so counting once, immediately, samples a state mid-transition and
                    //   reports a half-built chart as a failure. The first version of this did exactly
                    //   that and read 3 of 14. Poll to a SETTLED value instead: stop as soon as all are
                    //   live, and otherwise keep going until the count stops improving.
                    int settleMs = 60000;
                    try { int w; if (int.TryParse(ExtractJsonString(text, "activateWaitMs"), out w) && w > 0) settleMs = w; }
                    catch { }
                    DateTime adl = DateTime.UtcNow.AddMilliseconds(settleMs);
                    int best = -1, stalls = 0;
                    while (DateTime.UtcNow < adl)
                    {
                        int r = -1;
                        try
                        {
                            var opP = target.Dispatcher.BeginInvoke(new Func<int>(delegate
                            {
                                object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                                int n = 0;
                                var en = FirstMember(cc, new[] { "Indicators" }) as IEnumerable;
                                if (en != null)
                                    foreach (object o in en)
                                    {
                                        if (o == null) continue;
                                        string st = Convert.ToString(FirstMember(o, new[] { "State" }));
                                        if (st == "Active" || st == "Realtime" || st == "Historical"
                                            || st == "Transition") n++;
                                    }
                                return n;
                            }));
                            if (opP.Wait(TimeSpan.FromSeconds(10))
                                == System.Windows.Threading.DispatcherOperationStatus.Completed)
                                r = (int)opP.Result;
                        }
                        catch { }
                        if (r >= after2) { best = r; break; }
                        if (r > best) { best = r; stalls = 0; }
                        else if (++stalls >= 12) break;   // ~6s with no further progress
                        try { Thread.Sleep(500); } catch { }
                    }
                    ladder.Add("settled at " + best + "/" + after2 + " running");
                }

                // ATTACHED IS NOT RUNNING. Read every indicator's State back and require that the
                // ones we added are live, not parked at SetDefaults/Configure — that distinction is
                // the entire point of this command existing.
                int runCount = 0; var stateList = new List<string>();
                try
                {
                    var op2 = target.Dispatcher.BeginInvoke(new Func<int>(delegate
                    {
                        int r = 0;
                        object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                        var en = FirstMember(cc, new[] { "Indicators" }) as IEnumerable;
                        if (en != null)
                            foreach (object o in en)
                            {
                                if (o == null) continue;
                                string st = Convert.ToString(FirstMember(o, new[] { "State" }));
                                stateList.Add(SafeTypeName(o) + "=" + st);
                                if (st == "Active" || st == "Realtime" || st == "Historical" || st == "Transition") r++;
                            }
                        return r;
                    }));
                    if (op2.Wait(TimeSpan.FromSeconds(20))
                        == System.Windows.Threading.DispatcherOperationStatus.Completed)
                        runCount = (int)op2.Result;
                }
                catch { }
                statesAfter = string.Join(", ", stateList.ToArray());

                var sb2 = new StringBuilder();
                sb2.Append("{\"id\":").Append(JsonStr(id))
                   .Append(",\"status\":\"ok\",\"action\":\"applyTemplate\"")
                   .Append(",\"chart\":").Append(JsonStr(chartTitle2))
                   .Append(",\"template\":").Append(JsonStr(path))
                   .Append(",\"declaredInTemplate\":").Append(declared.ToString(InvCi))
                   .Append(",\"indicatorsBefore\":").Append(before2.ToString(InvCi))
                   .Append(",\"indicatorsAfter\":").Append(after2.ToString(InvCi))
                   .Append(",\"running\":").Append(runCount.ToString(InvCi))
                   .Append(",\"states\":").Append(JsonStr(statesAfter))
                   .Append(",\"via\":").Append(JsonStr(via2))
                   .Append(",\"succeeded\":").Append(moved && runCount >= after2 ? "true" : "false")
                   .Append(",\"verdict\":").Append(JsonStr(
                        !moved
                          ? "NOTHING WAS LOADED — indicator count stayed at " + before2
                            + ". Every candidate resolved and changed nothing; treat as NOT applied."
                          : runCount < after2
                            ? "LOADED BUT " + (after2 - runCount) + " OF " + after2
                              + " ARE NOT RUNNING — they will not compute (" + statesAfter + ")"
                            : "applied via " + via2 + " — indicators " + before2 + " -> " + after2
                              + ", all " + runCount + " running"))
                   .Append(",\"attempts\":[");
                for (int i = 0; i < tried.Count; i++) { if (i > 0) sb2.Append(","); sb2.Append(JsonStr(tried[i])); }
                sb2.Append("],\"activation\":[");
                for (int i = 0; i < ladder.Count; i++) { if (i > 0) sb2.Append(","); sb2.Append(JsonStr(ladder[i])); }
                sb2.Append("],\"charts\":[],\"notes\":[],\"errors\":[]}");
                return sb2.ToString();
            }

            // ---- addIndicator / removeIndicator ----
            if (action == "addIndicator" && string.IsNullOrEmpty(typeQ))
                return ChartError(id, action, "NOTYPE", "addIndicator requires --type (the CLASS name)");
            if (action == "removeIndicator" && string.IsNullOrEmpty(nameQ))
                return ChartError(id, action, "NONAME", "removeIndicator requires --name");

            Type indType = null;
            if (action == "addIndicator")
            {
                try
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string an = null;
                        try { an = a.GetName().Name; } catch { continue; }
                        if (an == null || an.IndexOf("NinjaTrader", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        Type[] types;
                        try { types = a.GetTypes(); } catch { continue; }
                        foreach (Type t in types)
                        {
                            if (t.IsAbstract || !typeof(IndicatorBase).IsAssignableFrom(t)) continue;
                            if (string.Equals(t.Name, typeQ, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(t.FullName, typeQ, StringComparison.OrdinalIgnoreCase))
                            { indType = t; break; }
                        }
                        if (indType != null) break;
                    }
                }
                catch (Exception ex) { return ChartError(id, action, "TYPESCAN", ex.Message); }
                if (indType == null)
                    return ChartError(id, action, "NOTYPEFOUND",
                                      "no IndicatorBase subclass named '" + typeQ + "' is loaded — use the "
                                      + "CLASS name, not the display Name (a Sentinel tool blanks its Name)");
            }

            string chartTitle = null, via = null, stateAfter = null;
            int countBefore = 0, countAfter = 0;
            try
            {
                Type it = indType;
                string nq = nameQ;
                string act = action;
                var op = target.Dispatcher.BeginInvoke(new Func<string>(delegate
                {
                    try { chartTitle = target.Title; } catch { }
                    object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                    if (cc == null) return "no ChartControl on that window";
                    object coll = FirstMember(cc, new[] { "Indicators" });
                    if (coll == null) return "ChartControl has no Indicators collection";
                    var en = coll as IEnumerable;
                    if (en != null) foreach (object o in en) if (o != null) countBefore++;

                    if (act == "addIndicator")
                    {
                        object inst = Activator.CreateInstance(it);
                        foreach (var kv in prms)
                        {
                            try
                            {
                                PropertyInfo p = it.GetProperty(kv.Key,
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (p == null || !p.CanWrite)
                                { notes.Add("param skipped (not writable): " + kv.Key); continue; }
                                p.SetValue(inst, ConvertToken(kv.Value, p.PropertyType), null);
                            }
                            catch (Exception ex) { notes.Add("param '" + kv.Key + "': " + ex.Message); }
                        }
                        MethodInfo add = coll.GetType().GetMethod("Add");
                        if (add == null) return "the Indicators collection exposes no Add";
                        add.Invoke(coll, new object[] { inst });
                        try { SetNsState(inst, State.Active); }
                        catch (Exception ex) { notes.Add("SetState(Active): " + ex.Message); }

                        // ⭐ ADDING IS NOT RUNNING — measured, not assumed. Indicators.Add followed by
                        // SetState(Active) leaves the indicator at `Configure` with enabled=false,
                        // while the chart's existing indicators sit at Realtime. NT drives the rest of
                        // the state machine when the chart reconfigures its series, so ask it to.
                        // ⭐ `chart --api` named the real member: RefreshIndicators(bool, bool). The
                        // earlier guesses (Reload/Refresh/ReloadNinjaScript, no args) resolved to
                        // nothing at all, which is why an added indicator sat at Configure forever.
                        try
                        {
                            MethodInfo ri = cc.GetType().GetMethod("RefreshIndicators",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                null, new[] { typeof(bool), typeof(bool) }, null);
                            if (ri != null)
                            {
                                ri.Invoke(cc, new object[] { true, true });
                                notes.Add("ChartControl.RefreshIndicators(true,true) called");
                            }
                            else notes.Add("RefreshIndicators(bool,bool) did not resolve on this build");
                        }
                        // `Explain`, not `ex.Message`: MethodInfo.Invoke wraps the real fault in a
                        // TargetInvocationException whose message is the content-free "Exception has
                        // been thrown by the target of an invocation." This note read exactly that
                        // until now, which is a diagnostic that tells you nothing.
                        catch (Exception ex) { notes.Add("RefreshIndicators: " + Explain(ex)); }

                        object sv = FirstMember(inst, new[] { "State" });
                        stateAfter = sv != null ? Convert.ToString(sv) : null;
                        // A second pass costs nothing and covers a build where one refresh only
                        // schedules the work. Still judged by the State that comes back, never by
                        // the call having returned.
                        if (!(stateAfter == "Active" || stateAfter == "Realtime"
                              || stateAfter == "Historical" || stateAfter == "Transition"))
                        {
                            try
                            {
                                MethodInfo rb = NoArgMethod(cc.GetType(), "RefreshAllBars");
                                if (rb != null)
                                {
                                    rb.Invoke(cc, null);
                                    notes.Add("ChartControl.RefreshAllBars() called as a second pass");
                                    object sv2 = FirstMember(inst, new[] { "State" });
                                    stateAfter = sv2 != null ? Convert.ToString(sv2) : stateAfter;
                                }
                            }
                            catch (Exception ex) { notes.Add("RefreshAllBars: " + ex.Message); }
                        }
                    }
                    else
                    {
                        object victim = null;
                        var en2 = coll as IEnumerable;
                        if (en2 != null)
                            foreach (object o in en2)
                            {
                                if (o == null) continue;
                                string nm = null;
                                try { nm = Convert.ToString(FirstMember(o, new[] { "Name" })); } catch { }
                                string ty = SafeTypeName(o);
                                if (string.IsNullOrEmpty(nm)) nm = ty;
                                if ((nm != null && nm.IndexOf(nq, StringComparison.OrdinalIgnoreCase) >= 0)
                                    || (ty != null && ty.IndexOf(nq, StringComparison.OrdinalIgnoreCase) >= 0))
                                { victim = o; break; }
                            }
                        if (victim == null) return "no indicator matching '" + nq + "' on that chart";
                        try { SetNsState(victim, State.Terminated); }
                        catch (Exception ex) { notes.Add("SetState(Terminated): " + ex.Message); }
                        MethodInfo rem = coll.GetType().GetMethod("Remove");
                        if (rem == null) return "the Indicators collection exposes no Remove";
                        rem.Invoke(coll, new object[] { victim });
                    }

                    var en3 = FirstMember(cc, new[] { "Indicators" }) as IEnumerable;
                    if (en3 != null) foreach (object o in en3) if (o != null) countAfter++;
                    return act == "addIndicator" ? "Indicators.Add + SetState(Active)"
                                                 : "SetState(Terminated) + Indicators.Remove";
                }));
                if (op.Wait(TimeSpan.FromSeconds(30))
                    != System.Windows.Threading.DispatcherOperationStatus.Completed)
                    return ChartError(id, action, "TIMEOUT",
                                      "the chart's UI thread did not answer within 30s — verify with "
                                      + "`chart --list` before assuming anything");
                via = op.Result as string;
            }
            catch (Exception ex) { return ChartError(id, action, "APPLY", Explain(ex)); }

            // ⭐⭐ SETTLE BEFORE JUDGING — this read used to happen immediately and LIED.
            // Measured: adding SentinelTrend to a 2-indicator chart reported `2 -> 1`, state
            // `Configure`, verdict "treat this as NOT applied" — while the chart a moment later held
            // all THREE, every one at Realtime. RefreshIndicators tears the collection down and
            // rebuilds it, so a count taken during the rebuild sees a transient that never existed as
            // a real state.
            // ⇒ A FALSE NEGATIVE HERE IS WORSE THAN A FALSE POSITIVE: a caller that believes "not
            //   applied" RETRIES, and the chart ends up with duplicates of the indicator.
            // Same lesson as applyTemplate, and as playbackctl's seek: a state observed once is not a
            // state change. Poll until the count reaches its target and the new indicator is live,
            // then stop; give up only after it stops improving.
            if (action == "addIndicator" || action == "removeIndicator")
            {
                int want = action == "addIndicator" ? countBefore + 1 : countBefore - 1;
                string typeWanted = indType != null ? indType.Name : null;
                DateTime sdl = DateTime.UtcNow.AddSeconds(45);
                int stalls = 0, bestSeen = -1;
                while (DateTime.UtcNow < sdl)
                {
                    int n = -1; string st = null;
                    try
                    {
                        var opS = target.Dispatcher.BeginInvoke(new Func<object[]>(delegate
                        {
                            object cc = FirstMember(target, new[] { "ActiveChartControl", "ChartControl" });
                            int c = 0; string s = null;
                            var en = FirstMember(cc, new[] { "Indicators" }) as IEnumerable;
                            if (en != null)
                                foreach (object o in en)
                                {
                                    if (o == null) continue;
                                    c++;
                                    if (typeWanted != null && SafeTypeName(o) == typeWanted)
                                        s = Convert.ToString(FirstMember(o, new[] { "State" }));
                                }
                            return new object[] { c, s };
                        }));
                        if (opS.Wait(TimeSpan.FromSeconds(10))
                            == System.Windows.Threading.DispatcherOperationStatus.Completed)
                        { var r = (object[])opS.Result; n = (int)r[0]; st = r[1] as string; }
                    }
                    catch { }
                    if (n >= 0) { countAfter = n; if (st != null) stateAfter = st; }
                    bool live = stateAfter == "Active" || stateAfter == "Realtime"
                             || stateAfter == "Historical" || stateAfter == "Transition";
                    if (n == want && (action == "removeIndicator" || live)) break;
                    if (n > bestSeen) { bestSeen = n; stalls = 0; }
                    else if (++stalls >= 16) break;   // ~8s with no further progress
                    try { Thread.Sleep(500); } catch { }
                }
            }

            // Verify by COUNT, not by the call returning — and for an add, ATTACHED IS NOT RUNNING.
            // Measured on a live chart: Indicators.Add + SetState(Active) leaves the indicator at
            // `Configure` with enabled=false while its neighbours sit at Realtime. Reporting that as
            // success is precisely the overstatement this whole family of commands exists to refuse,
            // so the count answers "attached" and the State answers "running", and both must hold.
            bool attached = action == "addIndicator" ? countAfter == countBefore + 1
                                                     : countAfter == countBefore - 1;
            bool running = stateAfter == "Active" || stateAfter == "Realtime"
                        || stateAfter == "Historical" || stateAfter == "Transition";
            bool ok = action == "addIndicator" ? (attached && running) : attached;
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"action\":").Append(JsonStr(action))
              .Append(",\"chart\":").Append(JsonStr(chartTitle))
              .Append(",\"indicatorsBefore\":").Append(countBefore.ToString(InvCi))
              .Append(",\"indicatorsAfter\":").Append(countAfter.ToString(InvCi))
              .Append(",\"stateAfter\":").Append(JsonStr(stateAfter))
              .Append(",\"via\":").Append(JsonStr(via))
              .Append(",\"attached\":").Append(attached ? "true" : "false")
              .Append(",\"running\":").Append(running ? "true" : "false")
              .Append(",\"succeeded\":").Append(ok ? "true" : "false")
              .Append(",\"verdict\":").Append(JsonStr(
                    ok ? action + " took — indicators " + countBefore + " -> " + countAfter
                         + (action == "addIndicator" ? " and it is " + stateAfter : "")
                  : !attached
                        ? "THE CALL RESOLVED BUT THE INDICATOR COUNT WENT " + countBefore + " -> "
                          + countAfter + " — treat this as NOT applied (" + (via ?? "no detail") + ")"
                        : "ATTACHED BUT NOT RUNNING — it is at " + (stateAfter ?? "an unreadable state")
                          + " while a live indicator reads Realtime. It will not compute until the "
                          + "chart configures it; reload the chart, or re-add it from the UI."))
              .Append(",\"charts\":[],\"notes\":[");
            for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(notes[i])); }
            sb.Append("],\"errors\":[]}");
            return sb.ToString();
        }

        // The data window as one comparable line, so before/after is a diff a human can read and a
        // caller can assert on.
        private static string DescribeWindow(object props)
        {
            if (props == null) return null;
            var b = new StringBuilder();
            string[] names = { "RangeType", "DaysBack", "BarsBack", "MonthsBack", "From", "To" };
            foreach (string n in names)
            {
                object v = SafeProp(props, n);
                string s;
                if (v is DateTime) s = ((DateTime)v).ToString("yyyy-MM-dd", InvCi);
                else s = v == null ? "null" : Convert.ToString(v, InvCi);
                if (b.Length > 0) b.Append(" ");
                b.Append(n).Append("=").Append(s);
            }
            return b.ToString();
        }

        // Set an int property only when the caller actually supplied it. A field nobody asked about
        // must not be quietly rewritten to a default — that is how a command grows side effects.
        private static void SetIntIfGiven(object o, string name, string raw, List<string> notes)
        {
            if (string.IsNullOrEmpty(raw)) return;
            int v;
            if (!int.TryParse(raw, NumberStyles.Integer, InvCi, out v))
            { notes.Add(name + ": not an integer: " + raw); return; }
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name);
                if (p == null || !p.CanWrite) { notes.Add(name + ": not writable on this build"); return; }
                p.SetValue(o, v, null);
            }
            catch (Exception ex) { notes.Add(name + ": " + ex.Message); }
        }

        // Dates are parsed CULTURE-INVARIANTLY on purpose: the fleet spans machines, and a window
        // that means one thing on a US box and another on an EU box is a silently different backtest.
        private static void SetDateIfGiven(object o, string name, string raw, List<string> notes)
        {
            if (string.IsNullOrEmpty(raw)) return;
            DateTime v;
            if (!DateTime.TryParse(raw, InvCi, DateTimeStyles.None, out v))
            { notes.Add(name + ": not a date (use yyyy-MM-dd): " + raw); return; }
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name);
                if (p == null || !p.CanWrite) { notes.Add(name + ": not writable on this build"); return; }
                p.SetValue(o, v, null);
            }
            catch (Exception ex) { notes.Add(name + ": " + ex.Message); }
        }

        // Read one property off an object, swallowing everything. Discovery must never be the thing
        // that throws — a dump that dies halfway tells you less than no dump at all.
        private static object SafeProp(object o, string name)
        {
            if (o == null) return null;
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return p != null && p.CanRead ? p.GetValue(o, null) : null;
            }
            catch { return null; }
        }

        // Dump an object's properties as JSON: name, type, [RW]/[RO] and the current value, keeping
        // only members whose name contains one of `filters` (case-insensitive). `null` is emitted for
        // an object that did not resolve — distinct from `[]`, which means "resolved, nothing matched".
        private static string DumpMembers(object o, string[] filters)
        {
            if (o == null) return "null";
            var b = new StringBuilder();
            b.Append("{\"type\":").Append(JsonStr(o.GetType().FullName)).Append(",\"members\":[");
            bool first = true;
            try
            {
                foreach (PropertyInfo pi in o.GetType().GetProperties(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    bool keep = filters == null || filters.Length == 0;
                    if (!keep)
                        foreach (string f in filters)
                            if (pi.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) { keep = true; break; }
                    if (!keep) continue;
                    string val;
                    try
                    {
                        object v = pi.CanRead ? pi.GetValue(o, null) : null;
                        val = v == null ? "null" : Convert.ToString(v);
                    }
                    catch { val = "(threw)"; }
                    if (!first) b.Append(","); first = false;
                    b.Append(JsonStr(pi.Name + " : " + pi.PropertyType.Name
                                     + (pi.CanWrite ? " [RW] = " : " [RO] = ") + val));
                }
            }
            catch (Exception ex) { if (!first) b.Append(","); b.Append(JsonStr("ERR " + ex.Message)); }
            b.Append("]}");
            return b.ToString();
        }

        // ⚠ THE TITLE MUST BE READ ON THE WINDOW'S OWN DISPATCHER.
        //    `Window.Title` is a DependencyProperty, so reading it from the poller thread throws —
        //    and the first version of this swallowed that into `title = null`, after which the
        //    `--chart` filter silently matched the wrong windows. `chart --api --chart NQ` came back
        //    reporting no ChartControl at all, on a box with three working charts. The type name is
        //    safe off-thread (plain reflection); everything else is not.
        private List<Window> CollectChartWindows(string chartQ, List<string> notes)
        {
            var targets = new List<Window>();
            var snap = new List<Window>();
            try
            {
                var all = Globals.AllWindows;
                if (all != null) for (int i = 0; i < all.Count; i++) snap.Add(all[i]);
            }
            catch (Exception ex) { notes.Add("AllWindows: " + ex.Message); }

            foreach (Window w in snap)
            {
                if (w == null) continue;
                string tn;
                try { tn = w.GetType().FullName; } catch { continue; }
                if (tn == null || tn.IndexOf("Chart", StringComparison.Ordinal) < 0) continue;
                if (tn.IndexOf("ChartTrader", StringComparison.Ordinal) >= 0) continue;
                if (string.IsNullOrEmpty(chartQ)) { targets.Add(w); continue; }

                Window win = w;
                try
                {
                    var op = win.Dispatcher.BeginInvoke(new Func<bool>(delegate
                    {
                        string t = null;
                        try { t = win.Title; } catch { }
                        return t != null && t.IndexOf(chartQ, StringComparison.OrdinalIgnoreCase) >= 0;
                    }));
                    if (op.Wait(TimeSpan.FromSeconds(5))
                        != System.Windows.Threading.DispatcherOperationStatus.Completed)
                    { notes.Add("chart did not answer its title within 5s: " + tn); continue; }
                    if (op.Result is bool && (bool)op.Result) targets.Add(win);
                }
                catch (Exception ex) { notes.Add("title read (" + tn + "): " + ex.Message); }
            }
            return targets;
        }

        private string ChartListing(string id, List<Window> targets, List<string> notes, string refusal)
        {
            var rows = new List<string>();
            foreach (Window w in targets)
            {
                Window win = w;
                try
                {
                    var op = win.Dispatcher.BeginInvoke(new Func<string>(delegate { return DescribeChart(win); }));
                    if (op.Wait(TimeSpan.FromSeconds(5))
                        == System.Windows.Threading.DispatcherOperationStatus.Completed)
                    {
                        string row = op.Result as string;
                        if (row != null) rows.Add(row);
                    }
                    else notes.Add("chart did not answer within 5s");
                }
                catch (Exception ex) { notes.Add("chart read: " + ex.Message); }
            }
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":").Append(refusal == null ? "\"ok\"" : "\"error\"")
              .Append(",\"action\":\"list\",\"succeeded\":").Append(refusal == null ? "true" : "false")
              .Append(",\"count\":").Append(rows.Count.ToString(InvCi))
              .Append(",\"charts\":[");
            for (int i = 0; i < rows.Count; i++) { if (i > 0) sb.Append(","); sb.Append(rows[i]); }
            sb.Append("],\"notes\":[");
            for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(notes[i])); }
            sb.Append("]");
            if (refusal != null)
                sb.Append(",\"errors\":[{\"file\":\"\",\"line\":0,\"code\":\"REFUSED\",\"message\":")
                  .Append(JsonStr(refusal)).Append("}]}");
            else sb.Append(",\"errors\":[]}");
            return sb.ToString();
        }

        private static string ChartError(string id, string action, string code, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"action\":" + JsonStr(action)
                 + ",\"succeeded\":false,\"count\":0,\"charts\":[],\"notes\":[],"
                 + "\"errors\":[{\"file\":\"\",\"line\":0,\"code\":" + JsonStr(code)
                 + ",\"message\":" + JsonStr(message) + "}]}";
        }

        private static string LogError(string id, string code, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"exists\":false,\"lines\":[],"
                 + "\"matched\":0,\"errors\":[{\"file\":\"\",\"line\":0,\"code\":" + JsonStr(code)
                 + ",\"message\":" + JsonStr(message) + "}]}";
        }

        // All three log families NinjaTrader writes share a `yyyy-MM-dd HH:mm:ss` prefix and differ
        // only in the separator before the milliseconds:
        //   2026-08-04 12:05:44.176  [Sentinel:Council] …      (a tool's own log)
        //   2026-08-04 11:41:22:239 (Tradovate) Cbi.…          (trace\)
        //   2026-08-04 08:33:53:726|1|64|Instrument='NQ 09-26' (log\)
        // One parser therefore covers every log on the box. Stamps are LOCAL time, so the cutoff is
        // built from DateTime.Now — comparing them against UtcNow would silently shift the window by
        // the machine's offset and quietly return the wrong hour.
        private static bool TryParseLogStamp(string line, out DateTime ts)
        {
            ts = default(DateTime);
            if (line == null || line.Length < 19) return false;
            if (!DateTime.TryParseExact(line.Substring(0, 19), "yyyy-MM-dd HH:mm:ss",
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out ts))
                return false;
            if (line.Length >= 23 && (line[19] == '.' || line[19] == ':'))
            {
                int ms;
                if (int.TryParse(line.Substring(20, 3), NumberStyles.None,
                                 CultureInfo.InvariantCulture, out ms))
                    ts = ts.AddMilliseconds(ms);
            }
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════════════════════════
        //  READ-ONLY STATE COMMANDS  (playback · ntstatus · workspace)
        //
        //  WHY THESE EXIST — 2026-08-02, and each one is tied to a failure that actually cost a day.
        //    A Gate 3 equivalence run compared two boxes proven byte-identical in every input we had made
        //    hashable: code (muster), replay .nrd, historical bars, and the chart+strategy blob. It still
        //    diverged. The cause was in none of them — it was the transport state inside a running NT:
        //    one box's replay clock was parked at the range start, the other's was 27 h in and moving, so
        //    the same Reset() call seeked on one and no-opped on the other.
        //
        //    ⭐ THE PATTERN: every input we could verify was a FILE. Everything still diverging lives only
        //    in a running NinjaTrader — no file, no hash, no read-back — and so was set by hand, per box,
        //    at different moments. We were verifying what was easy to verify. These three commands give
        //    that state a read-back, which is the whole prerequisite for ever trusting it.
        //
        //    ⭐ THE SCALING ARGUMENT: the Watch is SIX replay workers. Any input that needs a GUI click per
        //    box cannot be held equal across a matrix, and any check that needs an eye on a screen cannot
        //    be run across one either. Read-only first — these three answer questions, they change nothing.
        //
        //  Each maps to a specific incident:
        //    playback   — the divergence above, and the Playback range silently reverting across restarts
        //    ntstatus   — a STALE DLL ran an entire 33-minute cell while the source on disk said otherwise
        //    workspace  — two runs where the strategy was silently disabled (toggling Playback disables it)
        // ═══════════════════════════════════════════════════════════════════════════════════════════════

        private const BindingFlags BFStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        // Long enough that a real replay clock provably moves (400 ms at 100x is 40 replay-seconds), short
        // enough that a status call stays snappy. Same constant, same reasoning, as the Conductor pre-flight.
        private const int PlaybackSampleMs = 400;
        private static string NtCoreVersion()
        {
            try { return typeof(Connection).Assembly.GetName().Version.ToString(); } catch { return ""; }
        }

        // ── playback ────────────────────────────────────────────────────────────────────────────────────
        //  Connection state + the replay clock + speed + what the .nrd files on disk actually cover.
        //
        //  ⚠ Reflection, not a direct reference — the same reasoning as SentinelConductor: PlaybackAdapter's
        //  members are internal-ish, and NinjaTrader compiles every .cs under bin\Custom into ONE assembly,
        //  so a hard binding would turn any NT API change into a whole-suite compile break. Reflection
        //  degrades to nulls in the JSON instead.
        //
        //  ⭐ `movingSec` is the field this was written for. A single clock reading cannot distinguish a
        //  parked transport from a running one, and that distinction is exactly what broke Gate 3. Two
        //  samples a real gap apart can. REPORT THE OUTCOME, NOT THE INTENT.
        private string RunPlayback(string id, string instrument)
        {
            var sb = new StringBuilder();
            try
            {
                Type pb = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                PropertyInfo piSpeed = pb != null ? pb.GetProperty("PlaybackSpeed", BFStatic) : null;
                PropertyInfo piNowEst = pb != null ? pb.GetProperty("NowEst", BFStatic) : null;
                FieldInfo fiMaxSpeed = pb != null ? pb.GetField("MaxSpeedValue", BFStatic) : null;
                MethodInfo miMinMax = pb != null
                    ? pb.GetMethod("GetReplayMinMaxDates", BFStatic, null,
                        new[] { typeof(string), typeof(DateTime).MakeByRefType(), typeof(DateTime).MakeByRefType() }, null)
                    : null;
                PropertyInfo piConn = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);

                object speed = null, maxSpeed = null;
                try { if (piSpeed != null) speed = piSpeed.GetValue(null); } catch (Exception ex) { LogSafe("playback speed: " + ex.Message); }
                try { if (fiMaxSpeed != null) maxSpeed = fiMaxSpeed.GetValue(null); } catch (Exception ex) { LogSafe("playback maxspeed: " + ex.Message); }

                // Two clock samples, a real gap apart — parked vs running (see above).
                DateTime? c1 = ReadClock(piNowEst);
                Thread.Sleep(PlaybackSampleMs);
                DateTime? c2 = ReadClock(piNowEst);
                double movingSec = (c1.HasValue && c2.HasValue) ? (c2.Value - c1.Value).TotalSeconds : 0.0;

                string connStatus = "none";
                bool connected = false;
                try
                {
                    var c = piConn != null ? piConn.GetValue(null) as Connection : null;
                    if (c != null) { connStatus = c.Status.ToString(); connected = c.Status == ConnectionStatus.Connected; }
                }
                catch (Exception ex) { LogSafe("playback conn: " + ex.Message); }

                sb.Append("{\"id\":").Append(JsonStr(id))
                  .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"transportResolved\":").Append(pb != null ? "true" : "false")
                  .Append(",\"connection\":{\"status\":").Append(JsonStr(connStatus))
                  .Append(",\"connected\":").Append(connected ? "true" : "false").Append("}")
                  .Append(",\"clockEst\":").Append(c2.HasValue ? JsonStr(c2.Value.ToString("o")) : "null")
                  .Append(",\"clockEstFirst\":").Append(c1.HasValue ? JsonStr(c1.Value.ToString("o")) : "null")
                  .Append(",\"sampleMs\":").Append(PlaybackSampleMs.ToString(InvCi))
                  .Append(",\"movingSec\":").Append(movingSec.ToString("0.###", InvCi))
                  .Append(",\"moving\":").Append(movingSec > 2.0 ? "true" : "false")
                  .Append(",\"speed\":").Append(speed is int ? ((int)speed).ToString(InvCi) : "null")
                  .Append(",\"maxSpeedValue\":").Append(maxSpeed is int ? ((int)maxSpeed).ToString(InvCi) : "null");

                // Coverage — what the .nrd files actually contain, from NT's own reader. The Playback
                // slider is NOT this: its bounds are the CONNECTION range you typed, not indexed data,
                // and misreading it as proof of loaded data cost hours on 2026-08-02.
                sb.Append(",\"coverage\":");
                AppendCoverage(sb, miMinMax, instrument);
                sb.Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"code\":\"BRIDGE\",\"message\":"
                     + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        private static DateTime? ReadClock(PropertyInfo piNowEst)
        {
            try { if (piNowEst != null) return (DateTime)piNowEst.GetValue(null); } catch { }
            return null;
        }

        // Per-instrument .nrd spans. `instrument` null/empty => every instrument folder under db\replay.
        private void AppendCoverage(StringBuilder sb, MethodInfo miMinMax, string instrument)
        {
            sb.Append("[");
            if (miMinMax == null) { sb.Append("]"); return; }
            bool firstInstr = true;
            try
            {
                string root = Path.Combine(Globals.UserDataDir, "db", "replay");
                if (!Directory.Exists(root)) { sb.Append("]"); return; }
                string[] dirs = string.IsNullOrEmpty(instrument)
                    ? Directory.GetDirectories(root)
                    : new[] { Path.Combine(root, instrument) };
                foreach (string dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;
                    string[] files = Directory.GetFiles(dir, "*.nrd");
                    Array.Sort(files);
                    DateTime lo = DateTime.MaxValue, hi = DateTime.MinValue;
                    int ok = 0, bad = 0;
                    var days = new StringBuilder();
                    foreach (string f in files)
                    {
                        DateTime a = DateTime.MinValue, b = DateTime.MinValue;
                        bool read = false;
                        try
                        {
                            object[] args = new object[] { f, DateTime.MinValue, DateTime.MinValue };
                            miMinMax.Invoke(null, args);
                            a = (DateTime)args[1]; b = (DateTime)args[2];
                            read = true;
                        }
                        catch { }
                        if (days.Length > 0) days.Append(",");
                        days.Append("{\"file\":").Append(JsonStr(Path.GetFileNameWithoutExtension(f)))
                            .Append(",\"readable\":").Append(read ? "true" : "false")
                            .Append(",\"from\":").Append(read ? JsonStr(a.ToString("o")) : "null")
                            .Append(",\"to\":").Append(read ? JsonStr(b.ToString("o")) : "null")
                            .Append(",\"bytes\":").Append(SafeLen(f).ToString(InvCi)).Append("}");
                        if (read) { ok++; if (a < lo) lo = a; if (b > hi) hi = b; } else bad++;
                    }
                    if (!firstInstr) sb.Append(",");
                    firstInstr = false;
                    sb.Append("{\"instrument\":").Append(JsonStr(Path.GetFileName(dir)))
                      .Append(",\"files\":").Append(files.Length.ToString(InvCi))
                      .Append(",\"readable\":").Append(ok.ToString(InvCi))
                      .Append(",\"unreadable\":").Append(bad.ToString(InvCi))
                      .Append(",\"from\":").Append(ok > 0 ? JsonStr(lo.ToString("o")) : "null")
                      .Append(",\"to\":").Append(ok > 0 ? JsonStr(hi.ToString("o")) : "null")
                      .Append(",\"days\":[").Append(days).Append("]}");
                }
            }
            catch (Exception ex) { LogSafe("AppendCoverage: " + ex.Message); }
            sb.Append("]");
        }

        private static long SafeLen(string f)
        {
            try { return new FileInfo(f).Length; } catch { return -1; }
        }

        // ── ntstatus ────────────────────────────────────────────────────────────────────────────────────
        //  "Is the code NinjaTrader is RUNNING the code that is on disk?"
        //
        //  ⭐ WHY: on 2026-08-02 a box ran Conductor v0.1.0 for 33 minutes while the source on disk said
        //  v0.2.0b — the deploy had copied source without compiling, and every downstream conclusion from
        //  that cell was worthless. The version chip was on screen the whole time and went unread.
        //  A comparison of PROCESS START vs ASSEMBLY BUILD makes the same mistake impossible to miss, and
        //  unlike the chip it can be read across six boxes in one command.
        //
        //  Answered from INSIDE NT deliberately: only this process knows which assembly it actually loaded.
        //  The Python side additionally stats the DLL on disk, so the two can disagree — and that
        //  disagreement IS the finding.
        private string RunNtStatus(string id)
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                DateTime procStart = proc.StartTime.ToUniversalTime();

                string loadedPath = null, loadedVer = null;
                DateTime? loadedBuilt = null;
                try
                {
                    Assembly custom = null;
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string n = a.GetName().Name;
                        if (string.Equals(n, "NinjaTrader.Custom", StringComparison.OrdinalIgnoreCase)) { custom = a; break; }
                    }
                    if (custom != null)
                    {
                        loadedVer = custom.GetName().Version != null ? custom.GetName().Version.ToString() : null;
                        try { loadedPath = custom.Location; } catch { }
                        // An in-memory (byte[]-loaded) assembly has no Location — NT swaps NinjaScript in
                        // that way on a reload, so an empty Location is INFORMATION, not an error.
                        if (!string.IsNullOrEmpty(loadedPath) && File.Exists(loadedPath))
                            loadedBuilt = File.GetLastWriteTimeUtc(loadedPath);
                    }
                }
                catch (Exception ex) { LogSafe("ntstatus assembly: " + ex.Message); }

                string dllOnDisk = Path.Combine(Globals.UserDataDir, "bin", "Custom", "NinjaTrader.Custom.dll");
                DateTime? diskBuilt = File.Exists(dllOnDisk) ? (DateTime?)File.GetLastWriteTimeUtc(dllOnDisk) : null;

                // The finding, stated rather than left for the reader to compute.
                bool stale = diskBuilt.HasValue && diskBuilt.Value > procStart;

                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(JsonStr(id))
                  .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
                  .Append(",\"pid\":").Append(proc.Id.ToString(InvCi))
                  .Append(",\"processStartUtc\":").Append(JsonStr(procStart.ToString("o")))
                  .Append(",\"ntVersion\":").Append(JsonStr(NtCoreVersion()))
                  .Append(",\"userDataDir\":").Append(JsonStr(Globals.UserDataDir))
                  .Append(",\"loadedAssembly\":{\"version\":").Append(JsonStr(loadedVer))
                  .Append(",\"location\":").Append(JsonStr(loadedPath))
                  .Append(",\"inMemory\":").Append(string.IsNullOrEmpty(loadedPath) ? "true" : "false")
                  .Append(",\"builtUtc\":").Append(loadedBuilt.HasValue ? JsonStr(loadedBuilt.Value.ToString("o")) : "null").Append("}")
                  .Append(",\"dllOnDisk\":{\"path\":").Append(JsonStr(dllOnDisk))
                  .Append(",\"builtUtc\":").Append(diskBuilt.HasValue ? JsonStr(diskBuilt.Value.ToString("o")) : "null").Append("}")
                  .Append(",\"assemblyOlderThanDisk\":").Append(stale ? "true" : "false")
                  .Append("}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"code\":\"BRIDGE\",\"message\":"
                     + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
        }

        // ── screenshot ──────────────────────────────────────────────────────────────────────────────────
        //  LET THE OPERATOR SEE THE SCREEN.
        //
        //  ⭐ WHY: 2026-08-02, and this one is a process failure rather than a code failure. A replay would
        //  not seek on one node. Every diagnosis was made by asking the user what was on their screen and
        //  reasoning about the answer — and the reasoning was wrong repeatedly, because a relayed screen is
        //  a lossy channel: the Playback window and the Conductor panel were displaying two DIFFERENT clock
        //  values the whole time and nobody noticed, because nobody was looking at both at once.
        //
        //  A fleet of six headless replay workers cannot be operated through a human describing a window.
        //  ⇒ Capture the pixels and ship them back. This is the read-only sibling of `windows`: that command
        //  says a window EXISTS, this one says what it SAYS.
        //
        //  ⚠ MUST run inside NinjaTrader, not over SSH. SSH lands in session 0, which owns no desktop — a
        //  capture from there returns black, and black is worse than an error because it looks like an answer.
        //  Running in the AddOn means running in the interactive session where the pixels actually are.
        //
        //  ⚠ GDI, not WPF rendering. RenderTargetBitmap would need each window's own dispatcher (NT is
        //  multi-UI-threaded) and would miss child HWNDs and the DWM composition. PrintWindow with
        //  PW_RENDERFULLCONTENT captures what is genuinely on screen, and Win32 is thread-agnostic — the
        //  same reason `windows` enumerates via EnumWindows rather than Globals.AllWindows.
        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        private const int SRCCOPY = 0x00CC0020;

        private string RunScreenshot(string id, string triggerJson)
        {
            string title = ExtractJsonString(triggerJson, "title");
            string hwndStr = ExtractJsonString(triggerJson, "hwnd");
            string outPath = ExtractJsonString(triggerJson, "out");

            try { SetProcessDPIAware(); } catch { }

            IntPtr target = IntPtr.Zero;
            string matched = null;
            long parsed;
            if (!string.IsNullOrEmpty(hwndStr) && long.TryParse(hwndStr, out parsed))
            {
                target = new IntPtr(parsed);
                var tb = new StringBuilder(400);
                GetWindowText(target, tb, tb.Capacity);
                matched = tb.ToString();
            }
            else if (!string.IsNullOrEmpty(title))
            {
                // Substring, case-insensitive — a caller should not have to know a window's exact caption,
                // and NT retitles windows as their content changes.
                var found = new List<KeyValuePair<IntPtr, string>>();
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        if (pid != self || !IsWindow(h) || !IsWindowVisible(h)) return true;
                        var tb2 = new StringBuilder(400);
                        GetWindowText(h, tb2, tb2.Capacity);
                        string t = tb2.ToString();
                        if (t.Length > 0 && t.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                            found.Add(new KeyValuePair<IntPtr, string>(h, t));
                    }
                    catch { }
                    return true;
                };
                EnumWindows(cb, IntPtr.Zero);
                GC.KeepAlive(cb);
                if (found.Count == 0)
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"code\":\"BRIDGE\",\"message\":"
                         + JsonStr("no visible window matching '" + title + "'") + "}]}";
                target = found[0].Key;
                matched = found[0].Value;
            }

            int x = 0, y = 0, w, h2;
            bool fullScreen = target == IntPtr.Zero;
            if (fullScreen)
            {
                // Virtual screen: every monitor, including negative-origin ones.
                x = GetSystemMetrics(76); y = GetSystemMetrics(77);
                w = GetSystemMetrics(78); h2 = GetSystemMetrics(79);
                matched = "(virtual screen)";
            }
            else
            {
                BridgeRect r;
                if (!GetWindowRect(target, out r))
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"code\":\"BRIDGE\",\"message\":\"GetWindowRect failed\"}]}";
                w = r.Right - r.Left; h2 = r.Bottom - r.Top; x = r.Left; y = r.Top;
                // A minimized window has a real HWND and nonsense geometry. Say so rather than return an
                // 8x8 sliver that looks like a failed render.
                if (IsIconic(target) || w <= 0 || h2 <= 0)
                    return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"code\":\"BRIDGE\",\"message\":"
                         + JsonStr("window is minimized or has no area (" + w + "x" + h2 + ") — restore it first") + "}]}";
            }

            if (string.IsNullOrEmpty(outPath))
                outPath = Path.Combine(Globals.UserDataDir, "NT8Bridge", "result", "shot_" + id + ".png");

            IntPtr srcDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                srcDc = fullScreen ? GetDC(IntPtr.Zero) : GetWindowDC(target);
                if (srcDc == IntPtr.Zero) throw new Exception("could not get a device context");
                memDc = CreateCompatibleDC(srcDc);
                bmp = CreateCompatibleBitmap(srcDc, w, h2);
                old = SelectObject(memDc, bmp);

                bool ok = false;
                if (!fullScreen)
                    ok = PrintWindow(target, memDc, PW_RENDERFULLCONTENT);
                if (!ok)
                    // Fallback: blit from the screen DC. Captures whatever is actually visible, so an
                    // occluded window comes back occluded — which is the truth, not a defect.
                    ok = BitBlt(memDc, 0, 0, w, h2, fullScreen ? srcDc : GetDC(IntPtr.Zero), x, y, SRCCOPY);
                if (!ok) throw new Exception("both PrintWindow and BitBlt failed");

                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bmp, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));

                string dir = Path.GetDirectoryName(outPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                    enc.Save(fs);

                return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"ts\":" + JsonStr(DateTime.UtcNow.ToString("o"))
                     + ",\"path\":" + JsonStr(outPath)
                     + ",\"window\":" + JsonStr(matched)
                     + ",\"hwnd\":" + target.ToInt64().ToString(InvCi)
                     + ",\"width\":" + w.ToString(InvCi) + ",\"height\":" + h2.ToString(InvCi)
                     + ",\"bytes\":" + SafeLen(outPath).ToString(InvCi) + "}";
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"errors\":[{\"code\":\"BRIDGE\",\"message\":"
                     + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }
            finally
            {
                try { if (old != IntPtr.Zero) SelectObject(memDc, old); } catch { }
                try { if (bmp != IntPtr.Zero) DeleteObject(bmp); } catch { }
                try { if (memDc != IntPtr.Zero) DeleteDC(memDc); } catch { }
                try { if (srcDc != IntPtr.Zero) ReleaseDC(fullScreen ? IntPtr.Zero : target, srcDc); } catch { }
            }
        }

        // ── workspace ───────────────────────────────────────────────────────────────────────────────────
        //  What is actually ON each chart: instrument, bar type, indicators, strategies + their State.
        //
        //  ⭐ WHY: two Gate 3 runs were lost to a strategy that was silently DISABLED — toggling the
        //  Playback connection disables chart strategies, and on an unattended boot a workspace can restore
        //  with the strategy off. That looks identical to a healthy run until the corpus comes back empty.
        //  "Is my strategy enabled on both boxes?" should be one command, not an RDP session per box.
        //
        //  ⚠ NT IS MULTI-UI-THREADED — every window owns its own dispatcher, and reading a WPF member from
        //  the poller thread throws "the calling thread cannot access this object" for EVERY window. So:
        //  snapshot Globals.AllWindows (a plain collection — safe), then marshal to EACH window's OWN
        //  dispatcher to read it. A short timeout per window so one busy chart cannot wedge the bridge.
        //
        //  ⚠ Read by REFLECTION with the resolved member recorded. NT's chart object model is not part of
        //  the documented NinjaScript surface, so a hard binding is both a compile risk and a silent-wrong
        //  risk across versions. When a member does not resolve the JSON says so rather than reporting an
        //  empty chart — an absent reading and a zero reading are not the same claim.
        private string RunWorkspace(string id)
        {
            var charts = new List<string>();
            var notes = new List<string>();
            try
            {
                var snap = new List<Window>();
                try
                {
                    var all = Globals.AllWindows;
                    if (all != null) for (int i = 0; i < all.Count; i++) snap.Add(all[i]);
                }
                catch (Exception ex) { notes.Add("AllWindows snapshot: " + ex.Message); }

                foreach (Window w in snap)
                {
                    if (w == null) continue;
                    string tn;
                    try { tn = w.GetType().FullName; } catch { continue; }
                    if (tn == null || tn.IndexOf("Chart", StringComparison.Ordinal) < 0) continue;
                    if (tn.IndexOf("ChartTrader", StringComparison.Ordinal) >= 0) continue;

                    Window win = w;
                    string row = null;
                    try
                    {
                        var op = win.Dispatcher.BeginInvoke(new Func<string>(delegate { return DescribeChart(win); }));
                        // Bounded wait: a chart mid-render must not hold the poller. Silence is reported,
                        // not swallowed — a window we could not read is a fact worth returning.
                        if (op.Wait(TimeSpan.FromSeconds(5)) == System.Windows.Threading.DispatcherOperationStatus.Completed)
                            row = op.Result as string;
                        else
                            notes.Add("chart window did not answer within 5s: " + tn);
                    }
                    catch (Exception ex) { notes.Add("chart read (" + tn + "): " + ex.Message); }
                    if (row != null) charts.Add(row);
                }
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"charts\":[],\"errors\":[{\"code\":\"BRIDGE\",\"message\":"
                     + JsonStr(ex.GetType().Name + ": " + ex.Message) + "}]}";
            }

            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(JsonStr(id))
              .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
              .Append(",\"workspace\":").Append(JsonStr(CurrentWorkspaceName()))
              .Append(",\"chartCount\":").Append(charts.Count.ToString(InvCi))
              .Append(",\"charts\":[").Append(string.Join(",", charts.ToArray())).Append("]")
              .Append(",\"notes\":[");
            for (int i = 0; i < notes.Count; i++) { if (i > 0) sb.Append(","); sb.Append(JsonStr(notes[i])); }
            sb.Append("]}");
            return sb.ToString();
        }

        // Reflected, not bound: the member has moved across NT builds and is not worth a compile break.
        private static string CurrentWorkspaceName()
        {
            string[] names = { "CurrentWorkspace", "Workspace", "WorkspaceName" };
            foreach (string n in names)
            {
                try
                {
                    PropertyInfo pi = typeof(Globals).GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (pi != null)
                    {
                        object v = pi.GetValue(null, null);
                        if (v != null) return Convert.ToString(v);
                    }
                }
                catch { }
            }
            return null;
        }

        // MUST be called on the chart window's OWN dispatcher (see RunWorkspace).
        private string DescribeChart(Window win)
        {
            var sb = new StringBuilder();
            string title = null;
            try { title = win.Title; } catch { }
            sb.Append("{\"title\":").Append(JsonStr(title))
              .Append(",\"type\":").Append(JsonStr(SafeTypeName(win)));

            object cc = FirstMember(win, new[] { "ActiveChartControl", "ChartControl" });
            if (cc == null)
            {
                sb.Append(",\"chartControlResolved\":false,\"instrument\":null,\"barsPeriod\":null")
                  .Append(",\"indicators\":null,\"strategies\":null}");
                return sb.ToString();
            }
            sb.Append(",\"chartControlResolved\":true");

            object instr = FirstMember(cc, new[] { "Instrument" });
            object bp = FirstMember(cc, new[] { "BarsPeriod" });
            sb.Append(",\"instrument\":").Append(JsonStr(instr != null ? Convert.ToString(instr) : null))
              .Append(",\"barsPeriod\":").Append(JsonStr(bp != null ? Convert.ToString(bp) : null));

            sb.Append(",\"indicators\":");
            AppendNsList(sb, FirstMember(cc, new[] { "Indicators" }));
            sb.Append(",\"strategies\":");
            AppendNsList(sb, FirstMember(cc, new[] { "Strategies" }));

            sb.Append("}");
            return sb.ToString();
        }

        // NinjaScript objects (indicators/strategies) as {name, state, enabled}. `state` is NT's own State
        // enum verbatim — Active/Realtime/Historical/Terminated — because the raw value is the evidence and
        // any collapsing of it into a boolean is a claim we would then have to defend.
        private void AppendNsList(StringBuilder sb, object list)
        {
            if (list == null) { sb.Append("null"); return; }
            sb.Append("[");
            try
            {
                var en = list as IEnumerable;
                if (en != null)
                {
                    bool first = true;
                    foreach (object o in en)
                    {
                        if (o == null) continue;
                        string nm = null, st = null;
                        // ⚠ A Sentinel tool BLANKS its own Name at DataLoaded (that is how the on-chart label
                        // is hidden — the label IS the Name property), so `Name` is legitimately "" for most
                        // of this suite. Fall back to the type name, or every row reads as anonymous and any
                        // caller matching on name matches nothing. Found by running it against a live chart.
                        try { object v = FirstMember(o, new[] { "Name" }); nm = Convert.ToString(v); } catch { }
                        if (string.IsNullOrEmpty(nm)) nm = SafeTypeName(o);
                        try { object v = FirstMember(o, new[] { "State" }); st = v != null ? Convert.ToString(v) : null; } catch { }
                        if (!first) sb.Append(",");
                        first = false;
                        sb.Append("{\"name\":").Append(JsonStr(nm))
                          .Append(",\"type\":").Append(JsonStr(SafeTypeName(o)))
                          .Append(",\"state\":").Append(JsonStr(st))
                          .Append(",\"enabled\":").Append(st == "Active" || st == "Realtime" || st == "Historical" ? "true" : "false")
                          .Append("}");
                    }
                }
            }
            catch (Exception ex) { LogSafe("AppendNsList: " + ex.Message); }
            sb.Append("]");
        }

        private static string SafeTypeName(object o)
        {
            try { return o != null ? o.GetType().Name : null; } catch { return null; }
        }

        // First of the candidate property/field names that resolves on this object, else null.
        private static object FirstMember(object target, string[] names)
        {
            if (target == null) return null;
            Type t;
            try { t = target.GetType(); } catch { return null; }
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (string n in names)
            {
                try
                {
                    PropertyInfo pi = t.GetProperty(n, bf);
                    if (pi != null && pi.CanRead) { object v = pi.GetValue(target, null); if (v != null) return v; }
                }
                catch { }
                try
                {
                    FieldInfo fi = t.GetField(n, bf);
                    if (fi != null) { object v = fi.GetValue(target); if (v != null) return v; }
                }
                catch { }
            }
            return null;
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

        // ── dialog P/Invoke ────────────────────────────────────────────────────────────────────────────
        //  A MODAL dialog is detectable without touching WPF at all: Windows disables the owner window
        //  while a modal child is up. `owner != 0 && !IsWindowEnabled(owner)` is therefore the signal,
        //  and it is thread-agnostic — which matters, because the dialog belongs to another UI thread.
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        private const uint BM_CLICK = 0x00F5;
        private const uint GW_OWNER = 4;
        private const uint WM_CLOSE = 0x0010;

        [StructLayout(LayoutKind.Sequential)]
        private struct BridgeRect { public int Left, Top, Right, Bottom; }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);

        // ── screenshot P/Invoke ─────────────────────────────────────────────────────────────────────────
        //  GDI, not System.Drawing: the encode goes through WPF's PngBitmapEncoder (PresentationCore, which
        //  this suite already depends on everywhere) so nothing new has to be referenced to build.
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr h);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
                                                                   IntPtr src, int sx, int sy, int rop);

        // ── layout P/Invoke ─────────────────────────────────────────────────────────────────────────────
        //  DWM frame bounds, restored placement, monitor work areas, and the one mutating call in the
        //  whole file. See RunLayout for why each is needed.
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out BridgeRect r, int size);
        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
        private static extern int DwmGetWindowAttributeInt(IntPtr h, int attr, out int v, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct BridgePlacement
        {
            public int length, flags, showCmd;
            public int minX, minY, maxX, maxY;
            public int normLeft, normTop, normRight, normBottom;
        }
        [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr h, ref BridgePlacement p);

        [StructLayout(LayoutKind.Sequential)]
        private struct BridgeMonitorInfo
        {
            public int cbSize;
            public BridgeRect monitor;
            public BridgeRect work;
            public uint flags;
        }
        private delegate bool MonitorEnumProc(IntPtr mon, IntPtr dc, ref BridgeRect r, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref BridgeMonitorInfo info);

        private const int  DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        private const int  DWMWA_CLOAKED = 14;
        private const int  SW_RESTORE_L = 9, SW_MINIMIZE_L = 6, SW_MAXIMIZE_L = 3;
        private const uint SWP_NOZORDER_L = 0x0004, SWP_NOACTIVATE_L = 0x0010;
        private const int  GWL_EXSTYLE_L = -20;
        private const int  WS_EX_TOOLWINDOW_L = 0x00000080;
        private const uint GW_OWNER_L = 4;
        private const uint MONITORINFOF_PRIMARY = 1;

        // ── layout ──────────────────────────────────────────────────────────────────────────────────────
        //  READ AND WRITE WHERE NINJATRADER'S WINDOWS SIT.
        //
        //  ⭐ WHY: every input to a replay-equivalence run that we made verifiable is a FILE we can hash
        //  — the code, the .nrd, the historical bars, the chart+strategy blob. Window layout was not one
        //  of them: it lived only inside a running NinjaTrader, set by hand, per box, at different
        //  moments. That is the same shape as the transport state that cost a day, and the Playback range
        //  that "does not travel". A fleet of six workers cannot have an input that needs a GUI click per
        //  box — it is guaranteed to diverge across a matrix.
        //
        //  ⚠ THIS HANDLER IS DELIBERATELY DUMB. It enumerates, and it moves an HWND it is told to move.
        //  It does NOT match windows, compute fractions, or decide anything. All of that lives in the
        //  Python half, where it is unit-testable without a running NinjaTrader — the AddOn is the one
        //  place in this system that cannot be tested offline, so the less judgement it holds the better.
        //
        //  ⚠ ORDER: place FIRST, then enumerate. The response therefore describes the state the apply
        //  actually produced rather than the one it intended, so a caller can verify instead of trusting.
        private string RunLayout(string id, string triggerJson)
        {
            string place = ExtractJsonString(triggerJson, "place");
            int placed = 0;
            var failed = new List<string>();

            try { SetProcessDPIAware(); } catch { }

            if (!string.IsNullOrEmpty(place))
            {
                foreach (string rec in place.Split(';'))
                {
                    if (rec.Trim().Length == 0) continue;
                    try
                    {
                        // hwnd,x,y,w,h,state
                        string[] f = rec.Split(',');
                        if (f.Length < 5) { failed.Add(rec + " (malformed)"); continue; }
                        IntPtr h = new IntPtr(long.Parse(f[0], InvCi));
                        int x = int.Parse(f[1], InvCi), y = int.Parse(f[2], InvCi);
                        int w = int.Parse(f[3], InvCi), ht = int.Parse(f[4], InvCi);
                        string state = f.Length > 5 ? f[5] : "normal";

                        if (!IsWindow(h)) { failed.Add(f[0] + " (dead hwnd)"); continue; }

                        // Windows refuses to move a maximized or minimized window; restore it first or
                        // SetWindowPos silently succeeds and does nothing.
                        if (IsZoomed(h) || IsIconic(h)) { ShowWindow(h, SW_RESTORE_L); System.Threading.Thread.Sleep(40); }

                        // Aim the VISIBLE edge, not the window rect. Since Windows 10 the window rect
                        // includes an invisible resize border (~7px/side, measured), so snapping to it
                        // leaves every window visibly misaligned against its neighbour.
                        BridgeRect wr, vr;
                        bool gotW = GetWindowRect(h, out wr);
                        bool gotV = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out vr, Marshal.SizeOf(typeof(BridgeRect))) == 0;
                        int dl = 0, dt = 0, dr = 0, db = 0;
                        if (gotW && gotV)
                        {
                            dl = vr.Left - wr.Left; dt = vr.Top - wr.Top;
                            dr = wr.Right - vr.Right; db = wr.Bottom - vr.Bottom;
                        }
                        bool ok = SetWindowPos(h, IntPtr.Zero, x - dl, y - dt, w + dl + dr, ht + dt + db,
                                               SWP_NOZORDER_L | SWP_NOACTIVATE_L);
                        if (!ok) { failed.Add(f[0] + " (SetWindowPos failed)"); continue; }

                        // Restore the STATE too. Placing requires un-minimizing, so without this an
                        // apply pops open every minimized window it touches.
                        if (state == "minimized") ShowWindow(h, SW_MINIMIZE_L);
                        else if (state == "maximized") ShowWindow(h, SW_MAXIMIZE_L);
                        placed++;
                    }
                    catch (Exception ex) { failed.Add(rec + " (" + ex.GetType().Name + ")"); }
                }
            }

            var mons = new List<string>();
            var monWork = new List<BridgeRect>();
            try
            {
                MonitorEnumProc mcb = delegate(IntPtr mon, IntPtr dc, ref BridgeRect r, IntPtr data)
                {
                    try
                    {
                        var mi = new BridgeMonitorInfo();
                        mi.cbSize = Marshal.SizeOf(typeof(BridgeMonitorInfo));
                        if (!GetMonitorInfo(mon, ref mi)) return true;
                        monWork.Add(mi.work);
                        mons.Add("{\"id\":" + (mons.Count).ToString(InvCi) +
                                 ",\"primary\":" + (((mi.flags & MONITORINFOF_PRIMARY) != 0) ? "true" : "false") +
                                 ",\"work\":{\"x\":" + mi.work.Left.ToString(InvCi) +
                                 ",\"y\":" + mi.work.Top.ToString(InvCi) +
                                 ",\"w\":" + (mi.work.Right - mi.work.Left).ToString(InvCi) +
                                 ",\"h\":" + (mi.work.Bottom - mi.work.Top).ToString(InvCi) + "}}");
                    }
                    catch (Exception ex) { LogSafe("RunLayout monitor: " + ex.Message); }
                    return true;
                };
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, mcb, IntPtr.Zero);
                GC.KeepAlive(mcb);
            }
            catch (Exception ex) { LogSafe("RunLayout monitors: " + ex.Message); }

            var rows = new List<string>();
            try
            {
                uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
                {
                    try
                    {
                        uint pid; GetWindowThreadProcessId(h, out pid);
                        if (pid != self || !IsWindow(h)) return true;
                        var title = new StringBuilder(400);
                        GetWindowText(h, title, title.Capacity);
                        if (title.Length == 0) return true;
                        var cls = new StringBuilder(200);
                        GetClassName(h, cls, cls.Capacity);

                        BridgeRect wr; bool gotW = GetWindowRect(h, out wr);
                        BridgeRect vr;
                        bool gotV = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out vr, Marshal.SizeOf(typeof(BridgeRect))) == 0;
                        if (!gotV) vr = wr;

                        var wp = new BridgePlacement();
                        wp.length = Marshal.SizeOf(typeof(BridgePlacement));
                        bool gotP = GetWindowPlacement(h, ref wp);

                        int cloaked = 0;
                        try { DwmGetWindowAttributeInt(h, DWMWA_CLOAKED, out cloaked, sizeof(int)); } catch { }

                        // Which monitor: by the window's CENTRE, and never fail to answer — a window
                        // straddling two screens still has to be attributed to one of them.
                        int cx = (vr.Left + vr.Right) / 2, cy = (vr.Top + vr.Bottom) / 2;
                        int mi2 = -1; long bestD = long.MaxValue;
                        for (int i = 0; i < monWork.Count; i++)
                        {
                            BridgeRect m = monWork[i];
                            if (cx >= m.Left && cx < m.Right && cy >= m.Top && cy < m.Bottom) { mi2 = i; break; }
                            long mx = (m.Left + m.Right) / 2, my = (m.Top + m.Bottom) / 2;
                            long d = (cx - mx) * (cx - mx) + (cy - my) * (cy - my);
                            if (d < bestD) { bestD = d; mi2 = i; }
                        }

                        int ex = GetWindowLong(h, GWL_EXSTYLE_L);
                        rows.Add("{\"hwnd\":" + h.ToInt64().ToString(InvCi) +
                                 ",\"title\":" + JsonStr(title.ToString()) +
                                 ",\"class\":" + JsonStr(cls.ToString()) +
                                 ",\"visible\":" + (IsWindowVisible(h) ? "true" : "false") +
                                 ",\"minimized\":" + (IsIconic(h) ? "true" : "false") +
                                 ",\"maximized\":" + (IsZoomed(h) ? "true" : "false") +
                                 ",\"cloaked\":" + (cloaked != 0 ? "true" : "false") +
                                 ",\"owned\":" + (GetWindow(h, GW_OWNER_L) != IntPtr.Zero ? "true" : "false") +
                                 ",\"toolWindow\":" + (((ex & WS_EX_TOOLWINDOW_L) != 0) ? "true" : "false") +
                                 ",\"monitor\":" + mi2.ToString(InvCi) +
                                 ",\"visual\":{\"x\":" + vr.Left.ToString(InvCi) + ",\"y\":" + vr.Top.ToString(InvCi) +
                                 ",\"w\":" + (vr.Right - vr.Left).ToString(InvCi) + ",\"h\":" + (vr.Bottom - vr.Top).ToString(InvCi) + "}" +
                                 ",\"restored\":{\"x\":" + (gotP ? wp.normLeft : 0).ToString(InvCi) +
                                 ",\"y\":" + (gotP ? wp.normTop : 0).ToString(InvCi) +
                                 ",\"w\":" + (gotP ? wp.normRight - wp.normLeft : 0).ToString(InvCi) +
                                 ",\"h\":" + (gotP ? wp.normBottom - wp.normTop : 0).ToString(InvCi) + "}}");
                    }
                    catch (Exception ex2) { LogSafe("RunLayout row: " + ex2.Message); }
                    return true;
                };
                EnumWindows(cb, IntPtr.Zero);
                GC.KeepAlive(cb);
            }
            catch (Exception ex)
            {
                return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"message\":" + JsonStr(ex.Message) + "}";
            }

            var fj = new List<string>();
            foreach (string s in failed) fj.Add(JsonStr(s));
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\"" +
                   ",\"ts\":" + JsonStr(DateTime.UtcNow.ToString("o")) +
                   ",\"placed\":" + placed.ToString(InvCi) +
                   ",\"failed\":[" + string.Join(",", fj.ToArray()) + "]" +
                   ",\"monitors\":[" + string.Join(",", mons.ToArray()) + "]" +
                   ",\"count\":" + rows.Count.ToString(InvCi) +
                   ",\"windows\":[" + string.Join(",", rows.ToArray()) + "]}";
        }

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
        // ══════════════════════════════════════════════════════════════════════════════════════════
        //  ORDER — the highest-risk verb family in this bridge: it can place real orders on a real
        //  account from a headless shell with no human at the keyboard. It is therefore built
        //  REFUSAL-FIRST, and this first cut is deliberately READ-ONLY (`api` + `list`).
        //
        //  Why read-only first, and it is not caution for its own sake: the mutating path needs
        //  CreateOrder's real overload, the real name of whatever marks an account as simulated, and
        //  the real settled-state enum. Guessing any of those produces code that compiles, runs, and
        //  reports success while doing something else — and here "something else" is an order. The
        //  `--api` discovery rule has already refuted two assumptions in this codebase within a
        //  minute each; a mutating order path is the last place to stop applying it.
        //
        //  ⚠ `api` dumps Account and its Connection/Options UNFILTERED. A filtered dump is what hid
        //  the answer last time ("Break at EOD" — the filter was the bug), and the whole question
        //  here is what marks an account as SIM, which is a field nobody has yet named correctly.
        private string RunOrder(string id, string text)
        {
            string action = ExtractJsonString(text, "action");
            if (string.IsNullOrEmpty(action)) action = "list";

            try
            {
                if (action == "api") return RunOrderApi(id);
                if (action == "list") return RunOrderList(id, text);
                if (action == "status") return RunOrderStatus(id, text);
                if (action == "place") return RunOrderPlace(id, text);
                if (action == "cancel") return RunOrderCancel(id, text);
                if (action == "change") return RunOrderChange(id, text);

                return OrderErr(id, "UNKNOWNACTION", "order action '" + action + "' does not exist. "
                    + "Known: api, list, status, place, cancel, change.");
            }
            catch (Exception ex)
            {
                return OrderErr(id, "BRIDGE", ex.GetType().Name + ": " + Unwrap(ex));
            }
        }

        private string OrderErr(string id, string code, string message)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"ts\":"
                 + JsonStr(DateTime.UtcNow.ToString("o")) + ",\"orders\":[],\"errors\":[{"
                 + "\"code\":" + JsonStr(code) + ",\"message\":" + JsonStr(message) + "}]}";
        }

        // Rule 4 of the bridge's own lessons: ex.Message on a reflection call prints the content-free
        // "Exception has been thrown by the target of an invocation." Always unwrap.
        private static string Unwrap(Exception ex)
        {
            try { return ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
            catch { return "(unreadable exception)"; }
        }

        private string RunOrderApi(string id)
        {
            var b = new StringBuilder();
            b.Append("{\"id\":").Append(JsonStr(id))
             .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
             .Append(",\"action\":\"api\"");

            Account acct = null;
            try { lock (Account.All) { foreach (Account a in Account.All) { acct = a; break; } } } catch { }

            b.Append(",\"accountName\":").Append(JsonStr(acct == null ? "" : SafeStr(delegate { return acct.Name; })));
            b.Append(",\"account\":").Append(DumpMembers(acct, null));
            b.Append(",\"connection\":").Append(DumpMembers(SafeProp(acct, "Connection"), null));
            b.Append(",\"connectionOptions\":").Append(DumpMembers(SafeProp(SafeProp(acct, "Connection"), "Options"), null));

            // Account METHODS — the submit/modify surface. Unfiltered on name would be hundreds of
            // members; these five prefixes are the entire order lifecycle.
            b.Append(",\"accountMethods\":[");
            bool f = true;
            try
            {
                if (acct != null)
                    foreach (MethodInfo mi in acct.GetType().GetMethods(
                                 BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        string n = mi.Name;
                        if (n.IndexOf("Create", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Submit", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Change", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) < 0
                            && n.IndexOf("Flatten", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var ps = mi.GetParameters();
                        var sig = new StringBuilder(n).Append("(");
                        for (int k = 0; k < ps.Length; k++)
                        { if (k > 0) sig.Append(","); sig.Append(ps[k].ParameterType.Name).Append(" ").Append(ps[k].Name); }
                        sig.Append(") -> ").Append(mi.ReturnType.Name);
                        if (!f) b.Append(","); f = false;
                        b.Append(JsonStr(sig.ToString()));
                    }
            }
            catch (Exception ex) { if (!f) b.Append(","); b.Append(JsonStr("ERR " + Unwrap(ex))); }
            b.Append("]");

            // A live Order instance, if one exists — the authoritative field list for `list`/`status`,
            // including whatever the id property is actually called.
            Order sample = null;
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                    {
                        lock (a.Orders) { foreach (Order o in a.Orders) { sample = o; break; } }
                        if (sample != null) break;
                    }
                }
            }
            catch { }
            b.Append(",\"orderSampleFound\":").Append(sample != null ? "true" : "false");
            b.Append(",\"order\":").Append(DumpMembers(sample, null));

            // The enums a caller has to spell. Emitted as the REAL member names so the client can
            // validate input against NT rather than against a hand-copied list that drifts.
            b.Append(",\"enums\":{");
            b.Append("\"OrderType\":").Append(EnumNames(typeof(OrderType)));
            b.Append(",\"OrderAction\":").Append(EnumNames(typeof(OrderAction)));
            b.Append(",\"TimeInForce\":").Append(EnumNames(typeof(TimeInForce)));
            b.Append(",\"OrderState\":").Append(EnumNames(typeof(OrderState)));
            b.Append("}}");
            return b.ToString();
        }

        private static string EnumNames(Type t)
        {
            var b = new StringBuilder("[");
            try
            {
                string[] ns = Enum.GetNames(t);
                for (int i = 0; i < ns.Length; i++) { if (i > 0) b.Append(","); b.Append(JsonStr(ns[i])); }
            }
            catch (Exception ex) { b.Append(JsonStr("ERR " + Unwrap(ex))); }
            return b.Append("]").ToString();
        }

        // Read-only listing. `working` (default true) keeps it to live orders; pass working=false to
        // see terminal states too. Account name is optional here BECAUSE this reads nothing and
        // changes nothing — the mutating verbs will require it explicitly.
        private string RunOrderList(string id, string text)
        {
            string acctFilter = ExtractJsonString(text, "account");
            string instFilter = ExtractJsonString(text, "instrument");
            bool workingOnly = ExtractJsonString(text, "working") != "false";

            var b = new StringBuilder();
            b.Append("{\"id\":").Append(JsonStr(id))
             .Append(",\"status\":\"ok\",\"ts\":").Append(JsonStr(DateTime.UtcNow.ToString("o")))
             .Append(",\"action\":\"list\",\"workingOnly\":").Append(workingOnly ? "true" : "false")
             .Append(",\"orders\":[");

            var rows = new List<Order>();
            var owner = new List<string>();
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                    {
                        string an = SafeStr(delegate { return a.Name; });
                        if (!string.IsNullOrEmpty(acctFilter) && an != acctFilter) continue;
                        lock (a.Orders)
                        {
                            foreach (Order o in a.Orders)
                            {
                                string st = SafeStr(delegate { return o.OrderState.ToString(); });
                                if (workingOnly && !IsWorkingState(st)) continue;
                                string fn = SafeStr(delegate { return o.Instrument.FullName; });
                                if (!string.IsNullOrEmpty(instFilter) && fn != instFilter) continue;
                                rows.Add(o); owner.Add(an);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { LogSafe("order list: " + Unwrap(ex)); }

            for (int i = 0; i < rows.Count; i++)
            {
                Order o = rows[i];
                if (i > 0) b.Append(",");
                b.Append("{\"account\":").Append(JsonStr(owner[i]))
                 .Append(",\"orderId\":").Append(JsonStr(SafeStr(delegate { return o.OrderId; })))
                 .Append(",\"name\":").Append(JsonStr(SafeStr(delegate { return o.Name; })))
                 .Append(",\"instrument\":").Append(JsonStr(SafeStr(delegate { return o.Instrument.FullName; })))
                 .Append(",\"action\":").Append(JsonStr(SafeStr(delegate { return o.OrderAction.ToString(); })))
                 .Append(",\"type\":").Append(JsonStr(SafeStr(delegate { return o.OrderType.ToString(); })))
                 .Append(",\"state\":").Append(JsonStr(SafeStr(delegate { return o.OrderState.ToString(); })))
                 .Append(",\"quantity\":").Append(SafeInt(delegate { return o.Quantity; }).ToString(InvCi))
                 .Append(",\"filled\":").Append(SafeInt(delegate { return o.Filled; }).ToString(InvCi))
                 .Append(",\"limitPrice\":").Append(SafeNum(delegate { return o.LimitPrice; }))
                 .Append(",\"stopPrice\":").Append(SafeNum(delegate { return o.StopPrice; }))
                 .Append(",\"avgFillPrice\":").Append(SafeNum(delegate { return o.AverageFillPrice; }))
                 .Append(",\"tif\":").Append(JsonStr(SafeStr(delegate { return o.TimeInForce.ToString(); })))
                 .Append(",\"oco\":").Append(JsonStr(SafeStr(delegate { return o.Oco; })))
                 .Append("}");
            }
            b.Append("]}");
            return b.ToString();
        }

        // ── the mutating half ─────────────────────────────────────────────────────────────────────
        //  Every one of these refuses BEFORE it constructs anything. The order of the gates is the
        //  design: confirm → account named → account found → account is SIMULATED → instrument named
        //  → instrument resolves → side/type/quantity valid. Nothing is inferred at any step. An
        //  omitted account does not mean "the only one"; an omitted side has no sensible default.
        //
        //  ⚠ THE SIM GATE READS A MUTABLE PROPERTY. `Account.Provider` came back `[RW]` from the
        //  discovery dump, so this is a guard against ACCIDENT — a script pointed at the wrong
        //  account name — and not against a caller determined to defeat it. Said plainly because a
        //  guard whose strength is overestimated is worse than one whose limits are written down.
        //  Live trading is not one flag away here: there is deliberately NO escalation parameter in
        //  this build, so the only way to reach a live account is to edit and recompile this file.
        private static bool IsSimAccount(Account a)
        {
            try { return a.Provider == Provider.Simulator; } catch { return false; }
        }

        // Terminal = the broker is done with it. Anything else may still move, and a caller must not
        // read "not terminal yet" as failure — see the settle-poll note in OrderOutcome.
        private static bool IsTerminalState(string st)
        {
            return st == "Filled" || st == "Cancelled" || st == "Rejected";
        }

        // Resolve the gates common to every mutating action. Returns null on success and sets acct;
        // returns a ready-to-send error JSON otherwise.
        private string OrderGate(string id, string text, out Account acct)
        {
            acct = null;
            if (ExtractJsonString(text, "confirm") != "true")
                return OrderErr(id, "CONFIRM", "refusing: this action mutates orders and requires confirm=true.");

            string name = ExtractJsonString(text, "account");
            if (string.IsNullOrEmpty(name))
                return OrderErr(id, "NOACCOUNT", "refusing: account must be named explicitly. "
                    + "There is no default, and 'the only account' is not a safe inference.");

            Account found = null;
            try { lock (Account.All) { foreach (Account a in Account.All) { if (a.Name == name) { found = a; break; } } } } catch { }
            if (found == null)
                return OrderErr(id, "NOTFOUND", "account not found: " + name);

            if (!IsSimAccount(found))
                return OrderErr(id, "NOTSIM", "REFUSING: account '" + name + "' is not a simulation account "
                    + "(Provider=" + SafeStr(delegate { return found.Provider.ToString(); }) + "). This build places orders on "
                    + "SIMULATED accounts only, and carries no escalation flag by design.");

            // ⛔⛔ RISK-GOVERNOR CONSULT (2026-08-17). MEASURED that day, and it is why this exists:
            // a Sentinel risk container auto-flattened SimBURN-1 at its daily loss stop and LOCKED
            // THE ACCOUNT OUT. A market order placed through THIS verb then FILLED on it -- 1 lot,
            // status ok, no refusal, no error.
            //
            // ⭐ THE LESSON, and it is architectural rather than a bug: the container is a
            // PARTICIPANT, not a PERIMETER. SentinelCore's own header says "that acts must consult
            // it before acting" -- there is no interception layer. So every order path that does
            // not ASK walks straight past a locked-out account: this verb, the DOM, a chart trade
            // button, a hand-placed ticket. A lockout was a property of the asking ORDER SOURCE
            // and never of the ACCOUNT.
            //
            // ⚠ REFLECTION, DELIBERATELY, NOT A DIRECT CALL. This AddOn is published standalone and
            // must compile on a machine with no Sentinel suite present; a hard reference to
            // SentinelCore would make it uncompilable there. Absent Sentinel this is a no-op and
            // the verb behaves exactly as before -- fail-OPEN is correct here because this file
            // cannot be the risk authority for a suite it may not be installed alongside. What it
            // must never do is fail-open SILENTLY when Sentinel IS present and says no.
            string govRefusal = SentinelGovernorRefusal(found);
            if (govRefusal != null)
                return OrderErr(id, "RISKLOCKED", "REFUSING: the Sentinel risk container has halted '"
                    + name + "': " + govRefusal + ". Flatten and exit paths are unaffected; this "
                    + "refusal applies to NEW positions only.");

            acct = found;
            return null;
        }

        /// <summary>Non-null reason when Sentinel says this account may not take a NEW position.
        /// Null = permitted, or Sentinel is not installed. Never throws: a risk consult that can
        /// crash the order path is a worse failure than the one it prevents.
        ///
        /// ⛔ TWO INDEPENDENT HALT MECHANISMS, AND THE FIRST CUT OF THIS WIRED ONLY ONE. v1 consulted
        /// GetGovernorState().Status == "DayHalted" and the re-test STILL FILLED on a locked-out
        /// account, because the DAILY-LOSS governor had rolled with the trading day while the
        /// TRAILING-DRAWDOWN floor was still breaching every heartbeat. Two separate states, and
        /// reasoning from the auto-flatten log alone could not tell them apart.
        /// ⇒ Consult BOTH, and lead with DrawdownAllowsEntry -- the purpose-built "may this account
        ///   take a new position" API that SentinelCore.CanEnter itself calls. Using the same
        ///   predicate as the suite's own order sources is the point: a second implementation of
        ///   "is this allowed" is a second thing to keep in sync, and it will drift.</summary>
        private static bool _sentinelAbsentLogged;

        private string SentinelGovernorRefusal(Account found)
        {
            try
            {
                // ⛔⛔ THE NAME WAS WRONG AND IT FAILED SILENTLY, 2026-08-17. v2 looked up
                // "NinjaTrader.NinjaScript.AddOns.SentinelCore". The real namespace is
                // ...AddOns.**Sentinel**.SentinelCore, so the lookup returned null, both consults
                // were SKIPPED, and this method returned "permitted" WITH NO TRACE ANYWHERE. The
                // probe filled three times and each fill looked like a policy decision.
                // ⇒ I wrote a silent fail-open into the fix FOR a silent fail-open. The name is now
                //   right, several candidates are tried, and — the part that matters more than the
                //   name — NOT FINDING THE TYPE IS LOGGED. A guard that cannot say "I did not run"
                //   is indistinguishable from a guard that ran and permitted.
                Type core = null;
                string[] candidates = new string[] {
                    "NinjaTrader.NinjaScript.AddOns.Sentinel.SentinelCore",
                    "NinjaTrader.NinjaScript.AddOns.SentinelCore",
                };
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (var n in candidates)
                    {
                        core = asm.GetType(n, false);
                        if (core != null) break;
                    }
                    if (core != null) break;
                }
                // ⛔ COUNT THE GENERATIONS. MEASURED 2026-08-17: the risk service wrote
                // "GOV TRACE ... -> DayHalted" and 82 s later this consult read "Trading", with NO
                // intervening change logged by the writer. A value cannot be both unless the writer
                // and the reader are looking at DIFFERENT static stores -- i.e. two generations of
                // NinjaTrader.Custom are loaded (six `reload`s that night) and GetAssemblies() order
                // decided which one this resolved. SentinelCore v1.40.0's generation beacon exists
                // for exactly this hazard; a seam read that does not name its generation is a guess.
                int genCount = 0;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    foreach (var n in candidates)
                        if (asm.GetType(n, false) != null) { genCount++; break; }
                if (genCount > 1)
                    LogSafe("RISK CONSULT AMBIGUOUS: " + genCount + " loaded assemblies expose "
                          + "SentinelCore. This read may target an ORPHANED seam store, so a "
                          + "'permitted' answer here is NOT authoritative. Restart NT to collapse "
                          + "the generations before trusting it.");

                if (core == null)
                {
                    if (!_sentinelAbsentLogged)
                    {
                        _sentinelAbsentLogged = true;
                        LogSafe("RISK CONSULT INERT: SentinelCore type not found — order placement is "
                              + "NOT risk-gated on this box. Logged once. If the Sentinel suite IS "
                              + "installed here, this is a BUG, not a configuration.");
                    }
                    return null;                                    // suite absent -> documented no-op
                }

                // 1) the TRAILING-DRAWDOWN floor: blocks when the cushion is thin.
                var dd = core.GetMethod("DrawdownAllowsEntry",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (dd != null)
                {
                    object[] args = new object[] { found, null };
                    object ok = dd.Invoke(null, args);
                    // ⭐ LOG WHAT WAS READ, ALWAYS -- not only on refusal. Three probes in a row
                    // filled and each looked like a policy decision; there was no way to tell a
                    // guard that ran and permitted from a guard that never ran. An unobservable
                    // decision is the same failure as a silent one.
                    LogSafe("risk consult " + found.Name + ": DrawdownAllowsEntry=" +
                            (ok == null ? "null" : ok.ToString()) +
                            " reason=" + ((args[1] as string) ?? "-"));
                    if (ok is bool && !((bool)ok))
                        return "drawdown floor: " + ((args[1] as string) ?? "cushion exhausted");
                }

                // 2) the DAILY-LOSS governor, which is a DIFFERENT state on a DIFFERENT clock.
                var gv = core.GetMethod("GetGovernorState",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                    null, new Type[] { typeof(string) }, null);
                if (gv != null)
                {
                    object gs = gv.Invoke(null, new object[] { found.Name });
                    if (gs != null)                                 // null = Core's documented FAIL-OPEN
                    {
                        var f = gs.GetType().GetField("Status");
                        string status = f == null ? null : f.GetValue(gs) as string;
                        // Only a HALT blocks. "DayComplete" is a trader who hit target and may
                        // legitimately hold a winner; blocking there would be this file inventing a
                        // rule the suite does not have, which is how a safety check becomes a bug.
                        LogSafe("risk consult " + found.Name + ": governorStatus=" + (status ?? "null"));
                        if (status == "DayHalted") return "governor status DayHalted";
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                LogSafe("sentinel risk consult failed (allowing): " + Unwrap(ex));
                return null;
            }
        }

        // Poll an order to a settled state. ⭐ The lesson this codebase has paid for four times over:
        // NEVER judge an NT mutation by the call. apply-template read 3 of 14, addIndicator read
        // 2 -> 1, both mid-transition. So we poll, and — the part that matters — a still-moving order
        // is reported as settled:false with its current state, NOT as an error. A false negative here
        // makes a caller RETRY, and a retried order placement is a DUPLICATE ORDER.
        private string OrderOutcome(Order o, double settleSeconds)
        {
            string st = "";
            DateTime deadline = DateTime.UtcNow.AddSeconds(settleSeconds);
            bool settled = false;
            while (DateTime.UtcNow < deadline)
            {
                st = SafeStr(delegate { return o.OrderState.ToString(); });
                if (IsTerminalState(st) || st == "Working" || st == "Accepted" || st == "TriggerPending")
                { settled = true; break; }
                System.Threading.Thread.Sleep(100);
            }
            if (string.IsNullOrEmpty(st)) st = SafeStr(delegate { return o.OrderState.ToString(); });

            var b = new StringBuilder();
            b.Append("{\"orderId\":").Append(JsonStr(SafeStr(delegate { return o.OrderId; })))
             .Append(",\"name\":").Append(JsonStr(SafeStr(delegate { return o.Name; })))
             .Append(",\"instrument\":").Append(JsonStr(SafeStr(delegate { return o.Instrument.FullName; })))
             .Append(",\"action\":").Append(JsonStr(SafeStr(delegate { return o.OrderAction.ToString(); })))
             .Append(",\"type\":").Append(JsonStr(SafeStr(delegate { return o.OrderType.ToString(); })))
             .Append(",\"state\":").Append(JsonStr(st))
             .Append(",\"settled\":").Append(settled ? "true" : "false")
             .Append(",\"terminal\":").Append(IsTerminalState(st) ? "true" : "false")
             .Append(",\"quantity\":").Append(SafeInt(delegate { return o.Quantity; }).ToString(InvCi))
             .Append(",\"filled\":").Append(SafeInt(delegate { return o.Filled; }).ToString(InvCi))
             .Append(",\"limitPrice\":").Append(SafeNum(delegate { return o.LimitPrice; }))
             .Append(",\"stopPrice\":").Append(SafeNum(delegate { return o.StopPrice; }))
             .Append(",\"avgFillPrice\":").Append(SafeNum(delegate { return o.AverageFillPrice; }))
             .Append("}");
            return b.ToString();
        }

        private static double SettleSecs(string text)
        {
            double s;
            string raw = ExtractJsonString(text, "settle");
            if (!string.IsNullOrEmpty(raw) && double.TryParse(raw, System.Globalization.NumberStyles.Float, InvCi, out s)
                && s >= 0 && s <= 60) return s;
            return 5.0;
        }

        private string RunOrderPlace(string id, string text)
        {
            Account acct;
            string refusal = OrderGate(id, text, out acct);
            if (refusal != null) return refusal;

            string instName = ExtractJsonString(text, "instrument");
            if (string.IsNullOrEmpty(instName))
                return OrderErr(id, "NOINSTRUMENT", "refusing: instrument must be named explicitly.");
            Instrument instr = null;
            try { instr = Instrument.GetInstrument(instName); } catch (Exception ex) { LogSafe("order place instrument: " + Unwrap(ex)); }
            if (instr == null)
                return OrderErr(id, "BADINSTRUMENT", "instrument did not resolve: " + instName);

            OrderAction oa;
            if (!TryParseEnum<OrderAction>(ExtractJsonString(text, "side"), out oa))
                return OrderErr(id, "BADSIDE", "side must be one of Buy, Sell, BuyToCover, SellShort. "
                    + "There is no default side.");

            OrderType ot;
            if (!TryParseEnum<OrderType>(ExtractJsonString(text, "type"), out ot) || ot == OrderType.Unknown)
                return OrderErr(id, "BADTYPE", "type must be one of Market, Limit, StopMarket, StopLimit, MIT.");

            TimeInForce tif;
            string tifRaw = ExtractJsonString(text, "tif");
            if (string.IsNullOrEmpty(tifRaw)) tif = TimeInForce.Day;
            else if (!TryParseEnum<TimeInForce>(tifRaw, out tif))
                return OrderErr(id, "BADTIF", "tif must be one of Day, Gtc, Ioc, Opg, Gtd.");

            int qty;
            if (!int.TryParse(ExtractJsonString(text, "quantity"), System.Globalization.NumberStyles.Integer, InvCi, out qty) || qty <= 0)
                return OrderErr(id, "BADQTY", "quantity must be a positive integer.");

            double limit = ParsePrice(ExtractJsonString(text, "limitPrice"));
            double stop = ParsePrice(ExtractJsonString(text, "stopPrice"));

            // A price a type REQUIRES must be present. Submitting a Limit at 0 is a market order in
            // all but name — the single most expensive way for this to "succeed".
            if ((ot == OrderType.Limit || ot == OrderType.StopLimit) && limit <= 0)
                return OrderErr(id, "NOLIMIT", ot.ToString() + " requires limitPrice > 0.");
            if ((ot == OrderType.StopMarket || ot == OrderType.StopLimit || ot == OrderType.MIT) && stop <= 0)
                return OrderErr(id, "NOSTOP", ot.ToString() + " requires stopPrice > 0.");

            string oco = ExtractJsonString(text, "oco");
            string oname = ExtractJsonString(text, "name");
            if (string.IsNullOrEmpty(oname)) oname = "bridge";

            Order ord = null;
            try
            {
                ord = acct.CreateOrder(instr, oa, ot, tif, qty, limit, stop, oco ?? "", oname, null);
            }
            catch (Exception ex) { return OrderErr(id, "CREATE", "CreateOrder threw: " + Unwrap(ex)); }
            if (ord == null) return OrderErr(id, "CREATE", "CreateOrder returned null.");

            try { acct.Submit(new[] { ord }); }
            catch (Exception ex) { return OrderErr(id, "SUBMIT", "Submit threw: " + Unwrap(ex)); }

            LogSafe("ORDER PLACE account=" + SafeStr(delegate { return acct.Name; }) + " " + oa + " " + qty + " "
                    + instName + " " + ot + " lmt=" + limit + " stp=" + stop + " tif=" + tif);

            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"ts\":" + JsonStr(DateTime.UtcNow.ToString("o"))
                 + ",\"action\":\"place\",\"account\":" + JsonStr(SafeStr(delegate { return acct.Name; }))
                 + ",\"orders\":[" + OrderOutcome(ord, SettleSecs(text)) + "]}";
        }

        private string RunOrderCancel(string id, string text)
        {
            Account acct;
            string refusal = OrderGate(id, text, out acct);
            if (refusal != null) return refusal;

            string orderId = ExtractJsonString(text, "orderId");
            bool all = ExtractJsonString(text, "all") == "true";
            if (string.IsNullOrEmpty(orderId) && !all)
                return OrderErr(id, "NOORDER", "refusing: pass orderId, or all=true to cancel every working "
                    + "order on this account. Cancelling everything is never an inference from silence.");

            var targets = new List<Order>();
            try
            {
                lock (acct.Orders)
                {
                    foreach (Order o in acct.Orders)
                    {
                        if (!IsWorkingState(SafeStr(delegate { return o.OrderState.ToString(); }))) continue;
                        if (!all && SafeStr(delegate { return o.OrderId; }) != orderId) continue;
                        targets.Add(o);
                    }
                }
            }
            catch (Exception ex) { LogSafe("order cancel scan: " + Unwrap(ex)); }

            if (targets.Count == 0)
                return OrderErr(id, "NOMATCH", all ? "no working orders on that account."
                                                   : "no working order with orderId " + orderId);

            // Lesson #159: act AFTER releasing the collection lock.
            try { acct.Cancel(targets); }
            catch (Exception ex) { return OrderErr(id, "CANCEL", "Cancel threw: " + Unwrap(ex)); }
            LogSafe("ORDER CANCEL account=" + SafeStr(delegate { return acct.Name; }) + " count=" + targets.Count);

            var b = new StringBuilder();
            b.Append("{\"id\":").Append(JsonStr(id)).Append(",\"status\":\"ok\",\"ts\":")
             .Append(JsonStr(DateTime.UtcNow.ToString("o")))
             .Append(",\"action\":\"cancel\",\"account\":").Append(JsonStr(SafeStr(delegate { return acct.Name; })))
             .Append(",\"orders\":[");
            double settle = SettleSecs(text);
            for (int i = 0; i < targets.Count; i++) { if (i > 0) b.Append(","); b.Append(OrderOutcome(targets[i], settle)); }
            b.Append("]}");
            return b.ToString();
        }

        private string RunOrderChange(string id, string text)
        {
            Account acct;
            string refusal = OrderGate(id, text, out acct);
            if (refusal != null) return refusal;

            string orderId = ExtractJsonString(text, "orderId");
            if (string.IsNullOrEmpty(orderId))
                return OrderErr(id, "NOORDER", "refusing: change requires orderId.");

            Order target = null;
            try
            {
                lock (acct.Orders)
                {
                    foreach (Order o in acct.Orders)
                    {
                        if (SafeStr(delegate { return o.OrderId; }) != orderId) continue;
                        target = o; break;
                    }
                }
            }
            catch (Exception ex) { LogSafe("order change scan: " + Unwrap(ex)); }
            if (target == null) return OrderErr(id, "NOMATCH", "no order with orderId " + orderId);
            if (!IsWorkingState(SafeStr(delegate { return target.OrderState.ToString(); })))
                return OrderErr(id, "NOTWORKING", "order " + orderId + " is not in a working state ("
                    + SafeStr(delegate { return target.OrderState.ToString(); }) + ") — nothing to change.");

            // Absent fields mean "leave alone", which is why they are read as -1/0 sentinels rather
            // than defaulted. A change that silently reset an untouched price to 0 would be a market
            // order wearing a limit order's name.
            string qRaw = ExtractJsonString(text, "quantity");
            string lRaw = ExtractJsonString(text, "limitPrice");
            string sRaw = ExtractJsonString(text, "stopPrice");
            if (string.IsNullOrEmpty(qRaw) && string.IsNullOrEmpty(lRaw) && string.IsNullOrEmpty(sRaw))
                return OrderErr(id, "NOCHANGE", "refusing: pass at least one of quantity, limitPrice, stopPrice.");

            int qty = SafeInt(delegate { return target.Quantity; });
            double limit = SafeNumRaw(delegate { return target.LimitPrice; });
            double stop = SafeNumRaw(delegate { return target.StopPrice; });

            if (!string.IsNullOrEmpty(qRaw)
                && (!int.TryParse(qRaw, System.Globalization.NumberStyles.Integer, InvCi, out qty) || qty <= 0))
                return OrderErr(id, "BADQTY", "quantity must be a positive integer.");
            if (!string.IsNullOrEmpty(lRaw)) limit = ParsePrice(lRaw);
            if (!string.IsNullOrEmpty(sRaw)) stop = ParsePrice(sRaw);

            try
            {
                target.QuantityChanged = qty;
                target.LimitPriceChanged = limit;
                target.StopPriceChanged = stop;
                acct.Change(new[] { target });
            }
            catch (Exception ex) { return OrderErr(id, "CHANGE", "Change threw: " + Unwrap(ex)); }
            LogSafe("ORDER CHANGE account=" + SafeStr(delegate { return acct.Name; }) + " id=" + orderId
                    + " qty=" + qty + " lmt=" + limit + " stp=" + stop);

            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"ts\":" + JsonStr(DateTime.UtcNow.ToString("o"))
                 + ",\"action\":\"change\",\"account\":" + JsonStr(SafeStr(delegate { return acct.Name; }))
                 + ",\"orders\":[" + OrderOutcome(target, SettleSecs(text)) + "]}";
        }

        // Read-only: one order by id, with its CURRENT state. No confirm, no sim gate — it changes
        // nothing. This is what a caller polls after a place/cancel rather than re-submitting.
        private string RunOrderStatus(string id, string text)
        {
            string orderId = ExtractJsonString(text, "orderId");
            if (string.IsNullOrEmpty(orderId))
                return OrderErr(id, "NOORDER", "status requires orderId.");

            Order target = null;
            string owner = "";
            try
            {
                lock (Account.All)
                {
                    foreach (Account a in Account.All)
                    {
                        lock (a.Orders)
                            foreach (Order o in a.Orders)
                                if (SafeStr(delegate { return o.OrderId; }) == orderId)
                                { target = o; owner = SafeStr(delegate { return a.Name; }); break; }
                        if (target != null) break;
                    }
                }
            }
            catch (Exception ex) { LogSafe("order status: " + Unwrap(ex)); }
            if (target == null) return OrderErr(id, "NOMATCH", "no order with orderId " + orderId);

            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"ts\":" + JsonStr(DateTime.UtcNow.ToString("o"))
                 + ",\"action\":\"status\",\"account\":" + JsonStr(owner)
                 + ",\"orders\":[" + OrderOutcome(target, 0) + "]}";
        }

        private static bool TryParseEnum<T>(string raw, out T value)
        {
            value = default(T);
            if (string.IsNullOrEmpty(raw)) return false;
            try
            {
                foreach (string n in Enum.GetNames(typeof(T)))
                    if (string.Equals(n, raw, StringComparison.OrdinalIgnoreCase))
                    { value = (T)Enum.Parse(typeof(T), n); return true; }
            }
            catch { }
            return false;
        }

        private static double ParsePrice(string raw)
        {
            double d;
            if (!string.IsNullOrEmpty(raw)
                && double.TryParse(raw, System.Globalization.NumberStyles.Float, InvCi, out d)) return d;
            return 0;
        }

        private static double SafeNumRaw(Func<double> f)
        {
            try { return f(); } catch { return 0; }
        }

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
        // CONNECT / DISCONNECT a configured connection.
        //
        // WHY THIS EXISTS: the read side has been here since 1.5.0, but nothing could raise a
        // connection — which meant a replay cell still needed a human to pick "Market Replay" from
        // the Connections menu, on every box. Discovery (`chart --api`) named the member:
        // `static Connection.Connect(ConnectOptions)`, with per-connection `Options` and `Status`.
        //
        // THE THREE HOUSE RULES APPLY:
        //  1. An ambiguous name is REFUSED, never resolved to the first match.
        //  2. CONNECT requires --confirm (a broker connection is an order-capable surface);
        //     DISCONNECT does not, because the safe direction must never be the harder one to reach.
        //  3. The VERDICT comes from polling Status to a settled value, never from Connect()
        //     returning. `succeeded` != `changed`: connecting an already-connected feed moves
        //     nothing and says so.
        private string RunConnectionMutate(string id, string text, string action)
        {
            string nameQ = ExtractJsonString(text, "name");
            bool confirm = JsonBoolFlag(text, "confirm");
            int waitMs = 30000;
            try { int w; if (int.TryParse(ExtractJsonString(text, "waitMs"), out w) && w > 0) waitMs = w; }
            catch { }

            if (string.IsNullOrEmpty(nameQ))
                return ConnErr(id, "NONAME", action + " requires --name");
            if (action == "connect" && !confirm)
                return ConnErr(id, "NOCONFIRM",
                               "connect requires --confirm: raising a connection can arm an "
                               + "order-capable surface");

            // Resolve against the CONFIGURED list. Exact (case-insensitive) wins outright;
            // otherwise a substring match must be UNIQUE or we refuse.
            var cfg = new List<ConnectOptions>();
            try { lock (Globals.ConnectOptions) { foreach (ConnectOptions o in Globals.ConnectOptions) cfg.Add(o); } }
            catch (Exception ex) { return ConnErr(id, "ENUM", ex.Message); }

            ConnectOptions target = null;
            var partial = new List<ConnectOptions>();
            foreach (ConnectOptions o in cfg)
            {
                string nm = SafeStr(delegate { return o.Name; });
                if (string.IsNullOrEmpty(nm)) continue;
                if (string.Equals(nm, nameQ, StringComparison.OrdinalIgnoreCase)) { target = o; break; }
                if (nm.IndexOf(nameQ, StringComparison.OrdinalIgnoreCase) >= 0) partial.Add(o);
            }
            if (target == null)
            {
                if (partial.Count == 1) target = partial[0];
                else if (partial.Count > 1)
                {
                    var names = new List<string>();
                    foreach (ConnectOptions o in partial) names.Add(SafeStr(delegate { return o.Name; }));
                    return ConnErr(id, "AMBIGUOUS",
                                   "'" + nameQ + "' matches " + partial.Count + " connections ("
                                   + string.Join(", ", names.ToArray())
                                   + ") — name it exactly. Nothing was changed.");
                }
                else
                {
                    var names = new List<string>();
                    foreach (ConnectOptions o in cfg) names.Add(SafeStr(delegate { return o.Name; }));
                    return ConnErr(id, "NOTFOUND",
                                   "no configured connection matching '" + nameQ + "'. Configured: "
                                   + string.Join(", ", names.ToArray()));
                }
            }

            string targetName = SafeStr(delegate { return target.Name; });
            string before = LiveStatusNow(targetName);

            try
            {
                if (action == "connect")
                {
                    if (before == "Connected")
                        return ConnVerdict(id, action, targetName, before, before, true, false,
                                           "already Connected — nothing to do");
                    Connection.Connect(target);
                }
                else
                {
                    Connection victim = null;
                    try
                    {
                        lock (Connection.Connections)
                            foreach (Connection c in Connection.Connections)
                            {
                                string nm = SafeStr(delegate { return c.Options != null ? c.Options.Name : null; });
                                if (string.Equals(nm, targetName, StringComparison.OrdinalIgnoreCase)) { victim = c; break; }
                            }
                    }
                    catch { }
                    if (victim == null)
                        return ConnVerdict(id, action, targetName, before, before, true, false,
                                           "not connected — nothing to do");
                    victim.Disconnect();
                }
            }
            catch (Exception ex) { return ConnErr(id, "APPLY", Explain(ex)); }

            // Poll to a SETTLED verdict. Re-reads the collection every pass: a held Connection
            // reference can watch a corpse while the live one moves on.
            string want = action == "connect" ? "Connected" : "Disconnected";
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
            string after = before;
            while (DateTime.UtcNow < deadline)
            {
                after = LiveStatusNow(targetName);
                if (action == "connect" && after == "Connected") break;
                if (action == "disconnect" && (after == "Disconnected" || after == "(none)")) break;
                try { Thread.Sleep(250); } catch { }
            }

            bool ok = action == "connect"
                        ? after == "Connected"
                        : (after == "Disconnected" || after == "(none)");
            return ConnVerdict(id, action, targetName, before, after, ok, before != after,
                               ok ? action + " took — status " + before + " -> " + after
                                  : "THE CALL RESOLVED BUT STATUS IS " + after + " AFTER "
                                    + waitMs + "ms (wanted " + want + ") — treat as NOT "
                                    + (action == "connect" ? "connected" : "disconnected"));
        }

        // Current live status of a configured connection, re-read from the collection each call.
        private string LiveStatusNow(string name)
        {
            var live = new List<Connection>();
            try { lock (Connection.Connections) { foreach (Connection c in Connection.Connections) live.Add(c); } }
            catch { }
            return LiveStatusOf(live, name);
        }

        private string ConnVerdict(string id, string action, string name, string before, string after,
                                   bool ok, bool changed, string verdict)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"ok\",\"action\":" + JsonStr(action)
                 + ",\"name\":" + JsonStr(name)
                 + ",\"statusBefore\":" + JsonStr(before) + ",\"statusAfter\":" + JsonStr(after)
                 + ",\"succeeded\":" + (ok ? "true" : "false")
                 + ",\"changed\":" + (changed ? "true" : "false")
                 + ",\"verdict\":" + JsonStr(verdict) + ",\"connections\":[],\"errors\":[]}";
        }

        private string ConnErr(string id, string code, string msg)
        {
            return "{\"id\":" + JsonStr(id) + ",\"status\":\"error\",\"succeeded\":false,\"changed\":false"
                 + ",\"connections\":[],\"errors\":[{\"code\":" + JsonStr(code)
                 + ",\"message\":" + JsonStr(msg) + "}]}";
        }

        private string RunConnections(string id, string text)
        {
            string action = ExtractJsonString(text, "action");
            if (action == "connect" || action == "disconnect")
                return RunConnectionMutate(id, text, action);
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

        // ⚠ A KEY IS A QUOTED TOKEN FOLLOWED BY A COLON — anything else is a VALUE that happens to
        // read like the key. This used to take the first `"key"` anywhere and then hunt for the next
        // ':', which meant `ExtractJsonString(req, "chart")` matched the VALUE in `"kind":"chart"`
        // and returned the next field's value instead. On the chart handler that silently became a
        // title filter of "list", so a box with charts answered "no matching charts" — a confident,
        // well-formed, completely wrong answer. Found by driving it, not by reading it.
        private static string ExtractJsonString(string json, string key)
        {
            if (json == null) return null;
            string pat = "\"" + key + "\"";
            int from = 0;
            while (true)
            {
                int i = json.IndexOf(pat, from, StringComparison.Ordinal);
                if (i < 0) return null;
                int c = i + pat.Length;
                while (c < json.Length && char.IsWhiteSpace(json[c])) c++;
                if (c >= json.Length || json[c] != ':') { from = i + pat.Length; continue; }
                int q1 = json.IndexOf('"', c + 1);
                if (q1 < 0) return null;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) return null;
                return json.Substring(q1 + 1, q2 - q1 - 1);
            }
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
