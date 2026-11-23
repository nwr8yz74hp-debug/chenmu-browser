using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ChenmuBrowser
{
    internal sealed class RoundedPanel : Panel
    {
        private int _cornerRadius = 16;
        private Color _borderColor = Color.Transparent;

        public int CornerRadius
        {
            get { return _cornerRadius; }
            set
            {
                _cornerRadius = value < 0 ? 0 : value;
                UpdateRegion();
                Invalidate();
            }
        }
        public Color BorderColor
        {
            get { return _borderColor; }
            set
            {
                _borderColor = value;
                Invalidate();
            }
        }

        private int ScaledCornerRadius
        {
            get { return Math.Max(0, (int)Math.Round(CornerRadius * DeviceDpi / 96.0)); }
        }

        public RoundedPanel()
        {
            CornerRadius = 16;
            BorderColor = Color.Transparent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnResize(System.EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        private void UpdateRegion()
        {
            if (Width < 2 || Height < 2)
                return;

            using (var path = RoundedPath(new Rectangle(0, 0, Width, Height), ScaledCornerRadius))
            {
                var nextRegion = new Region(path);
                var previousRegion = Region;
                Region = nextRegion;
                if (previousRegion != null)
                    previousRegion.Dispose();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = RoundedPath(bounds, ScaledCornerRadius))
            using (var brush = new SolidBrush(BackColor))
                e.Graphics.FillPath(brush, path);

            if (BorderColor != Color.Transparent)
            {
                using (var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), ScaledCornerRadius))
                using (var pen = new Pen(BorderColor, 1F))
                    e.Graphics.DrawPath(pen, path);
            }
        }

        internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2;
            if (diameter > bounds.Width) diameter = bounds.Width;
            if (diameter > bounds.Height) diameter = bounds.Height;
            if (diameter < 2)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class RoundedButton : Button
    {
        public int CornerRadius { get; set; }

        public RoundedButton()
        {
            CornerRadius = 16;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnResize(System.EventArgs e)
        {
            base.OnResize(e);
            if (Width < 2 || Height < 2)
                return;

            var radius = Math.Max(0, (int)Math.Round(CornerRadius * DeviceDpi / 96.0));
            using (var path = RoundedPanel.RoundedPath(new Rectangle(0, 0, Width, Height), radius))
            {
                var oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                    oldRegion.Dispose();
            }
        }
    }

    internal sealed class GradientPanel : Panel
    {
        public Color StartColor { get; set; }
        public Color EndColor { get; set; }

        public GradientPanel()
        {
            StartColor = Color.FromArgb(248, 250, 253);
            EndColor = Color.FromArgb(237, 243, 250);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1)
                return;
            using (var brush = new LinearGradientBrush(ClientRectangle, StartColor, EndColor, 90F))
                e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
}
