using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.PlayerData.Factories;
using PocketSizedUniverse.PlayerData.Pairs;
using PocketSizedUniverse.Services;
using PocketSizedUniverse.Services.Events;
using PocketSizedUniverse.Services.Mediator;
using PocketSizedUniverse.Services.ServerConfiguration;
using PocketSizedUniverse.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PocketSizedUniverse.API.Dto.CharaData;
using System.Collections.Concurrent;
using System.Diagnostics;
using ObjectKind = PocketSizedUniverse.API.Data.Enum.ObjectKind;

namespace PocketSizedUniverse.PlayerData.Handlers;

public sealed class PairHandler : DisposableMediatorSubscriberBase
{
    private sealed record CombatData(Guid ApplicationId, CharacterData CharacterData, bool Forced);

    private readonly DalamudUtilService _dalamudUtil;
    private readonly BitTorrentService _torrentService;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ServerConfigurationManager _serverConfigManager;
    private readonly PluginWarningNotificationService _pluginWarningNotificationManager;
    private readonly FileCacheInfoFactory _fileCacheInfoFactory;
    private CancellationTokenSource? _applicationCancellationTokenSource = new();
    private Guid _applicationId;
    private Task? _applicationTask;
    private CharacterData? _cachedData = null;
    private GameObjectHandler? _charaHandler;
    private readonly Dictionary<ObjectKind, Guid?> _customizeIds = [];
    private CombatData? _dataReceivedInDowntime;
    private CancellationTokenSource? _downloadCancellationTokenSource = new();
    private bool _forceApplyMods = false;
    private bool _isVisible;
    private Guid _penumbraCollection;
    private bool _redrawOnNextApplication = false;

    public PairHandler(ILogger<PairHandler> logger, Pair pair,
        GameObjectHandlerFactory gameObjectHandlerFactory,
        IpcManager ipcManager, BitTorrentService bitTorrentService,
        PluginWarningNotificationService pluginWarningNotificationManager,
        DalamudUtilService dalamudUtil, IHostApplicationLifetime lifetime,
        MareMediator mediator, FileCacheInfoFactory fileCacheInfoFactory,
        ServerConfigurationManager serverConfigManager) : base(logger, mediator)
    {
        Pair = pair;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _ipcManager = ipcManager;
        _torrentService = bitTorrentService;
        _pluginWarningNotificationManager = pluginWarningNotificationManager;
        _dalamudUtil = dalamudUtil;
        _lifetime = lifetime;
        _serverConfigManager = serverConfigManager;
        _fileCacheInfoFactory = fileCacheInfoFactory;
        // Initialize penumbra collection asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                _penumbraCollection = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, Pair.UserData.UID).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to create temporary Penumbra collection for {uid}", Pair.UserData.UID);
            }
        });

        Mediator.Subscribe<FrameworkUpdateMessage>(this, (_) => FrameworkUpdate());
        Mediator.Subscribe<ZoneSwitchStartMessage>(this, (_) =>
        {
            _downloadCancellationTokenSource?.CancelDispose();
            _charaHandler?.Invalidate();
            IsVisible = false;
        });
        Mediator.Subscribe<PenumbraInitializedMessage>(this, (_) =>
        {

            _penumbraCollection = _ipcManager.Penumbra.CreateTemporaryCollectionAsync(logger, Pair.UserData.UID).ConfigureAwait(false).GetAwaiter().GetResult();

            if (!IsVisible && _charaHandler != null)
            {
                PlayerName = string.Empty;
                _charaHandler.Dispose();
                _charaHandler = null;
            }
        });
        Mediator.Subscribe<ClassJobChangedMessage>(this, (msg) =>
        {
            if (msg.GameObjectHandler == _charaHandler)
            {
                _redrawOnNextApplication = true;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceEndMessage>(this, (msg) =>
        {
            if (IsVisible && _dataReceivedInDowntime != null)
            {
                ApplyCharacterData(_dataReceivedInDowntime.ApplicationId,
                    _dataReceivedInDowntime.CharacterData, _dataReceivedInDowntime.Forced);
                _dataReceivedInDowntime = null;
            }
        });
        Mediator.Subscribe<CombatOrPerformanceStartMessage>(this, _ =>
        {
            _dataReceivedInDowntime = null;
            _downloadCancellationTokenSource = _downloadCancellationTokenSource?.CancelRecreate();
            _applicationCancellationTokenSource = _applicationCancellationTokenSource?.CancelRecreate();
        });

        LastAppliedDataBytes = -1;
    }

    public bool IsVisible
    {
        get => _isVisible;
        private set
        {
            if (_isVisible != value)
            {
                _isVisible = value;
                string text = "User Visibility Changed, now: " + (_isVisible ? "Is Visible" : "Is not Visible");
                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational, text)));
                Mediator.Publish(new RefreshUiMessage());
            }
        }
    }

    public long LastAppliedDataBytes { get; private set; }
    public Pair Pair { get; private set; }
    public nint PlayerCharacter => _charaHandler?.Address ?? nint.Zero;

    public unsafe uint PlayerCharacterId => (_charaHandler?.Address ?? nint.Zero) == nint.Zero
        ? uint.MaxValue
        : ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)_charaHandler!.Address)->EntityId;

    public string? PlayerName { get; private set; }
    public string PlayerNameHash => Pair.Ident;

    public void ApplyCharacterData(Guid applicationBase, CharacterData characterData,
        bool forceApplyCustomization = false)
    {
        if (_dalamudUtil.IsInCombatOrPerforming)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                EventSeverity.Warning,
                "Cannot apply character data: you are in combat or performing music, deferring application")));
            Logger.LogDebug("[BASE-{appBase}] Received data but player is in combat or performing", applicationBase);
            _dataReceivedInDowntime = new(applicationBase, characterData, forceApplyCustomization);
            SetUploading(isUploading: false);
            return;
        }

        if (_charaHandler == null || (PlayerCharacter == IntPtr.Zero))
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                EventSeverity.Warning,
                "Cannot apply character data: Receiving Player is in an invalid state, deferring application")));
            Logger.LogDebug(
                "[BASE-{appBase}] Received data but player was in invalid state, charaHandlerIsNull: {charaIsNull}, playerPointerIsNull: {ptrIsNull}",
                applicationBase, _charaHandler == null, PlayerCharacter == IntPtr.Zero);
            var hasDiffMods = characterData.CheckUpdatedData(applicationBase, _cachedData, Logger,
                    this, forceApplyCustomization, forceApplyMods: false)
                .Any(p => p.Value.Contains(PlayerChanges.ModManip) || p.Value.Contains(PlayerChanges.ModFiles));
            _forceApplyMods = hasDiffMods || _forceApplyMods || (PlayerCharacter == IntPtr.Zero && _cachedData == null);
            _cachedData = characterData;
            Logger.LogDebug("[BASE-{appBase}] Setting data: {hash}, forceApplyMods: {force}", applicationBase,
                _cachedData.DataHash.Value, _forceApplyMods);
            return;
        }

        SetUploading(isUploading: false);

        Logger.LogDebug(
            "[BASE-{appbase}] Applying data for {player}, forceApplyCustomization: {forced}, forceApplyMods: {forceMods}",
            applicationBase, this, forceApplyCustomization, _forceApplyMods);
        Logger.LogDebug("[BASE-{appbase}] Hash for data is {newHash}, current cache hash is {oldHash}", applicationBase,
            characterData.DataHash.Value, _cachedData?.DataHash.Value ?? "NODATA");

        if (string.Equals(characterData.DataHash.Value, _cachedData?.DataHash.Value ?? string.Empty,
                StringComparison.Ordinal) && !forceApplyCustomization) return;

        if (_dalamudUtil.IsInCutscene || _dalamudUtil.IsInGpose || !_ipcManager.Penumbra.APIAvailable ||
            !_ipcManager.Glamourer.APIAvailable)
        {
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                EventSeverity.Warning,
                "Cannot apply character data: you are in GPose, a Cutscene or Penumbra/Glamourer is not available")));
            Logger.LogInformation(
                "[BASE-{appbase}] Application of data for {player} while in cutscene/gpose or Penumbra/Glamourer unavailable, returning",
                applicationBase, this);
            return;
        }

        Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
            EventSeverity.Informational,
            "Applying Character Data")));

        _forceApplyMods |= forceApplyCustomization;

        var charaDataToUpdate = characterData.CheckUpdatedData(applicationBase, _cachedData?.DeepClone() ?? new(),
            Logger, this, forceApplyCustomization, _forceApplyMods);

        if (_charaHandler != null && _forceApplyMods)
        {
            _forceApplyMods = false;
        }

        if (_redrawOnNextApplication && charaDataToUpdate.TryGetValue(ObjectKind.Player, out var player))
        {
            player.Add(PlayerChanges.ForcedRedraw);
            _redrawOnNextApplication = false;
        }

        if (charaDataToUpdate.TryGetValue(ObjectKind.Player, out var playerChanges))
        {
            _pluginWarningNotificationManager.NotifyForMissingPlugins(Pair.UserData, PlayerName!, playerChanges);
        }

        Logger.LogDebug("[BASE-{appbase}] Downloading and applying character for {name}", applicationBase, this);

        DownloadAndApplyCharacter(applicationBase, characterData.DeepClone(), charaDataToUpdate);
    }

    public override string ToString()
    {
        return Pair == null
            ? base.ToString() ?? string.Empty
            : Pair.UserData.AliasOrUID + ":" + PlayerName + ":" + (PlayerCharacter != nint.Zero ? "HasChar" : "NoChar");
    }

    internal void SetUploading(bool isUploading = true)
    {
        Logger.LogTrace("Setting {this} uploading {uploading}", this, isUploading);
        if (_charaHandler != null)
        {
            Mediator.Publish(new PlayerUploadingMessage(_charaHandler, isUploading));
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        SetUploading(isUploading: false);
        var name = PlayerName;
        Logger.LogDebug("Disposing {name} ({user})", name, Pair);
        try
        {
            Guid applicationId = Guid.NewGuid();
            _applicationCancellationTokenSource?.CancelDispose();
            _applicationCancellationTokenSource = null;
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            _charaHandler?.Dispose();
            _charaHandler = null;

            if (!string.IsNullOrEmpty(name))
            {
                Mediator.Publish(new EventMessage(new Event(name, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational, "Disposing User")));
            }

            if (_lifetime.ApplicationStopping.IsCancellationRequested) return;

            if (_dalamudUtil is { IsZoning: false, IsInCutscene: false } && !string.IsNullOrEmpty(name))
            {
                Logger.LogTrace("[{applicationId}] Restoring state for {name} ({OnlineUser})", applicationId, name,
                    Pair.UserPair);
                Logger.LogDebug("[{applicationId}] Removing Temp Collection for {name} ({user})", applicationId, name,
                    Pair.UserPair);
                // Clean up penumbra collection asynchronously
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(Logger, applicationId, _penumbraCollection).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to remove temporary Penumbra collection during dispose");
                    }
                });
                if (!IsVisible)
                {
                    Logger.LogDebug("[{applicationId}] Restoring Glamourer for {name} ({user})", applicationId, name,
                        Pair.UserPair);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _ipcManager.Glamourer.RevertByNameAsync(Logger, name, applicationId).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning(ex, "Failed to revert Glamourer for {name} during dispose", name);
                        }
                    });
                }
                else
                {
                    using var cts = new CancellationTokenSource();
                    cts.CancelAfter(TimeSpan.FromSeconds(60));

                    Logger.LogInformation("[{applicationId}] CachedData is null {isNull}, contains things: {contains}",
                        applicationId, _cachedData == null, _cachedData?.FileReplacements.Any() ?? false);

                    foreach (KeyValuePair<ObjectKind, List<FileRedirectEntry>> item in _cachedData?.FileReplacements ??
                             [])
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException ex)
                            {
                                Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "Error reverting customization data during dispose");
                            }
                        });
                    }

                    foreach (var item in _cachedData?.FileSwaps ?? [])
                    {
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await RevertCustomizationDataAsync(item.Key, name, applicationId, cts.Token).ConfigureAwait(false);
                            }
                            catch (InvalidOperationException ex)
                            {
                                Logger.LogWarning(ex, "Failed disposing player (not present anymore?)");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "Error reverting file swaps during dispose");
                            }
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error on disposal of {name}", name);
        }
        finally
        {
            PlayerName = null;
            _cachedData = null;
            Logger.LogDebug("Disposing {name} complete", name);
        }
    }

    private async Task ApplyCustomizationDataAsync(Guid applicationId,
        KeyValuePair<ObjectKind, HashSet<PlayerChanges>> changes, CharacterData charaData, CancellationToken token)
    {
        if (PlayerCharacter == nint.Zero) return;
        var ptr = PlayerCharacter;

        var handler = changes.Key switch
        {
            ObjectKind.Player => _charaHandler!,
            ObjectKind.Companion => await _gameObjectHandlerFactory
                .Create(changes.Key, () => _dalamudUtil.GetCompanionPtr(ptr), isWatched: false).ConfigureAwait(false),
            ObjectKind.MinionOrMount => await _gameObjectHandlerFactory
                .Create(changes.Key, () => _dalamudUtil.GetMinionOrMountPtr(ptr), isWatched: false)
                .ConfigureAwait(false),
            ObjectKind.Pet => await _gameObjectHandlerFactory
                .Create(changes.Key, () => _dalamudUtil.GetPetPtr(ptr), isWatched: false).ConfigureAwait(false),
            _ => throw new NotSupportedException("ObjectKind not supported: " + changes.Key)
        };

        try
        {
            if (handler.Address == nint.Zero)
            {
                return;
            }

            Logger.LogDebug("[{applicationId}] Applying Customization Data for {handler}", applicationId, handler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, handler, applicationId, 30000, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            foreach (var change in changes.Value.OrderBy(p => (int)p))
            {
                Logger.LogDebug("[{applicationId}] Processing {change} for {handler}", applicationId, change, handler);
                switch (change)
                {
                    case PlayerChanges.Customize:
                        if (charaData.CustomizePlusData.TryGetValue(changes.Key, out var customizePlusData))
                        {
                            _customizeIds[changes.Key] = await _ipcManager.CustomizePlus
                                .SetBodyScaleAsync(handler.Address, customizePlusData).ConfigureAwait(false);
                        }
                        else if (_customizeIds.TryGetValue(changes.Key, out var customizeId))
                        {
                            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                            _customizeIds.Remove(changes.Key);
                        }

                        break;

                    case PlayerChanges.Heels:
                        await _ipcManager.Heels.SetOffsetForPlayerAsync(handler.Address, charaData.HeelsData)
                            .ConfigureAwait(false);
                        break;

                    case PlayerChanges.Honorific:
                        await _ipcManager.Honorific.SetTitleAsync(handler.Address, charaData.HonorificData)
                            .ConfigureAwait(false);
                        break;

                    case PlayerChanges.Glamourer:
                        if (charaData.GlamourerData.TryGetValue(changes.Key, out var glamourerData))
                        {
                            await _ipcManager.Glamourer
                                .ApplyAllAsync(Logger, handler, glamourerData, applicationId, token)
                                .ConfigureAwait(false);
                        }

                        break;

                    case PlayerChanges.Moodles:
                        await _ipcManager.Moodles.SetStatusAsync(handler.Address, charaData.MoodlesData)
                            .ConfigureAwait(false);
                        break;

                    case PlayerChanges.PetNames:
                        await _ipcManager.PetNames.SetPlayerData(handler.Address, charaData.PetNamesData)
                            .ConfigureAwait(false);
                        break;

                    case PlayerChanges.ForcedRedraw:
                        await _ipcManager.Penumbra.RedrawAsync(Logger, handler, applicationId, token)
                            .ConfigureAwait(false);
                        break;

                    default:
                        break;
                }

                token.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            if (handler != _charaHandler) handler.Dispose();
        }
    }

    private void DownloadAndApplyCharacter(Guid applicationBase, CharacterData charaData,
        Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData)
    {
        if (!updatedData.Any())
        {
            Logger.LogDebug("[BASE-{appBase}] Nothing to update for {obj}", applicationBase, this);
            return;
        }

        var updateModdedPaths = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModFiles));
        var updateManip = updatedData.Values.Any(v => v.Any(p => p == PlayerChanges.ModManip));

        _downloadCancellationTokenSource =
            _downloadCancellationTokenSource?.CancelRecreate() ?? new CancellationTokenSource();
        var downloadToken = _downloadCancellationTokenSource.Token;

        _ = DownloadAndApplyCharacterAsync(applicationBase, charaData, updatedData, updateModdedPaths, updateManip,
            downloadToken).ConfigureAwait(false);
    }

    private Task? _pairDownloadTask;

    private async Task DownloadAndApplyCharacterAsync(Guid applicationBase, CharacterData charaData,
        Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData,
        bool updateModdedPaths, bool updateManip, CancellationToken downloadToken)
    {
        Dictionary<(string GamePath, byte[] Hash), string> moddedPaths = [];

        if (updateModdedPaths)
        {
            int attempts = 0;
            List<TorrentFileEntry> toDownloadReplacements =
                TryCalculateModdedDictionary(applicationBase, charaData, out moddedPaths, downloadToken);

            while (toDownloadReplacements.Count > 0 && attempts++ <= 10 && !downloadToken.IsCancellationRequested)
            {
                if (_pairDownloadTask != null && !_pairDownloadTask.IsCompleted)
                {
                    Logger.LogDebug("[BASE-{appBase}] Finishing prior running download task for player {name}, {kind}",
                        applicationBase, PlayerName, updatedData);
                    await _pairDownloadTask.ConfigureAwait(false);
                }

                Logger.LogDebug("[BASE-{appBase}] Downloading missing files for player {name}, {kind}", applicationBase,
                    PlayerName, updatedData);

                Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                    EventSeverity.Informational,
                    $"Starting download for {toDownloadReplacements.Count} files")));
                foreach (var file in toDownloadReplacements)
                {
                    Logger.LogDebug("[BASE-{appBase}] Downloading {file}", applicationBase, file);
                    var fileCache = _fileCacheInfoFactory.CreateFromTorrentFileEntry(file);
                    await fileCache.ProcessFile().ConfigureAwait(false);
                }


                if (downloadToken.IsCancellationRequested)
                {
                    Logger.LogTrace("[BASE-{appBase}] Detected cancellation", applicationBase);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), downloadToken).ConfigureAwait(false);
            }

            return;
        }

        downloadToken.ThrowIfCancellationRequested();

        var appToken = _applicationCancellationTokenSource?.Token;
        while ((!_applicationTask?.IsCompleted ?? false)
               && !downloadToken.IsCancellationRequested
               && (!appToken?.IsCancellationRequested ?? false))
        {
            // block until current application is done
            Logger.LogDebug(
                "[BASE-{appBase}] Waiting for current data application (Id: {id}) for player ({handler}) to finish",
                applicationBase, _applicationId, PlayerName);
            await Task.Delay(250).ConfigureAwait(false);
        }

        if (downloadToken.IsCancellationRequested || (appToken?.IsCancellationRequested ?? false)) return;

        _applicationCancellationTokenSource =
            _applicationCancellationTokenSource.CancelRecreate() ?? new CancellationTokenSource();
        var token = _applicationCancellationTokenSource.Token;

        _applicationTask = ApplyCharacterDataAsync(applicationBase, charaData, updatedData, updateModdedPaths,
            updateManip, moddedPaths, token);
    }

    private async Task ApplyCharacterDataAsync(Guid applicationBase, CharacterData charaData,
        Dictionary<ObjectKind, HashSet<PlayerChanges>> updatedData, bool updateModdedPaths, bool updateManip,
        Dictionary<(string GamePath, byte[] Hash), string> moddedPaths, CancellationToken token)
    {
        try
        {
            _applicationId = Guid.NewGuid();
            Logger.LogDebug("[BASE-{applicationId}] Starting application task for {this}: {appId}", applicationBase,
                this, _applicationId);

            Logger.LogDebug("[{applicationId}] Waiting for initial draw for for {handler}", _applicationId,
                _charaHandler);
            await _dalamudUtil.WaitWhileCharacterIsDrawing(Logger, _charaHandler!, _applicationId, 30000, token)
                .ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            if (updateModdedPaths)
            {
                // ensure collection is set
                var objIndex = await _dalamudUtil
                    .RunOnFrameworkThread(() => _charaHandler!.GetGameObject()!.ObjectIndex).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(Logger, _penumbraCollection, objIndex)
                    .ConfigureAwait(false);

                await _ipcManager.Penumbra.SetTemporaryModsAsync(Logger, _applicationId, _penumbraCollection,
                        moddedPaths.ToDictionary(k => k.Key.GamePath, k => k.Value, StringComparer.Ordinal))
                    .ConfigureAwait(false);
                LastAppliedDataBytes = -1;
                foreach (var path in moddedPaths.Values.Distinct(StringComparer.OrdinalIgnoreCase)
                             .Select(v => new FileInfo(v)).Where(p => p.Exists))
                {
                    if (LastAppliedDataBytes == -1) LastAppliedDataBytes = 0;

                    LastAppliedDataBytes += path.Length;
                }
            }

            if (updateManip)
            {
                await _ipcManager.Penumbra
                    .SetManipulationDataAsync(Logger, _applicationId, _penumbraCollection, charaData.ManipulationData)
                    .ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();

            foreach (var kind in updatedData)
            {
                await ApplyCustomizationDataAsync(_applicationId, kind, charaData, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }

            _cachedData = charaData;

            Logger.LogDebug("[{applicationId}] Application finished", _applicationId);
        }
        catch (Exception ex)
        {
            if (ex is AggregateException aggr && aggr.InnerExceptions.Any(e => e is ArgumentNullException))
            {
                IsVisible = false;
                _forceApplyMods = true;
                _cachedData = charaData;
                Logger.LogDebug("[{applicationId}] Cancelled, player turned null during application", _applicationId);
            }
            else
            {
                Logger.LogWarning(ex, "[{applicationId}] Cancelled", _applicationId);
            }
        }
    }

    private void FrameworkUpdate()
    {
        if (string.IsNullOrEmpty(PlayerName))
        {
            var pc = _dalamudUtil.FindPlayerByNameHash(Pair.Ident);
            if (pc == default((string, nint))) return;
            Logger.LogDebug("One-Time Initializing {this}", this);
            Initialize(pc.Name);
            Logger.LogDebug("One-Time Initialized {this}", this);
            Mediator.Publish(new EventMessage(new Event(PlayerName, Pair.UserData, nameof(PairHandler),
                EventSeverity.Informational,
                $"Initializing User For Character {pc.Name}")));
        }

        if (_charaHandler?.Address != nint.Zero && !IsVisible)
        {
            Guid appData = Guid.NewGuid();
            IsVisible = true;
            if (_cachedData != null)
            {
                Logger.LogTrace("[BASE-{appBase}] {this} visibility changed, now: {visi}, cached data exists", appData,
                    this, IsVisible);

                _ = Task.Run(() =>
                {
                    ApplyCharacterData(appData, _cachedData!, forceApplyCustomization: true);
                });
            }
            else
            {
                Logger.LogTrace("{this} visibility changed, now: {visi}, no cached data exists", this, IsVisible);
            }
        }
        else if (_charaHandler?.Address == nint.Zero && IsVisible)
        {
            IsVisible = false;
            _charaHandler.Invalidate();
            _downloadCancellationTokenSource?.CancelDispose();
            _downloadCancellationTokenSource = null;
            Logger.LogTrace("{this} visibility changed, now: {visi}", this, IsVisible);
        }
    }

    private void Initialize(string name)
    {
        PlayerName = name;
        // Create character handler asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                _charaHandler = await _gameObjectHandlerFactory
                    .Create(ObjectKind.Player, () => _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident),
                        isWatched: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to create character handler for {name}", name);
            }
        });

        _serverConfigManager.AutoPopulateNoteForUid(Pair.UserData.UID, name);

        Mediator.Subscribe<HonorificReadyMessage>(this, async (_) =>
        {
            if (string.IsNullOrEmpty(_cachedData?.HonorificData)) return;
            Logger.LogTrace("Reapplying Honorific data for {this}", this);
            await _ipcManager.Honorific.SetTitleAsync(PlayerCharacter, _cachedData.HonorificData).ConfigureAwait(false);
        });

        Mediator.Subscribe<PetNamesReadyMessage>(this, async (_) =>
        {
            if (string.IsNullOrEmpty(_cachedData?.PetNamesData)) return;
            Logger.LogTrace("Reapplying Pet Names data for {this}", this);
            await _ipcManager.PetNames.SetPlayerData(PlayerCharacter, _cachedData.PetNamesData).ConfigureAwait(false);
        });

        // Assign collection asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                if (_charaHandler?.GetGameObject() != null)
                {
                    await _ipcManager.Penumbra
                        .AssignTemporaryCollectionAsync(Logger, _penumbraCollection, _charaHandler.GetGameObject()!.ObjectIndex)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to assign temporary collection for {name}", name);
            }
        });
    }

    private async Task RevertCustomizationDataAsync(ObjectKind objectKind, string name, Guid applicationId,
        CancellationToken cancelToken)
    {
        nint address = _dalamudUtil.GetPlayerCharacterFromCachedTableByIdent(Pair.Ident);
        if (address == nint.Zero) return;

        Logger.LogDebug("[{applicationId}] Reverting all Customization for {alias}/{name} {objectKind}", applicationId,
            Pair.UserData.AliasOrUID, name, objectKind);

        if (_customizeIds.TryGetValue(objectKind, out var customizeId))
        {
            _customizeIds.Remove(objectKind);
        }

        if (objectKind == ObjectKind.Player)
        {
            using GameObjectHandler tempHandler = await _gameObjectHandlerFactory
                .Create(ObjectKind.Player, () => address, isWatched: false).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Customization and Equipment for {alias}/{name}", applicationId,
                Pair.UserData.AliasOrUID, name);
            await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken)
                .ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Heels for {alias}/{name}", applicationId,
                Pair.UserData.AliasOrUID, name);
            await _ipcManager.Heels.RestoreOffsetForPlayerAsync(address).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring C+ for {alias}/{name}", applicationId,
                Pair.UserData.AliasOrUID, name);
            await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
            tempHandler.CompareNameAndThrow(name);
            Logger.LogDebug("[{applicationId}] Restoring Honorific for {alias}/{name}", applicationId,
                Pair.UserData.AliasOrUID, name);
            await _ipcManager.Honorific.ClearTitleAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Moodles for {alias}/{name}", applicationId,
                Pair.UserData.AliasOrUID, name);
            await _ipcManager.Moodles.RevertStatusAsync(address).ConfigureAwait(false);
            Logger.LogDebug("[{applicationId}] Restoring Pet Nicknames for {alias}/{name}", applicationId,
                Pair.UserData.AliasOrUID, name);
            await _ipcManager.PetNames.ClearPlayerData(address).ConfigureAwait(false);
        }
        else if (objectKind == ObjectKind.MinionOrMount)
        {
            var minionOrMount = await _dalamudUtil.GetMinionOrMountAsync(address).ConfigureAwait(false);
            if (minionOrMount != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory
                    .Create(ObjectKind.MinionOrMount, () => minionOrMount, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken)
                    .ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken)
                    .ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Pet)
        {
            var pet = await _dalamudUtil.GetPetAsync(address).ConfigureAwait(false);
            if (pet != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory
                    .Create(ObjectKind.Pet, () => pet, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken)
                    .ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken)
                    .ConfigureAwait(false);
            }
        }
        else if (objectKind == ObjectKind.Companion)
        {
            var companion = await _dalamudUtil.GetCompanionAsync(address).ConfigureAwait(false);
            if (companion != nint.Zero)
            {
                await _ipcManager.CustomizePlus.RevertByIdAsync(customizeId).ConfigureAwait(false);
                using GameObjectHandler tempHandler = await _gameObjectHandlerFactory
                    .Create(ObjectKind.Pet, () => companion, isWatched: false).ConfigureAwait(false);
                await _ipcManager.Glamourer.RevertAsync(Logger, tempHandler, applicationId, cancelToken)
                    .ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(Logger, tempHandler, applicationId, cancelToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private List<TorrentFileEntry> TryCalculateModdedDictionary(Guid applicationBase, CharacterData charaData,
        out Dictionary<(string GamePath, byte[] Hash), string> moddedDictionary, CancellationToken token)
    {
        Stopwatch st = Stopwatch.StartNew();
        List<TorrentFileEntry> missingFiles = [];
        moddedDictionary = [];
        ConcurrentDictionary<(string GamePath, byte[] Hash), string> outputDict = new();

        try
        {
            var replacementList = charaData.FileSwaps.SelectMany(k => k.Value).ToList();
            Parallel.ForEach(replacementList,
                new ParallelOptions() { CancellationToken = token, MaxDegreeOfParallelism = 4 },
                (item) =>
                {
                    token.ThrowIfCancellationRequested();
                    var fileCache = _fileCacheInfoFactory.CreateFromTorrentFileEntry(item);
                    // Process file synchronously in this context since we're already in Parallel.ForEach
                    try
                    {
                        fileCache.ProcessFile().ConfigureAwait(false).GetAwaiter().GetResult();
                        var trueFile = fileCache.TrueFile;
                        if (trueFile != null)
                        {
                            outputDict[(item.GamePath, item.Hash)] = trueFile.FullName;
                        }
                        else
                        {
                            missingFiles.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to process file cache for {gamePath}", item.GamePath);
                        missingFiles.Add(item);
                    }
                });

            moddedDictionary = outputDict.ToDictionary(k => k.Key, k => k.Value);

            foreach (var item in charaData.FileReplacements.SelectMany(k => k.Value).ToList())
            {
                Logger.LogTrace("[BASE-{appBase}] Adding file swap for {path}: {fileSwap}", applicationBase,
                    item.GamePath, item.SwapPath);
                moddedDictionary[(item.GamePath, null)] = item.SwapPath;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BASE-{appBase}] Something went wrong during calculation replacements",
                applicationBase);
        }

        st.Stop();
        Logger.LogDebug(
            "[BASE-{appBase}] ModdedPaths calculated in {time}ms, missing files: {count}, total files: {total}",
            applicationBase, st.ElapsedMilliseconds, missingFiles.Count, moddedDictionary.Keys.Count);
        return missingFiles;
    }
}