from nt8bridge import perfwindow


def test_build_request_basic():
    req = perfwindow.build_perfwindow_request("id1", "Sim101")
    assert req["id"] == "id1"
    assert req["kind"] == "perfwindow"
    assert req["account"] == "Sim101"
    # booleans/dates are strings so the AddOn's quoted-string parser reads them.
    assert req["generate"] == "false"
    assert req["from"] == "" and req["to"] == ""


def test_build_request_generate_and_dates():
    req = perfwindow.build_perfwindow_request(
        "id2", "Sim101", generate=True, from_="2026-06-01", to="2026-07-13"
    )
    assert req["generate"] == "true"
    assert req["from"] == "2026-06-01"
    assert req["to"] == "2026-07-13"


def test_build_request_defaults_blank_account():
    req = perfwindow.build_perfwindow_request("id3")
    assert req["account"] == "" and req["generate"] == "false"
