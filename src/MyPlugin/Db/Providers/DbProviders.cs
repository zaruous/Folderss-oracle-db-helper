using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace MyPlugin
{
    /// <summary>공급자 등록·조회. 새 DBMS는 여기 <see cref="All"/>에 한 줄 더한다.</summary>
    internal static class DbProviders
    {
        public static readonly IDbProvider Oracle = new OracleProvider();
        public static readonly IDbProvider Postgres = new PostgresProvider();
        public static readonly IDbProvider SqlServer = new SqlServerProvider();
        /// <summary>시험(SQLite 등)·모르는 연결용: 방언 Generic, 데이터 사전 없음.</summary>
        public static readonly IDbProvider Generic = new GenericProvider();

        /// <summary>접속 관리 화면의 공급자 목록 순서(맨 앞이 기본).</summary>
        public static readonly IReadOnlyList<IDbProvider> All = new[] { Oracle, Postgres, SqlServer };

        public static IDbProvider Default
        {
            get { return Oracle; }
        }

        /// <summary>저장 값(provider)으로 찾는다. 비었거나 모르는 값이면 기본(Oracle) — 이전 설정 JSON에는 provider가 없다.</summary>
        public static IDbProvider For(string kind)
        {
            var key = (kind ?? "").Trim();
            return All.FirstOrDefault(p => string.Equals(p.Kind, key, StringComparison.OrdinalIgnoreCase)) ?? Default;
        }

        public static IDbProvider For(OracleConnectionProfile profile)
        {
            return For(profile == null ? null : profile.Provider);
        }

        /// <summary>연결 객체 형식으로 찾는다(세션이 공급자 없이 만들어졌을 때): OracleConnection → Oracle, NpgsqlConnection → PostgreSQL, 그 밖 → Generic.</summary>
        public static IDbProvider For(DbConnection connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (connection is OracleConnection)
                return Oracle;
            if (connection is NpgsqlConnection)
                return Postgres;
            if (connection is SqlConnection)
                return SqlServer;
            return Generic;
        }

        /// <summary>표시용 "Oracle"·"PostgreSQL". 프로필이 null이면 기본 공급자 이름.</summary>
        public static string DisplayNameOf(OracleConnectionProfile profile)
        {
            return For(profile).DisplayName;
        }
    }
}
