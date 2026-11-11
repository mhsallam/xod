using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Xod.Services
{
    internal partial class IndexService
    {
        internal bool TryPlanExpression<T>(Expression<Func<T, bool>> expression, out IReadOnlyCollection<string> candidateCodes, out bool isFullyCovered)
        {
            candidateCodes = null;
            isFullyCovered = false;
            if (expression?.Body == null) return false;

            Type entityType = typeof(T);
            if (expression.Body is BinaryExpression binary &&
                binary.NodeType == ExpressionType.Equal &&
                TryResolveComparison(entityType, binary, out var prop, out _, out string normTarget))
            {
                if (normTarget == null)
                {
                    candidateCodes = Array.Empty<string>(); // a value no row can equal
                    isFullyCovered = true;
                    return true;
                }
                string key = prop.CachedIndexKey ??= GetIndexKey(entityType, prop.PropertyName);
                lock (syncLock)
                {
                    EnsurePropertyHydratedIfInBoundsUnlocked(key, normTarget);
                    if (propertyIndexes.TryGetValue(key, out var tree) && tree.TryGetValue(normTarget, out var bucket))
                    {
                        if (bucket is string singleCode)
                            candidateCodes = new[] { singleCode };
                        else if (bucket is HashSet<string> codeSet)
                            candidateCodes = codeSet;
                        else
                            candidateCodes = Array.Empty<string>();
                    }
                    else
                    {
                        candidateCodes = Array.Empty<string>();
                    }
                }
                isFullyCovered = true;
                return true;
            }

            if (TryEvaluateNode(entityType, expression.Body, out var setCodes, out isFullyCovered))
            {
                candidateCodes = setCodes;
                return true;
            }
            return false;
        }

        private bool TryEvaluateNode(Type entityType, Expression node, out HashSet<string> codes, out bool isFullyCovered)
        {
            codes = null;
            isFullyCovered = false;
            if (node is BinaryExpression binary)
            {
                if (binary.NodeType == ExpressionType.AndAlso)
                {
                    bool leftIndexed = TryEvaluateNode(entityType, binary.Left, out var leftCodes, out bool leftFull);
                    bool rightIndexed = TryEvaluateNode(entityType, binary.Right, out var rightCodes, out bool rightFull);

                    if (leftIndexed && rightIndexed)
                    {
                        leftCodes.IntersectWith(rightCodes);
                        codes = leftCodes;
                        isFullyCovered = leftFull && rightFull;
                        return true;
                    }
                    if (leftIndexed)
                    {
                        codes = leftCodes;
                        isFullyCovered = false;
                        return true;
                    }
                    if (rightIndexed)
                    {
                        codes = rightCodes;
                        isFullyCovered = false;
                        return true;
                    }
                    return false;
                }

                if (binary.NodeType == ExpressionType.OrElse)
                {
                    if (TryEvaluateNode(entityType, binary.Left, out var leftCodes, out bool leftFull) &&
                        TryEvaluateNode(entityType, binary.Right, out var rightCodes, out bool rightFull))
                    {
                        leftCodes.UnionWith(rightCodes);
                        codes = leftCodes;
                        isFullyCovered = leftFull && rightFull;
                        return true;
                    }
                    return false;
                }

                if (TryResolveComparison(entityType, binary, out var prop, out var op, out string normTarget))
                {
                    codes = normTarget == null
                        ? new HashSet<string>(StringComparer.Ordinal) // no row can match
                        : QueryIndexRange(entityType, prop, op, normTarget);
                    isFullyCovered = true;
                    return true;
                }
            }

            return false;
        }

        private HashSet<string> QueryIndexRange(Type entityType, PropertyInfoItem prop, ExpressionType op, string normTarget)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            string key = GetIndexKey(entityType, prop.PropertyName);

            lock (syncLock)
            {
                if (op == ExpressionType.Equal)
                    EnsurePropertyHydratedIfInBoundsUnlocked(key, normTarget);
                else
                    EnsurePropertyHydratedUnlocked(key);

                if (!propertyIndexes.TryGetValue(key, out var tree))
                    return result; // Hydrated index is empty -> 0 matching records

                if (op == ExpressionType.Equal)
                {
                    if (tree.TryGetValue(normTarget, out var exact))
                        UnionBucketWithResult(result, exact);
                    return result;
                }

                // Keys of an ordered kind sort the way C# compares the values (IndexKeys).
                foreach (var kvp in tree)
                {
                    int c = string.CompareOrdinal(kvp.Key, normTarget);
                    bool match = op switch
                    {
                        ExpressionType.GreaterThan => c > 0,
                        ExpressionType.GreaterThanOrEqual => c >= 0,
                        ExpressionType.LessThan => c < 0,
                        _ => c <= 0, // LessThanOrEqual, the one op left (TryResolveComparison)
                    };
                    if (match)
                        UnionBucketWithResult(result, kvp.Value);
                }
            }

            return result;
        }

        private static void UnionBucketWithResult(HashSet<string> result, object bucket)
        {
            if (bucket is string singleCode)
                result.Add(singleCode);
            else if (bucket is HashSet<string> codeSet)
                result.UnionWith(codeSet);
        }

        /// <summary>
        /// Turns <c>member op constant</c> (either way round) on an indexed property into a key the index
        /// answers exactly as C# would. False when it can't: the caller then filters every row.
        /// <paramref name="normTarget"/> is null when no row can match: NaN, or a constant the property's
        /// type can't hold, compared for equality.
        ///
        /// C# compares in the type of the binary expression and converts the member into it
        /// (<c>(int)x.Tier == 2</c>, <c>(double)x.Ratio == 0.1</c>). The comparison moves onto the
        /// property's own type only when every such conversion is exact, so the two mean the same thing;
        /// the constant is then converted back exactly, or found to have no equal there.
        /// </summary>
        private bool TryResolveComparison(
            Type entityType,
            BinaryExpression binary,
            out PropertyInfoItem prop,
            out ExpressionType op,
            out string normTarget)
        {
            prop = null;
            normTarget = null;
            op = binary.NodeType;

            if (op != ExpressionType.Equal &&
                op != ExpressionType.GreaterThan &&
                op != ExpressionType.GreaterThanOrEqual &&
                op != ExpressionType.LessThan &&
                op != ExpressionType.LessThanOrEqual)
            {
                return false;
            }
            if (binary.Method != null && !IsKeyedOperator(binary))
                return false; // a user-defined operator: its meaning isn't the keys'

            Expression memberSide, constantSide;
            string propName;
            if (TryUnwrapExactConvert(binary.Left, out var member) && IsParameterMember(entityType, member, out propName))
            {
                memberSide = member;
                constantSide = binary.Right;
            }
            else if (TryUnwrapExactConvert(binary.Right, out member) && IsParameterMember(entityType, member, out propName))
            {
                memberSide = member;
                constantSide = binary.Left;
                op = FlipOperator(op);
            }
            else
            {
                return false;
            }

            var meta = propertyService.GetMetadata(entityType);
            if (meta != null)
                meta.ReadPropertyMap.TryGetValue(propName, out prop);
            else
                prop = propertyService.Properties(entityType.FullName).FirstOrDefault(p => p.PropertyName == propName);
            if (prop == null || (!prop.IsIndexed && !prop.IsPrimaryKey && !prop.IsUnique))
                return false;

            if (!TryEvaluateConstant(constantSide, out object value) || value == null)
                return false; // a comparison with null is answered by the rows

            Type propertyType = IndexKeys.Underlying(memberSide.Type);
            if (op != ExpressionType.Equal && !IndexKeys.IsOrdered(propertyType))
                return false;

            if ((value is double d && double.IsNaN(d)) || (value is float f && float.IsNaN(f)))
                return true; // NaN compares false with everything: normTarget stays null

            if (!IndexKeys.TryConvertExact(value, propertyType, out object onProperty))
                return op == ExpressionType.Equal; // the property holds no equal value; a range is left to the rows

            normTarget = NormalizeIndexValue(propertyType, onProperty);
            return true;
        }

        // decimal, DateTime, TimeSpan and the like compare through operator methods that mean what the
        // keys mean (IndexKeys); any other method is the type's own business.
        private static bool IsKeyedOperator(BinaryExpression binary)
        {
            Type declaring = binary.Method.DeclaringType;
            return declaring == typeof(decimal) || declaring == typeof(DateTime) || declaring == typeof(DateTimeOffset) ||
                   declaring == typeof(TimeSpan) || declaring == typeof(DateOnly) || declaring == typeof(TimeOnly) ||
                   declaring == typeof(string) || declaring == typeof(Guid);
        }

        /// <summary>Strips conversions that change no value (IndexKeys.IsExactWidening). False when one
        /// would: the expression is then not a plain comparison of the property.</summary>
        private static bool TryUnwrapExactConvert(Expression expr, out Expression inner)
        {
            while (expr is UnaryExpression unary &&
                   (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                if (unary.Method != null || !IndexKeys.IsExactWidening(unary.Operand.Type, unary.Type))
                {
                    inner = null;
                    return false;
                }
                expr = unary.Operand;
            }
            inner = expr;
            return true;
        }

        private static bool IsParameterMember(Type entityType, Expression expr, out string propName)
        {
            propName = null;
            if (expr is MemberExpression member &&
                member.Expression is ParameterExpression &&
                member.Member is PropertyInfo pi &&
                pi.DeclaringType != null &&
                pi.DeclaringType.IsAssignableFrom(entityType))
            {
                propName = pi.Name;
                return true;
            }
            return false;
        }

        /// <summary>The value of an expression that doesn't read the row, in the expression's own type.</summary>
        private static bool TryEvaluateConstant(Expression expr, out object value)
        {
            value = null;

            if (expr is ConstantExpression constant)
            {
                value = constant.Value;
                return true;
            }

            if (expr is UnaryExpression unary &&
                (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked) &&
                unary.Method == null &&
                TryEvaluateConstant(unary.Operand, out object operand))
            {
                if (operand == null)
                    return true; // null, converted to a nullable type
                if (IndexKeys.TryConvertExact(operand, unary.Type, out value))
                    return true;
                // A conversion that changes the value (rounding, truncation) is compiled below, so it is C#'s.
            }

            if (expr is MemberExpression memberExpr)
            {
                object target = null;
                if (memberExpr.Expression != null && !TryEvaluateConstant(memberExpr.Expression, out target))
                    return false;

                if (memberExpr.Member is FieldInfo fi)
                {
                    value = fi.GetValue(target);
                    return true;
                }
                if (memberExpr.Member is PropertyInfo pi)
                {
                    value = pi.GetValue(target);
                    return true;
                }
            }

            if (ParameterFinder.Reads(expr))
                return false;

            try
            {
                var lambda = Expression.Lambda(expr);
                value = lambda.Compile().DynamicInvoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class ParameterFinder : ExpressionVisitor
        {
            private bool found;

            internal static bool Reads(Expression expr)
            {
                var finder = new ParameterFinder();
                finder.Visit(expr);
                return finder.found;
            }

            protected override Expression VisitParameter(ParameterExpression node)
            {
                found = true;
                return node;
            }

        }

        private static ExpressionType FlipOperator(ExpressionType op) => op switch
        {
            ExpressionType.GreaterThan => ExpressionType.LessThan,
            ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
            ExpressionType.LessThan => ExpressionType.GreaterThan,
            ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
            _ => op
        };
    }
}
