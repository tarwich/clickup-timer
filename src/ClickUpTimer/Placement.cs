namespace ClickUpTimer;

// All geometry is in physical screen pixels, including monitors with negative origins.
public readonly record struct Box(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public bool Intersects(Box b) => Left < b.Right && Right > b.Left && Top < b.Bottom && Bottom > b.Top;
}

public static class Placement
{
    public static Box Floating(Box work, double scale, double horizontal, double vertical, double logicalWidth = 336, double logicalHeight = 40)
    {
        var width = Math.Min(work.Width, (int)Math.Ceiling(logicalWidth * scale));
        var height = Math.Min(work.Height, (int)Math.Ceiling(logicalHeight * scale));
        var x = work.Left + (int)Math.Round(Math.Max(0, work.Width - width) * Math.Clamp(horizontal, 0, 1));
        var y = work.Top + (int)Math.Round(Math.Max(0, work.Height - height) * Math.Clamp(vertical, 0, 1));
        return new(x, y, x + width, y + height);
    }
    public static Box? Find(Box bar, IReadOnlyList<Box> occupied, double scale, int? preferredX = null, double logicalWidth = 336, double logicalHeight = 40)
    {
        if (scale <= 0 || bar.Width <= bar.Height || bar.Height < 24 * scale) return null;
        var gap = (int)Math.Ceiling(6 * scale);
        var width = (int)Math.Ceiling(logicalWidth * scale);
        var height = Math.Min((int)Math.Ceiling(logicalHeight * scale), bar.Height - 4);
        var intervals = occupied.Where(b => b.Intersects(bar))
            .Select(b => (Left: Math.Max(bar.Left, b.Left - gap), Right: Math.Min(bar.Right, b.Right + gap)))
            .OrderBy(b => b.Left).ToList();
        var candidates = new List<Box>();
        var left = bar.Left + gap;
        foreach (var interval in intervals.Append((Left: bar.Right - gap, Right: bar.Right)))
        {
            if (interval.Left - left >= width)
            {
                var x = Math.Clamp(preferredX ?? left, left, interval.Left - width);
                var y = bar.Top + (bar.Height - height) / 2;
                candidates.Add(new Box(x, y, x + width, y + height));
            }
            left = Math.Max(left, interval.Right);
        }
        return candidates.OrderBy(b => preferredX is int x ? Math.Abs((long)b.Left - x) : b.Left).Cast<Box?>().FirstOrDefault();
    }
}
