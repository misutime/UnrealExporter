using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UE5LibraryBrowser
{
    internal sealed class GltfThumbnailRenderPool : IDisposable
    {
        private const int MaxJobsPerWorkerProcess = 20;
        private static readonly TimeSpan WorkerRenderTimeout = TimeSpan.FromSeconds(90);
        private readonly BlockingCollection<RenderJob> _jobs = new();
        private readonly Thread[] _threads;
        private bool _disposed;

        public GltfThumbnailRenderPool(int workerCount)
        {
            workerCount = Math.Max(1, workerCount);
            _threads = new Thread[workerCount];
            for (var i = 0; i < _threads.Length; i++)
            {
                _threads[i] = new Thread(RenderLoop)
                {
                    IsBackground = true,
                    Name = $"UE5LibraryBrowser GL Thumbnail Worker Bridge {i + 1}"
                };
                _threads[i].Start();
            }
        }

        public Task<RenderResult> RenderAsync(string gltfPath, string outputPath, CancellationToken cancellationToken)
        {
            if (_disposed)
            {
                return Task.FromResult(RenderResult.Fail("常驻渲染池已经关闭。"));
            }

            var completion = new TaskCompletionSource<RenderResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return completion.Task;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(gltfPath))
                {
                    completion.TrySetResult(RenderResult.Fail("没有可渲染的 glTF 预览源。"));
                    return completion.Task;
                }

                _jobs.Add(new RenderJob(gltfPath, outputPath, completion), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                completion.TrySetResult(RenderResult.Fail(ex.Message));
            }

            return completion.Task;
        }

        private void RenderLoop()
        {
            ThumbnailWorkerProcess? worker = null;
            var workerJobCount = 0;
            try
            {
                worker = ThumbnailWorkerProcess.Start();
                foreach (var job in _jobs.GetConsumingEnumerable())
                {
                    try
                    {
                        var result = worker.Render(job.GltfPath, job.OutputPath);
                        if (!result.Success)
                        {
                            TryDeleteTemp(job.OutputPath);
                        }

                        job.Completion.TrySetResult(result);
                        workerJobCount++;
                        if (workerJobCount >= MaxJobsPerWorkerProcess)
                        {
                            // SharpGLTF/OpenGL 在大模型上可能留下较大的进程内存；定期重启 worker，避免长队列越跑越胖。
                            worker.Dispose();
                            worker = ThumbnailWorkerProcess.Start();
                            workerJobCount = 0;
                        }
                    }
                    catch (Exception ex)
                    {
                        TryDeleteTemp(job.OutputPath);
                        job.Completion.TrySetResult(RenderResult.Fail(ex.Message));
                        worker.Dispose();
                        worker = ThumbnailWorkerProcess.Start();
                        workerJobCount = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                while (_jobs.TryTake(out var job))
                {
                    job.Completion.TrySetResult(RenderResult.Fail("常驻渲染 worker 启动失败: " + ex.Message));
                }
            }
            finally
            {
                worker?.Dispose();
            }
        }

        private static void TryDeleteTemp(string outputPath)
        {
            try
            {
                var temp = outputPath + ".tmp.png";
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // 临时 PNG 清理失败不影响后续重新生成。
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _jobs.CompleteAdding();
            while (_jobs.TryTake(out var job))
            {
                job.Completion.TrySetResult(RenderResult.Fail("常驻渲染池已经关闭。"));
            }

            foreach (var thread in _threads)
            {
                try
                {
                    thread.Join(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // 关闭窗口时不等待异常 worker。
                }
            }
        }

        private sealed record RenderJob(string GltfPath, string OutputPath, TaskCompletionSource<RenderResult> Completion);

        private sealed class ThumbnailWorkerProcess : IDisposable
        {
            private readonly Process _process;
            private readonly object _lock = new();

            private ThumbnailWorkerProcess(Process process)
            {
                _process = process;
            }

            public static ThumbnailWorkerProcess Start()
            {
                var dll = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrWhiteSpace(dll) || !File.Exists(dll))
                {
                    throw new FileNotFoundException("无法定位当前 UE5LibraryBrowser dll。");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                startInfo.ArgumentList.Add(dll);
                startInfo.ArgumentList.Add("--thumbnail-worker");

                var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动缩略图 worker。");
                process.ErrorDataReceived += (_, _) => { };
                process.BeginErrorReadLine();
                return new ThumbnailWorkerProcess(process);
            }

            public RenderResult Render(string gltfPath, string outputPath)
            {
                lock (_lock)
                {
                    if (_process.HasExited)
                    {
                        return RenderResult.Fail("缩略图 worker 已退出。");
                    }

                    var request = new ThumbnailWorkerRequest
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        GltfPath = gltfPath,
                        OutputPath = outputPath
                    };
                    _process.StandardInput.WriteLine(JsonSerializer.Serialize(request));
                    _process.StandardInput.Flush();

                    var readTask = _process.StandardOutput.ReadLineAsync();
                    if (!readTask.Wait(WorkerRenderTimeout))
                    {
                        try
                        {
                            if (!_process.HasExited)
                                _process.Kill(true);
                        }
                        catch
                        {
                            // RenderLoop will replace this worker.
                        }

                        throw new TimeoutException($"缩略图 worker 超时 {WorkerRenderTimeout.TotalSeconds:0}s。");
                    }

                    var line = readTask.Result;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        if (_process.HasExited)
                            throw new InvalidOperationException("缩略图 worker 已退出。");

                        return RenderResult.Fail("缩略图 worker 没有返回结果。");
                    }

                    var response = JsonSerializer.Deserialize<ThumbnailWorkerResponse>(line);
                    if (response == null)
                    {
                        return RenderResult.Fail("缩略图 worker 返回了不可解析结果。");
                    }

                    return response.Success
                        ? RenderResult.Ok()
                        : RenderResult.Fail(response.Error);
                }
            }

            public void Dispose()
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.StandardInput.Close();
                        if (!_process.WaitForExit(1500))
                        {
                            _process.Kill(true);
                        }
                    }
                }
                catch
                {
                    // 关闭 worker 时不影响主 UI 退出。
                }
                finally
                {
                    _process.Dispose();
                }
            }
        }

        private sealed class ThumbnailWorkerRequest
        {
            public string Id { get; set; } = "";
            public string GltfPath { get; set; } = "";
            public string OutputPath { get; set; } = "";
        }

        private sealed class ThumbnailWorkerResponse
        {
            public string Id { get; set; } = "";
            public bool Success { get; set; }
            public string Error { get; set; } = "";
        }
    }

    internal readonly record struct RenderResult(bool Success, string Error)
    {
        public static RenderResult Ok() => new(true, "");
        public static RenderResult Fail(string error) => new(false, error ?? "");
    }
}
