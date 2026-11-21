using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Ostrov.Services;

namespace Ostrov.Tray;

/// <summary>Иконка в трее: клик — показать/скрыть остров, меню — автозапуск и выход.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostartItem;

    public TrayIcon(Action toggleIsland, Action exit)
    {
        var menu = new ContextMenuStrip();

        var toggleItem = new ToolStripMenuItem("Показать / скрыть остров");
        toggleItem.Click += (_, _) => toggleIsland();

        _autostartItem = new ToolStripMenuItem("Запускать вместе с Windows") { CheckOnClick = true };
        _autostartItem.Click += (_, _) => AutostartService.SetEnabled(_autostartItem.Checked);

        var exitItem = new ToolStripMenuItem("Выход");
        exitItem.Click += (_, _) => exit();

        menu.Items.AddRange([toggleItem, _autostartItem, new ToolStripSeparator(), exitItem]);
        TrayStyle.Apply(menu);
        // Галочку берём из реального состояния системы, а не из памяти.
        menu.Opening += (_, _) => _autostartItem.Checked = AutostartService.IsEnabled;

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Остров",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) toggleIsland();
        };
    }

    // Простая иконка, нарисованная кодом: чёрная плашка с белой окантовкой (видна и на тёмной, и на светлой панели задач).
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            const int x = 2, y = 9, w = 28, h = 14, d = 14;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + w - d, y, d, d, 270, 90);
            path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            path.AddArc(x, y + h - d, d, d, 90, 90);
            path.CloseFigure();
            g.FillPath(Brushes.Black, path);
            using var pen = new Pen(Color.White, 2);
            g.DrawPath(pen, path);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
