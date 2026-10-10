"""Builders for a fake NinjaTrader 8 folder, shaped like the real thing.

The database uses NT8's own CREATE TABLE statements (copied from a live 8.1.8.3 database). Stored
strategy settings are built the way NT8 writes them: UTF-16LE <NinjaTrader><_Impl>, with the strategy
XML escaped once inside it - so a brush setting, itself an XML string, ends up escaped twice.
Workspace files carry a UTF-8 BOM and CRLF line endings, like NT8's.
"""
from __future__ import annotations

import html
import io
import sqlite3
import xml.etree.ElementTree as ET
from pathlib import Path

CLASS = "NinjaTrader.NinjaScript.Strategies.DemoStrategy"
CLASS2 = "NinjaTrader.NinjaScript.Strategies.Vendor.OtherStrategy"
BRUSH = ('<SolidColorBrush xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">'
         '#FF00FF00</SolidColorBrush>')
DEC_2026_TICKS = 639331488000000000          # .NET ticks of 2026-12-18 -> "NQ 12-26"

SCHEMA = [
    """CREATE TABLE Strategies (Id integer not null primary key, Category integer, Classname text,
       IsReplay integer, IsResetOnNewTradingDay integer, IsTerminal integer, Name text,
       ServerId integer, Template text, Userdata blob, Workspace text)""",
    """CREATE TABLE Accounts (Id integer not null primary key, Denomination integer,
       ForexLotSize integer, Name text, Provider integer, SimulatorDelayExchange integer,
       SimulatorDelayInternet integer, SimulatorInitialCash real, Commission text,
       DisplayName text(250), Fcm text, Risk text, MinimumCashValue real, PrimaryUser integer,
       AccountStatus int, LossLimit real, Fee text(50), MarginMultiplier real, MaxOrderSize int,
       MaxPositionSize int, Data blob, LiquidationState int)""",
    """CREATE TABLE MasterInstruments (Id integer not null primary key, Currency integer,
       Description text, InstrumentType integer, MergePolicy integer, Name text not null,
       PointValue real, PriceLevel real, TickSize real, TradingHours text, Url text, UserData blob,
       Version integer, IsServerSupported integer, AutoLiquidation integer)""",
    """CREATE TABLE Instruments (Id integer not null primary key, Exchange integer, Expiry integer,
       MasterInstrument integer not null references MasterInstruments on delete cascade,
       [Right] integer, StrikePrice real)""",
    """CREATE TABLE Strategy2Account (Account integer not null references Accounts on delete cascade,
       Strategy integer not null references Strategies on delete cascade, Nr integer not null,
       primary key (Strategy, Account))""",
    """CREATE TABLE Strategy2Instrument (Instrument integer not null references Instruments
       on delete cascade, Strategy integer not null references Strategies on delete cascade,
       Nr integer not null, primary key (Strategy, Instrument, Nr))""",
    "CREATE UNIQUE INDEX Accounts_ui0 on Accounts(Name, Fcm, Provider)",
    "CREATE INDEX Strategy2Account_i0 on Strategy2Account(Account)",
    "CREATE INDEX Strategy2Instrument_i0 on Strategy2Instrument(Instrument)",
]


def userdata(fields: dict[str, str] | None = None, short: str = "DemoStrategy") -> bytes:
    fields = {"Period": "14", "UpColorSerializable": BRUSH} if fields is None else fields
    body = "".join(f"<{k}>{html.escape(v, quote=False)}</{k}>" for k, v in fields.items())
    inner = (f'<?xml version="1.0"?>\r\n<{short} xmlns:xsd="http://www.w3.org/2001/XMLSchema">'
             f"{body}</{short}>")
    return f"<NinjaTrader><_Impl>{html.escape(inner, quote=False)}</_Impl></NinjaTrader>".encode("utf-16-le")


# A strategy's XML as NT8 serializes it: xsd/xsi declared on the root and xsd used ONLY inside an
# attribute VALUE (xsi:type="xsd:int"), exactly the pattern a parse-and-reserialize loses.
TYPED_INNER = ('<?xml version="1.0"?>\r\n<DemoStrategy xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
               'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\r\n'
               '  <BarsPeriodParameter>\r\n    <Max xsi:type="xsd:int">5</Max>\r\n  </BarsPeriodParameter>\r\n'
               f"  <UpColorSerializable>{html.escape(BRUSH, quote=False)}</UpColorSerializable>\r\n"
               "</DemoStrategy>")


def userdata_raw(inner_xml: str) -> bytes:
    """Userdata around a given strategy XML document (escaped once inside <_Impl>, as NT8 stores it)."""
    return f"<NinjaTrader><_Impl>{html.escape(inner_xml, quote=False)}</_Impl></NinjaTrader>".encode("utf-16-le")


XSI_TYPE = "{http://www.w3.org/2001/XMLSchema-instance}type"


def undeclared_type_prefixes(xml_text: str) -> set[str]:
    """Prefixes used in xsi:type VALUES that no enclosing element declares. .NET's XmlSerializer (what
    NT8 loads templates with) refuses such a file: "Namespace prefix 'xsd' is not defined"."""
    frames: list[set[str]] = []
    pending: list[str] = []
    bad: set[str] = set()
    for event, obj in ET.iterparse(io.BytesIO(xml_text.encode("utf-8")), events=("start-ns", "start", "end")):
        if event == "start-ns":
            pending.append(obj[0])
        elif event == "start":
            frames.append(set(pending))
            pending = []
            value = obj.get(XSI_TYPE)
            if value and ":" in value:
                prefix = value.split(":", 1)[0]
                if not any(prefix in f for f in frames):
                    bad.add(prefix)
        else:
            frames.pop()
    return bad


def nt8_folder(root: Path) -> Path:
    nt8 = root / "NinjaTrader 8"
    for sub in ("db", "templates/Strategy", "workspaces"):
        (nt8 / sub).mkdir(parents=True, exist_ok=True)
    return nt8


def make_db(nt8: Path) -> sqlite3.Connection:
    con = sqlite3.connect(nt8 / "db" / "NinjaTrader.sqlite")
    for stmt in SCHEMA:
        con.execute(stmt)
    con.execute("insert into Accounts (Id, Name, Provider, Fcm) values (1, 'Sim101', 1, '')")
    con.execute("insert into MasterInstruments (Id, Name, InstrumentType) values (10, 'NQ', 1)")
    con.execute("insert into Instruments (Id, Exchange, Expiry, MasterInstrument) values (100, 1, ?, 10)",
                (DEC_2026_TICKS,))
    con.commit()
    return con


def add_strategy(con: sqlite3.Connection, sid: int, name: str, classname: str = CLASS,
                 fields: dict[str, str] | None = None, account: int = 1, instrument: int = 100,
                 blob: bytes | None = None) -> None:
    short = classname.rsplit(".", 1)[-1]
    con.execute("insert into Strategies (Id, Category, Classname, IsReplay, IsResetOnNewTradingDay, "
                "IsTerminal, Name, ServerId, Template, Userdata, Workspace) "
                "values (?, 2, ?, 0, 0, 0, ?, -1, '', ?, 'Main')",
                (sid, classname, name, blob if blob is not None else userdata(fields, short)))
    con.execute("insert into Strategy2Account (Account, Strategy, Nr) values (?, ?, 0)", (account, sid))
    con.execute("insert into Strategy2Instrument (Instrument, Strategy, Nr) values (?, ?, 0)",
                (instrument, sid))
    con.commit()


def delete_like_nt8(con: sqlite3.Connection, sid: int) -> None:
    """NT8 deletes a strategy row and the cascade takes its link rows."""
    con.execute("pragma foreign_keys = on")
    con.execute("delete from Strategies where Id = ?", (sid,))
    con.commit()


def rekey(con: sqlite3.Connection, old: int, new: int) -> None:
    """What a hand re-add looks like in the database: the same strategy under a new Id."""
    con.execute("pragma foreign_keys = off")
    con.execute("update Strategies set Id = ? where Id = ?", (new, old))
    con.execute("update Strategy2Account set Strategy = ? where Strategy = ?", (new, old))
    con.execute("update Strategy2Instrument set Strategy = ? where Strategy = ?", (new, old))
    con.commit()


def write_workspace(nt8: Path, stem: str, tabs: list[dict]) -> Path:
    """tabs: [{"chart": "Chart-a1", "tab": "Tab-b1", "name": "Demo", "refs": [("Strategy0", 0, 746)]}].

    Adds two decoys Guard must ignore: a <Strategies> list inside a non-chart window, and one nested
    inside an indicator (deeper indentation, with its own <TabName>) inside every tab.
    """
    out = ['<?xml version="1.0" encoding="utf-8"?>', "<NinjaTrader>", "  <NTWindows>",
           "    <StrategyAnalyzer-0000>", "      <Strategies>",
           '        <Strategy0 BarsIndex="0">555</Strategy0>', "      </Strategies>",
           "    </StrategyAnalyzer-0000>"]
    by_chart: dict[str, list[dict]] = {}
    for t in tabs:
        by_chart.setdefault(t["chart"], []).append(t)
    for chart, chart_tabs in by_chart.items():
        out += [f"    <{chart}>", '      <Class Assembly="NinjaTrader.Gui">NinjaTrader.Gui.Chart.Chart</Class>',
                "      <TabControl>", "        <SelectedIndex>0</SelectedIndex>"]
        for t in chart_tabs:
            out += [f"        <{t['tab']}>", "          <DataSeries />"]
            if t["refs"]:
                out.append("          <Strategies>")
                out += [f'            <{slot} BarsIndex="{bi}">{sid}</{slot}>' for slot, bi, sid in t["refs"]]
                out.append("          </Strategies>")
            else:
                out.append("          <Strategies />")
            out += ["          <Indicators>",
                    '            <Indicator BarsIndex="0" Name="Decoy">',
                    "              <TabName>Decoy</TabName>",
                    "              <Strategies>",
                    '                <Strategy0 BarsIndex="0">999</Strategy0>',
                    "              </Strategies>",
                    "            </Indicator>",
                    "          </Indicators>",
                    f"          <TabName>{t['name']}</TabName>",
                    f"        </{t['tab']}>"]
        out += ["      </TabControl>", f"    </{chart}>"]
    out += ["  </NTWindows>", "</NinjaTrader>"]
    path = nt8 / "workspaces" / f"{stem}.xml"
    path.write_bytes(b"\xef\xbb\xbf" + "\r\n".join(out).encode("utf-8"))
    return path
