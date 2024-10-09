import requests
import concurrent.futures
import os
import threading
import urllib3
import time

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

Token=os.getenv("token")
print(Token)
#url = http://localhost:5000/openai/deployments/gpt-4-turbo-2024/deployments/gpt4-turbo-2024-04-09/chat/completions?api-version=2024-02-15-preview
url = "https://ca.api.4i.com/echo/resource?param1=sample"
url = "http://localhost:5000/echo/resource?param1=sample"


headers = {
    "Content-Type": "application/json",
    "Authorization": f"Bearer {Token}",
    "api-key": "d9d1041700e24487b835101bee228f32"
}
data = {
    "messages": [
        {
            "role": "system",
            "content": "You are an AI assistant that helps people find information. tell me a joke."
        }
    ],
    "max_tokens": 800,
    "temperature": 0.7,
    "frequency_penalty": 0,
    "presence_penalty": 0,
    "top_p": 0.95,
    "stop": None
}

# Global counter and lock for thread safety
counter = 0
counter_lock = threading.Lock()
max_retries = 5

def make_request():
    global counter

    with counter_lock:
        counter += 1
        seq_number = counter

    # Add the "seq" header
    headers_with_seq = headers.copy()
    headers_with_seq["x-Request-Sequence"] = str(seq_number)
    #headers_with_seq["S7PDEBUG"]='true'

    print("Making request: " + str(seq_number))

    for attempt in range(max_retries):
        response = requests.post(url, headers=headers_with_seq, json=data, timeout=120, verify=False)
        queue_time = response.headers.get("x-Request-Queue-Duration") or response.headers.get("x-request-queue-duration") or'-'
        process_time = response.headers.get("x-Request-Process-Duration") or response.headers.get("x-request-process-duration") or '-'

        print(response.status_code, " - ", str(seq_number), " Q: ", queue_time, " P  ", process_time)

        if response.status_code == 200:
            return "response.json()"
        elif response.status_code == 429:
            retry_delay = int(response.headers.get("Retry-After", 500)) / 1000
            print(f"Request {seq_number} failed with status code 429. Retrying in {retry_delay} seconds...")
            time.sleep(retry_delay)
        else:
            return f"Request failed with status code {response.status_code}: {response.text}"

    return f"Request {seq_number} failed after {max_retries} retries."

def main():
    with concurrent.futures.ThreadPoolExecutor(max_workers=100) as executor:
        futures = [executor.submit(make_request) for _ in range(10000)]
        for future in concurrent.futures.as_completed(futures):
            try:
                result = future.result()
                print(result)
            except Exception as e:
                print(f"An error occurred: {e}")

if __name__ == "__main__":
    main()