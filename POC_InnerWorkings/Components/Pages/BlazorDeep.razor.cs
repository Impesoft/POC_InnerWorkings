using System.Collections;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace POC_InnerWorkings.Components.Pages;

public partial class BlazorDeep
{
    private string rendererInfo = "Click the button to inspect...";

    private void InspectButton()
    {
        var sb = new StringBuilder();

        sb.AppendLine("╔══════════════════════════════════════════════════════════════════════");
        sb.AppendLine("║ RENDERER INTERNAL INSPECTION (ROBUST)");
        sb.AppendLine("╚══════════════════════════════════════════════════════════════════════");
        sb.AppendLine();

        try
        {
            var renderHandle = GetPrivateFieldValue(ComponentBaseType(), this, "_renderHandle");
            sb.AppendLine($"✅ RenderHandle Retrieved: {renderHandle?.GetType().Name ?? "null"}");
            sb.AppendLine($"   Instance: {renderHandle?.GetHashCode()}");
            sb.AppendLine();

            if (renderHandle is null)
            {
                sb.AppendLine("❌ Could not retrieve RenderHandle.");
                rendererInfo = sb.ToString();
                return;
            }

            var renderer = GetPrivateFieldValue(renderHandle.GetType(), renderHandle, "_renderer");
            sb.AppendLine($"✅ Renderer Retrieved: {renderer?.GetType().FullName ?? "null"}");
            sb.AppendLine($"   Instance: {renderer?.GetHashCode()}");
            sb.AppendLine();

            if (renderer is null)
            {
                sb.AppendLine("❌ Could not retrieve Renderer from RenderHandle._renderer.");
                rendererInfo = sb.ToString();
                return;
            }

            var componentId = GetPrivateFieldValue(renderHandle.GetType(), renderHandle, "_componentId");
            sb.AppendLine($"📋 THIS COMPONENT:");
            sb.AppendLine($"   • Component ID: {componentId ?? "(unknown)"}");
            sb.AppendLine($"   • Component Type: {GetType().FullName}");
            sb.AppendLine($"   • Instance Hash: {GetHashCode()}");
            sb.AppendLine();

            sb.AppendLine("🔎 Searching for event handler storage (ulong-keyed dictionary-like) ...");
            sb.AppendLine();

            var foundAny = false;

            // 1) Try common historical names first (fast path)
            foreach (var name in new[]
                     {
                         "_eventBindings",
                         "_eventHandlers",
                         "_eventHandlerIdToCallback",
                         "_eventCallbackById",
                         "_eventDelegates",
                         "_eventBindingsById",
                         "_eventHandlerRegistry",
                         "_eventDispatcher",
                     })
            {
                if (TryDumpField(sb, renderer, name))
                {
                    foundAny = true;
                }
            }

            // 2) Heuristic scan: find ANY private field that looks like a ulong-keyed dictionary/registry
            sb.AppendLine();
            sb.AppendLine("🔬 Heuristic scan of all private instance fields for ulong-keyed dictionaries...");
            sb.AppendLine();

            var rendererType = renderer.GetType();
            var fields = GetAllInstanceFields(rendererType);

            var candidates = new List<(FieldInfo Field, object? Value)>();

            foreach (var f in fields)
            {
                object? value;
                try { value = f.GetValue(renderer); }
                catch { continue; }

                if (value is null)
                    continue;

                if (LooksLikeUlongKeyedDictionary(value.GetType()))
                {
                    candidates.Add((f, value));
                }
                else
                {
                    // Sometimes the mapping is nested inside an internal registry object
                    // Example: _eventHandlerRegistry (then inside it: Dictionary<ulong, ...>)
                    if (LooksEventy(f, value))
                    {
                        // Try one-level deep scan inside this object
                        var nested = FindNestedUlongKeyedDictionaries(value);
                        if (nested.Count > 0)
                        {
                            sb.AppendLine($"🧭 Found event-ish container: {f.Name} : {value.GetType().FullName}");
                            sb.AppendLine($"   Instance: {value.GetHashCode()}");

                            foreach (var n in nested)
                            {
                                sb.AppendLine($"   ✅ Nested mapping candidate: {n.Path}");
                                DumpDictionarySummary(sb, n.Value);
                                sb.AppendLine();
                                foundAny = true;
                            }
                        }
                    }
                }
            }

            if (candidates.Count > 0)
            {
                foundAny = true;
                sb.AppendLine("✅ Direct ulong-keyed dictionary-like candidates on the renderer:");
                sb.AppendLine();

                foreach (var (field, value) in candidates.OrderBy(c => c.Field.Name))
                {
                    sb.AppendLine($"🎯 Field: {field.Name}");
                    sb.AppendLine($"   DeclaringType: {field.DeclaringType?.FullName}");
                    sb.AppendLine($"   RuntimeType:   {value!.GetType().FullName}");
                    DumpDictionarySummary(sb, value);
                    sb.AppendLine();
                }
            }

            // 3) Component state (your existing check, kept)
            var componentStateById = TryGetField(renderer, "_componentStateById");
            if (componentStateById is not null)
            {
                sb.AppendLine("📦 COMPONENT STATE COLLECTION:");
                sb.AppendLine($"   • Field: _componentStateById");
                sb.AppendLine($"   • Type: {componentStateById.GetType().FullName}");
                var count = TryGetCount(componentStateById);
                if (count is not null)
                    sb.AppendLine($"   • Active Components in This Circuit: {count}");
                sb.AppendLine();
            }

            sb.AppendLine("═══════════════════════════════════════════════════════════════════════");
            sb.AppendLine("CONCLUSION:");
            sb.AppendLine("═══════════════════════════════════════════════════════════════════════");
            sb.AppendLine();

            sb.AppendLine("✅ Renderer exists as a .NET object in server memory");
            sb.AppendLine("✅ Event dispatch is still keyed by eventHandlerId (ulong) (see DispatchEventAsync signature)");
            sb.AppendLine("✅ Internal storage can move/rename; this page now *discovers* it instead of hardcoding _eventBindings");
            sb.AppendLine();

            if (!foundAny)
            {
                sb.AppendLine("⚠️ Could not locate a ulong-keyed mapping via reflection in this build.");
                sb.AppendLine("   This usually means the mapping is:");
                sb.AppendLine("   • stored in a nested registry object deeper than 1 level, or");
                sb.AppendLine("   • stored in a non-dictionary structure, or");
                sb.AppendLine("   • protected by runtime trimming / AOT / internal refactors.");
                sb.AppendLine();
                sb.AppendLine("   Next step: extend FindNestedUlongKeyedDictionaries(...) to recurse deeper (2-3 levels).");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"❌ Exception during reflection: {ex}");
        }

        rendererInfo = sb.ToString();
    }

    private static Type ComponentBaseType() => typeof(ComponentBase);

    private static object? GetPrivateFieldValue(Type type, object instance, string fieldName)
    {
        var f = GetAllInstanceFields(type).FirstOrDefault(x => x.Name == fieldName);
        return f?.GetValue(instance);
    }

    private static IEnumerable<FieldInfo> GetAllInstanceFields(Type t)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;

        while (t != null)
        {
            foreach (var f in t.GetFields(flags))
                yield return f;

            t = t.BaseType!;
        }
    }

    private static object? TryGetField(object obj, string fieldName)
    {
        var f = GetAllInstanceFields(obj.GetType()).FirstOrDefault(x => x.Name == fieldName);
        return f?.GetValue(obj);
    }

    private static int? TryGetCount(object obj)
    {
        var p = obj.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        if (p?.PropertyType == typeof(int))
            return (int?)p.GetValue(obj);

        return null;
    }

    private static bool TryDumpField(StringBuilder sb, object renderer, string fieldName)
    {
        var v = TryGetField(renderer, fieldName);
        if (v is null)
            return false;

        sb.AppendLine($"✅ Found field: {fieldName}");
        sb.AppendLine($"   RuntimeType: {v.GetType().FullName}");
        var count = TryGetCount(v);
        if (count is not null)
            sb.AppendLine($"   Count: {count}");

        if (LooksLikeUlongKeyedDictionary(v.GetType()))
        {
            DumpDictionarySummary(sb, v);
        }

        sb.AppendLine();
        return true;
    }

    private static bool LooksLikeUlongKeyedDictionary(Type t)
    {
        if (!t.IsGenericType)
            return false;

        var genDef = t.GetGenericTypeDefinition();

        // Dictionary<,>, ConcurrentDictionary<,>, etc. (best effort without referencing assembly types)
        if (genDef.FullName is null)
            return false;

        if (!genDef.FullName.Contains("Dictionary`2", StringComparison.OrdinalIgnoreCase))
            return false;

        var args = t.GetGenericArguments();
        return args.Length == 2 && args[0] == typeof(ulong);
    }

    private static bool LooksEventy(FieldInfo f, object value)
    {
        var name = f.Name;
        var typeName = value.GetType().FullName ?? value.GetType().Name;

        return name.Contains("event", StringComparison.OrdinalIgnoreCase)
               || name.Contains("handler", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("Event", StringComparison.OrdinalIgnoreCase)
               || typeName.Contains("Handler", StringComparison.OrdinalIgnoreCase);
    }

    private static void DumpDictionarySummary(StringBuilder sb, object dictLike)
    {
        // We avoid casting to IDictionary<ulong, T> because T is internal.
        // Instead, rely on Count + enumerator.
        var count = TryGetCount(dictLike);
        sb.AppendLine($"   • Dictionary-like Count: {count?.ToString() ?? "(unknown)"}");

        // Try to enumerate a few keys (works for Dictionary/ConcurrentDictionary)
        try
        {
            if (dictLike is IEnumerable enumerable)
            {
                var i = 0;
                foreach (var item in enumerable)
                {
                    // item is KeyValuePair<ulong, T> (internal T)
                    var it = item.GetType();
                    var keyProp = it.GetProperty("Key");
                    var valProp = it.GetProperty("Value");

                    var key = keyProp?.GetValue(item);
                    var val = valProp?.GetValue(item);

                    sb.AppendLine($"   • [{i}] Key={key} ValueType={val?.GetType().FullName ?? "null"}");
                    i++;
                    if (i >= 5) break;
                }

                if (i == 0)
                    sb.AppendLine("   • (empty)");
            }
        }
        catch
        {
            sb.AppendLine("   • (enumeration failed)");
        }
    }

    private static List<(string Path, object Value)> FindNestedUlongKeyedDictionaries(object container)
    {
        var results = new List<(string Path, object Value)>();

        var fields = GetAllInstanceFields(container.GetType());
        foreach (var f in fields)
        {
            object? v;
            try { v = f.GetValue(container); }
            catch { continue; }

            if (v is null)
                continue;

            if (LooksLikeUlongKeyedDictionary(v.GetType()))
            {
                results.Add(($"{container.GetType().Name}.{f.Name}", v));
            }
        }

        return results;
    }
}
