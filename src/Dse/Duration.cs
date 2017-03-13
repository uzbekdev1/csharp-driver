﻿//
//  Copyright (C) 2017 DataStax, Inc.
//
//  Please see the license for details:
//  http://www.datastax.com/terms/datastax-dse-driver-license-terms
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Dse
{
    /// <summary>
    /// Represents a duration. A duration stores separately months, days, and seconds due to the fact that the
    /// number of days in a month varies, and a day can have 23 or 25 hours if a daylight saving is involved.
    /// </summary>
    /// <remarks>Duration instances are immutable and thread-safe.</remarks>
    public struct Duration : IEquatable<Duration>, IComparable<Duration>
    {
        private const long NanosPerMicro = 1000L;
        private const long NanosPerMilli = 1000L * NanosPerMicro;
        private const long NanosPerSecond = 1000L * NanosPerMilli;
        private const long NanosPerMinute = 60L * NanosPerSecond;
        private const long NanosPerHour = 60L * NanosPerMinute;
        private const int DaysPerWeek = 7;
        private const int MonthsPerYear = 12;
        private static readonly Regex StandardRegex = new Regex(
            @"(\d+)(y|mo|w|d|h|s|ms|us|µs|ns|m)", RegexOptions.Compiled);
        private static readonly Regex Iso8601Regex = new Regex(
            @"^P((\d+)Y)?((\d+)M)?((\d+)D)?(T((\d+)H)?((\d+)M)?((\d+)S)?)?$", RegexOptions.Compiled);
        private static readonly Regex Iso8601WeekRegex = new Regex(
            @"^P(\d+)W$", RegexOptions.Compiled);
        private static readonly Regex Iso8601AlternateRegex = new Regex(
            @"^P(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})$", RegexOptions.Compiled);

        public static readonly Duration Zero = new Duration();

        /// <summary>
        /// Gets the number of months.
        /// </summary>
        public int Months { get; private set; }

        /// <summary>
        /// Gets the number of days.
        /// </summary>
        public int Days { get; private set; }
        
        /// <summary>
        /// Gets the number of nanoseconds.
        /// </summary>
        public long Nanoseconds { get; private set; }

        /// <summary>
        /// Creates a new instance of <see cref="Duration"/>.
        /// </summary>
        /// <param name="months">The number of months.</param>
        /// <param name="days">The number of days.</param>
        /// <param name="nanoseconds">The number of nanoseconds.</param>
        public Duration(int months, int days, long nanoseconds) : this()
        {
            Months = months;
            Days = days;
            Nanoseconds = nanoseconds;
        }

        /// <summary>
        /// Returns true if the value of the <see cref="Duration"/> is the same.
        /// </summary>
        public bool Equals(Duration other)
        {
            return other.Months == Months && other.Days == Days && other.Nanoseconds == Nanoseconds;
        }

        /// <summary>
        /// Returns true if the value of the <see cref="Duration"/> is the same.
        /// </summary>
        public override bool Equals(object obj)
        {
            if (!(obj is Duration))
            {
                return false;
            }
            return Equals((Duration) obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = 17;
                hash = hash * 31 + Months.GetHashCode();
                hash = hash * 31 + Days.GetHashCode();
                hash = hash * 31 + Nanoseconds.GetHashCode();
                return hash;
            }
        }

        /// <summary>
        /// Compares this instance against another <see cref="Duration"/> instance based on the bytes representation
        /// of the values.
        /// </summary>
        public int CompareTo(Duration other)
        {
            var result = Months.CompareTo(other.Months);
            if (result != 0)
            {
                return result;
            }
            result = Days.CompareTo(other.Days);
            if (result != 0)
            {
                return result;
            }
            return Nanoseconds.CompareTo(other.Nanoseconds);
        }

        /// <summary>
        /// Returns the string representation of the value.
        /// </summary>
        public override string ToString()
        {
            if (Equals(Zero))
            {
                return "0";
            }
            var builder = new StringBuilder();
            if (Months < 0 || Days < 0 || Nanoseconds < 0)
            {
                builder.Append('-');
            }
            var remainder = Append(builder, Math.Abs(Months), MonthsPerYear, "y");
            Append(builder, remainder, 1, "mo");
            Append(builder, Math.Abs(Days), 1, "d");

            if (Nanoseconds != 0L)
            {
                var nanos = Math.Abs(Nanoseconds);
                remainder = Append(builder, nanos, NanosPerHour, "h");
                remainder = Append(builder, remainder, NanosPerMinute, "m");
                remainder = Append(builder, remainder, NanosPerSecond, "s");
                remainder = Append(builder, remainder, NanosPerMilli, "ms");
                remainder = Append(builder, remainder, NanosPerMicro, "us");
                Append(builder, remainder, 1L, "ns");
            }
            return builder.ToString();
        }

        private static long Append(StringBuilder builder, long dividend, long divisor, string unit)
        {
            if (dividend == 0L || dividend < divisor)
            {
                return dividend;
            }
            builder.Append(dividend/divisor).Append(unit);
            return dividend % divisor;
        }


        /// <summary>
        /// Compares value equality of 2 DateRange instances.
        /// </summary>
        public static bool operator ==(Duration a, Duration b)
        {
            return a.Equals(b);
        }

        /// <summary>
        /// Compares value inequality of 2 DateRange instances.
        /// </summary>
        public static bool operator !=(Duration a, Duration b)
        {
            return !(a == b);
        }

        /// <summary>
        /// Creates a new <see cref="Duration"/> instance from the string representation of the value.
        /// <para>Accepted formats:</para>
        /// <ul>
        ///   <li>multiple digits followed by a time unit like: 12h30m where the time unit can be:
        ///     <ul>
        ///       <li><c>y</c>: years</li>
        ///       <li><c>m</c>: months</li>
        ///       <li><c>w</c>: weeks</li>
        ///       <li><c>d</c>: days</li>
        ///       <li><c>h</c>: hours</li>
        ///       <li><c>m</c>: minutes</li>
        ///       <li><c>s</c>: seconds</li>
        ///       <li><c>ms</c>: milliseconds</li>
        ///       <li><c>us</c> or <c>µs</c>: microseconds</li>
        ///       <li><c>ns</c>: nanoseconds</li>
        ///     </ul>
        ///   </li>
        ///   <li>ISO 8601 format:  <code>P[n]Y[n]M[n]DT[n]H[n]M[n]S or P[n]W</code></li>
        ///   <li>ISO 8601 alternative format: <code>P[YYYY]-[MM]-[DD]T[hh]:[mm]:[ss]</code></li>
        /// </ul> 
        /// </summary>
        public static Duration Parse(string input)
        {
            if (String.IsNullOrEmpty(input))
            {
                throw new ArgumentNullException("input");
            }
            if (input == "0")
            {
                return new Duration();
            }
            var isNegative = input[0] == '-';
            var source = isNegative ? input.Substring(1) : input;
            if (source[0] == 'P')
            {
                if (source[source.Length - 1] == 'W')
                {
                    return ParseIso8601WeekFormat(isNegative, source);
                }
                if (source.Contains('-'))
                {
                    return ParseIso8601AlternativeFormat(isNegative, source);
                }
                return ParseIso8601Format(isNegative, source);
            }
            return ParseStandardFormat(isNegative, source, input);
        }

        private static Duration ParseStandardFormat(bool isNegative, string source, string input)
        {
            var builder = new Builder(isNegative);
            var matches = StandardRegex.Matches(source.ToLowerInvariant());
            foreach (Match match in matches)
            {
                builder.Add(match.Groups[1].Value, match.Groups[2].Value, input);
            }
            return builder.Build();
        }

        private static Duration ParseIso8601Format(bool isNegative, string source)
        {
            var match = Iso8601Regex.Match(source);
            if (!match.Success)
            {
                throw new FormatException(string.Format("Unable to convert '{0}' to a duration", source));
            }
            var builder = new Builder(isNegative);
            if (match.Groups[1].Success)
            {
                builder.AddYears(match.Groups[2].Value);
            }
            if (match.Groups[3].Success)
            {
                builder.AddMonths(match.Groups[4].Value);
            }
            if (match.Groups[5].Success)
            {
                builder.AddDays(match.Groups[6].Value);
            }
            if (match.Groups[7].Success)
            {
                if (match.Groups[8].Success)
                {
                    builder.AddHours(match.Groups[9].Value);
                }
                if (match.Groups[10].Success)
                {
                    builder.AddMinutes(match.Groups[11].Value);
                }
                if (match.Groups[12].Success)
                {
                    builder.AddSeconds(match.Groups[13].Value);
                }
            }
            return builder.Build();
        }

        private static Duration ParseIso8601AlternativeFormat(bool isNegative, string source)
        {
            var match = Iso8601AlternateRegex.Match(source);
            if (!match.Success)
            {
                throw new FormatException(string.Format("Unable to convert '{0}' to a duration", source));
            }
            return new Builder(isNegative).AddYears(match.Groups[1].Value)
                .AddMonths(match.Groups[2].Value)
                .AddDays(match.Groups[3].Value)
                .AddHours(match.Groups[4].Value)
                .AddMinutes(match.Groups[5].Value)
                .AddSeconds(match.Groups[6].Value)
                .Build();
        }

        private static Duration ParseIso8601WeekFormat(bool isNegative, string source)
        {
            var match = Iso8601WeekRegex.Match(source);
            if (!match.Success)
            {
                throw new FormatException(string.Format("Unable to convert '{0}' to a duration", source));
            }
            return new Builder(isNegative).AddWeeks(match.Groups[1].Value).Build();
        }

        private class Builder
        {
            private readonly bool _isNegative;
            private int _index;
            private int _months;
            private int _days;
            private long _nanoseconds;
            private readonly Dictionary<string, Func<string, Builder>> _addMethods;

            private static readonly string[] UnitNames =
            {
                 null, "years", "months", "weeks", "days",
                "hours", "minutes", "seconds", "milliseconds","microseconds", "nanoseconds"
            };
            
            public Builder(bool isNegative)
            {
                _isNegative = isNegative;
                _addMethods = new Dictionary<string, Func<string, Builder>>
                {
                    { "y", AddYears },
                    { "mo", AddMonths },
                    { "w", AddWeeks },
                    { "d", AddDays },
                    { "h", AddHours },
                    { "m", AddMinutes },
                    { "s", AddSeconds },
                    { "ms", AddMillis },
                    { "µs", AddMicros },
                    { "us", AddMicros },
                    { "ns", AddNanos }
                };
            }

            public void Add(string textValue, string symbol, string input)
            {
                Func<string, Builder> addMethod;
                if (!_addMethods.TryGetValue(symbol, out addMethod))
                {
                    throw new FormatException(string.Format("Unknown duration symbol {0}: {1}", symbol, input));
                }
                addMethod(textValue);
            }

            public Builder AddYears(string textValue)
            {
                return AddMonths(1, textValue, MonthsPerYear);
            }

            public Builder AddMonths(string textValue)
            {
                return AddMonths(2, textValue, 1);
            }

            public Builder AddWeeks(string textValue)
            {
                return AddDays(3, textValue, DaysPerWeek);
            }

            public Builder AddDays(string textValue)
            {
                return AddDays(4, textValue, 1);
            }

            public Builder AddHours(string textValue)
            {
                return AddNanos(5, textValue, NanosPerHour);
            }

            public Builder AddMinutes(string textValue)
            {
                return AddNanos(6, textValue, NanosPerMinute);
            }

            public Builder AddSeconds(string textValue)
            {
                return AddNanos(7, textValue, NanosPerSecond);
            }

            public Builder AddMillis(string textValue)
            {
                return AddNanos(8, textValue, NanosPerMilli);
            }

            public Builder AddMicros(string textValue)
            {
                return AddNanos(9, textValue, NanosPerMicro);
            }

            public Builder AddNanos(string textValue)
            {
                return AddNanos(10, textValue, 1L);
            }

            private Builder AddMonths(int order, string textValue, int monthsPerUnit)
            {
                ValidateOrder(order);
                var limit = (long.MaxValue - _months) / monthsPerUnit;
                var value = Convert.ToInt32(textValue);
                if (value > limit)
                {
                    throw new FormatException(
                        string.Format("Invalid duration. The total number of months must be less or equal to {0}",
                        int.MaxValue));
                }
                _months += monthsPerUnit * value;
                return this;
            }

            private Builder AddDays(int order, string textValue, int daysPerUnit)
            {
                ValidateOrder(order);
                var limit = (long.MaxValue - _days) / daysPerUnit;
                var value = Convert.ToInt32(textValue);
                if (value > limit)
                {
                    throw new FormatException(
                        string.Format("Invalid duration. The total number of days must be less or equal to {0}",
                        int.MaxValue));
                }
                _days += daysPerUnit * value;
                return this;
            }

            private Builder AddNanos(int order, string textValue, long nanosPerUnit)
            {
                ValidateOrder(order);
                var limit = (long.MaxValue - _nanoseconds) / nanosPerUnit;
                var value = Convert.ToInt64(textValue);
                if (value > limit)
                {
                    throw new FormatException(
                        string.Format("Invalid duration. The total number of nanoseconds must be less or equal to {0}",
                        long.MaxValue));
                }
                _nanoseconds += nanosPerUnit * value;
                return this;
            }

            private void ValidateOrder(int order)
            {
                if (order == _index)
                {
                    throw new FormatException(
                        string.Format("Invalid duration. The {0} are specified multiple times", GetUnitName(order)));
                }
                if (order < _index)
                {
                    throw new FormatException(
                        string.Format("Invalid duration. The {0} should be after {1}", 
                        GetUnitName(_index), GetUnitName(order)));
                }
                _index = order;
            }

            private string GetUnitName(int order)
            {
                return UnitNames[order];
            }

            public Duration Build()
            {
                return _isNegative ? 
                    new Duration(-_months, -_days, -_nanoseconds) : 
                    new Duration(_months, _days, _nanoseconds);
            }
        }
    }
}
