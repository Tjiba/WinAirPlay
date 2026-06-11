import pytest
from unittest.mock import patch
from zeroconf import ServiceStateChange

from winairplay.discovery import AirPlayDevice, DeviceDiscovery, _parse_service_name


class _StubServiceInfo:
    """Resolved-record stub exposing only the modern zeroconf API
    (parsed_addresses), as used by _add_device."""

    def __init__(self, service_type, name, *, addresses, port=7000, props=None):
        self._addresses = addresses
        self.port = port
        self.properties = props or {b"et": b"0,3,5", b"md": b"0,1,2"}

    def request(self, zeroconf, timeout):
        return True

    def parsed_addresses(self):
        return list(self._addresses)


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

    def _add(self, disc, service_id, addresses, port=7000):
        full = f"{service_id}._raop._tcp.local."
        def _make(service_type, name):
            return _StubServiceInfo(service_type, name, addresses=addresses, port=port)
        with patch("winairplay.discovery.ServiceInfo", side_effect=_make):
            disc._add_device(None, "_raop._tcp.local.", full)

    def test_updated_state_refreshes_device(self):
        """A device that changes IP announces via ServiceStateChange.Updated —
        it must be re-resolved, not silently kept at the stale address."""
        disc = self._make_disc()
        seen = []
        disc._add_device = lambda zc, st, n: seen.append(n)
        disc._on_service_state_change(
            None, "_raop._tcp.local.", "AA@TV._raop._tcp.local.",
            ServiceStateChange.Updated,
        )
        assert seen == ["AA@TV._raop._tcp.local."]

    def test_add_device_prefers_routable_ipv4(self):
        """parsed_addresses() may return link-local and IPv6 entries; the routable
        IPv4 must win (pyatv connects by IPv4)."""
        disc = self._make_disc()
        self._add(disc, "AA@Salon",
                  ["fe80::1", "169.254.1.5", "192.168.1.10"])
        devs = disc.devices
        assert len(devs) == 1
        dev = next(iter(devs.values()))
        assert dev.host == "192.168.1.10"
        assert dev.name == "Salon"

    def test_add_device_falls_back_to_ipv6_when_no_ipv4(self):
        disc = self._make_disc()
        self._add(disc, "AA@Salon", ["fe80::1", "2a01:db8::5"])
        dev = next(iter(disc.devices.values()))
        assert dev.host == "2a01:db8::5"

    def test_devices_with_same_display_name_coexist(self):
        """Two devices both named 'TV' (distinct MAC prefixes) must not collide —
        keying is by the unique mDNS service id, not the display name."""
        disc = self._make_disc()
        self._add(disc, "AAA@TV", ["192.168.1.10"])
        self._add(disc, "BBB@TV", ["192.168.1.11"])
        devs = disc.devices
        assert len(devs) == 2
        assert {d.name for d in devs.values()} == {"TV"}
        assert {d.host for d in devs.values()} == {"192.168.1.10", "192.168.1.11"}

    def test_device_id_holds_unique_service_id(self):
        disc = self._make_disc()
        self._add(disc, "AAA@TV", ["192.168.1.10"])
        dev = next(iter(disc.devices.values()))
        assert dev.id == "AAA@TV"

    def test_removed_keyed_by_service_id(self):
        """Removal must match the same key the device was added under."""
        disc = self._make_disc()
        self._add(disc, "AAA@TV", ["192.168.1.10"])
        self._add(disc, "BBB@TV", ["192.168.1.11"])
        disc._on_service_state_change(
            None, "_raop._tcp.local.", "AAA@TV._raop._tcp.local.",
            ServiceStateChange.Removed,
        )
        devs = disc.devices
        assert len(devs) == 1
        assert next(iter(devs.values())).host == "192.168.1.11"
