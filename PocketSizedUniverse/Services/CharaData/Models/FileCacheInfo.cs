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
        
        public FileCacheInfo(string path, string gamePath, BitTorrentService bitTorrentService, ApiController apiController, IpcCallerPenumbra ipcCallerPenumbra)
        {
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            GamePath = gamePath;
            Extension = System.IO.Path.GetExtension(path);
            if (_ipcCallerPenumbra.ModDirectory != null && path.Contains(_ipcCallerPenumbra.ModDirectory) && File.Exists(path))
            {
                IsFileSwap = true;
                var bytes = File.ReadAllBytes(path);
                Hash = SHA256.Create().ComputeHash(bytes);
                Path = path.Replace(_ipcCallerPenumbra.ModDirectory, "");
            }
            else
            {
                IsFileSwap = false;
                Path = path;
            }
        }

        public FileCacheInfo(TorrentFileDto torrentFileEntry, BitTorrentService bitTorrentService,
            ApiController apiController, IpcCallerPenumbra ipcCallerPenumbra)
        {
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            _ipcCallerPenumbra = ipcCallerPenumbra;
            GamePath = torrentFileEntry.GamePath;
            Extension = torrentFileEntry.Extension;
            Path = torrentFileEntry.Filename;
            Hash = torrentFileEntry.Hash;
            IsFileSwap = true;
            TorrentFile = torrentFileEntry;
        }

        public string Extension { get; private set; }

        public TorrentFileDto? TorrentFile { get; private set; }

        public async Task ProcessFile()
        {
            if (IsFileSwap)
            {
                if (TorrentFile == null)
                    TorrentFile = await _apiController.GetTorrentFileForHash(Hash).ConfigureAwait(false);
                if (TorrentFile == null)
                {
                    var oldPath = System.IO.Path.Combine(_ipcCallerPenumbra!.ModDirectory, Path);
                    var newPath = System.IO.Path.Combine(_bitTorrentService.FilesDirectory, ShortHash + Extension);
                    File.Move(oldPath, newPath, true);
                    TorrentFile = await _bitTorrentService.CreateAndSeedNewTorrent(newPath, Hash).ConfigureAwait(false);
                }
                await _bitTorrentService.EnsureTorrentFileAndStart(TorrentFile).ConfigureAwait(false);
            }
        }

        public FileInfo? TrueFile => new FileInfo(System.IO.Path.Combine(_bitTorrentService.FilesDirectory, Path));

        public string Path { get; private set; }
        public string GamePath { get; private set; }
        public byte[]? Hash { get; private set; }
        public string? ShortHash
        {
            get
            {
                if (Hash != null)
                {
                    return Convert.ToBase64String(Hash);
                }
                return null;
            }
        }

        public bool IsFileSwap { get; private set; }
    }
}