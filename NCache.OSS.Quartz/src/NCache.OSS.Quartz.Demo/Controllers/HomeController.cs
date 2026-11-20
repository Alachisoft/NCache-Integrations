using Microsoft.AspNetCore.Mvc;

namespace NCache.OSS.Quartz.Demo.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();
}
