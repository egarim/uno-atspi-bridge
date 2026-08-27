// Minimal AT-SPI2 backend for Uno on Linux (PoC).
//
// Walks Uno's AutomationPeer tree, models each control as an AT-SPI accessible,
// and serves them over the a11y D-Bus so an AT-SPI client (Orca / atspi_dump.py)
// can read role/name/box/states. Implements enough of org.a11y.atspi.Accessible +
// Component + Application + the Socket.Embed handshake for the tree to be visible.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Tmds.DBus.Protocol;

namespace UnoDemo.Atspi;

internal sealed class Node
{
    public string Path = "";
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

public sealed class AtspiBridge
{
    const string RootPath = "/org/a11y/atspi/accessible/root";
    const string AccIface = "org.a11y.atspi.Accessible";
    const string CompIface = "org.a11y.atspi.Component";
    const string AppIface = "org.a11y.atspi.Application";
    const string ActionIface = "org.a11y.atspi.Action";
    const string ValueIface = "org.a11y.atspi.Value";
    const string EditIface = "org.a11y.atspi.EditableText";
    const string TextIface = "org.a11y.atspi.Text";
    const string SelIface = "org.a11y.atspi.Selection";
    const string PropIface = "org.freedesktop.DBus.Properties";
    const int ST_CHECKED = 4, ST_EDITABLE = 7, ST_ENABLED = 8, ST_EXPANDABLE = 9,
              ST_EXPANDED = 10, ST_FOCUSABLE = 11, ST_SELECTABLE = 22, ST_SELECTED = 23,
              ST_SENSITIVE = 24, ST_SHOWING = 25, ST_VISIBLE = 30;

    DBusConnection? _conn;
    string _unique = "";
    readonly Dictionary<string, Node> _byPath = new();
    readonly Node _root = new() { Path = RootPath, Name = "UnoDemo", Role = 75, RoleName = "application" };
    int _next = 1;
    (int x, int y) _origin;   // window position on screen (added to window-relative rects)

    public static async void TryStart(Microsoft.UI.Xaml.FrameworkElement uiRoot)
    {
        try { await new AtspiBridge().StartAsync(uiRoot); }
        catch (Exception ex) { Console.WriteLine($"[atspi] bridge failed: {ex}"); }
    }

    async Task StartAsync(Microsoft.UI.Xaml.FrameworkElement uiRoot)
    {
        // window position on screen (for screen-space coordinates)
        try
        {
            var p = App.Win?.AppWindow?.Position;
            if (p is { } pos) _origin = (pos.X, pos.Y);
        }
        catch { }

        WalkVisual(uiRoot, _root);
        ApplyScreenCoordinates();
        Console.WriteLine($"[atspi] built {_byPath.Count} control nodes (+root); window origin {_origin}");

        var address = await GetA11yBusAddressAsync();
        if (string.IsNullOrEmpty(address)) { Console.WriteLine("[atspi] no a11y bus"); return; }
        Console.WriteLine($"[atspi] a11y bus = {address}");

        _conn = new DBusConnection(address);
        await _conn.ConnectAsync();
        _unique = _conn.UniqueName ?? "";
        Console.WriteLine($"[atspi] connected, unique = {_unique}");

        _conn.AddMethodHandler(new NodeHandler(this, _root));
        foreach (var n in _byPath.Values) _conn.AddMethodHandler(new NodeHandler(this, n));

        await EmbedAsync();
        HookLiveEvents();
        Console.WriteLine("[atspi] embedded; tree is live, events wired");

        _ = EventDemoAsync();   // drive a focus + toggle so events are observable
    }

    // WinUI's GetBoundingRectangle is spec'd as screen-relative. If a head returns
    // window-relative rects instead (outermost node sits at ~0 while the window is
    // offset), add the window origin so every box is true screen space.
    void ApplyScreenCoordinates()
    {
        if (_origin == (0, 0)) return;                       // nothing to add
        var top = _root.Children.Count > 0 ? _root.Children[0] : null;
        bool windowRelative = top != null && top.Box.x < _origin.x - 4;
        if (!windowRelative) return;
        foreach (var n in _byPath.Values)
            n.Box = (n.Box.x + _origin.x, n.Box.y + _origin.y, n.Box.w, n.Box.h);
    }

    async Task<string?> GetA11yBusAddressAsync()
    {
        var session = DBusConnection.Session;
        await session.ConnectAsync();
        var w = session.GetMessageWriter();
        w.WriteMethodCallHeader("org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus",
                                "GetAddress", null, MessageFlags.None);
        return await session.CallMethodAsync(w.CreateMessage(),
            static (Message m, object? s) => m.GetBodyReader().ReadString(), null);
    }

    async Task EmbedAsync()
    {
        var w = _conn!.GetMessageWriter();
        w.WriteMethodCallHeader("org.a11y.atspi.Registry", RootPath,
                                "org.a11y.atspi.Socket", "Embed", "(so)", MessageFlags.None);
        w.WriteStructureStart();
        w.WriteString(_unique);
        w.WriteObjectPath(RootPath);
        await _conn.CallMethodAsync(w.CreateMessage());
    }

    void WalkVisual(DependencyObject node, Node parent)
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
                    Path = $"/org/a11y/atspi/accessible/{_next++}",
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
                    // can't reach them — surface the Items collection as bus children
                    // (role: list item), each selectable through the Selection iface.
                    n.Expandable = true;
                    for (int ix = 0; ix < cb0.Items.Count; ix++)
                    {
                        var item = cb0.Items[ix];
                        var itemEl = item as Microsoft.UI.Xaml.Controls.ComboBoxItem;
                        var child = new Node
                        {
                            Path = $"/org/a11y/atspi/accessible/{_next++}",
                            Name = (itemEl?.Content ?? item)?.ToString() ?? $"item {ix}",
                            Role = 32, RoleName = "list item",
                            Enabled = true, Parent = n, Element = itemEl,
                            Selectable = true, Selected = ix == cb0.SelectedIndex,
                            ItemIndex = ix,
                        };
                        n.Children.Add(child);
                        _byPath[child.Path] = child;
                    }
                    descend = false;   // don't walk the visual subtree — the content
                                       // presenter shows a copy of the selected item
                }
                parent.Children.Add(n);
                _byPath[n.Path] = n;
                attach = n;
            }
        }
        if (!descend) return;
        int c = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < c; i++)
            WalkVisual(VisualTreeHelper.GetChild(node, i), attach);
    }

    static T Try<T>(Func<T> f, T dflt) { try { return f(); } catch { return dflt; } }

    // ---- live events: project Uno UI events onto AT-SPI state-changed signals ----
    void HookLiveEvents()
    {
        foreach (var n in _byPath.Values)
        {
            if (n.Element is null) continue;
            var node = n;
            node.Element.GotFocus  += (_, _) => EmitStateChanged(node, "focused", 1);
            node.Element.LostFocus += (_, _) => EmitStateChanged(node, "focused", 0);
            if (node.Element is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton tb)
            {
                tb.Checked   += (_, _) => { node.Checked = true;  EmitStateChanged(node, "checked", 1); };
                tb.Unchecked += (_, _) => { node.Checked = false; EmitStateChanged(node, "checked", 0); };
            }
            if (node.Element is Microsoft.UI.Xaml.Controls.Primitives.RangeBase rb)
                rb.ValueChanged += (_, e) =>
                { node.Val = e.NewValue; EmitPropertyChange(node, "accessible-value", e.NewValue); };
            if (node.Element is Microsoft.UI.Xaml.Controls.TextBox tx)
                tx.TextChanged += (_, _) =>
                { node.Text = tx.Text ?? ""; EmitPropertyChange(node, "accessible-value", node.Text); };
            if (node.Element is Microsoft.UI.Xaml.Controls.ComboBox cb)
            {
                cb.DropDownOpened += (_, _) => { node.Expanded = true;  EmitStateChanged(node, "expanded", 1); };
                cb.DropDownClosed += (_, _) => { node.Expanded = false; EmitStateChanged(node, "expanded", 0); };
                cb.SelectionChanged += (_, _) =>
                {
                    foreach (var item in node.Children)
                        item.Selected = item.ItemIndex == cb.SelectedIndex;
                    EmitSelectionChanged(node);
                };
            }
        }
    }

    // org.a11y.atspi.Event.Object.StateChanged  body: siiv(so)
    void EmitStateChanged(Node n, string detail, int value)
    {
        if (_conn is null) return;
        try
        {
            var w = _conn.GetMessageWriter();
            w.WriteSignalHeader(null, n.Path, "org.a11y.atspi.Event.Object", "StateChanged", "siiv(so)");
            w.WriteString(detail);
            w.WriteInt32(value);
            w.WriteInt32(0);
            w.WriteVariantInt32(0);
            w.WriteStructureStart(); w.WriteString(_unique); w.WriteObjectPath(RootPath);
            _conn.TrySendMessage(w.CreateMessage());
            Console.WriteLine($"[atspi] emit state-changed:{detail}={value} on {n.Name}");
        }
        catch (Exception ex) { Console.WriteLine($"[atspi] emit failed: {ex.Message}"); }
    }

    // org.a11y.atspi.Event.Object.SelectionChanged — emitted (empty detail) when a
    // container's selection changes.
    void EmitSelectionChanged(Node n)
    {
        if (_conn is null) return;
        try
        {
            var w = _conn.GetMessageWriter();
            w.WriteSignalHeader(null, n.Path, "org.a11y.atspi.Event.Object", "SelectionChanged", "siiv(so)");
            w.WriteString("");
            w.WriteInt32(0);
            w.WriteInt32(0);
            w.WriteVariantInt32(0);
            w.WriteStructureStart(); w.WriteString(_unique); w.WriteObjectPath(RootPath);
            _conn.TrySendMessage(w.CreateMessage());
            Console.WriteLine($"[atspi] emit selection-changed on {n.Name}");
        }
        catch (Exception ex) { Console.WriteLine($"[atspi] emit failed: {ex.Message}"); }
    }

    // org.a11y.atspi.Event.Object.PropertyChange  body: siiv(so) — the signal AT-SPI
    // clients expect for value/text changes ("accessible-value").
    void EmitPropertyChange(Node n, string prop, object value)
    {
        if (_conn is null) return;
        try
        {
            var w = _conn.GetMessageWriter();
            w.WriteSignalHeader(null, n.Path, "org.a11y.atspi.Event.Object", "PropertyChange", "siiv(so)");
            w.WriteString(prop);
            w.WriteInt32(0);
            w.WriteInt32(0);
            if (value is double d) w.WriteVariantDouble(d); else w.WriteVariantString(value.ToString() ?? "");
            w.WriteStructureStart(); w.WriteString(_unique); w.WriteObjectPath(RootPath);
            _conn.TrySendMessage(w.CreateMessage());
            Console.WriteLine($"[atspi] emit property-change:{prop}={value} on {n.Name}");
        }
        catch (Exception ex) { Console.WriteLine($"[atspi] emit failed: {ex.Message}"); }
    }

    // Drive a focus + a checkbox toggle so a listening client (Orca / atspi_listen)
    // observes real, live events without a human touching anything.
    async Task EventDemoAsync()
    {
        // Skip the canned focus/toggle when an external agent is driving, so the only
        // events on the bus are the ones the agent's DoAction actually caused.
        if (Environment.GetEnvironmentVariable("UNODEMO_NO_AUTODEMO") == "1") return;
        await Task.Delay(3000);
        var entry = FindByRole("entry");
        entry?.Element?.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        await Task.Delay(1500);
        if (FindByRole("check box")?.Element is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton cb)
            cb.IsChecked = true;
    }

    Node? FindByRole(string roleName)
    {
        foreach (var n in _byPath.Values) if (n.RoleName == roleName) return n;
        return null;
    }

    // ---- the write-path: an incoming AT-SPI Action.DoAction drives the real control ----
    // AT-SPI calls arrive on the D-Bus thread; UI mutation must hop to Uno's dispatcher.
    // We reply true once the action is *dispatched* (AT-SPI semantics); the actual proof
    // is the state-changed event HookLiveEvents emits when the control really changes.
    internal bool InvokeOnUi(Node n)
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
            var (ok, detail) = UnoDemo.Agent.Act(el);   // GetPattern(Invoke/Toggle) — same helper as the in-app agent
            Console.WriteLine($"[atspi] DoAction '{n.Name}': {detail} (ok={ok})");
        });
    }

    // Value.CurrentValue set → IRangeValueProvider.SetValue, clamped to [min, max].
    internal bool SetRangeValueOnUi(Node n, double value)
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
    internal bool SelectChildOnUi(Node combo, int index)
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
    internal bool SetTextOnUi(Node n, string text)
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

    sealed class NodeHandler : IPathMethodHandler
    {
        readonly AtspiBridge _b; readonly Node _n;
        public NodeHandler(AtspiBridge b, Node n) { _b = b; _n = n; }
        public string Path => _n.Path;
        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext ctx)
        {
            string iface = ctx.Request.InterfaceAsString ?? "";
            string member = ctx.Request.MemberAsString ?? "";
            if (iface == AccIface) Accessible(ctx, member);
            else if (iface == CompIface) Component(ctx, member);
            else if (iface == ActionIface) Action(ctx, member);
            else if (iface == EditIface) EditableText(ctx, member);
            else if (iface == TextIface) Text(ctx, member);
            else if (iface == SelIface) Selection(ctx, member);
            else if (iface == AppIface) Application(ctx, member);
            else if (iface == PropIface) Properties(ctx, member);
            else if (iface == "org.freedesktop.DBus.Introspectable" && member == "Introspect")
                ReplyStr(ctx, "s", "<node/>");
            return default;
        }

        void Accessible(MethodContext ctx, string m)
        {
            switch (m)
            {
                case "GetRole": ReplyU(ctx, _n.Role); break;
                case "GetRoleName":
                case "GetLocalizedRoleName": ReplyStr(ctx, "s", _n.RoleName); break;
                case "GetState": ReplyStates(ctx); break;
                case "GetInterfaces": ReplyIfaces(ctx); break;
                case "GetChildAtIndex":
                {
                    int i = ctx.Request.GetBodyReader().ReadInt32();
                    var c = (i >= 0 && i < _n.Children.Count) ? _n.Children[i] : null;
                    ReplyRef(ctx, c?.Path ?? "/org/a11y/atspi/null"); break;
                }
                case "GetChildren": ReplyRefs(ctx); break;
                case "GetIndexInParent": ReplyI(ctx, _n.Parent?.Children.IndexOf(_n) ?? -1); break;
                case "GetApplication": ReplyRef(ctx, RootPath); break;
                case "GetAttributes": ReplyEmptyArray(ctx, "a{ss}"); break;
                case "GetRelationSet": ReplyEmptyArray(ctx, "a(ua(so))"); break;
            }
        }

        void Component(MethodContext ctx, string m)
        {
            switch (m)
            {
                case "GetExtents":
                {
                    var w = ctx.CreateReplyWriter("(iiii)");
                    w.WriteStructureStart();
                    w.WriteInt32(_n.Box.x); w.WriteInt32(_n.Box.y);
                    w.WriteInt32(_n.Box.w); w.WriteInt32(_n.Box.h);
                    ctx.Reply(w.CreateMessage()); break;
                }
                case "GetPosition":
                {
                    var w = ctx.CreateReplyWriter("(ii)");
                    w.WriteStructureStart(); w.WriteInt32(_n.Box.x); w.WriteInt32(_n.Box.y);
                    ctx.Reply(w.CreateMessage()); break;
                }
                case "GetSize":
                {
                    var w = ctx.CreateReplyWriter("(ii)");
                    w.WriteStructureStart(); w.WriteInt32(_n.Box.w); w.WriteInt32(_n.Box.h);
                    ctx.Reply(w.CreateMessage()); break;
                }
                case "GetLayer": ReplyU(ctx, 3); break;
                case "GrabFocus": ReplyBool(ctx, false); break;
                case "Contains": ReplyBool(ctx, false); break;
            }
        }

        // org.a11y.atspi.Action — the write-path. A client (Orca / our agent) calls
        // DoAction to activate the control the way a screen reader would.
        bool Actionable => _n.Selectable ||
            _n.RoleName is "push button" or "check box" or "radio button" or "combo box";
        string ActionName() => _n.Selectable ? "select" : _n.RoleName switch
        {
            "push button" => "press",
            "check box" => "toggle", "radio button" => "toggle",
            "combo box" => "expand",
            _ => "activate",
        };

        void Action(MethodContext ctx, string m)
        {
            switch (m)
            {
                case "GetNActions": ReplyI(ctx, Actionable ? 1 : 0); break;
                case "GetName":
                case "GetLocalizedName":
                    ctx.Request.GetBodyReader().ReadInt32();          // action index (only 0)
                    ReplyStr(ctx, "s", ActionName()); break;
                case "GetDescription":
                    ctx.Request.GetBodyReader().ReadInt32(); ReplyStr(ctx, "s", ""); break;
                case "GetKeyBinding":
                    ctx.Request.GetBodyReader().ReadInt32(); ReplyStr(ctx, "s", ""); break;
                case "GetActions":
                {
                    var w = ctx.CreateReplyWriter("a(sss)");
                    var a = w.WriteArrayStart(DBusType.Struct);
                    if (Actionable)
                    { w.WriteStructureStart(); w.WriteString(ActionName()); w.WriteString(""); w.WriteString(""); }
                    w.WriteArrayEnd(a);
                    ctx.Reply(w.CreateMessage()); break;
                }
                case "DoAction":
                {
                    ctx.Request.GetBodyReader().ReadInt32();          // action index
                    bool ok = _b.InvokeOnUi(_n);
                    ReplyBool(ctx, ok); break;
                }
            }
        }

        // org.a11y.atspi.Selection — minimal surface: enumerate/select the combo's
        // items over the bus.
        void Selection(MethodContext ctx, string m)
        {
            var sel = _n.Children.Find(c => c.Selected);
            switch (m)
            {
                case "GetSelectedChild":
                {
                    int i = ctx.Request.GetBodyReader().ReadInt32();
                    ReplyRef(ctx, i == 0 && sel != null ? sel.Path : "/org/a11y/atspi/null"); break;
                }
                case "SelectChild":
                {
                    int i = ctx.Request.GetBodyReader().ReadInt32();
                    ReplyBool(ctx, _b.SelectChildOnUi(_n, i)); break;
                }
                case "IsChildSelected":
                {
                    int i = ctx.Request.GetBodyReader().ReadInt32();
                    ReplyBool(ctx, i >= 0 && i < _n.Children.Count && _n.Children[i].Selected); break;
                }
                case "GetNSelectedChildren": ReplyI(ctx, sel != null ? 1 : 0); break;
                case "DeselectSelectedChild":
                case "DeselectChild":
                case "SelectAll":
                case "ClearSelection": ReplyBool(ctx, false); break;   // combo always has one selection
            }
        }

        // org.a11y.atspi.Text — minimal read side so act-then-verify works for entries.
        void Text(MethodContext ctx, string m)
        {
            if (m == "GetText")
            {
                var r = ctx.Request.GetBodyReader();
                int start = r.ReadInt32(); int end = r.ReadInt32();
                var t = _n.Text;
                start = Math.Max(0, Math.Min(start, t.Length));
                end = end < 0 ? t.Length : Math.Max(start, Math.Min(end, t.Length));
                ReplyStr(ctx, "s", t.Substring(start, end - start));
            }
        }

        // org.a11y.atspi.EditableText — set/insert/delete text through IValueProvider.
        // Replies true once dispatched (same semantics as DoAction); the PropertyChange
        // event from TextChanged is the confirmation.
        void EditableText(MethodContext ctx, string m)
        {
            if (!_n.HasText) { ReplyBool(ctx, false); return; }
            var r = ctx.Request.GetBodyReader();
            switch (m)
            {
                case "SetTextContents":
                    ReplyBool(ctx, _b.SetTextOnUi(_n, r.ReadString())); break;
                case "InsertText":
                {
                    int pos = r.ReadInt32(); string s = r.ReadString(); int len = r.ReadInt32();
                    var cur = _n.Text;
                    pos = Math.Max(0, Math.Min(pos, cur.Length));
                    var ins = len >= 0 && len < s.Length ? s.Substring(0, len) : s;
                    ReplyBool(ctx, _b.SetTextOnUi(_n, cur.Insert(pos, ins))); break;
                }
                case "DeleteText":
                {
                    int start = r.ReadInt32(); int end = r.ReadInt32();
                    var cur = _n.Text;
                    start = Math.Max(0, Math.Min(start, cur.Length));
                    end = Math.Max(start, Math.Min(end, cur.Length));
                    if (start >= end) { ReplyBool(ctx, false); break; }
                    ReplyBool(ctx, _b.SetTextOnUi(_n, cur.Remove(start, end - start))); break;
                }
                case "CopyText": ctx.Reply(ctx.CreateReplyWriter(null).CreateMessage()); break;
                case "CutText":
                case "PasteText": ReplyBool(ctx, false); break;   // no clipboard via IValueProvider
            }
        }

        void Application(MethodContext ctx, string m)
        {
            if (m == "GetLocale") ReplyStr(ctx, "s", "C");
        }

        void Properties(MethodContext ctx, string m)
        {
            if (m == "Get")
            {
                var reader = ctx.Request.GetBodyReader();
                string piface = reader.ReadString();
                string prop = reader.ReadString();
                if (piface == ValueIface && _n.HasRange)
                {
                    var w = ctx.CreateReplyWriter("v");
                    w.WriteVariantDouble(prop switch
                    {
                        "CurrentValue" => _n.Val,
                        "MinimumValue" => _n.Min,
                        "MaximumValue" => _n.Max,
                        _ => 0,                      // MinimumIncrement: RangeBase has no step
                    });
                    ctx.Reply(w.CreateMessage());
                }
                else if (piface == SelIface && _n.Expandable)
                {
                    var w = ctx.CreateReplyWriter("v");
                    w.WriteVariantInt32(prop == "NSelectedChildren" ? (_n.Children.Exists(c => c.Selected) ? 1 : 0) : 0);
                    ctx.Reply(w.CreateMessage());
                }
                else if (piface == TextIface && _n.HasText)
                {
                    var w = ctx.CreateReplyWriter("v");
                    w.WriteVariantInt32(prop == "CharacterCount" ? _n.Text.Length : 0);
                    ctx.Reply(w.CreateMessage());
                }
                else ReplyVariant(ctx, prop);
            }
            else if (m == "Set")
            {
                var reader = ctx.Request.GetBodyReader();
                string piface = reader.ReadString();
                string prop = reader.ReadString();
                if (piface == ValueIface && prop == "CurrentValue" && _n.HasRange)
                    _b.SetRangeValueOnUi(_n, reader.ReadVariantValue().GetDouble());
                ctx.Reply(ctx.CreateReplyWriter(null).CreateMessage());
            }
            else if (m == "GetAll")
            {
                var w = ctx.CreateReplyWriter("a{sv}");
                var a = w.WriteArrayStart(DBusType.Struct);
                w.WriteArrayEnd(a);                  // empty; clients fall back to Get
                ctx.Reply(w.CreateMessage());
            }
        }

        void ReplyVariant(MethodContext ctx, string prop)
        {
            var w = ctx.CreateReplyWriter("v");
            switch (prop)
            {
                case "Name": w.WriteVariantString(_n.Name); break;
                case "Description": w.WriteVariantString(""); break;
                case "Locale": w.WriteVariantString("C"); break;
                case "AccessibleId": w.WriteVariantString(""); break;
                case "ChildCount": w.WriteVariantInt32(_n.Children.Count); break;
                case "NActions": w.WriteVariantInt32(Actionable ? 1 : 0); break;
                case "ToolkitName": w.WriteVariantString("Uno"); break;
                case "Version": w.WriteVariantString("1.0"); break;
                case "AtspiVersion": w.WriteVariantString("2.1"); break;
                case "Parent":
                    w.WriteSignature("(so)");
                    w.WriteStructureStart();
                    w.WriteString(_n.Parent is null ? "org.a11y.atspi.Registry" : _b._unique);
                    w.WriteObjectPath(_n.Parent?.Path ?? RootPath);
                    break;
                default: w.WriteVariantString(""); break;
            }
            ctx.Reply(w.CreateMessage());
        }

        void ReplyStr(MethodContext ctx, string sig, string v)
        { var w = ctx.CreateReplyWriter(sig); w.WriteString(v); ctx.Reply(w.CreateMessage()); }
        void ReplyU(MethodContext ctx, uint v)
        { var w = ctx.CreateReplyWriter("u"); w.WriteUInt32(v); ctx.Reply(w.CreateMessage()); }
        void ReplyI(MethodContext ctx, int v)
        { var w = ctx.CreateReplyWriter("i"); w.WriteInt32(v); ctx.Reply(w.CreateMessage()); }
        void ReplyBool(MethodContext ctx, bool v)
        { var w = ctx.CreateReplyWriter("b"); w.WriteBool(v); ctx.Reply(w.CreateMessage()); }

        void ReplyStates(MethodContext ctx)
        {
            uint w0 = 0;
            void set(int bit) => w0 |= (1u << bit);
            if (_n.Enabled) { set(ST_ENABLED); set(ST_SENSITIVE); }
            set(ST_SHOWING); set(ST_VISIBLE);
            if (_n.Focusable) set(ST_FOCUSABLE);
            if (_n.Checked) set(ST_CHECKED);
            if (_n.HasText) set(ST_EDITABLE);
            if (_n.Expandable) { set(ST_EXPANDABLE); if (_n.Expanded) set(ST_EXPANDED); }
            if (_n.Selectable) { set(ST_SELECTABLE); if (_n.Selected) set(ST_SELECTED); }
            var w = ctx.CreateReplyWriter("au");
            var a = w.WriteArrayStart(DBusType.UInt32);
            w.WriteUInt32(w0); w.WriteUInt32(0);
            w.WriteArrayEnd(a);
            ctx.Reply(w.CreateMessage());
        }

        void ReplyIfaces(MethodContext ctx)
        {
            var w = ctx.CreateReplyWriter("as");
            var a = w.WriteArrayStart(DBusType.String);
            w.WriteString(AccIface); w.WriteString(CompIface);
            if (Actionable) w.WriteString(ActionIface);
            if (_n.HasRange) w.WriteString(ValueIface);
            if (_n.HasText) { w.WriteString(TextIface); w.WriteString(EditIface); }
            if (_n.Expandable) w.WriteString(SelIface);
            if (_n.Parent is null) w.WriteString(AppIface);
            w.WriteArrayEnd(a);
            ctx.Reply(w.CreateMessage());
        }

        void ReplyRef(MethodContext ctx, string path)
        {
            var w = ctx.CreateReplyWriter("(so)");
            w.WriteStructureStart(); w.WriteString(_b._unique); w.WriteObjectPath(path);
            ctx.Reply(w.CreateMessage());
        }
        void ReplyRefs(MethodContext ctx)
        {
            var w = ctx.CreateReplyWriter("a(so)");
            var a = w.WriteArrayStart(DBusType.Struct);
            foreach (var c in _n.Children)
            { w.WriteStructureStart(); w.WriteString(_b._unique); w.WriteObjectPath(c.Path); }
            w.WriteArrayEnd(a);
            ctx.Reply(w.CreateMessage());
        }
        void ReplyEmptyArray(MethodContext ctx, string sig)
        {
            var w = ctx.CreateReplyWriter(sig);
            var a = w.WriteArrayStart(DBusType.Struct); w.WriteArrayEnd(a);
            ctx.Reply(w.CreateMessage());
        }
    }
}
