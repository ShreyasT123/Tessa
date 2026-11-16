namespace Tessellation.Core;

public readonly record struct RectI(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsValid => Width > 0 && Height > 0;

    public bool Intersects(RectI other) =>
        Left < other.Right && other.Left < Right &&
        Top < other.Bottom && other.Top < Bottom;
}
