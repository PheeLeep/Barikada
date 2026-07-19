namespace Sipat.Core;

public static class HttpFactory
{
    /// <summary>
    /// A plain current-Chrome string. The goal is to observe what an ordinary
    /// visitor is served, so this deliberately does not impersonate a named
    /// crawler such as Googlebot — doing so would trip cloaking paths and give
    /// unrepresentative results.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public static HttpClient Create(string? userAgent = null, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };

        var http = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("User-Agent", userAgent ?? DefaultUserAgent);
        return http;
    }
}
