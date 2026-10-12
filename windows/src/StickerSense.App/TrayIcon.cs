using System.Drawing;
using System.Drawing.Drawing2D;
using StickerSense.App.Platform;
using Forms = System.Windows.Forms;

namespace StickerSense.App;

/// <summary>
/// The notification-area icon. Enter or a click opens the main window; the Applications key or a
/// right click opens the menu (Windows+B, then arrow to StickerSense), rebuilt each time it opens.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon image;

    /// <param name="activate">Enter or a click on the icon: the main window. The Applications key or a right click opens the menu.</param>
    public TrayIcon(Action<Forms.ToolStripItemCollection> populate, Action activate)
    {
        menu = new Forms.ContextMenuStrip { AccessibleName = "StickerSense", ShowImageMargin = false };
        menu.Opening += (_, e) =>
        {
            menu.Items.Clear();
            populate(menu.Items);
            e.Cancel = menu.Items.Count == 0;
        };
        populate(menu.Items);
        image = AppIcon.TrayIcon() ?? CreateIcon();
        icon = new Forms.NotifyIcon
        {
            Text = "StickerSense",
            Icon = image,
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Enter on the icon (Windows+B, then arrow to it) and a left click open the main window.
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) activate();
        };
    }

    public static Forms.ToolStripMenuItem Item(string text, Action action, bool isChecked = false, bool enabled = true)
    {
        // The accessible name comes from the text, and screen readers announce "checked" themselves.
        var item = new Forms.ToolStripMenuItem(text) { Checked = isChecked, Enabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Fallback when the icon resource can't be read: a small smiling face drawn in code.</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var face = new SolidBrush(Color.FromArgb(255, 210, 63));
            using var rim = new Pen(Color.FromArgb(160, 100, 0), 2);
            g.FillEllipse(face, 2, 2, 28, 28);
            g.DrawEllipse(rim, 2, 2, 28, 28);
            using var ink = new SolidBrush(Color.FromArgb(90, 50, 0));
            g.FillEllipse(ink, 10, 10, 4, 6);
            g.FillEllipse(ink, 18, 10, 4, 6);
            using var smile = new Pen(Color.FromArgb(90, 50, 0), 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(smile, 9, 12, 14, 11, 20, 140);
        }
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        image.Dispose();
    }
}
