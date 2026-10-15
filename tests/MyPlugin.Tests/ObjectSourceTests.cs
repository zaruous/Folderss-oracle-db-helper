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

        private static List<SourcePart> Sequence(params string[] keyAndValue)
        {
            var part = new SourcePart { Part = "SEQUENCE" };
            for (var i = 0; i < keyAndValue.Length; i += 2)
                part.Fields[keyAndValue[i]] = keyAndValue[i + 1];
            return new List<SourcePart> { part };
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
            Assert.True(ObjectSource.Supports("SEQUENCE"));
            Assert.False(ObjectSource.Supports("TABLE"));
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

        [Fact]
        public void Oracle_Sequence_FromAllSequences_StartWithLastNumber()
        {
            var meta = DbProviders.Oracle.Metadata;
            var query = meta.Source("HR", "SEQUENCE", "SEQ_A");
            Assert.Contains("ALL_SEQUENCES", query.Sql);
            Assert.Equal("SEQ_A", Param(query, "name"));

            Assert.Equal("-- START WITH는 지금의 LAST_NUMBER(다음에 나올 번호 근처)입니다. 처음 만들 때의 시작 값은 데이터 사전에 남지 않습니다.\n"
                + "CREATE SEQUENCE HR.SEQ_A\n  START WITH 21\n  INCREMENT BY 1\n  MINVALUE 1\n  MAXVALUE 9999999999999999999999999999\n  CACHE 20\n  NOCYCLE\n  NOORDER;\n",
                meta.BuildSource("HR", "SEQUENCE", "SEQ_A", Sequence("LAST_VALUE", "21", "INCREMENT_BY", "1", "MIN_VALUE", "1",
                    "MAX_VALUE", "9999999999999999999999999999", "CACHE_SIZE", "20", "CYCLE_FLAG", "N", "ORDER_FLAG", "N")));
            // 캐시 0 → NOCACHE, 순환·순서 Y
            var text = meta.BuildSource("HR", "SEQUENCE", "SEQ_A", Sequence("LAST_VALUE", "-5", "INCREMENT_BY", "-1", "MIN_VALUE", "-100",
                "MAX_VALUE", "-1", "CACHE_SIZE", "0", "CYCLE_FLAG", "Y", "ORDER_FLAG", "Y"));
            Assert.Contains("\n  NOCACHE\n  CYCLE\n  ORDER;\n", text);
            Assert.Contains("START WITH -5\n  INCREMENT BY -1\n", text);
            // 행이 없으면(권한 없음·없는 시퀀스) null
            Assert.Null(meta.BuildSource("HR", "SEQUENCE", "SEQ_A", Parts()));
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

        [Fact]
        public void Postgres_Sequence_FromPgSequences()
        {
            var meta = DbProviders.Postgres.Metadata;
            Assert.Contains("pg_sequences", meta.Source("public", "SEQUENCE", "s").Sql);
            Assert.Equal("-- 마지막 값(last_value): 7\n"
                + "CREATE SEQUENCE public.\"Seq A\"\n  AS bigint\n  START WITH 1\n  INCREMENT BY 1\n  MINVALUE 1\n  MAXVALUE 9223372036854775807\n  CACHE 1\n  NO CYCLE;\n",
                meta.BuildSource("public", "SEQUENCE", "Seq A", Sequence("QNAME", "public.\"Seq A\"", "TYPE_NAME", "bigint", "START_VALUE", "1",
                    "INCREMENT_BY", "1", "MIN_VALUE", "1", "MAX_VALUE", "9223372036854775807", "CACHE_SIZE", "1", "CYCLE_FLAG", "N", "LAST_VALUE", "7")));
            // nextval을 한 번도 안 했으면 last_value가 NULL
            Assert.StartsWith("-- 마지막 값(last_value): 없음(아직 nextval 전이거나 볼 권한 없음)\n",
                meta.BuildSource("public", "SEQUENCE", "s", Sequence("QNAME", "public.s", "TYPE_NAME", "integer", "START_VALUE", "1",
                    "INCREMENT_BY", "1", "MIN_VALUE", "1", "MAX_VALUE", "2147483647", "CACHE_SIZE", "1", "CYCLE_FLAG", "Y", "LAST_VALUE", null)));
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

        [Fact]
        public void SqlServer_Sequence_FromSysSequences()
        {
            var meta = DbProviders.SqlServer.Metadata;
            Assert.Contains("sys.sequences", meta.Source("dbo", "SEQUENCE", "s").Sql);
            Assert.Equal("-- 현재 값(current_value): 41\n"
                + "CREATE SEQUENCE [dbo].[s]\n  AS bigint\n  START WITH 1\n  INCREMENT BY 1\n  MINVALUE 1\n  MAXVALUE 9223372036854775807\n  NO CYCLE\n  CACHE 50;\n",
                meta.BuildSource("dbo", "SEQUENCE", "s", Sequence("QNAME", "[dbo].[s]", "TYPE_NAME", "bigint", "START_VALUE", "1", "INCREMENT_BY", "1",
                    "MIN_VALUE", "1", "MAX_VALUE", "9223372036854775807", "CYCLE_FLAG", "N", "CACHE_FLAG", "Y", "CACHE_SIZE", "50", "LAST_VALUE", "41")));
            // 캐시 크기 기본값(NULL)은 CACHE만, 캐시 안 함은 NO CACHE
            Assert.Contains("\n  CACHE;\n", meta.BuildSource("dbo", "SEQUENCE", "s", Sequence("QNAME", "[dbo].[s]", "TYPE_NAME", "int", "START_VALUE", "1",
                "INCREMENT_BY", "1", "MIN_VALUE", "1", "MAX_VALUE", "10", "CYCLE_FLAG", "Y", "CACHE_FLAG", "Y", "CACHE_SIZE", null, "LAST_VALUE", "1")));
            Assert.Contains("\n  CYCLE\n  NO CACHE;\n", meta.BuildSource("dbo", "SEQUENCE", "s", Sequence("QNAME", "[dbo].[s]", "TYPE_NAME", "int", "START_VALUE", "1",
                "INCREMENT_BY", "1", "MIN_VALUE", "1", "MAX_VALUE", "10", "CYCLE_FLAG", "Y", "CACHE_FLAG", "N", "CACHE_SIZE", null, "LAST_VALUE", "1")));
        }
    }
}
