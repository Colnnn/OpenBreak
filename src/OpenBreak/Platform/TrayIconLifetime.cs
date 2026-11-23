namespace OpenBreak;

internal readonly record struct TrayIconDesign(string? Glyphs, bool FinalMinute, int Size, uint Background, uint Ink)
{
    internal static TrayIconDesign For(CountdownDisplay? countdown, AppearanceState appearance)
    {
        var glyphs = countdown?.Glyphs;
        var finalMinute = countdown is { Glyphs: not null, FinalMinute: true };
        var (background, ink) = (appearance.HighContrast, finalMinute, appearance.Theme) switch
        {
            (true, _, _) => (appearance.WindowColor, appearance.TextColor),
            (_, true, AppearanceTheme.Light) => (0xFDE5D6U, 0x631E17U),
            (_, true, _) => (0x792B25U, 0xFFF0D8U),
            (_, _, AppearanceTheme.Light) => (0xD9F1EAU, 0x183C35U),
            (_, _, _) => (0x173A35U, 0xEDFFF7U),
        };
        return new TrayIconDesign(glyphs, finalMinute, 16, background, ink);
    }
}

internal sealed class TrayIconLifetime(nint fallback, Func<TrayIconDesign, nint> draw, Action<nint> destroy) : IDisposable
{
    private nint owned;
    private TrayIconDesign? applied;
    private string? tip;

    internal bool Apply(TrayIconDesign design, string tooltip, bool add,
        Func<nint, string, bool, bool, bool> publish)
    {
        var iconChanged = applied != design;
        var tipChanged = tip != tooltip;
        if (!add && !iconChanged && !tipChanged)
        {
            return true;
        }

        var next = owned;
        var nextApplied = applied;
        if (iconChanged)
        {
            next = draw(design);
            if (next != 0)
            {
                nextApplied = design;
            }
            else if (fallback != 0 && design.Glyphs is null && applied is { Glyphs: not null })
            {
                // Retire stale numbers even if drawing the requested idle mark fails.
                nextApplied = null;
            }
            else
            {
                // Keep the shell callback and exact tooltip usable if rasterization fails.
                next = owned;
                iconChanged = false;
            }
        }
        if (!add && !iconChanged && !tipChanged)
        {
            return true;
        }

        var icon = next != 0 ? next : fallback;
        if (icon == 0 || !publish(icon, tooltip, iconChanged || add, tipChanged || add))
        {
            if (next != 0 && next != owned)
            {
                destroy(next);
            }
            return false;
        }
        if (owned != 0 && owned != next)
        {
            destroy(owned);
        }
        owned = next;
        applied = nextApplied;
        tip = tooltip;
        return true;
    }

    public void Dispose()
    {
        if (owned != 0)
        {
            destroy(owned);
            owned = 0;
        }
        applied = null;
        tip = null;
    }
}
