using System.Drawing.Drawing2D;

namespace FF6PakEditor.UI;

/// <summary>Small file-type icons for the archive tree, drawn at the current DPI.</summary>
static class Icons
{
    public static ImageList Create(int size)
    {
        var list = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(size, size) };
        list.Images.Add("archive", Draw(size, Archive));
        list.Images.Add("folder", Draw(size, Folder));
        list.Images.Add("lua", Draw(size, Lua));
        list.Images.Add("text", Draw(size, (g, s) => Page(g, s, Color.FromArgb(200, 200, 200), lines: true)));
        list.Images.Add("file", Draw(size, (g, s) => Page(g, s, Color.FromArgb(140, 140, 140), lines: false)));
        list.Images.Add("audio", Draw(size, Audio));
        list.Images.Add("model", Draw(size, Model));
        list.Images.Add("image", Draw(size, Picture));
        return list;
    }

    public static string KeyFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".luac" or ".lua" => "lua",
        ".wavebatch" or ".wav" or ".ogg" or ".xma" or ".mp3" => "audio",
        ".bdae" or ".bullet" or ".hzc" or ".p" or ".x" => "model",
        ".brres" or ".dds" or ".png" or ".tga" or ".jpg" or ".bmp" => "image",
        ".txt" or ".xml" or ".ini" or ".cfg" or ".json" or ".csv" or ".str" => "text",
        _ => "file",
    };

    static Bitmap Draw(int size, Action<Graphics, float> paint)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 16f, size / 16f);
        paint(g, 16f);
        return bmp;
    }

    static void Folder(Graphics g, float s)
    {
        using var back = new SolidBrush(Color.FromArgb(190, 150, 80));
        using var front = new SolidBrush(Color.FromArgb(220, 182, 110));
        g.FillRectangle(back, 1.5f, 3f, 6f, 3f);
        g.FillRectangle(back, 1.5f, 4.5f, 13f, 9f);
        g.FillRectangle(front, 1.5f, 6.5f, 13f, 7f);
    }

    static void Archive(Graphics g, float s)
    {
        using var box = new SolidBrush(Color.FromArgb(70, 130, 200));
        using var lid = new SolidBrush(Color.FromArgb(110, 165, 230));
        using var strap = new SolidBrush(Color.FromArgb(230, 230, 230));
        g.FillRectangle(box, 2f, 5f, 12f, 9f);
        g.FillRectangle(lid, 1.5f, 2.5f, 13f, 3.5f);
        g.FillRectangle(strap, 7f, 2.5f, 2f, 11.5f);
    }

    static void Lua(Graphics g, float s)
    {
        using var planet = new SolidBrush(Color.FromArgb(40, 90, 200));
        using var hole = new SolidBrush(Color.White);
        using var moon = new SolidBrush(Color.FromArgb(40, 90, 200));
        g.FillEllipse(planet, 1f, 3f, 12f, 12f);
        g.FillEllipse(hole, 7f, 5f, 3.5f, 3.5f);
        g.FillEllipse(moon, 11.5f, 0.5f, 4f, 4f);
    }

    static void Page(Graphics g, float s, Color color, bool lines)
    {
        using var pen = new Pen(color, 1.2f);
        var path = new GraphicsPath();
        path.AddLines([new PointF(3, 1.5f), new PointF(10, 1.5f), new PointF(13, 4.5f), new PointF(13, 14.5f), new PointF(3, 14.5f)]);
        path.CloseFigure();
        g.DrawPath(pen, path);
        g.DrawLines(pen, [new PointF(10, 1.5f), new PointF(10, 4.5f), new PointF(13, 4.5f)]);
        if (!lines) return;
        using var thin = new Pen(color, 1f);
        for (float y = 7f; y <= 12f; y += 2.5f) g.DrawLine(thin, 5f, y, 11f, y);
    }

    static void Audio(Graphics g, float s)
    {
        using var brush = new SolidBrush(Color.FromArgb(200, 120, 200));
        using var pen = new Pen(Color.FromArgb(200, 120, 200), 1.6f);
        g.FillEllipse(brush, 2.5f, 10f, 4.5f, 4f);
        g.FillEllipse(brush, 9f, 8.5f, 4.5f, 4f);
        g.DrawLine(pen, 6.6f, 12f, 6.6f, 3f);
        g.DrawLine(pen, 13.1f, 10.5f, 13.1f, 1.5f);
        g.DrawLine(pen, 6.6f, 3f, 13.1f, 1.5f);
    }

    static void Model(Graphics g, float s)
    {
        using var top = new SolidBrush(Color.FromArgb(120, 210, 190));
        using var left = new SolidBrush(Color.FromArgb(70, 170, 150));
        using var right = new SolidBrush(Color.FromArgb(50, 130, 115));
        g.FillPolygon(top, [new PointF(8, 1.5f), new PointF(14, 4.5f), new PointF(8, 7.5f), new PointF(2, 4.5f)]);
        g.FillPolygon(left, [new PointF(2, 4.5f), new PointF(8, 7.5f), new PointF(8, 14.5f), new PointF(2, 11.5f)]);
        g.FillPolygon(right, [new PointF(14, 4.5f), new PointF(8, 7.5f), new PointF(8, 14.5f), new PointF(14, 11.5f)]);
    }

    static void Picture(Graphics g, float s)
    {
        using var frame = new Pen(Color.FromArgb(170, 170, 170), 1.2f);
        using var sky = new SolidBrush(Color.FromArgb(90, 150, 210));
        using var hill = new SolidBrush(Color.FromArgb(90, 170, 90));
        g.FillRectangle(sky, 2f, 3f, 12f, 10f);
        g.FillPolygon(hill, [new PointF(2, 13), new PointF(7, 7), new PointF(10, 10), new PointF(12, 8.5f), new PointF(14, 13)]);
        g.DrawRectangle(frame, 2f, 3f, 12f, 10f);
    }
}
