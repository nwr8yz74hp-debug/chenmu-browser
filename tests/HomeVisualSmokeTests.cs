using System;
using System.Drawing;
using System.Windows.Forms;
using ChenmuBrowser;

internal static class HomeVisualSmokeTests
{
    private static int _assertions;

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition)
            throw new InvalidOperationException("失败：" + message);
    }

    private static Bitmap Render(GradientPanel panel)
    {
        var bitmap = new Bitmap(panel.Width, panel.Height);
        panel.DrawToBitmap(bitmap, new Rectangle(Point.Empty, panel.Size));
        return bitmap;
    }

    private static Color AssertCenterColor(Bitmap frame, Color expected, string message)
    {
        var actual = frame.GetPixel(frame.Width / 2, frame.Height / 2);
        // DrawToBitmap can return a darker color through the Windows control-capture path.
        // Keep enough tolerance for that capture variation while rejecting the old white veil.
        const int tolerance = 50;
        Assert(Math.Abs(actual.R - expected.R) <= tolerance &&
               Math.Abs(actual.G - expected.G) <= tolerance &&
               Math.Abs(actual.B - expected.B) <= tolerance &&
               actual.G >= expected.G * 0.6 && actual.B >= expected.B * 0.6,
            message + "；期望 " + expected + "，实际 " + actual);
        return actual;
    }

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var firstColor = Color.FromArgb(28, 78, 132);
        var secondColor = Color.FromArgb(43, 151, 112);
        using (var firstWallpaper = new Bitmap(1, 1))
        using (var secondWallpaper = new Bitmap(1, 1))
        using (var form = new Form { ShowInTaskbar = false, ClientSize = new Size(180, 120) })
        using (var panel = new GradientPanel { Location = Point.Empty, Size = new Size(180, 120) })
        {
            firstWallpaper.SetPixel(0, 0, firstColor);
            secondWallpaper.SetPixel(0, 0, secondColor);
            panel.BackgroundImage = firstWallpaper;
            form.Controls.Add(panel);
            form.Show();
            Application.DoEvents();

            Color firstRenderColor;
            using (var frame = Render(panel))
                firstRenderColor = AssertCenterColor(frame, firstColor, "壁纸绘制不应被白色遮罩洗淡");

            form.Hide();
            Application.DoEvents();
            form.Show();
            Application.DoEvents();
            using (var frame = Render(panel))
            {
                var restoredColor = AssertCenterColor(frame, firstColor, "窗口从后台重新显示后仍应绘制壁纸");
                Assert(Math.Abs(restoredColor.R - firstRenderColor.R) <= 2 &&
                       Math.Abs(restoredColor.G - firstRenderColor.G) <= 2 &&
                       Math.Abs(restoredColor.B - firstRenderColor.B) <= 2,
                    "窗口隐藏再显示后壁纸像素应保持稳定");
            }

            for (var index = 0; index < 4; index++)
            {
                using (var frame = Render(panel))
                {
                    var repeatedColor = AssertCenterColor(frame, firstColor, "重复重绘应保持壁纸颜色稳定");
                    Assert(Math.Abs(repeatedColor.R - firstRenderColor.R) <= 2 &&
                           Math.Abs(repeatedColor.G - firstRenderColor.G) <= 2 &&
                           Math.Abs(repeatedColor.B - firstRenderColor.B) <= 2,
                        "重复重绘的壁纸像素应保持稳定");
                }
            }

            panel.BackgroundImage = secondWallpaper;
            Application.DoEvents();
            using (var frame = Render(panel))
                AssertCenterColor(frame, secondColor, "更换壁纸后应立即显示新图而非旧缓存");

            panel.Size = new Size(220, 144);
            form.ClientSize = panel.Size;
            Application.DoEvents();
            using (var frame = Render(panel))
                AssertCenterColor(frame, secondColor, "窗口尺寸变化后应重建匹配尺寸的壁纸帧");
        }

        Console.WriteLine("通过：" + _assertions + " 项 Windows 壁纸绘制断言。");
        return 0;
    }
}
