using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.Services;
using PocketSizedUniverse.Services.Mediator;
using PocketSizedUniverse.Utils;
using PocketSizedUniverse.WebAPI;
using Microsoft.Extensions.Logging;

namespace PocketSizedUniverse.PlayerData.Pairs;

public class VisibleUserDataDistributor : DisposableMediatorSubscriberBase
{
    private readonly ApiController _apiController;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly BitTorrentService _torrentService;
    private readonly PairManager _pairManager;
    private CharacterData? _lastCreatedData;
    private CharacterData? _uploadingCharacterData = null;
    private readonly List<UserData> _previouslyVisiblePlayers = [];
    private Task<CharacterData>? _fileUploadTask = null;
    private readonly HashSet<UserData> _usersToPushDataTo = [];
    private readonly SemaphoreSlim _pushDataSemaphore = new(1, 1);
    private readonly CancellationTokenSource _runtimeCts = new();


    public VisibleUserDataDistributor(ILogger<VisibleUserDataDistributor> logger, ApiController apiController,
        DalamudUtilService dalamudUtil,
        PairManager pairManager, MareMediator mediator, BitTorrentService torrentService) : base(logger, mediator)
    {
        _apiController = apiController;
        _dalamudUtil = dalamudUtil;
        _pairManager = pairManager;
        _torrentService = torrentService;
        Mediator.Subscribe<DelayedFrameworkUpdateMessage>(this, (_) => FrameworkOnUpdate());
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            var newData = msg.CharacterData;
            Logger.LogInformation("Received CharacterDataCreatedMessage with hash {hash}, FileReplacements: {count}",
                newData.DataHash.Value, newData.FileReplacements.Sum(kvp => kvp.Value.Count));

            if (_lastCreatedData == null || (!string.Equals(newData.DataHash.Value, _lastCreatedData.DataHash.Value,
                    StringComparison.Ordinal)))
            {
                _lastCreatedData = newData;
                Logger.LogInformation("Storing new data hash {hash}, triggering upload", newData.DataHash.Value);
                PushToAllVisibleUsers(forced: true);
            }
            else
            {
                Logger.LogDebug("Data hash {hash} equal to stored data, no upload needed", newData.DataHash.Value);
            }
        });

        Mediator.Subscribe<ConnectedMessage>(this, (_) => PushToAllVisibleUsers());
        Mediator.Subscribe<DisconnectedMessage>(this, (_) => _previouslyVisiblePlayers.Clear());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _runtimeCts.Cancel();
            _runtimeCts.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PushToAllVisibleUsers(bool forced = false)
    {
        var visibleUsers = _pairManager.GetPairedUsers();
        Logger.LogInformation("PushToAllVisibleUsers called, found {count} visible users", visibleUsers.Count);

        foreach (var user in visibleUsers)
        {
            _usersToPushDataTo.Add(user);
            Logger.LogDebug("Added visible user {user} to push queue", user.AliasOrUID);
        }

        if (_usersToPushDataTo.Count <= 0)
        {
            return;
        }

        Logger.LogInformation("Pushing data {hash} for {count} visible players",
            _lastCreatedData?.DataHash.Value ?? "UNKNOWN", _usersToPushDataTo.Count);
        PushCharacterData(forced);
    }

    private void FrameworkOnUpdate()
    {
        if (!_dalamudUtil.GetIsPlayerPresent() || !_apiController.IsConnected) return;

        var allVisibleUsers = _pairManager.GetVisibleUsers();
        var newVisibleUsers = allVisibleUsers.Except(_previouslyVisiblePlayers).ToList();
        _previouslyVisiblePlayers.Clear();
        _previouslyVisiblePlayers.AddRange(allVisibleUsers);
        if (newVisibleUsers.Count == 0) return;

        Logger.LogDebug("Scheduling character data push of {data} to {users}",
            _lastCreatedData?.DataHash.Value ?? string.Empty,
            string.Join(", ", newVisibleUsers.Select(k => k.AliasOrUID)));
        foreach (var user in newVisibleUsers)
        {
            _usersToPushDataTo.Add(user);
        }

        PushCharacterData();
    }

    private void PushCharacterData(bool forced = false)
    {
        if (_lastCreatedData == null || _usersToPushDataTo.Count == 0) return;

        _ = Task.Run(async () =>
        {
            _uploadingCharacterData = _lastCreatedData.DeepClone();
            await _pushDataSemaphore.WaitAsync(_runtimeCts.Token).ConfigureAwait(false);
            try
            {
                if (_usersToPushDataTo.Count == 0) return;
                List<UserData> usersToSend = [.. _usersToPushDataTo];
                Logger.LogDebug("Pushing {data} to {users}", _uploadingCharacterData.DataHash,
                    string.Join(", ", usersToSend.Select(k => k.AliasOrUID)));
                _usersToPushDataTo.Clear();
                await _apiController.PushCharacterData(_uploadingCharacterData, usersToSend).ConfigureAwait(false);
            }
            finally
            {
                _pushDataSemaphore.Release();
            }
        });
    }
}