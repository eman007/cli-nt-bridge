# The REAL FeedGuardRollover block from addon\AutoReconnect.cs: which contract "MNQ ##-##" means on a
# given day - from NinjaTrader's own roll schedule, or by the CME quarterly rule when that schedule
# cannot answer - and the warning for a concrete contract about to expire.
#
# WHY: 2026-09-18 the watch list said "MNQ 09-26" the morning that contract expired. It never ticked
# again, read as a dead feed, and the recovery ladder disconnected every ~110s all session - each
# disconnect switching every strategy off. The feed was fine the whole time.
. (Join-Path $PSScriptRoot 'FeedGuardTestKit.ps1')
$rollover = Get-AddOnBlock 'FeedGuardRollover'

$code = @"
using System; using System.Collections.Generic; using System.Globalization;
$rollover
class T {
$script:CSharpHarness
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static DateTime D(string s) { return DateTime.ParseExact(s, "yyyy-MM-dd", Inv); }
    static void Q(string today, string sym, string want) {
        string got = FeedGuardRollover.Quarterly(sym, D(today), 8);
        Check("rule " + today + "  " + sym + " ##-## -> " + want, got == want, got);
    }
    static List<KeyValuePair<DateTime, DateTime>> Rolls(params string[] monthThenDate) {
        var l = new List<KeyValuePair<DateTime, DateTime>>();
        for (int i = 0; i + 1 < monthThenDate.Length; i += 2) l.Add(new KeyValuePair<DateTime, DateTime>(D(monthThenDate[i]), D(monthThenDate[i + 1])));
        return l;
    }

    static int Main() {
        // ---- the quarterly rule (the fallback), the cases the old test pinned ----
        Q("2026-09-18", "MNQ", "MNQ 12-26");   // expiry morning -> must be on December already
        Q("2026-09-09", "MNQ", "MNQ 09-26");   // the day before the roll -> still September
        Q("2026-09-10", "MNQ", "MNQ 12-26");   // roll day -> December
        Q("2026-08-01", "MES", "MES 09-26");
        Q("2026-12-09", "MNQ", "MNQ 12-26");
        Q("2026-12-10", "MNQ", "MNQ 03-27");   // rolls in place, no restart needed
        Q("2027-01-05", "M2K", "M2K 03-27");
        Q("2026-12-31", "MNQ", "MNQ 03-27");   // year boundary
        string tf = FeedGuardRollover.ThirdFriday(2026, 9).ToString("yyyy-MM-dd ddd", Inv) + " | "
                  + FeedGuardRollover.ThirdFriday(2026, 12).ToString("yyyy-MM-dd ddd", Inv) + " | "
                  + FeedGuardRollover.ThirdFriday(2027, 3).ToString("yyyy-MM-dd ddd", Inv);
        Check("third Fridays", tf == "2026-09-18 Fri | 2026-12-18 Fri | 2027-03-19 Fri", tf);

        // ---- what counts as an alias ----
        string sym;
        Check("'MNQ ##-##' is an alias for MNQ", FeedGuardRollover.IsAlias("  MNQ ##-##  ", out sym) && sym == "MNQ", sym);
        Check("'MNQ 12-26' is a concrete contract", !FeedGuardRollover.IsAlias("MNQ 12-26", out sym), "treated as alias");
        Check("' ##-##' with no symbol is not an alias", !FeedGuardRollover.IsAlias(" ##-##", out sym), "treated as alias");
        Check("null is not an alias", !FeedGuardRollover.IsAlias(null, out sym), "treated as alias");

        // ---- NinjaTrader's own schedule (contract month -> the date NinjaTrader moves to it) ----
        var mnq = Rolls("2026-06-01", "2026-03-12", "2026-09-01", "2026-06-11", "2026-12-01", "2026-09-10", "2027-03-01", "2026-12-10");
        Check("schedule: before the Sep roll -> September", FeedGuardRollover.FromSchedule("MNQ", D("2026-09-09"), mnq) == "MNQ 09-26", FeedGuardRollover.FromSchedule("MNQ", D("2026-09-09"), mnq));
        Check("schedule: ON the roll date -> December", FeedGuardRollover.FromSchedule("MNQ", D("2026-09-10"), mnq) == "MNQ 12-26", FeedGuardRollover.FromSchedule("MNQ", D("2026-09-10"), mnq));
        Check("schedule: order of the list does not matter", FeedGuardRollover.FromSchedule("MNQ", D("2026-10-01"),
              Rolls("2027-03-01", "2026-12-10", "2026-09-01", "2026-06-11", "2026-12-01", "2026-09-10")) == "MNQ 12-26", "wrong month");
        // a MONTHLY contract the quarterly rule would get wrong: NinjaTrader's dates decide
        var cl = Rolls("2026-11-01", "2026-10-13", "2026-12-01", "2026-11-12");
        Check("schedule: a monthly contract (CL) follows NinjaTrader, not the quarterly rule",
              FeedGuardRollover.FromSchedule("CL", D("2026-10-20"), cl) == "CL 11-26" && FeedGuardRollover.Quarterly("CL", D("2026-10-20"), 8) == "CL 12-26",
              FeedGuardRollover.FromSchedule("CL", D("2026-10-20"), cl));
        Check("schedule: no roll on or before today -> null (caller uses the rule)", FeedGuardRollover.FromSchedule("MNQ", D("2026-01-05"), mnq) == null, "answered");
        Check("schedule: empty -> null", FeedGuardRollover.FromSchedule("MNQ", D("2026-09-10"), Rolls()) == null, "answered");
        Check("schedule: null -> null", FeedGuardRollover.FromSchedule("MNQ", D("2026-09-10"), null) == null, "answered");
        // NinjaTrader's list not brought up to date: its last entry's month is already over
        Check("schedule: never answers with an EXPIRED contract (stale list -> null)",
              FeedGuardRollover.FromSchedule("MNQ", D("2027-04-02"), mnq) == null, FeedGuardRollover.FromSchedule("MNQ", D("2027-04-02"), mnq));
        Check("schedule: still answers inside the last contract's month", FeedGuardRollover.FromSchedule("MNQ", D("2027-03-10"), mnq) == "MNQ 03-27", "no answer");

        // ---- a STALE schedule must not hold an expired contract (final review) ----
        // NinjaTrader's Instrument.Expiry is MONTH resolution: the first day of the expiry month. From that
        // day on, the schedule's answer is not trusted and the quarterly rule decides (it rolls 8 days
        // before the 3rd Friday, so it is right for the whole expiry month).
        string stale = FeedGuardRollover.FromSchedule("MNQ", D("2027-03-19"), mnq);
        Check("stale list after the 3rd Friday still names MNQ 03-27 (why PickFront exists)", stale == "MNQ 03-27", stale ?? "null");
        Check("PickFront: in the expiry month -> not trusted (null)", FeedGuardRollover.PickFront(stale, D("2027-03-01"), D("2027-03-19")) == null, "trusted an expired contract");
        Check("PickFront + the rule: 2027-03-19 -> MNQ 06-27", (FeedGuardRollover.PickFront(stale, D("2027-03-01"), D("2027-03-19"))
              ?? FeedGuardRollover.Quarterly("MNQ", D("2027-03-19"), 8)) == "MNQ 06-27", "wrong contract");
        Check("PickFront: before the expiry month -> NinjaTrader's answer", FeedGuardRollover.PickFront("MNQ 03-27", D("2027-03-01"), D("2027-02-20")) == "MNQ 03-27", "dropped a good answer");
        Check("PickFront: expiry unknown -> NinjaTrader's answer", FeedGuardRollover.PickFront("MNQ 03-27", DateTime.MinValue, D("2027-03-19")) == "MNQ 03-27", "dropped");
        Check("PickFront: no answer -> null", FeedGuardRollover.PickFront(null, D("2027-03-01"), D("2027-02-20")) == null, "invented one");

        // ---- the warning for a concrete contract: Expiry is the first day of the expiry MONTH ----
        DateTime dec = D("2026-12-01");   // what NinjaTrader returns for MNQ 12-26 (it trades until 2026-12-18)
        Check("8 days before the expiry month -> quiet", FeedGuardRollover.ExpiryNotice("MNQ 12-26", dec, D("2026-11-23"), 7) == null, "warned");
        string n = FeedGuardRollover.ExpiryNotice("MNQ 12-26", dec, D("2026-11-24"), 7);
        Check("7 days before the expiry month -> warned, month named, alias given", n != null && n.Contains("expires in December 2026") && n.Contains("MNQ ##-##"), n ?? "null");
        n = FeedGuardRollover.ExpiryNotice("MNQ 12-26", dec, D("2026-12-11"), 7);
        Check("inside the expiry month (still trading) -> EXPIRING, never 'EXPIRED'", n != null && n.StartsWith("WATCH LIST CONTRACT EXPIRING: MNQ 12-26"), n ?? "null");
        n = FeedGuardRollover.ExpiryNotice("MNQ 09-26", D("2026-09-01"), D("2026-10-02"), 7);
        Check("the expiry month is over -> EXPIRED", n != null && n.StartsWith("WATCH LIST CONTRACT EXPIRED: MNQ 09-26"), n ?? "null");
        Check("no expiry known (default date) -> quiet", FeedGuardRollover.ExpiryNotice("MNQ 12-26", DateTime.MinValue, D("2026-12-11"), 7) == null, "warned");
        return Done();
    }
}
"@
Invoke-CSharpTest 'frontmonth' $code
$rc = $script:CSharpExit

# ResolveFrontMonth is the glue in the add-on: it must ask NinjaTrader first and fall back to the rule.
$glue = Get-AddOnSlice 'internal string ResolveFrontMonth(' 'private static string CombineRolloverSource('
if ($glue -match 'FeedGuardRollover\.FromSchedule\(' -and $glue -match 'return fromNt8 \?\? FeedGuardRollover\.Quarterly\(') { '  PASS  ResolveFrontMonth: NinjaTrader''s answer when it has one, else the rule' }
else { '  FAIL  ResolveFrontMonth does not use FromSchedule then Quarterly'; $rc = 1 }
if ($glue -match 'FeedGuardRollover\.PickFront\(' -and $glue -match '\.Expiry\b') { '  PASS  ResolveFrontMonth: NinjaTrader''s answer is checked against that contract''s expiry month' }
else { '  FAIL  ResolveFrontMonth trusts a stale schedule (no PickFront / Expiry check)'; $rc = 1 }
if ($rc -eq 0) { 'RESULT: all checks passed' } else { 'RESULT: FAILED' }
exit $rc
