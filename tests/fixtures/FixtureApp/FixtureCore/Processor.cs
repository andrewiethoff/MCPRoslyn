namespace FixtureCore;

public class Processor
{
    public int Counter;

    private int _unusedField;

    /// <summary>Processes a number.</summary>
    public string Process(int value) => $"int:{value}";

    /// <summary>Processes a string.</summary>
    public string Process(string value) => $"string:{value}";

    /// <summary>Scales a long (exists to exercise fully-qualified System.Int64 addressing).</summary>
    public long Scale(long factor) => factor * 2;

    public int IncrementTwice()
    {
        Counter = 1;
        Counter += 1;
        return Counter;
    }

    private void UnusedHelper()
    {
    }

    public static bool TryParse(string text, out int value) => int.TryParse(text, out value);

    public class Nested
    {
        public int Depth => 2;
    }
}
