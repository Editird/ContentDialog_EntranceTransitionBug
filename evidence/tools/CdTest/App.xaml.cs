using Microsoft.UI.Xaml;

namespace CdTest
{
    public partial class App : Application
    {
        MainWindow _w;
        public App() { InitializeComponent(); }
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _w = new MainWindow();
            _w.Activate();
        }
    }
}
