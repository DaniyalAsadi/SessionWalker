using System.Collections.Generic;
using System.Web.Mvc;

namespace DemoWeb.Controllers
{
    /// <summary>
    /// Deliberately mixes real Session usage with Session-shaped accesses
    /// that are NOT Session (a dictionary field named _session and a local
    /// named session) so the dashboard shows both detected operations and
    /// rejected candidates.
    /// </summary>
    public class BAController : Controller
    {
        private readonly Dictionary<string, object> _session = new Dictionary<string, object>();

        public void Index()
        {
            Session["cart"] = new object();      // detected: Write (element access)
            var item = Session["cart"];          // detected: Read
            Session.Remove("This is Tests");     // detected: Remove / Write
            Session.Add("user", 42);             // detected: Add / Write
            Session.Abandon();                   // detected: Abandon / Write

            _session["local-data"] = 1;          // NOT Session -> rejected candidate
            var session = new System.Collections.Hashtable();
            session["x"] = 1;                    // NOT Session -> rejected candidate
        }

        public void Login()
        {
            // No Session at the call site; the write is found by
            // interprocedural tracing into LoginHelper.
            DemoWeb.Helpers.LoginHelper.SetUserId(42);
        }
    }
}
