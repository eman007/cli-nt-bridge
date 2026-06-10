import nt8bridge


def test_package_has_version():
    assert isinstance(nt8bridge.__version__, str)
    assert nt8bridge.__version__
