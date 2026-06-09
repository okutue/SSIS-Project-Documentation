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
        public static List<SqlLineageRecord> Parse(string sqlScript, string defaultDatabase, string defaultServer)
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

            // ── Pre-pass: harvest  SET @var = 'literal sql'  before processing EXEC ──
            foreach (var stmt in stmtList)
            {
                if (stmt is SetVariableStatement setVar)
                {
                    var sqlText = GetFullSqlFromExpression(setVar.Expression);
                    if (!string.IsNullOrEmpty(sqlText))
                        dynamicSqlMap[setVar.Variable.Name] = sqlText;
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
                                    if (sourceAlias != null && updAliasVisitor.Aliases.TryGetValue(sourceAlias, out var aliasTable))
                                    {
                                        sourceTable = aliasTable;
                                        if (updInfoVisitor.TableSchemas.TryGetValue(aliasTable, out var s))
                                            sourceSchema = s;
                                    }

                                    records.Add(new SqlLineageRecord
                                    {
                                        ProcedureName    = procName,
                                        OperationType    = "UPDATE",
                                        SourceServer     = defaultServer,
                                        SourceDatabase   = defaultDatabase,
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

        private static string? GetFullSqlFromExpression(ScalarExpression? expr) => expr switch
        {
            StringLiteral lit         => lit.Value,
            VariableReference varRef  => varRef.Name,
            BinaryExpression bin      =>
                (GetFullSqlFromExpression(bin.FirstExpression)  ?? "") +
                (GetFullSqlFromExpression(bin.SecondExpression) ?? ""),
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
                                if (sourceAlias != null &&
                                    tableAliasVisitor.Aliases.TryGetValue(sourceAlias, out var aliasTbl))
                                {
                                    sourceTable  = aliasTbl;
                                    if (tableInfoVisitor.TableSchemas.TryGetValue(aliasTbl, out var s))
                                        sourceSchema = s;
                                }
                                else if (tableInfoVisitor.TableSchemas.Count > 0)
                                {
                                    var first    = tableInfoVisitor.TableSchemas.First();
                                    sourceTable  = string.IsNullOrEmpty(sourceTable) ? first.Key : sourceTable;
                                    sourceSchema = first.Value;
                                }

                                records.Add(new SqlLineageRecord
                                {
                                    ProcedureName    = procName,
                                    OperationType    = opType,
                                    SourceServer     = defaultServer,
                                    SourceDatabase   = tgtDb,
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

                // SELECT * — emit one *→* record per source table so data flow is visible
                if (hasStar)
                {
                    var sourceTables = tableInfoVisitor.TableSchemas.Keys.ToList();
                    if (sourceTables.Count == 0) sourceTables.Add("");
                    foreach (var srcTable in sourceTables)
                    {
                        tableInfoVisitor.TableSchemas.TryGetValue(srcTable, out var srcSchema);
                        records.Add(new SqlLineageRecord
                        {
                            ProcedureName    = procName,
                            OperationType    = opType,
                            SourceServer     = defaultServer,
                            SourceDatabase   = tgtDb,
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

            // The MERGE ON condition is the join condition between source and target
            var mergeOn = "";
            if (mg.SearchCondition != null)
                generator.GenerateScript(mg.SearchCondition, out mergeOn);

            foreach (var clause in mg.ActionClauses)
            {
                if (clause.Action is InsertMergeAction insertAction)
                {
                    var tCols = insertAction.Columns
                        .Select(c => c.MultiPartIdentifier.Identifiers[^1].Value)
                        .ToList();
                    var colVisitor = new ColumnReferenceVisitor();
                    clause.Action.Accept(colVisitor);
                    var idx = 0;
                    foreach (var fullRef in colVisitor.Columns)
                    {
                        var srcCol = fullRef.Contains('.') ? fullRef.Split('.', 2)[1] : fullRef;
                        records.Add(new SqlLineageRecord
                        {
                            ProcedureName    = procName,
                            OperationType    = "MERGE-INSERT",
                            SourceServer     = defaultServer,
                            SourceDatabase   = tgtDb,
                            SourceSchema     = tgtSchema,
                            SourceTable      = "",
                            SourceColumnName = srcCol,
                            TargetServer     = defaultServer,
                            TargetDatabase   = tgtDb,
                            TargetSchema     = tgtSchema,
                            TargetTable      = tgt,
                            TargetColumnName = idx < tCols.Count ? tCols[idx] : srcCol,
                            JoinDetails      = mergeOn
                        });
                        idx++;
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
                                var colName = srcCol.Contains('.') ? srcCol.Split('.', 2)[1] : srcCol;
                                records.Add(new SqlLineageRecord
                                {
                                    ProcedureName    = procName,
                                    OperationType    = "MERGE-UPDATE",
                                    SourceServer     = defaultServer,
                                    SourceDatabase   = defaultDatabase,
                                    SourceSchema     = tgtSchema,
                                    SourceTable      = "",
                                    SourceColumnName = colName,
                                    SourceExpression = exprText,
                                    TargetServer     = defaultServer,
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
    }

    internal class TableInfoVisitor : TSqlFragmentVisitor
    {
        /// <summary>table name → schema name</summary>
        public Dictionary<string, string> TableSchemas { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override void Visit(NamedTableReference node)
        {
            var table  = node.SchemaObject.BaseIdentifier.Value;
            var schema = node.SchemaObject.SchemaIdentifier?.Value ?? "dbo";
            if (!string.IsNullOrEmpty(table))
                TableSchemas[table] = schema;
        }
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
