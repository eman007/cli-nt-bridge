from __future__ import annotations

import xml.etree.ElementTree as ET

import pytest

from guard_fixtures import BRUSH, CLASS, TYPED_INNER, undeclared_type_prefixes, userdata, userdata_raw
from nt8guard_backup import userdata as ud


def test_brush_setting_survives_as_text():
    el = ud.decode_userdata(userdata({"UpColorSerializable": BRUSH, "Period": "14"}))
    assert el.tag == "DemoStrategy"
    assert el.find("UpColorSerializable").text == BRUSH
    assert len(el.find("UpColorSerializable")) == 0
    assert el.findtext("Period") == "14"


def test_text_with_ampersand_and_angle_bracket_survives():
    assert ud.decode_userdata(userdata({"Note": "A & B < C"})).findtext("Note") == "A & B < C"


def test_missing_impl_is_an_error():
    with pytest.raises(ValueError):
        ud.decode_userdata("<NinjaTrader></NinjaTrader>".encode("utf-16-le"))


@pytest.mark.parametrize("name,expected", [
    ("Plain", "Plain"),
    ('A/B:C*D?"E<F>G|H', "A_B_C_D_E_F_G_H"),
    ("Trailing dots...", "Trailing dots"),
    ("", "unnamed"),
])
def test_safe_name(name, expected):
    assert ud.safe_name(name) == expected


def test_template_path_uses_class_folder_and_readable_name(tmp_path):
    p = ud.template_path(tmp_path, "NinjaTrader.NinjaScript.Strategies.Vendor.Demo", "Demo - v1.0")
    assert p == tmp_path / "templates" / "Strategy" / "Vendor.Demo" / "Guard backup - Demo - v1.0.xml"
    assert ud.template_path(tmp_path, CLASS, "X", suffix=" (id 7)").name == "Guard backup - X (id 7).xml"


def test_written_template_round_trips_every_setting(tmp_path):
    blob = userdata({"UpColorSerializable": BRUSH, "Note": "A & B"})
    folder = tmp_path / "templates" / "Strategy" / "DemoStrategy"
    path = folder / "Guard backup - Demo.xml"
    ud.write_template(path, ud.build_template(CLASS, ud.strategy_xml(blob), folder))
    raw = path.read_bytes()
    assert raw.startswith(b"\xef\xbb\xbf")                      # UTF-8 with BOM, like NT8's own templates
    root = ET.fromstring(raw.decode("utf-8-sig"))
    assert root.tag == "StrategyTemplate"
    assert root.findtext("StrategyType") == CLASS
    back = root.find("Strategy")[0]
    assert back.findtext("UpColorSerializable") == BRUSH
    assert back.findtext("Note") == "A & B"
    assert not path.with_suffix(".xml.tmp").exists()


def test_template_keeps_namespace_declarations_used_only_in_values(tmp_path):
    blob = userdata_raw(TYPED_INNER)
    folder = tmp_path / "DemoStrategy"
    path = folder / "Guard backup - Demo.xml"
    ud.write_template(path, ud.build_template(CLASS, ud.strategy_xml(blob), folder))
    text = path.read_bytes().decode("utf-8-sig")
    assert undeclared_type_prefixes(text) == set()
    assert ud.strategy_xml(blob) in text                       # NT8's strategy XML spliced in verbatim


def test_strategy_xml_is_the_inner_document_without_its_declaration():
    xml = ud.strategy_xml(userdata_raw(TYPED_INNER))
    assert xml.startswith("<DemoStrategy ") and 'xsi:type="xsd:int"' in xml
    assert ud.decode_userdata(userdata_raw(TYPED_INNER)).tag == "DemoStrategy"


SIBLING = """<?xml version="1.0" encoding="utf-8"?>\r
<StrategyTemplate>\r
  <StrategyType>Old.Type</StrategyType>\r
  <OptimizerType>FROM_USER</OptimizerType>\r
  <OptimizerParameters>\r
    <ArrayOfParameterWrapper xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\r
      <ParameterWrapper><Value xsi:type="xsd:int">3</Value></ParameterWrapper>\r
    </ArrayOfParameterWrapper>\r
  </OptimizerParameters>\r
  <OptimizationFitness>X</OptimizationFitness>\r
  <OptimizationParameters />\r
  <Strategy><OldStrategy><Strategy>nested</Strategy></OldStrategy></Strategy>\r
</StrategyTemplate>"""


def test_sibling_template_wrapper_is_reused_but_backups_are_not(tmp_path):
    folder = tmp_path / "DemoStrategy"
    folder.mkdir()
    (folder / "Guard backup - old.xml").write_text(
        "<StrategyTemplate><StrategyType/><OptimizerType>FROM_BACKUP</OptimizerType><Strategy/></StrategyTemplate>",
        encoding="utf-8")
    (folder / "My saved.xml").write_bytes(b"\xef\xbb\xbf" + SIBLING.encode("utf-8"))
    text = ud.build_template(CLASS, "<DemoStrategy />", folder)
    root = ET.fromstring(text)
    assert root.findtext("OptimizerType") == "FROM_USER"
    assert root.findtext("StrategyType") == CLASS
    assert [c.tag for c in root.find("Strategy")] == ["DemoStrategy"]       # the old strategy fully replaced
    assert undeclared_type_prefixes(text) == set()                          # the wrapper's own xsd: values too
    assert "\r\n" in text                                                   # the sibling's line endings kept
