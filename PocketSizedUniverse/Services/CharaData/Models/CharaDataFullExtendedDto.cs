using PocketSizedUniverse.API.Dto.CharaData;
using System.Collections.ObjectModel;

namespace PocketSizedUniverse.Services.CharaData.Models;

public sealed record CharaDataFullExtendedDto : CharaDataFullDto
{
    public CharaDataFullExtendedDto(CharaDataFullDto baseDto) : base(baseDto)
    {
        FullId = baseDto.Uploader.UID + ":" + baseDto.Id;
        MissingFiles = new ReadOnlyCollection<TorrentFileEntry>(baseDto.FileSwaps.ToList());
        HasMissingFiles = MissingFiles.Any();
    }

    public string FullId { get; set; }
    public bool HasMissingFiles { get; init; }
    public IReadOnlyCollection<TorrentFileEntry> MissingFiles { get; init; }
}
