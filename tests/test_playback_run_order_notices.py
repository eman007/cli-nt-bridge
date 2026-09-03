"""The "order notices" step of the play loop: what the driver reads out of it."""
from nt8bridge.playback_run import order_notice_counts


def test_counts_are_read_from_the_step_text():
    assert order_notice_counts("1 dismissed this sample, 3 in this run") == (1, 3)
    assert order_notice_counts("0 dismissed this sample, 0 in this run") == (0, 0)


def test_absent_or_foreign_step_text_claims_nothing():
    assert order_notice_counts("") == (0, 0)
    assert order_notice_counts(None) == (0, 0)
    assert order_notice_counts("yes - the strategy has terminated") == (0, 0)
