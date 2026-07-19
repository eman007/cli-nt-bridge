"""Download missing MarketReplay .nrd files via the marketReplayDownload bridge kind.

The AddOn drives NT8's own RequestMarketReplay(instrument, dateEst, callback, ...) per date
(the same reflection path as the community MultidayReplayDownloader), writing
db/replay/<instrument>/<yyyyMMdd>.nrd. This client loops a date range, skips Saturdays (no
session) and — by default — dates whose .nrd already exists, so it's cheap to re-run. Pair with
`histdump` to then convert the freshly-downloaded .nrd to CSV/parquet."""
from __future__ import annotations

import uuid
from datetime import date as _date, timedelta
from pathlib import Path

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


def run_histget(*, instrument: str, from_date: str, to_date: str, skip_existing: bool = True,
                replay_dir=None, timeout: float = 600.0) -> dict:
    """Download each session date in [from_date, to_date] (YYYYMMDD) for one instrument."""
    replay_dir = Path(replay_dir) if replay_dir else (ntio.nt8_root() / "db" / "replay")
    inst_dir = replay_dir / instrument
    downloaded: list[str] = []
    skipped: list[str] = []
    failed: list[dict] = []

    for d in _iter_dates(from_date, to_date):
        if d.weekday() == 5:            # Saturday — no session data
            continue
        ds = d.strftime("%Y%m%d")
        nrd = inst_dir / f"{ds}.nrd"
        if skip_existing and nrd.exists():
            skipped.append(ds)
            continue
        try:
            res = download_one(instrument, ds, timeout=timeout)
        except TimeoutError as e:
            failed.append({"date": ds, "error": f"timeout: {e}"})
            continue
        if res.get("status") == "ok" and res.get("exists"):
            downloaded.append(ds)
        else:
            failed.append({"date": ds, "error": res.get("error") or res.get("message") or "download failed"})

    return {"status": "ok", "instrument": instrument, "from": from_date, "to": to_date,
            "downloaded": downloaded, "skipped": skipped, "failed": failed,
            "count": len(downloaded)}
