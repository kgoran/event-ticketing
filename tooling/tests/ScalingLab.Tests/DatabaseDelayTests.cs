using System.Net;
using System.Text;

namespace EventTicketing.Tooling.ScalingLab.Tests;

/// <summary>D57: the slow database is one latency toxic on Toxiproxy's "database" proxy, added, changed and removed.</summary>
public sealed class DatabaseDelayTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_delay_adds_a_latency_toxic_on_the_databases_answers()
    {
        var toxiproxy = new FakeToxiproxy(changeAnswers: HttpStatusCode.NotFound);

        await Delay(toxiproxy).SetAsync(50, CancellationToken);

        toxiproxy.Requests.Should().Equal(
            "POST /proxies/database/toxics/slow-database {\"attributes\":{\"latency\":50,\"jitter\":10}}",
            "POST /proxies/database/toxics {\"name\":\"slow-database\",\"type\":\"latency\",\"stream\":\"downstream\",\"toxicity\":1,\"attributes\":{\"latency\":50,\"jitter\":10}}");
    }

    [Fact]
    public async Task Another_delay_changes_the_toxic_in_place()
    {
        var toxiproxy = new FakeToxiproxy(changeAnswers: HttpStatusCode.OK);

        await Delay(toxiproxy).SetAsync(200, CancellationToken);

        toxiproxy.Requests.Should().ContainSingle().Which.Should().StartWith("POST /proxies/database/toxics/slow-database ");
    }

    [Fact]
    public async Task No_delay_removes_the_toxic_even_if_there_was_none()
    {
        var toxiproxy = new FakeToxiproxy(removeAnswers: HttpStatusCode.NotFound);

        await Delay(toxiproxy).SetAsync(0, CancellationToken);

        toxiproxy.Requests.Should().Equal("DELETE /proxies/database/toxics/slow-database");
    }

    [Fact]
    public async Task The_delay_is_read_from_the_toxic_and_is_0_without_it()
    {
        var slow = new FakeToxiproxy(toxics: """[{"name":"slow-database","type":"latency","stream":"downstream","toxicity":1,"attributes":{"latency":50,"jitter":10}}]""");
        var normal = new FakeToxiproxy(toxics: "[]");

        (await Delay(slow).GetAsync(CancellationToken)).Should().Be(50);
        (await Delay(normal).GetAsync(CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task A_delay_is_never_more_than_a_second()
    {
        var toxiproxy = new FakeToxiproxy(changeAnswers: HttpStatusCode.OK);

        await Delay(toxiproxy).SetAsync(60_000, CancellationToken);

        toxiproxy.Requests.Single().Should().Contain("\"latency\":1000");
    }

    private static DatabaseDelay Delay(FakeToxiproxy toxiproxy) =>
        new(new HttpClient(toxiproxy) { BaseAddress = new Uri("http://toxiproxy:8474/") });

    /// <summary>Toxiproxy's API as far as the lab uses it; it records each request as "METHOD /path body".</summary>
    private sealed class FakeToxiproxy(
        HttpStatusCode changeAnswers = HttpStatusCode.OK,
        HttpStatusCode removeAnswers = HttpStatusCode.NoContent,
        string toxics = "[]") : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : " " + await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(toxics, Encoding.UTF8, "application/json") };
            }

            Requests.Add($"{request.Method} {path}{body}");
            var status = request.Method == HttpMethod.Delete ? removeAnswers
                : path.EndsWith("/slow-database", StringComparison.Ordinal) ? changeAnswers
                : HttpStatusCode.OK;
            return new HttpResponseMessage(status);
        }
    }
}
