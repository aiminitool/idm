// ============================================================================
// DownloadCore.cs - Lõi tải file đa luồng + resume, chỉ dùng .NET BCL
// Được MainForm (GUI) sử dụng, không phụ thuộc gì vào WinForms.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IDMLikeDownloaderGui
{
    public class ChunkInfo
    {
        public int Index { get; set; }
        public long Start { get; set; }
        public long End { get; set; }          // inclusive, -1 nếu không rõ dung lượng
        public long Length => End - Start + 1;
    }

    public class DownloadState
    {
        public string Url { get; set; } = "";
        public long TotalSize { get; set; }
        public bool SupportsRange { get; set; }
        public List<ChunkInfo> Chunks { get; set; } = new();
    }

    public enum DownloadStatus
    {
        Waiting,     // Chờ
        Probing,     // Đang chuẩn bị
        Downloading, // Đang tải
        Paused,      // Đã tạm dừng
        Completed,   // Hoàn tất
        Error        // Lỗi
    }

    /// <summary>
    /// Một tác vụ tải file: đa luồng (nếu server hỗ trợ Range), có resume.
    /// Dùng chung 1 HttpClient tĩnh cho mọi downloader (khuyến nghị của .NET).
    /// </summary>
    public class MultiThreadDownloader
    {
        private static readonly HttpClient Http = CreateHttpClient();

        public string Url { get; }
        public string OutputPath { get; }
        public string FileName => Path.GetFileName(OutputPath);
        public int RequestedThreads { get; }

        public long TotalSize { get; private set; } = -1;
        public long TotalDownloaded => Interlocked.Read(ref _totalDownloaded);
        public double CurrentSpeedBytesPerSec { get; private set; }
        public DownloadStatus Status { get; private set; } = DownloadStatus.Waiting;
        public string? ErrorMessage { get; private set; }

        /// <summary>Báo cho UI biết cần cập nhật hiển thị (có thể gọi từ thread nền).</summary>
        public event Action<MultiThreadDownloader>? Updated;

        private readonly string _statePath;
        private long _totalDownloaded;
        private CancellationTokenSource? _cts;
        private bool _isRunning;

        public MultiThreadDownloader(string url, string outputPath, int threads)
        {
            Url = url;
            OutputPath = outputPath;
            RequestedThreads = Math.Max(1, threads);
            _statePath = outputPath + ".ntdm.json";
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.None
            };
            var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; IDMLikeDownloaderGui/1.0)");
            return client;
        }

        /// <summary>Bắt đầu hoặc tiếp tục (resume) tải file. An toàn khi gọi nhiều lần.</summary>
        public async Task StartAsync()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            SetStatus(DownloadStatus.Probing);
            try
            {
                var state = await LoadOrCreateStateAsync(token);
                TotalSize = state.TotalSize;
                Interlocked.Exchange(ref _totalDownloaded, SumExistingBytes(state));
                SetStatus(DownloadStatus.Downloading);

                var progressLoop = ReportProgressLoopAsync(token);

                if (state.SupportsRange && state.Chunks.Count > 1)
                {
                    var tasks = state.Chunks.Select(c => DownloadChunkAsync(c, token)).ToArray();
                    await Task.WhenAll(tasks);
                }
                else
                {
                    await DownloadChunkAsync(state.Chunks[0], token);
                }

                _cts.Cancel(); // dừng vòng lặp report progress
                await SafeAwaitAsync(progressLoop);

                MergeChunks(state);
                TryDeleteFile(_statePath);
                CurrentSpeedBytesPerSec = 0;
                SetStatus(DownloadStatus.Completed);
            }
            catch (OperationCanceledException)
            {
                CurrentSpeedBytesPerSec = 0;
                SetStatus(DownloadStatus.Paused);
            }
            catch (Exception ex)
            {
                CurrentSpeedBytesPerSec = 0;
                ErrorMessage = ex.Message;
                SetStatus(DownloadStatus.Error);
            }
            finally
            {
                _isRunning = false;
            }
        }

        /// <summary>Tạm dừng (giữ nguyên phần đã tải để resume sau).</summary>
        public void Pause() => _cts?.Cancel();

        /// <summary>Xoá toàn bộ file tạm + trạng thái. Chỉ gọi khi không còn đang chạy.</summary>
        public void DeleteFiles()
        {
            if (File.Exists(_statePath))
            {
                try
                {
                    var json = File.ReadAllText(_statePath);
                    var state = JsonSerializer.Deserialize<DownloadState>(json);
                    if (state != null)
                        foreach (var c in state.Chunks)
                            TryDeleteFile(PartPath(c.Index));
                }
                catch { /* bỏ qua */ }
                TryDeleteFile(_statePath);
            }
            else
            {
                TryDeleteFile(PartPath(0));
            }
        }

        // --------------------------------------------------------------
        private async Task<DownloadState> LoadOrCreateStateAsync(CancellationToken ct)
        {
            var (totalSize, supportsRange) = await ProbeServerAsync(ct);

            if (File.Exists(_statePath))
            {
                try
                {
                    var json = File.ReadAllText(_statePath);
                    var old = JsonSerializer.Deserialize<DownloadState>(json);
                    if (old != null && old.Url == Url && old.TotalSize == totalSize)
                        return old;
                }
                catch { /* state hỏng, tạo lại */ }
            }

            var state = new DownloadState
            {
                Url = Url,
                TotalSize = totalSize,
                SupportsRange = supportsRange,
                Chunks = BuildChunks(totalSize, supportsRange ? RequestedThreads : 1)
            };
            SaveState(state);
            return state;
        }

        private List<ChunkInfo> BuildChunks(long totalSize, int threads)
        {
            var chunks = new List<ChunkInfo>();
            if (totalSize <= 0)
            {
                chunks.Add(new ChunkInfo { Index = 0, Start = 0, End = -1 });
                return chunks;
            }

            long chunkSize = totalSize / threads;
            long start = 0;
            for (int i = 0; i < threads; i++)
            {
                long end = (i == threads - 1) ? totalSize - 1 : start + chunkSize - 1;
                chunks.Add(new ChunkInfo { Index = i, Start = start, End = end });
                start = end + 1;
            }
            return chunks;
        }

        private async Task<(long totalSize, bool supportsRange)> ProbeServerAsync(CancellationToken ct)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, Url);
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                {
                    long size = resp.Content.Headers.ContentLength ?? -1;
                    bool acceptsRanges = resp.Headers.AcceptRanges.Contains("bytes");
                    if (size > 0) return (size, acceptsRanges);
                }
            }
            catch { /* thử GET Range bên dưới */ }

            using var req2 = new HttpRequestMessage(HttpMethod.Get, Url);
            req2.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp2 = await Http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
            bool supportsRange = resp2.StatusCode == System.Net.HttpStatusCode.PartialContent;
            long total = resp2.Content.Headers.ContentRange?.Length
                         ?? resp2.Content.Headers.ContentLength
                         ?? -1;
            return (total, supportsRange);
        }

        private async Task DownloadChunkAsync(ChunkInfo chunk, CancellationToken ct)
        {
            string partPath = PartPath(chunk.Index);
            long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            if (chunk.End >= 0 && existing >= chunk.Length)
                return; // đoạn này đã tải xong từ trước

            const int maxRetries = 5;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, Url);
                    long rangeStart = chunk.Start + existing;
                    if (chunk.End >= 0)
                        req.Headers.Range = new RangeHeaderValue(rangeStart, chunk.End);

                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
                    await using var fileStream = new FileStream(
                        partPath, existing > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        Interlocked.Add(ref _totalDownloaded, read);
                    }
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) when (attempt < maxRetries)
                {
                    existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                }
            }

            throw new IOException($"Đoạn {chunk.Index} tải thất bại sau {maxRetries} lần thử.");
        }

        private void MergeChunks(DownloadState state)
        {
            using (var output = new FileStream(OutputPath, FileMode.Create, FileAccess.Write))
            {
                foreach (var chunk in state.Chunks.OrderBy(c => c.Index))
                {
                    using var partStream = new FileStream(PartPath(chunk.Index), FileMode.Open, FileAccess.Read);
                    partStream.CopyTo(output);
                }
            }
            foreach (var chunk in state.Chunks)
                TryDeleteFile(PartPath(chunk.Index));
        }

        private async Task ReportProgressLoopAsync(CancellationToken ct)
        {
            long last = Interlocked.Read(ref _totalDownloaded);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(500, ct); }
                catch (OperationCanceledException) { break; }

                long current = Interlocked.Read(ref _totalDownloaded);
                double seconds = sw.Elapsed.TotalSeconds;
                CurrentSpeedBytesPerSec = seconds > 0 ? (current - last) / seconds : 0;
                last = current;
                sw.Restart();
                Updated?.Invoke(this);
            }
        }

        private void SetStatus(DownloadStatus status)
        {
            Status = status;
            Updated?.Invoke(this);
        }

        private string PartPath(int index) => $"{OutputPath}.part{index}";

        private void SaveState(DownloadState state)
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }

        private long SumExistingBytes(DownloadState state)
        {
            long sum = 0;
            foreach (var c in state.Chunks)
            {
                string p = PartPath(c.Index);
                if (File.Exists(p)) sum += new FileInfo(p).Length;
            }
            return sum;
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* bỏ qua */ }
        }

        private static async Task SafeAwaitAsync(Task t)
        {
            try { await t; } catch { /* bỏ qua lỗi khi đang dừng */ }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "?";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int i = 0;
            while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
            return $"{size:0.##} {units[i]}";
        }

        public static string GuessFileName(string url)
        {
            try
            {
                var uri = new Uri(url);
                var name = Path.GetFileName(uri.LocalPath);
                return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
            }
            catch { return "download.bin"; }
        }
    }
}
