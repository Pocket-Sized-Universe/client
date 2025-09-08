using Microsoft.Extensions.Logging;
using MonoTorrent;
using MonoTorrent.Client;
using PocketSizedUniverse.API.Dto.CharaData;
using PocketSizedUniverse.API.Dto.Files;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.MareConfiguration;
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
        private readonly MareConfigService _configService;
        private readonly ILogger<FileCacheInfo> _logger;

        public FileCacheInfo(string path, string gamePath, BitTorrentService bitTorrentService,
            ApiController apiController, IpcCallerPenumbra ipcCallerPenumbra, ILogger<FileCacheInfo> logger, MareConfigService configService)
        {
            _logger = logger;
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            _configService = configService;
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
            ApiController apiController, IpcCallerPenumbra ipcCallerPenumbra, ILogger<FileCacheInfo> logger, MareConfigService mareConfigService)
        {
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            _logger = logger;
            _configService = mareConfigService;
            GamePath = torrentFileEntry.GamePath;
            Extension = torrentFileEntry.Extension;
            Path = torrentFileEntry.Filename;
            Hash = torrentFileEntry.Hash;
            IsFileSwap = true;

            //_logger.LogInformation("FileCacheInfo created via TorrentFileEntry | FileSwap:{fileSwap} | GamePath:{gamePath} | Extension:{extension} | Hash:{hash}", IsFileSwap, GamePath, Extension, Hash.ShortHash());
        }

        public string Extension { get; private set; }

        public async Task<TorrentFileDto?> ProcessFile()
        {
            if (_apiController.IsSuperSeeder)
            {
                var list = await _apiController.GetSuperSeederPackage(50).ConfigureAwait(false);
                if (_bitTorrentService.TotalFileBytes <= _configService.Current.MaxFolderBytes)
                {
                    foreach (var file in list)
                    {
                        await _bitTorrentService.EnsureTorrentFileAndStart(file).ConfigureAwait(false);
                    }
                }
            }
            if (IsFileSwap)
            {
                try
                {
                    var torrentFile = await _apiController.GetTorrentFileForHash(Hash).ConfigureAwait(false);
                    var expectedFilePath =
                        System.IO.Path.Combine(_bitTorrentService.FilesDirectory, Hash.ShortHash() + Extension);

                    if (torrentFile == null)
                    {
                        // No existing torrent on server - try to create a new one from local file
                        if (File.Exists(Path))
                        {
                            _logger.LogInformation("No torrent for {Path}. Moving to {expectedFilePath} for seeding",
                                Path, expectedFilePath);
                            File.Copy(Path, expectedFilePath, true);
                            torrentFile = await CreateAndSeedNewTorrent(expectedFilePath)
                                .ConfigureAwait(false);
                            await _apiController.CreateNewTorrentFileEntry(torrentFile).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("Cannot create torrent - source file does not exist: {Path}", Path);
                            return null;
                        }
                    }
                    else
                    {
                        // Torrent exists on server - start downloading if file doesn't exist locally
                        if (!File.Exists(expectedFilePath))
                        {
                            _logger.LogInformation("Starting torrent download for {filename}",
                                Hash.ShortHash() + Extension);
                        }
                        else
                        {
                            _logger.LogDebug("File already exists locally: {filename}", Hash.ShortHash() + Extension);
                        }
                    }

                    if (torrentFile == null)
                        return null;

                    // Start or ensure the torrent is active (this handles both seeding and downloading)
                    await _bitTorrentService.EnsureTorrentFileAndStart(torrentFile).ConfigureAwait(false);
                    return torrentFile;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during torrent file processing");
                }
            }

            return null;
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
                Hash = Hash,
                Extension = Extension,
                IsForbidden = false,
                Data = torrent.Encode(),
                GamePath = GamePath
            };
        }

        public FileInfo? TrueFile 
        {
            get 
            {
                // If Path is just a filename (no directory separators), combine with FilesDirectory
                // If Path is already a full path, use it as-is
                string fullPath = System.IO.Path.IsPathRooted(Path) ? Path : System.IO.Path.Combine(_bitTorrentService.FilesDirectory, Path);
                var fileInfo = new FileInfo(fullPath);
                return fileInfo.Exists ? fileInfo : null;
            }
        }

        public string Path { get; private set; }
        public string GamePath { get; private set; }
        public byte[]? Hash { get; private set; }
        public bool IsFileSwap { get; private set; }
    }
}