using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LoLHelper.Services
{
    internal sealed class SignatureVerifier
    {
        private readonly ConcurrentDictionary<string, SignatureResult> _cache =
            new ConcurrentDictionary<string, SignatureResult>(StringComparer.OrdinalIgnoreCase);

        public SignatureResult Verify(string path, bool useCache = true)
        {
            try
            {
                var info = new FileInfo(path);
                if (!useCache) return VerifyCore(info.FullName);
                var key = string.Concat(info.FullName, "|", info.Length, "|", info.LastWriteTimeUtc.Ticks);
                return _cache.GetOrAdd(key, _ => VerifyCore(info.FullName));
            }
            catch (Exception ex)
            {
                return new SignatureResult { Error = ex.Message };
            }
        }

        private static SignatureResult VerifyCore(string path)
        {
            var fileInfo = new WinTrustFileInfo(path);
            var data = new WinTrustData(fileInfo);
            try
            {
                var action = WinTrustActionGenericVerifyV2;
                var status = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                if (status != 0)
                    return new SignatureResult { Error = "数字签名无效（0x" + status.ToString("X8") + "）" };

                using (var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)))
                {
                    var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
                    if (string.IsNullOrWhiteSpace(publisher)) publisher = certificate.Subject;
                    return new SignatureResult
                    {
                        IsTrusted = true,
                        IsRiot = IsRiotPublisher(publisher, certificate.Subject),
                        Publisher = publisher
                    };
                }
            }
            catch (Exception ex)
            {
                return new SignatureResult { Error = ex.Message };
            }
            finally
            {
                data.Dispose();
                fileInfo.Dispose();
            }
        }

        private static bool IsRiotPublisher(params string[] names)
        {
            foreach (var name in names)
            {
                var normalized = new string((name ?? string.Empty)
                    .Where(char.IsLetterOrDigit)
                    .Select(char.ToLowerInvariant).ToArray());
                if (normalized.Contains("riotgamesinc") ||
                    normalized.Contains("riotgameslimited") ||
                    normalized.Contains("riotgamesltd"))
                    return true;
            }
            return false;
        }

        private static readonly Guid WinTrustActionGenericVerifyV2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
        private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, ref WinTrustData data);

        private enum WinTrustDataUIChoice : uint { None = 2 }
        private enum WinTrustDataRevocationChecks : uint { None = 0 }
        private enum WinTrustDataChoice : uint { File = 1 }
        private enum WinTrustDataStateAction : uint { Ignore = 0 }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustFileInfo : IDisposable
        {
            public uint StructSize;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;

            public WinTrustFileInfo(string path)
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo));
                FilePath = Marshal.StringToCoTaskMemUni(path);
                FileHandle = IntPtr.Zero;
                KnownSubject = IntPtr.Zero;
            }

            public void Dispose()
            {
                if (FilePath == IntPtr.Zero) return;
                Marshal.FreeCoTaskMem(FilePath);
                FilePath = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WinTrustData : IDisposable
        {
            public uint StructSize;
            public IntPtr PolicyCallbackData;
            public IntPtr SIPClientData;
            public WinTrustDataUIChoice UIChoice;
            public WinTrustDataRevocationChecks RevocationChecks;
            public WinTrustDataChoice UnionChoice;
            public IntPtr FileInfoPtr;
            public WinTrustDataStateAction StateAction;
            public IntPtr StateData;
            public IntPtr URLReference;
            public uint ProviderFlags;
            public uint UIContext;

            public WinTrustData(WinTrustFileInfo fileInfo)
            {
                StructSize = (uint)Marshal.SizeOf(typeof(WinTrustData));
                PolicyCallbackData = IntPtr.Zero;
                SIPClientData = IntPtr.Zero;
                UIChoice = WinTrustDataUIChoice.None;
                RevocationChecks = WinTrustDataRevocationChecks.None;
                UnionChoice = WinTrustDataChoice.File;
                FileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
                Marshal.StructureToPtr(fileInfo, FileInfoPtr, false);
                StateAction = WinTrustDataStateAction.Ignore;
                StateData = IntPtr.Zero;
                URLReference = IntPtr.Zero;
                // Full Authenticode policy, with revocation URLs limited to the local cache.
                ProviderFlags = 0x00001080;
                UIContext = 0;
            }

            public void Dispose()
            {
                if (FileInfoPtr == IntPtr.Zero) return;
                Marshal.FreeCoTaskMem(FileInfoPtr);
                FileInfoPtr = IntPtr.Zero;
            }
        }
    }
}
