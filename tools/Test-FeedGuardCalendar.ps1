# The REAL FeedGuardCalendar block from addon\AutoReconnect.cs, against CME's published equity-index
# holiday calendars for 2026 and 2027, plus the moments that matter around each window.
#
# WHY: the add-on used to carry a hand-typed list of four 2026 dates. On 2027-01-01 it would have known
# no holiday at all, and even in 2026 it treated a holiday as midnight-to-midnight: the evening BEFORE
# New Year's Day (market shut from 17:00 ET) read as open, so a quiet feed there was a "stall" and the
# recovery ladder could disconnect a healthy connection into a closed market.
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
$calendar = Get-AddOnBlock 'FeedGuardCalendar'

$code = @"
using System; using System.Collections.Generic; using System.Globalization;
$calendar
class T {
$script:CSharpHarness
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static DateTime Et(string s) { return DateTime.ParseExact(s, "yyyy-MM-dd HH:mm", Inv); }

    // "MM-dd:F" (full close - the holiday is the day the window ENDS) or "MM-dd:E" (early close)
    static string Year(int y) {
        var parts = new List<string>();
        foreach (FeedGuardClosure w in FeedGuardCalendar.ForYear(y))
            parts.Add(w.Full ? w.EndEt.ToString("MM-dd", Inv) + ":F" : w.StartEt.ToString("MM-dd", Inv) + ":E");
        return string.Join(" ", parts.ToArray());
    }
    static void Closed(string label, string et, bool want) {
        bool got = FeedGuardCalendar.IsHolidayClosed(Et(et), null);
        Check(label + " (" + et + " ET)", got == want, "closed=" + got + ", wanted " + want);
    }

    static int Main() {
        // ---- the published calendars (CME Group equity products; observed days applied) ----
        string y2026 = Year(2026), y2027 = Year(2027);
        Check("2026 matches CME's calendar", y2026 == "01-01:F 01-19:E 02-16:E 04-03:F 05-25:E 06-19:E 07-03:E 09-07:E 11-26:E 11-27:E 12-24:E 12-25:F", y2026);
        Check("2027 matches CME's calendar", y2027 == "01-01:F 01-18:E 02-15:E 03-26:F 05-31:E 06-18:E 07-05:E 09-06:E 11-25:E 11-26:E 12-23:E 12-24:F", y2027);
        // observed-day shifts in other years
        string y2022 = Year(2022), y2023 = Year(2023);
        Check("2022: New Year's Day on a Saturday is not observed; Christmas on a Sunday moves to Monday, Dec 23 closes early",
              !y2022.Contains("01-01") && y2022.Contains("12-23:E") && y2022.Contains("12-26:F") && !y2022.Contains("12-24"), y2022);
        Check("2023: New Year's Day on a Sunday is observed Monday; Christmas on a Monday has no early close on the Sunday before",
              y2023.StartsWith("01-02:F") && y2023.Contains("12-25:F") && !y2023.Contains("12-24") && !y2023.Contains("12-23"), y2023);
        // Easter (Good Friday = Easter - 2)
        string[] easter = { "2024-03-31", "2025-04-20", "2026-04-05", "2027-03-28", "2028-04-16", "2029-04-01", "2030-04-21" };
        foreach (string e in easter) {
            DateTime want = DateTime.ParseExact(e, "yyyy-MM-dd", Inv);
            DateTime got = FeedGuardCalendar.Easter(want.Year);
            Check("Easter " + want.Year, got == want, got.ToString("yyyy-MM-dd", Inv));
        }

        // ---- the edges of the windows ----
        Closed("New Year's EVE after the 17:00 close is closed - the old list missed this evening", "2025-12-31 19:00", true);
        Closed("New Year's Day afternoon is closed", "2026-01-01 15:00", true);
        Closed("New Year's Day reopens at 18:00", "2026-01-01 18:00", false);
        Closed("Thanksgiving morning trades", "2026-11-26 12:59", false);
        Closed("Thanksgiving closes early at 13:00", "2026-11-26 13:00", true);
        Closed("Thanksgiving is still closed at 17:59", "2026-11-26 17:59", true);
        Closed("Thanksgiving reopens at 18:00", "2026-11-26 18:00", false);
        Closed("the Thursday before Good Friday trades until 17:00", "2026-04-02 16:59", false);
        Closed("Good Friday's window starts Thursday 17:00", "2026-04-02 17:00", true);
        Closed("Dec 23 2027 closes early (Christmas observed on Friday Dec 24)", "2027-12-23 13:30", true);
        Closed("July 2 2026 is an ordinary day", "2026-07-02 13:30", false);
        Closed("an ordinary Wednesday afternoon", "2026-10-14 14:00", false);
        Closed("Dec 31 2027 is ordinary - New Year's Day 2028 is a Saturday", "2027-12-31 13:30", false);

        // ---- extra dates from the config ----
        FeedGuardClosure full = FeedGuardCalendar.ParseExtra("2026-03-10");
        Check("'yyyy-MM-dd' is a full close from 17:00 the evening before to 18:00",
              full != null && full.Full && full.StartEt == Et("2026-03-09 17:00") && full.EndEt == Et("2026-03-10 18:00"),
              full == null ? "null" : FeedGuardCalendar.Describe(full));
        FeedGuardClosure early = FeedGuardCalendar.ParseExtra(" 2026-03-10 12:30 ");
        Check("'yyyy-MM-dd HH:mm' is closed from that time to the 18:00 reopen",
              early != null && !early.Full && early.StartEt == Et("2026-03-10 12:30") && early.EndEt == Et("2026-03-10 18:00"),
              early == null ? "null" : FeedGuardCalendar.Describe(early));
        FeedGuardClosure late = FeedGuardCalendar.ParseExtra("2026-03-10 19:00");
        Check("a time at or after 18:00 is closed until midnight",
              late != null && late.StartEt == Et("2026-03-10 19:00") && late.EndEt == Et("2026-03-11 00:00"),
              late == null ? "null" : FeedGuardCalendar.Describe(late));
        Check("anything else is refused", FeedGuardCalendar.ParseExtra("tomorrow") == null && FeedGuardCalendar.ParseExtra("2026-13-01") == null
              && FeedGuardCalendar.ParseExtra("") == null && FeedGuardCalendar.ParseExtra(null) == null, "accepted a bad date");
        var extras = new List<FeedGuardClosure> { full, early };
        Check("an extra date counts as closed", FeedGuardCalendar.IsHolidayClosed(Et("2026-03-10 09:00"), extras), "not closed");
        Check("...and only inside its window", !FeedGuardCalendar.IsHolidayClosed(Et("2026-03-10 18:30"), extras), "closed after the reopen");

        // ---- the next closure, for the heartbeat and the control panel ----
        string next = FeedGuardCalendar.Describe(FeedGuardCalendar.Next(Et("2026-10-09 12:00"), null));
        Check("next closure after 2026-10-09 is Thanksgiving", next == "2026-11-26 13:00 ET Thanksgiving (early close, reopens 2026-11-26 18:00 ET)", next);
        next = FeedGuardCalendar.Describe(FeedGuardCalendar.Next(Et("2026-12-26 10:00"), null));
        Check("after Christmas the next is New Year's Day 2027, from Dec 31 17:00", next == "2026-12-31 17:00 ET New Year's Day (closed, reopens 2027-01-01 18:00 ET)", next);
        next = FeedGuardCalendar.Describe(FeedGuardCalendar.Next(Et("2026-11-26 14:00"), null));
        Check("during a closure, that closure is the next one", next.StartsWith("2026-11-26 13:00 ET Thanksgiving"), next);
        extras.Add(FeedGuardCalendar.ParseExtra("2026-11-02 12:00"));
        next = FeedGuardCalendar.Describe(FeedGuardCalendar.Next(Et("2026-10-09 12:00"), extras));
        Check("an extra date before the next holiday comes first (past ones are skipped)", next.StartsWith("2026-11-02 12:00 ET extra date"), next);
        return Done();
    }
}
"@
Invoke-CSharpTest 'calendar' $code
exit $script:CSharpExit
