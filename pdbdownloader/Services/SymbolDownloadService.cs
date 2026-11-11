using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using pdbdownloader.Models;

namespace pdbdownloader.Services
{
    internal sealed class SymbolDownloadProgress
    {
        public long BytesReceived { get; set; }

        public long TotalBytes { get; set; }

        public int Percentage { get; set; }

        public string Message { get; set; }
    }

    internal sealed class SymbolDownloadService : IDisposable
    {
        private readonly HttpClient httpClient;
        private readonly int timeoutMilliseconds;
        private readonly int maximumAttempts;

        public SymbolDownloadService(int timeoutMilliseconds, int maximumAttempts)
        {
            this.maximumAttempts = AppSettings.NormalizeRetryCount(maximumAttempts);
            this.timeoutMilliseconds = timeoutMilliseconds > 0
                ? timeoutMilliseconds
                : AppSettings.DefaultTimeoutMilliseconds;

            HttpClientHandler handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            httpClient = new HttpClient(handler);
            httpClient.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Microsoft-Symbol-Server/10.0.10586.567");
            httpClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        }

        public async Task DownloadAsync(
            PdbItem item,
            IProgress<SymbolDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            Exception compressedError;
            try
            {
                await DownloadWithRetriesAsync(
                    item.CompressedUri,
                    item.PdbPath,
                    true,
                    item.PdbFileName,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                compressedError = ex;
            }

            Report(progress, 0, -1, -1, "Compressed PDB unavailable. Trying raw PDB...");

            try
            {
                await DownloadWithRetriesAsync(
                    item.RawUri,
                    item.PdbPath,
                    false,
                    item.PdbFileName,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception rawError)
            {
                throw new IOException(
                    "Neither the compressed nor the raw PDB could be downloaded.",
                    new AggregateException(compressedError, rawError));
            }
        }

        public void Dispose()
        {
            httpClient.Dispose();
        }

        private async Task DownloadWithRetriesAsync(
            Uri uri,
            string destinationPath,
            bool compressed,
            string pdbFileName,
            IProgress<SymbolDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new InvalidOperationException("The PDB destination path has no directory.");
            }

            Directory.CreateDirectory(destinationDirectory);

            Exception lastError = null;
            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string temporaryFile = Path.Combine(
                    destinationDirectory,
                    "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".part");

                using (CancellationTokenSource attemptCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    attemptCancellation.CancelAfter(timeoutMilliseconds);

                    try
                    {
                        await DownloadFileAsync(
                            uri,
                            temporaryFile,
                            compressed ? "compressed" : "raw",
                            progress,
                            attemptCancellation.Token).ConfigureAwait(false);

                        if (compressed)
                        {
                            ExpandCab(
                                temporaryFile,
                                destinationPath,
                                pdbFileName,
                                attemptCancellation.Token);
                        }
                        else
                        {
                            CommitTemporaryFile(temporaryFile, destinationPath);
                        }

                        long completedSize = new FileInfo(destinationPath).Length;
                        Report(progress, completedSize, completedSize, 100, "Downloaded");
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException ex)
                    {
                        lastError = new TimeoutException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "The download timed out after {0} seconds.",
                                timeoutMilliseconds / 1000.0),
                            ex);
                    }
                    catch (HttpStatusException ex) when (!IsTransient(ex.StatusCode))
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                    finally
                    {
                        TryDeleteFile(temporaryFile);
                    }
                }

                if (attempt < maximumAttempts)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                    Report(
                        progress,
                        0,
                        -1,
                        -1,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Retrying in {0:0} second(s) after attempt {1}/{2}...",
                            delay.TotalSeconds,
                            attempt,
                            maximumAttempts));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("The symbol download failed after multiple attempts.", lastError);
        }

        private async Task DownloadFileAsync(
            Uri uri,
            string temporaryFile,
            string source,
            IProgress<SymbolDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            using (HttpResponseMessage response = await httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpStatusException(response.StatusCode);
                }

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                using (Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (FileStream output = new FileStream(
                    temporaryFile,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[81920];
                    long receivedBytes = 0;
                    DateTime lastProgressUpdate = DateTime.MinValue;

                    while (true)
                    {
                        int bytesRead = await input.ReadAsync(
                            buffer,
                            0,
                            buffer.Length,
                            cancellationToken).ConfigureAwait(false);

                        if (bytesRead == 0)
                        {
                            break;
                        }

                        await output.WriteAsync(
                            buffer,
                            0,
                            bytesRead,
                            cancellationToken).ConfigureAwait(false);
                        receivedBytes += bytesRead;

                        DateTime now = DateTime.UtcNow;
                        if ((now - lastProgressUpdate).TotalMilliseconds >= 100)
                        {
                            lastProgressUpdate = now;
                            Report(
                                progress,
                                receivedBytes,
                                totalBytes,
                                CalculatePercentage(receivedBytes, totalBytes),
                                "Downloading " + source + " PDB");
                        }
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Report(progress, receivedBytes, totalBytes > 0 ? totalBytes : receivedBytes, 100, "Downloaded");
                }
            }
        }

        private static int CalculatePercentage(long receivedBytes, long totalBytes)
        {
            if (totalBytes <= 0)
            {
                return -1;
            }

            long percentage = receivedBytes * 100L / totalBytes;
            return (int)Math.Max(0, Math.Min(100, percentage));
        }

        private static bool IsTransient(HttpStatusCode statusCode)
        {
            int code = (int)statusCode;
            return statusCode == HttpStatusCode.RequestTimeout || code == 429 || code >= 500;
        }

        private static void ExpandCab(
            string cabFile,
            string destinationPath,
            string pdbFileName,
            CancellationToken cancellationToken)
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath);
            string extractionDirectory = Path.Combine(
                destinationDirectory,
                ".pdbdownloader-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(extractionDirectory);

            try
            {
                string expandPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "expand.exe");

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = expandPath,
                    Arguments = "-R \"" + cabFile + "\" \"" + extractionDirectory + "\"",
                    WorkingDirectory = extractionDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                string standardOutput;
                string standardError;
                int exitCode;

                using (Process process = new Process())
                {
                    process.StartInfo = startInfo;
                    if (!process.Start())
                    {
                        throw new IOException("Failed to start expand.exe.");
                    }

                    Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> errorTask = process.StandardError.ReadToEndAsync();

                    try
                    {
                        while (!process.WaitForExit(200))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                    catch
                    {
                        try
                        {
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                        }
                        catch
                        {
                        }

                        throw;
                    }

                    exitCode = process.ExitCode;
                    standardOutput = outputTask.GetAwaiter().GetResult();
                    standardError = errorTask.GetAwaiter().GetResult();
                }

                if (exitCode != 0)
                {
                    string detail = (standardError + Environment.NewLine + standardOutput).Trim();
                    if (detail.Length > 500)
                    {
                        detail = detail.Substring(0, 500);
                    }

                    throw new IOException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "expand.exe failed with exit code {0}. {1}",
                            exitCode,
                            detail));
                }

                string extractedPdb = Path.Combine(extractionDirectory, pdbFileName);
                if (!File.Exists(extractedPdb))
                {
                    string[] extractedFiles = Directory.GetFiles(extractionDirectory);
                    if (extractedFiles.Length != 1)
                    {
                        throw new IOException("The compressed PDB did not produce the expected file.");
                    }

                    extractedPdb = extractedFiles[0];
                }

                FileInfo extractedFile = new FileInfo(extractedPdb);
                if (!extractedFile.Exists || extractedFile.Length == 0)
                {
                    throw new IOException("The expanded PDB is missing or empty.");
                }

                CommitTemporaryFile(extractedPdb, destinationPath);
            }
            finally
            {
                TryDeleteDirectory(extractionDirectory);
            }
        }

        private static void CommitTemporaryFile(string temporaryFile, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                File.Replace(temporaryFile, destinationPath, null);
            }
            else
            {
                File.Move(temporaryFile, destinationPath);
            }
        }

        private static void Report(
            IProgress<SymbolDownloadProgress> progress,
            long receivedBytes,
            long totalBytes,
            int percentage,
            string message)
        {
            if (progress == null)
            {
                return;
            }

            progress.Report(new SymbolDownloadProgress
            {
                BytesReceived = receivedBytes,
                TotalBytes = totalBytes,
                Percentage = percentage,
                Message = message
            });
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private sealed class HttpStatusException : Exception
        {
            public HttpStatusException(HttpStatusCode statusCode)
                : base("The symbol server returned HTTP " + (int)statusCode + " (" + statusCode + ").")
            {
                StatusCode = statusCode;
            }

            public HttpStatusCode StatusCode { get; private set; }
        }
    }
}
