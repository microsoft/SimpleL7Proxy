# Python nulled server for testing purposes
# This server will listen on port 3000 and will return a 200 OK response

import time
import random
import json
import math
import signal
import http.server
import socket
import socketserver
import threading
import os
from urllib.parse import urlparse, parse_qs
from socketserver import ThreadingMixIn
import argparse
import glob

httpd = None  # Declare httpd as a global variable

# Paths excluded from request counting, fail-first, and rate-limit tracking —
# includes the default APIM probe path so health polling doesn't consume test state.
_EXCLUDED_TRACKING_PATHS = ('/health', '/status-0123456789abcdef', '/echo/resource')

# Cache: filename -> tuple of pre-encoded HTTP chunks, one per input line
_file_cache = {}
# Cache: filename -> full response body bytes without HTTP chunk framing
_body_cache = {}

def _encode_chunked_lines(lines):
    """Pre-encode lines into individual HTTP chunks so each can be flushed separately."""
    chunks = []
    for line in lines:
        chunk = line.encode('utf-8')
        chunks.append(b"".join((f"{len(chunk):X}\r\n".encode('utf-8'), chunk, b"\r\n")))
    return tuple(chunks)

def _encode_body_lines(lines):
    """Pre-encode lines into a single response body without HTTP chunk framing."""
    return b"".join(line.encode('utf-8') for line in lines)

def load_file_cache():
    """Scan the current directory for .txt files and cache their pre-encoded contents.
    .json files are always read fresh from disk to support live editing."""
    for filepath in glob.glob('*.txt'):
        try:
            with open(filepath, 'r') as f:
                lines = f.readlines()
            _file_cache[filepath] = _encode_chunked_lines(lines)
            _body_cache[filepath] = _encode_body_lines(lines)
            total_bytes = sum(len(chunk) for chunk in _file_cache[filepath])
            print(f"Cached: {filepath} ({len(lines)} lines, {total_bytes} bytes across {len(_file_cache[filepath])} chunks)")
        except Exception as e:
            print(f"Warning: could not cache {filepath}: {e}")
    for filepath in glob.glob('*.json'):
        if os.path.isfile(filepath):
            print(f"Found:  {filepath} (will be read fresh on each request)")

def _is_json(filename):
    return filename.lower().endswith('.json')

def _read_file_fresh(filename):
    """Read a file from disk and return (chunked_data, body_bytes) or None."""
    try:
        with open(filename, 'r') as f:
            lines = f.readlines()
        return _encode_chunked_lines(lines), _encode_body_lines(lines)
    except FileNotFoundError:
        return None

def get_cached_data(filename):
    """Return pre-encoded chunked data for a file.
    .json files are always re-read from disk; .txt files use the cache."""
    if _is_json(filename):
        result = _read_file_fresh(filename)
        return result[0] if result else None
    if filename not in _file_cache:
        result = _read_file_fresh(filename)
        if result is None:
            return None
        _file_cache[filename] = result[0]
        _body_cache[filename] = result[1]
        total_bytes = sum(len(chunk) for chunk in _file_cache[filename])
        print(f"Cached on demand: {filename} ({total_bytes} bytes across {len(_file_cache[filename])} chunks)")
    return _file_cache[filename]

def get_cached_body(filename):
    """Return the full response body without chunk framing.
    .json files are always re-read from disk."""
    if _is_json(filename):
        result = _read_file_fresh(filename)
        return result[1] if result else None
    if filename not in _body_cache:
        if get_cached_data(filename) is None:
            return None
    return _body_cache[filename]

def is_cached_or_exists(filename):
    """Check if a file is cached or exists on disk."""
    if filename in _file_cache:
        return True
    if _is_json(filename):
        return os.path.isfile(filename)
    return get_cached_data(filename) is not None

import re as _re

def parse_delay(value):
    """Parse a delay value string and return seconds as a float.

    Supported formats:
      '1s'    -> 1.0 seconds
      '1.5s'  -> 1.5 seconds
      '500ms' -> 0.5 seconds
      '1000'  -> 1.0 seconds  (bare number = milliseconds)
    """
    value = value.strip()
    m = _re.match(r'^([\d.]+)\s*(s|ms)?$', value, _re.IGNORECASE)
    if not m:
        return 0.0
    num = float(m.group(1))
    unit = (m.group(2) or 'ms').lower()
    if unit == 's':
        return num
    return num / 1000.0

class MyHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = 'HTTP/1.1'
    _rate_limit = 0
    _rate_limit_lock = threading.Lock()
    _rate_limit_window_start = 0.0
    _rate_limit_request_count = 0
    _fail_first_n = 0
    _fail_first_status = 429
    _fail_first_lock = threading.Lock()
    _fail_first_count = 0
    _retry_after_once_lock = threading.Lock()
    _retry_after_once_keys = set()
    _request_count_lock = threading.Lock()
    _request_counts = {}

    def __init__(self, *args, **kwargs):
        self.gotAuth = ""
        super().__init__(*args, **kwargs)
    
    def log_message(self, format, *args):
        """Override the default log message to include Authorization and delay info"""
        if os.environ.get('NULL_SERVER_QUIET', 'false').lower() == 'true':
            return

        auth_info = f"[AUTH: {self.gotAuth}]" if self.gotAuth else "[AUTH: None]"
        delay_info = f" [DELAY: {self._delay_secs}s]" if getattr(self, '_delay_secs', 0) > 0 else ""
        # Insert auth and delay info before the request method
        original_message = format % args
        # Split the message to insert info in the right place
        parts = original_message.split('"')
        if len(parts) >= 2:
            modified_message = f'{parts[0]}{auth_info}{delay_info} "{parts[1]}"'
            if len(parts) > 2:
                modified_message += '"'.join(parts[2:])
        else:
            modified_message = f"{original_message} {auth_info}{delay_info}"
        
        print(f"{self.address_string()} - - [{self.log_date_time_string()}] {modified_message}")

    def do_POST(self):
        self.do_GET()
        
    def do_GET(self):
        parsed_path = urlparse(self.path)
        query_params = parse_qs(parsed_path.query)

        if parsed_path.path == '/stress-stats':
            with MyHandler._request_count_lock:
                body = json.dumps(MyHandler._request_counts, sort_keys=True).encode('utf-8')
            self.send_fixed_response(200, body, content_type="application/json")
            return

        if parsed_path.path not in _EXCLUDED_TRACKING_PATHS:
            with MyHandler._request_count_lock:
                MyHandler._request_counts[parsed_path.path] = MyHandler._request_counts.get(parsed_path.path, 0) + 1

        # Forces this process's first N non-health requests to fail, regardless of path,
        # so one backend can simulate an unhealthy host while a second one stays healthy.
        if parsed_path.path not in _EXCLUDED_TRACKING_PATHS and MyHandler._fail_first_n > 0:
            with MyHandler._fail_first_lock:
                should_fail = MyHandler._fail_first_count < MyHandler._fail_first_n
                if should_fail:
                    MyHandler._fail_first_count += 1

            if should_fail:
                self.close_connection = True
                # S7PREQUEUE=true is required for the proxy to sleep+requeue on 429 exhaustion.
                extra_headers = {"Retry-After-Ms": "1000", "Connection": "close"}
                if MyHandler._fail_first_status == 429:
                    extra_headers["S7PREQUEUE"] = "true"
                self.send_fixed_response(
                    MyHandler._fail_first_status,
                    b"Forced failure (NULL_SERVER_FAIL_FIRST_N)",
                    extra_headers=extra_headers)
                return

        if parsed_path.path not in _EXCLUDED_TRACKING_PATHS and MyHandler._rate_limit > 0:
            retry_after_seconds = None
            retry_after_ms = None

            with MyHandler._rate_limit_lock:
                now = time.monotonic()
                elapsed = now - MyHandler._rate_limit_window_start
                if elapsed >= 5.0:
                    elapsed_windows = int(elapsed // 5.0)
                    MyHandler._rate_limit_window_start += elapsed_windows * 5.0
                    MyHandler._rate_limit_request_count = 0

                if MyHandler._rate_limit_request_count >= MyHandler._rate_limit:
                    remaining = MyHandler._rate_limit_window_start + 5.0 - now
                    retry_after_seconds = max(1, math.ceil(remaining))
                    retry_after_ms = max(1, math.ceil(remaining * 1000))
                else:
                    MyHandler._rate_limit_request_count += 1

            if retry_after_seconds is not None:
                self.close_connection = True
                self.send_fixed_response(
                    429,
                    b"Rate limit exceeded",
                    extra_headers={
                        "Retry-After": str(retry_after_seconds),
                        "retry-after-ms": str(retry_after_ms),
                        "Connection": "close"
                    })
                return

        processor = self.headers.get('X-TokenProcessor', 'MultiLineAllUsage')
        delayms = self.headers.get('X-DelaySecs', '0')
        streaming = self.headers.get('X-Streaming', 'false').lower() == 'true'

        if delayms and float(delayms) > 0:
            delay_val = float(delayms)
            sleep_time = random.uniform(delay_val, delay_val * 1.5)  # Random sleep time
            print("Sleeping for " + str(sleep_time) + " seconds before sending response.")
            time.sleep(sleep_time)

        # Optional process-wide delay for a specific backend in a test harness.
        process_delay_ms = float(os.environ.get('NULL_SERVER_DELAY_MS', '0') or '0')
        if process_delay_ms > 0 and parsed_path.path not in ('/health', '/status-0123456789abcdef', '/stress-stats'):
            time.sleep(process_delay_ms / 1000.0)

        # All endpoints support ?delay=<value> (e.g. 1s, 500ms, 1000). Default 0 (no delay).
        delay_secs = parse_delay(query_params.get('delay', ['0'])[0])
        self._delay_secs = delay_secs
        if delay_secs > 0:
            time.sleep(delay_secs)

        # check if Authorization header is present
        self.gotAuth = ""
        for header, value in self.headers.items():
            if header == "Authorization":
                self.gotAuth = (len(value) > 1) and "yes" or "no"

        # Example: /status-0123456789abcdef endpoint
        if parsed_path.path == '/status-0123456789abcdef':
            self.send_fixed_response(200, b"OK")
            return
        
        # Example: /health endpoint
        if parsed_path.path == '/health':
            self.send_fixed_response(200, b"OK")
            return

        if parsed_path.path == '/retry-after-once':
            key = query_params.get('key', ['default'])[0]
            try:
                retry_after_ms = max(1, int(query_params.get('retryAfterMs', ['1500'])[0]))
            except ValueError:
                self.send_fixed_response(400, b"retryAfterMs must be a positive integer")
                return

            throttle_port = query_params.get('throttlePort', [None])[0]
            if throttle_port is not None:
                try:
                    throttle_port = int(throttle_port)
                except ValueError:
                    self.send_fixed_response(400, b"throttlePort must be an integer")
                    return

                if self.server.server_address[1] != throttle_port:
                    print(
                        f"RETRY_AFTER_ONCE key={key} attempt=1 "
                        f"timestamp={time.time():.6f} retry_after_ms={retry_after_ms} throttled=false",
                        flush=True)
                    self.send_fixed_response(200, b"Retry succeeded")
                    return

            with MyHandler._retry_after_once_lock:
                first_attempt = key not in MyHandler._retry_after_once_keys
                MyHandler._retry_after_once_keys.add(key)

            attempt = 1 if first_attempt else 2
            print(
                f"RETRY_AFTER_ONCE key={key} attempt={attempt} "
                f"timestamp={time.time():.6f} retry_after_ms={retry_after_ms} throttled={str(first_attempt).lower()}",
                flush=True)

            if first_attempt:
                self.close_connection = True
                self.send_fixed_response(
                    503,
                    b"Retry later",
                    extra_headers={
                        "Retry-After-Ms": str(retry_after_ms),
                        "Connection": "close"
                    })
            else:
                self.send_fixed_response(200, b"Retry succeeded")
            return
        
        if parsed_path.path == '/429error':
            # Read the body
            content_length = int(self.headers.get('Content-Length', 0))  # Get the length of the body
            request_body = self.rfile.read(content_length).decode('utf-8')  # Read and decode the body
            print(f"Request: {parsed_path.path}  Body: {request_body}")
            body = b"Hello, world!"
            self.send_response(429)
            self.send_header("Content-Type", "text/plain")
            self.send_header("retry-after-ms", "10000")
            self.send_header("S7PREQUEUE", "true")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if parsed_path.path == '/429terminal':
            self.send_fixed_response(
                429,
                b"Terminal rate limit",
                extra_headers={"Retry-After-Ms": "100"})
            return
        
        # Pattern: /{code}error   ex: /412error, /500error, etc.
        if parsed_path.path.endswith('error') and len(parsed_path.path) > 5:

        
            try:
                # Extract error code from /{code}error format
                error_code_str = parsed_path.path[1:-5]  # Remove leading '/' and trailing 'error'
                error_code = int(error_code_str)
                body = f"Error {error_code} occurred!".encode('utf-8')
                self.send_fixed_response(error_code, body)
                return
            except ValueError:
                # Not a valid error code, fall through to default handling
                pass

        if parsed_path.path == '/killConnection':
            self.wfile.close()
            print("Connection closed")
            return
        
        # Handle delay endpoints with pattern matching
        delay_patterns = {
            '/delay10seconds': 10,
            '/delay100seconds': 100,
            '/delay200seconds': 200,
            '/delay400seconds': 400,
            '/delay800seconds': 800
        }
        
        if parsed_path.path in delay_patterns:
            delay_seconds = delay_patterns[parsed_path.path]
            self.handle_delay_endpoint(delay_seconds, streaming)
            return
                
        if parsed_path.path == '/echo/requeueME':

            # Read the body
            content_length = int(self.headers.get('Content-Length', 0))  # Get the length of the body
            request_body = self.rfile.read(content_length).decode('utf-8')  # Read and decode the body
            print(f"Request: {parsed_path.path}  Body: {request_body}")
            body = b"Hello, world!"
            self.send_response(429)
            self.send_header("Content-Type", "text/plain")
            self.send_header("retry-after-ms", "10000")
            self.send_header("S7PREQUEUE", "true")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if parsed_path.path == '/success':
            # Log all incoming headers
            print("Headers received:")
            for header, value in self.headers.items():
                print(f"{header}: {value}")
            self.send_fixed_response(200, b" Congrats! You did it!")
            return

        # Example: /echo/resource?param1=sample
        if parsed_path.path == '/echo/resource':
            self.send_fixed_response(200, b"Hello, world!")
            return

        if parsed_path.path.startswith('/openai'):
            self.send_streaming_response("openAI.txt", "OpenAI", streaming)
            return

        if parsed_path.path == '/openai-ml':
            self.send_streaming_response("openAI.txt", "MultiLineAllUsage", streaming)
            return

        if parsed_path.path == '/multiline':
            self.send_streaming_response("multiline.txt", "MultiLineAllUsage", streaming)
            return

        # /file-stream/<name> — chunked streaming response with text/event-stream content type
        if parsed_path.path.startswith('/file-stream/'):
            filename = parsed_path.path[len('/file-stream/'):]
            if not is_cached_or_exists(filename):
                self.send_fixed_response(404, b"File not found")
                return
            self.send_streaming_response(filename, processor, streaming)
            return

        # /file/<name> — fixed response with content type inferred from file extension
        if parsed_path.path.startswith('/file/'):
            filename = parsed_path.path[len('/file/'):]
            if not is_cached_or_exists(filename):
                self.send_fixed_response(404, b"File not found")
                return
            import mimetypes
            body = get_cached_body(filename)
            content_type, _ = mimetypes.guess_type(filename)
            if content_type is None:
                content_type = "application/octet-stream"
            self.send_fixed_response(200, body, content_type=content_type)
            return

        # Default response
        # Extract specific headers
        request_sequence, queue_time, process_time, s7pid = self.extract_request_headers()

        print(f"Request: {parsed_path.path}  Sequence: {request_sequence} QueueTime: {queue_time} ProcessTime: {process_time} ID: {s7pid}")

        try:
            self.send_streaming_response("stream_data.txt", processor, streaming)
        except BrokenPipeError:
            print(f"Client disconnected during streaming for {parsed_path.path}")

    def send_fixed_response(self, code, body, content_type="text/plain", extra_headers=None):
        """Send a non-chunked response with proper Content-Length for HTTP/1.1."""
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        if extra_headers:
            for k, v in extra_headers.items():
                self.send_header(k, v)
        self.end_headers()
        self.wfile.write(body)

    def extract_request_headers(self):
        request_sequence = self.headers.get('x-Request-Sequence', 'N/A')
        queue_time = self.headers.get('x-Request-Queue-Duration', 'N/A')
        process_time = self.headers.get('x-Request-Process-Duration', 'N/A')
        s7pid = self.headers.get('x-S7PID', 'N/A')
        return request_sequence,queue_time,process_time,s7pid
    
    def handle_delay_endpoint(self, delay_seconds, streaming=False):
        """Handle delay endpoints with the specified delay time."""
        print(f"Delaying for {delay_seconds} seconds...")
        time.sleep(delay_seconds)
        self.send_streaming_response("openAI.txt", "OpenAI", streaming)
    
    def send_streaming_response(self, filename="openAI.txt", processor="OpenAI", streaming=False):
        """Send a streaming response with the specified file and processor."""
        request_sequence, queue_time, process_time, s7pid = self.extract_request_headers()
        self.send_response(200)
        body = None
        if streaming:
            self.set_streaming_response_headers(request_sequence, queue_time, process_time, s7pid)
        else:
            body = get_cached_body(filename)
            if body is None:
                raise FileNotFoundError(f"File not found: {filename}")
            self.set_fixed_event_response_headers(request_sequence, queue_time, process_time, s7pid, len(body))
        self.send_header('TOKENPROCESSOR', processor)
        self.end_headers()

        try:
            if streaming:
                self.stream_file_contents(filename)
                self.wfile.write(b"0\r\n\r\n")
                self.wfile.flush()
            else:
                self.wfile.write(body)
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            print(f"Client disconnected during streaming of {filename}")

    def set_fixed_event_response_headers(self, request_sequence, queue_time, process_time, s7pid, content_length):
        self.send_header("x-Request-Sequence", request_sequence)
        self.send_header("x-Request-Queue-Duration", queue_time)
        self.send_header("x-Request-Process-Duration", process_time)
        self.send_header("x-S7PID", s7pid)
        self.send_header("Random-Header", "Random-Value")
        self.send_header("x-Random-Header", "Random-Value")
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Cache-Control', 'no-cache')
        self.send_header('Content-Length', str(content_length))
    
    def set_streaming_response_headers(self, request_sequence, queue_time, process_time, s7pid):
        self.send_header("x-Request-Sequence", request_sequence)
        self.send_header("x-Request-Queue-Duration", queue_time)
        self.send_header("x-Request-Process-Duration", process_time)
        self.send_header("x-S7PID", s7pid)
        self.send_header("Random-Header", "Random-Value")
        self.send_header("x-Random-Header", "Random-Value")
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Cache-Control', 'no-cache')
        self.send_header('Transfer-Encoding', 'chunked')

    def stream_file_contents(self, filename):
        chunks = get_cached_data(filename)
        if chunks is None:
            raise FileNotFoundError(f"File not found: {filename}")
        for chunk in chunks:
            self.wfile.write(chunk)
            self.wfile.flush()

class ThreadedTCPServer(ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def server_bind(self):
        rate_limit_value = os.environ.get('RATE_LIMIT_REQUESTS_PER_5_SECONDS', '0')
        try:
            rate_limit = int(rate_limit_value)
        except ValueError as exc:
            raise ValueError("RATE_LIMIT_REQUESTS_PER_5_SECONDS must be a non-negative integer") from exc
        if rate_limit < 0:
            raise ValueError("RATE_LIMIT_REQUESTS_PER_5_SECONDS must be a non-negative integer")

        MyHandler._rate_limit = rate_limit
        MyHandler._rate_limit_window_start = time.monotonic()
        MyHandler._rate_limit_request_count = 0

        fail_first_n_value = os.environ.get('NULL_SERVER_FAIL_FIRST_N', '0')
        try:
            fail_first_n = int(fail_first_n_value)
        except ValueError as exc:
            raise ValueError("NULL_SERVER_FAIL_FIRST_N must be a non-negative integer") from exc
        if fail_first_n < 0:
            raise ValueError("NULL_SERVER_FAIL_FIRST_N must be a non-negative integer")

        fail_first_status_value = os.environ.get('NULL_SERVER_FAIL_FIRST_STATUS', '429')
        try:
            fail_first_status = int(fail_first_status_value)
        except ValueError as exc:
            raise ValueError("NULL_SERVER_FAIL_FIRST_STATUS must be an integer") from exc

        MyHandler._fail_first_n = fail_first_n
        MyHandler._fail_first_status = fail_first_status
        MyHandler._fail_first_count = 0

        self.socket.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        super().server_bind()

shutdown_event = threading.Event()

def handle_sigint(signum, frame):
    print("\nReceived interrupt, shutting down server...")
    shutdown_event.set()

def mt_main(port=None, shutdown_after=None):
    """Start the threaded HTTP server.

    Args:
        port: int or None. If None, read from PORT env var or default 3000.
        shutdown_after: float seconds or None. If set, schedule an automatic shutdown.
    """
    global httpd

    # Determine effective port: CLI arg > env var > default
    effective_port = port if port is not None else int(os.environ.get('PORT', 3000))

    if effective_port < 1024 or effective_port > 65535:
        raise ValueError("Port must be between 1024 and 65535")

    httpd = ThreadedTCPServer(("localhost", effective_port), MyHandler)
    rate_limit_status = (f"rate limit {MyHandler._rate_limit} requests/5s"
                         if MyHandler._rate_limit > 0 else "rate limit disabled")
    if shutdown_after is not None:
        print(f"Server started on port {effective_port} ({rate_limit_status}, will stop after {shutdown_after}s)...")
    else:
        print(f"Server started on port {effective_port} ({rate_limit_status})...")

    # Start server in a separate thread
    server_thread = threading.Thread(target=httpd.serve_forever)
    server_thread.daemon = True
    server_thread.start()

    # If shutdown_after was supplied, schedule an automatic shutdown (useful for tests)
    if shutdown_after is not None:
        try:
            t = float(shutdown_after)
            if t > 0:
                timer = threading.Timer(t, shutdown_event.set)
                timer.daemon = True
                timer.start()
        except Exception:
            pass

    # Wait for shutdown signal
    shutdown_event.wait()

    # Shutdown the server
    httpd.shutdown()
    httpd.server_close()
    httpd = None
    print("Server shut down successfully")
        
def single_main():
    global httpd

    # Listen on port 3000
    httpd = ThreadedTCPServer(("localhost", 3000), MyHandler)
    rate_limit_status = (f"rate limit {MyHandler._rate_limit} requests/5s"
                         if MyHandler._rate_limit > 0 else "rate limit disabled")
    print(f"Server started on port 3000 ({rate_limit_status})...")
    try:
        httpd.serve_forever()
    finally:
        httpd.server_close()
        httpd = None

if __name__ == '__main__':
    signal.signal(signal.SIGINT, handle_sigint)
    parser = argparse.ArgumentParser(description='Lightweight stream/null server for local testing')
    parser.add_argument('--port', '-p', type=int, help='Port to listen on (overrides PORT env var)')
    # Short flag --shutdown/-s is the preferred name. Keep --shutdown-after as a long-only compatibility alias.
    parser.add_argument('--shutdown', '-s', type=float, dest='shutdown_after', help='If provided, server will automatically stop after N seconds (useful for tests)')
    parser.add_argument('--shutdown-after', type=float, dest='shutdown_after', help=argparse.SUPPRESS)
    args = parser.parse_args()

    # Call mt_main with explicit arguments (no attribute indirection)
    cli_port = args.port if args.port is not None else None
    shutdown_after = args.shutdown_after if args.shutdown_after is not None else None

    load_file_cache()
    mt_main(port=cli_port, shutdown_after=shutdown_after)
