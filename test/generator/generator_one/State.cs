using System.Threading;

namespace test.generator.generator_one;

// Thread states for tracking test execution phases
public enum TestThreadState
{
    None = -1,          // No state / thread not active
    Active = 0,         // Thread is active/running
    Prep = 1,
    Begin = 2,          // Beginning/preparing request
    Sending = 3,        // Sending HTTP request
    Reading = 4,        // Reading HTTP response
    Calculate = 5,
    Disposing = 6,      // Disposing resources
    Sleeping = 7        // Sleeping/waiting between requests
}

// Thread state tracker - each testing thread instantiates one
public class ThreadState
{
    private TestThreadState _currentState = TestThreadState.None;
    private static int[]? _stats;

    public static void Initialize(int[] stats)
    {
        if (_stats != null) return; // Already initialized
        _stats = stats;
    }

    public void ChangeState(TestThreadState newState)
    {
        if (_stats == null) return; // Not initialized yet

        if (_currentState != newState)
        {
            // Decrement the old state
            if (_currentState >= 0)
            {
                Interlocked.Decrement(ref _stats[(int)_currentState]);
            }

            // Increment the new state
            if (newState >= 0)
            {
                Interlocked.Increment(ref _stats[(int)newState]);
            }

            _currentState = newState;
        }
    }

    public static string GetStateString()
    {
        return $" B-{_stats[(int)TestThreadState.Begin]}, S-{_stats[(int)TestThreadState.Sending]}, R-{_stats[(int)TestThreadState.Reading]}, C-{_stats[(int)TestThreadState.Calculate]}, D-{_stats[(int)TestThreadState.Disposing]}, SL-{_stats[(int)TestThreadState.Sleeping]} ";
    }




    public void Reset()
    {
        ChangeState(TestThreadState.None);
    }
}