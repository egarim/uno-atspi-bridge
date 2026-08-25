using System;
using System.Linq;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace UnoDemo;

public sealed partial class MainPage : Page
{
    int _n = 1;   // names for dropped controls

    public MainPage()
    {
        this.InitializeComponent();
        this.Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshTree();
        Say("agent", "Ready. Ask me “what can you see?”, then “enable notifications”.");

        // The AT-SPI bridge is Linux-only (no session bus elsewhere). On Windows/macOS
        // the same tree is exposed natively by Uno; the in-app agent below reads it directly.
        if (OperatingSystem.IsLinux())
            UnoDemo.Atspi.AtspiBridge.TryStart(SurfacePanel);

        if (Environment.GetEnvironmentVariable("UNODEMO_AUTOPLAY") == "1")
            AutoPlay();
    }

    // Self-playing demo for headless screen-recording: drop a control (tree recomputes),
    // then let the in-app agent perceive + act — all visible on screen, no human input.
    private async void AutoPlay()
    {
        await System.Threading.Tasks.Task.Delay(1200);
        AddCheckBox(this, null!);                                   // drop → tree recomputes
        await System.Threading.Tasks.Task.Delay(1800);

        Say("you", "what can you see?");
        await System.Threading.Tasks.Task.Delay(500);
        Say("agent", Agent.Handle("what can you see?", SurfacePanel));
        await System.Threading.Tasks.Task.Delay(2800);

        Say("you", "enable notifications");
        await System.Threading.Tasks.Task.Delay(500);
        Say("agent", Agent.Handle("enable notifications", SurfacePanel));   // checkbox ticks
        RefreshTree();
        await System.Threading.Tasks.Task.Delay(2800);

        Say("you", "press Save");
        await System.Threading.Tasks.Task.Delay(500);
        Say("agent", Agent.Handle("press Save", SurfacePanel));
        await System.Threading.Tasks.Task.Delay(1800);
    }

    // ---- palette: drop controls, tree recomputes ----
    private void AddButton(object s, RoutedEventArgs e)   => DropControl(new Button   { Content = $"Button {_n}" },              $"Button {_n}");
    private void AddTextBox(object s, RoutedEventArgs e)  => DropControl(new TextBox  { PlaceholderText = "…" },                 $"Field {_n}");
    private void AddCheckBox(object s, RoutedEventArgs e) => DropControl(new CheckBox { Content = $"Option {_n}" },              $"Option {_n}");
    private void AddSlider(object s, RoutedEventArgs e)   => DropControl(new Slider   { Minimum = 0, Maximum = 100, Value = 50, Width = 220 }, $"Level {_n}");
    private void AddCombo(object s, RoutedEventArgs e)
    {
        var c = new ComboBox { SelectedIndex = 0 };
        c.Items.Add(new ComboBoxItem { Content = "One" });
        c.Items.Add(new ComboBoxItem { Content = "Two" });
        DropControl(c, $"Choice {_n}");
    }

    private void DropControl(FrameworkElement el, string name)
    {
        AutomationProperties.SetName(el, name);
        SurfacePanel.Children.Add(el);
        _n++;
        RefreshTree();
        Say("system", $"dropped ‹{name}› — tree recomputed");
    }

    // ---- the live accessibility tree ----
    private void RefreshTree()
    {
        var tree = Agent.Interactive(Agent.ReadTree(SurfacePanel)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"[application] 'Accessibility Designer'   {tree.Count} controls");
        foreach (var t in tree)
        {
            var st = new StringBuilder();
            if (t.Enabled) st.Append("enabled ");
            if (t.Focusable) st.Append("focusable ");
            if (t.Element is CheckBox { IsChecked: true }) st.Append("checked ");
            sb.AppendLine($"  [{RoleTag(t.Role)}] '{t.Name}'  ({(int)t.Box.X},{(int)t.Box.Y},{(int)t.Box.Width},{(int)t.Box.Height})");
            sb.AppendLine($"      {st}".TrimEnd());
        }
        TreeText.Text = sb.ToString().TrimEnd();
    }

    private static string RoleTag(string role) => role switch
    {
        "Button" => "push button", "Edit" => "entry", "CheckBox" => "check box",
        "Slider" => "slider", "ComboBox" => "combo box", "RadioButton" => "radio button", _ => role.ToLowerInvariant(),
    };

    // ---- agent chat ----
    private void ChatKeyDown(object s, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; OnSend(s, null!); }
    }

    private void OnSend(object s, RoutedEventArgs e)
    {
        var msg = (ChatInput.Text ?? "").Trim();
        if (msg.Length == 0) return;
        ChatInput.Text = "";
        Say("you", msg);
        string reply = Agent.Handle(msg, SurfacePanel);   // reads the tree, acts through it
        Say("agent", reply);
        RefreshTree();                                     // reflect any state change (e.g. checked)
    }

    private void Say(string who, string text)
    {
        var bubble = new Border
        {
            Padding = new Thickness(9, 6, 9, 6),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = who == "you" ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                who == "you" ? "SystemControlBackgroundAccentBrush" : "SystemControlBackgroundChromeMediumLowBrush"],
            Child = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                FontFamily = who == "system" ? new Microsoft.UI.Xaml.Media.FontFamily("Consolas") : null,
                FontSize = who == "system" ? 11 : 13,
            },
        };
        ChatLog.Children.Add(bubble);
        ChatScroll.UpdateLayout();
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null);
    }
}
