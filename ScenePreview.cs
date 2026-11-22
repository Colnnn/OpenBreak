using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace Afterbreak;

internal sealed class ScenePreview : UserControl
{
    private readonly ScenePalette palette = new();
    private readonly Border backdrop = new();
    private BreakScene? renderedScene;

    internal ScenePreview()
    {
        var viewport = new Grid { Background = palette.Background, MinHeight = 212 };
        // Scenery fills the measured content height without imposing its source dimensions.
        var scenery = new Canvas { IsHitTestVisible = false };
        scenery.Children.Add(backdrop);
        viewport.Children.Add(scenery);
        viewport.SizeChanged += (_, args) =>
        {
            backdrop.Width = args.NewSize.Width;
            backdrop.Height = args.NewSize.Height;
        };

        var timer = SurfaceStyle.Timer("2:00", 44);
        timer.HorizontalAlignment = HorizontalAlignment.Center;
        timer.Foreground = palette.Foreground;
        AutomationProperties.SetName(timer, "Sample timer, 2 minutes");
        var task = new StackPanel { Spacing = SurfaceStyle.TightGap };
        var taskLabel = SurfaceStyle.Label("When you're back", SurfaceStyle.Caption);
        taskLabel.Foreground = palette.Secondary;
        taskLabel.TextWrapping = TextWrapping.Wrap;
        var taskTitle = SurfaceStyle.Label("Review the meeting notes", SurfaceStyle.StrongBody, FontWeights.SemiBold);
        taskTitle.Foreground = palette.Foreground;
        taskTitle.TextWrapping = TextWrapping.Wrap;
        task.Children.Add(taskLabel);
        task.Children.Add(taskTitle);
        var content = new StackPanel
        {
            MaxWidth = 380,
            Margin = new Thickness(SurfaceStyle.SectionGap),
            Spacing = SurfaceStyle.ControlGap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(timer);
        content.Children.Add(palette.Card(task));
        viewport.Children.Add(content);
        Content = new Border
        {
            BorderBrush = palette.Stroke,
            BorderThickness = new Thickness(1),
            CornerRadius = SurfaceStyle.PanelCorner,
            Child = viewport,
        };
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        palette.Apply(state);
        backdrop.Visibility = state.ShowScene ? Visibility.Visible : Visibility.Collapsed;
        if (state.ShowScene && renderedScene != state.Scene)
        {
            renderedScene = state.Scene;
            backdrop.Child = StillBackdrop.Create(state.Scene);
        }
    }
}
