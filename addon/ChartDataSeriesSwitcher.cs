// ChartDataSeriesSwitcher.cs — shared, reusable engine that switches an OPEN chart's data series
// (instrument + bar type + Value / Value2 / BaseBarsPeriodValue) on the chart's OWN UI thread.
//
// NinjaTrader runs EACH chart window on its own UI thread; Globals.MainThreadDispatcher (the Control
// Center thread) cannot touch chart objects. So every chart read/mutate runs INSIDE that window's
// w.Dispatcher, bounded by TryInvoke so a wedged chart thread can't stall the caller's poller. Only
// STRINGS (and Window references, never touched off-thread) cross thread boundaries; the returned
// SwitchResult is plain strings, safe to hand back. Reused by the NT8Bridge chartseries handler and
// (later) the ChartScheduler — target by tab name (Switch) or by current instrument (SwitchByInstrument).
//
// Bar type is passed as an int id (BarsPeriodType ordinal OR a custom add-on id, e.g. ninZaRenko=12345,
// UniRenko=2018). Value / Value2 / BaseBarsPeriodValue are set ONLY when provided (set-if-present),
// covering UniRenko's 3 params, NinzaRenko's 2, and SHHeikenAshi's Value2-only. See
// docs/superpowers/specs/spike-bartypes.md §6.
#region Using declarations
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public static class ChartDataSeriesSwitcher
    {
        // Plain-string result (safe to cross threads). Status: "ok" | "blocked" | "missing" | "error".
        // before/after are split into instrument + bars ("Type:Value") for the caller to assemble.
        public class SwitchResult
        {
            public string Status { get; set; }
            public string Title { get; set; }
            public string BeforeInstrument { get; set; }
            public string BeforeBars { get; set; }
            public string AfterInstrument { get; set; }
            public string AfterBars { get; set; }
            public string Message { get; set; }
        }

        // One scanned chart tab. Win is held by the caller thread but only dereferenced INSIDE w.Dispatcher.
        private class TabRec
        {
            public Window Win;
            public int TabIndex;
            public string Name;
            public string Instr;
            public bool WinActive;
            public bool Selected;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // Target the chart whose ChartTab name == tabName. Empty/null tabName => the active chart
        // (focused window's selected tab), else the sole chart window. instrumentName empty => keep the
        // chart's current instrument; barsPeriodTypeId < 0 => keep the chart's current bar type.
        public static SwitchResult Switch(string tabName, string instrumentName, int barsPeriodTypeId,
                                          int? value, int? value2, int? baseValue, int? baseType, bool force)
        {
            string availRead; int scanned; string firstErr;
            List<TabRec> recs = Scan(out availRead, out scanned, out firstErr);
            var hits = new List<TabRec>();
            bool byActive = string.IsNullOrEmpty(tabName);
            if (byActive)
            {
                foreach (TabRec r in recs) if (r.WinActive && r.Selected) hits.Add(r);
                if (hits.Count == 0)
                {
                    // sole-chart fallback: if exactly one chart window exists, target its selected/first tab.
                    var wins = new List<Window>();
                    foreach (TabRec r in recs) if (!wins.Contains(r.Win)) wins.Add(r.Win);
                    if (wins.Count == 1)
                    {
                        TabRec sel = null;
                        foreach (TabRec r in recs) if (ReferenceEquals(r.Win, wins[0]) && r.Selected) { sel = r; break; }
                        if (sel == null) foreach (TabRec r in recs) if (ReferenceEquals(r.Win, wins[0])) { sel = r; break; }
                        if (sel != null) hits.Add(sel);
                    }
                }
            }
            else foreach (TabRec r in recs) if (Eq(r.Name, tabName)) hits.Add(r);

            string desc = byActive ? "active chart" : ("tab '" + tabName + "'");
            return Resolve(hits, desc, availRead, scanned, firstErr, instrumentName, barsPeriodTypeId, value, value2, baseValue, baseType, force);
        }

        // Target the chart currently showing currentInstrument (the CLI --on-instrument mode).
        public static SwitchResult SwitchByInstrument(string currentInstrument, string instrumentName, int barsPeriodTypeId,
                                                      int? value, int? value2, int? baseValue, int? baseType, bool force)
        {
            string availRead; int scanned; string firstErr;
            List<TabRec> recs = Scan(out availRead, out scanned, out firstErr);
            var hits = new List<TabRec>();
            foreach (TabRec r in recs) if (Eq(r.Instr, currentInstrument)) hits.Add(r);
            return Resolve(hits, "instrument '" + currentInstrument + "'", availRead, scanned, firstErr,
                           instrumentName, barsPeriodTypeId, value, value2, baseValue, baseType, force);
        }

        private static SwitchResult Resolve(List<TabRec> hits, string desc, string availRead, int scanned, string firstErr,
                                            string instrumentName, int typeId, int? value, int? value2, int? baseValue, int? baseType, bool force)
        {
            if (hits.Count != 1)
            {
                string note = scanned == 0 ? (" (no chart window could be scanned" + (firstErr != null ? ": " + firstErr : "") + ")") : "";
                string msg = (hits.Count == 0 ? "no chart matched " : ("ambiguous: " + hits.Count + " charts matched ")) + desc + note + "; available: " + availRead;
                return new SwitchResult { Status = hits.Count == 0 ? "missing" : "error", Message = msg };
            }
            return Mutate(hits[0], instrumentName, typeId, value, value2, baseValue, baseType, force);
        }

        // Run the whole mutation on the matched window's OWN thread, then poll the settled state from the
        // caller thread (never sleep on the chart thread — that blocks the reload it must run).
        private static SwitchResult Mutate(TabRec rec, string instrumentName, int typeId,
                                           int? value, int? value2, int? baseValue, int? baseType, bool force)
        {
            Window w = rec.Win;
            int ti = rec.TabIndex;
            SwitchResult sr;
            bool ok = TryInvoke(w, new Func<SwitchResult>(delegate
            {
                var r = new SwitchResult();
                try
                {
                    object cc = ResolveCcByIndex(w, ti);
                    if (cc == null)
                    {
                        object item = TabItemAt(w, ti);
                        r.Status = "error";
                        r.Message = "matched tab '" + rec.Name + "' but its ChartControl did not resolve "
                                  + "(TabItem.Content -> ChartTab.ChartControl). Content=" + TypeNameOf(PropVal(item, "Content"))
                                  + ", DataContext=" + TypeNameOf(PropVal(item, "DataContext")) + ", Tag=" + TypeNameOf(PropVal(item, "Tag"));
                        return r;
                    }
                    r.Title = !string.IsNullOrEmpty(rec.Name) ? rec.Name : ChartTabNameOf(cc);
                    if (string.IsNullOrEmpty(r.Title)) r.Title = ReadStrProp(w, "Title");
                    r.BeforeInstrument = CcInstrument(cc);
                    r.BeforeBars = CcBars(cc);

                    // Safety guard (skip with force): enabled/realtime strategy on the chart, or a non-flat
                    // position on the current OR target instrument IN THIS CHART'S OWN Chart Trader account
                    // (an unrelated account's position must NOT block a display-only switch). FAILS CLOSED.
                    if (!force)
                    {
                        string trip = StrategyGuardTrip(cc);
                        if (trip == null) trip = PositionGuardTrip(w, cc, instrumentName);
                        if (trip != null)
                        {
                            r.Status = "blocked"; r.Message = trip;
                            r.AfterInstrument = r.BeforeInstrument; r.AfterBars = r.BeforeBars;
                            return r;
                        }
                    }

                    object instrObj;
                    if (!string.IsNullOrEmpty(instrumentName))
                    {
                        instrObj = Instrument.GetInstrument(instrumentName);
                        if (instrObj == null) { r.Status = "error"; r.Message = "Instrument.GetInstrument returned null for: " + instrumentName; return r; }
                    }
                    else instrObj = cc.GetType().GetProperty("Instrument").GetValue(cc, null);

                    object bpObj;
                    if (typeId < 0) bpObj = cc.GetType().GetProperty("BarsPeriod").GetValue(cc, null);   // keep current
                    else
                    {
                        BarsPeriod bp = new BarsPeriod { BarsPeriodType = (BarsPeriodType)typeId };
                        // set-if-present; via reflection so BaseBarsPeriodValue (base-period types) is robust.
                        if (value.HasValue) SetIntProp(bp, "Value", value.Value);
                        if (value2.HasValue) SetIntProp(bp, "Value2", value2.Value);
                        if (baseValue.HasValue) SetIntProp(bp, "BaseBarsPeriodValue", baseValue.Value);
                        if (baseType.HasValue) bp.BaseBarsPeriodType = (BarsPeriodType)baseType.Value;   // base-period types (HeikenAshi)
                        bpObj = bp;
                    }

                    MethodInfo apply = w.GetType().GetMethod("OnDataSeriesChanged", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (apply == null) { r.Status = "error"; r.Message = "Chart.OnDataSeriesChanged not found on " + w.GetType().FullName; return r; }
                    apply.Invoke(w, new object[] { cc, instrObj, bpObj, false, false, true, null });

                    r.AfterInstrument = CcInstrument(cc);
                    r.AfterBars = CcBars(cc);
                    r.Status = "ok"; r.Message = "";
                    return r;
                }
                catch (Exception ex) { r.Status = "error"; r.Message = "chartseries apply: " + ex.GetType().Name + ": " + ex.Message; return r; }
            }), 5, out sr);

            if (!ok) return new SwitchResult { Status = "error", Message = "chart UI thread unresponsive (timed out)" };
            if (sr.Status != "ok") return sr;

            // Settled after-read: poll IsBarsLoading from the CALLER thread (short bounded reads on the
            // chart thread + sleep here). OnDataSeriesChanged is synchronous in practice -> usually iter 1.
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                string[] probe;   // { instrument, bars, isLoading("true"/"false") }
                bool pok = TryInvoke(w, new Func<string[]>(delegate
                {
                    try
                    {
                        object cc = ResolveCcByIndex(w, ti);
                        if (cc == null) return new string[] { "", "", "false" };
                        return new string[] { CcInstrument(cc), CcBars(cc), ReadIsBarsLoading(cc) ? "true" : "false" };
                    }
                    catch { return new string[] { "", "", "false" }; }
                }), 5, out probe);
                if (!pok) break;
                if (!string.IsNullOrEmpty(probe[0])) { sr.AfterInstrument = probe[0]; sr.AfterBars = probe[1]; }
                if (probe[2] == "false") break;
                if (DateTime.UtcNow >= deadline) break;
                try { Thread.Sleep(100); } catch { }
            }
            return sr;
        }

        // Scan every chart window (bounded per-window dispatch) into TabRecs. availRead is a readable
        // listing for diagnostics; windowsScanned/firstErr report scan health (timeouts).
        private static List<TabRec> Scan(out string availRead, out int windowsScanned, out string firstErr)
        {
            var recs = new List<TabRec>();
            var ar = new StringBuilder();
            windowsScanned = 0; firstErr = null;
            bool firstW = true;
            foreach (Window w in FindAllChartWindows())
            {
                List<string[]> rows;
                if (!TryInvoke(w, new Func<List<string[]>>(delegate { return ScanWindowTabs(w); }), 5, out rows))
                {
                    rows = null;
                    if (firstErr == null) firstErr = "a chart window's UI thread was unresponsive (timed out)";
                }
                string wtitle = ""; bool wactive = false; int selIdx = -1;
                var tabs = new List<string[]>();
                if (rows != null && rows.Count > 0)
                {
                    windowsScanned++;
                    wtitle = rows[0][0]; wactive = rows[0][1] == "true"; int.TryParse(rows[0][2], out selIdx);
                    for (int k = 1; k < rows.Count; k++) tabs.Add(rows[k]);
                }
                if (!firstW) ar.Append("; ");
                firstW = false;
                ar.Append("window '").Append(wtitle).Append("'").Append(wactive ? " (active)" : "").Append(" tabs:");
                for (int i = 0; i < tabs.Count; i++)
                {
                    ar.Append(" '").Append(tabs[i][0]).Append("'(").Append(tabs[i][1]).Append(")");
                    recs.Add(new TabRec { Win = w, TabIndex = i, Name = tabs[i][0], Instr = tabs[i][1], WinActive = wactive, Selected = (i == selIdx) });
                }
            }
            availRead = ar.ToString();
            return recs;
        }

        // ---- self-contained reflection helpers (run on the chart's own thread where noted) ----

        private static List<Window> FindAllChartWindows()
        {
            var outl = new List<Window>();
            var all = Globals.AllWindows;
            if (all == null) return outl;
            var snap = new List<Window>();
            try { for (int i = 0; i < all.Count; i++) snap.Add(all[i]); } catch { }
            foreach (Window w in snap)
                if (w != null && w.GetType().FullName == "NinjaTrader.Gui.Chart.Chart") outl.Add(w);
            return outl;
        }

        // ON THE WINDOW'S THREAD: rows[0]={title,active,selIdx}; rows[1..]={tabName,instrument}.
        private static List<string[]> ScanWindowTabs(Window w)
        {
            var rows = new List<string[]>();
            string wtitle = ReadStrProp(w, "Title");
            bool wactive = false;
            try { object a = w.GetType().GetProperty("IsActive").GetValue(w, null); if (a is bool) wactive = (bool)a; } catch { }
            object tc = TabControlOf(w);
            int selIdx = -1;
            try { object si = PropVal(tc, "SelectedIndex"); if (si is int) selIdx = (int)si; } catch { }
            rows.Add(new string[] { wtitle, wactive ? "true" : "false", selIdx.ToString(Inv) });
            foreach (object item in RawTabItems(tc))
            {
                string tabName, instr;
                TabNameAndInstrument(item, out tabName, out instr);
                rows.Add(new string[] { tabName, instr });
            }
            return rows;
        }

        // prefer the public MainTabControl property, fall back to the internal tabControl field.
        private static object TabControlOf(Window w)
        {
            if (w == null) return null;
            try
            {
                PropertyInfo p = w.GetType().GetProperty("MainTabControl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) { object v = p.GetValue(w, null); if (v != null) return v; }
                FieldInfo f = w.GetType().GetField("tabControl", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(w);
            }
            catch { }
            return null;
        }

        private static List<object> RawTabItems(object tc)
        {
            var outl = new List<object>();
            if (tc == null) return outl;
            try
            {
                object items = tc.GetType().GetProperty("Items").GetValue(tc, null);
                if (items is IEnumerable) foreach (object it in (IEnumerable)items) outl.Add(it);
            }
            catch { }
            return outl;
        }

        // on the window's own thread a TabItem.Content IS the ChartTab: name = ChartTab.TabName
        // (fallback TabItem.Header); instrument = ChartTab.ChartControl.Instrument.FullName.
        private static void TabNameAndInstrument(object item, out string tabName, out string instr)
        {
            tabName = ""; instr = "";
            object content = PropVal(item, "Content");
            if (content != null && content.GetType().FullName == "NinjaTrader.Gui.Chart.ChartTab")
            {
                string tn = ReadStrProp(content, "TabName");
                tabName = !string.IsNullOrEmpty(tn) ? tn : ReadStrProp(item, "Header");
                instr = CcInstrument(PropVal(content, "ChartControl"));
            }
            else tabName = ReadStrProp(item, "Header");
        }

        private static object ResolveCcByIndex(Window w, int tabIndex)
        {
            List<object> items = RawTabItems(TabControlOf(w));
            if (tabIndex < 0 || tabIndex >= items.Count) return null;
            object content = PropVal(items[tabIndex], "Content");
            if (content == null || content.GetType().FullName != "NinjaTrader.Gui.Chart.ChartTab") return null;
            return PropVal(content, "ChartControl");
        }

        private static object TabItemAt(Window w, int tabIndex)
        {
            List<object> items = RawTabItems(TabControlOf(w));
            return (tabIndex >= 0 && tabIndex < items.Count) ? items[tabIndex] : null;
        }

        private static string ChartTabNameOf(object cc)
        {
            object tab = PropVal(cc, "ChartTab");
            if (tab == null) return "";
            string t = ReadStrProp(tab, "TabName");
            return !string.IsNullOrEmpty(t) ? t : ReadStrProp(tab, "ActualTabName");
        }

        private static string CcInstrument(object cc)
        {
            if (cc == null) return "";
            try
            {
                object i = cc.GetType().GetProperty("Instrument").GetValue(cc, null);
                if (i == null) return "";
                object fn = i.GetType().GetProperty("FullName").GetValue(i, null);
                return fn != null ? fn.ToString() : "";
            }
            catch { return ""; }
        }

        // "Minute:5" / "12345:50" — BarsPeriodType (custom ids print their int) + Value.
        private static string CcBars(object cc)
        {
            if (cc == null) return "";
            try
            {
                object bp = cc.GetType().GetProperty("BarsPeriod").GetValue(cc, null);
                if (bp == null) return "";
                object t = bp.GetType().GetProperty("BarsPeriodType").GetValue(bp, null);
                object v = bp.GetType().GetProperty("Value").GetValue(bp, null);
                return "" + t + ":" + v;
            }
            catch { return ""; }
        }

        private static void SetIntProp(object o, string name, int val)
        {
            try { PropertyInfo p = o.GetType().GetProperty(name); if (p != null && p.CanWrite) p.SetValue(o, val, null); } catch { }
        }

        private static object PropVal(object o, string name)
        {
            if (o == null) return null;
            try { PropertyInfo p = o.GetType().GetProperty(name); return p != null ? p.GetValue(o, null) : null; } catch { return null; }
        }

        private static string ReadStrProp(object o, string name)
        {
            try { PropertyInfo p = o.GetType().GetProperty(name); object v = p != null ? p.GetValue(o, null) : null; return v != null ? v.ToString() : ""; }
            catch { return ""; }
        }

        private static string TypeNameOf(object o) { return o == null ? "null" : o.GetType().FullName; }

        private static bool Eq(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // FAILS CLOSED: an unverifiable strategy state blocks (only force bypasses).
        private static string StrategyGuardTrip(object cc)
        {
            const string unverifiable = "blocked: could not verify chart strategies (failing closed) — pass --force to override";
            try
            {
                object strategies = cc.GetType().GetProperty("Strategies").GetValue(cc, null);
                if (strategies is IEnumerable)
                    foreach (object s in (IEnumerable)strategies)
                    {
                        bool en; object st;
                        try { en = (bool)s.GetType().GetProperty("IsEnabled").GetValue(s, null); st = s.GetType().GetProperty("State").GetValue(s, null); }
                        catch { return unverifiable; }
                        if (en || (st != null && st.ToString() == "Realtime"))
                        {
                            string nm = "";
                            try { object dn = s.GetType().GetProperty("DisplayName").GetValue(s, null); nm = dn != null ? dn.ToString() : ""; } catch { }
                            return "strategy enabled/realtime on chart: " + (nm.Length > 0 ? nm : "(unnamed)");
                        }
                    }
            }
            catch { return unverifiable; }
            return null;
        }

        // FAILS CLOSED on an UNREADABLE position state. Scoped to the CHART'S OWN Chart Trader account: a
        // position in some other account must not block a display-only data-series switch (that was the
        // "complaining about an unrelated account" bug). If the chart has no Chart Trader account, there is no
        // chart-scoped trading context to protect, so the switch is allowed. Runs on the chart's own UI thread.
        private static string PositionGuardTrip(Window w, object cc, string targetInstr)
        {
            const string unverifiable = "blocked: could not verify open positions (failing closed) — pass --force to override";
            try
            {
                Account acct = ResolveChartAccount(w);
                if (acct == null) return null;   // no Chart Trader account on this chart -> nothing to guard
                string curInstr = CcInstrument(cc);
                var positions = new List<Position>();
                try { lock (acct.Positions) { foreach (Position p in acct.Positions) positions.Add(p); } } catch { return unverifiable; }
                foreach (Position p in positions)
                {
                    string mp = SafeStr(delegate { return p.MarketPosition.ToString(); });
                    if (mp == "Flat" || mp == "") continue;
                    string fn = SafeStr(delegate { return p.Instrument.FullName; });
                    if (fn == curInstr || (!string.IsNullOrEmpty(targetInstr) && fn == targetInstr))
                        return "open " + mp + " position on " + fn + " (account " + SafeStr(delegate { return acct.Name; }) + ")";
                }
            }
            catch { return unverifiable; }
            return null;
        }

        // The account THIS chart trades under — its Chart Trader's selected account — or null if the chart has
        // no Chart Trader / no account (no chart-scoped trading context). Reflection (FindFirst on the chart
        // window -> ChartTrader -> Account); property name hedged. MUST be called on the chart's UI thread.
        private static Account ResolveChartAccount(Window w)
        {
            try
            {
                if (w == null) return null;
                MethodInfo findFirst = w.GetType().GetMethod("FindFirst", new Type[] { typeof(string) });
                object ct = findFirst != null ? findFirst.Invoke(w, new object[] { "ChartWindowChartTraderControl" }) : null;
                if (ct == null) return null;   // no Chart Trader on this chart
                PropertyInfo ap = ct.GetType().GetProperty("Account") ?? ct.GetType().GetProperty("SelectedAccount");
                return ap != null ? ap.GetValue(ct, null) as Account : null;
            }
            catch { return null; }   // unresolvable account -> treat as no trading context (display switch is safe)
        }

        private static string SafeStr(Func<string> f) { try { string s = f(); return s == null ? "" : s; } catch { return ""; } }

        // Bounded dispatch onto a chart window's OWN UI thread; false on timeout (abort the queued op so
        // it cannot run late). A wedged chart thread must not stall the caller (the bridge poller).
        private static bool TryInvoke<T>(Window w, Func<T> func, int timeoutSec, out T result)
        {
            result = default(T);
            try
            {
                System.Windows.Threading.DispatcherOperation op = w.Dispatcher.BeginInvoke(func);
                if (op.Wait(TimeSpan.FromSeconds(timeoutSec)) == System.Windows.Threading.DispatcherOperationStatus.Completed)
                { result = (T)op.Result; return true; }
                try { op.Abort(); } catch { }
            }
            catch { }
            return false;
        }

        private static bool ReadIsBarsLoading(object cc)
        {
            try
            {
                PropertyInfo il = cc.GetType().GetProperty("IsBarsLoading", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                object v = il != null ? il.GetValue(cc, null) : null;
                if (v is bool) return (bool)v;
            }
            catch { }
            return false;
        }
    }
}
