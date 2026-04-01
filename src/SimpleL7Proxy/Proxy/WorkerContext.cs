namespace SimpleL7Proxy.Proxy;

using SimpleL7Proxy.User;
using SimpleL7Proxy.Events;
using SimpleL7Proxy.Queue;
using SimpleL7Proxy.Backend;
using SimpleL7Proxy.Config;
using SimpleL7Proxy.StreamProcessor;
using Microsoft.Extensions.Logging;
using SimpleL7Proxy.Backend.Iterators;


public class WorkerContext
{
    //public ProxyStreamWriter ProxyStreamWriter { get; }
    public ProxyConfig BackendOptions { get; }
    public ConfigChangeNotifier ConfigChangeNotifier { get; }
    public EventDataBuilder EventDataBuilder { get; }
    public HealthCheckService HealthCheckService { get; }
    public IAsyncWorkerFactory AsyncWorkerFactory { get; }
    public IBackendService Backends { get; }
    public IConcurrentPriQueue<RequestData> Queue { get; }
    public IEventClient EventClient { get; }
    public ILogger<ProxyWorker> Logger { get; }
    public IRequeueWorker RequeueWorker { get; }
    public ISharedIteratorRegistry? SharedIteratorRegistry { get; }
    public IUserPriorityService UserPriorityService { get; }
    public IUserProfileService UserProfileService { get; }
    public RequestLifecycleManager LifecycleManager { get; }
    public StreamProcessorFactory StreamProcessorFactory { get; }

    public WorkerContext(
        ProxyConfig backendOptions,
        IConcurrentPriQueue<RequestData> queue,
        IBackendService backends,
        IUserPriorityService userPriorityService,
        IUserProfileService userProfileService,
        IRequeueWorker requeueWorker,
        IEventClient eventClient,
        ILogger<ProxyWorker> logger,
        IAsyncWorkerFactory asyncWorkerFactory,
        StreamProcessorFactory streamProcessorFactory,
        RequestLifecycleManager lifecycleManager,
        EventDataBuilder eventDataBuilder,
        HealthCheckService healthCheckService,
        ConfigChangeNotifier configChangeNotifier,
        ISharedIteratorRegistry? sharedIteratorRegistry = null)
    {

        ArgumentNullException.ThrowIfNull(backendOptions);
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(backends);
        ArgumentNullException.ThrowIfNull(userPriorityService);
        ArgumentNullException.ThrowIfNull(userProfileService);
        ArgumentNullException.ThrowIfNull(requeueWorker);
        ArgumentNullException.ThrowIfNull(eventClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(asyncWorkerFactory);
        ArgumentNullException.ThrowIfNull(streamProcessorFactory);
        ArgumentNullException.ThrowIfNull(lifecycleManager);
        ArgumentNullException.ThrowIfNull(eventDataBuilder);
        ArgumentNullException.ThrowIfNull(healthCheckService);
        ArgumentNullException.ThrowIfNull(configChangeNotifier);
        ArgumentNullException.ThrowIfNull(sharedIteratorRegistry);

        BackendOptions = backendOptions;
        Queue = queue;
        Backends = backends;
        UserPriorityService = userPriorityService;
        UserProfileService = userProfileService;
        RequeueWorker = requeueWorker;
        EventClient = eventClient;
        Logger = logger;
        AsyncWorkerFactory = asyncWorkerFactory;
        StreamProcessorFactory = streamProcessorFactory;
        LifecycleManager = lifecycleManager;
        EventDataBuilder = eventDataBuilder;
        HealthCheckService = healthCheckService;
        ConfigChangeNotifier = configChangeNotifier;
        SharedIteratorRegistry = sharedIteratorRegistry;
    }
}