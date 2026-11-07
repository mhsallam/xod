# PLAN-003: Fix `[Required]` Validation on Value-Type Defaults & Persist Explicit Scalar Values

- **Task ID**: `TASK-008`
- **Fixes Issue**: [`ISS-008`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
1. In `XmlEngine.WriteProperty`:
   ```csharp
   if (prop.IsRequired && (value == null || value.Equals(ValueHelper.DefaultOf(prop.PropertyType))))
       exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
   ```
   If an entity has `[Required] public int Quantity { get; set; }` or `[Required] public bool IsVerified { get; set; }`, passing `0` or `false` throws `RequiredPropertyException`, even though `0` and `false` are valid value-type values!
2. In `XmlEngine.WriteProperty`:
   ```csharp
   if (value == null || value.Equals(ValueHelper.DefaultOf(effectivePropType)))
       return null;
   ```
   Scalar value types with `0` or `false` were omitted from the serialized `<Row>` element. This meant raw XML/JSON files omitted `Done="false"` or `<Score>0</Score>`, and updating a property with `UpdateFilter` or inspecting the file on disk was inconsistent.

## Design & Refactoring Steps
1. Update `[Required]` validation in `WriteProperty` so `RequiredPropertyException` is thrown when:
   - `value == null`
   - `value is string s && string.IsNullOrEmpty(s)`
   - `value is Guid g && g == Guid.Empty`
   - `value is DateTime dt && dt == default`
   - `value is DateOnly d && d == default`
   Non-nullable numeric (`0`) and boolean (`false`) values are valid values and must not fail `[Required]`.
2. In `WriteProperty`, when `shallowWrite == false` (normal entity persistence), always write non-null value-type properties (`bool`, numeric types, enums) even when their value is `false` or `0`, while omitting `null` / empty strings / uninitialized `DateTime`/`Guid.Empty`.
3. Keep `shallowWrite == true` (used by `SelectItemsByExample` / `GetRow`) skipping `DefaultOf(type)` properties so Query-By-Example (`db.Query(new ToDo { Id = 5 })`) continues to match only the non-default properties set on the example POCO.

## Verification Plan
- Add unit test `Required_Attribute_Allows_Zero_And_False_And_Persists_Explicit_Scalars` verifying `[Required]` on `int = 0` and `bool = false` succeeds, rejects `null`/`""` strings and `Guid.Empty`, and persists `false`/`0` in the stored document.
