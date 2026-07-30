namespace ArchiveAssist.Core.Models;

public readonly struct PageSize(double width, double height)
{
    public double Width { get; } = width;
    public double Height { get; } = height;
    public PageSize Normalized => new(Math.Min(Width, Height), Math.Max(Width, Height));
    public double Area => Width * Height;

    public override string ToString() => $"{Width:N2} x {Height:N2} in";
}
