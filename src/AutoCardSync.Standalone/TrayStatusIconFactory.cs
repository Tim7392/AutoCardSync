using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AutoCardSync.Standalone;

internal enum TrayVisualState
{
    Waiting,
    Processing,
    Completed,
    Attention,
}

/// <summary>
/// Creates small, state-specific tray images without retaining a native icon handle.
/// The caller owns and disposes the returned <see cref="Icon"/> when replacing it.
/// </summary>
internal static class TrayStatusIconFactory
{
    private const int IconSize = 32;

    public static Icon Create(TrayVisualState state)
    {
        using var bitmap = new Bitmap(IconSize, IconSize);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            DrawApplicationMark(graphics);
            DrawStatusBadge(graphics, state);
        }

        IntPtr iconHandle = bitmap.GetHicon();
        try
        {
            using Icon temporaryIcon = Icon.FromHandle(iconHandle);
            return (Icon)temporaryIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(iconHandle);
        }
    }

    private static void DrawApplicationMark(Graphics graphics)
    {
        using var background = new SolidBrush(Color.FromArgb(31, 119, 231));
        graphics.FillEllipse(background, 1, 1, 27, 27);

        using var font = new Font(FontFamily.GenericSansSerif, 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.White);
        var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        try
        {
            graphics.DrawString("A", font, text, new RectangleF(1, 0, 27, 28), format);
        }
        finally
        {
            format.Dispose();
        }
    }

    private static void DrawStatusBadge(Graphics graphics, TrayVisualState state)
    {
        Color color = state switch
        {
            TrayVisualState.Waiting => Color.FromArgb(109, 122, 140),
            TrayVisualState.Processing => Color.FromArgb(21, 131, 255),
            TrayVisualState.Completed => Color.FromArgb(44, 166, 97),
            TrayVisualState.Attention => Color.FromArgb(235, 132, 54),
            _ => Color.FromArgb(109, 122, 140),
        };
        using var fill = new SolidBrush(color);
        using var outline = new Pen(Color.White, 2);
        graphics.FillEllipse(fill, 17, 17, 14, 14);
        graphics.DrawEllipse(outline, 17, 17, 14, 14);

        using var symbol = new Pen(Color.White, 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        switch (state)
        {
            case TrayVisualState.Waiting:
                graphics.DrawEllipse(symbol, 23, 23, 1, 1);
                graphics.DrawEllipse(symbol, 26, 23, 1, 1);
                break;
            case TrayVisualState.Processing:
                graphics.DrawArc(symbol, 20, 20, 8, 8, 35, 250);
                graphics.DrawLine(symbol, 27, 20, 28, 23);
                graphics.DrawLine(symbol, 27, 20, 24, 20);
                break;
            case TrayVisualState.Completed:
                graphics.DrawLine(symbol, 20.5f, 24, 23.4f, 26.8f);
                graphics.DrawLine(symbol, 23.4f, 26.8f, 28.2f, 21.5f);
                break;
            case TrayVisualState.Attention:
                graphics.DrawLine(symbol, 24, 20.8f, 24, 25.1f);
                graphics.DrawEllipse(symbol, 23.2f, 27, 1.6f, 1.6f);
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
