using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using Microsoft.Win32;
using pdbdownloader.Models;
using pdbdownloader.Services;

namespace pdbdownloader
{
    public partial class MainWindow : Window
    {
        private static readonly HashSet<string> FolderScanExtensions = new HashSet<string>(
            new[] { ".exe", ".dll", ".sys", ".ocx", ".cpl" },
            StringComparer.OrdinalIgnoreCase);

        private readonly ObservableCollection<PdbItem> items = new ObservableCollection<PdbItem>();
        private readonly SymbolIndexService symbolIndexService = new SymbolIndexService();
        private readonly ICollectionView itemsView;
        private AppSettings settings;
        private CancellationTokenSource downloadCancellation;
        private SymbolDownloadService downloadService;
        private List<PdbItem> activeQueue = new List<PdbItem>();
        private bool isDownloading;
        private bool isIndexing;
        private bool closeWhenBusyCompletes;

        public MainWindow()
        {
            InitializeComponent();
            settings = AppSettings.Load();
            itemsView = CollectionViewSource.GetDefaultView(items);
            itemsView.Filter = FilterItem;
            PdbDataGrid.ItemsSource = itemsView;
            items.CollectionChanged += Items_CollectionChanged;

            UpdateSettingsDisplay();
            UpdateSummary();
            UpdateEmptyState();
            UpdateInteractionState();
            SetStatus("Ready");
        }

        private async void addFile_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing || !EnsureSymbolDirectory(false))
            {
                return;
            }

            OpenFileDialog dialog = new OpenFileDialog
            {
                Filter = "Portable executable files (*.exe;*.dll;*.sys;*.ocx;*.cpl)|*.exe;*.dll;*.sys;*.ocx;*.cpl|All files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog(this) == true)
            {
                await AddFilesAsync(dialog.FileNames);
            }
        }

        private async void addFolder_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing || !EnsureSymbolDirectory(false))
            {
                return;
            }

            using (System.Windows.Forms.FolderBrowserDialog dialog =
                new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to scan recursively";
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return;
                }

                isIndexing = true;
                UpdateInteractionState();
                SetStatus("Scanning " + dialog.SelectedPath + "...");
                string[] files;
                try
                {
                    files = await Task.Run(() => EnumerateSupportedFiles(dialog.SelectedPath));
                }
                catch (Exception ex)
                {
                    SetStatus("Could not scan the folder: " + GetReadableError(ex));
                    return;
                }
                finally
                {
                    isIndexing = false;
                    UpdateInteractionState();
                }

                if (closeWhenBusyCompletes)
                {
                    closeWhenBusyCompletes = false;
                    Close();
                    return;
                }

                if (files.Length == 0)
                {
                    SetStatus("No supported PE files were found in the selected folder.");
                    return;
                }

                await AddFilesAsync(files);
            }
        }

        private async Task AddFilesAsync(IEnumerable<string> filePaths)
        {
            if (isDownloading || isIndexing)
            {
                return;
            }

            string[] paths = (filePaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            isIndexing = true;
            UpdateInteractionState();
            SetStatus(string.Format("Indexing {0} file(s)...", paths.Length));

            try
            {
                string symbolDirectory = settings.SymbolDirectory;
                string symbolServerUrl = settings.SymbolServerUrl;
                IndexResult result = await Task.Run(() => IndexFiles(paths, symbolDirectory, symbolServerUrl));

                HashSet<string> identities = new HashSet<string>(
                    items.Select(item => item.Identity),
                    StringComparer.OrdinalIgnoreCase);
                int added = 0;
                int duplicates = 0;

                foreach (PdbItem item in result.Items)
                {
                    if (!identities.Add(item.Identity))
                    {
                        duplicates++;
                        continue;
                    }

                    items.Add(item);
                    added++;
                }

                string summary = string.Format(
                    "Added {0}, duplicate {1}, skipped {2}. Total {3}.",
                    added,
                    duplicates,
                    result.InvalidCount,
                    items.Count);
                if (result.InvalidCount > 0 && !string.IsNullOrWhiteSpace(result.FirstSkipReason))
                {
                    summary += " Last skip reason: " + result.FirstSkipReason;
                }

                SetStatus(summary);
            }
            catch (Exception ex)
            {
                SetStatus("Could not index the selected files: " + GetReadableError(ex));
            }
            finally
            {
                isIndexing = false;
                UpdateSummary();
                UpdateEmptyState();
                UpdateInteractionState();

                if (closeWhenBusyCompletes && !isDownloading)
                {
                    closeWhenBusyCompletes = false;
                    Close();
                }
            }
        }

        private IndexResult IndexFiles(string[] filePaths, string symbolDirectory, string symbolServerUrl)
        {
            IndexResult result = new IndexResult();
            HashSet<string> identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in filePaths)
            {
                try
                {
                    string reason;
                    PdbItem item = symbolIndexService.TryCreate(
                        filePath,
                        symbolDirectory,
                        symbolServerUrl,
                        out reason);
                    if (item == null)
                    {
                        result.InvalidCount++;
                        if (result.FirstSkipReason == null)
                        {
                            result.FirstSkipReason = reason;
                        }
                        continue;
                    }

                    if (identities.Add(item.Identity))
                    {
                        result.Items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    result.InvalidCount++;
                    if (result.FirstSkipReason == null)
                    {
                        result.FirstSkipReason = GetReadableError(ex);
                    }
                }
            }

            return result;
        }

        private async void settings_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing)
            {
                return;
            }

            SettingsWindow dialog = new SettingsWindow(settings)
            {
                Owner = this
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            string previousSymbolDirectory = settings.SymbolDirectory;
            string previousSymbolServer = settings.SymbolServerUrl;
            settings = dialog.ResultSettings;

            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "The settings will be used for this session, but could not be saved:\n" + GetReadableError(ex),
                    "Settings",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            UpdateSettingsDisplay();
            bool mustReindex = items.Count > 0 &&
                (!string.Equals(previousSymbolDirectory, settings.SymbolDirectory, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(previousSymbolServer, settings.SymbolServerUrl, StringComparison.OrdinalIgnoreCase));

            if (mustReindex)
            {
                string[] sourceFiles = items
                    .Select(item => item.FilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                items.Clear();
                await AddFilesAsync(sourceFiles);
            }
            else
            {
                SetStatus("Settings updated.");
            }
        }
        private async void start_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing || items.Count == 0 || !EnsureSymbolDirectory(false))
            {
                return;
            }

            List<PdbItem> queue = items.ToList();
            await StartDownloadsAsync(queue);
        }

        private async void retrySelected_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing)
            {
                return;
            }

            List<PdbItem> queue = GetSelectedItems()
                .Where(item => item.State == PdbState.Failed || item.State == PdbState.Cancelled)
                .ToList();
            if (queue.Count == 0)
            {
                SetStatus("Select one or more failed or cancelled tasks to retry.");
                return;
            }

            await StartDownloadsAsync(queue);
        }

        private async Task StartDownloadsAsync(List<PdbItem> queue)
        {
            if (isDownloading || isIndexing || queue == null || queue.Count == 0 || !EnsureSymbolDirectory(false))
            {
                return;
            }

            isDownloading = true;
            activeQueue = queue;
            downloadCancellation = new CancellationTokenSource();

            try
            {
                downloadService = new SymbolDownloadService(
                    settings.TimeoutMilliseconds,
                    settings.RetryCount);
            }
            catch (Exception ex)
            {
                downloadCancellation.Dispose();
                downloadCancellation = null;
                isDownloading = false;
                activeQueue = new List<PdbItem>();
                UpdateInteractionState();
                SetStatus("Could not initialize the downloader: " + GetReadableError(ex));
                return;
            }

            UpdateInteractionState();
            SetStatus(string.Format(
                "Downloading {0} task(s), up to {1} at a time...",
                queue.Count,
                settings.Concurrency));

            CancellationToken cancellationToken = downloadCancellation.Token;
            int concurrency = settings.Concurrency;

            try
            {
                using (SemaphoreSlim gate = new SemaphoreSlim(concurrency, concurrency))
                {
                    Task[] tasks = queue
                        .Select(item => DownloadItemAsync(item, gate, cancellationToken))
                        .ToArray();
                    await Task.WhenAll(tasks);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                foreach (PdbItem item in queue.Where(item => item.State == PdbState.Pending || item.State == PdbState.Downloading))
                {
                    MarkCancelled(item);
                }
            }
            catch (Exception ex)
            {
                SetStatus("Download session failed: " + GetReadableError(ex));
            }
            finally
            {
                downloadService.Dispose();
                downloadService = null;
                downloadCancellation.Dispose();
                downloadCancellation = null;
                isDownloading = false;

                int completed = queue.Count(item => item.State == PdbState.Completed);
                int failed = queue.Count(item => item.State == PdbState.Failed);
                int cancelled = queue.Count(item => item.State == PdbState.Cancelled);

                TaskbarItemInfo.ProgressValue = 1;
                TaskbarItemInfo.ProgressState = failed > 0
                    ? TaskbarItemProgressState.Error
                    : cancelled > 0
                        ? TaskbarItemProgressState.Paused
                        : TaskbarItemProgressState.Normal;

                UpdateSummary();
                UpdateEmptyState();
                UpdateInteractionState();
                SetStatus(string.Format(
                    "Finished. Completed: {0}, failed: {1}, cancelled: {2}.",
                    completed,
                    failed,
                    cancelled));

                if (closeWhenBusyCompletes)
                {
                    closeWhenBusyCompletes = false;
                    Close();
                }
            }
        }

        private async Task DownloadItemAsync(PdbItem item, SemaphoreSlim gate, CancellationToken cancellationToken)
        {
            bool entered = false;
            try
            {
                await gate.WaitAsync(cancellationToken);
                entered = true;
                cancellationToken.ThrowIfCancellationRequested();

                if (IsUsableExistingPdb(item.PdbPath))
                {
                    item.ProgressValue = 100;
                    item.ProgressColor = Brushes.Blue;
                    item.Status = "Already exists";
                    item.ErrorMessage = null;
                    item.State = PdbState.Completed;
                    RefreshViewAndSummary();
                    return;
                }

                item.ProgressValue = 0;
                item.ProgressColor = Brushes.DodgerBlue;
                item.Status = "Starting...";
                item.ErrorMessage = null;
                item.State = PdbState.Downloading;
                SetStatus("Downloading " + item.PdbFileName + "...");
                RefreshViewAndSummary();

                Progress<SymbolDownloadProgress> progress = new Progress<SymbolDownloadProgress>(
                    update => ApplyProgress(item, update));
                await downloadService.DownloadAsync(item, progress, cancellationToken);

                item.ProgressValue = 100;
                item.ProgressColor = Brushes.SeaGreen;
                item.Status = "Completed";
                item.ErrorMessage = null;
                item.State = PdbState.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                MarkCancelled(item);
            }
            catch (Exception ex)
            {
                item.ProgressColor = Brushes.IndianRed;
                item.Status = "Failed";
                item.ErrorMessage = GetReadableError(ex);
                item.State = PdbState.Failed;
            }
            finally
            {
                if (entered)
                {
                    gate.Release();
                }

                RefreshViewAndSummary();
                UpdateTaskbarProgress();
            }
        }

        private void ApplyProgress(PdbItem item, SymbolDownloadProgress update)
        {
            if (item.State != PdbState.Downloading)
            {
                return;
            }

            if (update.Percentage >= 0)
            {
                item.ProgressValue = update.Percentage;
            }

            item.Status = "Downloading";
            if (update.BytesReceived > 0 && update.TotalBytes > 0)
            {
                SetStatus(string.Format(
                    "{0}: {1} / {2} ({3}%)",
                    item.PdbFileName,
                    FormatBytes(update.BytesReceived),
                    FormatBytes(update.TotalBytes),
                    update.Percentage));
            }
            else if (!string.IsNullOrWhiteSpace(update.Message))
            {
                SetStatus(item.PdbFileName + ": " + update.Message);
            }

            UpdateTaskbarProgress();
        }

        private void stop_Click(object sender, RoutedEventArgs e)
        {
            if (downloadCancellation == null)
            {
                return;
            }

            StopButton.IsEnabled = false;
            SetStatus("Stopping...");
            downloadCancellation.Cancel();
        }

        private void deleteFile_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing)
            {
                return;
            }

            foreach (PdbItem item in GetSelectedItems())
            {
                items.Remove(item);
            }
        }

        private void clearCompleted_Click(object sender, RoutedEventArgs e)
        {
            if (isDownloading || isIndexing)
            {
                return;
            }

            foreach (PdbItem item in items.Where(item => item.State == PdbState.Completed).ToList())
            {
                items.Remove(item);
            }

            SetStatus("Completed tasks cleared.");
        }

        private void openDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            PdbItem selected = GetSelectedItems().FirstOrDefault();
            if (selected != null)
            {
                OpenItemInExplorer(selected);
                return;
            }

            if (Directory.Exists(settings.SymbolDirectory))
            {
                StartExplorer(settings.SymbolDirectory, false);
            }
            else
            {
                SetStatus("The symbol directory does not exist yet.");
            }
        }

        private void PdbDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            PdbItem selected = GetSelectedItems().FirstOrDefault();
            if (selected != null)
            {
                OpenItemInExplorer(selected);
            }
        }

        private void copyPdbPath_Click(object sender, RoutedEventArgs e)
        {
            PdbItem selected = GetSelectedItems().FirstOrDefault();
            if (selected == null)
            {
                return;
            }

            TryCopyToClipboard(selected.PdbPath, "PDB path copied.");
        }

        private void copyUrl_Click(object sender, RoutedEventArgs e)
        {
            PdbItem selected = GetSelectedItems().FirstOrDefault();
            if (selected == null)
            {
                return;
            }

            TryCopyToClipboard(selected.CompressedUri.ToString(), "Source URL copied.");
        }
        private void TryCopyToClipboard(string text, string successMessage)
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                SetStatus(successMessage);
            }
            catch (Exception ex)
            {
                SetStatus("Could not copy to the clipboard: " + GetReadableError(ex));
            }
        }

        private async void PdbListView_Drop(object sender, DragEventArgs e)
        {
            if (isDownloading || isIndexing ||
                !e.Data.GetDataPresent(DataFormats.FileDrop) ||
                !EnsureSymbolDirectory(false))
            {
                return;
            }

            string[] droppedPaths = (string[])e.Data.GetData(DataFormats.FileDrop);
            isIndexing = true;
            UpdateInteractionState();
            SetStatus("Scanning dropped files and folders...");
            string[] files;
            try
            {
                files = await Task.Run(() => ExpandDroppedPaths(droppedPaths));
            }
            catch (Exception ex)
            {
                SetStatus("Could not scan the dropped items: " + GetReadableError(ex));
                return;
            }
            finally
            {
                isIndexing = false;
                UpdateInteractionState();
            }

            if (closeWhenBusyCompletes)
            {
                closeWhenBusyCompletes = false;
                Close();
                return;
            }

            if (files.Length == 0)
            {
                SetStatus("No supported files were found in the dropped items.");
                return;
            }

            await AddFilesAsync(files);
        }

        private void filter_Changed(object sender, EventArgs e)
        {
            if (itemsView == null)
            {
                return;
            }

            itemsView.Refresh();
            UpdateEmptyState();
        }

        private bool FilterItem(object value)
        {
            PdbItem item = value as PdbItem;
            if (item == null)
            {
                return false;
            }

            string query = SearchTextBox == null ? string.Empty : SearchTextBox.Text.Trim();
            if (query.Length > 0 &&
                item.FilePath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                item.PdbFileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                item.PdbPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            ComboBoxItem selectedFilter = FilterComboBox == null
                ? null
                : FilterComboBox.SelectedItem as ComboBoxItem;
            string state = selectedFilter == null ? "All" : Convert.ToString(selectedFilter.Tag);
            return state == "All" || item.State.ToString().Equals(state, StringComparison.OrdinalIgnoreCase);
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.O && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                addFolder_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                addFile_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
            {
                settings_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && !(Keyboard.FocusedElement is TextBox) && !isDownloading && !isIndexing)
            {
                deleteFile_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && isDownloading)
            {
                stop_Click(sender, e);
                e.Handled = true;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (isDownloading || isIndexing)
            {
                e.Cancel = true;
                closeWhenBusyCompletes = true;
                SetStatus(isDownloading ? "Stopping before closing..." : "Finishing scan before closing...");

                if (downloadCancellation != null)
                {
                    StopButton.IsEnabled = false;
                    downloadCancellation.Cancel();
                }

                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            if (downloadService != null)
            {
                downloadService.Dispose();
            }

            if (downloadCancellation != null)
            {
                downloadCancellation.Dispose();
            }

            base.OnClosed(e);
        }

        private bool EnsureSymbolDirectory(bool forcePrompt)
        {
            if (!forcePrompt && !string.IsNullOrWhiteSpace(settings.SymbolDirectory))
            {
                try
                {
                    Directory.CreateDirectory(settings.SymbolDirectory);
                    return true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "The configured symbol directory is not accessible:\n" + GetReadableError(ex) +
                        "\n\nUpdate the symbol directory in Settings before continuing.",
                        "Symbol directory",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }
            }

            using (System.Windows.Forms.FolderBrowserDialog dialog =
                new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Please select the symbol download path";
                if (!string.IsNullOrWhiteSpace(settings.SymbolDirectory) && Directory.Exists(settings.SymbolDirectory))
                {
                    dialog.SelectedPath = settings.SymbolDirectory;
                }

                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                {
                    return false;
                }

                settings.SymbolDirectory = dialog.SelectedPath;
                try
                {
                    settings.Save();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "The symbol path will be used for this session, but could not be saved:\n" +
                        GetReadableError(ex),
                        "Settings",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                UpdateSettingsDisplay();
                return true;
            }
        }

        private void UpdateSettingsDisplay()
        {
            SymbolPathText.Text = string.IsNullOrWhiteSpace(settings.SymbolDirectory)
                ? "Not configured"
                : settings.SymbolDirectory;
            SymbolPathText.ToolTip = SymbolPathText.Text;

            string serverText = settings.SymbolServerUrl ?? AppSettings.DefaultSymbolServerUrl;
            Uri serverUri;
            if (Uri.TryCreate(serverText, UriKind.Absolute, out serverUri) &&
                string.Equals(serverUri.Host, "msdl.microsoft.com", StringComparison.OrdinalIgnoreCase))
            {
                SymbolServerText.Text = "Microsoft Public Symbol Server";
            }
            else
            {
                SymbolServerText.Text = serverText;
            }

            SymbolServerText.ToolTip = serverText;
        }

        private void UpdateSummary()
        {
            if (TotalCountText == null)
            {
                return;
            }

            int completed = items.Count(item => item.State == PdbState.Completed);
            int failed = items.Count(item => item.State == PdbState.Failed);
            int remaining = items.Count - completed - failed;

            TotalCountText.Text = items.Count.ToString();
            CompletedCountText.Text = completed.ToString();
            WaitingCountText.Text = Math.Max(0, remaining).ToString();
            FailedCountText.Text = failed.ToString();
        }

        private void UpdateEmptyState()
        {
            if (EmptyStatePanel == null || itemsView == null)
            {
                return;
            }

            bool empty = itemsView.IsEmpty;
            EmptyStatePanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (!empty)
            {
                return;
            }

            if (items.Count == 0)
            {
                EmptyTitleText.Text = "Drop PE files or folders here";
                EmptyDescriptionText.Text = "Add executables manually, or drag a folder to scan it recursively.";
                EmptyActionsPanel.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyTitleText.Text = "No matching tasks";
                EmptyDescriptionText.Text = "Adjust the search text or status filter to see more tasks.";
                EmptyActionsPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateInteractionState()
        {
            bool busy = isDownloading || isIndexing;
            AddFilesButton.IsEnabled = !busy;
            AddFolderButton.IsEnabled = !busy;
            SettingsButton.IsEnabled = !busy;
            StartButton.IsEnabled = !busy && items.Count > 0;
            StopButton.IsEnabled = isDownloading;
            RemoveSelectedButton.IsEnabled = !busy;
            RetrySelectedButton.IsEnabled = !busy &&
                items.Any(item => item.State == PdbState.Failed || item.State == PdbState.Cancelled);
            ClearCompletedButton.IsEnabled = !busy && items.Any(item => item.State == PdbState.Completed);
            EmptyActionsPanel.IsEnabled = !busy;
            PdbDataGrid.AllowDrop = !busy;
        }
        private void UpdateTaskbarProgress()
        {
            if (!isDownloading || activeQueue == null || activeQueue.Count == 0)
            {
                return;
            }

            double progress = activeQueue.Sum(item => Math.Max(0, Math.Min(100, item.ProgressValue))) /
                              activeQueue.Count / 100.0;
            TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal;
            TaskbarItemInfo.ProgressValue = Math.Max(0, Math.Min(1, progress));
        }

        private void RefreshViewAndSummary()
        {
            itemsView.Refresh();
            UpdateSummary();
            UpdateEmptyState();
            UpdateInteractionState();
        }

        private void Items_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateSummary();
            UpdateEmptyState();
            UpdateInteractionState();
        }

        private void MarkCancelled(PdbItem item)
        {
            item.ProgressColor = Brushes.Orange;
            item.Status = "Cancelled";
            item.ErrorMessage = null;
            item.State = PdbState.Cancelled;
        }

        private List<PdbItem> GetSelectedItems()
        {
            return PdbDataGrid.SelectedItems.Cast<PdbItem>().ToList();
        }

        private void OpenItemInExplorer(PdbItem item)
        {
            string directory = Path.GetDirectoryName(item.PdbPath);
            if (File.Exists(item.PdbPath))
            {
                StartExplorer(item.PdbPath, true);
            }
            else if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                StartExplorer(directory, false);
            }
            else
            {
                SetStatus("The selected PDB has not been downloaded yet.");
            }
        }

        private void StartExplorer(string path, bool selectFile)
        {
            try
            {
                string explorerPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe");
                string arguments = selectFile
                    ? "/select,\"" + path + "\""
                    : "\"" + path + "\"";
                Process.Start(explorerPath, arguments);
            }
            catch (Exception ex)
            {
                SetStatus("Could not open Explorer: " + GetReadableError(ex));
            }
        }

        private static string[] ExpandDroppedPaths(IEnumerable<string> paths)
        {
            List<string> files = new List<string>();
            foreach (string path in paths)
            {
                if (Directory.Exists(path))
                {
                    files.AddRange(EnumerateSupportedFiles(path));
                }
                else if (File.Exists(path))
                {
                    files.Add(path);
                }
            }

            return files.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static string[] EnumerateSupportedFiles(string root)
        {
            List<string> files = new List<string>();
            Stack<string> pendingDirectories = new Stack<string>();
            pendingDirectories.Push(root);

            while (pendingDirectories.Count > 0)
            {
                string directory = pendingDirectories.Pop();
                try
                {
                    foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        try
                        {
                            FileAttributes attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
                            {
                                if ((attributes & FileAttributes.ReparsePoint) != FileAttributes.ReparsePoint)
                                {
                                    pendingDirectories.Push(entry);
                                }
                            }
                            else if (FolderScanExtensions.Contains(Path.GetExtension(entry)))
                            {
                                files.Add(entry);
                            }
                        }
                        catch
                        {
                            // Skip entries that disappear or become inaccessible while scanning.
                        }
                    }
                }
                catch
                {
                    // Skip directories that cannot be enumerated.
                }
            }

            return files.ToArray();
        }

        private void SetStatus(string message)
        {
            statusBarText.Text = message;
        }

        private static bool IsUsableExistingPdb(string path)
        {
            try
            {
                FileInfo file = new FileInfo(path);
                return file.Exists && file.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string GetReadableError(Exception exception)
        {
            AggregateException aggregate = exception as AggregateException;
            if (aggregate != null)
            {
                return string.Join(
                    " | ",
                    aggregate.Flatten().InnerExceptions
                        .Select(GetReadableError)
                        .Distinct(StringComparer.OrdinalIgnoreCase));
            }

            Exception current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return string.Format("{0:0.##} {1}", value, units[unit]);
        }

        private sealed class IndexResult
        {
            public IndexResult()
            {
                Items = new List<PdbItem>();
            }

            public List<PdbItem> Items { get; private set; }

            public int InvalidCount { get; set; }

            public string FirstSkipReason { get; set; }
        }
    }
}