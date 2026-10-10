"""A strategy's stored settings (Strategies.Userdata) and the NT8 template made from them.

Userdata is UTF-16LE: <NinjaTrader><_Impl>ESCAPED strategy XML</_Impl></NinjaTrader>. A template is
<StrategyTemplate> ... <Strategy><TheStrategy .../></Strategy>, which NT8's own Load-template dialog
reads. The optimizer sections come from a template the user saved for the same class when there is
one, otherwise from a generic skeleton.

The template is assembled as TEXT, with the strategy's XML spliced in exactly as NT8 serialized it.
Parsing and re-serializing (ElementTree) drops namespace declarations that are used only inside
attribute values - NT8 writes `xmlns:xsd` on the strategy element and then only `xsi:type="xsd:int"` -
and .NET's XmlSerializer refuses the whole file: "Namespace prefix 'xsd' is not defined".
"""
from __future__ import annotations

import os
import re
import xml.etree.ElementTree as ET
from pathlib import Path
from xml.sax.saxutils import escape

CLASS_PREFIX = "NinjaTrader.NinjaScript.Strategies."
TEMPLATE_PREFIX = "Guard backup - "
LEGACY_PREFIX = "_grid_"
BOM = b"\xef\xbb\xbf"

GENERIC_WRAPPER = "\r\n".join([
    '<?xml version="1.0" encoding="utf-8"?>',
    "<StrategyTemplate>",
    "  <StrategyType></StrategyType>",
    "  <OptimizerType>NinjaTrader.NinjaScript.Optimizers.DefaultOptimizer</OptimizerType>",
    "  <OptimizerParameters>",
    '    <ArrayOfParameterWrapper xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
    'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />',
    "  </OptimizerParameters>",
    "  <OptimizationFitness>NinjaTrader.NinjaScript.OptimizationFitnesses.MaxProfitFactor</OptimizationFitness>",
    "  <OptimizationParameters>",
    '    <ArrayOfParameterWrapper xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
    'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" />',
    "  </OptimizationParameters>",
    "  <Strategy />",
    "</StrategyTemplate>",
])

_STRATEGY_TYPE = re.compile(r"<StrategyType\s*/>|<StrategyType>.*?</StrategyType>", re.S)
_STRATEGY_OPEN = re.compile(r"<Strategy\s*/>|<Strategy>")


def strategy_xml(blob: bytes) -> str:
    """The strategy's XML document exactly as NT8 serialized it, minus its <?xml?> line.

    The parser has already undone the one escape NT8 applied; do NOT unescape again - brush settings
    are XML strings, and a second unescape turns each into a child element and blanks it."""
    outer = ET.fromstring(blob.decode("utf-16-le"))
    impl = outer.find("_Impl")
    if impl is None or not impl.text:
        raise ValueError("no <_Impl> in Userdata")
    inner = impl.text.strip()
    if inner.startswith("<?xml"):
        inner = inner[inner.index("?>") + 2:].strip()
    ET.fromstring(inner)                      # must be well-formed before it goes anywhere
    return inner


def decode_userdata(blob: bytes) -> ET.Element:
    """The strategy's XML as an element tree (for reading settings; never for writing templates)."""
    return ET.fromstring(strategy_xml(blob))


def safe_name(name: str) -> str:
    """A file-name-safe version of a strategy name (Windows forbids / : * ? " < > | and trailing dots)."""
    return re.sub(r'[<>:"/\\|?*\x00-\x1f]+', "_", name).strip().rstrip(".").strip() or "unnamed"


def class_folder(classname: str) -> str:
    return classname[len(CLASS_PREFIX):] if classname.startswith(CLASS_PREFIX) else classname


def template_path(nt8: Path, classname: str, name: str, suffix: str = "") -> Path:
    return (nt8 / "templates" / "Strategy" / class_folder(classname)
            / f"{TEMPLATE_PREFIX}{safe_name(name)}{suffix}.xml")


def _wrapper_text(folder: Path) -> str:
    """Raw text of a template the user saved for this class (read as bytes, so line endings and every
    declaration survive), or the generic skeleton."""
    if folder.is_dir():
        for p in sorted(folder.glob("*.xml")):
            if p.name.startswith((TEMPLATE_PREFIX, LEGACY_PREFIX)):
                continue
            try:
                text = p.read_bytes().decode("utf-8-sig")
                root = ET.fromstring(text)
            except (ET.ParseError, OSError, UnicodeDecodeError):
                continue
            if (root.tag == "StrategyTemplate" and root.find("Strategy") is not None
                    and root.find("StrategyType") is not None):
                return text
    return GENERIC_WRAPPER


def build_template(classname: str, strategy_text: str, folder: Path) -> str:
    """Template text: the wrapper with StrategyType set and the <Strategy> element's content replaced
    by `strategy_text` verbatim. The wrapper's own <Strategy> is its first opening tag (nothing before
    it in a template is named Strategy) and its last </Strategy> closing tag."""
    text = _wrapper_text(folder)
    text, n = _STRATEGY_TYPE.subn(lambda _m: f"<StrategyType>{escape(classname)}</StrategyType>", text, count=1)
    if n != 1:
        raise ValueError("template wrapper has no <StrategyType>")
    m = _STRATEGY_OPEN.search(text)
    if m is None:
        raise ValueError("template wrapper has no <Strategy>")
    end = m.end() if m.group(0).endswith("/>") else text.rindex("</Strategy>") + len("</Strategy>")
    return f"{text[:m.start()]}<Strategy>{strategy_text}</Strategy>{text[end:]}"


def write_template(path: Path, text: str) -> None:
    """Write a template as NT8 does: UTF-8 with a BOM. Atomic: a temp file, then a rename."""
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(".xml.tmp")
    tmp.write_bytes(BOM + text.encode("utf-8"))
    os.replace(tmp, path)
