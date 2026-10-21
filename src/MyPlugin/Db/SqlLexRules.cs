using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPlugin
{
    /// <summary>
    /// DBMS별 구문 규칙(<see cref="SqlScript"/>의 문장 나누기와 구문 강조가 함께 따른다 — 화면에 칠한 것과 실행할 때 나누는 것이 같게).
    /// 스캐너는 하나이고 DBMS마다 다른 것은 "이 문법을 아는가"뿐이라 규칙을 데이터로 둔다. 공급자가 <see cref="IDbProvider.Lex"/>로 내놓는다.
    /// 공통: '…'(''), N'…', "식별자", -- 줄 주석, /* */ 블록 주석, ';'·빈 줄로 끝나는 문장.
    /// </summary>
    public sealed class SqlLexRules
    {
        /// <summary>지금까지의 규칙 그대로: q'[…]'·nq'[…]', PL/SQL 블록은 '/' 줄까지, EXEC p → BEGIN p; END;. 규칙을 안 주면 이것.</summary>
        public static readonly SqlLexRules Oracle = new SqlLexRules
        {
            Name = "Oracle",
            QQuote = true,
            Blocks = true,
            BlockEndLine = "/",
            ExecAsPlSqlBlock = true,
            Keywords = SqlKeywords.Of(SqlKeywords.Common, SqlKeywords.Oracle),
            Types = SqlKeywords.Of(SqlKeywords.CommonTypes, SqlKeywords.OracleTypes)
        };

        /// <summary>
        /// $$…$$·$태그$…$태그$(함수·DO 본문), E'…'(\ 이스케이프), 중첩 블록 주석.
        /// PL/SQL 같은 블록이 없다 — 본문은 $$ 문자열이라 ';'로 끝난다(BEGIN;은 트랜잭션 시작). '/' 줄은 구분자가 아니다.
        /// </summary>
        public static readonly SqlLexRules Postgres = new SqlLexRules
        {
            Name = "PostgreSQL",
            DollarQuote = true,
            EscapeString = true,
            NestedComments = true,
            Keywords = SqlKeywords.Of(SqlKeywords.Common, SqlKeywords.Postgres),
            Types = SqlKeywords.Of(SqlKeywords.CommonTypes, SqlKeywords.PostgresTypes)
        };

        /// <summary>
        /// [식별자](]]는 ] 한 글자), 배치 구분 GO 줄(대소문자 무시, 줄에 GO만). CREATE PROCEDURE·BEGIN … END·DECLARE 블록은 GO 줄까지.
        /// BEGIN TRAN[SACTION]·BEGIN DISTRIBUTED는 블록이 아니다. '/' 줄은 구분자가 아니다. GO n(반복)은 구분자로 보지 않는다.
        /// </summary>
        public static readonly SqlLexRules SqlServer = new SqlLexRules
        {
            Name = "SQL Server",
            BracketIdentifier = true,
            Blocks = true,
            BlockEndLine = "GO",
            BeginTranIsStatement = true,
            Keywords = SqlKeywords.Of(SqlKeywords.Common, SqlKeywords.SqlServer),
            Types = SqlKeywords.Of(SqlKeywords.CommonTypes, SqlKeywords.SqlServerTypes)
        };

        private SqlLexRules() { }

        public string Name { get; private set; }

        /// <summary>Oracle 대체 따옴표 q'[…]'·nq'[…]'.</summary>
        public bool QQuote { get; private set; }

        /// <summary>PostgreSQL 달러 따옴표 $$…$$·$태그$…$태그$.</summary>
        public bool DollarQuote { get; private set; }

        /// <summary>PostgreSQL E'…'(\'·\\ 이스케이프).</summary>
        public bool EscapeString { get; private set; }

        /// <summary>블록 주석 중첩 /* /* */ */(PostgreSQL).</summary>
        public bool NestedComments { get; private set; }

        /// <summary>SQL Server [식별자].</summary>
        public bool BracketIdentifier { get; private set; }

        /// <summary>BEGIN·DECLARE·CREATE PROCEDURE 등을 블록으로 보고 안의 ';'·빈 줄로 끝내지 않는다(<see cref="BlockEndLine"/> 줄까지).</summary>
        public bool Blocks { get; private set; }

        /// <summary>문장(블록 포함)을 끝내는, 그것만 있는 줄: Oracle "/", SQL Server "GO". 없으면 null.</summary>
        public string BlockEndLine { get; private set; }

        /// <summary>BEGIN TRAN·BEGIN TRANSACTION·BEGIN DISTRIBUTED는 블록이 아닌 문장(SQL Server).</summary>
        public bool BeginTranIsStatement { get; private set; }

        /// <summary>EXEC p(…) → BEGIN p(…); END;(SQL*Plus처럼, Oracle).</summary>
        public bool ExecAsPlSqlBlock { get; private set; }

        /// <summary>구문 강조 키워드: 공통 SQL(문장·절·조건·DDL·트랜잭션·블록) + 이 DBMS 것(Oracle MINUS, PostgreSQL LIMIT·RETURNING, SQL Server TOP·GO …).</summary>
        public SqlKeywords Keywords { get; private set; }

        /// <summary>구문 강조 자료형(키워드와 따로 칠할 수 있게): 공통 + 이 DBMS 것(Oracle NUMBER·VARCHAR2, PostgreSQL TEXT·JSONB, SQL Server NVARCHAR·DATETIME2 …).</summary>
        public SqlKeywords Types { get; private set; }

        /// <summary>
        /// 강조용 사본: 키워드에 add를 더하고 키워드·자료형에서 remove를 뺀다(대소문자 무시 — 설정의 "더 칠할 낱말"·"칠하지 않을 낱말").
        /// 문장 나누기 규칙은 그대로이고 이름도 같다. 더할 것·뺄 것이 없으면 자신을 돌려준다.
        /// </summary>
        public SqlLexRules WithKeywords(IEnumerable<string> add, IEnumerable<string> remove)
        {
            var added = (add ?? Enumerable.Empty<string>()).ToList();
            var removed = new HashSet<string>(remove ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (added.Count == 0 && removed.Count == 0)
                return this;
            var copy = (SqlLexRules)MemberwiseClone();
            copy.Keywords = new SqlKeywords((Keywords == null ? Enumerable.Empty<string>() : Keywords.Words).Concat(added).Where(w => !removed.Contains(w)));
            copy.Types = new SqlKeywords((Types == null ? Enumerable.Empty<string>() : Types.Words).Where(w => !removed.Contains(w)));
            return copy;
        }
    }
}
