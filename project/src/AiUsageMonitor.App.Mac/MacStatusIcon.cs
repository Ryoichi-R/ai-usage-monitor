using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AiUsageMonitor.App.Mac;

/// <summary>メニューバー用のアイコン（18pt、2倍解像度）。利用量の横棒2本を図案化した単色画像。</summary>
internal static class MacStatusIcon
{
    public static WindowIcon Create()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(144, 144));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            // 144 dpiのため描画座標は1/2の18×18。外枠と、長さの異なる2本のバー。
            var pen = new Pen(Brushes.White, 1.4);
            context.DrawRectangle(null, pen, new Rect(2, 3, 14, 12), 2.5, 2.5);
            context.DrawRectangle(Brushes.White, null, new Rect(4.5, 6, 9, 2.2), 1, 1);
            context.DrawRectangle(Brushes.White, null, new Rect(4.5, 10, 5.5, 2.2), 1, 1);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
