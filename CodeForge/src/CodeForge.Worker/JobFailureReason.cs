namespace CodeForge.Worker;

internal static class JobFailureReason
{
    private const int MaximumLength = 4000;

    public static string From(Exception exception)
    {
        var reason = exception switch
        {
            OperationCanceledException => "Repository processing timed out before it could finish.",
            RepositoryProcessingException processingException => processingException.Message,
            _ => "The worker encountered an unexpected processing error. Check the worker logs for internal diagnostics."
        };

        reason = string.IsNullOrWhiteSpace(reason)
            ? "The worker failed without returning an error message."
            : reason.Trim();

        return reason.Length <= MaximumLength
            ? reason
            : $"{reason[..(MaximumLength - 1)]}…";
    }
}
