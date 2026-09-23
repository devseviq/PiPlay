namespace PiPlay;

/// <summary>
/// How the Source failure panel presents the WebView2 Runtime download next to Retry (polish
/// review 2026-09-10 F-3). Two accent buttons side by side gave the user no lead; the panel now
/// leads with the one action that can succeed in that state.
/// </summary>
internal enum RuntimeLinkMode
{
    /// <summary>The runtime is missing: the download leads (accent) and Retry is the quiet follow-up.</summary>
    Primary,

    /// <summary>The runtime is there and failed: Retry leads, the download stays as a quiet way out.</summary>
    Secondary,

    /// <summary>A reload or restart is already running: the download would be noise.</summary>
    Hidden,
}
