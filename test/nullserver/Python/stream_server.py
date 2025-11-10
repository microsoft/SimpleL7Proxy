# Python nulled server for testing purposes
# This server will listen on port 3000 and will return a 200 OK response

import time
import random
import json
import signal
import http.server
import socketserver
import threading
import os
from urllib.parse import urlparse, parse_qs
from socketserver import ThreadingMixIn
import argparse

httpd = None  # Declare httpd as a global variable

class MyHandler(http.server.BaseHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        self.gotAuth = ""
        super().__init__(*args, **kwargs)
    
    def log_message(self, format, *args):
        """Override the default log message to include Authorization info"""
        auth_info = f"[AUTH: {self.gotAuth}]" if self.gotAuth else "[AUTH: None]"
        # Insert auth info before the request method
        original_message = format % args
        # Split the message to insert auth info in the right place
        parts = original_message.split('"')
        if len(parts) >= 2:
            # Insert auth info before the quoted request
            modified_message = f'{parts[0]}{auth_info} "{parts[1]}"'
            if len(parts) > 2:
                modified_message += '"'.join(parts[2:])
        else:
            modified_message = f"{original_message} {auth_info}"
        
        print(f"{self.address_string()} - - [{self.log_date_time_string()}] {modified_message}")

    def do_POST(self):
        self.do_GET()
        
    def do_GET(self):
        parsed_path = urlparse(self.path)
        query_params = parse_qs(parsed_path.query)
        # check if Authorization header is present
        self.gotAuth = ""
        for header, value in self.headers.items():
            if header == "Authorization":
                self.gotAuth = (len(value) > 1) and "yes" or "no"
        
        # Example: /health endpoint
        if parsed_path.path == '/health':
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"OK")
            return
        
        if parsed_path.path == '/429error':
            sleep_time = random.uniform(1, 1.5)  # Random sleep time
            print("Sleeping before sending error response..." + str(sleep_time) + " seconds")
            time.sleep(sleep_time)

            # Read the body
            content_length = int(self.headers.get('Content-Length', 0))  # Get the length of the body
            request_body = self.rfile.read(content_length).decode('utf-8')  # Read and decode the body
            print(f"Request: {parsed_path.path}  Body: {request_body}")
            self.send_response(429)
            self.send_header("Content-Type", "text/plain")
            self.send_header("retry-after-ms", "10000")
            self.send_header("S7PREQUEUE", "true")
            self.end_headers()
            self.wfile.write(b"Hello, world!")
            return
        
        # Pattern: /{code}error   ex: /412error, /500error, etc.
        if parsed_path.path.endswith('error') and len(parsed_path.path) > 5:

        
            try:
                # Extract error code from /{code}error format
                error_code_str = parsed_path.path[1:-5]  # Remove leading '/' and trailing 'error'
                error_code = int(error_code_str)
                self.send_response(error_code)
                self.send_header("Content-Type", "text/plain")
                self.end_headers()
                self.wfile.write(f"Error {error_code} occurred!".encode('utf-8'))
                return
            except ValueError:
                # Not a valid error code, fall through to default handling
                pass

        if parsed_path.path == '/killConnection':
            time.sleep(.5)
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
            self.handle_delay_endpoint(delay_seconds)
            return
                
        if parsed_path.path == '/echo/requeueME':

            # Read the body
            content_length = int(self.headers.get('Content-Length', 0))  # Get the length of the body
            request_body = self.rfile.read(content_length).decode('utf-8')  # Read and decode the body
            print(f"Request: {parsed_path.path}  Body: {request_body}")
            self.send_response(429)
            self.send_header("Content-Type", "text/plain")
            self.send_header("retry-after-ms", "10000")
            self.send_header("S7PREQUEUE", "true")
            self.end_headers()
            self.wfile.write(b"Hello, world!")
            return

        if parsed_path.path == '/success':
            # Log all incoming headers
            print("Headers received:")
            for header, value in self.headers.items():
                print(f"{header}: {value}")
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b" Congrats! You did it!")
            return

        # Example: /echo/resource?param1=sample
        if parsed_path.path == '/echo/resource':
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"Hello, world!")
            return

        if parsed_path.path.startswith('/openai'):
            sleep_time = random.uniform(5, 6)  # Random sleep time
            time.sleep(sleep_time)
            self.send_streaming_response("openAI.txt", "OpenAI")
            return

        if parsed_path.path == '/openai-ml':
            sleep_time = random.uniform(.4, .7)  # Random sleep time 
            time.sleep(sleep_time)
            self.send_streaming_response("openAI.txt", "MultiLineAllUsage")
            return
        
        if parsed_path.path == '/multiline':
            sleep_time = random.uniform(.4, .7)  # Random sleep time 
            time.sleep(sleep_time)
            self.send_streaming_response("multiline.txt", "MultiLineAllUsage")
            return

        if parsed_path.path.startswith('/file/'):
            filename = parsed_path.path[len('/file/'):]
            # Make sure the file exists
            if not os.path.exists(filename):
                self.send_response(404)
                self.end_headers()
                self.wfile.write(b"File not found")
                return

            sleep_time = random.uniform(.4, .7)  # Random sleep time
            time.sleep(sleep_time)
            self.send_streaming_response(filename, "MultiLineAllUsage")
            return

        # Default response
        # Extract specific headers
        request_sequence, queue_time, process_time, s7pid = self.extract_request_headers()

        # Sleep for a random number from 60 to 65 seconds
        sleep_time = random.uniform(60, 65)  # Random sleep time 
        time.sleep(sleep_time)

        print(f"Request: {parsed_path.path}  Sequence: {request_sequence} QueueTime: {queue_time} ProcessTime: {process_time} ID: {s7pid}")

        # Send response
        self.send_response(200)
        self.set_streaming_response_headers(request_sequence, queue_time, process_time, s7pid)
        self.end_headers()

        time.sleep(1)
        # Stream file contents line by line with a 1-second delay
        self.stream_file_contents("stream_data.txt")

        # Send the zero-length chunk to indicate the end of the response
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

    def extract_request_headers(self):
        request_sequence = self.headers.get('x-Request-Sequence', 'N/A')
        queue_time = self.headers.get('x-Request-Queue-Duration', 'N/A')
        process_time = self.headers.get('x-Request-Process-Duration', 'N/A')
        s7pid = self.headers.get('x-S7PID', 'N/A')
        return request_sequence,queue_time,process_time,s7pid
    
    def handle_delay_endpoint(self, delay_seconds):
        """Handle delay endpoints with the specified delay time."""
        print(f"Delaying for {delay_seconds} seconds...")
        time.sleep(delay_seconds)
        
        request_sequence, queue_time, process_time, s7pid = self.extract_request_headers()
        self.send_response(200)
        self.set_streaming_response_headers(request_sequence, queue_time, process_time, s7pid)
        self.send_header('TOKENPROCESSOR', 'OpenAI')
        self.end_headers()

        self.stream_file_contents("openAI.txt")
        
        # Send the zero-length chunk to indicate the end of the response
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()
    
    def send_streaming_response(self, filename="openAI.txt", processor="OpenAI"):
        """Send a streaming response with the specified file and processor."""
        request_sequence, queue_time, process_time, s7pid = self.extract_request_headers()
        self.send_response(200)
        self.set_streaming_response_headers(request_sequence, queue_time, process_time, s7pid)
        self.send_header('TOKENPROCESSOR', processor)
        self.end_headers()

        self.stream_file_contents(filename)
        
        # Send the zero-length chunk to indicate the end of the response
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()
    
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
        with open(filename, 'r') as file:
            for line in file:
                chunk = line.encode('utf-8')
                chunk_length = f"{len(chunk):X}\r\n".encode('utf-8')
                self.wfile.write(chunk_length)
                self.wfile.write(chunk)
                self.wfile.write(b"\r\n")
                self.wfile.flush()
                time.sleep(.01)

class ThreadedTCPServer(ThreadingMixIn, socketserver.TCPServer):
    daemon_threads = True
    pass

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
    if shutdown_after is not None:
        print(f"Server started on port {effective_port} (will stop after {shutdown_after}s)...")
    else:
        print(f"Server started on port {effective_port}...")

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
    print("Server started on port 3000...")
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

    mt_main(port=cli_port, shutdown_after=shutdown_after)
