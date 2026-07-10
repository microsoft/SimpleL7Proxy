namespace chat_tester.Components.Shared.EventHub.Pipeline;

public readonly record struct PipelineRunResult(int ProcessedCount, int SkippedCount)
{
    public static PipelineRunResult Empty { get; } = new(0, 0);
}
