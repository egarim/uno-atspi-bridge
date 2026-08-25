// The "agent" — perceives and acts through the accessibility abstraction only.
//
// PERCEPTION: walks the AutomationPeer tree (role / name / box / states) — the exact
//   tree a screen reader (and, on Linux, our AT-SPI bridge) reads. No pixels.
// ACTION: invokes a control through its automation pattern
//   (IInvokeProvider.Invoke / IToggleProvider.Toggle) — the same channel it perceived
//   through. This is how "the agent clicks": a verb on the tree node, not a mouse move.
//
// The same Act() helper backs the Linux bridge's AT-SPI Action.DoAction, so "read the
// tree, click by name" is one code path on Windows (UIA), macOS (AX) and Linux (AT-SPI).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;

namespace UnoDemo;

public sealed record TreeNode(
    string Role, string Name, Windows.Foundation.Rect Box,
    bool Enabled, bool Focusable, FrameworkElement Element, int Depth);

public static class Agent
{
    // ---- perception: the accessibility tree ----
    public static List<TreeNode> ReadTree(DependencyObject root)
    {
        var list = new List<TreeNode>();
        Walk(root, 0, list);
        return list;
    }

    static void Walk(DependencyObject node, int depth, List<TreeNode> outp)
    {
        int nextDepth = depth;
        if (node is FrameworkElement fe)
        {
            var p = Safe(() => FrameworkElementAutomationPeer.CreatePeerForElement(fe));
            if (p != null)
            {
                list_add(outp, fe, p, depth);
                nextDepth = depth + 1;
            }
        }
        int c = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < c; i++)
            Walk(VisualTreeHelper.GetChild(node, i), nextDepth, outp);
    }

    static void list_add(List<TreeNode> outp, FrameworkElement fe, AutomationPeer p, int depth)
        => outp.Add(new TreeNode(
            Role: Safe(() => p.GetAutomationControlType().ToString()) ?? "Custom",
            Name: Safe(() => p.GetName()) ?? "",
            Box: Safe(() => p.GetBoundingRectangle()),
            Enabled: Safe(() => p.IsEnabled()),
            Focusable: Safe(() => p.IsKeyboardFocusable()),
            Element: fe, Depth: depth));

    // Only the nodes an agent would treat as actionable / addressable.
    public static IEnumerable<TreeNode> Interactive(IEnumerable<TreeNode> tree) =>
        tree.Where(n => !string.IsNullOrEmpty(n.Name) &&
                        n.Role is "Button" or "Edit" or "CheckBox" or "RadioButton"
                               or "Slider" or "ComboBox" or "List" or "Hyperlink");

    // ---- action: click *through* the tree ----
    // Returns (did-something, human description of what happened).
    public static (bool ok, string detail) Act(FrameworkElement el)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(el);
        if (peer is null) return (false, "no automation peer");

        if (peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider inv)
        {
            inv.Invoke();
            return (true, "Invoke()");
        }
        if (peer.GetPattern(PatternInterface.Toggle) is IToggleProvider tog)
        {
            tog.Toggle();
            return (true, $"Toggle() → {tog.ToggleState}");
        }
        // no semantic action exposed — the caller may fall back to a box-center click
        return (false, "no Invoke/Toggle pattern");
    }

    // ---- the chat brain (deterministic; swap for an LLM later) ----
    // Understands "what/list/see" (enumerate) and action verbs (press/click/toggle/
    // enable/turn on/check/open …) targeting a control matched by name substring.
    public static string Handle(string message, DependencyObject surface)
    {
        var tree = ReadTree(surface).ToList();
        var actionable = Interactive(tree).ToList();
        string m = (message ?? "").Trim();
        string lm = m.ToLowerInvariant();

        if (lm.Length == 0) return "…say something.";

        // enumeration intent
        if (lm.Contains("what") || lm.Contains("list") || lm.Contains("see") ||
            lm.Contains("read") || lm.Contains("which") || lm.Contains("controls"))
        {
            if (actionable.Count == 0) return "The tree has no interactive controls yet — drop some from the palette.";
            var lines = actionable.Select(n =>
                $"  [{RoleTag(n.Role)}] '{n.Name}'  ({(int)n.Box.X},{(int)n.Box.Y},{(int)n.Box.Width},{(int)n.Box.Height})");
            return $"Reading the accessibility tree → {actionable.Count} interactive controls:\n" +
                   string.Join("\n", lines) +
                   "\nI never saw a pixel — that's the tree a screen reader reads.";
        }

        // action intent: match a control by name mentioned in the message
        var target = actionable
            .Where(n => lm.Contains(n.Name.ToLowerInvariant()))
            .OrderByDescending(n => n.Name.Length)          // most specific name wins
            .FirstOrDefault();

        // fall back to keyword→control hints if no exact name appears
        target ??= KeywordMatch(lm, actionable);

        if (target is null)
            return "I couldn't find a control by that name in the tree. Try “what can you see?”.";

        var (ok, detail) = Act(target.Element);
        string where = $"({(int)target.Box.X},{(int)target.Box.Y},{(int)target.Box.Width},{(int)target.Box.Height})";
        if (!ok)
            return $"Found [{RoleTag(target.Role)}] '{target.Name}' {where}, but it exposes no action ({detail}). " +
                   "A real agent would click its box center as a fallback.";
        return $"Matched [{RoleTag(target.Role)}] '{target.Name}' {where} → {detail}. ✓ done — through the tree, not the pixels.";
    }

    static TreeNode? KeywordMatch(string lm, List<TreeNode> nodes)
    {
        (string kw, string role)[] hints =
        {
            ("notif", "CheckBox"), ("check", "CheckBox"), ("enable", "CheckBox"),
            ("save",  "Button"),   ("open",  "Button"), ("press", "Button"), ("click", "Button"),
            ("search","Edit"),     ("type",  "Edit"),
            ("volume","Slider"),   ("theme", "ComboBox"),
        };
        foreach (var (kw, role) in hints)
            if (lm.Contains(kw))
            {
                var n = nodes.FirstOrDefault(x => x.Role == role &&
                            (x.Name.ToLowerInvariant().Contains(kw) || nodes.Count(y => y.Role == role) == 1));
                if (n != null) return n;
            }
        return null;
    }

    static string RoleTag(string controlType) => controlType switch
    {
        "Button" => "push button", "Edit" => "entry", "CheckBox" => "check box",
        "Slider" => "slider", "ComboBox" => "combo box", "RadioButton" => "radio button",
        "Text" => "label", _ => controlType.ToLowerInvariant(),
    };

    static T Safe<T>(Func<T> f) { try { return f(); } catch { return default!; } }
}
