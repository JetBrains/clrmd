// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using JetBrains.Annotations;

namespace Microsoft.Diagnostics.Runtime.Utilities
{
    public class CustomSymbolLocator : ISymbolLocator
    {
        private static readonly IEnumerable<string> s_microsoftSymbolServers = new[]
        {
            "http://msdl.microsoft.com/download/symbols",
            "http://referencesource.microsoft.com/symbols"
        };
        private readonly IDictionary<FileEntry, string> _cache = new Dictionary<FileEntry, string>();
        private readonly string _cacheLocation;
        private readonly Action<string, object[]> _logMessage;

        private readonly object _lock = new object();
        private readonly ICollection<FileEntry> _missingEntries = new HashSet<FileEntry>();
        private readonly IEnumerable<SymPathElement> _symPathElements;

        public CustomSymbolLocator([NotNull] string cacheLocation, [NotNull] Action<string, object[]> logMessage)
        {
            _cacheLocation = cacheLocation ?? throw new ArgumentNullException(nameof(cacheLocation));
            _logMessage = logMessage ?? throw new ArgumentNullException(nameof(logMessage));

            _symPathElements = GetSymPathElements();
        }

        /// <summary>
        /// The timeout (in milliseconds) used when contacting each individual server. This is not a total timeout for the entire symbol server operation.
        /// </summary>
        public int Timeout { get; set; } = 60000;

        private static IEnumerable<SymPathElement> GetSymPathElements()
        {
            var result = new List<SymPathElement>();
            foreach (var entry in s_microsoftSymbolServers)
                result.Add(new SymPathElement(entry));

            var ntSymbolPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
            var entries = (ntSymbolPath ?? "").Split(';').Where(e => !string.IsNullOrEmpty(e)).ToArray();
            foreach (var entry in entries)
                result.Add(new SymPathElement(entry));

            return result;
        }

        private static bool TryCreateEntry(string fileName, int buildTimeStamp, int imageSize, out FileEntry entry)
        {
            entry = default;
            fileName = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(fileName))
                return false;

            entry = new FileEntry(fileName.ToLower(), buildTimeStamp, imageSize);
            return true;
        }

        public string FindBinary(string fileName, int buildTimeStamp, int imageSize, bool checkProperties = true)
        {
            if (!TryCreateEntry(fileName, buildTimeStamp, imageSize, out FileEntry entry))
                return null;

            lock (_lock)
            {
                if (_cache.TryGetValue(entry, out var result))
                    return result;

                if (_missingEntries.Contains(entry))
                    return null;

                if (ValidateBinary(fileName, buildTimeStamp, imageSize, checkProperties))
                {
                    _cache[entry] = fileName;
                    return fileName;
                }
            }

            // Finally, check the symbol paths.
            string indexPath = null;
            foreach (var element in _symPathElements)
                if (element.IsSymServer)
                {
                    if (indexPath == null)
                        indexPath = GetIndexPath(entry);

                    var target = TryGetFileFromServer(element.Target, indexPath, element.Cache ?? _cacheLocation);
                    if (target == null)
                    {
                        Trace($"Server '{element.Target}' did not have file '{Path.GetFileName(fileName)}' with timestamp={buildTimeStamp:x} and filesize={imageSize:x}.");
                    }
                    else if (ValidateBinary(target, buildTimeStamp, imageSize, checkProperties))
                    {
                        Trace($"Found '{fileName}' on server '{element.Target}'.  Copied to '{target}'.");
                        lock (_lock)
                        {
                            _cache[entry] = target;
                        }

                        return target;
                    }
                }
                else if (element.Target != null)
                {
                    var filePath = Path.Combine(element.Target, fileName);
                    if (ValidateBinary(filePath, buildTimeStamp, imageSize, checkProperties))
                    {
                        Trace($"Found '{fileName}' at '{filePath}'.");
                        lock (_lock)
                        {
                            _cache[entry] = filePath;
                        }

                        return filePath;
                    }
                }

            lock (_lock)
            {
                _missingEntries.Add(entry);
            }

            return null;
        }

        /// <summary>
        /// Validates whether a file on disk matches the properties we expect.
        /// </summary>
        /// <param name="fullPath">The full path on disk of a PEImage to inspect.</param>
        /// <param name="buildTimeStamp">The build timestamp we expect to match.</param>
        /// <param name="imageSize">The build image size we expect to match.</param>
        /// <param name="checkProperties">Whether we should actually validate the imagesize/timestamp or not.</param>
        /// <returns></returns>
        private bool ValidateBinary(string fullPath, int buildTimeStamp, int imageSize, bool checkProperties)
        {
            if (string.IsNullOrEmpty(fullPath))
                return false;

            if (!File.Exists(fullPath))
                return false;

            if (!checkProperties)
                return true;

            try
            {
                using (FileStream stream = File.OpenRead(fullPath))
                {
                    PEImage peImage = new PEImage(stream, false);
                    if (!peImage.IsValid)
                    {
                        Trace($"Rejected file '{fullPath}' because it is not a valid PE image.");
                        return false;
                    }

                    if (peImage.IndexTimeStamp == buildTimeStamp && peImage.IndexFileSize == imageSize)
                        return true;

                    Trace($"Rejected file '{fullPath}' because file size and time stamp did not match.");
                    return false;
                }
            }
            catch (Exception e)
            {
                Trace("Encountered exception {0} while attempting to inspect file '{1}'.", e.GetType().Name, fullPath);
                return false;
            }
        }

        private void Trace(string format, params object[] parameters)
        {
            _logMessage(format, parameters);
        }

        private static string GetIndexPath(FileEntry entry)
        {
            return entry.FileName + @"\" + entry.TimeStamp.ToString("x") + entry.FileSize.ToString("x") + @"\" + entry.FileName;
        }

        private string TryGetFileFromServer(string urlForServer, string fileIndexPath, string cache)
        {
            Debug.Assert(!string.IsNullOrEmpty(cache));
            if (string.IsNullOrEmpty(urlForServer))
                return null;

            var targetPath = Path.Combine(cache, fileIndexPath);

            // See if it is a compressed file by replacing the last character of the name with an _
            var compressedSigPath = fileIndexPath.Substring(0, fileIndexPath.Length - 1) + "_";
            var compressedFilePath = GetPhysicalFileFromServer(urlForServer, compressedSigPath, cache);
            if (compressedFilePath != null)
                try
                {
                    // Decompress it
                    Command.Run("Expand " + Command.Quote(compressedFilePath) + " " + Command.Quote(targetPath));
                    return targetPath;
                }
                catch (Exception e)
                {
                    Trace("Exception encountered while expanding file '{0}': {1}", compressedFilePath, e.Message);
                }
                finally
                {
                    if (File.Exists(compressedFilePath))
                        File.Delete(compressedFilePath);
                }

            // Just try to fetch the file directly
            var ret = GetPhysicalFileFromServer(urlForServer, fileIndexPath, cache);
            if (ret != null)
                return ret;

            // See if we have a file that tells us to redirect elsewhere. 
            var directoryName = Path.GetDirectoryName(fileIndexPath);
            if (directoryName == null)
                return null;

            var filePtrSigPath = Path.Combine(directoryName, "file.ptr");
            var filePtrData = GetPhysicalFileFromServer(urlForServer, filePtrSigPath, cache, true);
            if (filePtrData == null) return null;

            filePtrData = filePtrData.Trim();
            if (filePtrData.StartsWith("PATH:"))
                filePtrData = filePtrData.Substring(5);

            if (!filePtrData.StartsWith("MSG:") && File.Exists(filePtrData))
                using (var fs = File.OpenRead(filePtrData))
                {
                    CopyStreamToFile(fs, filePtrData, targetPath, fs.Length);
                    return targetPath;
                }

            Trace("Error resolving file.ptr: content '{0}' from '{1}.", filePtrData, filePtrSigPath);

            return null;
        }

        private string GetPhysicalFileFromServer(string serverPath, string pdbIndexPath, string symbolCacheDir, bool returnContents = false)
        {
            if (string.IsNullOrEmpty(serverPath))
                return null;

            var fullDestPath = Path.Combine(symbolCacheDir, pdbIndexPath);
            if (File.Exists(fullDestPath))
                return fullDestPath;

            if (serverPath.StartsWith("http:"))
            {
                var fullUri = serverPath + "/" + pdbIndexPath.Replace('\\', '/');
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(fullUri);
                    req.UserAgent = "Microsoft-Symbol-Server/6.13.0009.1140";
                    req.Timeout = Timeout;
                    var response = req.GetResponse();
                    using (var fromStream = response.GetResponseStream())
                    {
                        if (returnContents)
                        {
                            TextReader reader = new StreamReader(fromStream);
                            return reader.ReadToEnd();
                        }

                        CopyStreamToFile(fromStream, fullUri, fullDestPath, response.ContentLength);
                        return fullDestPath;
                    }
                }
                catch (WebException)
                {
                    // A timeout or 404.
                    return null;
                }
            }

            var fullSrcPath = Path.Combine(serverPath, pdbIndexPath);
            if (!File.Exists(fullSrcPath))
                return null;

            if (returnContents)
                try
                {
                    return File.ReadAllText(fullSrcPath);
                }
                catch
                {
                    return "";
                }

            using (var fs = File.OpenRead(fullSrcPath))
            {
                CopyStreamToFile(fs, fullSrcPath, fullDestPath, fs.Length);
            }

            return fullDestPath;
        }

        /// <summary>
        /// Copies a given stream to a file.
        /// </summary>
        /// <param name="input">The stream of data to copy.</param>
        /// <param name="fullSrcPath">The original source location of "stream".  This may be a URL or null.</param>
        /// <param name="fullDestPath">The full destination path to copy the file to.</param>
        /// <param name="size">A hint as to the length of the stream.  This may be 0 or negative if the length is unknown.</param>
        /// <returns>True if the method successfully copied the file, false otherwise.</returns>
        private void CopyStreamToFile(Stream input, string fullSrcPath, string fullDestPath, long size)
        {
            Debug.Assert(input != null);

            try
            {
                var fi = new FileInfo(fullDestPath);
                if (fi.Exists && fi.Length == size)
                    return;

                var folder = Path.GetDirectoryName(fullDestPath);
                Directory.CreateDirectory(folder);

                FileStream file = null;
                try
                {
                    file = new FileStream(fullDestPath, FileMode.OpenOrCreate);
                    var buffer = new byte[2048];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        file.Write(buffer, 0, read);
                }
                finally
                {
                    if (file != null)
                        file.Dispose();
                }
            }
            catch (Exception e)
            {
                try
                {
                    if (File.Exists(fullDestPath))
                        File.Delete(fullDestPath);
                }
                catch
                {
                    // We ignore errors of this nature.
                }

                Trace("Encountered an error while attempting to copy '{0} to '{1}': {2}", fullSrcPath, fullDestPath, e.Message);
            }
        }
    }
}