using System.Reflection;
using System.Windows;
using DiskTree.Features.Treemap;
using DiskTree.Ui;
using Xunit;

namespace DiskTree.Tests;

/// <summary>Theme.Changed is static: controls must subscribe while loaded only and never twice.</summary>
[Collection("ThemeEvents")]
public class ThemeSubscriptionTests
{
    private static int Subscribers()
    {
        var field = typeof(Theme).GetField("Changed", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (field.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private static void Raise(FrameworkElement e, RoutedEvent ev) => e.RaiseEvent(new RoutedEventArgs(ev));

    private static void Check(Func<FrameworkElement> create)
    {
        // WPF elements need an STA thread
        Exception? error = null;
        var t = new Thread(() => { try { CheckCore(create); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static void CheckCore(Func<FrameworkElement> create)
    {
        int baseline = Subscribers();
        var e = create();
        Assert.Equal(baseline, Subscribers());        // not loaded: no subscription

        Raise(e, FrameworkElement.LoadedEvent);
        Assert.Equal(baseline + 1, Subscribers());
        Raise(e, FrameworkElement.LoadedEvent);        // tab templates raise Loaded repeatedly
        Assert.Equal(baseline + 1, Subscribers());

        Raise(e, FrameworkElement.UnloadedEvent);
        Assert.Equal(baseline, Subscribers());
        Raise(e, FrameworkElement.UnloadedEvent);
        Assert.Equal(baseline, Subscribers());
    }

    [Fact]
    public void PieChart_SubscribesOnlyWhileLoaded() => Check(() => new PieChart());

    [Fact]
    public void TreemapView_SubscribesOnlyWhileLoaded() => Check(() => new TreemapView());
}
