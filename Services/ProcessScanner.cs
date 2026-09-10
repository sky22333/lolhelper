using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LoLHelper.Services
{
    internal sealed class ProcessScanner
    {
        private readonly SignatureVerifier _signatureVerifier;

        public ProcessScanner(SignatureVerifier signatureVerifier) => _signatureVerifier = signatureVerifier;

        public Task<IReadOnlyList<RiotProcessInfo>> ScanAsync(CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<RiotProcessInfo>>(() =>
            {
                var results = new List<RiotProcessInfo>();
                foreach (var process in Process.GetProcesses())
                {
                    using (process)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var path = process.MainModule?.FileName;
                            if (string.IsNullOrWhiteSpace(path)) continue;
                            var signature = _signatureVerifier.Verify(path!);
                            if (!signature.IsVerifiedRiot) continue;
                            results.Add(new RiotProcessInfo
                            {
                                ProcessId = process.Id,
                                StartTime = TryGetStartTime(process),
                                Name = process.ProcessName,
                                Path = path!,
                                Icon = ExtractIcon(path!)
                            });
                        }
                        catch { /* A process may exit or deny access during enumeration. */ }
                    }
                }
                return results.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }, cancellationToken);
        }

        public Task<int> TerminateAsync(IEnumerable<RiotProcessInfo> items, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                var stopped = 0;
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using (var process = Process.GetProcessById(item.ProcessId))
                        {
                            if (item.StartTime.HasValue && TryGetStartTime(process) != item.StartTime) continue;
                            var path = process.MainModule?.FileName;
                            if (string.IsNullOrWhiteSpace(path) ||
                                !string.Equals(path, item.Path, StringComparison.OrdinalIgnoreCase)) continue;
                            var signature = _signatureVerifier.Verify(path!);
                            if (!signature.IsVerifiedRiot) continue;
                            if (process.CloseMainWindow() && process.WaitForExit(2500))
                            {
                                stopped++;
                                continue;
                            }
                            process.Kill();
                            if (process.WaitForExit(4000)) stopped++;
                        }
                    }
                    catch { }
                }
                return stopped;
            }, cancellationToken);
        }

        private static DateTime? TryGetStartTime(Process process)
        {
            try { return process.StartTime.ToUniversalTime(); }
            catch { return null; }
        }

        private static ImageSource? ExtractIcon(string path)
        {
            try
            {
                using (var icon = Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    var image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    image.Freeze();
                    return image;
                }
            }
            catch { return null; }
        }
    }
}
