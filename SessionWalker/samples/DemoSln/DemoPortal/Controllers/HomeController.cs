using System.Web.Mvc;

namespace DemoPortal.Controllers
{
    /// <summary>
    /// An MVC project with no Session usage at all. The dashboard should
    /// report "No Session Usage" and create a manual-review investigation
    /// case rather than silently reporting zero findings.
    /// </summary>
    public class HomeController : Controller
    {
        public void Index()
        {
            var name = "guest";
            var value = name.Length;
        }

        public void About()
        {
            var value = 42;
        }
    }
}
