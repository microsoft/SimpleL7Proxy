using System;
using System.Collections;
using System.Collections.Generic;

namespace SimpleL7Proxy.Backend;

/// <summary>
/// Abstract base class for backend host iterators providing common functionality
/// for iteration mode handling, pass tracking, and basic state management.
/// </summary>
public abstract class BaseHostIterator : IBackendHostIterator
{
    protected readonly List<BackendHostHealth> _hosts;
    protected readonly IterationModeEnum _mode;
    protected readonly int _maxAttempts;
    
    protected int _currentLoop;
    protected int _totalAttempts; // Track total attempts across all passes
    protected bool _hasCompletedAllPasses;

    protected BaseHostIterator(List<BackendHostHealth> hosts, IterationModeEnum mode, int maxAttempts)
    {
        _hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
        _mode = mode;
        _maxAttempts = Math.Max(1, maxAttempts);
        _currentLoop = 1;
        _totalAttempts = 0;
        _hasCompletedAllPasses = false;
    }

    /// <summary>
    /// Gets the current host. Must be implemented by derived classes.
    /// </summary>
    public abstract BackendHostHealth Current { get; }
    
    /// <summary>
    /// Gets the current host as object for IEnumerator interface.
    /// </summary>
    object IEnumerator.Current => Current;
    
    /// <summary>
    /// Indicates whether there are more hosts to iterate through.
    /// </summary>
    public bool HasMoreHosts => !_hasCompletedAllPasses;
    
    /// <summary>
    /// Gets the maximum number of attempts configured for this iterator.
    /// </summary>
    public int MaxAttempts => _maxAttempts;
    
    /// <summary>
    /// Gets the iteration mode for this iterator.
    /// </summary>
    public IterationModeEnum Mode => _mode;

    /// <summary>
    /// Moves to the next host. Handles common pass completion logic.
    /// </summary>
    public bool MoveNext()
    {
        if (_hosts.Count == 0 || _hasCompletedAllPasses)
            return false;

        // In MultiPass mode, check if we've reached max attempts before trying next host
        if (_mode == IterationModeEnum.MultiPass && _totalAttempts >= _maxAttempts)
        {
            _hasCompletedAllPasses = true;
            return false;
        }

        bool hasNext = MoveToNextHost();
        
        if (!hasNext)
        {
            // Completed iteration through all hosts
            return HandlePassCompletion();
        }

        // Increment total attempts after successfully moving to a host
        _totalAttempts++;
        return true;
    }

    /// <summary>
    /// Abstract method that derived classes must implement to move to the next host.
    /// Should return false when all hosts in the current pass have been visited.
    /// </summary>
    protected abstract bool MoveToNextHost();

    /// <summary>
    /// Handles the completion of a pass through all hosts.
    /// Returns true if there are more passes to do, false if iteration should stop.
    /// </summary>
    protected virtual bool HandlePassCompletion()
    {
        switch (_mode)
        {
            case IterationModeEnum.SinglePass:
                _hasCompletedAllPasses = true;
                return false;

            case IterationModeEnum.MultiPass:
                if (_currentLoop >= _maxAttempts)
                {
                    _hasCompletedAllPasses = true;
                    return false;
                }
                _currentLoop++;
                OnNewPassStarted();
                return MoveToNextHost(); // Start the new pass
                
            default:
                _hasCompletedAllPasses = true;
                return false;
        }
    }

    /// <summary>
    /// Called when a new pass is started in MultiPass mode.
    /// Derived classes can override this to perform pass-specific initialization.
    /// </summary>
    protected virtual void OnNewPassStarted()
    {
        // Default implementation does nothing
    }

    /// <summary>
    /// Records the result of a request to a host.
    /// Default implementation does nothing - derived classes can override for specific tracking.
    /// </summary>
    public virtual void RecordResult(BackendHostHealth host, bool success)
    {
        // Default implementation does nothing
    }

    /// <summary>
    /// Resets the iterator to its initial state.
    /// </summary>
    public virtual void Reset()
    {
        _currentLoop = 1;
        _totalAttempts = 0;
        _hasCompletedAllPasses = false;
        ResetToInitialState();
    }

    /// <summary>
    /// Abstract method for derived classes to reset their specific state.
    /// </summary>
    protected abstract void ResetToInitialState();

    /// <summary>
    /// Disposes the iterator. Default implementation does nothing.
    /// </summary>
    public virtual void Dispose()
    {
        // Default implementation does nothing
    }
}