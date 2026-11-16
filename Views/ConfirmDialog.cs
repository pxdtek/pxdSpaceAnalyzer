using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace pxdSpaceAnalyzer.Views;

/// <summary>The "are you sure?" dialog before anything is deleted: the pxd title bar, what happens, the items, Yes and Cancel.</summary>
public sealed class ConfirmDialog : Window
{
    public bool Answer { get; private set; }

    public ConfirmDialog(Confirm c)
    {
        Title = c.Title;
        Icon = BrandIcon.Window();
        Width = 560;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var s = new StackPanel { Spacing = 12, Margin = new Thickness(22, 18, 22, 18) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new TextBlock { Text = "", FontFamily = MainWindow.Glyphs, FontSize = 26, Foreground = c.Danger ? MainWindow.Warn : MainWindow.Good, VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = c.Title, FontSize = 18, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
        s.Children.Add(head);
        s.Children.Add(new TextBlock { Text = c.Message, TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.TextB });
        if (c.Lines.Count > 0)
        {
            var list = new StackPanel { Spacing = 4 };
            foreach (var l in c.Lines.Take(30))
                list.Children.Add(new TextBlock { Text = "•  " + l, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = MainWindow.Muted });
            if (c.Lines.Count > 30) list.Children.Add(new TextBlock { Text = $"…and {c.Lines.Count - 30} more", FontSize = 12, Foreground = MainWindow.Muted });
            s.Children.Add(new Border
            {
                Background = MainWindow.Brush("#0E1015"), BorderBrush = MainWindow.Brush("#272C39"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10), Child = new ScrollViewer { Content = list, MaxHeight = 300 },
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var no = new Button { Content = "Cancel", MinWidth = 100 };
        no.Click += (_, _) => Finish(false);
        var yes = new Button { Content = c.Yes, Classes = { "primary" }, MinWidth = 140 };
        yes.Click += (_, _) => Finish(true);
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        s.Children.Add(buttons);

        Content = pxdStandard.pxdChrome.Wrap(this, s, c.Title, BrandIcon.Render(36), resizable: false);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(false);
        };
        Opened += (_, _) => no.Focus();   // Enter never deletes by accident
    }

    private void Finish(bool answer)
    {
        Answer = answer;
        base.Close(answer);
    }

    public static async Task<bool> AskAsync(Window owner, Confirm c)
    {
        var d = new ConfirmDialog(c);
        return await d.ShowDialog<bool>(owner);
    }
}
