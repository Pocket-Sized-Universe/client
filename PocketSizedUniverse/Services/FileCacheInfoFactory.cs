using PocketSizedUniverse.API.Dto.CharaData;
using PocketSizedUniverse.API.Dto.Files;
using PocketSizedUniverse.Interop.Ipc;
using PocketSizedUniverse.Services.CharaData.Models;
using PocketSizedUniverse.WebAPI;
using System.Collections.Concurrent;

namespace PocketSizedUniverse.Services
{
    public class FileCacheInfoFactory
    {
        private readonly ApiController _apiController;
        private readonly BitTorrentService _bitTorrentService;
        private readonly IpcCallerPenumbra _ipcCallerPenumbra;

        public FileCacheInfoFactory(ApiController apiController, BitTorrentService bitTorrentService, IpcCallerPenumbra ipcCallerPenumbra)
        {
            _apiController = apiController;
            _bitTorrentService = bitTorrentService;
            _ipcCallerPenumbra = ipcCallerPenumbra;
        }

        public FileCacheInfo CreateFromPath(string path, string gamePath)
        {
            return new FileCacheInfo(path, gamePath, _bitTorrentService, _apiController, _ipcCallerPenumbra);
        }

        public FileCacheInfo CreateFromTorrentFileEntry(TorrentFileEntry torrentFileEntry)
        {
            return new FileCacheInfo(torrentFileEntry.TorrentFile, _bitTorrentService, _apiController, _ipcCallerPenumbra);
        }

        public FileCacheInfo CreateFromTorrentFileDto(TorrentFileDto torrentFileDto)
        {
            return new FileCacheInfo(torrentFileDto, _bitTorrentService, _apiController, _ipcCallerPenumbra);
        }
    }
}
