import json

from winairplay import config


def _redirect(monkeypatch, tmp_path):
    monkeypatch.setenv("APPDATA", str(tmp_path))


class TestSettings:
    def test_get_setting_default_when_no_file(self, monkeypatch, tmp_path):
        _redirect(monkeypatch, tmp_path)
        assert config.get_setting("latency_ms", 120.0) == 120.0

    def test_set_then_get_roundtrip(self, monkeypatch, tmp_path):
        _redirect(monkeypatch, tmp_path)
        config.set_setting("latency_ms", 80.0)
        assert config.get_setting("latency_ms") == 80.0

    def test_set_merges_does_not_clobber(self, monkeypatch, tmp_path):
        _redirect(monkeypatch, tmp_path)
        config.set_setting("language", "fr")
        config.set_setting("latency_ms", 80.0)
        assert config.get_setting("language") == "fr"
        assert config.get_setting("latency_ms") == 80.0

    def test_corrupt_file_returns_default(self, monkeypatch, tmp_path):
        _redirect(monkeypatch, tmp_path)
        folder = tmp_path / "WinAirPlay"
        folder.mkdir()
        (folder / "config.json").write_text("{not json", encoding="utf-8")
        assert config.get_setting("language", "en") == "en"

    def test_writes_to_appdata_winairplay(self, monkeypatch, tmp_path):
        _redirect(monkeypatch, tmp_path)
        config.set_setting("k", 1)
        path = tmp_path / "WinAirPlay" / "config.json"
        assert path.exists()
        assert json.loads(path.read_text(encoding="utf-8"))["k"] == 1
