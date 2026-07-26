using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using WingSync.Infrastructure.Diagnostics;

namespace WingSync.Integration.Tests;

internal static class SupportBundleTests
{
    public static async Task SensitiveConfigurationAndLogDataIsRedactedAsync()
    {
        using var temporary = new SupportTemporaryDirectory();
        const string fohIp = "203.0.113.10";
        const string stageIp = "203.0.113.11";
        const string fohSerial = "PIN-A";
        const string stageSerial = "DEMO-STAGE-PRIVATE-0002";
        const string observedUnknownSerial = "IMPOSTOR-B";
        const string parameterValue = "VOCAL_SECRET_VALUE_3DB";
        const string privateDrivePath =
            @"D:\Clients\Secret Show\WingSync\cache\state.json";
        const string privateUncPath =
            @"\\FOH-SERVER\Client X\Secret Show\config.json";
        const string privateProfilePath =
            @"C:\Users\Example Operator\Documents\Secret Show\cache.json";
        var configurationPath = Path.Combine(temporary.Path, "config.json");
        var logDirectory = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logDirectory);

        var configuration = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["savedAtUtc"] = "2026-07-26T10:00:00+00:00",
            ["configuration"] = new JsonObject
            {
                ["foh"] = new JsonObject
                {
                    ["ipAddress"] = fohIp,
                    ["port"] = 2222,
                    ["expectedSerial"] = fohSerial,
                },
                ["monitor"] = new JsonObject
                {
                    ["ipAddress"] = stageIp,
                    ["port"] = 2222,
                    ["expectedSerial"] = stageSerial,
                },
                ["direction"] = "FohToMonitor",
                ["scopes"] = new JsonArray("Eq", "Gate"),
                ["channels"] = new JsonObject
                {
                    ["inputChannels"] = new JsonArray(
                        new JsonObject
                        {
                            ["sourceChannel"] = 1,
                            ["targetChannel"] = 12,
                        }),
                },
            },
        };
        await File.WriteAllTextAsync(
                configurationPath,
                configuration.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
            .ConfigureAwait(false);

        var parameterLog = new JsonObject
        {
            ["timestamp"] = "2026-07-26T10:00:01+00:00",
            ["level"] = "Information",
            ["eventName"] = "PARAMETER_WRITE",
            ["message"] = $"Eq: /ch/1/eq/1 -> /ch/12/eq/1 ({parameterValue})",
            ["properties"] = new JsonObject
            {
                ["action"] = "write",
                ["sourceToken"] = "/ch/1/eq/1",
                ["targetToken"] = "/ch/12/eq/1",
                ["scope"] = "Eq",
                ["type"] = "S",
                ["value"] = parameterValue,
                ["dryRun"] = "False",
            },
        };
        var identityLog = new JsonObject
        {
            ["timestamp"] = "2026-07-26T10:00:02+00:00",
            ["level"] = "Error",
            ["eventName"] = "READBACK_MISMATCH",
            ["message"] =
                $"Console serial '{fohSerial}' op {fohIp}: verwacht {parameterValue}.",
            ["properties"] = new JsonObject
            {
                ["actualSerial"] = fohSerial,
                ["remoteAddress"] = fohIp,
                ["expectedValue"] = parameterValue,
                ["targetToken"] = "/ch/12/eq/1",
            },
        };
        var profileLog = new JsonObject
        {
            ["timestamp"] = "2026-07-26T10:00:03+00:00",
            ["level"] = "Error",
            ["eventName"] = "CACHE_LOCATION_NOTE",
            ["message"] = $"Kon {privateProfilePath} niet openen.",
        };
        var ordinaryPathLog = new JsonObject
        {
            ["timestamp"] = "2026-07-26T10:00:04+00:00",
            ["level"] = "Warning",
            ["eventName"] = "CACHE_RECOVERY_DEFERRED",
            ["message"] =
                $"Cachebestand {privateDrivePath} kon niet worden gelezen; retry blijft beschikbaar.",
        };
        var identityMismatchLog = new JsonObject
        {
            ["timestamp"] = "2026-07-26T10:00:05+00:00",
            ["level"] = "Critical",
            ["eventName"] = "CONNECTED_IDENTITY_MISMATCH",
            ["message"] =
                $"Identity pin {fohSerial} stemt niet overeen; verbonden serienummer {observedUnknownSerial}.",
        };
        var secondaryBatchFailureLog = new JsonObject
        {
            ["timestamp"] = "2026-07-26T10:00:06+00:00",
            ["level"] = "Critical",
            ["eventName"] = "SYNC_BATCH_FAILED",
            ["message"] = "Readback wijkt af: verwacht OFF, ontvangen ON.",
        };
        await File.WriteAllLinesAsync(
                Path.Combine(logDirectory, "wingsync.jsonl"),
                [
                    parameterLog.ToJsonString(),
                    identityLog.ToJsonString(),
                    profileLog.ToJsonString(),
                    ordinaryPathLog.ToJsonString(),
                    identityMismatchLog.ToJsonString(),
                    secondaryBatchFailureLog.ToJsonString(),
                ])
            .ConfigureAwait(false);

        var bundlePath = Path.Combine(temporary.Path, "support.zip");
        await SupportBundleExporter.ExportAsync(
                new SupportBundleExportRequest(
                    bundlePath,
                    configurationPath,
                    logDirectory,
                    "WingSync 1.2.3-test",
                    "RunningLive",
                    $"Herstelbron {privateUncPath} is onbereikbaar; synchronisatie gepauzeerd.",
                    2))
            .ConfigureAwait(false);

        using var archive = ZipFile.OpenRead(bundlePath);
        var configText = await ReadEntryAsync(archive, "config.json").ConfigureAwait(false);
        var logsText = await ReadEntryAsync(archive, "logs/wingsync.jsonl")
            .ConfigureAwait(false);
        var diagnosticsText = await ReadEntryAsync(archive, "diagnostics.txt")
            .ConfigureAwait(false);
        var allExportedText = string.Concat(configText, logsText, diagnosticsText);

        AssertNotContains(allExportedText, fohIp);
        AssertNotContains(allExportedText, stageIp);
        AssertNotContains(allExportedText, fohSerial);
        AssertNotContains(allExportedText, stageSerial);
        AssertNotContains(allExportedText, observedUnknownSerial);
        AssertNotContains(allExportedText, parameterValue);
        AssertNotContains(allExportedText, "Example Operator");
        AssertNotContains(allExportedText, privateDrivePath);
        AssertNotContains(allExportedText, privateUncPath);
        AssertNotContains(allExportedText, privateProfilePath);
        AssertNotContains(allExportedText, "Secret Show");
        AssertNotContains(allExportedText, "FOH-SERVER");
        AssertNotContains(allExportedText, "Client X");
        AssertNotContains(logsText, "verwacht OFF");
        AssertNotContains(logsText, "ontvangen ON");
        AssertEx.True(
            configText.Contains("\"schemaVersion\": 1", StringComparison.Ordinal),
            "The redacted configuration lost its schema structure.");
        AssertEx.True(
            configText.Contains("\"port\": 2222", StringComparison.Ordinal),
            "Non-sensitive endpoint structure was not retained.");
        AssertEx.True(
            configText.Contains("\"sourceChannel\": 1", StringComparison.Ordinal) &&
            configText.Contains("\"targetChannel\": 12", StringComparison.Ordinal),
            "The useful channel mapping structure was not retained.");
        AssertEx.True(
            configText.Contains("[REDACTED:IP]", StringComparison.Ordinal) &&
            configText.Contains("[REDACTED:SERIAL]", StringComparison.Ordinal),
            "The configuration does not identify the redacted fields.");
        AssertEx.True(
            logsText.Contains("\"eventName\":\"PARAMETER_WRITE\"", StringComparison.Ordinal) &&
            logsText.Contains("\"sourceToken\":\"/ch/1/eq/1\"", StringComparison.Ordinal) &&
            logsText.Contains("\"targetToken\":\"/ch/12/eq/1\"", StringComparison.Ordinal) &&
            logsText.Contains("\"scope\":\"Eq\"", StringComparison.Ordinal),
            "Safe event and token-path diagnostics were not preserved.");
        AssertEx.True(
            logsText.Contains("[REDACTED:PARAMETER_VALUE]", StringComparison.Ordinal),
            "The structured WING parameter value was not explicitly redacted.");
        AssertEx.True(
            logsText.Contains(
                "Cachebestand [REDACTED:PATH] kon niet worden gelezen; retry blijft beschikbaar.",
                StringComparison.Ordinal),
            "An ordinary log path was not redacted while preserving its diagnostic context.");
        AssertEx.True(
            logsText.Contains(
                "Kon [REDACTED:PATH] niet openen.",
                StringComparison.Ordinal),
            "A user-profile path containing spaces was not fully redacted.");
        AssertEx.True(
            logsText.Contains(
                "verbonden serienummer [REDACTED:SERIAL]",
                StringComparison.Ordinal),
            "An observed serial in Dutch identity-mismatch text was not redacted.");
        AssertEx.True(
            logsText.Contains(
                "\"eventName\":\"SYNC_BATCH_FAILED\"",
                StringComparison.Ordinal) &&
            logsText.Contains(
                "Ruwe helper- of parameterinhoud is verwijderd uit het supportpakket.",
                StringComparison.Ordinal),
            "A secondary sync failure retained its raw short parameter values.");
        AssertEx.True(
            diagnosticsText.Contains("Dropped log entries: 2", StringComparison.Ordinal) &&
            diagnosticsText.Contains("Data directory: [OMITTED]", StringComparison.Ordinal) &&
            diagnosticsText.Contains(
                "Herstelbron [REDACTED:PATH] is onbereikbaar; synchronisatie gepauzeerd.",
                StringComparison.Ordinal) &&
            diagnosticsText.Contains("bestandslocaties", StringComparison.Ordinal),
            "The safe diagnostic summary is incomplete.");
    }

    public static async Task MalformedSourceContentIsOmittedRatherThanCopiedAsync()
    {
        using var temporary = new SupportTemporaryDirectory();
        const string privateIp = "198.51.100.77";
        const string privateSerial = "PRIVATE-SERIAL-77";
        const string privateValue = "PRIVATE-PARAMETER-VALUE-77";
        var configurationPath = Path.Combine(temporary.Path, "config.json");
        var logDirectory = Path.Combine(temporary.Path, "logs");
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(
                configurationPath,
                $"not-json {privateIp} {privateSerial} {privateValue}")
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                Path.Combine(logDirectory, "wingsync.jsonl"),
                $"not-json {privateIp} {privateSerial} {privateValue}")
            .ConfigureAwait(false);

        var bundlePath = Path.Combine(temporary.Path, "support.zip");
        await SupportBundleExporter.ExportAsync(
                new SupportBundleExportRequest(
                    bundlePath,
                    configurationPath,
                    logDirectory,
                    "WingSync test",
                    "Faulted",
                    "Diagnose beschikbaar.",
                    0))
            .ConfigureAwait(false);

        using var archive = ZipFile.OpenRead(bundlePath);
        var configText = await ReadEntryAsync(archive, "config.json").ConfigureAwait(false);
        var logsText = await ReadEntryAsync(archive, "logs/wingsync.jsonl")
            .ConfigureAwait(false);
        var allExportedText = string.Concat(configText, logsText);
        AssertNotContains(allExportedText, privateIp);
        AssertNotContains(allExportedText, privateSerial);
        AssertNotContains(allExportedText, privateValue);
        AssertEx.True(
            configText.Contains("original content omitted", StringComparison.Ordinal),
            "A malformed configuration did not receive a safe diagnostic placeholder.");
        AssertEx.True(
            logsText.Contains("SUPPORT_LOG_LINE_OMITTED", StringComparison.Ordinal),
            "A malformed log line did not receive a safe diagnostic placeholder.");
    }

    private static async Task<string> ReadEntryAsync(
        ZipArchive archive,
        string entryName)
    {
        var entry = archive.GetEntry(entryName) ??
            throw new InvalidOperationException($"Missing ZIP entry '{entryName}'.");
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static void AssertNotContains(string text, string privateValue)
    {
        AssertEx.False(
            text.Contains(privateValue, StringComparison.OrdinalIgnoreCase),
            $"The support bundle leaked private value '{privateValue}'.");
    }

    private sealed class SupportTemporaryDirectory : IDisposable
    {
        public SupportTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "WingSync-SupportTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
