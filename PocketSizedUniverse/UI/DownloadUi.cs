using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using PocketSizedUniverse.MareConfiguration;
using PocketSizedUniverse.PlayerData.Handlers;
using PocketSizedUniverse.Services;
using PocketSizedUniverse.Services.Mediator;
using Microsoft.Extensions.Logging;
using MonoTorrent.Client;
using System.Collections.Concurrent;
using System.Numerics;

namespace PocketSizedUniverse.UI;

public class DownloadUi : WindowMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly BitTorrentService _bitTorrentService;
    private readonly UiSharedService _uiShared;
    private readonly ConcurrentDictionary<GameObjectHandler, bool> _uploadingPlayers = new();

    public DownloadUi(ILogger<DownloadUi> logger, DalamudUtilService dalamudUtilService, MareConfigService configService,
        BitTorrentService bitTorrentService, MareMediator mediator, UiSharedService uiShared, PerformanceCollectorService performanceCollectorService)
        : base(logger, mediator, "Pocket Sized Universe Downloads", performanceCollectorService)
    {
        _dalamudUtilService = dalamudUtilService;
        _configService = configService;
        _bitTorrentService = bitTorrentService;
        _uiShared = uiShared;

        SizeConstraints = new WindowSizeConstraints()
        {
            MaximumSize = new Vector2(500, 90),
            MinimumSize = new Vector2(500, 90),
        };

        Flags |= ImGuiWindowFlags.NoMove;
        Flags |= ImGuiWindowFlags.NoBackground;
        Flags |= ImGuiWindowFlags.NoInputs;
        Flags |= ImGuiWindowFlags.NoNavFocus;
        Flags |= ImGuiWindowFlags.NoResize;
        Flags |= ImGuiWindowFlags.NoScrollbar;
        Flags |= ImGuiWindowFlags.NoTitleBar;
        Flags |= ImGuiWindowFlags.NoDecoration;
        Flags |= ImGuiWindowFlags.NoFocusOnAppearing;

        DisableWindowSounds = true;

        ForceMainWindow = true;

        IsOpen = true;

        Mediator.Subscribe<GposeStartMessage>(this, (_) => IsOpen = false);
        Mediator.Subscribe<GposeEndMessage>(this, (_) => IsOpen = true);
        Mediator.Subscribe<PlayerUploadingMessage>(this, (msg) =>
        {
            if (msg.IsUploading)
            {
                _uploadingPlayers[msg.Handler] = true;
            }
            else
            {
                _uploadingPlayers.TryRemove(msg.Handler, out _);
            }
        });
    }

    protected override void DrawInternal()
    {
        if (_configService.Current.ShowTransferWindow)
        {
            try
            {
                // BitTorrent uploads (seeding) are tracked differently - just show if anyone is uploading
                if (_uploadingPlayers.Any())
                {
                    var totalUploaders = _uploadingPlayers.Count;

                    UiSharedService.DrawOutlinedFont($"▲", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont($"Seeding for {totalUploaders} player(s)",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);

                    if (_bitTorrentService.GetActiveTorrents().Any()) ImGui.Separator();
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }

            try
            {
                foreach (var item in _bitTorrentService.GetActiveTorrents())
                {
                    UiSharedService.DrawOutlinedFont($"▼", ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                    ImGui.SameLine();
                    var xDistance = ImGui.GetCursorPosX();
                    UiSharedService.DrawOutlinedFont(
                        $"{item.Name} [P:{item.Progress}/S:{item.Peers.Seeds}/L:{item.Peers.Leechs}]",
                        ImGuiColors.DalamudWhite, new Vector4(0, 0, 0, 255), 1);
                }
            }
            catch
            {
                // ignore errors thrown from UI
            }
        }

    //     if (_configService.Current.ShowTransferBars)
    //     {
    //         const int transparency = 100;
    //         const int dlBarBorder = 3;
    //
    //         foreach (var transfer in _bitTorrentService.GetActiveTorrents())
    //         {
    //             var screenPos = _dalamudUtilService.WorldToScreen(transfer.GetGameObject());
    //             if (screenPos == Vector2.Zero) continue;
    //
    //             var totalBytes = transfer.Value.Sum(c => c.Value.TotalBytes);
    //             var transferredBytes = transfer.Value.Sum(c => c.Value.TransferredBytes);
    //
    //             var maxDlText = $"{UiSharedService.ByteToString(totalBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)}";
    //             var textSize = _configService.Current.TransferBarsShowText ? ImGui.CalcTextSize(maxDlText) : new Vector2(10, 10);
    //
    //             int dlBarHeight = _configService.Current.TransferBarsHeight > ((int)textSize.Y + 5) ? _configService.Current.TransferBarsHeight : (int)textSize.Y + 5;
    //             int dlBarWidth = _configService.Current.TransferBarsWidth > ((int)textSize.X + 10) ? _configService.Current.TransferBarsWidth : (int)textSize.X + 10;
    //
    //             var dlBarStart = new Vector2(screenPos.X - dlBarWidth / 2f, screenPos.Y - dlBarHeight / 2f);
    //             var dlBarEnd = new Vector2(screenPos.X + dlBarWidth / 2f, screenPos.Y + dlBarHeight / 2f);
    //             var drawList = ImGui.GetBackgroundDrawList();
    //             drawList.AddRectFilled(
    //                 dlBarStart with { X = dlBarStart.X - dlBarBorder - 1, Y = dlBarStart.Y - dlBarBorder - 1 },
    //                 dlBarEnd with { X = dlBarEnd.X + dlBarBorder + 1, Y = dlBarEnd.Y + dlBarBorder + 1 },
    //                 UiSharedService.Color(0, 0, 0, transparency), 1);
    //             drawList.AddRectFilled(dlBarStart with { X = dlBarStart.X - dlBarBorder, Y = dlBarStart.Y - dlBarBorder },
    //                 dlBarEnd with { X = dlBarEnd.X + dlBarBorder, Y = dlBarEnd.Y + dlBarBorder },
    //                 UiSharedService.Color(220, 220, 220, transparency), 1);
    //             drawList.AddRectFilled(dlBarStart, dlBarEnd,
    //                 UiSharedService.Color(0, 0, 0, transparency), 1);
    //             var dlProgressPercent = transferredBytes / (double)totalBytes;
    //             drawList.AddRectFilled(dlBarStart,
    //                 dlBarEnd with { X = dlBarStart.X + (float)(dlProgressPercent * dlBarWidth) },
    //                 UiSharedService.Color(50, 205, 50, transparency), 1);
    //
    //             if (_configService.Current.TransferBarsShowText)
    //             {
    //                 var downloadText = $"{UiSharedService.ByteToString(transferredBytes, addSuffix: false)}/{UiSharedService.ByteToString(totalBytes)}";
    //                 UiSharedService.DrawOutlinedFont(drawList, downloadText,
    //                     screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
    //                     UiSharedService.Color(255, 255, 255, transparency),
    //                     UiSharedService.Color(0, 0, 0, transparency), 1);
    //             }
    //         }
    //
    //         if (_configService.Current.ShowUploading)
    //         {
    //             foreach (var player in _uploadingPlayers.Select(p => p.Key).ToList())
    //             {
    //                 var screenPos = _dalamudUtilService.WorldToScreen(player.GetGameObject());
    //                 if (screenPos == Vector2.Zero) continue;
    //
    //                 try
    //                 {
    //                     using var _ = _uiShared.UidFont.Push();
    //                     var uploadText = "Uploading";
    //
    //                     var textSize = ImGui.CalcTextSize(uploadText);
    //
    //                     var drawList = ImGui.GetBackgroundDrawList();
    //                     UiSharedService.DrawOutlinedFont(drawList, uploadText,
    //                         screenPos with { X = screenPos.X - textSize.X / 2f - 1, Y = screenPos.Y - textSize.Y / 2f - 1 },
    //                         UiSharedService.Color(255, 255, 0, transparency),
    //                         UiSharedService.Color(0, 0, 0, transparency), 2);
    //                 }
    //                 catch
    //                 {
    //                     // ignore errors thrown on UI
    //                 }
    //             }
    //         }
    //     }
    }

    public override bool DrawConditions()
    {
        if (_uiShared.EditTrackerPosition) return true;
        if (!_configService.Current.ShowTransferWindow && !_configService.Current.ShowTransferBars) return false;
        if (!_bitTorrentService.GetActiveTorrents().Any() && !_uploadingPlayers.Any()) return false;
        if (!IsOpen) return false;
        return true;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        if (_uiShared.EditTrackerPosition)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
            Flags &= ~ImGuiWindowFlags.NoBackground;
            Flags &= ~ImGuiWindowFlags.NoInputs;
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
            Flags |= ImGuiWindowFlags.NoBackground;
            Flags |= ImGuiWindowFlags.NoInputs;
            Flags |= ImGuiWindowFlags.NoResize;
        }

        var maxHeight = ImGui.GetTextLineHeight() * (_configService.Current.ParallelDownloads + 3);
        SizeConstraints = new()
        {
            MinimumSize = new Vector2(300, maxHeight),
            MaximumSize = new Vector2(300, maxHeight),
        };
    }
}