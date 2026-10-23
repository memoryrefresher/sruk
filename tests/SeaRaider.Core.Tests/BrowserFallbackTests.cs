using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SeaRaider.Core.WebChallenge;

namespace SeaRaider.Core.Tests;

/// <summary>The universal fallback: still blocked after verification → read the page from the verified browser session.</summary>
public class BrowserFallbackTests
{
    private const string Blocked = "<html><head><title>Just a moment...</title></head><body><script src=\"/cdn-cgi/challenge-platform/h/b\"></script></body></html>";

    private sealed class AlwaysBlocked : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(Blocked, System.Text.Encoding.UTF8, "text/html"),
            });
        }
    }

    private sealed class FakeResolver(BrowserPage? page) : IChallengeResolver
    {
        public int Resolves;
        public int Fetches;

        public bool IsAvailable => true;

        public event EventHandler<ChallengePrompt>? PromptChanged { add { } remove { } }

        public Task<ChallengeResolution> ResolveAsync(ChallengeRequest request, CancellationToken cancellationToken)
        {
            Resolves++;
            return Task.FromResult(new ChallengeResolution(true, "Mozilla/5.0 Test", [], null, null));
        }

        public Task<BrowserPage?> FetchPageAsync(Uri url, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(page);
        }

        public void BringToFront() { }
    }

    private static (HttpClient Client, AlwaysBlocked Inner, WebSessionStore Session) Build(IChallengeResolver resolver)
    {
        var inner = new AlwaysBlocked();
        var session = new WebSessionStore();
        var handler = new ChallengeHandler(session, resolver, NullLogger<ChallengeHandler>.Instance) { InnerHandler = inner };
        return (new HttpClient(handler), inner, session);
    }

    [Fact]
    public async Task Blocked_retry_is_answered_by_the_browser_and_the_site_is_remembered()
    {
        var page = new BrowserPage("<html><body>real page</body></html>", "text/html", new Uri("https://games.example/list"));
        var resolver = new FakeResolver(page);
        var (client, inner, session) = Build(resolver);

        var first = await client.GetAsync("https://games.example/list");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("real page", await first.Content.ReadAsStringAsync());
        Assert.Equal("text/html", first.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, resolver.Resolves);
        Assert.Equal(1, resolver.Fetches);
        Assert.True(session.IsBrowserOnly("games.example"));

        // Next page request goes straight to the browser: no more blocked HTTP calls, no new verification.
        var httpCallsBefore = inner.Calls;
        var second = await client.GetAsync("https://games.example/list?page=2");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(httpCallsBefore, inner.Calls);
        Assert.Equal(1, resolver.Resolves);
        Assert.Equal(2, resolver.Fetches);
    }

    [Fact]
    public async Task Without_a_browser_page_the_old_error_is_kept()
    {
        var resolver = new FakeResolver(null);
        var (client, _, session) = Build(resolver);

        var ex = await Assert.ThrowsAsync<ChallengeException>(() => client.GetAsync("https://games.example/list"));
        Assert.Contains("still blocks SeaRaider", ex.Message);
        Assert.False(session.IsBrowserOnly("games.example"));
    }

    [Fact]
    public async Task File_requests_never_use_the_page_fallback()
    {
        var page = new BrowserPage("<html>page</html>", "text/html", new Uri("https://games.example/file"));
        var resolver = new FakeResolver(page);
        var (client, _, _) = Build(resolver);

        var request = new HttpRequestMessage(HttpMethod.Get, "https://games.example/file");
        request.Options.Set(ChallengeRequestOptions.ExpectFile, true);

        await Assert.ThrowsAsync<ChallengeException>(() => client.SendAsync(request));
        Assert.Equal(0, resolver.Fetches);
    }
}
