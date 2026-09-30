#!/bin/bash
cd ~/repos/SimpleL7Proxy/test/RegressionTests
./run-tests.sh all 2>&1 | tee /tmp/test_output.log
echo "Tests completed with exit code: $?"
