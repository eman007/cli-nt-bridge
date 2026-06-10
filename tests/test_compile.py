from nt8bridge import compile as bc


def test_build_request_shape():
    req = bc.build_compile_request("abc123", "MyStrategy")
    assert req == {"id": "abc123", "kind": "compile", "typeName": "MyStrategy"}


def test_parse_ok_response():
    res = bc.parse_compile_response(
        {"id": "abc123", "status": "ok", "errors": [], "assemblyReloaded": True}
    )
    assert res.ok is True
    assert res.errors == []
    assert res.assembly_reloaded is True


def test_parse_error_response():
    res = bc.parse_compile_response(
        {
            "id": "abc123",
            "status": "error",
            "errors": [
                {"file": "MyStrategy.cs", "line": 10, "code": "CS0246", "message": "type not found"}
            ],
            "assemblyReloaded": False,
        }
    )
    assert res.ok is False
    assert res.errors[0]["code"] == "CS0246"


def test_new_request_id_is_unique():
    assert bc.new_request_id() != bc.new_request_id()


def test_run_compile_writes_trigger_and_reads_result(monkeypatch, tmp_path):
    from nt8bridge import ntio

    monkeypatch.setenv("NT8_DIR", str(tmp_path))
    monkeypatch.setattr(bc, "new_request_id", lambda: "fixed1")
    trigger, result = ntio.ensure_bridge_dirs()
    # Simulate the in-NT8 AddOn having already written the result.
    ntio.atomic_write_json(
        result / "compile_fixed1.json",
        {"id": "fixed1", "status": "ok", "errors": [], "assemblyReloaded": True},
    )
    res = bc.run_compile("MyStrategy", timeout=1.0)
    assert res.ok is True
    assert res.assembly_reloaded is True
    # The request file was dropped for the AddOn to consume.
    assert (trigger / "compile_fixed1.json").exists()
