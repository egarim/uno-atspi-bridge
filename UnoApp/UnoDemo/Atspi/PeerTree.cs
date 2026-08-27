// The platform-neutral half of the bridge: walk Uno's AutomationPeer/visual tree
// into a Node graph, keep it live (state cached on the node, updated on the UI
// thread), and expose the write-path (invoke / set value / set text / select)
// through the automation providers. No D-Bus here — a transport (AT-SPI today,
// another platform a11y API tomorrow) consumes the tree and subscribes to its
// change events.
using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;

namespace UnoDemo.Atspi;

internal sealed class Node
{
    public string Path = "";                                 // opaque id, assigned by the transport's naming scheme
    public string Name = "";
    public uint Role;
    public string RoleName = "";
    public (int x, int y, int w, int h) Box;                 // screen coordinates
    public bool Enabled, Focusable;
    public bool Checked;                                     // live toggle state
    public bool HasRange; public double Min, Max, Val;       // live slider state → Value iface
    public bool HasText;  public string Text = "";           // live entry text → EditableText iface
    public bool Expandable, Expanded;                        // combo box → Selection iface
    public bool Selectable, Selected; public int ItemIndex;  // combo item
    public Node? Parent;
    public readonly List<Node> Children = new();
    public Microsoft.UI.Xaml.FrameworkElement? Element;      // source, for live events
}

internal static class RoleMap
{
    // ids are the real AtspiRole enum values (libatspi derives the role NAME from
    // the numeric GetRole, not from GetRoleName).
    public static (uint, string) Map(AutomationControlType t) => t switch
    {
        AutomationControlType.Button   => (43u, "push button"),  // PUSH_BUTTON
        AutomationControlType.Edit     => (79u, "entry"),        // ENTRY
        AutomationControlType.CheckBox => (7u,  "check box"),     // CHECK_BOX
        AutomationControlType.Slider   => (51u, "slider"),       // SLIDER
        AutomationControlType.ComboBox => (11u, "combo box"),    // COMBO_BOX
        AutomationControlType.Text     => (29u, "label"),        // LABEL
        AutomationControlType.List     => (31u, "list"),         // LIST
        AutomationControlType.ListItem => (32u, "list item"),    // LIST_ITEM
        _                              => (39u, "panel"),        // PANEL
    };
}

internal sealed class PeerTree
{
    public readonly Node Root;
    public readonly Dictionary<string, Node> ByPath = new();
    readonly string _pathPrefix;
    int _next = 1;

    // change notifications, raised on the UI thread; the transport turns them into signals
    public event Action<Node, string, int>? StateChanged;       // focused / checked / expanded
    public event Action<Node, string, object>? PropertyChanged; // accessible-value (double or string)
    public event Action<Node>? SelectionChanged;

    public PeerTree(Microsoft.UI.Xaml.FrameworkElement uiRoot, Node root,
                    string pathPrefix, (int x, int y) origin)
    {
        Root = root;
        _pathPrefix = pathPrefix;
        Walk(uiRoot, root);
        ApplyScreenCoordinates(origin);
    }

    // WinUI's GetBoundingRectangle is spec'd as screen-relative. If a head returns
    // window-relative rects instead (outermost node sits at ~0 while the window is
    // offset), add the window origin so every box is true screen space.
    void ApplyScreenCoordinates((int x, int y) origin)
    {
        if (origin == (0, 0)) return;                        // nothing to add
        var top = Root.Children.Count > 0 ? Root.Children[0] : null;
        bool windowRelative = top != null && top.Box.x < origin.x - 4;
        if (!windowRelative) return;
        foreach (var n in ByPath.Values)
            n.Box = (n.Box.x + origin.x, n.Box.y + origin.y, n.Box.w, n.Box.h);
    }

    void Walk(DependencyObject node, Node parent)
    {
        Node attach = parent;
        bool descend = true;
        if (node is Microsoft.UI.Xaml.FrameworkElement fe)
        {
            var p = FrameworkElementAutomationPeer.CreatePeerForElement(fe);
            if (p != null)
            {
                var (role, roleName) = RoleMap.Map(Try(() => p.GetAutomationControlType(), AutomationControlType.Custom));
                var r = Try(() => p.GetBoundingRectangle(), default(Windows.Foundation.Rect));
                var n = new Node
                {
                    Path = $"{_pathPrefix}{_next++}",
                    Name = Try(() => p.GetName(), "") ?? "",
                    Role = role, RoleName = roleName,
                    Box = ((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height),
                    Enabled = Try(() => p.IsEnabled(), false),
                    Focusable = Try(() => p.IsKeyboardFocusable(), false),
                    Parent = parent,
                    Element = fe,
                };
                if (fe is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb0)
                    n.Checked = tb0.IsChecked == true;
                if (fe is Microsoft.UI.Xaml.Controls.Primitives.RangeBase rb0)
                { n.HasRange = true; n.Min = rb0.Minimum; n.Max = rb0.Maximum; n.Val = rb0.Value; }
                if (fe is Microsoft.UI.Xaml.Controls.TextBox tx0)
                { n.HasText = true; n.Text = tx0.Text ?? ""; }
                if (fe is Microsoft.UI.Xaml.Controls.ComboBox cb0)
                {
                    // Combo items live in a popup, not the visual tree, so the walk
                    // can't reach them — surface the Items collection as children
                    // (role: list item), each selectable through the Selection iface.
                    n.Expandable = true;
                    for (int ix = 0; ix < cb0.Items.Count; ix++)
                    {
                        var item = cb0.Items[ix];
                        var itemEl = item as Microsoft.UI.Xaml.Controls.ComboBoxItem;
                        var child = new Node
                        {
                            Path = $"{_pathPrefix}{_next++}",
                            Name = (itemEl?.Content ?? item)?.ToString() ?? $"item {ix}",
                            Role = 32, RoleName = "list item",
                            Enabled = true, Parent = n, Element = itemEl,
                            Selectable = true, Selected = ix == cb0.SelectedIndex,
                            ItemIndex = ix,
                        };
                        n.Children.Add(child);
                        ByPath[child.Path] = child;
                    }
                    // the combo also exposes its current selection as Text
                    n.Text = n.Children.Find(c => c.Selected)?.Name ?? "";
                    descend = false;   // don't walk the visual subtree — the content
                                       // presenter shows a copy of the selected item
                }
                parent.Children.Add(n);
                ByPath[n.Path] = n;
                attach = n;
            }
        }
        if (!descend) return;
        int c = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < c; i++)
            Walk(VisualTreeHelper.GetChild(node, i), attach);
    }

    static T Try<T>(Func<T> f, T dflt) { try { return f(); } catch { return dflt; } }

    // ---- live events: project Uno UI events onto the change notifications ----
    public void HookLiveEvents()
    {
        foreach (var n in ByPath.Values)
        {
            if (n.Element is null) continue;
            var node = n;
            node.Element.GotFocus  += (_, _) => StateChanged?.Invoke(node, "focused", 1);
            node.Element.LostFocus += (_, _) => StateChanged?.Invoke(node, "focused", 0);
            if (node.Element is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
            {
                tb.Checked   += (_, _) => { node.Checked = true;  StateChanged?.Invoke(node, "checked", 1); };
                tb.Unchecked += (_, _) => { node.Checked = false; StateChanged?.Invoke(node, "checked", 0); };
            }
            if (node.Element is Microsoft.UI.Xaml.Controls.Primitives.RangeBase rb)
                rb.ValueChanged += (_, e) =>
                { node.Val = e.NewValue; PropertyChanged?.Invoke(node, "accessible-value", e.NewValue); };
            if (node.Element is Microsoft.UI.Xaml.Controls.TextBox tx)
                tx.TextChanged += (_, _) =>
                { node.Text = tx.Text ?? ""; PropertyChanged?.Invoke(node, "accessible-value", node.Text); };
            if (node.Element is Microsoft.UI.Xaml.Controls.ComboBox cb)
            {
                cb.DropDownOpened += (_, _) => { node.Expanded = true;  StateChanged?.Invoke(node, "expanded", 1); };
                cb.DropDownClosed += (_, _) => { node.Expanded = false; StateChanged?.Invoke(node, "expanded", 0); };
                cb.SelectionChanged += (_, _) =>
                {
                    foreach (var item in node.Children)
                        item.Selected = item.ItemIndex == cb.SelectedIndex;
                    node.Text = node.Children.Find(c => c.Selected)?.Name ?? "";
                    SelectionChanged?.Invoke(node);
                };
            }
        }
    }

    public Node? FindByRole(string roleName)
    {
        foreach (var n in ByPath.Values) if (n.RoleName == roleName) return n;
        return null;
    }

    // ---- the write-path: incoming a11y requests drive the real control ----
    // Requests arrive on the transport's thread; UI mutation must hop to Uno's
    // dispatcher. Callers report success once the action is *dispatched*; the
    // change notification the control raises afterwards is the confirmation.
    public bool InvokeOnUi(Node n)
    {
        // A combo item's element may be an unrealized container (popup never opened),
        // so its action routes through the parent combo's selection instead.
        if (n.Selectable && n.Parent is { } p)
            return SelectChildOnUi(p, n.ItemIndex);
        var el = n.Element;
        var dq = el?.DispatcherQueue;
        if (el is null || dq is null) return false;
        return dq.TryEnqueue(() =>
        {
            var (ok, detail) = UnoDemo.Agent.Act(el);   // GetPattern(Invoke/Toggle/…) — same helper as the in-app agent
            Console.WriteLine($"[atspi] DoAction '{n.Name}': {detail} (ok={ok})");
        });
    }

    // Value.CurrentValue set → IRangeValueProvider.SetValue, clamped to [min, max].
    public bool SetRangeValueOnUi(Node n, double value)
    {
        var el = n.Element; var dq = el?.DispatcherQueue;
        if (el is null || dq is null) return false;
        return dq.TryEnqueue(() =>
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
            if (peer?.GetPattern(PatternInterface.RangeValue)
                is Microsoft.UI.Xaml.Automation.Provider.IRangeValueProvider rv)
            {
                var clamped = Math.Max(rv.Minimum, Math.Min(rv.Maximum, value));
                rv.SetValue(clamped);
                Console.WriteLine($"[atspi] SetCurrentValue '{n.Name}' = {clamped}");
            }
        });
    }

    // Selection.SelectChild — the indexed item peer's ISelectionItemProvider
    // .AddToSelection, falling back to SelectedIndex when the item container has
    // no peer yet (Uno realizes popup containers lazily).
    public bool SelectChildOnUi(Node combo, int index)
    {
        var el = combo.Element as Microsoft.UI.Xaml.Controls.ComboBox;
        var dq = el?.DispatcherQueue;
        if (el is null || dq is null || index < 0) return false;
        return dq.TryEnqueue(() =>
        {
            if (index >= el.Items.Count) return;
            var itemEl = el.Items[index] as Microsoft.UI.Xaml.Controls.ComboBoxItem;
            var peer = itemEl is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(itemEl);
            if (peer?.GetPattern(PatternInterface.SelectionItem)
                is Microsoft.UI.Xaml.Automation.Provider.ISelectionItemProvider sip)
                sip.AddToSelection();
            else
                el.SelectedIndex = index;   // ponytail: unrealized container → select on the combo
            Console.WriteLine($"[atspi] SelectChild {index} on '{combo.Name}' → '{combo.Children[index].Name}'");
        });
    }

    // EditableText → IValueProvider.SetValue; Insert/Delete are string surgery on
    // the live text.
    public bool SetTextOnUi(Node n, string text)
    {
        var el = n.Element; var dq = el?.DispatcherQueue;
        if (el is null || dq is null) return false;
        return dq.TryEnqueue(() =>
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
            if (peer?.GetPattern(PatternInterface.Value)
                is Microsoft.UI.Xaml.Automation.Provider.IValueProvider { IsReadOnly: false } vp)
            {
                vp.SetValue(text);
                Console.WriteLine($"[atspi] SetTextContents '{n.Name}' = \"{text}\"");
            }
        });
    }
}
