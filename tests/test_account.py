from nt8bridge import account


def test_build_request_includes_account_filter():
    req = account.build_account_request("id1", "Sim101")
    assert req["id"] == "id1"
    assert req["kind"] == "account"
    assert req["account"] == "Sim101"


def test_build_request_defaults_to_all_accounts():
    req = account.build_account_request("id1")
    assert req["account"] == ""


def test_parse_ok_response_exposes_accounts_and_lookup():
    res = account.parse_account_response(
        {
            "id": "id1",
            "status": "ok",
            "accounts": [
                {
                    "name": "Sim101",
                    "realizedPnl": 204.0,
                    "unrealizedPnl": 0.0,
                    "positions": [],
                    "workingOrders": [],
                    "recentExecutions": [
                        {"instrument": "MNQ 06-26", "marketPosition": "Long",
                         "price": 28780.5, "orderName": "Profit target"}
                    ],
                }
            ],
        }
    )
    assert res.ok is True
    acct = res.account("Sim101")
    assert acct is not None
    assert acct["realizedPnl"] == 204.0
    assert acct["recentExecutions"][0]["price"] == 28780.5
    assert res.account("Nope") is None


def test_parse_error_response_carries_errors():
    res = account.parse_account_response(
        {"id": "id1", "status": "error", "errors": [{"message": "NT8 down"}]}
    )
    assert res.ok is False
    assert res.errors[0]["message"] == "NT8 down"


def test_open_position_visible_when_substream_would_have_stalled():
    """The exact gap this command closes: an open position an upstream feed missed."""
    res = account.parse_account_response(
        {
            "status": "ok",
            "accounts": [
                {
                    "name": "Sim101",
                    "positions": [
                        {"instrument": "MNQ 06-26", "marketPosition": "Long",
                         "quantity": 1, "avgPrice": 28678.5, "unrealizedPnl": 204.0}
                    ],
                    "workingOrders": [],
                    "recentExecutions": [],
                }
            ],
        }
    )
    pos = res.account("Sim101")["positions"]
    assert len(pos) == 1 and pos[0]["marketPosition"] == "Long" and pos[0]["quantity"] == 1


def test_run_account_state_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio

    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(account, "new_request_id", lambda: "ac1")
    trigger, result = ntio.ensure_bridge_dirs()
    ntio.atomic_write_json(
        result / "account_ac1.json",
        {"id": "ac1", "status": "ok", "accounts": [{"name": "Sim101"}]},
    )
    payload = account.run_account_state("Sim101", timeout=1.0)
    assert payload["status"] == "ok"
    assert payload["accounts"][0]["name"] == "Sim101"
    # the request the AddOn will consume was written with the account filter
    import json
    req = json.loads((trigger / "account_ac1.json").read_text(encoding="utf-8"))
    assert req["kind"] == "account" and req["account"] == "Sim101"
