#!/bin/bash
# MultiPass retry test: cycles through priority groups (Host1 -> Host2) repeatedly,
# re-trying from Host1 each new pass, until MaxAttempts total backend attempts is reached.
unset Path_api Path_api2
unset Host_api_A Host_api_B Host_api2_A Host_api2_B

export Host1="host=http://localhost:3000;mode=direct;prioritygroup=1;acceptablepriorities=1:2:3"
export Host2="host=http://localhost:3001;mode=direct;prioritygroup=2;acceptablepriorities=1:2:3"
export LoadBalanceMode=prioritygroup
export IterationMode=MultiPass
export MaxAttempts=5
