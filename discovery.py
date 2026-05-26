import socket
import threading
from dataclasses import dataclass
from typing import Callable, Dict, Optional

from zeroconf import ServiceBrowser, ServiceInfo, ServiceStateChange, Zeroconf


AIRPLAY_SERVICE_TYPE = "_raop._tcp.local."


@dataclass
class AirPlayDevice:
    name: str
    host: str
    port: int
    et: str = ""    # encryption types from TXT record (e.g. "0,3,5")
    md: str = ""    # metadata types from TXT record (e.g. "0,1,2")

    def __str__(self) -> str:
        return f"{self.name} ({self.host}:{self.port})"


def _parse_service_name(raw: str) -> str:
    """Strip MAC-like prefix from RAOP service name: 'AABBCC@TV' -> 'TV'."""
    if "@" in raw:
        return raw.split("@", 1)[1]
    return raw


class DeviceDiscovery:
    def __init__(
        self,
        on_change: Optional[Callable[[Dict[str, AirPlayDevice]], None]] = None,
    ):
        self._devices: Dict[str, AirPlayDevice] = {}
        self._lock = threading.Lock()
        self._on_change = on_change or (lambda _: None)
        self._zeroconf: Optional[Zeroconf] = None
        self._browser: Optional[ServiceBrowser] = None

    @property
    def devices(self) -> Dict[str, AirPlayDevice]:
        with self._lock:
            return dict(self._devices)

    def start(self) -> None:
        self._zeroconf = Zeroconf()
        self._browser = ServiceBrowser(
            self._zeroconf,
            AIRPLAY_SERVICE_TYPE,
            handlers=[self._on_service_state_change],
        )

    def stop(self) -> None:
        if self._zeroconf:
            self._zeroconf.close()
            self._zeroconf = None
        self._browser = None

    def _on_service_state_change(
        self,
        zeroconf: Zeroconf,
        service_type: str,
        name: str,
        state_change: ServiceStateChange,
    ) -> None:
        if state_change == ServiceStateChange.Added:
            self._add_device(zeroconf, service_type, name)
        elif state_change == ServiceStateChange.Removed:
            raw = name.replace("." + service_type.rstrip("."), "").rstrip(".")
            self._remove_device(_parse_service_name(raw))

    def _add_device(self, zeroconf: Zeroconf, service_type: str, name: str) -> None:
        info = ServiceInfo(service_type, name)
        if not info.request(zeroconf, timeout=3000):
            return
        if not info.addresses:
            return
        host = socket.inet_ntoa(info.addresses[0])
        port = info.port
        raw = name.replace("." + service_type.rstrip("."), "").rstrip(".")
        display_name = _parse_service_name(raw)

        def _prop(key: str) -> str:
            val = info.properties.get(key.encode(), b"")
            return val.decode(errors="replace") if isinstance(val, bytes) else str(val)

        device = AirPlayDevice(
            name=display_name,
            host=host,
            port=port,
            et=_prop("et"),
            md=_prop("md"),
        )
        with self._lock:
            self._devices[display_name] = device
        self._on_change(self.devices)

    def _remove_device(self, display_name: str) -> None:
        with self._lock:
            self._devices.pop(display_name, None)
        self._on_change(self.devices)
