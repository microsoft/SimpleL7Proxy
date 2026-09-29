using System.Collections.Frozen;

namespace SimpleL7Proxy.Llm;

public static class ModelMap
{
    /// <summary>
    /// Gets the field removal and rename maps for a model transition.
    /// </summary>
    public static (FrozenSet<string> FieldsToRemove, FrozenDictionary<string, string> FieldsToRename) Get(
        string sourceModel,
        string destinationModel)
    {
        var sourceFamily = GetFamily(sourceModel);
        var destinationFamily = GetFamily(destinationModel);

        return (sourceFamily, destinationFamily) switch
        {
            (ModelFamily.Classic, ModelFamily.Gpt5) =>
                (FieldRemovalMap.ClassicToReasoning, FieldRenameMap.ClassicToReasoning),
            (ModelFamily.Classic, ModelFamily.Reasoning) =>
                (FieldRemovalMap.ClassicToReasoning, FieldRenameMap.ClassicToReasoning),
            (ModelFamily.Gpt5, ModelFamily.Classic) =>
                (FieldRemovalMap.Gpt5ToClassic, FieldRenameMap.ReasoningToClassic),
            (ModelFamily.Gpt5, ModelFamily.Reasoning) =>
                (FieldRemovalMap.Gpt5ToReasoning, FieldRenameMap.Empty),
            (ModelFamily.Reasoning, ModelFamily.Classic) =>
                (FieldRemovalMap.ReasoningToClassic, FieldRenameMap.ReasoningToClassic),
            _ => (FieldRemovalMap.Empty, FieldRenameMap.Empty)
        };
    }

    private static ModelFamily GetFamily(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return ModelFamily.Unknown;
        }

        ReadOnlySpan<char> modelName = model.AsSpan().Trim();

        if (IsModel(modelName, "gpt-5"))
        {
            return ModelFamily.Gpt5;
        }

        if (IsModel(modelName, "o3") || IsModel(modelName, "o4-mini"))
        {
            return ModelFamily.Reasoning;
        }

        if (IsModel(modelName, "gpt-4")
            || IsModel(modelName, "gpt-4o")
            || IsModel(modelName, "gpt-4.1"))
        {
            return ModelFamily.Classic;
        }

        return ModelFamily.Unknown;
    }

    private static bool IsModel(ReadOnlySpan<char> model, ReadOnlySpan<char> canonicalName)
    {
        return model.Equals(canonicalName, StringComparison.OrdinalIgnoreCase)
            || (model.Length > canonicalName.Length
                && model[canonicalName.Length] == '-'
                && model.StartsWith(canonicalName, StringComparison.OrdinalIgnoreCase));
    }

    private enum ModelFamily
    {
        Unknown,
        Classic,
        Gpt5,
        Reasoning
    }
}