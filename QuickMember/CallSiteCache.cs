using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.CSharp.RuntimeBinder;

namespace QuickMember;

internal static class CallSiteCache
{
    private static readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object>>> s_getters = new();
    private static readonly ConcurrentDictionary<string, CallSite<Func<CallSite, object, object, object>>> s_setters = new();
    private static readonly CSharpArgumentInfo[] s_getterArgs = [CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null)];
    private static readonly CSharpArgumentInfo[] s_setterArgs = [CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.None, null), CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.UseCompileTimeType, null)];

    internal static object GetValue(string name, object target)
    {
        CallSite<Func<CallSite, object, object>> callSite = s_getters.GetOrAdd(name, static n =>
            CallSite<Func<CallSite, object, object>>.Create(
                Binder.GetMember(CSharpBinderFlags.None, n, typeof(CallSiteCache), s_getterArgs)));
        return callSite.Target(callSite, target);
    }

    internal static void SetValue(string name, object target, object value)
    {
        CallSite<Func<CallSite, object, object, object>> callSite = s_setters.GetOrAdd(name, static n =>
            CallSite<Func<CallSite, object, object, object>>.Create(
                Binder.SetMember(CSharpBinderFlags.None, n, typeof(CallSiteCache), s_setterArgs)));
        callSite.Target(callSite, target, value);
    }
}
