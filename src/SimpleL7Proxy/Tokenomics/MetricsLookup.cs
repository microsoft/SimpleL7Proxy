namespace SimpleL7Proxy.Tokenomics;

/// <summary>Defines asynchronous user-metrics lookups used to evaluate tokenomics conditions.</summary>
public class MetricsLookup {
    /// <summary>Gets the user's input and output token usage for the current UTC day.</summary>
    public virtual Task<long> GetDailyTokenBalanceAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }

    /// <summary>Gets the user's input and output token usage for the current UTC month.</summary>
    public virtual Task<long> GetMonthlyTokenBalanceAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }

    /// <summary>Gets the user's USD spend for the current UTC day.</summary>
    public virtual Task<decimal> GetDailyBudgetUsageAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }

    /// <summary>Gets the user's USD spend for the current UTC month.</summary>
    public virtual Task<decimal> GetMonthlyBudgetUsageAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }

    /// <summary>Determines whether the user's recent activity is classified as abusive.</summary>
    public virtual Task<bool> IsAbuseDetectedAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }

    /// <summary>Determines whether an administrator override is active for the user.</summary>
    public virtual Task<bool> HasAdministratorOverrideAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }

    /// <summary>Determines whether the user has an approved policy exception.</summary>
    public virtual Task<bool> HasApprovedExceptionAsync(string userId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotImplementedException("No tokenomics metrics lookup provider is configured.");
    }
}