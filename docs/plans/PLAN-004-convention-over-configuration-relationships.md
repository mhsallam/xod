# PLAN-004: Convention-over-Configuration for 1-M and 1-Shared Relationships

- **Task ID**: `TASK-009`
- **Fixes Issue**: [`ISS-009`](../KNOWN-ISSUES.md)
- **Status**: Implemented

## Problem Statement
In XOD 1.x:
- If `Category` has `public List<Product> Products { get; set; }` and `Product` has `public int CategoryId { get; set; }` + `public Category Category { get; set; }`, XOD fails or treats them as unlinked unless the developer explicitly adds `[Children]` on `Category.Products` AND `[ParentKey(nameof(CategoryId))]` on `Product.Category`.
- Similarly, if `Book` has `public int AuthorId { get; set; }` and `public Author Author { get; set; }`, XOD requires `[ForeignKey(nameof(AuthorId))]` on `Book.Author` to link `AuthorId` automatically.

## Design & Refactoring Steps
1. In `PropertyService.LoadType`:
   - **Auto-detect 1-Shared `[ForeignKey]` by convention**: When a class reference property `prop` (e.g. `Author Author`) has no explicit `[ForeignKey]`, `[ParentKey]`, or `[Children]` attribute, check if the declaring class has a scalar property named `{prop.Name}Id` or `{prop.PropertyType.Name}Id` (e.g. `AuthorId`) whose type matches the target type's primary key (`Id`), AND the target type does not have a collection of the declaring type. If so, automatically synthesize a `ForeignKeyAttribute(localProp, "Id")` and set `ReferenceType = PropertyReferenceType.Foreign`.
   - **Auto-detect 1-M `[Children]` and `[ParentKey]` by convention**:
     - When a collection property `List<TChild>` / `TChild[]` on `TParent` has no `[Children]` attribute, check if `TChild` has either:
       - A property of type `TParent` (with or without `[ParentKey]`), OR
       - A scalar property named `{TParent.Name}Id` (e.g. `DepartmentId` or `CategoryId`).
       If so, automatically treat the collection as `PropertyReferenceType.Children` with `CascadeOptions.Delete`.
     - On `TChild`, if it has a reference property of type `TParent` (and `TParent` has a collection of `TChild`), automatically configure `PropertyReferenceType.Parent` with `ParentKeyAttribute("{TParent.Name}Id", "Id")`.
     - Even if `TChild` only has `public int DepartmentId { get; set; }` (and NO navigation property `public Department Department { get; set; }`!), update `XmlEngine.ParseChildren` so it populates `DepartmentId` on each child and persists the 1-M children without throwing `MissingParentKeyException`!

## Verification Plan
- Add unit test `Convention_Based_OneToMany_And_ForeignKey_Work_Without_Attributes` testing plain POCO classes (`Category` / `Product` / `Supplier`) that have zero XOD relationship attributes (`[Children]`, `[ParentKey]`, `[ForeignKey]`).
