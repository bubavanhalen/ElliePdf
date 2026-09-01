namespace ElliePdf.Pdfium.Worker;

public sealed class WorkerStaleIdentityException : InvalidOperationException
{
    public WorkerStaleIdentityException(string message)
        : base(message)
    {
    }
}

public sealed class WorkerDocumentNotFoundException : KeyNotFoundException
{
    public WorkerDocumentNotFoundException(string message)
        : base(message)
    {
    }
}

public sealed class WorkerRestartRequiredException : InvalidOperationException
{
    public WorkerRestartRequiredException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
