# MIT License
# Copyright (c) 2026 Quantrosoft Pty. Ltd.
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.

"""Satemplate: put one of NinjaTrader's own strategy templates on the SA tab.

`configure` sets individual properties from a config.json. That is the right
shape for a handful of parameters and the wrong one for the files NinjaTrader
writes itself under templates\\Strategy: those carry the COMPLETE parameter set,
and a test suite is usually one strategy class against many of them, identical
in everything else. Naming the file beats restating hundreds of properties, and
it cannot drift from what the GUI runs, because it IS what the GUI runs.

    python -m nt8bridge satemplate --template "C:\\...\\VariantA.xml"
    python -m nt8bridge backtest   --config run.json     # then fire Run

A bare name is looked up in the strategy's own template folder, which
NinjaTrader is asked for rather than assembled from a naming rule; a full path
is taken as given.

THE STRATEGY IS SELECTED FIRST WHEN IT HAS TO CHANGE

Writing the tab's `Strategy` makes NinjaTrader install a fresh StrategyTemplate
and silently drop whatever was on the old one (see configure.py and issue #6).
So when the template belongs to a different class than the tab holds, the AddOn
selects the class BEFORE assigning - the other order loses the whole template
without an error. `switchedFrom` in the response names the class that was
replaced, and is empty when nothing was switched.

THE INSTRUMENT TRAVELS WITH THE TEMPLATE

NinjaTrader does not hand it over by itself. Measured on a running instance:
after assigning a template naming NQ 06-26, the strategy read NQ 06-26 and the
tab still read ES 09-26 - and it is the TAB's instrument that the Analyzer runs.
Left alone, that applies one template's parameters to another instrument and
reports nothing. So when the template names an instrument, the AddOn writes it
to the tab as well.

Response:
  {id, status, template, strategy, tabStrategy, instrument, from, to,
   switchedFrom, applied}, plus `params` when applied is true and `error`
  when it is false

`instrument`, `from` and `to` are the three values the run will actually be
carried out with, read back after the writes - the instrument off the TAB, the
dates off the template. `applied` is a reference comparison: the instance read
back off the tab has to be the very one that was assigned. A property that
accepts a write and quietly keeps its own object would otherwise pass as
success, and the run afterwards would use the wrong parameters while every step
reported ok.
"""
from __future__ import annotations

from nt8bridge import ntio
from nt8bridge.compile import new_request_id


def run_satemplate(template: str, timeout: float = 30.0) -> dict:
    trigger, result = ntio.ensure_bridge_dirs()
    rid = new_request_id()
    ntio.atomic_write_json(
        trigger / f"satemplate_{rid}.json",
        {"id": rid, "kind": "satemplate", "template": template},
    )
    return ntio.poll_for_json(result / f"satemplate_{rid}.json", timeout=timeout)
