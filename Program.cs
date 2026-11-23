using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace ChenmuBrowser
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var isolatedTest = false;
            var initialTestTabs = 1;
            string dataRoot = null;
#if DEBUG
            isolatedTest = args.Any(argument => String.Equals(argument, "--isolated-test", StringComparison.OrdinalIgnoreCase));
            if (isolatedTest)
            {
                dataRoot = Path.Combine(Path.GetTempPath(), "CHENMU Browser UI QA");
                var tabsArgument = args.FirstOrDefault(argument =>
                    argument.StartsWith("--qa-tabs=", StringComparison.OrdinalIgnoreCase));
                if (tabsArgument != null)
                    Int32.TryParse(tabsArgument.Substring("--qa-tabs=".Length), out initialTestTabs);
            }
#endif
            var mutexName = isolatedTest
                ? "Local\\CHENMU_Browser_UI_QA"
                : "Local\\CHENMU_Browser_SingleInstance";
            bool createdNew;
            using (var mutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("CHENMU Browser 已在运行，请切换到已打开的窗口使用。", "CHENMU Browser",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                try
                {
                    Application.Run(new BrowserForm(dataRoot, isolatedTest, initialTestTabs));
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }
    }
}
