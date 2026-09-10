using System.IO;
using System.Windows;

namespace Ritual.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool testMode = args.Contains("--ui-shot") || args.Contains("--wgc-shot");
        using var instance = new System.Threading.Mutex(false, "Local\\PoE2RitualChecker.Active");
        bool owned = false;
        if (!testMode)
        {
            try
            {
                owned = instance.WaitOne(0);
            }
            catch (System.Threading.AbandonedMutexException)
            {
                owned = true;
            }
            if (!owned)
            {
                MessageBox.Show("Ritual Checker가 이미 실행 중입니다.", "Ritual Checker");
                return;
            }
        }
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "Ritual Checker");
            e.Handled = true;
        };
        try
        {
            var window = new MainWindow(args);
            app.Run(window);
        }
        finally
        {
            if (owned)
                instance.ReleaseMutex();
        }
    }
}
