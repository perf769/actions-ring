using ActionsRing.Platform.Windows.Input;

namespace ActionsRing.Platform.Windows.Display;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public ScreenPoint Center => new(checked(X + (Width / 2)), checked(Y + (Height / 2)));
}

public sealed record MonitorSnapshot(
    nint Handle,
    string DeviceName,
    ScreenRect Bounds,
    ScreenRect WorkArea,
    bool IsPrimary,
    uint DpiX,
    uint DpiY)
{
    public double ScaleX => DpiX / 96d;
    public double ScaleY => DpiY / 96d;
}
