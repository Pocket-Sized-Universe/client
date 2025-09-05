using Lumina.Data.Files;
using PocketSizedUniverse.API.Data;
using PocketSizedUniverse.API.Data.Enum;
using PocketSizedUniverse.Services.Mediator;
using PocketSizedUniverse.UI;
using PocketSizedUniverse.Utils;
using Microsoft.Extensions.Logging;

namespace PocketSizedUniverse.Services;

public sealed class CharacterAnalyzer : MediatorSubscriberBase, IDisposable
{
    private readonly XivDataAnalyzer _xivDataAnalyzer;
    private readonly BitTorrentService _torrentService;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource _baseAnalysisCts = new();
    private string _lastDataHash = string.Empty;

    public CharacterAnalyzer(ILogger<CharacterAnalyzer> logger, MareMediator mediator, XivDataAnalyzer modelAnalyzer, BitTorrentService torrentService)
        : base(logger, mediator)
    {
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (msg) =>
        {
            _baseAnalysisCts = _baseAnalysisCts.CancelRecreate();
            var token = _baseAnalysisCts.Token;
        });
        _xivDataAnalyzer = modelAnalyzer;
        _torrentService = torrentService;
    }

    public int CurrentFile { get; internal set; }
    public bool IsAnalysisRunning => _analysisCts != null;
    public int TotalFiles { get; internal set; }

    public void CancelAnalyze()
    {
        _analysisCts?.CancelDispose();
        _analysisCts = null;
    }

    public void Dispose()
    {
        _analysisCts.CancelDispose();
    }
    
}