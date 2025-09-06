using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using MonoTorrent.Dht;
using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.API.Dto.Files;
using PocketSizedUniverse.MareConfiguration;
using PocketSizedUniverse.PlayerData.Data;
using PocketSizedUniverse.PlayerData.Handlers;
using PocketSizedUniverse.Services.Mediator;
using PocketSizedUniverse.Utils;
using PocketSizedUniverse.WebAPI;
using ApiCharacterData = PocketSizedUniverse.API.Data.CharacterData;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CharacterData = PocketSizedUniverse.PlayerData.Data.CharacterData;

namespace PocketSizedUniverse.Services;

public class BitTorrentService : MediatorSubscriberBase, IDisposable, IHostedService
{
    private readonly ILogger<BitTorrentService> _logger;
    private readonly MareMediator _mediator;
    private readonly MareConfigService _configService;

    private ClientEngine _clientEngine;
    private readonly IPEndPoint _listenEndPoint;

    public string TorrentsDirectory => Path.Combine(_configService.Current.CacheFolder, "Torrents");
    public string FilesDirectory => Path.Combine(_configService.Current.CacheFolder, "Files");
    public string CacheDirectory => Path.Combine(_configService.Current.CacheFolder, "TorrentCache");
    private string DhtNodesPath => Path.Combine(_configService.Current.CacheFolder, "dht.dat");

    public BitTorrentService(ILogger<BitTorrentService> logger, MareMediator mediator,
        MareConfigService configService)
        : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _configService = configService;

        _listenEndPoint = new IPEndPoint(IPAddress.Any, _configService.Current.ListenPort);

        // Ensure torrents directory exists
        Directory.CreateDirectory(TorrentsDirectory);
        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(CacheDirectory);
        var settings = new EngineSettingsBuilder()
        {
            CacheDirectory = CacheDirectory,
            MaximumDownloadRate = _configService.Current.TorrentDownloadRateLimit,
            MaximumUploadRate = _configService.Current.TorrentUploadRateLimit,
            ListenEndPoints = new(StringComparer.Ordinal)
            {
                {"PsuPort", _listenEndPoint }
            }
        };

        _clientEngine = new ClientEngine(settings.ToSettings());
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting BitTorrent service");


        await _clientEngine.StartAllAsync().ConfigureAwait(false);
        _logger.LogInformation("Engine listening on {endpoint}", string.Join(',', _clientEngine.Settings.ListenEndPoints.Select(l => l.Value.ToString())));

        // Start the engine, which will also start DHT if configured
        _logger.LogInformation("BitTorrent engine initialized successfully");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping BitTorrent service");
    }

    public void Dispose()
    {
        _clientEngine.Dispose();
    }

    public async Task EnsureTorrentFileAndStart(TorrentFileDto torrentFileDto)
    {
        var torrentPath = Path.Combine(TorrentsDirectory, torrentFileDto.TorrentName);
        if (!File.Exists(torrentPath))
        {
            await File.WriteAllBytesAsync(torrentPath, torrentFileDto.Data).ConfigureAwait(false);
        }
        var torrent = await Torrent.LoadAsync(torrentPath).ConfigureAwait(false);
        await VerifyAndStartTorrent(torrent).ConfigureAwait(false);
    }

    private async Task VerifyAndStartTorrent(Torrent torrent)
    {
        if (_clientEngine.Torrents.Any(t => string.Equals(t.Name, torrent.Name, StringComparison.Ordinal)))
            return;
        var manager = await _clientEngine.AddAsync(torrent, FilesDirectory).ConfigureAwait(false);
        await manager.HashCheckAsync(true).ConfigureAwait(false);
    }

    public async Task<string?> GetFilePathForHash(string hash)
    {
        var manager = ActiveTorrents.FirstOrDefault(t => string.Equals(t.Name, hash, StringComparison.Ordinal));
        if (manager == null)
        {
            _logger.LogDebug("No torrent manager found for hash {hash}", hash);
            return null;
        }

        if (manager.Progress >= 100.00)
        {
            _logger.LogDebug("Torrent manager for hash {hash} is already complete", hash);
            return manager.Files.FirstOrDefault()?.FullPath;
        }

        _logger.LogDebug("Torrent manager for hash {hash} is not complete", hash);
        return null;
    }

    public async Task<TorrentFileDto> CreateAndSeedNewTorrent(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File does not exist", filePath);

        var fileInfo = new FileInfo(filePath);
        var hash = filePath.GetFileHash();
        var extension = Path.GetExtension(filePath);
        var newPath = Path.Combine(FilesDirectory, $"{hash}{extension}");
        File.Copy(filePath, newPath, overwrite: true);
        var creator = new TorrentCreator()
        {
            Comment = "Pocket Sized Universe File Share",
            CreatedBy = "Pocket Sized Universe Plugin",
            Publisher = "PSU Plugin",
            PieceLength = fileInfo.Length < 16 * 1024 * 1024 ? 65536 : 262144 // 64KB for <16MB, 256KB for larger
        };
        // Add trackers
        foreach (var tracker in _configService.Current.BitTorrentTrackers)
        {
            creator.Announces.Add([tracker]);
        }
        var torrent = await creator.CreateAsync(new TorrentFileSource(newPath), CancellationToken.None).ConfigureAwait(false);

        return new TorrentFileDto()
        {
            Hash = hash,
            Extension = extension,
            IsForbidden = false,
            Data = torrent.Encode()
        };
    }

    public Dictionary<string, TorrentManager> GetActiveTorrentsByHash()
    {
        var activeTorrents = new Dictionary<string, TorrentManager>(StringComparer.Ordinal);
        foreach (var manager in ActiveTorrents)
        {
            activeTorrents.Add(manager.Torrent?.Name ?? string.Empty, manager);
        }

        return activeTorrents;
    }

    public IReadOnlyCollection<TorrentManager> ActiveTorrents => _clientEngine.Torrents.AsReadOnly();
}