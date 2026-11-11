using System;
using System.Globalization;
using System.IO;
using System.Windows;
using pdbdownloader.Services;

namespace pdbdownloader
{
    public partial class SettingsWindow : Window
    {
        internal SettingsWindow(AppSettings settings)
        {
            InitializeComponent();

            SymbolPathTextBox.Text = settings.SymbolDirectory ?? string.Empty;
            SymbolServerTextBox.Text = settings.SymbolServerUrl ?? AppSettings.DefaultSymbolServerUrl;
            TimeoutTextBox.Text = (settings.TimeoutMilliseconds / 1000).ToString(CultureInfo.InvariantCulture);
            RetryTextBox.Text = settings.RetryCount.ToString(CultureInfo.InvariantCulture);
            ConcurrencyTextBox.Text = settings.Concurrency.ToString(CultureInfo.InvariantCulture);
        }

        internal AppSettings ResultSettings { get; private set; }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using (System.Windows.Forms.FolderBrowserDialog dialog =
                new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Please select the symbol download path";
                if (Directory.Exists(SymbolPathTextBox.Text))
                {
                    dialog.SelectedPath = SymbolPathTextBox.Text;
                }

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    SymbolPathTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ValidationText.Text = string.Empty;

            string symbolPath = SymbolPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(symbolPath))
            {
                ShowValidation("Select a symbol directory.");
                return;
            }

            try
            {
                symbolPath = Path.GetFullPath(symbolPath);
                Directory.CreateDirectory(symbolPath);
            }
            catch (Exception ex)
            {
                ShowValidation("The symbol directory is not accessible: " + ex.Message);
                return;
            }

            if (!AppSettings.IsValidSymbolServerUrl(SymbolServerTextBox.Text))
            {
                ShowValidation("Enter a valid HTTP or HTTPS symbol server URL.");
                return;
            }

            int timeoutSeconds;
            if (!int.TryParse(TimeoutTextBox.Text.Trim(), out timeoutSeconds) || timeoutSeconds < 5 || timeoutSeconds > 3600)
            {
                ShowValidation("Timeout must be between 5 and 3600 seconds.");
                return;
            }

            int retryCount;
            if (!int.TryParse(RetryTextBox.Text.Trim(), out retryCount) || retryCount < 1 || retryCount > 10)
            {
                ShowValidation("Retry attempts must be between 1 and 10.");
                return;
            }

            int concurrency;
            if (!int.TryParse(ConcurrencyTextBox.Text.Trim(), out concurrency) || concurrency < 1 || concurrency > 8)
            {
                ShowValidation("Parallel downloads must be between 1 and 8.");
                return;
            }

            ResultSettings = new AppSettings
            {
                SymbolDirectory = symbolPath,
                SymbolServerUrl = AppSettings.NormalizeSymbolServerUrl(SymbolServerTextBox.Text),
                TimeoutMilliseconds = timeoutSeconds * 1000,
                RetryCount = retryCount,
                Concurrency = concurrency
            };

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void ShowValidation(string message)
        {
            ValidationText.Text = message;
        }
    }
}