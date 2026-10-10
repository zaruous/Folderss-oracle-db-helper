using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>테이블 정의서: 대상 선택(트리 행 → 요청, 메뉴 글, 정규식, 파일 이름), 일괄 조회 SQL·읽기, 템플릿 변수 모델.</summary>
    public class TableSpecTests
    {
        private static TreeRow Db(string db = "d1") { return new TreeRow { Kind = TreeRowKind.Database, DbId = db, Key = TreeKeys.Db(db) }; }
        private static TreeRow Schema(string owner, string db = "d1") { return new TreeRow { Kind = TreeRowKind.Schema, DbId = db, Owner = owner, Key = TreeKeys.Schema(db, owner) }; }
        private static TreeRow Group(string owner, string group) { return new TreeRow { Kind = TreeRowKind.Group, DbId = "d1", Owner = owner, ObjectType = group, Key = TreeKeys.Group("d1", owner, group) }; }

        private static TreeRow Obj(string owner, string name, string type = "TABLE", string db = "d1")
        {
            return new TreeRow { Kind = TreeRowKind.Object, DbId = db, Owner = owner, ObjectName = name, ObjectType = type, Key = TreeKeys.Object(db, owner, type, name) };
        }

        // ---------- 메뉴 글 ----------

        [Fact]
        public void MenuText_ByRowKind()
        {
            Assert.Equal("테이블 정의서 만들기…", TableSpecLogic.MenuText(Db(), new[] { Db() }));
            Assert.Equal("테이블 정의서 만들기 (스키마 전체)…", TableSpecLogic.MenuText(Schema("SCOTT"), new[] { Schema("SCOTT") }));
            Assert.Equal("테이블 정의서 만들기 (테이블 전체)…", TableSpecLogic.MenuText(Group("SCOTT", TreeGroups.Table), null));
            Assert.Equal("테이블 정의서 만들기 (뷰 전체)…", TableSpecLogic.MenuText(Group("SCOTT", TreeGroups.View), null));
            Assert.Null(TableSpecLogic.MenuText(Group("SCOTT", TreeGroups.Sequence), null));
            Assert.Equal("테이블 정의서 만들기…", TableSpecLogic.MenuText(Obj("SCOTT", "EMP"), new[] { Obj("SCOTT", "EMP") }));
            Assert.Null(TableSpecLogic.MenuText(Obj("SCOTT", "SEQ1", "SEQUENCE"), null));
        }

        [Fact]
        public void MenuText_ManySelectedObjects_ShowsCount()
        {
            var rows = new[] { Obj("SCOTT", "EMP"), Obj("SCOTT", "DEPT"), Obj("HR", "JOBS", "VIEW"), Schema("SCOTT") };
            Assert.Equal("테이블 정의서 만들기 (선택한 3개)…", TableSpecLogic.MenuText(rows[0], rows));
        }

        // ---------- 요청 ----------

        [Fact]
        public void Request_FromSelectedObjects()
        {
            var request = TableSpecLogic.Request("d1", new[] { Obj("SCOTT", "EMP"), Obj("SCOTT", "DEPT"), Obj("SCOTT", "EMP"), Obj("X", "OTHER_DB", "TABLE", "d2") }, "ME", null, false);

            Assert.Equal(new[] { "SCOTT.EMP", "SCOTT.DEPT" }, request.Checked.Select(k => k.ToString()).ToArray());
            Assert.Equal("SCOTT", request.InitialOwner);
            Assert.Equal("ME", request.MySchema);
            Assert.False(request.IncludeViews);
            Assert.Null(request.Schemas);
            Assert.Equal(new[] { "SCOTT" }, request.OwnersToLoad.ToArray());
        }

        [Fact]
        public void Request_ColumnRowMeansItsTable_ViewTurnsOnViews()
        {
            var column = new TreeRow { Kind = TreeRowKind.Column, DbId = "d1", Owner = "HR", ObjectName = "EMP_V", ObjectType = "VIEW", ColumnName = "ID" };
            var request = TableSpecLogic.Request("d1", new[] { column }, "ME", null, false);

            Assert.Equal("HR.EMP_V", request.Checked.Single().ToString());
            Assert.True(request.IncludeViews);
        }

        [Fact]
        public void Request_SchemaAndGroups_CheckAll()
        {
            var request = TableSpecLogic.Request("d1", new[] { Schema("SCOTT"), Group("HR", TreeGroups.Table), Group("HR", TreeGroups.View) }, "ME", null, false);

            Assert.Equal(new[] { "SCOTT", "HR" }, request.CheckAllTables.ToArray());
            Assert.Equal(new[] { "HR" }, request.CheckAllViews.ToArray());
            Assert.True(request.IncludeViews);
            Assert.Equal("SCOTT", request.InitialOwner);
        }

        [Fact]
        public void Request_DatabaseOnly_ShowsMySchemaWithoutChecks()
        {
            var schemas = new[]
            {
                new SchemaInfo { Name = "SYS", OracleMaintained = true },
                new SchemaInfo { Name = "SCOTT" },
                new SchemaInfo { Name = "ME" }
            };
            var request = TableSpecLogic.Request("d1", new[] { Db() }, "ME", schemas, false);

            Assert.Empty(request.Checked);
            Assert.Equal("ME", request.InitialOwner);
            Assert.Equal(new[] { "SCOTT", "ME" }, request.Schemas.ToArray());
            Assert.Equal(new[] { "SYS", "SCOTT", "ME" }, TableSpecLogic.Request("d1", new[] { Db() }, "ME", schemas, true).Schemas.ToArray());
        }

        [Fact]
        public void Request_SystemSchemaPickedInTree_StaysInList()
        {
            var schemas = new[] { new SchemaInfo { Name = "SYS", OracleMaintained = true }, new SchemaInfo { Name = "ME" } };
            var request = TableSpecLogic.Request("d1", new[] { Obj("SYS", "DUAL") }, "ME", schemas, false);
            Assert.Contains("SYS", request.Schemas);
        }

        [Fact]
        public void OrderSchemas_MySchemaFirst()
        {
            Assert.Equal(new[] { "ME", "A", "B" }, TableSpecLogic.OrderSchemas(new[] { "B", "ME", "A", "B", null }, "ME").ToArray());
        }

        // ---------- 정규식 ----------

        private static TableSpecCandidate Candidate(string name, string comment = null)
        {
            return new TableSpecCandidate { Owner = "SCOTT", Name = name, Type = "TABLE", Comment = comment };
        }

        [Fact]
        public void Pattern_EmptyMatchesAll()
        {
            string error;
            Assert.Null(TableSpecLogic.Pattern("  ", true, out error));
            Assert.Null(error);
            Assert.True(TableSpecLogic.Matches(null, Candidate("ANY"), false));
        }

        [Fact]
        public void Pattern_IgnoreCaseAndComment()
        {
            string error;
            var pattern = TableSpecLogic.Pattern("^tb_.*_m$", true, out error);
            Assert.True(TableSpecLogic.Matches(pattern, Candidate("TB_ORDER_M"), false));
            Assert.False(TableSpecLogic.Matches(pattern, Candidate("TB_ORDER_D"), false));
            Assert.False(TableSpecLogic.Matches(TableSpecLogic.Pattern("^tb_", false, out error), Candidate("TB_ORDER_M"), false));

            var byComment = TableSpecLogic.Pattern("주문", true, out error);
            Assert.False(TableSpecLogic.Matches(byComment, Candidate("TB_A", "주문 마스터"), false));
            Assert.True(TableSpecLogic.Matches(byComment, Candidate("TB_A", "주문 마스터"), true));
        }

        [Fact]
        public void Pattern_InvalidGivesError()
        {
            string error;
            Assert.Null(TableSpecLogic.Pattern("TB_(", true, out error));
            Assert.StartsWith("정규식 오류: ", error);
        }

        // ---------- 글 ----------

        [Fact]
        public void SelectionAndMatchText()
        {
            Assert.Equal("체크한 테이블 없음", TableSpecLogic.SelectionText(new List<TableKey>()));
            Assert.Equal("체크 2개", TableSpecLogic.SelectionText(new[] { new TableKey("A", "X"), new TableKey("A", "Y") }));
            Assert.Equal("체크 2개 · 스키마 2개", TableSpecLogic.SelectionText(new[] { new TableKey("A", "X"), new TableKey("B", "Y") }));
            Assert.Equal("일치 37 / 전체 1,200", TableSpecLogic.MatchText(37, 1200, true));
            Assert.Equal("전체 120", TableSpecLogic.MatchText(120, 120, false));
        }

        [Fact]
        public void Order_ByOwnerThenName_Distinct()
        {
            var keys = new[] { new TableKey("B", "A"), new TableKey("A", "Z"), new TableKey("A", "B"), new TableKey("A", "Z") };
            Assert.Equal(new[] { "A.B", "A.Z", "B.A" }, TableSpecLogic.Order(keys).Select(k => k.ToString()).ToArray());
        }

        [Fact]
        public void DefaultFileName_SafeAndDescriptive()
        {
            var date = new DateTime(2026, 10, 10);
            Assert.Equal("테이블_정의서_개발_DB_SCOTT_20261010.xlsx", TableSpecLogic.DefaultFileName("테이블 정의서", "개발 DB", new[] { "SCOTT", "SCOTT" }, date));
            Assert.Equal("테이블_정의서_A_외_2_20261010.xlsx", TableSpecLogic.DefaultFileName(" ", null, new[] { "A", "B", "C" }, date));
            Assert.Equal("a_b_c_20261010.xlsx", TableSpecLogic.DefaultFileName("a/b:c", "", null, date));
        }

        [Theory]
        [InlineData("2026-10-10")]
        [InlineData("2026.10.10")]
        [InlineData("20261010")]
        [InlineData(" 2026-10-10 ")]
        public void ParseDate_Formats(string text)
        {
            Assert.Equal(new DateTime(2026, 10, 10), TableSpecLogic.ParseDate(text));
        }

        [Fact]
        public void ParseDate_Invalid()
        {
            Assert.Null(TableSpecLogic.ParseDate("10/10/2026"));
            Assert.Null(TableSpecLogic.ParseDate(""));
        }

        // ---------- 지난번 입력값 저장·불러오기 ----------

        [Fact]
        public void Settings_RoundTripThroughJson()
        {
            var document = new TableSpecDocument
            {
                Title = "주문 테이블 명세서", Project = "주문 관리", System = "주문 · 결제", Author = "홍길동",
                Version = "1.2", Date = new DateTime(2026, 9, 30), Description = "1차 오픈 기준"
            };
            var json = TableSpecLogic.SerializeSettings(TableSpecLogic.SettingsFrom(document, TableSpecTemplates.SpecSheetId, @"C:\양식\명세서.xlsx", @"D:\산출물"));

            Assert.Contains("\"system\":", json);
            var settings = TableSpecLogic.ParseSettings(json);
            var restored = TableSpecLogic.DocumentFrom(settings, "기본사용자", new DateTime(2026, 10, 10));
            Assert.Equal("주문 테이블 명세서", restored.Title);
            Assert.Equal("주문 관리", restored.Project);
            Assert.Equal("주문 · 결제", restored.System);
            Assert.Equal("홍길동", restored.Author);
            Assert.Equal("1.2", restored.Version);
            Assert.Equal(new DateTime(2026, 9, 30), restored.Date);
            Assert.Equal("1차 오픈 기준", restored.Description);
            Assert.Equal(TableSpecTemplates.SpecSheetId, settings.TemplateId);
            Assert.Equal(@"C:\양식\명세서.xlsx", settings.TemplatePath);
            Assert.Equal(@"D:\산출물", settings.Folder);
        }

        [Fact]
        public void Settings_DefaultTemplateAndBlankFolder_AreNull()
        {
            var settings = TableSpecLogic.SettingsFrom(new TableSpecDocument(), TableSpecTemplates.DefaultId, " ", "");
            Assert.Null(settings.TemplatePath);
            Assert.Null(settings.Folder);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("{깨진 json")]
        public void Settings_MissingOrBroken_OpensWithDefaults(string json)
        {
            var settings = TableSpecLogic.ParseSettings(json);
            Assert.Null(settings);
            var document = TableSpecLogic.DocumentFrom(settings, "사용자", new DateTime(2026, 10, 10));
            Assert.Equal(TableSpecDocument.DefaultTitle, document.Title);
            Assert.Equal("사용자", document.Author);
            Assert.Equal("1.0", document.Version);
            Assert.Equal(new DateTime(2026, 10, 10), document.Date);
        }

        [Fact]
        public void Settings_ClearedFieldsStayEmpty_BadDateFallsBackToToday()
        {
            var settings = TableSpecLogic.ParseSettings("{\"title\":\"\",\"author\":\"\",\"date\":\"언젠가\"}");
            var document = TableSpecLogic.DocumentFrom(settings, "사용자", new DateTime(2026, 10, 10));
            Assert.Equal("", document.Title);
            Assert.Equal("", document.Author);
            Assert.Equal("1.0", document.Version);
            Assert.Equal(new DateTime(2026, 10, 10), document.Date);
        }

        [Fact]
        public void DoneText_MentionsMissing()
        {
            Assert.Equal("테이블 3개로 정의서를 만들었습니다: C:\\a.xlsx", TableSpecLogic.DoneText(3, 0, "C:\\a.xlsx"));
            Assert.EndsWith("(찾지 못한 1개는 뺐습니다)", TableSpecLogic.DoneText(3, 1, "C:\\a.xlsx"));
        }

        // ---------- 일괄 조회 SQL ----------

        [Fact]
        public void Candidates_TablesOrAlsoViews()
        {
            var tables = TableSpecQueries.Candidates("SCOTT", false);
            Assert.Contains("o.OBJECT_TYPE IN ('TABLE')", tables.Sql);
            Assert.Contains("NOT LIKE 'BIN$%'", tables.Sql);
            Assert.Contains("ALL_TAB_COMMENTS", tables.Sql);
            Assert.Equal("SCOTT", tables.Parameters.Single().Value);
            Assert.Contains("('TABLE','VIEW')", TableSpecQueries.Candidates("SCOTT", true).Sql);
        }

        [Fact]
        public void BulkQueries_BindOwnerAndNames()
        {
            var names = new[] { "EMP", "DEPT" };
            foreach (var query in new[] { TableSpecQueries.Summaries("SCOTT", names), TableSpecQueries.Columns("SCOTT", names), TableSpecQueries.Indexes("SCOTT", names), TableSpecQueries.Constraints("SCOTT", names) })
            {
                Assert.Contains("IN (:t0, :t1)", query.Sql);
                Assert.DoesNotContain("{0}", query.Sql);
                Assert.Equal(new[] { "owner", "t0", "t1" }, query.Parameters.Select(p => p.Key).ToArray());
                Assert.Equal(new object[] { "SCOTT", "EMP", "DEPT" }, query.Parameters.Select(p => p.Value).ToArray());
            }
            Assert.Contains("c.DATA_DEFAULT", TableSpecQueries.Columns("S", names).Sql);
            Assert.Contains("CHR(31)", TableSpecQueries.Indexes("S", names).Sql);
            Assert.Contains("R_COLS", TableSpecQueries.Constraints("S", names).Sql);
            Assert.Contains("c.SEARCH_CONDITION", TableSpecQueries.Constraints("S", names).Sql);
            Assert.Contains("NOT (c.CONSTRAINT_TYPE = 'C' AND c.GENERATED = 'GENERATED NAME')", TableSpecQueries.Constraints("S", names).Sql);
        }

        [Fact]
        public void BulkQueries_RejectEmptyOrTooMany()
        {
            Assert.Throws<ArgumentException>(() => TableSpecQueries.Columns("S", new string[0]));
            Assert.Throws<ArgumentException>(() => TableSpecQueries.Columns("S", Enumerable.Range(0, TableSpecQueries.ChunkSize + 1).Select(i => "T" + i).ToList()));
        }

        [Fact]
        public void Chunks_ByOwnerAndSize()
        {
            var keys = Enumerable.Range(0, 1001).Select(i => new TableKey("A", "T" + i)).Concat(new[] { new TableKey("B", "X"), new TableKey("A", "T0") }).ToList();
            var chunks = TableSpecQueries.Chunks(keys);

            Assert.Equal(new[] { "A", "A", "A", "B" }, chunks.Select(c => c.Key).ToArray());
            Assert.Equal(new[] { 500, 500, 1, 1 }, chunks.Select(c => c.Value.Count).ToArray());
        }

        [Fact]
        public void ReadConstraint_SplitsListsAndCondition()
        {
            var record = Record(new Dictionary<string, object>
            {
                { "CONSTRAINT_NAME", "FK_X" }, { "CONSTRAINT_TYPE", "R" }, { "STATUS", "ENABLED" },
                { "COLS", "A, 1\u001FB" }, { "R_OWNER", "HR" }, { "R_TABLE", "T" }, { "R_COLS", "ID\u001FNO" }, { "SEARCH_CONDITION", DBNull.Value },
                { "DELETE_RULE", "CASCADE" }, { "INDEX_NAME", DBNull.Value }
            });
            var constraint = TableSpecQueries.ReadConstraint(record);

            Assert.Equal(new[] { "A, 1", "B" }, constraint.ColumnNames.ToArray());
            Assert.Equal("A, 1, B", constraint.Columns);
            Assert.Equal("HR.T", constraint.Reference);
            Assert.Equal(new[] { "ID", "NO" }, constraint.ReferenceColumns.ToArray());
            Assert.Null(constraint.Condition);
            Assert.Equal("CASCADE", constraint.DeleteRule);
            Assert.Null(constraint.IndexName);
        }

        [Fact]
        public void ReadColumn_TypeAndTrimmedDefault()
        {
            var record = Record(new Dictionary<string, object>
            {
                { "TABLE_NAME", "EMP" }, { "COLUMN_NAME", "NAME" }, { "DATA_TYPE", "VARCHAR2" }, { "DATA_LENGTH", 40m }, { "DATA_PRECISION", DBNull.Value },
                { "DATA_SCALE", DBNull.Value }, { "NULLABLE", "N" }, { "COLUMN_ID", 2m }, { "CHAR_USED", "C" }, { "CHAR_LENGTH", 10m },
                { "DATA_DEFAULT", "'없음'  \n" }, { "COMMENTS", "이름" }
            });
            var column = TableSpecQueries.ReadColumn(record, "SCOTT");

            Assert.Equal("VARCHAR2(10 CHAR)", column.Column.TypeLabel);
            Assert.False(column.Column.Nullable);
            Assert.Equal(2, column.Column.Position);
            Assert.Equal("'없음'", column.Default);
            Assert.Equal("이름", column.Comment);
        }

        private static IDataRecord Record(Dictionary<string, object> values)
        {
            var table = new DataTable();
            foreach (var pair in values)
                table.Columns.Add(pair.Key, pair.Value is DBNull ? typeof(string) : pair.Value.GetType());
            table.Rows.Add(values.Values.ToArray());
            var reader = table.CreateDataReader();
            reader.Read();
            return reader;
        }

        // ---------- 변수 모델 ----------

        [Theory]
        [InlineData("VARCHAR2(10 CHAR)", "VARCHAR2", "10 CHAR")]
        [InlineData("NUMBER(7,2)", "NUMBER", "7,2")]
        [InlineData("TIMESTAMP(6) WITH TIME ZONE", "TIMESTAMP WITH TIME ZONE", "6")]
        [InlineData("DATE", "DATE", "")]
        [InlineData(null, "", "")]
        public void SplitType(string label, string type, string length)
        {
            string dataType;
            string len;
            TableSpecModel.SplitType(label, out dataType, out len);
            Assert.Equal(type, dataType);
            Assert.Equal(length, len);
        }

        [Fact]
        public void Context_ColumnsGetPkFkAndEmptyStrings()
        {
            var table = new TableDescription { Owner = "SCOTT", Name = "ORDER_ITEM", Type = "TABLE" };
            table.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "ORDER_ID", TypeLabel = "NUMBER", Position = 1, Nullable = false } });
            table.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "LINE", TypeLabel = "NUMBER(3)", Position = 2, Nullable = false } });
            table.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "NOTE", TypeLabel = "VARCHAR2(100)", Position = 3, Nullable = true } });
            var pk = new ConstraintDescription { Name = "PK_OI", Type = "P" };
            pk.ColumnNames.AddRange(new[] { "ORDER_ID", "LINE" });
            var fk = new ConstraintDescription { Name = "FK_OI", Type = "R", Reference = "SCOTT.ORDERS" };
            fk.ColumnNames.Add("ORDER_ID");
            fk.ReferenceColumns.Add("ID");
            table.Constraints.Add(pk);
            table.Constraints.Add(fk);

            var context = TableSpecModel.Context(new TableSpecDocument { Title = " ", Date = new DateTime(2026, 1, 2) }, null, new[] { table });
            var render = new Func<string, string>(t => VelocityTemplate.Parse(t).Render(context));

            Assert.Equal("테이블 정의서|2026-01-02|SCOTT|1", render("$doc.title|$doc.date|$doc.schemas|$doc.tableCount"));
            Assert.Equal("PK_OI|ORDER_ID, LINE|1|ORDER_ITEM", render("#foreach($t in $tables)$t.pkName|$t.pkColumns|$t.no|$t.sheetName#end"));
            Assert.Equal("ORDER_ID:Y1Y:SCOTT.ORDERS.ID;LINE:Y2:;NOTE::;",
                render("#foreach($t in $tables)#foreach($c in $t.columns)$c.name:$c.pk$c.pkOrder$c.fk:$c.fkRef;#end#end"));
            // 값이 없는 것은 빈 글자(참조가 그대로 남지 않음)
            Assert.Equal("[][][]", render("#foreach($t in $tables)[$t.comment][$t.numRows][$t.tablespace]#end"));
            Assert.Equal("1", render("#foreach($t in $tables)$t.foreignKeys.size()#end"));
            Assert.Equal("[]", render("[$db.name]"));
        }

        [Fact]
        public void Context_SheetNamesUniqueAcrossSchemas()
        {
            var tables = new[] { new TableDescription { Owner = "A", Name = "EMP" }, new TableDescription { Owner = "B", Name = "EMP" } };
            var context = TableSpecModel.Context(null, null, tables);
            Assert.Equal("EMP,EMP (2)", VelocityTemplate.Parse("#foreach($t in $tables)$t.sheetName#if($foreach.hasNext),#end#end").Render(context));
        }

        [Fact]
        public void Variables_DocumentedNamesExistInContext()
        {
            // 안내 시트에 적은 변수(첫 이름)가 실제로 있는지: $doc·$db·$table·$col·$idx·$con
            var emp = new TableDescription { Owner = "S", Name = "T", Type = "TABLE" };
            emp.Columns.Add(new DescribedColumn { Column = new ColumnInfo { Name = "C", TypeLabel = "DATE", Position = 1 } });
            emp.Indexes.Add(new IndexDescription { Name = "I" });
            emp.Constraints.Add(new ConstraintDescription { Name = "K", Type = "C", Columns = "C", Condition = "C IN ('A')" });
            var context = TableSpecModel.Context(new TableSpecDocument(), new OracleConnectionProfile { Name = "n" }, new[] { emp });
            foreach (var variable in TableSpecModel.Variables)
            {
                foreach (var part in variable.Name.Split('/').Select(p => p.Trim()))
                {
                    var name = part.StartsWith("$") ? part : variable.Name.Substring(0, variable.Name.IndexOf('.') + 1) + part;
                    if (name.StartsWith("$foreach") || name == "$tables")
                        continue;
                    var template = Wrap(name);
                    var result = VelocityTemplate.Parse(template).Render(context);
                    Assert.False(result.Contains("$"), name + " 이(가) 모델에 없습니다: " + result);
                }
            }
        }

        // $col.name → #foreach($t in $tables)#foreach($col in $t.columns)$!{col.name}...처럼 감싸고, 값이 없으면 "$"가 남게 ${…}로 쓴다
        private static string Wrap(string name)
        {
            var path = name.Substring(1);
            var head = path.Split('.')[0];
            string loop;
            switch (head)
            {
                case "table": loop = "#foreach($table in $tables){0}#end"; break;
                case "col": loop = "#foreach($table in $tables)#foreach($col in $table.columns){0}#end#end"; break;
                case "idx": loop = "#foreach($table in $tables)#foreach($idx in $table.indexes){0}#end#end"; break;
                case "con": loop = "#foreach($table in $tables)#foreach($con in $table.constraints){0}#end#end"; break;
                case "code": loop = "#foreach($code in $codes){0}#end"; break;
                default: loop = "{0}"; break;
            }
            return loop.Replace("{0}", "${" + path + "}");
        }
    }
}
