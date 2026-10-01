using System;
using System.IO;

namespace ClashResolveAI.Core
{
    public static class Diagnostics
    {
        private static readonly object Gate = new object();
        public static void Log(string message, Exception? error = null)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClashResolveAI", "Logs");
                Directory.CreateDirectory(folder);
                lock (Gate) File.AppendAllText(Path.Combine(folder, DateTime.Today.ToString("yyyy-MM-dd") + ".log"),
                    DateTime.Now.ToString("O") + " " + message + (error == null ? "" : " " + error) + Environment.NewLine);
            }
            catch { /* Logging must never bring down the host. */ }
        }
    }
}
