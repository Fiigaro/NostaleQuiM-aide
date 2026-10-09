using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NosSmoothCustomClient.Gui;

/// <summary>
/// Shows why the bot could not start, in a window.
/// </summary>
/// <remarks>
/// Binding failures used to go to standard error and take the process with them, so a graphical
/// application answered "the game could not be reached" by never appearing at all - the operator
/// sees a window that does not open and has no way to tell a refusal from a crash. The message was
/// always written; it was written somewhere nobody was looking.
/// </remarks>
public sealed class StartupErrorWindow : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.Parse("#E6E8EA"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B9096"));
    private static readonly IBrush Blocked = new SolidColorBrush(Color.Parse("#D45B5B"));
    private static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#1E2022"));

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupErrorWindow"/> class.
    /// </summary>
    /// <param name="error">The reason the transport could not be bound.</param>
    public StartupErrorWindow(string error)
    {
        Title = "NosSmoothCustomClient - impossible de démarrer";
        Width = 720;
        Height = 420;
        Background = new SolidColorBrush(Color.Parse("#161819"));

        var panel = new StackPanel { Spacing = 14, Margin = new Avalonia.Thickness(20) };

        panel.Children.Add(new TextBlock
        {
            Text = "Le bot n'a pas pu se lier au jeu",
            Foreground = Blocked,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold
        });

        // Selectable, because the first thing anyone does with an error is paste it somewhere.
        panel.Children.Add(new Border
        {
            Background = Panel,
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(14),
            Child = new ScrollViewer
            {
                MaxHeight = 220,
                Content = new SelectableTextBlock
                {
                    Text = error,
                    Foreground = Ink,
                    FontSize = 12.5,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Consolas, Menlo, DejaVu Sans Mono, monospace")
                }
            }
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Le plus souvent : plusieurs clients sont ouverts, ou le numéro de processus a "
                   + "changé depuis le dernier lancement de NosTale.\n\n"
                   + "Pour voir les clients détectés :\n"
                   + "    dotnet run --project NosSmoothCustomClient -- --list\n\n"
                   + "Pour savoir quelle fenêtre est quel numéro (elles clignotent une par une) :\n"
                   + "    dotnet run --project NosSmoothCustomClient -- --identify\n\n"
                   + "Puis relance en désignant le bon :\n"
                   + "    dotnet run --project NosSmoothCustomClient.Gui -- --pcap --pid <numéro>",
            Foreground = Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        var close = new Button { Content = "Fermer", Width = 110, Height = 32, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        panel.Children.Add(close);

        Content = panel;
    }
}
