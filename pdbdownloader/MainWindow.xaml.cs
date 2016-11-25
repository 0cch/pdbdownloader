using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Threading;
using System.Runtime.InteropServices;
using System.Net;
using System.Diagnostics;
using Microsoft.Win32;

namespace pdbdownloader
{  
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>

    [StructLayout(LayoutKind.Sequential, Size = 0x10)]
    public struct _GUID
    {
        public UInt32 Data1;
        public UInt16 Data2;
        public UInt16 Data3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data4;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SYMSRV_INDEX_INFO
    {
        public int sizeofstruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
        public string file;
        public UInt32 stripped;
        public UInt32 timestamp;
        public Int32 size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
        public string dbgfile;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
        public string pdbfile;
        public Guid guid;
        public Int32 sig;
        public Int32 age;
    }

    public partial class MainWindow : Window, IDisposable
    {
        

        [DllImport("dbghelp", CharSet = CharSet.Unicode)]
        static extern bool SymSrvGetFileIndexInfoW(String file, ref SYMSRV_INDEX_INFO info, uint flags);

        public MainWindow()
        {
            InitializeComponent();
            statusBarText.Content = "Ready";
            PdbListView.ItemsSource = m_Items;
        }

        public void Dispose()
        {
            // Dispose your objects here as before.
        }

        private void addFile_Click(object sender, RoutedEventArgs e)
        {
            string ConfigFile = AppDomain.CurrentDomain.BaseDirectory + "pdbdownloader.ini";
            IniFile ini = new IniFile(ConfigFile);
            m_SymbolDir = ini.IniReadValue("Settings", "SymPath");
            if (String.IsNullOrEmpty(m_SymbolDir)) 
            {
                System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
                fbd.Description = "Please select symbol download path";
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    m_SymbolDir = fbd.SelectedPath;
                    ini.IniWriteValue("Settings", "SymPath", m_SymbolDir);
                }
                else
                {
                    return;
                }
            }

            OpenFileDialog addFileDlg = new OpenFileDialog();
            addFileDlg.Filter = "All files (*.*)|*.*";
            addFileDlg.Multiselect = true;
            if (addFileDlg.ShowDialog() == true)
            {
                foreach(string it in addFileDlg.FileNames) 
                {
                    HandleOpenFile(it);
                }
            }
        }

        void HandleOpenFile(string path)
        {
            SYMSRV_INDEX_INFO info = new SYMSRV_INDEX_INFO();
            info.sizeofstruct = Marshal.SizeOf(info);
            if (!SymSrvGetFileIndexInfoW(path, ref info, 0))
            {
                return;
            }

            if (String.IsNullOrEmpty(info.pdbfile) || info.pdbfile.Contains(":"))
            {
                return;
            }

            string cab = info.pdbfile.Replace(".pdb", ".pd_");

            string PdbPath = String.Format("{0}\\{1}\\{2}{3}\\{4}",
                m_SymbolDir, info.pdbfile, info.guid.ToString("N").ToUpper(), info.age.ToString("X"), info.pdbfile);

            string DownloadUrl = String.Format("http://msdl.microsoft.com/download/symbols/{0}/{1}{2}/{3}",
                info.pdbfile, info.guid.ToString("N").ToUpper(), info.age.ToString("X"), cab);

            PdbItem item = new PdbItem(path, PdbPath, DownloadUrl, 0);
            m_Items.Add(item);
        }

        private ObservableCollection<PdbItem> m_Items = new ObservableCollection<PdbItem>();
        private string m_SymbolDir;

        private void exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private bool m_StopDownload;
        private void start_Click(object sender, RoutedEventArgs e)
        {
            if (m_Items.Count == 0)
            {
                return;
            }

            m_StopDownload = false;
            Thread worker = new Thread(DownloadProc);
            worker.Start();
            startMenuItem.IsEnabled = false;
            stopMenuItem.IsEnabled = true;
        }

        private void stop_Click(object sender, RoutedEventArgs e)
        {
            m_StopDownload = true;
            startMenuItem.IsEnabled = true;
            m_Downloader.CancelAsync();
        }

        private void deleteFile_Click(object sender, RoutedEventArgs e)
        {
            System.Collections.IList PdbItems = PdbListView.SelectedItems;
            for (int i = PdbItems.Count - 1; i >= 0; i--)
            {
                ((ObservableCollection<PdbItem>)PdbListView.ItemsSource).Remove((PdbItem)PdbItems[i]);
            }
        }

        private void openDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            System.Collections.IList PdbItems = PdbListView.SelectedItems;
            for (int i = PdbItems.Count - 1; i >= 0; i--)
            {
                string PdbPath = System.IO.Path.GetDirectoryName(((PdbItem)PdbItems[i]).PdbPath);
                Process.Start("explorer.exe", PdbPath);
            }
        }

        private void setSymbolPath_Click(object sender, RoutedEventArgs e)
        {
            string ConfigFile = AppDomain.CurrentDomain.BaseDirectory + "pdbdownloader.ini";
            IniFile ini = new IniFile(ConfigFile);
            m_SymbolDir = ini.IniReadValue("Settings", "SymPath");

            System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();

            if (!String.IsNullOrEmpty(m_SymbolDir))
            {
                fbd.SelectedPath = m_SymbolDir;
            }

            fbd.Description = "Please select symbol download path";
            if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                m_SymbolDir = fbd.SelectedPath;
                ini.IniWriteValue("Settings", "SymPath", m_SymbolDir);
            }
            else
            {
                return;
            }
        }

        private int m_DownloadIndex;
        private WebDownload m_Downloader;
        [DllImport("shell32.dll")]
        static extern int SHCreateDirectoryEx(IntPtr hwnd, string pszPath, IntPtr psa);

        private void DownloadProc()
        {
            string ConfigFile = AppDomain.CurrentDomain.BaseDirectory + "pdbdownloader.ini";
            IniFile ini = new IniFile(ConfigFile);
            int Timeout = 0;
            Int32.TryParse(ini.IniReadValue("Settings", "Timeout"), out Timeout);
            

            for (m_DownloadIndex = 0; m_DownloadIndex < m_Items.Count; m_DownloadIndex++)
            {
                try
                {
                    if (Timeout == 0)
                    {
                        m_Downloader = new WebDownload();
                    }
                    else
                    {
                        m_Downloader = new WebDownload(Timeout);
                    }

                    AutoResetEvent ev = new AutoResetEvent(false);

                    m_Downloader.Headers.Add("user-agent", "Microsoft-Symbol-Server/10.0.10586.567");
                    m_Downloader.DownloadProgressChanged += (s, e) =>
                    {
                        statusBarText.Dispatcher.Invoke(new Action(() =>
                        {
                            statusBarText.Content = "Download: " + String.Format("{0}/{1}({2}%) ",
                                e.BytesReceived, e.TotalBytesToReceive, e.ProgressPercentage) + m_Items[m_DownloadIndex].PdbUrl;
                        }));

                        m_Items[(int)e.UserState].ProgressValue = e.ProgressPercentage;
                    };

                    m_Downloader.DownloadFileCompleted += (s, e) =>
                    {
                        if (e.Cancelled)
                        {

                        }
                        else if (e.Error != null)
                        {
                            m_Items[(int)e.UserState].ProgressColor = Brushes.Red;
                        }
                        else
                        {
                            StringBuilder cab = new StringBuilder(m_Items[(int)e.UserState].PdbPath);
                            cab[cab.Length - 1] = '_';

                            Process process = new Process();
                            process.StartInfo.FileName = "expand.exe";
                            process.StartInfo.Arguments = "expand.exe -R " + cab.ToString();
                            process.StartInfo.CreateNoWindow = true;
                            process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                            process.Start();
                            process.WaitForExit();
                            File.Delete(cab.ToString());

                            m_Items[(int)e.UserState].ProgressColor = Brushes.Blue;
                        }

                        ev.Set();
                    };

                    SHCreateDirectoryEx(IntPtr.Zero, System.IO.Path.GetDirectoryName(m_Items[m_DownloadIndex].PdbPath), IntPtr.Zero);
                    StringBuilder sb = new StringBuilder(m_Items[m_DownloadIndex].PdbPath);
                    sb[sb.Length - 1] = '_';
                    statusBarText.Dispatcher.Invoke(new Action(() => { statusBarText.Content = "Download: " + m_Items[m_DownloadIndex].PdbUrl; }));
                    m_Items[m_DownloadIndex].ProgressColor = new SolidColorBrush(Color.FromArgb(0xff, 0x01, 0xd3, 0x28));
                    m_Items[m_DownloadIndex].ProgressColor.Freeze();
                    m_Downloader.DownloadFileAsync(new Uri(m_Items[m_DownloadIndex].PdbUrl), sb.ToString(), m_DownloadIndex);

                    while (!ev.WaitOne(1000))
                    {
                        if (m_StopDownload)
                        {
                            break;
                        }
                    }

                    if (m_StopDownload)
                    {
                        break;
                    }
                
                }
                finally
                {
                    m_Downloader.Dispose();
                }
                
            }


            statusBarText.Dispatcher.Invoke(new Action(() => { statusBarText.Content = "Finish"; }));
            startMenuItem.Dispatcher.Invoke(new Action(() => { startMenuItem.IsEnabled = true; }));
            stopMenuItem.Dispatcher.Invoke(new Action(() => { stopMenuItem.IsEnabled = false; }));
        }

        private void PdbListView_Drop(object sender, DragEventArgs e)
        {
            string ConfigFile = AppDomain.CurrentDomain.BaseDirectory + "pdbdownloader.ini";
            IniFile ini = new IniFile(ConfigFile);
            m_SymbolDir = ini.IniReadValue("Settings", "SymPath");
            if (String.IsNullOrEmpty(m_SymbolDir))
            {
                System.Windows.Forms.FolderBrowserDialog fbd = new System.Windows.Forms.FolderBrowserDialog();
                fbd.Description = "Please select symbol download path";
                if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    m_SymbolDir = fbd.SelectedPath;
                    ini.IniWriteValue("Settings", "SymPath", m_SymbolDir);
                }
                else
                {
                    return;
                }
            }

            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (string it in files)
            {
                HandleOpenFile(it);
            }
        }
    }


    class PdbItem : INotifyPropertyChanged
    {
        private string filePath;
        public string FilePath
        {
            get { return filePath; }
            set
            {
                filePath = value;
                OnPropertyChanged(new PropertyChangedEventArgs("FilePath"));
            }
        }

        private string pdbPath;
        public string PdbPath
        {
            get { return pdbPath; }
            set
            {
                pdbPath = value;
                OnPropertyChanged(new PropertyChangedEventArgs("PdbPath"));
            }
        }

        private double progressValue;
        public double ProgressValue
        {
            get { return progressValue; }
            set
            {
                progressValue = value;
                OnPropertyChanged(new PropertyChangedEventArgs("ProgressValue"));
            }
        }

        private SolidColorBrush progressColor = new SolidColorBrush(Color.FromArgb(0xff, 0x01, 0xd3, 0x28));
        public SolidColorBrush ProgressColor
        {
            get { return progressColor; }
            set 
            { 
                progressColor = value;
                OnPropertyChanged(new PropertyChangedEventArgs("ProgressColor"));
            }
        }
        private string pdbUrl;
        public string PdbUrl
        {
            get { return pdbUrl; }
            set { pdbUrl = value; }
        }
        public PdbItem(string filePath, string pdbPath, string pdbUrl, double progress)
        {
            PdbPath = pdbPath;
            FilePath = filePath;
            ProgressValue = progress;
            PdbUrl = pdbUrl;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, e);
        }
    }

    public class WebDownload : WebClient
    {
        /// <summary>
        /// Time in milliseconds
        /// </summary>
        public int Timeout { get; set; }

        public WebDownload() : this(60000) { }

        public WebDownload(int timeout)
        {
            this.Timeout = timeout;
        }

        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest request = (WebRequest)base.GetWebRequest(address);
            if (request != null)
            {
                request.Timeout = this.Timeout;
            }
            return request;
        }
    }

    public class IniFile
    {
        public string path;

        [DllImport("kernel32")]
        private static extern long WritePrivateProfileString(string section,
            string key, string val, string filePath);
        [DllImport("kernel32")]
        private static extern int GetPrivateProfileString(string section,
                 string key, string def, StringBuilder retVal,
            int size, string filePath);

        
        public IniFile(string INIPath)
        {
            path = INIPath;
        }
       
        public void IniWriteValue(string Section, string Key, string Value)
        {
            WritePrivateProfileString(Section, Key, Value, this.path);
        }

        public string IniReadValue(string Section, string Key)
        {
            StringBuilder temp = new StringBuilder(255);
            int i = GetPrivateProfileString(Section, Key, "", temp,
                                            255, this.path);
            return temp.ToString();

        }
    }
}
