// Locks the AutomationControlType → AT-SPI role mapping (issue #6).
//
// Role mappings are load-bearing and silent when wrong: an AT-SPI-first driver that
// searches for [entry] finds nothing if Edit quietly maps elsewhere (the Win11
// Notepad Document-vs-Edit bug). No display needed — RoleMap is pure.
using Microsoft.UI.Xaml.Automation.Peers;
using UnoDemo.Atspi;
using Xunit;

namespace UnoDemo.Tests;

public class RoleMapTests
{
    [Theory]
    [InlineData(AutomationControlType.Button,   43u, "push button")]
    [InlineData(AutomationControlType.Edit,     79u, "entry")]
    [InlineData(AutomationControlType.CheckBox,  7u, "check box")]
    [InlineData(AutomationControlType.Slider,   51u, "slider")]
    [InlineData(AutomationControlType.ComboBox, 11u, "combo box")]
    [InlineData(AutomationControlType.Text,     29u, "label")]
    [InlineData(AutomationControlType.List,     31u, "list")]
    [InlineData(AutomationControlType.ListItem, 32u, "list item")]
    public void Maps_control_type_to_atspi_role(AutomationControlType t, uint id, string name)
        => Assert.Equal((id, name), RoleMap.Map(t));

    [Theory]
    [InlineData(AutomationControlType.Custom)]
    [InlineData(AutomationControlType.Window)]
    [InlineData(AutomationControlType.Image)]
    public void Unmapped_types_fall_back_to_panel(AutomationControlType t)
        => Assert.Equal((39u, "panel"), RoleMap.Map(t));
}
