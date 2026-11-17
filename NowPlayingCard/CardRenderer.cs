using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace NowPlayingCard;

/// <summary>Draws the 248x170 now-playing card.</summary>
public static class CardRenderer
{
    public static Color Accent { get; set; } = Color.FromArgb(0x1D, 0xB9, 0x54);

    public static Bitmap Render(NowPlaying np)
    {
        const int W = ScreenFiles.Width, H = ScreenFiles.Height;
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var cover = LoadCover(np.Cover);

        // Background: heavily blurred, darkened cover (or near-black).
        g.Clear(Color.FromArgb(18, 18, 18));
        if (cover != null)
        {
            using var tiny = new Bitmap(8, 6);
            using (var tg = Graphics.FromImage(tiny))
            {
                tg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                tg.DrawImage(cover, new Rectangle(0, 0, 8, 6));
            }
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(tiny, new Rectangle(-20, -20, W + 40, H + 40));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var scrim = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
            g.FillRectangle(scrim, 0, 0, W, H);
        }

        // Cover art, left.
        const int pad = 10, coverSize = 120;
        var coverRect = new Rectangle(pad, (H - 8 - coverSize) / 2, coverSize, coverSize);
        using (var path = RoundRect(coverRect, 6))
        {
            if (cover != null)
            {
                g.SetClip(path);
                g.DrawImage(cover, coverRect);
                if (!np.IsPlaying)
                {
                    using var dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
                    g.FillRectangle(dim, coverRect);
                }
                g.ResetClip();
            }
            else
            {
                using var ph = new SolidBrush(Color.FromArgb(40, 40, 40));
                g.FillPath(ph, path);
                using var disc = new SolidBrush(Color.FromArgb(70, 70, 70));
                g.FillEllipse(disc, coverRect.X + 28, coverRect.Y + 28, 44, 44);
            }
        }

        // Right column.
        int x = coverRect.Right + 10, colW = W - x - pad;
        int y = coverRect.Y;

        // Status pill
        using (var accent = new SolidBrush(Accent))
        using (var fStatus = new Font("Segoe UI", 7f, FontStyle.Bold, GraphicsUnit.Point))
        {
            g.FillEllipse(accent, x, y + 1, 12, 12);
            using var black = new SolidBrush(Color.Black);
            if (np.IsPlaying)
                g.FillPolygon(black, [new PointF(x + 4.5f, y + 4f), new PointF(x + 4.5f, y + 10f), new PointF(x + 9.5f, y + 7f)]);
            else
            {
                g.FillRectangle(black, x + 3.5f, y + 4f, 2f, 6f);
                g.FillRectangle(black, x + 6.5f, y + 4f, 2f, 6f);
            }
            g.DrawString(np.IsPlaying ? "NOW PLAYING" : "PAUSED", fStatus, accent, x + 15, y);
        }
        y += 20;

        // Title (up to 3 lines), artist (up to 2 lines)
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        using var faint = new SolidBrush(Color.FromArgb(130, 255, 255, 255));
        using var fTitle = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold, GraphicsUnit.Point);
        using var fArtist = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        using var fAlbum = new Font("Segoe UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);

        y += DrawWrapped(g, np.Title, fTitle, white, x, y, colW, 3);
        y += 2;
        y += DrawWrapped(g, np.Artist, fArtist, muted, x, y, colW, 2);
        y += 3;

        // Album, in whatever room is left beside the cover.
        int albumLines = (coverRect.Bottom - y) / (int)Math.Ceiling(fAlbum.GetHeight(g));
        if (!string.IsNullOrWhiteSpace(np.Album) && albumLines > 0)
            DrawWrapped(g, np.Album, fAlbum, faint, x, y, colW, Math.Min(2, albumLines));

        // Accent line at the very bottom.
        using (var accent = new SolidBrush(np.IsPlaying ? Accent : Color.FromArgb(90, 90, 90)))
            g.FillRectangle(accent, pad, H - 7, W - pad * 2, 2);

        return bmp;
    }

    /// <summary>Draws text word-wrapped to maxLines (ellipsis on overflow). Returns height used.</summary>
    private static int DrawWrapped(Graphics g, string text, Font f, Brush b, int x, int y, int w, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int lineH = (int)Math.Ceiling(f.GetHeight(g));
        var fmt = new StringFormat(StringFormatFlags.LineLimit) { Trimming = StringTrimming.EllipsisWord };
        var measured = g.MeasureString(text, f, new SizeF(w, lineH * maxLines), fmt);
        int h = Math.Min(lineH * maxLines, (int)Math.Ceiling(measured.Height));
        g.DrawString(text, f, b, new RectangleF(x, y, w, lineH * maxLines), fmt);
        return h;
    }

    private static Bitmap? LoadCover(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            // Spotify thumbnails can include letterboxing; crop to the centered square.
            int side = Math.Min(img.Width, img.Height);
            var sq = new Bitmap(side, side);
            using var g = Graphics.FromImage(sq);
            g.DrawImage(img, new Rectangle(0, 0, side, side), new Rectangle((img.Width - side) / 2, (img.Height - side) / 2, side, side), GraphicsUnit.Pixel);
            return sq;
        }
        catch { return null; }
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Returns the bitmap's pixels as tightly packed RGBA.</summary>
    public static byte[] ToRgba(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bgra = new byte[bmp.Width * bmp.Height * 4];
            for (int row = 0; row < bmp.Height; row++)
                Marshal.Copy(data.Scan0 + row * data.Stride, bgra, row * bmp.Width * 4, bmp.Width * 4);
            for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]); // BGRA -> RGBA
            return bgra;
        }
        finally { bmp.UnlockBits(data); }
    }
}
