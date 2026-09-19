using NosSmoothCustomClient.Configuration;

namespace NosSmoothCustomClient.Input;

/// <summary>
/// Turns a map coordinate into the minimap pixel that walks there.
/// </summary>
/// <remarks>
/// Recording a waypoint captures the same place twice - where the character stood, and where that
/// place sits on the minimap - so a handful of them describe the whole mapping between the two.
/// That is what lifts the bot off its recorded points: with it, anywhere on the map is somewhere it
/// can be sent, which is what reaching a monster the attack key cannot see requires.
///
/// The fit is only as good as the spread of the points it was built from. Two waypoints one cell
/// apart in X say almost nothing about the X scale, so an axis without real spread is reported as
/// unusable rather than guessed at from noise - a projection that is confidently wrong would send
/// the character somewhere no one asked for.
/// </remarks>
public sealed class MinimapProjection
{
    private const int MinimumSpread = 4;

    private MinimapProjection(double scaleX, double originX, double scaleY, double originY, int spreadX, int spreadY)
    {
        ScaleX = scaleX;
        OriginX = originX;
        ScaleY = scaleY;
        OriginY = originY;
        SpreadX = spreadX;
        SpreadY = spreadY;
    }

    /// <summary>Gets the pixels per cell across.</summary>
    public double ScaleX { get; }

    /// <summary>Gets the pixel of map column zero.</summary>
    public double OriginX { get; }

    /// <summary>Gets the pixels per cell down.</summary>
    public double ScaleY { get; }

    /// <summary>Gets the pixel of map row zero.</summary>
    public double OriginY { get; }

    /// <summary>Gets how many cells apart the widest pair of points is across.</summary>
    public int SpreadX { get; }

    /// <summary>Gets how many cells apart the widest pair of points is down.</summary>
    public int SpreadY { get; }

    /// <summary>Gets a value indicating whether both axes were measured over a real distance.</summary>
    public bool IsWellSpread => SpreadX >= MinimumSpread && SpreadY >= MinimumSpread;

    /// <summary>Gets a sentence describing the fit, for the window and the log.</summary>
    public string Description
        => IsWellSpread
            ? $"{ScaleX:0.00} px/case en X, {ScaleY:0.00} px/case en Y"
            : $"{ScaleX:0.00} x {ScaleY:0.00} px/case, mais mesuré sur {SpreadX} case(s) en X et "
              + $"{SpreadY} en Y - enregistre un waypoint plus éloigné pour affiner";

    /// <summary>
    /// Builds a projection from the waypoints that carry a minimap point.
    /// </summary>
    /// <param name="waypoints">The route.</param>
    /// <returns>The projection, or null when the points cannot describe one.</returns>
    public static MinimapProjection? Build(IReadOnlyList<Waypoint> waypoints)
    {
        var points = waypoints
            .Where(w => w.ClickX is not null && w.ClickY is not null)
            .Select(w => (MapX: w.X, MapY: w.Y, PixelX: (double)w.ClickX!.Value, PixelY: (double)w.ClickY!.Value))
            .ToList();

        if (points.Count < 2)
        {
            return null;
        }

        var spreadX = points.Max(p => p.MapX) - points.Min(p => p.MapX);
        var spreadY = points.Max(p => p.MapY) - points.Min(p => p.MapY);

        var fitX = Fit(points.Select(p => ((double)p.MapX, p.PixelX)));
        var fitY = Fit(points.Select(p => ((double)p.MapY, p.PixelY)));

        if (fitX is null && fitY is null)
        {
            return null;
        }

        // A minimap is drawn to one scale, so an axis with no spread borrows the other's rather than
        // being read off noise. Better a scale that is right and an origin that is approximate than
        // a confident number with nothing behind it.
        var (scaleX, originX) = fitX ?? (fitY!.Value.Scale, points[0].PixelX - fitY!.Value.Scale * points[0].MapX);
        var (scaleY, originY) = fitY ?? (fitX!.Value.Scale, points[0].PixelY - fitX!.Value.Scale * points[0].MapY);

        return new MinimapProjection(scaleX, originX, scaleY, originY, spreadX, spreadY);
    }

    /// <summary>
    /// Finds the minimap pixel for a map coordinate.
    /// </summary>
    /// <param name="mapX">The map column.</param>
    /// <param name="mapY">The map row.</param>
    /// <returns>The pixel inside the game window.</returns>
    public (int X, int Y) Project(int mapX, int mapY)
        => ((int)Math.Round(OriginX + ScaleX * mapX), (int)Math.Round(OriginY + ScaleY * mapY));

    private static (double Scale, double Origin)? Fit(IEnumerable<(double Map, double Pixel)> pairs)
    {
        var points = pairs.ToList();
        var n = points.Count;
        var sumMap = points.Sum(p => p.Map);
        var sumPixel = points.Sum(p => p.Pixel);
        var sumMapMap = points.Sum(p => p.Map * p.Map);
        var sumMapPixel = points.Sum(p => p.Map * p.Pixel);

        var denominator = n * sumMapMap - sumMap * sumMap;
        if (Math.Abs(denominator) < 1e-9)
        {
            return null;
        }

        var scale = (n * sumMapPixel - sumMap * sumPixel) / denominator;
        return (scale, (sumPixel - scale * sumMap) / n);
    }
}
