using System.Collections.Generic;
using System.Linq;
using MyPlugin;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>뷰·코드 객체 소스 보기(트리 [소스 보기]): 대상 종류, 공급자별 조회 SQL, 조회 결과를 SQL 탭에 넣을 글로 잇기.</summary>
    public class ObjectSourceTests
    {
        private static List<SourcePart> Parts(params string[] partAndText)
        {
            var parts = new List<SourcePart>();
            for (var i = 0; i < partAndText.Length; i += 2)
                parts.Add(new SourcePart { Part = partAndText[i], Text = partAndText[i + 1] });
            return parts;
        }

        private static object Param(SqlQuery query, string name)
        {
            return query.Parameters.Single(p => p.Key == name).Value;
        }

        [Fact]
        public void Supports_ViewAndCodeOnly()
        {
            Assert.True(ObjectSource.Supports("VIEW"));
            Assert.True(ObjectSource.Supports("PROCEDURE"));
            Assert.True(ObjectSource.Supports("FUNCTION"));
            Assert.True(ObjectSource.Supports("PACKAGE"));
            Assert.False(ObjectSource.Supports("TABLE"));
            Assert.False(ObjectSource.Supports("SEQUENCE"));
            Assert.False(ObjectSource.Supports(null));
        }

        // ---------- Oracle ----------

        [Fact]
        public void Oracle_Query_ViewFromAllViews_CodeFromAllSource()
        {
            var meta = DbProviders.Oracle.Metadata;
            var view = meta.Source("HR", "VIEW", "EMP_V");
            Assert.Contains("ALL_VIEWS", view.Sql);
            Assert.Equal("HR", Param(view, "owner"));
            Assert.Equal("EMP_V", Param(view, "name"));

            var proc = meta.Source("HR", "PROCEDURE", "P1");
            Assert.Contains("ALL_SOURCE", proc.Sql);
            Assert.Contains("ORDER BY", proc.Sql);
            Assert.Equal("PROCEDURE", Param(proc, "type"));
            Assert.DoesNotContain("PACKAGE BODY", proc.Sql);

            // 패키지는 명세 다음 본문
            var pkg = meta.Source("HR", "PACKAGE", "PK");
            Assert.Contains("'PACKAGE', 'PACKAGE BODY'", pkg.Sql);
        }

        [Fact]
        public void Oracle_Build_View_CreateOrReplaceWithQuotedName()
        {
            var meta = DbProviders.Oracle.Metadata;
            Assert.Equal("CREATE OR REPLACE VIEW HR.EMP_V AS\nselect id, name from emp\n;\n",
                meta.BuildSource("HR", "VIEW", "EMP_V", Parts("VIEW", "select id, name from emp\n  ")));
            // 소문자·예약어 이름은 따옴표
            Assert.StartsWith("CREATE OR REPLACE VIEW \"hr\".\"Emp V\" AS\n",
                meta.BuildSource("hr", "VIEW", "Emp V", Parts("VIEW", "select 1 from dual")));
        }

        [Fact]
        public void Oracle_Build_Code_LinesJoined_SlashAfterEachPart()
        {
            var meta = DbProviders.Oracle.Metadata;
            var proc = meta.BuildSource("HR", "PROCEDURE", "P1", Parts(
                "PROCEDURE", "PROCEDURE p1 IS\n",
                "PROCEDURE", "BEGIN\n",
                "PROCEDURE", "  NULL;\n",
                "PROCEDURE", "END;\n"));
            Assert.Equal("CREATE OR REPLACE PROCEDURE p1 IS\nBEGIN\n  NULL;\nEND;\n/\n", proc);

            var pkg = meta.BuildSource("HR", "PACKAGE", "PK", Parts(
                "PACKAGE", "PACKAGE pk IS\n",
                "PACKAGE", "  PROCEDURE a;\n",
                "PACKAGE", "END;\n",
                "PACKAGE BODY", "PACKAGE BODY pk IS\n",
                "PACKAGE BODY", "  PROCEDURE a IS BEGIN NULL; END;\n",
                "PACKAGE BODY", "END;\n"));
            Assert.Equal("CREATE OR REPLACE PACKAGE pk IS\n  PROCEDURE a;\nEND;\n/\n\n"
                + "CREATE OR REPLACE PACKAGE BODY pk IS\n  PROCEDURE a IS BEGIN NULL; END;\nEND;\n/\n", pkg);

            // SQL 탭에서 실행하면 명세·본문 두 문장으로 나뉜다('/' 줄)
            Assert.Equal(2, SqlScript.Split(pkg).Count);
        }

        [Fact]
        public void Build_NoRowsOrEmptyText_IsNull()
        {
            Assert.Null(DbProviders.Oracle.Metadata.BuildSource("HR", "PROCEDURE", "P1", Parts()));
            Assert.Null(DbProviders.Oracle.Metadata.BuildSource("HR", "VIEW", "V", Parts("VIEW", null)));
            Assert.Null(DbProviders.Postgres.Metadata.BuildSource("s", "FUNCTION", "f", Parts("FUNCTION", "  ")));
            // SQL Server 암호화 객체: definition이 NULL
            Assert.Null(DbProviders.SqlServer.Metadata.BuildSource("dbo", "PROCEDURE", "p", Parts("SQL_STORED_PROCEDURE", null)));
        }

        // ---------- PostgreSQL ----------

        [Fact]
        public void Postgres_Query_ViewdefAndFunctiondef_FilteredByKind()
        {
            var meta = DbProviders.Postgres.Metadata;
            var view = meta.Source("public", "VIEW", "v1");
            Assert.Contains("pg_get_viewdef", view.Sql);
            Assert.Equal("public", Param(view, "owner"));
            Assert.Equal("v1", Param(view, "name"));

            var func = meta.Source("public", "FUNCTION", "f1");
            Assert.Contains("pg_get_functiondef", func.Sql);
            Assert.Contains("p.prokind = 'f'", func.Sql);
            Assert.Contains("p.prokind = 'p'", meta.Source("public", "PROCEDURE", "p1").Sql);
        }

        [Fact]
        public void Postgres_Build_OverloadsSeparated_SemicolonEnsured()
        {
            var text = DbProviders.Postgres.Metadata.BuildSource("public", "FUNCTION", "f", Parts(
                "FUNCTION", "CREATE OR REPLACE FUNCTION public.f(a integer)\n RETURNS integer\n LANGUAGE sql\nAS $function$select a$function$\n",
                "FUNCTION", "CREATE OR REPLACE FUNCTION public.f(a text)\n RETURNS text\n LANGUAGE sql\nAS $function$select a$function$\n"));
            Assert.Equal("CREATE OR REPLACE FUNCTION public.f(a integer)\n RETURNS integer\n LANGUAGE sql\nAS $function$select a$function$;\n\n"
                + "CREATE OR REPLACE FUNCTION public.f(a text)\n RETURNS text\n LANGUAGE sql\nAS $function$select a$function$;\n", text);
            // pg_get_viewdef 결과는 이미 ;로 끝난다
            Assert.Equal("CREATE OR REPLACE VIEW public.v AS\n SELECT 1;\n",
                DbProviders.Postgres.Metadata.BuildSource("public", "VIEW", "v", Parts("VIEW", "CREATE OR REPLACE VIEW public.v AS\n SELECT 1;")));
        }

        // ---------- SQL Server ----------

        [Fact]
        public void SqlServer_Query_SqlModules()
        {
            var query = DbProviders.SqlServer.Metadata.Source("dbo", "PROCEDURE", "usp_a");
            Assert.Contains("sys.sql_modules", query.Sql);
            Assert.Equal("dbo", Param(query, "owner"));
            Assert.Equal("usp_a", Param(query, "name"));
        }

        [Fact]
        public void SqlServer_Build_DefinitionAsWritten()
        {
            Assert.Equal("CREATE VIEW dbo.v AS SELECT 1 AS a\n",
                DbProviders.SqlServer.Metadata.BuildSource("dbo", "VIEW", "v", Parts("VIEW", "\r\nCREATE VIEW dbo.v AS SELECT 1 AS a\r\n")).Replace("\r\n", "\n"));
        }
    }
}
