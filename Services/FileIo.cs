using System.IO;

namespace LoLHelper.Services
{
    internal static class FileIo
    {
        /// <summary>
        /// Best-effort delete for temporary and cache files. Cleanup must never turn a completed
        /// operation into a failure, so every error is swallowed.
        /// </summary>
        internal static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
