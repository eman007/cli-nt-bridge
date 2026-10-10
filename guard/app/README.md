# NT8 Guard — watchdogs (PowerShell)

Windows PowerShell 5.1 scripts that keep NinjaTrader 8 running. They ship as source, run as scheduled
tasks under your own Windows account, and need no administrator rights. `Install.cmd` (in the release zip,
beside `app\`) copies them into Guard's folder - see "Installing and updating" below.

| Script | What it does | Runs |
|---|---|---|
| `Guard-Relaunch.ps1` | NinjaTrader is **down** → puts back any strategies it dropped (and back on their charts), then starts it and logs in. Stuck at the login window → stops it so the next start can log in (after 6 minutes; sooner only for a NinjaTrader Guard itself started). | task, every minute |
| `Guard-Overwatch.ps1` | NinjaTrader is **up but sick** (frozen, its add-ons stopped, no market data with the market open) → proves it over 3 minutes, then restarts it. After any restart: reconnects what was connected and switches back on what was running. Saves every strategy's settings every 30 minutes. Starts NinjaTrader itself only if the relauncher has stopped. | task, every minute |
| `Guard-LogArchive.ps1` | Copies NinjaTrader's logs before a restart recycles them; keeps 7 days. | task, every 10 minutes |
| `Guard-Login.ps1` | Starts NinjaTrader and enters the saved password. `-SetPassword` saves it; `-Check` tests it. | called by the relauncher |
| `Register-GuardTasks.ps1` | Installs the tasks for the current user and proves each one runs. `-Remove` uninstalls. | by hand / installer |
| `Guard-Run.vbs` | Starts a script with no window (no flashing console every minute). | used by the tasks |
| `Guard-Panel.ps1` | The control panel: what every piece is doing, and the settings. | when you open it |
| `Install-Guard.ps1` | Installs or updates Guard from an extracted release (run by `Install.cmd`). | by hand |

`lib\` holds the shared modules: folders (`Guard.Paths`), settings (`Guard.Config`), events
(`Guard.Events`), phone alerts (`Guard.Alerts`), pause (`Guard.Pause`), the one NinjaTrader start
routine and its rules (`Guard.Start`), the overwatch's helpers (`Guard.Overwatch`), the feed guard
add-on's settings (`Guard.FeedGuard`), what Guard asks the NT8 link add-on and the "you are flat" check
every button that closes NinjaTrader or cuts its feed runs first (`Guard.Nt8Link`), those buttons
themselves (`Guard.Actions`), the saved NinjaTrader password (`Guard.Password`), the setup wizard's checks
and its last step (`Guard.Setup`), logs (`Guard.Log`), what the control panel shows
(`Guard.Status`: every piece read from the files it writes, never by asking it, and without side
effects - so the panel never stamps a file or sends an alert by looking) and what its buttons do
(`Guard.Panel`). `panel\` holds the panel's window (`MainWindow.xaml`), its theme (`Theme.xaml`) and
fonts (IBM Plex, SIL Open Font License - `panel\fonts\OFL.txt`).

## The control panel

    wscript.exe Guard-Run.vbs Guard-Panel.ps1                 (no console window)
    powershell -STA -ExecutionPolicy Bypass -File Guard-Panel.ps1 [-Tab Alerts]
    powershell -STA -ExecutionPolicy Bypass -File Guard-Panel.ps1 -Wizard       (the setup wizard)

| Tab | What it is for |
|---|---|
| **Status** | One card per piece - NinjaTrader, its market data, the auto-login, the two watchdogs, the two add-ons, the strategy backups, phone alerts - green, amber or red, with what to do about anything not green. Recent events below. **Pause** here. |
| **Protection** | The preset (Watch only / Recover NT8 / Hands-off) and each switch on its own. Off means Guard only alerts. |
| **Alerts** | Phone alerts through ntfy: a private random topic, a QR code to subscribe, how loud each kind of alert is, an optional webhook, and Send a test. The topic and the webhook address stay hidden until you press Show (a webhook address carries its own key). |
| **Strategy backups** | Every strategy Guard has saved: account, instrument, chart, when it was saved, and whether it is on NinjaTrader's list, missing (dropped across a restart) or removed by you. Save now; **Put back now** a missing one (or all of them) and **Bring back** one you removed - see below; Forget one that is gone for good. |
| **Watch list** | The instruments the feed guard watches, how long a silence counts as a dead feed, extra closed days - and what the feed guard made of the list right now (current contracts, the connection serving them, the next closure). |
| **Tools** | Test alert, pause, **Test login**, the **fire drill**, a diagnostics zip for a bug report (alert address, Windows user name and PC name masked; no password or strategy settings), the folders, and **Uninstall**. |

The panel only **reads** what the watchdogs and add-ons write (every 2 seconds, off the window's
thread) and writes settings, the pause and the backup list when you ask. It does no watchdog work, so
closing it changes nothing. Each tab's **Save** writes only that tab's settings, checked first: a value
the watchdogs or the feed guard add-on could not read is refused with the reason and nothing is
written. Saving also rewrites the feed guard's own settings file and tells it to re-read it (below).

A button that fails says so in the window's footer and in `logs\panel.log`; the window stays open. A panel
that cannot start at all writes why to `logs\panel.log` and shows it in a message box.

`-ScreenshotDir DIR` renders every tab to `DIR\<Tab>.png` without showing a window. The QR code is
drawn by the bundled Python (`..\python\qr_png.py`, segno - BSD licence, in `..\python\vendor\`).

### The setup wizard

Seven steps, each one revisitable later (Tools > Setup, or the banner "Setup is not finished" until it is):

1. **Check this PC** - NinjaTrader's version (8.1.6.3 and later green, other 8.1 amber, 8.0 red), its
   folders, Guard's Python, display scaling, whether Windows signs in by itself after a restart, and
   whether something else already restarts NinjaTrader (an older relaunch or overwatch task, cli-nt-bridge's
   `watchdog`, another copy of Guard). That last one is red, and **Start Guard refuses** until you disable
   it: two watchdogs would each restart NinjaTrader and fight over it.
2. **NinjaTrader login** - tick *Remember username* in NinjaTrader once; choose Guard's own login or your
   own login program; save the password (it goes from the box straight to the encrypted file - never into
   a log or an event); optionally **Test login**.
3. **NinjaTrader add-ons** - import `NT8Guard-AddOns.zip` in NinjaTrader; **Next waits** until both add-ons
   have reported from it (the feed guard at this Guard's version).
4. **Watch list**, 5. **Phone alerts** (can be skipped), 6. **Protection** - the ordinary tabs; Next saves.
7. **Start Guard** - installs the three scheduled tasks (each run once and proven by its heartbeat), sends
   a test alert if alerts are set up, and marks setup complete. Then the fire drill is offered.

### The buttons that touch NinjaTrader

**Put back now**, **Bring back**, **Test login** and the **fire drill** each ask first, then list their
steps as they happen. Each first pauses Guard and waits (up to a minute) for the overwatch to finish
whatever it is doing, holding it off until the action ends; then it asks the NT8 link add-on whether you
are **flat** - no position on any account, no working order, no strategy running (a strategy counts as
running by its own state, not just its tick) - and stops if you are not, or if the NT8 link cannot answer
or answers something that cannot be true, such as no account at all ("can't confirm you're flat").
NinjaTrader closed, or every NinjaTrader at its login window, counts as flat. None of them runs while
Guard is logging NinjaTrader in.

**Strategies you switched off to be flat stay off.** Once the check passes, Guard empties the overwatch's
record of running strategies, so neither the restart nor the drill's disconnect switches back on what you
switched off. Switch them on again yourself when you are ready.

- **Put back now / Bring back** pause Guard, close NinjaTrader **abruptly, on purpose** (a normal close
  drops switched-off strategies from its list - the very thing this repairs), put the strategy back from
  its backup and onto its chart (Protection's *Put them back on their charts* decides the chart part), end
  the pause, and let the relauncher start NinjaTrader within a minute. Save your workspace in NinjaTrader
  first if you changed charts since it started: an abrupt close does not save it. A strategy on
  NinjaTrader's list right now is refused before anything is touched.
- **Test login** starts NinjaTrader through the login helper, the way every restart does, and reports
  whether it logged in (the built-in helper's own result; for your own login program, NinjaTrader's window
  leaving *Welcome*). A running NinjaTrader is closed abruptly first. Refused while the password is on
  hold after NinjaTrader refused it twice: save the right one first (each try is another wrong attempt
  with your broker).
- **Fire drill** (market open, add-ons 5.0.0 or later) makes the feed guard ignore market data for the
  stale time plus a minute, by dropping `AutoReconnect.deafen` in NinjaTrader's folder, then follows the
  feed guard's own events: it notices, re-subscribes, disconnects and reconnects the connection serving the
  watch list, and the data comes back - with the phone alerts a real outage sends. With *Recover a silent
  feed* on alert only, it checks the alert and the all-clear instead. On a weekend, connect NinjaTrader
  to its **Simulated Data Feed**: when every watched instrument is served by it, the feed guard turns the
  market calendar off (that feed never closes - its heartbeat says `simulatedFeed: true`), so the drill runs.

A pause you set yourself is left as it is; the pause an action sets ends with it. The panel will not
close while one of these runs. Each one is recorded in Recent events.

**Uninstall** removes the three tasks, the Start-menu shortcut and - once the window closes - Guard's
folder (settings, saved password, logs, the backup list). It never touches NinjaTrader: the two add-ons
stay until you delete them in NinjaTrader's NinjaScript Editor (the result says which), and the strategy
backup templates stay in NinjaTrader's templates folder. A task it cannot remove keeps Guard's folder in
place, or that task would run a missing script every minute. It deletes a folder only if it looks like
Guard's, and never a drive root, your profile, a Windows folder, NinjaTrader's own folders, a folder that
holds any of those, or one where NinjaTrader's files sit.

## When Guard will NOT start NinjaTrader

Every start goes through the same rules, whichever part of Guard asks:

- **Paused** (see below).
- **The screen is locked.** The password cannot reach NinjaTrader's window behind the lock screen, so
  Guard waits and starts it within a minute of the unlock.
- **Backoff.** 4 starts or stops within 30 minutes and NinjaTrader still is not staying up → no starts
  for 60 minutes, then 120, 240 … for each further backoff that day (24 hours at most). A phone alert
  says so. Hammering a failing login is what gets a broker account flagged.
- **The password was refused twice.** If two logins in a row left NinjaTrader at its login window
  after the password went in, Guard stops starting it until you save the password again (the control
  panel: Tools > NinjaTrader login) or log in by hand once.
- **Another start is already running** (only one at a time).

Only NinjaTrader and the lock screen of **your own** Windows session count.

## What the overwatch does

Every minute, while NinjaTrader is running, it checks signs of life that **stop** when something
hangs - the feed guard's and the NT8 link's heartbeats, whether NinjaTrader's window still answers,
and how old the last market-data tick is (only while the market is open). A problem must persist for 3
checks in a row before it acts, and after a restart it waits 20 minutes before restarting again. Each
problem pages your phone **once** (again after 6 hours if it is still there), not every minute.

| Problem | What happens |
|---|---|
| Window frozen **and** an add-on stopped, or an add-on's heartbeat stale | Restarts NinjaTrader (Protection: *Restart NT8 when it freezes*) |
| Window frozen, add-ons alive (usually you compiling a script) | Alert only - never restarted |
| No market data with the market open, and the feed guard could not fix it | Restarts NinjaTrader - but holds off while the feed guard is still trying, or when it blamed the data provider itself (a restart cannot fix that), up to a ceiling. A CME holiday or early close counts as closed: the feed guard's calendar says so in its heartbeat. Its verdicts come from its events file; a feed guard older than 5.0.0 writes none and is read from its log instead |
| An add-on has not run at all since NinjaTrader started (not loaded, removed, or it failed to compile after an update) | Alert only - a restart cannot fix that |
| The screen is locked, or *Start NinjaTrader when it is down* is off | Never restarts (nothing could bring NinjaTrader back); alerts instead |

After **any** restart, and after a connection drop that switched strategies off, it:

1. reconnects the connections that were connected before, **one at a time** (two connects close
   together can leave a strategy running twice) - Protection: *Reconnect dropped connections*;
2. switches back on the strategies that were running - Protection: *Re-enable strategies* (alert only
   in Recover NT8). Before switching one on it checks that account's position: a position still
   protected by working orders is left alone (switching the strategy on would cancel them); a position
   with nothing protecting it is closed first - Protection: *Flatten an unprotected position first*.
   When either is set to alert only, the alert still says, per strategy, whether its account has no
   position, a protected one, or an **UNPROTECTED** one.

It also watches NinjaTrader's log for a strategy started **twice** (both copies trade, so positions
can be double size) and alerts at once - only a NinjaTrader restart clears the extra copy.

With *Save a crash report* on, it saves a memory dump of a frozen NinjaTrader before restarting it
(`dumps\`, newest 2 kept, each can be several GB).

`Guard-Overwatch.ps1 -Report` prints what it sees as JSON and changes nothing; `-TestAlert` pushes a
test alert from this PC.

## How the password is entered

1. If Windows UI Automation can reach NinjaTrader's password box, the password is set **directly in
   the box** and the Log In button is pressed — no keyboard, no clipboard.
2. Otherwise the keyboard is used, but **only after checking that NinjaTrader's window is in front with
   the keyboard focus**. Windows often refuses to bring a window forward while you are typing in
   another app; then nothing is typed (it would land in your chat or browser), and you get an alert.
   The clipboard copy used for pasting is marked to stay out of clipboard history and cloud sync, and
   is cleared right after.

Every login result is recorded; every failure is a phone alert that says what to do.

## Where things live

`%LOCALAPPDATA%\NT8Guard\` (override with `NT8GUARD_HOME`):

- `guard.json` — settings (the control panel writes it; every key has a default)
- `guard.json.lastgood` — the last settings that loaded cleanly. If `guard.json` is damaged or a value
  in it is unusable, Guard uses this copy for the broken parts (never silently switching your
  protection preset), logs which settings were affected, and sends one alert.
- `secrets\nt8.cred` — your NinjaTrader password, encrypted for **this Windows user on this PC only**
- `state\` — heartbeats, pause, backoff, the strategy backups, and the overwatch's records of what
  was running and connected (`overwatch.strategies.json`, `overwatch.connections.json`)
- `events\*.jsonl` — what happened, one line per event (the panel's "Recent events")
- `logs\` — one log per component, plus `logs\nt8\` with archived NinjaTrader logs and
  `overwatch-strategies.log` (every change to which strategies were running)
- `dumps\` — crash reports, only if switched on
- `app\` and `python\` — the program and its bundled Python (Python 3.14 embeddable, with the backup
  tools); `NT8Guard-AddOns.zip`, the setup guide and `VERSION` beside them - all replaced by an update

NinjaTrader's folder is found the way Windows reports your Documents folder (so a OneDrive-moved
Documents works), or from `nt8.dataDir` in `guard.json`.

## Installing and updating

Extract `NT8Guard-v<version>.zip` (right-click, *Extract All*), then double-click `Install.cmd` in the
extracted folder. It unblocks the files (a download from the internet marks each one), copies `app\` and
`python\` into Guard's folder, adds the Start-menu shortcut **NT8 Guard**, and opens the setup wizard.

**Updating** is the same: extract the new zip and run its `Install.cmd`. Your settings, saved password,
logs, events and strategy backups are kept; the program is swapped in whole (copied beside the old one,
then two renames), so a task never starts a half-copied program. It refuses while the control panel is
open or while NinjaTrader is being started - close the window, or wait a minute, and run it again. If the
add-ons in the new release are newer, Status says so: import the new `NT8Guard-AddOns.zip` in NinjaTrader.

## Before first use

Run the setup wizard (above): `powershell -STA -ExecutionPolicy Bypass -File Guard-Panel.ps1 -Wizard`.
Without the panel, the same in three commands:

1. In NinjaTrader's login window, tick **Remember username** once. Only the password is entered.
2. Save the password: `powershell -ExecutionPolicy Bypass -File Guard-Login.ps1 -SetPassword`
3. Install the tasks: `powershell -ExecutionPolicy Bypass -File Register-GuardTasks.ps1`

## Settings without the control panel

The panel writes `guard.json`. Without it, create `%LOCALAPPDATA%\NT8Guard\guard.json` by hand. Only
list what you change; everything else keeps its default:

```json
{
  "alerts":     { "ntfyTopic": "pick-a-long-random-name" },
  "python":     { "exe": "C:\\Path\\To\\python.exe" },
  "protection": { "preset": "recover" }
}
```

- `alerts.ntfyTopic` — your phone alerts (install the ntfy app and subscribe to the same name). Treat
  the name like a password: anyone who knows it can read your alerts.
- `python.exe` — `auto` (the default) uses the Python bundled in Guard's `python\` folder. Set a path
  only to use another Python 3 (a checkout without the bundled runtime; the tests run on 3.14). Without any Python
  the start still happens, and the log says the backups could not run.
- `login.clearCacheBeforeLaunch` (off by default) empties NinjaTrader's `cache`, `db\cache` and `tmp`
  folders before a start. It never touches `db\day`, `db\minute` or `db\tick` (your historical data).

The feed guard add-on inside NinjaTrader has its own settings file, `AutoReconnect.config.json` in
NinjaTrader's folder. Guard writes it from `guard.json`, keeps any other key you put there yourself, and
tells the running add-on to re-read it within 10 seconds - no NinjaTrader restart:

```powershell
Import-Module .\lib\Guard.Config.psm1, .\lib\Guard.FeedGuard.psm1
Write-FeedGuardConfig -Config (Get-GuardConfig)
```

| guard.json | AutoReconnect.config.json |
|---|---|
| `watch.instruments`, `watch.staleSeconds`, `watch.marketTimeZone` | `watchdogInstruments`, `watchdogStaleSeconds`, `marketTimeZoneId` |
| `watch.extraDates` | `holidays` (extra closed dates; CME's own holidays are built in) |
| Protection: *Reconnect dropped connections* | `reconnect` |
| Protection: *Recover a silent feed* | `autoRecover` |
| Protection: *Re-enable strategies* | `reEnableAfterRecover` |
| `alerts.ntfyTopic` (else `alerts.webhookUrl`) | `webhookUrl` + `webhookFormat` |

The add-on reads that file with a deliberately tiny parser (it does not unescape text), so a value
of Guard's own holding a quote or a backslash is refused rather than written; your own keys are
written back exactly as the add-on read them. If the existing file cannot be read at all, Guard
keeps it as `AutoReconnect.config.json.bak` before writing a fresh one - copy your own settings back
from it.

## Pausing

Closing NinjaTrader on purpose? Pause first (the panel's Status tab: 30 min, 2 h or 8 h), or the
relauncher starts it again within a minute. A pause always ends by itself (24 hours at most):

```powershell
Import-Module .\lib\Guard.Pause.psm1
Set-GuardPause -Minutes 30 -Reason 'maintenance'
Clear-GuardPause
```

## Protection presets

| Setting | Watch only | Recover NT8 (default) | Hands-off |
|---|---|---|---|
| Start NinjaTrader when it is down | alert | on | on |
| Put back dropped strategies, on their charts | alert | on | on |
| Re-enable strategies / flatten unprotected positions | alert | alert | on |

## The NinjaTrader add-ons

Guard's watchdogs read two add-ons that run inside NinjaTrader: the **feed guard** (`AutoReconnect.cs`:
market-data watchdog, reconnects, recovery ladder, events and heartbeat) and the **NT8 link**
(`NT8BridgeServer.cs` + `NT8BridgeServerPlayback.cs` - one add-on in two files, always both). Build the
file NinjaTrader imports:

    powershell -NoProfile -ExecutionPolicy Bypass -File ..\build\Build-AddOnsZip.ps1

then in NinjaTrader: **Tools > Import > NinjaScript Add-On** > `guard\build\out\NT8Guard-AddOns.zip`.
Importing compiles NinjaScript, which restarts running strategies - do it while flat.

## Tests

```
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-GuardTests.ps1               # unit tests
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Run-GuardTests.ps1 -Integration  # + real Task Scheduler
```

Every test uses temporary folders; none touches your NinjaTrader folder, your Guard home or your
real tasks. The integration test registers throwaway tasks and removes them.

`Test-GuardPanel.ps1` builds the panel's window from fixture files, presses its buttons, and renders
every tab to a PNG (set `NT8GUARD_SHOT_DIR` to keep them); it also runs `Guard-Panel.ps1 -ScreenshotDir`
in its own process. The QR-code checks need Python on PATH (`py` or `python`) and print SKIP without it.

The suite also runs the feed guard add-on's own tests (`..\..\tools\Test-FeedGuard*.ps1`, plus
`Test-StallGate`, `Test-LadderTarget` and `Test-FrontMonth`). They compile real code cut out of
`addon\AutoReconnect.cs` with the C# 5 compiler, so they cannot pass against a stale copy of it. Two
check against this PC's NinjaTrader (the whole add-on compiles against its assemblies; the members it
reads by reflection exist) and print SKIP where NinjaTrader is not installed.

`..\build\Find-PersonalStrings.ps1` checks a folder for personal strings before publishing. It reads
every file, so point it at what will ship (the files git tracks), not a working tree with generated
`__pycache__` files.
