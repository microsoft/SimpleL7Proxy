using System.Collections;
using System.Reflection;

namespace SimpleL7Proxy.Config;

public static class ConfigComparer
{
  public static IReadOnlyDictionary<string, (object? OldValue, object? NewValue)> GetChangedOptions(BackendOptions previousOptions, BackendOptions currentOptions)
  {
    var changes = new SortedDictionary<string, (object? OldValue, object? NewValue)>(StringComparer.Ordinal);

    foreach (var prop in typeof(BackendOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (!prop.CanRead)
        continue;

      if (prop.Name == nameof(BackendOptions.Client) || prop.Name == nameof(BackendOptions.Hosts))
        continue;

      var oldValue = prop.GetValue(previousOptions);
      var newValue = prop.GetValue(currentOptions);

      if (!AreOptionValuesEqual(oldValue, newValue))
        changes[prop.Name] = (oldValue, newValue);
    }

    return changes;
  }

  private static bool AreOptionValuesEqual(object? left, object? right)
  {
    if (ReferenceEquals(left, right))
      return true;

    if (left is null || right is null)
      return false;

    if (left is IDictionary leftDict && right is IDictionary rightDict)
      return AreDictionariesEqual(leftDict, rightDict);

    if (left is string || right is string)
      return Equals(left, right);

    if (left is IEnumerable leftEnumerable && right is IEnumerable rightEnumerable)
      return AreEnumerablesEqual(leftEnumerable, rightEnumerable);

    return Equals(left, right);
  }

  private static bool AreDictionariesEqual(IDictionary left, IDictionary right)
  {
    if (left.Count != right.Count)
      return false;

    foreach (DictionaryEntry entry in left)
    {
      if (!right.Contains(entry.Key))
        return false;

      if (!AreOptionValuesEqual(entry.Value, right[entry.Key]))
        return false;
    }

    return true;
  }

  private static bool AreEnumerablesEqual(IEnumerable left, IEnumerable right)
  {
    var leftItems = new List<object?>();
    var rightItems = new List<object?>();

    foreach (var item in left)
      leftItems.Add(item);

    foreach (var item in right)
      rightItems.Add(item);

    if (leftItems.Count != rightItems.Count)
      return false;

    for (int i = 0; i < leftItems.Count; i++)
    {
      if (!AreOptionValuesEqual(leftItems[i], rightItems[i]))
        return false;
    }

    return true;
  }
}
