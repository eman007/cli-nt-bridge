/* MIT License
Copyright (c) 2026 Quantrosoft Pty. Ltd.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

// NT8BridgeServerPlayback.cs — the DRIVING half of the playback path.
//
// It is a `partial` of NT8BridgeServer so that the existing file keeps almost all
// of its own text: what it gains is the lease hooks, the dispatch for kind
// "playbackrun", the `partial` keyword and a result-prefix hook. Nothing existing
// is deleted, so a later change to that file cannot collide with this one.
//
// Upstream's `playback` command READS the transport - connection, clock, speed,
// coverage - and its changelog states it mutates nothing. This file is the half
// that acts: connect, source, range, speed, set a strategy up from a TEMPLATE, arm
// it, start it, detect the end, and restore the baseline.
//
// ⚠ It contains no GUI click of any kind, and it must stay that way. The check:
//     grep -c "OnCli""ck()|Automation""Peer|IInvoke""Provider|Click""Event"   ->   0
//   (the pattern is split so this very line does not match it - a self-matching
//    check line reports 1 forever and hides the day a real click comes back)
// The call behind a button is found with stage `findmethod`, never simulated.

#region Using declarations
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.Gui.NinjaScript.StrategyAnalyzer;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public partial class NT8BridgeServer
    {
        // ── The calls behind the Strategies grid's context menu ──────────────────────────────
        //  Asked of NinjaTrader with `findmethod`, not guessed:
        //      static StrategiesGrid.StrategyEnable(StrategyBase, Window, StrategiesGridEntry)
        //      static StrategiesGrid.StrategyDisable(StrategyBase)
        //      static StrategiesGrid.StrategyRemove(StrategyBase)
        //  Enable takes the row object; the other two do not. All three are FIRED with
        //  BeginInvoke and never awaited from inside the dispatcher: awaiting one from
        //  the dispatcher froze NinjaTrader. The memory dump that established it is
        //  quoted at the `attach` stage, where the strategy is created.
        private static Type StrategiesGridType()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type ty = null;
                try { ty = asm.GetType("NinjaTrader.Gui.NinjaScript.StrategiesGrid", false); }
                catch { }
                if (ty != null) return ty;
            }
            return null;
        }

        private static MethodInfo StrategiesGridStatic(string name, int argc)
        {
            Type ty = StrategiesGridType();
            if (ty == null) return null;
            foreach (MethodInfo mi in ty.GetMethods(BFStatic))
                if (mi.Name == name && mi.GetParameters().Length == argc) return mi;
            return null;
        }

        // The strategy a grid row carries. The row is a view object; the strategy hangs off
        // it under a property, and identity against it is what makes a row OURS.
        private static StrategyBase RowStrategy(object row)
        {
            if (row == null) return null;
            foreach (PropertyInfo pr in row.GetType().GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (pr.GetIndexParameters().Length > 0) continue;
                object v = null;
                try { v = pr.GetValue(row, null); } catch { continue; }
                StrategyBase s = v as StrategyBase;
                if (s != null) return s;
            }
            return null;
        }

        // Every row of the Strategies grid, as (row, strategy). Read on the dispatcher by
        // the caller; this method itself only walks the collection it is handed.
        // ⚠ THIS COLLECTION BELONGS TO NINJATRADER AND CHANGES WHILE IT IS READ.
        //
        // Measured 25.08.2026, on the teardown of a run that had just reached the
        // data end:
        //     restore disconnect confirmed: ... 15:18:19.657 ...
        //     RestoreBaselineNow: Collection was modified; enumeration operation
        //                         may not execute.
        // A `foreach` over the live DataSource threw, the exception unwound the
        // whole handler into its catch, and the handler therefore never answered.
        // The caller reported "NO REACTION in 36 s" for work NinjaTrader had
        // finished 4 s earlier - the worst kind of report, because it names the
        // wrong component. A disconnect disables and removes strategy rows by
        // itself, so the window in which this enumerator is invalid is exactly
        // the window every teardown runs in.
        //
        // An IList is read BY INDEX and needs no enumerator, which removes the
        // race rather than surviving it. The enumerating path stays for anything
        // that is not an IList, and both are retried: a collection that is being
        // rewritten right now is readable a moment later, on the same thread.
        //
        // FAIL LOUD at the end. Returning an empty list would say "no strategy
        // rows" - which reads as a clean baseline and is exactly the silent wrong
        // answer this file exists to avoid.
        private static List<KeyValuePair<object, StrategyBase>> GridRows(object grid)
        {
            var outp = new List<KeyValuePair<object, StrategyBase>>();
            if (grid == null) return outp;
            PropertyInfo pSrc = grid.GetType().GetProperty("DataSource");
            System.Collections.IEnumerable rows =
                pSrc == null ? null : pSrc.GetValue(grid, null) as System.Collections.IEnumerable;
            if (rows == null) return outp;

            System.Collections.IList list = rows as System.Collections.IList;
            string lastError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                outp.Clear();
                try
                {
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            object o = list[i];
                            if (o == null) continue;
                            outp.Add(new KeyValuePair<object, StrategyBase>(o, RowStrategy(o)));
                        }
                    }
                    else
                    {
                        foreach (object o in rows)
                        {
                            if (o == null) continue;
                            outp.Add(new KeyValuePair<object, StrategyBase>(o, RowStrategy(o)));
                        }
                    }
                    if (attempt > 0)
                        LogStatic("GridRows: read on attempt " + (attempt + 1)
                                  + " after: " + lastError);
                    return outp;
                }
                catch (InvalidOperationException ex) { lastError = ex.Message; }
                catch (ArgumentOutOfRangeException ex) { lastError = ex.Message; }
            }
            throw new InvalidOperationException(
                "GridRows: the strategies grid kept changing across 5 reads ("
                + lastError + "). Reporting no rows here would read as a clean "
                + "baseline, so this is raised instead.");
        }

        // Disable, then Remove - in that order and never the other way round. NinjaTrader
        // only asks "are you sure" for a strategy that is still ENABLED, and that modal
        // blocks every later command. Disabling an already-disabled strategy is a no-op, so
        // the order costs nothing and removes the dialog instead of answering it.
        private static void FireDisableRemove(Window cc, StrategyBase strat)
        {
            if (cc == null || strat == null) return;
            MethodInfo dis = StrategiesGridStatic("StrategyDisable", 1);
            MethodInfo rem = StrategiesGridStatic("StrategyRemove", 1);
            StrategyBase s = strat;
            cc.Dispatcher.BeginInvoke(new Action(delegate
            {
                try { if (dis != null) dis.Invoke(null, new object[] { s }); } catch { }
                try { if (rem != null) rem.Invoke(null, new object[] { s }); } catch { }
            }));
        }

        // A modal must never be clicked away. If one is standing, that is the finding.
        private string StandingModal()
        {
            foreach (Window w in AllWindowsIncludingOwned())
            {
                string ty = null;
                try { ty = w.GetType().Name; } catch { }
                if (ty != null && ty.IndexOf("MessageBox", StringComparison.OrdinalIgnoreCase) >= 0)
                    return ty;
            }
            return null;
        }

        // ── The transport, without the play button ───────────────────────────────────────────
        //  Writing PlaybackAdapter.PlaybackSpeed IS the run control: a positive value runs,
        //  0 parks. Bisected on 2026-08-19 (writing the speed started the
        //  transport) and independently documented upstream in PR #5 ("Setting a speed starts
        //  the transport", and "ZERO IS A REAL SPEED, not a missing argument"). The play
        //  button was therefore never needed - it was only ever a slower way to do this.
        // A range that cannot be right must never be armed. Measured 2026-08-19: a
        // strategy restored from a template carried From 2099-12-01 / To 1800-01-01 -
        // the placeholder values of a fresh NinjaScript instance - and arming it made
        // NinjaTrader load until it stopped answering. Refusing costs one run.
        private static string RangeProblem(DateTime from, DateTime to)
        {
            if (from > to)
                return "From " + from.ToString("yyyy-MM-dd") + " is AFTER To "
                     + to.ToString("yyyy-MM-dd") + " - inverted range";
            if (from.Year < 1990 || from.Year > 2090)
                return "From " + from.ToString("yyyy-MM-dd") + " is outside 1990..2090 - "
                     + "this is a placeholder, not a range";
            if (to.Year < 1990 || to.Year > 2090)
                return "To " + to.ToString("yyyy-MM-dd") + " is outside 1990..2090 - "
                     + "this is a placeholder, not a range";
            if ((to - from).TotalDays > 3660.0)
                return "range spans " + ((int)(to - from).TotalDays) + " days - "
                     + "more than ten years is not a run, it is a mistake";
            return null;
        }

        // ── NinjaTrader's OWN log, in memory ─────────────────────────────────────────────────
        //  `NinjaTrader.Cbi.Log` raises a STATIC event for every entry it writes, carrying
        //  Time, LogLevel, LogCategory and Message. Subscribing once turns that into a
        //  bot-free channel that is strictly better than reading the log file:
        //
        //    * no file rotation, no retention window, no ".en.txt" twin to pick between
        //    * event-driven, so there is nothing to be "0.8 s too early" for - which is
        //      exactly the mistake that made me report "NinjaTrader never armed it" three
        //      times on 2026-08-19 while it had armed it a moment later
        //    * a bot cannot write into it, cannot suppress it, and does not have to exist
        //
        //  That last point is the mission: the remote control has to work with a COMPLETELY
        //  EMPTY bot, so no evidence may come from the bot.
        private static readonly List<string> _ntLog = new List<string>();
        private static readonly object _ntLogGate = new object();

        // How many entries have fallen off the FRONT of _ntLog so far.
        //
        // ⚠ A LIST INDEX CANNOT MARK A POSITION IN A RING. The mark a caller takes
        // before it acts, and the position it later searches from, must be a running
        // SEQUENCE NUMBER - otherwise, once the buffer sits at NtLogMax, Count stops
        // growing, the mark is pinned at NtLogMax and every new entry lands BELOW it.
        // The search `for (i = since; i < snap.Count; i++)` then has nothing to do and
        // the wait can never succeed again for the life of the process.
        //
        // Measured 27.08.2026: after about two hours of playback runs the buffer was
        // saturated by order events, and from then on every attach failed with
        // "NinjaTrader never reported NinjaScriptStrategyBaseEnabling" - while
        // NinjaTrader's own log carried the entry 21 seconds after the request. Six
        // variants had passed before it; every one after it failed identically, and a
        // restart of the run did not help because this buffer is process-wide.
        private static long _ntLogDropped;
        private static bool _ntLogSubscribed;
        private static string _ntLogFile;

        // ── THE BOT'S OWN OUTPUT, LIVE ──────────────────────────────────────
        //
        // Everything a NinjaScript writes with Print() goes through ONE public
        // static event (measured 2026-08-21 by decompiling NinjaTrader.Core):
        //     NinjaTrader.Code.Output.OutputEvent : EventHandler<OutputEventArgs>
        //     OutputEventArgs { string Message; PrintTo OutputTab; bool IsReset; }
        // Subscribing to it gives the driver the bot's output WHILE the run is
        // going on, without touching the bot and without reading the Output
        // window's visual tree. That is what makes a console that shows harness
        // lines and [BOT] lines together possible.
        //
        // ⚠ This is an OUTPUT channel, never an evidence channel. The remote
        // control must work with a bot that prints nothing, so nothing in the
        // chain may wait for, or conclude from, a line that appears here.
        private static readonly List<string> _botOut = new List<string>();
        private static readonly object _botOutGate = new object();
        private static bool _botOutSubscribed;
        // A run can print a lot. The buffer keeps the newest lines; the driver
        // polls it faster than it fills, and the count of dropped lines is
        // reported so a gap can never pass unnoticed.
        private const int BotOutMax = 20000;
        private static long _botOutDropped;

        // ⚠ DEBUGGING AID, OFF BY DEFAULT.
        //
        // The in-memory buffer plus the `ntlog` stage is the normal channel and costs
        // nothing. Mirroring every entry into a FILE exists for one situation only: the
        // request worker is stuck inside a UI call, so no stage answers - and then the
        // file, written from NinjaTrader's own log thread, is the only way to see what it
        // reported last. That was the open blind spot on 2026-08-19.
        //
        // It is a switch and not a permanent feature, so it cannot quietly stay on:
        //     {"stage":"ntlog","toFile":"true"}   turn it on for a debugging session
        //     {"stage":"ntlog","toFile":"false"}  off again
        private static bool _ntLogToFile;
        private const int NtLogMax = 2000;

        // ── Wait for one of NinjaTrader's own log entries ────────────────────────────────────
        //
        //  The building block for "trigger and wait belong together" without inventing a
        //  duration. A caller notes the log index, does the thing, and then waits HERE for
        //  the entry NinjaTrader writes when it is done:
        //
        //      int mark = NtLogCount();
        //      pc.Disconnect();
        //      WaitForLogName(mark, "CbiConnectionProcessConnectionStatusUpdate", "Disconnected", ttl)
        //
        //  Matching is on `Name` - the resource identifier - so it survives a NinjaTrader
        //  update and a different UI language. `contains` is an optional extra filter on the
        //  rendered text for cases where one name covers several transitions (Connecting and
        //  Connected share a name).
        //
        //  ⚠ The only bound is the caller's own patience. Nothing in here decides how long
        //  NinjaTrader is allowed to take.
        // ── Phase 3 of the handshake: wait until the state CHANGES ───────────────────────────
        //
        //  User, 2026-08-20 (translated from German): "if you do not wait at all after an
        //  action, it can happen that the system has not reacted to your action yet and
        //  therefore reports ready straight away."
        //
        //  Exactly what happened with `ready`: it answered after 1.0 s with the state from
        //  BEFORE the connect. Phase 3 closes that hole - the value has to leave where it was,
        //  or reach where it was sent, before anything is called done.
        //
        //  The sleep in here is a SAMPLING RATE, not a wait: it decides how often the value is
        //  looked at, never how long NinjaTrader may take. The only bound is the caller's ttl.
        private static bool WaitUntilChanged(Func<string> read, string before, string want,
                                             double ttlSec, out string got, out long ms)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            got = before;
            while (true)
            {
                try { got = read(); } catch { }
                bool done = want == null
                            ? (got != before)                                   // just: it moved
                            : string.Equals(got, want, StringComparison.OrdinalIgnoreCase);
                if (done) { ms = sw.ElapsedMilliseconds; return true; }
                if (sw.Elapsed.TotalSeconds >= ttlSec) { ms = sw.ElapsedMilliseconds; return false; }
                Thread.Sleep(100);
            }
        }

        // ⚠ THE HANDSHAKE FOR "NINJATRADER IS DONE WITH THE PREVIOUS STEP".
        //
        // Between two stages there was no handshake at all. Each stage waited for its
        // OWN effect and then returned, while NinjaTrader kept working - and the next
        // stage fired into that. When the user stepped through the chain by hand, his
        // keypresses were accidentally supplying the missing pause; measured
        // 2026-08-20, the same `attach` that came back in 1.9 s between keypresses left
        // its enable operation Pending when the steps ran back to back.
        //
        // The signal is NinjaTrader's own: a delegate posted at ApplicationIdle runs
        // only once the dispatcher has processed everything of higher priority. When it
        // completes, the UI thread has drained its queue. That is an event, not a
        // duration - nothing here guesses how long anything takes.
        //
        // ⚠ The wait happens on the WORKER thread. BeginInvoke posts, and the returned
        // operation is waited on from outside the dispatcher - never inside it, which is
        // the mistake that froze NinjaTrader twice on 2026-08-20. The only bound is the
        // caller's own ttlSec.
        private static bool WaitForUiIdle(Window w, double ttlSec, out long ms, out string status)
        {
            ms = 0;
            status = "no window";
            if (w == null) return false;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                System.Windows.Threading.DispatcherOperation op =
                    w.Dispatcher.BeginInvoke(new Action(delegate { }),
                        System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                System.Windows.Threading.DispatcherOperationStatus st =
                    op.Wait(TimeSpan.FromSeconds(ttlSec));
                ms = sw.ElapsedMilliseconds;
                status = st.ToString();
                return st == System.Windows.Threading.DispatcherOperationStatus.Completed;
            }
            catch (Exception ex)
            {
                ms = sw.ElapsedMilliseconds;
                status = ex.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// The mark a caller takes before it acts: the number of log entries seen SO
        /// FAR, dropped ones included. It only ever grows, so it stays a valid
        /// position even after the buffer has wrapped.
        /// </summary>
        private static int NtLogCount()
        {
            SubscribeNtLog();
            lock (_ntLogGate) return (int)(_ntLogDropped + _ntLog.Count);
        }

        /// <summary>
        /// Turn a mark from <see cref="NtLogCount"/> into an offset into the buffer as
        /// it stands now. Entries between the mark and the oldest one still held were
        /// dropped and cannot be searched; the offset is then 0, i.e. everything left.
        /// Call it under _ntLogGate together with the snapshot it belongs to.
        /// </summary>
        private static int NtLogOffset(int since)
        {
            long off = since - _ntLogDropped;
            if (off < 0) return 0;
            if (off > _ntLog.Count) return _ntLog.Count;
            return (int)off;
        }

        private static bool WaitForLogName(int since, string name, string contains, double ttlSec,
                                           out string hit, out int nowIndex)
        {
            SubscribeNtLog();
            hit = null;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                List<string> snap; int from;
                lock (_ntLogGate)
                {
                    snap = new List<string>(_ntLog);
                    from = NtLogOffset(since);   // the mark counts entries; this is its slot
                }
                for (int i = from; i < snap.Count; i++)
                {
                    string[] f = snap[i].Split('|');
                    if (f.Length < 6) continue;
                    if (!string.IsNullOrEmpty(name)
                        && f[3].IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!string.IsNullOrEmpty(contains)
                        && f[5].IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    hit = snap[i];
                    nowIndex = NtLogCount();
                    return true;
                }
                if (sw.Elapsed.TotalSeconds >= ttlSec)
                {
                    nowIndex = NtLogCount();
                    return false;
                }
                // sampling rate, NOT a deadline: this only decides how often the buffer is
                // looked at, never how long NinjaTrader may take.
                Thread.Sleep(100);
            }
        }

        /// <summary>The static twin of LogSafe - same file, same shape. Needed
        /// because the bot-output subscription runs from static context (the event
        /// is static) while LogSafe is an instance method.</summary>
        private static void LogStatic(string msg)
        {
            try
            {
                string dir = Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "NT8Bridge", "result");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "bridge.log"),
                    DateTime.UtcNow.ToString("o") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        /// <summary>Subscribe to NinjaTrader's own Print() event so the driver can
        /// show the bot's output live. Idempotent; every failure is logged with the
        /// reflection step that failed, because a silent miss here would look like
        /// "the bot printed nothing".</summary>
        private static void SubscribeBotOutput()
        {
            if (_botOutSubscribed) return;
            try
            {
                Type ot = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { ot = asm.GetType("NinjaTrader.Code.Output", false); } catch { }
                    if (ot != null) break;
                }
                if (ot == null) { LogStatic("SubscribeBotOutput: type NinjaTrader.Code.Output not found"); return; }
                EventInfo ev = ot.GetEvent("OutputEvent", BFStatic);
                if (ev == null) { LogStatic("SubscribeBotOutput: event OutputEvent not found"); return; }
                MethodInfo handler = typeof(NT8BridgeServer).GetMethod("OnBotOutput",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (handler == null) { LogStatic("SubscribeBotOutput: handler OnBotOutput not found"); return; }
                ev.AddEventHandler(null, Delegate.CreateDelegate(ev.EventHandlerType, handler));
                _botOutSubscribed = true;
                LogStatic("subscribed to NinjaTrader.Code.Output.OutputEvent (bot Print() stream)");
            }
            catch (Exception ex) { LogStatic("SubscribeBotOutput: " + Deep(ex)); }
        }

        /// <summary>One Print() line from any NinjaScript. Runs on whatever thread
        /// printed, so it only appends to the buffer - no dispatcher, no file IO,
        /// nothing that could slow the strategy down.</summary>
        private static void OnBotOutput(object sender, EventArgs e)
        {
            try
            {
                Type et = e.GetType();
                PropertyInfo pm = et.GetProperty("Message");
                PropertyInfo pt = et.GetProperty("OutputTab");
                PropertyInfo pr = et.GetProperty("IsReset");
                bool isReset = pr != null && pr.GetValue(e, null) is bool && (bool)pr.GetValue(e, null);
                string msg = pm == null ? "" : (pm.GetValue(e, null) as string ?? "");
                string tab = pt == null ? "" : ("" + pt.GetValue(e, null));
                // A reset (the user clearing the window) is an event of its own -
                // reported, never silently swallowed.
                string line = isReset ? ("<<output reset, tab " + tab + ">>")
                                      : (tab.Length > 0 ? ("[" + tab + "] " + msg) : msg);
                lock (_botOutGate)
                {
                    _botOut.Add(line);
                    if (_botOut.Count > BotOutMax)
                    {
                        int cut = _botOut.Count - BotOutMax;
                        _botOut.RemoveRange(0, cut);
                        _botOutDropped += cut;
                    }
                }
            }
            catch { }   // never let a print break the printer
        }

        private static void SubscribeNtLog()
        {
            if (_ntLogSubscribed) return;
            try
            {
                Type lt = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { lt = asm.GetType("NinjaTrader.Cbi.Log", false); } catch { }
                    if (lt != null) break;
                }
                if (lt == null) return;
                EventInfo ev = lt.GetEvent("LogEvent", BFStatic);
                if (ev == null) return;
                MethodInfo handler = typeof(NT8BridgeServer).GetMethod("OnNtLogEvent",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (handler == null) return;
                Delegate d = Delegate.CreateDelegate(ev.EventHandlerType, handler);
                ev.AddEventHandler(null, d);
                _ntLogSubscribed = true;
                if (!_ntLogToFile) return;
                try
                {
                    string dir0 = Path.Combine(Globals.UserDataDir, "NT8Bridge");
                    Directory.CreateDirectory(dir0);
                    _ntLogFile = Path.Combine(dir0, "ntlog.txt");
                    File.AppendAllText(_ntLogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                        + "|Information|Bridge|BridgeNtLogSubscribed|-|channel open"
                        + Environment.NewLine);
                }
                catch { }
            }
            catch { }   // reported by the stage as subscribed=false, not swallowed silently
        }

        private static void OnNtLogEvent(object sender, EventArgs e)
        {
            try
            {
                // ⚠ THE NAME IS THE IDENTIFIER, THE MESSAGE IS ONLY ITS RENDERING.
                //
                // LogEventArgs carries `Name` (a resource name) and `ResourceType` next to
                // `Message`, and NinjaTrader builds the text from those two through a
                // ResourceManager - the signature says so:
                //     Log.Process(Type resourceType, String name, Object[] args, LogLevel, LogCategories)
                // So matching on the English wording would break on a NinjaTrader update or a
                // different UI language, while the NAME is what the caller passed and stays.
                // Both are captured; callers should match on the name.
                Type et = e.GetType();
                object tm = null, lv = null, cat = null, msg = null, nm = null, rt = null;
                PropertyInfo p1 = et.GetProperty("Time");         if (p1 != null) tm = p1.GetValue(e, null);
                PropertyInfo p2 = et.GetProperty("LogLevel");     if (p2 != null) lv = p2.GetValue(e, null);
                PropertyInfo p3 = et.GetProperty("LogCategory");  if (p3 != null) cat = p3.GetValue(e, null);
                PropertyInfo p4 = et.GetProperty("Message");      if (p4 != null) msg = p4.GetValue(e, null);
                PropertyInfo p5 = et.GetProperty("Name");         if (p5 != null) nm = p5.GetValue(e, null);
                PropertyInfo p6 = et.GetProperty("ResourceType"); if (p6 != null) rt = p6.GetValue(e, null);
                Type rtt = rt as Type;
                string line = (tm is DateTime ? ((DateTime)tm).ToString("yyyy-MM-dd HH:mm:ss.fff") : "?")
                            + "|" + lv + "|" + cat
                            + "|" + (nm == null ? "-" : nm.ToString())
                            + "|" + (rtt == null ? "-" : rtt.Name)
                            + "|" + msg;
                lock (_ntLogGate)
                {
                    _ntLog.Add(line);
                    if (_ntLog.Count > NtLogMax)
                    {
                        int drop = _ntLog.Count - NtLogMax;
                        _ntLog.RemoveRange(0, drop);
                        _ntLogDropped += drop;      // the mark counts entries, not slots
                    }
                }

                // ⚠ ALSO TO A FILE, and that is the whole point.
                //
                // This handler runs on NinjaTrader's own log thread. The bridge's request
                // worker is a different thread, and when it is stuck inside a UI call - which
                // happened three times on 2026-08-19 - every stage stops answering, including
                // the one that would report this buffer. A file written from HERE stays
                // readable while that is going on, so for the first time the question "what
                // did NinjaTrader report last before it stopped?" has an answer.
                //
                // Appending is deliberate: a reader that arrives late must still see what
                // happened, and a run produces a handful of entries, not a stream.
                if (!_ntLogToFile) return;   // off unless a debugging session asked for it
                try
                {
                    if (_ntLogFile == null)
                    {
                        string dir = Path.Combine(Globals.UserDataDir, "NT8Bridge");
                        Directory.CreateDirectory(dir);
                        _ntLogFile = Path.Combine(dir, "ntlog.txt");
                    }
                    File.AppendAllText(_ntLogFile, line + Environment.NewLine);
                }
                catch { }   // never let logging break the thing being logged
            }
            catch { }
        }

        // ── When the Playback panel becomes usable, as an EVENT ──────────────────────────────
        //
        //  Polling `slider.IsEnabled` against a deadline was wrong twice over:
        //    * it needs a number nobody can know. It took ~6 s here and the user says it can
        //      take minutes; any deadline is either a false alarm or a long stall.
        //    * running it late reports "never went grey" for a panel that is perfectly fine.
        //
        //  WPF raises IsEnabledChanged whenever the value flips, so the change can be
        //  RECORDED instead of hunted for. The stage then answers instantly with the history
        //  and the caller waits for what it needs - as long as that takes - without anyone
        //  guessing a duration.
        private static readonly List<string> _panelFlips = new List<string>();
        private static readonly object _panelGate = new object();
        private static bool _panelWatched;
        private static string _panelWatchedIdent;
        // How many flips have fallen off the FRONT, so the mark handed to a caller can
        // be a running sequence number rather than a slot index - see _ntLogDropped.
        private static long _panelDropped;

        private void WatchPanelEnabled()
        {
            if (_panelWatched) return;
            Window wp0 = FindWindowByTitle("Playback");
            if (wp0 == null) return;
            wp0.Dispatcher.Invoke(new Action(delegate
            {
                object sl = FindElement(wp0, "slider");
                System.Windows.FrameworkElement fe = sl as System.Windows.FrameworkElement;
                if (fe == null) return;
                fe.IsEnabledChanged += OnPanelEnabledChanged;
                _panelWatched = true;
                _panelWatchedIdent = fe.GetType().Name + "#" + fe.GetHashCode();
                NotePanelFlip(fe.IsEnabled ? "enabled" : "disabled", "subscribed");
            }));
        }

        private static void NotePanelFlip(string state, string why)
        {
            lock (_panelGate)
            {
                _panelFlips.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "|" + state + "|" + why);
                if (_panelFlips.Count > 400)
                {
                    int cutP = _panelFlips.Count - 400;
                    _panelFlips.RemoveRange(0, cutP);
                    _panelDropped += cutP;
                }
            }
        }

        private static void OnPanelEnabledChanged(object sender,
            System.Windows.DependencyPropertyChangedEventArgs e)
        {
            try { NotePanelFlip(((bool)e.NewValue) ? "enabled" : "disabled", "IsEnabledChanged"); }
            catch { }
        }

        // ── Waiting on an EVENT, never on a clock ────────────────────────────────────────────
        //
        //  User, 2026-08-20 (translated from German): "NO TIMEOUTS!!!! ON NO ACTION!!! this
        //  can take several minutes when the cache is not loaded."
        //
        //  Right. Any constant in here is a guess about NinjaTrader's workload, and every
        //  guess is either a false alarm or a stall. WPF already says when the work is done:
        //
        //    EventManager.RegisterClassHandler(typeof(Window), Loaded)  -> the Playback
        //        window has been created and laid out. Registered once, class-wide, so it
        //        also catches the window NinjaTrader builds AFTER the connect returns -
        //        measured 2026-08-20: it does not exist yet at that moment.
        //    slider.IsEnabledChanged -> the controls became usable. Subscribed on the object
        //        that exists NOW, because the panel is rebuilt and a subscription on the
        //        discarded control is silent forever.
        //
        //  The wait is on a handle. The only bound is the CALLER's ttlSec - the caller's own
        //  decision about how long it is willing to wait - and never a number invented here.
        private static readonly System.Threading.ManualResetEventSlim _panelUsable
            = new System.Threading.ManualResetEventSlim(false);
        private static bool _panelClassHandler;
        private static string _panelTrace = "";

        /// <summary>Does this process have a WPF user interface at all?
        ///
        /// Inside NinjaTrader.exe it does. Inside a host that starts the platform
        /// itself there is no window and no Application object, and the whole panel
        /// machinery below has nothing to attach to.
        ///
        /// Application.Current is the discriminator: WPF sets it when an Application
        /// is constructed, which only the GUI does. Reading it can itself throw if
        /// the WPF stack is not initialised, which answers the same question.</summary>
        private static bool NoUiHost()
        {
            try { return System.Windows.Application.Current == null; }
            catch { return true; }
        }

        private void ArmPanelWatch()
        {
            _panelUsable.Reset();
            _panelTrace = "";
            // ⚠ NOTHING TO WATCH WITHOUT A UI - and the old code CRASHED here.
            //
            // Measured 2026-08-24, first headless run: no "Control Center" window,
            // so the fallback Application.Current.Dispatcher ran, and
            // Application.Current is null in a process that never built an
            // Application. The connect stage died on its very first statement,
            // which is why its run.json held no steps at all.
            //
            // What is recorded is what was FOUND, not what was expected - if
            // Application.Current is ever non-null here, this guard does not fire
            // and the trace says so.
            if (NoUiHost())
            {
                _panelTrace += "noUi:appCurrentNull;";
                return;
            }
            _panelTrace += "ui:appCurrentPresent;";
            Window cc0 = FindWindowByTitle("Control Center");
            System.Windows.Threading.Dispatcher disp = cc0 != null ? cc0.Dispatcher
                : System.Windows.Application.Current.Dispatcher;
            disp.Invoke(new Action(delegate
            {
                if (!_panelClassHandler)
                {
                    System.Windows.EventManager.RegisterClassHandler(typeof(Window),
                        System.Windows.FrameworkElement.LoadedEvent,
                        new System.Windows.RoutedEventHandler(OnAnyWindowLoaded));
                    _panelClassHandler = true;
                }
            }));
            // OUTSIDE the dispatcher: HookPlaybackSlider marshals to each window's
            // own dispatcher, and a nested cross-dispatcher Invoke is exactly what
            // froze NinjaTrader three times on 2026-08-19.
            HookPlaybackSlider();
        }

        private static void OnAnyWindowLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            try
            {
                Window w = sender as Window;
                if (w == null) return;
                string ti = null;
                try { ti = w.Title; } catch { }
                // Every load is recorded, not just the match: with only the match
                // recorded, an empty trace could not tell "the handler never fired"
                // from "it fired and the title did not match yet" - the title may
                // still be empty when Loaded is raised.
                if (_panelTrace.Length < 400)
                    _panelTrace += "wl:" + (ti == null ? "<null>" : ti) + ";";
                if (ti == null || ti.IndexOf("Playback", StringComparison.OrdinalIgnoreCase) < 0) return;
                _panelTrace += "windowLoaded;";
                HookPlaybackSlider();
            }
            catch { }
        }

        // ⚠ MEASURED BROKEN 2026-08-20, and fixed here. The wait reported "panel
        // STILL not usable after 120000 ms" with an EMPTY trace, while reading the
        // very same controls directly said window=enabled after 12236 ms - the
        // documented ~11.3/11.9 s. Two defects, both in this one method:
        //
        //   1. It enumerated Application.Current.Windows. FindWindowByTitle says
        //      why that is wrong: NinjaTrader is multi-UI-threaded and that
        //      collection does not list every window. Every other lookup in this
        //      file already uses AllWindowsIncludingOwned(); this was the outlier.
        //   2. Every failure returned SILENTLY, so an empty trace could mean "no
        //      events yet" or "never even found the window". A guard that cannot
        //      say it failed is not a guard - every path now writes its outcome.
        //
        // WPF members are thread-affine, so the tree is touched on the window's OWN
        // dispatcher. This method must therefore NOT be called from inside another
        // dispatcher - nested waiting across dispatchers froze NinjaTrader three
        // times on 2026-08-19. ArmPanelWatch calls it from the worker thread.
        private static void HookPlaybackSlider()
        {
            try
            {
                foreach (Window w0 in AllWindowsIncludingOwned())
                {
                    Window w = w0;
                    string ti = null;
                    try { ti = (string)w.Dispatcher.Invoke(new Func<string>(delegate { return w.Title; })); }
                    catch { continue; }
                    if (ti == null || ti.IndexOf("Playback", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string res;
                    try
                    {
                        res = (string)w.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            object sl = FindElementStatic(w, "slider");
                            System.Windows.FrameworkElement f = sl as System.Windows.FrameworkElement;
                            if (f == null) return "windowButNoSlider;";
                            f.IsEnabledChanged -= OnPanelUsableChanged;
                            f.IsEnabledChanged += OnPanelUsableChanged;
                            if (f.IsEnabled) { _panelUsable.Set(); return "hooked;alreadyEnabled;"; }
                            return "hooked;";
                        }));
                    }
                    catch (Exception ex) { res = "hookThrew:" + ex.GetType().Name + ";"; }
                    _panelTrace += res;
                    return;
                }
                _panelTrace += "noPlaybackWindow;";
            }
            catch (Exception ex) { _panelTrace += "enumThrew:" + ex.GetType().Name + ";"; }
        }

        private static void OnPanelUsableChanged(object sender,
            System.Windows.DependencyPropertyChangedEventArgs e)
        {
            try
            {
                bool v = (bool)e.NewValue;
                _panelTrace += v ? "enabled;" : "disabled;";
                if (v) _panelUsable.Set(); else _panelUsable.Reset();
            }
            catch { }
        }

        // Static twin of FindElement, so the class handler can use it without an instance.
        private static object FindElementStatic(System.Windows.DependencyObject root, string name)
        {
            if (root == null) return null;
            System.Windows.FrameworkElement fe = root as System.Windows.FrameworkElement;
            if (fe != null && fe.Name == name) return fe;
            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); } catch { return null; }
            for (int i = 0; i < n; i++)
            {
                object hit = FindElementStatic(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
                if (hit != null) return hit;
            }
            return null;
        }

        // The build's own maximum, so "run" means the same thing the panel means by Max.
        private static int MaxSpeedOrOne()
        {
            try
            {
                Type pbT = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                FieldInfo fi = pbT == null ? null : pbT.GetField("MaxSpeedValue", BFStatic);
                object v = fi != null ? fi.GetValue(null) : null;
                if (v is int && (int)v > 0) return (int)v;
            }
            catch { }
            return 1;
        }

        private static string SetPlaybackSpeed(int want, out int readBack)
        {
            readBack = -1;
            Type pbT = null;
            try { pbT = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false); }
            catch { }
            if (pbT == null) return "PlaybackAdapter did not resolve";
            PropertyInfo pi = pbT.GetProperty("PlaybackSpeed", BFStatic);
            if (pi == null || !pi.CanWrite) return "PlaybackSpeed is not writable on this build";
            try { pi.SetValue(null, want, null); }
            catch (Exception ex) { return "write threw " + ex.GetType().Name + ": " + ex.Message; }
            try { object v = pi.GetValue(null, null); if (v is int) readBack = (int)v; } catch { }
            return readBack == want ? null
                 : "wrote " + want + " but it reads back " + (readBack < 0 ? "unreadable" : readBack.ToString());
        }
        // Is the transport advancing? Two NowEst samples a real gap apart. A single
        // reading cannot tell a parked clock from a running one, and btnPlay.IsChecked
        // answers a different question entirely (see ParkTransport).
        private bool TransportMoving(out DateTime seen)
        {
            seen = DateTime.MinValue;
            try
            {
                Type pbT = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                if (pbT == null) return false;
                PropertyInfo piNow = pbT.GetProperty("NowEst", BFStatic);
                if (piNow == null) return false;
                // NinjaTrader refreshes NowEst on its own timer (playbackUiTimerMs =
                // 1000), so the gap has to clear one tick of that timer - but only just.
                // 1200 ms is enough: at the slowest possible rate, 1x, that is 1.2 s of
                // data time, comfortably over a 0.5 s threshold.
                //
                // It used to be 2 x 2500 ms. Five seconds per measurement, called up to
                // five times by ParkTransport, made a cleanup take ~25 s - and a teardown
                // that slow does not get run when it matters. The cost was mine, not
                // NinjaTrader's.
                DateTime prev = (DateTime)piNow.GetValue(null);
                seen = prev;
                Thread.Sleep(1200);
                DateTime now1 = (DateTime)piNow.GetValue(null);
                seen = now1;
                return (now1 - prev).TotalSeconds > 0.5;
            }
            catch (Exception ex) { LogSafe("TransportMoving: " + ex.Message); return false; }
        }

        // Stop the transport and PROVE it stopped.
        //
        // Parking is a write of PlaybackSpeed=0 - no button, no window, no dispatcher.
        // The result is verified against the CLOCK, never against the button: measured
        // 2026-08-19, btnPlay.IsChecked read false while NowEst kept advancing, and the
        // caller went on to configure a strategy on top of a running transport.
        private bool ParkTransport(Action<string, bool, string> step, int tries)
        {
            DateTime seen;
            if (!TransportMoving(out seen))
            {
                if (step != null) step("transport parked", true, "already parked at " + seen);
                return true;
            }

            // Speed 0 parks it. No window, no button, no dispatcher: PlaybackSpeed is a
            // static property. The play button used to be pressed here, which needed the
            // Playback window to exist and to be in a state that accepts the press.
            for (int i = 0; i < tries; i++)
            {
                int back;
                string problem = SetPlaybackSpeed(0, out back);
                // Wait OUTSIDE any dispatcher - NinjaTrader has to act on the write.
                Thread.Sleep(500);
                if (!TransportMoving(out seen))
                {
                    if (step != null)
                        step("transport parked", true,
                             "parked at " + seen + " after writing PlaybackSpeed=0"
                             + (problem == null ? "" : "  (" + problem + ")"));
                    return true;
                }
                if (problem != null && step != null) LogSafe("park: " + problem);
            }
            if (step != null)
                step("transport parked", false,
                     "STILL RUNNING at " + seen + " after " + tries + " write(s) of PlaybackSpeed=0");
            return false;
        }

        // Where a dialog sat before it was moved off-screen, so `unhide` can put it
        // back. Keyed by the window instance.
        private readonly Dictionary<object, double[]> _hiddenAt = new Dictionary<object, double[]>();

        private const double OffScreenX = -32000.0;
        private const double OffScreenY = -32000.0;

        // Move every open ObjectDialog off-screen. It stays realised and fully
        // functional - unlike Visibility=Hidden, which stops the window rendering and
        // can leave property-grid items unrealised, and an unrealised item has no
        // visual-tree node for SetByLabel to find.
        private int HideDialogs(Action<string, bool, string> step)
        {
            int moved = 0;
            foreach (Window w in AllWindowsIncludingOwned())
            {
                string ty = null;
                try { ty = w.GetType().FullName; } catch { }
                if (ty == null || ty.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) < 0) continue;
                Window ww = w;
                try
                {
                    // Move it out of sight, then READ THE POSITION BACK. A step that
                    // reports "-> off-screen" without looking is the same self-confirming
                    // pattern that made an unset account read as set (2026-08-19).
                    // WindowState and ShowInTaskbar go with it: a maximised window ignores
                    // Left/Top, and a taskbar button is visible even when the window is not.
                    double[] pos = (double[])ww.Dispatcher.Invoke(new Func<double[]>(delegate
                    {
                        if (!_hiddenAt.ContainsKey(ww))
                            _hiddenAt[ww] = new double[] { ww.Left, ww.Top };
                        if (ww.WindowState != System.Windows.WindowState.Normal)
                            ww.WindowState = System.Windows.WindowState.Normal;
                        ww.ShowInTaskbar = false;
                        ww.Left = OffScreenX;
                        ww.Top = OffScreenY;
                        ww.UpdateLayout();
                        return new double[] { ww.Left, ww.Top };
                    }));
                    bool gone = pos[0] <= -10000.0 && pos[1] <= -10000.0;
                    moved++;
                    if (step != null)
                        step(gone ? "hidden" : "hide DID NOT STICK", gone,
                             ty + " at (" + pos[0].ToString("0", InvCi) + "," +
                             pos[1].ToString("0", InvCi) + ")");
                }
                catch (Exception ex)
                {
                    if (step != null) step("hide failed", false, ty + ": " + ex.Message);
                }
            }
            if (step != null && moved == 0) step("hidden", true, "no dialog open");
            return moved;
        }

        private int UnhideDialogs(Action<string, bool, string> step)
        {
            int moved = 0;
            foreach (Window w in AllWindowsIncludingOwned())
            {
                Window ww = w;
                double[] at;
                if (!_hiddenAt.TryGetValue(ww, out at)) continue;
                try
                {
                    double[] a = at;
                    ww.Dispatcher.Invoke(new Action(delegate { ww.Left = a[0]; ww.Top = a[1]; }));
                    _hiddenAt.Remove(ww);
                    moved++;
                    if (step != null) step("shown", true, ww.GetType().FullName + " -> back");
                }
                catch (Exception ex)
                {
                    if (step != null) step("unhide failed", false, ex.Message);
                }
            }
            if (step != null && moved == 0) step("shown", true, "nothing was hidden");
            return moved;
        }

        // ---- driver lease -------------------------------------------------
        // A driver announces "I am working" with `leaseSec`. If it stops sending
        // requests before the deadline, it died - and the AddOn cleans up, because
        // a killed process cannot run its own teardown. See the file header.
        private DateTime _leaseUntilUtc = DateTime.MinValue;
        private bool _leaseRestoreDone = true;
        // The last lease span the driver declared - it becomes the budget of a
        // lease-triggered RestoreBaselineNow (the caller's ttl, per the rule that
        // no wait invents its own bound). 120 is the span the driver sends today
        // (playback_run.py stage(): leaseSec=120); overwritten by every request.
        private double _lastLeaseSec = 120.0;

        private void NoteLease(string json)
        {
            if (json == null) return;
            const string pat = "\"leaseSec\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return;
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return;
            int p2 = c + 1;
            while (p2 < json.Length && (char.IsWhiteSpace(json[p2]) || json[p2] == '"')) p2++;
            int s = p2;
            while (p2 < json.Length && (char.IsDigit(json[p2]) || json[p2] == '.')) p2++;
            double v;
            if (p2 <= s || !double.TryParse(json.Substring(s, p2 - s),
                    System.Globalization.NumberStyles.Float, InvCi, out v)) return;
            if (v <= 0.0)
            {
                _leaseUntilUtc = DateTime.MinValue;      // clean exit - nothing to guard
                _leaseRestoreDone = true;
                return;
            }
            _leaseUntilUtc = DateTime.UtcNow.AddSeconds(v);
            _lastLeaseSec = v;
            _leaseRestoreDone = false;
        }

        private void CheckLease()
        {
            try
            {
                if (_leaseRestoreDone) return;
                if (_leaseUntilUtc == DateTime.MinValue) return;
                if (DateTime.UtcNow <= _leaseUntilUtc) return;
                _leaseRestoreDone = true;                 // fire exactly once
                _leaseUntilUtc = DateTime.MinValue;
                LogSafe("LEASE EXPIRED - the driver is gone. Restoring the baseline.");
                RestoreBaselineNow(_lastLeaseSec);
            }
            catch (Exception ex) { LogSafe("CheckLease: " + ex.Message); }
        }

        // The same repair the driver would have done: park, disable, remove, close.
        // Runs off the poller thread; every UI touch goes through the owning
        // dispatcher, and nothing here sleeps inside one.
        //
        // ttlSec is the CALLER's budget - the only bound any wait in here may use
        // (restore stage: the request's ttlSec; lease restore: the lease span the
        // driver declared). Measured 2026-08-20 (bridge.log): with the previous
        // hard-coded 300.0 the strategy-row block sat out the FULL five minutes
        // after every finished run - two pairs "restore disconnect confirmed" ->
        // "LEASE RESTORE done." lie exactly 300.07 s apart (20:31:56->20:36:56Z,
        // 20:41:43->20:46:43Z), while empty cases finish in 2-3 ms. Cause: the
        // wait watched for a 'Disabling' LOG LINE, but the disconnect right above
        // had already disabled the strategy BEFORE the mark was taken (NT8 log
        // 22:13:45.913 Disabling vs .919 Disconnecting), and no line arrived
        // after the mark. The row block below now waits for the EFFECT instead.
        private void RestoreBaselineNow(double ttlSec)
        {
            try
            {
                // ⚠ PUT THE SOURCE BACK TO MARKET REPLAY *BEFORE* DISCONNECTING.
                //
                // This is what makes a Historical run repeatable within one NinjaTrader
                // session, and it has to happen before the disconnect, because that is
                // when NinjaTrader persists the value.
                //
                // NOT the panel radios. That was tried first and did nothing, and the
                // measurement says why: at the teardown of a Historical run the panel
                // still showed Market Replay - it follows the adapter only on the next
                // connect, as the note in the connect stage records - so a condition on
                // rbHistoricalData never fired, silently. NinjaTrader nevertheless wrote
                // True, so the persisted value comes from the ADAPTER STATIC, not from
                // the radio buttons.
                //
                // What it prevents, measured three times on 25.08.2026:
                //     15:39:37.897 Playback: Disconnected   -> 15:39:37.926 UI.xml = True
                //     16:18:13.016 Playback: Disconnected   -> 16:18:13.045 UI.xml = True
                // Both writes land 29 ms after the disconnect of a HISTORICAL run, and
                // every connect afterwards dies with
                //     Playback: Error in opening connection Playback, exception caught:
                //     String was not recognized as a valid DateTime. (Panic)
                // - 4 attempts, 4 panics, including Market Replay, which had connected
                // three times an hour earlier. With the value at False: 10 connects, 0
                // panics. The GUI-less host never sees any of this, because it has no
                // Playback panel to rebuild.
                //
                // BeginInvoke, not Invoke: writing IsChecked makes NinjaTrader rebuild
                // the transport on the UI thread, and a synchronous call from inside
                // that same dispatcher waits on itself (measured 2026-08-18/19, the
                // stage stopped answering). Posting it is enough - the disconnect right
                // below gives NinjaTrader the whole rebuild it wants.
                try
                {
                    // Same lookup as everywhere else in this file: the adapter lives
                    // in the assembly that carries Connection.
                    Type pbT = typeof(Connection).Assembly
                        .GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                    PropertyInfo piSrc = pbT == null
                        ? null : pbT.GetProperty("IsSourceHistoricalData", BFStatic);
                    if (piSrc == null)
                        LogStatic("restore: IsSourceHistoricalData not reachable - "
                                  + "the next connect may panic (see the note above)");
                    else
                    {
                        object before = piSrc.GetValue(null);
                        piSrc.SetValue(null, false, null);
                        object after = piSrc.GetValue(null);
                        LogStatic("restore: IsSourceHistoricalData " + before + " -> " + after
                                  + " (before the disconnect, so NinjaTrader persists False)");
                    }
                }
                catch (Exception ex) { LogStatic("restore source reset: " + ex.Message); }

                // ⚠ DISCONNECT FIRST - it is the universal abort.
                //
                // Measured 2026-08-19: a disconnect stops the transport immediately
                // (timer.Enabled=False, RealtimeTickCount delta=0, clock frozen), while
                // parking has to press a button and then MEASURE whether it worked. The
                // cleanup used to park first and cost ~25 s of its own sleeps; a teardown
                // that slow does not get run when it is needed. Disconnecting costs one
                // call and cannot fail to stop things.
                try
                {
                    PropertyInfo piPC = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                    Connection pc = piPC != null ? piPC.GetValue(null) as Connection : null;
                    if (pc != null && pc.Status != ConnectionStatus.Disconnected)
                    {
                        // ⚠ TRIGGER, THEN WAIT FOR NINJATRADER'S OWN ENTRY - not for 600 ms.
                        // The old sleep was a guess; disconnecting a busy connection can take
                        // longer, and then everything measured afterwards was measured too early.
                        int mark = NtLogCount();
                        pc.Disconnect();
                        string hit; int idx;
                        bool got = WaitForLogName(mark, "CbiConnectionProcessConnectionStatusUpdate",
                                                  "Disconnected", ttlSec, out hit, out idx);
                        LogSafe(got ? ("restore disconnect confirmed: " + hit)
                                    : "restore disconnect: NinjaTrader never reported Disconnected");
                    }
                }
                catch (Exception ex) { LogSafe("restore disconnect: " + ex.Message); }

                Window cc = FindWindowByTitle("Control Center");
                if (cc != null)
                {
                    // Disable + Remove through the statics behind the menu entries. No
                    // selection, no context menu, no modal to answer: Disable comes first,
                    // so NinjaTrader never asks.
                    for (int guard = 0; guard < 10; guard++)
                    {
                        Window ccx = cc;
                        List<StrategyBase> live = (List<StrategyBase>)ccx.Dispatcher.Invoke(
                            new Func<List<StrategyBase>>(delegate
                        {
                            var outp = new List<StrategyBase>();
                            object grid = FindElement(ccx, "grdStrategies");
                            foreach (var kv in GridRows(grid))
                                if (kv.Value != null) outp.Add(kv.Value);
                            return outp;
                        }));
                        if (live.Count == 0) break;
                        // ⚠ One entry per strategy, and NinjaTrader says when each is off.
                        // The key is MEASURED, not derived from the message text: stage `resname`
                        // over NinjaTrader.Resource returns
                        //     NinjaScriptStrategyBaseDisabling   = "Disabling NinjaScript strategy '{0}'"
                        //     NinjaScriptStrategyBaseEnabling1/2 = "Enabling NinjaScript strategy '{0}' : ..."
                        // The first version here said "NinjaScriptStrategyDisabl", invented from the
                        // English wording. It would never have matched - and a wait that never
                        // matches is indistinguishable from a hang.
                        // The old sleep(1200) assumed every disable finishes inside 1.2 s.
                        LogSafe("restore: " + live.Count + " strategy row(s) live - firing Disable+Remove");
                        foreach (StrategyBase s in live) FireDisableRemove(ccx, s);
                        // ⚠ WAIT FOR THE EFFECT, NOT FOR A LOG LINE.
                        //
                        // The goal of this block is: the rows are GONE. The old wait
                        // watched for NinjaTrader's 'Disabling' log entry per row - a
                        // line that only appears when the row was still enabled, and
                        // after a finished run the disconnect above has already
                        // disabled it (measured 2026-08-20, see the method header):
                        // the wait then sat out its full bound on every clean run.
                        // The row count in the grid is the direct, bot-free effect
                        // channel - the same source `attach` uses for row-appeared.
                        string rowsGotR; long rowsMsR;
                        Window ccR = ccx;
                        WaitUntilChanged(delegate
                        {
                            try
                            {
                                return "" + (int)ccR.Dispatcher.Invoke(new Func<int>(delegate
                                {
                                    object gR = FindElement(ccR, "grdStrategies");
                                    int nR = 0;
                                    foreach (var kvR in GridRows(gR)) if (kvR.Value != null) nR++;
                                    return nR;
                                }));
                            }
                            catch { return "?"; }
                        }, "" + live.Count, "0", ttlSec, out rowsGotR, out rowsMsR);
                        LogSafe("restore: strategy rows " + live.Count + " -> " + rowsGotR
                                + " after " + rowsMsR + " ms");
                        string modal = ccx.Dispatcher.Invoke(new Func<string>(delegate { return StandingModal(); })) as string;
                        if (modal != null) LogSafe("LEASE RESTORE: a modal is standing (" + modal
                                                   + ") - not clicked, reported");
                    }
                }

                HideDialogs(null);          // park them out of sight first
                foreach (Window w in AllWindowsIncludingOwned())
                {
                    string ty = null;
                    try { ty = w.GetType().FullName; } catch { }
                    if (ty == null || ty.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Window ww = w;
                    try { ww.Dispatcher.Invoke(new Action(delegate { ww.Close(); })); } catch { }
                }
                LogSafe("LEASE RESTORE done.");
            }
            catch (Exception ex) { LogSafe("RestoreBaselineNow: " + ex.Message); }
        }

        // ---- playback events -----------------------------------------------
        // A real end-of-run signal instead of "the clock has not moved for 60 s".
        //
        // PlaybackAdapter exposes STATIC events (seen in the member dump). Two of
        // them are subscribed here, IsAvailableChanged and OnReset, which turns
        // "finished" from something inferred into something reported - with the DATA
        // timestamp of the moment it happened, which is the only timestamp worth
        // recording in a run that spans months of trading days. One handler serves
        // both, so an entry says that an event arrived, not which one.
        private readonly List<string> _pbEvents = new List<string>();
        private bool _pbSubscribed;

        private void NotePlaybackEvent(string name)
        {
            try
            {
                Type pbT = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                string nowEst = "?", avail = "?";
                if (pbT != null)
                {
                    PropertyInfo pn = pbT.GetProperty("NowEst", BFStatic);
                    if (pn != null) { try { nowEst = ((DateTime)pn.GetValue(null)).ToString("dd.MM.yyyy HH:mm:ss"); } catch { } }
                    PropertyInfo pa = pbT.GetProperty("IsAvailable", BFStatic);
                    if (pa != null) { try { avail = "" + pa.GetValue(null); } catch { } }
                }
                lock (_pbEvents)
                {
                    _pbEvents.Add(name + " | dataTime=" + nowEst + " | IsAvailable=" + avail);
                    while (_pbEvents.Count > 60) _pbEvents.RemoveAt(0);
                }
                LogSafe("playback event: " + name + " dataTime=" + nowEst + " IsAvailable=" + avail);
            }
            catch { }
        }

        private void SubscribePlaybackEvents()
        {
            if (_pbSubscribed) return;
            try
            {
                Type pbT = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                if (pbT == null) return;
                foreach (string evName in new string[] { "IsAvailableChanged", "OnReset" })
                {
                    EventInfo ev = pbT.GetEvent(evName, BFStatic);
                    if (ev == null) { LogSafe("event " + evName + " not found"); continue; }
                    string captured = evName;
                    Delegate h = Delegate.CreateDelegate(
                        ev.EventHandlerType, this,
                        GetType().GetMethod("OnPbEvent", BindingFlags.Instance | BindingFlags.NonPublic));
                    // The handler signature has to match the event; both are
                    // EventHandler<EventArgs>, so one method serves both. Which one fired
                    // is recovered from the sender-less call by subscribing separately.
                    ev.AddEventHandler(null, h);
                    LogSafe("subscribed to PlaybackAdapter." + captured);
                }
                _pbSubscribed = true;
            }
            catch (Exception ex) { LogSafe("SubscribePlaybackEvents: " + ex.Message); }
        }

        private void OnPbEvent(object sender, EventArgs e)
        {
            NotePlaybackEvent("PlaybackAdapter event");
        }

        // Property accessors would flood the listing; they show up as properties anyway.
        private static bool IsAccessor(MethodInfo mi)
        {
            return mi.IsSpecialName && (mi.Name.StartsWith("get_", StringComparison.Ordinal)
                                     || mi.Name.StartsWith("set_", StringComparison.Ordinal)
                                     || mi.Name.StartsWith("add_", StringComparison.Ordinal)
                                     || mi.Name.StartsWith("remove_", StringComparison.Ordinal));
        }

        // ── Templates, without a dialog and without a click ──────────────────────────────────
        //  The `load` button in the strategy dialog calls
        //      StrategyTemplate.LoadTemplate(Window owner, StrategyBase, out StrategyBase)
        //  which needs a WINDOW and opens a picker - the path ruled out on 2026-08-19
        //  ("no mouse clicks in the GUI - in the cli bridge or the cli"). The three statics
        //  below do the same work with no UI at all. Their signatures were read back from the
        //  running NinjaTrader with the `findmethod` stage rather than assumed:
        //      GetTemplateFolder(StrategyBase)                    : String
        //      RestoreFullStrategyTemplate(XContainer)            : StrategyBase
        //      SaveFullStrategyTemplate(XContainer, StrategyBase) : Void
        // Reflection wraps everything in TargetInvocationException("Exception has been thrown
        // by the target of an invocation."), which names neither the cause nor the place. The
        // real exception sits in InnerException - report that one.
        private static string Deep(Exception ex)
        {
            if (ex == null) return "(null)";
            Exception cur = ex;
            int guard = 0;
            while (cur.InnerException != null && guard++ < 8) cur = cur.InnerException;
            string s = cur.GetType().Name + ": " + cur.Message;
            if (!ReferenceEquals(cur, ex)) s = s + "   [via " + ex.GetType().Name + "]";
            if (!string.IsNullOrEmpty(cur.StackTrace))
            {
                string[] lines = cur.StackTrace.Split(new char[] { (char)10 });
                if (lines.Length > 0) s = s + "   @ " + lines[0].Trim();
            }
            return s;
        }

        private static Type TemplateType()
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type ty = null;
                try { ty = asm.GetType("NinjaTrader.Gui.NinjaScript.StrategyTemplate", false); }
                catch { }
                if (ty != null) return ty;
            }
            return null;
        }

        // The folder is asked of NinjaTrader, never assembled from a naming rule: it differs
        // per bot (flat "Strategy\Foo" vs nested "Strategy\Foo.Foo") and NT8 creates it when
        // the strategy first appears.
        private static string TemplateFolder(StrategyBase strat)
        {
            Type tt = TemplateType();
            if (tt == null || strat == null) return null;
            MethodInfo mi = tt.GetMethod("GetTemplateFolder",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) return null;
            try { return (string)mi.Invoke(null, new object[] { strat }); }
            catch { return null; }
        }

        // Resolve a strategy type by simple name. Bots declare either Strategies.Foo or
        // Strategies.Foo.Foo, so both are tried before a full type scan; ambiguity is
        // REPORTED, never silently resolved.
        private static Type ResolveStrategyType(string typeName, out string problem)
        {
            problem = null;
            if (string.IsNullOrWhiteSpace(typeName)) { problem = "no strategy name given"; return null; }
            string[] candidates = new string[] {
                "NinjaTrader.NinjaScript.Strategies." + typeName,
                "NinjaTrader.NinjaScript.Strategies." + typeName + "." + typeName };
            // Every `reload` leaves the PREVIOUS NinjaTrader.Custom in the AppDomain, so the
            // same class exists more than once with identical FullName and different Type
            // identity. Taking the first hit would silently instantiate the OLD build of the
            // strategy. GetAssemblies() returns load order, so the LAST match is the freshest.
            Type newest = null;
            int seen = 0;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                foreach (string c in candidates)
                {
                    Type hit = null;
                    try { hit = asm.GetType(c, false); } catch { }
                    if (hit != null) { newest = hit; seen++; }
                }
            if (newest != null)
            {
                if (seen > 1) problem = "note: " + seen + " loaded copies, took the newest";
                return newest;
            }
            List<Type> hits2 = new List<Type>();
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] all;
                try { all = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtl) { all = rtl.Types; }
                catch { continue; }
                foreach (Type ty in all)
                    if (ty != null && ty.Name == typeName && typeof(StrategyBase).IsAssignableFrom(ty))
                        hits2.Add(ty);
            }
            if (hits2.Count == 1) return hits2[0];
            if (hits2.Count > 1)
            {
                List<string> n2 = new List<string>();
                foreach (Type ty in hits2) n2.Add(ty.FullName);
                problem = "ambiguous: " + string.Join(" | ", n2.ToArray());
                return null;
            }
            problem = typeName + " in no loaded assembly";
            return null;
        }

        // A bare name is resolved inside the bot's own template folder; an absolute path is
        // taken as given, because that is the form the CLI parameter carries.
        private static string TemplatePath(StrategyBase strat, string nameOrPath)
        {
            if (string.IsNullOrWhiteSpace(nameOrPath)) return null;
            if (nameOrPath.IndexOf(Path.DirectorySeparatorChar) >= 0 || nameOrPath.IndexOf(':') >= 0)
                return nameOrPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                     ? nameOrPath : nameOrPath + ".xml";
            string folder = TemplateFolder(strat);
            if (string.IsNullOrEmpty(folder)) return null;
            return Path.Combine(folder, nameOrPath.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                                        ? nameOrPath : nameOrPath + ".xml");
        }

        // Returns the strategy the template describes. NOT a property copy onto an existing
        // instance: that would require deciding member by member what is configuration and what
        // is runtime state (State, Account, Instrument), and every wrong call there is silent.
        private static StrategyBase RestoreTemplate(string file, out string problem)
        {
            problem = null;
            Type tt = TemplateType();
            if (tt == null) { problem = "StrategyTemplate type not found"; return null; }
            MethodInfo mi = tt.GetMethod("RestoreFullStrategyTemplate",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (mi == null) { problem = "RestoreFullStrategyTemplate not found"; return null; }
            if (!File.Exists(file)) { problem = "not found: " + file; return null; }
            try
            {
                System.Xml.Linq.XDocument doc = System.Xml.Linq.XDocument.Load(file);
                // Hand over the ROOT ELEMENT, symmetric to Save, which fills that element.
                // A whole document looks for <StrategyType>/<Strategy> as its own children -
                // and below a document there is only <StrategyTemplate>, so it found nothing
                // and returned null. Proven by the positive control: a template written by
                // NinjaTrader ITSELF failed the same way, which ruled out the file.
                System.Xml.Linq.XContainer node =
                    (System.Xml.Linq.XContainer)doc.Root ?? (System.Xml.Linq.XContainer)doc;
                object r = mi.Invoke(null, new object[] { node });
                StrategyBase sbr = r as StrategyBase;
                if (sbr == null) { problem = "template produced no StrategyBase"; return null; }
                return sbr;
            }
            catch (Exception ex) { problem = ex.GetType().Name + ": " + ex.Message; return null; }
        }

        // ── what the replay STORE holds ─────────────────────────────────────────
        //  The days available for one instrument, read from the files. Upstream's
        //  `playback` command reports exactly this as its "coverage" block, using
        //  NinjaTrader's own GetReplayMinMaxDates; this is the same call without
        //  the JSON, for the guard that has to decide whether a requested window
        //  can play at all.
        //
        //  The file NAME is used only to find the files. Every date comes from the
        //  bytes - a day whose name says 20260707 but whose content is another day
        //  would be counted where its content puts it, which is the only place it
        //  can be played from.
        //
        //  Returns false when the scan cannot be done at all (no such method, no
        //  such folder, nothing readable). The caller then says so instead of
        //  treating an unavailable scan as an empty store.
        private static bool ReplayCoverage(string instrument, out DateTime lo,
                                           out DateTime hi, out int readable)
        {
            lo = DateTime.MaxValue;
            hi = DateTime.MinValue;
            readable = 0;
            try
            {
                Type pb = typeof(Connection).Assembly.GetType(
                    "NinjaTrader.Adapter.PlaybackAdapter", false);
                MethodInfo mi = pb == null ? null : pb.GetMethod(
                    "GetReplayMinMaxDates", BFStatic, null,
                    new[] { typeof(string), typeof(DateTime).MakeByRefType(),
                            typeof(DateTime).MakeByRefType() }, null);
                if (mi == null || string.IsNullOrWhiteSpace(instrument)) return false;
                string dir = Path.Combine(Globals.UserDataDir, "db", "replay", instrument);
                if (!Directory.Exists(dir)) return false;
                foreach (string f in Directory.GetFiles(dir, "*.nrd"))
                {
                    try
                    {
                        object[] args = new object[] { f, DateTime.MinValue, DateTime.MinValue };
                        mi.Invoke(null, args);
                        DateTime a = (DateTime)args[1], b = (DateTime)args[2];
                        if (a < lo) lo = a;
                        if (b > hi) hi = b;
                        readable++;
                    }
                    catch { }
                }
                return readable > 0;
            }
            catch { return false; }
        }

        /// <summary>Coverage of the NCD tick store - what Playback/Historical reads.
        ///
        ///  The counterpart to ReplayCoverage, which answers for db\replay. NCD files
        ///  are one per hour and data type (YYYYMMDDHH.Last.ncd), so the DAY is taken
        ///  from the first 8 characters of the name. That is deliberately a name-based
        ///  scan, and it is only ever used as a PRE-FLIGHT: it answers "is there
        ///  anything for that day", not "is the content that day". The content is
        ///  decided when the series loads, and a run whose data is missing still fails
        ///  loudly there. Returns false when the folder does not exist or holds no
        ///  .ncd at all - the caller then says the scan was unavailable instead of
        ///  treating it as an empty store.</summary>
        private static bool TickCoverage(string instrument, out DateTime lo,
                                         out DateTime hi, out int readable)
        {
            lo = DateTime.MaxValue;
            hi = DateTime.MinValue;
            readable = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(instrument)) return false;
                string dir = Path.Combine(Globals.UserDataDir, "db", "tick", instrument);
                if (!Directory.Exists(dir)) return false;
                foreach (string f in Directory.GetFiles(dir, "*.ncd"))
                {
                    string name = Path.GetFileName(f);
                    if (name.Length < 8) continue;
                    DateTime d;
                    if (!DateTime.TryParseExact(name.Substring(0, 8), "yyyyMMdd",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out d)) continue;
                    readable++;
                    if (d < lo) lo = d;
                    if (d > hi) hi = d;
                }
                if (readable == 0) return false;
                hi = hi.Date.AddDays(1).AddSeconds(-1);
                lo = lo.Date;
                return true;
            }
            catch { return false; }
        }

        // ── satemplate ──────────────────────────────────────────────────────────
        //  Put one of NinjaTrader's own strategy templates on the Strategy Analyzer
        //  tab, so a backtest can be asked for by FILE instead of by parameter list.
        //
        //  `configure` writes individual properties from a config.json onto the
        //  tab's strategy, and `backtest` does the same before it runs. That is the
        //  right shape for a handful of parameters and the wrong one for the files
        //  NinjaTrader writes itself under templates\Strategy: those carry the
        //  COMPLETE parameter set, and a test suite is usually one strategy class
        //  against many of them, identical in everything else. Naming the file beats
        //  restating hundreds of properties, and it cannot drift from what the GUI
        //  runs, because it IS what the GUI runs.
        //
        //  The template is ASSIGNED, not copied member by member. RestoreTemplate
        //  hands back the strategy the file describes - a whole instance - and the
        //  tab's StrategyTemplate takes it. Read off the running NinjaTrader with
        //  `probe` before this was written, rather than assumed:
        //      TabStrategyProperties.StrategyTemplate  StrategyBase  canWrite=True
        //      TabStrategyProperties.Strategy          String        canWrite=True
        //  Copying onto the instance already sitting there was the alternative, and
        //  it would mean deciding for each of its 438 properties whether it is
        //  configuration or runtime state (State, Account, Instrument). Every wrong
        //  call there is silent, which is why RestoreTemplate returns an instance in
        //  the first place.
        //
        //  ⚠ ORDER: `Strategy` BEFORE the template, never after. Writing the name
        //  makes NinjaTrader install a FRESH StrategyTemplate, and whatever was put
        //  on the old one is dropped without an error - upstream's own finding, in
        //  configure.py's header and its issue #6. So a template for a different
        //  class selects the class first and assigns second; doing it the other way
        //  round loses the entire template and says nothing.
        //
        //  Switching is not skipped, because a suite is many strategy CLASSES, and
        //  refusing here would put a manual click between every one of them.
        private string RunSaTemplate(string id, string text)
        {
            string want = ExtractJsonString(text, "template");
            if (string.IsNullOrWhiteSpace(want))
                return BtErr(id, "satemplate: no template given");

            Window saWin = FindStrategyAnalyzerWindow();
            if (saWin == null)
                return BtErr(id, "satemplate: no Strategy Analyzer window open");

            try
            {
                return (string)saWin.Dispatcher.Invoke(new Func<string>(delegate
                {
                    try
                    {
                        StrategyAnalyzerViewModel vm = saWin.DataContext as StrategyAnalyzerViewModel;
                        StrategyAnalyzerTabControl tab = vm != null ? vm.SelectedTab : null;
                        if (tab == null) return BtErr(id, "satemplate: no active SA tab");
                        StrategyAnalyzerTabProperties tsp = tab.TabStrategyProperties;
                        if (tsp == null)
                            return BtErr(id, "satemplate: the tab has no TabStrategyProperties");

                        StrategyBase before = tsp.StrategyTemplate;
                        string file = TemplatePath(before, want);
                        if (string.IsNullOrEmpty(file))
                            return BtErr(id, "satemplate: cannot resolve template " + want
                                + " - give a full path, or open the strategy so its"
                                + " template folder is known");

                        string problem;
                        StrategyBase restored = RestoreTemplate(file, out problem);
                        if (restored == null)
                            return BtErr(id, "satemplate: " + problem);

                        string had = before != null ? before.GetType().FullName : "(none)";
                        string got = restored.GetType().FullName;
                        bool switched = false;
                        if (!string.Equals(got, had, StringComparison.Ordinal))
                        {
                            // The tab names the strategy the way its own dropdown does,
                            // by SHORT name, not by full type. Read off a running tab
                            // with `probe`, where Strategy held the bare class name
                            // beside a StrategyTemplate whose type was that same name
                            // under NinjaTrader.NinjaScript.Strategies.
                            tsp.Strategy = restored.GetType().Name;
                            switched = true;
                        }

                        tsp.StrategyTemplate = restored;

                        // ⚠ THE TAB KEEPS ITS OWN INSTRUMENT. It is not taken from the
                        // template, measured on a running NinjaTrader: after assigning
                        // a template that names NQ 06-26, the strategy read NQ 06-26
                        // and the tab still read ES 09-26. A run in that state applies
                        // the template's parameters to the tab's instrument and says
                        // nothing about it - a wrong result that looks like a right
                        // one. So the name travels, when the template carries one.
                        //
                        // Written AFTER the template, and read back below together
                        // with it: if this write were to reinstall the template the
                        // way `Strategy` does, the reference check would catch it.
                        string tmplInstrument = restored.InstrumentOrInstrumentList;
                        if (!string.IsNullOrWhiteSpace(tmplInstrument))
                            tsp.InstrumentOrInstrumentList = tmplInstrument;

                        // An assignment that does not throw is not evidence that it
                        // stuck: the property could be handing the value to a binding
                        // that drops it. So the instance is read back and compared by
                        // REFERENCE, which is the only comparison that cannot be
                        // satisfied by a copy that looks similar.
                        StrategyBase after = tsp.StrategyTemplate;
                        bool stuck = ReferenceEquals(after, restored);

                        var sb = new StringBuilder("{\"id\":").Append(JsonStr(id));
                        sb.Append(",\"status\":").Append(JsonStr(stuck ? "ok" : "error"));
                        sb.Append(",\"template\":").Append(JsonStr(file));
                        sb.Append(",\"strategy\":").Append(JsonStr(got));
                        // What the TAB says it holds now, not what was written to it.
                        sb.Append(",\"tabStrategy\":").Append(JsonStr(tsp.Strategy));
                        // The three values a run is actually carried out with, read off
                        // the tab and the template AFTER the writes. The instrument is
                        // the tab's, because that is the one the Analyzer uses.
                        sb.Append(",\"instrument\":").Append(JsonStr(tsp.InstrumentOrInstrumentList));
                        sb.Append(",\"from\":").Append(JsonStr(
                            after != null ? after.From.ToString("yyyy-MM-dd") : ""));
                        sb.Append(",\"to\":").Append(JsonStr(
                            after != null ? after.To.ToString("yyyy-MM-dd") : ""));
                        sb.Append(",\"switchedFrom\":").Append(JsonStr(switched ? had : ""));
                        sb.Append(",\"applied\":").Append(stuck ? "true" : "false");
                        if (stuck)
                            sb.Append(",\"params\":").Append(ParamReadback(after));
                        else
                            sb.Append(",\"error\":").Append(JsonStr(
                                "the tab kept its previous StrategyTemplate instance"));
                        sb.Append("}");
                        LogSafe("satemplate: " + Path.GetFileName(file) + " -> " + got
                            + (stuck ? " applied" : " NOT applied"));
                        return sb.ToString();
                    }
                    catch (Exception ex) { return BtErr(id, "satemplate: " + Deep(ex)); }
                }));
            }
            catch (Exception ex) { return BtErr(id, "satemplate outer: " + Deep(ex)); }
        }


        // ── playbackrun ──────────────────────────────────────────────────────
        // Drive Playback, not just read it. `playback` reports the transport's
        // state; this runs it: connect, set source and range, position the clock,
        // and get a strategy going — each step reported with what it WROTE and
        // what it READ BACK.
        //
        // Why the read-back on every write: a reflection assignment that does not
        // throw is not evidence the value stuck. Measured against NinjaTrader
        // 8.1.8.2: `PlaybackAdapter.FromEst` set before the connection is up reads
        // back as 01.01.0001, and `PlaybackSpeed` set before connecting is moved
        // to `oldPlaybackSpeed` while the live field falls back to 1 — a run that
        // looks configured and replays at 1x instead of max.
        //
        // Stages, so a failure is localized rather than global:
        //   members       reflect the PlaybackAdapter statics (names differ by build)
        //   connect       simulation mode, source, range, speed, connect
        //   range         range + speed + Reset — only valid AFTER connect
        //   uitree        dump a window's visual tree (window: title substring)
        //   elprobe       dump one named element, its bindings and context menu
        //   uiset         write the Playback window's own controls
        //   attach        add a strategy to the grid and configure it from a template
        //
        // Why `attach` goes through the grid: adding a strategy to Account.Strategies
        // is NOT what the Control Center does. Measured 2026-08-18 — the Strategies
        // grid stayed empty and SetState(DataLoaded) went straight to Finalized
        // with NinjaTrader's own "All data must first be loaded by the hosting
        // NinjaScript in its configure state". The grid is bound to its own
        // ObservableCollection, and it also creates the data series, loads it, and
        // steps the state machine - so `attach` uses AddStrategyToGrid.
        private string RunPlaybackRun(string id, string triggerJson)
        {
            string stage = ExtractJsonString(triggerJson, "stage");
            if (string.IsNullOrWhiteSpace(stage)) stage = "connect";
            string typeName = ExtractJsonString(triggerJson, "strategy");
            string instName = ExtractJsonString(triggerJson, "instrument");
            string fromS    = ExtractJsonString(triggerJson, "from");
            string toS      = ExtractJsonString(triggerJson, "to");
            string bpS      = ExtractJsonString(triggerJson, "barsPeriod");
            string trS      = ExtractJsonString(triggerJson, "tickReplay");
            string srcS     = ExtractJsonString(triggerJson, "source");
            Dictionary<string, string> prms = ParseParams(triggerJson);

            StringBuilder sb = new StringBuilder("{\"id\":");
            sb.Append(JsonStr(id)).Append(",\"status\":\"ok\",\"stage\":")
              .Append(JsonStr(stage)).Append(",\"steps\":[");
            bool[] first = new bool[] { true };
            Action<string, bool, string> step = delegate(string what, bool ok, string detail)
            {
                if (!first[0]) sb.Append(",");
                sb.Append("{\"step\":").Append(JsonStr(what))
                  .Append(",\"ok\":").Append(ok ? "true" : "false")
                  .Append(",\"detail\":").Append(JsonStr(detail == null ? "" : detail)).Append("}");
                first[0] = false;
                // Live progress: the result file only exists once this stage RETURNS.
                // Measured 2026-08-20 (runs 5 and 8): the UI thread died inside the
                // enable, the attach result appeared minutes after the driver was gone,
                // and nothing showed HOW FAR the stage had got while it ran. This file
                // is appended the moment a sub-step completes, so a frozen NinjaTrader
                // leaves behind the exact last step that still worked.
                Progress(id, (ok ? "ok    " : "FAIL  ") + what.Trim()
                             + (string.IsNullOrEmpty(detail) ? "" : ("  " + detail)));
            };

            try
            {
                Type pb = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                if (pb == null)
                {
                    step("locate PlaybackAdapter", false, "type not found");
                    sb.Append("]}");
                    return sb.ToString();
                }

                PbRunCtx ctx = new PbRunCtx();
                ctx.Id = id;
                ctx.TriggerJson = triggerJson;
                ctx.Stage = stage;
                ctx.TypeName = typeName;
                ctx.InstName = instName;
                ctx.FromS = fromS;
                ctx.ToS = toS;
                ctx.BpS = bpS;
                ctx.TrS = trS;
                ctx.SrcS = srcS;
                ctx.Prms = prms;
                ctx.Pb = pb;
                ctx.Sb = sb;
                ctx.Step = step;

                if (stage == "members") return Stage_Members(ctx);

                if (stage == "openwindow") return Stage_Openwindow(ctx);

                if (stage == "uitree" || stage == "elprobe") return Stage_Uitree_Elprobe(ctx);

                if (stage == "uiset") return Stage_Uiset(ctx);

                if (stage == "selprobe") return Stage_Selprobe(ctx);

                if (stage == "dialogset") return Stage_Dialogset(ctx);
                if (stage == "play") return Stage_Play(ctx);

                if (stage == "transport") return Stage_Transport(ctx);
                if (stage == "cmdprobe" || stage == "invokecmd") return Stage_Cmdprobe_Invokecmd(ctx);

                if (stage == "resname") return Stage_Resname(ctx);

                if (stage == "ntlog") return Stage_Ntlog(ctx);

                if (stage == "stratlive") return Stage_Stratlive(ctx);

                if (stage == "strategystate") return Stage_Strategystate(ctx);

                if (stage == "playbackevents") return Stage_Playbackevents(ctx);

                if (stage == "botout") return Stage_Botout(ctx);

                if (stage == "restore") return Stage_Restore(ctx);

                if (stage == "source") return Stage_Source(ctx);

                if (stage == "panelstate") return Stage_Panelstate(ctx);

                if (stage == "panelflips") return Stage_Panelflips(ctx);

                if (stage == "ready") return Stage_Ready(ctx);

                if (stage == "speed") return Stage_Speed(ctx);

                if (stage == "template") return Stage_Template(ctx);

                if (stage == "findmethod") return Stage_Findmethod(ctx);

                if (stage == "elmembers") return Stage_Elmembers(ctx);

                if (stage == "winmembers") return Stage_Winmembers(ctx);

                if (stage == "setel") return Stage_Setel(ctx);

                if (stage == "hide") return Stage_Hide(ctx);

                if (stage == "unhide") return Stage_Unhide(ctx);

                if (stage == "park") return Stage_Park(ctx);

                if (stage == "closedialogs") return Stage_Closedialogs(ctx);

                if (stage == "baseline") return Stage_Baseline(ctx);

                if (stage == "dialogs") return Stage_Dialogs(ctx);

                if (stage == "enablestrategy") return Stage_Enablestrategy(ctx);

                if (stage == "removestrategy") return Stage_Removestrategy(ctx);
                if (stage == "uiidle") return Stage_Uiidle(ctx);
                if (stage == "alloff") return Stage_Alloff(ctx);
                if (stage == "disconnect") return Stage_Disconnect(ctx);

                if (stage == "recycle") return Stage_Recycle(ctx);

                if (stage == "connect") return Stage_Connect(ctx);

                if (stage == "range") return Stage_Range(ctx);

                if (stage == "stratdump") return Stage_Stratdump(ctx);

                if (stage == "arm") return Stage_Arm(ctx);

                if (stage == "attach") return Stage_Attach(ctx);

                step("stage", false, "unknown stage '" + stage + "'");
            }
            catch (Exception ex) { step("exception", false, ex.GetType().Name + ": " + ex.Message); }
            sb.Append("]}");
            return sb.ToString();
        }

        // The state RunPlaybackRun sets up once and every stage below reads. It exists so
        // each stage can be its own method while its body stays exactly as it was.
        private sealed class PbRunCtx
        {
            public string Id;
            public string TriggerJson;
            public string Stage;
            public string TypeName;
            public string InstName;
            public string FromS;
            public string ToS;
            public string BpS;
            public string TrS;
            public string SrcS;
            public Dictionary<string, string> Prms;
            public Type Pb;
            public StringBuilder Sb;
            public Action<string, bool, string> Step;
        }

        private string Stage_Members(PbRunCtx ctx)
        {
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    foreach (PropertyInfo pi in pb.GetProperties(BFStatic))
                        step("prop " + pi.Name, pi.CanWrite, pi.PropertyType.Name + " = " + SafeRead(pi, null));
                    foreach (FieldInfo fi in pb.GetFields(BFStatic))
                        step("field " + fi.Name, !fi.IsInitOnly, fi.FieldType.Name + " = " + SafeReadField(fi, null));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Uitree_Elprobe(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string stage = ctx.Stage;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    string wTitle = ExtractJsonString(triggerJson, "window");
                    Window w = FindWindowByTitle(wTitle);
                    if (w == null)
                    {
                        step("window '" + (wTitle == null ? "Playback" : wTitle) + "'", false,
                             "not found. Present: " + WindowTitles());
                        sb.Append("]}");
                        return sb.ToString();
                    }
                    step("window", true, w.GetType().FullName);
                    string elName = ExtractJsonString(triggerJson, "element");
                    // Thread-affine: read on the window's OWN dispatcher. NinjaTrader
                    // is multi-UI-threaded, so Application.Current.Windows does not
                    // even list every window — hence Globals.AllWindows above.
                    w.Dispatcher.Invoke(new Action(delegate
                    {
                        if (stage == "uitree") DumpVisualTree(w, 0, step);
                        else DumpElement(w, elName, step);
                    }));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Uiset(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string fromS = ctx.FromS;
            string toS = ctx.ToS;
            string srcS = ctx.SrcS;
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    Window w = FindWindowByTitle("Playback");
                    // ⚠ THIS STAGE SETS THE PANEL, AND A HOST WITHOUT A UI HAS NONE.
                    //
                    // Its whole job is keeping the Playback window's controls in step with
                    // the transport - dtpStart, dtpEnd, the source radios - so the display
                    // does not contradict the run. Worth doing when a display exists.
                    //
                    // The values themselves are set by the NEXT stage, `range`, straight on
                    // the adapter (FromEst/ToEst), which is the route our GUI-less host
                    // uses for everything. So headless there is nothing here to do and
                    // nothing lost by not doing it - but the run died here anyway, because
                    // a stage that cannot find its window returns a failed step.
                    if (w == null && NoUiHost())
                    {
                        step("Playback window", true,
                             "no UI in this host - nothing to keep in step. The dates and the"
                             + " source are set on the adapter by stage 'range', which is the"
                             + " only place they take effect anyway.");
                        sb.Append("]}");
                        return sb.ToString();
                    }
                    if (w == null) { step("Playback window", false, "not found"); sb.Append("]}"); return sb.ToString(); }
                    step("Playback window", true, w.GetType().FullName);
                    DateTime uFrom = string.IsNullOrWhiteSpace(fromS) ? DateTime.MinValue : DateTime.Parse(fromS, InvCi);
                    DateTime uTo   = string.IsNullOrWhiteSpace(toS)   ? DateTime.MinValue : DateTime.Parse(toS, InvCi);
                    string play = ExtractJsonString(triggerJson, "play");
                    // Set inside the dispatcher block, ACTED ON after it returns. A one-element
                    // array because a delegate cannot assign to a local of the enclosing method.
                    bool[] wantsTransportWait = new bool[] { false };
                    w.Dispatcher.Invoke(new Action(delegate
                    {
                        // The transport statics do not move the panel; the panel is a
                        // separate set of controls. Leaving them out of sync is worse
                        // than not setting them at all — the display then contradicts
                        // the run.
                        if (uFrom != DateTime.MinValue) SetElementValue(w, "dtpStart", "Value", uFrom, step);
                        if (uTo   != DateTime.MinValue) SetElementValue(w, "dtpEnd", "Value", uTo.Date.AddDays(1).AddSeconds(-1), step);
                        if (!string.IsNullOrWhiteSpace(srcS))
                        {
                            bool hist = string.Equals(srcS, "historical", StringComparison.OrdinalIgnoreCase);
                            // ARM THE WATCHER FIRST. Switching TO Historical makes NT8 raise
                            // a modal (i) notice ("no Level II market depth in this mode"),
                            // and it does so SYNCHRONOUSLY inside the SetElementValue below -
                            // that Invoke does not return while the dialog stands. A handler
                            // called after the write therefore never runs; measured
                            // 30.08.2026 on a build that had exactly that, the dialog came up
                            // unchanged. So the watcher runs on its OWN thread, started
                            // before the write, and confirms through the dispatcher, which
                            // keeps pumping inside a modal's own frame.
                            string noticeResult = null;
                            Thread noticeWatch = null;
                            if (hist)
                            {
                                noticeWatch = new Thread(delegate ()
                                {
                                    noticeResult = DismissHistoricalNotice(20000);
                                });
                                noticeWatch.IsBackground = true;
                                noticeWatch.Start();
                            }
                            // DISPLAY ONLY - never fatal. The source itself is decided by
                            // PlaybackAdapter.IsSourceHistoricalData at stage "3 source";
                            // these radios only keep the panel from contradicting the run.
                            // Measured 30.08.2026: writing rbHistoricalData.IsChecked while
                            // the connection is up threw TargetInvocationException and took
                            // the whole run with it (rc 2). A display defect is reported,
                            // not paid for with the measurement.
                            // A SOFT step: same text, same visibility, never a failure
                            // verdict. SetElementValue catches internally and reports
                            // through step(..., false, ...), so wrapping the calls in
                            // try/catch changes nothing - the stage dies on the verdict,
                            // not on an exception.
                            //
                            // Writing the Historical radio makes NinjaTrader re-parse the
                            // panel's date fields and that parse throws (measured
                            // 30.08.2026: TargetInvocationException <- FormatException:
                            // "String was not recognized as a valid DateTime" - the same
                            // wording as the modal "String was not recognized as a valid
                            // DateTime. (Panic)" that NinjaTrader raises when Playback is
                            // connected without a date range, measured 2026-08-24). It is
                            // NinjaTrader's defect; the run's source is decided by
                            // PlaybackAdapter.IsSourceHistoricalData at stage "3 source".
                            // A display that refuses to move is reported, not paid for with
                            // the measurement.
                            bool radiosMoved = true;
                            Action<string, bool, string> softStep = delegate (string sN, bool sOk, string sD)
                            {
                                if (!sOk) radiosMoved = false;
                                step(sN, true, (sOk ? "" : "DISPLAY ONLY, not updated: ") + sD);
                            };
                            SetElementValue(w, "rbHistoricalData", "IsChecked", hist, softStep);
                            SetElementValue(w, "rbRecordedData", "IsChecked", !hist, softStep);
                            step("source radios", true, radiosMoved
                                 ? ("panel now shows " + (hist ? "Historical" : "Market Replay"))
                                 : ("panel keeps the PREVIOUS source - NinjaTrader throws while"
                                    + " re-parsing the date fields on this write. The RUN uses "
                                    + (hist ? "Historical" : "Market Replay")
                                    + ", set on PlaybackAdapter.IsSourceHistoricalData at stage 3,"
                                    + " which is what decides it."));
                            if (noticeWatch != null)
                            {
                                noticeWatch.Join(21000);
                                step("historical notice", true, noticeResult ?? "watcher gave no verdict");
                            }
                        }
                        if (!string.IsNullOrWhiteSpace(play))
                        {
                            // The transport is driven by PlaybackSpeed, NOT by the button.
                            // Measured 2026-08-19: writing btnPlay.IsChecked=false read back
                            // false while the transport kept running - the property changes
                            // the visual state only. Starting happened to work by writing;
                            // stopping did not, and that asymmetry is exactly what an
                            // unverified write hides. Hence: write the speed, then verify the
                            // EFFECT on the clock (below), never the button.
                            bool wish = string.Equals(play, "true", StringComparison.OrdinalIgnoreCase);

                            // The speed goes HERE, with the start - see "SPEED IS A
                            // START COMMAND" in stage "range". Written during setup it
                            // sent the transport racing through the range before
                            // anything was attached. The FIELD is written too:
                            // connecting moves the property value to `oldPlaybackSpeed`
                            // and leaves the live field at 1 - which is also why the
                            // panel read "1x" on a clock running at full speed. The
                            // label further down is refreshed from the ACTUAL value, so
                            // the panel stops contradicting the run.
                            if (wish)
                            {
                                FieldInfo fiMaxU = pb.GetField("MaxSpeedValue", BFStatic);
                                object maxU = fiMaxU != null ? fiMaxU.GetValue(null) : (object)int.MaxValue;
                                SetStatic(pb, "PlaybackSpeed", maxU, step);
                                try
                                {
                                    FieldInfo fiSpU = pb.GetField("playbackSpeed", BFStatic);
                                    if (fiSpU != null)
                                    {
                                        fiSpU.SetValue(null, maxU);
                                        step("field playbackSpeed",
                                             fiSpU.GetValue(null).ToString() == maxU.ToString(),
                                             "wrote=" + maxU + " read=" + fiSpU.GetValue(null));
                                    }
                                }
                                catch (Exception ex) { step("field playbackSpeed", false, ex.Message); }

                                // ⚠ THE WAIT DOES NOT BELONG HERE - THIS IS THE UI THREAD.
                                //
                                // Everything in this block runs inside w.Dispatcher.Invoke.
                                // A wait here is the UI thread waiting for itself, and
                                // TransportMoving samples NowEst twice 1200 ms apart - so it
                                // would hold the interface for at least that long, every time,
                                // while NinjaTrader needs exactly this thread to move the
                                // transport it is being asked about.
                                //
                                // The rule "never wait inside the dispatcher" was written down
                                // on 2026-08-19 and broken again on 2026-08-20 by this very
                                // block. So the flag is set here and the WAIT happens after the
                                // Invoke returns, on the worker thread.
                                wantsTransportWait[0] = !string.Equals(
                                    ExtractJsonString(triggerJson, "awaitTransport"),
                                    "false", StringComparison.OrdinalIgnoreCase);
                            }

                            // The transport is run by the SPEED, not by a button. A positive
                            // PlaybackSpeed runs it, 0 parks it - bisected on
                            // 2026-08-19 and documented independently upstream. Pressing the
                            // play button needed the window, the right visual state and a real
                            // Click event; a static property write needs none of that.
                            if (!wish)
                            {
                                int backU;
                                string probU = SetPlaybackSpeed(0, out backU);
                                step("transport", probU == null, probU == null
                                     ? "PlaybackSpeed=0 (parked; verify the EFFECT with stage 'transport')"
                                     : probU);
                            }
                            else
                            {
                                DateTime seenU;
                                bool movingU = TransportMoving(out seenU);
                                step("transport", true, "moving=" + movingU + " at " + seenU);
                            }
                        }
                        // Speed label derived from the ACTUAL value, never guessed.
                        PropertyInfo piSp = pb.GetProperty("PlaybackSpeed", BFStatic);
                        FieldInfo fiMx = pb.GetField("MaxSpeedValue", BFStatic);
                        object now = piSp != null ? piSp.GetValue(null) : null;
                        object max = fiMx != null ? fiMx.GetValue(null) : null;
                        string txt = (now != null && max != null && now.ToString() == max.ToString())
                                     ? "Max" : (now + "x");
                        SetElementValue(w, "textBlockSpeed", "Text", txt, step);
                    }));

                    // ⚠ PHASE 3, ON THE WORKER THREAD - the dispatcher is free again here.
                    //
                    // Writing the speed is not the same as the transport running: the
                    // read-back inside the block came from the same reference in the same
                    // instant and would report success for a write NinjaTrader ignored.
                    // NowEst is written by NinjaTrader alone, so its motion is the evidence -
                    // and TransportMoving samples it twice 1200 ms apart, which is a
                    // measurement interval and must never sit on the UI thread.
                    //
                    // `awaitTransport:"false"` splits trigger and wait for a walkthrough;
                    // stage `transport` then carries phases 3 and 4.
                    if (wantsTransportWait[0])
                    {
                        DateTime seenU;
                        string gotU; long msU;
                        bool runU = WaitUntilChanged(delegate
                        {
                            DateTime s4;
                            return TransportMoving(out s4) ? "moving" : "parked";
                        }, TransportMoving(out seenU) ? "moving" : "parked", "moving",
                           RequestTtlSec(triggerJson), out gotU, out msU);
                        step("transport moving", runU,
                             (runU ? "running after " : "STILL parked after ")
                             + msU + " ms   NowEst last seen " + seenU);
                    }
                    else
                        step("transport moving", true,
                             "NOT waited on request (awaitTransport=false) - "
                             + "watch it with stage 'transport'");

                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Selprobe(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Measure the selection BEFORE and AFTER, on the object itself.
                    // "Remove" stays disabled after setting Records[i].IsSelected, so
                    // the grid evidently tracks its selection somewhere else - this
                    // reports where, instead of guessing.
                    string want2 = ExtractJsonString(triggerJson, "strategy");
                    Window cc4 = FindWindowByTitle("Control Center");
                    if (cc4 == null) { step("Control Center", false, "not found"); sb.Append("]}"); return sb.ToString(); }
                    cc4.Dispatcher.Invoke(new Action(delegate
                    {
                        object grid = FindElement(cc4, "grdStrategies");
                        if (grid == null) { step("grdStrategies", false, "not found"); return; }
                        step("grid", true, grid.GetType().FullName);
                        ReportSelection(grid, "before", step);

                        PropertyInfo pRec = grid.GetType().GetProperty("Records");
                        System.Collections.IEnumerable recs =
                            pRec == null ? null : pRec.GetValue(grid, null) as System.Collections.IEnumerable;
                        if (recs == null) { step("Records", false, "not enumerable"); return; }
                        int n = 0;
                        object chosen = null;
                        foreach (object rec in recs)
                        {
                            n++;
                            if (rec == null) continue;
                            PropertyInfo pdi = rec.GetType().GetProperty("DataItem");
                            object di = pdi == null ? null : pdi.GetValue(rec, null);
                            string nm = "";
                            if (di != null)
                            {
                                PropertyInfo pn = di.GetType().GetProperty("Name");
                                if (pn != null) nm = ("" + pn.GetValue(di, null)).Trim();
                            }
                            step("record " + n, true, rec.GetType().Name + "  DataItem.Name='" + nm + "'");
                            if (chosen == null && (string.IsNullOrWhiteSpace(want2)
                                || nm.IndexOf(want2, StringComparison.OrdinalIgnoreCase) >= 0))
                                chosen = rec;
                        }
                        if (chosen == null) { step("match", false, "no record for '" + want2 + "'"); return; }

                        foreach (PropertyInfo rp in chosen.GetType().GetProperties(
                                     BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (rp.Name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) < 0
                                && rp.Name.IndexOf("Active", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            step("  record." + rp.Name, rp.CanWrite, rp.PropertyType.Name + " = " + SafeReadOn(rp, chosen));
                        }

                        try
                        {
                            PropertyInfo pis = chosen.GetType().GetProperty("IsSelected");
                            if (pis != null && pis.CanWrite) pis.SetValue(chosen, true, null);
                            step("set IsSelected", true, "wrote true, read " + SafeReadOn(pis, chosen));
                        }
                        catch (Exception ex) { step("set IsSelected", false, ex.Message); }

                        ReportSelection(grid, "after", step);

                        PropertyInfo pcm3 = grid.GetType().GetProperty("ContextMenu");
                        System.Windows.Controls.ContextMenu cm3 =
                            pcm3 == null ? null : pcm3.GetValue(grid, null) as System.Windows.Controls.ContextMenu;
                        if (cm3 != null)
                        {
                            // IsEnabled on a context menu that was never opened is stale:
                            // WPF evaluates CanExecute on ContextMenuOpening. Read it once
                            // closed, then open the menu, requery, and read it again - the
                            // difference tells us whether the entry is really unavailable
                            // or just not evaluated yet.
                            foreach (object it in cm3.Items)
                            {
                                System.Windows.Controls.MenuItem mi = it as System.Windows.Controls.MenuItem;
                                if (mi == null || mi.Header == null) continue;
                                string h = ("" + mi.Header).Trim();
                                if (h == "Remove" || h == "Enable" || h == "Disable")
                                    step("  closed menu " + h, mi.IsEnabled, mi.IsEnabled ? "enabled" : "disabled");
                            }
                            try
                            {
                                cm3.PlacementTarget = grid as System.Windows.UIElement;
                                cm3.IsOpen = true;
                                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                                cm3.Dispatcher.Invoke(new Action(delegate { }),
                                    System.Windows.Threading.DispatcherPriority.ContextIdle);
                                foreach (object it in cm3.Items)
                                {
                                    System.Windows.Controls.MenuItem mi = it as System.Windows.Controls.MenuItem;
                                    if (mi == null || mi.Header == null) continue;
                                    string h = ("" + mi.Header).Trim();
                                    if (h == "Remove" || h == "Enable" || h == "Disable")
                                        step("  opened menu " + h, mi.IsEnabled, mi.IsEnabled ? "enabled" : "disabled");
                                }
                                cm3.IsOpen = false;
                            }
                            catch (Exception ex) { step("open context menu", false, ex.GetType().Name + ": " + ex.Message); }
                        }
                    }));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Dialogset(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    Window dw = FindWindowByTitle(
                        string.IsNullOrWhiteSpace(ExtractJsonString(triggerJson, "window"))
                        ? "Strategies" : ExtractJsonString(triggerJson, "window"));
                    if (dw == null || dw.GetType().FullName.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        step("dialog", false, "no ObjectDialog found. Windows: " + WindowTitles());
                        sb.Append("]}");
                        return sb.ToString();
                    }
                    step("dialog", true, dw.GetType().FullName);

                    string select = ExtractJsonString(triggerJson, "select");
                    string confirm = ExtractJsonString(triggerJson, "confirm");
                    Dictionary<string, string> fields = ParseNamedMap(triggerJson, "fields");

                    dw.Dispatcher.Invoke(new Action(delegate
                    {
                        if (!string.IsNullOrWhiteSpace(select))
                        {
                            // A bot that declares its own namespace shows up as a FOLDER
                            // with the strategy inside, so "select" may be a path:
                            // "MyStrategy/MyStrategy". Each level is expanded before
                            // the next is looked for - collapsed children are virtualized
                            // and do not exist in the visual tree at all. Measured
                            // 2026-08-18: selecting the folder alone left the property
                            // grid on the previous strategy, and every field written
                            // afterwards belonged to the wrong bot.
                            string[] parts = select.Split('/');
                            bool hit = false;
                            for (int li = 0; li < parts.Length; li++)
                            {
                                string part = parts[li].Trim();
                                if (part.Length == 0) continue;
                                // Last segment must be a LEAF: a folder and the strategy
                                // inside it carry the SAME text, and a depth-first walk
                                // hits the folder first - which is why two selects in a
                                // row both landed on the folder and the grid never changed.
                                hit = SelectTreeItem(dw, part, li == parts.Length - 1);
                                if (!hit) { step("select '" + part + "'", false, "not found in the tree"); break; }
                                step("select '" + part + "'", true, li < parts.Length - 1 ? "expanded" : "selected");
                                if (li < parts.Length - 1) ExpandSelected(dw, part);
                            }
                        }
                    }));

                    // Selecting a different strategy rebuilds the property grid, so the
                    // fields must be written in a SECOND pass, after the UI settled.
                    if (!string.IsNullOrWhiteSpace(select)) Thread.Sleep(1200);

                    foreach (KeyValuePair<string, string> kv in fields)
                    {
                        KeyValuePair<string, string> f = kv;
                        string outcome = (string)dw.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            return SetByLabel(dw, f.Key, f.Value);
                        }));
                        step("field '" + f.Key + "'", outcome.StartsWith("wrote"), outcome);
                        // Some fields rebuild the grid - changing the bar type swaps the
                        // whole Data Series section. Let it settle OUTSIDE the dispatcher
                        // before the next field is looked for, otherwise the walk runs
                        // through a tree that is being rebuilt and finds nothing.
                        Thread.Sleep(700);
                    }

                    if (string.Equals(confirm, "true", StringComparison.OrdinalIgnoreCase))
                    {
                        // The OK button used to be invoked through its automation peer.
                        // That is a click, and clicks are out - so this reports instead of
                        // pressing. A strategy is set up with stage `attach`, which needs no
                        // dialog at all.
                        step("confirm", false,
                             "confirming a dialog would be a CLICK - use stage 'attach' "
                             + "(template -> AddStrategyToGrid -> StrategyEnable) instead");
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Play(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Step 8: start (or park) the transport - a property write, not a button
                    // press, so the value can be read back.
                    string vP = ExtractJsonString(triggerJson, "value");
                    int want;
                    if (string.IsNullOrWhiteSpace(vP) || vP.Equals("max", StringComparison.OrdinalIgnoreCase))
                        want = MaxSpeedOrOne();
                    else if (!int.TryParse(vP, out want) || want < 0)
                    { step("value", false, "expected 'max' or a non-negative integer, got '" + vP + "'"); sb.Append("]}"); return sb.ToString(); }

                    DateTime seenB;
                    bool movingB = TransportMoving(out seenB);
                    step("before", true, "moving=" + movingB + " at " + seenB);

                    int got;
                    string problem = SetPlaybackSpeed(want, out got);
                    step("PlaybackSpeed", problem == null,
                         problem == null ? ("wrote " + want + ", reads back " + got) : problem);
                    if (problem != null) { sb.Append("]}"); return sb.ToString(); }

                    // Phase 3: WAIT FOR THE CLOCK TO MOVE (or to stop), instead of sleeping
                    // 1.5 s and measuring whatever happens to be true then. Outside the
                    // dispatcher, because NinjaTrader needs that thread to act at all.
                    Type pbP = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                    PropertyInfo piNowP = pbP == null ? null : pbP.GetProperty("NowEst", BFStatic);
                    string beforeP = piNowP == null ? "?" : "" + piNowP.GetValue(null);
                    string gotP; long msP;
                    bool changed = WaitUntilChanged(delegate {
                            return piNowP == null ? "?" : "" + piNowP.GetValue(null); },
                        beforeP, null, RequestTtlSec(triggerJson), out gotP, out msP);
                    step("clock moved", changed || want == 0,
                         changed ? ("moved after " + msP + " ms: " + beforeP + " -> " + gotP)
                                 : ("did NOT move within this request's budget (" + beforeP + ")"));
                    DateTime seenA;
                    bool movingA = TransportMoving(out seenA);

                    // ⚠ THE CLOCK IS THE VERDICT, not the write. A write that resolves is not
                    // a transport that runs - the same class of mistake as a "set" reported
                    // into a discarded object.
                    bool wantMoving = want > 0;
                    step("after", movingA == wantMoving,
                         "moving=" + movingA + " at " + seenA + " (wanted " + wantMoving + ")");
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Transport(PbRunCtx ctx)
        {
            string id = ctx.Id;
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Report the adapter's own state. Two samples of the tick counter a
                    // second apart, plus the timer that drives the whole thing.
                    Type tp2 = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                    if (tp2 == null) { step("PlaybackAdapter", false, "type not found"); sb.Append("]}"); return sb.ToString(); }

                    FieldInfo fiTimer = tp2.GetField("timer", BFStatic);
                    object tim = fiTimer != null ? fiTimer.GetValue(null) : null;
                    if (tim == null) step("timer", false, "field 'timer' not found or null");
                    else
                    {
                        string en = "?", iv = "?";
                        try { PropertyInfo p1 = tim.GetType().GetProperty("Enabled"); if (p1 != null) en = "" + p1.GetValue(tim, null); } catch { }
                        try { PropertyInfo p2 = tim.GetType().GetProperty("Interval"); if (p2 != null) iv = "" + p2.GetValue(tim, null); } catch { }
                        step("timer.Enabled", true, en + "   Interval=" + iv + " ms");
                    }

                    FieldInfo fiTicks = tp2.GetField("RealtimeTickCount", BFStatic);
                    FieldInfo fiInH = tp2.GetField("inTimerHandler", BFStatic);
                    PropertyInfo piNowT = tp2.GetProperty("NowEst", BFStatic);
                    PropertyInfo piSpd = tp2.GetProperty("PlaybackSpeed", BFStatic);

                    // ⚠ NOT a wait - this is the MEASUREMENT WINDOW. A rate needs an interval:
                    // "how many ticks in how long". It is the caller's parameter, not a constant
                    // in here, so nothing about NinjaTrader's speed is being assumed.
                    int sampleMs = 1000;
                    string smS = ExtractJsonString(triggerJson, "sampleMs");
                    int smP;
                    if (!string.IsNullOrWhiteSpace(smS) && int.TryParse(smS, out smP) && smP > 0) sampleMs = smP;
                    // ⚠ ONE PROGRESS LINE PER READ.
                    //
                    // Measured 2026-08-24: after a connect this stage is picked up, writes
                    // "timer.Enabled True Interval=1000 ms" and never returns - 60 s and
                    // counting. Before the connect the same stage finishes in 212 ms. The
                    // last line brackets the blocking call to the four reads below, and a
                    // bracket is not a name: with a line in front of each, the next run
                    // says WHICH member does not come back, instead of leaving four
                    // candidates and an opinion.
                    Progress(id, "      read RealtimeTickCount ...");
                    object t1 = fiTicks != null ? fiTicks.GetValue(null) : null;
                    Progress(id, "      read NowEst ...");
                    object n1 = piNowT != null ? piNowT.GetValue(null) : null;
                    Progress(id, "      sampling for " + sampleMs + " ms ...");
                    Thread.Sleep(sampleMs);
                    Progress(id, "      read RealtimeTickCount again ...");
                    object t2 = fiTicks != null ? fiTicks.GetValue(null) : null;
                    Progress(id, "      read NowEst again ...");
                    object n2 = piNowT != null ? piNowT.GetValue(null) : null;
                    Progress(id, "      both reads returned");

                    long d1 = 0, d2 = 0;
                    try { d1 = Convert.ToInt64(t1); d2 = Convert.ToInt64(t2); } catch { }
                    // ⚠ RealtimeTickCount CANNOT tell running from parked in playback.
                    //
                    // Measured 2026-08-20, one variable changed (the window), everything
                    // else identical:
                    //     running, 1500 ms window   29087 -> 29088   delta 1
                    //     running, 6000 ms window   29089 -> 29095   delta 6
                    //     PARKED,  6000 ms window   29100 -> 29106   delta 6   <- same
                    // It advances about once per second of WALL time whatever the
                    // transport does, so a positive delta is not evidence of motion. It is
                    // reported because it is bot-free and useful against a DISCONNECTED
                    // adapter - not as proof that data is flowing.
                    step("RealtimeTickCount", true, t1 + " -> " + t2 + "   delta=" + (d2 - d1)
                         + " in " + sampleMs + " ms   (wall-clock driven - does NOT prove motion)");
                    step("NowEst", true, n1 + " -> " + n2);

                    // The discriminating verdict, from the channel that was PROVEN to
                    // discriminate: two NowEst samples a known window apart. Measured the
                    // same day, parked / running / parked again read False / True / False.
                    bool movedNow = false;
                    try { movedNow = n1 != null && n2 != null && !n1.ToString().Equals(n2.ToString()); }
                    catch { }
                    step("transport moving", true, movedNow
                         ? "YES - NowEst advanced within " + sampleMs + " ms"
                         : "no - NowEst stood still for " + sampleMs + " ms");
                    if (fiInH != null) step("inTimerHandler", true, "" + fiInH.GetValue(null));
                    if (piSpd != null) step("PlaybackSpeed", true, "" + piSpd.GetValue(null));
                    // The configured range comes along, so a caller can decide "finished"
                    // from ONE cheap probe: the run is over when NowEst reaches ToEst.
                    // Generic - no strategy and no GUI element involved.
                    PropertyInfo piFrom = tp2.GetProperty("FromEst", BFStatic);
                    PropertyInfo piTo = tp2.GetProperty("ToEst", BFStatic);
                    if (piFrom != null) step("FromEst", true, "" + piFrom.GetValue(null));
                    if (piTo != null) step("ToEst", true, "" + piTo.GetValue(null));

                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Cmdprobe_Invokecmd(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string stage = ctx.Stage;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // What does the GUI button actually DO? Toggling it through the
                    // automation peer six times did not stop the transport (measured
                    // 2026-08-19), so the control must react to its COMMAND rather than
                    // to its checked state. Report the command, and optionally execute it.
                    string wT = ExtractJsonString(triggerJson, "window");
                    string eN = ExtractJsonString(triggerJson, "element");
                    Window ws = FindWindowByTitle(string.IsNullOrWhiteSpace(wT) ? "Playback" : wT);
                    if (ws == null) { step("window", false, "not found: " + wT); sb.Append("]}"); return sb.ToString(); }
                    bool doExec = (stage == "invokecmd");
                    string outC = (string)ws.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object el = FindElement(ws, eN);
                        if (el == null) return "element '" + eN + "' not found";
                        PropertyInfo piC = el.GetType().GetProperty("Command");
                        System.Windows.Input.ICommand cmd =
                            piC == null ? null : piC.GetValue(el, null) as System.Windows.Input.ICommand;
                        object par = null;
                        PropertyInfo piP = el.GetType().GetProperty("CommandParameter");
                        if (piP != null) { try { par = piP.GetValue(el, null); } catch { } }
                        if (cmd == null) return el.GetType().Name + " has no Command (parameter=" + par + ")";
                        bool can = false;
                        try { can = cmd.CanExecute(par); } catch (Exception ex) { return "CanExecute threw " + ex.Message; }
                        string info = cmd.GetType().FullName + "  CanExecute=" + can + "  parameter=" + par;
                        if (!doExec) return info;
                        if (!can) return "NOT executed - CanExecute=false. " + info;
                        try { cmd.Execute(par); } catch (Exception ex) { return "Execute threw " + ex.GetType().Name + ": " + ex.Message + ". " + info; }
                        return "EXECUTED. " + info;
                    }));
                    step(stage + " " + eN, outC.IndexOf("not found") < 0 && outC.IndexOf("threw") < 0, outC);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Resname(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Find the RESOURCE NAME behind a piece of NinjaTrader text - read-only, and
                    // without performing the operation that would produce it.
                    //
                    // Log entries carry `Name` (the resource key) next to `Message` (its
                    // rendering). Matching on the name is what makes a check survive an update
                    // and a different UI language. But the name of an entry one has never seen
                    // cannot be guessed - and guessing it is exactly what this stage replaces
                    // (2026-08-20: I had written "NinjaScriptStrategyDisabl" from thin air).
                    //
                    // NinjaTrader exposes its resources as static string properties on NTRes.*
                    // types, named after the key. So the key for a text is found by asking every
                    // one of them what it says.
                    string needleR = ExtractJsonString(triggerJson, "contains");
                    if (string.IsNullOrWhiteSpace(needleR))
                    { step("contains", false, "missing"); sb.Append("]}"); return sb.ToString(); }

                    int scanned = 0, found = 0;
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string an = null;
                        try { an = asm.GetName().Name; } catch { }
                        if (an == null || an.IndexOf("NinjaTrader", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException rex) { types = rex.Types; }
                        catch { continue; }
                        foreach (Type ty in types)
                        {
                            if (ty == null || ty.FullName == null) continue;
                            if (!ty.FullName.StartsWith("NTRes", StringComparison.Ordinal)) continue;
                            PropertyInfo[] props;
                            try { props = ty.GetProperties(BFStatic); }
                            catch { continue; }
                            foreach (PropertyInfo pr in props)
                            {
                                if (pr.PropertyType != typeof(string) || !pr.CanRead) continue;
                                scanned++;
                                string v = null;
                                try { v = pr.GetValue(null, null) as string; } catch { continue; }
                                if (v == null || v.IndexOf(needleR, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                found++;
                                step("name", true, pr.Name + "   [" + ty.FullName + "]   = "
                                     + (v.Length > 70 ? v.Substring(0, 70) + "..." : v));
                                if (found >= 20) break;
                            }
                            if (found >= 20) break;
                        }
                        if (found >= 20) break;
                    }
                    // The NTRes.* properties are only a generated convenience for SOME
                    // resources. The log entries carry ResourceType = "Resource", and those
                    // strings live in an embedded .resources set reached through a
                    // ResourceManager - so ask that directly. Measured 2026-08-20: the property
                    // scan found 0 of 2931 for a text that NinjaTrader definitely writes.
                    if (found == 0)
                    {
                        foreach (Assembly asm2 in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            string an2 = null;
                            try { an2 = asm2.GetName().Name; } catch { }
                            if (an2 == null || an2.IndexOf("NinjaTrader", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            Type[] tys;
                            try { tys = asm2.GetTypes(); }
                            catch (ReflectionTypeLoadException rex2) { tys = rex2.Types; }
                            catch { continue; }
                            foreach (Type ty2 in tys)
                            {
                                if (ty2 == null || ty2.Name != "Resource") continue;
                                PropertyInfo rmP = ty2.GetProperty("ResourceManager", BFStatic);
                                object rm = null;
                                try { rm = rmP == null ? null : rmP.GetValue(null, null); } catch { }
                                if (rm == null) continue;
                                MethodInfo grs = rm.GetType().GetMethod("GetResourceSet",
                                    new Type[] { typeof(System.Globalization.CultureInfo), typeof(bool), typeof(bool) });
                                object set = null;
                                try
                                {
                                    set = grs.Invoke(rm, new object[] {
                                        System.Globalization.CultureInfo.InvariantCulture, true, true });
                                }
                                catch { }
                                System.Collections.IEnumerable en2 = set as System.Collections.IEnumerable;
                                if (en2 == null) continue;
                                step("resource set", true, ty2.FullName);
                                foreach (object o2 in en2)
                                {
                                    if (!(o2 is System.Collections.DictionaryEntry)) continue;
                                    System.Collections.DictionaryEntry de = (System.Collections.DictionaryEntry)o2;
                                    string val = de.Value as string;
                                    if (val == null) continue;
                                    scanned++;
                                    if (val.IndexOf(needleR, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    found++;
                                    step("name", true, de.Key + "   = "
                                         + (val.Length > 70 ? val.Substring(0, 70) + "..." : val));
                                    if (found >= 20) break;
                                }
                                if (found >= 20) break;
                            }
                            if (found >= 20) break;
                        }
                    }
                    step("scanned", true, scanned + " resource strings");
                    step("found", found > 0, "" + found);
                    sb.Append("]}");
                    return sb.ToString();
        }

        // The Playback control window is NOT persisted in any workspace: measured
        // 2026-08-29 after a NinjaTrader restart - 52 windows loaded across all 7
        // open workspaces, none titled "Playback", and the only 'playback' hit in
        // any workspaces\*.xml is a ChartTrader account line. Every stage that
        // writes the panel (source radios, uiset, speed, play) looks the window up
        // with FindWindowByTitle("Playback") and fails without it - measured
        // 2026-09-03: three runs in a row ended rc 2, "3 source check: panel radios
        // - Playback window not found", each after a healthy connect. So the run
        // opens the window itself, on the UI thread, by constructing NinjaTrader's
        // OWN window type - no clicks, no UI automation.
        //
        // Type and parameterless constructor proven by reflection 2026-08-29:
        //   NinjaTrader.Gui.Data.PlaybackControlCenter : NTWindow, .ctor()
        // Idempotent: an existing window is reported, never duplicated.
        private string Stage_Openwindow(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

            Window have = FindWindowByTitle("Playback");
            if (have != null)
            {
                string t0 = "";
                try { t0 = (string)have.Dispatcher.Invoke(new Func<string>(
                          delegate { return have.Title; })); }
                catch (Exception) { }
                step("already open", true, t0);
                sb.Append("]}");
                return sb.ToString();
            }
            Window ccw = FindWindowByTitle("Control Center");
            if (ccw == null)
            {
                step("control center", false,
                     "not found - no UI dispatcher to open the window on");
                sb.Append("]}");
                return sb.ToString();
            }
            string result = null;
            ccw.Dispatcher.Invoke(new Action(delegate
            {
                try
                {
                    Type t = typeof(NinjaTrader.Gui.Tools.NTWindow).Assembly
                        .GetType("NinjaTrader.Gui.Data.PlaybackControlCenter", false);
                    if (t == null)
                    {
                        result = "type NinjaTrader.Gui.Data.PlaybackControlCenter"
                                 + " not found in NinjaTrader.Gui";
                        return;
                    }
                    Window w = (Window)Activator.CreateInstance(t);
                    w.Show();
                    result = "shown: " + w.Title;
                }
                catch (Exception ex)
                {
                    Exception e2 = ex.InnerException == null ? ex : ex.InnerException;
                    result = "threw " + e2.GetType().Name + ": " + e2.Message;
                }
            }));
            bool okOpen = result != null && result.StartsWith("shown");
            step("open PlaybackControlCenter", okOpen,
                 result == null ? "?" : result);
            Window now = FindWindowByTitle("Playback");
            step("window present", now != null,
                 now == null ? "still not found after Show()" : "found");
            sb.Append("]}");
            return sb.ToString();
        }

        private string Stage_Ntlog(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // NinjaTrader's own entries, from memory. `since` is the index returned by
                    // the previous call, so a caller sees exactly what happened in between and
                    // never has to guess when to look.
                    SubscribeNtLog();
                    string toFile = ExtractJsonString(triggerJson, "toFile");
                    if (!string.IsNullOrWhiteSpace(toFile))
                    {
                        _ntLogToFile = string.Equals(toFile, "true", StringComparison.OrdinalIgnoreCase);
                        step("mirror to file", true, _ntLogToFile
                             ? ("ON - " + Path.Combine(Globals.UserDataDir, "NT8Bridge", "ntlog.txt")
                                + "   (debugging aid, remember to turn it off)")
                             : "OFF");
                    }
                    string needle = ExtractJsonString(triggerJson, "contains");
                    int since = 0;
                    string sinceS = ExtractJsonString(triggerJson, "since");
                    if (!string.IsNullOrWhiteSpace(sinceS)) int.TryParse(sinceS, out since);

                    List<string> snap; int from;
                    lock (_ntLogGate)
                    {
                        snap = new List<string>(_ntLog);
                        from = NtLogOffset(since);
                    }
                    step("subscribed", _ntLogSubscribed, _ntLogSubscribed ? "yes" : "NO - no entries will arrive");
                    // THE MARK THE CALLER TAKES AWAY MUST BE THE SEQUENCE NUMBER,
                    // not the slot count - NtLogOffset() above reads it as one.
                    // Reporting snap.Count here while the search counted entries
                    // left the two halves speaking different languages: once the
                    // buffer sat at its cap the reported mark stopped growing, and
                    // the next call asked for a window that no longer meant what it
                    // said. Measured 27.08.2026: the connect check scanned 60
                    // entries and missed a "Connected" that NinjaTrader had logged
                    // nine seconds earlier.
                    step("index", true, "" + NtLogCount());
                    int shown = 0;
                    for (int i = from; i < snap.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(needle)
                            && snap[i].IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        step("e" + i, true, snap[i]);
                        if (++shown >= 60) break;
                    }
                    step("matched", shown > 0 || string.IsNullOrWhiteSpace(needle), "" + shown);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Stratlive(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Read-only. Dumps what NINJATRADER maintains for the strategy that is
                    // actually in the grid - counters no bot has to cooperate with.
                    //
                    // The rule (established 2026-08-19): nothing that comes from the bot may serve as
                    // evidence, because the final test runs an EMPTY bot. Print statements and
                    // the census file are the bot's; CurrentBar, BarsArray, Bars.Count and the
                    // adapter's tick counter are NinjaTrader's.
                    // ⚠ ALL ROWS, not the first one.
                    //
                    // Measured 2026-08-22: the grid can hold more than one strategy
                    // row (it went 0 -> 1 -> 2), but this stage reported "1 row"
                    // throughout because it returned the FIRST entry only. A reader
                    // that stops at the first row cannot say what is enabled - so
                    // every row is listed, and its counters are reported per row.
                    Window ccL = FindWindowByTitle("Control Center");
                    if (ccL == null) { step("Control Center", false, "not found"); sb.Append("]}"); return sb.ToString(); }
                    List<StrategyBase> liveAll = (List<StrategyBase>)ccL.Dispatcher.Invoke(new Func<object>(delegate
                    {
                        var found = new List<StrategyBase>();
                        object g = FindElement(ccL, "grdStrategies");
                        foreach (var kv in GridRows(g)) if (kv.Value != null) found.Add(kv.Value);
                        return found;
                    }));
                    step("rows", liveAll.Count > 0, liveAll.Count + " strategy row(s) in the grid");
                    if (liveAll.Count == 0) { step("strategy", false, "no strategy in the grid"); sb.Append("]}"); return sb.ToString(); }

                    // Per-row identity and state; the detail counters below stay on the
                    // FIRST row (they are the expensive ones and the single-bot chain
                    // reads them by that name).
                    for (int r = 0; r < liveAll.Count; r++)
                    {
                        StrategyBase sr = liveAll[r];
                        string acc = "";
                        try { acc = sr.Account == null ? "?" : sr.Account.Name; } catch { }
                        string cb = "";
                        try { cb = "  CurrentBar=" + sr.CurrentBar; } catch { }
                        step("row[" + r + "]", true, sr.GetType().Name + "  State=" + sr.State
                             + "  Account=" + acc + cb);
                    }

                    StrategyBase live = liveAll[0];
                    step("strategy", true, live.GetType().Name + "  State=" + live.State);

                    // What NinjaTrader counts for this strategy
                    try { step("CurrentBar", true, "" + live.CurrentBar); } catch (Exception ex) { step("CurrentBar", false, Deep(ex)); }
                    try
                    {
                        Bars[] ba = live.BarsArray;
                        step("BarsArray", ba != null, ba == null ? "null" : ba.Length + " series");
                        if (ba != null)
                            for (int i = 0; i < ba.Length; i++)
                            {
                                Bars b0 = ba[i];
                                if (b0 == null) { step("bars[" + i + "]", false, "null"); continue; }
                                string fromTo = "";
                                try { if (b0.Count > 0) fromTo = "  " + b0.GetTime(0) + " .. " + b0.GetTime(b0.Count - 1); }
                                catch { }
                                step("bars[" + i + "]", true, b0.Count + " bars  " + b0.BarsPeriod + fromTo);
                            }
                    }
                    catch (Exception ex) { step("BarsArray", false, Deep(ex)); }
                    try { step("CurrentBars", true, live.CurrentBars == null ? "null" : string.Join(",", Array.ConvertAll(live.CurrentBars, delegate(int v) { return v.ToString(); }))); }
                    catch (Exception ex) { step("CurrentBars", false, Deep(ex)); }
                    try { step("Account", true, live.Account == null ? "null" : live.Account.Name); } catch { }
                    try { step("IsTickReplay", true, "" + live.IsTickReplay); } catch { }
                    try { step("Instrument", true, live.Instrument == null ? "null" : live.Instrument.FullName); } catch { }

                    // and the adapter's own tick counter, sampled twice
                    Type pbL = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                    if (pbL != null)
                    {
                        FieldInfo fT = pbL.GetField("RealtimeTickCount", BFStatic);
                        PropertyInfo pN = pbL.GetProperty("NowEst", BFStatic);
                        object a1 = fT == null ? null : fT.GetValue(null);
                        object n1 = pN == null ? null : pN.GetValue(null);
                        Thread.Sleep(1000);
                        object a2 = fT == null ? null : fT.GetValue(null);
                        object n2 = pN == null ? null : pN.GetValue(null);
                        long d1 = 0, d2 = 0;
                        try { d1 = Convert.ToInt64(a1); d2 = Convert.ToInt64(a2); } catch { }
                        // See stage `transport`: this counter runs on wall time, not on
                        // data - measured 2026-08-20, parked and running both gave 6 in a
                        // 6000 ms window. NowEst below is the channel that discriminates.
                        step("RealtimeTickCount", true, a1 + " -> " + a2 + "   delta=" + (d2 - d1)
                             + " in 1000 ms   (wall-clock driven - does NOT prove motion)");
                        step("NowEst", true, n1 + " -> " + n2);
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Strategystate(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // NinjaTrader's order-rejection notice is the one modal this stage
                    // confirms itself - see DismissOrderRejectNotices for the measurement
                    // and the scope. Counted here so the transcript carries every one.
                    int noticesNow = DismissOrderRejectNotices();
                    _orderNoticesDismissed += noticesNow;
                    step("order notices", true, noticesNow + " dismissed this sample, "
                                                + _orderNoticesDismissed + " in this run ("
                                                + _orderNoticesScan + ")");

                    // ⚠ THE GENERIC END-OF-RUN SIGNAL: State.Terminated.
                    //
                    // Every NinjaScript passes through OnStateChange and ends at
                    // State.Terminated - no strategy has to cooperate, nothing has to be
                    // written to disk, and no GUI element is involved. That matters
                    // because this code is meant to go back upstream.
                    //
                    // Rejected earlier, each for a measured reason: the clock standing
                    // still (a guess about stillness), IsAvailableChanged (subscribed
                    // 2026-08-19, never fired), the progress slider (falls short of its
                    // maximum when data is missing), and a strategy's own counter file
                    // (only some strategies write one).
                    Window ccs = FindWindowByTitle("Control Center");
                    if (ccs == null) { step("Control Center", false, "not found"); sb.Append("]}"); return sb.ToString(); }
                    string outSt = (string)ccs.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object grid = FindElement(ccs, "grdStrategies");
                        if (grid == null) return "grdStrategies not found";
                        PropertyInfo pSrc = grid.GetType().GetProperty("DataSource");
                        System.Collections.IEnumerable rows =
                            pSrc == null ? null : pSrc.GetValue(grid, null) as System.Collections.IEnumerable;
                        if (rows == null) return "no DataSource";
                        StringBuilder o = new StringBuilder();
                        int n = 0;
                        foreach (object row in rows)
                        {
                            if (row == null) continue;
                            n++;
                            PropertyInfo pn = row.GetType().GetProperty("Name");
                            string nm = pn == null ? "?" : ("" + pn.GetValue(row, null)).Trim();
                            // The row is a view; the strategy hangs off it under one of a
                            // few names depending on the build, so try them in turn.
                            object strat = null;
                            foreach (string cand in new string[] { "Strategy", "StrategyBase", "NinjaScript", "Instance" })
                            {
                                PropertyInfo ps = row.GetType().GetProperty(cand);
                                if (ps == null) continue;
                                try { strat = ps.GetValue(row, null); } catch { }
                                if (strat != null) break;
                            }
                            string st = "?";
                            if (strat != null)
                            {
                                PropertyInfo pst = strat.GetType().GetProperty("State");
                                if (pst != null) { try { st = "" + pst.GetValue(strat, null); } catch { } }
                            }
                            PropertyInfo pe = row.GetType().GetProperty("IsEnabled");
                            string en = pe == null ? "?" : ("" + pe.GetValue(row, null));
                            o.Append(nm).Append(" State=").Append(st).Append(" IsEnabled=").Append(en).Append(" | ");
                        }
                        if (n == 0) return "no strategy rows";
                        return o.ToString();
                    }));
                    bool terminated = outSt.IndexOf("State=Terminated", StringComparison.Ordinal) >= 0;
                    step("rows", outSt.IndexOf("not found") < 0, outSt);
                    // ⚠ `ok` MEANS "THIS STEP DID ITS JOB", NEVER "THE ANSWER IS YES".
                    //
                    // This line used to pass `terminated` as the ok flag, so the GOOD case
                    // - the strategy is still running - was reported as a FAIL. Measured
                    // 2026-08-20: the driver's hard stop killed a healthy run on it,
                    // twice. A guard cannot act on FAIL while a step uses the flag as a
                    // data field, and a guard that cannot act is the same as none.
                    //
                    // Reading the state IS the job here, so ok is true when it was read.
                    // The answer goes where answers belong: into the detail.
                    step("terminated", true, terminated ? "yes - the strategy has terminated"
                                                        : "no - the strategy is still running");
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Playbackevents(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    Type pbE = typeof(Connection).Assembly.GetType("NinjaTrader.Adapter.PlaybackAdapter", false);
                    if (pbE != null)
                    {
                        PropertyInfo pa = pbE.GetProperty("IsAvailable", BFStatic);
                        if (pa != null) step("IsAvailable", true, "" + pa.GetValue(null));
                        PropertyInfo pn = pbE.GetProperty("NowEst", BFStatic);
                        if (pn != null) step("NowEst", true, "" + pn.GetValue(null));
                    }
                    // Same rule as `terminated` above: this is an answer, not an outcome.
                    // Whether the playback event channel happens to be subscribed does not
                    // decide whether THIS step worked - it decides what the event list
                    // below can contain, and that is said in the text.
                    step("subscribed", true, _pbSubscribed
                         ? "yes - playback events are being recorded"
                         : "no - no playback events will appear below");
                    lock (_pbEvents)
                    {
                        if (_pbEvents.Count == 0) step("events", true, "none recorded yet");
                        foreach (string s in _pbEvents) step("event", true, s);
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Botout(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Hand out the bot's Print() lines written since index `since`
                    // and report the new index, so the driver can poll without
                    // gaps and without repeats - the same contract as `ntlog`.
                    SubscribeBotOutput();
                    int sinceB = 0;
                    string sB = ExtractJsonString(triggerJson, "since");
                    if (!string.IsNullOrWhiteSpace(sB)) int.TryParse(sB, out sinceB);
                    List<string> snapB; long droppedB; int seenB; int fromB;
                    lock (_botOutGate)
                    {
                        snapB = new List<string>(_botOut);
                        droppedB = _botOutDropped;
                        // THE MARK IS A SEQUENCE NUMBER, NOT A SLOT INDEX. _botOut is a
                        // ring and drops from the FRONT, so a slot index stops meaning
                        // anything the moment it wraps: Count pins at BotOutMax, the
                        // caller hands that back as `since`, and the window below is
                        // empty from then on. That is exactly what killed every `ntlog`
                        // wait once THAT buffer saturated (measured, see _ntLogDropped
                        // above); here the code shape is identical and the only
                        // difference is a cap ten times larger.
                        seenB = (int)(droppedB + snapB.Count);
                        long offB = sinceB - droppedB;
                        fromB = offB < 0 ? 0 : (offB > snapB.Count ? snapB.Count : (int)offB);
                    }
                    // The buffer is a RING: if it dropped lines, the caller's index
                    // no longer points where it thinks. Say so instead of quietly
                    // handing over a shifted window.
                    step("subscribed", _botOutSubscribed,
                         _botOutSubscribed ? "NinjaTrader.Code.Output.OutputEvent"
                                           : "NOT subscribed - see bridge.log for the failing step");
                    step("dropped", droppedB == 0, "" + droppedB
                         + (droppedB > 0 ? "  (buffer overflowed - poll faster)" : ""));
                    for (int i = fromB; i < snapB.Count; i++)
                        step("o" + (droppedB + i), true, snapB[i]);
                    step("nowIndex", true, "" + seenB);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Restore(PbRunCtx ctx)
        {
            string id = ctx.Id;
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // ONE call that puts NinjaTrader back: disconnect, disable and remove
                    // every strategy row, close every dialog. Server-side, so it costs
                    // ONE round trip.
                    //
                    // Measured 2026-08-19: the client used to do this as ten separate
                    // stages with 45-60 s budgets each - minutes of waiting whenever
                    // NinjaTrader was busy, which is exactly when a cleanup is needed. A
                    // teardown that takes minutes does not get run.
                    Progress(id, "-> RestoreBaselineNow (disconnect, remove strategies, "
                                 + "close dialogs - goes through the UI thread)");
                    RestoreBaselineNow(RequestTtlSec(triggerJson));
                    // ⚠ THE GRID IS NOT IN THE VISUAL TREE UNTIL ITS TAB HAS BEEN SHOWN.
                    //
                    // This looked for an element named "grdStrategies" in the Control
                    // Center's tree. NinjaTrader virtualizes a tab's content while the tab
                    // is inactive, so on a profile whose UI.xml has never had the Strategies
                    // tab selected there is nothing to find - and the step reported -1,
                    // which the driver treats as a failed mandatory step and ends the run
                    // before it reaches the connect.
                    //
                    // Measured 2026-08-24 on a freshly created UI.xml: "strategy rows -1",
                    // run aborted; one manual click on the Strategies tab and the same run
                    // went through to the data end. That click cannot be part of a headless
                    // procedure, and every user with a new profile meets it.
                    //
                    // WithStrategiesGrid is upstream's own answer to exactly this - it
                    // activates tabs until the grid materializes, reads, and puts the user's
                    // tab back, on the Control Center's own dispatcher and with a bounded
                    // Invoke so a busy UI thread cannot wedge the poller. Using it beats a
                    // second implementation of the same knowledge.
                    //
                    // int? on purpose: default(int) is 0, and "could not reach the grid" must
                    // not read as "no strategies are left".
                    List<string> gridNotes = new List<string>();
                    Progress(id, "-> counting strategy rows (materializing the tab if need be)");
                    // Two counts, and a bounded settle. RestoreBaselineNow waits for the rows
                    // WITH a strategy object to reach 0 (GridRows); the raw entry count of the
                    // grid's source (RowCount) can lag behind that by a refresh. Measured
                    // 2026-09-03 06:32 on a loaded machine (twelve concurrent headless
                    // NinjaTrader hosts): "restore: strategy
                    // rows 1 -> 0 after 103 ms" in the log, RowCount still 1 when this verdict
                    // read it, baselineClean false and rc 2 for a run that
                    // had reached its data end with no strategy running. So the verdict is the
                    // removal's own measurement (rows carrying a strategy), the raw count is
                    // reported next to it, and the grid gets up to ROWS_SETTLE_MS to catch up.
                    const int ROWS_SETTLE_MS = 10000;
                    int left = -1, withStrategy = -1;
                    long settleMs = 0;
                    System.Diagnostics.Stopwatch swRows = System.Diagnostics.Stopwatch.StartNew();
                    while (true)
                    {
                        int[] counted = WithStrategiesGrid<int[]>(delegate(object g)
                        {
                            int raw = RowCount(g);
                            int live = 0;
                            try { foreach (var kv in GridRows(g)) if (kv.Value != null) live++; }
                            catch { live = -1; }
                            return new int[] { raw, live };
                        }, gridNotes);
                        if (counted != null) { left = counted[0]; withStrategy = counted[1]; }
                        settleMs = swRows.ElapsedMilliseconds;
                        if (left == 0 || withStrategy == 0 || counted == null || settleMs >= ROWS_SETTLE_MS) break;
                        Thread.Sleep(250);
                    }
                    if (left < 0 && gridNotes.Count > 0)
                        Progress(id, "   grid unreachable: " + string.Join("; ", gridNotes.ToArray()));
                    int dlgs = 0;
                    foreach (Window w in AllWindowsIncludingOwned())
                    {
                        try { if (w.GetType().FullName.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0) dlgs++; }
                        catch { }
                    }
                    DateTime seenR;
                    bool movingR = TransportMoving(out seenR);
                    bool rowsClean = withStrategy >= 0 ? withStrategy == 0 : left == 0;
                    step("strategy rows", rowsClean,
                         left + " entries, " + (withStrategy >= 0 ? withStrategy + " with a strategy" : "strategy column unreadable")
                         + " after " + settleMs + " ms");
                    step("dialogs", dlgs == 0, "" + dlgs);
                    step("transport", !movingR, movingR ? ("STILL RUNNING at " + seenR) : ("parked at " + seenR));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Source(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string srcS = ctx.SrcS;
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Step 3 of the operating sequence: Market Replay or Historical, set
                    // AFTER the connection is up and the panel is no longer greyed.
                    //
                    // Both places are written: the adapter static that governs the run and
                    // the two radio buttons the user reads. Writing only the static leaves
                    // the panel showing the other source - the same split that made the
                    // panel report "1x" on a clock running at full speed.
                    //
                    // Caution, measured 2026-08-18: flipping these radios on a CONNECTED
                    // transport once blocked NinjaTrader's dispatcher for 900 s. That was
                    // before today's fixes; the caller should use a short timeout so a
                    // repeat shows up as a finding instead of a wait.
                    bool histSrc = string.Equals(srcS, "historical", StringComparison.OrdinalIgnoreCase);
                    bool writeRadios = string.Equals(ExtractJsonString(triggerJson, "radios"),
                                                     "true", StringComparison.OrdinalIgnoreCase);
                    SetStatic(pb, "IsSourceHistoricalData", histSrc, step);

                    Window wsrc = FindWindowByTitle("Playback");
                    // ⚠ THE RADIOS ARE A SECOND OPINION, AND A HOST WITHOUT A UI HAS NONE.
                    //
                    // The source itself was already settled one line above, by the object
                    // model: IsSourceHistoricalData written and read back. These radios only
                    // confirm that the PANEL agrees - which is worth checking when a panel
                    // exists and is meaningless when none does.
                    //
                    // Failing on their absence stopped a run whose source was correct
                    // (measured 2026-08-24, headless: "IsSourceHistoricalData wrote=False
                    // read=False" immediately followed by "panel radios FAIL - Playback
                    // window not found").
                    if (wsrc == null && NoUiHost())
                        step("panel radios", true,
                             "no UI in this host - the source is settled by"
                             + " IsSourceHistoricalData above, which read back correctly");
                    else if (wsrc == null) step("panel radios", false, "Playback window not found");
                    else
                    {
                        Window w2 = wsrc;
                        bool h2 = histSrc;
                        string outR = (string)w2.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            System.Windows.Controls.RadioButton rh =
                                FindElement(w2, "rbHistoricalData") as System.Windows.Controls.RadioButton;
                            System.Windows.Controls.RadioButton rr =
                                FindElement(w2, "rbRecordedData") as System.Windows.Controls.RadioButton;
                            if (rh == null || rr == null) return "radio buttons not found";
                            if (rh.IsChecked == h2 && rr.IsChecked == !h2)
                                return "already " + (h2 ? "Historical" : "Market Replay");
                            // ⚠ The radios are READ, not written.
                            //
                            // Measured 2026-08-18 (900 s) and again 2026-08-19 (the stage
                            // stopped answering within its 10 s budget): assigning
                            // IsChecked on a CONNECTED Playback panel makes NinjaTrader
                            // rebuild the transport synchronously on the UI thread - and
                            // this call sits in that same dispatcher, so it waits on
                            // itself. The adapter static above already decides the source;
                            // the panel follows it on the next connect.
                            if (!writeRadios)
                                return "panel shows " + (rh.IsChecked == true ? "Historical" : "Market Replay")
                                     + ", adapter set to " + (h2 ? "Historical" : "Market Replay")
                                     + " - panel follows on the next connect (radios not written:"
                                     + " that deadlocks the dispatcher)";
                            rh.IsChecked = h2;
                            rr.IsChecked = !h2;
                            return "Historical=" + rh.IsChecked + " MarketReplay=" + rr.IsChecked;
                        }));
                        step("panel radios", outR.IndexOf("not found") < 0, outR);
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Panelstate(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Read-only. Samples every named control of the Playback panel and reports
                    // when each first became enabled. Every named control is sampled, because
                    // the controls do not become enabled at the same moment and one of
                    // them alone does not say the panel is ready.
                    int waitMs2 = 30000;
                    string wS2b = ExtractJsonString(triggerJson, "timeoutMs");
                    int parsed2;
                    if (!string.IsNullOrWhiteSpace(wS2b) && int.TryParse(wS2b, out parsed2) && parsed2 > 0)
                        waitMs2 = parsed2;

                    Window wp = FindWindowByTitle("Playback");
                    if (wp == null) { step("Playback window", false, "not found: " + WindowTitles()); sb.Append("]}"); return sb.ToString(); }

                    string[] names = new string[] {
                        "slider", "btnPlay", "dtpStart", "dtpEnd",
                        "rbHistoricalData", "rbRecordedData",
                        "speedButtonsGrid", "textBlockSpeed" };
                    Dictionary<string, int> firstOk = new Dictionary<string, int>();
                    Dictionary<string, string> lastSeen = new Dictionary<string, string>();
                    foreach (string nm2 in names) { firstOk[nm2] = -1; lastSeen[nm2] = "not found"; }
                    int winFirst = -1;

                    System.Diagnostics.Stopwatch sw2 = System.Diagnostics.Stopwatch.StartNew();
                    while (sw2.ElapsedMilliseconds < waitMs2)
                    {
                        int ms = (int)sw2.ElapsedMilliseconds;
                        Window wpp = wp;
                        string snap = (string)wpp.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            StringBuilder o2 = new StringBuilder();
                            o2.Append(wpp.IsEnabled ? "1" : "0");
                            foreach (string nm3 in names)
                            {
                                object el = FindElement(wpp, nm3);
                                System.Windows.UIElement ue2 = el as System.Windows.UIElement;
                                if (ue2 == null) { o2.Append("|-"); continue; }
                                o2.Append(ue2.IsEnabled ? "|1" : "|0");
                            }
                            return o2.ToString();
                        }));
                        string[] parts2 = snap.Split('|');
                        if (winFirst < 0 && parts2[0] == "1") winFirst = ms;
                        for (int i2 = 0; i2 < names.Length && i2 + 1 < parts2.Length; i2++)
                        {
                            string v2 = parts2[i2 + 1];
                            lastSeen[names[i2]] = v2 == "1" ? "enabled" : (v2 == "0" ? "disabled" : "not found");
                            if (firstOk[names[i2]] < 0 && v2 == "1") firstOk[names[i2]] = ms;
                        }
                        bool allOk = winFirst >= 0;
                        foreach (string nm4 in names)
                            if (firstOk[nm4] < 0 && lastSeen[nm4] != "not found") { allOk = false; break; }
                        if (allOk) break;
                        Thread.Sleep(250);
                    }

                    step("window", winFirst >= 0,
                         winFirst >= 0 ? ("enabled after " + winFirst + " ms") : "still disabled");
                    foreach (string nm5 in names)
                        step(nm5, firstOk[nm5] >= 0,
                             firstOk[nm5] >= 0 ? ("enabled after " + firstOk[nm5] + " ms")
                                               : (lastSeen[nm5] + " for the whole " + waitMs2 + " ms"));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Panelflips(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Instant answer, no waiting, no deadline: the recorded transitions of the
                    // Playback panel since `since`. The caller decides how long to keep asking.
                    WatchPanelEnabled();
                    int sinceP = 0;
                    string sp = ExtractJsonString(triggerJson, "since");
                    if (!string.IsNullOrWhiteSpace(sp)) int.TryParse(sp, out sinceP);
                    List<string> snapP; long droppedP; int seenP; int fromP;
                    lock (_panelGate)
                    {
                        snapP = new List<string>(_panelFlips);
                        droppedP = _panelDropped;
                        // Sequence number, not slot index - _panelFlips is a ring too.
                        seenP = (int)(droppedP + snapP.Count);
                        long offP = sinceP - droppedP;
                        fromP = offP < 0 ? 0 : (offP > snapP.Count ? snapP.Count : (int)offP);
                    }
                    step("watching", _panelWatched, _panelWatched ? "IsEnabledChanged subscribed"
                                                                 : "NOT subscribed - Playback window missing?");
                    step("index", true, "" + seenP);
                    for (int i = fromP; i < snapP.Count; i++)
                        step("f" + (droppedP + i), true, snapP[i]);
                    // the value right now, so a caller that arrives late still knows where it stands
                    Window wpN = FindWindowByTitle("Playback");
                    if (wpN != null)
                    {
                        string nowState = (string)wpN.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            object sl = FindElement(wpN, "slider");
                            System.Windows.UIElement ue = sl as System.Windows.UIElement;
                            return ue == null ? "slider not found" : (ue.IsEnabled ? "enabled" : "disabled");
                        }));
                        step("now", true, nowState);
                        // ⚠ WHICH OBJECT are we watching? NinjaTrader REBUILDS this panel, and a
                        // subscription on the discarded control is silent forever - it would look
                        // exactly like "the panel never changed". The identity makes the
                        // difference visible instead of leaving it as a guess.
                        string ident = (string)wpN.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            object sl2 = FindElement(wpN, "slider");
                            return sl2 == null ? "-" : (sl2.GetType().Name + "#" + sl2.GetHashCode());
                        }));
                        step("watched object", _panelWatchedIdent == ident,
                             "now=" + ident + "   subscribed=" + (_panelWatchedIdent ?? "-")
                             + (_panelWatchedIdent == ident ? "" : "   >>> REPLACED, the subscription is dead"));
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Ready(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Step 2 of the operating sequence: wait until the Playback panel is
                    // no longer greyed out. Right after connecting, NinjaTrader leaves its
                    // controls disabled while it prepares - the progress slider reported
                    // "[DISABLED]" in earlier dumps. Anything written into a disabled panel
                    // is written into a control that is about to be rebuilt.
                    //
                    // The readiness signal is the slider: IsEnabled on the control the
                    // panel uses for the run itself, not on the window.
                    int waitMs = 15000;
                    string wS = ExtractJsonString(triggerJson, "timeoutMs");
                    int parsed;
                    if (!string.IsNullOrWhiteSpace(wS) && int.TryParse(wS, out parsed) && parsed > 0) waitMs = parsed;

                    Window wr = FindWindowByTitle("Playback");
                    if (wr == null) { step("Playback window", false, "not found"); sb.Append("]}"); return sb.ToString(); }

                    // ⚠ THE TRANSITION IS THE SIGNAL, NOT THE CURRENT VALUE.
                    //
                    // "is it enabled right now" cannot tell "finished preparing" from "has not
                    // STARTED preparing". Measured 2026-08-19: this stage answered "ready" in
                    // 1.0 s while an independent sampling run showed the panel stays disabled
                    // for 11.3 s after the connect. It had read the state from BEFORE the
                    // connect, and the run then wrote its source, dates and speed into a panel
                    // NinjaTrader was about to rebuild - the user saw the box still grey while
                    // the run carried on.
                    //
                    // So: the panel must be seen DISABLED first, then ENABLED, and it must STAY
                    // enabled for a settling period. A check that can pass for the wrong reason
                    // is not a check.
                    // The driver sets this; a human stepping through does not.
                    bool requireTransition = string.Equals(
                        ExtractJsonString(triggerJson, "requireTransition"), "true",
                        StringComparison.OrdinalIgnoreCase);
                    int settleMs = 1500;
                    string setS = ExtractJsonString(triggerJson, "settleMs");
                    int settleParsed;
                    if (!string.IsNullOrWhiteSpace(setS) && int.TryParse(setS, out settleParsed) && settleParsed > 0)
                        settleMs = settleParsed;

                    bool ready = false;
                    bool sawDisabled = false;
                    string last = "?";
                    int firstDisabledMs = -1, firstEnabledMs = -1;
                    System.Diagnostics.Stopwatch swR = System.Diagnostics.Stopwatch.StartNew();
                    long enabledSince = -1;
                    while (swR.ElapsedMilliseconds < waitMs)
                    {
                        Window wrr = wr;
                        last = (string)wrr.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            // IsEnabled is inherited from the window, so every named control
                            // reports the same value - measured: all eight flip in the same
                            // sample. The slider is read as the panel's own signal.
                            object sl = FindElement(wrr, "slider");
                            System.Windows.UIElement ue = sl as System.Windows.UIElement;
                            if (ue == null) return "slider not found";
                            return ue.IsEnabled ? "enabled" : "disabled";
                        }));
                        long now = swR.ElapsedMilliseconds;
                        if (last == "disabled")
                        {
                            if (!sawDisabled) { sawDisabled = true; firstDisabledMs = (int)now; }
                            enabledSince = -1;          // the streak restarts after every grey phase
                        }
                        else if (last == "enabled")
                        {
                            if (enabledSince < 0) { enabledSince = now; if (firstEnabledMs < 0 && sawDisabled) firstEnabledMs = (int)now; }
                            if (sawDisabled && now - enabledSince >= settleMs) { ready = true; break; }
                            // no transition demanded and it has been free long enough: stop early
                            // instead of burning the whole budget on a panel that is fine.
                            if (!requireTransition && !sawDisabled && now - enabledSince >= settleMs) break;
                        }
                        Thread.Sleep(250);
                    }
                    // ⚠ SEEING THE TRANSITION IS THE STRONG CASE, NOT THE ONLY ONE.
                    //
                    // "grey seen -> free seen -> stable" proves the panel was rebuilt AFTER the
                    // connect. But the grey phase lasts about 12 s and then it is over: a check
                    // that runs two minutes later never sees it and would report a failure for a
                    // panel that is perfectly ready. That happened on 2026-08-20 during a
                    // single-step session, and it is a false alarm, not a finding.
                    //
                    // So both outcomes are reported for what they are, and the CALLER decides:
                    // a driver that connects and checks back to back passes requireTransition
                    // (the connect must have had an effect), a human stepping through does not.
                    bool stableFree = last == "enabled" && enabledSince >= 0
                                      && swR.ElapsedMilliseconds - enabledSince >= settleMs;
                    if (!ready && !sawDisabled && stableFree && !requireTransition)
                    {
                        step("panel ready", true,
                             "already free and stable for " + settleMs + " ms - no grey phase seen, "
                             + "so this does NOT prove the connect took effect (pass "
                             + "requireTransition=true where that matters)");
                    }
                    else
                    {
                        step("panel ready", ready,
                             ready ? ("grey from " + firstDisabledMs + " ms, free from " + firstEnabledMs
                                      + " ms, then " + settleMs + " ms stable")
                                   : (!sawDisabled
                                      ? ("never went grey within " + waitMs + " ms - with "
                                         + "requireTransition the connect must show its effect here")
                                      : ("still " + last + " after " + waitMs + " ms")));
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Speed(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Four places hold the playback speed, and each one alone is a lie
                    // (measured 2026-08-19). By default this stage writes the PANEL's
                    // two; `apply:"now"` adds the adapter's. Every one written is read
                    // back:
                    //
                    //   PlaybackAdapter.PlaybackSpeed  the property. Writing it STARTS the
                    //                                  transport (bisected, see stage "range").
                    //   PlaybackAdapter.playbackSpeed  the live field. Connecting moves the
                    //                                  property to `oldPlaybackSpeed` and
                    //                                  leaves this at 1.
                    //   PlaybackControlCenter.oldReplaySpeed  the PANEL's own memory. It sat
                    //                                  at 1 while the adapter said Max, which
                    //                                  is why the panel kept reading "1x".
                    //   textBlockSpeed.Text            the label. Writing only this makes the
                    //                                  display agree with itself and nothing else.
                    //
                    // No clicking: everything here is a field or property write.
                    string vS2 = ExtractJsonString(triggerJson, "value");
                    Window ws2 = FindWindowByTitle("Playback");
                    FieldInfo fiMaxS = pb.GetField("MaxSpeedValue", BFStatic);
                    object maxS = fiMaxS != null ? fiMaxS.GetValue(null) : (object)int.MaxValue;
                    object want = maxS;
                    if (!string.IsNullOrWhiteSpace(vS2) && !string.Equals(vS2, "max", StringComparison.OrdinalIgnoreCase))
                    {
                        int iv;
                        if (int.TryParse(vS2, System.Globalization.NumberStyles.Integer, InvCi, out iv)) want = iv;
                    }

                    // ⚠ NOT ONE TICK MAY PASS DURING SETUP.
                    //
                    // Writing PlaybackAdapter.PlaybackSpeed starts the transport (bisected
                    // 2026-08-19). Parking it again afterwards is NOT good enough: the data
                    // consumed in between is gone, and a strategy attached later starts
                    // mid-stream. So the adapter is left alone here.
                    //
                    // What is written instead is the PANEL's paused memory,
                    // `oldReplaySpeed`. Measured the same day: pausing sets
                    // PlaybackAdapter.PlaybackSpeed to 0 while the panel keeps the real
                    // value in oldReplaySpeed, and pressing play restores it - one click on
                    // btnPlay took PlaybackSpeed 0 -> 2147483647 and the clock went
                    // +3.7 h in 8 s, with nothing having written the speed.
                    //
                    // `apply:"now"` writes the adapter too - for a caller that deliberately
                    // wants the change to take effect on a running transport.
                    bool applyNow = string.Equals(ExtractJsonString(triggerJson, "apply"),
                                                  "now", StringComparison.OrdinalIgnoreCase);
                    if (!applyNow)
                        step("adapter untouched", true,
                             "PlaybackSpeed not written - that would start the transport and "
                             + "cost the strategy the ticks in between");
                    else
                    {
                        // 1. STATE BEFORE - and it is the EFFECT that is read, not the
                        //    property. SetStatic writes and reads the value back from the
                        //    same reference in the same instant: that proves the value
                        //    landed, never that the transport reacted to it. NowEst is
                        //    written by NinjaTrader and by no bot, so it is evidence.
                        DateTime seenB2;
                        bool movingB2 = TransportMoving(out seenB2);
                        step("transport before", true,
                             (movingB2 ? "moving" : "parked") + " at " + seenB2);

                        SetStatic(pb, "PlaybackSpeed", want, step);
                        try
                        {
                            FieldInfo fiSp = pb.GetField("playbackSpeed", BFStatic);
                            if (fiSp == null) step("field playbackSpeed", false, "not found");
                            else
                            {
                                fiSp.SetValue(null, want);
                                step("field playbackSpeed", ("" + fiSp.GetValue(null)) == ("" + want),
                                     "wrote=" + want + " read=" + fiSp.GetValue(null));
                            }
                        }
                        catch (Exception ex) { step("field playbackSpeed", false, ex.Message); }

                        // 3. WAIT FOR THE CHANGE the write is supposed to cause: a speed
                        //    above zero starts the transport, zero parks it. Bound only by
                        //    the caller's own ttlSec - no constant in this file decides how
                        //    long NinjaTrader may take. TransportMoving's 1200 ms is the
                        //    sampling interval of the measurement, not a wait: a rate needs
                        //    two readings a known gap apart.
                        long wantN = 0;
                        try { wantN = Convert.ToInt64(want); } catch { }
                        string wantMotion = wantN > 0 ? "moving" : "parked";
                        string gotMotion; long msMotion;
                        bool reacted = WaitUntilChanged(delegate
                        {
                            DateTime s3;
                            return TransportMoving(out s3) ? "moving" : "parked";
                        }, movingB2 ? "moving" : "parked", wantMotion,
                           RequestTtlSec(triggerJson), out gotMotion, out msMotion);
                        step("transport reacted", reacted,
                             (movingB2 ? "moving" : "parked") + " -> " + gotMotion
                             + " after " + msMotion + " ms (wanted " + wantMotion
                             + " for PlaybackSpeed=" + want + ")");
                    }

                    if (ws2 == null) step("panel", false, "Playback window not found");
                    else
                    {
                        Window wsp = ws2;
                        object wantP = want;
                        string outSp = (string)wsp.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            StringBuilder o = new StringBuilder();
                            FieldInfo fiOld = wsp.GetType().GetField("oldReplaySpeed",
                                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            if (fiOld == null) o.Append("oldReplaySpeed not found; ");
                            else
                            {
                                try
                                {
                                    fiOld.SetValue(wsp, Convert.ToInt32(wantP));
                                    o.Append("oldReplaySpeed=").Append(fiOld.GetValue(wsp)).Append("; ");
                                }
                                catch (Exception ex) { o.Append("oldReplaySpeed threw ").Append(ex.Message).Append("; "); }
                            }
                            object tbEl = FindElement(wsp, "textBlockSpeed");
                            System.Windows.Controls.TextBlock tb2 = tbEl as System.Windows.Controls.TextBlock;
                            if (tb2 == null) o.Append("textBlockSpeed not found");
                            else
                            {
                                tb2.Text = ("" + wantP) == ("" + maxS) ? "Max" : (wantP + "x");
                                o.Append("label=").Append(tb2.Text);
                            }
                            return o.ToString();
                        }));
                        step("panel", outSp.IndexOf("not found") < 0, outSp);
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Template(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string typeName = ctx.TypeName;
            string fromS = ctx.FromS;
            string toS = ctx.ToS;
            string bpS = ctx.BpS;
            string trS = ctx.TrS;
            Dictionary<string, string> prms = ctx.Prms;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Dialog-free on purpose. The first attempt read the strategy out of the
                    // open Strategies dialog and picked the wrong object (the first entry of
                    // viewModel.ListAvailable). Nothing here needs a window.
                    string act = ExtractJsonString(triggerJson, "action");
                    if (string.IsNullOrWhiteSpace(act)) act = "list";
                    string tname = ExtractJsonString(triggerJson, "name");

                    string why;
                    Type stT = ResolveStrategyType(typeName, out why);
                    if (stT == null) { step("strategy type", false, why); sb.Append("]}"); return sb.ToString(); }
                    step("strategy type", true, stT.FullName);

                    StrategyBase probe;
                    try
                    {
                        probe = (StrategyBase)Activator.CreateInstance(stT);
                        probe.SetState(State.SetDefaults);
                    }
                    catch (Exception ex)
                    { step("instance", false, Deep(ex)); sb.Append("]}"); return sb.ToString(); }

                    string folder = TemplateFolder(probe);
                    step("folder", !string.IsNullOrEmpty(folder),
                         string.IsNullOrEmpty(folder) ? "GetTemplateFolder returned nothing" : folder);
                    if (string.IsNullOrEmpty(folder)) { sb.Append("]}"); return sb.ToString(); }

                    if (act == "list")
                    {
                        if (!Directory.Exists(folder)) step("templates", true, "folder does not exist yet");
                        else
                        {
                            string[] fs = Directory.GetFiles(folder, "*.xml");
                            if (fs.Length == 0) step("templates", true, "none");
                            foreach (string f in fs) step("template", true, Path.GetFileNameWithoutExtension(f));
                        }
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    string tfile = TemplatePath(probe, tname);
                    if (string.IsNullOrEmpty(tfile)) { step("name", false, "missing"); sb.Append("]}"); return sb.ToString(); }

                    if (act == "save")
                    {
                        // Saved from a fresh instance plus the request's params, so the whole
                        // template set for a test matrix can be produced from the CLI.
                        Type tt2 = TemplateType();
                        MethodInfo miSave = tt2 == null ? null : tt2.GetMethod("SaveFullStrategyTemplate",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (miSave == null) { step("save", false, "SaveFullStrategyTemplate not found"); sb.Append("]}"); return sb.ToString(); }

                        try
                        {
                            if (!string.IsNullOrWhiteSpace(bpS))
                                probe.BarsPeriod = (BarsPeriod)ConvertConfigToken(bpS, typeof(BarsPeriod));
                            if (!string.IsNullOrWhiteSpace(trS))
                                probe.IsTickReplay = string.Equals(trS, "true", StringComparison.OrdinalIgnoreCase);
                            if (!string.IsNullOrWhiteSpace(fromS)) probe.From = DateTime.Parse(fromS, InvCi);
                            if (!string.IsNullOrWhiteSpace(toS))
                                probe.To = DateTime.Parse(toS, InvCi).Date.AddDays(1).AddSeconds(-1);
                            InjectParams(probe, prms);

                            // Do not write a range into a file that could never be run. A
                            // fresh instance carries From 2099 / To 1800; saved unchanged,
                            // that template stays a loaded gun for every later `attach`.
                            string saveBad = RangeProblem(probe.From, probe.To);
                            if (saveBad != null)
                            {
                                step("save", false, "template not written - " + saveBad
                                     + ". Pass 'from' and 'to' with the save request.");
                                sb.Append("]}");
                                return sb.ToString();
                            }
                            Directory.CreateDirectory(Path.GetDirectoryName(tfile));
                            // The container is the ROOT ELEMENT, not the document.
                            // SaveFullStrategyTemplate FILLS the container - it writes
                            // <StrategyType> and <Strategy> side by side - so a document
                            // handed in directly would end up with two top-level elements
                            // ("This operation would create an incorrectly structured
                            // document"). The element name matches the files NinjaTrader
                            // itself writes: root <StrategyTemplate>.
                            System.Xml.Linq.XElement root =
                                new System.Xml.Linq.XElement("StrategyTemplate");
                            miSave.Invoke(null, new object[] { root, probe });
                            new System.Xml.Linq.XDocument(root).Save(tfile);
                            step("save", File.Exists(tfile),
                                 tfile + "  (" + new FileInfo(tfile).Length + " Bytes)");
                        }
                        catch (Exception ex) { step("save", false, Deep(ex)); }
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    if (act == "load")
                    {
                        // Read-back check: proves the file restores into a usable strategy
                        // BEFORE a run depends on it. `attach` does the same restore.
                        string p2;
                        StrategyBase got = RestoreTemplate(tfile, out p2);
                        if (got == null) { step("load", false, p2); sb.Append("]}"); return sb.ToString(); }
                        step("load", true, Path.GetFileName(tfile) + " -> " + got.GetType().Name
                             + ", State " + got.State
                             + ", BarsPeriod " + (got.BarsPeriod == null ? "null" : got.BarsPeriod.ToString())
                             + ", IsTickReplay " + got.IsTickReplay);
                        step("params", true, ParamReadback(got));
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    step("action", false, "unknown: " + act);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Findmethod(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Search the loaded NinjaTrader assemblies for methods by name.
                    //
                    // This is the tool for the standing task (established 2026-08-19): every
                    // place the bridge still presses a button has to be replaced by the
                    // call that button makes. Finding that call means searching the object
                    // model, not the visual tree.
                    string need = ExtractJsonString(triggerJson, "name");
                    string inType = ExtractJsonString(triggerJson, "type");
                    if (string.IsNullOrWhiteSpace(need)) { step("name", false, "missing"); sb.Append("]}"); return sb.ToString(); }
                    int hits = 0;
                    foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string an = null;
                        try { an = asm.GetName().Name; } catch { }
                        if (an == null || an.IndexOf("NinjaTrader", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                        catch { continue; }
                        foreach (Type ty in types)
                        {
                            if (ty == null) continue;
                            if (!string.IsNullOrWhiteSpace(inType)
                                && (ty.FullName == null || ty.FullName.IndexOf(inType, StringComparison.OrdinalIgnoreCase) < 0))
                                continue;
                            MethodInfo[] ms;
                            try { ms = ty.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); }
                            catch { continue; }
                            foreach (MethodInfo mi in ms)
                            {
                                if (mi.Name.IndexOf(need, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                if (hits++ > 120) break;
                                StringBuilder ps = new StringBuilder();
                                foreach (ParameterInfo pi3 in mi.GetParameters())
                                {
                                    if (ps.Length > 0) ps.Append(", ");
                                    ps.Append(pi3.ParameterType.Name).Append(" ").Append(pi3.Name);
                                }
                                step("m", true, (mi.IsStatic ? "static " : "") + ty.FullName + "." + mi.Name
                                                + "(" + ps + ") : " + mi.ReturnType.Name);
                            }
                            if (hits > 120) break;
                        }
                        if (hits > 120) break;
                    }
                    step("hits", true, "" + hits);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Elmembers(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Reflect over a named ELEMENT's type - methods included.
                    //
                    // This is the tool that made the AddOn click-free: for every button the
                    // bridge used to press, it showed the method behind it, and that method
                    // is what is called now (StrategiesGrid.StrategyEnable/Disable/Remove,
                    // PlaybackAdapter.PlaybackSpeed). It stays, because the next control
                    // that has to be driven is found the same way.
                    string wT2 = ExtractJsonString(triggerJson, "window");
                    string eN2 = ExtractJsonString(triggerJson, "element");
                    string filt2 = ExtractJsonString(triggerJson, "filter");
                    Window wq = FindWindowByTitle(string.IsNullOrWhiteSpace(wT2) ? "Strategies" : wT2);
                    if (wq == null) { step("window", false, "not found: " + wT2); sb.Append("]}"); return sb.ToString(); }
                    string outQ = (string)wq.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object el = FindElement(wq, eN2);
                        if (el == null) return "element '" + eN2 + "' not found";
                        Type et = el.GetType();
                        StringBuilder o = new StringBuilder();
                        o.Append("TYPE ").Append(et.FullName).Append(Environment.NewLine);
                        foreach (MethodInfo mi in et.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (IsAccessor(mi)) continue;
                            if (!string.IsNullOrWhiteSpace(filt2) && mi.Name.IndexOf(filt2, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            StringBuilder ps = new StringBuilder();
                            foreach (ParameterInfo pi2 in mi.GetParameters())
                            {
                                if (ps.Length > 0) ps.Append(", ");
                                ps.Append(pi2.ParameterType.Name).Append(" ").Append(pi2.Name);
                            }
                            o.Append("method ").Append(mi.Name).Append("(").Append(ps).Append(") : ")
                             .Append(mi.ReturnType.Name).Append(Environment.NewLine);
                        }
                        foreach (FieldInfo fi in et.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (!string.IsNullOrWhiteSpace(filt2) && fi.Name.IndexOf(filt2, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string v = "?";
                            try { v = "" + fi.GetValue(el); } catch { }
                            o.Append("field ").Append(fi.Name).Append(" : ").Append(fi.FieldType.Name)
                             .Append(" = ").Append(v.Length > 60 ? v.Substring(0, 60) : v).Append(Environment.NewLine);
                        }
                        foreach (PropertyInfo pr in et.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (!string.IsNullOrWhiteSpace(filt2) && pr.Name.IndexOf(filt2, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string v = "?";
                            try { v = pr.CanRead ? ("" + pr.GetValue(el, null)) : "(write-only)"; } catch { }
                            o.Append("prop ").Append(pr.Name).Append(" : ").Append(pr.PropertyType.Name)
                             .Append(" = ").Append(v.Length > 60 ? v.Substring(0, 60) : v)
                             .Append(pr.CanWrite ? "  [writable]" : "").Append(Environment.NewLine);
                        }
                        return o.ToString();
                    }));
                    foreach (string line in outQ.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                        step("m", true, line.Trim());
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Winmembers(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Reflect over the WINDOW's own type. The panel's speed is not the
                    // TextBlock that displays it - writing that text only makes the label
                    // agree with itself. What is needed is whatever the two arrows in
                    // `speedButtonsGrid` actually change.
                    string wT = ExtractJsonString(triggerJson, "window");
                    string filt = ExtractJsonString(triggerJson, "filter");
                    Window wm = FindWindowByTitle(string.IsNullOrWhiteSpace(wT) ? "Playback" : wT);
                    if (wm == null) { step("window", false, "not found: " + wT); sb.Append("]}"); return sb.ToString(); }
                    Type wt = wm.GetType();
                    step("type", true, wt.FullName);
                    string res = (string)wm.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        StringBuilder o = new StringBuilder();
                        foreach (PropertyInfo pi in wt.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (!string.IsNullOrWhiteSpace(filt) && pi.Name.IndexOf(filt, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string val = "?";
                            try { val = pi.CanRead ? ("" + pi.GetValue(wm, null)) : "(write-only)"; } catch (Exception ex) { val = ex.GetType().Name; }
                            o.Append("prop ").Append(pi.Name).Append(" : ").Append(pi.PropertyType.Name)
                             .Append(" = ").Append(val).Append(pi.CanWrite ? "  [writable]" : "").Append(Environment.NewLine);
                        }
                        foreach (FieldInfo fi in wt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            if (!string.IsNullOrWhiteSpace(filt) && fi.Name.IndexOf(filt, StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string val = "?";
                            try { val = "" + fi.GetValue(wm); } catch (Exception ex) { val = ex.GetType().Name; }
                            o.Append("field ").Append(fi.Name).Append(" : ").Append(fi.FieldType.Name)
                             .Append(" = ").Append(val).Append(Environment.NewLine);
                        }
                        return o.ToString();
                    }));
                    foreach (string line in res.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                        if (!string.IsNullOrWhiteSpace(line)) step("m", true, line.Trim());
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Setel(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Set one property on one named element, and read it back. Needed to
                    // test a control before wiring it into a chain - here the Playback
                    // progression slider, which an operator reports stops a run when dragged
                    // fully left. Testing beats assuming: the play BUTTON was assumed to
                    // stop the transport and does not.
                    string wT = ExtractJsonString(triggerJson, "window");
                    string eN = ExtractJsonString(triggerJson, "element");
                    string pN = ExtractJsonString(triggerJson, "property");
                    string vS = ExtractJsonString(triggerJson, "value");
                    Window ws = FindWindowByTitle(string.IsNullOrWhiteSpace(wT) ? "Playback" : wT);
                    if (ws == null) { step("window", false, "not found: " + wT); sb.Append("]}"); return sb.ToString(); }
                    string outS = (string)ws.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object el = FindElement(ws, eN);
                        if (el == null) return "element '" + eN + "' not found";
                        PropertyInfo pi = el.GetType().GetProperty(pN);
                        if (pi == null) return "no property '" + pN + "' on " + el.GetType().Name;
                        object before = null;
                        try { before = pi.GetValue(el, null); } catch { }
                        if (!pi.CanWrite) return "read-only, value=" + before;
                        try
                        {
                            // Nullable<T> needs the UNDERLYING type: ToggleButton.IsChecked is
                            // bool?, and Convert.ChangeType to bool? throws InvalidCastException
                            // (measured 2026-08-19 while trying to open the template slideout).
                            Type pt = pi.PropertyType;
                            Type under = Nullable.GetUnderlyingType(pt);
                            object conv = string.IsNullOrEmpty(vS) && under != null
                                          ? null
                                          : Convert.ChangeType(vS, under ?? pt, InvCi);
                            pi.SetValue(el, conv, null);
                        }
                        catch (Exception ex) { return "set threw " + ex.GetType().Name + ": " + ex.Message; }
                        object after = null;
                        try { after = pi.GetValue(el, null); } catch { }
                        return el.GetType().Name + "." + pN + ": " + before + " -> " + after;
                    }));
                    step("set " + eN + "." + pN, outS.IndexOf(" -> ", StringComparison.Ordinal) > 0, outS);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Hide(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    HideDialogs(step);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Unhide(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    UnhideDialogs(step);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Park(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Stopping the transport as its own step, so a caller can make sure
                    // nothing moves before it touches a dialog.
                    int tries = 6;
                    ParkTransport(step, tries);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Closedialogs(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    int closed = 0;
                    foreach (Window w in AllWindowsIncludingOwned())
                    {
                        string ty = null;
                        try { ty = w.GetType().FullName; } catch { }
                        if (ty == null || ty.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        Window ww = w;
                        try
                        {
                            ww.Dispatcher.Invoke(new Action(delegate { ww.Close(); }));
                            closed++;
                            step("closed", true, ty);
                        }
                        catch (Exception ex) { step("close failed", false, ty + ": " + ex.Message); }
                    }
                    step("dialogs closed", true, closed + " closed");
                    Thread.Sleep(800);
                    step("windows now", true, WindowTitles());
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Baseline(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Verify the starting state and, unless checkOnly, restore it.
                    // A run must begin from a state we established, not from whatever
                    // the last attempt left behind.
                    bool checkOnly = string.Equals(ExtractJsonString(triggerJson, "checkOnly"),
                                                   "true", StringComparison.OrdinalIgnoreCase);

                    // a) dialogs
                    List<Window> dlgs = new List<Window>();
                    foreach (Window w in AllWindowsIncludingOwned())
                    {
                        try { if (w.GetType().FullName.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0) dlgs.Add(w); }
                        catch { }
                    }
                    if (dlgs.Count == 0) step("no open dialog", true, "ok");
                    else if (checkOnly) step("no open dialog", false, dlgs.Count + " open");
                    else
                    {
                        foreach (Window w in dlgs)
                        {
                            Window ww = w;
                            try { ww.Dispatcher.Invoke(new Action(delegate { ww.Close(); })); } catch { }
                        }
                        Thread.Sleep(800);
                        int left = 0;
                        foreach (Window w in AllWindowsIncludingOwned())
                        {
                            try { if (w.GetType().FullName.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0) left++; }
                            catch { }
                        }
                        step("no open dialog", left == 0, "closed " + dlgs.Count + ", left " + left);
                    }

                    // b) strategy rows
                    Window cc3 = FindWindowByTitle("Control Center");
                    if (cc3 == null) step("strategy rows", false, "Control Center not found");
                    else
                    {
                        string r = (string)cc3.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            object grid = FindElement(cc3, "grdStrategies");
                            if (grid == null) return "grdStrategies not found";
                            PropertyInfo pSrc = grid.GetType().GetProperty("DataSource");
                            System.Collections.IEnumerable rows =
                                pSrc == null ? null : pSrc.GetValue(grid, null) as System.Collections.IEnumerable;
                            if (rows == null) return "no DataSource";
                            List<object> list = new List<object>();
                            foreach (object o in rows) if (o != null) list.Add(o);
                            if (list.Count == 0) return "none";
                            if (checkOnly) return list.Count + " row(s) present";
                            // One removal per dispatcher trip - waiting in here would block
                            // the thread that performs it.
                            int start = RowCount(grid);
                            foreach (var kv in GridRows(grid))
                                if (kv.Value != null) FireDisableRemove(cc3, kv.Value);
                            return "rows before=" + start;
                        }));
                        // Outside the dispatcher: wait, count, and repeat while rows remain.
                        int left2 = -1;
                        for (int round = 0; round < 20; round++)
                        {
                            Thread.Sleep(700);
                            Window ccx = cc3;
                            left2 = (int)ccx.Dispatcher.Invoke(new Func<int>(delegate
                            {
                                object g3 = FindElement(ccx, "grdStrategies");
                                return g3 == null ? -1 : RowCount(g3);
                            }));
                            if (left2 <= 0) break;
                            ccx.Dispatcher.Invoke(new Action(delegate
                            {
                                object g3 = FindElement(ccx, "grdStrategies");
                                if (g3 == null) return;
                                PropertyInfo ps3 = g3.GetType().GetProperty("DataSource");
                                System.Collections.IEnumerable rs =
                                    ps3 == null ? null : ps3.GetValue(g3, null) as System.Collections.IEnumerable;
                                if (rs == null) return;
                                foreach (var kv in GridRows(g3))
                                    if (kv.Value != null) FireDisableRemove(ccx, kv.Value);
                            }));
                        }
                        step("strategy rows", r == "none" || left2 == 0, r + "  rows now=" + left2);
                    }

                    // c) a running transport saturates the UI thread. Measured
                    // 2026-08-19: with the player at Max speed the strategy dialog
                    // disappeared between two bridge calls and "Enable" came back
                    // greyed. A moving clock is not a baseline.
                    if (checkOnly)
                    {
                        DateTime seenB;
                        bool movingB = TransportMoving(out seenB);
                        step("transport parked", !movingB,
                             movingB ? ("STILL RUNNING at " + seenB) : ("parked at " + seenB));
                    }
                    else ParkTransport(step, 6);

                    // d) connections. The Control Center can be found in any of three
                    //    states - Playback up, nothing connected, or connected to some
                    //    other provider - and each needs a different repair. `require`
                    //    says which one the caller needs: "playback" (default) or "none".
                    string require = ExtractJsonString(triggerJson, "require");
                    if (string.IsNullOrWhiteSpace(require)) require = "playback";
                    List<string> live = new List<string>();
                    foreach (Connection c in Connection.Connections)
                        if (c != null && c.Options != null && c.Options.Provider != Provider.Playback
                            && c.Status != ConnectionStatus.Disconnected)
                            live.Add(c.Options.Name + "=" + c.Status);
                    step("other connections", live.Count == 0,
                         live.Count == 0 ? "none" : string.Join(", ", live.ToArray()));

                    PropertyInfo piC2 = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                    Connection pc2 = piC2 != null ? piC2.GetValue(null) as Connection : null;
                    bool pbUp = pc2 != null && pc2.Status == ConnectionStatus.Connected;

                    if (!checkOnly && string.Equals(require, "playback", StringComparison.OrdinalIgnoreCase))
                    {
                        // Playback refuses to connect while anything else is up, and says
                        // so in a modal dialog that also blocks the dispatcher.
                        if (live.Count > 0)
                        {
                            foreach (Connection c in Connection.Connections)
                                if (c != null && c.Options != null && c.Options.Provider != Provider.Playback
                                    && c.Status != ConnectionStatus.Disconnected)
                                    try { c.Disconnect(); } catch { }
                            for (int i = 0; i < 60; i++)
                            {
                                Thread.Sleep(500);
                                bool any = false;
                                foreach (Connection c in Connection.Connections)
                                    if (c != null && c.Options != null && c.Options.Provider != Provider.Playback
                                        && c.Status != ConnectionStatus.Disconnected) any = true;
                                if (!any) break;
                            }
                            // Counted, not assumed: "requested" described the call.
                        int stillOn = 0;
                        foreach (Connection cx in Connection.Connections)
                            if (cx != null && cx.Status == ConnectionStatus.Connected) stillOn++;
                        step("other connections closed", true, stillOn + " still connected");
                        }
                        if (!pbUp)
                        {
                            try { NinjaTrader.Core.Globals.TradingOptions.IsGlobalSimulationMode = true; } catch { }
                            ConnectOptions po2 = null;
                            foreach (ConnectOptions o in NinjaTrader.Core.Globals.ConnectOptions)
                                if (o.Provider == Provider.Playback) { po2 = o; break; }
                            if (po2 == null) step("connect playback", false, "no Playback ConnectOptions");
                            else
                            {
                                Connection.Connect(po2);
                                string s3 = "?";
                                for (int i = 0; i < 600; i++)
                                {
                                    Thread.Sleep(500);
                                    Connection c = piC2 != null ? piC2.GetValue(null) as Connection : null;
                                    s3 = c == null ? "null" : c.Status.ToString();
                                    if (c != null && c.Status == ConnectionStatus.Connected) break;
                                }
                                pbUp = s3 == "Connected";
                                step("connect playback", pbUp, "Status=" + s3);
                            }
                        }
                    }
                    step("playback connected", pbUp,
                         pc2 == null ? "no connection object" : ("Status=" + (piC2.GetValue(null) as Connection == null
                             ? "null" : ((Connection)piC2.GetValue(null)).Status.ToString())));
                    PropertyInfo piNow3 = pb.GetProperty("NowEst", BFStatic);
                    if (piNow3 != null) step("NowEst", true, "" + piNow3.GetValue(null));

                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Dialogs(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // What is already open? Never guess the starting state.
                    step("windows", true, WindowTitles());
                    foreach (Window w in AllWindowsIncludingOwned())
                    {
                        string ti = null, ty = null;
                        try
                        {
                            Window ww = w;
                            ti = (string)ww.Dispatcher.Invoke(new Func<string>(delegate { return ww.Title; }));
                            ty = ww.GetType().FullName;
                        }
                        catch { }
                        if (ty != null && ty.IndexOf("Dialog", StringComparison.OrdinalIgnoreCase) >= 0)
                            step("open dialog", false, ty + "  title='" + ti + "'");
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Enablestrategy(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Calls NinjaTrader's own StrategyEnable / StrategyDisable statics - the
                    // calls behind the menu entries, no menu and no selection involved.
                    // Setting StrategiesGridEntry.IsEnabled would flip a display value
                    // without starting anything, which is why the property is not used.
                    string wantE = ExtractJsonString(triggerJson, "strategy");
                    // Direction. A teardown must be able to switch a strategy OFF; the
                    // stage used to be hardwired to "Enable".
                    string enableFlag = ExtractJsonString(triggerJson, "enable");
                    bool wantEnabled = string.IsNullOrWhiteSpace(enableFlag)
                                       || string.Equals(enableFlag, "true", StringComparison.OrdinalIgnoreCase);
                    string entryE = wantEnabled ? "Enable" : "Disable";
                    Window cce = FindWindowByTitle("Control Center");
                    if (cce == null) { step("Control Center", false, "not found"); sb.Append("]}"); return sb.ToString(); }
                    string resE = (string)cce.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object grid = FindElement(cce, "grdStrategies");
                        if (grid == null) return "grdStrategies not found";
                        PropertyInfo pSrc = grid.GetType().GetProperty("DataSource");
                        System.Collections.IEnumerable rows =
                            pSrc == null ? null : pSrc.GetValue(grid, null) as System.Collections.IEnumerable;
                        if (rows == null) return "no DataSource";
                        object hit = null;
                        foreach (object row in rows)
                        {
                            if (row == null) continue;
                            PropertyInfo pn = row.GetType().GetProperty("Name");
                            string nm = pn == null ? null : ("" + pn.GetValue(row, null)).Trim();
                            if (string.IsNullOrWhiteSpace(wantE) || (nm != null
                                && nm.IndexOf(wantE, StringComparison.OrdinalIgnoreCase) >= 0))
                            { hit = row; break; }
                        }
                        if (hit == null) return "no row matching '" + wantE + "'";

                        // Already there? NinjaTrader greys out the entry that would be a
                        // no-op, so invoking it would report a failure for exactly the
                        // state the caller asked for.
                        PropertyInfo piE = hit.GetType().GetProperty("IsEnabled");
                        if (piE != null)
                        {
                            object cur = null;
                            try { cur = piE.GetValue(hit, null); } catch { }
                            if (cur is bool && ((bool)cur) == wantEnabled)
                                return "already " + (wantEnabled ? "enabled" : "disabled");
                        }

                        // The call behind the menu entry, fired not awaited. Enable takes the
                        // row; Disable does not.
                        StrategyBase sHit = RowStrategy(hit);
                        if (sHit == null) return "row carries no StrategyBase";
                        MethodInfo mEn = wantEnabled ? StrategiesGridStatic("StrategyEnable", 3)
                                                     : StrategiesGridStatic("StrategyDisable", 1);
                        if (mEn == null) return (wantEnabled ? "StrategyEnable" : "StrategyDisable")
                                              + " not found on StrategiesGrid";
                        object[] argsEn = wantEnabled ? new object[] { sHit, cce, hit }
                                                      : new object[] { sHit };
                        MethodInfo mEn2 = mEn;
                        cce.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            try { mEn2.Invoke(null, argsEn); } catch { }
                        }));
                        return "fired " + mEn.Name;
                    }));
                    Thread.Sleep(2000);
                    string stateE = (string)cce.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object grid = FindElement(cce, "grdStrategies");
                        if (grid == null) return "grid gone";
                        PropertyInfo pSrc = grid.GetType().GetProperty("DataSource");
                        System.Collections.IEnumerable rows =
                            pSrc == null ? null : pSrc.GetValue(grid, null) as System.Collections.IEnumerable;
                        if (rows == null) return "no DataSource";
                        foreach (object row in rows)
                        {
                            if (row == null) continue;
                            PropertyInfo pe = row.GetType().GetProperty("IsEnabled");
                            PropertyInfo pn = row.GetType().GetProperty("Name");
                            return ("" + (pn == null ? "?" : pn.GetValue(row, null))).Trim()
                                   + " IsEnabled=" + (pe == null ? "?" : "" + pe.GetValue(row, null));
                        }
                        return "no rows";
                    }));
                    string wantMark = wantEnabled ? "IsEnabled=True" : "IsEnabled=False";
                    step(wantEnabled ? "enable" : "disable",
                         stateE.IndexOf(wantMark, StringComparison.OrdinalIgnoreCase) >= 0,
                         resE + "  ->  " + stateE);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Removestrategy(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Select by name, then let NinjaTrader remove the row. Removing it
                    // from the bound collection directly would leave the strategy on the
                    // account - the grid is a view, not the owner.
                    string want = ExtractJsonString(triggerJson, "strategy");
                    Window cc2 = FindWindowByTitle("Control Center");
                    if (cc2 == null) { step("Control Center", false, "not found"); sb.Append("]}"); return sb.ToString(); }
                    // Counted OUTSIDE the dispatcher, before anything is fired: the verdict
                    // below compares against this, so it rests on the row count and not on
                    // the fact that a call was made.
                    int before3 = (int)cc2.Dispatcher.Invoke(new Func<int>(delegate
                    {
                        object g0 = FindElement(cc2, "grdStrategies");
                        return g0 == null ? -1 : RowCount(g0);
                    }));
                    string res = (string)cc2.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        object grid = FindElement(cc2, "grdStrategies");
                        if (grid == null) return "grdStrategies not found";
                        PropertyInfo pSrc = grid.GetType().GetProperty("DataSource");
                        System.Collections.IEnumerable rows =
                            pSrc == null ? null : pSrc.GetValue(grid, null) as System.Collections.IEnumerable;
                        if (rows == null) return "grid has no DataSource";
                        object hit = null;
                        int seen = 0;
                        foreach (object row in rows)
                        {
                            seen++;
                            if (row == null) continue;
                            PropertyInfo pn = row.GetType().GetProperty("Name");
                            string nm = pn == null ? null : ("" + pn.GetValue(row, null)).Trim();
                            if (string.IsNullOrWhiteSpace(want) || (nm != null
                                && nm.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0))
                            { hit = row; break; }
                        }
                        if (hit == null) return "no row matching '" + want + "' among " + seen;
                        // NEVER wait inside the dispatcher: sleeping here blocks the very
                        // UI thread that has to carry out the removal, so the count can
                        // only ever come back unchanged. Measured 2026-08-18 - the row was
                        // in fact gone while this reported "rows 1 -> 1". Count before,
                        // fire, return; the caller waits and counts again outside.
                        //
                        // Disable FIRST. NinjaTrader only asks "are you sure" when the
                        // strategy is still enabled, and that modal blocks every following
                        // command. Disabling an already-disabled strategy is a no-op, so
                        // the order costs nothing and removes the dialog instead of
                        // answering it - answering would be a click.
                        int before2 = RowCount(grid);
                        StrategyBase sHit2 = RowStrategy(hit);
                        if (sHit2 == null) return "row carries no StrategyBase";
                        FireDisableRemove(cc2, sHit2);
                        return "fired StrategyDisable + StrategyRemove  rows before=" + before2;
                    }));
                    // Outside the dispatcher now: give NinjaTrader time to act, then
                    // count again in a fresh Invoke. The count is the evidence.
                    Thread.Sleep(1500);
                    int after3 = (int)cc2.Dispatcher.Invoke(new Func<int>(delegate
                    {
                        object g2 = FindElement(cc2, "grdStrategies");
                        return g2 == null ? -1 : RowCount(g2);
                    }));
                    // The EVIDENCE is the count dropping, never "the call returned".
                    step("remove", after3 >= 0 && after3 < before3,
                         res + "  rows " + before3 + " -> " + after3);
                    step("windows now", true, WindowTitles());
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Uiidle(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Wait until NinjaTrader has drained its UI queue - the handshake that
                    // was missing between every pair of stages. Both windows are asked,
                    // because they live on different UI threads: measured 2026-08-19,
                    // Application.Current.Windows does not even list all of them.
                    //
                    // `window` picks one by title; without it, Control Center and Playback
                    // are both checked. A window that does not exist is reported as such,
                    // never silently skipped.
                    string wantW = ExtractJsonString(triggerJson, "window");
                    string[] titles = string.IsNullOrWhiteSpace(wantW)
                        ? new string[] { "Control Center", "Playback" }
                        : new string[] { wantW };
                    bool allIdle = true;
                    foreach (string ti in titles)
                    {
                        Window wi = FindWindowByTitle(ti);
                        if (wi == null) { step("idle " + ti, true, "window not present"); continue; }
                        long msI; string stI;
                        bool idle = WaitForUiIdle(wi, RequestTtlSec(triggerJson), out msI, out stI);
                        if (!idle) allIdle = false;
                        step("idle " + ti, idle,
                             idle ? ("queue drained after " + msI + " ms")
                                  : ("STILL busy after " + msI + " ms - operation " + stI));
                    }
                    step("ui idle", allIdle,
                         allIdle ? "every window reported its queue drained"
                                 : "at least one window is still working - do not send the next step");
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Alloff(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // ⚠ THE BASELINE STEP MUST COPE WITH ANY STARTING SITUATION:
                    // nothing connected, one live feed, several at once. It turns
                    // everything off and proves it, by name.
                    //
                    // ⚠ MODE WARNING: Playback wants every other connection DOWN, the
                    // Strategy Analyzer wants a LIVE one UP and no Playback. Called
                    // bare, this stage serves the first and destroys the second. Pass
                    // `keep` with the connection names that must survive.
                    //
                    // Neither existing stage does that: `disconnect` touches only
                    // Connection.PlaybackConnection, and `connect` disconnects the others
                    // but then connects Playback. A baseline built from those two is a
                    // baseline nobody measured.
                    //
                    // It enumerates Connection.Connections - the LIVE list. The
                    // configuration is the wrong source and hid a running connection on
                    // 2026-08-20: NinjaTrader's menu showed "Live" connected while a
                    // report built from Globals.ConnectOptions listed six rows without
                    // it (see RunConnections in NT8BridgeServer.cs).
                    List<Connection> all = new List<Connection>();
                    try { foreach (Connection c in Connection.Connections) if (c != null) all.Add(c); }
                    catch (Exception ex) { step("enumerate", false, Deep(ex)); }

                    // ⚠ `keep` EXISTS BECAUSE THE OPERATING MODES NEED OPPOSITE THINGS.
                    //
                    // Playback needs every other connection DOWN. The Strategy Analyzer
                    // needs a LIVE connection up and must not have Playback at all - the
                    // wrong one gives 262,379 callbacks instead of 1,968,635, or every
                    // counter at zero, and the backtest still reports status: ok.
                    //
                    // Without this parameter the stage is a blunt instrument that any
                    // future caller could fire before an Analyzer run, killing the very
                    // connection that run depends on. `keep` names the connections that
                    // stay up, comma separated; they are reported, so what survived is a
                    // measurement and not an assumption. Empty = everything off.
                    string keepS = ExtractJsonString(triggerJson, "keep");
                    List<string> keepList = new List<string>();
                    if (!string.IsNullOrWhiteSpace(keepS))
                        foreach (string k in keepS.Split(','))
                            if (k.Trim().Length > 0) keepList.Add(k.Trim());

                    // 1. STATE BEFORE - names and status, so "it changed" is answerable
                    List<string> names = new List<string>();
                    List<Connection> open = new List<Connection>();
                    List<string> kept = new List<string>();
                    foreach (Connection c in all)
                    {
                        string nm = "?", st = "?";
                        try { nm = c.Options == null ? "<no options>" : c.Options.Name; } catch { }
                        try { st = c.Status.ToString(); } catch { }
                        names.Add(nm + "=" + st);
                        bool spare = false;
                        foreach (string k in keepList)
                            if (string.Equals(k, nm, StringComparison.OrdinalIgnoreCase)) spare = true;
                        if (spare) { kept.Add(nm + "=" + st); continue; }
                        if (st != ConnectionStatus.Disconnected.ToString()) open.Add(c);
                    }
                    step("before", true, all.Count + " connection(s): "
                         + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())));
                    if (keepList.Count > 0)
                        step("kept up on request", true,
                             kept.Count == 0
                             ? ("keep=" + string.Join(",", keepList.ToArray())
                                + " matched no connection - NOTHING was spared")
                             : string.Join(", ", kept.ToArray()));

                    if (open.Count == 0)
                    {
                        step("all disconnected", true, keepList.Count > 0
                             ? "nothing left to disconnect beside the kept one(s)"
                             : "nothing was connected - nothing to do");
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    // 2. TRIGGER on every one that is not already down
                    int markOff = NtLogCount();
                    List<string> hit = new List<string>();
                    foreach (Connection c in open)
                    {
                        string nm = "?";
                        try { nm = c.Options == null ? "<no options>" : c.Options.Name; } catch { }
                        hit.Add(nm);
                        try { c.Disconnect(); }
                        catch (Exception ex) { step("disconnect " + nm, false, Deep(ex)); }
                    }
                    step("triggered", true, "Disconnect() on " + string.Join(", ", hit.ToArray()));

                    // 3. WAIT FOR THE CHANGE - NinjaTrader's own entry, matched on the
                    //    resource NAME so it holds in any UI language. Proven to
                    //    discriminate on 2026-08-20: silent while nothing happened,
                    //    5 entries on connect, 2 on disconnect.
                    string hitLine; int nowIdx;
                    bool sawIt = WaitForLogName(markOff, "CbiConnectionProcessConnectionStatusUpdate",
                                                null, RequestTtlSec(triggerJson), out hitLine, out nowIdx);
                    step("change seen", sawIt, sawIt ? hitLine : "NinjaTrader logged no status change");

                    // 4. WAIT FOR THE END - every connection reports Disconnected. The
                    //    250 ms is the SAMPLING RATE of the measurement, never a
                    //    deadline: the only bound is the caller's own ttlSec.
                    System.Diagnostics.Stopwatch swOff = System.Diagnostics.Stopwatch.StartNew();
                    double ttlOff = RequestTtlSec(triggerJson);
                    List<string> left = new List<string>();
                    while (swOff.Elapsed.TotalSeconds < ttlOff)
                    {
                        left.Clear();
                        foreach (Connection c in open)
                        {
                            string st = "?", nm = "?";
                            try { nm = c.Options == null ? "<no options>" : c.Options.Name; } catch { }
                            try { st = c.Status.ToString(); } catch { }
                            if (st != ConnectionStatus.Disconnected.ToString()) left.Add(nm + "=" + st);
                        }
                        if (left.Count == 0) break;
                        Thread.Sleep(250);
                    }
                    step("all disconnected", left.Count == 0,
                         left.Count == 0
                         ? ("every connection reports Disconnected after "
                            + swOff.ElapsedMilliseconds + " ms: " + string.Join(", ", hit.ToArray()))
                         : ("STILL UP after " + swOff.ElapsedMilliseconds + " ms: "
                            + string.Join(", ", left.ToArray())));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Disconnect(PbRunCtx ctx)
        {
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Only disconnect - no reconnect. Recycling re-indexes the replay
                    // store and locks the whole panel again; sometimes all that is
                    // wanted is a transport that stops.
                    PropertyInfo piD = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                    Connection pd = piD != null ? piD.GetValue(null) as Connection : null;
                    if (pd == null || pd.Status == ConnectionStatus.Disconnected)
                        step("disconnect playback", true, "was not connected");
                    else
                    {
                        pd.Disconnect();
                        string sd = "?";
                        for (int i = 0; i < 120; i++)
                        {
                            Thread.Sleep(500);
                            Connection c = piD.GetValue(null) as Connection;
                            sd = c == null ? "null" : c.Status.ToString();
                            if (c == null || c.Status == ConnectionStatus.Disconnected) break;
                        }
                        step("disconnect playback", sd == "Disconnected" || sd == "null", "Status=" + sd);
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Recycle(PbRunCtx ctx)
        {
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Disconnect Playback and connect it again. A transport left at the
                    // end of a finished range keeps its clock there, and anything started
                    // afterwards has no data ahead of it. Recycling is what the
                    // Connections menu does by hand.
                    PropertyInfo piC = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                    Connection pc = piC != null ? piC.GetValue(null) as Connection : null;
                    if (pc != null && pc.Status != ConnectionStatus.Disconnected)
                    {
                        pc.Disconnect();
                        string s1 = "?";
                        for (int i = 0; i < 120; i++)
                        {
                            Thread.Sleep(500);
                            Connection c = piC.GetValue(null) as Connection;
                            s1 = c == null ? "null" : c.Status.ToString();
                            if (c == null || c.Status == ConnectionStatus.Disconnected) break;
                        }
                        step("disconnect playback", s1 == "Disconnected" || s1 == "null", "Status=" + s1);
                    }
                    else step("disconnect playback", true, "was not connected");

                    ConnectOptions po = null;
                    foreach (ConnectOptions o in NinjaTrader.Core.Globals.ConnectOptions)
                        if (o.Provider == Provider.Playback) { po = o; break; }
                    if (po == null) { step("connect playback", false, "no Playback ConnectOptions"); sb.Append("]}"); return sb.ToString(); }
                    Connection.Connect(po);
                    string s2 = "?";
                    for (int i = 0; i < 600; i++)
                    {
                        Thread.Sleep(500);
                        Connection c = piC != null ? piC.GetValue(null) as Connection : null;
                        s2 = c == null ? "null" : c.Status.ToString();
                        if (c != null && c.Status == ConnectionStatus.Connected) break;
                    }
                    step("connect playback", s2 == "Connected", "Status=" + s2);
                    PropertyInfo piNow2 = pb.GetProperty("NowEst", BFStatic);
                    if (piNow2 != null) step("NowEst", true, "" + piNow2.GetValue(null));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Connect(PbRunCtx ctx)
        {
            _orderNoticesDismissed = 0;     // a run starts with its connect - see DismissOrderRejectNotices
            string triggerJson = ctx.TriggerJson;
            string fromS = ctx.FromS;
            string toS = ctx.ToS;
            string srcS = ctx.SrcS;
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // One clock for the whole stage: every wait below draws from the
                    // caller's single ttlSec instead of each taking it in full.
                    System.Diagnostics.Stopwatch stageClock = System.Diagnostics.Stopwatch.StartNew();
                    // What the range was MEANT to be; confirmed once the connection is up.
                    DateTime? wantFrom = null, wantTo = null;
                    // The unpadded request - see the note where these are filled.
                    DateTime? reqFrom = null, reqTo = null;
                    ArmPanelWatch();   // before anything connects, so no event is missed
                    // Global simulation mode FIRST. Connecting Playback without it
                    // puts NinjaTrader on its restart-in-simulation path.
                    // Skippable so it can be tested as a single variable. It is the one
                    // thing this stage did on EVERY connect that a person connecting from
                    // the Playback dialog never does - and an operator reports their manual
                    // connect leaves the transport standing while ours starts by itself.
                    bool skipGlobalSim = string.Equals(ExtractJsonString(triggerJson, "skipGlobalSim"),
                                                       "true", StringComparison.OrdinalIgnoreCase);
                    if (skipGlobalSim) step("global simulation mode", true, "SKIPPED on request");
                    else
                    {
                        try
                        {
                            NinjaTrader.Core.Globals.TradingOptions.IsGlobalSimulationMode = true;
                            step("global simulation mode", true, "IsGlobalSimulationMode=true");
                        }
                        catch (Exception ex) { step("global simulation mode", false, ex.GetType().Name + ": " + ex.Message); }
                    }

                    // `statics:"false"` connects WITHOUT touching the adapter - the
                    // instrument for finding out which of these writes makes the
                    // transport start on its own. A human connecting from the GUI writes
                    // none of them, and an operator reports the transport then stays put.
                    // Each write is also individually skippable, so the culprit can be
                    // isolated by changing ONE thing against a working run.
                    bool writeStatics = !string.Equals(ExtractJsonString(triggerJson, "statics"),
                                                       "false", StringComparison.OrdinalIgnoreCase);
                    bool skipMode = string.Equals(ExtractJsonString(triggerJson, "skipFromMode"),
                                                  "true", StringComparison.OrdinalIgnoreCase);
                    bool skipSpeed = string.Equals(ExtractJsonString(triggerJson, "skipSpeed"),
                                                   "true", StringComparison.OrdinalIgnoreCase);
                    bool hist = string.Equals(srcS, "historical", StringComparison.OrdinalIgnoreCase);
                    if (!writeStatics) step("statics", true, "SKIPPED on request");
                    else
                    {
                        SetStatic(pb, "IsSourceHistoricalData", hist, step);
                        if (skipMode) step("PlaybackFromMode", true, "SKIPPED on request");
                        else SetStatic(pb, "PlaybackFromMode", Enum.Parse(typeof(PlaybackFromMode), "SelectedTime"), step);
                        // ⚠ WRITE NOW, VERIFY AFTER THE CONNECT.
                        //
                        // The adapter reads these while connecting, so they have to be
                        // written here. But its getter does not report the pending value
                        // yet: measured, both read straight back as DateTime.MinValue,
                        // and the run died on a read-back of a state that does not exist
                        // before the connection is up. Our own host writes the same
                        // statics and reads FromEst=10.08.2026 back AFTER connecting.
                        //
                        // So the confirming read is the step further down. Nothing is
                        // accepted silently - it is checked where a check can mean
                        // something.
                        // ⚠ TWO PAIRS, AND THEY ARE NOT INTERCHANGEABLE.
                        //
                        //   reqFrom/reqTo    the days the CALLER asked for
                        //   wantFrom/wantTo  those days padded by one on each side, which is
                        //                    what gets WRITTEN, so the clock can be parked
                        //                    before the first bar and run past the last one
                        //
                        // The coverage check further down must use the REQUESTED pair. It used
                        // the padded one until 25.08.2026, and then a request for exactly the
                        // days that exist could never pass it: measured coverage
                        // "FromEst=10.08.2026 00:00:00 ToEst=10.08.2026 23:59:59" against a
                        // --from/--to of 10.08.2026 reported "requested window inside it: False",
                        // because it was comparing 09.08. against 10.08. Two of three cells died
                        // on that, after connecting perfectly.
                        if (!string.IsNullOrWhiteSpace(fromS))
                        {
                            reqFrom = DateTime.Parse(fromS, InvCi).Date;
                            wantFrom = reqFrom.Value.AddDays(-1);
                            SetStaticQuiet(pb, "FromEst", wantFrom.Value, step);
                        }
                        if (!string.IsNullOrWhiteSpace(toS))
                        {
                            reqTo = DateTime.Parse(toS, InvCi).Date;
                            wantTo = reqTo.Value.AddDays(1);
                            SetStaticQuiet(pb, "ToEst", wantTo.Value, step);
                        }
                        // ⚠ PlaybackSpeed is NOT written here. Writing it BEFORE connecting
                        // starts the transport on its own - and it does not even take
                        // effect: the value is moved to `oldPlaybackSpeed` and the live
                        // field falls back to 1, which is why the panel then showed "1x"
                        // while the clock advanced by itself.
                        //
                        // Bisected 2026-08-19 against an operator watching the panel, one
                        // variable per run, disconnect + connect each time:
                        //
                        //   nothing written .................... stands
                        //   + IsSourceHistoricalData ........... stands
                        //   + FromEst / ToEst .................. stands
                        //   + PlaybackFromMode ................. stands
                        //   + PlaybackSpeed .................... RUNS
                        //
                        // It was written here for one afternoon on 2026-08-24, on the theory
                        // that our GUI-less host does it and connects. That theory did not
                        // hold - the connect kept failing with the write in place, and what
                        // actually fixed it was watching the Connection that Connect RETURNS
                        // instead of a static that stays null. So the write is gone again and
                        // the bisect above stands.
                        //
                        // The speed belongs after the connection is up, where stage
                        // "range" sets it together with Reset and reads it back as Max.
                        if (skipSpeed) step("PlaybackSpeed", true, "SKIPPED on request");
                        else step("PlaybackSpeed", true,
                                  "not set before connecting - that starts the transport; stage 'range' sets it");
                    }

                    // Playback refuses to connect while anything else is connected and
                    // says so in a MODAL dialog — which also blocks the dispatcher, so
                    // the bridge stops answering entirely. Disconnect first, and prove
                    // it by status rather than by the call returning.
                    try
                    {
                        List<Connection> others = new List<Connection>();
                        foreach (Connection c in Connection.Connections)
                            if (c != null && c.Options != null && c.Options.Provider != Provider.Playback
                                && c.Status != ConnectionStatus.Disconnected)
                                others.Add(c);
                        if (others.Count == 0) step("disconnect others", true, "nothing connected");
                        else
                        {
                            List<string> names = new List<string>();
                            foreach (Connection c in others) { names.Add(c.Options.Name); c.Disconnect(); }
                            bool allGone = false;
                            for (int i = 0; i < 60 && !allGone; i++)
                            {
                                Thread.Sleep(500);
                                allGone = true;
                                foreach (Connection c in others)
                                    if (c.Status != ConnectionStatus.Disconnected) allGone = false;
                            }
                            step("disconnect others", allGone,
                                 string.Join(", ", names.ToArray()) + " -> " + (allGone ? "Disconnected" : "still connected"));
                            if (!allGone) { sb.Append("]}"); return sb.ToString(); }
                        }
                    }
                    catch (Exception ex)
                    {
                        step("disconnect others", false, ex.GetType().Name + ": " + ex.Message);
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    ConnectOptions opts = null;
                    foreach (ConnectOptions o in NinjaTrader.Core.Globals.ConnectOptions)
                        if (o.Provider == Provider.Playback) { opts = o; break; }
                    if (opts == null) step("playback connection", false, "no Playback ConnectOptions configured");
                    else
                    {
                        PropertyInfo piConn = typeof(Connection).GetProperty("PlaybackConnection", BFStatic);
                        Connection existing = piConn != null ? piConn.GetValue(null) as Connection : null;
                        if (existing != null && existing.Status == ConnectionStatus.Connected)
                            step("playback connection", true, "already Connected");
                        else
                        {
                            // ⚠ WATCH THE CONNECTION Connect RETURNS.
                            //
                            // This polled the static Connection.PlaybackConnection and read
                            // "Status=null" for the whole budget, every single run - null is
                            // not a status, it is this code looking where the connection was
                            // never put. Our own host, which connects reliably on the same
                            // machine, watches the RETURN VALUE and nothing else.
                            Connection conn = Connection.Connect(opts);
                            if (conn == null)
                            {
                                step("playback connection", false,
                                     "Connection.Connect returned null");
                                sb.Append("]}");
                                return sb.ToString();
                            }
                            // First connect indexes every replay file of the instrument
                            // and can take minutes — measured on an MNQ ##-## store of
                            // 2,355 files. Poll rather than assume.
                            //
                            // ⚠ THE BOUND IS THE CALLER'S ttlSec, NOT A NUMBER FROM HERE.
                            // A fixed 300 s held this single-threaded poller for five
                            // minutes after a connect that had already panicked (measured
                            // 2026-08-24: panic at 15:01:15, queue free at 15:06:42), and
                            // four queued requests expired unexecuted meanwhile. The caller
                            // states what it is willing to wait; spending more of everyone
                            // else's time than that is not this stage's to decide.
                            string last = "?";
                            System.Diagnostics.Stopwatch swPc = System.Diagnostics.Stopwatch.StartNew();
                            // What is left of the request budget once the earlier steps
                            // have had their share. ONE request, ONE budget.
                            double ttlPc = RequestTtlSec(triggerJson) - stageClock.Elapsed.TotalSeconds;
                            if (ttlPc < 1.0) ttlPc = 1.0;
                            while (swPc.Elapsed.TotalSeconds < ttlPc)
                            {
                                Thread.Sleep(100);      // sampling rate from the reference host
                                last = conn.Status.ToString();
                                if (conn.Status == ConnectionStatus.Connected) break;
                                // A connect that FAILED reports its own end state; waiting out
                                // the rest of the budget on it only delays the report.
                                if (conn.Status == ConnectionStatus.Disconnected
                                    && swPc.Elapsed.TotalSeconds > 2.0) break;
                            }
                            // Connected is not usable: measured in a GUI-less host, every
                            // account carried Connection=null while the connection itself read
                            // Connected, and NinjaTrader then takes a strategy to Finalized
                            // instead of Realtime without a word. So the count is reported.
                            int acctsOn = 0;
                            try
                            {
                                foreach (Account a2 in Account.All)
                                    if (a2 != null && a2.Connection == conn) acctsOn++;
                            }
                            catch (Exception ex) { last += " (accounts: " + ex.GetType().Name + ")"; }
                            step("playback connection", last == "Connected",
                                 "Status=" + last + " after " + swPc.ElapsedMilliseconds
                                 + " ms (caller allowed " + ((int)ttlPc) + " s), accounts on it: "
                                 + acctsOn);

                            // ⚠ WHAT THE CONNECT LEFT IS THE STORE'S COVERAGE, NOT OUR RANGE.
                            //
                            // Measured right after Connected, having written 09.08.2026 and
                            // 11.08.2026:
                            //     FromEst read=12.03.2013   ToEst read=18.08.2026 23:59:59
                            // which is the span of everything in the replay store. Connecting
                            // replaces the requested window with the available one - our own
                            // host knows this and positions AGAIN afterwards, and stage
                            // `range` in this file carries the same note: "AFTER connect -
                            // before it the adapter discards these".
                            //
                            // So the COVERAGE is a measurement; the verdict below is narrow on
                            // purpose. It fails on one thing only: a requested day that is not
                            // in the store at all, which can never play. Everything else about
                            // the window is set and verified in stage `range`.
                            //
                            // The comment used to say "a MEASUREMENT, not a verdict" while the
                            // line below emitted a pass/fail step that the driver turns into a
                            // hard stop (25.08.2026). Code and prose now say the same thing.
                            if (last == "Connected")
                            {
                                // ⚠ RE-ASSERT THE SOURCE AFTER THE CONNECT.
                                //
                                // Connect overwrites the playback statics from the SAVED
                                // configuration - the GUI-less host in the sibling project
                                // states this in as many words and re-applies its statics
                                // after every connect for exactly that reason. The proof is
                                // one line below in this very stage: FromEst/ToEst read back
                                // as the replay store's whole span (12.03.2013..18.08.2026),
                                // not as the values written seconds earlier.
                                //
                                // Written BEFORE the connect it decides how NinjaTrader
                                // connects; written again AFTER it, it decides what the rest
                                // of the run - and the value NinjaTrader later persists -
                                // actually is. Both writes are needed, and neither replaces
                                // the other.
                                try
                                {
                                    object srcBefore = null, srcAfter = null;
                                    PropertyInfo piS = pb.GetProperty("IsSourceHistoricalData", BFStatic);
                                    if (piS != null)
                                    {
                                        srcBefore = piS.GetValue(null);
                                        piS.SetValue(null, hist, null);
                                        srcAfter = piS.GetValue(null);
                                    }
                                    step("source after connect",
                                         piS != null && srcAfter != null
                                             && srcAfter.Equals(hist),
                                         piS == null
                                             ? "IsSourceHistoricalData not reachable"
                                             : ("was " + srcBefore + " after Connect, re-applied "
                                                + hist + ", reads " + srcAfter));
                                }
                                catch (Exception ex)
                                {
                                    step("source after connect", false, ex.GetType().Name + ": " + ex.Message);
                                }

                                string covDetail = "";
                                foreach (string nm in new string[] { "FromEst", "ToEst" })
                                {
                                    object got = null;
                                    try
                                    {
                                        PropertyInfo pr = pb.GetProperty(nm, BFStatic);
                                        if (pr != null) got = pr.GetValue(null);
                                    }
                                    catch (Exception ex) { got = ex.GetType().Name; }
                                    covDetail += nm + "=" + got + "   ";
                                }
                                bool inside = true;
                                if (reqFrom.HasValue || reqTo.HasValue)
                                {
                                    try
                                    {
                                        // ⚠ THE STORE, NOT THE PANEL. FromEst/ToEst are the range the
                                        // Playback panel is SET TO, and Connect restores that from the
                                        // saved configuration - so right here they say what the last
                                        // session used, not what data exists.
                                        //
                                        // Measured 26.08.2026: the step failed with
                                        //     FromEst=11.08.2026 ToEst=18.08.2026
                                        //     requested 07.07.2026..14.07.2026 inside it: False
                                        // while `playback` scanning the SAME instrument's files
                                        // reported 2,523 of 2,523 readable, 17.12.2018..17.08.2026.
                                        // The day was in the store all along; the panel simply had
                                        // not been set yet, which stage 4 does a few steps later.
                                        //
                                        // A guard that reads a value the run is about to change
                                        // cannot answer the question it was written for - its own
                                        // comment says "not in the store at all".
                                        DateTime lo, hi;
                                        int nread;
                                        string covInstr = ExtractJsonString(triggerJson, "instrument");
                                        // ASK ABOUT THE STORE THIS RUN READS. Playback/Historical
                                        // is served from db\tick (NCD); only Market Replay reads
                                        // db\replay (NRD). Measured 30.08.2026: a Historical run
                                        // on MNQ 09-26 was refused here with "store scan
                                        // unavailable, panel range used / requested
                                        // 28.08.2026..28.08.2026 inside it: False", while that day
                                        // held 24 Ask / 24 Bid / 23 Last NCD files. It has no .nrd
                                        // at all - the recordings live under the continuous name
                                        // (MNQ ##-##) - so scanning db\replay could only ever
                                        // return nothing, and the fallback then judged the request
                                        // against the PREVIOUS run's panel range (27.08.).
                                        string covSrc = ExtractJsonString(triggerJson, "source");
                                        bool covFromNcd = string.Equals(
                                            (covSrc ?? "").Trim(), "historical",
                                            StringComparison.OrdinalIgnoreCase);
                                        bool covOk = covFromNcd
                                            ? TickCoverage(covInstr, out lo, out hi, out nread)
                                            : ReplayCoverage(covInstr, out lo, out hi, out nread);
                                        if (covOk)
                                            covDetail += "store " + nread
                                                       + (covFromNcd ? " NCD files " : " files readable ")
                                                       + lo.ToString("dd.MM.yyyy") + ".."
                                                       + hi.ToString("dd.MM.yyyy") + "   ";
                                        else
                                        {
                                            lo = (DateTime)pb.GetProperty("FromEst", BFStatic).GetValue(null);
                                            hi = (DateTime)pb.GetProperty("ToEst", BFStatic).GetValue(null);
                                            covDetail += "store scan unavailable, panel range used   ";
                                        }
                                        // ⚠ COMPARE WHOLE DAYS, and compare the REQUESTED window.
                                        // Coverage is reported as 00:00:00 on the first day and
                                        // 23:59:59 on the last, so a same-day request is inside
                                        // only when the comparison is done on .Date - otherwise
                                        // the end 23:59:59 vs a midnight request decides it.
                                        if (reqFrom.HasValue && reqFrom.Value.Date < lo.Date) inside = false;
                                        if (reqTo.HasValue && reqTo.Value.Date > hi.Date) inside = false;
                                        covDetail += "requested " + (reqFrom.HasValue ? reqFrom.Value.ToString("dd.MM.yyyy") : "-")
                                                   + ".." + (reqTo.HasValue ? reqTo.Value.ToString("dd.MM.yyyy") : "-")
                                                   + " inside it: " + inside;
                                    }
                                    catch (Exception ex) { covDetail += "range check: " + ex.GetType().Name; }
                                }
                                // FAILS only when the day we were asked for is not in the
                                // store at all - that cannot play, and finding out here beats
                                // finding out from an empty result.
                                step("coverage after connect", inside, covDetail);
                            }
                        }
                    }

                    // ⚠ TRIGGERING AND WAITING BELONG TOGETHER (established 2026-08-20).
                    //
                    // "Connected" is not "usable". NinjaTrader rebuilds the Playback panel after
                    // the connect, and anything written while that runs goes into a control it is
                    // about to discard. Measured 2026-08-19: about 11 s - but that is not a
                    // constant to rely on, the user says it can take minutes. So the step WAITS
                    // and REPORTS the duration it measured instead of assuming one. The bound
                    // below is a backstop against waiting forever, not a verdict: reaching it is
                    // reported as "still not usable", never quietly as success.
                    // ⚠ WAIT ON THE EVENT, NEVER ON A CLOCK (established 2026-08-20, shouted).
                    //
                    // Everything that used to stand here was a number I invented: 40 s for the
                    // panel, 1500 ms "stable", a 10-minute bound. The user is right that these
                    // are guesses about NinjaTrader's workload - loading a cold cache can take
                    // minutes - and a guess is either a false alarm or a stall.
                    //
                    // So the end of this action is marked by NinjaTrader, not by me:
                    //     Window.LoadedEvent (class handler)  the Playback window exists
                    //     slider.IsEnabledChanged             its controls became usable
                    // ArmPanelWatch() was called BEFORE connecting, so no event can slip through
                    // in between. The wait below has exactly one bound: the caller's own ttlSec,
                    // which is the caller's decision and not a constant in this file.
                    // ⚠ TRIGGER AND WAIT ARE ONE STEP - BUT A WALKTHROUGH MUST BE ABLE TO SPLIT
                    // THEM.
                    //
                    // In a run this stage triggers AND waits, because a step that returns before
                    // its effect exists is useless. When a human steps through it phase by phase
                    // (state before / trigger / change / end), the trigger has to come back at
                    // once so the change can be watched separately - `awaitPanel:"false"` does
                    // that. Then stage `panelflips` carries phases 3 and 4.
                    bool doWait = !string.Equals(ExtractJsonString(triggerJson, "awaitPanel"),
                                                 "false", StringComparison.OrdinalIgnoreCase);
                    if (!doWait)
                    {
                        step("panel usable", true,
                             "NOT waited on request (awaitPanel=false) - watch it with stage 'panelflips'");
                        sb.Append("]}");
                        return sb.ToString();
                    }
                    // ⚠ The window does not exist yet when the watch is armed - measured
                    // 2026-08-20, the trace read [noPlaybackWindow;] and the class handler
                    // never fired, so the wait sat out the full ttl twice while reading
                    // the same controls directly said enabled after 12236 ms.
                    //
                    // So discovery no longer depends on the event: it keeps LOOKING, on the
                    // path that demonstrably finds the window (stage `panelstate` uses the
                    // same one). The 250 ms is a SAMPLING RATE, not a deadline - how often
                    // it looks, never how long NinjaTrader may take. The only bound stays
                    // the caller's own ttlSec. Once hooked, the enable TRANSITION is still
                    // observed through the event, which is what an event is good for.
                    // ⚠ THE PANEL IS A GUI CONCEPT. Waiting for it in a host that has
                    // no windows would sit out the caller's whole ttl and then report
                    // failure for something that cannot happen here.
                    //
                    // The hazard this wait guards against is stated above: NinjaTrader
                    // rebuilds the Playback PANEL after connecting and writes land in a
                    // control it is about to discard. With no window nothing is rebuilt
                    // and nothing is discarded.
                    //
                    // The readiness that matters was already established WITHOUT the UI,
                    // by the step above: PlaybackConnection.Status polled until
                    // Connected. And stage `range` reads every value it writes back, so
                    // a write that was too early still cannot pass silently.
                    if (NoUiHost())
                    {
                        step("panel usable", true,
                             "no UI in this host - no panel is rebuilt, so no write can"
                             + " land in a discarded control   [" + _panelTrace + "]");
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    System.Diagnostics.Stopwatch swC = System.Diagnostics.Stopwatch.StartNew();
                    // The REMAINDER of the caller's budget. Taking a fresh ttlSec here
                    // made the stage cost twice what the caller allowed, and the answer
                    // then arrived after the caller had given up - measured as
                    // "(no answer) - stage returned nothing" on a stage that did answer.
                    double ttlC = RequestTtlSec(triggerJson) - stageClock.Elapsed.TotalSeconds;
                    bool usable = false;
                    if (ttlC <= 0.5)
                        step("panel usable", false,
                             "not waited - the request budget was already spent by the"
                             + " steps above   [" + _panelTrace + "]");
                    while (ttlC > 0.5 && swC.Elapsed.TotalSeconds < ttlC)
                    {
                        if (_panelTrace.IndexOf("hooked;") < 0) HookPlaybackSlider();
                        if (_panelUsable.Wait(TimeSpan.FromMilliseconds(250))) { usable = true; break; }
                    }
                    if (ttlC > 0.5)
                        step("panel usable", usable,
                             (usable ? "usable after " : "STILL not usable after ")
                             + swC.ElapsedMilliseconds + " ms of the "
                             + ((int)ttlC) + " s left   [" + _panelTrace + "]"
                             + (usable ? "" : "  - waited as long as this request allowed"));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Range(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string fromS = ctx.FromS;
            string toS = ctx.ToS;
            Type pb = ctx.Pb;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // AFTER connect — before it the adapter discards these.
                    if (!string.IsNullOrWhiteSpace(fromS))
                        SetStatic(pb, "FromEst", DateTime.Parse(fromS, InvCi), step);
                    if (!string.IsNullOrWhiteSpace(toS))
                        SetStatic(pb, "ToEst", DateTime.Parse(toS, InvCi).Date.AddDays(1).AddSeconds(-1), step);

                    // Speed on the FIELD as well: connecting moves the property value
                    // to `oldPlaybackSpeed` and leaves the live field at 1.
                    //
                    // `skipReset` exists to separate the two things this stage does.
                    // Writing PlaybackSpeed BEFORE connecting was bisected on 2026-08-19
                    // as the cause of the transport starting by itself; the same write
                    // happens here, after connecting, and the transport starts here too.
                    // Which of the two - the speed or the Reset - is the trigger is
                    // decided by a run with one of them switched off.
                    // ⚠ SPEED IS A START COMMAND.
                    //
                    // Bisected 2026-08-19, one variable per run, confirmed by the
                    // adapter clock, by the panel's progress slider and by an operator:
                    //
                    //   connect without PlaybackSpeed .......... stands
                    //   connect with    PlaybackSpeed .......... RUNS
                    //   range   without PlaybackSpeed .......... stands (also after 10 s)
                    //   range   with    PlaybackSpeed .......... RUNS
                    //
                    // Writing PlaybackAdapter.PlaybackSpeed starts the transport,
                    // whenever it happens. So the speed is not part of setting things
                    // up: stage "uiset" writes it together with pressing play, the one
                    // moment a run is meant to begin. `speed:"true"` restores the old
                    // behaviour for anyone who needs it.
                    bool rangeSkipSpeed = !string.Equals(ExtractJsonString(triggerJson, "speed"),
                                                         "true", StringComparison.OrdinalIgnoreCase);
                    bool rangeSkipReset = string.Equals(ExtractJsonString(triggerJson, "skipReset"),
                                                        "true", StringComparison.OrdinalIgnoreCase);
                    FieldInfo fiMax = pb.GetField("MaxSpeedValue", BFStatic);
                    object max = fiMax != null ? fiMax.GetValue(null) : (object)int.MaxValue;
                    if (rangeSkipSpeed) step("PlaybackSpeed", true, "SKIPPED on request");
                    else SetStatic(pb, "PlaybackSpeed", max, step);
                    if (rangeSkipSpeed) step("field playbackSpeed", true, "SKIPPED on request");
                    else
                    try
                    {
                        FieldInfo fiSpeed = pb.GetField("playbackSpeed", BFStatic);
                        if (fiSpeed == null) step("field playbackSpeed", false, "not found");
                        else
                        {
                            fiSpeed.SetValue(null, max);
                            step("field playbackSpeed", fiSpeed.GetValue(null).ToString() == max.ToString(),
                                 "wrote=" + max + " read=" + fiSpeed.GetValue(null));
                        }
                    }
                    catch (Exception ex) { step("field playbackSpeed", false, ex.GetType().Name + ": " + ex.Message); }

                    // Park the clock at the range start. After a completed range it
                    // sits at the END, and a strategy activated there has nothing
                    // ahead of it.
                    if (rangeSkipReset) step("Reset", true, "SKIPPED on request");
                    else if (!string.IsNullOrWhiteSpace(fromS))
                    {
                        try
                        {
                            MethodInfo reset = null;
                            foreach (MethodInfo mi in pb.GetMethods(BFStatic))
                                if (mi.Name == "Reset" && mi.GetParameters().Length == 2) { reset = mi; break; }
                            if (reset == null) step("Reset", false, "method not found");
                            else
                            {
                                DateTime wantClock = DateTime.Parse(fromS, InvCi);
                                // Phase 3: WAIT FOR THE CLOCK TO ARRIVE, do not sleep 3 s and
                                // hope. Reset is asynchronous and walks the clock toward the
                                // target; three seconds is a guess about how far it gets.
                                reset.Invoke(null, new object[] { wantClock, null });
                                PropertyInfo piNowW = pb.GetProperty("NowEst", BFStatic);
                                string beforeClock = piNowW == null ? "?" : "" + piNowW.GetValue(null);
                                string gotClock; long msClock;
                                WaitUntilChanged(delegate {
                                        return piNowW == null ? "?" : "" + piNowW.GetValue(null); },
                                    beforeClock, wantClock.ToString("dd.MM.yyyy HH:mm:ss"),
                                    RequestTtlSec(triggerJson), out gotClock, out msClock);
                                // The verdict is the CLOCK, not the call. "invoked" says only
                                // that nothing threw.
                                step("Reset", gotClock == wantClock.ToString("dd.MM.yyyy HH:mm:ss"),
                                     "clock " + beforeClock + " -> " + gotClock + " after " + msClock
                                     + " ms (wanted " + wantClock.ToString("dd.MM.yyyy HH:mm:ss") + ")");
                            }
                        }
                        catch (Exception ex) { step("Reset", false, ex.GetType().Name + ": " + ex.Message); }
                    }
                    PropertyInfo piNow = pb.GetProperty("NowEst", BFStatic);
                    if (piNow != null) step("NowEst", true, "" + piNow.GetValue(null));
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Stratdump(PbRunCtx ctx)
        {
            string triggerJson = ctx.TriggerJson;
            string typeName = ctx.TypeName;
            string instName = ctx.InstName;
            string fromS = ctx.FromS;
            string toS = ctx.ToS;
            string bpS = ctx.BpS;
            string trS = ctx.TrS;
            Dictionary<string, string> prms = ctx.Prms;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // Read-only twin of `attach`: builds the SAME object and stops before
                    // AddStrategyToGrid, so the object it would have added can be inspected
                    // without the run starting.
                    Instrument instD = Instrument.GetInstrument(instName);
                    if (instD == null) { step("instrument", false, instName + " not found"); sb.Append("]}"); return sb.ToString(); }
                    step("instrument", true, instD.FullName);

                    string whyD;
                    Type stD = ResolveStrategyType(typeName, out whyD);
                    if (stD == null) { step("strategy type", false, whyD); sb.Append("]}"); return sb.ToString(); }
                    step("strategy type", true, stD.FullName + (whyD == null ? "" : "   " + whyD));

                    string tmplD = ExtractJsonString(triggerJson, "template");
                    StrategyBase sd;
                    if (string.IsNullOrWhiteSpace(tmplD))
                    {
                        sd = (StrategyBase)Activator.CreateInstance(stD);
                        sd.SetState(State.SetDefaults);
                        step("template", true, "none - defaults");
                    }
                    else
                    {
                        StrategyBase probeD = (StrategyBase)Activator.CreateInstance(stD);
                        probeD.SetState(State.SetDefaults);
                        string fileD = TemplatePath(probeD, tmplD);
                        if (fileD == null) { step("template", false, "no folder for " + stD.Name); sb.Append("]}"); return sb.ToString(); }
                        string pD;
                        sd = RestoreTemplate(fileD, out pD);
                        if (sd == null) { step("template", false, fileD + ": " + pD); sb.Append("]}"); return sb.ToString(); }
                        step("template", true, Path.GetFileName(fileD) + " -> State " + sd.State);
                    }

                    // the same property writes as `attach`, without the account and
                    // without its trading-hours override
                    sd.Instrument = instD;
                    sd.InstrumentOrInstrumentList = instD.FullName;
                    if (!string.IsNullOrWhiteSpace(bpS))
                        sd.BarsPeriod = (BarsPeriod)ConvertConfigToken(bpS, typeof(BarsPeriod));
                    if (!string.IsNullOrWhiteSpace(fromS)) sd.From = DateTime.Parse(fromS, InvCi);
                    if (!string.IsNullOrWhiteSpace(toS))   sd.To = DateTime.Parse(toS, InvCi).Date.AddDays(1).AddSeconds(-1);
                    if (!string.IsNullOrWhiteSpace(trS))
                        sd.IsTickReplay = string.Equals(trS, "true", StringComparison.OrdinalIgnoreCase);
                    TradingHours thD = instD.MasterInstrument.TradingHours;
                    if (thD != null) { sd.TradingHoursInstance = thD; sd.TradingHoursSerializable = thD.Name; sd.TradingHours = thD; }
                    InjectParams(sd, prms);

                    step("state", true, "" + sd.State + "   (NOT added to the grid, NOT enabled)");

                    // ⚠ ASK NINJATRADER, do not guess. `IsStrategyConfigurationValid` is its
                    // OWN verdict on a set of strategies and it RETURNS A REASON - so the
                    // question "is this object fit to be armed" has an answer that costs
                    // nothing and arms nothing. It was never asked before; three times today
                    // the answer was found out the expensive way instead.
                    MethodInfo miValid = StrategiesGridStatic("IsStrategyConfigurationValid", 1);
                    if (miValid == null) step("IsStrategyConfigurationValid", false, "not found");
                    else
                    {
                        try
                        {
                            List<StrategyBase> one = new List<StrategyBase>();
                            one.Add(sd);
                            object verdict = miValid.Invoke(null, new object[] { one });
                            string vs = verdict == null ? null : verdict.ToString();
                            step("IsStrategyConfigurationValid", string.IsNullOrEmpty(vs),
                                 string.IsNullOrEmpty(vs) ? "valid (returned nothing)" : vs);
                        }
                        catch (Exception ex) { step("IsStrategyConfigurationValid", false, Deep(ex)); }
                    }

                    // Everything readable, so the diff against NinjaTrader's own object is
                    // complete rather than a selection of what seemed relevant.
                    int shown = 0;
                    foreach (PropertyInfo pd in sd.GetType().GetProperties(
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (pd.GetIndexParameters().Length > 0 || !pd.CanRead) continue;
                        object v;
                        try { v = pd.GetValue(sd, null); }
                        catch (Exception ex) { step("p " + pd.Name, true, "<" + ex.GetType().Name + ">"); continue; }
                        string s = v == null ? "null" : v.ToString();
                        if (s.Length > 90) s = s.Substring(0, 90) + "...";
                        step("p " + pd.Name, true, s);
                        shown++;
                    }
                    foreach (FieldInfo fd in sd.GetType().GetFields(
                                 BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        object v;
                        try { v = fd.GetValue(sd); }
                        catch (Exception ex) { step("f " + fd.Name, true, "<" + ex.GetType().Name + ">"); continue; }
                        string s = v == null ? "null" : v.ToString();
                        if (s.Length > 90) s = s.Substring(0, 90) + "...";
                        step("f " + fd.Name, true, s);
                        shown++;
                    }
                    step("members", true, "" + shown);
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Arm(PbRunCtx ctx)
        {
            string id = ctx.Id;
            string triggerJson = ctx.TriggerJson;
            string typeName = ctx.TypeName;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    // The SECOND HALF of `attach`, split off 2026-08-20. Measured that
                    // evening: the enable fired in the same breath as the load stuck
                    // inside StrategyEnable twice (runs 9/10, operation Executing, no
                    // 'Enabling' log line, Control Center frozen) - while the user
                    // enabling the SAME loaded row by hand worked (log 20:56:36).
                    // This stage arms an ALREADY LOADED row in its own request, so the
                    // gap between load and enable becomes the variable under test.
                    Window ccA = FindWindowByTitle("Control Center");
                    if (ccA == null)
                    { step("Control Center", false, "not found: " + WindowTitles()); sb.Append("]}"); return sb.ToString(); }

                    // Find the row by the TYPE NAME of the strategy it carries - the
                    // loading request's object is gone, so identity cannot anchor it.
                    object[] armBox = new object[3];   // [0]=StrategiesGridEntry [1]=StrategyBase [2]=DispatcherOperation
                    // NinjaTrader virtualizes a Control Center tab's content while the tab is
                    // inactive, so on a profile whose Strategies tab has never been shown the
                    // named element `grdStrategies` is simply absent (measured 2026-08-24 on a
                    // UI.xml NinjaTrader had just written itself). WithStrategiesGrid activates
                    // tabs until the grid materializes, hands it over, and restores the user's
                    // tab afterwards - so the lookup has to happen INSIDE its callback, not
                    // before it. It marshals onto the Control Center's dispatcher itself, which
                    // is why the Dispatcher.Invoke that used to wrap this block is gone.
                    List<string> rowNotes = new List<string>();
                    Progress(id, "-> locating the loaded strategy row (materializing the tab if need be)");
                    string rowFind = WithStrategiesGrid<string>(delegate(object realizedA)
                    {
                        // Prefer the inner NTGrid by name; fall back to what the helper realized.
                        object gridA = FindElement(ccA, "grdStrategies") ?? realizedA;
                        if (gridA == null) return "grdStrategies not in the visual tree";
                        // The rows live under a different member on the NTGrid than on the
                        // StrategiesGrid control, so ask for all of them - same set SnapshotGrid uses.
                        System.Collections.IEnumerable rowsA = FirstMember(gridA,
                            new[] { "DataSource", "source", "Entries", "ItemsSource" }) as System.Collections.IEnumerable;
                        if (rowsA == null) return "no row source on " + gridA.GetType().Name;
                        int nA = 0;
                        foreach (object row in rowsA)
                        {
                            if (row == null) continue;
                            nA++;
                            StrategyBase sA = RowStrategy(row);
                            if (sA != null && sA.GetType().Name == typeName)
                            { armBox[0] = row; armBox[1] = sA; }
                        }
                        return armBox[0] != null
                            ? "ok:row of " + typeName + " among " + nA + " row(s)"
                            : "no row carries a " + typeName + " (rows: " + nA + ")";
                    }, rowNotes);
                    // WithStrategiesGrid returns default(T) - null here - when it cannot reach
                    // the grid at all; its reason is in the notes.
                    if (rowFind == null)
                        rowFind = "grid not reachable" + (rowNotes.Count > 0 ? ": " + string.Join("; ", rowNotes.ToArray()) : "");
                    step("row found", rowFind.StartsWith("ok:"), rowFind);
                    if (!rowFind.StartsWith("ok:")) { sb.Append("]}"); return sb.ToString(); }
                    StrategyBase stratA = (StrategyBase)armBox[1];
                    step("state before enable", true, "" + stratA.State);

                    int markA = NtLogCount();
                    Progress(id, "-> posting StrategyEnable via Dispatcher.Invoke (UI thread)");
                    string armPost = (string)ccA.Dispatcher.Invoke(new Func<string>(delegate
                    {
                        MethodInfo enA = StrategiesGridStatic("StrategyEnable", 3);
                        if (enA == null) return "StrategyEnable(StrategyBase, Window, StrategiesGridEntry) not found";
                        System.Windows.Threading.DispatcherOperation opA =
                            ccA.Dispatcher.BeginInvoke(new Action(delegate
                            {
                                try { enA.Invoke(null, new object[] { stratA, ccA, armBox[0] }); }
                                catch (Exception exA) { LogSafe("arm: StrategyEnable threw " + Deep(exA)); }
                            }));
                        armBox[2] = opA;
                        return "ok:posted, op=" + opA.Status;
                    }));
                    step("StrategyEnable posted", armPost.StartsWith("ok:"), armPost);
                    if (!armPost.StartsWith("ok:")) { sb.Append("]}"); return sb.ToString(); }

                    Progress(id, "-> waiting for NinjaTrader's own 'Enabling' log line, budget "
                                 + RequestTtlSec(triggerJson).ToString("0") + "s (worker thread, "
                                 + "log-file channel - touches no dispatcher)");
                    string hitA; int idxA;
                    bool startedA = WaitForLogName(markA, "NinjaScriptStrategyBaseEnabling",
                                                   null, RequestTtlSec(triggerJson),
                                                   out hitA, out idxA);
                    System.Windows.Threading.DispatcherOperation opLateA =
                        armBox[2] as System.Windows.Threading.DispatcherOperation;
                    step("enable operation status", true,
                         opLateA == null ? "operation not captured"
                                         : opLateA.Status + " (read after the log wait)");
                    step("NinjaTrader started arming", startedA,
                         startedA ? hitA.Substring(0, Math.Min(96, hitA.Length))
                                  : "NinjaTrader never reported NinjaScriptStrategyBaseEnabling "
                                    + "within this request's budget - it did not begin");
                    sb.Append("]}");
                    return sb.ToString();
        }

        private string Stage_Attach(PbRunCtx ctx)
        {
            string id = ctx.Id;
            string triggerJson = ctx.TriggerJson;
            string typeName = ctx.TypeName;
            string instName = ctx.InstName;
            string fromS = ctx.FromS;
            string toS = ctx.ToS;
            string bpS = ctx.BpS;
            string trS = ctx.TrS;
            Dictionary<string, string> prms = ctx.Prms;
            StringBuilder sb = ctx.Sb;
            Action<string, bool, string> step = ctx.Step;

                    Instrument inst = Instrument.GetInstrument(instName);
                    if (inst == null) { step("instrument", false, instName + " not found"); sb.Append("]}"); return sb.ToString(); }
                    step("instrument", true, inst.FullName);

                    // One resolver for every stage: it prefers the FRESHEST loaded copy of the
                    // class (each `reload` leaves the previous NinjaTrader.Custom behind) and
                    // reports ambiguity instead of picking silently.
                    string whyT;
                    Type st = ResolveStrategyType(typeName, out whyT);
                    if (st == null) { step("strategy type", false, whyT); sb.Append("]}"); return sb.ToString(); }
                    step("strategy type", true, st.FullName + (whyT == null ? "" : "   " + whyT));

                    // With a template, the strategy IS the restored object - NinjaTrader
                    // built it from the XML exactly as the dialog's `load` button would.
                    // The request's own fields are applied afterwards and therefore win, so
                    // one template can serve a whole matrix of ranges and bar types.
                    string tmplName = ExtractJsonString(triggerJson, "template");
                    // ⚠ CREATE THE STRATEGY ON THE CONTROL CENTER'S DISPATCHER - NEVER
                    // ON THIS POLLER THREAD.
                    //
                    // Proven 2026-08-20 by a full-memory dump of the frozen
                    // process, read with dotnet-dump:
                    //   EmptyStrategy.<Dispatcher>k__BackingField == 0x1734c7b3278,
                    //   whose _dispatcherThread is managed id 83 = OS thread 0x9ec0,
                    //   an MTA THREADPOOL worker - the timer thread this poller ran
                    //   CreateInstance on. NinjaTrader's StrategyEnable internally does
                    //   strategy.Dispatcher.Invoke(StrategiesGrid+<>c__DisplayClass110_0)
                    //   - a thread-pool thread never pumps messages, the operation never
                    //   runs, the grid thread (0x4f44) waits forever and the Control
                    //   Center freezes. Same freeze whether OUR call or the user's own
                    //   checkbox fired the enable: the poisoned binding sits in the
                    //   OBJECT, not in the caller.
                    // Creating the instance on the Control Center window's dispatcher
                    // binds strategy.Dispatcher to the SAME thread StrategyEnable later
                    // runs on, so its inner Invoke executes directly - exactly what the
                    // GUI dialog path produces.
                    Window ccMake = FindWindowByTitle("Control Center");
                    if (ccMake == null)
                    { step("Control Center", false, "not found: " + WindowTitles()); sb.Append("]}"); return sb.ToString(); }
                    StrategyBase strat;
                    if (string.IsNullOrWhiteSpace(tmplName))
                    {
                        strat = (StrategyBase)ccMake.Dispatcher.Invoke(new Func<object>(delegate
                        {
                            StrategyBase s0 = (StrategyBase)Activator.CreateInstance(st);
                            s0.SetState(State.SetDefaults);
                            return s0;
                        }));
                        step("template", true, "none given - defaults   (created on the grid dispatcher, "
                             + "thread of " + ccMake.GetType().Name + ")");
                    }
                    else
                    {
                        // [0] = restored strategy or null, [1] = failure text
                        object[] mk = new object[2];
                        ccMake.Dispatcher.Invoke(new Action(delegate
                        {
                            StrategyBase probe2 = (StrategyBase)Activator.CreateInstance(st);
                            probe2.SetState(State.SetDefaults);
                            string tfile2i = TemplatePath(probe2, tmplName);
                            if (tfile2i == null) { mk[1] = "no template folder for " + st.Name; return; }
                            string why2i;
                            StrategyBase r2 = RestoreTemplate(tfile2i, out why2i);
                            mk[0] = r2;
                            mk[1] = r2 == null ? (tfile2i + ": " + why2i) : tfile2i;
                        }));
                        strat = mk[0] as StrategyBase;
                        string tfile2 = strat == null ? null : (string)mk[1];
                        if (strat == null)
                        { step("template", false, (string)mk[1]); sb.Append("]}"); return sb.ToString(); }
                        // Compare by NAME, not by Type identity: after a reload the same class
                        // is loaded twice (old + new NinjaTrader.Custom), and IsInstanceOfType
                        // then reports a mismatch between two types with the SAME FullName -
                        // measured 2026-08-19, it aborted this very run. The restored object is
                        // the authoritative one; NinjaTrader built it itself.
                        if (strat.GetType().FullName != st.FullName)
                        {
                            step("template", false, "template holds " + strat.GetType().FullName
                                 + " but " + st.FullName + " was requested");
                            sb.Append("]}");
                            return sb.ToString();
                        }
                        if (!ReferenceEquals(strat.GetType(), st))
                            step("assembly", true, "template instance comes from "
                                 + strat.GetType().Assembly.FullName
                                 + " - resolved type sits in " + st.Assembly.FullName);
                        step("template", true, Path.GetFileName(tfile2) + " -> State " + strat.State);
                    }
                    strat.Instrument = inst;
                    strat.InstrumentOrInstrumentList = inst.FullName;
                    if (!string.IsNullOrWhiteSpace(bpS))
                        strat.BarsPeriod = (BarsPeriod)ConvertConfigToken(bpS, typeof(BarsPeriod));
                    if (!string.IsNullOrWhiteSpace(fromS)) strat.From = DateTime.Parse(fromS, InvCi);
                    if (!string.IsNullOrWhiteSpace(toS))   strat.To = DateTime.Parse(toS, InvCi).Date.AddDays(1).AddSeconds(-1);
                    if (!string.IsNullOrWhiteSpace(trS))
                        strat.IsTickReplay = string.Equals(trS, "true", StringComparison.OrdinalIgnoreCase);

                    // ⚠ CHECK THE RANGE BEFORE ARMING - whether we set it or the template did.
                    // Whichever wrote it, this object is about to be handed to NinjaTrader,
                    // and an absurd range there is not a bad measurement, it is a dead process.
                    string rangeBad = RangeProblem(strat.From, strat.To);
                    step("range", rangeBad == null,
                         strat.From.ToString("yyyy-MM-dd") + " .. " + strat.To.ToString("yyyy-MM-dd")
                         + (rangeBad == null ? "" : "   REFUSED: " + rangeBad));
                    if (rangeBad != null) { sb.Append("]}"); return sb.ToString(); }

                    // Trading hours on all three members: NinjaScriptBase.Setup asserts
                    // on TradingHoursList[0] == null if any of them is missing.
                    // Optional override via the request's `tradingHours` field (e.g. a
                    // 24/7 template for coverage experiments). The name must match an
                    // installed template EXACTLY - on a miss the step fails and lists
                    // every available name, so the caller never has to guess one.
                    TradingHours th = inst.MasterInstrument.TradingHours;
                    string thWanted = ExtractJsonString(triggerJson, "tradingHours");
                    if (!string.IsNullOrWhiteSpace(thWanted))
                    {
                        TradingHours thHit = null;
                        var thNames = new StringBuilder();
                        foreach (TradingHours cand in TradingHours.All)
                        {
                            if (thNames.Length > 0) thNames.Append(" | ");
                            thNames.Append(cand.Name);
                            if (cand.Name == thWanted) thHit = cand;
                        }
                        if (thHit == null)
                        {
                            step("trading hours", false, "no template named '" + thWanted
                                 + "'. Installed: " + thNames);
                            sb.Append("]}"); return sb.ToString();
                        }
                        th = thHit;
                    }
                    if (th == null) { step("trading hours", false, "MasterInstrument has none"); sb.Append("]}"); return sb.ToString(); }
                    strat.TradingHoursInstance = th;
                    strat.TradingHoursSerializable = th.Name;
                    strat.TradingHours = th;
                    step("trading hours", true, th.Name);

                    InjectParams(strat, prms);

                    // Which playback account this strategy trades on. Empty falls
                    // back to the one NinjaTrader names itself
                    // (Account.PlaybackAccountName); a name that does not exist
                    // stops the run rather than being swapped for another.
                    string accName = ExtractJsonString(triggerJson, "account");
                    if (string.IsNullOrWhiteSpace(accName)) accName = Account.PlaybackAccountName;
                    Account acc = null;
                    foreach (Account a in Account.All)
                        if (string.Equals(a.Name, accName, StringComparison.OrdinalIgnoreCase)) { acc = a; break; }
                    if (acc == null)
                    {
                        List<string> names = new List<string>();
                        foreach (Account a in Account.All) names.Add(a.Name);
                        step("account", false, accName + " missing; present: " + string.Join(", ", names.ToArray()));
                        sb.Append("]}");
                        return sb.ToString();
                    }
                    strat.Account = acc;
                    step("account chosen", true, acc.Name
                         + (string.IsNullOrWhiteSpace(ExtractJsonString(triggerJson, "account"))
                            ? "  (default)" : "  (requested)"));

                    // ⚠ THE COMPARISON VALUE FOR THE HEADLESS REFUSAL (2026-08-22).
                    //
                    // In the headless host NinjaTrader accepts StrategyEnable and then
                    // takes the strategy to Finalized instead of Realtime, writing no
                    // reason to its log. The forensics dump there showed
                    // `account.Connection = null`. This step prints the same field from
                    // the GUI process, where the enable is known to succeed - so the
                    // two can be compared instead of guessed about.
                    try
                    {
                        step("account.Connection", acc.Connection != null,
                             acc.Connection == null ? "null"
                             : (acc.Connection.Options == null ? "?" : acc.Connection.Options.Name)
                               + " / " + acc.Connection.Status);
                    }
                    catch (Exception exCon) { step("account.Connection", false, Deep(exCon)); }

                    // ⚠ ASK NINJATRADER BEFORE ARMING. `IsStrategyConfigurationValid` is its
                    // OWN verdict and it returns a REASON. It costs nothing, arms nothing, and
                    // it is the check that was never made while the enable froze the process
                    // three times on 2026-08-19.
                    MethodInfo miOk = StrategiesGridStatic("IsStrategyConfigurationValid", 1);
                    if (miOk == null) step("configuration valid", true, "check not available on this build");
                    else
                    {
                        string verdictS;
                        try
                        {
                            List<StrategyBase> oneS = new List<StrategyBase>();
                            oneS.Add(strat);
                            object vv = miOk.Invoke(null, new object[] { oneS });
                            verdictS = vv == null ? null : vv.ToString();
                        }
                        catch (Exception ex) { verdictS = "check threw " + Deep(ex); }
                        bool okS = string.IsNullOrEmpty(verdictS);
                        step("configuration valid", okS, okS ? "NinjaTrader raised no objection" : verdictS);
                        if (!okS) { sb.Append("]}"); return sb.ToString(); }
                    }

                    // `via` decides who drives. Default is the grid, because that is what the
                    // Control Center itself does; the account variant is kept so the two can
                    // be compared rather than believed.
                    string via = ExtractJsonString(triggerJson, "via");
                    if (string.IsNullOrWhiteSpace(via)) via = "grid";

                    if (string.Equals(via, "grid", StringComparison.OrdinalIgnoreCase))
                    {
                        // [0] = StrategiesGrid (owns AddStrategyToGrid), [1] = inner NTGrid
                        // (owns DataSource, where the rows actually live - the same place the
                        // teardown counts them).
                        object[] gridCtl = new object[2];
                        Window cc2 = FindWindowByTitle("Control Center");
                        if (cc2 == null)
                        { step("Control Center", false, "not found: " + WindowTitles()); sb.Append("]}"); return sb.ToString(); }

                        // "->" lines mark what is ABOUT TO run on the dispatcher. If the UI
                        // thread never comes back, the progress file ends on the marker that
                        // names the exact call it died in - the step() line never happens.
                        // Same virtualization as in `restore`: while the Strategies tab has never
                        // been shown, `grdStrategies` is not in the tree and this step died with
                        // "not in the visual tree" (measured 2026-08-24, GUI Historical run).
                        // WithStrategiesGrid activates tabs until the grid exists and restores the
                        // user's tab afterwards, so the work happens inside its callback. It
                        // marshals onto the Control Center's dispatcher itself - hence no
                        // Dispatcher.Invoke around this any more.
                        List<string> addNotes = new List<string>();
                        Progress(id, "-> AddStrategyToGrid (materializing the tab if need be, UI thread)");
                        string outcome2 = WithStrategiesGrid<string>(delegate(object realized2)
                        {
                            // `grdStrategies` is the inner NTGrid; AddStrategyToGrid sits on the
                            // StrategiesGrid CONTROL that contains it. Walk up until the type
                            // that owns the method appears, instead of assuming the named
                            // element is the right object (measured: it is an NTGrid).
                            // The helper already hands over a StrategiesGrid, so it is also the
                            // fallback when the inner element cannot be resolved by name.
                            object grid2 = FindElement(cc2, "grdStrategies") ?? realized2;
                            if (grid2 == null) return "grdStrategies not in the visual tree";
                            gridCtl[1] = grid2;
                            System.Windows.DependencyObject up = grid2 as System.Windows.DependencyObject;
                            List<string> climbed = new List<string>();
                            while (up != null)
                            {
                                if (up.GetType().Name == "StrategiesGrid") { grid2 = up; break; }
                                climbed.Add(up.GetType().Name);
                                System.Windows.DependencyObject nx = null;
                                try { nx = System.Windows.Media.VisualTreeHelper.GetParent(up); } catch { }
                                if (nx == null)
                                {
                                    System.Windows.FrameworkElement fe = up as System.Windows.FrameworkElement;
                                    nx = fe == null ? null : fe.Parent as System.Windows.DependencyObject;
                                }
                                up = nx;
                            }
                            if (grid2.GetType().Name != "StrategiesGrid")
                                return "no StrategiesGrid above grdStrategies; climbed: "
                                       + string.Join(" > ", climbed.ToArray());
                            MethodInfo add2 = null;
                            foreach (MethodInfo mi2 in grid2.GetType().GetMethods(
                                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                            {
                                if (mi2.Name != "AddStrategyToGrid") continue;
                                ParameterInfo[] ps2 = mi2.GetParameters();
                                if (ps2.Length == 1 && ps2[0].ParameterType.IsAssignableFrom(typeof(StrategyBase)))
                                { add2 = mi2; break; }
                            }
                            gridCtl[0] = grid2;   // the enable step looks its row up in this control

                            // ⚠ AddStrategyToGrid, NOT StrategyAdd - and the reason is evidence,
                            // not taste. Both runs that completed (2026-08-19, 21:49 and 22:37)
                            // used AddStrategyToGrid and fired the enable while the strategy was
                            // still at State.SetDefaults, and the enable worked. Switching to
                            // StrategyAdd advanced it to Configure - which LOOKS better and was
                            // never shown to work. The working path is the one with the track
                            // record; the other one is a hypothesis.
                            if (add2 == null) return "AddStrategyToGrid(StrategyBase) not found";
                            try { add2.Invoke(grid2, new object[] { strat }); }
                            catch (Exception ex2) { return "AddStrategyToGrid threw " + Deep(ex2); }
                            return "ok:AddStrategyToGrid";
                        }, addNotes);
                        // null means the helper never reached a grid; its reason is in the notes.
                        if (outcome2 == null)
                            outcome2 = "grid not reachable" + (addNotes.Count > 0 ? ": " + string.Join("; ", addNotes.ToArray()) : "");
                        step("AddStrategyToGrid", outcome2 != null && outcome2.StartsWith("ok:"),
                             outcome2 + "   -> state " + strat.State);
                        if (outcome2 == null || !outcome2.StartsWith("ok:")) { sb.Append("]}"); return sb.ToString(); }

                        // Adding is not arming: measured, the strategy sat in SetDefaults after
                        // the row appeared. `StrategyEnable` is the static behind the Enable menu
                        // item - the call itself, not the click.
                        // ⚠ THE ROW HAS TO EXIST BEFORE ANYTHING IS ARMED.
                        // Without this the enable was fired at a strategy the Strategies window
                        // did not show, and waited forever. A missing row is a FINDING, not a
                        // reason to carry on.
                        // Phase 3 for the add: wait until the collection actually GAINS the row.
                        // The previous version ran 20 rounds of 250 ms - a five-second deadline
                        // dressed up as a loop. How long NinjaTrader needs is its business; the
                        // only bound is this request's own budget.
                        string rowsGot; long rowsMs;
                        Progress(id, "-> waiting for the grid row to appear (polls the UI thread)");
                        WaitUntilChanged(delegate
                        {
                            return "" + (int)cc2.Dispatcher.Invoke(new Func<int>(delegate
                            {
                                object g5 = gridCtl[1];
                                if (g5 == null) return 0;
                                PropertyInfo ps5 = g5.GetType().GetProperty("DataSource");
                                System.Collections.IEnumerable rs5 = ps5 == null ? null
                                    : ps5.GetValue(g5, null) as System.Collections.IEnumerable;
                                if (rs5 == null) return 0;
                                int n5 = 0;
                                foreach (object o5 in rs5) if (o5 != null) n5++;
                                return n5;
                            }));
                        }, "0", null, RequestTtlSec(triggerJson), out rowsGot, out rowsMs);
                        step("row appeared", rowsGot != "0",
                             "rows 0 -> " + rowsGot + " after " + rowsMs + " ms");

                        // ⚠ IS THE STRATEGY ACTUALLY LOADED? Three checks, none of which
                        // reads our own side of the fence. Counting rows in the bound
                        // collection was exactly that mistake: it reported "1 row(s)" while
                        // the Strategies window showed none (2026-08-19).
                        //
                        // 1) the ACCOUNT holds it - the domain truth, not a view
                        bool inAccount = false;
                        foreach (StrategyBase sx in acc.Strategies)
                            if (ReferenceEquals(sx, strat)) { inAccount = true; break; }
                        // Reported, not gated: neither completed run was ever checked this way,
                        // so making it a hard gate would risk blocking a path that works for a
                        // reason I have not measured.
                        step("strategy on the account", true,
                             inAccount ? (acc.Name + " holds it, " + acc.Strategies.Count + " total")
                                       : (acc.Name + " does NOT hold it"));

                        // The STATE is reported, never gated on. A gate at DataLoaded would
                        // have blocked both runs that completed on 2026-08-19: they fired the
                        // enable from State.SetDefaults and it worked. Whatever loads the series
                        // happens inside the enable, not before it - so a state below DataLoaded
                        // here is information, not a fault.
                        step("state before enable", true, "" + strat.State);

                        // 3) and only now the row, as a secondary sign
                        Progress(id, "-> reading the row count via Dispatcher.Invoke (UI thread)");
                        int rowsNow = (int)cc2.Dispatcher.Invoke(new Func<int>(delegate
                        {
                            object g7 = gridCtl[1];
                            return g7 == null ? -1 : RowCount(g7);
                        }));
                        step("row in the grid", true, rowsNow + " row(s)   (secondary - the account "
                             + "and the state are the evidence)");

                        // enable=false: load the strategy row and STOP - do not arm it.
                        // Built 2026-08-20 after runs 9 and 10 both stuck inside NinjaTrader's
                        // StrategyEnable (operation dequeued, 'Enabling' log line never written).
                        // Loading up to the grid row worked in EVERY run today; this switch cuts
                        // the chain exactly at the measured boundary so the loaded-but-disarmed
                        // state can be inspected.
                        string enableS = ExtractJsonString(triggerJson, "enable");
                        if (string.Equals(enableS, "false", StringComparison.OrdinalIgnoreCase))
                        {
                            step("enable", true, "SKIPPED on request (enable=false) - the strategy "
                                 + "row is loaded and stays disarmed, state " + strat.State);
                            sb.Append("]}");
                            return sb.ToString();
                        }

                        // Phase 1 for the enable: where NinjaTrader's log stands BEFORE it is
                        // triggered, so phase 3 can tell a new entry from an old one.
                        int markEnable = NtLogCount();
                        string[] enableStatus = new string[] { "?" };
                        // The DispatcherOperation is created inside the delegate below (on the
                        // UI thread); this box carries it out so its status can be read AGAIN
                        // after the log wait - see "enable operation status" further down.
                        object[] opBox = new object[1];
                        Progress(id, "-> posting StrategyEnable via Dispatcher.Invoke (UI thread)");
                        string outcome3 = (string)cc2.Dispatcher.Invoke(new Func<string>(delegate
                        {
                            Type sgT = null;
                            foreach (Assembly asm3 in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try { sgT = asm3.GetType("NinjaTrader.Gui.NinjaScript.StrategiesGrid", false); }
                                catch { }
                                if (sgT != null) break;
                            }
                            if (sgT == null) return "StrategiesGrid type not found";
                            MethodInfo en = null;
                            foreach (MethodInfo mi3 in sgT.GetMethods(BFStatic))
                                if (mi3.Name == "StrategyEnable" && mi3.GetParameters().Length == 3) { en = mi3; break; }
                            if (en == null) return "StrategyEnable(StrategyBase, Window, StrategiesGridEntry) not found";

                            // The row object the GUI would hand in. Found by identity against
                            // OUR strategy, so it can never be somebody else's row.
                            object sge = null;
                            string rowNote = "";
                            PropertyInfo pSrc4 = gridCtl[1] == null ? null
                                : gridCtl[1].GetType().GetProperty("DataSource");
                            System.Collections.IEnumerable rows4 = pSrc4 == null ? null
                                : pSrc4.GetValue(gridCtl[1], null) as System.Collections.IEnumerable;
                            if (rows4 == null) return "grdStrategies has no DataSource";
                            int nrows = 0;
                            foreach (object row in rows4)
                            {
                                if (row == null) continue;
                                nrows++;
                                foreach (PropertyInfo pr4 in row.GetType().GetProperties(
                                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                {
                                    if (pr4.GetIndexParameters().Length > 0) continue;
                                    object v4 = null;
                                    try { v4 = pr4.GetValue(row, null); } catch { continue; }
                                    if (ReferenceEquals(v4, strat)) { sge = row; rowNote = row.GetType().Name + "." + pr4.Name; break; }
                                }
                                if (sge != null) break;
                            }
                            if (sge == null)
                                return "none of the " + nrows + " row(s) in DataSource holds this strategy";
                            // ⚠ FIRE, THEN READ WHAT BECAME OF IT - WITHOUT WAITING.
                            //
                            // ⚠ THIS CODE RUNS ON THE UI THREAD. Everything from the enclosing
                            // cc2.Dispatcher.Invoke onwards executes on the dispatcher, so any
                            // wait here is the UI thread waiting for itself. The earlier
                            // version of this comment claimed the opposite and sent the search
                            // for the freeze somewhere else for hours.
                            //
                            // BeginInvoke returns a DispatcherOperation. Its status is read as
                            // it stands, never waited on, and it still separates three cases
                            // that looked identical all evening:
                            //
                            //   Completed  the enable ran and finished
                            //   Executing  it started and is stuck inside
                            //   Pending    it NEVER STARTED - the UI thread never picked it up
                            //
                            // Until now all three produced the same picture: no log line, no
                            // error, an empty Strategies list, and a NinjaTrader that had to be
                            // killed. Whatever comes back here, it is a finding instead of a
                            // freeze.
                            System.Windows.Threading.DispatcherOperation opEn =
                                cc2.Dispatcher.BeginInvoke(new Action(delegate
                                {
                                    try { en.Invoke(null, new object[] { strat, cc2, sge }); }
                                    catch (Exception exq) { LogSafe("StrategyEnable threw " + Deep(exq)); }
                                }));
                            opBox[0] = opEn;   // carried out for the late status re-read
                            // ⚠ DO NOT WAIT HERE. THIS CODE RUNS ON THE UI THREAD.
                            //
                            // Everything from the enclosing cc2.Dispatcher.Invoke onwards
                            // executes ON the dispatcher. Waiting here for an operation that
                            // only the dispatcher can run means the UI thread waits for
                            // itself: DispatcherOperation.Wait then pumps a nested frame, and
                            // whether that ever returns depends on what the enable does.
                            //
                            // Measured 2026-08-20: with a template it came back in 1.9 s
                            // (op=Completed); with an EMPTY strategy and no template it did
                            // not come back at all - NinjaTrader stopped answering the bridge
                            // AND the user could no longer operate the Playback or Control
                            // Center windows. The comment that used to sit above this line
                            // asserted the wait stayed clear of the UI thread; the nesting
                            // says otherwise, and the nesting is the code.
                            //
                            // The 25 s were wrong twice over: a guessed deadline, and an
                            // unnecessary one. The PROOF that arming started comes right
                            // after this, on the WORKER thread, from NinjaTrader's own log
                            // entry (NinjaScriptStrategyBaseEnabling) - a handshake, bounded
                            // only by the caller's ttlSec.
                            //
                            // The status is still read, because Pending / Executing /
                            // Completed separates "never picked up" from "started" - but it
                            // is read as it stands RIGHT NOW, without waiting for it.
                            enableStatus[0] = opEn.Status.ToString() + " (read, not waited on)";
                            return "ok:" + rowNote + "  op=" + enableStatus[0];
                        }));
                        // ── Phase 3: DID NINJATRADER REACT AT ALL? ──────────────────────────
                        //
                        // The enable is posted asynchronously, so "the call returned" says
                        // nothing. NinjaTrader writes its own entry when it starts arming, and
                        // the resource key for it is MEASURED, not guessed (stage `resname`
                        // over NinjaTrader.Resource):
                        //     NinjaScriptStrategyBaseEnabling1 / ...Enabling2
                        // Waiting for that entry is the difference between "it has begun" and
                        // "nothing happened yet" - the distinction that was missing every time
                        // this step froze NinjaTrader.
                        string hitEn; int idxEn;
                        Progress(id, "-> enable posted (op=" + enableStatus[0].Split(' ')[0]
                                     + "); waiting for NinjaTrader's own 'Enabling' log line, budget "
                                     + RequestTtlSec(triggerJson).ToString("0") + "s (worker thread, "
                                     + "log-file channel - touches no dispatcher)");
                        bool started = WaitForLogName(markEnable, "NinjaScriptStrategyBaseEnabling",
                                                      null, RequestTtlSec(triggerJson),
                                                      out hitEn, out idxEn);
                        step("NinjaTrader started arming", started,
                             started ? hitEn.Substring(0, Math.Min(96, hitEn.Length))
                                     : "NinjaTrader never reported NinjaScriptStrategyBaseEnabling "
                                       + "within this request's budget - it did not begin");

                        // ⚠ THE DISPATCHER STATUS IS NOT THE VERDICT ANY MORE.
                        //
                        // It used to demand Completed - which was reachable only because the
                        // code waited for it, on the UI thread, and that wait is what froze
                        // NinjaTrader. Since the wait is gone, the status is read microseconds
                        // after BeginInvoke, so Pending is the NORMAL reading, not a failure.
                        // Measured 2026-08-20 with the empty strategy: op=Pending while
                        // NinjaTrader had ALREADY written NinjaScriptStrategyBaseEnabling1 -
                        // the two say different things, and only the second one is evidence.
                        //
                        // So the verdict rests on what NinjaTrader itself reported (`started`)
                        // plus the call having been posted at all. The status stays in the
                        // text, because Pending / Executing / Completed still tells a reader
                        // how far the operation had got when it was looked at.
                        // Re-read the operation status AFTER the log wait. At post time
                        // Pending is the normal reading; HERE it separates two different
                        // freezes: Pending after the full budget = the dispatcher never
                        // dequeued the operation; Executing = StrategyEnable started and is
                        // stuck inside it. Run A 2026-08-20: the account reset ran and no
                        // 'Enabling' line followed - only this late read can say which of
                        // the two that was.
                        System.Windows.Threading.DispatcherOperation opLate =
                            opBox[0] as System.Windows.Threading.DispatcherOperation;
                        step("enable operation status", true,
                             opLate == null
                               ? "operation not captured (posting path did not run)"
                               : opLate.Status + " (read after the log wait; at post time it was "
                                 + enableStatus[0].Split(' ')[0] + ")");
                        bool enablePosted = outcome3 != null && outcome3.StartsWith("ok:");
                        step("StrategyEnable", enablePosted && started,
                             outcome3 + (started
                               ? "   >>> posted, and NinjaTrader reported it started arming"
                               : "   >>> NinjaTrader never reported that it began - see the step above"));
                        if (!(enablePosted && started)) { sb.Append("]}"); return sb.ToString(); }

                        // TRIGGER AND RETURN. Do not watch from in here.
                        //
                        // Two ways of waiting were tried on 2026-08-19 and both froze the
                        // bridge, each for its own reason:
                        //   - calling StrategyEnable synchronously inside the dispatcher never
                        //     came back; three requests queued behind it for seven minutes
                        //     while the heartbeat kept ticking, so the UI was alive and only
                        //     this worker was stuck.
                        //   - firing it and then polling the row through Dispatcher.Invoke in
                        //     a loop queued every poll BEHIND the enable's own dispatcher work
                        //     and froze just the same.
                        // Enabling is heavy: NinjaTrader loads the series and steps the state
                        // machine. Watching it therefore belongs in SEPARATE requests, each with
                        // its own budget - which is the trigger/wait/continue discipline every
                        // other stage follows. Use stage `strategystate` to watch.
                        // NOT a result: BeginInvoke only posted it. The proof is NinjaTrader's
                        // own log line, which the caller waits for - a step that cannot measure
                        // its effect must not claim one.
                        step("enable requested (NOT proof)", true,
                             "proof = NinjaTrader's log line \"Enabling NinjaScript strategy\"; "
                             + "our own object stays at " + strat.State + " because NinjaTrader "
                             + "runs its own instance");
                        sb.Append("]}");
                        return sb.ToString();
                    }

                    acc.Strategies.Add(strat);
                    step("added to account", true, acc.Name + ", state " + strat.State);

                    // `Active` is not part of the chain NinjaTrader walks.
                    State[] chain = new State[] { State.Configure, State.DataLoaded,
                                                  State.Historical, State.Transition, State.Realtime };
                    foreach (State target in chain)
                    {
                        State before = strat.State;
                        if ((int)before >= (int)target) { step("SetState(" + target + ")", true, "already " + before); continue; }
                        try
                        {
                            strat.SetState(target);
                            step("SetState(" + target + ")", strat.State == target, before + " -> " + strat.State);
                        }
                        catch (Exception ex)
                        {
                            step("SetState(" + target + ")", false,
                                 "threw " + ex.GetType().Name + ": " + ex.Message + " -> " + strat.State);
                        }
                        if (strat.State == State.Terminated || strat.State == State.Finalized) break;
                    }
                    sb.Append("]}");
                    return sb.ToString();
        }
        // One appended line per event, written the instant it happens - worker or UI
        // thread alike, plain file IO, no dispatcher. This is the channel that stays
        // readable while NinjaTrader's UI thread is stuck: the result JSON only exists
        // once a stage returns, but this file grows WHILE it runs. The driver tails it.
        // No clock TIME here (protocol rule: data-stream time or none) - but every
        // line now carries the milliseconds ELAPSED since this request was first
        // written to, which is a measurement rather than a clock and is the one
        // thing the file was missing.
        //
        // ⚠ WHY IT WAS NEEDED. A full run showed `ui idle` taking 31.2 s and then
        // 81.6 s and `ntlog` 16.1 s and then 36.3 s - each exactly its budget, so
        // each a timeout. The same stages answered in 2 s when sent on their own.
        // Timeless step lines cannot tell "this step is slow" from "this step
        // waited for the one before it", and that is precisely the question.
        private static readonly Dictionary<string, System.Diagnostics.Stopwatch> _progClocks
            = new Dictionary<string, System.Diagnostics.Stopwatch>();

        private void Progress(string id, string text)
        {
            try
            {
                if (_resultDir == null || id == null) return;
                System.Diagnostics.Stopwatch sw;
                lock (_progClocks)
                {
                    if (!_progClocks.TryGetValue(id, out sw))
                    {
                        sw = System.Diagnostics.Stopwatch.StartNew();
                        // Bounded: ids are unique per request, so this would grow for
                        // the life of the process otherwise. The oldest are of no
                        // further use once their file has been read.
                        if (_progClocks.Count > 500) _progClocks.Clear();
                        _progClocks[id] = sw;
                    }
                }
                File.AppendAllText(Path.Combine(_resultDir, "playbackrun_" + id + ".progress.txt"),
                                   string.Format(InvCi, "+{0,7} ms  {1}{2}",
                                                 sw.ElapsedMilliseconds, text, Environment.NewLine));
            }
            catch { }
        }
        /// <summary>How many rows the strategies grid holds, or -1 when that cannot
        /// be read at all - the two are different claims and the caller acts on it.
        ///
        /// ⚠ THE MEMBER IS NOT ALWAYS CALLED DataSource. This looked at that one
        /// property, found null on a freshly created profile, and reported -1 -
        /// "cannot read the grid" where the truth was "zero rows". The driver treats
        /// -1 as a failed mandatory step, so the run ended before it reached the
        /// connect (measured 2026-08-24, twice, on a UI.xml NinjaTrader had just
        /// written itself).
        ///
        /// Upstream's own SnapshotGrid looks under `source`, `Entries` and
        /// `ItemsSource`, in that order, because the member differs by NinjaTrader
        /// build. Same list here, with DataSource kept last so nothing that used to
        /// work stops working.</summary>
        private int RowCount(object grid)
        {
            try
            {
                object rows = FirstMember(grid, new[] { "source", "Entries", "ItemsSource", "DataSource" });
                System.Collections.IEnumerable seq = rows as System.Collections.IEnumerable;
                if (seq == null) return -1;
                int c = 0;
                foreach (object o in seq) if (o != null) c++;
                return c;
            }
            catch { return -1; }
        }

        // Parse {"fields":{"a":"b",...}} without a JSON library - the AddOn has none
        // and the payloads are flat.
        private static Dictionary<string, string> ParseNamedMap(string json, string name)
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            if (json == null) return map;
            int i = json.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
            if (i < 0) return map;
            int open = json.IndexOf('{', i);
            if (open < 0) return map;
            int depth = 0, close = -1;
            for (int k = open; k < json.Length; k++)
            {
                if (json[k] == '{') depth++;
                else if (json[k] == '}') { depth--; if (depth == 0) { close = k; break; } }
            }
            if (close < 0) return map;
            string body = json.Substring(open + 1, close - open - 1);
            int p = 0;
            while (p < body.Length)
            {
                int k1 = body.IndexOf('"', p); if (k1 < 0) break;
                int k2 = body.IndexOf('"', k1 + 1); if (k2 < 0) break;
                string key = body.Substring(k1 + 1, k2 - k1 - 1);
                int c = body.IndexOf(':', k2); if (c < 0) break;
                int v1 = body.IndexOf('"', c); if (v1 < 0) break;
                int v2 = body.IndexOf('"', v1 + 1); if (v2 < 0) break;
                map[key] = body.Substring(v1 + 1, v2 - v1 - 1);
                p = v2 + 1;
            }
            return map;
        }

        // Walk the visual tree in document order; the last TextBlock seen is the label
        // of the editor that follows. Writes and reads back.
        private string SetByLabel(System.Windows.DependencyObject root, string label, string value)
        {
            string[] last = new string[] { null };
            object[] target = new object[] { null };
            WalkForLabel(root, label, last, target);
            if (target[0] == null) return "no editor after a label '" + label + "'";
            object el = target[0];

            // An InstrumentSelector resolves what the user TYPED on commit; writing its
            // inner TextBox leaves the resolved instrument untouched, and OK then applies
            // the old one. Measured 2026-08-18: the dialog read back "MNQ ##-##" while
            // the row it created said "FDAX ##-##". Set the selector's own Instrument
            // property instead - that is the value NinjaTrader applies.
            System.Windows.DependencyObject sel = el as System.Windows.DependencyObject;
            while (sel != null && sel.GetType().Name.IndexOf("InstrumentSelector", StringComparison.Ordinal) < 0)
            {
                try { sel = System.Windows.Media.VisualTreeHelper.GetParent(sel); }
                catch { sel = null; }
            }
            if (sel != null)
            {
                try
                {
                    Instrument inst2 = Instrument.GetInstrument(value);
                    if (inst2 == null) return "instrument '" + value + "' unknown to NinjaTrader";
                    PropertyInfo pi2 = sel.GetType().GetProperty("Instrument");
                    if (pi2 != null && pi2.CanWrite)
                    {
                        pi2.SetValue(sel, inst2, null);
                        object back2 = pi2.GetValue(sel, null);
                        string bs2 = back2 == null ? "null" : ("" + back2);
                        return (bs2.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0
                                ? "wrote" : "wrote (differs)")
                               + " " + sel.GetType().Name + ".Instrument=" + value + " read=" + bs2;
                    }
                }
                catch (Exception ex) { return "InstrumentSelector threw " + ex.GetType().Name + ": " + ex.Message; }
            }
            foreach (string prop in new string[] { "SelectedItem", "IsChecked", "Text", "Value" })
            {
                PropertyInfo pi = el.GetType().GetProperty(prop);
                if (pi == null || !pi.CanWrite) continue;
                try
                {
                    object v;
                    if (prop == "IsChecked") v = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                    else if (prop == "SelectedItem")
                    {
                        v = null;
                        System.Windows.Controls.ItemsControl ic = el as System.Windows.Controls.ItemsControl;
                        if (ic != null)
                            foreach (object item in ic.Items)
                                if (item != null && string.Equals("" + item, value, StringComparison.OrdinalIgnoreCase))
                                { v = item; break; }
                        if (v == null)
                        {
                            // ⚠ NO FALLING BACK TO Text. For a control with items the ITEM is
                            // the truth and the text is a display artifact. Measured
                            // 2026-08-19: the Account combo had no 'Playback101' (the Playback
                            // connection was down, so no account existed); the old code wrote
                            // .Text, read .Text back, reported "ok", and confirming the dialog
                            // raised "Trying to add realtime strategy with no account" -
                            // NinjaTrader had to be restarted. The value's ABSENCE from the
                            // list is the finding, not an obstacle to route around.
                            if (ic != null && ic.Items.Count > 0)
                            {
                                StringBuilder opts = new StringBuilder();
                                int shown = 0;
                                foreach (object item in ic.Items)
                                {
                                    if (shown++ >= 12) { opts.Append(", ..."); break; }
                                    if (shown > 1) opts.Append(", ");
                                    opts.Append(item == null ? "null" : ("" + item));
                                }
                                return "'" + value + "' is not among the " + ic.Items.Count
                                     + " item(s) of " + el.GetType().Name + ": " + opts;
                            }
                            continue;   // genuinely not an items control - try Text/Value
                        }
                    }
                    else v = value;
                    pi.SetValue(el, v, null);
                    object back = pi.GetValue(el, null);
                    string bs = back == null ? "null" : back.ToString();
                    return (string.Equals(bs, value, StringComparison.OrdinalIgnoreCase) ? "wrote" : "wrote (differs)")
                           + " " + el.GetType().Name + "." + prop + "=" + value + " read=" + bs;
                }
                catch (Exception ex) { return prop + " threw " + ex.GetType().Name + ": " + ex.Message; }
            }
            return "no writable editor property on " + el.GetType().Name;
        }

        private void WalkForLabel(System.Windows.DependencyObject o, string label,
                                  string[] last, object[] target)
        {
            if (o == null || target[0] != null) return;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n && target[0] == null; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
                System.Windows.Controls.TextBlock tb = k as System.Windows.Controls.TextBlock;
                if (tb != null && !string.IsNullOrWhiteSpace(tb.Text)) last[0] = tb.Text.Trim();
                bool editor = k is System.Windows.Controls.ComboBox
                              || k is System.Windows.Controls.CheckBox
                              || k is System.Windows.Controls.TextBox;
                if (editor && last[0] != null
                    && string.Equals(last[0], label, StringComparison.OrdinalIgnoreCase))
                { target[0] = k; return; }
                WalkForLabel(k, label, last, target);
            }
        }
        private System.Windows.Controls.Primitives.ButtonBase FindButtonByText(
            System.Windows.DependencyObject o, string text)
        {
            if (o == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
                System.Windows.Controls.Primitives.ButtonBase bb =
                    k as System.Windows.Controls.Primitives.ButtonBase;
                if (bb != null)
                {
                    // Strip WPF access-key underscores: NinjaTrader's message boxes label
                    // their buttons "_Yes"/"_No", which renders as Yes/No with an
                    // underlined letter. Comparing the raw content never matches, and the
                    // dialog then blocks everything behind it.
                    string c = bb.Content == null ? null : ("" + bb.Content).Replace("_", "").Trim();
                    if ((c != null && string.Equals(c, text, StringComparison.OrdinalIgnoreCase))
                        || HasVisibleText(bb, text))
                        return bb;
                }
                System.Windows.Controls.Primitives.ButtonBase deep = FindButtonByText(k, text);
                if (deep != null) return deep;
            }
            return null;
        }
        // Expand the item that shows this text, so its children are realized. Without
        // this the child items do not exist in the visual tree and cannot be found.
        private void ExpandSelected(System.Windows.DependencyObject root, string text)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                System.Windows.Controls.TreeViewItem tvi = k as System.Windows.Controls.TreeViewItem;
                if (tvi != null && HasVisibleText(tvi, text))
                {
                    tvi.IsExpanded = true;
                    tvi.UpdateLayout();
                    return;
                }
                ExpandSelected(k, text);
            }
        }

        // Does this element display exactly this text? Searches its own subtree only.
        private bool HasVisibleText(System.Windows.DependencyObject o, string text)
        {
            if (o == null) return false;
            System.Windows.Controls.TextBlock tb = o as System.Windows.Controls.TextBlock;
            if (tb != null && tb.Text != null
                && string.Equals(tb.Text.Replace("_", "").Trim(), text, StringComparison.OrdinalIgnoreCase)) return true;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
                // Do not descend into a nested item - that text belongs to another row.
                if (k is System.Windows.Controls.TreeViewItem || k is System.Windows.Controls.ListBoxItem) continue;
                if (HasVisibleText(k, text)) return true;
            }
            return false;
        }

        // Select an entry in the dialog's strategy tree by its header text.
        private bool SelectTreeItem(System.Windows.DependencyObject root, string header)
        { return SelectTreeItem(root, header, false); }

        private bool SelectTreeItem(System.Windows.DependencyObject root, string header, bool leafOnly)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                // Header/Content are data objects here, not strings - their ToString()
                // is not the visible name. Match the TextBlock the item actually shows.
                // BringIntoView() before IsSelected: measured 2026-08-18, selecting an
                // item that had never been realised left the property grid showing the
                // previous strategy, so every field written after it belonged to the
                // wrong bot. Realising the container first is what makes the selection
                // reach the grid.
                System.Windows.Controls.TreeViewItem tvi = k as System.Windows.Controls.TreeViewItem;
                if (tvi != null && HasVisibleText(tvi, header) && !(leafOnly && tvi.HasItems))
                {
                    tvi.BringIntoView();
                    tvi.IsSelected = true;
                    return true;
                }
                System.Windows.Controls.ListBoxItem lbi = k as System.Windows.Controls.ListBoxItem;
                if (lbi != null && HasVisibleText(lbi, header))
                {
                    lbi.BringIntoView();
                    lbi.IsSelected = true;
                    return true;
                }
                if (SelectTreeItem(k, header, leafOnly)) return true;
            }
            return false;
        }

        private static string SafeReadOn(PropertyInfo pi, object target)
        {
            try { object v = pi.GetValue(target, null); return v == null ? "null" : v.ToString(); }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        // Report what the grid considers selected, from every angle it exposes.
        private void ReportSelection(object grid, string when, Action<string, bool, string> step)
        {
            foreach (string nm in new string[] { "SelectedItems", "SelectedDataItem", "SelectedDataItems", "ActiveDataItem", "ActiveRecord" })
            {
                PropertyInfo pi = grid.GetType().GetProperty(nm);
                if (pi == null) { step(when + "." + nm, false, "no such property"); continue; }
                object v = null;
                try { v = pi.GetValue(grid, null); } catch { }
                if (v == null) { step(when + "." + nm, true, "null"); continue; }
                string extra = v.GetType().Name;
                foreach (string sub in new string[] { "Records", "Cells", "DataItems" })
                {
                    PropertyInfo ps = v.GetType().GetProperty(sub);
                    if (ps == null) continue;
                    try
                    {
                        System.Collections.IEnumerable e2 = ps.GetValue(v, null) as System.Collections.IEnumerable;
                        int c = 0;
                        if (e2 != null) foreach (object x in e2) c++;
                        extra += "  " + sub + "=" + c;
                    }
                    catch { }
                }
                step(when + "." + nm, true, extra);
            }
        }
        /// <summary>Tick "Don't show this message again" on NinjaTrader's Historical
        /// notice and confirm it. Returns what happened, for the step log.
        ///
        /// Scope is deliberately narrow: only a window whose text carries BOTH
        /// "Level II market depth" and "Market Replay" is touched. That is the
        /// wording of this one notice. Any other dialog is left standing - the rule
        /// "a modal must never be clicked away" is unchanged for everything that is
        /// not this measured, self-inflicted, question-free notice.
        ///
        /// Runs on the UI thread via BeginInvoke: a WPF modal pushes its own
        /// dispatcher frame, and queued operations still run inside it.</summary>
        private static string DismissHistoricalNotice(int ttlMs)
        {
            string outcome = "not seen";
            int waited = 0;
            while (waited < ttlMs)
            {
                Window hit = null;
                try
                {
                    foreach (Window w in AllWindowsIncludingOwned())
                    {
                        if (w == null) continue;
                        string text = null;
                        try
                        {
                            Window ww = w;
                            text = ww.Dispatcher.Invoke(new Func<string>(delegate
                            {
                                return CollectText(ww);
                            })) as string;
                        }
                        catch { }
                        if (text == null) continue;
                        if (text.IndexOf("Level II market depth", StringComparison.OrdinalIgnoreCase) >= 0
                            && text.IndexOf("Market Replay", StringComparison.OrdinalIgnoreCase) >= 0)
                        { hit = w; break; }
                    }
                }
                catch { }

                if (hit != null)
                {
                    Window target = hit;
                    int boxes = 0, buttons = 0;
                    try
                    {
                        target.Dispatcher.Invoke(new Action(delegate
                        {
                            foreach (var cb in DescendantsOfType<System.Windows.Controls.CheckBox>(target))
                            { cb.IsChecked = true; boxes++; }
                            foreach (var b in DescendantsOfType<System.Windows.Controls.Button>(target))
                            {
                                string c = b.Content == null ? "" : b.Content.ToString();
                                if (c.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(b);
                                    var inv = peer.GetPattern(
                                        System.Windows.Automation.Peers.PatternInterface.Invoke)
                                        as System.Windows.Automation.Provider.IInvokeProvider;
                                    if (inv != null) { inv.Invoke(); buttons++; }
                                }
                            }
                        }));
                    }
                    catch (Exception ex)
                    { return "found, but could not confirm: " + ex.GetType().Name + ": " + ex.Message; }
                    return "confirmed after " + waited + " ms ("
                           + boxes + " box(es) ticked, " + buttons + " OK invoked)"
                           + " - it will not appear again on this machine";
                }
                Thread.Sleep(250);
                waited += 250;
            }
            return outcome;
        }

        // ── NinjaTrader's order-rejection notice during a run ────────────────────
        //  "Playback101, Stop price can't be changed above the market. affected
        //  Order: Sell 1 StopMarket @ 29861,5" - an NTMessageBox NinjaTrader raises
        //  when a strategy's stop modification is refused because the market crossed
        //  the stop between the strategy's own side check and NinjaTrader's
        //  validation. Measured 2026-09-02 (Playback/Historical, VwapSignal/VWAP_Long,
        //  NinjaTrader log 16:55:34): seven such rejections in one run; the strategy
        //  handled every one itself (closed the remainder at market) and the run
        //  reached the data end. The box is informational - it asks nothing - but it
        //  is MODAL: it stays until someone clicks OK, and an unattended run collects
        //  one per rejection.
        //
        //  This is the second, and only other, exception to "a modal must never be
        //  clicked away" (see StandingModal and DismissHistoricalNotice). It is as
        //  narrow as the first: only a window whose type name carries "MessageBox"
        //  AND whose text carries NinjaTrader's order-rejection trailer "affected
        //  Order:" is confirmed (see the match below for the measured wordings). Every
        //  other modal stays standing and stays a finding. The stage that runs every
        //  sample of the play loop (`strategystate`) calls this once per sample, so a
        //  notice stands for at most one sample interval, and the count travels in
        //  the stage's step text ("order notices") so the transcript shows every one.
        private static int _orderNoticesDismissed;      // per run; reset at the connect
        private static string _orderNoticesScan = "";     // what the last scan saw, for the step detail

        //  Which windows are looked at, and why all three sources: Globals.AllWindows
        //  holds the windows NinjaTrader registers (Control Center, charts, the
        //  Playback panel); a dialog they own is reached through OwnedWindows. A
        //  MessageBox NinjaTrader opens without an owner is in neither - measured
        //  2026-09-02 19:22-19:24: NinjaTrader's log carried seven rejections, the
        //  scan over AllWindows + OwnedWindows answered "0 dismissed" every sample.
        //  PresentationSource.CurrentSources lists EVERY WPF top-level window of the
        //  process, each with the dispatcher of the UI thread that owns it, so a
        //  window is read and confirmed on its own thread.
        private int DismissOrderRejectNotices()
        {
            int dismissed = 0;
            int seen = 0, boxes = 0;
            List<Window> candidates = new List<Window>();
            HashSet<Window> known = new HashSet<Window>();
            List<Window> all = new List<Window>();
            try { all.AddRange(AllWindowsIncludingOwned()); } catch { }
            try
            {
                foreach (System.Windows.PresentationSource src in System.Windows.PresentationSource.CurrentSources)
                {
                    if (src == null) continue;
                    System.Windows.PresentationSource s = src;
                    Window w = null;
                    try
                    {
                        w = s.Dispatcher.Invoke(new Func<Window>(delegate { return s.RootVisual as Window; }),
                                                TimeSpan.FromSeconds(3)) as Window;
                    }
                    catch { }
                    if (w != null) all.Add(w);
                }
            }
            catch (Exception ex) { LogSafe("order notice: PresentationSource scan failed: " + ex.GetType().Name + ": " + ex.Message); }
            foreach (Window w in all)
            {
                if (w == null || !known.Add(w)) continue;
                seen++;
                string ty = null;
                try { ty = w.GetType().Name; } catch { }
                if (ty == null || ty.IndexOf("MessageBox", StringComparison.OrdinalIgnoreCase) < 0) continue;
                boxes++;
                string text = null;
                try
                {
                    Window ww = w;
                    text = ww.Dispatcher.Invoke(new Func<string>(delegate { return CollectText(ww); }),
                                                TimeSpan.FromSeconds(3)) as string;
                }
                catch { }
                // The common signature of NinjaTrader's order-rejection boxes is the
                // trailer "affected Order: <action> <qty> <type> @ <price>". Measured
                // 2026-09-02 20:00-20:09 on the Short variant of the same strategy,
                // one box each: "Stop price can't be changed below the market", "Sell
                // stop or sell stop limit orders can't be placed above the market",
                // "Buy stop or buy stop limit orders can't be placed below the market"
                // and "Order '...' can't be submitted: The OCO ID '...' cannot be
                // reused" - all with that trailer; a match on the first wording alone
                // confirmed one of seven and left six standing.
                if (text != null && text.IndexOf("affected Order:", StringComparison.OrdinalIgnoreCase) >= 0)
                    candidates.Add(w);
            }
            foreach (Window target0 in candidates)
            {
                Window target = target0;
                int buttons = 0;
                try
                {
                    target.Dispatcher.Invoke(new Action(delegate
                    {
                        foreach (var b in DescendantsOfType<System.Windows.Controls.Button>(target))
                        {
                            string c = b.Content == null ? "" : b.Content.ToString();
                            if (c.IndexOf("OK", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(b);
                                var inv = peer.GetPattern(
                                    System.Windows.Automation.Peers.PatternInterface.Invoke)
                                    as System.Windows.Automation.Provider.IInvokeProvider;
                                if (inv != null) { inv.Invoke(); buttons++; }
                            }
                        }
                    }), TimeSpan.FromSeconds(3));
                }
                catch (Exception ex) { LogSafe("order notice: found, but could not confirm: " + ex.GetType().Name + ": " + ex.Message); }
                if (buttons > 0) dismissed++;
            }
            _orderNoticesScan = "windows " + seen + ", message boxes " + boxes + ", matching " + candidates.Count;
            return dismissed;
        }

        /// <summary>All text of a window, for identifying a notice by its wording.</summary>
        private static string CollectText(System.Windows.DependencyObject root)
        {
            var sb = new StringBuilder();
            foreach (var tb in DescendantsOfType<System.Windows.Controls.TextBlock>(root))
            { sb.Append(tb.Text); sb.Append(' '); }
            foreach (var lb in DescendantsOfType<System.Windows.Controls.Label>(root))
            { if (lb.Content != null) { sb.Append(lb.Content.ToString()); sb.Append(' '); } }
            return sb.ToString();
        }

        /// <summary>Visual-tree descendants of one type - same walk as
        /// FindElementStatic, by type instead of by name.</summary>
        private static List<T> DescendantsOfType<T>(System.Windows.DependencyObject root)
            where T : System.Windows.DependencyObject
        {
            var found = new List<T>();
            if (root == null) return found;
            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); }
            catch { return found; }
            for (int i = 0; i < n; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                T t = child as T;
                if (t != null) found.Add(t);
                found.AddRange(DescendantsOfType<T>(child));
            }
            return found;
        }

        // Every top-level window plus the dialogs they own. Globals.AllWindows lists
        // only the former; a picker opened from a context menu is an owned window and
        // is otherwise invisible to us.
        private static List<Window> AllWindowsIncludingOwned()
        {
            List<Window> all = new List<Window>();
            foreach (Window w in NinjaTrader.Core.Globals.AllWindows)
            {
                if (w == null) continue;
                all.Add(w);
                try
                {
                    Window ww = w;
                    System.Windows.WindowCollection owned = (System.Windows.WindowCollection)
                        ww.Dispatcher.Invoke(new Func<System.Windows.WindowCollection>(
                            delegate { return ww.OwnedWindows; }));
                    foreach (Window o in owned) if (o != null) all.Add(o);
                }
                catch { }
            }
            return all;
        }

        private static string SafeRead(PropertyInfo pi, object target)
        {
            try { object v = pi.GetValue(target); return v == null ? "null" : v.ToString(); }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        private static string SafeReadField(FieldInfo fi, object target)
        {
            try { object v = fi.GetValue(target); return v == null ? "null" : v.ToString(); }
            catch (Exception ex) { return "<" + ex.GetType().Name + ">"; }
        }

        // Write one static of the PlaybackAdapter and READ IT BACK. The read-back is
        // the point: several of these silently ignore a write depending on connection
        // state, and reporting "set" for those produces a run nobody can attribute.
        /// <summary>Write a static and REPORT the read-back without judging it.
        ///
        /// For values the adapter does not surface until it is live: measured,
        /// FromEst and ToEst read back as DateTime.MinValue before the connect no
        /// matter what was written, and failing the step on that killed runs whose
        /// connect then succeeded. The confirming check runs after the connection is
        /// up, where the getter answers.</summary>
        private void SetStaticQuiet(Type pb, string name, object value, Action<string, bool, string> step)
        {
            try
            {
                PropertyInfo pi = pb.GetProperty(name, BFStatic);
                if (pi == null) { step(name, false, "property not found"); return; }
                pi.SetValue(null, value, null);
                object back = pi.GetValue(null);
                bool same = back != null && back.ToString() == value.ToString();
                step(name, true, "wrote=" + value + " read=" + back
                     + (same ? "" : "  - not verifiable before the connect; confirmed by"
                                    + " step 'range on the connection'"));
            }
            catch (Exception ex) { step(name, false, ex.GetType().Name + ": " + ex.Message); }
        }

        private void SetStatic(Type pb, string name, object value, Action<string, bool, string> step)
        {
            try
            {
                PropertyInfo pi = pb.GetProperty(name, BFStatic);
                if (pi == null) { step(name, false, "property not found"); return; }
                pi.SetValue(null, value, null);
                object back = pi.GetValue(null);
                step(name, back != null && back.ToString() == value.ToString(),
                     "wrote=" + value + " read=" + back);
            }
            catch (Exception ex) { step(name, false, ex.GetType().Name + ": " + ex.Message); }
        }

        // NinjaTrader is multi-UI-threaded: Application.Current.Windows does not list
        // every window. Globals.AllWindows does, and the title is read on the window's
        // own dispatcher because every WPF member is thread-affine.
        private Window FindWindowByTitle(string titlePart)
        {
            if (string.IsNullOrWhiteSpace(titlePart)) titlePart = "Playback";
            // Exact title wins over a substring. NinjaTrader has both a
            // "Control Center - Strategies" tab and a separate "Strategies" dialog;
            // a substring search finds the wrong one and every following step then
            // reports on a window nobody asked for.
            // Globals.AllWindows lists NinjaTrader's own top-level windows but NOT
            // dialogs it owns - measured: the "Strategies" picker opened by
            // "New Strategy..." is absent there while the Win32 enumeration sees it.
            // Owned windows live on their owner's dispatcher, so walking
            // Window.OwnedWindows reaches them without any thread gymnastics.
            List<Window> all = new List<Window>();
            foreach (Window w in NinjaTrader.Core.Globals.AllWindows)
            {
                if (w == null) continue;
                all.Add(w);
                try
                {
                    Window ww = w;
                    System.Windows.WindowCollection owned = (System.Windows.WindowCollection)
                        ww.Dispatcher.Invoke(new Func<System.Windows.WindowCollection>(
                            delegate { return ww.OwnedWindows; }));
                    foreach (Window o in owned) if (o != null) all.Add(o);
                }
                catch { }
            }
            Window loose = null;
            foreach (Window w in all)
            {
                string ti = null;
                try { ti = (string)w.Dispatcher.Invoke(new Func<string>(delegate { return w.Title; })); }
                catch { }
                if (ti == null) continue;
                if (string.Equals(ti, titlePart, StringComparison.OrdinalIgnoreCase)) return w;
                if (loose == null && ti.IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) >= 0) loose = w;
            }
            return loose;
        }

        private string WindowTitles()
        {
            List<string> t = new List<string>();
            foreach (Window w in NinjaTrader.Core.Globals.AllWindows)
            {
                if (w == null) continue;
                try { t.Add((string)w.Dispatcher.Invoke(new Func<string>(delegate { return w.Title; }))); }
                catch { }
            }
            return string.Join(" | ", t.ToArray());
        }

        // FindName does not reach these: the controls live inside ControlTemplates,
        // which is a different name scope. Walk the visual tree instead.
        private object FindElement(System.Windows.DependencyObject root, string name)
        {
            if (root == null) return null;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                System.Windows.FrameworkElement fe = k as System.Windows.FrameworkElement;
                if (fe != null && fe.Name == name) return fe;
                object deep = FindElement(k, name);
                if (deep != null) return deep;
            }
            return null;
        }

        private void SetElementValue(System.Windows.DependencyObject root, string name,
                                     string property, object value, Action<string, bool, string> step)
        { SetElementValue(root, name, property, value, step, 120.0); }

        private void SetElementValue(System.Windows.DependencyObject root, string name,
                                     string property, object value, Action<string, bool, string> step,
                                     double ttlSec)
        {
            object el = FindElement(root, name);
            if (el == null) { step(name, false, "not in the visual tree"); return; }
            PropertyInfo pi = el.GetType().GetProperty(property);
            if (pi == null) { step(name, false, property + " not on " + el.GetType().Name); return; }
            try
            {
                // ── The four-phase handshake, for a UI write ─────────────────────────────────
                //
                // 1. what does it say BEFORE?  2. write  3. wait until the LIVE tree says the
                // new value  4. report the move with its measured duration.
                //
                // ⚠ Phase 3 re-FINDS the element every time instead of reading back the object
                // just written to. NinjaTrader rebuilds this panel, and a write that landed on a
                // control it is about to discard reads back perfectly - the same shape as
                // upstream's issue #6, where a detached template echoed its own value while the
                // tab kept the old one. Only a fresh lookup can tell the two apart.
                string beforeV = "";
                try { object b0 = pi.GetValue(el, null); beforeV = b0 == null ? "null" : b0.ToString(); }
                catch { beforeV = "<unreadable>"; }

                pi.SetValue(el, value, null);

                string want = value.ToString();
                string gotV; long msV;
                System.Windows.DependencyObject rootV = root;
                string nameV = name, propV = property;
                bool arrived = WaitUntilChanged(delegate
                {
                    try
                    {
                        object el2 = FindElement(rootV, nameV);          // fresh, every time
                        if (el2 == null) return "<element gone>";
                        PropertyInfo pi2 = el2.GetType().GetProperty(propV);
                        if (pi2 == null) return "<property gone>";
                        object v2 = pi2.GetValue(el2, null);
                        return v2 == null ? "null" : v2.ToString();
                    }
                    catch (Exception) { return "<tree being rebuilt>"; }
                }, beforeV, want, ttlSec, out gotV, out msV);

                step(name + "." + property, arrived,
                     beforeV + " -> " + gotV + " after " + msV + " ms (wanted " + want + ")"
                     + (beforeV == want ? "   [was already the target - nothing moved]" : ""));
            }
            catch (Exception ex)
            {
                // A TargetInvocationException carries only "the callee threw"; the
                // reason is one level down. Dropping it left the log naming a symptom
                // with no cause (measured 30.08.2026 on rbHistoricalData.IsChecked).
                string why = ex.GetType().Name + ": " + ex.Message;
                Exception inner = ex.InnerException;
                int depth = 0;
                while (inner != null && depth++ < 3)
                {
                    why += "  <- " + inner.GetType().Name + ": " + inner.Message;
                    inner = inner.InnerException;
                }
                step(name + "." + property, false, why);
            }
        }

        // Depth 40, not 12: the Historical Data window's download fields sit deeper
        // than 12 levels and a shallower walk finds only the window chrome.
        private void DumpVisualTree(System.Windows.DependencyObject o, int depth, Action<string, bool, string> step)
        {
            if (o == null || depth > 40) return;
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++)
            {
                System.Windows.DependencyObject k = System.Windows.Media.VisualTreeHelper.GetChild(o, i);
                System.Windows.FrameworkElement fe = k as System.Windows.FrameworkElement;
                // Labels carry the meaning: in NinjaTrader's property grids every editor
                // is named PART_editor, so a field is only identifiable by the TextBlock
                // that precedes it. Report those too, in document order.
                System.Windows.Controls.TextBlock tb = k as System.Windows.Controls.TextBlock;
                if (tb != null && !string.IsNullOrWhiteSpace(tb.Text))
                    step(new string(' ', depth) + "label", true, tb.Text);
                // Report unnamed buttons too: NinjaTrader's message-box buttons carry no
                // x:Name, so a name-only dump hides exactly the controls one needs.
                System.Windows.Controls.Primitives.ButtonBase ub =
                    k as System.Windows.Controls.Primitives.ButtonBase;
                if (ub != null && (fe == null || string.IsNullOrEmpty(fe.Name)))
                    step(new string(' ', depth) + k.GetType().Name + " (unnamed)", true,
                         "Content=" + (ub.Content == null ? "null" : ("" + ub.Content)));
                if (fe != null && !string.IsNullOrEmpty(fe.Name))
                {
                    string v = "";
                    try
                    {
                        if (k is System.Windows.Controls.DatePicker)
                            v = "SelectedDate=" + ((System.Windows.Controls.DatePicker)k).SelectedDate;
                        else if (k is System.Windows.Controls.Primitives.RangeBase)
                        {
                            System.Windows.Controls.Primitives.RangeBase rb = (System.Windows.Controls.Primitives.RangeBase)k;
                            v = "Value=" + rb.Value + " Min=" + rb.Minimum + " Max=" + rb.Maximum;
                        }
                        else if (k is System.Windows.Controls.ComboBox)
                            v = "SelectedItem=" + ((System.Windows.Controls.ComboBox)k).SelectedItem;
                        else if (k is System.Windows.Controls.TextBox)
                            v = "Text=" + ((System.Windows.Controls.TextBox)k).Text;
                        else if (k is System.Windows.Controls.Primitives.ToggleButton)
                            v = "IsChecked=" + ((System.Windows.Controls.Primitives.ToggleButton)k).IsChecked
                              + " Content=" + ((System.Windows.Controls.Primitives.ToggleButton)k).Content;
                        else if (k is System.Windows.Controls.Button)
                            v = "Content=" + ((System.Windows.Controls.Button)k).Content;
                    }
                    catch (Exception ex) { v = "<" + ex.GetType().Name + ">"; }
                    // Report IsEnabled as well: a greyed control cannot be driven, and
                    // without this value a failed write looks like a fault in the driving
                    // code rather than a locked UI.
                    System.Windows.UIElement ue = k as System.Windows.UIElement;
                    if (ue != null && !ue.IsEnabled) v = (v.Length > 0 ? v + "  " : "") + "[DISABLED]";
                    step(new string(' ', depth) + k.GetType().Name + " '" + fe.Name + "'", true, v);
                }
                DumpVisualTree(k, depth + 1, step);
            }
        }

        // One element, its binding-relevant members and its context menu. Used to find
        // what a grid is actually bound to — Account.Strategies is not what the
        // Control Center shows.
        private void DumpElement(System.Windows.DependencyObject root, string name, Action<string, bool, string> step)
        {
            object o = FindElement(root, name);
            if (o == null) { step("element '" + name + "'", false, "not in the visual tree"); return; }
            step("element '" + name + "'", true, o.GetType().FullName);
            foreach (PropertyInfo pi in o.GetType().GetProperties(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string nm = pi.Name;
                if (nm.IndexOf("Item", StringComparison.OrdinalIgnoreCase) < 0
                    && nm.IndexOf("Source", StringComparison.OrdinalIgnoreCase) < 0
                    && nm.IndexOf("Selected", StringComparison.OrdinalIgnoreCase) < 0
                    && nm.IndexOf("Context", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string v;
                try
                {
                    object val = pi.GetValue(o, null);
                    v = val == null ? "null" : val.GetType().FullName;
                    System.Collections.IEnumerable en = val as System.Collections.IEnumerable;
                    if (en != null && !(val is string))
                    {
                        int c = 0;
                        foreach (object x in en) c++;
                        v += " (" + c + " items)";
                    }
                    // The grid's rows are the objects NinjaTrader itself manages. Dump the
                    // first one so a caller can see what is settable on a strategy row
                    // instead of guessing at Account.Strategies, which is a different
                    // collection entirely.
                    if (en != null && !(val is string) && nm.IndexOf("Source", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (object row in en)
                        {
                            step("  first row", true, row == null ? "null" : row.GetType().FullName);
                            if (row != null)
                                foreach (PropertyInfo rp in row.GetType().GetProperties(
                                             BindingFlags.Public | BindingFlags.Instance))
                                {
                                    string rv;
                                    try { object x = rp.GetValue(row, null); rv = x == null ? "null" : x.ToString(); }
                                    catch (Exception ex2) { rv = "<" + ex2.GetType().Name + ">"; }
                                    step("    " + rp.Name, rp.CanWrite,
                                         rp.PropertyType.Name + " = " + (rv.Length > 60 ? rv.Substring(0, 60) : rv));
                                }
                            break;
                        }
                    }
                    System.Windows.Controls.ContextMenu cm = val as System.Windows.Controls.ContextMenu;
                    if (cm != null)
                        foreach (object it in cm.Items)
                        {
                            System.Windows.Controls.MenuItem mi = it as System.Windows.Controls.MenuItem;
                            step("    menu", mi != null && mi.IsEnabled,
                                 mi == null ? it.GetType().Name : ("" + mi.Header));
                        }
                }
                catch (Exception ex) { v = "<" + ex.GetType().Name + ">"; }
                step("  " + nm, pi.CanWrite, v.Length > 110 ? v.Substring(0, 110) : v);
            }
        }


        // ── Generic, not playback-specific ───────────────────────────────────────────────
        //  These four belong to the bridge as a whole, not to our stages. They live here
        //  only so upstream's file stays close to GitHub; their CALL SITES are still in
        //  NT8BridgeServer.cs, because a call inside his method cannot move. Each is a
        //  candidate to offer upstream, with the measurement that motivated it.
        // How long a queued request stays valid, in seconds. `ttlSec` in the request
        // wins so the long commands (backtest, downloads, compile) can outlive the
        // default without weakening it for everything else.
        private const double DefaultTtlSec = 300.0;

        // Reads a NUMBER, which ExtractJsonString cannot: that one takes the first
        // '"' after the colon, and for `"ttlSec": 300` the next quote belongs to the
        // FOLLOWING key. A quoted value is accepted as well.
        private static double RequestTtlSec(string json)
        {
            if (json == null) return DefaultTtlSec;
            const string pat = "\"ttlSec\"";
            int i = json.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0) return DefaultTtlSec;
            int c = json.IndexOf(':', i + pat.Length);
            if (c < 0) return DefaultTtlSec;
            int p = c + 1;
            while (p < json.Length && (char.IsWhiteSpace(json[p]) || json[p] == '"')) p++;
            int s = p;
            while (p < json.Length && (char.IsDigit(json[p]) || json[p] == '.' ||
                                       json[p] == '-' || json[p] == '+' ||
                                       json[p] == 'e' || json[p] == 'E')) p++;
            double v;
            if (p > s && double.TryParse(json.Substring(s, p - s),
                                         System.Globalization.NumberStyles.Float, InvCi, out v)
                && v > 0.0)
                return v;
            return DefaultTtlSec;
        }

        // Result files are read once - the client polls for one name and moves on.
        // Measured 2026-08-19: 133 MB in 392 files, the oldest from a previous
        // session, because nothing ever deleted them.
        private DateTime _lastPrune = DateTime.MinValue;


        private void PruneResults()
        {
            try
            {
                if ((DateTime.UtcNow - _lastPrune).TotalMinutes < 30.0) return;
                _lastPrune = DateTime.UtcNow;
                if (_resultDir == null) return;
                DateTime cutoff = DateTime.UtcNow.AddHours(-24.0);
                int gone = 0;
                foreach (string f in Directory.GetFiles(_resultDir, "*.json"))
                {
                    // heartbeat.json is rewritten continuously and must survive.
                    if (string.Equals(Path.GetFileName(f), "heartbeat.json",
                                      StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                        File.Delete(f);
                        gone++;
                    }
                    catch { }
                }
                // The live-progress companions (playbackrun_<id>.progress.txt) age out
                // on the same 24h cutoff - same lifecycle as the result they belong to.
                foreach (string f in Directory.GetFiles(_resultDir, "playbackrun_*.progress.txt"))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) >= cutoff) continue;
                        File.Delete(f);
                        gone++;
                    }
                    catch { }
                }
                if (gone > 0) LogSafe("pruned " + gone + " result file(s) older than 24h");
            }
            catch (Exception ex) { LogSafe("PruneResults: " + ex.Message); }
        }

        // Upstream's ResultPrefix() does not know our kind, and an unknown kind falls
        // through to "compile_". Measured 2026-08-19: the TTL refusal for a `playbackrun`
        // was therefore written as compile_<id>.json - the log said
        //     expired playbackrun b227d6f2...: 601s old, ttl 5s - NOT executed
        // while the caller polled playbackrun_<id>.json and waited out its own timeout.
        // A guard that MISFILES its answer turns a safety net into the hang it exists to
        // prevent. Kept here rather than patching his table, so his file stays close to
        // GitHub.
        private static string ResultPrefixEx(string kind, string upstreamPrefix)
        {
            if (kind == "playbackrun") return "playbackrun_";
            if (kind == "satemplate") return "satemplate_";
            return upstreamPrefix;
        }
    }
}
