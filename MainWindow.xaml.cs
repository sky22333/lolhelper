using LoLHelper.Services;
using LoLHelper.Controls;
using System.Windows.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LoLHelper
{
    public partial class MainWindow : Window
    {
        private readonly SignatureVerifier _signatureVerifier = new SignatureVerifier();
        private readonly ProcessScanner _processScanner;
        private readonly LocaleService _localeService = new LocaleService();
        private readonly InstallerDownloader _downloader;
        private readonly DownloadSession _download = new DownloadSession();
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim _dialogs = new SemaphoreSlim(1, 1);
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _downloadCts;
        private IReadOnlyList<RiotProcessInfo> _processes = Array.Empty<RiotProcessInfo>();
        private bool _pauseRequested;
        private bool _localeBusy;
        private bool _cancelBusy;
        private bool _installBusy;
        private bool _closing;
        private bool _allowClose;
        private int _localeRevision;
        private string? _verifiedInstallerPath;
        private Task? _downloadOperation;

        public MainWindow()
        {
            InitializeComponent();
            Width = Math.Max(MinWidth, Math.Min(Width, SystemParameters.WorkArea.Width - 24));
            Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 24));
            _processScanner = new ProcessScanner(_signatureVerifier);
            _downloader = new InstallerDownloader(_signatureVerifier);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await Task.WhenAll(RefreshProcessesAsync(), RefreshLocaleAsync());
        }

        private async Task RefreshProcessesAsync()
        {
            _scanCts?.Cancel();
            var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _scanCts = source;
            RefreshButton.IsEnabled = false;
            ProcessStatusText.Text = "正在验证进程签名…";
            ConnectionText.Text = "检测中";
            try
            {
                var processes = await _processScanner.ScanAsync(source.Token);
                if (source.IsCancellationRequested) return;
                _processes = processes;
                ProcessPreview.ItemsSource = processes;
                ProcessListScroll.Visibility = processes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
                ProcessStatusText.Text = processes.Count == 0 ? "启动客户端后，可重新检测运行状态。" : "以下进程已通过 Riot Games 签名验证。";
                ConnectionText.Text = processes.Count == 0 ? "未运行" : processes.Count + " 个进程";
                ConnectionDot.Fill = (Brush)FindResource(processes.Count == 0 ? "MutedText" : "SuccessBrush");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (source.IsCancellationRequested) return;
                _processes = Array.Empty<RiotProcessInfo>();
                ProcessPreview.ItemsSource = null;
                ProcessStatusText.Text = "检测失败：" + ex.Message;
                ProcessListScroll.Visibility = Visibility.Collapsed;
                ConnectionText.Text = "检测失败";
                ConnectionDot.Fill = (Brush)FindResource("WarningBrush");
            }
            finally
            {
                if (ReferenceEquals(_scanCts, source)) { _scanCts = null; RefreshButton.IsEnabled = true; }
                source.Dispose();
            }
        }

        private async Task RefreshLocaleAsync()
        {
            var revision = ++_localeRevision;
            try
            {
                var status = await _localeService.GetStatusAsync();
                if (revision != _localeRevision || _localeBusy || _lifetime.IsCancellationRequested) return;
                LocaleStatusText.Text = !status.Exists ? "尚未找到游戏配置" : status.IsChinese ? "简体中文 · " + (status.IsLocked ? "配置已锁定" : "配置未锁定") : "尚未应用简体中文";
                LocaleDetailText.Text = !status.Exists ? "请先安装游戏，并启动一次客户端。" : status.IsLocked ? "更新客户端前，可解除语言配置锁定。" : "应用后请重启客户端，等待语言资源更新。";
                UnlockButton.IsEnabled = status.Exists && status.IsLocked && !_localeBusy;
                ApplyLocaleButton.IsEnabled = status.Exists && !_localeBusy;
            }
            catch (Exception ex)
            {
                if (revision != _localeRevision || _localeBusy || _lifetime.IsCancellationRequested) return;
                LocaleStatusText.Text = "无法读取配置";
                LocaleDetailText.Text = ex.Message;
                ApplyLocaleButton.IsEnabled = false;
                UnlockButton.IsEnabled = false;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await Task.WhenAll(RefreshProcessesAsync(), _localeBusy ? Task.CompletedTask : RefreshLocaleAsync());
        }

        private async void ApplyLocaleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_localeBusy || _closing) return;
            _localeBusy = true;
            _localeRevision++;
            ApplyLocaleButton.IsEnabled = UnlockButton.IsEnabled = false;
            ApplyLocaleButton.Content = "正在应用…";
            FooterStatusText.Text = "正在应用语言配置";
            try
            {
                await _localeService.ApplyChineseAsync(_lifetime.Token);
                await RefreshProcessesAsync();
                var stopped = 0;
                if (_processes.Count > 0 && await ShowProcessConfirmationAsync(_processes) == ModalResult.Primary)
                {
                    stopped = await _processScanner.TerminateAsync(_processes, _lifetime.Token);
                    await RefreshProcessesAsync();
                }
                await ShowNoticeAsync("简体中文已应用", "配置已写入并锁定。" + (stopped > 0 ? "已关闭 " + stopped + " 个 Riot 进程。" : string.Empty) +
                    (_processes.Count > 0 ? "\n\n仍有 Riot 进程运行，请保存游戏进度后手动退出客户端。" : string.Empty) +
                    "\n\n请重新启动 Riot Client，并等待语言资源更新完成后进入游戏。");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { await ShowNoticeAsync("无法应用简体中文", ex.Message); }
            finally
            {
                _localeBusy = false;
                ApplyLocaleButton.Content = "应用简体中文";
                await RefreshLocaleAsync();
                FooterStatusText.Text = "就绪";
            }
        }

        private async void UnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (_localeBusy || _closing) return;
            _localeBusy = true;
            _localeRevision++;
            ApplyLocaleButton.IsEnabled = UnlockButton.IsEnabled = false;
            UnlockButton.Content = "正在解除…";
            try
            {
                await _localeService.UnlockAsync();
                FooterStatusText.Text = "已解除锁定 · 保留当前语言设置";
            }
            catch (Exception ex) { await ShowNoticeAsync("解除失败", ex.Message); }
            finally
            {
                _localeBusy = false;
                UnlockButton.Content = "解除锁定";
                await RefreshLocaleAsync();
            }
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancelBusy || _closing) return;
            if (_download.CanPause)
            {
                _pauseRequested = true;
                _download.RequestStop();
                _downloadCts?.Cancel();
                RenderDownloadState();
                return;
            }
            if (!_download.TryStart()) return;
            _downloadOperation = RunDownloadAsync();
            await _downloadOperation;
        }

        private async Task RunDownloadAsync()
        {
            _pauseRequested = false;
            _downloadCts?.Dispose();
            _downloadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var source = _downloadCts;
            var generation = _download.Generation;
            _verifiedInstallerPath = null;
            DownloadStatusText.Text = "正在连接 Riot 官方下载源…";
            RenderDownloadState();
            var progress = new Progress<DownloadProgressInfo>(info =>
            {
                if (!_download.Report(generation, info.State)) return;
                DownloadStatusText.Text = info.Message;
                var percent = info.TotalBytes > 0 ? Math.Max(0, Math.Min(100, info.DownloadedBytes * 100d / info.TotalBytes)) : 0;
                SetProgress(percent);
                DownloadPercentText.Text = info.TotalBytes > 0 ? percent.ToString("0") + "%" : "…";
                DownloadedText.Text = info.TotalBytes > 0 ? FormatBytes(info.DownloadedBytes) + " / " + FormatBytes(info.TotalBytes) : "连接官方源";
                SpeedText.Text = info.BytesPerSecond > 0 ? FormatBytes((long)info.BytesPerSecond) + "/s" : string.Empty;
                RenderDownloadState();
            });
            try
            {
                _verifiedInstallerPath = await _downloader.DownloadAsync(progress, source.Token);
                _download.Finish(DownloadState.Ready);
                DownloadStatusText.Text = "Riot 签名验证通过，可以安全启动安装器。";
                DownloadStatusText.ToolTip = _verifiedInstallerPath;
                SetProgress(100);
                DownloadPercentText.Text = "100%";
            }
            catch (OperationCanceledException)
            {
                _download.Finish(_pauseRequested ? DownloadState.Paused : DownloadState.Canceled);
                DownloadStatusText.Text = _pauseRequested ? "下载已暂停，继续时将尝试恢复断点。" : "下载已停止。";
            }
            catch (Exception ex)
            {
                _download.Finish(DownloadState.Failed);
                DownloadStatusText.Text = "下载失败：" + ex.Message;
            }
            finally
            {
                SpeedText.Text = string.Empty;
                RenderDownloadState();
            }
        }

        private void RenderDownloadState()
        {
            var state = _download.State;
            DownloadButton.Visibility = state == DownloadState.Ready ? Visibility.Collapsed : Visibility.Visible;
            InstallButton.Visibility = state == DownloadState.Ready ? Visibility.Visible : Visibility.Collapsed;
            InstallButton.IsEnabled = !_cancelBusy && !_installBusy && !_closing;
            DownloadButton.IsEnabled = !_cancelBusy && !_closing && (_download.CanStart || _download.CanPause);
            CancelDownloadButton.IsEnabled = !_cancelBusy && !_closing && state != DownloadState.Idle && state != DownloadState.Ready && state != DownloadState.Canceled && !_download.IsStopping;
            DownloadButton.Content = _download.IsStopping ? "正在停止…" : state == DownloadState.Verifying ? "正在验证签名…" : _download.CanPause ? "暂停下载" : state == DownloadState.Paused ? "继续下载" : state == DownloadState.Failed ? "重试下载" : "下载安装器";
            DownloadPhaseText.Text = _download.IsStopping ? "正在保存进度" : state == DownloadState.Probing ? "连接官方源" : state == DownloadState.Downloading ? "正在下载" : state == DownloadState.Verifying ? "安全校验中" : state == DownloadState.Paused ? "已暂停" : state == DownloadState.Ready ? "已验证 · 准备安装" : state == DownloadState.Failed ? "下载未完成" : state == DownloadState.Canceled ? "已取消" : "等待下载";
        }

        private void SetProgress(double value)
        {
            if (!SystemParameters.ClientAreaAnimation) { DownloadProgressBar.Value = value; return; }
            var previous = DownloadProgressBar.Value;
            DownloadProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            DownloadProgressBar.Value = value;
            DownloadProgressBar.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                new DoubleAnimation(previous, value, TimeSpan.FromMilliseconds(140)) { FillBehavior = FillBehavior.Stop });
        }

        private async void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_cancelBusy || _closing) return;
            _cancelBusy = true;
            RenderDownloadState();
            try
            {
                if (await ShowConfirmationAsync("取消并清除下载？", "这会删除已下载的分片和断点信息，下次需要重新下载。", "清除下载", "保留下载", DialogKind.Destructive) != ModalResult.Primary) return;
                _pauseRequested = false;
                _download.RequestStop();
                _downloadCts?.Cancel();
                if (_downloadOperation != null) await _downloadOperation;
                // Completion may have won the race while the confirmation was open.
                if (_download.State == DownloadState.Ready) return;
                await Task.Run(() => _downloader.CancelAndDelete());
                _download.Finish(DownloadState.Canceled);
                SetProgress(0);
                DownloadPercentText.Text = "—";
                DownloadedText.Text = "支持暂停与断点续传";
                SpeedText.Text = string.Empty;
                DownloadStatusText.Text = "下载已取消，断点数据已清除。";
            }
            catch (Exception ex) { await ShowNoticeAsync("无法清除下载", ex.Message); }
            finally { _cancelBusy = false; RenderDownloadState(); }
        }

        private async void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_installBusy || _cancelBusy || _closing) return;
            _installBusy = true;
            RenderDownloadState();
            try
            {
                var path = _verifiedInstallerPath;
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("安装器已移动或删除，请重新下载。");
                var result = await Task.Run(() => _signatureVerifier.Verify(path!, useCache: false));
                if (!result.IsVerifiedRiot) throw new InvalidDataException("安装器签名未通过验证，请重新下载。");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                FooterStatusText.Text = "已启动安装器，请按安装向导继续";
            }
            catch (Exception ex)
            {
                _verifiedInstallerPath = null;
                _download.Finish(DownloadState.Failed);
                DownloadStatusText.Text = ex.Message;
                await ShowNoticeAsync("无法启动安装器", ex.Message);
            }
            finally { _installBusy = false; RenderDownloadState(); }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024d / 1024).ToString("0.0") + " MB";
            return (bytes / 1024d / 1024 / 1024).ToString("0.00") + " GB";
        }

        private async void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            var content = new StackPanel();
            AddHelpSection(content, "下载安装", "从 Riot 官方源获取台服安装器，支持暂停和断点续传。数字签名验证通过后，点击“立即安装”。");
            AddHelpSection(content, "应用简体中文", "先安装并启动一次游戏，点击“重新检测”，再应用简体中文。完成后重启客户端，等待语言资源更新。");
            AddHelpSection(content, "解除锁定", "允许客户端更新配置，并保留当前语言。解除锁定不会恢复语言，只是放开写入限制。");
            content.Children.Add(MessageContent("本工具不是 Riot Games 官方产品，不修改游戏程序，也不绕过 Vanguard。开源地址：https://github.com/sky22333/lolhelper"));
            await ShowDialogAsync("使用帮助", content, null, "知道了");
        }

        private void AddHelpSection(Panel content, string title, string description)
        {
            content.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("SectionTitle"), Margin = new Thickness(0, 0, 0, 8) });
            var text = MessageContent(description);
            text.Margin = new Thickness(0, 0, 0, 16);
            content.Children.Add(text);
        }

        private Task<ModalResult> ShowNoticeAsync(string title, string message) => ShowDialogAsync(title, MessageContent(message), null, "知道了");
        private Task<ModalResult> ShowConfirmationAsync(string title, string message, string primary, string close, DialogKind appearance) => ShowDialogAsync(title, MessageContent(message), primary, close, appearance);
        private TextBlock MessageContent(string message) => new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 20, Foreground = (Brush)FindResource("MutedText") };

        private Task<ModalResult> ShowProcessConfirmationAsync(IReadOnlyList<RiotProcessInfo> processes)
        {
            var stack = new StackPanel();
            stack.Children.Add(MessageContent("语言配置已保存。关闭以下进程后，请重新启动客户端。正在对局时请选择“稍后手动重启”，避免中断游戏。"));
            foreach (var process in processes)
                stack.Children.Add(new TextBlock { Text = process.Name + "   /   PID " + process.ProcessId, Margin = new Thickness(0, 12, 0, 0) });
            return ShowDialogAsync("重启前，关闭 Riot 进程？", stack, "关闭这些进程", "稍后手动重启", DialogKind.Destructive);
        }

        internal Task<ModalResult> ShowErrorAsync(string message) => ShowNoticeAsync("操作未能完成", message);

        private async Task<ModalResult> ShowDialogAsync(string title, object content, string? primary, string close, DialogKind appearance = DialogKind.Standard)
        {
            try { await _dialogs.WaitAsync(_lifetime.Token); }
            catch (OperationCanceledException) { return ModalResult.None; }
            var previousFocus = Keyboard.FocusedElement;
            AppSurface.IsEnabled = false;
            try
            {
                return await DialogHost.ShowAsync(title, content, primary, close, appearance, _lifetime.Token);
            }
            finally
            {
                AppSurface.IsEnabled = true;
                _dialogs.Release();
                if (!_lifetime.IsCancellationRequested && previousFocus != null) Keyboard.Focus(previousFocus);
            }
        }
        private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            if (_allowClose) return;
            if (DialogHost.IsOpen) { e.Cancel = true; DialogHost.Dismiss(); return; }
            if (_localeBusy || _installBusy || _cancelBusy)
            {
                e.Cancel = true;
                FooterStatusText.Text = "请等待当前操作完成后再关闭";
                return;
            }
            if (!_download.IsRunning) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            _pauseRequested = true;
            _download.RequestStop();
            _downloadCts?.Cancel();
            RenderDownloadState();
            FooterStatusText.Text = "正在保存下载断点并关闭…";
            if (_downloadOperation != null) await _downloadOperation;
            _allowClose = true;
            Close();
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _lifetime.Cancel();
            _scanCts?.Cancel();
            _downloadCts?.Dispose();
            _downloader.Dispose();
        }
    }
}


