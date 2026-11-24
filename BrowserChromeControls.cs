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
        private Color _surfaceColor = Color.White;

        public int CornerRadius
        {
            get { return _cornerRadius; }
            set
            {
                _cornerRadius = value < 0 ? 0 : value;
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
        public Color SurfaceColor
        {
            get { return _surfaceColor; }
            set
            {
                _surfaceColor = value;
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
            SurfaceColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 2 || Height < 2)
                return;

            var state = e.Graphics.Save();
            try
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var scale = DeviceDpi / 96F;
                var fillBounds = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
                using (var path = RoundedPath(fillBounds, Math.Max(0F, ScaledCornerRadius - 0.5F)))
                using (var brush = new SolidBrush(SurfaceColor))
                    e.Graphics.FillPath(brush, path);

                if (BorderColor != Color.Transparent)
                {
                    var penWidth = Math.Max(1F, scale);
                    var inset = penWidth / 2F;
                    var borderBounds = new RectangleF(inset, inset,
                        Math.Max(1F, Width - penWidth), Math.Max(1F, Height - penWidth));
                    using (var path = RoundedPath(borderBounds, Math.Max(0F, ScaledCornerRadius - inset)))
                    using (var pen = new Pen(BorderColor, penWidth))
                    {
                        pen.LineJoin = LineJoin.Round;
                        e.Graphics.DrawPath(pen, path);
                    }
                }
            }
            finally
            {
                e.Graphics.Restore(state);
            }
        }

        internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            return RoundedPath(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
        }

        internal static GraphicsPath RoundedPath(RectangleF bounds, float radius)
        {
            var path = new GraphicsPath();
            var diameter = radius * 2F;
            if (diameter > bounds.Width) diameter = bounds.Width;
            if (diameter > bounds.Height) diameter = bounds.Height;
            if (diameter < 2F)
            {
                path.AddRectangle(bounds);
                return path;
            }

            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270F, 90F);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
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

        private Bitmap _wallpaperFrame;
        private Image _wallpaperFrameSource;
        private Size _wallpaperFrameSize;

        public GradientPanel()
        {
            StartColor = Color.FromArgb(248, 250, 253);
            EndColor = Color.FromArgb(237, 243, 250);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        protected override void OnBackgroundImageChanged(System.EventArgs e)
        {
            base.OnBackgroundImageChanged(e);
            ResetWallpaperFrame();
        }

        protected override void OnSizeChanged(System.EventArgs e)
        {
            base.OnSizeChanged(e);
            ResetWallpaperFrame();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                ResetWallpaperFrame();
            base.Dispose(disposing);
        }

        private void ResetWallpaperFrame()
        {
            if (_wallpaperFrame != null)
            {
                _wallpaperFrame.Dispose();
                _wallpaperFrame = null;
            }
            _wallpaperFrameSource = null;
            _wallpaperFrameSize = Size.Empty;
        }

        private Bitmap GetWallpaperFrame(Image source)
        {
            var size = ClientSize;
            if (size.Width < 1 || size.Height < 1)
                return null;
            if (_wallpaperFrame != null && ReferenceEquals(_wallpaperFrameSource, source) &&
                _wallpaperFrameSize == size)
                return _wallpaperFrame;

            var frame = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(frame))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float scale = Math.Max((float)size.Width / source.Width, (float)size.Height / source.Height);
                    float width = source.Width * scale;
                    float height = source.Height * scale;
                    var bounds = new RectangleF((size.Width - width) / 2F, (size.Height - height) / 2F, width, height);
                    graphics.DrawImage(source, bounds);
                }

                ResetWallpaperFrame();
                _wallpaperFrame = frame;
                _wallpaperFrameSource = source;
                _wallpaperFrameSize = size;
                return _wallpaperFrame;
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Width < 1 || Height < 1)
                return;
            if (BackgroundImage != null)
            {
                var frame = GetWallpaperFrame(BackgroundImage);
                if (frame != null)
                    e.Graphics.DrawImageUnscaled(frame, ClientRectangle.Location);
                return;
            }
            using (var brush = new LinearGradientBrush(ClientRectangle, StartColor, EndColor, 90F))
                e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
}
