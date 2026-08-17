"""log — server-side filtered log reads.

The rules under test are the ones that keep a log read from LYING:
  • a missing file is an error, never `ok` with zero matches
  • a `--since` that could not be applied says so instead of passing for a filtered result
  • a truncated read announces that its line numbers count from the window, not the file
"""
from nt8bridge import logread as ntlog


def test_build_request_shape():
    req = ntlog.build_log_request("id1", "trace/x.txt", grep="ERROR", since_min=30, tail=50)
    assert req["kind"] == "log"
    assert req["file"] == "trace/x.txt"
    assert req["grep"] == "ERROR"
    assert req["sinceMin"] == "30"
    assert req["tail"] == "50"
    assert req["ignoreCase"] == "false"


def test_build_request_defaults_are_explicit():
    """Every knob is sent, even at its default: the AddOn must never have to infer an omitted one."""
    req = ntlog.build_log_request("id2", "sentinel.log")
    assert req["grep"] == ""
    assert req["sinceMin"] == "0"
    assert req["maxBytes"] == str(8 * 1024 * 1024)
    assert req["maxLineChars"] == "2000"


def test_ignore_case_flag():
    assert ntlog.build_log_request("i", "f", ignore_case=True)["ignoreCase"] == "true"


def _payload(**over):
    p = {
        "status": "ok", "file": "C:\\NT8\\trace\\t.txt", "exists": True,
        "sizeBytes": 1024, "scannedLines": 900, "matched": 3, "returned": 3,
        "truncatedFromStart": False, "lineNumbersFrom": "file",
        "timeFilter": {"sinceMin": 0, "applied": False, "stampedLines": 900},
        "lines": [{"n": 10, "text": "a"}, {"n": 20, "text": "b"}, {"n": 30, "text": "c"}],
        "errors": [],
    }
    p.update(over)
    return ntlog.parse_log_response(p)


def test_parse_ok_result():
    res = _payload()
    assert res.ok is True
    assert res.matched == 3
    assert res.texts() == ["a", "b", "c"]


def test_missing_file_is_not_a_clean_read():
    """⭐ ABSENCE IS NOT EVIDENCE. 'no faults found' and 'that log lives elsewhere on this box'
    must never render alike — otherwise a mistyped path reads as an all-clear."""
    res = ntlog.parse_log_response({
        "status": "error", "exists": False, "matched": 0, "lines": [],
        "errors": [{"code": "NOTFOUND", "message": "no such file: X"}],
    })
    assert res.ok is False
    assert res.matched == 0
    assert res.errors[0]["code"] == "NOTFOUND"


def test_bad_regex_is_an_error_not_a_no_match():
    """A pattern that will not compile must fail loudly. Degrading to match-none reads all-clear;
    degrading to match-all buries the answer in noise. Both are worse than an error."""
    res = ntlog.parse_log_response({
        "status": "error", "exists": False, "matched": 0, "lines": [],
        "errors": [{"code": "BADREGEX", "message": "pattern did not compile: ("}],
    })
    assert res.ok is False
    assert res.errors[0]["code"] == "BADREGEX"


def test_unapplied_time_filter_is_surfaced():
    """A log with no parseable stamps cannot honour --since. Returning everything while APPEARING
    filtered is the same defect class as a probe that can only pass one way."""
    res = _payload(timeFilter={"sinceMin": 30, "applied": False, "stampedLines": 0,
                               "note": "no line carried a parseable timestamp — --since was NOT applied"})
    assert res.time_filter_applied is False
    assert "NOT applied" in res.note


def test_applied_time_filter_reports_true():
    res = _payload(timeFilter={"sinceMin": 30, "applied": True, "stampedLines": 812})
    assert res.time_filter_applied is True
    assert res.note == ""


def test_truncated_read_is_flagged():
    res = _payload(truncatedFromStart=True, lineNumbersFrom="window", windowStartByte=99000)
    assert res.truncated is True


def test_returned_may_be_less_than_matched_when_tailed():
    """`tail` caps what travels, not what matched. Conflating them would under-report a busy log."""
    res = _payload(matched=5000, returned=200, lines=[{"n": 1, "text": "x"}])
    assert res.matched == 5000
    assert res.returned == 200
