using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using Npgsql;

namespace MyPlugin
{
    /// <summary>
    /// PostgreSQL 공급자(Npgsql). 접속 프로필의 ServiceName은 데이터베이스 이름이다.
    /// 지원: 접속, 트리(스키마·테이블·뷰·시퀀스·함수/프로시저·열), 검색, SQL 실행(세션의 일반 경로), GCM 정의서.
    /// 아직 아님: 테이블 정보(F4)·테이블 정의서(ALL_* 기반) — <see cref="IMetadataProvider.SupportsTableInfo"/> false.
    /// </summary>
    internal sealed class PostgresProvider : IDbProvider
    {
        public const string KindName = "postgres";

        private readonly IMetadataProvider _metadata = new PostgresMetadata();

        public string Kind { get { return KindName; } }
        public string DisplayName { get { return "PostgreSQL"; } }
        public int DefaultPort { get { return 5432; } }
        public string ServiceLabel { get { return "데이터베이스"; } }
        public ISqlDialect Dialect { get { return SqlDialects.Generic; } }
        public IMetadataProvider Metadata { get { return _metadata; } }

        public List<string> Validate(OracleConnectionProfile profile, string label)
        {
            return ProviderRules.ValidateHostPortServiceUser(profile, label, ServiceLabel);
        }

        public string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = profile.Host.Trim(),
                Port = profile.Port,
                Database = profile.ServiceName.Trim(),
                Username = profile.UserId.Trim(),
                Password = password,
                Timeout = timeoutSeconds,
                Pooling = false,
                // 시간 제한 없음(사용자가 취소한다) — Oracle 세션의 CommandTimeout = 0과 같은 뜻
                CommandTimeout = 0
            };
            return builder.ConnectionString;
        }

        public DbConnection CreateConnection(string connectionString)
        {
            return new NpgsqlConnection(connectionString);
        }

        public void ConfigureProcess()
        {
        }

        public string ServerVersionOf(DbConnection connection)
        {
            return connection.ServerVersion;
        }

        public string Address(OracleConnectionProfile profile)
        {
            return ProviderRules.Address(profile);
        }

        /// <summary>Npgsql은 연결당 명령 하나 — 열린 커서가 있으면 다른 명령이 "A command is already in progress"로 실패한다.</summary>
        public bool SupportsMultipleOpenReaders { get { return false; } }

        /// <summary>57014 query_canceled.</summary>
        public bool IsCancellation(Exception ex)
        {
            var pg = ex as PostgresException;
            return pg != null && pg.SqlState == PostgresErrorCodes.QueryCanceled;
        }

        /// <summary>08*** 연결 예외, 57P01~03 서버 종료·크래시, 또는 Npgsql이 끊긴 소켓을 알린 경우.</summary>
        public bool IsBroken(Exception ex)
        {
            var pg = ex as PostgresException;
            if (pg != null)
            {
                var state = pg.SqlState ?? "";
                return state.StartsWith("08", StringComparison.Ordinal) || state == PostgresErrorCodes.AdminShutdown
                    || state == PostgresErrorCodes.CrashShutdown || state == PostgresErrorCodes.CannotConnectNow;
            }
            var npgsql = ex as NpgsqlException;
            return npgsql != null && (npgsql.InnerException is System.IO.IOException || npgsql.InnerException is System.Net.Sockets.SocketException);
        }
    }
}
