﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Entity.Infrastructure.Interception;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EntityFramework.SqlServer.FullTextSearch
{
    public class DbInterceptor : IDbCommandInterceptor
    {
        private static readonly IList<DbType> StringDbTypes = new List<DbType> { DbType.String, DbType.AnsiString, DbType.StringFixedLength, DbType.AnsiStringFixedLength };
        private static DbInterceptor Interceptor = null;

        public static void Register()
        {
            if (Interceptor == null)
                DbInterception.Add(Interceptor = new DbInterceptor());
        }

        public void NonQueryExecuted(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
        }

        public void NonQueryExecuting(DbCommand command, DbCommandInterceptionContext<int> interceptionContext)
        {
        }

        public void ReaderExecuted(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
        {
        }

        public void ReaderExecuting(DbCommand command, DbCommandInterceptionContext<DbDataReader> interceptionContext)
        {
            if (command == null)
                throw new ArgumentNullException("command");

            //The SQL generated, and thus what we look for, is provider specific I think.
            //So we want to limit this to the provider it was produced for.
            if (command.GetType().Namespace.Equals("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase))
                RewriteFullTextQuery(command);
        }

        public void ScalarExecuted(DbCommand command, DbCommandInterceptionContext<object> interceptionContext)
        {
        }

        public void ScalarExecuting(DbCommand command, DbCommandInterceptionContext<object> interceptionContext)
        {
            if (command == null)
                throw new ArgumentNullException("command");

            //The SQL generated, and thus what we look for, is provider specific I think.
            //So we want to limit this to the provider it was produced for.
            if (command.GetType().Namespace.Equals("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase))
                RewriteFullTextQuery(command);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The values going into the modified form of the SQL are either what was already generated by the provider or static strings, not user input, and shouldn't be vulnerable to SQLi.")]
        private static void RewriteFullTextQuery(DbCommand command)
        {
            for (var i = 0; i < command.Parameters.Count; i++)
            {
                //Can a parameter object be shared?
                //Currently the way I can see finding shared columns for a full text operation is to pass in
                //and id on the value, similar to (or part of) the tags to identify that more than 1 parameter
                //originated from the same call and it's value.
                //We can then presumably replace the multiple instances with a single parameter.
                //Also, we can do our column combining, though the regex will be messy.

                var parameter = command.Parameters[i];
                if (StringDbTypes.Contains(parameter.DbType) == false ||
                    parameter.Value == DBNull.Value)
                    continue;

                var value = (string)parameter.Value;
                var match = FullTextTags.AnyTag.Match(value);//Better to do index check first for speed?
                if (match.Success)
                {
                    parameter.Size = 4096;
                    parameter.DbType = DbType.AnsiStringFixedLength;
                    //TODO: Better replacement?
                    value = FullTextTags.AnyTag.Replace(value, String.Empty, 1);//Replace the first occurance only, if the terms occur in the search string we want to keep them.
                    value = value.Substring(1, value.Length - 2); // remove %% escaping by linq translator from string.Contains to sql LIKE
                    parameter.Value = value;

                    var operation = match.Value == FullTextTags.ContainsTag ? "CONTAINS" :
                                    match.Value == FullTextTags.FreeTextTag ? "FREETEXT" :
                                    null;//This _should_ never happen.
                    if (operation == null)
                        throw new InvalidOperationException(Resource.MalformedSqlParameter);

                    var wildcard = new Regex(String.Format(CultureInfo.InvariantCulture, @"N?'\*'\s*LIKE\s*@{0}\s?(?:ESCAPE N?'~')", parameter.ParameterName));
                    var singleColumn = new Regex(String.Format(CultureInfo.InvariantCulture, @"\[(\w*)\].\[(\w*)\]\s*LIKE\s*@{0}\s?(?:ESCAPE N?'~')", parameter.ParameterName));
                    //var multiColumn = new Regex(@"");

                    var text = command.CommandText;
                    if (wildcard.IsMatch(text))
                        command.CommandText = wildcard.Replace(text, String.Format(CultureInfo.InvariantCulture, @"{0}(*, @{1})", operation, parameter.ParameterName));
                    else if (singleColumn.IsMatch(text))
                        command.CommandText = singleColumn.Replace(text, String.Format(CultureInfo.InvariantCulture, @"{0}([$1].[$2], @{1})", operation, parameter.ParameterName));
                    //else if (multiColumn.IsMatch(text))
                    //    cmd.CommandText = multiColumn.Replace(text, String.Format(CultureInfo.InvariantCulture, @"{0}(, @{1})", operation, parameter.ParameterName));

                    if (text == command.CommandText)
                        throw new InvalidOperationException(Resource.MalformedSql);
                    text = command.CommandText;
                }
            }
        }
    }
}