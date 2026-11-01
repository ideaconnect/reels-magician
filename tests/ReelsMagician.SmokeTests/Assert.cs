namespace ReelsMagician.SmokeTests;

/// <summary>Minimal assertion helper — the smoke tests are a console app, not a test framework.</summary>
public static class Check
{
    private static int _passed;
    private static readonly List<string> Failures = [];
    private static string _section = "";

    public static void Section(string name)
    {
        _section = name;
        Console.WriteLine();
        Console.WriteLine($"── {name} ".PadRight(72, '─'));
    }

    public static void True(bool condition, string description)
    {
        if (condition)
        {
            _passed++;
            Console.WriteLine($"  PASS  {description}");
        }
        else
        {
            Failures.Add($"{_section}: {description}");
            Console.WriteLine($"  FAIL  {description}");
        }
    }

    public static void Equal<T>(T expected, T actual, string description) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"{description} (expected {expected}, got {actual})");

    public static void Near(double expected, double actual, double tolerance, string description) =>
        True(Math.Abs(expected - actual) <= tolerance,
            $"{description} (expected {expected:0.###} ± {tolerance:0.###}, got {actual:0.###})");

    public static void Contains(string haystack, string needle, string description) =>
        True(haystack.Contains(needle, StringComparison.Ordinal), description);

    public static int Report()
    {
        Console.WriteLine();
        Console.WriteLine(new string('═', 72));
        if (Failures.Count == 0)
        {
            Console.WriteLine($"All {_passed} checks passed.");
            return 0;
        }
        Console.WriteLine($"{_passed} passed, {Failures.Count} FAILED:");
        foreach (var f in Failures) Console.WriteLine($"  · {f}");
        return 1;
    }
}
