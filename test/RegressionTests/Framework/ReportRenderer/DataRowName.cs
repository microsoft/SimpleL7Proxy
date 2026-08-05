using System.Text;

namespace RegressionReportRenderer;

internal sealed record DataRowName(string MethodName, string RawArguments, IReadOnlyList<string> Arguments)
{
    public static DataRowName Parse(string displayName)
    {
        var marker = displayName.IndexOf(" (", StringComparison.Ordinal);
        if (marker <= 0 || !displayName.EndsWith(')'))
        {
            return new DataRowName(displayName, string.Empty, []);
        }

        var raw = displayName[(marker + 2)..^1];
        return new DataRowName(displayName[..marker], raw, ParseCsv(raw));
    }

    private static IReadOnlyList<string> ParseCsv(string value)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '"')
            {
                if (quoted && index + 1 < value.Length && value[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (character == ',' && !quoted)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }
        values.Add(current.ToString().Trim());
        return values;
    }
}
