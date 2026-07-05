namespace FixtureCore;

/// <summary>Common shape abstraction.</summary>
public interface IShape
{
    /// <summary>Computes the area of the shape.</summary>
    /// <returns>The area in square units.</returns>
    double Area();
}

/// <summary>A shape that also has a display name.</summary>
public interface INamedShape : IShape
{
    string Name { get; }
}

public abstract class ShapeBase : IShape
{
    public abstract double Area();

    public virtual string Describe() => $"shape with area {Area()}";
}

public class Circle : ShapeBase, INamedShape
{
    public double Radius { get; set; }

    public string Name => "circle";

    public override double Area() => Math.PI * Radius * Radius;

    public override string Describe() => $"circle r={Radius}";
}

public class Square : ShapeBase
{
    public double Side;

    public override double Area() => Side * Side;
}

public static class ShapeExtensions
{
    /// <summary>Sums the area of all shapes.</summary>
    public static double TotalArea(this IEnumerable<IShape> shapes) => shapes.Sum(s => s.Area());
}
