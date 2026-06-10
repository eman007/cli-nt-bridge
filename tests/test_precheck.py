from nt8bridge import precheck

SAMPLE = r"""
Microsoft (R) Build Engine
  Determining projects to restore...
C:\src\MyStrategy.cs(42,17): error CS0103: The name 'foo' does not exist in the current context [C:\check.csproj]
C:\src\MyStrategy.cs(58,9): error CS1002: ; expected [C:\check.csproj]
  1 Warning(s)
  2 Error(s)
"""

CLEAN = "Build succeeded.\n  0 Error(s)\n"


def test_parse_errors_extracts_structured_fields():
    errors = precheck.parse_errors(SAMPLE)
    assert len(errors) == 2
    first = errors[0]
    assert first.file.endswith("MyStrategy.cs")
    assert first.line == 42
    assert first.code == "CS0103"
    assert "does not exist" in first.message


def test_parse_errors_clean_output_is_empty():
    assert precheck.parse_errors(CLEAN) == []


def test_compile_error_is_jsonable():
    err = precheck.parse_errors(SAMPLE)[0]
    d = err.to_dict()
    assert d == {
        "file": err.file,
        "line": 42,
        "code": "CS0103",
        "message": err.message,
    }
