export RATE_LIMIT_TOKENS_PER_MINUTE=0  # disable rate limiting for this server
export NULL_SERVER_FAIL_FIRST_N=10
export NULL_SERVER_FAIL_FIRST_STATUS=429
python3 stream_server.py --port 3002
