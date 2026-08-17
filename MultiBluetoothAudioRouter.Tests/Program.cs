namespace MultiBluetoothAudioRouter.Tests;

public static class Program
{
    public static int Main()
    {
        var cases = RoutingCompatibilityClassifierTests.Cases
            .Concat(AudioErrorClassifierTests.Cases)
            .ToArray();
        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            try
            {
                testCase.Run();
                Console.WriteLine($"PASS {testCase.Name}");
            }
            catch (Exception exception)
            {
                failures.Add(testCase.Name);
                Console.Error.WriteLine(
                    $"FAIL {testCase.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{cases.Length - failures.Count}/{cases.Length} tests passed.");
        return failures.Count == 0 ? 0 : 1;
    }
}

public sealed record TestCase(string Name, Action Run);

public static class TestAssert
{
    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected '{expected}', but received '{actual}'.");
        }
    }
}
