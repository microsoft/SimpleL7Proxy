import requests
import concurrent.futures
import os
import threading

Token=os.getenv("token")
print(Token)
#url = http://localhost:5000/openai/deployments/gpt-4-turbo-2024/deployments/gpt4-turbo-2024-04-09/chat/completions?api-version=2024-02-15-preview
url = "http://localhost:5000/openai/deployments/"


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

def make_request():
    global counter

    with counter_lock:
        counter += 1
        seq_number = counter

    # Add the "seq" header
    headers_with_seq = headers.copy()
    headers_with_seq["x-Request-Sequence"] = str(seq_number)

    response = requests.post(url, headers=headers_with_seq, json=data, timeout=120)
    print(response.status_code)
    if response.status_code == 200:
        return "response.json()"
    else:
        return f"Request failed with status code {response.status_code}: {response.text}"

def main():
    with concurrent.futures.ThreadPoolExecutor(max_workers=50) as executor:
        futures = [executor.submit(make_request) for _ in range(1000)]
        for future in concurrent.futures.as_completed(futures):
            try:
                result = future.result()
                print(result)
            except Exception as e:
                print(f"An error occurred: {e}")

if __name__ == "__main__":
    main()