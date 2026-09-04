using Microsoft.AspNetCore.Mvc;

namespace NCache.OSS.Hangfire.Demo.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}