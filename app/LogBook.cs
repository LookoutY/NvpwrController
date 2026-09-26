using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;

namespace Nvpwr
{
    public sealed class LogSession : INotifyPropertyChanged
    {
        public readonly StringBuilder Text = new StringBuilder();
        public int Number { get; set; }
        public DateTime Started { get; set; }
        public string Title { get; set; }
        private string status = "RUN";
        public string Label { get { return "#" + Number.ToString("00") + " " + Started.ToString("HH:mm:ss") + "  " + Title + "  [" + status + "]"; } }
        public void Complete(bool success) { status = success ? "OK" : "ERR"; if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("Label")); }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    public sealed class LogBook
    {
        public readonly ObservableCollection<LogSession> Sessions = new ObservableCollection<LogSession>();
        private int sequence;
        public readonly string RunId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        public LogSession Begin(string title)
        {
            var entry = new LogSession { Number = ++sequence, Started = DateTime.Now, Title = title };
            entry.Text.AppendLine("=== " + entry.Started.ToString("yyyy-MM-dd HH:mm:ss") + " | " + title + " ===");
            Sessions.Insert(0, entry);
            if (Sessions.Count > 100) Sessions.RemoveAt(Sessions.Count - 1);
            return entry;
        }
        public void Append(LogSession entry, string text)
        {
            if (entry == null || string.IsNullOrWhiteSpace(text)) return;
            entry.Text.AppendLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + text.Trim());
            if (entry.Text.Length > 150000) entry.Text.Remove(0, entry.Text.Length - 100000);
        }
        public void Save(LogSession entry)
        {
            string directory = AppPaths.Beside("logs", RunId);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, entry.Number.ToString("000") + ".log"), entry.Label + "\r\n" + entry.Text, Encoding.UTF8);
        }
    }
}