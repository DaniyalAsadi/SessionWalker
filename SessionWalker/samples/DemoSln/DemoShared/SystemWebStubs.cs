// Source-only stand-ins for the ASP.NET types SessionWalker detects. Kept in
// source so the demo solution can be analyzed without System.Web /
// System.Web.Mvc binaries on the machine. The fully-qualified names are what
// matters: SessionSymbolDetector and ControllerActionDetector work on
// metadata names, not assemblies.
namespace System.Web
{
    public abstract class HttpSessionStateBase
    {
        public virtual object this[string name]
        {
            get { return null; }
            set { }
        }

        public virtual void Add(string name, object value) { }
        public virtual void Remove(string name) { }
        public virtual void RemoveAll() { }
        public virtual void Clear() { }
        public virtual void Abandon() { }
    }
}

namespace System.Web.SessionState
{
    public class HttpSessionState : System.Web.HttpSessionStateBase
    {
    }
}

namespace System.Web.Mvc
{
    public abstract class ControllerBase
    {
        public System.Web.HttpSessionStateBase Session { get; set; }
    }

    public abstract class Controller : ControllerBase
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class NonActionAttribute : System.Attribute
    {
    }

    [System.AttributeUsage(System.AttributeTargets.Method)]
    public sealed class ChildActionOnlyAttribute : System.Attribute
    {
    }
}
