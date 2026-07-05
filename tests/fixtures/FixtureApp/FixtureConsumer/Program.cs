using FixtureCore;

var shapes = new List<IShape> { new Circle { Radius = 2 }, new Square { Side = 3 } };
Console.WriteLine(shapes.TotalArea());

var processor = new Processor();
Console.WriteLine(processor.Process(42));
Console.WriteLine(processor.Process("x"));
Console.WriteLine(processor.IncrementTwice());
processor.Counter = 10;
Console.WriteLine(processor.Counter);
Console.WriteLine(nameof(Processor.Counter));
Processor.TryParse("5", out var parsed);
Console.WriteLine(parsed);
Console.WriteLine(Helper.CallProcess(processor));

internal static class Helper
{
    public static string CallProcess(Processor p) => p.Process(1);
}
