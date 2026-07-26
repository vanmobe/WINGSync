using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WingSync.Infrastructure.Diagnostics;

/// <summary>
/// Describes the local inputs and safe diagnostic summary used to create a support bundle.
/// </summary>
/// <param name="DestinationPath">The final ZIP path.</param>
/// <param name="ConfigurationPath">The schema-versioned configuration file.</param>
/// <param name="LogDirectory">The directory containing rotating JSON-lines logs.</param>
/// <param name="ProductVersion">The product and build version shown in diagnostics.</param>
/// <param name="CoordinatorState">The synchronization state name.</param>
/// <param name="CoordinatorDetail">The operator-facing state detail.</param>
/// <param name="DroppedLogEntries">The number of diagnostics that could not be persisted.</param>
public sealed record SupportBundleExportRequest(
    string DestinationPath,
    string ConfigurationPath,
    string LogDirectory,
    string ProductVersion,
    string CoordinatorState,
    string CoordinatorDetail,
    long DroppedLogEntries);

/// <summary>
/// Creates privacy-preserving support ZIPs without copying local source files verbatim.
/// </summary>
public static partial class SupportBundleExporter
{
    private const string RedactedIp = "[REDACTED:IP]";
    private const string RedactedSerial = "[REDACTED:SERIAL]";
    private const string RedactedPath = "[REDACTED:PATH]";
    private const string RedactedParameterValue = "[REDACTED:PARAMETER_VALUE]";
    private const string OmittedRawMessage =
        "Ruwe helper- of parameterinhoud is verwijderd uit het supportpakket.";

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
    };

    private static readonly HashSet<string> IpPropertyNames = new(
        [
            "address",
            "hostAddress",
            "ip",
            "ipAddress",
            "localAddress",
            "remoteAddress",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SerialPropertyNames = new(
        [
            "actualSerial",
            "consoleSerial",
            "expectedSerial",
            "serial",
            "serialNumber",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ParameterValuePropertyNames = new(
        [
            "actualValue",
            "encodedValue",
            "expectedValue",
            "newValue",
            "oldValue",
            "parameterValue",
            "readbackValue",
            "sourceValue",
            "targetValue",
            "value",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates one ZIP atomically. IP addresses, console serial numbers, local user paths,
    /// and WING parameter values are redacted while event codes, timestamps, scopes, and
    /// token paths remain available for diagnosis.
    /// </summary>
    /// <param name="request">Bundle paths and the diagnostic summary.</param>
    /// <param name="cancellationToken">Cancels reading and ZIP creation.</param>
    /// <returns>A task that completes after the final ZIP has been moved into place.</returns>
    public static async Task ExportAsync(
        SupportBundleExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);

        var destinationPath = Path.GetFullPath(request.DestinationPath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(destinationDirectory);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"The support bundle already exists: {destinationPath}");
        }

        var temporaryPath = string.Concat(
            destinationPath,
            ".tmp-",
            Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = await ReadJsonOrPlaceholderAsync(
                    request.ConfigurationPath,
                    cancellationToken)
                .ConfigureAwait(false);
            var sensitiveLiterals = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            CollectSensitiveLiterals(configuration, null, sensitiveLiterals);
            var safeConfiguration = SanitizeNode(
                configuration,
                null,
                sensitiveLiterals);

            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 65_536,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using var archive = new ZipArchive(
                    output,
                    ZipArchiveMode.Create,
                    leaveOpen: true,
                    Encoding.UTF8);
                await WriteJsonEntryAsync(
                        archive,
                        "config.json",
                        safeConfiguration,
                        IndentedJson,
                        cancellationToken)
                    .ConfigureAwait(false);
                await AddRedactedLogsAsync(
                        archive,
                        request.LogDirectory,
                        sensitiveLiterals,
                        cancellationToken)
                    .ConfigureAwait(false);
                await WriteDiagnosticsAsync(
                        archive,
                        request,
                        sensitiveLiterals,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, destinationPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void ValidateRequest(SupportBundleExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ConfigurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.LogDirectory);
        ArgumentNullException.ThrowIfNull(request.ProductVersion);
        ArgumentNullException.ThrowIfNull(request.CoordinatorState);
        ArgumentNullException.ThrowIfNull(request.CoordinatorDetail);
        if (request.DroppedLogEntries < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.DroppedLogEntries,
                "Dropped log entries cannot be negative.");
        }
    }

    private static async Task<JsonNode?> ReadJsonOrPlaceholderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new JsonObject
            {
                ["supportBundleState"] = "configuration file not found",
            };
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonNode.ParseAsync(
                    stream,
                    documentOptions: default,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return new JsonObject
            {
                ["supportBundleState"] =
                    "configuration could not be parsed; original content omitted",
            };
        }
    }

    private static async Task AddRedactedLogsAsync(
        ZipArchive archive,
        string logDirectory,
        Dictionary<string, string> sensitiveLiterals,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(logDirectory, "*.jsonl*")
                     .OrderBy(static item => item, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(
                $"logs/{Path.GetFileName(path)}",
                CompressionLevel.Optimal);
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(
                entryStream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16_384,
                leaveOpen: false);
            await using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(
                input,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 16_384,
                leaveOpen: false);

            var lineNumber = 0;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                   is { } line)
            {
                lineNumber++;
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(line);
                }
                catch (JsonException)
                {
                    node = new JsonObject
                    {
                        ["eventName"] = "SUPPORT_LOG_LINE_OMITTED",
                        ["lineNumber"] = lineNumber,
                        ["message"] =
                            "Ongeldige JSON-logregel is niet in het supportpakket opgenomen.",
                    };
                }

                CollectSensitiveLiterals(node, null, sensitiveLiterals);
                var safeNode = SanitizeNode(node, null, sensitiveLiterals);
                ReplaceRawDiagnosticMessage(safeNode);
                await writer.WriteLineAsync(
                        safeNode?.ToJsonString(CompactJson) ?? "null")
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task WriteDiagnosticsAsync(
        ZipArchive archive,
        SupportBundleExportRequest request,
        IReadOnlyDictionary<string, string> sensitiveLiterals,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry("diagnostics.txt", CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 4_096,
            leaveOpen: false);
        await writer.WriteLineAsync(RedactText(request.ProductVersion, sensitiveLiterals))
            .ConfigureAwait(false);
        await writer.WriteLineAsync(
                $"Status: {RedactText(request.CoordinatorState, sensitiveLiterals)} - " +
                RedactText(request.CoordinatorDetail, sensitiveLiterals))
            .ConfigureAwait(false);
        await writer.WriteLineAsync("Data directory: [OMITTED]")
            .ConfigureAwait(false);
        await writer.WriteLineAsync($"Dropped log entries: {request.DroppedLogEntries}")
            .ConfigureAwait(false);
        await writer.WriteLineAsync(
                "Privacy: IP-adressen, serienummers, bestandslocaties en WING-parameterwaarden zijn geredigeerd.")
            .ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonEntryAsync(
        ZipArchive archive,
        string entryName,
        JsonNode? node,
        JsonSerializerOptions serializerOptions,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(
                stream,
                node,
                serializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void CollectSensitiveLiterals(
        JsonNode? node,
        string? propertyName,
        IDictionary<string, string> sensitiveLiterals)
    {
        switch (node)
        {
            case JsonObject jsonObject:
                foreach (var property in jsonObject)
                {
                    CollectSensitiveLiterals(
                        property.Value,
                        property.Key,
                        sensitiveLiterals);
                }

                break;

            case JsonArray jsonArray:
                foreach (var item in jsonArray)
                {
                    CollectSensitiveLiterals(item, propertyName, sensitiveLiterals);
                }

                break;

            case JsonValue jsonValue
                when IsIdentityProperty(propertyName) &&
                     jsonValue.TryGetValue<string>(out var identity) &&
                     !string.IsNullOrWhiteSpace(identity):
                sensitiveLiterals[identity] =
                    IpPropertyNames.Contains(propertyName ?? string.Empty)
                        ? RedactedIp
                        : RedactedSerial;
                break;

            case JsonValue jsonValue
                when IsParameterValueProperty(propertyName) &&
                     jsonValue.TryGetValue<string>(out var parameterValue) &&
                     parameterValue.Length >= 4:
                sensitiveLiterals.TryAdd(parameterValue, RedactedParameterValue);
                break;
        }
    }

    private static JsonNode? SanitizeNode(
        JsonNode? node,
        string? propertyName,
        IReadOnlyDictionary<string, string> sensitiveLiterals)
    {
        if (IpPropertyNames.Contains(propertyName ?? string.Empty))
        {
            return JsonValue.Create(RedactedIp);
        }

        if (SerialPropertyNames.Contains(propertyName ?? string.Empty))
        {
            return JsonValue.Create(RedactedSerial);
        }

        if (ParameterValuePropertyNames.Contains(propertyName ?? string.Empty))
        {
            return JsonValue.Create(RedactedParameterValue);
        }

        switch (node)
        {
            case JsonObject jsonObject:
            {
                var safeObject = new JsonObject();
                foreach (var property in jsonObject)
                {
                    safeObject[property.Key] = SanitizeNode(
                        property.Value,
                        property.Key,
                        sensitiveLiterals);
                }

                return safeObject;
            }

            case JsonArray jsonArray:
            {
                var safeArray = new JsonArray();
                foreach (var item in jsonArray)
                {
                    safeArray.Add(SanitizeNode(item, propertyName, sensitiveLiterals));
                }

                return safeArray;
            }

            case JsonValue jsonValue
                when jsonValue.TryGetValue<string>(out var text):
                return JsonValue.Create(RedactText(text, sensitiveLiterals));

            default:
                return node?.DeepClone();
        }
    }

    private static bool IsIdentityProperty(string? propertyName) =>
        IpPropertyNames.Contains(propertyName ?? string.Empty) ||
        SerialPropertyNames.Contains(propertyName ?? string.Empty);

    private static bool IsParameterValueProperty(string? propertyName) =>
        ParameterValuePropertyNames.Contains(propertyName ?? string.Empty);

    private static void ReplaceRawDiagnosticMessage(JsonNode? node)
    {
        if (node is not JsonObject jsonObject ||
            jsonObject["eventName"] is not JsonValue eventValue ||
            !eventValue.TryGetValue<string>(out var eventName) ||
            !ShouldOmitRawMessage(eventName) ||
            jsonObject.ContainsKey("message") == false)
        {
            return;
        }

        jsonObject["message"] = OmittedRawMessage;
    }

    private static bool ShouldOmitRawMessage(string eventName) =>
        eventName.StartsWith("PARAMETER_", StringComparison.OrdinalIgnoreCase) ||
        eventName.StartsWith("PLAN_", StringComparison.OrdinalIgnoreCase) ||
        eventName.EndsWith("_FAILED", StringComparison.OrdinalIgnoreCase) ||
        eventName.Contains("READBACK", StringComparison.OrdinalIgnoreCase) ||
        eventName.Contains("VERIFICATION", StringComparison.OrdinalIgnoreCase) ||
        eventName.Equals("WAPI_HELPER_STDERR", StringComparison.OrdinalIgnoreCase);

    private static string RedactText(
        string value,
        IReadOnlyDictionary<string, string> sensitiveLiterals)
    {
        var redacted = value;
        foreach (var sensitiveValue in sensitiveLiterals
                     .Where(static item => item.Key.Length > 0)
                     .OrderByDescending(static item => item.Key.Length))
        {
            redacted = redacted.Replace(
                sensitiveValue.Key,
                sensitiveValue.Value,
                StringComparison.OrdinalIgnoreCase);
        }

        redacted = Ipv4AddressRegex().Replace(redacted, RedactedIp);
        redacted = Ipv6CandidateRegex().Replace(
            redacted,
            static match =>
                IPAddress.TryParse(match.Value, out _)
                    ? RedactedIp
                    : match.Value);
        redacted = SerialContextRegex().Replace(
            redacted,
            static match => string.Concat(match.Groups[1].Value, RedactedSerial));
        redacted = WindowsAbsolutePathRegex().Replace(redacted, RedactedPath);
        redacted = WindowsUserProfileRegex().Replace(redacted, "%USERPROFILE%");
        return UnixHomeRegex().Replace(redacted, "/home/[USER]");
    }

    [GeneratedRegex(
        @"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])",
        RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4AddressRegex();

    [GeneratedRegex(
        @"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![0-9A-Fa-f:])",
        RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6CandidateRegex();

    [GeneratedRegex(
        @"(?i)(\b(?:(?:serial|serie)(?:nummer)?|s/n)\b(?:\s*(?:is|=|:))?\s*['""]?)[A-Z0-9._-]{4,64}['""]?",
        RegexOptions.CultureInvariant)]
    private static partial Regex SerialContextRegex();

    [GeneratedRegex(
        @"(?ix)(?:(?:(?<![A-Z0-9])[A-Z]:[\\/]|(?<![\\/])\\\\)[^\r\n""'<>|:*?]*?\.[A-Z0-9]{1,16}(?=$|[\s,;:)\]}""'])|(?:(?<![A-Z0-9])[A-Z]:[\\/]|(?<![\\/])\\\\)[^\r\n""']*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsAbsolutePathRegex();

    [GeneratedRegex(
        @"(?i)\b[A-Z]:\\Users\\[^\\/\r\n""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsUserProfileRegex();

    [GeneratedRegex(
        @"/home/[^/\r\n""']+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UnixHomeRegex();
}
