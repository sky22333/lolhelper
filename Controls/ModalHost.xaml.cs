using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace LoLHelper.Controls
{
    internal enum ModalResult { None, Primary, Close }
    internal enum DialogKind { Standard, Destructive }

    public partial class ModalHost : UserControl
    {
        private TaskCompletionSource<ModalResult>? _completion;
        private CancellationTokenRegistration _cancellation;
        internal bool IsOpen => _completion != null;

        public ModalHost() => InitializeComponent();

        internal Task<ModalResult> ShowAsync(string title, object content, string? primary, string close,
            DialogKind kind, CancellationToken cancellationToken)
        {
            if (IsOpen) throw new InvalidOperationException("请先关闭当前对话框。");
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(ModalResult.None);
            var completion = new TaskCompletionSource<ModalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completion = completion;
            DialogTitle.Text = title;
            AutomationProperties.SetName(this, title);
            DialogContent.Content = content;
            DismissButton.Content = close;
            ConfirmButton.Content = primary;
            ConfirmButton.Visibility = string.IsNullOrEmpty(primary) ? Visibility.Collapsed : Visibility.Visible;
            ConfirmButton.Style = (Style)FindResource(kind == DialogKind.Destructive ? "DangerButton" : "PrimaryButton");
            Visibility = Visibility.Visible;
            DialogScroll.ScrollToTop();
            UpdateSize();
            _cancellation = cancellationToken.Register(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(_completion, completion)) Complete(ModalResult.None);
            })));
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { if (IsOpen) DismissButton.Focus(); }));
            return completion.Task;
        }

        internal void Dismiss() => Complete(ModalResult.Close);
        private void DismissButton_Click(object sender, RoutedEventArgs e) => Dismiss();
        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Complete(ModalResult.Primary);

        private void Host_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || !IsOpen) return;
            e.Handled = true;
            Dismiss();
        }

        private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateSize();
        private void UpdateSize() => DialogPanel.MaxHeight = Math.Max(180, ActualHeight - 48);

        private void Complete(ModalResult result)
        {
            var completion = _completion;
            if (completion == null) return;
            _completion = null;
            _cancellation.Dispose();
            Visibility = Visibility.Collapsed;
            DialogContent.Content = null;
            completion.TrySetResult(result);
        }
    }
}

