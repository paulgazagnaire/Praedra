namespace Rebellion.Core.Data;

/// <summary>Raised when a data file is broken. The message always says what is wrong and where.</summary>
public sealed class DataLoadException : Exception
{
    public DataLoadException(string message) : base(message) { }
}
