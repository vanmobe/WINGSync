namespace WingSync.Infrastructure.Persistence;

internal static class AtomicJsonFile
{
    public static async Task WriteAsync(
        string targetPath,
        string backupPath,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath)
            ?? throw new ArgumentException(
                "The target path must include a directory.",
                nameof(targetPath));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16_384,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(targetPath))
            {
                File.Replace(
                    temporaryPath,
                    targetPath,
                    backupPath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public static async Task<byte[]> ReadBoundedAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"The JSON file is {stream.Length} bytes; the limit is {maximumBytes} bytes.");
        }

        using var content = new MemoryStream(
            capacity: (int)Math.Min(stream.Length, maximumBytes));
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (content.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    $"The JSON file grew beyond its {maximumBytes}-byte limit while being read.");
            }

            content.Write(buffer, 0, read);
        }

        return content.ToArray();
    }

    public static string Quarantine(
        string path,
        DateTimeOffset timestamp,
        string category)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException(
                "The quarantined path must include a directory.",
                nameof(path));
        var baseName = Path.GetFileName(path);
        var stamp = timestamp.UtcDateTime.ToString(
            "yyyyMMddTHHmmssfffffffZ",
            System.Globalization.CultureInfo.InvariantCulture);
        var quarantinePath = Path.Combine(
            directory,
            $"{baseName}.{category}-{stamp}-{Guid.NewGuid():N}.json");
        File.Move(path, quarantinePath);
        return quarantinePath;
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A failed best-effort cleanup must not mask the original persistence result.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed best-effort cleanup must not mask the original persistence result.
        }
    }
}
