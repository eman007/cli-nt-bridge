"""Load and validate config.json against config.schema.json."""
from __future__ import annotations

import json
from pathlib import Path

import jsonschema

_SCHEMA_PATH = Path(__file__).with_name("config.schema.json")


class ConfigError(ValueError):
    """Raised when a config file is missing or invalid."""


def _schema() -> dict:
    return json.loads(_SCHEMA_PATH.read_text(encoding="utf-8"))


def load_config(path) -> dict:
    path = Path(path)
    if not path.exists():
        raise ConfigError(f"Config not found: {path}")
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        raise ConfigError(f"Config is not valid JSON: {e}") from e
    try:
        jsonschema.validate(data, _schema())
    except jsonschema.ValidationError as e:
        field = "/".join(str(p) for p in e.absolute_path) or e.message
        raise ConfigError(f"Config invalid at '{field}': {e.message}") from e
    return data
