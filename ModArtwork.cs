using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace OrbusRebornManager;

/// <summary>
/// Original, self-contained vector artwork for RM mod identities.
/// Rendered by WPF at any DPI; no font glyphs, external image downloads,
/// or changes to mod IDs / release matching.
/// Unknown community mods receive an honest generic rune fallback.
/// </summary>
public static class ModArtwork
{
    private static readonly Dictionary<string, ImageSource> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource For(string? modId)
    {
        string key = string.IsNullOrWhiteSpace(modId) ? "default" : modId.Trim();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out ImageSource? ready)) return ready;
            ImageSource result = Create(key);
            Cache[key] = result;
            return result;
        }
    }

    private static Color Colour(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush(Colour(hex));
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(string colour, double thickness)
    {
        var pen = new Pen(Brush(colour), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static void Outline(DrawingContext ctx, string path,
        string colour = "#F3D99D", double width = 2.5)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        // Deep offset outline preserves legibility at 32–44 logical pixels.
        ctx.DrawGeometry(null, Pen("#201E27", width + 2.3), geometry);
        ctx.DrawGeometry(null, Pen(colour, width), geometry);
    }

    private static void Shape(DrawingContext ctx, string path,
        string fill, string stroke = "#302735", double width = 1.6)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        ctx.DrawGeometry(Brush(fill), Pen(stroke, width), geometry);
    }

    private static void Dot(DrawingContext ctx, double x, double y,
        double radius, string colour)
    {
        ctx.DrawEllipse(Brush(colour), null, new Point(x, y), radius, radius);
    }

    private static LinearGradientBrush Gradient(string dark, string light)
    {
        var brush = new LinearGradientBrush(
            Colour(light), Colour(dark), new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    private static ImageSource Create(string id)
    {
        string dark, light, highlight;
        switch (id.ToLowerInvariant())
        {
            case "orbusreborncamera":
                dark = "#243E3B"; light = "#397C74"; highlight = "#9EF4D5"; break;
            case "orbusquesthelper":
                dark = "#53412D"; light = "#A27640"; highlight = "#FFE2A0"; break;
            case "castmetrics":
                dark = "#292F55"; light = "#5559A3"; highlight = "#C9B8FF"; break;
            case "orbusgoblintimer":
                dark = "#304629"; light = "#66874A"; highlight = "#C5F397"; break;
            case "orbusabilitylabels":
                dark = "#3C315A"; light = "#7F5798"; highlight = "#F1BBF9"; break;
            case "orbusrebornmenu":
                dark = "#533C31"; light = "#9A6548"; highlight = "#FFE5B0"; break;
            case "orbusrebornautomount":
                dark = "#3D463E"; light = "#648777"; highlight = "#D0EBC7"; break;
            default:
                dark = "#343944"; light = "#4C586B"; highlight = "#C7D4E8"; break;
        }

        var group = new DrawingGroup();
        using (DrawingContext ctx = group.Open())
        {
            // The same small embossed wood-and-metal frame ties all icons to
            // the Orbus visual language while preserving a different motif
            // and colour palette for each mod.
            ctx.DrawRoundedRectangle(Brush("#161920"), Pen("#635447", 1),
                new Rect(1, 1, 62, 62), 13, 13);
            ctx.DrawRoundedRectangle(Gradient(dark, light), Pen("#80694C", 1.6),
                new Rect(3.5, 3.5, 57, 57), 11, 11);
            ctx.DrawRoundedRectangle(null, Pen("#FFFFFF26", 1),
                new Rect(6.5, 6.5, 51, 51), 8.5, 8.5);

            ctx.DrawGeometry(Brush("#180D1B20"), null,
                Geometry.Parse("M 7,40 L 7,52 Q 8,56 14,57 L 50,57 Q 56,56 57,50 L 57,34 Q 38,51 7,40 Z"));

            // Corner rivets / rune plate detailing.
            Dot(ctx, 10.5, 11, 1.4, "#BC9863");
            Dot(ctx, 53.5, 11, 1.4, "#BC9863");
            Dot(ctx, 10.5, 53, 1.4, "#BC9863");
            Dot(ctx, 53.5, 53, 1.4, "#BC9863");

            switch (id.ToLowerInvariant())
            {
                case "orbusreborncamera":
                    // Lens-equipped creator camera, not the letter R.
                    Shape(ctx, "M 14,24 L 23,24 L 27,19 L 38,19 L 42,24 L 50,24 Q 53,24 53,28 L 53,45 Q 53,48 49,48 L 15,48 Q 11,48 11,44 L 11,28 Q 11,24 14,24 Z", "#1D3E43", "#C5F2D8", 1.9);
                    ctx.DrawEllipse(Brush("#17353D"), Pen("#B8EACC", 2.1), new Point(32, 35.5), 11.2, 11.2);
                    ctx.DrawEllipse(Gradient("#376D91", "#8EF7D4"), Pen("#142B33", 1.9), new Point(32, 35.5), 6.8, 6.8);
                    Dot(ctx, 29, 32, 2.4, "#E4FFF5");
                    Dot(ctx, 47.5, 29, 1.7, "#9EF4D5");
                    break;

                case "orbusquesthelper":
                    // A folded mission scroll with a bright quest marker.
                    Shape(ctx, "M 20,13 Q 25,15 27,12 L 45,14 Q 48,15 47,19 L 45,44 Q 44,50 38,50 L 18,48 Q 14,47 16,42 L 17,19 Q 17,15 20,13 Z", "#D7BA82", "#55412C", 2);
                    Shape(ctx, "M 18,15 C 8,15 8,27 17,27 L 22,26 L 22,18 Q 21,15 18,15 Z", "#AC8956", "#3B3029", 1.5);
                    Outline(ctx, "M 27,27 L 38,27 M 27,33 L 36,33 M 27,39 L 34,39", "#5F3B29", 2.2);
                    Shape(ctx, "M 42,8 L 45,14 L 51,17 L 45,20 L 42,26 L 39,20 L 33,17 L 39,14 Z", "#F9E5AA", "#74512B", 1.2);
                    break;

                case "castmetrics":
                    // Spell wand over a segmented magic timing ring.
                    ctx.DrawEllipse(null, Pen("#B3B0FF", 2.4), new Point(32, 32), 17.2, 17.2);
                    Outline(ctx, "M 18,45 L 44,19", "#EEE0FF", 3.8);
                    Outline(ctx, "M 43,18 L 47,13 M 46,23 L 53,21 M 38,15 L 39,8", "#EDBEFF", 2.2);
                    Shape(ctx, "M 47,9 L 50,16 L 57,19 L 50,22 L 47,29 L 44,22 L 37,19 L 44,16 Z", "#E2C6FF", "#49417F", 1.5);
                    Dot(ctx, 22.3, 22.4, 2.6, "#D4BDFF");
                    Dot(ctx, 42, 43, 2.6, "#D4BDFF");
                    break;

                case "orbusgoblintimer":
                    // Goblin ears + timepiece; never mistaken for CastMetrics.
                    Shape(ctx, "M 18,26 L 8,20 L 13,37 L 20,41 Z", "#92BC70", "#203321", 1.9);
                    Shape(ctx, "M 46,26 L 56,20 L 51,37 L 44,41 Z", "#92BC70", "#203321", 1.9);
                    ctx.DrawEllipse(Brush("#3C5735"), Pen("#E0CD88", 3.2), new Point(32, 34), 18.0, 18.0);
                    ctx.DrawEllipse(Brush("#B4C68E"), Pen("#385136", 1.5), new Point(32, 34), 12.7, 12.7);
                    Outline(ctx, "M 32,25 L 32,34 L 40,38", "#304B3A", 2.8);
                    Dot(ctx, 32, 34, 2.4, "#F9E5B0");
                    Outline(ctx, "M 24,18 L 22,14 M 40,18 L 43,14", "#F5DF9E", 1.8);
                    break;

                case "orbusabilitylabels":
                    // Three spell-orbs annotated by little banner labels.
                    Outline(ctx, "M 14,20 L 19,20 M 14,33 L 19,33 M 14,46 L 19,46", "#EAD9FC", 2.3);
                    ctx.DrawEllipse(Brush("#A7D6F5"), Pen("#2E3556", 2), new Point(26, 20), 6.2, 6.2);
                    ctx.DrawEllipse(Brush("#F4A7C4"), Pen("#4A254C", 2), new Point(26, 33), 6.2, 6.2);
                    ctx.DrawEllipse(Brush("#C1EBA4"), Pen("#365342", 2), new Point(26, 46), 6.2, 6.2);
                    Shape(ctx, "M 33,14 L 51,14 L 47,26 L 33,26 Z", "#DFBFEF", "#403455", 1.3);
                    Shape(ctx, "M 33,27 L 51,27 L 47,39 L 33,39 Z", "#DFBFEF", "#403455", 1.3);
                    Shape(ctx, "M 33,40 L 51,40 L 47,52 L 33,52 Z", "#DFBFEF", "#403455", 1.3);
                    Outline(ctx, "M 37,20 L 44,20 M 37,33 L 44,33 M 37,46 L 44,46", "#5E4670", 2);
                    break;

                case "orbusrebornmenu":
                    // Orbus-inspired open parchment / mod catalogue.
                    Shape(ctx, "M 10,18 Q 21,15 31,21 L 31,50 Q 21,44 10,47 Z", "#DFBD80", "#5B3B2C", 1.7);
                    Shape(ctx, "M 54,18 Q 43,15 33,21 L 33,50 Q 43,44 54,47 Z", "#EFD29A", "#5B3B2C", 1.7);
                    Outline(ctx, "M 18,27 L 26,29 M 18,34 L 26,36 M 39,35 L 46,33", "#7A4D31", 1.6);
                    Shape(ctx, "M 43,22 L 47,27 L 43,32 L 39,27 Z", "#78DBC1", "#31554B", 1.5);
                    break;

                case "orbusrebornautomount":
                    // Original little horseshoe / mount trail.
                    Outline(ctx, "M 19,18 L 19,34 Q 19,48 32,48 Q 45,48 45,34 L 45,18", "#E8E9C6", 6);
                    Outline(ctx, "M 19,18 L 19,34 Q 19,48 32,48 Q 45,48 45,34 L 45,18", "#5A604D", 2.0);
                    Dot(ctx, 19, 25, 1.8, "#F0D798");
                    Dot(ctx, 45, 25, 1.8, "#F0D798");
                    Outline(ctx, "M 25,18 L 32,11 L 39,18", "#E7C991", 1.9);
                    break;

                default:
                    // Generic community mod: neutral, distinctive plugin rune.
                    Shape(ctx, "M 32,12 L 49,22 L 49,43 L 32,53 L 15,43 L 15,22 Z", "#A2B2C8", "#2F3E54", 2);
                    Outline(ctx, "M 23,31 L 29,37 L 41,25", "#F8E8BD", 3.8);
                    Dot(ctx, 32, 15, 2, highlight);
                    break;
            }
        }
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}
