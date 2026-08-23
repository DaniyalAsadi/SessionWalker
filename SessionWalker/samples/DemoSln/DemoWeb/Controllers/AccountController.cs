using System.Web.Mvc;
using DemoWeb.Models;

namespace DemoWeb.Controllers
{
    /// <summary>
    /// Demonstrates indirect Session writes through property setter assignments.
    /// SessionPropertySetterAnalyzer should detect the assignments to
    /// userContext.UserId and userContext.UserName as indirect Session writes.
    /// </summary>
    public class AccountController : Controller
    {
        public void Login(string userId, string userName)
        {
            var userContext = new UserContext((System.Web.SessionState.HttpSessionState)Session);

            // These assignments should be detected as indirect Session writes
            // by SessionPropertySetterAnalyzer with AccessPath.PropertySetter.
            userContext.UserId = userId;
            userContext.UserName = userName;

            // This is NOT a Session write — DisplayName's setter doesn't touch Session.
            userContext.DisplayName = userName;

            // Direct Session write — detected by SessionUsageAnalyzer (not the new analyzer).
            Session["LastLogin"] = System.DateTime.Now;
        }
    }
}
