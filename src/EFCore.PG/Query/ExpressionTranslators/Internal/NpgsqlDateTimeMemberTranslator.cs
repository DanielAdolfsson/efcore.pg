﻿using System;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using NpgsqlTypes;
using static Npgsql.EntityFrameworkCore.PostgreSQL.Utilities.Statics;

namespace Npgsql.EntityFrameworkCore.PostgreSQL.Query.ExpressionTranslators.Internal
{
    /// <summary>
    /// Provides translation services for <see cref="DateTime"/> members.
    /// </summary>
    /// <remarks>
    /// See: https://www.postgresql.org/docs/current/static/functions-datetime.html
    /// </remarks>
    public class NpgsqlDateTimeMemberTranslator : IMemberTranslator
    {
        private readonly NpgsqlSqlExpressionFactory _sqlExpressionFactory;

        public NpgsqlDateTimeMemberTranslator(NpgsqlSqlExpressionFactory sqlExpressionFactory)
            => _sqlExpressionFactory = sqlExpressionFactory;

        /// <inheritdoc />
        public virtual SqlExpression? Translate(
            SqlExpression? instance,
            MemberInfo member,
            Type returnType,
            IDiagnosticsLogger<DbLoggerCategory.Query> logger)
        {
            var type = member.DeclaringType;
            if (type != typeof(DateTime) && type != typeof(NpgsqlDateTime) && type != typeof(NpgsqlDate))
                return null;

            return member.Name switch
            {
                nameof(DateTime.Now)       => Now(),
                nameof(DateTime.UtcNow)    =>
                    _sqlExpressionFactory.AtTimeZone(Now(), _sqlExpressionFactory.Constant("UTC"), returnType),

                nameof(DateTime.Today)     => _sqlExpressionFactory.Function(
                    "date_trunc",
                    new SqlExpression[] { _sqlExpressionFactory.Constant("day"), Now() },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[2],
                    returnType),

                nameof(DateTime.Year)      => GetDatePartExpression(instance!, "year"),
                nameof(DateTime.Month)     => GetDatePartExpression(instance!, "month"),
                nameof(DateTime.DayOfYear) => GetDatePartExpression(instance!, "doy"),
                nameof(DateTime.Day)       => GetDatePartExpression(instance!, "day"),
                nameof(DateTime.Hour)      => GetDatePartExpression(instance!, "hour"),
                nameof(DateTime.Minute)    => GetDatePartExpression(instance!, "minute"),
                nameof(DateTime.Second)    => GetDatePartExpression(instance!, "second"),

                nameof(DateTime.Millisecond) => null, // Too annoying

                // .NET's DayOfWeek is an enum, but its int values happen to correspond to PostgreSQL
                nameof(DateTime.DayOfWeek) => GetDatePartExpression(instance!, "dow", floor: true),

                nameof(DateTime.Date) => _sqlExpressionFactory.Function(
                    "date_trunc",
                    new[] { _sqlExpressionFactory.Constant("day"), instance! },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[2],
                    returnType),

                // TODO: Technically possible simply via casting to PG time, should be better in EF Core 3.0
                // but ExplicitCastExpression only allows casting to PG types that
                // are default-mapped from CLR types (timespan maps to interval,
                // which timestamp cannot be cast into)
                nameof(DateTime.TimeOfDay) => null,

                // TODO: Should be possible
                nameof(DateTime.Ticks) => null,

                _ => null
            };

            SqlFunctionExpression Now()
                => _sqlExpressionFactory.Function(
                    "now",
                    Array.Empty<SqlExpression>(),
                    nullable: false,
                    argumentsPropagateNullability: TrueArrays[0],
                    returnType);
        }

        /// <summary>
        /// Constructs the DATE_PART expression.
        /// </summary>
        /// <param name="instance">The member expression.</param>
        /// <param name="partName">The name of the DATE_PART to construct.</param>
        /// <param name="floor">True if the result should be wrapped with FLOOR(...); otherwise, false.</param>
        /// <returns>
        /// The DATE_PART expression.
        /// </returns>
        /// <remarks>
        /// DATE_PART returns doubles, which we floor and cast into ints
        /// This also gets rid of sub-second components when retrieving seconds.
        /// </remarks>
        private SqlExpression GetDatePartExpression(
            SqlExpression instance,
            string partName,
            bool floor = false)
        {
            var result = _sqlExpressionFactory.Function(
                "date_part",
                new[]
                {
                    _sqlExpressionFactory.Constant(partName),
                    instance
                },
                nullable: true,
                argumentsPropagateNullability: TrueArrays[2],
                typeof(double));

            if (floor)
                result = _sqlExpressionFactory.Function(
                    "floor",
                    new[] { result },
                    nullable: true,
                    argumentsPropagateNullability: TrueArrays[1],
                    typeof(double));

            return _sqlExpressionFactory.Convert(result, typeof(int));
        }
    }
}
