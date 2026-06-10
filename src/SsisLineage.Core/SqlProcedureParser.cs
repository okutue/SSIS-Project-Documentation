using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SsisLineage.Core
{
    public class SqlProcedureParser
    {
        public static List<SqlLineageRecord> Parse(string sqlScript, string defaultDatabase, string defaultServer,
            IDictionary<string, string>? variableValues = null)
        {
            var records = new List<SqlLineageRecord>();
            if (string.IsNullOrWhiteSpace(sqlScript))
                return records;

            var parser = new TSql150Parser(false);
            using var reader = new StringReader(sqlScript);
            var fragment = parser.Parse(reader, out var errors);

            if (errors != null && errors.Count > 0)
            {
                Console.WriteLine($"[Warning] SQL Parse failed for script. Error count: {errors.Count}. First error: {errors[0].Message}");
                return records;
            }

            var generator = new Sql150ScriptGenerator();
            // Shared map of @varName → SQL text — populated by SET statements and consumed
            // by EXEC(@var) / EXEC sp_executesql @var calls anywhere in the same scope tree.
            var dynamicSqlMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Seed caller-supplied parameter/variable values (e.g. @Server, @Database) so
            // dynamic SQL composes real names instead of placeholders. Used to make
            // OPENQUERY linked-server and remote table names resolvable; empty = offline.
            if (variableValues != null)
            {
                foreach (var kv in variableValues)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var key = kv.Key.StartsWith("@", StringComparison.Ordinal) ? kv.Key : "@" + kv.Key;
                    dynamicSqlMap[key] = kv.Value ?? "";
                }
            }

            if (fragment is TSqlScript script)
            {
                foreach (var batch in script.Batches)
                {
                    foreach (TSqlStatement stmt in batch.Statements)
                    {
                        if (stmt is CreateProcedureStatement createProc)
                        {
                            ProcessStatements(createProc.StatementList.Statements,
                                createProc.ProcedureReference.Name.BaseIdentifier.Value,
                                defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                        }
                        else if (stmt is AlterProcedureStatement alterProc)
                        {
                            ProcessStatements(alterProc.StatementList.Statements,
                                alterProc.ProcedureReference.Name.BaseIdentifier.Value,
                                defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                        }
                        else
                        {
                            ProcessStatements(new TSqlStatement[] { stmt },
                                "AdHoc", defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                        }
                    }
                }
            }

            return records;
        }

        /// <summary>
        /// Replaces OLE DB positional parameter markers (?) with named variables (@P0, @P1, …)
        /// so ScriptDom can parse Execute SQL Task statements. Markers inside string literals
        /// and comments are left untouched.
        /// </summary>
        public static string ReplacePositionalParameters(string sql)
        {
            if (string.IsNullOrEmpty(sql) || !sql.Contains('?')) return sql;

            var sb = new System.Text.StringBuilder(sql.Length + 16);
            var inString = false;
            var inLineComment = false;
            var inBlockComment = false;
            var paramIndex = 0;

            for (var i = 0; i < sql.Length; i++)
            {
                var c = sql[i];
                if (inLineComment)
                {
                    if (c == '\n') inLineComment = false;
                }
                else if (inBlockComment)
                {
                    if (c == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { inBlockComment = false; sb.Append(c); c = sql[++i]; }
                }
                else if (inString)
                {
                    if (c == '\'') inString = false;
                }
                else if (c == '\'')
                {
                    inString = true;
                }
                else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                {
                    inLineComment = true;
                }
                else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                {
                    inBlockComment = true;
                }
                else if (c == '?')
                {
                    sb.Append("@P").Append(paramIndex++);
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ── Statement dispatcher ────────────────────────────────────────────────

        private static void ProcessStatements(
            IEnumerable<TSqlStatement> stmts,
            string procName,
            string defaultDatabase,
            string defaultServer,
            List<SqlLineageRecord> records,
            Sql150ScriptGenerator generator,
            Dictionary<string, string> dynamicSqlMap)
        {
            // Materialise so we can iterate twice (pre-pass + main pass).
            var stmtList = stmts is IList<TSqlStatement> l ? l : stmts.ToList();

            // ── Pre-pass: harvest  SET @var = 'literal sql'  and  DECLARE @var = '…'
            // before processing EXEC. Variables referenced inside another variable's
            // expression are inlined from the map (e.g. @OpenQuery embedding @SQL), so the
            // composed text mirrors what EXEC(@var) actually runs.
            foreach (var stmt in stmtList)
            {
                if (stmt is SetVariableStatement setVar)
                {
                    var sqlText = GetFullSqlFromExpression(setVar.Expression, dynamicSqlMap);
                    if (!string.IsNullOrEmpty(sqlText))
                        dynamicSqlMap[setVar.Variable.Name] = sqlText;
                }
                else if (stmt is DeclareVariableStatement declare)
                {
                    foreach (var decl in declare.Declarations)
                    {
                        if (decl.Value == null) continue;
                        var sqlText = GetFullSqlFromExpression(decl.Value, dynamicSqlMap);
                        if (!string.IsNullOrEmpty(sqlText))
                            dynamicSqlMap[decl.VariableName.Value] = sqlText;
                    }
                }
            }

            // ── Main pass ─────────────────────────────────────────────────────────
            foreach (var stmt in stmtList)
            {
                if (stmt is BeginEndBlockStatement block)
                {
                    ProcessStatements(block.StatementList.Statements, procName,
                        defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                }
                else if (stmt is IfStatement ifStmt)
                {
                    if (ifStmt.ThenStatement is BeginEndBlockStatement thenBlock)
                        ProcessStatements(thenBlock.StatementList.Statements, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                    else if (ifStmt.ThenStatement != null)
                        ProcessStatements(new[] { ifStmt.ThenStatement }, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);

                    if (ifStmt.ElseStatement is BeginEndBlockStatement elseBlock)
                        ProcessStatements(elseBlock.StatementList.Statements, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                    else if (ifStmt.ElseStatement != null)
                        ProcessStatements(new[] { ifStmt.ElseStatement }, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                }
                else if (stmt is InsertStatement insertStmt)
                {
                    ProcessCtes(insertStmt.WithCtesAndXmlNamespaces, procName, defaultDatabase, defaultServer, records, generator);

                    var spec = insertStmt.InsertSpecification;
                    if (spec.Target is NamedTableReference namedTarget)
                    {
                        var targetTable  = namedTarget.SchemaObject.BaseIdentifier.Value;
                        var targetSchema = namedTarget.SchemaObject.SchemaIdentifier?.Value ?? "dbo";
                        var targetDb     = namedTarget.SchemaObject.DatabaseIdentifier?.Value ?? defaultDatabase;

                        var targetColumns = new List<string>();
                        foreach (var col in spec.Columns)
                            targetColumns.Add(col.MultiPartIdentifier.Identifiers[^1].Value);

                        if (spec.InsertSource is SelectInsertSource selectSource)
                        {
                            ProcessSelect(selectSource.Select, targetColumns, "INSERT",
                                targetTable, targetSchema, targetDb, procName, defaultServer, records, generator);
                        }
                        else if (spec.InsertSource is ExecuteInsertSource execSource)
                        {
                            // INSERT INTO tgt EXEC schema.proc — record proc → table flow so the
                            // chain stays connected (columns unknown without resolving the proc's
                            // result set, hence the */* placeholder).
                            var procRef2 = execSource.Execute?.ExecutableEntity as ExecutableProcedureReference;
                            var srcProc = "";
                            if (procRef2?.ProcedureReference != null)
                                generator.GenerateScript(procRef2.ProcedureReference, out srcProc);

                            var srcSchema2 = "dbo";
                            var srcName2 = srcProc;
                            if (srcProc.Contains('.'))
                            {
                                var seg = srcProc.Split('.');
                                srcSchema2 = seg[^2].Trim('[', ']');
                                srcName2 = seg[^1].Trim('[', ']');
                            }

                            records.Add(new SqlLineageRecord
                            {
                                ProcedureName    = procName,
                                OperationType    = "INSERT_EXEC",
                                SourceServer     = defaultServer,
                                SourceDatabase   = defaultDatabase,
                                SourceSchema     = srcSchema2,
                                SourceTable      = srcName2,
                                SourceColumnName = "*",
                                SourceExpression = $"INSERT … EXEC {srcProc}",
                                TargetServer     = defaultServer,
                                TargetDatabase   = targetDb,
                                TargetSchema     = targetSchema,
                                TargetTable      = targetTable,
                                TargetColumnName = "*"
                            });
                        }
                    }
                }
                else if (stmt is DeleteStatement deleteStmt)
                {
                    ProcessCtes(deleteStmt.WithCtesAndXmlNamespaces, procName, defaultDatabase, defaultServer, records, generator);

                    var spec = deleteStmt.DeleteSpecification;
                    if (spec.Target is NamedTableReference delTarget)
                    {
                        var targetTable  = delTarget.SchemaObject.BaseIdentifier.Value;
                        var targetSchema = delTarget.SchemaObject.SchemaIdentifier?.Value ?? "dbo";
                        var targetDb     = delTarget.SchemaObject.DatabaseIdentifier?.Value ?? defaultDatabase;

                        // Resolve DELETE-alias targets (DELETE d FROM dbo.T d JOIN …)
                        var delAliasVisitor = new TableAliasVisitor();
                        deleteStmt.Accept(delAliasVisitor);
                        var delInfoVisitor = new TableInfoVisitor();
                        deleteStmt.Accept(delInfoVisitor);
                        if (delAliasVisitor.Aliases.TryGetValue(targetTable, out var resolvedDelTarget))
                        {
                            targetTable = resolvedDelTarget;
                            if (delInfoVisitor.TableSchemas.TryGetValue(targetTable, out var rs))
                                targetSchema = rs;
                        }

                        var delJoinVisitor = new JoinVisitor();
                        deleteStmt.Accept(delJoinVisitor);
                        var delJoins = delJoinVisitor.JoinDetails.Count > 0
                            ? string.Join("; ", delJoinVisitor.JoinDetails) : "";

                        var delFilter = "";
                        if (spec.WhereClause?.SearchCondition != null)
                            generator.GenerateScript(spec.WhereClause.SearchCondition, out delFilter);

                        // One operation-level record: DELETE removes rows, it moves no columns,
                        // but the target/filter/join still matter for the lineage report.
                        records.Add(new SqlLineageRecord
                        {
                            ProcedureName    = procName,
                            OperationType    = "DELETE",
                            SourceServer     = defaultServer,
                            SourceDatabase   = targetDb,
                            SourceSchema     = targetSchema,
                            SourceTable      = targetTable,
                            TargetServer     = defaultServer,
                            TargetDatabase   = targetDb,
                            TargetSchema     = targetSchema,
                            TargetTable      = targetTable,
                            JoinDetails      = delJoins,
                            FilterConditions = delFilter
                        });
                    }
                }
                else if (stmt is SelectStatement selectStmt)
                {
                    ProcessCtes(selectStmt.WithCtesAndXmlNamespaces, procName, defaultDatabase, defaultServer, records, generator);
                    var qe = selectStmt.QueryExpression;
                    if (qe is QuerySpecification && selectStmt.Into != null)
                    {
                        var targetTable  = selectStmt.Into.BaseIdentifier.Value;
                        var targetSchema = selectStmt.Into.SchemaIdentifier?.Value ?? "dbo";
                        var targetDb     = selectStmt.Into.DatabaseIdentifier?.Value ?? defaultDatabase;

                        var aliasVisitor = new ColumnAliasVisitor();
                        qe.Accept(aliasVisitor);

                        ProcessSelect(qe, new List<string>(aliasVisitor.Columns), "SELECTINTO",
                            targetTable, targetSchema, targetDb, procName, defaultServer, records, generator);
                    }
                    else
                    {
                        ProcessSelect(qe, new List<string>(), "SELECT",
                            "", "", "", procName, defaultServer, records, generator);
                    }
                }
                else if (stmt is UpdateStatement updateStmt)
                {
                    ProcessCtes(updateStmt.WithCtesAndXmlNamespaces, procName, defaultDatabase, defaultServer, records, generator);
                    var spec = updateStmt.UpdateSpecification;
                    if (spec.Target is NamedTableReference namedTarget)
                    {
                        var targetTable  = namedTarget.SchemaObject.BaseIdentifier.Value;
                        var targetSchema = namedTarget.SchemaObject.SchemaIdentifier?.Value ?? "dbo";
                        var targetDb     = namedTarget.SchemaObject.DatabaseIdentifier?.Value ?? defaultDatabase;

                        var updAliasVisitor = new TableAliasVisitor();
                        updateStmt.Accept(updAliasVisitor);
                        var updInfoVisitor = new TableInfoVisitor();
                        updateStmt.Accept(updInfoVisitor);

                        if (updAliasVisitor.Aliases.TryGetValue(targetTable, out var resolvedTarget))
                        {
                            targetTable = resolvedTarget;
                            if (updInfoVisitor.TableSchemas.TryGetValue(targetTable, out var resolvedSchema))
                                targetSchema = resolvedSchema;
                        }

                        // JOIN details (FROM … JOIN …) and WHERE filter for this UPDATE
                        var updJoinVisitor = new JoinVisitor();
                        updateStmt.Accept(updJoinVisitor);
                        var updJoinDetails = updJoinVisitor.JoinDetails.Count > 0
                            ? string.Join("; ", updJoinVisitor.JoinDetails) : "";

                        var updFilterText = "";
                        if (spec.WhereClause?.SearchCondition != null)
                            generator.GenerateScript(spec.WhereClause.SearchCondition, out updFilterText);

                        foreach (var setClause in spec.SetClauses)
                        {
                            if (setClause is AssignmentSetClause assignment)
                            {
                                var targetCol = assignment.Column.MultiPartIdentifier.Identifiers[^1].Value;
                                generator.GenerateScript(assignment.NewValue, out var exprText);

                                var colVisitor = new ColumnReferenceVisitor();
                                assignment.NewValue.Accept(colVisitor);

                                foreach (var srcCol in colVisitor.Columns)
                                {
                                    var sourceColName = srcCol.Contains('.') ? srcCol.Split('.', 2)[1] : srcCol;
                                    var sourceAlias   = srcCol.Contains('.') ? srcCol.Split('.', 2)[0] : null;

                                    var sourceTable  = "";
                                    var sourceSchema = "dbo";
                                    var sourceDb     = defaultDatabase;
                                    var sourceServer = defaultServer;
                                    if (sourceAlias != null && updAliasVisitor.Aliases.TryGetValue(sourceAlias, out var aliasTable))
                                    {
                                        sourceTable = aliasTable;
                                        if (updInfoVisitor.TableSchemas.TryGetValue(aliasTable, out var s))
                                            sourceSchema = s;
                                        if (updInfoVisitor.TableParts.TryGetValue(aliasTable, out var parts))
                                        {
                                            sourceDb     = parts.Database ?? sourceDb;
                                            sourceServer = parts.Server ?? sourceServer;
                                        }
                                    }

                                    records.Add(new SqlLineageRecord
                                    {
                                        ProcedureName    = procName,
                                        OperationType    = "UPDATE",
                                        SourceServer     = sourceServer,
                                        SourceDatabase   = sourceDb,
                                        SourceSchema     = sourceSchema,
                                        SourceTable      = sourceTable,
                                        SourceColumnName = sourceColName,
                                        SourceExpression = exprText,
                                        TargetServer     = defaultServer,
                                        TargetDatabase   = targetDb,
                                        TargetSchema     = targetSchema,
                                        TargetTable      = targetTable,
                                        TargetColumnName = targetCol,
                                        JoinDetails      = updJoinDetails,
                                        FilterConditions = updFilterText
                                    });
                                }
                            }
                        }
                    }
                }
                else if (stmt is ExecuteStatement executeStmt)
                {
                    ProcessExecuteStatement(executeStmt, procName,
                        defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                }
                else if (stmt is MergeStatement mergeStmt)
                {
                    ProcessCtes(mergeStmt.WithCtesAndXmlNamespaces, procName, defaultDatabase, defaultServer, records, generator);
                    ProcessMerge(mergeStmt, procName, defaultDatabase, defaultServer, records, generator);
                }
                else if (stmt is TryCatchStatement tryCatch)
                {
                    if (tryCatch.TryStatements != null)
                        ProcessStatements(tryCatch.TryStatements.Statements, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                    // CATCH block is error-handling only — no data flow to trace
                }
                else if (stmt is WhileStatement whileStmt)
                {
                    if (whileStmt.Statement is BeginEndBlockStatement wb)
                        ProcessStatements(wb.StatementList.Statements, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                    else if (whileStmt.Statement != null)
                        ProcessStatements(new[] { whileStmt.Statement }, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                }
            }
        }

        // ── CTEs — process each CTE's query with the CTE name as its target ────
        // The outer query's FROM clause references the CTE by name, so lineage chains
        // base tables → CTE → final target without special-casing the outer query.

        private static void ProcessCtes(
            WithCtesAndXmlNamespaces? ctes,
            string procName,
            string defaultDatabase,
            string defaultServer,
            List<SqlLineageRecord> records,
            Sql150ScriptGenerator generator)
        {
            if (ctes == null) return;
            foreach (var cte in ctes.CommonTableExpressions)
            {
                var cteName = cte.ExpressionName?.Value ?? "";
                if (string.IsNullOrEmpty(cteName)) continue;

                var cteColumns = cte.Columns?.Select(c => c.Value).ToList() ?? new List<string>();
                ProcessSelect(cte.QueryExpression, cteColumns, "CTE",
                    cteName, "", defaultDatabase, procName, defaultServer, records, generator);
            }
        }

        // ── EXEC handler — regular proc call OR dynamic SQL ────────────────────

        private static void ProcessExecuteStatement(
            ExecuteStatement executeStmt,
            string procName,
            string defaultDatabase,
            string defaultServer,
            List<SqlLineageRecord> records,
            Sql150ScriptGenerator generator,
            Dictionary<string, string> dynamicSqlMap)
        {
            var executeSpec = executeStmt.ExecuteSpecification;
            if (executeSpec == null) return;

            var entity = executeSpec.ExecutableEntity;

            // ── EXEC(@sql) ──────────────────────────────────────────────────────
            if (entity is ExecutableStringList strList)
            {
                foreach (var expr in strList.Strings)
                {
                    if (expr is VariableReference varRef &&
                        dynamicSqlMap.TryGetValue(varRef.Name, out var dynSql))
                    {
                        ParseAndProcessDynamicSql(dynSql, varRef.Name, procName,
                            defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                    }
                }
                return;
            }

            if (entity is ExecutableProcedureReference procRef)
            {
                var baseName = procRef.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;

                // ── EXEC sp_executesql @sql ──────────────────────────────────────
                if (string.Equals(baseName, "sp_executesql", StringComparison.OrdinalIgnoreCase)
                    && procRef.Parameters.Count > 0
                    && procRef.Parameters[0].ParameterValue is VariableReference dynVarRef
                    && dynamicSqlMap.TryGetValue(dynVarRef.Name, out var spDynSql))
                {
                    ParseAndProcessDynamicSql(spDynSql, dynVarRef.Name, procName,
                        defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
                    return;
                }

                // ── Regular stored-procedure call ────────────────────────────────
                if (procRef.ProcedureReference != null)
                {
                    generator.GenerateScript(procRef.ProcedureReference, out var fullName);
                    var schema = defaultDatabase;
                    if (!string.IsNullOrEmpty(fullName) && fullName.Contains('.'))
                    {
                        var segments = fullName.Split('.');
                        schema = segments.Length > 1 ? segments[^2].Trim('[', ']') : defaultDatabase;
                    }
                    generator.GenerateScript(executeStmt, out var commandText);

                    records.Add(new SqlLineageRecord
                    {
                        ProcedureName    = procName,
                        OperationType    = "EXECUTE_PROC",
                        SourceServer     = defaultServer,
                        SourceDatabase   = defaultDatabase,
                        SourceSchema     = schema,
                        SourceTable      = fullName,
                        SourceColumnName = "",
                        SourceExpression = commandText,
                        TargetServer     = defaultServer,
                        TargetDatabase   = defaultDatabase,
                        TargetSchema     = schema,
                        TargetTable      = fullName,
                        TargetColumnName = ""
                    });
                }
            }
        }

        // ── Dynamic SQL: substitute @params with DUMMY then re-parse ───────────

        private static void ParseAndProcessDynamicSql(
            string rawSql,
            string varName,
            string procName,
            string defaultDatabase,
            string defaultServer,
            List<SqlLineageRecord> records,
            Sql150ScriptGenerator generator,
            Dictionary<string, string> dynamicSqlMap)
        {
            // Replace @parameter tokens with the identifier DUMMY so the inner SQL
            // is independently parseable (mirrors the PS helper approach).
            var cleanSql = Regex.Replace(rawSql, @"@\w+", "DUMMY");

            var innerParser = new TSql150Parser(false);
            using var innerReader = new StringReader(cleanSql);
            var innerFrag = innerParser.Parse(innerReader, out var innerErrors);

            if (innerErrors?.Count > 0)
            {
                Console.WriteLine($"[Warning] Dynamic SQL parse failed for variable {varName}: {innerErrors[0].Message}");
                return;
            }

            if (innerFrag is TSqlScript innerScript)
            {
                foreach (var batch in innerScript.Batches)
                    ProcessStatements(batch.Statements, procName,
                        defaultDatabase, defaultServer, records, generator, dynamicSqlMap);
            }
        }

        // ── Reconstruct a SQL string from a SET @var = <expr> right-hand side ──
        // Variables already harvested into the map are inlined with their SQL text so the
        // composed string matches what EXEC(@var) executes at runtime (nested dynamic SQL).

        private static string? GetFullSqlFromExpression(
            ScalarExpression? expr, Dictionary<string, string>? dynamicSqlMap = null) => expr switch
        {
            StringLiteral lit         => lit.Value,
            VariableReference varRef  =>
                dynamicSqlMap != null && dynamicSqlMap.TryGetValue(varRef.Name, out var mapped)
                    ? mapped
                    : varRef.Name,
            BinaryExpression bin      =>
                (GetFullSqlFromExpression(bin.FirstExpression, dynamicSqlMap)  ?? "") +
                (GetFullSqlFromExpression(bin.SecondExpression, dynamicSqlMap) ?? ""),
            _                         => null
        };

        // ── SELECT / UNION dispatcher ───────────────────────────────────────────

        private static void ProcessSelect(
            QueryExpression qe,
            List<string> targetCols,
            string opType,
            string tgtTable,
            string tgtSchema,
            string tgtDb,
            string procName,
            string defaultServer,
            List<SqlLineageRecord> records,
            Sql150ScriptGenerator generator)
        {
            if (qe is QuerySpecification spec)
            {
                var tableAliasVisitor = new TableAliasVisitor();
                spec.Accept(tableAliasVisitor);

                var tableInfoVisitor = new TableInfoVisitor();
                spec.Accept(tableInfoVisitor);

                // JOIN details from every join in this query
                var joinVisitor = new JoinVisitor();
                spec.Accept(joinVisitor);
                var joinDetails = joinVisitor.JoinDetails.Count > 0
                    ? string.Join("; ", joinVisitor.JoinDetails) : "";

                // WHERE clause filter text
                var filterText = "";
                if (spec.WhereClause?.SearchCondition != null)
                    generator.GenerateScript(spec.WhereClause.SearchCondition, out filterText);

                var index   = 0;
                var hasStar = false;

                foreach (var element in spec.SelectElements)
                {
                    if (element is SelectScalarExpression scalar)
                    {
                        generator.GenerateScript(scalar.Expression, out var exprText);

                        var outAlias = scalar.ColumnName?.Value;
                        if (string.IsNullOrEmpty(outAlias) &&
                            scalar.Expression is ColumnReferenceExpression colRef)
                        {
                            outAlias = colRef.MultiPartIdentifier.Identifiers[^1].Value;
                        }

                        var colVisitor = new ColumnReferenceVisitor();
                        scalar.Expression.Accept(colVisitor);

                        var targetCol = (targetCols.Count > index) ? targetCols[index] : (outAlias ?? "");

                        if (colVisitor.Columns.Count == 0)
                        {
                            records.Add(new SqlLineageRecord
                            {
                                ProcedureName    = procName,
                                OperationType    = opType,
                                SourceServer     = defaultServer,
                                SourceDatabase   = tgtDb,
                                SourceSchema     = tgtSchema,
                                SourceTable      = "",
                                SourceColumnName = "",
                                SourceExpression = exprText,
                                TargetServer     = defaultServer,
                                TargetDatabase   = tgtDb,
                                TargetSchema     = tgtSchema,
                                TargetTable      = tgtTable,
                                TargetColumnName = targetCol,
                                JoinDetails      = joinDetails,
                                FilterConditions = filterText
                            });
                        }
                        else
                        {
                            foreach (var fullRef in colVisitor.Columns)
                            {
                                var sourceColName = fullRef.Contains('.') ? fullRef.Split('.', 2)[1] : fullRef;
                                var sourceAlias   = fullRef.Contains('.') ? fullRef.Split('.', 2)[0] : null;

                                var sourceTable  = "";
                                var sourceSchema = tgtSchema;
                                var sourceDb     = tgtDb;
                                var sourceServer = defaultServer;
                                if (sourceAlias != null &&
                                    tableAliasVisitor.Aliases.TryGetValue(sourceAlias, out var aliasTbl))
                                {
                                    sourceTable  = aliasTbl;
                                    if (tableInfoVisitor.TableSchemas.TryGetValue(aliasTbl, out var s))
                                        sourceSchema = s;
                                    if (tableInfoVisitor.TableParts.TryGetValue(aliasTbl, out var ap))
                                    {
                                        sourceDb     = ap.Database ?? sourceDb;
                                        sourceServer = ap.Server ?? sourceServer;
                                    }
                                }
                                else if (tableInfoVisitor.TableSchemas.Count > 0)
                                {
                                    // Unqualified column in a (possibly multi-table) FROM. With no
                                    // schema for the joined tables we cannot say which one owns it,
                                    // so list every candidate rather than guess the first.
                                    var (t, sc, db, srv) = ResolveUnqualifiedSource(
                                        tableInfoVisitor, sourceSchema, sourceDb, sourceServer);
                                    sourceTable  = t;
                                    sourceSchema = sc;
                                    sourceDb     = db;
                                    sourceServer = srv;
                                }

                                records.Add(new SqlLineageRecord
                                {
                                    ProcedureName    = procName,
                                    OperationType    = opType,
                                    SourceServer     = sourceServer,
                                    SourceDatabase   = sourceDb,
                                    SourceSchema     = sourceSchema,
                                    SourceTable      = sourceTable,
                                    SourceColumnName = sourceColName,
                                    SourceExpression = exprText,
                                    TargetServer     = defaultServer,
                                    TargetDatabase   = tgtDb,
                                    TargetSchema     = tgtSchema,
                                    TargetTable      = tgtTable,
                                    TargetColumnName = targetCol,
                                    JoinDetails      = joinDetails,
                                    FilterConditions = filterText
                                });
                            }
                        }
                        index++;
                    }
                    else if (element is SelectStarExpression)
                    {
                        hasStar = true;
                    }
                }

                // OPENQUERY / OPENROWSET pass-through queries — parse the inner remote query
                // and map its select list straight into this query's target. For SELECT *
                // INTO the inner output names ARE the target's column names, so any column
                // of the target traces through to the remote source tables.
                var openQueryVisitor = new OpenQueryVisitor();
                spec.Accept(openQueryVisitor);
                foreach (var (remoteServer, remoteSql) in openQueryVisitor.RemoteQueries)
                {
                    if (string.IsNullOrWhiteSpace(remoteSql)) continue;

                    var innerParser = new TSql150Parser(false);
                    using var innerReader = new StringReader(remoteSql);
                    var innerFrag = innerParser.Parse(innerReader, out var innerErrors);
                    if (innerErrors?.Count > 0 || innerFrag is not TSqlScript innerScript) continue;

                    foreach (var innerBatch in innerScript.Batches)
                    {
                        foreach (var innerStmt in innerBatch.Statements)
                        {
                            if (innerStmt is SelectStatement innerSelect)
                            {
                                ProcessSelect(innerSelect.QueryExpression, targetCols, opType,
                                    tgtTable, tgtSchema, tgtDb, procName,
                                    string.IsNullOrEmpty(remoteServer) ? defaultServer : remoteServer,
                                    records, generator);
                            }
                        }
                    }
                }

                // Derived-table subqueries in FROM — SELECT … FROM (SELECT …) AS alias —
                // trace each subquery into a pseudo-table named by its alias so base tables
                // chain through it to the outer target (same model as CTEs / MERGE USING).
                foreach (var derived in CollectDerivedTables(spec.FromClause))
                {
                    var derivedAlias = derived.Alias?.Value;
                    if (string.IsNullOrEmpty(derivedAlias)) continue;
                    var derivedCols = derived.Columns?.Select(c => c.Value).ToList() ?? new List<string>();
                    ProcessSelect(derived.QueryExpression, derivedCols, "DERIVED",
                        derivedAlias, "", tgtDb, procName, defaultServer, records, generator);
                }

                // SELECT * — emit one *→* record per source table so data flow is visible
                if (hasStar)
                {
                    var sourceTables = tableInfoVisitor.TableSchemas.Keys.ToList();
                    if (sourceTables.Count == 0) sourceTables.Add("");
                    foreach (var srcTable in sourceTables)
                    {
                        tableInfoVisitor.TableSchemas.TryGetValue(srcTable, out var srcSchema);
                        var starDb     = tgtDb;
                        var starServer = defaultServer;
                        if (tableInfoVisitor.TableParts.TryGetValue(srcTable, out var starParts))
                        {
                            starDb     = starParts.Database ?? starDb;
                            starServer = starParts.Server ?? starServer;
                        }
                        records.Add(new SqlLineageRecord
                        {
                            ProcedureName    = procName,
                            OperationType    = opType,
                            SourceServer     = starServer,
                            SourceDatabase   = starDb,
                            SourceSchema     = srcSchema ?? tgtSchema,
                            SourceTable      = srcTable,
                            SourceColumnName = "*",
                            SourceExpression = "SELECT *",
                            TargetServer     = defaultServer,
                            TargetDatabase   = tgtDb,
                            TargetSchema     = tgtSchema,
                            TargetTable      = tgtTable,
                            TargetColumnName = "*",
                            JoinDetails      = joinDetails,
                            FilterConditions = filterText
                        });
                    }
                }
            }
            else if (qe is BinaryQueryExpression binary)
            {
                ProcessSelect(binary.FirstQueryExpression,  targetCols, opType, tgtTable, tgtSchema, tgtDb, procName, defaultServer, records, generator);
                ProcessSelect(binary.SecondQueryExpression, targetCols, opType, tgtTable, tgtSchema, tgtDb, procName, defaultServer, records, generator);
            }
        }

        /// <summary>
        /// Attribution for a select-list column that carries no table alias. A single-table
        /// FROM is unambiguous; a multi-table FROM is not (we have no remote/base schema to
        /// tell which joined table declares the column), so we return every candidate table
        /// — pipe-delimited in FROM order — instead of asserting a wrong single table. The
        /// server/database/schema collapse to the shared value when all candidates agree.
        /// </summary>
        private static (string Table, string Schema, string Db, string Server) ResolveUnqualifiedSource(
            TableInfoVisitor info, string defaultSchema, string defaultDb, string defaultServer)
        {
            var tables = info.TableParts.Keys.ToList();
            if (tables.Count == 0)
                return ("", defaultSchema, defaultDb, defaultServer);

            if (tables.Count == 1)
            {
                var only = info.TableParts[tables[0]];
                return (tables[0], only.Schema, only.Database ?? defaultDb, only.Server ?? defaultServer);
            }

            var parts   = tables.Select(t => info.TableParts[t]).ToList();
            var schemas = parts.Select(p => p.Schema ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var dbs     = parts.Select(p => p.Database).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var servers = parts.Select(p => p.Server).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            return (
                string.Join(" | ", tables),
                schemas.Count == 1 ? schemas[0] : "",
                dbs.Count == 1 && !string.IsNullOrEmpty(dbs[0]) ? dbs[0]! : defaultDb,
                servers.Count == 1 && !string.IsNullOrEmpty(servers[0]) ? servers[0]! : defaultServer);
        }

        // FROM-level derived tables only — derived tables nested inside a subquery are
        // handled when that subquery is itself processed recursively.
        private static IEnumerable<QueryDerivedTable> CollectDerivedTables(FromClause? from)
        {
            if (from == null) yield break;
            var stack = new Stack<TableReference>(from.TableReferences);
            while (stack.Count > 0)
            {
                switch (stack.Pop())
                {
                    case QueryDerivedTable qdt:
                        yield return qdt;
                        break;
                    case QualifiedJoin qj:
                        stack.Push(qj.FirstTableReference);
                        stack.Push(qj.SecondTableReference);
                        break;
                    case UnqualifiedJoin uj:
                        stack.Push(uj.FirstTableReference);
                        stack.Push(uj.SecondTableReference);
                        break;
                    case JoinParenthesisTableReference jp:
                        stack.Push(jp.Join);
                        break;
                }
            }
        }

        // ── MERGE ───────────────────────────────────────────────────────────────

        private static void ProcessMerge(
            MergeStatement mergeStmt,
            string procName,
            string defaultDatabase,
            string defaultServer,
            List<SqlLineageRecord> records,
            Sql150ScriptGenerator generator)
        {
            var mg = mergeStmt.MergeSpecification;
            if (mg.Target is not NamedTableReference namedMergeTarget) return;

            var tgt       = namedMergeTarget.SchemaObject.BaseIdentifier.Value;
            var tgtSchema = namedMergeTarget.SchemaObject.SchemaIdentifier?.Value ?? "dbo";
            var tgtDb     = namedMergeTarget.SchemaObject.DatabaseIdentifier?.Value ?? defaultDatabase;
            var tgtServer = namedMergeTarget.SchemaObject.ServerIdentifier?.Value ?? defaultServer;

            // Alias → table and table → (server, db, schema) across the whole MERGE,
            // covering the target alias and the USING source (incl. 4-part linked-server names).
            var aliasVisitor = new TableAliasVisitor();
            mergeStmt.Accept(aliasVisitor);
            var infoVisitor = new TableInfoVisitor();
            mergeStmt.Accept(infoVisitor);

            // USING (SELECT …) AS alias — trace the subquery into a pseudo-table named by
            // the alias so base tables chain through it to the MERGE target.
            var derivedAlias = "";
            if (mg.TableReference is QueryDerivedTable derivedSource && derivedSource.Alias != null)
            {
                derivedAlias = derivedSource.Alias.Value;
                var derivedCols = derivedSource.Columns?.Select(c => c.Value).ToList() ?? new List<string>();
                ProcessSelect(derivedSource.QueryExpression, derivedCols, "MERGE-SOURCE",
                    derivedAlias, "", defaultDatabase, procName, defaultServer, records, generator);
            }

            // The MERGE ON condition is the join condition between source and target
            var mergeOn = "";
            if (mg.SearchCondition != null)
                generator.GenerateScript(mg.SearchCondition, out mergeOn);

            // Resolves "alias.Col" (or bare "Col") to the table it belongs to.
            (string Server, string Db, string Schema, string Table, string Column) ResolveSourceColumn(string fullRef)
            {
                var col   = fullRef.Contains('.') ? fullRef.Split('.', 2)[1] : fullRef;
                var alias = fullRef.Contains('.') ? fullRef.Split('.', 2)[0] : null;

                var table  = "";
                var schema = tgtSchema;
                var db     = defaultDatabase;
                var server = defaultServer;
                if (alias != null && aliasVisitor.Aliases.TryGetValue(alias, out var aliasTable))
                {
                    table = aliasTable;
                    if (infoVisitor.TableParts.TryGetValue(aliasTable, out var parts))
                    {
                        schema = parts.Schema;
                        db     = parts.Database ?? defaultDatabase;
                        server = parts.Server ?? defaultServer;
                    }
                }
                else if (alias != null && string.Equals(alias, derivedAlias, StringComparison.OrdinalIgnoreCase))
                {
                    // USING (SELECT …) AS alias — chain through the pseudo-table emitted
                    // by the MERGE-SOURCE records above.
                    table  = derivedAlias;
                    schema = "";
                }
                return (server, db, schema, table, col);
            }

            foreach (var clause in mg.ActionClauses)
            {
                if (clause.Action is InsertMergeAction insertAction)
                {
                    var tCols = insertAction.Columns
                        .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
                        .ToList();

                    // Pair each VALUES expression with its target column positionally so
                    // literals/GETDATE() don't shift the mapping.
                    if (insertAction.Source is ValuesInsertSource valuesSource)
                    {
                        foreach (var row in valuesSource.RowValues)
                        {
                            for (var i = 0; i < row.ColumnValues.Count; i++)
                            {
                                var targetCol = i < tCols.Count ? tCols[i] : "";
                                var valueExpr = row.ColumnValues[i];
                                generator.GenerateScript(valueExpr, out var exprText);

                                var colVisitor = new ColumnReferenceVisitor();
                                valueExpr.Accept(colVisitor);
                                if (colVisitor.Columns.Count == 0) continue; // literal/function — no source column

                                foreach (var fullRef in colVisitor.Columns)
                                {
                                    var src = ResolveSourceColumn(fullRef);
                                    records.Add(new SqlLineageRecord
                                    {
                                        ProcedureName    = procName,
                                        OperationType    = "MERGE-INSERT",
                                        SourceServer     = src.Server,
                                        SourceDatabase   = src.Db,
                                        SourceSchema     = src.Schema,
                                        SourceTable      = src.Table,
                                        SourceColumnName = src.Column,
                                        SourceExpression = exprText,
                                        TargetServer     = tgtServer,
                                        TargetDatabase   = tgtDb,
                                        TargetSchema     = tgtSchema,
                                        TargetTable      = tgt,
                                        TargetColumnName = targetCol,
                                        JoinDetails      = mergeOn
                                    });
                                }
                            }
                        }
                    }
                }
                else if (clause.Action is UpdateMergeAction updateAction)
                {
                    foreach (var setClause in updateAction.SetClauses)
                    {
                        if (setClause is AssignmentSetClause assignment)
                        {
                            var targetCol = assignment.Column.MultiPartIdentifier.Identifiers[^1].Value;
                            generator.GenerateScript(assignment.NewValue, out var exprText);
                            var colVisitor = new ColumnReferenceVisitor();
                            assignment.NewValue.Accept(colVisitor);
                            foreach (var srcCol in colVisitor.Columns)
                            {
                                var src = ResolveSourceColumn(srcCol);
                                records.Add(new SqlLineageRecord
                                {
                                    ProcedureName    = procName,
                                    OperationType    = "MERGE-UPDATE",
                                    SourceServer     = src.Server,
                                    SourceDatabase   = src.Db,
                                    SourceSchema     = src.Schema,
                                    SourceTable      = src.Table,
                                    SourceColumnName = src.Column,
                                    SourceExpression = exprText,
                                    TargetServer     = tgtServer,
                                    TargetDatabase   = tgtDb,
                                    TargetSchema     = tgtSchema,
                                    TargetTable      = tgt,
                                    TargetColumnName = targetCol,
                                    JoinDetails      = mergeOn
                                });
                            }
                        }
                    }
                }
            }
        }
    }

    // ── Record ────────────────────────────────────────────────────────────────────

    public class SqlLineageRecord
    {
        public string ProcedureName    { get; set; } = "";
        public string OperationType    { get; set; } = "";
        public string SourceServer     { get; set; } = "";
        public string SourceDatabase   { get; set; } = "";
        public string SourceSchema     { get; set; } = "";
        public string SourceTable      { get; set; } = "";
        public string SourceColumnName { get; set; } = "";
        public string SourceExpression { get; set; } = "";
        public string TargetServer     { get; set; } = "";
        public string TargetDatabase   { get; set; } = "";
        public string TargetSchema     { get; set; } = "";
        public string TargetTable      { get; set; } = "";
        public string TargetColumnName { get; set; } = "";
        /// <summary>JOIN conditions extracted from the FROM clause (QualifiedJoin) and
        /// the MERGE ON predicate, semicolon-separated.</summary>
        public string JoinDetails      { get; set; } = "";
        /// <summary>WHERE clause predicate text rendered by ScriptGenerator.</summary>
        public string FilterConditions { get; set; } = "";
    }

    // ── Visitors ──────────────────────────────────────────────────────────────────

    #region Visitors

    internal class ColumnReferenceVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> Columns { get; } = new();

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.MultiPartIdentifier == null) return;
            var ids = node.MultiPartIdentifier.Identifiers;
            var col = ids[^1].Value;
            Columns.Add(ids.Count >= 2 ? $"{ids[^2].Value}.{col}" : col);
        }
    }

    internal class ColumnAliasVisitor : TSqlFragmentVisitor
    {
        public HashSet<string> Columns { get; } = new();

        public override void Visit(SelectScalarExpression node)
        {
            if (node.ColumnName != null)
            {
                Columns.Add(node.ColumnName.Value);
            }
            else if (node.Expression is ColumnReferenceExpression colRef)
            {
                Columns.Add(colRef.MultiPartIdentifier.Identifiers[^1].Value);
            }
        }
    }

    internal class TableAliasVisitor : TSqlFragmentVisitor
    {
        public Dictionary<string, string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            var table = node.SchemaObject.BaseIdentifier.Value;
            Aliases[node.Alias != null ? node.Alias.Value : table] = table;
        }

        // Derived table (SELECT …) AS alias — the alias IS the pseudo-table that the
        // subquery's lineage records target, so references resolve to it by name.
        public override void Visit(QueryDerivedTable node)
        {
            if (node.Alias != null)
                Aliases[node.Alias.Value] = node.Alias.Value;
        }
    }

    internal class TableInfoVisitor : TSqlFragmentVisitor
    {
        /// <summary>table name → schema name</summary>
        public Dictionary<string, string> TableSchemas { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>table name → (server, database, schema). Server/database are null unless the
        /// reference used a 3- or 4-part name (e.g. linked server: [SRV].[Db].[Schema].[Table]).</summary>
        public Dictionary<string, (string? Server, string? Database, string Schema)> TableParts { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            var table  = node.SchemaObject.BaseIdentifier.Value;
            var schema = node.SchemaObject.SchemaIdentifier?.Value ?? "dbo";
            if (!string.IsNullOrEmpty(table))
            {
                TableSchemas[table] = schema;
                TableParts[table] = (node.SchemaObject.ServerIdentifier?.Value,
                                     node.SchemaObject.DatabaseIdentifier?.Value,
                                     schema);
            }
        }

        // Derived-table pseudo-tables have no schema — their node identity is the bare
        // alias, matching the records the subquery emits.
        public override void Visit(QueryDerivedTable node)
        {
            if (node.Alias == null) return;
            TableSchemas[node.Alias.Value] = "";
            TableParts[node.Alias.Value] = (null, null, "");
        }
    }

    /// <summary>
    /// Collects remote pass-through queries: OPENQUERY(linked_server, 'query') and
    /// OPENROWSET(provider, connection, 'query'). Each yields (server, query text).
    /// </summary>
    internal class OpenQueryVisitor : TSqlFragmentVisitor
    {
        public List<(string Server, string Query)> RemoteQueries { get; } = new();

        public override void Visit(OpenQueryTableReference node) =>
            RemoteQueries.Add((node.LinkedServer?.Value ?? "", node.Query?.Value ?? ""));

        public override void Visit(OpenRowsetTableReference node) =>
            RemoteQueries.Add((node.DataSource?.Value ?? "", node.Query?.Value ?? ""));
    }

    /// <summary>
    /// Collects all JOIN conditions from a query fragment.
    /// Mirrors the PS helper's JoinVisitor — renders each condition via ScriptGenerator
    /// and formats it as "JoinType: condition".
    /// </summary>
    internal class JoinVisitor : TSqlFragmentVisitor
    {
        private readonly Sql150ScriptGenerator _gen = new();
        public List<string> JoinDetails { get; } = new();

        public override void Visit(QualifiedJoin node)
        {
            var joinType = node.QualifiedJoinType.ToString();   // e.g. "Inner", "LeftOuter"
            var condition = "";
            if (node.SearchCondition != null)
                _gen.GenerateScript(node.SearchCondition, out condition);
            JoinDetails.Add(string.IsNullOrEmpty(condition) ? joinType : $"{joinType}: {condition}");
        }

        public override void Visit(UnqualifiedJoin node)
        {
            // CROSS JOIN, CROSS APPLY, OUTER APPLY — no condition
            JoinDetails.Add(node.UnqualifiedJoinType.ToString());
        }
    }

    #endregion
}
