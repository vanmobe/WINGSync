using System.Globalization;

namespace WingSync.Core.Tests;

internal sealed class TestSuite
{
    private readonly List<TestCase> tests = [];

    public void Add(string name, Action test)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(test);
        tests.Add(new TestCase(name, test));
    }

    public int Run()
    {
        var failed = new List<(string Name, Exception Error)>();
        var startedAt = DateTimeOffset.UtcNow;

        foreach (var test in tests)
        {
            try
            {
                test.Body();
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"PASS {test.Name}"));
            }
#pragma warning disable CA1031 // A test runner must isolate and report every failed test.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                failed.Add((test.Name, exception));
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"FAIL {test.Name}: {exception.GetType().Name}: {exception.Message}"));
            }
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Result: {tests.Count - failed.Count}/{tests.Count} passed in {elapsed.TotalMilliseconds:F0} ms."));

        if (failed.Count == 0)
        {
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("Failure details:");
        foreach (var failure in failed)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"--- {failure.Name} ---"));
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
            throw new TestAssertionException(message ?? "Expected true, but was false.");
        }
    }

    public static void False(bool condition, string? message = null) =>
        True(!condition, message ?? "Expected false, but was true.");

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new TestAssertionException(
                message
                ?? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected <{Format(expected)}>, but was <{Format(actual)}>."));
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new TestAssertionException(
                message
                ?? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Did not expect <{Format(actual)}>."));
        }
    }

    public static void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string? message = null)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var expectedArray = expected.ToArray();
        var actualArray = actual.ToArray();
        if (!expectedArray.SequenceEqual(actualArray))
        {
            throw new TestAssertionException(
                message
                ?? string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected [{string.Join(", ", expectedArray.Select(Format))}], "
                    + $"but was [{string.Join(", ", actualArray.Select(Format))}]."));
        }
    }

    public static TException Throws<TException>(Action action)
        where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new TestAssertionException(
            string.Create(
                CultureInfo.InvariantCulture,
                $"Expected {typeof(TException).Name}, but no exception was thrown."));
    }

    public static void Contains<T>(IEnumerable<T> values, T expected)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!values.Contains(expected))
        {
            throw new TestAssertionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected collection to contain <{Format(expected)}>."));
        }
    }

    public static void DoesNotContain<T>(IEnumerable<T> values, T unexpected)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Contains(unexpected))
        {
            throw new TestAssertionException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Expected collection not to contain <{Format(unexpected)}>."));
        }
    }

    private static string Format<T>(T value) =>
        value is null
            ? "null"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
}

internal sealed class TestAssertionException : Exception
{
    public TestAssertionException(string message)
        : base(message)
    {
    }
}
