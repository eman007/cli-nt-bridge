// AutoReconnect.cs — NinjaTrader 8 AddOn: auto-reconnect dropped connections + ALERT on dead feeds.
//
// DROP-IN: copy this file into  Documents\NinjaTrader 8\bin\Custom\AddOns\  and compile once
// (NinjaScript Editor -> F5). From then on, every NT8 session it watches your connections.
// No Python; config optional.
//
// WHAT IT DOES — two distinct behaviors, split so RUNNING STRATEGIES ARE NEVER KILLED:
//   * RECONNECT — only when NinjaTrader itself declares the LINK lost (Status == ConnectionLost).
//     After a grace period (NT8's own retry gets first crack) it re-issues
//     Connection.Connect(savedOptions) on the UI thread, with exponential backoff, a max-attempts
//     cap, then a cooldown. This is the long-standing v1 behavior. Config "reconnect": false makes it
//     ALERT-ONLY instead (NT8 Guard's Watch only preset).
//   * ALERT-ONLY — for every stall where the connection still READS Connected:
//       - FEED lost: Status == Connected but PriceStatus == ConnectionLost ("green icon, dead feed").
//       - SILENT stall: the optional watchdog sees no L1 update on ANY watched instrument for
//         watchdogStaleSeconds while the link reads healthy and data flowed recently.
//     These raise a loud alert (NT8 Alerts window + sound + optional webhook for a phone push) and
//     TOUCH NOTHING. Why alert instead of recover: an explicit Disconnect() KILLS running strategies
//     (confirmed live), and Connect() on a still-connected connection errors ("account already
//     exists"). NT8 offers no supported way to recover a stalled-but-connected feed without stopping
//     your strategies — so the human is woken to decide, and the strategies stay enabled.
//     With "autoRecover": true the SILENT-stall case instead runs the AUTO-RECOVERY LADDER below —
//     which still never calls Disconnect()/Connect() on a live connection; the alert stays as rung 4.
//   Any Disconnected state is left alone — user-parked or a login failure (off-hours rejection,
//   expired session). Retrying a refused login is futile + noisy. Playback/replay is excluded.
//
// DATA WATCHDOG (optional): set "watchdogInstruments" (JSON array) to subscribe a live L1
//   MarketData stream per instrument (bid/ask/last — the same movement you'd watch by eye). A stall
//   means NONE of them updated within the window; watching several cuts false positives. A
//   feed-recency gate (data within the last 30 min) keeps a quiet closed market from tripping it.
//
// AUTO-RECOVERY (opt-in: config "autoRecover": true; default false = alert-only): for the SILENT stall
//   only, escalate instead of only alerting. HISTORY: an earlier revision INJECTED a fake ConnectionLost
//   to recover while preserving strategies — ABANDONED 2026-07-08 after it corrupted NT8's account
//   registry into a duplicate ("account already exists", dead connection needing a manual fix). The clean
//   approach cycles the connection for real. Rungs (settle-waited; abandoned the instant the heartbeat resumes):
//     1. RE-SUBSCRIBE — recreate the watchdog's L1 subscriptions. NON-DISRUPTIVE. A watchdog sub created
//                 right after a connection cycle can sit blind while the feed is actually alive (a FALSE
//                 stall — 2026-07-08: a fresh subscribe flooded while the load-time subs went silent);
//                 fresh subs deliver, the heartbeat resumes, done. Nothing is cycled, no strategy touched.
//     2. DISCONNECT — still stalled after re-subscribe => the feed is genuinely dead => Connection.Disconnect()
//                 the SINGLE watched connection. A REAL Disconnect cleanly releases the account (no duplicate
//                 corruption) — but NT8 DISABLES running strategies on it (re-enabling is a separate problem).
//                 Multi-connection => ALERT, never mass-disconnect.
//     3. RECONNECT — once the connection reaches Disconnected (account released, so no "already exists"),
//                 fire Connection.Connect(savedOptions). Feed + data restored; strategies stay disabled.
//     4. ALERT  — still stalled after the reconnect settle: loud human fallback + a cooldown
//                 ("recoveryCooldownSeconds"). Means the feed is down at the source (e.g. broker
//                 maintenance) and no client action restores it — wait for it to return.
//   MARKET-HOURS GATE ("useSessionGate", default ON): during a scheduled CME equity-index close — the
//   daily maintenance break (17:00-18:00 ET), the weekend (Fri 17:00 -> Sun 18:00 ET), or a CME holiday
//   or early close (FeedGuardCalendar: computed by rule for any year, plus the config's extra
//   "holidays") — a quiet feed is EXPECTED, so the silent-stall alert AND the ladder are suppressed (no
//   inject into a window where the broker may refuse the login -> parked). Times are ET wall-clock,
//   DST-correct via TimeZoneInfo. Reconnect-on-real-ConnectionLost is NOT gated. The AutoReconnect.stall
//   test trigger deliberately bypasses the gate so the ladder is still testable at any hour.
//   PLAYBACK GATE ("suppressInPlayback", default ON): while NT8's Playback connection is up, NEITHER
//   silent-stall input describes the live feed. Playback becomes the data provider for its instruments, so
//   the L1 heartbeat measures REPLAY ticks — a paused replay is indistinguishable from a dead feed, and a
//   running one would MASK a real stall — and the live connection is normally Disconnected for the replay,
//   so there is no healthy watched connection for the ladder to recover. That combination raised
//   "NO healthy watched connection ... MANUAL intervention needed" at a human who had deliberately started
//   a replay, every alertRepeatMinutes, re-arming on each AddOn reload (2026-08-09). The alert AND the
//   ladder are suppressed while Playback is connected; reconnect-on-ConnectionLost is NOT gated.
//
// FILE-DROP TRIGGERS (the file is DELETED before acting so a crash can never loop it). The developer
//   triggers of the build-out (inject, poke, probestate, grid*) were removed for NT8 Guard (2026-10):
//   diagnostics, never part of normal use, and some of them acted on live strategies.
//     drop  Documents\NinjaTrader 8\AutoReconnect.stall   -> force the stall flag for N seconds (file
//        content, default 120) to exercise the alert path — or the FULL ladder when autoRecover is on —
//        on a healthy feed without pulling the network.
//     drop  Documents\NinjaTrader 8\AutoReconnect.deafen  -> swallow REAL L1 for N seconds (file content,
//        default 150) so the feed looks genuinely dead. THIS is the honest ladder test: staleness accrues
//        naturally and the real classifier decides. Prefer it over .stall, which forces the stalled flag
//        and so skips the snapshot logic entirely — that blind spot is how an unreachable rung 2 shipped
//        "live-validated" and self-cleared 399 real stalls (2026-07-15..27) without ever escalating.
//     drop  Documents\NinjaTrader 8\AutoReconnect.reload  -> re-read AutoReconnect.config.json now (NT8 Guard
//        writes the file, then drops this). A changed watch list re-subscribes at once, or when a running
//        recovery ends; a key DELETED from the file keeps its value until NinjaTrader restarts.
//
// ALERTS: NT8 Alerts window (LogLevel.Alert) + a sound (<install>\sounds\<alertSound>, default
//   Alert1.wav; "" = silent) + optional JSON POST to "webhookUrl" (fields: source/event/message/
//   utcTime — point it at a Telegram bot / push service so it can wake you). An active condition
//   re-alerts every alertRepeatMinutes (default 5); a one-time "recovered" webhook fires on clear.
//
// OPTIONAL CONFIG:  Documents\NinjaTrader 8\AutoReconnect.config.json  — scope with "connections" /
//   "exclude", tune timings, arm the watchdog, set alerts. See ReadConfig + docs/AutoReconnect.md.
//   Without it: watches ALL except Playback, grace 20s, 5 attempts, 30-min cooldown, watchdog OFF.
//
// LOG: actions are appended to  Documents\NinjaTrader 8\AutoReconnect.log  (rotated at 5 MB, one
//   previous copy kept as AutoReconnect.log.1).
//
// EVENTS + HEARTBEAT (for NT8 Guard): every decision that matters is also written as one JSON line to
//   Documents\NinjaTrader 8\NT8Guard\feedguard-events.jsonl  ({utc, component:"feedguard", kind,
//   severity, text, data}; kinds listed in docs\AutoReconnect.md). Readers act on `kind`, never on log
//   wording - a reworded log line once broke a reader without a sound. AutoReconnect.heartbeat.json is
//   rewritten every tick (see WriteGuardianHeartbeat).
//
// ANTI-LOCKUP (NinjaTrader best practice): every lifecycle override is try/catch-wrapped; the timer
//   AND the watchdog MarketData subscriptions are torn down on Terminated; polling is wall-clock; the
//   status + market-data handlers are NAMED methods unsubscribed on Terminated; connection actions are
//   marshalled to the UI dispatcher ASYNC (BeginInvoke — never a sync Invoke, AGENTS gotcha #17);
//   webhooks post on a background thread and never block the guardian.
#region Using declarations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using NinjaTrader.Adapter;
using NinjaTrader.Cbi;
using NinjaTrader.Core;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns
{
    public class AutoReconnect : AddOnBase
    {
        // --- tunables (defaults; overridable via AutoReconnect.config.json) ---
        private double _graceSeconds = 20.0;       // wait after a drop before the first reconnect (let NT8 retry)
        private double _intervalSeconds = 10.0;    // guardian tick cadence (wall-clock)
        private int    _maxAttempts = 5;           // attempts before a cooldown
        private double _cooldownSeconds = 1800.0;  // pause after maxAttempts, then reset + retry (never permanent give-up)
        private double _backoffBase = 15.0;        // backoff seconds = base * 2^(n-1), capped
        private double _backoffCap = 300.0;
        // config "reconnect" (NT8 Guard: "Reconnect dropped connections"). false = a dropped link is only
        // ALERTED, never reconnected - the Watch only preset. Default true: the behaviour since v1.
        private bool _reconnect = true;
        // The config's lists (_allow, _feedOwners, _exclude, _watchdogInstruments) are REPLACED whole by
        // ReadConfig, never edited in place: NinjaTrader's status thread reads _exclude (OnConnStatus) while
        // an AutoReconnect.reload re-reads the config on the guardian thread.
        private HashSet<string> _allow = new HashSet<string>();  // empty = watch ALL connections
        // config "feedOwnerConnections" — which connection(s) may be the recovery ladder's TARGET. Empty =
        // any watched one (legacy). Rung 2 refuses to act unless exactly ONE healthy candidate exists, so on
        // a box where a prop connection is up alongside the data-feed owner the ladder silently degraded to
        // alert-only (observed on both boxes: a prop-firm connection healthy next to the broker's feed). Scoping via
        // "connections" would fix that but ALSO narrows reconnect-on-ConnectionLost (same _allow gate at the
        // _dropClass loop), dropping the prop connections' reconnect backstop. This key scopes ONLY the
        // ladder, leaving watch/reconnect coverage exactly as it was.
        private HashSet<string> _feedOwners = new HashSet<string>();
        // never auto-reconnect these. Playback/replay is never something you auto-connect;
        // config "exclude" adds more. Case-insensitive.
        private HashSet<string> _exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Playback" };

        private readonly object _gate = new object();
        private Timer _timer;
        private string _logPath;

        // connection name -> last classification:
        //   "inadvertent" = Status ConnectionLost (LINK dropped)      -> reconnect state machine
        //   "feedlost"    = Status Connected, PriceStatus lost (FEED) -> ALERT-ONLY, never touched
        //   "parked"      = Disconnected (user OR connect-failure)    -> never touched
        //   "connected"   = healthy.
        // Written by the ConnectionStatusUpdate event (arbitrary threads) -> ConcurrentDictionary.
        private readonly ConcurrentDictionary<string, string> _dropClass = new ConcurrentDictionary<string, string>();

        // recovery bookkeeping (touched only by the guardian tick, under _gate -> plain dicts ok)
        private readonly Dictionary<string, DateTime> _droppedSince = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _attempts = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _nextAt = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _cooldownUntil = new Dictionary<string, DateTime>();

        // Alerts (guardian-thread only, under _gate): active alert key -> last time it fired.
        // Keys: "silent-stall", "feedlost:<conn>", "reconnect:<conn>".
        private readonly Dictionary<string, DateTime> _alertLast = new Dictionary<string, DateTime>();
        private double _alertRepeatMinutes = 5.0;   // re-alert cadence while a condition persists
        private string _alertSound = "Alert1.wav";  // in <install>\sounds (or absolute path); "" = silent
        private string _webhookUrl = "";            // optional POST target (phone push); "" = off
        private string _webhookFormat = "json";     // config "webhookFormat": "json" (default) | "text" (ntfy et al)

        // Build provenance. tools/deploy-nt8.ps1 rewrites this line IN THE DEPLOYED COPY ONLY (short commit
        // + UTC date); the repo keeps "dev". So a load banner reading build=dev means that file was
        // hand-copied, not deployed through the script — which is itself the thing you want to know when a
        // remote box is running something you can't account for.
        private const string BuildStamp = "dev";

        // The add-on's version, in the load banner, the heartbeat and the events. NT8 Guard compares it with
        // its own to tell you when the add-on inside NinjaTrader is older than the app (import the new one).
        // 5.0.0 = the first NT8 Guard build: events file, holidays by rule, NinjaTrader's own roll dates,
        // the reload trigger; developer triggers removed.
        internal const string AddOnVersion = "5.0.0";

        // Data-flow watchdog (catches a frozen feed where NT8 fires nothing and PriceStatus stays Connected).
        private List<string> _watchdogInstruments = new List<string>();   // empty => watchdog OFF
        // The watch list the current subscriptions were made from. After a reload the guardian compares the
        // config against it (FeedGuardReload.WatchPlan) and re-subscribes when they differ.
        private List<string> _watchdogSubscribedFrom = new List<string>();
        // ! A CONTRACT MONTH IN THE CONFIG IS A TIME BOMB. 2026-09-18: watchdogInstruments still said
        //   "MNQ 09-26"/"MES 09-26" the morning those contracts expired. No ticks on a dead contract read
        //   as "silent stall", the ladder disconnected every ~110s all session - 24 clean disconnects
        //   against 2 real drops - each one disabled every strategy, and when the operator restarted
        //   NinjaTrader five disabled rows were dropped from the grid. The feed was fine the whole time.
        //   Write the config as "MNQ ##-##" and the front month is resolved here - from NinjaTrader's own
        //   roll dates, else the CME quarterly rule (Mar/Jun/Sep/Dec, expiry the 3rd Friday, roll
        //   rollDaysBeforeExpiry before it) - and re-checked once a day, so it rolls in place without a
        //   restart (FeedGuardRollover); a CONCRETE month is alerted from 7 days before its expiry month.
        //   Instrument.GetInstrument does NOT resolve "##-##" itself - measured 2026-09-18: "MNQ 12-26"
        //   returns a price, "MNQ ##-##" returns nothing - which is why this is done by hand.
        private readonly List<string> _watchdogResolved = new List<string>();      // concrete names actually subscribed
        private int    _rollDaysBeforeExpiry = 8;                                   // CME convention for equity index futures
        private DateTime _lastRollCheckDate = DateTime.MinValue;
        private DateTime _lastExpiryCheckDate = DateTime.MinValue;
        // How the watch list's "##-##" entries were last resolved, for the heartbeat: "nt8" = NinjaTrader's
        // own roll dates, "rule" = the quarterly fallback (its dates were unreadable or out of date),
        // "n/a" = the watch list holds concrete contracts only.
        private string _rolloverSource = "n/a";
        // Self-check (spec section 9): can this NinjaTrader's Strategies grid be reached the way the re-enable
        // after a recovery reaches it? "ok" / "unavailable" / "unknown" (not checked yet). Set once at load.
        private string _reEnableCheck = "unknown";
        private double _watchdogStaleSeconds = 75.0;
        private const double _recentActiveMinutes = 30.0;  // market-open proxy: only flag if data flowed this recently
        private readonly List<MarketData> _watchdogSubs = new List<MarketData>();
        private long _lastDataTicks;                       // UtcNow.Ticks of the last watched L1 update (0 = none yet); Interlocked
        private int _firstTickLogged;                      // one-time "heartbeat live" log flag
        private bool _providerLogged;                      // one-time "which connection feeds us" log (guardian thread)
        private string _feedProvidersText = "";            // heartbeat "feedProviders", refreshed once a minute by Tick
        private volatile bool _simFeedOnly;                // every watched instrument served by the Simulated Data Feed (refreshed with _feedProvidersText)
        private DateTime _providersAt = DateTime.MinValue;
        private DateTime _watchdogDiagAt = DateTime.MinValue;   // hourly "alive" diagnostic timestamp

        // AUTO-RECOVERY for the SILENT stall (opt-in via config "autoRecover"; default OFF = alert-only).
        // Ladder: (1) RE-SUBSCRIBE the watchdog — non-disruptive; fixes a stale watchdog subscription / a
        // FALSE stall where the feed is actually alive (2026-07-08: a fresh subscribe flooded while the
        // load-time subs sat blind). (2) still stalled => the feed is genuinely dead => DISCONNECT + Connect
        // the connection: a CLEAN cycle (real Disconnect releases the account, so NO duplicate-account
        // corruption like the abandoned inject caused) — but NT8 DISABLES running strategies on Disconnect
        // (re-enabling them is a separate problem). Only a SINGLE healthy watched connection is auto-cycled;
        // multi-connection => alert, never mass-disconnect. Guardian-thread state only (under _gate).
        private bool   _autoRecover = false;
        private double _resubSettleSeconds = 20.0;         // re-subscribe -> decide (heartbeat back = false stall, fixed)
        private double _disconnectTimeoutSeconds = 30.0;   // wait for the conn to reach Disconnected before Connect()
        private double _injectSettleSeconds = 45.0;        // post-reconnect settle before declaring failure (config: injectSettleSeconds)
        private double _recoveryCooldownSeconds = 600.0;   // after the ALERT rung, before the ladder may run again
        private int    _rung;                              // 0 idle, 1 re-subscribed, 2 disconnecting, 3 reconnecting
        private string _ladderTargetSource = "";            // how the ladder target was chosen, for the log
        private DateTime _rungAt = DateTime.MinValue;
        private DateTime _stallSince = DateTime.MinValue;  // active recovery episode start (MinValue = none)
        private DateTime _ladderCooldownUntil = DateTime.MinValue;
        private string _ladderConn = "";                   // connection the active episode targets
        private DateTime _forceStallUntil = DateTime.MinValue;   // AutoReconnect.stall test trigger window
        // A fresh MarketData subscription delivers a cached SNAPSHOT immediately — alive feed or not. Rung 1
        // IS a re-subscribe, so that snapshot re-stamped the heartbeat ~10s in and cleared the very stall the
        // ladder was climbing, before the 20s rung-2 settle could elapse: rung 2 was UNREACHABLE and the
        // watchdog self-cleared 399 real episodes (2026-07-15..27), one of them 3s AFTER the connection had
        // reached Disconnected. Updates inside this post-subscribe window no longer count as proof of life;
        // only data still arriving after it does. Applied to the ladder's RestartWatchdog only — the load-time
        // StartWatchdog keeps stamping on its first update, which is what arms the watchdog at all.
        private long   _resubGraceUntilTicks;                    // ignore snapshot echoes until this UTC tick
        private double _resubSnapshotGraceMs = 1500.0;           // config "resubSnapshotGraceMs"
        private long   _updatesSinceResub;                       // post-snapshot L1 updates (0 = feed truly dead)
        private long   _deafenUntilTicks;                        // AutoReconnect.deafen test trigger: fake a dead feed
        private double _gridInvokeTimeoutSeconds = 20.0;         // config "gridInvokeTimeoutSeconds": cap on the
                                                                 // synchronous UI-dispatcher wait (see WithStrategiesGrid)
        // Battle 2: strategies enabled at the moment of a recovery Disconnect (which disables them). Re-enabled
        // by clicking each row's Enabled checkbox once the feed recovers (LadderReset). Union-accumulated so a
        // failed episode doesn't lose names; cleared on successful re-enable.
        private bool   _reEnableAfterRecover = true;             // config "reEnableAfterRecover"
        private double _reEnableSettleSeconds = 12.0;            // config "reEnableSettleSeconds": feed-resume -> re-enable
        private DateTime _reEnableAt = DateTime.MinValue;        // scheduled deferred re-enable time (MinValue = none)
        private readonly List<string> _savedEnabled = new List<string>();

        // MARKET-HOURS GATE: during a scheduled CME close a quiet feed is EXPECTED, not a stall, so the
        // silent-stall alert + recovery ladder are suppressed. Injecting into a maintenance/weekend window
        // can force a login the broker refuses -> parked until a human fixes it (observed live 2026-07-03) —
        // exactly the failure an UNATTENDED box can't tolerate. Times are ET wall-clock (DST via
        // TimeZoneInfo). Reconnect-on-real-ConnectionLost is intentionally NOT gated.
        private bool   _useSessionGate = true;                 // config "useSessionGate"
        private string _marketTzId = "Eastern Standard Time";  // Windows TZ id (observes EDT/EST)
        private TimeZoneInfo _marketTz;                        // resolved at load from _marketTzId (null -> DST-rule fallback)
        private int    _breakStartMin = 17 * 60;               // daily maintenance break start, ET minutes-of-day (17:00)
        private int    _breakEndMin   = 18 * 60;               // ...break end / Sunday open (18:00)
        private int    _weekCloseMin  = 17 * 60;               // Friday weekly close, ET (17:00)
        private int    _weekOpenMin   = 18 * 60;               // Sunday weekly open, ET (18:00)
        // CME holidays and early closes come from FeedGuardCalendar's rules, for any year. These are the
        // config's EXTRA "holidays" on top: "yyyy-MM-dd" (a full close) or "yyyy-MM-dd HH:mm" (closed from
        // that ET time). Replaced whole on every config read, never edited in place.
        private List<FeedGuardClosure> _extraClosed = new List<FeedGuardClosure>();
        private DateTime _sessionDiagAt = DateTime.MinValue;   // throttle the "scheduled-closed" diagnostic line

        // PLAYBACK GATE: a replay makes both silent-stall inputs meaningless. NT8's Playback connection
        // becomes the data provider for its instruments, so the watchdog's L1 heartbeat measures REPLAY
        // ticks (pause the replay and the feed reads dead; run it and it would mask a real stall), while
        // the live connection is normally Disconnected for the duration — leaving no healthy watched
        // connection for the ladder. Result: the "NO healthy watched connection ... MANUAL intervention
        // needed" alert fired repeatedly at a human who had deliberately started a replay (2026-08-09:
        // Broker-Demo disconnected 00:03:41 ET, Playback up 00:03:52, first alert 00:07:50, then every
        // repeat window plus a fresh one 90s after each AddOn reload). Suppressed like a scheduled close.
        private bool   _suppressInPlayback = true;              // config "suppressInPlayback"
        private DateTime _playbackDiagAt = DateTime.MinValue;   // throttle the "playback active" diagnostic line

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults)
                {
                    Name = "AutoReconnect";
                }
                else if (State == State.Configure)
                {
                    Init();
                }
                else if (State == State.Terminated)
                {
                    try { Connection.ConnectionStatusUpdate -= OnConnStatus; } catch { }
                    StopWatchdog();
                    try { if (_timer != null) _timer.Dispose(); } catch { }
                    _timer = null;
                }
            }
            catch (Exception ex) { LogSafe("OnStateChange: " + ex.Message); }
        }

        private void Init()
        {
            _logPath = Path.Combine(Globals.UserDataDir, "AutoReconnect.log");
            ReadConfig();
            ResolveMarketTz();
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }   // https webhooks
            Connection.ConnectionStatusUpdate += OnConnStatus;
            // Seed currently-live connections as "connected" so they're never mistaken for a drop.
            try
            {
                lock (Connection.Connections)
                    foreach (Connection c in Connection.Connections)
                    {
                        string nm = NameOf(c);
                        if (!IsExcluded(nm)) _dropClass[nm] = "connected";
                    }
            }
            catch { }
            LogSafe("AutoReconnect build=" + BuildStamp + " version=" + AddOnVersion + " loaded ("
                    + (_reconnect ? "reconnect on ConnectionLost; " : "ALERT-ONLY on ConnectionLost (reconnect off); ")
                    + (_autoRecover ? "AUTO-RECOVERY on silent stalls (re-subscribe -> clean disconnect+reconnect -> alert)" : "ALERT-ONLY on connected-but-stalled feeds") + ")."
                    + " watching=" + (_allow.Count == 0 ? "ALL" : string.Join(",", _allow))
                    + " ladderTarget=" + (_feedOwners.Count == 0 ? "any-watched" : string.Join(",", _feedOwners))
                    + " grace=" + _graceSeconds + "s interval=" + _intervalSeconds + "s maxAttempts=" + _maxAttempts
                    + " cooldown=" + _cooldownSeconds + "s webhook=" + (_webhookUrl.Length > 0 ? "on(" + _webhookFormat + ")" : "off")
                    + " snapshotGrace=" + (int)_resubSnapshotGraceMs + "ms"
                    + " autoRecover=" + (_autoRecover ? "ON (resubSettle=" + _resubSettleSeconds + "s disconnectTimeout=" + _disconnectTimeoutSeconds
                                                        + "s reconnectSettle=" + _injectSettleSeconds + "s recoveryCooldown=" + _recoveryCooldownSeconds
                                                        + "s; DISCONNECT disables strategies; single-conn only)"
                                                      : "off")
                    + " reEnableAfterRecover=" + (_reEnableAfterRecover ? "ON (settle=" + (int)_reEnableSettleSeconds + "s, clicks Enabled checkbox)" : "off"));
            if (_useSessionGate)
                LogSafe("session gate ON: tz='" + _marketTzId + "'" + (_marketTz == null ? " (NOT FOUND -> DST-rule fallback)" : "")
                        + " dailyBreak=" + Hhmm(_breakStartMin) + "-" + Hhmm(_breakEndMin)
                        + " ET, weekend Fri " + Hhmm(_weekCloseMin) + " -> Sun " + Hhmm(_weekOpenMin) + " ET, holidays=CME rules + "
                        + _extraClosed.Count + " extra date(s), next: " + FeedGuardCalendar.Describe(FeedGuardCalendar.Next(ToEastern(DateTime.UtcNow), _extraClosed))
                        + "; RIGHT NOW scheduledClosed=" + IsScheduledClosed(DateTime.UtcNow));
            else
                LogSafe("session gate OFF (silent-stall alert/ladder run 24/7 — will misfire at scheduled closes)");
            LogSafe("playback gate " + (_suppressInPlayback
                        ? "ON: silent-stall alert + ladder suppressed while NT8's Playback connection is up"
                        : "OFF: silent-stall alert + ladder run during replays too — a paused replay will misfire")
                    + "; RIGHT NOW playbackActive=" + IsPlaybackActive());
            long ms = (long)(_intervalSeconds * 1000.0);
            _timer = new Timer(delegate { Tick(); }, null, ms, ms);
            StartWatchdog();
            _reEnableCheck = CheckReEnableReach();
            LogSafe("self-checks: feed provider lookup " + (_mdConnProp != null ? "ok" : "UNAVAILABLE (the ladder targets the config's connection)")
                    + ", contract months from " + RolloverSourceText() + ", re-enable after a recovery " + _reEnableCheck);
            if (_reEnableCheck != "ok" && _reEnableAfterRecover)
                AlertRaise("selfcheck:reenable", "This NinjaTrader version does not offer what the feed guard uses to switch"
                           + " strategies back on after a recovery disconnect. A recovery will still restore the feed, but the"
                           + " strategies it switches off stay off - re-enable them by hand.", DateTime.UtcNow);
            Emit("feed.loaded", "info", "Feed guard " + AddOnVersion + " started (build " + BuildStamp + ")",
                 "version", AddOnVersion, "build", BuildStamp, "reason", "start", "reEnable", _reEnableCheck);
        }

        // Self-check for the re-enable after a recovery (spec section 9): does this NinjaTrader still have
        // what WithStrategiesGrid / GridEntries / ToggleEnableCheckbox use - ControlCenter.Instance, its
        // MainTabControl, the StrategiesGrid's row source, ButtonBase.OnClick? Types and members only:
        // nothing on the UI is touched. Pinned against the installed DLL by tools\Test-FeedGuardNt8Api.ps1.
        private static string CheckReEnableReach()
        {
            try
            {
                Type cc = Type.GetType("NinjaTrader.Gui.ControlCenter, NinjaTrader.Gui");
                if (cc == null) return "unavailable";
                System.Reflection.BindingFlags anyStatic = System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
                bool instance = cc.GetProperty("Instance", anyStatic) != null || cc.GetProperty("Current", anyStatic) != null;
                bool tabs = cc.GetProperty("MainTabControl", AnyInstance) != null || cc.GetField("MainTabControl", AnyInstance) != null;
                Type grid = cc.Assembly.GetType("NinjaTrader.Gui.NinjaScript.StrategiesGrid");
                bool rows = grid != null && (grid.GetField("source", AnyInstance) != null || grid.GetProperty("source", AnyInstance) != null);
                bool click = typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null;
                return instance && tabs && rows && click ? "ok" : "unavailable";
            }
            catch { return "unavailable"; }
        }

        // Classify every connection status change the instant it happens. Fires on arbitrary
        // threads, so it only touches the ConcurrentDictionary + logs (never blocks).
        private void OnConnStatus(object sender, ConnectionStatusEventArgs e)
        {
            try
            {
                if (e == null || e.Connection == null) return;
                string name = NameOf(e.Connection);
                if (IsExcluded(name)) return;                    // Playback/excluded -> never tracked
                ConnectionStatus s = e.Status;
                ConnectionStatus ps = e.PriceStatus;             // the DATA-feed link (can drop while Status stays Connected)
                string before;
                _dropClass.TryGetValue(name, out before);
                if (s == ConnectionStatus.Disconnected)
                    _dropClass[name] = "parked";                 // user park OR connect failure -> never auto-touched
                else if (s == ConnectionStatus.ConnectionLost)
                    _dropClass[name] = "inadvertent";            // LINK lost -> reconnect state machine
                else if (s == ConnectionStatus.Connected && ps == ConnectionStatus.ConnectionLost)
                    _dropClass[name] = "feedlost";               // link up, DATA feed dead -> ALERT-ONLY (never touched)
                else if (s == ConnectionStatus.Connected && ps == ConnectionStatus.Connected)
                    _dropClass[name] = "connected";
                // Connecting / Disconnecting (or price still Connecting): transient -> keep prior class.
                LogSafe("status: " + name + " -> " + s + "/" + ps + " (err=" + e.Error + ")");
                string after;
                _dropClass.TryGetValue(name, out after);
                if (after != before)
                    Emit("feed.connection", after == "inadvertent" || after == "feedlost" ? "warn" : "info",
                         "'" + name + "' is now " + s + "/" + ps, "connection", name, "state", after ?? "");
                // NinjaTrader switches running strategies off by itself when the price feed drops under a
                // connected link (the 2026-09-09 night). The overwatch reads this to put them back.
                if (s == ConnectionStatus.Connected && ps == ConnectionStatus.ConnectionLost)
                    Emit("feed.strategies-disabled", "warn", "'" + name + "' lost its price feed (Connected/ConnectionLost);"
                         + " NinjaTrader switches its strategies off", "connection", name, "reason", "price-feed-lost");
            }
            catch (Exception ex) { LogSafe("OnConnStatus: " + ex.Message); }
        }

        // Guardian loop on the wall-clock timer thread (re-entrancy guarded).
        private void Tick()
        {
            if (!Monitor.TryEnter(_gate)) return;
            try
            {
                DateTime now = DateTime.UtcNow;

                CheckManualTriggers(now);   // file-drop triggers (stall / deafen); see header
                CheckContractRoll();        // once a day: has a "##-##" watched contract rolled? re-subscribe if so
                CheckLiteralExpiry(now);    // once a day: is a CONCRETE watched contract about to expire? alert
                ApplyWatchListChange();     // a reload changed the watch list: re-subscribe once no recovery runs
                if (_watchdogSubs.Count > 0 && (now - _providersAt).TotalSeconds >= 60.0)
                {
                    _providersAt = now;     // which connection serves the watch list, for the heartbeat
                    _feedProvidersText = string.Join(",", ResolveFeedProviders());
                }
                // Every tick (10 s), not once a minute: someone who just connected the Simulated Data Feed for
                // a weekend fire drill should not wait a minute for the calendar to step aside.
                if (_watchdogSubs.Count > 0) RefreshSimFeedOnly();

                // live health (Status + PriceStatus) per connection; a fully-dropped one is ABSENT here
                var liveHealth = new Dictionary<string, string>();
                try
                {
                    lock (Connection.Connections)
                        foreach (Connection c in Connection.Connections)
                        {
                            string nm = NameOf(c);
                            if (nm.Length == 0) continue;
                            string h = Health(c), prev;   // a same-name husk can linger after a recovery cycle -> keep the healthiest
                            if (!liveHealth.TryGetValue(nm, out prev) || HealthRank(h) > HealthRank(prev)) liveHealth[nm] = h;
                        }
                }
                catch { }

                // ===== watchdog: silent-stall detection -> ALERT-ONLY (never touches a connection) =====
                // A truly-silent feed freeze fires NO event and PriceStatus stays Connected. If nothing we
                // watch has updated beyond the threshold while a watched link still reads healthy (and data
                // flowed recently), wake the human. Auto-recovery is deliberately impossible here: an explicit
                // Disconnect() kills running strategies, and Connect() on a live connection errors.
                bool stalled = false;
                double staleSec = 0;
                if (_watchdogSubs.Count > 0)
                {
                    long lt = Interlocked.Read(ref _lastDataTicks);
                    if (lt != 0)   // never flag before the first update ever arrives
                    {
                        DateTime last = new DateTime(lt, DateTimeKind.Utc);
                        staleSec = (now - last).TotalSeconds;
                        // ! A FEED THAT IS ALREADY LONG DEAD MUST STILL COUNT AS A STALL.
                        //   IsMarketLikelyOpen is a DATA-RECENCY proxy (<= _recentActiveMinutes), so past
                        //   that window it answers false forever and the ladder can never even start. That
                        //   is not hypothetical: the desktop slept 2026-09-23 06:17:29Z -> 10:15:32Z (a
                        //   Windows Update wake timer), and on resume the staleness was ALREADY 4 hours.
                        //   The watchdog then sat at rung 0 logging "alive; last L1 update 25089s ago" once
                        //   an hour while every chart was frozen and the market was open - 7.8 hours, no
                        //   ladder, no alert, until a human noticed. The recency proxy exists to avoid
                        //   flagging a clean market close, but the session gate answers that question from
                        //   the CLOCK and needs no data at all. Where it is on, prefer it.
                        bool openByClock = _useSessionGate && !IsScheduledClosed(now);
                        stalled = ShouldFlagStall(staleSec, _watchdogStaleSeconds,
                                                  IsMarketLikelyOpen(now, last), openByClock);
                        // Once per load, as soon as data has flowed: which connection NT8 is serving the watched
                        // instruments from. That is the connection the ladder will cycle on a stall, so it belongs
                        // in the log BEFORE the stall, not discovered after it (2026-09-29).
                        if (!_providerLogged && Interlocked.CompareExchange(ref _firstTickLogged, 1, 1) == 1)
                        {
                            _providerLogged = true;
                            List<string> fp = ResolveFeedProviders();
                            LogSafe("watchdog: feed provider per NT8: [" + (fp.Count > 0 ? string.Join(",", fp)
                                    : "unknown - ladder uses config") + "] - the connection a stall will cycle");
                        }
                        // hourly alive line so heartbeat reliability is provable from the log
                        if ((now - _watchdogDiagAt).TotalMinutes >= 60.0)
                        {
                            _watchdogDiagAt = now;
                            List<string> feedNow = ResolveFeedProviders();
                            LogSafe("watchdog: alive; last L1 update " + (int)staleSec + "s ago; feed provider per NT8: ["
                                    + (feedNow.Count > 0 ? string.Join(",", feedNow) : "unknown - ladder uses config") + "]");
                        }
                    }
                }
                if (now < _forceStallUntil)   // AutoReconnect.stall test trigger: exercise the stall path / ladder on a healthy feed
                {
                    stalled = true;
                    staleSec = Math.Max(staleSec, _watchdogStaleSeconds);
                }
                // Market-hours gate: a quiet feed during a scheduled CME close is EXPECTED, not a stall.
                // Suppress the alert AND the ladder (never inject into a maintenance/weekend/holiday window).
                // The force-stall test trigger (now < _forceStallUntil) deliberately bypasses the gate so the
                // ladder can still be exercised on demand at any hour.
                // A new stall on what was the Simulated Data Feed a minute ago: re-read the providers first, so a
                // switch to a broker since then puts the calendar back before it decides (final review I4).
                if (stalled && _rung == 0 && _simFeedOnly) RefreshSimFeedOnly();
                bool scheduledClosed = _useSessionGate && IsScheduledClosed(now);
                if (stalled && now >= _forceStallUntil && scheduledClosed)
                {
                    if ((now - _sessionDiagAt).TotalMinutes >= 30.0)
                    {
                        _sessionDiagAt = now;
                        LogSafe("session: market scheduled-closed (ET) — silent-stall watchdog + auto-recovery suppressed");
                    }
                    AlertClear("silent-stall", now);
                    LadderResetQuiet();   // end any in-flight episode without a bogus "recovered" log
                    stalled = false;
                }
                // Playback gate: during a replay the heartbeat carries REPLAY ticks and the live connection is
                // normally Disconnected, so a paused/finished replay reads exactly like a dead feed with nothing
                // to recover -> a "MANUAL intervention needed" alert at the human who started the replay on
                // purpose. Suppress the alert AND the ladder while Playback is up (see _suppressInPlayback).
                // Only evaluated while `stalled`, so a live box never pays for the lookup; the force-stall test
                // trigger bypasses it, same as the session gate.
                if (stalled && now >= _forceStallUntil && _suppressInPlayback && IsPlaybackActive())
                {
                    if ((now - _playbackDiagAt).TotalMinutes >= 30.0)
                    {
                        _playbackDiagAt = now;
                        LogSafe("playback: NT8 Playback connection is up — silent-stall watchdog + auto-recovery"
                                + " suppressed (the L1 heartbeat is replay data, not the live feed)");
                    }
                    AlertClear("silent-stall", now);
                    LadderResetQuiet();
                    stalled = false;
                }
                if (stalled)
                {
                    // WHICH CONNECTION TO CYCLE: ask NinjaTrader, don't trust the config.
                    // The ladder used to target the configured feedOwnerConnections. On the desktop that is
                    // Broker-Demo - but NT8 was actually serving MNQ/MES from DataVendor, and DataVendor's feed is
                    // the one a machine sleep wedges. 2026-09-29: seven full ladders disconnected and
                    // reconnected a HEALTHY Broker-Demo between 05:49 and 07:08 PT, each ending "feed
                    // likely down at the source" (false), and DataVendor was never touched. The operator
                    // disconnected DataVendor at 07:16:28 and L1 resumed at 07:16:29 - one second later.
                    // Instrument.MarketDataConnection is NT8's own answer; the config is now only a fallback
                    // for when that cannot be read.
                    List<string> providers = ResolveFeedProviders();
                    string targetSource;
                    var healthyWatched = SelectLadderTargets(providers, liveHealth, _feedOwners, _allow,
                                                             IsExcluded, out targetSource);
                    _ladderTargetSource = targetSource
                        + (providers.Count > 0 ? " (NT8 feed provider: " + string.Join(",", providers) + ")" : "");
                    // `|| _rung >= 2`: a reload that switches auto-recovery off mid-cycle must still reconnect
                    // what rung 2 has already disconnected - never strand a connection in Disconnected.
                    if (_autoRecover || _rung >= 2)
                    {
                        // start an episode only when something healthy exists; once started (rung>0), keep
                        // progressing on the settle timers even while the connection is mid disconnect/reconnect.
                        if (healthyWatched.Count > 0 || _rung > 0)
                            RunRecoveryLadder(healthyWatched, staleSec, now);
                        else
                            // NO healthy candidate and no episode in flight => the ladder cannot run. This
                            // used to fall through to NOTHING: the alert below sits in an `else if
                            // (!_autoRecover)`, so with auto-recovery ON a stall in this state produced no
                            // ladder, no alert, no log. The 24/7 box sat exactly here for 3h16m on
                            // 2026-07-28 (hourly "last L1 update 8954s ago") with three strategies enabled
                            // on a stranded connection, silent until a manual NT8 restart.
                            AlertRaise("silent-stall", "SILENT FEED STALL: no L1 update from ["
                                       + string.Join(",", _watchdogResolved) + "] for " + (int)staleSec
                                       + "s and NO healthy watched connection to recover — the auto-recovery"
                                       + " ladder cannot run. Strategies may be enabled on a dead connection."
                                       + " MANUAL intervention needed.", now);
                    }
                    else if (healthyWatched.Count > 0)
                    {
                        AlertRaise("silent-stall", "SILENT FEED STALL: no L1 update from ["
                                   + string.Join(",", _watchdogResolved) + "] for " + (int)staleSec
                                   + "s while the connection reads Connected. Strategies are running BLIND on stale data."
                                   + " Auto-recovery is OFF — reconnect manually.", now);
                    }
                }
                else
                {
                    // `stalled` is false for two OPPOSITE reasons: the heartbeat genuinely returned, or
                    // IsMarketLikelyOpen's recency window aged out while the feed stayed dead (staleness
                    // past _recentActiveMinutes). Treating the second as recovery clears the alert, claims
                    // "heartbeat RESUMED" and re-enables strategies onto a DEAD feed. That was unreachable
                    // while the re-subscribe snapshot kept the clock permanently fresh; the snapshot fix
                    // (2026-07-27) makes it reachable, so the two are separated here.
                    // A ladder at rung >= 2 has ALREADY fired Disconnect(). Abandoning it here strands the
                    // connection in Disconnected — a state the reconnect machine deliberately ignores as
                    // "user-parked" — and the re-enable then puts strategies back onto a dead connection.
                    // The heartbeat CAN resume mid-cycle for a phantom reason (a subscribe echo did exactly
                    // that on the 24/7 box, 2026-07-28 18:52 "heartbeat RESUMED during DISCONNECT"), which
                    // stranded it for 3h16m until a manual NT8 restart. Finish the cycle we started; once the
                    // connection is healthy again this falls through to the normal reset + re-enable below.
                    if (_rung >= 2 && _ladderConn.Length > 0 && HealthOf(_ladderConn) != "healthy")
                    {
                        // conns is only read at rung 0 (to pick the target); past rung 1 the episode already
                        // owns _ladderConn, so an empty list is safe here — healthyWatched is scoped to the
                        // stalled branch and by definition would be empty for a mid-cycle connection anyway.
                        RunRecoveryLadder(new List<string>(), staleSec, now);
                    }
                    else if (staleSec <= _watchdogStaleSeconds)
                    {
                        AlertClear("silent-stall", now);
                        LadderReset(now);   // heartbeat resumed -> log which rung recovered it + clear the episode
                    }
                    else if (_stallSince != DateTime.MinValue || _rung > 0)
                    {
                        LogSafe("recover: GIVING UP — still no L1 after " + (int)staleSec + "s, past the "
                                + (int)_recentActiveMinutes + "-min recency window. Feed is still dead; the alert"
                                + " STAYS up and no strategies are re-enabled (not a recovery).");
                        LadderResetQuiet();   // end the episode WITHOUT a bogus "recovered" claim
                    }
                }

                MaybeReEnableAfterRecover(now);   // deferred: after a recovery cycle settles, re-enable disabled strategies

                foreach (KeyValuePair<string, string> kv in _dropClass)
                {
                    string name = kv.Key;
                    if (IsExcluded(name)) continue;
                    if (_allow.Count > 0 && !_allow.Contains(name)) continue;

                    string health = liveHealth.ContainsKey(name) ? liveHealth[name] : "gone";

                    // FEED lost while the link reads Connected: ALERT-ONLY (see header for why no recovery).
                    if (health == "feedlost")
                    {
                        if (!scheduledClosed)
                            AlertRaise("feedlost:" + name, "DATA FEED LOST on '" + name + "' (connection still shows Connected)."
                                       + " Strategies are running blind. Not auto-recovering (a disconnect would kill running"
                                       + " strategies) — reconnect manually.", now);
                        else
                            AlertClear("feedlost:" + name, now);   // scheduled close: a dead feed here isn't actionable
                        continue;
                    }

                    if (health == "healthy")                                            // recovered (link + feed both up)
                    {
                        if (kv.Value != "connected") _dropClass[name] = "connected";
                        AlertClear("feedlost:" + name, now);
                        AlertClear("reconnect:" + name, now);
                        AlertClear("dropped:" + name, now);
                        ClearState(name);
                        continue;
                    }
                    if (health == "transition") continue;                              // a transition is underway
                    if (kv.Value != "inadvertent") { ClearState(name); continue; }      // parked / feedlost-class / unknown -> never touch

                    // inadvertent LINK drop (Status == ConnectionLost) -> v1 reconnect state machine
                    if (!_droppedSince.ContainsKey(name)) _droppedSince[name] = now;
                    if ((now - _droppedSince[name]).TotalSeconds < _graceSeconds) continue;  // grace (let NT8 retry first)
                    if (!_reconnect)
                    {
                        // NT8 Guard's "Reconnect dropped connections" is set to alert only (the Watch only preset)
                        AlertRaise("dropped:" + name, "CONNECTION DROPPED: '" + name + "' lost its link "
                                   + (int)(now - _droppedSince[name]).TotalSeconds + "s ago. Reconnecting is switched off"
                                   + " (alert only) - reconnect it by hand.", now);
                        continue;
                    }

                    DateTime cd;
                    if (_cooldownUntil.TryGetValue(name, out cd))
                    {
                        if (now < cd) continue;                          // cooling down
                        _attempts[name] = 0;                             // cooldown elapsed -> fresh cycle
                        _cooldownUntil.Remove(name);
                        _nextAt.Remove(name);
                    }
                    DateTime na;
                    if (_nextAt.TryGetValue(name, out na) && now < na) continue;          // backing off

                    int n;
                    _attempts.TryGetValue(name, out n);
                    DoReconnect(name);
                    n += 1;
                    _attempts[name] = n;
                    _nextAt[name] = now.AddSeconds(Backoff(n));
                    Emit("feed.reconnect", "warn", "'" + name + "' dropped its link - reconnect attempt " + n + " of " + _maxAttempts,
                         "connection", name, "attempt", n.ToString(Inv));
                    if (n >= _maxAttempts)
                    {
                        _cooldownUntil[name] = now.AddSeconds(_cooldownSeconds);
                        LogSafe("reconnect: " + name + " reached maxAttempts=" + _maxAttempts
                                + " -> cooldown " + _cooldownSeconds + "s (will retry, e.g. once a login is refreshed)");
                        // connection down AND auto-reconnect failing = wake-worthy
                        AlertRaise("reconnect:" + name, "RECONNECT FAILING: '" + name + "' is still down after "
                                   + _maxAttempts + " attempts; next cycle in " + (int)(_cooldownSeconds / 60.0)
                                   + " min. Manual attention likely needed.", now);
                    }
                }
            }
            catch (Exception ex) { LogSafe("Tick: " + ex.Message); }
            finally { WriteGuardianHeartbeat(); Monitor.Exit(_gate); }
        }

        // ===== GUARDIAN HEARTBEAT — the file an EXTERNAL overwatch reads to know this watchdog is alive. =====
        //
        // ⭐ WHY THIS EXISTS (2026-08-25, after a 24-minute silent death on the 24/7 box).
        //   Tick() is `Monitor.TryEnter(_gate) … finally Monitor.Exit(_gate)`. That is correct for
        //   re-entrancy and catastrophic for observability: if ONE tick blocks inside the gate, every later
        //   tick returns instantly at TryEnter and this component emits NOTHING. Not an error, not a warning
        //   — silence. On 2026-08-25 the guardian blocked in the Control-Center grid reach at rung 1→2 and
        //   sat there for 24 minutes with a dead feed and two connections reading Connected. Proof: a
        //   file-drop trigger went unconsumed for 45s (CheckManualTriggers is the FIRST statement in the try)
        //   while NinjaTrader itself stayed fully responsive.
        //
        //   ⚠ THE STRUCTURAL POINT: a watchdog whose only failure signal is its own log CANNOT report its own
        //   death, because the thing that would report it is the thing that died. And a process-liveness
        //   check is blind to it too — NinjaTrader.exe was up the whole time. The only fix is a signal that
        //   STOPS, read by something outside this process. That is this file: absence is the alarm.
        //
        //   Written from the FINALLY, so it lands whether the tick succeeded or threw — and, crucially, does
        //   NOT land when the tick blocks. `utc` going stale by more than a few intervals means the guardian
        //   is wedged. Do not "fix" a stale heartbeat by widening the reader's threshold.
        //
        //   Deliberately last-and-cheap: one small local file write per interval, fully swallowed. This must
        //   never be able to become the thing that wedges the tick it is reporting on.
        private void WriteGuardianHeartbeat()
        {
            try
            {
                string nextClosure = "";
                try { nextClosure = FeedGuardCalendar.Describe(FeedGuardCalendar.Next(ToEastern(DateTime.UtcNow), _extraClosed)); } catch { }
                long lt = Interlocked.Read(ref _lastDataTicks);
                string lastAge = lt == 0 ? "null"
                    : ((long)(DateTime.UtcNow - new DateTime(lt, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                var sb = new StringBuilder();
                sb.Append("{\"component\":\"AutoReconnect\"")
                  .Append(",\"build\":\"").Append(BuildStamp).Append("\"")
                  .Append(",\"utc\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\"")
                  .Append(",\"intervalSeconds\":").Append(((int)_intervalSeconds).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"rung\":").Append(_rung.ToString(CultureInfo.InvariantCulture))
                  .Append(",\"autoRecover\":").Append(_autoRecover ? "true" : "false")
                  .Append(",\"lastL1AgeSeconds\":").Append(lastAge)
                  .Append(",\"watchdogStaleSeconds\":").Append(((int)_watchdogStaleSeconds).ToString(CultureInfo.InvariantCulture))
                  .Append(",\"watchdogSubs\":").Append(_watchdogSubs.Count.ToString(CultureInfo.InvariantCulture))
                  // the CONCRETE contracts actually subscribed - so an external reader can see a roll happen
                  .Append(",\"watchdogResolved\":\"").Append(string.Join(",", _watchdogResolved).Replace("\"", "")).Append("\"")
                  .Append(",\"ladderConn\":\"").Append(_ladderConn == null ? "" : _ladderConn.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append("\"")
                  .Append(",\"savedEnabledCount\":").Append(_savedEnabled.Count.ToString(CultureInfo.InvariantCulture))
                  // ---- for NT8 Guard (5.0.0+) ----
                  .Append(",\"version\":").Append(JsonStr(AddOnVersion))
                  // writes NT8Guard\feedguard-events.jsonl, so readers trust its events over its log wording
                  .Append(",\"events\":true")
                  // the calendar's verdict for right now, whatever useSessionGate says: the overwatch must not
                  // read a holiday's silence as a dead feed and restart NinjaTrader over it
                  .Append(",\"scheduledClosed\":").Append(IsScheduledClosed(DateTime.UtcNow) ? "true" : "false")
                  .Append(",\"nextClosure\":").Append(JsonStr(nextClosure))
                  .Append(",\"watchdogConfigured\":").Append(JsonStr(string.Join(",", _watchdogInstruments)))
                  .Append(",\"feedProviders\":").Append(JsonStr(_feedProvidersText))
                  // the watch list is on the Simulated Data Feed: the calendar is off (scheduledClosed stays false)
                  .Append(",\"simulatedFeed\":").Append(_simFeedOnly ? "true" : "false")
                  // what this NinjaTrader version let the add-on read; the control panel shows each in words
                  .Append(",\"selfChecks\":{\"feedProvider\":")
                  .Append(JsonStr(_mdConnProp == null ? "unavailable" : _feedProvidersText.Length > 0 ? "ok" : "unknown"))
                  .Append(",\"rollover\":").Append(JsonStr(_rolloverSource))
                  .Append(",\"reEnable\":").Append(JsonStr(_reEnableCheck))
                  .Append(",\"calendar\":\"rules\"}")
                  .Append("}");
                // Write-then-replace so a reader never catches a half-written file.
                string path = Path.Combine(Globals.UserDataDir, "AutoReconnect.heartbeat.json");
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        // Reconnect from the SAVED options — used ONLY for a true ConnectionLost (never on a connection that
        // still reads Connected: Connect() there errors with "account already exists"). A fully-dropped
        // connection is gone from Connection.Connections, but Globals.ConnectOptions persists its config.
        // Fire on the UI dispatcher (BeginInvoke -> async, never blocks the timer thread).
        private void DoReconnect(string name)
        {
            ConnectOptions opt = null;
            try
            {
                lock (Globals.ConnectOptions)
                    foreach (ConnectOptions o in Globals.ConnectOptions)
                        if (SafeName(o) == name) { opt = o; break; }
            }
            catch { }
            if (opt == null) { LogSafe("reconnect: " + name + " - no saved ConnectOptions, skipping"); return; }

            ConnectOptions o2 = opt;
            Action act = delegate { try { Connection.Connect(o2); } catch (Exception ex) { LogSafe("reconnect Connect(" + name + "): " + ex.Message); } };
            try
            {
                Dispatcher disp = Globals.MainThreadDispatcher;
                if (disp != null) disp.BeginInvoke(act); else act();
            }
            catch (Exception ex) { LogSafe("reconnect dispatch(" + name + "): " + ex.Message); }
            LogSafe("reconnect: " + name + " attempt fired");
        }

        // ===== watchdog plumbing =====

        // "MNQ ##-##" -> the contract NinjaTrader itself treats as the front month today, from its own roll
        // dates; when those cannot be read, or end before today, the CME quarterly rule (FeedGuardRollover).
        // A concrete month ("MNQ 12-26") is returned untouched. how = "literal" | "nt8" | "rule".
        internal string ResolveFrontMonth(string configured, DateTime today, out string how)
        {
            string symbol;
            if (!FeedGuardRollover.IsAlias(configured, out symbol))
            {
                how = "literal";
                return configured == null ? null : configured.Trim();
            }
            string fromNt8 = FeedGuardRollover.FromSchedule(symbol, today, ReadNt8Rollovers(symbol));
            DateTime expiryMonth = DateTime.MinValue;
            if (fromNt8 != null)
            {
                try { Instrument inst = Instrument.GetInstrument(fromNt8); if (inst != null) expiryMonth = inst.Expiry; } catch { }
            }
            fromNt8 = FeedGuardRollover.PickFront(fromNt8, expiryMonth, today);   // a stale schedule never wins in the expiry month
            how = fromNt8 != null ? "nt8" : "rule";
            return fromNt8 ?? FeedGuardRollover.Quarterly(symbol, today, _rollDaysBeforeExpiry);
        }

        // One pass over the watch list -> the heartbeat's rolloverSource: "rule" if any "##-##" fell back to
        // the quarterly rule, else "nt8" if any used NinjaTrader's dates, else "n/a".
        private static string CombineRolloverSource(string soFar, string how)
        {
            if (how == "rule" || soFar == "rule") return "rule";
            if (how == "nt8" || soFar == "nt8") return "nt8";
            return "n/a";
        }

        private string RolloverSourceText()
        {
            if (_rolloverSource == "nt8") return "NinjaTrader's roll dates";
            if (_rolloverSource == "rule") return "the quarterly rule, roll " + _rollDaysBeforeExpiry + "d before expiry (NinjaTrader's roll dates unreadable or out of date)";
            return "the config (concrete contracts)";
        }

        // NinjaTrader's roll schedule for one master instrument ("MNQ") as (contract month, roll date)
        // pairs; empty when it cannot be read. REFLECTION ON PURPOSE: NinjaTrader compiles every
        // NinjaScript file into one assembly, so a compile-time reference to a member a future version
        // renames would break every indicator and strategy on the machine. Read this way, a rename only
        // means the quarterly rule is used (heartbeat "rollover":"rule"). Member names verified against
        // NinjaTrader 8.1.8.3 by tools\Test-FeedGuardNt8Api.ps1.
        private static List<KeyValuePair<DateTime, DateTime>> ReadNt8Rollovers(string symbol)
        {
            var outp = new List<KeyValuePair<DateTime, DateTime>>();
            try
            {
                Type mt = typeof(Instrument).Assembly.GetType("NinjaTrader.Cbi.MasterInstrument");
                System.Reflection.PropertyInfo allP = mt == null ? null
                    : mt.GetProperty("All", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                System.Collections.IEnumerable all = allP == null ? null : allP.GetValue(null, null) as System.Collections.IEnumerable;
                if (all == null) return outp;
                object master = null;
                foreach (object m in all)
                {
                    System.Reflection.PropertyInfo np = m == null ? null : m.GetType().GetProperty("Name");
                    if (np != null && string.Equals(np.GetValue(m, null) as string, symbol, StringComparison.OrdinalIgnoreCase)) { master = m; break; }
                }
                if (master == null) return outp;
                System.Reflection.PropertyInfo rp = master.GetType().GetProperty("RolloverCollection");
                System.Collections.IEnumerable rolls = rp == null ? null : rp.GetValue(master, null) as System.Collections.IEnumerable;
                if (rolls == null) return outp;
                foreach (object r in rolls)
                {
                    if (r == null) continue;
                    Type rt = r.GetType();
                    System.Reflection.PropertyInfo risk = rt.GetProperty("IsRiskManagementOnly");   // not a data roll: skip
                    object riskOnly = risk == null ? null : risk.GetValue(r, null);
                    if (riskOnly is bool && (bool)riskOnly) continue;
                    System.Reflection.PropertyInfo cmP = rt.GetProperty("ContractMonth"), dP = rt.GetProperty("Date");
                    object cm = cmP == null ? null : cmP.GetValue(r, null);
                    object d = dP == null ? null : dP.GetValue(r, null);
                    if (cm is DateTime && d is DateTime) outp.Add(new KeyValuePair<DateTime, DateTime>((DateTime)cm, (DateTime)d));
                }
            }
            catch { outp.Clear(); }   // a collection NinjaTrader changed mid-read: the quarterly rule decides this time
            return outp;
        }

        // Subscribe a live L1 MarketData stream per watched instrument (bid/ask/last — the same movement
        // you'd watch by eye; more direct than a bars series). Every event stamps the SAME _lastDataTicks,
        // so it holds "latest L1 update from ANY watched instrument": a stall means NONE of them moved.
        private void StartWatchdog()
        {
            _watchdogSubscribedFrom = new List<string>(_watchdogInstruments);
            if (_watchdogInstruments.Count == 0) return;   // watchdog OFF
            _lastDataTicks = 0;   // no update yet -> never flag before the first one
            _watchdogResolved.Clear();
            _lastRollCheckDate = DateTime.Now.Date;
            string source = "n/a";
            foreach (string cfgName in _watchdogInstruments)
            {
                string how;
                string name = ResolveFrontMonth(cfgName, DateTime.Now, out how);
                source = CombineRolloverSource(source, how);
                try
                {
                    Instrument inst = Instrument.GetInstrument(name);
                    if (inst == null) { LogSafe("watchdog: instrument '" + name + "' (from '" + cfgName + "') not found -> skipped"); continue; }
                    MarketData md = new MarketData(inst);
                    md.Update += OnWatchdogTick;   // SAME handler -> shared _lastDataTicks
                    _watchdogSubs.Add(md);
                    _watchdogResolved.Add(name);
                }
                catch (Exception ex) { LogSafe("watchdog start '" + name + "': " + ex.Message); }
            }
            _rolloverSource = source;
            if (_watchdogSubs.Count == 0)
            {
                LogSafe("watchdog: no valid instruments -> OFF");
                AlertRaise("watchlist-empty", WatchListEmptyMessage(), DateTime.UtcNow);
                return;
            }
            AlertClear("watchlist-empty", DateTime.UtcNow);
            LogSafe("watchdog: armed on [" + string.Join(",", _watchdogResolved) + "] (configured ["
                    + string.Join(",", _watchdogInstruments) + "], contract months from " + RolloverSourceText() + ") (" + _watchdogSubs.Count
                    + " L1 streams), staleSeconds=" + _watchdogStaleSeconds + " — "
                    + (_autoRecover ? "AUTO-RECOVERY (re-subscribe -> clean disconnect+reconnect; DISABLES strategies on a genuine dead feed)"
                                    : "ALERT-ONLY (never disconnects)"));
        }

        // Every L1 update (bid/ask/last/…) stamps the wall-clock; read by the guardian to detect a frozen
        // feed. Fires on arbitrary threads -> touches only the Interlocked field (+ a one-time log).
        private void OnWatchdogTick(object sender, MarketDataEventArgs e)
        {
            long nowTicks = DateTime.UtcNow.Ticks;
            // AutoReconnect.deafen test trigger: swallow REAL L1 so the feed looks genuinely dead to the
            // real classifier (unlike AutoReconnect.stall, which forces the stalled flag and thereby skips
            // the snapshot logic entirely — which is how the unreachable rung 2 shipped "validated").
            if (nowTicks < Interlocked.Read(ref _deafenUntilTicks)) return;
            // Snapshot echo from the ladder's own re-subscribe: not evidence the feed is alive.
            if (nowTicks < Interlocked.Read(ref _resubGraceUntilTicks)) return;
            Interlocked.Increment(ref _updatesSinceResub);
            Interlocked.Exchange(ref _lastDataTicks, nowTicks);
            if (Interlocked.CompareExchange(ref _firstTickLogged, 1, 0) == 0)
                LogSafe("watchdog: first L1 update received (heartbeat live)");
        }

        // Market-open gate (avoid churn at market close). A precise TradingHours "is-now-in-session" check
        // needs session-TZ + day/time windowing that isn't reliable to compute here, so we use the
        // Which connection(s) the ladder may cycle. Pure, and tested in isolation by Test-LadderTarget.ps1.
        //
        // `providers` = the connections NinjaTrader itself says are serving the watched instruments
        // (Instrument.MarketDataConnection). When it can answer, ONLY those are candidates: that is the
        // connection whose feed is actually dead, and cycling anything else fixes nothing while claiming
        // the broker is down. The config (`feedOwners`) is the fallback for when NT8 cannot answer.
        // A provider that is not healthy yields NO candidate - never a fallback to the config target,
        // because cycling the wrong connection is exactly the failure this exists to stop.
        internal static List<string> SelectLadderTargets(IList<string> providers, IDictionary<string, string> health,
                                                         ICollection<string> feedOwners, ICollection<string> allow,
                                                         Func<string, bool> isExcluded, out string source)
        {
            var result = new List<string>();
            if (providers != null && providers.Count > 0)
            {
                source = "provider";
                foreach (string p in providers)
                {
                    string h;
                    if (p == null || !health.TryGetValue(p, out h) || h != "healthy") continue;
                    if (isExcluded(p)) continue;
                    if (allow.Count > 0 && !allow.Contains(p)) continue;
                    if (!result.Contains(p)) result.Add(p);
                }
                return result;
            }
            source = "config";
            foreach (KeyValuePair<string, string> kv in health)
            {
                if (kv.Value != "healthy" || isExcluded(kv.Key)) continue;
                if (allow.Count > 0 && !allow.Contains(kv.Key)) continue;
                // ladder-only scope: an unrelated healthy connection must not disqualify recovery
                if (feedOwners.Count > 0 && !feedOwners.Contains(kv.Key)) continue;
                result.Add(kv.Key);
            }
            return result;
        }

        // Instrument.MarketDataConnection is INTERNAL to NinjaTrader.Core, so it is read by reflection.
        // Resolved once; a null here (a future NT8 renaming it) just means the config fallback is used.
        private static readonly System.Reflection.PropertyInfo _mdConnProp =
            typeof(Instrument).GetProperty("MarketDataConnection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic);

        // Names of the connections NinjaTrader is using to serve the watched instruments right now.
        // Empty when that cannot be determined - which callers treat as "use the config".
        private List<string> ResolveFeedProviders()
        {
            var names = new List<string>();
            if (_mdConnProp == null) return names;
            foreach (string instName in _watchdogResolved.ToArray())
            {
                try
                {
                    Instrument inst = Instrument.GetInstrument(instName);
                    if (inst == null) continue;
                    Connection c = _mdConnProp.GetValue(inst, null) as Connection;
                    if (c == null || c.Options == null) continue;
                    string n = c.Options.Name;
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
                }
                catch { }
            }
            return names;
        }

        // Is the watch list served ONLY by NinjaTrader's Simulated Data Feed? That feed makes up prices around
        // the clock, so the CME calendar does not apply to it: a quiet Simulated Data Feed on a Saturday is a
        // real silence, and the fire drill can run on a weekend (spec 10.4). Pure, and tested in isolation by
        // Test-StallGate.ps1. Any real broker, Playback, or no answer at all keeps the calendar.
        internal static bool IsSimulatedFeedOnly(IList<string> providerKinds)
        {
            if (providerKinds == null || providerKinds.Count == 0) return false;
            foreach (string k in providerKinds) if (k != "Simulator") return false;
            return true;
        }

        // The answer holds still while a recovery runs (rung > 0): the drill's own rung-2 disconnect leaves the
        // instruments with no provider for a moment, and flipping to "broker" there would make a Saturday read
        // as a scheduled close - which abandons the episode with the feed left disconnected. Between recoveries
        // it follows the providers. Pure, and tested in isolation by Test-StallGate.ps1.
        internal static bool NextSimFeedOnly(bool current, int rung, IList<string> providerKinds)
        {
            if (rung > 0) return current;
            return IsSimulatedFeedOnly(providerKinds);
        }

        // The provider KIND (NinjaTrader's Provider enum) behind each watched instrument's feed.
        private void RefreshSimFeedOnly()
        {
            var kinds = new List<string>();
            if (_mdConnProp != null)
            {
                foreach (string instName in _watchdogResolved.ToArray())
                {
                    try
                    {
                        Instrument inst = Instrument.GetInstrument(instName);
                        Connection c = inst == null ? null : _mdConnProp.GetValue(inst, null) as Connection;
                        kinds.Add(c == null || c.Options == null ? "" : c.Options.Provider.ToString());
                    }
                    catch { kinds.Add(""); }
                }
            }
            bool now = NextSimFeedOnly(_simFeedOnly, _rung, kinds);
            if (now != _simFeedOnly)
                LogSafe(now ? "session: the watch list is on the Simulated Data Feed, which never closes - the market calendar is off"
                            : "session: the watch list is on a broker's feed - the market calendar applies");
            _simFeedOnly = now;
        }

        // Is a quiet feed a STALL? Pure, and tested in isolation by Test-StallGate.ps1.
        //
        // `dataRecencyOpen` (IsMarketLikelyOpen) answers "data flowed recently, so the market is open" -
        // useful, but it goes false once the feed has been dead longer than _recentActiveMinutes, and
        // then it can never come back on its own. On its own it therefore makes a LONG-dead feed
        // undetectable, which is how the desktop sat 7.8h with frozen charts after a 4-hour machine
        // sleep (2026-09-23). `openByClock` is the session gate's clock-only answer and has no such
        // blind spot, so either one is enough to call it a stall.
        internal static bool ShouldFlagStall(double staleSec, double thresholdSec,
                                             bool dataRecencyOpen, bool openByClock)
        {
            return staleSec > thresholdSec && (dataRecencyOpen || openByClock);
        }

        // FEED-RECENCY proxy: data flowed within the last _recentActiveMinutes. A clean market close stops
        // the feed for far longer than that, so a stall (>staleSeconds quiet) with data <30 min ago is mid-session.
        private bool IsMarketLikelyOpen(DateTime nowUtc, DateTime lastDataUtc)
        {
            return lastDataUtc != DateTime.MinValue && (nowUtc - lastDataUtc).TotalMinutes <= _recentActiveMinutes;
        }

        // ===== playback gate =====

        // True when NT8 is in Playback/replay: the Playback connection object exists and is up (or coming
        // up — the switch window, where the live connection is already gone). Plain static property read,
        // no locking. FAIL-SAFE points the same way as IsScheduledClosed: on any error return false (treat
        // as NOT playback) so a genuine stall on a live box is never silently swallowed.
        private bool IsPlaybackActive()
        {
            try
            {
                Connection pb = Connection.PlaybackConnection;
                if (pb == null) return false;
                ConnectionStatus s = pb.Status;
                return s == ConnectionStatus.Connected || s == ConnectionStatus.Connecting;
            }
            catch (Exception ex) { LogSafe("playback: state read error -> treating as NOT playback (" + ex.Message + ")"); return false; }
        }

        // ===== market-hours gate (CME equity-index schedule, ET wall-clock) =====

        // True when NOW is inside a scheduled close: the daily maintenance break (17:00-18:00 ET), the
        // weekend (Fri 17:00 -> Sun 18:00 ET), a CME holiday or early close (FeedGuardCalendar), or an extra
        // date from the config. Used to suppress the silent-stall alert
        // + recovery ladder — a quiet feed there is expected, and injecting would force a login the broker
        // may refuse (-> parked). FAIL-SAFE: on any error returns false (treat as OPEN) so a genuine stall
        // is never silently ignored on an unattended box.
        private bool IsScheduledClosed(DateTime nowUtc)
        {
            try
            {
                if (_simFeedOnly) return false;                                        // the Simulated Data Feed never closes
                DateTime et = ToEastern(nowUtc);
                int mins = et.Hour * 60 + et.Minute;
                DayOfWeek d = et.DayOfWeek;

                if (d == DayOfWeek.Saturday) return true;                              // all Saturday
                if (d == DayOfWeek.Friday && mins >= _weekCloseMin) return true;       // Fri from the weekly close
                if (d == DayOfWeek.Sunday && mins < _weekOpenMin) return true;         // Sun before the weekly open
                if (d != DayOfWeek.Sunday && mins >= _breakStartMin && mins < _breakEndMin) return true;  // daily break (Mon-Fri)

                if (FeedGuardCalendar.IsHolidayClosed(et, _extraClosed)) return true;  // CME holiday / early close, or a config extra date

                return false;
            }
            catch (Exception ex) { LogSafe("session: IsScheduledClosed error -> treating as OPEN (" + ex.Message + ")"); return false; }
        }

        // UTC -> US-Eastern wall-clock. Prefers the OS time-zone db; on a missing id falls back to the
        // US DST rule (2nd Sunday March 02:00 -> 1st Sunday November 02:00 = EDT/UTC-4, else EST/UTC-5).
        private DateTime ToEastern(DateTime utc)
        {
            if (_marketTz != null) return TimeZoneInfo.ConvertTimeFromUtc(utc, _marketTz);
            return utc.AddHours(IsUsEasternDst(utc) ? -4 : -5);
        }
        private static bool IsUsEasternDst(DateTime utc)
        {
            int y = utc.Year;
            DateTime dstStart = SecondSunday(y, 3).AddHours(7);   // 02:00 EST = 07:00 UTC
            DateTime dstEnd   = FirstSunday(y, 11).AddHours(6);   // 02:00 EDT = 06:00 UTC
            return utc >= dstStart && utc < dstEnd;
        }
        private static DateTime FirstSunday(int year, int month)
        {
            DateTime first = new DateTime(year, month, 1);
            return first.AddDays(((int)DayOfWeek.Sunday - (int)first.DayOfWeek + 7) % 7);
        }
        private static DateTime SecondSunday(int year, int month) { return FirstSunday(year, month).AddDays(7); }
        private void ResolveMarketTz()
        {
            try { _marketTz = TimeZoneInfo.FindSystemTimeZoneById(_marketTzId); }
            catch (Exception ex) { _marketTz = null; LogSafe("session: TZ '" + _marketTzId + "' not found (" + ex.Message + ") -> DST-rule fallback"); }
        }
        private static string Hhmm(int mins) { return (mins / 60).ToString("00", Inv) + ":" + (mins % 60).ToString("00", Inv); }

        // ===== file-drop triggers (see header) =====

        // Each file is read + DELETED before acting, so a crash can never loop the action. Guardian
        // thread, under _gate.
        private void CheckManualTriggers(DateTime now)
        {
            try
            {
                string stallPath = Path.Combine(Globals.UserDataDir, "AutoReconnect.stall");
                if (File.Exists(stallPath)) DoForceStall(SafeReadDelete(stallPath), now);
                string deafPath = Path.Combine(Globals.UserDataDir, "AutoReconnect.deafen");
                if (File.Exists(deafPath)) DoDeafen(SafeReadDelete(deafPath), now);
                string reloadPath = Path.Combine(Globals.UserDataDir, "AutoReconnect.reload");
                if (File.Exists(reloadPath)) { SafeReadDelete(reloadPath); DoReload(); }
            }
            catch (Exception ex) { LogSafe("manual trigger: " + ex.Message); }
        }

        // AutoReconnect.reload: re-read AutoReconnect.config.json now (NT8 Guard's control panel writes the
        // file, then drops this). Numbers, switches and lists apply at once; a changed watch list re-subscribes
        // through ApplyWatchListChange - on this tick, or once a running recovery ends. A key DELETED from the
        // file keeps its current value until NinjaTrader restarts (the panel always writes every key).
        private void DoReload()
        {
            double interval = _intervalSeconds;
            if (!ReadConfig()) { LogSafe("reload: no AutoReconnect.config.json - nothing to re-read"); return; }
            ResolveMarketTz();
            if (_intervalSeconds != interval && _timer != null)
            {
                long ms = (long)(_intervalSeconds * 1000.0);
                try { _timer.Change(ms, ms); } catch { }
            }
            LogSafe("reload: settings re-read - watch=[" + string.Join(",", _watchdogInstruments) + "] stale=" + _watchdogStaleSeconds
                    + "s reconnect=" + (_reconnect ? "on" : "ALERT-ONLY") + " autoRecover=" + (_autoRecover ? "on" : "off")
                    + " reEnableAfterRecover=" + (_reEnableAfterRecover ? "on" : "off") + " extraDates=" + _extraClosed.Count
                    + " webhook=" + (_webhookUrl.Length > 0 ? "on" : "off")
                    + (_rung > 0 ? "; a recovery is running - a changed watch list applies when it ends" : ""));
            Emit("feed.loaded", "info", "Feed guard settings reloaded", "version", AddOnVersion, "build", BuildStamp, "reason", "reload");
            ApplyWatchListChange();
        }

        private string SafeReadDelete(string path)
        {
            string body = "";
            try { body = File.ReadAllText(path).Trim(); } catch { }
            try { File.Delete(path); } catch { }
            return body;
        }

        // Among same-name matches, return the HEALTHIEST object: after a ConnectionLost -> Connect()
        // recovery cycle (e.g. an injected recovery) a stale still-ConnectionLost husk of the same name
        // lingers in Connection.Connections next to the live object (observed live 2026-07-05) — a
        // first-match lookup would poke/inject the husk (a no-op) and miss the real connection.
        private Connection FindLiveConnection(string name)
        {
            Connection best = null; int bestRank = -1;
            try
            {
                lock (Connection.Connections)
                    foreach (Connection c in Connection.Connections)
                        if (NameOf(c) == name)
                        {
                            int r = HealthRank(Health(c));
                            if (r > bestRank) { bestRank = r; best = c; }
                        }
            }
            catch { }
            return best;
        }

        // healthy > transition > feedlost > lost > parked (used only to pick between same-name objects)
        private static int HealthRank(string h)
        {
            return h == "healthy" ? 5 : h == "transition" ? 4 : h == "feedlost" ? 3 : h == "lost" ? 2 : h == "parked" ? 1 : 0;
        }

        private static string DescribeConn(Connection c)
        {
            try { return c == null ? "(none)" : (NameOf(c) + " " + c.Status + "/" + c.PriceStatus); } catch { return "(err)"; }
        }

        // ===== AUTO-RECOVERY ladder (opt-in): RE-SUBSCRIBE -> DISCONNECT -> RECONNECT -> ALERT + cooldown =====

        // One silent-stall episode, escalated rung by rung with settle waits. Recovery = the heartbeat
        // resuming, which flips stalled false and lands Tick in the else branch -> LadderReset (so every
        // rung is implicitly "if the feed came back, stop"). Rung 2 DISCONNECTS the feed's connection -
        // never with a position open (the exposure gate). Guardian thread, under _gate.
        private void RunRecoveryLadder(List<string> conns, double staleSec, DateTime now)
        {
            if (now < _ladderCooldownUntil)
            {
                // ALERT rung holds until the cooldown expires (AlertRaise self-throttles the re-fires)
                AlertRaise("silent-stall", "SILENT FEED STALL persists (auto-recovery cooling down): no L1 update from ["
                           + string.Join(",", _watchdogResolved) + "] for " + (int)staleSec
                           + "s. Strategies are running BLIND on stale data — reconnect manually.", now);
                return;
            }
            // rung 1 — RE-SUBSCRIBE the watchdog (non-disruptive). If the feed is actually alive (a stale
            // watchdog subscription / false stall), the fresh subs deliver and the heartbeat resumes ->
            // LadderReset, no disconnect. Nothing is cycled, no strategy is touched.
            if (_rung == 0)
            {
                if (conns.Count == 0) return;
                bool restart = _ladderCooldownUntil != DateTime.MinValue;
                _stallSince = now; _rung = 1; _rungAt = now;
                LogSafe("recover: silent stall (" + (int)staleSec + "s), target=[" + string.Join(",", conns) + "] via " + _ladderTargetSource + " -> "
                        + (restart ? "ladder RESTART after cooldown, " : "") + "rung 1 RE-SUBSCRIBE watchdog (non-disruptive)");
                Emit("feed.stall", "warn", "No market data for " + (int)staleSec + "s - re-subscribing (recovery step 1 of 3)",
                     "seconds", ((int)staleSec).ToString(Inv), "target", string.Join(",", conns));
                RestartWatchdog();
                return;
            }
            // rung 2 — re-subscribe did NOT bring data back => the feed is genuinely dead => DISCONNECT.
            // A real Disconnect() cleanly releases the account (no duplicate-account corruption, unlike the
            // abandoned ConnectionLost inject) — but NT8 disables running strategies on this connection.
            if (_rung == 1)
            {
                if ((now - _rungAt).TotalSeconds < _resubSettleSeconds) return;
                if (conns.Count == 0)
                {
                    // Nothing healthy to cycle RIGHT NOW. After a wake from sleep every connection flaps
                    // through ConnectionLost for 20-30s, and this check lands inside that window - on
                    // 2026-09-29 it did, escalated, and parked the ladder in a 10-minute cooldown for a
                    // condition that cleared 6 seconds later. That is transient, not a verdict: drop back to
                    // rung 0 with NO cooldown, and the next tick restarts the ladder once the feed provider is
                    // healthy again. (While nothing is healthy the stall branch raises its own alert.)
                    LogSafe("recover: stall survived re-subscribe but the feed provider is not healthy yet (connections"
                            + " still settling) -> restarting the ladder when it is, no cooldown");
                    _rung = 0; _rungAt = DateTime.MinValue;
                    return;
                }
                if (conns.Count > 1)
                {
                    // multi-connection: refuse to mass-disconnect (would disable strategies on every connection)
                    LogSafe("recover: stall survived re-subscribe but " + conns.Count + " healthy candidate connection(s) ["
                            + string.Join(",", conns) + "] -> can't safely auto-disconnect; ALERT");
                    EscalateToAlert(staleSec, now);
                    return;
                }
                _ladderConn = conns[0];
                // ! EXPOSURE GATE. Rung 2 makes NinjaTrader disable every strategy on this connection, and
                //   the disable/re-enable cycle takes the live stop and target with it. With a position
                //   open that trades PROTECTION for DATA: the resting stop/target at the broker still
                //   execute while the feed is dead, and cancelling them is what actually loses money.
                //   2026-09-22 21:43 ET: a manual Chart Trader close cancelled a sim strategy's brackets and
                //   rung 2 fired 3.6s later, so the close never filled and the short sat naked through the
                //   outage. The ladder did not cause that one - it collided with it - but it would have
                //   produced the same end state on its own, and there was nothing stopping it.
                //   Blind-but-bracketed beats flat-out-naked, so when exposed: alert, never disconnect.
                string exposure = DescribeExposure(_ladderConn);
                if (exposure != null)
                {
                    LogSafe("recover: stall survived re-subscribe, but '" + _ladderConn + "' has OPEN EXPOSURE ("
                            + exposure + ") -> refusing rung 2 DISCONNECT (it would cancel the live stop/target "
                            + "and leave the position naked); ALERT instead");
                    AlertRaise("silent-stall-exposed", "SILENT FEED STALL WITH AN OPEN POSITION: no L1 update from ["
                               + string.Join(",", _watchdogResolved) + "] for " + (int)staleSec + "s on '" + _ladderConn
                               + "', and auto-recovery is HOLDING because you are exposed (" + exposure + "). A disconnect "
                               + "would disable the strategies and cancel their stop/target, leaving the position naked - "
                               + "the resting brackets are still protecting you, so they are being left alone. Close the "
                               + "position or fix the feed BY HAND.", now);
                    _ladderCooldownUntil = now.AddSeconds(_recoveryCooldownSeconds);
                    _rung = 0; _rungAt = DateTime.MinValue;
                    return;
                }
                if (_reEnableAfterRecover)
                {
                    foreach (string s in CaptureEnabledStrategyNames()) if (!_savedEnabled.Contains(s)) _savedEnabled.Add(s);
                    LogSafe("recover: " + _savedEnabled.Count + " enabled strateg(y/ies) recorded to re-enable after recovery: " + string.Join(", ", _savedEnabled));
                }
                LogSafe("recover: stall survived re-subscribe (" + Interlocked.Read(ref _updatesSinceResub)
                        + " post-snapshot L1 update(s) => feed genuinely dead) -> rung 2 DISCONNECT '" + _ladderConn
                        + "' (clean cycle; NT8 will DISABLE running strategies on this connection)");
                PostWebhook("auto-recover", "auto-recover: disconnect+reconnect '" + _ladderConn + "' (dead feed; strategies disabled)");
                Emit("feed.disconnect", "warn", "Market data still dead after re-subscribing - disconnecting '" + _ladderConn
                     + "' to reconnect it (recovery step 2 of 3)", "connection", _ladderConn);
                Emit("feed.strategies-disabled", "warn", "NinjaTrader switches the strategies on '" + _ladderConn + "' off when it disconnects",
                     "connection", _ladderConn, "reason", "recovery-disconnect", "enabledBefore", string.Join(",", _savedEnabled));
                DoDisconnect(_ladderConn);
                _rung = 2; _rungAt = now;
                return;
            }
            // rung 3 — once the connection has actually reached Disconnected (account released), fire Connect().
            // Waiting for Disconnected avoids the "account already exists" duplicate that firing Connect() early
            // would cause. Falls through to reconnect anyway if it doesn't disconnect within the timeout.
            if (_rung == 2)
            {
                string h = HealthOf(_ladderConn);
                bool disconnected = h == "parked" || h == "gone";
                if (!disconnected && (now - _rungAt).TotalSeconds < _disconnectTimeoutSeconds) return;
                LogSafe(disconnected ? "recover: '" + _ladderConn + "' reached Disconnected -> reconnecting"
                                     : "recover: '" + _ladderConn + "' did not reach Disconnected in " + (int)_disconnectTimeoutSeconds + "s -> reconnecting anyway");
                DoReconnect(_ladderConn);
                _rung = 3; _rungAt = now;
                return;
            }
            // rung 3 wait — reconnected; if the heartbeat resumes we land in LadderReset (recovered, strategies
            // disabled). If not, the feed is down at the source (e.g. Tradovate maintenance) -> alert + cooldown.
            if ((now - _rungAt).TotalSeconds < _injectSettleSeconds) return;
            LogSafe("recover: still stalled after disconnect+reconnect settle -> ALERT + cooldown (feed likely down at the source)");
            // The verdict NT8 Guard's overwatch waits for: the whole ladder ran and the feed is still dead, so
            // restarting NinjaTrader cannot help - the outage is upstream of it.
            Emit("feed.source-outage", "alert", "Market data still dead after disconnect and reconnect - the data source itself is likely down",
                 "connection", _ladderConn, "seconds", ((int)staleSec).ToString(Inv));
            EscalateToAlert(staleSec, now);
        }

        // Re-subscribe the watchdog L1 streams WITHOUT resetting the heartbeat clock. A subscription created
        // right after a connection cycle can sit blind (delivered a snapshot then went silent, 2026-07-08);
        // a fresh subscribe on a now-stable connection delivers again -> the heartbeat advances -> stall clears.
        // Once per calendar day, re-resolve every "##-##" watched instrument. If the front month has
        // moved since we subscribed (the roll date passed while NinjaTrader stayed up), re-subscribe
        // onto the new contract. Only with the ladder idle: mid-climb the old subscriptions are the
        // evidence the ladder is reasoning about, and RestartWatchdog is rung 1's own move.
        private void CheckContractRoll()
        {
            try
            {
                if (_watchdogSubs.Count == 0 || _rung != 0) return;
                DateTime today = DateTime.Now.Date;
                if (today == _lastRollCheckDate) return;
                _lastRollCheckDate = today;
                var want = new List<string>();
                foreach (string cfgName in _watchdogInstruments) { string how; want.Add(ResolveFrontMonth(cfgName, DateTime.Now, out how)); }
                bool same = want.Count == _watchdogResolved.Count;
                if (same) for (int i = 0; i < want.Count; i++) if (want[i] != _watchdogResolved[i]) { same = false; break; }
                if (same) return;
                LogSafe("watchdog: CONTRACT ROLL - subscribed [" + string.Join(",", _watchdogResolved) + "] -> front month now ["
                        + string.Join(",", want) + "]; re-subscribing so an expiring contract is never mistaken for a dead feed");
                Emit("feed.contract-roll", "info", "Watch list moved to the new front month: " + string.Join(",", want),
                     "from", string.Join(",", _watchdogResolved), "to", string.Join(",", want));
                RestartWatchdog();
            }
            catch (Exception ex) { LogSafe("watchdog roll check: " + ex.Message); }
        }

        // Once a day: warn when a CONCRETE contract on the watch list ("MNQ 12-26") is within 7 days of its
        // expiry month, in it, or past it - after expiry it never ticks again and reads as a dead feed
        // (2026-09-18). Instrument.Expiry is month resolution: see FeedGuardRollover.ExpiryNotice.
        private void CheckLiteralExpiry(DateTime now)
        {
            try
            {
                DateTime today = DateTime.Now.Date;
                if (today == _lastExpiryCheckDate) return;
                _lastExpiryCheckDate = today;
                foreach (string cfgName in _watchdogInstruments)
                {
                    string symbol;
                    if (FeedGuardRollover.IsAlias(cfgName, out symbol)) continue;   // "##-##" rolls by itself
                    Instrument inst = null;
                    try { inst = Instrument.GetInstrument(cfgName.Trim()); } catch { }
                    if (inst == null) continue;
                    string notice = FeedGuardRollover.ExpiryNotice(inst.FullName, inst.Expiry, today, 7);
                    if (notice == null) continue;
                    AlertRaise("contract-expiring:" + inst.FullName, notice, now);
                    Emit("feed.contract-expiring", "warn", notice, "instrument", inst.FullName,
                         "expiryMonth", inst.Expiry.ToString("yyyy-MM", Inv));
                }
            }
            catch (Exception ex) { LogSafe("watchdog expiry check: " + ex.Message); }
        }

        private string WatchListEmptyMessage()
        {
            return "WATCH LIST: none of [" + string.Join(",", _watchdogInstruments) + "] is an instrument NinjaTrader knows, so the"
                   + " feed watchdog is OFF and market data is not being watched. Fix the watch list (for example MNQ ##-##).";
        }

        // Drop every watchdog subscription (the add-on shutting down, or a reload that emptied the watch
        // list). The heartbeat clock restarts from "no update yet", so nothing is flagged until data flows.
        private void StopWatchdog()
        {
            try { foreach (MarketData md in _watchdogSubs) { try { md.Update -= OnWatchdogTick; } catch { } } } catch { }
            _watchdogSubs.Clear();
            _watchdogResolved.Clear();
            Interlocked.Exchange(ref _lastDataTicks, 0);
        }

        // After a reload: bring the subscriptions in line with the config's watch list. Every tick; a no-op
        // unless the list changed, and it waits while a recovery episode is running (FeedGuardReload).
        private void ApplyWatchListChange()
        {
            string plan = FeedGuardReload.WatchPlan(_watchdogSubscribedFrom, _watchdogInstruments, _watchdogSubs.Count, _rung);
            if (plan == "same" || plan == "wait") return;
            LogSafe("reload: watch list [" + string.Join(",", _watchdogSubscribedFrom) + "] -> ["
                    + string.Join(",", _watchdogInstruments) + "]: " + plan);
            if (plan == "stop")
            {
                StopWatchdog();
                _watchdogSubscribedFrom = new List<string>();
                LogSafe("watchdog: OFF (the watch list is empty)");
            }
            else if (plan == "start") StartWatchdog();
            else RestartWatchdog();
        }

        private void RestartWatchdog()
        {
            _watchdogSubscribedFrom = new List<string>(_watchdogInstruments);
            if (_watchdogInstruments.Count == 0) return;
            try { foreach (MarketData md in _watchdogSubs) { try { md.Update -= OnWatchdogTick; } catch { } } } catch { }
            _watchdogSubs.Clear();
            // Arm the snapshot grace BEFORE subscribing, so the cached snapshot each new stream fires on
            // creation cannot masquerade as a live heartbeat and reset the ladder (see _resubGraceUntilTicks).
            Interlocked.Exchange(ref _resubGraceUntilTicks, DateTime.UtcNow.AddMilliseconds(_resubSnapshotGraceMs).Ticks);
            Interlocked.Exchange(ref _updatesSinceResub, 0);
            int n = 0;
            _watchdogResolved.Clear();
            _lastRollCheckDate = DateTime.Now.Date;
            string source = "n/a";
            foreach (string cfgName in _watchdogInstruments)
            {
                string how;
                string name = ResolveFrontMonth(cfgName, DateTime.Now, out how);
                source = CombineRolloverSource(source, how);
                try
                {
                    Instrument inst = Instrument.GetInstrument(name);
                    if (inst == null) continue;
                    MarketData md = new MarketData(inst);
                    md.Update += OnWatchdogTick;
                    _watchdogSubs.Add(md); n++;
                    _watchdogResolved.Add(name);
                }
                catch (Exception ex) { LogSafe("watchdog resub '" + name + "': " + ex.Message); }
            }
            _rolloverSource = source;
            if (n == 0)
            {
                // Nothing could be subscribed (names NinjaTrader does not know - e.g. a reload with "MNQ" for
                // "MNQ ##-##"). Clear the heartbeat clock as StartWatchdog does: a kept clock would age for ever,
                // read as a dead feed, and NT8 Guard's overwatch would restart NinjaTrader over a settings typo.
                Interlocked.Exchange(ref _lastDataTicks, 0);
                LogSafe("watchdog: none of [" + string.Join(",", _watchdogInstruments) + "] could be subscribed -> OFF");
                AlertRaise("watchlist-empty", WatchListEmptyMessage(), DateTime.UtcNow);
                return;
            }
            AlertClear("watchlist-empty", DateTime.UtcNow);
            LogSafe("watchdog: re-subscribed " + n + " L1 stream(s) on [" + string.Join(",", _watchdogResolved)
                    + "] (heartbeat clock preserved; ignoring snapshot echoes for "
                    + (int)_resubSnapshotGraceMs + "ms — only data arriving after that counts as alive)");
        }

        // Clean DISCONNECT of one connection on the UI dispatcher. Real Disconnect() releases the account
        // (no duplicate) but DISABLES running strategies on it — only reached for a genuinely dead feed.
        private void DoDisconnect(string name)
        {
            Connection conn = FindLiveConnection(name);
            if (conn == null) { LogSafe("recover disconnect: '" + name + "' not live -> nothing to disconnect"); return; }
            Connection c2 = conn;
            Action act = delegate { try { c2.Disconnect(); LogSafe("recover: Disconnect() fired on " + DescribeConn(c2)); } catch (Exception ex) { LogSafe("recover disconnect(" + name + "): " + ex.Message); } };
            try
            {
                Dispatcher disp = Globals.MainThreadDispatcher;
                if (disp != null) disp.BeginInvoke(act); else act();
            }
            catch (Exception ex) { LogSafe("recover disconnect dispatch(" + name + "): " + ex.Message); }
        }

        // Current health of a named connection (healthiest same-name object), or "gone" if absent.
        private string HealthOf(string name)
        {
            Connection c = FindLiveConnection(name);
            return c == null ? "gone" : Health(c);
        }

        // Final rung: today's loud human fallback + a cooldown; after the cooldown the ladder may restart.
        // Open positions or working orders on the accounts of ONE connection, or null when it is flat.
        // Fails CLOSED: anything unreadable counts as exposure, because the only action this gates is a
        // disconnect that cancels live brackets - guessing "flat" there is the expensive way to be wrong.
        private string DescribeExposure(string connName)
        {
            int positions = 0, working = 0, unreadable = 0, accounts = 0;
            try
            {
                List<Account> all = new List<Account>();
                try { lock (Account.All) { foreach (Account a in Account.All) all.Add(a); } }
                catch (Exception ex) { return "account list unreadable: " + ex.GetType().Name; }

                foreach (Account a in all)
                {
                    string owner = null;
                    try { owner = (a.Connection != null && a.Connection.Options != null) ? a.Connection.Options.Name : null; }
                    catch { unreadable++; continue; }
                    if (owner == null || owner != connName) continue;
                    accounts++;
                    try
                    {
                        lock (a.Positions) foreach (Position p in a.Positions)
                            if (p.MarketPosition != MarketPosition.Flat) positions++;
                    }
                    catch { unreadable++; }
                    try
                    {
                        lock (a.Orders) foreach (Order o in a.Orders)
                            if (o.OrderState == OrderState.Working || o.OrderState == OrderState.Accepted
                                || o.OrderState == OrderState.PartFilled) working++;
                    }
                    catch { unreadable++; }
                }
            }
            catch (Exception ex) { return "exposure check threw: " + ex.GetType().Name; }

            if (unreadable > 0)
                return positions + " position(s), " + working + " working order(s), and " + unreadable
                       + " unreadable account field(s) - treating as exposed";
            if (positions == 0 && working == 0) return null;
            return positions + " position(s), " + working + " working order(s) across " + accounts + " account(s)";
        }

        private void EscalateToAlert(double staleSec, DateTime now)
        {
            // Resolved names, not the configured "MNQ ##-##" pattern: the pattern in this alert on
            // 2026-09-22 read exactly like a contract roll that had failed, and cost a detour to rule out.
            AlertRaise("silent-stall", "SILENT FEED STALL (auto-recovery did not restore the feed): no L1 update from ["
                       + string.Join(",", _watchdogResolved) + "] for " + (int)staleSec
                       + "s while the connection reads Connected. Strategies are running BLIND on stale data — reconnect manually.", now);
            _ladderCooldownUntil = now.AddSeconds(_recoveryCooldownSeconds);
            _rung = 0; _rungAt = DateTime.MinValue;
        }

        // Heartbeat resumed: log which rung the recovery landed on, then clear the episode.
        private void LadderReset(DateTime now)
        {
            if (_stallSince == DateTime.MinValue && _rung == 0 && _ladderCooldownUntil == DateTime.MinValue) return;
            if (_stallSince != DateTime.MinValue)
            {
                string at = _rung == 1 ? "after RE-SUBSCRIBE (false stall — feed was alive; "
                                         + Interlocked.Read(ref _updatesSinceResub) + " post-snapshot L1 update(s))"
                            : _rung == 2 ? "during DISCONNECT" : _rung == 3 ? "after DISCONNECT+RECONNECT (strategies disabled)"
                            : _ladderCooldownUntil != DateTime.MinValue ? "during the ALERT cooldown" : "before rung 1";
                LogSafe("recover: heartbeat RESUMED " + at + ", " + (int)(now - _stallSince).TotalSeconds + "s after stall start");
                PostWebhook("auto-recover", "auto-recover: heartbeat resumed " + at);
                Emit("feed.recovered", "info", "Market data is back " + at, "rung", _rung.ToString(Inv),
                     "seconds", ((int)(now - _stallSince).TotalSeconds).ToString(Inv));
                if (_rung >= 2)   // the disconnect switched strategies off: refresh the overwatch's marker
                    Emit("feed.strategies-disabled", "warn", "Recovered after a disconnect: the strategies on '" + _ladderConn
                         + "' were switched off", "connection", _ladderConn, "reason", "recovered-after-disconnect");
            }
            // If a recovery disconnect disabled strategies, schedule a re-enable once the feed has settled
            // (heartbeat resumed = connection live; give the account a few seconds to fully register first).
            if (_reEnableAfterRecover && _savedEnabled.Count > 0)
            {
                _reEnableAt = now.AddSeconds(_reEnableSettleSeconds);
                LogSafe("recover: " + _savedEnabled.Count + " strateg(y/ies) to re-enable in " + (int)_reEnableSettleSeconds + "s (feed settling): " + string.Join(", ", _savedEnabled));
            }
            LadderResetQuiet();
        }

        // Fires the deferred re-enable once the post-recovery settle elapses: click each saved strategy's Enabled
        // checkbox. Runs every guardian tick; a no-op until _reEnableAt is set (by LadderReset) and reached.
        private void MaybeReEnableAfterRecover(DateTime now)
        {
            if (_reEnableAt == DateTime.MinValue || now < _reEnableAt) return;
            _reEnableAt = DateTime.MinValue;
            if (!_reEnableAfterRecover || _savedEnabled.Count == 0) return;
            var todo = new List<string>(_savedEnabled);
            LogSafe("recover: re-enabling " + todo.Count + " strateg(y/ies) after recovery: " + string.Join(", ", todo));
            int n = 0;
            try { n = ReEnableStrategies(todo, false); } catch (Exception ex) { LogSafe("recover re-enable: " + ex.Message); }
            PostWebhook("auto-recover", "auto-recover: re-enabled " + n + "/" + todo.Count + " strategies after recovery");
            Emit("feed.reenabled", n >= todo.Count ? "info" : "warn", "Re-enabled " + n + " of " + todo.Count + " strategies after the recovery",
                 "enabled", n.ToString(Inv), "total", todo.Count.ToString(Inv), "names", string.Join(",", todo));
            if (n >= todo.Count) _savedEnabled.Clear();
            else LogSafe("recover: " + (todo.Count - n) + " strateg(y/ies) NOT re-enabled — will retry on next feed recovery");
        }

        // Clear all ladder state WITHOUT the "heartbeat resumed" recovery log — used when a scheduled
        // close (not a recovery) ends an in-flight episode.
        private void LadderResetQuiet()
        {
            _rung = 0; _rungAt = DateTime.MinValue; _stallSince = DateTime.MinValue;
            _ladderCooldownUntil = DateTime.MinValue; _ladderConn = "";
        }

        // AutoReconnect.stall test trigger: force the stall flag for N seconds (file content, default 120)
        // so the alert path — or the FULL ladder when autoRecover is on — can be exercised on a healthy
        // feed without pulling the network. The forced window ignores IsMarketLikelyOpen by design.
        private void DoForceStall(string body, DateTime now)
        {
            double secs;
            if (!double.TryParse(body, NumberStyles.Any, Inv, out secs) || secs <= 0) secs = 120.0;
            _forceStallUntil = now.AddSeconds(secs);
            LogSafe("stall-test: FORCING stalled for " + (int)secs + "s (autoRecover="
                    + (_autoRecover ? "ON -> full ladder" : "off -> alert only") + ")");
        }

        // AutoReconnect.deafen test trigger: swallow REAL L1 updates for N seconds so the feed looks
        // genuinely dead. This is the HONEST ladder test — staleness accrues on its own and the real
        // classifier has to decide, so it exercises exactly the path that 399 real episodes took.
        // AutoReconnect.stall cannot do this: it forces the stalled flag, which masks the snapshot bug
        // (and is why rung 2 passed its live test while being unreachable in production).
        // Expect: stall at ~staleSeconds -> rung 1 re-subscribe -> 0 post-snapshot updates -> rung 2
        // DISCONNECT at +resubSettleSeconds -> rung 3 reconnect -> heartbeat resumes once deafen expires.
        private void DoDeafen(string body, DateTime now)
        {
            double secs;
            if (!double.TryParse(body, NumberStyles.Any, Inv, out secs) || secs <= 0) secs = 150.0;
            Interlocked.Exchange(ref _deafenUntilTicks, now.AddSeconds(secs).Ticks);
            LogSafe("deafen-test: swallowing ALL L1 updates for " + (int)secs + "s — the feed will look genuinely"
                    + " dead to the real classifier (autoRecover=" + (_autoRecover ? "ON -> expect rung 2 DISCONNECT" : "off -> expect alert only") + ")");
        }

        // ===== the Strategies grid: re-enabling the strategies a recovery disconnect switched off =====

        private const System.Reflection.BindingFlags AnyInstance =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // Recursive WPF visual-tree search for the first descendant whose type Name contains a substring.
        private System.Windows.DependencyObject FindDescendantByTypeName(System.Windows.DependencyObject root, string contains)
        {
            try
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    System.Windows.DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                    if (child == null) continue;
                    if (child.GetType().Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0) return child;
                    System.Windows.DependencyObject deeper = FindDescendantByTypeName(child, contains);
                    if (deeper != null) return deeper;
                }
            }
            catch { }
            return null;
        }

        private object GetMember(object o, Type t, string name)
        {
            try { var p = t.GetProperty(name, AnyInstance); if (p != null) return p.GetValue(o); } catch { }
            try { var f = t.GetField(name, AnyInstance); if (f != null) return f.GetValue(o); } catch { }
            return null;
        }
        private string StratName(object s)
        {
            if (s == null) return "(null)";
            try { var p = s.GetType().GetProperty("Name"); object n = p != null ? p.GetValue(s) : null; return n as string ?? s.GetType().Name; }
            catch { return s.GetType().Name; }
        }
        // Reach ControlCenter.Instance (NT8's real windows aren't in Application.Current.Windows).
        private object GetControlCenter()
        {
            try
            {
                Type cc = Type.GetType("NinjaTrader.Gui.ControlCenter, NinjaTrader.Gui");
                if (cc == null) return null;
                foreach (string sm in new[] { "Instance", "Current" })
                {
                    var sp = cc.GetProperty(sm, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                    if (sp != null) { object v = sp.GetValue(null); if (v != null) return v; }
                }
            }
            catch { }
            return null;
        }

        // ===== Battle 2 re-enable: run a function against the live StrategiesGrid, on the ControlCenter's OWN
        // dispatcher (thread-affine), with the Strategies tab realized (it virtualizes when inactive), then
        // restore the user's original tab. Synchronous (Dispatcher.Invoke) so callers can use the result. =====
        private T WithStrategiesGrid<T>(Func<object, T> fn)
        {
            object cc = GetControlCenter();
            if (cc == null) { LogSafe("grid: ControlCenter.Instance null"); return default(T); }
            System.Windows.Threading.Dispatcher disp = null;
            try { disp = ((System.Windows.Threading.DispatcherObject)cc).Dispatcher; } catch { }
            Func<T> body = delegate
            {
                object mtc = null; int savedIdx = -1; System.Reflection.PropertyInfo selProp = null;
                try
                {
                    System.Windows.DependencyObject dep = cc as System.Windows.DependencyObject;
                    mtc = GetMember(cc, cc.GetType(), "MainTabControl");
                    if (mtc == null) { LogSafe("grid: MainTabControl null"); return default(T); }
                    Type mt = mtc.GetType(); selProp = mt.GetProperty("SelectedIndex");
                    try { object si = GetMember(mtc, mt, "SelectedIndex"); if (si is int) savedIdx = (int)si; } catch { }
                    System.Windows.DependencyObject grid = dep != null ? FindDescendantByTypeName(dep, "StrategiesGrid") : null;
                    if (grid == null)
                    {
                        object items = GetMember(mtc, mt, "Items"); int count = 0;
                        try { var cp = items.GetType().GetProperty("Count"); if (cp != null) count = (int)cp.GetValue(items); } catch { }
                        for (int i = 0; i < count && grid == null; i++)
                        {
                            try { if (selProp != null) selProp.SetValue(mtc, i); } catch { continue; }
                            try { ((System.Windows.UIElement)mtc).UpdateLayout(); } catch { }
                            grid = dep != null ? FindDescendantByTypeName(dep, "StrategiesGrid") : null;
                        }
                    }
                    if (grid == null) { LogSafe("grid: StrategiesGrid not found (Strategies tab unrealizable)"); return default(T); }
                    return fn(grid);
                }
                catch (Exception ex) { LogSafe("grid: " + ex.Message); return default(T); }
                finally { try { if (mtc != null && selProp != null && savedIdx >= 0) { selProp.SetValue(mtc, savedIdx); ((System.Windows.UIElement)mtc).UpdateLayout(); } } catch { } }
            };
            // BOUNDED wait. This runs on the guardian timer thread while Tick() holds _gate, and `body`
            // touches the UI (tab activation + UpdateLayout). A bare disp.Invoke(body) has no timeout, so a
            // saturated UI dispatcher would block the guardian FOREVER holding _gate — every later Tick then
            // no-ops silently at Monitor.TryEnter and the watchdog is dead with no log. That path only became
            // reachable in production once the snapshot fix un-blocked rung 2 (2026-07-27), so it is guarded
            // here. On timeout we lose this grid read (at worst: strategies not re-enabled, logged) — never
            // the watchdog itself.
            try
            {
                if (disp != null && !disp.CheckAccess())
                    return disp.Invoke(body, DispatcherPriority.Normal, CancellationToken.None,
                                       TimeSpan.FromSeconds(_gridInvokeTimeoutSeconds));
                return body();
            }
            catch (TimeoutException)
            {
                LogSafe("grid dispatch: UI dispatcher did not respond in " + (int)_gridInvokeTimeoutSeconds
                        + "s (saturated) -> abandoning this grid read; guardian stays alive");
                return default(T);
            }
            catch (Exception ex) { LogSafe("grid dispatch: " + ex.Message); return default(T); }
        }

        // Enumerate grid.source entries → (StrategiesGridEntry, Strategy.Name, IsEnabled). Runs inside WithStrategiesGrid.
        private List<object[]> GridEntries(object grid)
        {
            var outp = new List<object[]>();
            try
            {
                object rows = GetMember(grid, grid.GetType(), "source");
                if (!(rows is System.Collections.IEnumerable)) return outp;
                foreach (object entry in (System.Collections.IEnumerable)rows)
                {
                    if (entry == null) continue;
                    object strat = GetMember(entry, entry.GetType(), "Strategy");
                    string name = StratName(strat);
                    object en = GetMember(entry, entry.GetType(), "IsEnabled");
                    bool enabled = en is bool && (bool)en;
                    outp.Add(new object[] { entry, name, enabled });
                }
            }
            catch (Exception ex) { LogSafe("GridEntries: " + ex.Message); }
            return outp;
        }

        // Names of strategies currently enabled in the grid (call BEFORE a disconnect that disables them).
        private List<string> CaptureEnabledStrategyNames()
        {
            return WithStrategiesGrid<List<string>>(delegate (object grid)
            {
                var names = new List<string>();
                foreach (object[] e in GridEntries(grid)) if ((bool)e[2]) names.Add((string)e[1]);
                LogSafe("grid: captured " + names.Count + " enabled strateg" + (names.Count == 1 ? "y" : "ies") + ": " + string.Join(", ", names));
                return names;
            }) ?? new List<string>();
        }

        // Re-enable the named strategies by setting each matching entry's IsEnabled=true (or dry-run log). Returns count set.
        // Re-enable the named strategies by CLICKING each row's Enabled checkbox (ButtonBase.OnClick — the only
        // path that actually starts the strategy; setting IsEnabled or executing the RoutedCommand only flips
        // grid state). Returns count now enabled. dryRun logs intent without clicking.
        private int ReEnableStrategies(List<string> names, bool dryRun)
        {
            if (names == null || names.Count == 0) return 0;
            return WithStrategiesGrid<int>(delegate (object grid)
            {
                int c = 0;
                foreach (string nm in names)
                {
                    if (dryRun)
                    {
                        bool found = false, enabled = false;
                        foreach (object[] e in GridEntries(grid)) if (string.Equals((string)e[1], nm, StringComparison.OrdinalIgnoreCase)) { found = true; enabled = (bool)e[2]; break; }
                        if (!found) LogSafe("grid: [dry-run] '" + nm + "' not in grid");
                        else if (enabled) LogSafe("grid: [dry-run] '" + nm + "' already enabled");
                        else { LogSafe("grid: [dry-run] would enable '" + nm + "'"); c++; }
                    }
                    else if (ToggleEnableCheckbox(grid, nm, true)) c++;
                }
                LogSafe("grid: re-enable " + (dryRun ? "(dry-run) " : "") + "done — " + c + "/" + names.Count + " enabled");
                return c;
            });
        }

        private void CollectToggleButtons(System.Windows.DependencyObject root, List<System.Windows.Controls.Primitives.ToggleButton> outp)
        {
            if (root == null) return;
            try
            {
                int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < n; i++)
                {
                    System.Windows.DependencyObject c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                    System.Windows.Controls.Primitives.ToggleButton tb = c as System.Windows.Controls.Primitives.ToggleButton;
                    if (tb != null) outp.Add(tb);
                    CollectToggleButtons(c, outp);
                }
            }
            catch { }
        }

        // Walk up the visual tree from an element, returning the Strategy.Name of the first DataContext that has one.
        private string RowNameOf(System.Windows.DependencyObject el)
        {
            try
            {
                System.Windows.DependencyObject cur = el;
                for (int hop = 0; hop < 14 && cur != null; hop++)
                {
                    System.Windows.FrameworkElement fe = cur as System.Windows.FrameworkElement;
                    if (fe != null && fe.DataContext != null)
                    {
                        object dc = fe.DataContext;
                        object s = GetMember(dc, dc.GetType(), "Strategy");
                        if (s == null) { object di = GetMember(dc, dc.GetType(), "DataItem"); if (di != null) s = GetMember(di, di.GetType(), "Strategy"); }
                        if (s != null) { string nm = StratName(s); if (!string.IsNullOrEmpty(nm) && nm != "(null)") return nm; }
                    }
                    cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
                }
            }
            catch { }
            return "";
        }

        // Drive the named row's real Enabled checkbox via ButtonBase.OnClick() (toggles IsChecked + raises
        // Checked/Unchecked + runs the command = a real user click). Only flips a DISABLED strategy ON (skips if
        // already enabled). The grid must be realized (WithStrategiesGrid activates it).
        private bool ToggleEnableCheckbox(object grid, string name, bool wantEnabled)
        {
            bool cur = false; object entry = null;
            foreach (object[] e in GridEntries(grid)) if (string.Equals((string)e[1], name, StringComparison.OrdinalIgnoreCase)) { entry = e[0]; cur = (bool)e[2]; break; }
            if (entry == null) { LogSafe("gridtoggle: entry '" + name + "' not found"); return false; }
            if (cur == wantEnabled) { LogSafe("gridtoggle: '" + name + "' already IsEnabled=" + cur + " — no click"); return true; }
            var boxes = new List<System.Windows.Controls.Primitives.ToggleButton>();
            CollectToggleButtons(grid as System.Windows.DependencyObject, boxes);
            var matches = new List<System.Windows.Controls.Primitives.ToggleButton>();
            foreach (var b in boxes) { string bnm = ""; try { bnm = b.Name; } catch { } if (bnm == "ExpansionIndicator") continue; if (string.Equals(RowNameOf(b), name, StringComparison.OrdinalIgnoreCase)) matches.Add(b); }
            LogSafe("gridtoggle: '" + name + "' cur=" + cur + " want=" + wantEnabled + "; " + boxes.Count + " checkboxes, " + matches.Count + " on this row");
            // The Enabled checkbox reflects enabled state (IsChecked==cur). Pick that one.
            System.Windows.Controls.Primitives.ToggleButton pick = null;
            foreach (var b in matches) { bool? ck = null; try { ck = b.IsChecked; } catch { } LogSafe("gridtoggle:   candidate IsChecked=" + (ck.HasValue ? ck.Value.ToString() : "null") + " type=" + b.GetType().Name); if (pick == null && ck.HasValue && ck.Value == cur) pick = b; }
            if (pick == null && matches.Count == 1) pick = matches[0];
            if (pick == null) { LogSafe("gridtoggle: could not identify the Enabled checkbox for '" + name + "'"); return false; }
            try
            {
                var onClick = typeof(System.Windows.Controls.Primitives.ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (onClick == null) { LogSafe("gridtoggle: ButtonBase.OnClick not found"); return false; }
                onClick.Invoke(pick, null);
                bool? now = null; try { now = pick.IsChecked; } catch { }
                LogSafe("gridtoggle: OnClick() on '" + name + "' Enabled checkbox -> IsChecked now " + (now.HasValue ? now.Value.ToString() : "null"));
                return true;
            }
            catch (Exception ex) { LogSafe("gridtoggle: OnClick: " + (ex.InnerException != null ? ex.InnerException.GetType().Name + " " + ex.InnerException.Message : ex.Message)); return false; }
        }

        // ===== alerts: file log + NT8 Alerts window + sound + optional webhook. Guardian thread, under _gate. =====

        // Raise one keyed alert condition; re-fires every _alertRepeatMinutes while it persists.
        private void AlertRaise(string key, string message, DateTime now)
        {
            DateTime last;
            if (_alertLast.TryGetValue(key, out last) && (now - last).TotalMinutes < _alertRepeatMinutes) return;
            _alertLast[key] = now;
            LogSafe("ALERT: " + message);
            Emit("feed.alert", "alert", message, "key", key);
            try { Log(message, LogLevel.Alert); } catch { }   // NT8 Alerts window + Control Center log
            try
            {
                if (!string.IsNullOrEmpty(_alertSound))
                {
                    string p = Path.IsPathRooted(_alertSound) ? _alertSound : Path.Combine(Globals.InstallDir, "sounds", _alertSound);
                    if (File.Exists(p)) Globals.PlaySound(p);
                }
            }
            catch { }
            PostWebhook(key, message);
        }

        // One-time "recovered" (log + webhook only) when a previously-alerted condition clears.
        private void AlertClear(string key, DateTime now)
        {
            if (!_alertLast.Remove(key)) return;
            LogSafe("ALERT cleared: " + key);
            Emit("feed.alert-cleared", "info", "Cleared: " + key, "key", key);
            PostWebhook("recovered", "recovered: " + key);
        }

        // Fire-and-forget JSON POST on a background thread; never blocks the guardian; failures logged.
        // HTTP headers cannot carry CR/LF and choke on non-ASCII (these messages contain em-dashes),
        // so anything going into X-Title is flattened to printable ASCII and capped.
        private static string HeaderSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(c >= 32 && c < 127 ? c : ' ');
            string flat = sb.ToString().Trim();
            return flat.Length > 120 ? flat.Substring(0, 120) : flat;
        }

        private void PostWebhook(string evt, string message)
        {
            if (string.IsNullOrEmpty(_webhookUrl)) return;
            string url = _webhookUrl;
            // "text" posts the bare human message, because push services that render the raw request
            // BODY as the notification (ntfy) otherwise show a JSON blob you have to read punctuation
            // out of at 3am. JSON stays the default so existing receivers keep parsing fields.
            bool textMode = string.Equals(_webhookFormat, "text", StringComparison.OrdinalIgnoreCase);
            bool isAlert = evt != "auto-recover" && evt != "recovered";   // everything else comes from AlertRaise
            string body = textMode
                        ? message
                        : "{\"source\":\"AutoReconnect\",\"event\":" + JsonStr(evt) + ",\"message\":" + JsonStr(message)
                          + ",\"utcTime\":" + JsonStr(DateTime.UtcNow.ToString("o")) + "}";
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Method = "POST";
                    req.ContentType = textMode ? "text/plain; charset=utf-8" : "application/json";
                    if (textMode)
                    {
                        // ntfy conventions; receivers that don't know these headers just ignore them.
                        try
                        {
                            req.Headers.Add("X-Title", HeaderSafe("NT8 " + (isAlert ? "ALERT" : "recovery") + " - " + evt));
                            req.Headers.Add("X-Priority", isAlert ? "4" : "3");
                            req.Headers.Add("X-Tags", isAlert ? "rotating_light" : "white_check_mark");
                        }
                        catch { }   // a receiver rejecting custom headers must never lose the alert body
                    }
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 10000;
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    req.ContentLength = bytes.Length;
                    using (Stream s = req.GetRequestStream()) { s.Write(bytes, 0, bytes.Length); }
                    using (WebResponse resp = req.GetResponse()) { }
                }
                catch (Exception ex) { LogSafe("webhook: " + ex.Message); }
            });
        }

        private static string JsonStr(string s) { return FeedGuardFiles.Json(s); }

        // One structured event for NT8 Guard (its overwatch and control panel):
        // <NT8>\NT8Guard\feedguard-events.jsonl. data = name, value, name, value... Any thread; never throws.
        private void Emit(string kind, string severity, string text, params string[] data)
        {
            try
            {
                string dir = Path.Combine(Globals.UserDataDir, "NT8Guard");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                FeedGuardFiles.AppendLine(Path.Combine(dir, "feedguard-events.jsonl"),
                                          FeedGuardFiles.EventLine(DateTime.UtcNow, kind, severity, text, data), FeedGuardFiles.MaxBytes);
            }
            catch { }
        }

        private void ClearState(string name)
        {
            _droppedSince.Remove(name);
            _attempts.Remove(name);
            _nextAt.Remove(name);
            _cooldownUntil.Remove(name);
        }

        private double Backoff(int attempt)
        {
            return attempt <= 1 ? _backoffBase : Math.Min(_backoffCap, _backoffBase * Math.Pow(2, attempt - 1));
        }

        private static string NameOf(Connection c)
        {
            try { return (c.Options != null && c.Options.Name != null) ? c.Options.Name : ""; } catch { return ""; }
        }
        private static string SafeName(ConnectOptions o) { try { return o.Name != null ? o.Name : ""; } catch { return ""; } }
        // Live health from BOTH the connection link (Status) AND the data feed (PriceStatus):
        //   parked     = Status Disconnected (user or login-failure)
        //   lost       = Status ConnectionLost (LINK dropped -> reconnectable)
        //   transition = anything Connecting/Disconnecting (don't act mid-transition)
        //   feedlost   = Status Connected but PriceStatus ConnectionLost (ALERT-ONLY)
        //   healthy    = both Connected
        private static string Health(Connection c)
        {
            try
            {
                ConnectionStatus s = c.Status;
                ConnectionStatus ps = c.PriceStatus;
                if (s == ConnectionStatus.Disconnected) return "parked";
                if (s == ConnectionStatus.ConnectionLost) return "lost";
                if (s == ConnectionStatus.Connecting || s == ConnectionStatus.Disconnecting
                    || ps == ConnectionStatus.Connecting || ps == ConnectionStatus.Disconnecting) return "transition";
                if (ps == ConnectionStatus.ConnectionLost) return "feedlost";   // s == Connected here
                if (s == ConnectionStatus.Connected && ps == ConnectionStatus.Connected) return "healthy";
                return "transition";   // unknown combo -> treat as transient (never act on a guess)
            }
            catch { return "transition"; }
        }
        // Never auto-manage: empty names, or excluded connections (Playback/replay + config "exclude").
        private bool IsExcluded(string name) { return string.IsNullOrEmpty(name) || _exclude.Contains(name); }

        // Optional config: <UserDataDir>\AutoReconnect.config.json (flat JSON, no external deps):
        //   {"connections":["My Broker"], "exclude":["Sim101"], "graceSeconds":20, "intervalSeconds":10,
        //    "maxAttempts":5, "cooldownSeconds":1800, "backoffBase":15, "backoffCap":300,
        //    "reconnect":true,   (false = a dropped link is only alerted - NT8 Guard's Watch only preset)
        //    "watchdogInstruments":["MNQ ##-##","MES ##-##"], "watchdogStaleSeconds":75, "rollDaysBeforeExpiry":8,
        //        ("##-##" = the front month, from NinjaTrader's roll dates or else the CME quarterly rule, re-checked
        //         daily; a literal "MNQ 12-26" still works but WILL expire on you - you are warned 7 days before)
        //    "alertRepeatMinutes":5, "alertSound":"Alert1.wav", "webhookUrl":"https://...",
        //    "autoRecover":false,   (true = re-subscribe -> clean disconnect+reconnect on a dead feed; DISABLES strategies)
        //    "resubSettleSeconds":20, "disconnectTimeoutSeconds":30, "injectSettleSeconds":45,
        //    "recoveryCooldownSeconds":600,
        //    "reEnableAfterRecover":true, "reEnableSettleSeconds":12,   (re-enable strategies the recovery disconnect disabled)
        //    "useSessionGate":true, "suppressInPlayback":true,   (suppressInPlayback: no silent-stall alert/ladder during a replay)
        //    "marketTimeZoneId":"Eastern Standard Time",
        //    "dailyBreakStartEt":"17:00", "dailyBreakEndEt":"18:00", "weeklyCloseEt":"17:00",
        //    "weeklyOpenEt":"18:00", "holidays":["2026-03-10","2026-03-11 12:30"]}   (EXTRA dates on top of CME's own,
        //        which are built in for every year: "date" = a full close, "date HH:mm" = closed from that ET time)
        // "connections": [] or omitted = watch ALL. "exclude" always includes "Playback".
        // false = there is no config file (nothing was read).
        private bool ReadConfig()
        {
            try
            {
                string path = Path.Combine(Globals.UserDataDir, "AutoReconnect.config.json");
                if (!File.Exists(path)) return false;
                string txt = File.ReadAllText(path);
                _graceSeconds = NumCfg(txt, "graceSeconds", _graceSeconds);
                _reconnect = BoolCfg(txt, "reconnect", _reconnect);
                _intervalSeconds = NumCfg(txt, "intervalSeconds", _intervalSeconds);
                _maxAttempts = (int)NumCfg(txt, "maxAttempts", _maxAttempts);
                _cooldownSeconds = NumCfg(txt, "cooldownSeconds", _cooldownSeconds);
                _backoffBase = NumCfg(txt, "backoffBase", _backoffBase);
                _backoffCap = NumCfg(txt, "backoffCap", _backoffCap);
                _watchdogStaleSeconds = NumCfg(txt, "watchdogStaleSeconds", _watchdogStaleSeconds);
                _rollDaysBeforeExpiry = (int)NumCfg(txt, "rollDaysBeforeExpiry", _rollDaysBeforeExpiry);   // the quarterly fallback rolls this many days before the 3rd Friday
                _alertRepeatMinutes = NumCfg(txt, "alertRepeatMinutes", _alertRepeatMinutes);
                _alertSound = StrCfg(txt, "alertSound", _alertSound);
                _webhookUrl = StrCfg(txt, "webhookUrl", _webhookUrl);
                _webhookFormat = StrCfg(txt, "webhookFormat", _webhookFormat);
                _autoRecover = BoolCfg(txt, "autoRecover", _autoRecover);
                _resubSettleSeconds = NumCfg(txt, "resubSettleSeconds", _resubSettleSeconds);
                _disconnectTimeoutSeconds = NumCfg(txt, "disconnectTimeoutSeconds", _disconnectTimeoutSeconds);
                _injectSettleSeconds = NumCfg(txt, "injectSettleSeconds", _injectSettleSeconds);
                _recoveryCooldownSeconds = NumCfg(txt, "recoveryCooldownSeconds", _recoveryCooldownSeconds);
                _resubSnapshotGraceMs = NumCfg(txt, "resubSnapshotGraceMs", _resubSnapshotGraceMs);
                _gridInvokeTimeoutSeconds = NumCfg(txt, "gridInvokeTimeoutSeconds", _gridInvokeTimeoutSeconds);
                _reEnableAfterRecover = BoolCfg(txt, "reEnableAfterRecover", _reEnableAfterRecover);
                _reEnableSettleSeconds = NumCfg(txt, "reEnableSettleSeconds", _reEnableSettleSeconds);
                _useSessionGate = BoolCfg(txt, "useSessionGate", _useSessionGate);
                _suppressInPlayback = BoolCfg(txt, "suppressInPlayback", _suppressInPlayback);
                _marketTzId = StrCfg(txt, "marketTimeZoneId", _marketTzId);
                _breakStartMin = MinOfDayCfg(txt, "dailyBreakStartEt", _breakStartMin);
                _breakEndMin   = MinOfDayCfg(txt, "dailyBreakEndEt", _breakEndMin);
                _weekCloseMin  = MinOfDayCfg(txt, "weeklyCloseEt", _weekCloseMin);
                _weekOpenMin   = MinOfDayCfg(txt, "weeklyOpenEt", _weekOpenMin);
                var extra = new List<FeedGuardClosure>();
                foreach (string h in ListCfg(txt, "holidays"))   // EXTRA closed dates; CME's own are FeedGuardCalendar's rules
                {
                    FeedGuardClosure w = FeedGuardCalendar.ParseExtra(h);
                    if (w != null) extra.Add(w);
                    else LogSafe("config: holidays entry '" + h + "' is not yyyy-MM-dd or yyyy-MM-dd HH:mm - ignored");
                }
                // The lists are built aside and swapped in whole (see _allow): a reader on another thread sees
                // the old set or the new one, never one half-built.
                var instruments = new List<string>();
                foreach (string nm in ListCfg(txt, "watchdogInstruments"))
                    if (nm.Length > 0) instruments.Add(nm);
                if (instruments.Count == 0)                                 // back-compat: old single "watchdogInstrument" string
                {
                    string one = StrCfg(txt, "watchdogInstrument", "");
                    if (one.Length > 0) instruments.Add(one);
                }
                var allow = new HashSet<string>();
                foreach (string nm in ListCfg(txt, "connections"))
                    if (nm.Length > 0) allow.Add(nm);
                var owners = new HashSet<string>();
                foreach (string nm in ListCfg(txt, "feedOwnerConnections"))
                    if (nm.Length > 0) owners.Add(nm);
                var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Playback" };
                foreach (string nm in ListCfg(txt, "exclude"))
                    if (nm.Length > 0) exclude.Add(nm);
                _extraClosed = extra;
                _watchdogInstruments = instruments;
                _allow = allow;
                _feedOwners = owners;
                _exclude = exclude;
            }
            catch (Exception ex) { LogSafe("ReadConfig: " + ex.Message); }
            return true;
        }

        private static double NumCfg(string json, string key, double dflt)
        {
            try
            {
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return dflt;
                i = json.IndexOf(':', i);
                if (i < 0) return dflt;
                int j = i + 1;
                while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
                int k = j;
                while (k < json.Length && (char.IsDigit(json[k]) || json[k] == '.' || json[k] == '-')) k++;
                double v;
                return double.TryParse(json.Substring(j, k - j), NumberStyles.Any, Inv, out v) ? v : dflt;
            }
            catch { return dflt; }
        }

        // Read a single JSON bool value: "key": true|false.
        private static bool BoolCfg(string json, string key, bool dflt)
        {
            try
            {
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return dflt;
                i = json.IndexOf(':', i);
                if (i < 0) return dflt;
                int j = i + 1;
                while (j < json.Length && (json[j] == ' ' || json[j] == '\t')) j++;
                if (string.CompareOrdinal(json, j, "true", 0, 4) == 0) return true;
                if (string.CompareOrdinal(json, j, "false", 0, 5) == 0) return false;
                return dflt;
            }
            catch { return dflt; }
        }

        // Read a config "HH:mm" (ET) value as minutes-of-day; falls back to dflt.
        private int MinOfDayCfg(string json, string key, int dflt)
        {
            string s = StrCfg(json, key, "");
            return s.Length == 0 ? dflt : ParseHhmm(s, dflt);
        }
        private static int ParseHhmm(string s, int dflt)
        {
            try
            {
                string[] p = s.Split(':');
                if (p.Length < 2) return dflt;
                int h = int.Parse(p[0].Trim(), Inv), m = int.Parse(p[1].Trim(), Inv);
                if (h < 0 || h > 24 || m < 0 || m > 59) return dflt;
                return h * 60 + m;
            }
            catch { return dflt; }
        }

        // Read a single JSON string value: "key": "value".
        private static string StrCfg(string json, string key, string dflt)
        {
            try
            {
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return dflt;
                i = json.IndexOf(':', i);
                if (i < 0) return dflt;
                int q1 = json.IndexOf('"', i + 1);
                if (q1 < 0) return dflt;
                int q2 = json.IndexOf('"', q1 + 1);
                if (q2 < 0) return dflt;
                return json.Substring(q1 + 1, q2 - q1 - 1).Trim();
            }
            catch { return dflt; }
        }

        private static List<string> ListCfg(string json, string key)
        {
            var outp = new List<string>();
            try
            {
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) return outp;
                int lb = json.IndexOf('[', i);
                if (lb < 0) return outp;
                int rb = json.IndexOf(']', lb);
                if (rb < 0) return outp;
                foreach (string part in json.Substring(lb + 1, rb - lb - 1).Split(','))
                {
                    string p = part.Trim().Trim('"').Trim();
                    if (p.Length > 0) outp.Add(p);
                }
            }
            catch { }
            return outp;
        }

        private void LogSafe(string msg)
        {
            try
            {
                if (_logPath != null)
                    FeedGuardFiles.AppendLine(_logPath, DateTime.UtcNow.ToString("o") + "  " + msg, FeedGuardFiles.MaxBytes);
            }
            catch { }
        }
    }

    // ===== BEGIN FeedGuardCalendar =====
    // CME equity-index holidays and early closes, computed by rule for any year. Pure (System only):
    // tools\Test-FeedGuardCalendar.ps1 cuts this block out of the file and checks it against CME's
    // published 2026 and 2027 calendars - keep it between the BEGIN/END marker lines.
    //
    // ! WHEN UNSURE, CLOSED. The calendar only decides when a quiet feed is NOT a stall. Calling an open
    //   hour "closed" costs a missed stall alert during it; calling a closed hour "open" lets the recovery
    //   ladder disconnect and reconnect a healthy connection - switching strategies off - into a market
    //   that is shut. So every early close counts from 13:00 ET (CME's earliest equity halt, 12:00 CT)
    //   even on days that trade until 13:15 ET, and Good Friday is a full close even in years CME opens
    //   briefly for a jobs report.
    //     full close on day D  = closed from D-1 17:00 ET to D 18:00 ET (New Year's Day, Good Friday,
    //                            Christmas - each on the day it is observed)
    //     early close on day D = closed from D 13:00 ET to D 18:00 ET (MLK, Presidents, Memorial,
    //                            Juneteenth, Independence, Labor, Thanksgiving and the day after,
    //                            Christmas Eve, July 3, and Dec 23 when Christmas is observed on a
    //                            Friday or a Monday)
    //   Weekends and the daily 17:00-18:00 ET break are IsScheduledClosed's own rules, not this block's.
    internal sealed class FeedGuardClosure
    {
        internal DateTime StartEt;   // inclusive, US-Eastern wall clock
        internal DateTime EndEt;     // exclusive
        internal string Name;
        internal bool Full;          // true = closed for the session, false = early close
    }

    internal static class FeedGuardCalendar
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private const int DailyCloseMin = 17 * 60, ReopenMin = 18 * 60, EarlyCloseMin = 13 * 60;

        // Every holiday window of one calendar year, in time order.
        internal static List<FeedGuardClosure> ForYear(int y)
        {
            var c = new List<FeedGuardClosure>();
            DateTime newYear = new DateTime(y, 1, 1);
            if (newYear.DayOfWeek == DayOfWeek.Sunday) AddFull(c, newYear.AddDays(1), "New Year's Day (observed)");
            else if (newYear.DayOfWeek != DayOfWeek.Saturday) AddFull(c, newYear, "New Year's Day");   // a Saturday one is not observed
            AddEarly(c, NthWeekday(y, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
            AddEarly(c, NthWeekday(y, 2, DayOfWeek.Monday, 3), "Presidents Day");
            AddFull(c, Easter(y).AddDays(-2), "Good Friday");
            AddEarly(c, LastWeekday(y, 5, DayOfWeek.Monday), "Memorial Day");
            AddEarlyObserved(c, new DateTime(y, 6, 19), "Juneteenth");
            DateTime july4 = new DateTime(y, 7, 4);
            if (july4.DayOfWeek >= DayOfWeek.Tuesday && july4.DayOfWeek <= DayOfWeek.Friday)
                AddEarly(c, new DateTime(y, 7, 3), "Day before Independence Day");
            AddEarlyObserved(c, july4, "Independence Day");
            AddEarly(c, NthWeekday(y, 9, DayOfWeek.Monday, 1), "Labor Day");
            DateTime thanksgiving = NthWeekday(y, 11, DayOfWeek.Thursday, 4);
            AddEarly(c, thanksgiving, "Thanksgiving");
            AddEarly(c, thanksgiving.AddDays(1), "Day after Thanksgiving");
            DateTime christmas = new DateTime(y, 12, 25);
            DateTime christmasObserved = Observed(christmas);
            if (christmasObserved != christmas)                          // Dec 24 is the observed day, or a Saturday
                AddEarly(c, new DateTime(y, 12, 23), "Christmas Eve (early close moved to Dec 23)");
            else if (christmas.DayOfWeek >= DayOfWeek.Tuesday && christmas.DayOfWeek <= DayOfWeek.Friday)
                AddEarly(c, new DateTime(y, 12, 24), "Christmas Eve");
            AddFull(c, christmasObserved, christmasObserved == christmas ? "Christmas" : "Christmas (observed)");
            c.Sort(delegate (FeedGuardClosure a, FeedGuardClosure b) { return a.StartEt.CompareTo(b.StartEt); });
            return c;
        }

        // Inside a window? This ET moment's year and the next (a New Year's Day window starts on Dec 31),
        // plus the config's extra dates.
        internal static bool IsHolidayClosed(DateTime et, IList<FeedGuardClosure> extras)
        {
            if (Inside(ForYear(et.Year), et)) return true;
            if (et.Month == 12 && Inside(ForYear(et.Year + 1), et)) return true;
            return extras != null && Inside(extras, et);
        }

        // The first window that has not ended yet, rules and extras together; null if there is none.
        internal static FeedGuardClosure Next(DateTime et, IList<FeedGuardClosure> extras)
        {
            var all = new List<FeedGuardClosure>(ForYear(et.Year));
            all.AddRange(ForYear(et.Year + 1));
            if (extras != null) all.AddRange(extras);
            FeedGuardClosure best = null;
            foreach (FeedGuardClosure w in all)
                if (w != null && w.EndEt > et && (best == null || w.StartEt < best.StartEt)) best = w;
            return best;
        }

        // "2026-11-26 13:00 ET Thanksgiving (early close, reopens 2026-11-26 18:00 ET)"
        internal static string Describe(FeedGuardClosure w)
        {
            if (w == null) return "";
            return w.StartEt.ToString("yyyy-MM-dd HH:mm", Inv) + " ET " + w.Name
                   + (w.Full ? " (closed, reopens " : " (early close, reopens ") + w.EndEt.ToString("yyyy-MM-dd HH:mm", Inv) + " ET)";
        }

        // One extra date from the config: "yyyy-MM-dd" = a full close that day; "yyyy-MM-dd HH:mm" = closed
        // from that ET time until the 18:00 ET reopen (until midnight when the time is 18:00 or later).
        // null for anything else - the caller logs it.
        internal static FeedGuardClosure ParseExtra(string text)
        {
            if (text == null) return null;
            string t = text.Trim();
            DateTime d;
            if (DateTime.TryParseExact(t, "yyyy-MM-dd", Inv, DateTimeStyles.None, out d))
                return Window(d.AddDays(-1).AddMinutes(DailyCloseMin), d.AddMinutes(ReopenMin), "extra date " + t, true);
            if (DateTime.TryParseExact(t, "yyyy-MM-dd HH:mm", Inv, DateTimeStyles.None, out d))
            {
                DateTime end = d.Date.AddMinutes(ReopenMin);
                if (end <= d) end = d.Date.AddDays(1);
                return Window(d, end, "extra date " + t, false);
            }
            return null;
        }

        // Easter Sunday (Gregorian; the anonymous "Meeus/Jones/Butcher" algorithm).
        internal static DateTime Easter(int y)
        {
            int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
            int m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31, day = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(y, month, day);
        }

        private static DateTime NthWeekday(int y, int month, DayOfWeek dow, int n)
        {
            DateTime first = new DateTime(y, month, 1);
            return first.AddDays(((int)dow - (int)first.DayOfWeek + 7) % 7 + 7 * (n - 1));
        }

        private static DateTime LastWeekday(int y, int month, DayOfWeek dow)
        {
            DateTime last = new DateTime(y, month, DateTime.DaysInMonth(y, month));
            return last.AddDays(-(((int)last.DayOfWeek - (int)dow + 7) % 7));
        }

        // A Saturday holiday is observed on the Friday before, a Sunday one on the Monday after.
        private static DateTime Observed(DateTime d)
        {
            if (d.DayOfWeek == DayOfWeek.Saturday) return d.AddDays(-1);
            if (d.DayOfWeek == DayOfWeek.Sunday) return d.AddDays(1);
            return d;
        }

        private static bool Inside(IList<FeedGuardClosure> windows, DateTime et)
        {
            foreach (FeedGuardClosure w in windows) if (w != null && et >= w.StartEt && et < w.EndEt) return true;
            return false;
        }

        private static FeedGuardClosure Window(DateTime start, DateTime end, string name, bool full)
        {
            return new FeedGuardClosure { StartEt = start, EndEt = end, Name = name, Full = full };
        }

        private static void AddFull(List<FeedGuardClosure> c, DateTime day, string name)
        {
            c.Add(Window(day.AddDays(-1).AddMinutes(DailyCloseMin), day.AddMinutes(ReopenMin), name, true));
        }

        private static void AddEarly(List<FeedGuardClosure> c, DateTime day, string name)
        {
            c.Add(Window(day.AddMinutes(EarlyCloseMin), day.AddMinutes(ReopenMin), name, false));
        }

        private static void AddEarlyObserved(List<FeedGuardClosure> c, DateTime day, string name)
        {
            DateTime o = Observed(day);
            AddEarly(c, o, o == day ? name : name + " (observed)");
        }
    }
    // ===== END FeedGuardCalendar =====

    // ===== BEGIN FeedGuardFiles =====
    // The add-on's own files: AutoReconnect.log and NT8 Guard's events file. Pure (System only):
    // tools\Test-FeedGuardEvents.ps1 cuts this block out of the file - keep it between the marker lines.
    internal static class FeedGuardFiles
    {
        // Rotate at 5 MB, keeping one previous copy (<file>.1) - the rule for every NT8 Guard log.
        internal const long MaxBytes = 5L * 1024 * 1024;

        // NinjaTrader's status thread, the market-data threads and the guardian all write: one at a time,
        // or two lines can interleave into one that no reader can parse.
        private static readonly object Gate = new object();

        // Append one line, rotating first when the file has reached maxBytes. Never throws: a log that
        // cannot be written must not take down the guardian that is writing it.
        internal static void AppendLine(string path, string line, long maxBytes)
        {
            lock (Gate)
            {
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.Length >= maxBytes)
                    {
                        string previous = path + ".1";
                        if (File.Exists(previous)) File.Delete(previous);
                        File.Move(path, previous);
                    }
                }
                catch { }   // a reader holding the file blocks the move: rotate next time - never lose the line for it
                // A reader opening the file (NT8 Guard's overwatch reads the events every minute) shares it
                // read-only for a moment: retry briefly. Kept short - this lock is held.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try { File.AppendAllText(path, line + Environment.NewLine); return; }
                    catch (IOException) { Thread.Sleep(15); }
                    catch { return; }   // no such folder, no permission: retrying cannot help
                }
            }
        }

        // One events line, the shape every NT8 Guard component writes (guard\app\lib\Guard.Events.psm1):
        //   {"utc":"2026-10-09T07:14:13.0000000Z","component":"feedguard","kind":"feed.stall",
        //    "severity":"info|warn|alert","text":"...","data":{"name":"value"}}
        // data = name, value, name, value... (strings).
        internal static string EventLine(DateTime utc, string kind, string severity, string text, IList<string> data)
        {
            var sb = new StringBuilder();
            sb.Append("{\"utc\":").Append(Json(utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)))
              .Append(",\"component\":\"feedguard\",\"kind\":").Append(Json(kind))
              .Append(",\"severity\":").Append(Json(severity))
              .Append(",\"text\":").Append(Json(text))
              .Append(",\"data\":{");
            for (int i = 0; data != null && i + 1 < data.Count; i += 2)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Json(data[i])).Append(':').Append(Json(data[i + 1]));
            }
            return sb.Append("}}").ToString();
        }

        // A JSON string literal, quotes included.
        internal static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
    // ===== END FeedGuardFiles =====

    // ===== BEGIN FeedGuardRollover =====
    // Which contract "MNQ ##-##" means today. Pure (System only): tools\Test-FrontMonth.ps1 cuts this
    // block out of the file - keep it between the marker lines.
    //
    // ! A CONTRACT MONTH IN THE WATCH LIST IS A TIME BOMB. 2026-09-18: "MNQ 09-26" the morning it expired
    //   never ticked again, read as a dead feed all session, and the ladder disconnected every ~110s - each
    //   time switching every strategy off. So "##-##" is resolved here and re-checked daily: from
    //   NinjaTrader's own roll dates when the add-on can read them, else by the CME quarterly rule.
    internal static class FeedGuardRollover
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // "MNQ ##-##" -> true, symbol "MNQ". A concrete month ("MNQ 12-26") or anything else -> false.
        internal static bool IsAlias(string configured, out string symbol)
        {
            symbol = null;
            if (configured == null) return false;
            string s = configured.Trim();
            if (!s.EndsWith(" ##-##", StringComparison.Ordinal)) return false;
            symbol = s.Substring(0, s.Length - " ##-##".Length).Trim();
            return symbol.Length > 0;
        }

        // Third Friday of a month - CME expiry for equity index futures.
        internal static DateTime ThirdFriday(int year, int month)
        {
            DateTime d = new DateTime(year, month, 1);
            int toFri = ((int)DayOfWeek.Friday - (int)d.DayOfWeek + 7) % 7;
            return d.AddDays(toFri + 14);
        }

        // The quarterly rule (Mar/Jun/Sep/Dec, expiry the 3rd Friday, roll rollDays before it): the
        // contract whose roll date is still ahead of today. Right for ES/NQ/YM/RTY and their micros -
        // NOT for monthly contracts like CL or GC, which need NinjaTrader's dates.
        internal static string Quarterly(string symbol, DateTime today, int rollDays)
        {
            int y = today.Year, m = today.Month;
            for (int i = 0; i < 8; i++)                       // at most two years of quarters
            {
                int qm = ((m - 1) / 3 + 1) * 3;               // next quarterly month >= m (3,6,9,12)
                DateTime expiry = ThirdFriday(y, qm);
                if (today.Date < expiry.AddDays(-rollDays).Date) return symbol + " " + expiry.ToString("MM-yy", Inv);
                m = qm + 1; if (m > 12) { m = 1; y++; }       // that quarter has rolled -> try the next
            }
            return symbol + " ##-##";   // unreachable in practice; leaves the alias so the failure shows in the log
        }

        // NinjaTrader's own roll schedule as (contract month, roll date) pairs: the contract of the latest
        // roll on or before today. null - and the caller falls back to the quarterly rule - when there is
        // no roll on or before today, or when that contract's month is already over (NinjaTrader's list
        // has not been brought up to date). The caller also runs PickFront on the answer: a contract can
        // expire well before its month is over (index futures on the 3rd Friday).
        internal static string FromSchedule(string symbol, DateTime today, IList<KeyValuePair<DateTime, DateTime>> rolls)
        {
            if (rolls == null) return null;
            bool found = false;
            DateTime bestRoll = DateTime.MinValue, month = DateTime.MinValue;
            foreach (KeyValuePair<DateTime, DateTime> r in rolls)
                if (r.Value.Date <= today.Date && (!found || r.Value > bestRoll)) { found = true; bestRoll = r.Value; month = r.Key; }
            if (!found) return null;
            if (today.Date >= new DateTime(month.Year, month.Month, 1).AddMonths(1)) return null;
            return symbol + " " + month.ToString("MM-yy", Inv);
        }

        // FromSchedule's answer, checked against that contract's expiry. NinjaTrader's Instrument.Expiry is
        // MONTH resolution - the first day of the expiry month (MNQ 03-27 -> 2027-03-01, though it trades to
        // the 19th) - so from that day on a schedule answer is not trusted: a list nobody brought up to date
        // would otherwise name the contract through its last days and past them, the 2026-09-18 failure.
        // null -> the caller's quarterly rule decides, and it is right for the whole expiry month. An
        // unknown expiry (default date) keeps the answer.
        internal static string PickFront(string fromSchedule, DateTime expiryMonth, DateTime today)
        {
            if (fromSchedule == null) return null;
            if (expiryMonth.Year >= 2000 && today.Date >= new DateTime(expiryMonth.Year, expiryMonth.Month, 1)) return null;
            return fromSchedule;
        }

        // A warning when a CONCRETE contract on the watch list ("MNQ 12-26") is about to expire, or has;
        // null otherwise. An expired contract never ticks again, which reads as a dead feed.
        // ! expiryMonth is NinjaTrader's Instrument.Expiry: MONTH resolution, the first day of the expiry
        //   month (MNQ 12-26 -> 2026-12-01, though it trades to the 18th). Read as a date it would call a
        //   live contract "EXPIRED" every day for weeks. So: warn from warnDays before the expiry month
        //   begins and through it (it ends somewhere in that month); "expired" only once the month is over.
        internal static string ExpiryNotice(string name, DateTime expiryMonth, DateTime today, int warnDays)
        {
            if (string.IsNullOrEmpty(name) || expiryMonth.Year < 2000) return null;
            DateTime monthStart = new DateTime(expiryMonth.Year, expiryMonth.Month, 1);
            if (today.Date < monthStart.AddDays(-warnDays)) return null;
            int sp = name.LastIndexOf(' ');
            string alias = (sp > 0 ? name.Substring(0, sp) : name) + " ##-##";
            string month = monthStart.ToString("MMMM yyyy", Inv);
            if (today.Date >= monthStart.AddMonths(1))
                return "WATCH LIST CONTRACT EXPIRED: " + name + " expired in " + month + " and never ticks again, so the feed guard"
                       + " reads it as a dead feed. Change the watch list to " + alias + ".";
            return "WATCH LIST CONTRACT EXPIRING: " + name + " expires in " + month + " (NinjaTrader gives the month, not the day)."
                   + " Change the watch list to " + alias + " so it rolls by itself.";
        }
    }
    // ===== END FeedGuardRollover =====

    // ===== BEGIN FeedGuardReload =====
    // What a changed watch list needs after AutoReconnect.reload. Pure (System only):
    // tools\Test-FeedGuardReload.ps1 cuts this block out of the file - keep it between the marker lines.
    internal static class FeedGuardReload
    {
        // "same" (nothing to do), "stop" (the list is now empty), "start" (nothing is subscribed yet),
        // "restart" (re-subscribe now), or "wait": a recovery is running. Its rung 1 IS a re-subscribe and
        // its later rungs weigh what the CURRENT subscriptions report, so swapping them mid-climb would
        // reset the evidence; the first tick after the episode ends applies the change.
        internal static string WatchPlan(IList<string> subscribedFrom, IList<string> configured, int subscriptions, int rung)
        {
            bool same = subscribedFrom.Count == configured.Count;
            for (int i = 0; same && i < configured.Count; i++)
                if (!string.Equals(subscribedFrom[i].Trim(), configured[i].Trim(), StringComparison.Ordinal)) same = false;
            if (same) return "same";
            if (rung > 0) return "wait";
            if (configured.Count == 0) return "stop";
            return subscriptions == 0 ? "start" : "restart";
        }
    }
    // ===== END FeedGuardReload =====
}
