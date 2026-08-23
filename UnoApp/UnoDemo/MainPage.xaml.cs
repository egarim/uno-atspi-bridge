using System;
using System.Text;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;

namespace UnoDemo;

public sealed partial class MainPage : Page
{
    public MainPage()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await System.Threading.Tasks.Task.Delay(1500);
        var sb = new StringBuilder();
        sb.AppendLine("=== UNO PEERS (A) via peer hierarchy from named controls ===");
        // Peers exist per-control if CreatePeerForElement returns non-null.
        foreach (var el in new FrameworkElement[]
                 { OpenFileManagerButton, SaveDocumentButton, SearchBox,
                   NotificationsCheckBox, VolumeSlider, ThemeCombo })
        {
            var p = FrameworkElementAutomationPeer.CreatePeerForElement(el);
            if (p is null) { sb.AppendLine($"  (no peer for {el.Name})"); continue; }
            sb.AppendLine($"  [{Safe(() => p.GetAutomationControlType().ToString())}] " +
                          $"name={Q(Safe(() => p.GetName()))} " +
                          $"box={Box(Safe(() => p.GetBoundingRectangle()))} " +
                          $"focusable={Safe(() => p.IsKeyboardFocusable())}");
        }

        sb.AppendLine("=== UNO PEERS (B) full walk of the visual tree ===");
        int count = WalkVisual(this, 0, sb);
        sb.AppendLine($"=== peers found on visual walk: {count} ===");
        Console.WriteLine(sb.ToString());
    }

    // walk the visual tree; for each FrameworkElement, create its peer and print it
    private static int WalkVisual(DependencyObject node, int depth, StringBuilder sb)
    {
        int found = 0;
        if (node is FrameworkElement fe)
        {
            var p = FrameworkElementAutomationPeer.CreatePeerForElement(fe);
            if (p != null)
            {
                found++;
                sb.AppendLine($"{new string(' ', depth * 2)}[{Safe(() => p.GetAutomationControlType().ToString())}] " +
                              $"name={Q(Safe(() => p.GetName()))} class={fe.GetType().Name} " +
                              $"box={Box(Safe(() => p.GetBoundingRectangle()))}");
            }
        }
        int n = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < n; i++)
            found += WalkVisual(VisualTreeHelper.GetChild(node, i), depth + 1, sb);
        return found;
    }

    private static string Q(string s) => s is null ? "null" : $"'{s}'";
    private static string Box(Windows.Foundation.Rect r) =>
        $"({(int)r.X},{(int)r.Y},{(int)r.Width},{(int)r.Height})";
    private static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
}
