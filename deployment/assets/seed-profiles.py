import base64
import email.utils
import json
import os
import sys
import time
import urllib.error
import urllib.parse
import urllib.request


payload = base64.b64decode(os.environ["PROFILE_SAMPLE_BASE64"], validate=True)
json.loads(payload)
blob_url = os.environ["PROFILE_BLOB_URL"]
identity_url = os.environ["IDENTITY_ENDPOINT"]
identity_url += ("&" if "?" in identity_url else "?") + urllib.parse.urlencode({
    "resource": os.environ["STORAGE_AUDIENCE"],
    "api-version": "2019-08-01",
})

for attempt in range(1, 25):
    try:
        token_request = urllib.request.Request(identity_url, headers={
            "X-IDENTITY-HEADER": os.environ["IDENTITY_HEADER"],
        })
        with urllib.request.urlopen(token_request, timeout=15) as response:
            token = json.load(response)["access_token"]
        request = urllib.request.Request(blob_url, data=payload, method="PUT", headers={
            "Authorization": "Bearer " + token,
            "x-ms-version": "2023-11-03",
            "x-ms-date": email.utils.formatdate(usegmt=True),
            "x-ms-blob-type": "BlockBlob",
            "If-None-Match": "*",
            "Content-Type": "application/json",
        })
        with urllib.request.urlopen(request, timeout=15) as response:
            if response.status != 201:
                raise RuntimeError("Unexpected upload status: " + str(response.status))
        print("profiles/config.json created; existing blobs are never overwritten.", flush=True)
        sys.exit(0)
    except urllib.error.HTTPError as error:
        code = error.headers.get("x-ms-error-code", "")
        if error.url == blob_url and (
            (error.code == 412 and code == "ConditionNotMet")
            or (error.code == 409 and code == "BlobAlreadyExists")
        ):
            print("profiles/config.json already exists; preserved unchanged.", flush=True)
            sys.exit(0)
        if error.code not in (401, 403, 408, 429, 500, 502, 503, 504):
            print(f"Profile seed failed: HTTP {error.code}, storage code {code}.", file=sys.stderr, flush=True)
            sys.exit(1)
        print(f"Profile seed attempt {attempt}/24: HTTP {error.code}; waiting for identity/RBAC/network.", flush=True)
    except (urllib.error.URLError, TimeoutError, ConnectionError) as error:
        print(f"Profile seed attempt {attempt}/24: network unavailable ({type(error).__name__}).", flush=True)
    if attempt < 24:
        time.sleep(10)

print("Profile seed exhausted retries; check job execution logs, container RBAC and private DNS.", file=sys.stderr, flush=True)
sys.exit(1)
