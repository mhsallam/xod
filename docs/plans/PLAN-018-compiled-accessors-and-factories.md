# PLAN-018: Compiled Expression Property Accessors & Constructor Delegates

- **Task ID**: `TASK-023`
- **Fixes Issue**: [`ISS-023`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
At 250 MB scale (`100,000+` rows with 6–10 properties per entity), `XmlEngine.Write`, `XmlEngine.Read`, and `IndexService.IndexItem` invoke `PropertyInfo.GetValue(item)` and `PropertyInfo.SetValue(item, value)` via reflection millions of times, and `ValueHelper.CreateInstance(type)` calls `Activator.CreateInstance(type)` on every deserialized row. Reflection invocation and boxing/argument array allocation add substantial CPU overhead during large bulk loads and queries.

## Design & Refactoring Steps
1. Enhance `PropertyInfoItem` (`src/Xod/Models.cs`) with pre-compiled fast delegates:
   - `public Func<object, object> Getter { get; set; }`
   - `public Action<object, object> Setter { get; set; }`
   - Add helper methods `GetValue(object instance)` and `SetValue(object instance, object value)` on `PropertyInfoItem` that invoke the compiled `Expression` delegates (falling back to `Property.GetValue`/`SetValue` only if needed).
2. In `PropertyService.LoadType(Type type)`, compile `Getter` and `Setter` expression trees once when the type is registered.
3. In `ValueHelper.CreateInstance(Type type)`, cache compiled `Expression.New(type)` factory delegates (`ConcurrentDictionary<Type, Func<object>>`).

## Verification Plan
- Verify all 28 existing unit and stress tests pass and measure bulk insert & query throughput gains on the 250 MB benchmark.
