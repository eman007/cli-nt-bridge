"""Client-side gates on the order path.

These are the cheap half of a two-gate design: the AddOn refuses independently, in NT,
with its own checks. Testing this half matters because it is the half that fires
WITHOUT touching NinjaTrader — a typo caught here never reaches a broker connection at
all, and it is the only half a contributor can exercise without a running platform.

⛔ Every test here asserts a REFUSAL. That is deliberate: the failure mode this verb
family has to survive is not "the order didn't go through", it is "an order went
through that nobody asked for".
"""
from __future__ import annotations

import pytest

from nt8bridge import order as ntorder


def _place(**kw):
    base = dict(account="Sim101", instrument="MES 09-26", side="Buy",
                type="Market", quantity=1, confirm=True)
    base.update(kw)
    return base


def test_unknown_action_is_refused():
    with pytest.raises(ValueError, match="unknown order action"):
        ntorder.run_order("obliterate", **_place())


@pytest.mark.parametrize("action", ntorder.MUTATING_ACTIONS)
def test_every_mutating_action_requires_confirm(action):
    kw = _place(confirm=False, orderId="x")
    with pytest.raises(ValueError, match="requires --confirm"):
        ntorder.run_order(action, **kw)


@pytest.mark.parametrize("action", ntorder.MUTATING_ACTIONS)
def test_every_mutating_action_requires_a_named_account(action):
    kw = _place(account="", orderId="x")
    with pytest.raises(ValueError, match="no default account"):
        ntorder.run_order(action, **kw)


def test_read_only_actions_do_not_require_confirm():
    # `list`/`api` must stay usable without ceremony, or people stop using the safe verbs.
    # They fail on transport here (no NT), never on validation.
    for action in ("api", "list"):
        with pytest.raises((TimeoutError, FileNotFoundError, OSError)):
            ntorder.run_order(action, timeout=0.01)


@pytest.mark.parametrize("side", ["", "Long", "buy_to_cover", "BUYY"])
def test_side_is_never_inferred(side):
    with pytest.raises(ValueError, match="requires --side"):
        ntorder.run_order("place", **_place(side=side))


@pytest.mark.parametrize("otype", ["", "market", "Stop", "Bracket"])
def test_type_must_be_one_nt_knows(otype):
    with pytest.raises(ValueError, match="requires --type"):
        ntorder.run_order("place", **_place(type=otype))


@pytest.mark.parametrize("qty", [None, 0, -1, "1", 1.5])
def test_quantity_must_be_a_positive_int(qty):
    with pytest.raises(ValueError, match="positive integer"):
        ntorder.run_order("place", **_place(quantity=qty))


def test_instrument_is_never_inferred():
    with pytest.raises(ValueError, match="requires --instrument"):
        ntorder.run_order("place", **_place(instrument=""))


# ⭐ The pair that matters most. A Limit with no price is not a rejected order — without
# this it is a MARKET order wearing a limit order's name, which is the single most
# expensive way for this path to "succeed".
@pytest.mark.parametrize("otype", ntorder.NEEDS_LIMIT)
def test_limit_types_require_a_limit_price(otype):
    kw = _place(type=otype, stopPrice=5000)
    with pytest.raises(ValueError, match="requires --limit-price"):
        ntorder.run_order("place", **kw)


@pytest.mark.parametrize("otype", ntorder.NEEDS_STOP)
def test_stop_types_require_a_stop_price(otype):
    kw = _place(type=otype, limitPrice=5000)
    with pytest.raises(ValueError, match="requires --stop-price"):
        ntorder.run_order("place", **kw)


def test_bad_tif_is_refused():
    with pytest.raises(ValueError, match="--tif must be one of"):
        ntorder.run_order("place", **_place(tif="GoodTillWhenever"))


def test_cancel_requires_a_target():
    # Silence must never mean "everything".
    with pytest.raises(ValueError, match="--order-id, or --all"):
        ntorder.run_order("cancel", account="Sim101", confirm=True)


def test_cancel_all_is_accepted_only_when_asked_for_explicitly():
    # Reaches transport (no NT here) => validation passed, which is the assertion.
    with pytest.raises((TimeoutError, FileNotFoundError, OSError)):
        ntorder.run_order("cancel", account="Sim101", confirm=True, all=True, timeout=0.01)


@pytest.mark.parametrize("action", ["change", "status"])
def test_change_and_status_require_an_order_id(action):
    with pytest.raises(ValueError, match="requires --order-id"):
        ntorder.run_order(action, account="Sim101", confirm=True)


# ⛔ REGRESSION. `change --limit-price` alone came back BADQTY from the AddOn because the
# CLI defaulted --quantity to 0, so "absent" arrived as an explicit zero. `change` treats
# absent as "leave alone"; for a PRICE the same bug would have repriced a resting order
# to 0. Found by driving a live resting order on Sim101, not by reading the code.
def test_absent_change_fields_are_absent_not_zero():
    req = ntorder.build_order_request("rid", "change", account="Sim101",
                                      orderId="abc", limitPrice=1200, confirm=True)
    assert req["limitPrice"] == "1200"
    assert req["quantity"] == "", "absent quantity must be empty, never '0'"
    assert req["stopPrice"] == "", "absent stopPrice must be empty, never '0'"


def test_confirm_and_all_are_sent_as_explicit_strings():
    req = ntorder.build_order_request("rid", "cancel", account="Sim101", confirm=True, all=True)
    assert req["confirm"] == "true" and req["all"] == "true"
    req = ntorder.build_order_request("rid", "cancel", account="Sim101", orderId="x")
    assert req["confirm"] == "false" and req["all"] == "false"
