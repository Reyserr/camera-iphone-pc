using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CameraPC.Media;

/// <summary>Convertit une image en pixels BGR 24 bits (de haut en bas) à une taille fixe, avec bandes noires si besoin.</summary>
public sealed class FrameConverter
{
    private byte[]? _source;
    private int[]? _columns;

    public void ToBgr24(BitmapSource image, byte[] destination, int width, int height)
    {
        BitmapSource bgr = image.Format == PixelFormats.Bgr24
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        int sw = bgr.PixelWidth, sh = bgr.PixelHeight, sourceStride = sw * 3;

        if (sw == width && sh == height)
        {
            bgr.CopyPixels(destination, width * 3, 0);
            return;
        }

        int needed = sourceStride * sh;
        if (_source == null || _source.Length < needed) _source = new byte[needed];
        bgr.CopyPixels(_source, sourceStride, 0);

        double scale = Math.Min((double)width / sw, (double)height / sh);
        int targetW = Math.Max(1, Math.Min(width, (int)Math.Round(sw * scale)));
        int targetH = Math.Max(1, Math.Min(height, (int)Math.Round(sh * scale)));
        int offsetX = (width - targetW) / 2, offsetY = (height - targetH) / 2;

        if (_columns == null || _columns.Length != targetW) _columns = new int[targetW];
        for (int x = 0; x < targetW; x++) _columns[x] = Math.Min(sw - 1, (int)(x / scale)) * 3;

        Array.Clear(destination);
        var src = _source;
        var cols = _columns;
        for (int y = 0; y < targetH; y++)
        {
            int sourceRow = Math.Min(sh - 1, (int)(y / scale)) * sourceStride;
            int d = ((offsetY + y) * width + offsetX) * 3;
            for (int x = 0; x < targetW; x++, d += 3)
            {
                int s = sourceRow + cols[x];
                destination[d] = src[s];
                destination[d + 1] = src[s + 1];
                destination[d + 2] = src[s + 2];
            }
        }
    }
}
