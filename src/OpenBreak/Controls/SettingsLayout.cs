using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace OpenBreak;

internal sealed class SettingsLayout : Grid
{
    private static readonly string[] ToggleBrushKeys =
    [
        "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
        "ToggleSwitchStrokeOn", "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed",
        "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
    ];
    private readonly Grid content = new();
    private readonly List<SettingsRow> rows = [];

    internal SettingsLayout(string title)
    {
        Form = new StackPanel { Spacing = ManagementStyle.SectionGap };
        Form.Children.Add(ManagementStyle.Label(title, SurfaceStyle.PageTitleSize, heading: AutomationHeadingLevel.Level1));
        content.Margin = ManagementStyle.PageMargin(0);
        content.ColumnDefinitions.Add(new ColumnDefinition { MaxWidth = 760 });
        content.RowDefinitions.Add(new RowDefinition());
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.Children.Add(SurfaceStyle.Scroll(Form));
        Children.Add(content);
        SizeChanged += (_, args) => content.Margin = ManagementStyle.PageMargin(args.NewSize.Width);
    }

    internal StackPanel Form { get; }

    internal Grid Row(string title, FrameworkElement field, string id, string? help = null)
    {
        if (field is ToggleSwitch toggle)
        {
            SetToggleResources(toggle);
        }
        var row = new SettingsRow(title, field, id, help);
        rows.Add(row);
        return row;
    }

    private static void SetToggleResources(ToggleSwitch toggle)
    {
        // The default template supplies these keys itself. Put the app brushes in the control's resource scope.
        var themes = Application.Current.Resources.ThemeDictionaries;
        foreach (var theme in new[] { "Light", "Default", "HighContrast" })
        {
            var palette = (ResourceDictionary)themes[theme];
            var overrides = new ResourceDictionary();
            foreach (var key in ToggleBrushKeys)
            {
                overrides[key] = palette[key];
            }
            toggle.Resources.ThemeDictionaries[theme] = overrides;
        }
    }

    internal void SetFooter(FrameworkElement footer)
    {
        footer.Margin = new Thickness(0, SurfaceStyle.ControlGap, 0, 0);
        SetRow(footer, 1);
        content.Children.Add(footer);
    }

    internal void ApplyAppearance(AppearanceState state)
    {
        foreach (var row in rows)
        {
            row.SetTextScale(state.TextScale);
        }
        ManagementStyle.ApplyContrast(content, state);
    }

    internal static StackPanel Group(string title, params UIElement[] fields)
    {
        var group = new StackPanel { Spacing = SurfaceStyle.SectionGap };
        group.Children.Add(ManagementStyle.SectionHeading(title));
        foreach (var field in fields)
        {
            group.Children.Add(field);
        }
        return group;
    }

    internal static StackPanel Footer(InfoBar feedback, params Button[] buttons)
    {
        var footer = new StackPanel { Spacing = SurfaceStyle.ControlGap };
        var actions = new ActionRow { HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var button in buttons)
        {
            actions.Children.Add(button);
        }
        footer.Children.Add(feedback);
        footer.Children.Add(actions);
        return footer;
    }

    private sealed class SettingsRow : Grid
    {
        private readonly ColumnDefinition fieldColumn = new();
        private readonly StackPanel label = new() { Spacing = SurfaceStyle.TightGap, VerticalAlignment = VerticalAlignment.Center };
        private readonly FrameworkElement field;
        private double textScale = 1;

        internal SettingsRow(string title, FrameworkElement field, string id, string? help)
        {
            this.field = field;
            AutomationProperties.SetAutomationId(this, id + "Row");
            AutomationProperties.SetAutomationId(field, id);
            if (field is Control)
            {
                AutomationProperties.SetName(field, title);
                if (help is not null)
                {
                    AutomationProperties.SetHelpText(field, help);
                }
            }
            label.Children.Add(ManagementStyle.Label(title, SurfaceStyle.Body));
            if (help is not null)
            {
                label.Children.Add(ManagementStyle.BodyText(help));
            }
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(fieldColumn);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            field.HorizontalAlignment = HorizontalAlignment.Stretch;
            field.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(label);
            Children.Add(field);
            SizeChanged += (_, args) => Layout(args.NewSize.Width);
            Layout(0);
        }

        internal void SetTextScale(double scale)
        {
            textScale = scale;
            Layout(ActualWidth);
        }

        private void Layout(double width)
        {
            var stack = width < 490 * textScale;
            var fieldWidth = Math.Min(280, 248 * textScale);
            fieldColumn.Width = new GridLength(stack ? 0 : fieldWidth);
            ColumnSpacing = stack ? 0 : SurfaceStyle.SectionGap;
            RowSpacing = stack ? SurfaceStyle.ControlGap : 0;
            SetColumnSpan(label, stack ? 2 : 1);
            SetColumn(field, stack ? 0 : 1);
            SetColumnSpan(field, stack ? 2 : 1);
            SetRow(field, stack ? 1 : 0);
        }
    }
}
