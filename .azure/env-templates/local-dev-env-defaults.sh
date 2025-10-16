#!/bin/bash
# Seeded local defaults for SimpleL7Proxy local development

# Proxy listening port
export Port=8000

# 100s timeout when making backend requests
export Timeout=100000

# Backend #1
export Host1=http://localhost:3000
export Probe_path1=/health

# Backend #2
export Host2=http://localhost:3000
export Probe_path2=/health

# parallell connection workers
export Workers=5

# Queue depth for the priority queue 
export MaxQueueLength=40000

# Log these response headers to event log and application insights
export LogHeaders=Random-Header,x-Random-Header

# Report these headers back to the client on failures
export DependancyHeaders="Backend-Host, Host-URL, Status, Duration, Error, Message, Request-Date, backendLog, x-PolicyCycleCounter"

# Enable / disable async mode
export AsyncModeEnabled=false

# Enables user profiles
export UseProfiles=true

# Used for identifying used during profile lookup 
export UniqueUserHeaders=X-UserProfile

# Validation: required incoming request headers
export RequiredHeaders=test

# Validation: Remove these headers from incoming requests
export DisallowedHeaders=gg

# Validation: Require Request header[xx] value is contained in the user's profile 'Header1'
# if the user profile has  Header1: cat, dog, bird    then  request header[xx] has to be one of 'cat', 'dog'  or 'bird'
export ValidateHeaders=xx:Header1

# Validation: enable / disable request application ID lookup
export ValidateAuthAppID=false

# Logging: 
export APPINSIGHTS_CONNECTIONSTRING="<InstrumentationKey>"

# Send logs to ./events.log instead of the event hub
export LOGTOFILE=true

# Log poll events
export LogPoller=false

# Log probe events
export LogProbes=false

# Enable / disable logging of all response headers
export LogAllResponseHeaders=true
