using MonoTorrent.Client;
using PocketSizedUniverse.API.Dto.Files;
using PocketSizedUniverse.WebAPI;

namespace PocketSizedUniverse.Services.CharaData.Models
{
    public class FileCacheInfo
    {
        private readonly BitTorrentService _bitTorrentService;
        private readonly ApiController _apiController;
        private TorrentFileDto? _torrentFile;
        
        public FileCacheInfo(string hash, BitTorrentService bitTorrentService, ApiController apiController)
        {
            _bitTorrentService = bitTorrentService;
            _apiController = apiController;
            Hash = hash;
        }
        
        public string Hash { get; private set; }
        public TorrentFileDto? TorrentFile => _torrentFile;

        public TorrentManager? TorrentManager => _bitTorrentService.GetTorrentManagerByTorrentFile(TorrentFile);

        public FileInfo? TrueFile
        {
            get
            {
                if (TorrentFile == null) return null;
                if (TorrentManager == null) return null;
                var info = new FileInfo(TorrentManager.Torrent!.Files[0].Path);
                return info;
            }
        }

        public async Task<bool> EnsureTorrentFileAndStart()
        {
            if (_torrentFile == null)
            {
                _torrentFile = await _apiController.GetTorrentFileForHash(Hash).ConfigureAwait(false);
            }
            
            if (_torrentFile == null) return false;
            await _bitTorrentService.EnsureTorrentFileAndStart(_torrentFile).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> CopyFileToCache(string path)
        {
            var fileInfo = new FileInfo(path);
            var newPath = Path.Combine(_bitTorrentService.FilesDirectory, Hash + fileInfo.Extension);
            File.Copy(path, newPath, true);
            var torrentFileDto = await _bitTorrentService.CreateAndSeedNewTorrent(newPath).ConfigureAwait(false);
            _torrentFile = torrentFileDto;

            await _bitTorrentService.EnsureTorrentFileAndStart(_torrentFile).ConfigureAwait(false);
            return true;
        }
    }
}