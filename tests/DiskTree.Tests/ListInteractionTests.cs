using System.Windows.Input;
using DiskTree.Ui;
using Xunit;

namespace DiskTree.Tests;

public class ListInteractionTests
{
    [Theory]
    [InlineData(Key.Apps, Key.None, ModifierKeys.None, true)]
    [InlineData(Key.System, Key.F10, ModifierKeys.Shift, true)]   // how WPF delivers Shift+F10
    [InlineData(Key.System, Key.F10, ModifierKeys.None, false)]   // plain F10 activates the window menu
    [InlineData(Key.System, Key.F10, ModifierKeys.Shift | ModifierKeys.Control, false)]
    [InlineData(Key.F10, Key.None, ModifierKeys.Shift, true)]     // (not seen in practice, kept working)
    [InlineData(Key.System, Key.F4, ModifierKeys.Shift, false)]
    [InlineData(Key.Delete, Key.None, ModifierKeys.None, false)]
    public void IsMenuKey(Key key, Key systemKey, ModifierKeys mods, bool expected) =>
        Assert.Equal(expected, ListInteraction.IsMenuKey(key, systemKey, mods));
}
