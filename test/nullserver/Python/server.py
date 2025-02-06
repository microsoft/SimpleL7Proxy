# Python nulled server for testing purposes
# This server will listen on port 3000 and will return a 200 OK response

import socket
import time
import random
from concurrent.futures import ThreadPoolExecutor

def handle_client(client_socket):
    try:
        # Read the request
        request = client_socket.recv(1024).decode('utf-8')
        
        # If the URL is /health, respond immediately
        if request.startswith('GET /health'):
            response = "HTTP/1.1 200 OK\r\n"
            response += "Content-Type: text/plain\r\n"
            response += "\r\n"
            response += "OK"
            client_socket.send(response.encode('utf-8'))
            client_socket.close()
            return

        # Parse the headers
        headers = {}
        lines = request.split('\r\n')
        for line in lines[1:]:
            if line == '':
                break
            key, value = line.split(': ', 1)
            headers[key] = value
        
        # Extract specific headers
        request_sequence = headers.get('x-Request-Sequence', 'N/A')
        queue_time = headers.get('x-Request-Queue-Duration', 'N/A')
        process_time = headers.get('x-Request-Process-Duration', 'N/A')
        s7pid = headers.get('x-S7PID', 'N/A')
        
        # Write status on the console
        print(f"Request: {lines[0]}  Sequence: {request_sequence} QueueTime: {queue_time} ProcessTime: {process_time} ID: {s7pid}")

        # Create a response
        response = "HTTP/1.1 200 OK\r\n"
        response += "Content-Type: text/plain\r\n"
        response += "x-Request-Sequence: " + request_sequence + "\r\n"
        response += "x-Request-Queue-Duration: " + queue_time + "\r\n"
        response += "x-Request-Process-Duration: " + process_time + "\r\n"
        response += "x-S7PID: " + s7pid + "\r\n"
        response += "Random-Header: Random-Value\r\n"
        response += "x-Random-Header: Random-Value\r\n"
        response += "\r\n"
        response += "Hello, world!"

        # Sleep for a random number from 0ms to 5 seconds
        sleep_time = random.uniform(4, 5)  # Random float between 0 and 0.2 seconds
        time.sleep(sleep_time)

        # Send a response
        client_socket.send(response.encode('utf-8'))
        client_socket.flush()
    finally:
        # Close the connection
        client_socket.close()

def main():
    # Create a socket object
    server_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    # Bind to the port
    server_socket.bind(('localhost', 3000))
    # Queue up to 5 requests
    server_socket.listen(5)

    # Create a ThreadPoolExecutor with up to 100 workers
    with ThreadPoolExecutor(max_workers=100) as executor:
        while True:
            # Establish a connection
            client_socket, addr = server_socket.accept()
            # Submit the client handling function to the executor
            executor.submit(handle_client, client_socket)

if __name__ == '__main__':
    main()