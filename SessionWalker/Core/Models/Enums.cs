namespace SessionWalker.Core.Models;

/// <summary>
/// High-level classification of how Session state was touched at a call site.
/// </summary>
public enum SessionOperationType
{
    Read,
    Write,
    Remove,
    Clear,
    Abandon,
    Unknown
}

/// <summary>
/// Fine-grained classification of a Session mutation. Only populated when
/// <see cref="SessionOperationType"/> is not <see cref="SessionOperationType.Read"/>.
/// </summary>
public enum SessionMutationKind
{
    /// <summary>Session["key"] = value</summary>
    Assignment,

    /// <summary>Session.Add("key", value)</summary>
    Add,

    /// <summary>Session.Remove("key")</summary>
    Remove,

    /// <summary>Session.Clear()</summary>
    Clear,

    /// <summary>Session.RemoveAll()</summary>
    RemoveAll,

    /// <summary>Session.Abandon()</summary>
    Abandon
}

/// <summary>
/// How the tool arrived at a given classification. Direct member access on a
/// symbol whose type is provably an ASP.NET Session type is High. Access
/// reached through interprocedural tracing (helper methods, parameters) is
/// Medium. Anything only pattern-matched by name without a resolvable
/// symbol/type is Low and should never be silently treated as a definite write.
/// </summary>
public enum ConfidenceLevel
{
    High,
    Medium,
    Low
}

/// <summary>
/// Whether a given Session access happened directly at the call site, or was
/// discovered by tracing through a local variable, a method parameter, or a
/// call to another method (interprocedural).
/// </summary>
public enum AccessPath
{
    Direct,
    LocalVariableIndirection,
    MethodParameter,
    InterproceduralCall,
    PropertySetter
}
