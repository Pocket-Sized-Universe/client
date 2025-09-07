using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.Services.CharaData.Models;

namespace PocketSizedUniverse.Services.CharaData;

public sealed class MareCharaFileDataFactory
{
    private readonly FileCacheInfoFactory _bitTorrentService;

    public MareCharaFileDataFactory(FileCacheInfoFactory fileCacheManager)
    {
        _bitTorrentService = fileCacheManager;
    }

    public MareCharaFileData Create(string description, CharacterData characterCacheDto)
    {
        return new MareCharaFileData(_bitTorrentService, description, characterCacheDto);
    }
}