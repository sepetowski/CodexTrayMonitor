namespace CodexTrayMonitor.Core;

public sealed record TokenUsage(
    long? Input,
    long? CachedInput,
    long? Output,
    long? ReasoningOutput,
    long? Total);
