using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OpenSO.Launcher.Services;

public sealed class DownloadService
{
    private readonly int _maxRetries;
    private readonly TimeSpan _retryDelay;
    private readonly TimeSpan _headersTimeout;
    private readonly TimeSpan _stallTimeout;
    private const int DefaultMaxRetries = 15;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultHeadersTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(120);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 }) { Timeout = Timeout.InfiniteTimeSpan };

    public string From { get; }
    public string To { get; }
    public int Retries { get; private set; }
    public long BytesRead { get; private set; }
    public long Length { get; private set; }
    public string? Md5Base64 { get; private set; }
    public double SpeedBytesPerSec { get; private set; }
    public string? ExpectedMd5 { get; }
    public string? ExpectedSha256 { get; }

    public DownloadService(string from, string to, string? expectedMd5 = null, int? maxRetries = null, TimeSpan? stallTimeout = null, TimeSpan? headersTimeout = null, TimeSpan? retryDelay = null, string? expectedSha256 = null)
    {
        From = from; To = to; ExpectedMd5 = expectedMd5; ExpectedSha256 = expectedSha256;
        _maxRetries = maxRetries ?? DefaultMaxRetries; _stallTimeout = stallTimeout ?? DefaultStallTimeout;
        _headersTimeout = headersTimeout ?? DefaultHeadersTimeout; _retryDelay = retryDelay ?? DefaultRetryDelay;
    }

    public async Task RunAsync(IProgress<ProgressReport>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(To)!);
        Exception? last = null;
        for (Retries = 0; Retries <= _maxRetries; Retries++)
        {
            if (Retries > 0)
            {
                long have = File.Exists(To) ? new FileInfo(To).Length / 1048576 : 0;
                progress?.Report(new ProgressReport("download", Fraction(), $"connection slow/interrupted — resuming from {have} MB (attempt {Retries}/{_maxRetries})…"));
                await Task.Delay(_retryDelay, ct);
            }
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                long existing = File.Exists(To) ? new FileInfo(To).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, From);
                ApplyGitHubHeaders(req, From);
                if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
                attemptCts.CancelAfter(_headersTimeout);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                bool resuming = existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (existing > 0 && resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) { BytesRead = existing; Length = existing; await FinishAsync(progress); return; }
                if (existing > 0 && !resuming) { File.Delete(To); existing = 0; }
                resp.EnsureSuccessStatusCode();
                Length = (resp.Content.Headers.ContentLength ?? 0) + (resuming ? existing : 0);
                BytesRead = resuming ? existing : 0;
                await using (var net = await resp.Content.ReadAsStreamAsync(attemptCts.Token))
                await using (var file = new FileStream(To, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[81920]; int n;
                    attemptCts.CancelAfter(_stallTimeout);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    long windowStartBytes = BytesRead;
                    double windowStartMs = 0;
                    double lastReportMs = -1000;
                    while ((n = await net.ReadAsync(buffer, attemptCts.Token)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), ct);
                        BytesRead += n;
                        attemptCts.CancelAfter(_stallTimeout);
                        double nowMs = clock.Elapsed.TotalMilliseconds;
                        if (nowMs - lastReportMs >= 250)
                        {
                            double elapsed = (nowMs - windowStartMs) / 1000.0;
                            if (elapsed >= 0.2)
                            {
                                double sample = (BytesRead - windowStartBytes) / elapsed;
                                SpeedBytesPerSec = SpeedBytesPerSec <= 0 ? sample : SpeedBytesPerSec * 0.7 + sample * 0.3;
                                windowStartBytes = BytesRead;
                                windowStartMs = nowMs;
                            }
                            lastReportMs = nowMs;
                            progress?.Report(new ProgressReport("download", Fraction(), FormatDetail()));
                        }
                    }
                    await file.FlushAsync(ct);
                }
                if (Length > 0 && BytesRead < Length) throw new IOException($"Connection dropped at {BytesRead / 1048576}/{Length / 1048576} MB.");
                await FinishAsync(progress);
                return;
            }
            catch (ChecksumMismatchException) { throw; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex) { last = new TimeoutException($"Attempt stalled: {From}", ex); }
            catch (Exception ex) { last = ex; }
        }
        throw new IOException($"Download failed after {_maxRetries + 1} attempts: {From}. Last error: {last?.Message}", last);
    }

    private async Task FinishAsync(IProgress<ProgressReport>? progress)
    {
        if (BytesRead == 0) throw new IOException("Server returned an empty response.");
        progress?.Report(new ProgressReport("download", 1.0, "verifying…"));
        var (hex, b64) = ComputeMd5(To); Md5Base64 = b64;
        if (!string.IsNullOrEmpty(ExpectedMd5)) { var want = ExpectedMd5.Trim(); bool ok = want.Equals(hex, StringComparison.OrdinalIgnoreCase) || want.Equals(b64, StringComparison.Ordinal); if (!ok) { try { File.Delete(To); } catch { } throw new ChecksumMismatchException($"Checksum mismatch for {From}."); } }
        if (!string.IsNullOrEmpty(ExpectedSha256))
        {
            // Accept GitHub's release-asset digest format ("sha256:<hex>") or a bare hex string.
            var want = ExpectedSha256.Trim();
            if (want.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) want = want["sha256:".Length..];
            var got = ComputeSha256(To);
            if (!want.Equals(got, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(To); } catch { }
                throw new ChecksumMismatchException($"SHA-256 mismatch for {From} — the download may be corrupt or tampered with.");
            }
        }
        progress?.Report(new ProgressReport("download", 1.0, "done"));
        await Task.CompletedTask;
    }

    private double Fraction() => Length > 0 ? Math.Clamp((double)BytesRead / Length, 0, 1) : 0;
    private string FormatDetail() { var size = Length > 0 ? $"{BytesRead / 1048576} / {Length / 1048576} MB" : $"{BytesRead / 1048576} MB"; var speed = FormatSpeed(SpeedBytesPerSec); return speed != null ? $"{size} · {speed}" : size; }
    private static string? FormatSpeed(double bps) { if (bps <= 0) return null; if (bps >= 1048576) return $"{bps / 1048576:0.0} MB/s"; if (bps >= 1024) return $"{bps / 1024:0.0} KB/s"; return $"{bps:0} B/s"; }
    public async Task CleanupAsync() { try { if (File.Exists(To)) File.Delete(To); } catch { } await Task.CompletedTask; }
    private static (string Hex, string Base64) ComputeMd5(string path) { using var md5 = MD5.Create(); using var fs = File.OpenRead(path); var h = md5.ComputeHash(fs); return (Convert.ToHexString(h).ToLowerInvariant(), Convert.ToBase64String(h)); }
    private static string ComputeSha256(string path) { using var sha = SHA256.Create(); using var fs = File.OpenRead(path); return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant(); }

    private static void ApplyGitHubHeaders(HttpRequestMessage req, string url)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", "OpenSO.Launcher");
        req.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        if (url.StartsWith("https://api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            var t = Environment.GetEnvironmentVariable("GITHUB_RATELIMIT_TOKEN");
            if (!string.IsNullOrEmpty(t)) req.Headers.TryAddWithoutValidation("Authorization", $"token {t}");
        }
    }
}

public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException(string message) : base(message) { }
}
