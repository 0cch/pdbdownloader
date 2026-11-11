using System;
using System.ComponentModel;
using System.Windows.Media;

namespace pdbdownloader.Models
{
    public enum PdbState
    {
        Pending,
        Downloading,
        Completed,
        Failed,
        Cancelled
    }

    internal sealed class PdbItem : INotifyPropertyChanged
    {
        private double progressValue;
        private PdbState state;
        private string status;
        private Brush progressColor;
        private string errorMessage;

        public PdbItem(
            string filePath,
            string pdbFileName,
            string pdbPath,
            string identity,
            Uri compressedUri,
            Uri rawUri)
        {
            FilePath = filePath;
            PdbFileName = pdbFileName;
            PdbPath = pdbPath;
            Identity = identity;
            CompressedUri = compressedUri;
            RawUri = rawUri;
            state = PdbState.Pending;
            status = "Pending";
            progressColor = Brushes.Gray;
        }

        public string FilePath { get; private set; }

        public string PdbFileName { get; private set; }

        public string PdbPath { get; private set; }

        public string Identity { get; private set; }

        public Uri CompressedUri { get; private set; }

        public Uri RawUri { get; private set; }

        public double ProgressValue
        {
            get { return progressValue; }
            set
            {
                if (Math.Abs(progressValue - value) < 0.01)
                {
                    return;
                }

                progressValue = value;
                OnPropertyChanged("ProgressValue");
            }
        }

        public PdbState State
        {
            get { return state; }
            set
            {
                if (state == value)
                {
                    return;
                }

                state = value;
                OnPropertyChanged("State");
            }
        }

        public string Status
        {
            get { return status; }
            set
            {
                if (status == value)
                {
                    return;
                }

                status = value;
                OnPropertyChanged("Status");
            }
        }

        public Brush ProgressColor
        {
            get { return progressColor; }
            set
            {
                if (ReferenceEquals(progressColor, value))
                {
                    return;
                }

                progressColor = value;
                OnPropertyChanged("ProgressColor");
            }
        }

        public string ErrorMessage
        {
            get { return errorMessage; }
            set
            {
                if (errorMessage == value)
                {
                    return;
                }

                errorMessage = value;
                OnPropertyChanged("ErrorMessage");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }
}
