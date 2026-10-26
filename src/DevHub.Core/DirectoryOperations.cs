namespace DevHub.Core;

internal static class DirectoryOperations
{
    // Antivirus/indexing can briefly hold newly extracted executable directories on Windows.
    // Keep the final rename atomic; a persistent lock or denied permission still fails normally.
    internal static async Task MoveAsync(string source, string destination, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (Exception error) when (attempt < 4 && error is IOException or UnauthorizedAccessException && !Directory.Exists(destination))
            { await Task.Delay(150 * (attempt + 1), ct); }
        }
    }
}
