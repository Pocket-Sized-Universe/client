using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using PocketSizedUniverse.API.Data.Enum;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.MareConfiguration;
using PocketSizedUniverse.Services;
using PocketSizedUniverse.Services.Mediator;
using PocketSizedUniverse.Utils;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace PocketSizedUniverse.UI;

public class DataAnalysisUi : WindowMediatorSubscriberBase
{
    private readonly CharacterAnalyzer _characterAnalyzer;
    private readonly Progress<(string, int)> _conversionProgress = new();
    private readonly IpcManager _ipcManager;
    private readonly UiSharedService _uiSharedService;
    private readonly PlayerPerformanceConfigService _playerPerformanceConfig;
    private readonly TransientConfigService _transientConfigService;
    private readonly Dictionary<string, string[]> _texturesToConvert = new(StringComparer.Ordinal);
    private CancellationTokenSource _conversionCancellationTokenSource = new();
    private string _conversionCurrentFileName = string.Empty;
    private int _conversionCurrentFileProgress = 0;
    private Task? _conversionTask;
    private bool _enableBc7ConversionMode = false;
    private bool _hasUpdate = false;
    private bool _modalOpen = false;
    private string _selectedFileTypeTab = string.Empty;
    private string _selectedHash = string.Empty;
    private ObjectKind _selectedObjectTab;
    private bool _showModal = false;
    private CancellationTokenSource _transientRecordCts = new();

    public DataAnalysisUi(ILogger<DataAnalysisUi> logger, MareMediator mediator,
        CharacterAnalyzer characterAnalyzer, IpcManager ipcManager,
        PerformanceCollectorService performanceCollectorService, UiSharedService uiSharedService,
        PlayerPerformanceConfigService playerPerformanceConfig,
        TransientConfigService transientConfigService)
        : base(logger, mediator, "PSU Character Data Analysis", performanceCollectorService)
    {
        _characterAnalyzer = characterAnalyzer;
        _ipcManager = ipcManager;
        _uiSharedService = uiSharedService;
        _playerPerformanceConfig = playerPerformanceConfig;
        _transientConfigService = transientConfigService;
        Mediator.Subscribe<CharacterDataAnalyzedMessage>(this, (_) =>
        {
            _hasUpdate = true;
        });
        SizeConstraints = new()
        {
            MinimumSize = new()
            {
                X = 800,
                Y = 600
            },
            MaximumSize = new()
            {
                X = 3840,
                Y = 2160
            }
        };

        _conversionProgress.ProgressChanged += ConversionProgress_ProgressChanged;
    }

    protected override void DrawInternal()
    {
        if (_conversionTask != null && !_conversionTask.IsCompleted)
        {
            _showModal = true;
            if (ImGui.BeginPopupModal("BC7 Conversion in Progress"))
            {
                ImGui.TextUnformatted("BC7 Conversion in progress: " + _conversionCurrentFileProgress + "/" + _texturesToConvert.Count);
                UiSharedService.TextWrapped("Current file: " + _conversionCurrentFileName);
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.StopCircle, "Cancel conversion"))
                {
                    _conversionCancellationTokenSource.Cancel();
                }
                UiSharedService.SetScaledWindowSize(500);
                ImGui.EndPopup();
            }
            else
            {
                _modalOpen = false;
            }
        }
        else if (_conversionTask != null && _conversionTask.IsCompleted && _texturesToConvert.Count > 0)
        {
            _conversionTask = null;
            _texturesToConvert.Clear();
            _showModal = false;
            _modalOpen = false;
            _enableBc7ConversionMode = false;
        }

        if (_showModal && !_modalOpen)
        {
            ImGui.OpenPopup("BC7 Conversion in Progress");
            _modalOpen = true;
        }

        if (_hasUpdate)
        {
            _hasUpdate = false;
        }

        using var tabBar = ImRaii.TabBar("analysisRecordingTabBar");
        using (var tabItem = ImRaii.TabItem("Analysis"))
        {
            if (tabItem)
            {
                using var id = ImRaii.PushId("analysis");
            }
        }
        using (var tabItem = ImRaii.TabItem("Transient Files"))
        {
            if (tabItem)
            {
                using var tabbar = ImRaii.TabBar("transientData");

                using (var transientData = ImRaii.TabItem("Stored Transient File Data"))
                {
                    using var id = ImRaii.PushId("data");

                    if (transientData)
                    {
                        DrawStoredData();
                    }
                }
                using (var transientRecord = ImRaii.TabItem("Record Transient Data"))
                {
                    using var id = ImRaii.PushId("recording");
                }
            }
        }
    }

    private bool _showAlreadyAddedTransients = false;
    private bool _acknowledgeReview = false;
    private string _selectedStoredCharacter = string.Empty;
    private string _selectedJobEntry = string.Empty;
    private readonly List<string> _storedPathsToRemove = [];
    private readonly Dictionary<string, string> _filePathResolve = [];
    private string _filterGamePath = string.Empty;
    private string _filterFilePath = string.Empty;

    private void DrawStoredData()
    {
        UiSharedService.DrawTree("What is this? (Explanation / Help)", () =>
        {
            UiSharedService.TextWrapped("This tab allows you to see which transient files are attached to your character.");
            UiSharedService.TextWrapped("Transient files are files that cannot be resolved to your character permanently. PSU gathers these files in the background while you execute animations, VFX, sound effects, etc.");
            UiSharedService.TextWrapped("When sending your character data to others, PSU will combine the files listed in \"All Jobs\" and the corresponding currently used job.");
            UiSharedService.TextWrapped("The purpose of this tab is primarily informational for you to see which files you are carrying with you. You can remove added game paths, however if you are using the animations etc. again, "
                + "Mare will automatically attach these after using them. If you disable associated mods in Penumbra, the associated entries here will also be deleted automatically.");
        });

        ImGuiHelpers.ScaledDummy(5);

        var config = _transientConfigService.Current.TransientConfigs;
        Vector2 availableContentRegion = Vector2.Zero;
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("Character");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            availableContentRegion = ImGui.GetContentRegionAvail();
            using (ImRaii.ListBox("##characters", new Vector2(200, availableContentRegion.Y)))
            {
                foreach (var entry in config)
                {
                    var name = entry.Key.Split("_");
                    if (!_uiSharedService.WorldData.TryGetValue(ushort.Parse(name[1]), out var worldname))
                    {
                        continue;
                    }
                    if (ImGui.Selectable(name[0] + " (" + worldname + ")", string.Equals(_selectedStoredCharacter, entry.Key, StringComparison.Ordinal)))
                    {
                        _selectedStoredCharacter = entry.Key;
                        _selectedJobEntry = string.Empty;
                        _storedPathsToRemove.Clear();
                        _filePathResolve.Clear();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
            }
        }
        ImGui.SameLine();
        bool selectedData = config.TryGetValue(_selectedStoredCharacter, out var transientStorage) && transientStorage != null;
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("Job");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            using (ImRaii.ListBox("##data", new Vector2(150, availableContentRegion.Y)))
            {
                if (selectedData)
                {
                    if (ImGui.Selectable("All Jobs", string.Equals(_selectedJobEntry, "alljobs", StringComparison.Ordinal)))
                    {
                        _selectedJobEntry = "alljobs";
                    }
                    foreach (var job in transientStorage!.JobSpecificCache)
                    {
                        if (!_uiSharedService.JobData.TryGetValue(job.Key, out var jobName)) continue;
                        if (ImGui.Selectable(jobName, string.Equals(_selectedJobEntry, job.Key.ToString(), StringComparison.Ordinal)))
                        {
                            _selectedJobEntry = job.Key.ToString();
                            _storedPathsToRemove.Clear();
                            _filePathResolve.Clear();
                            _filterFilePath = string.Empty;
                            _filterGamePath = string.Empty;
                        }
                    }
                }
            }
        }
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            var selectedList = string.Equals(_selectedJobEntry, "alljobs", StringComparison.Ordinal)
                ? config[_selectedStoredCharacter].GlobalPersistentCache
                : (string.IsNullOrEmpty(_selectedJobEntry) ? [] : config[_selectedStoredCharacter].JobSpecificCache[uint.Parse(_selectedJobEntry)]);
            ImGui.TextUnformatted($"Attached Files (Total Files: {selectedList.Count})");
            ImGui.Separator();
            ImGuiHelpers.ScaledDummy(3);
            using (ImRaii.Disabled(string.IsNullOrEmpty(_selectedJobEntry)))
            {

                var restContent = availableContentRegion.X - ImGui.GetCursorPosX();
                using var group = ImRaii.Group();
                if (_uiSharedService.IconTextButton(FontAwesomeIcon.ArrowRight, "Resolve Game Paths to used File Paths"))
                {
                    _ = Task.Run(async () =>
                    {
                        var paths = selectedList.ToArray();
                        var resolved = await _ipcManager.Penumbra.ResolvePathsAsync(paths, []).ConfigureAwait(false);
                        _filePathResolve.Clear();

                        for (int i = 0; i < resolved.forward.Length; i++)
                        {
                            _filePathResolve[paths[i]] = resolved.forward[i];
                        }
                    });
                }
                ImGui.SameLine();
                ImGuiHelpers.ScaledDummy(20, 1);
                ImGui.SameLine();
                using (ImRaii.Disabled(!_storedPathsToRemove.Any()))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Remove selected Game Paths"))
                    {
                        foreach (var item in _storedPathsToRemove)
                        {
                            selectedList.Remove(item);
                        }

                        _transientConfigService.Save();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
                ImGui.SameLine();
                using (ImRaii.Disabled(!UiSharedService.CtrlPressed()))
                {
                    if (_uiSharedService.IconTextButton(FontAwesomeIcon.Trash, "Clear ALL Game Paths"))
                    {
                        selectedList.Clear();
                        _transientConfigService.Save();
                        _filterFilePath = string.Empty;
                        _filterGamePath = string.Empty;
                    }
                }
                UiSharedService.AttachToolTip("Hold CTRL to delete all game paths from the displayed list"
                    + UiSharedService.TooltipSeparator + "You usually do not need to do this. All animation and VFX data will be automatically handled through Mare.");
                ImGuiHelpers.ScaledDummy(5);
                ImGuiHelpers.ScaledDummy(30);
                ImGui.SameLine();
                ImGui.SetNextItemWidth((restContent - 30) / 2f);
                ImGui.InputTextWithHint("##filterGamePath", "Filter by Game Path", ref _filterGamePath, 255);
                ImGui.SameLine();
                ImGui.SetNextItemWidth((restContent - 30) / 2f);
                ImGui.InputTextWithHint("##filterFilePath", "Filter by File Path", ref _filterFilePath, 255);

                using (var dataTable = ImRaii.Table("##table", 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg))
                {
                    if (dataTable)
                    {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
                        ImGui.TableSetupColumn("Game Path", ImGuiTableColumnFlags.WidthFixed, (restContent - 30) / 2f);
                        ImGui.TableSetupColumn("File Path", ImGuiTableColumnFlags.WidthFixed, (restContent - 30) / 2f);
                        ImGui.TableSetupScrollFreeze(0, 1);
                        ImGui.TableHeadersRow();
                        int id = 0;
                        foreach (var entry in selectedList)
                        {
                            if (!string.IsNullOrWhiteSpace(_filterGamePath) && !entry.Contains(_filterGamePath, StringComparison.OrdinalIgnoreCase))
                                continue;
                            bool hasFileResolve = _filePathResolve.TryGetValue(entry, out var filePath);

                            if (hasFileResolve && !string.IsNullOrEmpty(_filterFilePath) && !filePath!.Contains(_filterFilePath, StringComparison.OrdinalIgnoreCase))
                                continue;

                            using var imguiid = ImRaii.PushId(id++);
                            ImGui.TableNextColumn();
                            bool isSelected = _storedPathsToRemove.Contains(entry, StringComparer.Ordinal);
                            if (ImGui.Checkbox("##", ref isSelected))
                            {
                                if (isSelected)
                                    _storedPathsToRemove.Add(entry);
                                else
                                    _storedPathsToRemove.Remove(entry);
                            }
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(entry);
                            UiSharedService.AttachToolTip(entry + UiSharedService.TooltipSeparator + "Click to copy to clipboard");
                            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                            {
                                ImGui.SetClipboardText(entry);
                            }
                            ImGui.TableNextColumn();
                            if (hasFileResolve)
                            {
                                ImGui.TextUnformatted(filePath ?? "Unk");
                                UiSharedService.AttachToolTip(filePath ?? "Unk" + UiSharedService.TooltipSeparator + "Click to copy to clipboard");
                                if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
                                {
                                    ImGui.SetClipboardText(filePath);
                                }
                            }
                            else
                            {
                                ImGui.TextUnformatted("-");
                                UiSharedService.AttachToolTip("Resolve Game Paths to used File Paths to display the associated file paths.");
                            }
                        }
                    }
                }
            }
        }
    }

    public override void OnOpen()
    {
        _hasUpdate = true;
        _selectedHash = string.Empty;
        _enableBc7ConversionMode = false;
        _texturesToConvert.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _conversionProgress.ProgressChanged -= ConversionProgress_ProgressChanged;
    }

    private void ConversionProgress_ProgressChanged(object? sender, (string, int) e)
    {
        _conversionCurrentFileName = e.Item1;
        _conversionCurrentFileProgress = e.Item2;
    }
}