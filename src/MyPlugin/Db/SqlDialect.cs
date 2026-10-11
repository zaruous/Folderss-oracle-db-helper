using System;
using System.Data.Common;
using Oracle.ManagedDataAccess.Client;

namespace MyPlugin
{
    /// <summary>
    /// DBMS마다 다른 SQL 조각(방언). 사용자 SQL을 그대로 실행하는 기능(GCM 정의서 등)이 DBMS 고유 문법을 코드에 박지 않게 여기로 모은다.
    /// 데이터 사전 조회(ALL_* 뷰 등)는 여기가 아니라 메타데이터 공급자의 몫이다.
    /// </summary>
    internal interface ISqlDialect
    {
        /// <summary>표시·로그용 이름("Oracle", "Generic").</summary>
        string Name { get; }

        /// <summary>SELECT 문 sql을 인라인 뷰로 감싸 한 행만 읽는 문장(열 확인용 — 큰 조회가 다 돌지 않게). sql은 끝의 ';'를 뗀 것이어야 한다.</summary>
        string LimitOne(string sql);
    }

    /// <summary>Oracle: ROWNUM(11g에는 FETCH FIRST가 없다). 인라인 뷰 안의 ORDER BY는 그대로 된다.</summary>
    internal sealed class OracleDialect : ISqlDialect
    {
        public string Name { get { return "Oracle"; } }

        public string LimitOne(string sql)
        {
            return "SELECT * FROM (\n" + sql + "\n) WHERE ROWNUM <= 1";
        }
    }

    /// <summary>
    /// 일반 방언: LIMIT 1(SQLite·PostgreSQL·MySQL). 인라인 뷰에 별칭(q)을 붙인다 — PostgreSQL·MySQL은 별칭이 없으면 오류.
    /// MSSQL(TOP 1)은 별도 방언이 필요하다. 시험(SQLite)에서 쓴다.
    /// </summary>
    internal sealed class GenericDialect : ISqlDialect
    {
        public string Name { get { return "Generic"; } }

        public string LimitOne(string sql)
        {
            return "SELECT * FROM (\n" + sql + "\n) AS q LIMIT 1";
        }
    }

    /// <summary>
    /// SQL Server: 파생 테이블 안의 ORDER BY는 TOP 없이는 오류라 감싸지 않고, 배치 앞뒤에 SET ROWCOUNT 1 / 0을 둔다
    /// (SELECT가 첫 행에서 멈추고, 결과 집합은 사용자 SQL 하나뿐이라 열 이름을 그대로 읽는다).
    /// </summary>
    internal sealed class SqlServerDialect : ISqlDialect
    {
        public string Name { get { return "SQL Server"; } }

        public string LimitOne(string sql)
        {
            return "SET ROWCOUNT 1;\n" + sql + ";\nSET ROWCOUNT 0;";
        }
    }

    /// <summary>방언 인스턴스와 연결 형식 → 방언 대응.</summary>
    internal static class SqlDialects
    {
        public static readonly ISqlDialect Oracle = new OracleDialect();
        public static readonly ISqlDialect Generic = new GenericDialect();
        public static readonly ISqlDialect SqlServer = new SqlServerDialect();

        /// <summary>연결 형식으로 방언을 고른다: OracleConnection이면 Oracle, 그 밖(SQLite 시험 등)은 Generic.</summary>
        public static ISqlDialect For(DbConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            return connection is OracleConnection ? Oracle : Generic;
        }
    }
}
