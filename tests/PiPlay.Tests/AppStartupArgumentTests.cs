namespace PiPlay.Tests;

/// <summary>
/// The command-line boundary (<see cref="App.ExtractUrlArg"/>). A launch argument is a startup
/// URL only when <c>YouTubeUrlHelper.TryParse</c> — the product's real URL parser — recognises it,
/// so the CLI, the address bar, and popout all share one definition of a supported target.
/// </summary>
[Trait(TestCategories.Key, TestCategories.Logic)]
public class AppStartupArgumentTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=42")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/playlist?list=PL1234567890")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]                    // scheme-less forms stay accepted
    [InlineData("youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void Supported_youtube_targets_are_taken_as_the_launch_url(string arg)
    {
        Assert.Equal(arg, App.ExtractUrlArg(new[] { arg }));
    }

    [Theory]
    [InlineData("youtube")]                                  // prefix bait, not a host
    [InlineData("youtu.be")]                                 // no video id
    [InlineData("https://youtu.be/")]
    [InlineData("youtube.com/evil")]
    [InlineData("https://www.youtube.com/")]                 // home page carries no target
    [InlineData("https://www.youtube.com/results?search_query=cats")]
    [InlineData("https://www.youtube.com/playlist")]         // no list id
    [InlineData("https://example.com")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]   // non-YouTube host, YouTube-shaped query
    [InlineData("https://youtube.com.evil.test/watch?v=dQw4w9WgXcQ")]
    [InlineData("--verbose")]
    [InlineData("")]
    public void Arguments_the_parser_rejects_are_not_taken_as_a_launch_url(string arg)
    {
        Assert.Null(App.ExtractUrlArg(new[] { arg }));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%E2%82%")]
    [InlineData("https://www.youtube.com/watch?v=%")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ%")]
    public void Malformed_escapes_never_throw_out_of_startup_argument_parsing(string arg)
    {
        Assert.Null(Record.Exception(() => Assert.Null(App.ExtractUrlArg(new[] { arg }))));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=%zz")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=99999999999999999999s")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=%E2%82")]
    public void A_valid_video_with_a_malformed_offset_or_list_is_still_handed_off_verbatim(string arg)
    {
        Assert.Equal(arg, App.ExtractUrlArg(new[] { arg }));
    }

    [Fact]
    public void A_bare_video_id_is_a_launch_url_like_it_is_in_the_address_bar()
    {
        // Newly accepted by delegating to TryParse - the old prefix check never took a bare id.
        // Nothing guards it: an 11-char [A-Za-z0-9_-] flag (e.g. -fullscreen) would read as a video
        // id, so revisit ExtractUrlArg at PiPlay's first launch flag (such an id still works as a URL).
        Assert.Equal("dQw4w9WgXcQ", App.ExtractUrlArg(new[] { "dQw4w9WgXcQ" }));
    }

    [Fact]
    public void The_first_supported_argument_wins_and_unsupported_ones_are_skipped()
    {
        Assert.Equal(
            "https://youtu.be/dQw4w9WgXcQ",
            App.ExtractUrlArg(new[] { "--verbose", "https://example.com", "https://youtu.be/dQw4w9WgXcQ" }));
    }

    [Fact]
    public void No_supported_argument_means_no_launch_url()
    {
        Assert.Null(App.ExtractUrlArg(Array.Empty<string>()));
        Assert.Null(App.ExtractUrlArg(new[] { "--verbose", "https://example.com" }));
    }
}
