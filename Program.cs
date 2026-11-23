using System;
using System.Threading;
using System.Windows.Forms;

namespace ChenmuBrowser
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\CHENMU_Browser_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("CHENMU Browser 已在运行，请切换到已打开的窗口使用。", "CHENMU Browser",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    Application.Run(new BrowserForm());
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }
}
