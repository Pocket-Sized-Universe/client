using FFXIVClientStructs.FFXIV.Client.Game.Character;
using PocketSizedUniverse.API.Data.Enum;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.MareConfiguration.Models;
using PocketSizedUniverse.PlayerData.Data;
using PocketSizedUniverse.PlayerData.Handlers;
using PocketSizedUniverse.Services;
using PocketSizedUniverse.Services.Mediator;
using Microsoft.Extensions.Logging;
using PocketSizedUniverse.API.Dto.CharaData;
using PocketSizedUniverse.Services.CharaData.Models;
using PocketSizedUniverse.Utils;
using PocketSizedUniverse.WebAPI;
using CharacterData = PocketSizedUniverse.PlayerData.Data.CharacterData;

namespace PocketSizedUniverse.PlayerData.Factories;

public class PlayerDataFactory
{
    private readonly DalamudUtilService _dalamudUtil;
    private readonly IpcManager _ipcManager;
    private readonly ILogger<PlayerDataFactory> _logger;
    private readonly PerformanceCollectorService _performanceCollector;
    private readonly XivDataAnalyzer _modelAnalyzer;
    private readonly MareMediator _mareMediator;
    private readonly ApiController _apiController;
    private readonly FileCacheInfoFactory _fileCacheInfoFactory;

    public PlayerDataFactory(ILogger<PlayerDataFactory> logger, DalamudUtilService dalamudUtil, IpcManager ipcManager,
        PerformanceCollectorService performanceCollector, XivDataAnalyzer modelAnalyzer, MareMediator mareMediator,
        BitTorrentService torrentService, ApiController apiController, FileCacheInfoFactory fileCacheInfoFactory)
    {
        _logger = logger;
        _dalamudUtil = dalamudUtil;
        _ipcManager = ipcManager;
        _performanceCollector = performanceCollector;
        _modelAnalyzer = modelAnalyzer;
        _mareMediator = mareMediator;
        _apiController = apiController;
        _fileCacheInfoFactory = fileCacheInfoFactory;
        _logger.LogTrace("Creating {this}", nameof(PlayerDataFactory));
    }

    public async Task<CharacterDataFragment?> BuildCharacterData(GameObjectHandler playerRelatedObject,
        CancellationToken token)
    {
        if (!_ipcManager.Initialized)
        {
            throw new InvalidOperationException("Penumbra or Glamourer is not connected");
        }

        if (playerRelatedObject == null) return null;

        bool pointerIsZero = true;
        try
        {
            pointerIsZero = playerRelatedObject.Address == IntPtr.Zero;
            try
            {
                pointerIsZero = await CheckForNullDrawObject(playerRelatedObject.Address).ConfigureAwait(false);
            }
            catch
            {
                pointerIsZero = true;
                _logger.LogDebug("NullRef for {object}", playerRelatedObject);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not create data for {object}", playerRelatedObject);
        }

        if (pointerIsZero)
        {
            _logger.LogTrace("Pointer was zero for {objectKind}", playerRelatedObject.ObjectKind);
            return null;
        }

        try
        {
            return await _performanceCollector.LogPerformance(this,
                $"CreateCharacterData>{playerRelatedObject.ObjectKind}", async () =>
                {
                    return await CreateCharacterData(playerRelatedObject, token).ConfigureAwait(false);
                }).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Cancelled creating Character data for {object}", playerRelatedObject);
            throw;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to create {object} data", playerRelatedObject);
        }

        return null;
    }

    private async Task<bool> CheckForNullDrawObject(IntPtr playerPointer)
    {
        return await _dalamudUtil.RunOnFrameworkThread(() => CheckForNullDrawObjectUnsafe(playerPointer))
            .ConfigureAwait(false);
    }

    private unsafe bool CheckForNullDrawObjectUnsafe(IntPtr playerPointer)
    {
        return ((Character*)playerPointer)->GameObject.DrawObject == null;
    }

    private async Task<CharacterDataFragment> CreateCharacterData(GameObjectHandler playerRelatedObject,
        CancellationToken ct)
    {
        var objectKind = playerRelatedObject.ObjectKind;
        CharacterDataFragment fragment = objectKind == ObjectKind.Player ? new CharacterDataFragmentPlayer() : new();

        _logger.LogDebug("Building character data for {obj}", playerRelatedObject);

        // wait until chara is not drawing and present so nothing spontaneously explodes
        await _dalamudUtil.WaitWhileCharacterIsDrawing(_logger, playerRelatedObject, Guid.NewGuid(), 30000, ct: ct)
            .ConfigureAwait(false);
        int totalWaitTime = 10000;
        while (!await _dalamudUtil
                   .IsObjectPresentAsync(await _dalamudUtil.CreateGameObjectAsync(playerRelatedObject.Address)
                       .ConfigureAwait(false)).ConfigureAwait(false) && totalWaitTime > 0)
        {
            _logger.LogTrace("Character is null but it shouldn't be, waiting");
            await Task.Delay(50, ct).ConfigureAwait(false);
            totalWaitTime -= 50;
        }

        ct.ThrowIfCancellationRequested();

        Dictionary<string, List<ushort>>? boneIndices =
            objectKind != ObjectKind.Player
                ? null
                : await _dalamudUtil
                    .RunOnFrameworkThread(() => _modelAnalyzer.GetSkeletonBoneIndices(playerRelatedObject))
                    .ConfigureAwait(false);

        DateTime start = DateTime.UtcNow;

        Dictionary<string, HashSet<string>>? resolvedPaths;

        resolvedPaths =
            (await _ipcManager.Penumbra.GetCharacterData(_logger, playerRelatedObject).ConfigureAwait(false));
        if (resolvedPaths == null) throw new InvalidOperationException("Penumbra returned null data");

        ct.ThrowIfCancellationRequested();

        foreach (var path in resolvedPaths)
        {
            foreach (var file in path.Value)
            {
                var cacheFile = _fileCacheInfoFactory.CreateFromPath(path.Key, file);
                await cacheFile.ProcessFile().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (cacheFile.IsFileSwap)
                {

                    TorrentFileEntry torrentFile = new(cacheFile.Hash!, file, cacheFile.TorrentFile!);
                    fragment.FileSwaps.Add(torrentFile);
                }
                else
                {
                    FileRedirectEntry redirectEntry = new FileRedirectEntry(path.Key, file);
                    fragment.FileReplacements.Add(redirectEntry);
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("== Static Replacements ==");
        foreach (var replacement in
                 fragment.FileReplacements.OrderBy(i => i.GamePath, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug("=> {repl}", replacement);
            ct.ThrowIfCancellationRequested();
        }

        // if it's pet then it's summoner, if it's summoner we actually want to keep all filereplacements alive at all times
        // or we get into redraw city for every change and nothing works properly
        if (objectKind == ObjectKind.Pet)
        {
            _logger.LogTrace("Clearing {count} Static Replacements for Pet", fragment.FileReplacements.Count);
            fragment.FileReplacements.Clear();
        }

        ct.ThrowIfCancellationRequested();

        _logger.LogDebug("Handling transient update for {obj}", playerRelatedObject);

        ct.ThrowIfCancellationRequested();

        // gather up data from ipc
        Task<string> getHeelsOffset = _ipcManager.Heels.GetOffsetAsync();
        Task<string> getGlamourerData =
            _ipcManager.Glamourer.GetCharacterCustomizationAsync(playerRelatedObject.Address);
        Task<string?> getCustomizeData = _ipcManager.CustomizePlus.GetScaleAsync(playerRelatedObject.Address);
        Task<string> getHonorificTitle = _ipcManager.Honorific.GetTitle();
        fragment.GlamourerString = await getGlamourerData.ConfigureAwait(false);
        _logger.LogDebug("Glamourer is now: {data}", fragment.GlamourerString);
        var customizeScale = await getCustomizeData.ConfigureAwait(false);
        fragment.CustomizePlusScale = customizeScale ?? string.Empty;
        _logger.LogDebug("Customize is now: {data}", fragment.CustomizePlusScale);

        if (objectKind == ObjectKind.Player)
        {
            var playerFragment = (fragment as CharacterDataFragmentPlayer)!;
            playerFragment.ManipulationString = _ipcManager.Penumbra.GetMetaManipulations();

            playerFragment!.HonorificData = await getHonorificTitle.ConfigureAwait(false);
            _logger.LogDebug("Honorific is now: {data}", playerFragment!.HonorificData);

            playerFragment!.HeelsData = await getHeelsOffset.ConfigureAwait(false);
            _logger.LogDebug("Heels is now: {heels}", playerFragment!.HeelsData);

            playerFragment!.MoodlesData =
                await _ipcManager.Moodles.GetStatusAsync(playerRelatedObject.Address).ConfigureAwait(false) ??
                string.Empty;
            _logger.LogDebug("Moodles is now: {moodles}", playerFragment!.MoodlesData);

            playerFragment!.PetNamesData = _ipcManager.PetNames.GetLocalNames();
            _logger.LogDebug("Pet Nicknames is now: {petnames}", playerFragment!.PetNamesData);
        }

        ct.ThrowIfCancellationRequested();

        // if (objectKind == ObjectKind.Player)
        // {
        //     try
        //     {
        //         await VerifyPlayerAnimationBones(boneIndices, (fragment as CharacterDataFragmentPlayer)!, ct)
        //             .ConfigureAwait(false);
        //     }
        //     catch (OperationCanceledException e)
        //     {
        //         _logger.LogDebug(e, "Cancelled during player animation verification");
        //         throw;
        //     }
        //     catch (Exception e)
        //     {
        //         _logger.LogWarning(e, "Failed to verify player animations, continuing without further verification");
        //     }
        // }

        _logger.LogInformation("Building character data for {obj} took {time}ms", objectKind,
            TimeSpan.FromTicks(DateTime.UtcNow.Ticks - start.Ticks).TotalMilliseconds);

        return fragment;
    }

    // private async Task VerifyPlayerAnimationBones(Dictionary<string, List<ushort>>? boneIndices,
    //     CharacterDataFragmentPlayer fragment, CancellationToken ct)
    // {
    //     if (boneIndices == null) return;
    //
    //     foreach (var kvp in boneIndices)
    //     {
    //         _logger.LogDebug("Found {skellyname} ({idx} bone indices) on player: {bones}", kvp.Key,
    //             kvp.Value.Any() ? kvp.Value.Max() : 0, string.Join(',', kvp.Value));
    //     }
    //
    //     if (boneIndices.All(u => u.Value.Count == 0)) return;
    //
    //     int noValidationFailed = 0;
    //     foreach (var file in fragment.FileSwaps
    //                  .Where(f => f.GamePath.EndsWith("pap", StringComparison.OrdinalIgnoreCase)).ToList())
    //     {
    //         ct.ThrowIfCancellationRequested();
    //         var fileCache = _fileCacheInfoFactory.CreateFromTorrentFileEntry(file);
    //         await fileCache.ProcessFile().ConfigureAwait(false);
    //         var fileInfo = fileCache.TrueFile;
    //         if (fileInfo == null) continue;
    //
    //         var skeletonIndices = await _dalamudUtil
    //             .RunOnFrameworkThread(() => _modelAnalyzer.GetBoneIndicesFromPap(file.Hash)).ConfigureAwait(false);
    //         bool validationFailed = false;
    //         if (skeletonIndices != null)
    //         {
    //             // 105 is the maximum vanilla skellington spoopy bone index
    //             if (skeletonIndices.All(k => k.Value.Max() <= 105))
    //             {
    //                 _logger.LogTrace("All indices of {path} are <= 105, ignoring", fileInfo.FullName);
    //                 continue;
    //             }
    //
    //             _logger.LogDebug("Verifying bone indices for {path}, found {x} skeletons", fileInfo.FullName,
    //                 skeletonIndices.Count);
    //
    //             foreach (var boneCount in skeletonIndices.Select(k => k).ToList())
    //             {
    //                 if (boneCount.Value.Max() > boneIndices.SelectMany(b => b.Value).Max())
    //                 {
    //                     _logger.LogWarning(
    //                         "Found more bone indices on the animation {path} skeleton {skl} (max indice {idx}) than on any player related skeleton (max indice {idx2})",
    //                         fileInfo.FullName, boneCount.Key, boneCount.Value.Max(),
    //                         boneIndices.SelectMany(b => b.Value).Max());
    //                     validationFailed = true;
    //                     break;
    //                 }
    //             }
    //         }
    //
    //         if (validationFailed)
    //         {
    //             noValidationFailed++;
    //             _logger.LogDebug("Removing {file} from sent file replacements and transient data", fileInfo.FullName);
    //             fragment.FileSwaps.Remove(file);
    //         }
    //     }
    //
    //     if (noValidationFailed > 0)
    //     {
    //         _mareMediator.Publish(new NotificationMessage("Invalid Skeleton Setup",
    //             $"Your client is attempting to send {noValidationFailed} animation files with invalid bone data. Those animation files have been removed from your sent data. " +
    //             $"Verify that you are using the correct skeleton for those animation files (Check /xllog for more information).",
    //             NotificationType.Warning, TimeSpan.FromSeconds(10)));
    //     }
    // }

    private async Task<IReadOnlyDictionary<string, string[]>> GetFileReplacementsFromPaths(
        HashSet<string> forwardResolve, HashSet<string> reverseResolve)
    {
        var forwardPaths = forwardResolve.ToArray();
        var reversePaths = reverseResolve.ToArray();
        Dictionary<string, List<string>> resolvedPaths = new(StringComparer.Ordinal);
        var (forward, reverse) =
            await _ipcManager.Penumbra.ResolvePathsAsync(forwardPaths, reversePaths).ConfigureAwait(false);
        for (int i = 0; i < forwardPaths.Length; i++)
        {
            var filePath = forward[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.Add(forwardPaths[i].ToLowerInvariant());
            }
            else
            {
                resolvedPaths[filePath] = [forwardPaths[i].ToLowerInvariant()];
            }
        }

        for (int i = 0; i < reversePaths.Length; i++)
        {
            var filePath = reversePaths[i].ToLowerInvariant();
            if (resolvedPaths.TryGetValue(filePath, out var list))
            {
                list.AddRange(reverse[i].Select(c => c.ToLowerInvariant()));
            }
            else
            {
                resolvedPaths[filePath] = new List<string>(reverse[i].Select(c => c.ToLowerInvariant()).ToList());
            }
        }

        return resolvedPaths.ToDictionary(k => k.Key, k => k.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
            .AsReadOnly();
    }
}