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

    private static void AssertCenterColor(Bitmap frame, Color expected, string message)
    {
        var actual = frame.GetPixel(frame.Width / 2, frame.Height / 2);
        const int tolerance = 4;
        Assert(Math.Abs(actual.R - expected.R) <= tolerance &&
               Math.Abs(actual.G - expected.G) <= tolerance &&
               Math.Abs(actual.B - expected.B) <= tolerance,
            message + "；期望 " + expected + "，实际 " + actual);
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

            using (var frame = Render(panel))
                AssertCenterColor(frame, firstColor, "壁纸绘制不应被白色遮罩洗淡");

            for (var index = 0; index < 4; index++)
            {
                using (var frame = Render(panel))
                    AssertCenterColor(frame, firstColor, "重复重绘应保持壁纸颜色稳定");
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
