namespace ElliePdf.Services;

public sealed class PdfPasswordRequiredException : Exception
{
    public PdfPasswordRequiredException(string filePath)
        : base($"A password is required to open '{System.IO.Path.GetFileName(filePath)}'.")
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}

public sealed class PdfIncorrectPasswordException : Exception
{
    public PdfIncorrectPasswordException(string filePath)
        : base($"The password for '{System.IO.Path.GetFileName(filePath)}' is incorrect.")
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}
