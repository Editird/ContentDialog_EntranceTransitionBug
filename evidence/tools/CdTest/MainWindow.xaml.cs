using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CdTest
{
    public sealed partial class MainWindow : Window
    {
        // Modes:
        //   popup         : new ContentDialog() in code, ShowAsync()                 -> EntireControlInPopup
        //   tree          : ContentDialog declared in XAML, ShowAsync()              -> TransplantedRootInPopup
        //   inplace       : ContentDialog declared in XAML, ShowAsync(InPlace)       -> InPlace (VSM transitions)
        //   unconstrained : new ContentDialog() in code, ShowAsync(UnconstrainedPopup)
        //   prewarm       : like popup, but the same instance is shown/hidden once before the measured show
        //   heavy         : like popup, but the UI thread is blocked 200 ms in the frame where the popup opens
        readonly string _mode;
        readonly string _logPath;
        readonly Stopwatch _sw = Stopwatch.StartNew();
        readonly List<string> _log = new();
        double _lastRender = -1;
        bool _blockNextOpenFrame;

        public MainWindow()
        {
            InitializeComponent();
            var args = Environment.GetCommandLineArgs();
            _mode = args.Length > 1 ? args[1] : "popup";
            _logPath = args.Length > 3 ? args[3] : Path.Combine(AppContext.BaseDirectory, $"log_{_mode}.csv");
            Title = "CdTest-" + _mode;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 700));
            AppWindow.Move(new Windows.Graphics.PointInt32(100, 100));
            Status.Text = "mode = " + _mode + "   UISettings.AnimationsEnabled = " + new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

            CompositionTarget.Rendering += (s, e) =>
            {
                var now = _sw.Elapsed.TotalMilliseconds;
                if (_lastRender >= 0) Log("frame", $"{now - _lastRender:F1}");
                _lastRender = now;

                if (_watch != null && _watchFrames-- > 0) LogDialogState(_watch, "frame-state");

                if (_blockNextOpenFrame && Root.XamlRoot != null &&
                    VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Count > 0)
                {
                    _blockNextOpenFrame = false;
                    Log("block-start");
                    System.Threading.Thread.Sleep(200);
                    Log("block-end");
                }
            };

            Root.Loaded += async (s, e) =>
            {
                if (args.Contains("--auto")) await RunAuto();
            };
        }

        void Log(string evt, string data = "") => _log.Add($"{_sw.Elapsed.TotalMilliseconds:F1},{evt},{data},{Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency:F1}");

        ContentDialog MakeDialog() => new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Do you want to end SearchIndexer.exe?",
            Content = "If an open program is associated with this process, it will close and you will lose any unsaved data.",
            PrimaryButtonText = "End process",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        void DumpPopups()
        {
            foreach (var p in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
            {
                var child = p.Child;
                Log("popup", $"child={child?.GetType().Name} childTransitions={p.ChildTransitions?.Count ?? 0}" +
                             (p.ChildTransitions?.Count > 0 ? ":" + string.Join("|", p.ChildTransitions.Select(t => t.GetType().Name)) : ""));
            }
        }

        int _stallAfterShow;
        ContentDialog _watch;
        int _watchFrames;

        static FrameworkElement FindByName(DependencyObject root, string name)
        {
            if (root is FrameworkElement fe && fe.Name == name) return fe;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++) { var r = FindByName(VisualTreeHelper.GetChild(root, i), name); if (r != null) return r; }
            return null;
        }

        void LogDialogState(ContentDialog d, string tag)
        {
            var lr = FindByName(d, "LayoutRoot");
            var be = FindByName(d, "BackgroundElement");
            Log(tag, $"dialog={d.ActualWidth:F0}x{d.ActualHeight:F0} (Width={d.Width:F0};Height={d.Height:F0}) " +
                     $"LayoutRoot={(lr == null ? "n/a" : lr.Visibility + " " + lr.ActualWidth.ToString("F0") + "x" + lr.ActualHeight.ToString("F0"))} " +
                     $"BackgroundElement={(be == null ? "n/a" : be.ActualWidth.ToString("F0") + "x" + be.ActualHeight.ToString("F0"))}");
        }

        async Task ShowOnce(ContentDialog d, ContentDialogPlacement placement, int holdMs, bool allowHeavy = true)
        {
            TypedEventHandler_Opened(d);
            if (_mode == "explicitstyle")
            {
                // Workaround: resolve the style up front so the template is applied exactly once (inside ShowAsync,
                // with the popup placement already known) and is NOT re-applied when the dialog enters the popup.
                d.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            }
            if (_mode == "presize")
            {
                d.Width = Root.XamlRoot.Size.Width;
                d.Height = Root.XamlRoot.Size.Height;
            }
            _watch = d; _watchFrames = 12;
            LogDialogState(d, "before-ShowAsync");
            if (allowHeavy && _mode == "heavy") _blockNextOpenFrame = true;
            Log("ShowAsync");
            var op = placement == ContentDialogPlacement.Popup ? d.ShowAsync() : d.ShowAsync(placement);
            if (_stallAfterShow > 0) { Log("stall-start"); System.Threading.Thread.Sleep(_stallAfterShow); Log("stall-end"); _stallAfterShow = 0; }
            await Task.Delay(holdMs);
            DumpPopups();
            Log("Hide");
            d.Hide();
            await op;
        }

        readonly HashSet<ContentDialog> _hooked = new();
        void TypedEventHandler_Opened(ContentDialog d)
        {
            if (!_hooked.Add(d)) return;
            d.Opened += (s, e) => Log("Opened");
            d.SizeChanged += (s, e) => Log("dialog-SizeChanged", $"{e.PreviousSize.Width:F0}x{e.PreviousSize.Height:F0} -> {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
            d.Loaded += (s, e) => Log("dialog-Loaded");
            d.Unloaded += (s, e) => Log("dialog-Unloaded");
            d.Closed += (s, e) => Log("Closed");
        }

        async Task RunAuto()
        {
            await Task.Delay(1500);
            switch (_mode)
            {
                case "tree":
                    await ShowOnce(TreeDialog, ContentDialogPlacement.Popup, 1500); break;
                case "inplace":
                    await ShowOnce(TreeDialog, ContentDialogPlacement.InPlace, 1500); break;
                case "prewarm":
                    {
                        var d = MakeDialog();
                        TypedEventHandler_Opened(d);
                        Log("warmup-ShowAsync");
                        _watch = d; _watchFrames = 12; var op = d.ShowAsync(); await Task.Delay(700); Log("Hide"); d.Hide(); await op;
                        await Task.Delay(1500);
                        Log("measured");
                        await ShowOnce(d, ContentDialogPlacement.Popup, 1500);
                        break;
                    }
                case "second":      // instance A warms global resources; a NEW instance B is measured
                case "heavywarm":   // same instance re-shown; 200 ms UI stall inside the frame that opens the popup
                case "stallwarm":   // same instance re-shown; 150 ms UI stall right after ShowAsync (before DeferredOpenPopup)
                    {
                        var a = MakeDialog();
                        TypedEventHandler_Opened(a);
                        Log("warmup-ShowAsync");
                        _watch = a; _watchFrames = 12; var op = a.ShowAsync(); await Task.Delay(700); Log("Hide"); a.Hide(); await op;
                        await Task.Delay(1500);
                        var b = _mode == "second" ? MakeDialog() : a;
                        if (_mode == "heavywarm") _blockNextOpenFrame = true;
                        if (_mode == "stallwarm") _stallAfterShow = 150;
                        await ShowOnce(b, ContentDialogPlacement.Popup, 1500, false);
                        break;
                    }
                case "pretemplate": // new instance whose template is applied + measured 1 s before ShowAsync
                    {
                        var d = MakeDialog();
                        Log("pre-ApplyTemplate", d.ApplyTemplate().ToString());
                        d.Measure(new Windows.Foundation.Size(1000, 700));
                        await Task.Delay(1000);
                        await ShowOnce(d, ContentDialogPlacement.Popup, 1500);
                        break;
                    }
                case "xamlfile":
                    await ShowOnce(new MyDialog { XamlRoot = Root.XamlRoot }, ContentDialogPlacement.Popup, 1500); break;
                case "xamlfile_nostyle":
                    await ShowOnce(new MyDialogNoStyle { XamlRoot = Root.XamlRoot }, ContentDialogPlacement.Popup, 1500); break;
                default:
                    await ShowOnce(MakeDialog(), ContentDialogPlacement.Popup, 1500); break;
            }
            await Task.Delay(1200);
            File.WriteAllLines(_logPath, _log);
            Application.Current.Exit();
        }

        async void Btn_Click(object sender, RoutedEventArgs e)
        {
            await ShowOnce(MakeDialog(), ContentDialogPlacement.Popup, 1500);
        }
    }
}
