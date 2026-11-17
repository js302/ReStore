using System.Text.Json.Serialization;

namespace ReStore.Services
{
    /// <summary>A rectangle in device-independent units. WPF-free so the placement rules can be unit-tested.</summary>
    public readonly record struct PlacementRect(double Left, double Top, double Width, double Height)
    {
        [JsonIgnore] public double Right => Left + Width;
        [JsonIgnore] public double Bottom => Top + Height;

        public bool IsFinite => double.IsFinite(Left) && double.IsFinite(Top) && double.IsFinite(Width) && double.IsFinite(Height);
    }

    /// <summary>The main window's last placement as stored in appsettings.json.</summary>
    public sealed class WindowPlacementSettings
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public bool Maximized { get; set; }

        [JsonIgnore]
        public PlacementRect Bounds => new(Left, Top, Width, Height);
    }

    /// <summary>Sizing and "is this placement still on a connected screen" rules for the main window.</summary>
    public static class WindowPlacementPolicy
    {
        public const double PreferredMinWidth = 1100;
        public const double PreferredMinHeight = 700;
        public const double DefaultFraction = 0.8;

        // A restored window must leave at least this much of its title bar on a screen so it can be dragged back.
        private const double TitleBarHeight = 32;
        private const double MinVisibleWidth = 100;
        private const double MinVisibleHeight = 16;

        /// <summary>
        /// 1100x700, except that it never exceeds the work area (a 1366x768 laptop at 150% scaling only has
        /// 910x512 usable units, and a window that cannot fit is the bug this exists to prevent).
        /// </summary>
        public static (double Width, double Height) MinimumSize(PlacementRect workArea)
            => (Math.Min(PreferredMinWidth, workArea.Width), Math.Min(PreferredMinHeight, workArea.Height));

        /// <summary>80% of the work area, centred, never below <see cref="MinimumSize"/>.</summary>
        public static PlacementRect DefaultBounds(PlacementRect workArea)
        {
            var (minW, minH) = MinimumSize(workArea);
            var width = Math.Max(minW, workArea.Width * DefaultFraction);
            var height = Math.Max(minH, workArea.Height * DefaultFraction);
            return new PlacementRect(
                workArea.Left + (workArea.Width - width) / 2,
                workArea.Top + (workArea.Height - height) / 2,
                width,
                height);
        }

        /// <summary>True when the window's title bar overlaps some connected screen's work area enough to grab it.</summary>
        public static bool IsVisible(PlacementRect bounds, IReadOnlyList<PlacementRect> workAreas)
        {
            if (!bounds.IsFinite || bounds.Width <= 0 || bounds.Height <= 0)
            {
                return false;
            }

            var titleBottom = bounds.Top + Math.Min(TitleBarHeight, bounds.Height);
            foreach (var area in workAreas)
            {
                var overlapWidth = Math.Min(bounds.Right, area.Right) - Math.Max(bounds.Left, area.Left);
                var overlapHeight = Math.Min(titleBottom, area.Bottom) - Math.Max(bounds.Top, area.Top);
                if (overlapWidth >= MinVisibleWidth && overlapHeight >= MinVisibleHeight)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The bounds to open with: the saved ones if they are on a connected screen (sized no smaller than
        /// the minimum), otherwise the default for <paramref name="primaryWorkArea"/>. The flag says which.
        /// </summary>
        public static (PlacementRect Bounds, bool FromSaved) Resolve(
            PlacementRect? saved,
            IReadOnlyList<PlacementRect> workAreas,
            PlacementRect primaryWorkArea)
        {
            if (saved is { } s && IsVisible(s, workAreas))
            {
                var (minW, minH) = MinimumSize(primaryWorkArea);
                return (s with { Width = Math.Max(s.Width, minW), Height = Math.Max(s.Height, minH) }, true);
            }

            return (DefaultBounds(primaryWorkArea), false);
        }
    }
}
