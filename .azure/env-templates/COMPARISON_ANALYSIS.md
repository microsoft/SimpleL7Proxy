# Environment Variables Comparison Analysis

## Current Development Template vs Local Dev Script

### Key Differences Identified:

#### 1. **Backend Hosts**
- **Local Dev (`env copy.sh`)**: Uses `localhost:3000` for both hosts
- **Container Dev (`development.env`)**: Uses example URLs (commented out)

#### 2. **Port Configuration**
- **Local Dev**: `Port=8000` (explicit port setting)
- **Container Dev**: Uses default port (80) - containers handle port mapping

#### 3. **Queue Size**
- **Local Dev**: `MaxQueueLength=40000` (very large for local testing)
- **Container Dev**: `MaxQueueLength=10` (small for development)

#### 4. **Timeout**
- **Local Dev**: `Timeout=100000` (100 seconds)
- **Container Dev**: `Timeout=1800000` (30 minutes - much longer)

#### 5. **Workers**
- **Local Dev**: `Workers=5`
- **Container Dev**: `Workers=8`

#### 6. **Async Configuration**
- **Local Dev**: Full async setup with real Azure resources
  - Event Hub connection strings
  - Service Bus with real credentials
  - Blob storage with real URIs
  - `AsyncTriggerTimeout=1` (very fast)
- **Container Dev**: Basic async settings without real credentials

#### 7. **Logging**
- **Local Dev**: 
  - `LOGTOFILE=true`
  - `LogPoller=false`
  - `LogProbes=false`
  - `LogAllResponseHeaders=true`
- **Container Dev**: More comprehensive logging for debugging

#### 8. **Headers & Validation**
- **Local Dev**: Specific test headers and validation rules
  - `LogHeaders=Random-Header,x-Random-Header`
  - `RequiredHeaders=test`
  - `UniqueUserHeaders=X-UserProfile`
  - `DisallowedHeaders=gg`
  - `ValidateHeaders=xx:Header1`
- **Container Dev**: Generic development settings

#### 9. **Application Insights**
- **Local Dev**: Real Application Insights connection string
- **Container Dev**: No Application Insights configured

#### 10. **Missing Variables**
- **Local Dev**: Has many specific Azure resource references (ACR, GROUP, EVENTHUB_NAME)
- **Container Dev**: More generic development settings

## Recommendation: Create Two Development Scenarios

### Scenario 1: Local Development (`local-development.env`)
- For running with `dotnet run` on local machine
- Uses localhost backends
- Real Azure resource connections
- Smaller timeouts
- File logging enabled

### Scenario 2: Container Development (`container-development.env`) 
- For running in containers (local Docker or Azure Container Apps)
- Uses example backend URLs
- Generic settings for any development environment
- Extended timeouts for debugging
- Console logging focused