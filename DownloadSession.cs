namespace LoLHelper
{
    // One owner for download transitions; late progress cannot revive a finished operation.
    internal sealed class DownloadSession
    {
        public DownloadState State { get; private set; } = DownloadState.Idle;
        public bool IsRunning { get; private set; }
        public bool IsStopping { get; private set; }
        public bool CanPause => IsRunning && !IsStopping && (State == DownloadState.Probing || State == DownloadState.Downloading);
        public bool CanStart => !IsRunning && State != DownloadState.Ready;
        public int Generation { get; private set; }

        public bool TryStart()
        {
            if (!CanStart) return false;
            Generation++;
            IsRunning = true;
            IsStopping = false;
            State = DownloadState.Probing;
            return true;
        }

        public bool Report(int generation, DownloadState state)
        {
            if (generation != Generation || !IsRunning || IsStopping) return false;
            if (state != DownloadState.Probing && state != DownloadState.Downloading && state != DownloadState.Verifying) return false;
            if (State == DownloadState.Verifying && state != DownloadState.Verifying) return false;
            if (State == DownloadState.Downloading && state == DownloadState.Probing) return false;
            State = state;
            return true;
        }

        public void RequestStop() { if (IsRunning) IsStopping = true; }
        public void Finish(DownloadState state)
        {
            State = state;
            IsRunning = false;
            IsStopping = false;
        }
    }
}
