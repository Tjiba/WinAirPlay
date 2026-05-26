import sys
import os
import pytest
from zeroconf import ServiceStateChange
sys.path.insert(0, os.path.dirname(os.path.dirname(__file__)))

from discovery import AirPlayDevice, DeviceDiscovery, _parse_service_name


class TestParseServiceName:
    def test_strips_mac_prefix(self):
        assert _parse_service_name("AABBCCDDEEFF@AppleTV") == "AppleTV"

    def test_no_at_sign_returns_full(self):
        assert _parse_service_name("MyDevice") == "MyDevice"

    def test_empty_string(self):
        assert _parse_service_name("") == ""

    def test_at_sign_only(self):
        assert _parse_service_name("@Device") == "Device"

    def test_preserves_spaces(self):
        assert _parse_service_name("AA@Living Room") == "Living Room"


class TestAirPlayDevice:
    def test_str_includes_name_and_host(self):
        d = AirPlayDevice(name="TV", host="192.168.1.5", port=5000)
        s = str(d)
        assert "TV" in s
        assert "192.168.1.5" in s


class TestDeviceDiscovery:
    def _make_disc(self, on_change=None):
        disc = DeviceDiscovery.__new__(DeviceDiscovery)
        import threading
        disc._devices = {}
        disc._lock = threading.Lock()
        disc._on_change = on_change or (lambda _: None)
        disc._zeroconf = None
        disc._browser = None
        return disc

    def test_devices_property_initially_empty(self):
        disc = self._make_disc()
        assert disc.devices == {}

    def test_devices_returns_copy(self):
        disc = self._make_disc()
        disc._devices["TV"] = AirPlayDevice("TV", "10.0.0.1", 5000)
        copy = disc.devices
        copy["X"] = AirPlayDevice("X", "x", 1)
        assert "X" not in disc._devices

    def test_remove_device_removes_and_fires_callback(self):
        fired = []
        disc = self._make_disc(on_change=lambda d: fired.append(dict(d)))
        disc._devices["TV"] = AirPlayDevice("TV", "10.0.0.1", 5000)
        disc._remove_device("TV")
        assert "TV" not in disc._devices
        assert len(fired) == 1
        assert "TV" not in fired[0]

    def test_remove_nonexistent_device_does_not_raise(self):
        disc = self._make_disc()
        disc._remove_device("DoesNotExist")  # should not raise

    def test_on_change_fires_with_current_devices(self):
        fired = []
        disc = self._make_disc(on_change=lambda d: fired.append(dict(d)))
        disc._devices["TV"] = AirPlayDevice("TV", "10.0.0.1", 5000)
        disc._on_change(disc._devices)
        assert "TV" in fired[0]
