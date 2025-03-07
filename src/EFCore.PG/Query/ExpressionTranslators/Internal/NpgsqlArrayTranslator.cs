using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Query.Expressions.Internal;
using Npgsql.EntityFrameworkCore.PostgreSQL.Storage.Internal.Mapping;
using static Npgsql.EntityFrameworkCore.PostgreSQL.Utilities.Statics;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal
{
    /// <summary>
    /// Translates method and property calls on arrays/lists into their corresponding PostgreSQL operations.
    /// </summary>
    /// <remarks>
    /// https://www.postgresql.org/docs/current/static/functions-array.html
    /// </remarks>
    public class NpgsqlArrayTranslator : IMethodCallTranslator, IMemberTranslator
    {
        private static readonly MethodInfo SequenceEqual =
            typeof(Enumerable).GetTypeInfo().GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == nameof(Enumerable.SequenceEqual) && m.GetParameters().Length == 2);

        private static readonly MethodInfo EnumerableContains =
            typeof(Enumerable).GetTypeInfo().GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

        private static readonly MethodInfo EnumerableAnyWithoutPredicate =
            typeof(Enumerable).GetTypeInfo().GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Single(mi => mi.Name == nameof(Enumerable.Any) && mi.GetParameters().Length == 1);

        private readonly IRelationalTypeMappingSource _typeMappingSource;
        private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;
        private readonly NpgsqlJsonPocoTranslator _jsonPocoTranslator;

        public NpgsqlArrayTranslator(
            IRelationalTypeMappingSource typeMappingSource,
            NpgsqlSqlExpressionFactory sqlExpressionFactory,
            NpgsqlJsonPocoTranslator jsonPocoTranslator)
        {
            _typeMappingSource = typeMappingSource;
            _sqlExpressionFactory = sqlExpressionFactory;
            _jsonPocoTranslator = jsonPocoTranslator;
        }

        public virtual SqlExpression? Translate(
            SqlExpression? instance,
            MethodInfo method,
            IReadOnlyList<SqlExpression> arguments,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (instance?.Type.IsGenericList() == true && !IsMappedToNonArray(instance))
            {
                // Translate list[i]. Note that array[i] is translated by NpgsqlSqlTranslatingExpressionVisitor.VisitBinary (ArrayIndex)
                if (method.Name == "get_Item" && arguments.Count == 1)
                {
                    return
                        // Try translating indexing inside json column
                        _jsonPocoTranslator.TranslateMemberAccess(instance, arguments[0], method.ReturnType) ??
                        // Other types should be subscriptable - but PostgreSQL arrays are 1-based, so adjust the index.
                        _sqlExpressionFactory.ArrayIndex(instance, GenerateOneBasedIndexExpression(arguments[0]));
                }

                return TranslateCommon(instance, arguments);
            }

            if (instance is null && arguments.Count > 0 && arguments[0].Type.IsArrayOrGenericList() && !IsMappedToNonArray(arguments[0]))
            {
                // Extension method over an array or list
                if (method.IsClosedFormOf(SequenceEqual) && arguments[1].Type.IsArray)
                    return _sqlExpressionFactory.Equal(arguments[0], arguments[1]);

                return TranslateCommon(arguments[0], arguments.Slice(1));
            }

            // Not an array/list
            return null;

            // The array/list CLR type may be mapped to a non-array database type (e.g. byte[] to bytea, or just
            // value converters) - we don't want to translate for those cases.
            static bool IsMappedToNonArray(SqlExpression arrayOrList)
                => arrayOrList.TypeMapping is RelationalTypeMapping typeMapping &&
                   typeMapping is not (NpgsqlArrayTypeMapping or NpgsqlJsonTypeMapping);

            SqlExpression? TranslateCommon(SqlExpression arrayOrList, IReadOnlyList<SqlExpression> arguments)
            {
                // Predicate-less Any - translate to a simple length check.
                if (method.IsClosedFormOf(EnumerableAnyWithoutPredicate))
                {
                    return _sqlExpressionFactory.GreaterThan(
                        _jsonPocoTranslator.TranslateArrayLength(arrayOrList) ??
                        _sqlExpressionFactory.Function(
                            "cardinality",
                            new[] { arrayOrList },
                            nullable: true,
                            argumentsPropagateNullability: TrueArrays[1],
                            typeof(int)),
                        _sqlExpressionFactory.Constant(0));
                }

                // Note that .Where(e => new[] { "a", "b", "c" }.Any(p => e.SomeText == p)))
                // is pattern-matched in AllAnyToContainsRewritingExpressionVisitor, which transforms it to
                // new[] { "a", "b", "c" }.Contains(e.Some Text).

                if ((method.IsClosedFormOf(EnumerableContains) || // Enumerable.Contains extension method
                     method.Name == nameof(List<int>.Contains) && method.DeclaringType.IsGenericList() &&
                     method.GetParameters().Length == 1)
                    &&
                    (
                        // Handle either array columns (with an array mapping) or parameters/constants (no mapping). We specifically
                        // don't want to translate if the type mapping is bytea (CLR type is array, but not an array in
                        // the database).
                        // arrayOrList.TypeMapping == null && _typeMappingSource.FindMapping(arrayOrList.Type) != null ||
                        arrayOrList.TypeMapping is NpgsqlArrayTypeMapping or null
                    ))
                {
                    var item = arguments[0];

                    switch (arrayOrList)
                    {
                    // When the array is a column, we translate to array @> ARRAY[item]. GIN indexes
                    // on array are used, but null semantics is impossible without preventing index use.
                    case ColumnExpression:
                        if (item is SqlConstantExpression constant && constant.Value is null)
                        {
                            // We special-case null constant item and use array_position instead, since it does
                            // nulls correctly (but doesn't use indexes)
                            // TODO: once lambda-based caching is implemented, move this to NpgsqlSqlNullabilityProcessor
                            // (https://github.com/dotnet/efcore/issues/17598) and do for parameters as well.
                            return _sqlExpressionFactory.IsNotNull(
                                _sqlExpressionFactory.Function(
                                    "array_position",
                                    new[] { arrayOrList, item },
                                    nullable: true,
                                    argumentsPropagateNullability: FalseArrays[2],
                                    typeof(int)));
                        }

                        return _sqlExpressionFactory.Contains(arrayOrList,
                            _sqlExpressionFactory.NewArrayOrConstant(new[] { item }, arrayOrList.Type));

                    // Don't do anything PG-specific for constant arrays since the general EF Core mechanism is fine
                    // for that case: item IN (1, 2, 3).
                    // After https://github.com/aspnet/EntityFrameworkCore/issues/16375 is done we may not need the
                    // check any more.
                    case SqlConstantExpression:
                        return null;

                    // For ParameterExpression, and for all other cases - e.g. array returned from some function -
                    // translate to e.SomeText = ANY (@p). This is superior to the general solution which will expand
                    // parameters to constants, since non-PG SQL does not support arrays.
                    // Note that this will allow indexes on the item to be used.
                    default:
                        return _sqlExpressionFactory.Any(item, arrayOrList, PostgresAnyOperatorType.Equal);
                    }
                }

                // Note: we also translate .Where(e => new[] { "a", "b", "c" }.Any(p => EF.Functions.Like(e.SomeText, p)))
                // to LIKE ANY (...). See NpgsqlSqlTranslatingExpressionVisitor.VisitArrayMethodCall.

                return null;
            }
        }

        public virtual SqlExpression? Translate(SqlExpression? instance,
            MemberInfo member,
            Type returnType,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            if (instance?.Type.IsGenericList() == true &&
                member.Name == nameof(List<object>.Count) &&
                (instance.TypeMapping is NpgsqlArrayTypeMapping || instance.TypeMapping is null))
            {
                return _jsonPocoTranslator.TranslateArrayLength(instance) ??
                       _sqlExpressionFactory.Function(
                           "cardinality",
                           new[] { instance },
                           nullable: true,
                           argumentsPropagateNullability: TrueArrays[1],
                           typeof(int));
            }

            return null;
        }

        /// <summary>
        /// PostgreSQL array indexing is 1-based. If the index happens to be a constant,
        /// just increment it. Otherwise, append a +1 in the SQL.
        /// </summary>
        private SqlExpression GenerateOneBasedIndexExpression(SqlExpression expression)
            => expression is SqlConstantExpression constant
                ? _sqlExpressionFactory.Constant(Convert.ToInt32(constant.Value) + 1, constant.TypeMapping)
                : (SqlExpression)_sqlExpressionFactory.Add(expression, _sqlExpressionFactory.Constant(1));
    }
}
