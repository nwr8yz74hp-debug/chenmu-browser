using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace ChenmuBrowser
{
    internal sealed partial class BrowserForm
    {
        private const long MaximumWallpaperInputBytes = 30L * 1024L * 1024L;
        private const long MaximumWallpaperPixels = 32000000L;
        private const int MaximumWallpaperDimension = 2560;

        private string HomeWallpaperPath { get { return Path.Combine(_dataRoot, "home-wallpaper.jpg"); } }

        private Image LoadHomeWallpaperImage()
        {
            try
            {
                if (!File.Exists(HomeWallpaperPath))
                    return null;
                using (var source = Image.FromFile(HomeWallpaperPath))
                    return CreateWallpaperBitmap(source);
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap CreateWallpaperBitmap(Image source)
        {
            if (source == null || source.Width < 1 || source.Height < 1)
                throw new InvalidDataException("图片尺寸无效。");
            double scale = Math.Min(1.0, (double)MaximumWallpaperDimension / Math.Max(source.Width, source.Height));
            int width = Math.Max(1, (int)Math.Round(source.Width * scale));
            int height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.White);
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    graphics.DrawImage(source, new Rectangle(0, 0, width, height));
                }
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private void ShowHomeWallpaperMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("从本机选择图片…", null, delegate { ChooseHomeWallpaper(); });
            menu.Items.Add("恢复默认渐变背景", null, delegate { RestoreDefaultHomeWallpaper(); });
            menu.Closed += delegate { menu.Dispose(); };
            menu.Show(Cursor.Position);
        }

        private void ChooseHomeWallpaper()
        {
            using (var dialog = new OpenFileDialog
            {
                Title = "选择主页壁纸",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif|所有文件|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                string temporaryPath = null;
                try
                {
                    var info = new FileInfo(dialog.FileName);
                    if (info.Length > MaximumWallpaperInputBytes)
                        throw new InvalidDataException("图片文件不能超过 30 MiB。");
                    Directory.CreateDirectory(_dataRoot);
                    temporaryPath = HomeWallpaperPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var source = Image.FromFile(dialog.FileName))
                    {
                        if ((long)source.Width * source.Height > MaximumWallpaperPixels)
                            throw new InvalidDataException("图片像素总数不能超过 3200 万。");
                        using (var bitmap = CreateWallpaperBitmap(source))
                            bitmap.Save(temporaryPath, ImageFormat.Jpeg);
                    }
                    if (File.Exists(HomeWallpaperPath))
                        File.Replace(temporaryPath, HomeWallpaperPath, null);
                    else
                        File.Move(temporaryPath, HomeWallpaperPath);
                    temporaryPath = null;
                    ApplyHomeWallpaper();
                    _statusLabel.Text = "主页壁纸已更换；压缩副本保存在本机，原图片未修改。";
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法设置壁纸：\r\n" + ex.Message, "更换壁纸",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                finally
                {
                    if (temporaryPath != null)
                    {
                        try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                        catch { }
                    }
                }
            }
        }

        private void RestoreDefaultHomeWallpaper()
        {
            try
            {
                if (File.Exists(HomeWallpaperPath))
                    File.Delete(HomeWallpaperPath);
                ApplyHomeWallpaper();
                _statusLabel.Text = "已恢复主页默认渐变背景。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法恢复默认背景：\r\n" + ex.Message, "更换壁纸",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ApplyHomeWallpaper()
        {
            if (_homeGradientPanel == null || _homeGradientPanel.IsDisposed)
                return;
            var previous = _homeGradientPanel.BackgroundImage;
            _homeGradientPanel.BackgroundImage = LoadHomeWallpaperImage();
            _homeGradientPanel.Invalidate();
            if (previous != null)
                previous.Dispose();
        }
    }
}
