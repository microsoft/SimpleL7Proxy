import concurrent.futures
import os
import threading

import requests
import urllib3

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

token = os.getenv("token")
api_key = os.getenv("API_KEY")
url = os.getenv("TARGET_URL", "http://localhost:5000/echo/resource?param1=sample")
oversized_header_count = int(os.getenv("OVERSIZED_HEADER_COUNT", "16"))
oversized_header_size_bytes = int(os.getenv("OVERSIZED_HEADER_SIZE_BYTES", "4096"))
request_count = int(os.getenv("REQUEST_COUNT", "1"))
max_workers = int(os.getenv("MAX_WORKERS", "1"))
request_timeout_seconds = float(os.getenv("REQUEST_TIMEOUT_SECONDS", "240"))

if oversized_header_count < 1:
    raise ValueError("OVERSIZED_HEADER_COUNT must be at least 1")
if oversized_header_size_bytes < 1:
    raise ValueError("OVERSIZED_HEADER_SIZE_BYTES must be at least 1")
if request_count < 1:
    raise ValueError("REQUEST_COUNT must be at least 1")
if max_workers < 1:
    raise ValueError("MAX_WORKERS must be at least 1")

headers = {
    "Content-Type": "application/json",
}
if token:
    headers["Authorization"] = f"Bearer {token}"
if api_key:
    headers["api-key"] = api_key

for header_number in range(1, oversized_header_count + 1):
    headers[f"x-Oversized-Header-{header_number}"] = "A" * oversized_header_size_bytes

generated_header_bytes = sum(
    len(header_name) + 2 + len(header_value) + 2
    for header_name, header_value in headers.items()
    if header_name.startswith("x-Oversized-Header-")
)

data = {
    "messages": [
        {
            "role": "system",
            "content": "You are an AI assistant that helps people find information. tell me a joke.",
        }
    ],
    "max_tokens": 800,
    "temperature": 0.7,
    "frequency_penalty": 0,
    "presence_penalty": 0,
    "top_p": 0.95,
    "stop": None,
}

counter = 0
counter_lock = threading.Lock()


def make_request():
    global counter

    with counter_lock:
        counter += 1
        seq_number = counter

    headers_with_seq = headers.copy()
    headers_with_seq["x-Request-Sequence"] = str(seq_number)

    try:
        response = requests.post(
            url,
            headers=headers_with_seq,
            json=data,
            timeout=request_timeout_seconds,
            verify=False,
        )
    except requests.RequestException as error:
        return f"Request {seq_number}: {type(error).__name__}: {error}"

    body_preview = response.text[:500].replace("\r", " ").replace("\n", " ")
    return (
        f"Request {seq_number}: status={response.status_code} "
        f"reason={response.reason!r} response_bytes={len(response.content)} "
        f"server={response.headers.get('Server', '-')} body={body_preview!r}"
    )


def main():
    print(f"Target: {url}")
    print(
        f"Oversized headers: count={oversized_header_count}, "
        f"value_bytes_each={oversized_header_size_bytes}, "
        f"generated_wire_bytes={generated_header_bytes}"
    )
    print(f"Requests: {request_count}; workers: {min(max_workers, request_count)}")

    with concurrent.futures.ThreadPoolExecutor(
        max_workers=min(max_workers, request_count)
    ) as executor:
        futures = [executor.submit(make_request) for _ in range(request_count)]
        for future in concurrent.futures.as_completed(futures):
            print(future.result())


if __name__ == "__main__":
    main()