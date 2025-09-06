using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Common.Lua;
using MonoTorrent.Client;
using OtterGui.Table;
using PocketSizedUniverse.Services;
using System.Globalization;

namespace PocketSizedUniverse.UI
{
    public class TorrentTable : Table<TorrentManager>
    {
        private readonly BitTorrentService _torrentService;
        private static readonly HashHeader _hashHeader = new() { Label = "File Hash" };
        private static readonly StateHeader _stateHeader = new() { Label = "State" };
        private static readonly SeedsColumn _seedsColumn = new() { Label = "Seeders" };
        private static readonly LeechColumn _leechColumn = new() { Label = "Leechers" };
        private static readonly ProgressColumn _progressColumn = new() { Label = "Progress" };
        private static readonly FileSizeColumn _fileSizeColumn = new() { Label = "Size" };

        public TorrentTable(IReadOnlyCollection<TorrentManager> torrents) : base("Torrents", torrents, _hashHeader, _stateHeader, _progressColumn, _seedsColumn, _leechColumn, _fileSizeColumn)
        {
            Flags |= ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Resizable | ImGuiTableFlags.Borders | ImGuiTableFlags.Reorderable;
        }

        public class HashHeader : ColumnString<TorrentManager>
        {
            public override float Width => ImGui.CalcTextSize(Label).X + 10;

            public override string ToName(TorrentManager item)
            {
                return item.Name;
            }

            public override void DrawColumn(TorrentManager item, int _)
            {
                ImGui.Text(item.Name);
            }

            public override int Compare(TorrentManager lhs, TorrentManager rhs)
            {
                return string.Compare(lhs.Name, rhs.Name, StringComparison.Ordinal);
            }
        }

        public class StateHeader : ColumnString<TorrentManager>
        {
            public override float Width => ImGui.CalcTextSize(Label).X + 10;

            public override string ToName(TorrentManager item)
            {
                return item.State.ToString();
            }

            public override void DrawColumn(TorrentManager item, int _)
            {
                ImGui.Text(item.State.ToString());
            }

            public override int Compare(TorrentManager lhs, TorrentManager rhs)
            {
                return string.Compare(lhs.State.ToString(), rhs.State.ToString(), StringComparison.Ordinal);
            }
        }

        public class SeedsColumn() : ColumnNumber<TorrentManager>(ComparisonMethod.GreaterEqual)
        {
            public override float Width => ImGui.CalcTextSize(Label).X + 10;

            public override string ToName(TorrentManager item)
            {
                return item.Peers.Seeds.ToString();
            }

            public override int Compare(TorrentManager lhs, TorrentManager rhs)
            {
                return lhs.Peers.Seeds.CompareTo(rhs.Peers.Seeds);
            }

            public override void DrawColumn(TorrentManager item, int _)
            {
                ImGui.Text(item.Peers.Seeds.ToString());
            }
        }

        public class LeechColumn() : ColumnNumber<TorrentManager>(ComparisonMethod.GreaterEqual)
        {
            public override float Width => ImGui.CalcTextSize(Label).X + 10;

            public override string ToName(TorrentManager item)
            {
                return item.Peers.Leechs.ToString();
            }

            public override int Compare(TorrentManager lhs, TorrentManager rhs)
            {
                return lhs.Peers.Leechs.CompareTo(rhs.Peers.Leechs);
            }

            public override void DrawColumn(TorrentManager item, int _)
            {
                ImGui.Text(item.Peers.Leechs.ToString());
            }
        }

        public class ProgressColumn() : ColumnNumber<TorrentManager>(ComparisonMethod.GreaterEqual)
        {
            public override float Width => ImGui.CalcTextSize(Label).X + 10;

            public override string ToName(TorrentManager item)
            {
                return item.Progress.ToString(CultureInfo.InvariantCulture);
            }

            public override int Compare(TorrentManager lhs, TorrentManager rhs)
            {
                return lhs.Progress.CompareTo(rhs.Progress);
            }

            public override void DrawColumn(TorrentManager item, int _)
            {
                ImGui.Text(item.Progress.ToString(CultureInfo.InvariantCulture));
            }
        }

        public class FileSizeColumn : ColumnString<TorrentManager>
        {
            public override float Width => ImGui.CalcTextSize(Label).X + 10;

            public override string ToName(TorrentManager item)
            {
                return FormatSize(item.Files.Sum(x => x.Length));
            }

            public override void DrawColumn(TorrentManager item, int _)
            {
                ImGui.Text(FormatSize(item.Files.Sum(x => x.Length)));
            }

            private string FormatSize(long size)
            {
                return size < 1024 ? $"{size} B" : size < 1024 * 1024 ? $"{size / 1024} KB" : size < 1024 * 1024 * 1024 ? $"{size / (1024 * 1024)} MB" : $"{size / (1024 * 1024 * 1024)} GB";
            }

            public override int Compare(TorrentManager lhs, TorrentManager rhs)
            {
                return lhs.Files.Sum(x => x.Length).CompareTo(rhs.Files.Sum(x => x.Length));
            }
        }
    }
}