using System;
using System.Web.Mvc;

namespace AspNetOutputCache_TestApp.Controllers
{
    public class HomeController : Controller
    {
        [OutputCache(Duration = 10, VaryByParam = "none")]
        public ActionResult Index()
        {
            return Content("Cached at: " + DateTime.Now.ToString());
        }

        [OutputCache(CacheProfile = "DefaultCache")]
        public ActionResult Profiled()
        {
            return Content("Profile cached at: " + DateTime.Now.ToString());
        }
    }
}