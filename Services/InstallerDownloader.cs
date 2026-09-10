using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LoLHelper.Services
{
    internal sealed class InstallerDownloader : IDisposable
    {
        private static readonly Uri PrimaryUri = new Uri("https://lol.secure.dyn.riotcdn.net/channels/public/x/installer/current/live.tw2.exe");
        private static readonly Uri OfficialPage = new Uri("https://www.leagueoflegends.com/zh-tw/download/");
        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
        private readonly HttpClient _client;
        private readonly SignatureVerifier _signatureVerifier;
        private readonly string _workDirectory;

        public InstallerDownloader(SignatureVerifier signatureVerifier)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _signatureVerifier = signatureVerifier;
            // Resolved lazily: the app runs elevated, and an elevated token has no write access to the
            // invoking user's LocalAppData, so probing directories in the constructor could fail for a
            // user who has not downloaded anything yet.
            _workDirectory = ResolveWorkDirectory();
            _client = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            {
                // HttpClient.Timeout spans the whole response body, so a multi-gigabyte installer
                // would be aborted mid-transfer. Body reads are bounded only by the caller's token;
                // the short header-only probe requests below carry their own timeout instead.
                Timeout = Timeout.InfiniteTimeSpan
            };
            _client.DefaultRequestHeaders.UserAgent.ParseAdd("LoLHelper/1.0");
        }

        /// <summary>
        /// Picks the first usable download directory. ProgramData is the fallback for an elevated
        /// process, whose token cannot create anything under the standard user's LocalAppData.
        /// </summary>
        private static string ResolveWorkDirectory()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LoLHelper", "Download"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LoLHelper", "Download"),
                Path.Combine(Path.GetTempPath(), "LoLHelper", "Download")
            };
            foreach (var candidate in candidates)
            {
                try
                {
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            throw new IOException("无法创建下载目录，请检查磁盘权限。");
        }

        public async Task<string> DownloadAsync(IProgress<DownloadProgressInfo> progress, CancellationToken cancellationToken)
        {
            progress.Report(new DownloadProgressInfo { State = DownloadState.Probing, Message = "正在检测官方下载地址…" });
            var probe = await ResolveAndProbeAsync(cancellationToken).ConfigureAwait(false);
            var metadataPath = Path.Combine(_workDirectory, "installer.download");
            var metadata = LoadMetadata(metadataPath);
            if (metadata == null || metadata.Uri != probe.Uri.AbsoluteUri || metadata.Length != probe.Length || metadata.ETag != probe.ETag || metadata.SupportsRanges != probe.SupportsRanges)
            {
                DeleteWorkingFiles();
                metadata = new DownloadMetadata
                {
                    Uri = probe.Uri.AbsoluteUri,
                    Length = probe.Length,
                    ETag = probe.ETag,
                    SupportsRanges = probe.SupportsRanges
                };
                SaveMetadata(metadataPath, metadata);
            }

            var stopwatch = Stopwatch.StartNew();
            long downloaded = GetExistingLength(metadata);
            long lastBytes = downloaded;
            var lastTime = stopwatch.Elapsed;
            Action report = () =>
            {
                var now = stopwatch.Elapsed;
                var current = Interlocked.Read(ref downloaded);
                var seconds = Math.Max(0.001, (now - lastTime).TotalSeconds);
                var speed = (current - lastBytes) / seconds;
                lastBytes = current;
                lastTime = now;
                progress.Report(new DownloadProgressInfo
                {
                    State = DownloadState.Downloading,
                    DownloadedBytes = current,
                    TotalBytes = metadata.Length,
                    BytesPerSecond = Math.Max(0, speed),
                    Message = probe.SupportsRanges ? "正在分片下载" : "正在普通下载"
                });
            };

            var reportGate = new object();
            var reporting = true;
            using (var timer = new Timer(_ => { lock (reportGate) { if (reporting) report(); } }, null, 0, 300))
            {
                try
                {
                    if (metadata.SupportsRanges && metadata.Length > 0)
                        await DownloadSegmentsAsync(metadata, value => Interlocked.Add(ref downloaded, value), cancellationToken).ConfigureAwait(false);
                    else
                        await DownloadSingleAsync(metadata, value => Interlocked.Add(ref downloaded, value), cancellationToken).ConfigureAwait(false);
                }
                finally { lock (reportGate) { reporting = false; } }
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new DownloadProgressInfo
            {
                State = DownloadState.Verifying,
                DownloadedBytes = downloaded,
                TotalBytes = metadata.Length,
                Message = "正在合并并验证数字签名…"
            });
            var assembled = Path.Combine(_workDirectory, "live.tw2.complete.exe");
            await Task.Run(() => Assemble(metadata, assembled), cancellationToken).ConfigureAwait(false);

            var signature = await Task.Run(() => _signatureVerifier.Verify(assembled, useCache: false), cancellationToken).ConfigureAwait(false);
            if (!signature.IsTrusted || !signature.IsRiot)
            {
                TryDelete(assembled);
                throw new InvalidDataException("安装器数字签名未通过 Riot Games 发布者验证，已禁止执行。" +
                    (string.IsNullOrWhiteSpace(signature.Error) ? string.Empty : " " + signature.Error));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var finalPath = GetUniqueDestination();
            File.Move(assembled, finalPath);
            DeleteWorkingFiles();
            progress.Report(new DownloadProgressInfo
            {
                State = DownloadState.Ready,
                DownloadedBytes = downloaded,
                TotalBytes = metadata.Length,
                Message = "官方安装器已验证",
                FilePath = finalPath
            });
            return finalPath;
        }

        public void CancelAndDelete()
        {
            // The work directory may have been removed between downloading and cancelling.
            if (Directory.Exists(_workDirectory))
                foreach (var file in Directory.GetFiles(_workDirectory, "installer.*")) File.Delete(file);
            var assembled = Path.Combine(_workDirectory, "live.tw2.complete.exe");
            if (File.Exists(assembled)) File.Delete(assembled);
        }

        private async Task<ProbeResult> ResolveAndProbeAsync(CancellationToken token)
        {
            // A probe timeout surfaces as cancellation too, so distinguish it from a real user
            // cancel by checking the caller's token; only the caller may skip the fallback.
            try { return await ProbeAsync(PrimaryUri, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch
            {
                var html = await GetStringValidatedAsync(OfficialPage, token).ConfigureAwait(false);
                var decoded = WebUtility.HtmlDecode(html).Replace("\\u002F", "/").Replace("\\/", "/");
                var matches = Regex.Matches(decoded, "https://[^\\s'\\\"<>]+\\.exe(?:\\?[^\\s'\\\"<>]*)?", RegexOptions.IgnoreCase);
                foreach (Match match in matches)
                {
                    Uri candidate;
                    if (Uri.TryCreate(match.Value, UriKind.Absolute, out candidate) && IsAllowedInstallerUri(candidate))
                    {
                        try { return await ProbeAsync(candidate, token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch { }
                    }
                }
                throw new HttpRequestException("官方下载地址不可用，且未能从 Riot 台湾下载页找到有效的 Windows 安装器。");
            }
        }

        private async Task<ProbeResult> ProbeAsync(Uri uri, CancellationToken token)
        {
            // Header-only request, so a bounded timeout cannot truncate a body, while a stalled
            // handshake still fails fast instead of hanging the probe forever.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(ProbeTimeout);
                using (var response = await SendValidatedAsync(uri, new RangeHeaderValue(0, 0), timeout.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException("下载地址返回 HTTP " + (int)response.StatusCode);
                    var range = response.Content.Headers.ContentRange;
                    var supportsRanges = response.StatusCode == HttpStatusCode.PartialContent && range != null && range.Length.HasValue;
                    var length = supportsRanges ? range!.Length!.Value : response.Content.Headers.ContentLength ?? 0;
                    if (length <= 0) throw new HttpRequestException("服务器未提供有效的安装器大小。");
                    return new ProbeResult
                    {
                        Uri = response.RequestMessage!.RequestUri!,
                        Length = length,
                        SupportsRanges = supportsRanges,
                        ETag = response.Headers.ETag?.Tag ??
                               response.Content.Headers.LastModified?.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture) ??
                               string.Empty
                    };
                }
            }
        }

        private async Task<HttpResponseMessage> SendValidatedAsync(Uri uri, RangeHeaderValue? range, CancellationToken token)
        {
            var current = uri;
            for (var redirect = 0; redirect < 6; redirect++)
            {
                if (!IsAllowedNetworkUri(current)) throw new InvalidDataException("服务器重定向到了非 Riot 官方域名。");
                using (var request = new HttpRequestMessage(HttpMethod.Get, current))
                {
                    request.Headers.Range = range;
                    var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                    {
                        var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(current, response.Headers.Location);
                        response.Dispose();
                        current = next;
                        continue;
                    }
                    if (!IsAllowedInstallerUri(response.RequestMessage!.RequestUri!))
                    {
                        response.Dispose();
                        throw new InvalidDataException("最终下载地址不是 Riot 官方 CDN。");
                    }
                    return response;
                }
            }
            throw new HttpRequestException("官方下载地址重定向次数过多。");
        }

        private async Task<string> GetStringValidatedAsync(Uri uri, CancellationToken token)
        {
            // The landing page is small and only used for link discovery, so it keeps a bounded
            // timeout even though installer bodies no longer do.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                timeout.CancelAfter(ProbeTimeout);
                using (var response = await _client.SendAsync(request, timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if (!IsAllowedNetworkUri(response.RequestMessage!.RequestUri!))
                        throw new InvalidDataException("下载页跳转到了非 Riot 官方域名。");
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task DownloadSegmentsAsync(DownloadMetadata metadata, Action<long> onBytes, CancellationToken token)
        {
            var count = metadata.Length >= 512L * 1024 * 1024 ? 8 : 4;
            var segmentSize = metadata.Length / count;
            var tasks = new List<Task>();
            for (var i = 0; i < count; i++)
            {
                var index = i;
                var start = segmentSize * i;
                var end = i == count - 1 ? metadata.Length - 1 : start + segmentSize - 1;
                tasks.Add(DownloadRangeWithRetryAsync(new Uri(metadata.Uri), index, start, end, metadata.ETag, onBytes, token));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task DownloadRangeWithRetryAsync(Uri uri, int index, long start, long end, string etag, Action<long> onBytes, CancellationToken token)
        {
            var path = SegmentPath(index);
            var expected = end - start + 1;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (existing == expected) return;
                if (existing > expected) { File.Delete(path); onBytes(-existing); existing = 0; }
                try
                {
                    using (var response = await SendValidatedAsync(uri, new RangeHeaderValue(start + existing, end), token).ConfigureAwait(false))
                    {
                        if (response.StatusCode != HttpStatusCode.PartialContent)
                            throw new HttpRequestException("服务器中止了分片下载支持。");
                        if (!string.IsNullOrEmpty(etag) && response.Headers.ETag != null && response.Headers.ETag.Tag != etag)
                            throw new InvalidDataException("远端安装器已更新，请取消后重新下载。");
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 131072, true))
                            await CopyAsync(input, output, onBytes, token).ConfigureAwait(false);
                    }
                    if (new FileInfo(path).Length != expected) throw new IOException("分片长度不完整。");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(600 * Math.Pow(2, attempt) + index * 73), token).ConfigureAwait(false);
                }
            }
        }

        private async Task DownloadSingleAsync(DownloadMetadata metadata, Action<long> onBytes, CancellationToken token)
        {
            var path = SegmentPath(0);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(path))
                    {
                        var discarded = new FileInfo(path).Length;
                        File.Delete(path);
                        onBytes(-discarded);
                    }
                    using (var response = await SendValidatedAsync(new Uri(metadata.Uri), null, token).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 131072, true))
                            await CopyAsync(input, output, onBytes, token).ConfigureAwait(false);
                    }
                    if (new FileInfo(path).Length != metadata.Length) throw new IOException("安装器长度不完整。");
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch when (attempt < 4)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * Math.Pow(2, attempt)), token).ConfigureAwait(false);
                }
            }
        }

        private static async Task CopyAsync(Stream input, Stream output, Action<long> onBytes, CancellationToken token)
        {
            var buffer = new byte[131072];
            while (true)
            {
                var read = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                onBytes(read);
            }
            await output.FlushAsync(token).ConfigureAwait(false);
        }

        private void Assemble(DownloadMetadata metadata, string destination)
        {
            TryDelete(destination);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072))
            {
                var count = metadata.SupportsRanges ? (metadata.Length >= 512L * 1024 * 1024 ? 8 : 4) : 1;
                for (var i = 0; i < count; i++)
                using (var input = new FileStream(SegmentPath(i), FileMode.Open, FileAccess.Read, FileShare.Read, 131072))
                    input.CopyTo(output, 131072);
                output.Flush(true);
                if (output.Length != metadata.Length) throw new InvalidDataException("合并后的安装器大小不正确。");
            }
        }

        private long GetExistingLength(DownloadMetadata metadata)
        {
            var count = metadata.SupportsRanges ? (metadata.Length >= 512L * 1024 * 1024 ? 8 : 4) : 1;
            long total = 0;
            for (var i = 0; i < count; i++)
                if (File.Exists(SegmentPath(i))) total += new FileInfo(SegmentPath(i)).Length;
            return total;
        }

        private string SegmentPath(int index) => Path.Combine(_workDirectory, "installer." + index + ".part");

        private static bool IsAllowedNetworkUri(Uri uri)
        {
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            var host = uri.IdnHost;
            return host.Equals("leagueoflegends.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".leagueoflegends.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".riotgames.com", StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith(".riotcdn.net", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAllowedInstallerUri(Uri uri) =>
            IsAllowedNetworkUri(uri) &&
            uri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            uri.IdnHost.EndsWith(".riotcdn.net", StringComparison.OrdinalIgnoreCase);

        private string GetUniqueDestination()
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            var basePath = Path.Combine(downloads, "live.tw2.exe");
            if (!File.Exists(basePath)) return basePath;
            for (var i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(downloads, "live.tw2 (" + i + ").exe");
                if (!File.Exists(candidate)) return candidate;
            }
            throw new IOException("下载目录中同名文件过多。");
        }

        private static void SaveMetadata(string path, DownloadMetadata metadata)
        {
            var lines = new[]
            {
                Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata.Uri)),
                metadata.Length.ToString(CultureInfo.InvariantCulture),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata.ETag ?? string.Empty)),
                metadata.SupportsRanges ? "1" : "0"
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static DownloadMetadata? LoadMetadata(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path, Encoding.UTF8);
                if (lines.Length != 4) return null;
                return new DownloadMetadata
                {
                    Uri = Encoding.UTF8.GetString(Convert.FromBase64String(lines[0])),
                    Length = long.Parse(lines[1], CultureInfo.InvariantCulture),
                    ETag = Encoding.UTF8.GetString(Convert.FromBase64String(lines[2])),
                    SupportsRanges = lines[3] == "1"
                };
            }
            catch { return null; }
        }

        private void DeleteWorkingFiles()
        {
            try
            {
                foreach (var file in Directory.GetFiles(_workDirectory, "installer.*"))
                    TryDelete(file);
                TryDelete(Path.Combine(_workDirectory, "live.tw2.complete.exe"));
            }
            catch { }
        }

        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        public void Dispose() => _client.Dispose();

        private sealed class ProbeResult
        {
            public Uri Uri { get; set; } = PrimaryUri;
            public long Length { get; set; }
            public bool SupportsRanges { get; set; }
            public string ETag { get; set; } = string.Empty;
        }

        private sealed class DownloadMetadata
        {
            public string Uri { get; set; } = string.Empty;
            public long Length { get; set; }
            public bool SupportsRanges { get; set; }
            public string ETag { get; set; } = string.Empty;
        }
    }
}
