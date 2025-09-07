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
using PocketSizedUniverse.Services.CharaData.Models;
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

public class BitTorrentService : MediatorSubscriberBase
{
    private readonly ILogger<BitTorrentService> _logger;
    private readonly MareMediator _mediator;
    private readonly MareConfigService _configService;

    private ClientEngine _clientEngine;

    public string TorrentsDirectory => Path.Combine(_configService.Current.CacheFolder, "Torrents");
    public string FilesDirectory => Path.Combine(_configService.Current.CacheFolder, "Files");
    public string CacheDirectory => Path.Combine(_configService.Current.CacheFolder, "TorrentCache");
    private string DhtNodesPath => Path.Combine(_configService.Current.CacheFolder, "dht.dat");

    private bool CacheDirectoryWritable
    {
        get
        {
            try
            {
                var touchFile = Path.Combine(_configService.Current.CacheFolder, "touch");
                File.Create(touchFile).Close();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public BitTorrentService(ILogger<BitTorrentService> logger, MareMediator mediator,
        MareConfigService configService)
        : base(logger, mediator)
    {
        _logger = logger;
        _mediator = mediator;
        _configService = configService;
        if (!CacheDirectoryWritable)
        {
            _configService.Current.CacheFolder = Path.GetTempPath();
            _configService.Save();
        }

        var settings = new EngineSettingsBuilder()
        {
            CacheDirectory = CacheDirectory,
            MaximumDownloadRate = _configService.Current.TorrentDownloadRateLimit,
            MaximumUploadRate = _configService.Current.TorrentUploadRateLimit,
            AllowLocalPeerDiscovery = true,
            AllowPortForwarding = true,
            ListenEndPoints = new(StringComparer.Ordinal)
            {
                { "IPv4", new IPEndPoint(IPAddress.Parse("0.0.0.0"), _configService.Current.ListenPort) },
                { "IPv6", new IPEndPoint(IPAddress.IPv6Any, _configService.Current.ListenPort) }
            },
            DhtEndPoint = new IPEndPoint(IPAddress.Any, 42070),
        };
        Directory.CreateDirectory(TorrentsDirectory);
        Directory.CreateDirectory(FilesDirectory);
        Directory.CreateDirectory(CacheDirectory);
        _clientEngine = new ClientEngine(settings.ToSettings());

        _ = Task.Run(async () => 
        {
            try 
            {
                await StartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start BitTorrent engine");
            }
        });
    }

    public async Task StartAsync()
    {
        _logger.LogInformation("Starting BitTorrent service");
        await _clientEngine.StartAllAsync().ConfigureAwait(false);
        _logger.LogInformation("Engine listening on {endpoint}", string.Join(',', _clientEngine.Settings.ListenEndPoints.Select(l => l.Value.ToString())));
        _logger.LogInformation("BitTorrent engine initialized successfully");
    }

    public async Task EnsureTorrentFileAndStart(TorrentFileDto torrentFileDto)
    {
        var torrent = await Torrent.LoadAsync(torrentFileDto.Data).ConfigureAwait(false);
        await VerifyAndStartTorrent(torrent).ConfigureAwait(false);
    }

    private async Task VerifyAndStartTorrent(Torrent torrent)
    {
        try
        {
            // Check if we already have this torrent in our engine
            if (_clientEngine.Torrents.Any(t => t.Torrent != null && 
                                   string.Equals(t.Torrent.Name, torrent.Name, StringComparison.Ordinal)))
            {
                return;
            }
            
            var manager = await _clientEngine.AddAsync(torrent, FilesDirectory).ConfigureAwait(false);
            await manager.HashCheckAsync(true).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error while checking torrent");
        }
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