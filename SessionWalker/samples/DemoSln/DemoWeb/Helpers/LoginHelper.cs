using System.Web;

namespace DemoWeb.Helpers
{
    public static class LoginHelper
    {
        public static void SetUserId(System.Web.HttpSessionStateBase session, int userId)
        {
            session["UserId"] = userId;
        }

        public static void SetUserId(int userId)
        {
            // Reaches Session through an overload; the tracer follows the call
            // chain inside the compilation.
            SetUserId(new System.Web.SessionState.HttpSessionState(), userId);
        }
    }
}
