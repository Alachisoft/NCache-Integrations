using System.Net;
using System.Net.Http;

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = int.MaxValue,

    SslOptions =
    {
        RemoteCertificateValidationCallback =
            (_, _, _, _) => true
    }
};

using var http = new HttpClient(handler)
{
    DefaultRequestVersion = HttpVersion.Version20,
    DefaultVersionPolicy =
        HttpVersionPolicy.RequestVersionOrHigher
};


var tasks = Enumerable.Range(1, 10)
    .Select(async i =>
    {
        try
        {
            Console.WriteLine(
                $"CLIENT START {i} {DateTime.Now:HH:mm:ss.fff}");

            var response =
                await http.GetAsync(
                    "https://localhost:7157/");

            var body =
                await response.Content.ReadAsStringAsync();

            Console.WriteLine(
                $"CLIENT END   {i} {DateTime.Now:HH:mm:ss.fff} " +
                $"Status={(int)response.StatusCode} " +
                $"Body={body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"CLIENT ERROR {i}: {ex}");
        }
    })
    .ToList();

var tasks2 = Enumerable.Range(1, 10)
    .Select(async i =>
    {
        try
        {
            Console.WriteLine(
                $"CLIENT START {i} {DateTime.Now:HH:mm:ss.fff}");

            var response =
                await http.GetAsync(
                    "https://localhost:7158/");

            var body =
                await response.Content.ReadAsStringAsync();

            Console.WriteLine(
                $"CLIENT END   {i} {DateTime.Now:HH:mm:ss.fff} " +
                $"Status={(int)response.StatusCode} " +
                $"Body={body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"CLIENT ERROR {i}: {ex}");
        }
    })
    .ToList();

await Task.WhenAll(tasks);
await Task.WhenAll(tasks2);

Console.WriteLine("DONE");