# Python nulled server for testing purposes
# This server will listen on port 3000 and will return a 200 OK response

import time
import random
import json
import http.server
import socketserver
from urllib.parse import urlparse, parse_qs
from socketserver import ThreadingMixIn

class MyHandler(http.server.BaseHTTPRequestHandler):
    def do_POST(self):
        self.do_GET()
        
    def do_GET(self):
        parsed_path = urlparse(self.path)
        query_params = parse_qs(parsed_path.query)

        # Example: /health endpoint
        if parsed_path.path == '/health':
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"OK")
            return

        # Example: /echo/resource?param1=sample
        if parsed_path.path == '/echo/resource':
            self.send_response(200)
            self.send_header("Content-Type", "text/plain")
            self.end_headers()
            self.wfile.write(b"Hello, world!")
            return

        # Default response

        # Extract specific headers
        request_sequence = self.headers.get('x-Request-Sequence', 'N/A')
        queue_time = self.headers.get('x-Request-Queue-Duration', 'N/A')
        process_time = self.headers.get('x-Request-Process-Duration', 'N/A')
        s7pid = self.headers.get('x-S7PID', 'N/A')

        # Sleep for a random number from 4 to 5 seconds
        sleep_time = random.uniform(0.5, 1.5)  # Random float between 0 and 0.2 seconds
        time.sleep(sleep_time)

        print(f"Request: {parsed_path.path}  Sequence: {request_sequence} QueueTime: {queue_time} ProcessTime: {process_time} ID: {s7pid}")

        # Send response
        self.send_response(200)
        self.send_header("x-Request-Sequence", request_sequence)
        self.send_header("x-Request-Queue-Duration", queue_time)
        self.send_header("x-Request-Process-Duration", process_time)
        self.send_header("x-S7PID", s7pid)
        self.send_header("Random-Header", "Random-Value")
        self.send_header("x-Random-Header", "Random-Value")
        self.send_header('Content-Type', 'text/event-stream')
        self.send_header('Cache-Control', 'no-cache')
        self.send_header('Transfer-Encoding', 'chunked')
        self.end_headers()

        # Initialize repeat_count attribute
        self.repeat_count = 1  # Set to desired repeat count

        # Stream file contents line by line with a 1-second delay
        file_path = 'stream_data.txt'
        with open(file_path, 'r') as file:
            for line in file:
                response_message = json.dumps({"choices": [{"delta": {"content": line.strip()}}]})
                chunk = f"data: {response_message}\n\n".encode('utf-8')
                chunk_length = f"{len(chunk):X}\r\n".encode('utf-8')
                self.wfile.write(chunk_length)
                self.wfile.write(chunk)
                self.wfile.write(b"\r\n")
                self.wfile.flush()
                time.sleep(1)

        # Send the zero-length chunk to indicate the end of the response
        self.wfile.write(b"0\r\n\r\n")
        self.wfile.flush()

class ThreadedTCPServer(ThreadingMixIn, socketserver.TCPServer):
    pass

def mt_main():
    # Listen on port 3000
    with ThreadedTCPServer(("localhost", 3000), MyHandler) as httpd:
        print("Server started on port 3000...")
        httpd.serve_forever()
        
def single_main():
    # Listen on port 3000
    with socketserver.TCPServer(("localhost", 3000), MyHandler) as httpd:
        print("Server started on port 3000...")
        httpd.serve_forever()

if __name__ == '__main__':
    mt_main()