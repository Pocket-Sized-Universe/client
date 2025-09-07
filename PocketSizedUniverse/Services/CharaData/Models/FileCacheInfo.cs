using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using PocketSizedUniverse.API.Dto.CharaData;
using PocketSizedUniverse.API.Dto.Files;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.WebAPI;
using System.Security.Cryptography;
using System.Text;

namespace PocketSizedUniverse.Services.CharaData.Models
{
    public class FileCacheInfo
    {
        private readonly BitTorrentService _bitTorrentService;
        private readonly ApiController _apiController;
        private readonly IpcCallerPenumbra _ipcCallerPenumbra;
        private readonly ILogger<FileCacheInfo> _logger;

        public FileCacheInfo(string path, string gamePath, BitTorrentService bitTorrentService,
            ApiController apiController, IpcCallerPenumbra ipcCallerPenumbra, ILogger<FileCacheInfo> logger)
        {
            _logger = logger;
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            GamePath = gamePath;
            Extension = System.IO.Path.GetExtension(path);
            if (File.Exists(path))
            {
                IsFileSwap = true;
                var bytes = File.ReadAllBytes(path);
                Hash = SHA256.Create().ComputeHash(bytes);
                Path = path;
            }
            else if (string.IsNullOrEmpty(path))
            {
                IsFileSwap = true;
                Path = path;
            }
            else
            {
                IsFileSwap = false;
                Path = path;
            }
            //_logger.LogInformation("FileCacheInfo created for {path} | FileSwap:{fileSwap} | GamePath:{gamePath} | Extension:{extension} | Hash:{hash}", path, IsFileSwap, GamePath, Extension, Hash.ShortHash());
        }

        public FileCacheInfo(TorrentFileDto torrentFileEntry, BitTorrentService bitTorrentService,
            ApiController apiController, IpcCallerPenumbra ipcCallerPenumbra, ILogger<FileCacheInfo> logger)
        {
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            _logger = logger;
            GamePath = torrentFileEntry.GamePath;
            Extension = torrentFileEntry.Extension;
            Path = torrentFileEntry.Filename;
            Hash = torrentFileEntry.Hash;
            IsFileSwap = true;
            TorrentFile = torrentFileEntry;

            //_logger.LogInformation("FileCacheInfo created via TorrentFileEntry | FileSwap:{fileSwap} | GamePath:{gamePath} | Extension:{extension} | Hash:{hash}", IsFileSwap, GamePath, Extension, Hash.ShortHash());
        }

        public string Extension { get; private set; }

        public TorrentFileDto? TorrentFile { get; private set; }

        public async Task ProcessFile()
        {
            if (IsFileSwap && TorrentFile == null)
            {
                TorrentFile = await _apiController.GetTorrentFileForHash(Hash).ConfigureAwait(false);
                if (TorrentFile == null)
                {
                    var newPath = System.IO.Path.Combine(_bitTorrentService.FilesDirectory, Hash.ShortHash() + Extension);
                    if (File.Exists(Path))
                    {
                        _logger.LogInformation("No torrent for {Path}. Moving to {newPath} for seeding", Path, newPath);
                        File.Copy(Path, newPath, true);
                        TorrentFile = await CreateAndSeedNewTorrent(newPath)
                            .ConfigureAwait(false);
                    }
                }
            }
            if (TorrentFile == null) throw new InvalidOperationException("TorrentFile is null");

            await _bitTorrentService.EnsureTorrentFileAndStart(TorrentFile).ConfigureAwait(false);
        }



        public List<string> BitTorrentTrackers { get; set; } = ["udp://tracker.opentrackr.org:1337/announce"];
        public async Task<TorrentFileDto> CreateAndSeedNewTorrent(string newPath)
        {
            var fileInfo = new FileInfo(Path);
            var creator = new TorrentCreator()
            {
                Comment = "Pocket Sized Universe File Share",
                CreatedBy = "Pocket Sized Universe Plugin",
                Publisher = "PSU Plugin",
            };
            // Add trackers
            foreach (var tracker in BitTorrentTrackers)
            {
                creator.Announces.Add([tracker]);
            }

            var torrent = await creator.CreateAsync(new TorrentFileSource(newPath), CancellationToken.None)
                .ConfigureAwait(false);

            return new TorrentFileDto()
            {
                Hash = Hash, Extension = Extension, IsForbidden = false, Data = torrent.Encode(), GamePath = GamePath
            };
        }

        public FileInfo? TrueFile => new FileInfo(System.IO.Path.Combine(_bitTorrentService.FilesDirectory, Path));

        public string Path { get; private set; }
        public string GamePath { get; private set; }
        public byte[]? Hash { get; private set; }
        public bool IsFileSwap { get; private set; }
    }
}