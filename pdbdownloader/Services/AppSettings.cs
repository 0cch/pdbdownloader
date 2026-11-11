using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace pdbdownloader.Services
{
    internal sealed class AppSettings
    {
        private const string SectionName = "Settings";
        private const string SymbolPathKey = "SymPath";
        private const string TimeoutKey = "Timeout";
        private const string RetryCountKey = "RetryCount";
        private const string ConcurrencyKey = "Concurrency";
        private const string SymbolServerKey = "SymbolServer";

        public const int DefaultTimeoutMilliseconds = 120000;
        public const int DefaultRetryCount = 3;
        public const int DefaultConcurrency = 2;
        public const string DefaultSymbolServerUrl = "https://msdl.microsoft.com/download/symbols/";

        public string SymbolDirectory { get; set; }

        public int TimeoutMilliseconds { get; set; }

        public int RetryCount { get; set; }

        public int Concurrency { get; set; }

        public string SymbolServerUrl { get; set; }

        public static string ConfigurationDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PdbDownloader");
            }
        }

        public static string ConfigurationPath
        {
            get { return Path.Combine(ConfigurationDirectory, "pdbdownloader.ini"); }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings
            {
                TimeoutMilliseconds = DefaultTimeoutMilliseconds,
                RetryCount = DefaultRetryCount,
                Concurrency = DefaultConcurrency,
                SymbolServerUrl = DefaultSymbolServerUrl
            };

            string path = ConfigurationPath;
            bool loadedLegacyConfiguration = false;
            if (!File.Exists(path))
            {
                string legacyPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "pdbdownloader.ini");

                if (File.Exists(legacyPath))
                {
                    path = legacyPath;
                    loadedLegacyConfiguration = true;
                }
            }

            if (!File.Exists(path))
            {
                return settings;
            }

            try
            {
                settings.SymbolDirectory = IniFile.Read(path, SectionName, SymbolPathKey).Trim();

                int timeout;
                if (int.TryParse(IniFile.Read(path, SectionName, TimeoutKey), out timeout))
                {
                    settings.TimeoutMilliseconds = NormalizeTimeout(timeout);
                }

                int retryCount;
                if (int.TryParse(IniFile.Read(path, SectionName, RetryCountKey), out retryCount))
                {
                    settings.RetryCount = NormalizeRetryCount(retryCount);
                }

                int concurrency;
                if (int.TryParse(IniFile.Read(path, SectionName, ConcurrencyKey), out concurrency))
                {
                    settings.Concurrency = NormalizeConcurrency(concurrency);
                }

                string symbolServer = IniFile.Read(path, SectionName, SymbolServerKey).Trim();
                if (!string.IsNullOrWhiteSpace(symbolServer))
                {
                    settings.SymbolServerUrl = NormalizeSymbolServerUrl(symbolServer);
                }

                if (loadedLegacyConfiguration && !string.IsNullOrWhiteSpace(settings.SymbolDirectory))
                {
                    settings.Save();
                }
            }
            catch
            {
                // Configuration failures must not prevent the application from starting.
            }

            return settings;
        }

        public void Save()
        {
            Directory.CreateDirectory(ConfigurationDirectory);
            IniFile.Write(ConfigurationPath, SectionName, SymbolPathKey, SymbolDirectory ?? string.Empty);
            IniFile.Write(ConfigurationPath, SectionName, TimeoutKey, NormalizeTimeout(TimeoutMilliseconds).ToString());
            IniFile.Write(ConfigurationPath, SectionName, RetryCountKey, NormalizeRetryCount(RetryCount).ToString());
            IniFile.Write(ConfigurationPath, SectionName, ConcurrencyKey, NormalizeConcurrency(Concurrency).ToString());
            IniFile.Write(ConfigurationPath, SectionName, SymbolServerKey, NormalizeSymbolServerUrl(SymbolServerUrl));
        }

        public static int NormalizeTimeout(int timeout)
        {
            return timeout >= 5000 && timeout <= 60 * 60 * 1000
                ? timeout
                : DefaultTimeoutMilliseconds;
        }

        public static int NormalizeRetryCount(int retryCount)
        {
            return retryCount >= 1 && retryCount <= 10
                ? retryCount
                : DefaultRetryCount;
        }

        public static int NormalizeConcurrency(int concurrency)
        {
            return concurrency >= 1 && concurrency <= 8
                ? concurrency
                : DefaultConcurrency;
        }

        public static string NormalizeSymbolServerUrl(string symbolServerUrl)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(symbolServerUrl) ||
                !Uri.TryCreate(symbolServerUrl.Trim(), UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return DefaultSymbolServerUrl;
            }

            string normalized = uri.AbsoluteUri;
            return normalized.EndsWith("/", StringComparison.Ordinal) ? normalized : normalized + "/";
        }

        public static bool IsValidSymbolServerUrl(string symbolServerUrl)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(symbolServerUrl) &&
                   Uri.TryCreate(symbolServerUrl.Trim(), UriKind.Absolute, out uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }
    }

    internal static class IniFile
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetPrivateProfileStringW(
            string section,
            string key,
            string defaultValue,
            StringBuilder returnedString,
            uint size,
            string filePath);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WritePrivateProfileStringW(
            string section,
            string key,
            string value,
            string filePath);

        public static string Read(string path, string section, string key)
        {
            StringBuilder value = new StringBuilder(32768);
            GetPrivateProfileStringW(section, key, string.Empty, value, (uint)value.Capacity, path);
            return value.ToString();
        }

        public static void Write(string path, string section, string key, string value)
        {
            if (!WritePrivateProfileStringW(section, key, value ?? string.Empty, path))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}