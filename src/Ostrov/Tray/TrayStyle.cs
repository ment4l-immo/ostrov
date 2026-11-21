using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ostrov.Tray;

/// <summary>
/// Оформление меню в трее под остров: чёрный фон, белый и серый текст, тонкая светлая обводка, при наведении светлая подложка,
/// скруглённые углы (Windows 11). Только белый и серый, как во всём острове.
/// </summary>
internal static class TrayStyle
{
    private static readonly Color Back = Color.FromArgb(0x0E, 0x0E, 0x0E);
    private static readonly Color Edge = Color.FromArgb(0x3A, 0x3A, 0x3A);
    private static readonly Color Hover = Color.FromArgb(0x2A, 0x2A, 0x2A);
    private static readonly Color Text = Color.FromArgb(0xF2, 0xF2, 0xF2);
    private static readonly Color Dim = Color.FromArgb(0x7A, 0x7A, 0x7A);

    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new Renderer();
        menu.BackColor = Back;
        menu.ForeColor = Text;
        menu.Font = new Font("Segoe UI", 10f);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Padding = new Padding(4);
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = Text;
            item.BackColor = Back;
            if (item is ToolStripMenuItem) item.Padding = new Padding(10, 5, 30, 5);
        }
        // Скруглённые углы у окна меню (Windows 11; на старых версиях просто не сработает).
        menu.HandleCreated += (_, _) =>
        {
            try
            {
                var preference = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(menu.Handle, 33, ref preference, sizeof(int));
            }
            catch { /* не страшно */ }
        };
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class Renderer : ToolStripProfessionalRenderer
    {
        public Renderer() : base(new Colors()) { RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(Back);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Edge);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = Rounded(r, 8);
            using var brush = new SolidBrush(Hover);
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Dim;
            base.OnRenderItemText(e);
            if (e.Item is ToolStripMenuItem { Checked: true })
            {
                // Включённый пункт: белая галочка справа от текста.
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using var pen = new Pen(Text, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                var x = e.Item.Width - 26;
                var y = e.Item.Height / 2;
                e.Graphics.DrawLines(pen, [new Point(x, y), new Point(x + 4, y + 4), new Point(x + 11, y - 4)]);
            }
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Edge);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemBorder => Hover;
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Edge;
        public override Color SeparatorLight => Edge;
    }
}
