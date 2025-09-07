using Dalamud.Game.ClientState.Objects.SubKinds;
using K4os.Compression.LZ4.Legacy;
using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.API.Data.Enum;
using PocketSizedUniverse.API.Dto.CharaData;
using PocketSizedUniverse.PlayerData.Factories;
using PocketSizedUniverse.PlayerData.Handlers;
using PocketSizedUniverse.Services.CharaData;
using PocketSizedUniverse.Services.CharaData.Models;
using PocketSizedUniverse.Utils;
using Microsoft.Extensions.Logging;

namespace PocketSizedUniverse.Services;

public sealed class CharaDataFileHandler : IDisposable
{
    private readonly DalamudUtilService _dalamudUtilService;
    private readonly BitTorrentService _torrentService;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly ILogger<CharaDataFileHandler> _logger;
    private readonly MareCharaFileDataFactory _mareCharaFileDataFactory;
    private readonly PlayerDataFactory _playerDataFactory;
    private readonly FileCacheInfoFactory _fileCacheInfoFactory;
    private int _globalFileCounter = 0;

    public CharaDataFileHandler(ILogger<CharaDataFileHandler> logger, BitTorrentService torrentService,
        DalamudUtilService dalamudUtilService, GameObjectHandlerFactory gameObjectHandlerFactory,
        PlayerDataFactory playerDataFactory, FileCacheInfoFactory fileCacheInfoFactory)
    {
        _torrentService = torrentService;
        _logger = logger;
        _dalamudUtilService = dalamudUtilService;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _playerDataFactory = playerDataFactory;
        _fileCacheInfoFactory = fileCacheInfoFactory;
        _mareCharaFileDataFactory = new(_fileCacheInfoFactory);
    }

    public void ComputeMissingFiles(CharaDataDownloadDto charaDataDownloadDto, out Dictionary<string, string> modPaths,
        out List<TorrentFileEntry> missingFiles)
    {
        modPaths = [];
        missingFiles = [];

        _logger.LogInformation("[ComputeMissingFiles] Analyzing {count} file paths from character data",
            charaDataDownloadDto.FileSwaps.Count);

        foreach (var file in charaDataDownloadDto.FileSwaps)
        {
            var localCache = _fileCacheInfoFactory.CreateFromTorrentFileEntry(file);
            localCache.ProcessFile().GetAwaiter().GetResult();
            var localCacheFile = localCache.TrueFile;
            if (localCacheFile == null)
            {
                if (localCache.TorrentFile != null)
                {
                    missingFiles.Add(new TorrentFileEntry(file.Hash, file.GamePath, localCache.TorrentFile));
                }
            }
            else
            {
                modPaths[file.GamePath] = localCacheFile.FullName;
            }
        }

        // foreach (var swap in charaDataDownloadDto.FileRedirects)
        // {
        //     modPaths[swap.GamePath] = swap.SwapPath;
        // }

        _logger.LogInformation(
            "[ComputeMissingFiles] Summary: {missingCount} missing files, {modPathCount} existing mod paths, {swapCount} file swaps",
            missingFiles.Count, modPaths.Count - charaDataDownloadDto.FileSwaps.Count,
            charaDataDownloadDto.FileSwaps.Count);
    }

    public async Task<CharacterData?> CreatePlayerData()
    {
        var chara = await _dalamudUtilService.GetPlayerCharacterAsync().ConfigureAwait(false);
        if (_dalamudUtilService.IsInGpose)
        {
            chara = (IPlayerCharacter?)(await _dalamudUtilService
                .GetGposeCharacterFromObjectTableByNameAsync(chara.Name.TextValue, _dalamudUtilService.IsInGpose)
                .ConfigureAwait(false));
        }

        if (chara == null)
            return null;

        using var tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
            () => _dalamudUtilService.GetCharacterFromObjectTableByIndex(chara.ObjectIndex)?.Address ?? IntPtr.Zero,
            isWatched: false).ConfigureAwait(false);
        PlayerData.Data.CharacterData newCdata = new();
        var fragment = await _playerDataFactory.BuildCharacterData(tempHandler, CancellationToken.None)
            .ConfigureAwait(false);
        newCdata.SetFragment(ObjectKind.Player, fragment);

        return newCdata.ToAPI();
    }

    public void Dispose()
    {
        _torrentService.Dispose();
    }

    public async Task DownloadFilesAsync(GameObjectHandler tempHandler, List<TorrentFileEntry> missingFiles,
        Dictionary<string, string> modPaths, CancellationToken token)
    {
        _logger.LogInformation("[DownloadFilesAsync] Starting download for {count} missing files", missingFiles.Count);

        // Log details of missing files and their magnet links
        foreach (var file in missingFiles)
        {
            var cacheInfo = _fileCacheInfoFactory.CreateFromTorrentFileEntry(file);
            await cacheInfo.ProcessFile().ConfigureAwait(false);
        }

        _logger.LogInformation("[DownloadFilesAsync] Download phase completed, checking for locally cached files...");
        token.ThrowIfCancellationRequested();
        foreach (var file in missingFiles)
        {
            var localFileCache = _fileCacheInfoFactory.CreateFromTorrentFileEntry(file);
            await localFileCache.ProcessFile().ConfigureAwait(false);
            var localFile = localFileCache.TrueFile;
            if (localFile == null)
            {
                _logger.LogError(
                    "[DownloadFilesAsync] File not found locally after download: Hash={hash}, GamePath={path}",
                    file.Hash, file.GamePath);
                throw new FileNotFoundException("File not found locally.");
            }
            else
            {
                _logger.LogDebug("[DownloadFilesAsync] Found local file for {hash}: {path}", file.Hash, localFile);
                modPaths[file.GamePath] = localFile.FullName;
            }
        }

        _logger.LogInformation("[DownloadFilesAsync] Download process completed successfully");
    }

    public Task<(MareCharaFileHeader loadedCharaFile, long expectedLength)> LoadCharaFileHeader(string filePath)
    {
        try
        {
            using var unwrapped = File.OpenRead(filePath);
            using var lz4Stream = new LZ4Stream(unwrapped, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
            using var reader = new BinaryReader(lz4Stream);
            var loadedCharaFile = MareCharaFileHeader.FromBinaryReader(filePath, reader);

            _logger.LogInformation("Read Mare Chara File");
            _logger.LogInformation("Version: {ver}", (loadedCharaFile?.Version ?? -1));
            long expectedLength = 0;
            if (loadedCharaFile != null)
            {
                _logger.LogTrace("Data");
                foreach (var item in loadedCharaFile.CharaFileData.FileSwaps)
                {
                    _logger.LogTrace("Swap: {gamePath} => {fileSwapPath}", item.GamePath, item.FileSwapPath);
                }

                var itemNr = 0;
                foreach (var item in loadedCharaFile.CharaFileData.Files)
                {
                    _logger.LogTrace("File {itemNr}: {gamePath} = {len}", itemNr, item.GamePath, item.TruePath);
                }

                _logger.LogInformation("Expected length: {expected}", expectedLength.ToByteString());
            }
            else
            {
                throw new InvalidOperationException("MCDF Header was null");
            }

            return Task.FromResult((loadedCharaFile, expectedLength));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse MCDF header of file {file}", filePath);
            throw;
        }
    }

    public Dictionary<string, string> McdfExtractFiles(MareCharaFileHeader? charaFileHeader, long expectedLength,
        List<string> extractedFiles)
    {
        if (charaFileHeader == null) return [];

        using var lz4Stream = new LZ4Stream(File.OpenRead(charaFileHeader.FilePath), LZ4StreamMode.Decompress,
            LZ4StreamFlags.HighCompression);
        using var reader = new BinaryReader(lz4Stream);
        MareCharaFileHeader.AdvanceReaderToData(reader);

        long totalRead = 0;
        Dictionary<string, string> gamePathToFilePath = new(StringComparer.Ordinal);
        foreach (var fileData in charaFileHeader.CharaFileData.Files)
        {
            var fileName = Path.Combine(_torrentService.FilesDirectory, "mare_" + _globalFileCounter++ + ".tmp");
            extractedFiles.Add(fileName);
            var length = new FileInfo(fileName).Length;
            var bufferSize = length;
            using var fs = File.OpenWrite(fileName);
            using var wr = new BinaryWriter(fs);
            _logger.LogTrace("Reading {length} of {fileName}", length.ToByteString(), fileName);
            var buffer = reader.ReadBytes((int)bufferSize);
            wr.Write(buffer);
            wr.Flush();
            wr.Close();
            if (buffer.Length == 0) throw new EndOfStreamException("Unexpected EOF");

            gamePathToFilePath[fileData.GamePath] = fileName;
            _logger.LogTrace("{path} => {fileName} [{hash}]", fileData.GamePath, fileName, fileData.Hash);

            totalRead += length;
            _logger.LogTrace("Read {read}/{expected} bytes", totalRead.ToByteString(), expectedLength.ToByteString());
        }

        return gamePathToFilePath;
    }

    public async Task UpdateCharaDataAsync(CharaDataExtendedUpdateDto updateDto)
    {
        var data = await CreatePlayerData().ConfigureAwait(false);

        if (data != null)
        {
            var hasGlamourerData = data.GlamourerData.TryGetValue(ObjectKind.Player, out var playerDataString);
            if (!hasGlamourerData) updateDto.GlamourerData = null;
            else updateDto.GlamourerData = playerDataString;

            var hasCustomizeData = data.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizeDataString);
            if (!hasCustomizeData) updateDto.CustomizeData = null;
            else updateDto.CustomizeData = customizeDataString;

            updateDto.ManipulationData = data.ManipulationData;

            updateDto.FileSwaps = data.FileSwaps.SelectMany(kvp => kvp.Value).ToList();

            // Cache local mod files and ensure they're available for torrent seeding
            try
            {
                //await _torrentService.CacheLocalModFiles(data, CancellationToken.None).ConfigureAwait(false);
                //await _torrentService.ManageTorrentStates(data, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to cache local mod files or manage torrent states during character data update");
            }
        }
    }

    internal async Task SaveCharaFileAsync(string description, string filePath)
    {
        var tempFilePath = filePath + ".tmp";

        try
        {
            var data = await CreatePlayerData().ConfigureAwait(false);
            if (data == null) return;

            var mareCharaFileData = _mareCharaFileDataFactory.Create(description, data);
            MareCharaFileHeader output = new(MareCharaFileHeader.CurrentVersion, mareCharaFileData);

            using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression);
            using var writer = new BinaryWriter(lz4);
            output.WriteToStream(writer);

            foreach (var item in output.CharaFileData.Files)
            {
                var fileCache = _fileCacheInfoFactory.CreateFromPath(item.TruePath, item.GamePath);
                await fileCache.ProcessFile().ConfigureAwait(false);
                var file = fileCache.TrueFile;
                if (file == null)
                    continue;
                _logger.LogDebug("Saving to MCDF: {hash}:{file}", item.Hash, file);
                _logger.LogDebug("\tAssociated GamePaths:");

                    _logger.LogDebug("\t{path}", item.GamePath);

                var fsRead = File.OpenRead(file.FullName);
                await using (fsRead.ConfigureAwait(false))
                {
                    using var br = new BinaryReader(fsRead);
                    writer.Write(br.ReadBytes((int)fsRead.Length));
                }
            }

            writer.Flush();
            await lz4.FlushAsync().ConfigureAwait(false);
            await fs.FlushAsync().ConfigureAwait(false);
            fs.Close();
            File.Move(tempFilePath, filePath, true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failure Saving Mare Chara File, deleting output");
            File.Delete(tempFilePath);
        }
    }
}