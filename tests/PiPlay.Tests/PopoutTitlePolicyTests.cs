using PiPlay.Services;
using Xunit;

namespace PiPlay.Tests;

[Trait(TestCategories.Key, TestCategories.Logic)]
public class PopoutTitlePolicyTests
{
    private const string WatchUrl = "https://www.youtube.com/watch?v=abcdefghijk";

    [Theory]
    [InlineData("Lofi beats to relax to - YouTube", "Lofi beats to relax to — PiPlay")]
    [InlineData("(3) Lofi beats to relax to - YouTube", "Lofi beats to relax to — PiPlay")]
    [InlineData("(99+) Talk - YouTube", "Talk — PiPlay")]
    [InlineData("  Spaced out - YouTube  ", "Spaced out — PiPlay")]
    [InlineData("Song - YouTube Music", "Song — PiPlay")]
    [InlineData("A - B - YouTube", "A - B — PiPlay")]                  // only the site suffix goes
    [InlineData("(Live) Concert - YouTube", "(Live) Concert — PiPlay")] // not a notification count
    [InlineData("Line\nbreak - YouTube", "Line break — PiPlay")]
    public void Video_titles_drop_youtube_noise(string documentTitle, string expected) =>
        Assert.Equal(expected, PopoutTitlePolicy.Format(documentTitle, WatchUrl));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YouTube")]
    [InlineData("(2) YouTube")]
    public void Pages_without_a_video_title_keep_the_generic_title(string? documentTitle) =>
        Assert.Equal(PopoutTitlePolicy.FallbackTitle, PopoutTitlePolicy.Format(documentTitle, WatchUrl));

    [Theory]
    [InlineData("https://piplay.local/player.html")]           // compact shell names itself
    [InlineData("https://accounts.google.com/signin")]
    [InlineData("http://www.youtube.com/watch?v=abcdefghijk")]  // not https
    [InlineData("https://www.youtube.com.evil.test/watch")]
    [InlineData("about:blank")]
    [InlineData("")]
    [InlineData(null)]
    public void Only_youtube_documents_name_the_popout(string? source) =>
        Assert.Equal(PopoutTitlePolicy.FallbackTitle, PopoutTitlePolicy.Format("Some title - YouTube", source));

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://m.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://music.youtube.com/watch?v=abcdefghijk")]
    public void Youtube_hosts_name_the_popout(string source) =>
        Assert.Equal("Some title — PiPlay", PopoutTitlePolicy.Format("Some title - YouTube", source));

    [Fact]
    public void Very_long_titles_are_shortened_with_an_ellipsis()
    {
        var title = PopoutTitlePolicy.Format(new string('x', 400) + " - YouTube", WatchUrl);

        Assert.EndsWith("… — PiPlay", title);
        Assert.Equal(PopoutTitlePolicy.MaxVideoTitleLength, title.Length - " — PiPlay".Length);
    }
}
