# Simple L7 Proxy - Disposal Architecture Documentation

## Overview

This document provides a comprehensive guide to the disposal patterns and lifecycle management in the Simple L7 Proxy application. Understanding these patterns is crucial for maintaining the application and preventing resource leaks.

## Key Architectural Principles

### 1. **ProxyWorkers are LONG-RUNNING services** ❌ **NO IDisposable**
- Thousands of ProxyWorker instances run continuously until shutdown
- Each worker processes multiple RequestData objects throughout its lifetime
- Workers DO NOT own RequestData objects - they only process them
- **Intentionally does NOT implement IDisposable**

### 2. **RequestData objects MIGRATE between workers** ✅ **Complex IDisposable + IAsyncDisposable**
- Created for each incoming HTTP request
- Can move between different ProxyWorker instances during processing
- May survive beyond initial HTTP response for async processing
- Uses `SkipDispose` flag to prevent premature disposal during migration

### 3. **Stream Processors are PER-REQUEST resources** ✅ **Simple IDisposable**
- Created per request and disposed after each request processing
- Simple disposal pattern with minimal resources to clean up
- Always disposed in ProxyWorker's finally block

### 4. **BlobWriter is a SINGLETON service** ✅ **Standard IDisposable**
- Manages multiple blob containers and streams
- Creates streams for async processing that get disposed by AsyncWorker
- Handles coordination to prevent double-disposal issues

## Component Disposal Patterns

### RequestData.cs
**Purpose**: Represents individual HTTP requests with complex lifecycle
**Pattern**: IDisposable + IAsyncDisposable with migration support

```csharp
// Key disposal features:
- SkipDispose flag prevents disposal during migration
- Handles both sync and async disposal patterns
- ObjectDisposedException is expected and caught (async streams)
- Stream ownership coordination with BlobWriter/AsyncWorker
```

### ProxyWorker.cs  
**Purpose**: Long-running request processing service
**Pattern**: NO IDisposable (intentional design)

```csharp
// Key disposal responsibilities:
- Creates and disposes stream processors per request
- Sets SkipDispose=true when requeuing requests
- Sets SkipDispose=false before allowing RequestData disposal
- Handles processor disposal in finally blocks with exception catching
```

### BlobWriter.cs
**Purpose**: Singleton service for Azure blob operations  
**Pattern**: Standard IDisposable with resource caching

```csharp
// Key disposal features:
- Caches BlobContainerClient instances per userId
- Creates blob streams for async processing
- Coordinates with AsyncWorker for stream disposal
- Proper dispose pattern with _disposed flag
```

### Stream Processors (OpenAI, AllUsage, etc.)
**Purpose**: Per-request stream processing with inheritance hierarchy
**Pattern**: Base class hierarchy with consistent disposal patterns

```csharp
// INHERITANCE HIERARCHY:
// IStreamProcessor
//   └── BaseStreamProcessor (abstract)
//       ├── DefaultStreamProcessor
//       ├── NullStreamProcessor  
//       ├── HeaderTokensProcessor
//       └── JsonStreamProcessor (abstract)
//           ├── OpenAIProcessor
//           └── AllUsageProcessor

// Key disposal features:
- BaseStreamProcessor provides consistent dispose pattern for all processors
- JsonStreamProcessor adds data dictionary management for JSON-based processors
- Template method pattern allows customization while maintaining consistency
- All processors inherit proper disposal with _disposed flag and GC.SuppressFinalize
```

## Expected Runtime Behaviors

### Normal Behaviors (Not Errors)
1. **"Failed to dispose output stream"** - Expected for async requests where BlobWriter already disposed the stream
2. **ObjectDisposedException** in RequestData disposal - Normal for async processing flows
3. **Multiple ProxyWorkers handling same RequestData** - Expected during retries/requeuing
4. **Stream processors created and disposed frequently** - Expected per-request pattern

### Anti-Patterns Avoided
1. ❌ ProxyWorker implementing IDisposable (it's a long-running service)
2. ❌ Disposing RequestData from ProxyWorker (migration would break)
3. ❌ Not disposing stream processors (would cause memory leaks)
4. ❌ Double-disposal crashes (handled with try-catch patterns)


## Testing Disposal Patterns

### Verification Checklist
- [ ] ProxyWorker does NOT implement IDisposable
- [ ] RequestData implements both IDisposable and IAsyncDisposable  
- [ ] Stream processors implement IDisposable
- [ ] BlobWriter implements IDisposable
- [ ] ObjectDisposedException is caught in RequestData disposal
- [ ] SkipDispose prevents premature disposal during migration
- [ ] Stream processors are disposed in finally blocks
- [ ] Null checks exist for nullable OutputStream

### Load Testing Considerations
- Monitor memory usage during high request volumes
- Verify no stream processor leaks during sustained load
- Check that RequestData migration doesn't cause disposal issues
- Validate async request processing doesn't accumulate resources

## Future Maintenance Guidelines

1. **When adding new stream processors**: Always implement IDisposable and follow the established pattern
2. **When modifying RequestData**: Be careful with SkipDispose logic and stream ownership
3. **When adding new async processing**: Coordinate stream disposal with existing BlobWriter patterns
4. **When debugging disposal issues**: Check the expected behaviors list before assuming errors

## Performance Implications

- **Stream Processor Creation/Disposal**: Lightweight objects, minimal performance impact
- **RequestData Migration**: SkipDispose prevents unnecessary disposal overhead
- **BlobWriter Caching**: Container client reuse improves performance
- **Exception Handling**: Try-catch blocks in disposal prevent application crashes

---

*This documentation should be updated when disposal patterns change or new components are added.*
