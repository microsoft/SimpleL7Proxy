using System.Collections;
using System.Reflection;

namespace RegressionReportRenderer;

internal sealed class MetadataCatalog
{
    private readonly Dictionary<(string ClassName, string MethodName), TestMetadata> _tests;
    public IReadOnlyList<string> Domains { get; }

    private MetadataCatalog(Dictionary<(string, string), TestMetadata> tests)
    {
        _tests = tests;
        Domains = tests.Values
            .Select(metadata => metadata.Feature.Domain)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .ToList();
    }

    public static MetadataCatalog Load(string assemblyPath)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        var metadataInterface = assembly.GetType("SimpleL7Proxy.Test.IRegressionTestMetadata")
            ?? throw new InvalidOperationException($"{assemblyPath} does not define {"SimpleL7Proxy.Test.IRegressionTestMetadata"}.");
        var tests = new Dictionary<(string, string), TestMetadata>();
        var errors = new List<string>();

        foreach (var type in assembly.GetTypes().Where(HasTestClassAttribute))
        {
            if (!metadataInterface.IsAssignableFrom(type))
            {
                errors.Add($"{type.FullName} does not implement {metadataInterface.Name}.");
                continue;
            }

            var instance = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Could not create metadata provider {type.FullName}.");
            var features = ReadFeatures(type, instance);

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                         .Where(IsTestMethod))
            {
                var attribute = method.GetCustomAttributes(false)
                    .SingleOrDefault(item => item.GetType().FullName == "SimpleL7Proxy.Test.RegressionTestCaseAttribute");
                if (attribute == null)
                {
                    errors.Add($"{type.FullName}.{method.Name} is missing RegressionTestCaseAttribute.");
                    continue;
                }

                var featureKey = ReadStringProperty(attribute, "Feature");
                if (!features.TryGetValue(featureKey, out var feature))
                {
                    errors.Add($"{type.FullName}.{method.Name} references unknown feature '{featureKey}'.");
                    continue;
                }

                var title = ReadStringProperty(attribute, "Title");
                var description = ReadStringProperty(attribute, "Description");
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
                {
                    errors.Add($"{type.FullName}.{method.Name} must define a title and description.");
                    continue;
                }

                tests[(type.FullName!, method.Name)] = new TestMetadata(feature, title, description);
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Regression metadata validation failed:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => "- " + error)));
        }

        return new MetadataCatalog(tests);
    }

    public void Apply(TestRecord test)
    {
        var methodName = DataRowName.Parse(test.Name).MethodName;
        if (!_tests.TryGetValue((test.ClassName, methodName), out var metadata))
        {
            throw new InvalidOperationException($"No regression metadata found for {test.ClassName}.{methodName}.");
        }

        var parsedName = DataRowName.Parse(test.Name);
        test.MethodName = methodName;
        test.Domain = metadata.Feature.Domain;
        test.Feature = metadata.Feature.Name;
        test.Why = metadata.Feature.WhyItMatters;
        test.Title = ExpandTemplate(metadata.Title, parsedName.Arguments);
        test.Description = ExpandTemplate(metadata.Description, parsedName.Arguments);
        if (!string.IsNullOrWhiteSpace(parsedName.RawArguments) &&
            !test.Description.Contains("Inputs:", StringComparison.Ordinal))
        {
            test.Description += $" Inputs: {parsedName.RawArguments}.";
        }
    }

    private static Dictionary<string, FeatureMetadata> ReadFeatures(Type type, object instance)
    {
        var property = type.GetProperty("RegressionFeatures", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{type.FullName} does not expose RegressionFeatures.");
        var value = property.GetValue(instance) as IEnumerable
            ?? throw new InvalidOperationException($"{type.FullName}.RegressionFeatures is not enumerable.");
        var features = new Dictionary<string, FeatureMetadata>(StringComparer.Ordinal);
        foreach (var item in value)
        {
            if (item == null) continue;
            var itemType = item.GetType();
            var key = itemType.GetProperty("Key")?.GetValue(item)?.ToString() ?? string.Empty;
            var feature = itemType.GetProperty("Value")?.GetValue(item)
                ?? throw new InvalidOperationException($"{type.FullName} contains a null feature.");
            features[key] = new FeatureMetadata(
                ReadStringProperty(feature, "Domain"),
                ReadStringProperty(feature, "Name"),
                ReadStringProperty(feature, "WhyItMatters"));
        }
        return features;
    }

    private static bool HasTestClassAttribute(Type type)
        => type.GetCustomAttributes(false).Any(attribute =>
            attribute.GetType().FullName == "Microsoft.VisualStudio.TestTools.UnitTesting.TestClassAttribute");

    private static bool IsTestMethod(MethodInfo method)
        => method.GetCustomAttributes(false).Any(attribute =>
            attribute.GetType().FullName is
                "Microsoft.VisualStudio.TestTools.UnitTesting.TestMethodAttribute" or
                "Microsoft.VisualStudio.TestTools.UnitTesting.DataTestMethodAttribute");

    private static string ReadStringProperty(object value, string property)
        => value.GetType().GetProperty(property)?.GetValue(value)?.ToString() ?? string.Empty;

    private static string ExpandTemplate(string template, IReadOnlyList<string> arguments)
    {
        var result = template;
        for (var index = 0; index < arguments.Count; index++)
        {
            result = result.Replace($"{{{index}}}", arguments[index], StringComparison.Ordinal);
        }
        return result;
    }

}
