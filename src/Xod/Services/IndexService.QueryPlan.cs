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
                TryExtractPropertyComparison(entityType, binary, out var prop, out _, out var constantValue) &&
                (prop.IsIndexed || prop.IsPrimaryKey || prop.IsUnique) &&
                constantValue != null)
            {
                string normTarget = NormalizeIndexValue(prop.PropertyType, constantValue);
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

                if (TryExtractPropertyComparison(entityType, binary, out var prop, out var op, out var constantValue))
                {
                    if (!prop.IsIndexed && !prop.IsPrimaryKey && !prop.IsUnique)
                        return false;

                    if (TryQueryIndexRange(entityType, prop, op, constantValue, out codes))
                    {
                        isFullyCovered = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TryQueryIndexRange(
            Type entityType,
            PropertyInfoItem prop,
            ExpressionType op,
            object value,
            out HashSet<string> result)
        {
            result = new HashSet<string>(StringComparer.Ordinal);
            if (value == null) return false;

            string normTarget = NormalizeIndexValue(prop.PropertyType, value);
            string key = GetIndexKey(entityType, prop.PropertyName);

            lock (syncLock)
            {
                if (op == ExpressionType.Equal)
                    EnsurePropertyHydratedIfInBoundsUnlocked(key, normTarget);
                else
                    EnsurePropertyHydratedUnlocked(key);

                if (!propertyIndexes.TryGetValue(key, out var tree))
                    return true; // Hydrated index is empty -> 0 matching records

                switch (op)
                {
                    case ExpressionType.Equal:
                        if (tree.TryGetValue(normTarget, out var exact))
                            UnionBucketWithResult(result, exact);
                        return true;

                    case ExpressionType.GreaterThan:
                        foreach (var kvp in tree)
                        {
                            if (string.CompareOrdinal(kvp.Key, normTarget) > 0)
                                UnionBucketWithResult(result, kvp.Value);
                        }
                        return true;

                    case ExpressionType.GreaterThanOrEqual:
                        foreach (var kvp in tree)
                        {
                            if (string.CompareOrdinal(kvp.Key, normTarget) >= 0)
                                UnionBucketWithResult(result, kvp.Value);
                        }
                        return true;

                    case ExpressionType.LessThan:
                        foreach (var kvp in tree)
                        {
                            if (string.CompareOrdinal(kvp.Key, normTarget) < 0)
                                UnionBucketWithResult(result, kvp.Value);
                        }
                        return true;

                    case ExpressionType.LessThanOrEqual:
                        foreach (var kvp in tree)
                        {
                            if (string.CompareOrdinal(kvp.Key, normTarget) <= 0)
                                UnionBucketWithResult(result, kvp.Value);
                        }
                        return true;
                }
            }

            return false;
        }

        private static void UnionBucketWithResult(HashSet<string> result, object bucket)
        {
            if (bucket is string singleCode)
                result.Add(singleCode);
            else if (bucket is HashSet<string> codeSet)
                result.UnionWith(codeSet);
        }

        private bool TryExtractPropertyComparison(
            Type entityType,
            BinaryExpression binary,
            out PropertyInfoItem prop,
            out ExpressionType op,
            out object value)
        {
            prop = null;
            op = binary.NodeType;
            value = null;

            if (op != ExpressionType.Equal &&
                op != ExpressionType.GreaterThan &&
                op != ExpressionType.GreaterThanOrEqual &&
                op != ExpressionType.LessThan &&
                op != ExpressionType.LessThanOrEqual)
            {
                return false;
            }

            Expression left = UnwrapConvert(binary.Left);
            Expression right = UnwrapConvert(binary.Right);

            if (IsParameterMember(entityType, left, out string propName) && TryEvaluateConstant(right, out value))
            {
                var meta = propertyService.GetMetadata(entityType);
                if (meta != null)
                    return meta.ReadPropertyMap.TryGetValue(propName, out prop);
                prop = propertyService.Properties(entityType.FullName).FirstOrDefault(p => p.PropertyName == propName);
                return prop != null;
            }

            if (IsParameterMember(entityType, right, out propName) && TryEvaluateConstant(left, out value))
            {
                op = FlipOperator(op);
                var meta = propertyService.GetMetadata(entityType);
                if (meta != null)
                    return meta.ReadPropertyMap.TryGetValue(propName, out prop);
                prop = propertyService.Properties(entityType.FullName).FirstOrDefault(p => p.PropertyName == propName);
                return prop != null;
            }

            return false;
        }

        private static Expression UnwrapConvert(Expression expr)
        {
            while (expr is UnaryExpression unary &&
                   (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
            {
                expr = unary.Operand;
            }
            return expr;
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

        private static bool TryEvaluateConstant(Expression expr, out object value)
        {
            value = null;
            expr = UnwrapConvert(expr);

            if (expr is ConstantExpression constant)
            {
                value = constant.Value;
                return true;
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
