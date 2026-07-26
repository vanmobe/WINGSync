using System.Globalization;
using System.IO;

namespace WingSync.UiTests;

internal sealed class TestSuite
{
    private readonly List<TestCase> tests = [];

    public void Add(string name, Action test)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(test);
        tests.Add(new TestCase(name, test));
    }

    public int Run(string? filter)
    {
        var selected = string.IsNullOrWhiteSpace(filter)
            ? tests
            : tests.Where(test =>
                    test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        if (selected.Count == 0)
        {
            Console.Error.WriteLine($"No UI tests matched filter '{filter}'.");
            return 2;
        }

        var failures = new List<(string Name, Exception Error)>();
        var startedAt = DateTimeOffset.UtcNow;
        foreach (var test in selected)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                test.Body();
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PASS {test.Name} ({stopwatch.Elapsed.TotalSeconds:F1}s)"));
            }
#pragma warning disable CA1031 // A test runner must isolate and report every failed scenario.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failures.Add((test.Name, exception));
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"FAIL {test.Name} ({stopwatch.Elapsed.TotalSeconds:F1}s): "
                    + $"{exception.GetType().Name}: {exception.Message}"));
            }
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"UI result: {selected.Count - failures.Count}/{selected.Count} passed "
            + $"in {elapsed.TotalSeconds:F1}s."));

        if (failures.Count == 0)
        {
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Failure details:");
        foreach (var failure in failures)
        {
            Console.WriteLine($"--- {failure.Name} ---");
            Console.WriteLine(failure.Error);
        }

        return 1;
    }

    private sealed record TestCase(string Name, Action Body);
}

internal static class AssertEx
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new UiTestAssertionException(message ?? "Expected true, but was false.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, but was true.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new UiTestAssertionException(
                message ??
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected <{Format(expected)}>, but was <{Format(actual)}>."));
        }
    }

    public static void Contains(
        string expectedSubstring,
        string actual,
        StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(expectedSubstring);
        ArgumentNullException.ThrowIfNull(actual);
        if (!actual.Contains(expectedSubstring, comparison))
        {
            throw new UiTestAssertionException(
                $"Expected text to contain <{expectedSubstring}>, but was <{actual}>.");
        }
    }

    public static void DoesNotContain(
        string unexpectedSubstring,
        string actual,
        StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(unexpectedSubstring);
        ArgumentNullException.ThrowIfNull(actual);
        if (actual.Contains(unexpectedSubstring, comparison))
        {
            throw new UiTestAssertionException(
                $"Expected text not to contain <{unexpectedSubstring}>, but was <{actual}>.");
        }
    }

    public static void FileExists(string path)
    {
        if (!File.Exists(path))
        {
            throw new UiTestAssertionException($"Expected file to exist: {path}");
        }
    }

    private static string Format<T>(T value) =>
        value is null
            ? "null"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}

internal sealed class UiTestAssertionException : Exception
{
    public UiTestAssertionException(string message)
        : base(message)
    {
    }
}
