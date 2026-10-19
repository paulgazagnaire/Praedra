namespace Praedra.Importer;

/// <summary>The importer stops on this: the message names the file and the reason. rule: B1-own-importer §Work 1 ("fails with the file name and the reason").</summary>
public sealed class ImportException : Exception
{
    public ImportException(string message) : base(message) { }
}
