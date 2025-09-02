namespace PocketSizedUniverse.WebAPI.Files.Models;

public enum DownloadStatus
{
    Initializing,
    WaitingForSlot,
    WaitingForQueue,
    Downloading,
    Decompressing
}