using Avalonia;

namespace AvaloniaDemo;

public static class Program
{
    public static void Main(string[] args) => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .StartWithClassicDesktopLifetime(args);
}
