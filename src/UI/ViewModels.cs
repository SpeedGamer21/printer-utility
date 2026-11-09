using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PrinterUtility
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class PrinterVm
    {
        internal PrinterVm(PrinterInfo p) { Info = p; }

        internal PrinterInfo Info { get; }
        public string Name => Info.Name;
        public bool IsDefault => Info.IsDefault;
        public bool IsShared => Info.IsShared && !Info.IsNetwork;
        public string StatusText => Info.StatusText;

        public bool IsVirtual =>
            (Info.Port ?? "").EndsWith(":", StringComparison.Ordinal) && !(Info.Port ?? "").StartsWith("LPT", StringComparison.OrdinalIgnoreCase) &&
            !(Info.Port ?? "").StartsWith("COM", StringComparison.OrdinalIgnoreCase) && !(Info.Port ?? "").StartsWith("USB", StringComparison.OrdinalIgnoreCase);

        public override string ToString() => Name;

        public string Glyph => IsVirtual ?"\uE8A5" : Info.IsNetwork ? "\uE968" : "\uE749";

        public string Subtitle
        {
            get
            {
                var where = Info.IsNetwork ? (Info.ServerName ?? "") : (IsShared ? $"Shared as {Info.ShareName}" : Info.Port);
                return string.Join("  \u2022  ", new[] { Info.TypeText, where, Info.Driver }.WhereNotEmpty());
            }
        }

        public Tone StatusTone
        {
            get
            {
                uint s = Info.Status;
                if (s == 0) return Tone.Success;
                if ((s & (0x2 | 0x8 | 0x10 | 0x40 | 0x80 | 0x1000 | 0x40000 | 0x100000 | 0x400000 | 0x800000)) != 0) return Tone.Error;
                if ((s & (0x400 | 0x4000 | 0x200 | 0x100)) != 0) return Tone.Accent;
                return Tone.Warning;
            }
        }
    }

    public sealed class ShareVm
    {
        internal ShareVm(PrinterInfo p) { Info = p; }
        internal PrinterInfo Info { get; }
        public string Name => Info.Name;
        public string Unc => Info.LocalUnc;
        public override string ToString() => Name;
        public bool IsShared => Info.IsShared;
        public string Caption => Info.IsShared ? $"Shared as {Info.LocalUnc}" + (Info.IsPublished ? "  \u2022  listed in AD" : "") : "Not shared";
        public string Glyph => "\uE749";
    }

    public sealed class ShareRowVm
    {
        public string Name { get; set; }
        public string ShareName { get; set; }
        public override string ToString() => ShareName;
    }

    public sealed class FixVm : Observable
    {
        private bool _selected, _expanded;
        private string _stateText = "Checking\u2026";
        private Tone _stateTone = Tone.Neutral;

        internal FixVm(Fix f) { Fix = f; }
        internal Fix Fix { get; }
        public string Title => Fix.Title;
        public string Id => Fix.Id;
        public string Description => Fix.Description;
        public string SecurityNote => Fix.SecurityNote;
        public bool HasSecurityNote => Fix.SecurityNote != null;
        public bool RequiresAdmin => Fix.RequiresAdmin;
        public bool PerUser => !Fix.RequiresAdmin;
        public bool RestartsSpooler => Fix.RestartSpoolerAfter;

        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
        public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
        public Tone StateTone { get => _stateTone; private set => Set(ref _stateTone, value); }

        internal void Update(FixState s)
        {
            switch (s)
            {
                case FixState.Applied: StateText = "Applied"; StateTone = Tone.Success; break;
                case FixState.Partial: StateText = "Partly applied"; StateTone = Tone.Warning; break;
                case FixState.NotApplied: StateText = "Not applied"; StateTone = Tone.Neutral; break;
                default: StateText = "Unknown"; StateTone = Tone.Neutral; break;
            }
        }
    }

    public sealed class LogEntry
    {
        public string Time { get; set; }
        public string Message { get; set; }
        public Tone Tone { get; set; }
        public string Glyph { get; set; }
        public string Raw { get; set; }

        /// <summary>Parses a Logger line: "HH:mm:ss LEVEL message".</summary>
        public static LogEntry Parse(string line)
        {
            var e = new LogEntry { Raw = line, Time = "", Message = line, Tone = Tone.Neutral, Glyph = "\uE946" };
            if (line.Length >= 15 && line[2] == ':' && line[8] == ' ')
            {
                e.Time = line.Substring(0, 8);
                var level = line.Substring(9, 5).Trim();
                e.Message = line.Substring(15);
                switch (level)
                {
                    case "OK": e.Tone = Tone.Success; e.Glyph = "\uE73E"; break;
                    case "WARN": e.Tone = Tone.Warning; e.Glyph = "\uE7BA"; break;
                    case "ERROR": e.Tone = Tone.Error; e.Glyph = "\uEA39"; break;
                }
            }
            return e;
        }
    }

    internal static class EnumerableExtensions
    {
        public static System.Collections.Generic.IEnumerable<string> WhereNotEmpty(this System.Collections.Generic.IEnumerable<string> items)
        {
            foreach (var i in items) if (!string.IsNullOrWhiteSpace(i)) yield return i;
        }
    }
}
