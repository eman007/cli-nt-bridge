from __future__ import annotations

import base64
import sqlite3

from guard_fixtures import CLASS, SCHEMA, add_strategy, make_db, nt8_folder, userdata
from nt8guard_backup import nt8db


def layout_db(tmp_path, strategies_sql: str | None = None, drop: str | None = None):
    """NT8's schema with the Strategies table replaced, or one table (and its indexes) left out."""
    con = sqlite3.connect(tmp_path / "x.sqlite")
    for stmt in SCHEMA:
        if strategies_sql and stmt.startswith("CREATE TABLE Strategies "):
            stmt = strategies_sql
        if drop and (stmt.startswith(f"CREATE TABLE {drop} ") or f" on {drop}(" in stmt):
            continue
        con.execute(stmt)
    return con


def test_real_layout_has_no_problems(tmp_path):
    assert nt8db.layout_problems(make_db(nt8_folder(tmp_path))) == []


def test_missing_column_is_reported(tmp_path):
    con = layout_db(tmp_path, "CREATE TABLE Strategies (Id integer primary key, Category integer, "
                    "Classname text, IsReplay integer, IsResetOnNewTradingDay integer, IsTerminal integer, "
                    "Name text, ServerId integer, Userdata blob, Workspace text)")
    assert nt8db.layout_problems(con) == ["Strategies has no column Template"]


def test_unexpected_column_in_a_written_table_is_reported(tmp_path):
    con = layout_db(tmp_path, "CREATE TABLE Strategies (Id integer primary key, Category integer, "
                    "Classname text, IsReplay integer, IsResetOnNewTradingDay integer, IsTerminal integer, "
                    "Name text, ServerId integer, Template text, Userdata blob, Workspace text, Owner text)")
    assert nt8db.layout_problems(con) == ["Strategies has unexpected column Owner"]


def test_missing_table_is_reported(tmp_path):
    con = layout_db(tmp_path, drop="Accounts")
    assert nt8db.layout_problems(con) == ["table Accounts is missing"]


def test_read_live_and_ids(tmp_path):
    con = make_db(nt8_folder(tmp_path))
    add_strategy(con, 746, "Demo")
    con.execute("insert into Strategies (Id, Classname, IsTerminal, Name, Userdata) "
                "values (900, 'NinjaTrader.NinjaScript.AtmStrategy', 0, 'atm', x'00')")
    con.execute("insert into Strategies (Id, Classname, IsTerminal, Name, Userdata) "
                "values (901, ?, 1, 'finished', x'00')", (CLASS,))
    con.commit()
    con.row_factory = sqlite3.Row
    live = nt8db.read_live(con)
    assert [r["strategies"]["Id"] for r in live] == [746]
    rec = live[0]
    assert base64.b64decode(rec["strategies"]["Userdata"]) == rec["userdata"] == userdata()
    assert rec["strategy2account"] == [{"Account": 1, "Strategy": 746, "Nr": 0}]
    assert rec["strategy2instrument"] == [{"Instrument": 100, "Strategy": 746, "Nr": 0}]
    assert nt8db.live_strategy_ids(con.cursor()) == {f"Demo@{CLASS}": 746}


def test_only_nt8s_own_atm_class_is_excluded(tmp_path):
    helper = "NinjaTrader.NinjaScript.Strategies.Vendor.MyAtmStrategyHelper"
    con = make_db(nt8_folder(tmp_path))
    add_strategy(con, 746, "Helper", classname=helper)
    con.execute("insert into Strategies (Id, Classname, IsTerminal, Name, Userdata) "
                "values (900, 'NinjaTrader.NinjaScript.AtmStrategy', 0, 'atm', x'00')")
    con.commit()
    con.row_factory = sqlite3.Row
    assert [r["strategies"]["Name"] for r in nt8db.read_live(con)] == ["Helper"]
    assert list(nt8db.live_strategy_ids(con.cursor())) == [f"Helper@{helper}"]


def test_snapshot_copies_bytes(tmp_path):
    nt8 = nt8_folder(tmp_path)
    make_db(nt8).close()
    src = nt8 / "db" / "NinjaTrader.sqlite"
    dst = nt8db.snapshot(src, tmp_path / "snap.sqlite")
    assert dst.read_bytes() == src.read_bytes()


def test_read_live_names_the_account_and_contract(tmp_path):
    con = make_db(nt8_folder(tmp_path))
    con.execute("insert into Accounts (Id, Name, Provider, Fcm) values (2, 'Sim102', 1, '')")
    con.execute("insert into MasterInstruments (Id, Name, InstrumentType) values (11, 'AAPL', 0)")
    con.execute("insert into Instruments (Id, Exchange, Expiry, MasterInstrument) values (101, 1, 0, 11)")
    add_strategy(con, 746, "Demo")
    add_strategy(con, 747, "Stock", account=2, instrument=101)
    con.execute("insert into Strategy2Account (Account, Strategy, Nr) values (99, 747, 1)")   # account deleted since
    con.commit()
    con.row_factory = sqlite3.Row
    labels = {r["strategies"]["Name"]: r["labels"] for r in nt8db.read_live(con)}
    assert labels["Demo"] == {"accounts": ["Sim101"], "instruments": ["NQ 12-26"]}
    assert labels["Stock"] == {"accounts": ["Sim102", "account 99"], "instruments": ["AAPL"]}


def test_expiry_label():
    assert nt8db.expiry_label(639331488000000000) == "12-26"
    assert nt8db.expiry_label(0) == ""
    assert nt8db.expiry_label(None) == ""
    assert nt8db.expiry_label(3155378975999999999) == ""        # DateTime.MaxValue: no expiry
