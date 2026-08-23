using System.Web;
using System.Web.SessionState;

namespace DemoWeb.Models
{
    /// <summary>
    /// Demonstrates indirect Session writes through property setters.
    /// SessionPropertySetterAnalyzer should detect assignments to UserId
    /// and UserName as indirect Session writes.
    /// </summary>
    public class UserContext
    {
        private readonly HttpSessionState _session;

        public UserContext(HttpSessionState session)
        {
            _session = session;
        }

        public string UserId
        {
            get
            {
                return _session["UserId"] as string;
            }
            set
            {
                _session["UserId"] = value;
            }
        }

        public string UserName
        {
            get
            {
                return _session["UserName"] as string;
            }
            set
            {
                _session["UserName"] = value;
            }
        }

        // This property does NOT write to Session — should not be detected.
        public string DisplayName { get; set; }
    }
}
