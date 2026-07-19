"""Probe: dump every public read/write property of the SA tab + tab.TabStrategyProperties
+ the configured strategy template. Used to DISCOVER property names before writing a
`configure` config — NT8's SA tab members are partly obfuscated so naive guessing wastes
cycles. Read-only; doesn't fire Run.

Output schema:
  {id, status, tab:{_type, properties:[{name,type,canWrite,value,repr}]}, tabStrategyProperties:..., strategyTemplate:...}
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def run_probe(timeout: float = 30.0) -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(trigger / f"probe_{rid}.json", {"id": rid, "kind": "probe"})
    return ntio.poll_for_json(result / f"probe_{rid}.json", timeout=timeout)
