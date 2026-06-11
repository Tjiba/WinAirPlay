import threading
from unittest.mock import MagicMock, patch

from winairplay.app import WinAirPlay
from winairplay.discovery import AirPlayDevice


def _make_app():
    """Bare WinAirPlay with the state _toggle_device & friends need — no tray,
    no Tk, no real audio device (object.__new__ skips __init__)."""
    app = WinAirPlay.__new__(WinAirPlay)
    app._lock = threading.Lock()
    app._capture_lock = threading.Lock()
    app._active_devices = {}
    app._raop_clients = {}
    app._client_volumes = {}
    app._reconnect_attempt_at = {}
    app._reconnect_fails = {}
    app._streaming = False
    app._stop_event = threading.Event()
    app._reconnect_needed = threading.Event()
    app._audio_loop_done = threading.Event()
    app._audio_loop_done.set()
    app._input_device_index = None
    app._input_device_name = "Default"
    app._tk_root = None
    app._popup = None
    app._capture = MagicMock()
    return app


class TestToggleDevice:
    def test_disconnect_last_device_stops_capture_under_capture_lock(self):
        """All capture start/stop must be serialized by _capture_lock; the
        disconnect-last-device path was the one site stopping without it."""
        app = _make_app()
        locked_at_stop = []
        app._capture.stop = lambda: locked_at_stop.append(
            app._capture_lock.locked())
        dev = AirPlayDevice(name="TV", host="1.2.3.4", port=7000)
        client = MagicMock()
        app._active_devices["TV"] = dev
        app._raop_clients["TV"] = client
        app._toggle_device(dev)
        client.disconnect.assert_called_once()
        assert locked_at_stop == [True]

    def test_toggle_keys_by_unique_id_not_display_name(self):
        """Two devices sharing a display name must toggle independently."""
        app = _make_app()
        dev_a = AirPlayDevice(name="TV", host="1.1.1.1", port=7000, id="AAA@TV")
        dev_b = AirPlayDevice(name="TV", host="2.2.2.2", port=7000, id="BBB@TV")
        client_a = MagicMock()
        app._active_devices["AAA@TV"] = dev_a
        app._raop_clients["AAA@TV"] = client_a
        with patch("winairplay.app.RAOPClient"), patch("winairplay.app.threading.Thread") as th:
            th.return_value = MagicMock()
            app._toggle_device(dev_b)  # same display name — must CONNECT, not disconnect A
        client_a.disconnect.assert_not_called()
        assert "AAA@TV" in app._active_devices
        assert "BBB@TV" in app._active_devices


class TestRestartStreams:
    def test_input_select_swaps_device_and_restarts_clients(self):
        app = _make_app()
        dev = AirPlayDevice(name="TV", host="1.2.3.4", port=7000, id="AA@TV")
        old_client = MagicMock()
        app._active_devices["AA@TV"] = dev
        app._raop_clients["AA@TV"] = old_client
        app._streaming = True
        new_client = MagicMock()
        with patch("winairplay.app.RAOPClient", return_value=new_client), \
             patch("winairplay.app.threading.Thread") as th:
            th.return_value = MagicMock()
            app._do_input_select(3, "Casque")
        old_client.disconnect.assert_called_once()
        app._capture.set_device_index.assert_called_once_with(3)
        app._capture.stop.assert_called_once()
        assert app._raop_clients["AA@TV"] is new_client
        assert app._active_devices["AA@TV"] is dev

    def test_latency_restart_reuses_devices_and_volumes(self):
        app = _make_app()
        dev = AirPlayDevice(name="TV", host="1.2.3.4", port=7000, id="AA@TV")
        old_client = MagicMock()
        app._active_devices["AA@TV"] = dev
        app._raop_clients["AA@TV"] = old_client
        app._client_volumes["AA@TV"] = 33.0
        app._streaming = True
        new_client = MagicMock()
        with patch("winairplay.app.RAOPClient", return_value=new_client), \
             patch("winairplay.app.threading.Thread") as th:
            th.return_value = MagicMock()
            app._restart_active_streams()
        old_client.disconnect.assert_called_once()
        assert app._raop_clients["AA@TV"] is new_client
