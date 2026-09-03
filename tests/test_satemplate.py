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

from pathlib import Path

from nt8bridge import satemplate


def test_run_satemplate_writes_the_template_request(monkeypatch):
    captured = {}
    monkeypatch.setattr(satemplate.ntio, "ensure_bridge_dirs", lambda: (Path("trig"), Path("res")))
    monkeypatch.setattr(satemplate, "new_request_id", lambda: "rid7")
    monkeypatch.setattr(satemplate.ntio, "atomic_write_json",
                        lambda p, o: captured.update(path=p, obj=o))
    monkeypatch.setattr(satemplate.ntio, "poll_for_json",
                        lambda p, timeout=30.0: {"status": "ok", "applied": True})
    out = satemplate.run_satemplate(r"C:\t\VariantA.xml")
    assert captured["obj"]["kind"] == "satemplate"
    assert captured["obj"]["template"] == r"C:\t\VariantA.xml"
    assert captured["path"] == Path("trig") / "satemplate_rid7.json"
    assert out["status"] == "ok"


def test_run_satemplate_reads_its_own_result_file(monkeypatch):
    """The reply is picked up under the request's OWN prefix.

    A stage whose answer is filed under another kind's prefix polls a name that
    never appears, and the caller waits out its timeout while the work is long
    done - which is how a guard turns into the hang it exists to prevent.
    """
    polled = {}
    monkeypatch.setattr(satemplate.ntio, "ensure_bridge_dirs", lambda: (Path("t"), Path("r")))
    monkeypatch.setattr(satemplate, "new_request_id", lambda: "rid8")
    monkeypatch.setattr(satemplate.ntio, "atomic_write_json", lambda p, o: None)
    monkeypatch.setattr(satemplate.ntio, "poll_for_json",
                        lambda p, timeout=30.0: polled.update(path=p, timeout=timeout) or {})
    satemplate.run_satemplate("VariantB", timeout=12.5)
    assert polled["path"] == Path("r") / "satemplate_rid8.json"
    assert polled["timeout"] == 12.5


def test_run_satemplate_passes_a_bare_name_through_unchanged(monkeypatch):
    """Resolving a bare name is the AddOn's job, not this side's.

    It asks NinjaTrader for the strategy's template folder, which differs per bot
    (flat "Strategy\\Foo" vs nested "Strategy\\Foo.Foo"). Assembling that path
    here from a naming rule would be a second answer to the same question.
    """
    captured = {}
    monkeypatch.setattr(satemplate.ntio, "ensure_bridge_dirs", lambda: (Path("t"), Path("r")))
    monkeypatch.setattr(satemplate, "new_request_id", lambda: "rid9")
    monkeypatch.setattr(satemplate.ntio, "atomic_write_json",
                        lambda p, o: captured.update(obj=o))
    monkeypatch.setattr(satemplate.ntio, "poll_for_json", lambda p, timeout=30.0: {"status": "ok"})
    satemplate.run_satemplate("VariantC")
    assert captured["obj"]["template"] == "VariantC"
