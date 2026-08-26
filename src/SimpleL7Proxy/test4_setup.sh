#!/bin/bash
# Named Path_* route test: hosts= list order is now honored directly.
#   /api  -> Host1 first, then Host2, retries up to 3 attempts (route override)
#   /api2 -> Host2 first, then Host1 (uses global MaxAttempts, no override)
unset Host_api_A Host_api_B Host_api2_A Host_api2_B

export Host1="host=http://localhost:3000;mode=direct;prioritygroup=1;acceptablepriorities=1:2:3"
export Host2="host=http://localhost:3001;mode=direct;prioritygroup=2;acceptablepriorities=1:2:3"
export Host3="host=http://localhost:3002;mode=direct;prioritygroup=3;acceptablepriorities=1:2:3"

export Path_api="prefix=/api;hosts=Host1:Host2:Host3;stripprefix=true;maxattempts=3"
export Path_api2="prefix=/api2;hosts=Host2:Host1;stripprefix=true"

export LoadBalanceMode=prioritygroup
export IterationMode=MultiPass
export MaxAttempts=10
export LogToConsole="-poller,-BackendRequest,-ProxyRequestEnqueued"

export Workers=100
