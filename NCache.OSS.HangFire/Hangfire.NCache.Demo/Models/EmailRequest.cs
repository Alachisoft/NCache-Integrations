namespace NCache.OSS.Hangfire.Demo.Models
{
    public class EmailRequest
    {
        public string Email { get; set; } = "user@test.com";
        public string UserName { get; set; } = "Test User";
    }
}
