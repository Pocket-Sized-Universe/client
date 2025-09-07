using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.API.Data.Enum;
using System.Text;
using System.Text.Json;

namespace PocketSizedUniverse.Services.CharaData.Models;

public record MareCharaFileData
{
    public string Description { get; set; } = string.Empty;
    public string GlamourerData { get; set; } = string.Empty;
    public string CustomizePlusData { get; set; } = string.Empty;
    public string ManipulationData { get; set; } = string.Empty;
    public List<FileData> Files { get; set; } = [];
    public List<FileSwap> FileSwaps { get; set; } = [];

    public MareCharaFileData() { }

    public MareCharaFileData(FileCacheInfoFactory fileFactory, string description, CharacterData dto)
    {
        Description = description;

        if (dto.GlamourerData.TryGetValue(ObjectKind.Player, out var glamourerData))
        {
            GlamourerData = glamourerData;
        }

        dto.CustomizePlusData.TryGetValue(ObjectKind.Player, out var customizePlusData);
        CustomizePlusData = customizePlusData ?? string.Empty;
        ManipulationData = dto.ManipulationData;

        if (dto.FileReplacements.TryGetValue(ObjectKind.Player, out var fileReplacements))
        {
            foreach (var file in fileReplacements)
            {
                FileSwaps.Add(new FileSwap(file.GamePath, file.SwapPath));
            }
        }

        if (dto.FileSwaps.TryGetValue(ObjectKind.Player, out var fileSwaps))
        {
            foreach (var swap in fileSwaps)
            {
                var truePath = fileFactory.CreateFromTorrentFileEntry(swap);
                try
                {
                    // Process files in background - this constructor pattern needs refactoring for proper async
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await truePath.ProcessFile().ConfigureAwait(false);
                            var trueFile = truePath.TrueFile;
                            if (trueFile != null)
                            {
                                Files.Add(new FileData(swap.GamePath, trueFile.FullName, swap.Hash));
                            }
                        }
                        catch (Exception ex)
                        {
                            // Log error but don't fail construction
                            Console.WriteLine($"Warning: Failed to process file {swap.GamePath}: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    // Log error but continue with other files
                    Console.WriteLine($"Warning: Failed to initiate file processing for {swap.GamePath}: {ex.Message}");
                }
            }
        }
    }

    public byte[] ToByteArray()
    {
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
    }

    public static MareCharaFileData FromByteArray(byte[] data)
    {
        return JsonSerializer.Deserialize<MareCharaFileData>(Encoding.UTF8.GetString(data))!;
    }

    public record FileSwap(string GamePath, string FileSwapPath);

    public record FileData(string GamePath, string TruePath, byte[] Hash);
}