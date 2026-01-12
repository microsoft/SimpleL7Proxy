"""Standalone health probe server for readiness, startup, and liveness checks.
Port is taken from the HEALTHPROBE_PORT env var (default 9000). Responses are synchronous
and avoid async frameworks to minimize overhead under load.
"""
from __future__ import annotations

import logging
import os
import socket
import threading
import time
from enum import Enum
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Optional
from urllib.parse import parse_qs, urlparse


class HealthStatus(Enum):
    LIVENESS_READY = 0
    READINESS_ZERO_HOSTS = 1
    READINESS_FAILED_HOSTS = 2
    READINESS_READY = 3
    STARTUP_WORKERS_NOT_READY = 4
    STARTUP_ZERO_HOSTS = 5
    STARTUP_FAILED_HOSTS = 6
    STARTUP_READY = 7


_OK = b"OK\n"
_ZERO_HOSTS = b"Not Healthy.  Active Hosts: 0\n"
_FAILED_HOSTS = b"Not Healthy.  Failed Hosts: True\n"


class HealthState:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._readiness: HealthStatus = HealthStatus.READINESS_ZERO_HOSTS
        self._startup: HealthStatus = HealthStatus.STARTUP_ZERO_HOSTS
        self._last_update: float = 0.0

    def snapshot(self) -> tuple[HealthStatus, HealthStatus, float]:
        with self._lock:
            return self._readiness, self._startup, self._last_update

    def update(self, readiness: HealthStatus, startup: HealthStatus) -> None:
        with self._lock:
            self._readiness = readiness
            self._startup = startup
            self._last_update = time.perf_counter()


class ProbeRequestHandler(BaseHTTPRequestHandler):
    state: HealthState
    logger = logging.getLogger("probe-server")
    _readiness_checks = 0
    _last_readiness_log = 0.0
    _liveness_checks = 0
    _last_liveness_log = 0.0

    server_version = "SimpleL7ProbeServr/1.0"
    sys_version = ""

    def log_message(self, format: str, *args) -> None:
        # Silence default access logs to keep output minimal under load.
        return

    def do_GET(self) -> None:  # noqa: N802 (BaseHTTPRequestHandler naming)
        parsed = urlparse(self.path)
        route = parsed.path
        if route == "/readiness":
            self._handle_readiness(is_head=False)
        elif route == "/startup":
            self._handle_startup(is_head=False)
        elif route == "/liveness":
            self._handle_liveness(is_head=False)
        elif route == "/internal/update-status":
            self._handle_update(parsed.query)
        else:
            self.send_error(404)

    def do_HEAD(self) -> None:  # noqa: N802
        parsed = urlparse(self.path)
        route = parsed.path
        if route == "/readiness":
            self._handle_readiness(is_head=True)
        elif route == "/startup":
            self._handle_startup(is_head=True)
        elif route == "/liveness":
            self._handle_liveness(is_head=True)
        else:
            self.send_error(404)

    def _write_response(self, status: int, body: bytes, *, close: bool = True, is_head: bool = False) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "text/plain")
        self.send_header("Cache-Control", "no-cache")
        if close:
            self.send_header("Connection", "close")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        if not is_head:
            self.wfile.write(body)

    def _stale_status(self, last_update: float) -> bool:
        return last_update > 0 and (time.perf_counter() - last_update) >= 10.0

    def _handle_readiness(self, *, is_head: bool) -> None:
        readiness, _, last_update = self.state.snapshot()
        self.__class__._readiness_checks += 1
        now = time.perf_counter()
        if now - self.__class__._last_readiness_log >= 60.0:
            self.logger.warning("Readiness checks in last minute: %s", self.__class__._readiness_checks)
            self.__class__._readiness_checks = 0
            self.__class__._last_readiness_log = now

        if self._stale_status(last_update):
            self._write_response(503, _FAILED_HOSTS, is_head=is_head)
            return

        if readiness == HealthStatus.READINESS_ZERO_HOSTS:
            self._write_response(503, _ZERO_HOSTS, is_head=is_head)
        elif readiness == HealthStatus.READINESS_FAILED_HOSTS:
            self._write_response(503, _FAILED_HOSTS, is_head=is_head)
        else:
            self._write_response(200, _OK, is_head=is_head)

    def _handle_startup(self, *, is_head: bool) -> None:
        _, startup, last_update = self.state.snapshot()
        if self._stale_status(last_update):
            self._write_response(503, _FAILED_HOSTS, is_head=is_head)
            return

        if startup == HealthStatus.STARTUP_ZERO_HOSTS:
            self._write_response(503, _ZERO_HOSTS, is_head=is_head)
        elif startup == HealthStatus.STARTUP_FAILED_HOSTS:
            self._write_response(503, _FAILED_HOSTS, is_head=is_head)
        else:
            self._write_response(200, _OK, is_head=is_head)

    def _handle_liveness(self, *, is_head: bool) -> None:
        start = time.perf_counter()
        self.__class__._liveness_checks += 1
        now = time.perf_counter()
        if now - self.__class__._last_liveness_log >= 60.0:
            self.logger.warning("Liveness checks in last minute: %s", self.__class__._liveness_checks)
            self.__class__._liveness_checks = 0
            self.__class__._last_liveness_log = now

        self._write_response(200, _OK, is_head=is_head)
        elapsed_ms = (time.perf_counter() - start) * 1000.0
        if elapsed_ms >= 100:
            self.logger.warning("Liveness latency %.1f ms (>= 100 ms)", elapsed_ms)

    def _handle_update(self, raw_query: str) -> None:
        query = parse_qs(raw_query, keep_blank_values=False)
        readiness_val: Optional[str] = None
        startup_val: Optional[str] = None
        if "readiness" in query:
            readiness_val = query["readiness"][0]
        if "startup" in query:
            startup_val = query["startup"][0]

        if not readiness_val or not startup_val:
            self.logger.warning("Missing query parameters. Readiness present: %s, Startup present: %s", bool(readiness_val), bool(startup_val))
            self._write_response(400, b"Missing query parameters", close=True)
            return

        readiness = self._parse_status(readiness_val)
        startup = self._parse_status(startup_val)
        if readiness is None or startup is None:
            self.logger.warning("Invalid enum values. Readiness: %s, Startup: %s", readiness_val, startup_val)
            self._write_response(400, b"Invalid enum values", close=True)
            return

        self.state.update(readiness, startup)
        self._write_response(200, _OK, close=True)

    def _parse_status(self, value: str) -> Optional[HealthStatus]:
        # Accept enum names sent from .NET (e.g., "ReadinessReady") and numeric values
        raw = value.strip()
        normalized_name = raw.replace("_", "").lower()

        for status in HealthStatus:
            name_no_underscores = status.name.replace("_", "").lower()
            if normalized_name == name_no_underscores:
                return status

        try:
            return HealthStatus[raw.upper()]
        except KeyError:
            pass

        for status in HealthStatus:
            if str(status.value) == raw:
                return status

        return None


def _create_server(port: int, state: HealthState) -> ThreadingHTTPServer:
    server = ThreadingHTTPServer(("", port), ProbeRequestHandler)
    server.socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    try:
        server.socket.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    except OSError:
        pass
    ProbeRequestHandler.state = state
    return server


def run() -> None:
    port = int(os.getenv("HEALTHPROBE_PORT", "9000"))
    logging.basicConfig(level=logging.WARNING, format="%(asctime)s %(levelname)s %(message)s")
    state = HealthState()
    server = _create_server(port, state)
    ProbeRequestHandler.logger.warning("Probe server starting on port %s", port)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        ProbeRequestHandler.logger.warning("Probe server stopping")
    finally:
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    run()
