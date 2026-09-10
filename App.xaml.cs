using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LoLHelper
{
    public partial class App : Application
    {
        private bool _handlingException;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void Button_StateChanged(object sender, MouseEventArgs e)
        {
            if (!(sender is Button button) || !(button.Template.FindName("HoverWash", button) is Border wash)) return;
            var target = button.IsMouseOver && button.IsEnabled ? 0.045 : 0;
            var previous = wash.Opacity;
            wash.BeginAnimation(UIElement.OpacityProperty, null);
            wash.Opacity = target;
            if (SystemParameters.ClientAreaAnimation)
                wash.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(previous, target, TimeSpan.FromMilliseconds(140))
                    { FillBehavior = FillBehavior.Stop });
        }

        private async void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (_handlingException || !(MainWindow is MainWindow window)) return;
            e.Handled = true;
            _handlingException = true;
            try { await window.ShowErrorAsync(e.Exception.Message); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError(error.Message); Shutdown(-1); }
            finally { _handlingException = false; }
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) => e.SetObserved();
    }
}
