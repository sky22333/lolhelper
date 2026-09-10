using System;
using System.Windows.Media;

namespace LoLHelper
{
    internal sealed class LocaleStatus
    {
        public bool Exists { get; set; }
        public bool IsChinese { get; set; }
        public bool IsLocked { get; set; }
    }

    internal sealed class SignatureResult
    {
        public bool IsTrusted { get; set; }
        public bool IsRiot { get; set; }
        public string Publisher { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    internal sealed class RiotProcessInfo
    {
        public int ProcessId { get; set; }
        public DateTime? StartTime { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public ImageSource? Icon { get; set; }
    }

    internal enum DownloadState
    {
        Idle,
        Probing,
        Downloading,
        Paused,
        Verifying,
        Ready,
        Failed,
        Canceled
    }

    internal sealed class DownloadProgressInfo
    {
        public DownloadState State { get; set; }
        public long DownloadedBytes { get; set; }
        public long TotalBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? FilePath { get; set; }
    }
}
