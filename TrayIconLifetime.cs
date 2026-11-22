namespace Afterbreak;

internal readonly record struct TrayIconDesign(string Glyphs, bool FinalMinute, int Size, uint Background, uint Ink);

internal sealed class TrayIconLifetime(nint logo, Func<TrayIconDesign, nint> draw, Action<nint> destroy) : IDisposable
{
    private nint owned;
    private TrayIconDesign? applied;
    private string? tip;

    internal bool Apply(TrayIconDesign? design, string tooltip, bool add,
        Func<nint, string, bool, bool, bool> publish)
    {
        var iconChanged = applied != design;
        var tipChanged = tip != tooltip;
        if (!add && !iconChanged && !tipChanged)
        {
            return true;
        }

        var next = owned;
        if (iconChanged)
        {
            next = design is { } request ? draw(request) : 0;
            if (design is not null && next == 0)
            {
                // Keep the shell callback and exact tooltip usable if rasterization fails.
                design = applied;
                next = owned;
                iconChanged = false;
            }
        }
        if (!add && !iconChanged && !tipChanged)
        {
            return true;
        }

        if (!publish(next != 0 ? next : logo, tooltip, iconChanged || add, tipChanged || add))
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
        applied = design;
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
