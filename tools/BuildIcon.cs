using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

// Renders our small SVG vocabulary with WPF; no downloads or external graphics tools.
internal static class BuildIcon
{
    private static readonly Dictionary<string, Brush> brushes = new Dictionary<string, Brush>();
    private static double Number(XElement element, string attribute, double fallback)
    {
        XAttribute value = element.Attribute(attribute);
        return value == null ? fallback : double.Parse(value.Value, CultureInfo.InvariantCulture);
    }
    private static Color Color(string value) { return (Color)ColorConverter.ConvertFromString(value); }
    private static Brush Paint(string value)
    {
        if (value == null || value == "none") return null;
        if (value.StartsWith("url(#")) return brushes[value.Substring(5, value.Length - 6)];
        return new SolidColorBrush(Color(value));
    }
    private static RenderTargetBitmap Render(XElement svg, int size)
    {
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            foreach (XElement element in svg.Elements())
            {
                if (Number(element, "data-min-size", 0) > size) continue;
                string tag = element.Name.LocalName;
                if (tag != "rect" && tag != "path" && tag != "circle") continue;
                drawing.PushOpacity(Number(element, "opacity", 1));
                string transform = (string)element.Attribute("transform");
                if (transform != null)
                {
                    string[] parts = transform.Replace("translate(", "").Replace(")", "").Split(' ');
                    drawing.PushTransform(new TranslateTransform(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture)));
                }
                Brush fill = Paint((string)element.Attribute("fill"));
                Brush stroke = Paint((string)element.Attribute("stroke"));
                Pen pen = stroke == null ? null : new Pen(stroke, Number(element, "stroke-width", 1));
                if (pen != null && (string)element.Attribute("stroke-linecap") == "round")
                { pen.StartLineCap = pen.EndLineCap = PenLineCap.Round; pen.LineJoin = PenLineJoin.Round; }
                if (tag == "rect") drawing.DrawRoundedRectangle(fill, pen,
                    new Rect(Number(element, "x", 0), Number(element, "y", 0), Number(element, "width", 0), Number(element, "height", 0)),
                    Number(element, "rx", 0), Number(element, "rx", 0));
                else if (tag == "circle") drawing.DrawEllipse(fill, pen,
                    new Point(Number(element, "cx", 0), Number(element, "cy", 0)), Number(element, "r", 0), Number(element, "r", 0));
                else drawing.DrawGeometry(fill, pen, Geometry.Parse((string)element.Attribute("d")));
                if (transform != null) drawing.Pop();
                drawing.Pop();
            }
            drawing.Pop();
        }
        RenderTargetBitmap image = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        return image;
    }
    private static byte[] Png(BitmapSource image)
    {
        PngBitmapEncoder encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (MemoryStream stream = new MemoryStream()) { encoder.Save(stream); return stream.ToArray(); }
    }
    private static byte[] Dib(BitmapSource image)
    {
        int size = image.PixelWidth;
        byte[] pixels = new byte[size * size * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, size * 4, 0);
        int maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(40); writer.Write(size); writer.Write(size * 2);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(0);
            writer.Write(pixels.Length + maskStride * size);
            writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int row = size - 1; row >= 0; row--) writer.Write(pixels, row * size * 4, size * 4);
            for (int row = size - 1; row >= 0; row--)
            {
                byte[] mask = new byte[maskStride];
                for (int col = 0; col < size; col++)
                    if (pixels[(row * size + col) * 4 + 3] == 0) mask[col / 8] |= (byte)(0x80 >> (col % 8));
                writer.Write(mask);
            }
            return stream.ToArray();
        }
    }
    [STAThread]
    private static void Main(string[] args)
    {
        XElement svg = XDocument.Load(args[0]).Root;
        foreach (XElement gradient in svg.Descendants())
        {
            if (gradient.Name.LocalName != "linearGradient") continue;
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(Number(gradient, "x1", 0), Number(gradient, "y1", 0));
            brush.EndPoint = new Point(Number(gradient, "x2", 0), Number(gradient, "y2", 1));
            foreach (XElement stop in gradient.Elements())
                brush.GradientStops.Add(new GradientStop(Color((string)stop.Attribute("stop-color")), Number(stop, "offset", 0)));
            brush.Freeze(); brushes[(string)gradient.Attribute("id")] = brush;
        }
        string directory = args[1];
        Directory.CreateDirectory(directory);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        foreach (int size in sizes)
        {
            RenderTargetBitmap image = Render(svg, size);
            frames.Add(size == 256 ? Png(image) : Dib(image));
        }
        using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(directory, "rumble.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
        File.WriteAllBytes(Path.Combine(directory, "rumble.png"), Png(Render(svg, 512)));
        // A native-size contact sheet makes the small-icon version reviewable.
        DrawingVisual preview = new DrawingVisual();
        using (DrawingContext drawing = preview.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color("#ECEEE9")), null, new Rect(0, 0, 640, 210));
            drawing.DrawRectangle(new SolidColorBrush(Color("#22252A")), null, new Rect(0, 210, 640, 210));
            int[] previewSizes = { 16, 24, 32, 48, 64, 128 };
            for (int row = 0; row < 2; row++)
            {
                double x = 38;
                foreach (int size in previewSizes)
                {
                    drawing.DrawImage(Render(svg, size), new Rect(x, row * 210 + 100 - size / 2.0, size, size));
                    FormattedText label = new FormattedText(size.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI"), 12, new SolidColorBrush(Color(row == 0 ? "#5B6258" : "#9399A1")), 1.0);
                    drawing.DrawText(label, new Point(x + size / 2.0 - label.Width / 2, row * 210 + 177));
                    x += size + 38;
                }
            }
        }
        RenderTargetBitmap sheet = new RenderTargetBitmap(640, 420, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(preview);
        File.WriteAllBytes(Path.Combine(directory, "icon-sizes.png"), Png(sheet));
        Console.WriteLine("Icon built: " + string.Join(", ", sizes) + " px");
    }
}
