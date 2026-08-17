"""Download missing MarketReplay .nrd files via the marketReplayDownload bridge kind.

The AddOn drives NT8's own RequestMarketReplay(instrument, dateEst, callback, ...) per date
(the same reflection path as the community MultidayReplayDownloader), writing
db/replay/<instrument>/<yyyyMMdd>.nrd. This client loops a date range, skips Saturdays (no
session), the CURRENT/future day (its replay is only partial until the session closes and NT8
processes it), and — by default — dates whose .nrd already exists, so it's cheap to re-run.
Pair with `histdump` to then convert the freshly-downloaded .nrd to CSV/parquet."""
from __future__ import annotations

import uuid
from datetime import date as _date, datetime as _datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from nt8bridge import ntio


def download_one(instrument: str, date: str, timeout: float = 600.0) -> dict:
    """Fire one marketReplayDownload (date = YYYYMMDD, ET) and return the bridge result dict.

    `timeout` is the AddOn's per-date wait, passed through as "timeoutSec"; the client poll waits a bit
    longer so it always outlives the AddOn's own timeout result (heavy MNQ days run 300-460s)."""
    rid = uuid.uuid4().hex[:12]
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(
        trigger / f"marketReplayDownload_{rid}.json",
        {"id": rid, "kind": "marketReplayDownload", "instrument": instrument, "date": date,
         "timeoutSec": str(int(timeout))},
    )
    return ntio.poll_for_json(result / f"marketReplayDownload_{rid}.json", timeout=timeout + 60)


def _iter_dates(from_str: str, to_str: str):
    d = _date(int(from_str[:4]), int(from_str[4:6]), int(from_str[6:8]))
    end = _date(int(to_str[:4]), int(to_str[4:6]), int(to_str[6:8]))
    while d <= end:
        yield d
        d += timedelta(days=1)


def nt_replay_dir() -> Path:
    """Where the AddOn ACTUALLY writes. This is not configurable from the client: the bridge
    request carries only instrument and date, and NT8 writes into its own db\\replay."""
    return ntio.nt8_root() / "db" / "replay"


def _snap(p: Path):
    """(size, mtime_ns) of a file, or None if absent — the before/after of a download."""
    try:
        st = p.stat()
        return (st.st_size, st.st_mtime_ns)
    except OSError:
        return None


def run_histget(*, instrument: str, from_date: str, to_date: str, skip_existing: bool = True,
                replay_dir=None, write_dir=None, timeout: float = 600.0, today=None) -> dict:
    """Download each session date in [from_date, to_date] (YYYYMMDD, ET) for one instrument.

    The CURRENT day and any future date are never downloaded — their replay data is only partial
    until the session closes and NT8 processes it — so the effective latest date is yesterday.
    `today` is the cutoff (a datetime.date, exclusive); it defaults to the current ET date
    (America/New_York — matching NT8's ET session dates regardless of the machine's own timezone,
    e.g. a PST box in the evening is already the next ET day) and is injectable for tests. With
    `skip_existing=False` (the CLI `--force`), dates that already have a .nrd are re-downloaded and
    overwritten.

    ⛔ `replay_dir` IS THE DIRECTORY WE *CHECK*, NOT THE ONE WE WRITE TO. It cannot be anything
    else: `download_one` sends the AddOn an instrument and a date and nothing more, and NT8 writes
    into its own db\\replay. Measured 2026-08-09 — a date requested with `replay_dir` pointed at an
    empty scratch directory landed in the LIVE corpus while the scratch stayed empty. A caller who
    read the old help text ("db/replay dir") as a sandbox was driving writes into the corpus of
    record. The write path is now returned as `write_dir` on every result so no caller has to guess,
    and the CLI refuses a `--replay-dir` that is not the real one.

    ⛔ AND A SUCCESSFUL REPLY IS NOT A DOWNLOAD. The AddOn answers `exists`, which is equally true
    for a file that was already on disk and never fetched — so every existing date was reported as
    `downloaded`. Each date is now stat'd in the REAL write directory before and after, and only a
    file that appeared or changed counts. A re-download that produced byte-identical output is
    reported `unchanged`, not `downloaded`: we cannot prove a fetch we cannot see, and under-claiming
    is the safe direction to be wrong in.
    """
    today = today or _datetime.now(ZoneInfo("America/New_York")).date()
    check_dir = Path(replay_dir) if replay_dir else nt_replay_dir()
    wdir = Path(write_dir) if write_dir else nt_replay_dir()
    inst_dir = check_dir / instrument
    downloaded: list[str] = []
    unchanged: list[str] = []
    skipped: list[str] = []
    skipped_current: list[str] = []
    failed: list[dict] = []

    for d in _iter_dates(from_date, to_date):
        ds = d.strftime("%Y%m%d")
        if d >= today:                  # current/future day -> partial data, never download
            skipped_current.append(ds)
            continue
        if d.weekday() == 5:            # Saturday — no session data
            continue
        nrd = inst_dir / f"{ds}.nrd"
        if skip_existing and nrd.exists():
            skipped.append(ds)
            continue
        target = wdir / instrument / f"{ds}.nrd"
        before = _snap(target)
        try:
            res = download_one(instrument, ds, timeout=timeout)
        except TimeoutError as e:
            failed.append({"date": ds, "error": f"timeout: {e}"})
            continue
        if res.get("status") != "ok" or not res.get("exists"):
            failed.append({"date": ds,
                           "error": res.get("error") or res.get("message") or "download failed"})
            continue
        after = _snap(target)
        if after is None:
            failed.append({"date": ds, "error": "the bridge reported success but no .nrd exists at "
                                                f"{target} — nothing was written"})
        elif before is None or before != after:
            downloaded.append(ds)
        else:
            unchanged.append(ds)

    return {"status": "ok", "instrument": instrument, "from": from_date, "to": to_date,
            "today": today.strftime("%Y%m%d"),
            "write_dir": str(wdir), "check_dir": str(check_dir),
            "downloaded": downloaded, "unchanged": unchanged, "skipped": skipped,
            "skipped_current": skipped_current, "failed": failed,
            "count": len(downloaded)}
