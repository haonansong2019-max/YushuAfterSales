using System.Windows;
using YushuAfterSales.Core;

namespace YushuAfterSales
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try { AppConfiguration.Load(); }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Configuration load failed: " + ex.GetType().Name);
            }
            base.OnStartup(e);
        }
    }
}
