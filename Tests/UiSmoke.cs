using LoLHelper.Controls;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LoLHelper.Tests
{
    internal static class UiSmoke
    {
        private const string PreviewDirectory = "tmp/ui-light";

        public static int Run()
        {
            var app = new App();
            app.InitializeComponent();
            var window = new MainWindow();
            var root = (FrameworkElement)window.Content;
            Directory.CreateDirectory(PreviewDirectory);
            try
            {
                TestStartup(window);
                TestWindowRounding(root);
                Render(root, 538, 554, "main");
                Require(((ScrollViewer)window.FindName("ContentScroll")).ScrollableHeight == 0, "default window contains all idle tasks");
                Render(root, 478, 478, "minimum");
                TestQuietHover(app, window, root);
                TestDownloadControls(window, root);
                TestDialogs(window, root);
                Console.WriteLine("Compact layout, shared styles, hover, download and modal tests passed; previews in tmp/ui-light.");
                return 0;
            }
            finally { window.Close(); app.Shutdown(); }
        }

        private static object Invoke(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args)!;

        private static void PumpUntil(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (sender, args) => { if (task.IsCompleted || elapsed.Elapsed.TotalSeconds > 30) frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);
                timer.Stop();
            }
            Require(task.IsCompleted, "operation completes without blocking dispatcher");
            task.GetAwaiter().GetResult();
        }

        private static void TestStartup(MainWindow window)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            PumpUntil(Task.WhenAll(new[] { "RefreshProcessesAsync", "RefreshLocaleAsync" }.Select(name => (Task)Invoke(window, name))));
            Require(!((TextBlock)window.FindName("LocaleStatusText")).Text.Contains("正在检查"), "configuration leaves loading state");
            Require(((Button)window.FindName("RefreshButton")).IsEnabled, "refresh restored after startup");
            Console.WriteLine("Read-only startup checks completed.");
        }

        private static void TestQuietHover(App app, MainWindow window, FrameworkElement root)
        {
            var button = (Button)window.FindName("DownloadButton");
            var before = button.TransformToAncestor(root).Transform(new Point(0, 0));
            var size = button.RenderSize;
            var args = new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent };
            Invoke(app, "Button_StateChanged", button, args);
            root.UpdateLayout();
            var after = button.TransformToAncestor(root).Transform(new Point(0, 0));
            Require(before == after && size == button.RenderSize && button.RenderTransform.Value.IsIdentity, "hover never shifts or scales the button");
            var wash = (Border)button.Template.FindName("HoverWash", button);
            Require(wash.Opacity <= 0.045, "hover is limited to subtle wash opacity");
        }

        private static void TestDownloadControls(MainWindow window, FrameworkElement root)
        {
            var session = (DownloadSession)typeof(MainWindow).GetField("_download", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var download = (Button)window.FindName("DownloadButton");
            var install = (Button)window.FindName("InstallButton");
            session.TryStart();
            session.Report(session.Generation, DownloadState.Verifying);
            Invoke(window, "RenderDownloadState");
            Require(!download.IsEnabled && install.Visibility == Visibility.Collapsed, "verification prevents restart and install");
            session.RequestStop();
            Invoke(window, "RenderDownloadState");
            Require(!download.IsEnabled, "stop prevents repeat click");
            session.Finish(DownloadState.Paused);
            Invoke(window, "RenderDownloadState");
            Require(download.IsEnabled && download.Content.ToString().Contains("继续"), "pause exposes resume");
            ((ProgressBar)window.FindName("DownloadProgressBar")).Value = 42;
            ((TextBlock)window.FindName("DownloadPercentText")).Text = "42%";
            ((TextBlock)window.FindName("DownloadedText")).Text = "42.0 MB / 100.0 MB";
            ((TextBlock)window.FindName("DownloadStatusText")).Text = "下载已暂停，继续时将尝试恢复断点。";
            Render(root, 538, 554, "paused");
            session.Finish(DownloadState.Ready);
            Invoke(window, "RenderDownloadState");
            Require(download.Visibility == Visibility.Collapsed && install.IsEnabled && install.Visibility == Visibility.Visible, "ready exposes installer without launching it");
            session.Finish(DownloadState.Failed);
            Invoke(window, "RenderDownloadState");
            Require(download.IsEnabled && install.Visibility == Visibility.Collapsed, "failure exposes retry");
            ((TextBlock)window.FindName("DownloadStatusText")).Text = "下载失败：服务器暂时无法连接。请检查网络后重试，已下载的分片将会保留。";
            Render(root, 478, 478, "download-error");
            var scroll = (ScrollViewer)window.FindName("ContentScroll");
            scroll.ScrollToEnd();
            root.UpdateLayout();
            var buttonBottom = download.TransformToAncestor(scroll).Transform(new Point(0, download.ActualHeight));
            Require(buttonBottom.Y <= scroll.ActualHeight + 1, "last action remains reachable in minimum window");
            Render(root, 478, 478, "minimum-scrolled");
            scroll.ScrollToTop();
            session.Finish(DownloadState.Idle);
            Invoke(window, "RenderDownloadState");
        }

        private static void TestDialogs(MainWindow window, FrameworkElement root)
        {
            var host = (ModalHost)window.FindName("DialogHost");
            var surface = (Grid)window.FindName("AppSurface");
            var primary = (Button)host.FindName("ConfirmButton");
            var dismiss = (Button)host.FindName("DismissButton");
            var download = (Button)window.FindName("DownloadButton");
            var confirmation = (Task<ModalResult>)Invoke(window, "ShowConfirmationAsync", "取消并清除下载？", "已下载的分片和断点信息会被删除，下次需要重新下载。", "清除下载", "保留下载", DialogKind.Destructive);
            Render(root, 538, 554, "confirmation");
            Require(!surface.IsEnabled && host.IsOpen, "modal blocks background actions");
            Require(ReferenceEquals(primary.Template, download.Template) && ReferenceEquals(dismiss.Template, download.Template), "main and dialog buttons share one template");
            Require(KeyboardNavigation.GetTabNavigation(host) == KeyboardNavigationMode.Cycle && dismiss.IsDefault && !primary.IsDefault, "modal traps tab and defaults to non-destructive action");
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(confirmation);
            Require(confirmation.Result == ModalResult.Close && surface.IsEnabled && !host.IsOpen, "dismiss resolves and restores background");

            var accepted = (Task<ModalResult>)Invoke(window, "ShowConfirmationAsync", "关闭 Riot 进程？", "请先保存游戏进度。", "关闭进程", "暂不关闭", DialogKind.Destructive);
            primary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(accepted);
            Require(accepted.Result == ModalResult.Primary, "explicit confirmation resolves primary action");

            var helpTask = (Task<ModalResult>)Invoke(window, "ShowNoticeAsync", "使用指南", string.Join("\n\n", Enumerable.Repeat("先安装并启动一次游戏，再点击重新检测。应用简体中文后，请重启客户端并等待语言资源更新。", 12)));
            Render(root, 478, 478, "long-help");
            var panel = (Border)host.FindName("DialogPanel");
            var scroll = (ScrollViewer)host.FindName("DialogScroll");
            Require(panel.ActualHeight <= root.ActualHeight - 48 && scroll.ScrollableHeight > 0, "long modal content scrolls inside small window");
            Require(primary.Visibility == Visibility.Collapsed, "notice has one dismiss action");
            var args = new CancelEventArgs();
            Invoke(window, "Window_Closing", window, args);
            PumpUntil(helpTask);
            Require(args.Cancel && !host.IsOpen && surface.IsEnabled, "window close dismisses modal before closing application");

            var errorTask = window.ShowErrorAsync("安装器已移动或删除，请重新下载。");
            Render(root, 538, 554, "error-dialog");
            var queued = window.ShowErrorAsync("第二条提示");
            Require(!queued.IsCompleted && ((TextBlock)host.FindName("DialogTitle")).Text == "操作未能完成", "dialogs queue rather than overlap");
            host.Dismiss();
            PumpUntil(errorTask);
            PumpUntil(Task.Delay(50));
            Require(host.IsOpen, "queued dialog becomes available");
            host.Dismiss();
            PumpUntil(queued);

            using (var cancellation = new CancellationTokenSource())
            {
                var canceled = host.ShowAsync("取消测试", new TextBlock { Text = "临时内容" }, null, "关闭", DialogKind.Standard, cancellation.Token);
                cancellation.Cancel();
                PumpUntil(canceled);
                Require(canceled.Result == ModalResult.None && !host.IsOpen, "shutdown cancellation releases modal task");
            }
        }

        private static Border FindOuterBorder(DependencyObject node)
        {
            if (node is Border self && self.CornerRadius.TopLeft > 0) return self;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var nested = FindOuterBorder(VisualTreeHelper.GetChild(node, i));
                if (nested != null) return nested;
            }
            return null!;
        }

        private static void TestWindowRounding(FrameworkElement root)
        {
            root.Measure(new Size(538, 554));
            root.Arrange(new Rect(0, 0, 538, 554));
            root.UpdateLayout();
            var outer = FindOuterBorder(root);
            if (outer == null) throw new InvalidOperationException("Failed: window shell border found");
            // Render the shell alone: the corner pixels must stay transparent so the corners are
            // actually cut out, and match the radius used by the app's controls.
            var radius = outer.CornerRadius.TopLeft;
            var image = new RenderTargetBitmap(538, 554, 96, 96, PixelFormats.Pbgra32);
            image.Render(root);
            var corner = new byte[4];
            image.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
            Require(corner[3] == 0, "window corner is cut out, alpha=" + corner[3]);
            var inside = new byte[4];
            image.CopyPixels(new Int32Rect(20, 20, 1, 1), inside, 4, 0);
            Require(inside[3] == 255, "window surface is opaque away from the corner, alpha=" + inside[3]);
            Require(Math.Abs(radius - 4) < 0.01, "window radius matches the control radius, got " + radius);
        }

        private static void Render(FrameworkElement root, int width, int height, string name)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(root);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(Path.Combine(PreviewDirectory, name + ".png"))) encoder.Save(output);
            Require(root.ActualWidth == width, "requested layout width");
        }
        private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}

