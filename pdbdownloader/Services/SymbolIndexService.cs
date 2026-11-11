using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using pdbdownloader.Models;

namespace pdbdownloader.Services
{
    internal sealed class SymbolIndexService
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SYMSRV_INDEX_INFO
        {
            public int SizeOfStruct;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
            public string File;

            public uint Stripped;
            public uint Timestamp;
            public int Size;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
            public string DbgFile;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 261)]
            public string PdbFile;

            public Guid Guid;
            public int Signature;
            public int Age;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Wow64DisableWow64FsRedirection(ref IntPtr oldValue);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Wow64RevertWow64FsRedirection(IntPtr oldValue);

        [DllImport("dbghelp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SymSrvGetFileIndexInfoW(
            string file,
            ref SYMSRV_INDEX_INFO info,
            uint flags);

        public PdbItem TryCreate(string filePath, string symbolDirectory, string symbolServerUrl, out string reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                reason = "File does not exist.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(symbolDirectory))
            {
                reason = "The symbol directory is not configured.";
                return null;
            }

            Uri symbolServerRoot = new Uri(AppSettings.NormalizeSymbolServerUrl(symbolServerUrl));

            SYMSRV_INDEX_INFO info = new SYMSRV_INDEX_INFO();
            info.SizeOfStruct = Marshal.SizeOf(info);

            if (!TryGetIndexInfo(filePath, ref info))
            {
                reason = "No PDB index information was found.";
                return null;
            }

            string pdbFileName = NormalizePdbFileName(info.PdbFile);
            if (pdbFileName == null)
            {
                reason = "The embedded PDB file name is invalid.";
                return null;
            }

            if (info.Age < 0)
            {
                reason = "The embedded PDB age is invalid.";
                return null;
            }

            string symbolRoot;
            string pdbPath;
            if (!TryBuildLocalPath(symbolDirectory, pdbFileName, info, out symbolRoot, out pdbPath))
            {
                reason = "The resulting PDB path is outside the configured symbol directory.";
                return null;
            }

            string guid = info.Guid.ToString("N").ToUpperInvariant();
            string age = info.Age.ToString("X", CultureInfo.InvariantCulture);
            string indexDirectory = guid + age;
            string compressedFileName = Path.GetFileNameWithoutExtension(pdbFileName) + ".pd_";

            Uri compressedUri = BuildSymbolUri(symbolServerRoot, pdbFileName, indexDirectory, compressedFileName);
            Uri rawUri = BuildSymbolUri(symbolServerRoot, pdbFileName, indexDirectory, pdbFileName);
            string identity = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                pdbFileName.ToUpperInvariant(),
                guid,
                age);

            return new PdbItem(
                filePath,
                pdbFileName,
                pdbPath,
                identity,
                compressedUri,
                rawUri);
        }

        private static bool TryGetIndexInfo(string filePath, ref SYMSRV_INDEX_INFO info)
        {
            IntPtr oldValue = IntPtr.Zero;
            bool redirectionDisabled = Wow64DisableWow64FsRedirection(ref oldValue);

            try
            {
                return SymSrvGetFileIndexInfoW(filePath, ref info, 0);
            }
            finally
            {
                if (redirectionDisabled)
                {
                    Wow64RevertWow64FsRedirection(oldValue);
                }
            }
        }

        private static string NormalizePdbFileName(string embeddedPdbFile)
        {
            if (string.IsNullOrWhiteSpace(embeddedPdbFile))
            {
                return null;
            }

            string fileName = Path.GetFileName(embeddedPdbFile.Replace('/', '\\'));
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName == "." ||
                fileName == ".." ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                fileName.EndsWith(" ", StringComparison.Ordinal) ||
                fileName.EndsWith(".", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(fileName), ".pdb", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return fileName;
        }

        private static bool TryBuildLocalPath(
            string symbolDirectory,
            string pdbFileName,
            SYMSRV_INDEX_INFO info,
            out string symbolRoot,
            out string pdbPath)
        {
            symbolRoot = Path.GetFullPath(symbolDirectory);
            string fullPath = Path.GetFullPath(Path.Combine(
                symbolRoot,
                pdbFileName,
                info.Guid.ToString("N").ToUpperInvariant() + info.Age.ToString("X", CultureInfo.InvariantCulture),
                pdbFileName));

            string rootWithSeparator = symbolRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                       Path.DirectorySeparatorChar;

            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                pdbPath = null;
                return false;
            }

            pdbPath = fullPath;
            return true;
        }

        private static Uri BuildSymbolUri(Uri symbolServerRoot, string fileName, string indexDirectory, string remoteFileName)
        {
            string relativePath = string.Join(
                "/",
                Uri.EscapeDataString(fileName),
                Uri.EscapeDataString(indexDirectory),
                Uri.EscapeDataString(remoteFileName));

            return new Uri(symbolServerRoot, relativePath);
        }
    }
}
