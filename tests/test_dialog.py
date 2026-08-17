"""dialog — the rules that keep an automated click from being worse than no click.

Three of these encode refusals. A tool that answers dialogs on an unattended box is only safe if it
would rather return an error than press the wrong button, because pressing the wrong button on a
rollover prompt is unrecoverable and returning an error never is.
"""
from nt8bridge import dialog as ntdialog


def test_build_list_request():
    req = ntdialog.build_dialog_request("id1")
    assert req["kind"] == "dialog"
    assert req["action"] == "list"
    assert req["title"] == "" and req["button"] == ""


def test_build_dismiss_request():
    req = ntdialog.build_dialog_request("id2", "dismiss", title="Rollover", button="No", wait_ms=8000)
    assert req["action"] == "dismiss"
    assert req["title"] == "Rollover"
    assert req["button"] == "No"
    assert req["waitMs"] == "8000"


def test_hwnd_is_sent_as_string_and_omitted_when_absent():
    assert ntdialog.build_dialog_request("i", "dismiss", hwnd=12345)["hwnd"] == "12345"
    assert ntdialog.build_dialog_request("i")["hwnd"] == ""


def _state(**over):
    p = {
        "status": "ok", "action": "list", "count": 2, "dismissed": False,
        "dialogs": [
            {"hwnd": 111, "title": "Auto Rollover", "class": "#32770", "modal": True,
             "buttonSource": "win32", "buttons": ["Yes", "No"]},
            {"hwnd": 222, "title": "Tips", "class": "HwndWrapper", "modal": False,
             "buttonSource": "wpf", "buttons": ["Close"]},
        ],
        "errors": [],
    }
    p.update(over)
    return ntdialog.parse_dialog_response(p)


def test_modals_are_separated_from_mere_windows():
    """A non-modal dialog is a window; a modal one is a stopped machine. Only the second is an
    incident, and a pre-flight that treats them alike either cries wolf or misses the block."""
    st = _state()
    assert len(st.dialogs) == 2
    assert [d["title"] for d in st.modals] == ["Auto Rollover"]


def test_describe_lists_buttons_so_the_next_command_can_be_written():
    st = _state()
    text = st.describe()
    assert "Auto Rollover" in text and "Yes, No" in text and "MODAL" in text


def test_describe_says_no_dialogs_rather_than_empty_string():
    assert ntdialog.parse_dialog_response({"status": "ok", "dialogs": []}).describe() == "no dialogs"


def test_dialog_with_no_buttons_found_says_so():
    """`buttons: []` from a WPF window whose tree could not be walked must not read as 'a dialog
    with no buttons' silently — the summary names the gap."""
    st = _state(dialogs=[{"hwnd": 1, "title": "Odd", "modal": True, "buttons": []}])
    assert "no named buttons" in st.describe()


# ---- the refusals ----

def test_ambiguous_title_is_refused_not_resolved():
    """Two matches means the tool does not know which machine state you meant. Taking the first is
    how you answer the wrong prompt."""
    st = ntdialog.parse_dialog_response({
        "status": "error", "action": "list", "dismissed": False,
        "dialogs": [{"hwnd": 1, "title": "Confirm A", "buttons": ["OK"]},
                    {"hwnd": 2, "title": "Confirm B", "buttons": ["OK"]}],
        "errors": [{"code": "REFUSED", "message": "AMBIGUOUS: 2 dialogs match — nothing was clicked"}],
    })
    assert st.ok is False
    assert "nothing was clicked" in st.errors[0]["message"]


def test_missing_button_argument_is_refused():
    """There is deliberately no default answer. The default on a rollover prompt is the one that
    spends the holdout."""
    st = ntdialog.parse_dialog_response({
        "status": "error", "action": "dismiss", "dismissed": False, "dialogs": [],
        "errors": [{"code": "NOBUTTON", "message": "dismiss requires --button; there is no default answer"}],
    })
    assert st.ok is False
    assert st.errors[0]["code"] == "NOBUTTON"


def test_no_match_reports_that_nothing_was_clicked():
    st = ntdialog.parse_dialog_response({
        "status": "error", "action": "dismiss", "dismissed": False, "dialogs": [],
        "errors": [{"code": "NOMATCH", "message": "no dialog matching 'X' — nothing was clicked"}],
    })
    assert st.dismissed is False
    assert "nothing was clicked" in st.errors[0]["message"]


# ---- verify the outcome, never the call ----

def test_successful_dismiss_is_reported_from_the_window_being_gone():
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "dismiss", "dismissed": True, "dialogs": [],
        "clickedVia": "win32 BM_CLICK", "verdict": "dialog is gone", "errors": [],
    })
    assert st.dismissed is True
    assert st.clicked_via == "win32 BM_CLICK"


def test_a_click_that_changed_nothing_is_not_success():
    """⭐ THE RULE THIS COMMAND EXISTS TO HONOUR. Posting a message proves only that it was queued.
    Every failure this project keeps re-paying for is a call that RESOLVED while doing nothing —
    deploy hooks reporting Success, SetForegroundWindow returning false into an empty catch."""
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "dismiss", "dismissed": False, "dialogs": [],
        "clickedVia": "wpf Click event",
        "verdict": "CLICK POSTED BUT THE DIALOG IS STILL UP after 5000ms — treat this as NOT dismissed",
        "errors": [],
    })
    assert st.ok is True          # the command ran
    assert st.dismissed is False  # ...and it did not work, which is the answer that matters
    assert "STILL UP" in st.verdict


# ---- button labelling (found against the real "About NinjaTrader" dialog) ----

def test_unlabelled_controls_are_counted_not_named():
    """⭐ A TYPE NAME IS NOT A LABEL. An icon button's Content is a Shape, and Content.ToString()
    dutifully returns "System.Windows.Shapes.Path" — which then appears as if it were a caption you
    could click by name. The real About dialog produced three of those beside its one real OK.
    Inventing a matchable label for an unlabelled control is worse than admitting there is none:
    a caller could match it, and would be clicking blind."""
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "list", "count": 1,
        "dialogs": [{"hwnd": 1, "title": "About NinjaTrader", "modal": True,
                     "buttonSource": "wpf", "buttons": ["OK"], "unlabelledButtons": 3}],
    })
    text = st.describe()
    assert "OK" in text
    assert "+3 unlabelled" in text
    assert "System.Windows" not in text


def test_a_dialog_with_only_unlabelled_controls_does_not_read_as_unanswerable():
    """'no buttons' and 'buttons we could not name' are different claims — the second one still has
    something to click, via --hwnd and the UI."""
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "list", "count": 1,
        "dialogs": [{"hwnd": 2, "title": "Odd", "modal": True, "buttons": [], "unlabelledButtons": 2}],
    })
    assert "no named buttons (+2 unlabelled)" in st.describe()


# ---- scope: a NON-modal window still blocks an unattended box ----

def test_scope_defaults_to_modal_and_can_widen():
    assert ntdialog.build_dialog_request("i")["scope"] == "modal"
    assert ntdialog.build_dialog_request("i", scope="all")["scope"] == "all"


def test_non_modal_windows_are_not_incidents_but_are_still_reported():
    """⭐ THE MODAL-ONLY SCAN GAVE A CLEAN BILL OF HEALTH TO A MACHINE WITH TWO PROMPTS ON SCREEN.
    A sentry was sitting with an `Error` box and an `Auto Rollover Notification` open; neither
    disables its owner, so neither counted as modal and `dialog` answered "no dialogs" on all six
    boxes. `modals` stays the incident list; `dialogs` must show everything you asked to see."""
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "list", "count": 2,
        "dialogs": [{"hwnd": 1, "title": "Error", "modal": False, "buttons": []},
                    {"hwnd": 2, "title": "Auto Rollover Notification", "modal": False,
                     "buttons": ["Done"]}],
    })
    assert len(st.dialogs) == 2
    assert st.modals == []          # neither is BLOCKING...
    assert "Error" in st.describe()  # ...but neither is hidden either


def test_close_is_a_distinct_action_from_dismiss():
    """`close` exists because the WPF walk found NO buttons on a real Error box whose OK was plainly
    visible in a screenshot. WM_CLOSE is what the title-bar X sends; the outcome is verified the
    same way, so it is a fallback rather than a shortcut."""
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "close", "dismissed": True,
        "clickedVia": "WM_CLOSE", "verdict": "window closed",
    })
    assert st.action == "close"
    assert st.dismissed is True
    assert st.clicked_via == "WM_CLOSE"


def test_a_close_that_left_the_window_up_is_not_success():
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "close", "dismissed": False,
        "verdict": "WM_CLOSE POSTED BUT THE WINDOW IS STILL UP after 5000ms",
    })
    assert st.dismissed is False


def test_an_assertion_dialog_names_the_button_that_would_quit_ninjatrader():
    """A .NET assertion box offers Abort=Quit, Retry=Debug, Ignore=Continue. Its DEFAULT is Abort.
    This is the case that justifies refusing to click anything unnamed: on an unattended trading
    box, the default answer kills the platform."""
    st = ntdialog.parse_dialog_response({
        "status": "ok", "action": "list", "count": 1,
        "dialogs": [{"hwnd": 9, "title": "Assertion Failed: Abort=Quit, Retry=Debug, Ignore=Continue",
                     "class": "#32770", "modal": False, "buttonSource": "win32",
                     "buttons": ["Abort", "Retry", "Ignore"]}],
    })
    buttons = st.dialogs[0]["buttons"]
    assert buttons == ["Abort", "Retry", "Ignore"]
    assert "Abort" in st.describe()   # visible, but never auto-selected
