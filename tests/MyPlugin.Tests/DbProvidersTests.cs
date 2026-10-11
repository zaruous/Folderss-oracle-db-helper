using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using MyPlugin;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Xunit;

namespace MyPlugin.Tests
{
    /// <summary>공급자 등록·조회, 접속 프로필의 provider 필드(이전 JSON 호환·복사·비교), 공급자별 검증·연결 문자열·주소, PostgreSQL 데이터 사전 SQL.</summary>
    public class DbProvidersTests
    {
        private static OracleConnectionProfile Profile(string provider = null)
        {
            return new OracleConnectionProfile { Id = "p1", Name = "운영", Provider = provider ?? "oracle", Host = "db.local", Port = 1521, ServiceName = "ORCL", UserId = "scott" };
        }

        // ---------- 등록·조회 ----------

        [Fact]
        public void For_KindName_DefaultsToOracle_IgnoresCase()
        {
            Assert.Same(DbProviders.Oracle, DbProviders.For("oracle"));
            Assert.Same(DbProviders.Postgres, DbProviders.For("Postgres"));
            Assert.Same(DbProviders.Oracle, DbProviders.For((string)null));
            Assert.Same(DbProviders.Oracle, DbProviders.For(""));
            Assert.Same(DbProviders.Oracle, DbProviders.For("db2"));
            Assert.Same(DbProviders.Oracle, DbProviders.For((OracleConnectionProfile)null));
            Assert.Same(DbProviders.Postgres, DbProviders.For(Profile("postgres")));
            Assert.Equal(new[] { "oracle", "postgres", "mssql" }, DbProviders.All.Select(p => p.Kind).ToArray());
            Assert.Same(DbProviders.SqlServer, DbProviders.For("MSSQL"));
        }

        [Fact]
        public void For_Connection_ByType()
        {
            Assert.Same(DbProviders.Oracle, DbProviders.For(new OracleConnection()));
            Assert.Same(DbProviders.Postgres, DbProviders.For(new NpgsqlConnection()));
            Assert.Same(DbProviders.SqlServer, DbProviders.For(new Microsoft.Data.SqlClient.SqlConnection()));
            Assert.Same(DbProviders.Generic, DbProviders.For(new SqliteConnection("Data Source=:memory:")));
            Assert.Throws<ArgumentNullException>(() => DbProviders.For((System.Data.Common.DbConnection)null));
        }

        [Fact]
        public void Providers_BasicFacts()
        {
            Assert.Equal("Oracle", DbProviders.Oracle.DisplayName);
            Assert.Equal(1521, DbProviders.Oracle.DefaultPort);
            Assert.Equal("서비스명", DbProviders.Oracle.ServiceLabel);
            Assert.Same(SqlDialects.Oracle, DbProviders.Oracle.Dialect);
            Assert.True(DbProviders.Oracle.Metadata.SupportsTableInfo);
            Assert.True(DbProviders.Oracle.Metadata.HasMaintainedFlagFallback);

            Assert.Equal("PostgreSQL", DbProviders.Postgres.DisplayName);
            Assert.Equal(5432, DbProviders.Postgres.DefaultPort);
            Assert.Equal("데이터베이스", DbProviders.Postgres.ServiceLabel);
            Assert.Same(SqlDialects.Generic, DbProviders.Postgres.Dialect);
            Assert.False(DbProviders.Postgres.Metadata.SupportsTableInfo);
            Assert.False(DbProviders.Postgres.Metadata.HasMaintainedFlagFallback);

            Assert.Throws<NotSupportedException>(() => DbProviders.Generic.Metadata);
        }

        // ---------- 프로필: provider 필드 ----------

        [Fact]
        public void Deserialize_WithoutProvider_IsOracle_AndUnknownBecomesOracle()
        {
            var old = OracleConnectionStore.Deserialize("[{\"name\":\"운영\",\"host\":\"h\",\"port\":1521,\"serviceName\":\"S\",\"userId\":\"u\"}]");
            Assert.Equal("oracle", old[0].Provider);
            var odd = OracleConnectionStore.Deserialize("[{\"name\":\"x\",\"provider\":\"db2\",\"host\":\"h\",\"port\":1,\"serviceName\":\"S\",\"userId\":\"u\"}]");
            Assert.Equal("oracle", odd[0].Provider);
        }

        [Fact]
        public void Serialize_Postgres_RoundTrip_KeepsProvider()
        {
            var json = OracleConnectionStore.Serialize(new[] { Profile("postgres") });
            Assert.Contains("\"provider\":\"postgres\"", json);
            Assert.Contains("\"serviceName\":\"ORCL\"", json);
            var back = OracleConnectionStore.Deserialize(json);
            Assert.Equal("postgres", back[0].Provider);
        }

        [Fact]
        public void Copy_SameAs_IncludeProvider()
        {
            var a = Profile("postgres");
            a.ProtectedPassword = "AQ";
            a.Color = "red";
            var copy = a.Copy();
            Assert.NotSame(a, copy);
            Assert.True(a.SameAs(copy));
            Assert.Equal("postgres", copy.Provider);
            copy.Provider = "oracle";
            Assert.False(a.SameAs(copy));
            Assert.True(ShellLogic.ProfileChanged(copy, a));
            var other = a.Copy();
            other.Id = "p2";
            Assert.False(a.SameAs(other));
            Assert.True(a.SameAs(other, false));
            Assert.False(a.SameAs(null));
            Assert.Equal("postgres", ShellLogic.SessionCopy(a).Provider);
            Assert.Equal("postgres", ConnectionManagerLogic.Clone(a).Provider);
            Assert.True(ConnectionManagerLogic.SameProfile(a, ConnectionManagerLogic.Clone(a)));
        }

        // ---------- 검증·연결 문자열·주소 ----------

        [Fact]
        public void Validate_UsesProviderServiceLabel()
        {
            var oracle = OracleConnectionStore.Validate(new[] { new OracleConnectionProfile { Name = "x", Provider = "oracle", Port = 1521 } });
            Assert.Contains(oracle, e => e.Contains("서비스명"));
            var pg = OracleConnectionStore.Validate(new[] { new OracleConnectionProfile { Name = "x", Provider = "postgres", Port = 5432 } });
            Assert.Contains(pg, e => e.Contains("데이터베이스"));
            Assert.DoesNotContain(pg, e => e.Contains("서비스명"));
            Assert.Contains(pg, e => e.Contains("호스트"));
            Assert.Contains(pg, e => e.Contains("사용자"));
        }

        [Fact]
        public void BuildConnectionString_Postgres_UsesNpgsqlBuilder_QuotesPassword()
        {
            var p = Profile("postgres");
            p.Port = 5433;
            p.ServiceName = "mesdb";
            var cs = OracleConnectionStore.BuildConnectionString(p, "Pw;x=1'y", 7);
            var parsed = new NpgsqlConnectionStringBuilder(cs);
            Assert.Equal("db.local", parsed.Host);
            Assert.Equal(5433, parsed.Port);
            Assert.Equal("mesdb", parsed.Database);
            Assert.Equal("scott", parsed.Username);
            Assert.Equal("Pw;x=1'y", parsed.Password);
            Assert.Equal(7, parsed.Timeout);
            Assert.False(parsed.Pooling);
            Assert.Equal(0, parsed.CommandTimeout);
            Assert.IsType<NpgsqlConnection>(DbProviders.Postgres.CreateConnection(cs));
        }

        [Fact]
        public void BuildConnectionString_Oracle_Unchanged()
        {
            var cs = OracleConnectionStore.BuildConnectionString(Profile(), "pw", 10);
            var parsed = new OracleConnectionStringBuilder(cs);
            Assert.Equal("db.local:1521/ORCL", parsed.DataSource);
            Assert.Equal("scott", parsed.UserID);
            Assert.IsType<OracleConnection>(DbProviders.Oracle.CreateConnection(cs));
        }

        [Fact]
        public void Address_And_TestSuccess_WithProduct()
        {
            Assert.Equal("scott@db.local:1521/ORCL", ConnectionManagerLogic.Address(Profile("postgres")));
            Assert.Equal("접속 성공 · PostgreSQL 16.15 · 0.2초", ConnectionManagerLogic.TestSuccess("16.15", TimeSpan.FromSeconds(0.2), "PostgreSQL"));
            Assert.Equal("접속 성공 · Oracle 23.4 · 0.2초", ConnectionManagerLogic.TestSuccess("23.4", TimeSpan.FromSeconds(0.2)));
        }

        [Fact]
        public void ErrorClassification_Postgres()
        {
            Assert.True(DbProviders.Postgres.IsCancellation(new PostgresException("canceled", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)));
            Assert.False(DbProviders.Postgres.IsCancellation(new PostgresException("x", "ERROR", "ERROR", "42P01")));
            Assert.True(DbProviders.Postgres.IsBroken(new PostgresException("x", "FATAL", "FATAL", "08006")));
            Assert.True(DbProviders.Postgres.IsBroken(new PostgresException("x", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown)));
            Assert.False(DbProviders.Postgres.IsBroken(new PostgresException("x", "ERROR", "ERROR", "42P01")));
            // 세션의 정적 판정도 공급자를 묻는다
            Assert.True(DbSession.IsCancellation(new PostgresException("canceled", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)));
            Assert.Equal("실행을 취소했습니다.", DbSession.DescribeError(new PostgresException("canceled", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled)));
            Assert.StartsWith("DB 연결이 끊겼습니다.", DbSession.DescribeError(new PostgresException("x", "FATAL", "FATAL", "08006")));
        }

        // ---------- SQL Server ----------

        [Fact]
        public void SqlServer_BasicFacts_ConnectionString_Dialect()
        {
            var p = DbProviders.SqlServer;
            Assert.Equal("SQL Server", p.DisplayName);
            Assert.Equal(1433, p.DefaultPort);
            Assert.Equal("데이터베이스", p.ServiceLabel);
            Assert.False(p.Metadata.SupportsTableInfo);
            Assert.False(p.Metadata.HasMaintainedFlagFallback);

            var profile = Profile("mssql");
            profile.Port = 1433;
            profile.ServiceName = "MESDB";
            var cs = OracleConnectionStore.BuildConnectionString(profile, "Pw;x=1'y", 9);
            var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
            Assert.Equal("db.local,1433", parsed.DataSource);
            Assert.Equal("MESDB", parsed.InitialCatalog);
            Assert.Equal("scott", parsed.UserID);
            Assert.Equal("Pw;x=1'y", parsed.Password);
            Assert.Equal(9, parsed.ConnectTimeout);
            Assert.False(parsed.Pooling);
            Assert.True(parsed.TrustServerCertificate);
            Assert.Equal(0, parsed.CommandTimeout);
            Assert.IsType<Microsoft.Data.SqlClient.SqlConnection>(p.CreateConnection(cs));

            Assert.Equal("SET ROWCOUNT 1;\nSELECT a AS TABLE_NAME FROM t ORDER BY a;\nSET ROWCOUNT 0;", CodeSpecQueries.ProbeSql("SELECT a AS TABLE_NAME FROM t ORDER BY a;", p.Dialect));
            Assert.Equal("SQL Server", p.Dialect.Name);
        }

        [Fact]
        public void SqlServer_MetadataSql()
        {
            var m = DbProviders.SqlServer.Metadata;
            Assert.Equal("SELECT SCHEMA_NAME()", m.CurrentSchema().Sql);
            Assert.Contains("sys.schemas", m.Schemas(false).Sql);
            var counts = m.GroupCounts("dbo");
            Assert.Equal("dbo", Param(counts, "owner"));
            Assert.Contains("@owner", counts.Sql);
            Assert.Contains("AS CNT", counts.Sql);
            var tables = m.Objects("dbo", TreeGroups.Table, 10);
            Assert.StartsWith("SELECT TOP (11) ", tables.Sql);
            Assert.Contains("('U')", tables.Sql);
            Assert.Contains("sys.partitions", tables.Sql);
            Assert.DoesNotContain("TOP", m.Objects("dbo", TreeGroups.View, 0).Sql);
            Assert.Contains("('SO')", m.Objects("dbo", TreeGroups.Sequence, 0).Sql);
            Assert.Contains("'FN', 'IF', 'TF'", m.Objects("dbo", TreeGroups.Code, 0).Sql);
            Assert.Throws<ArgumentException>(() => m.Objects("dbo", "X", 0));
            var columns = m.Columns("dbo", "T");
            Assert.Contains("is_primary_key = 1", columns.Sql);
            Assert.Contains("'max'", columns.Sql);
            Assert.Equal("T", Param(columns, "name"));
            var search = m.SearchObjects("%ORD%", false, false, 500);
            Assert.StartsWith("SELECT TOP (501) ", search.Sql);
            Assert.Contains("UPPER(o.name) LIKE @pattern ESCAPE '\\'", search.Sql);
            Assert.Contains("AND NOT (s.name IN ('sys'", search.Sql);
            Assert.DoesNotContain("AND NOT", m.SearchSchemas("%A%", true, false, 0).Sql);
            Assert.Contains("UPPER(c.name) LIKE @pattern", m.SearchColumns("%ID%", false, false, 10).Sql);
            Assert.Equal("SELECT @@VERSION", m.ServerVersion().Sql);
            var record = new FakeRecord(new Dictionary<string, object> { { "v", "Microsoft SQL Server 2019 (RTM-CU18) (KB5017593) - 15.0.4261.1 (X64) \n\tDeveloper Edition (64-bit)" } });
            Assert.Equal("Microsoft SQL Server 2019", m.ReadServerVersion(record));
            var column = m.ReadColumn(new FakeRecord(new Dictionary<string, object>
            {
                { "OWNER", "dbo" }, { "TABLE_NAME", "T" }, { "COLUMN_NAME", "ID" }, { "TYPE_LABEL", "nvarchar(50)" }, { "NULLABLE", "N" }, { "COLUMN_ID", 1 }, { "IS_PK", 1 }
            }));
            Assert.Equal("nvarchar(50)", column.TypeLabel);
            Assert.True(column.PrimaryKey);
        }

        [Fact]
        public void SupportsMultipleOpenReaders_PerProvider()
        {
            Assert.True(DbProviders.Oracle.SupportsMultipleOpenReaders);
            Assert.False(DbProviders.Postgres.SupportsMultipleOpenReaders);
            Assert.True(DbProviders.SqlServer.SupportsMultipleOpenReaders);
            Assert.True(DbProviders.Generic.SupportsMultipleOpenReaders);
            var cs = OracleConnectionStore.BuildConnectionString(Profile("mssql"), "pw", 5);
            Assert.True(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs).MultipleActiveResultSets);
        }

        [Fact]
        public void SqlServer_ErrorClassification()
        {
            // SqlException은 직접 만들 수 없어 공급자 판정은 다른 예외 형식으로만 확인한다
            Assert.False(DbProviders.SqlServer.IsCancellation(new InvalidOperationException("x")));
            Assert.False(DbProviders.SqlServer.IsBroken(new InvalidOperationException("x")));
        }

        [Fact]
        public void DroppedFiles_FiltersMissingAndDuplicates()
        {
            var files = SqlFileLogic.DroppedFiles(new[] { @"C:\a.sql", @"C:\dir", @"C:\A.SQL", "", null, @"C:\b.txt" },
                p => p == @"C:\a.sql" || p == @"C:\A.SQL" || p == @"C:\b.txt");
            Assert.Equal(new[] { @"C:\a.sql", @"C:\b.txt" }, files.ToArray());
            Assert.Empty(SqlFileLogic.DroppedFiles(null));
        }

        // ---------- PostgreSQL 데이터 사전 SQL ----------

        private static readonly IMetadataProvider Pg = DbProviders.Postgres.Metadata;

        private static object Param(SqlQuery q, string name)
        {
            return q.Parameters.Single(p => p.Key == name).Value;
        }

        [Fact]
        public void Pg_Schemas_And_CurrentSchema()
        {
            Assert.Equal("SELECT current_schema()", Pg.CurrentSchema().Sql);
            var q = Pg.Schemas(true);
            Assert.Contains("pg_catalog.pg_namespace", q.Sql);
            Assert.Contains("AS \"USERNAME\"", q.Sql);
            Assert.Contains("AS \"ORACLE_MAINTAINED\"", q.Sql);
            Assert.Empty(q.Parameters);
        }

        [Fact]
        public void Pg_GroupCounts_BindsOwner_ReturnsObjectTypeAndCnt()
        {
            var q = Pg.GroupCounts("public");
            Assert.Equal("public", Param(q, "owner"));
            Assert.Contains("\"OBJECT_TYPE\"", q.Sql);
            Assert.Contains("\"CNT\"", q.Sql);
            Assert.Contains("pg_proc", q.Sql);
            Assert.Contains("split_part", q.Sql);
        }

        [Fact]
        public void Pg_Objects_ByGroup_LimitPlusOne()
        {
            var table = Pg.Objects("public", TreeGroups.Table, 10);
            Assert.Contains("('r', 'p', 'f')", table.Sql);
            Assert.Contains("reltuples", table.Sql);
            Assert.EndsWith(" LIMIT :limit", table.Sql);
            Assert.Equal(11, Param(table, "limit"));
            Assert.Equal("public", Param(table, "owner"));

            var view = Pg.Objects("public", TreeGroups.View, 0);
            Assert.Contains("('v', 'm')", view.Sql);
            Assert.DoesNotContain("LIMIT", view.Sql);
            Assert.Contains("('S')", Pg.Objects("public", TreeGroups.Sequence, 0).Sql);
            var code = Pg.Objects("public", TreeGroups.Code, int.MaxValue);
            Assert.Contains("pg_proc", code.Sql);
            Assert.Contains("'PROCEDURE'", code.Sql);
            Assert.Equal(int.MaxValue, Param(code, "limit"));
            Assert.Throws<ArgumentException>(() => Pg.Objects("public", "WHAT", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => Pg.Objects("public", TreeGroups.Table, -1));
        }

        [Fact]
        public void Pg_Columns_FormatType_PkFlag()
        {
            var q = Pg.Columns("public", "orders");
            Assert.Contains("format_type(a.atttypid, a.atttypmod) AS \"TYPE_LABEL\"", q.Sql);
            Assert.Contains("contype = 'p'", q.Sql);
            Assert.Contains("NOT a.attisdropped", q.Sql);
            Assert.Equal("public", Param(q, "owner"));
            Assert.Equal("orders", Param(q, "name"));
        }

        [Fact]
        public void Pg_Search_PatternBind_SystemFilter()
        {
            var schemas = Pg.SearchSchemas("%ORD%", false, false, 500);
            Assert.Equal("%ORD%", Param(schemas, "pattern"));
            Assert.Contains("AND NOT (n.nspname IN ('pg_catalog'", schemas.Sql);
            Assert.Equal(501, Param(schemas, "limit"));
            var all = Pg.SearchSchemas("%ORD%", true, false, 0);
            Assert.DoesNotContain("AND NOT", all.Sql);

            var objects = Pg.SearchObjects("%ORD%", false, false, 500);
            Assert.Contains("UPPER(c.relname) LIKE :pattern ESCAPE '\\'", objects.Sql);
            Assert.Contains("UPPER(p.proname) LIKE :pattern ESCAPE '\\'", objects.Sql);
            Assert.Contains("UNION", objects.Sql);

            var columns = Pg.SearchColumns("%ID%", false, false, 500);
            Assert.Contains("UPPER(a.attname) LIKE :pattern ESCAPE '\\'", columns.Sql);
            Assert.Contains("\"TYPE_LABEL\"", columns.Sql);
        }

        [Fact]
        public void Pg_ServerVersion_Read()
        {
            Assert.Equal("SELECT version()", Pg.ServerVersion().Sql);
            var record = new FakeRecord(new Dictionary<string, object> { { "version", "PostgreSQL 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1) on x86_64-pc-linux-gnu, compiled by gcc" } });
            Assert.Equal("PostgreSQL 16.15 (Ubuntu 16.15-0ubuntu0.24.04.1)", Pg.ReadServerVersion(record));
        }

        [Fact]
        public void Pg_Readers()
        {
            var schema = Pg.ReadSchema(new FakeRecord(new Dictionary<string, object> { { "USERNAME", "pg_catalog" }, { "ORACLE_MAINTAINED", "Y" } }), false);
            Assert.Equal("pg_catalog", schema.Name);
            Assert.True(schema.OracleMaintained);

            var obj = Pg.ReadObject(new FakeRecord(new Dictionary<string, object> { { "OWNER", "public" }, { "OBJECT_NAME", "t" }, { "OBJECT_TYPE", "TABLE" }, { "STATUS", "VALID" }, { "NUM_ROWS", 3549L } }));
            Assert.Equal(3549, obj.NumRows);
            Assert.Null(Pg.ReadObject(new FakeRecord(new Dictionary<string, object> { { "OWNER", "p" }, { "OBJECT_NAME", "t" }, { "OBJECT_TYPE", "VIEW" }, { "STATUS", "VALID" }, { "NUM_ROWS", DBNull.Value } })).NumRows);

            var column = Pg.ReadColumn(new FakeRecord(new Dictionary<string, object>
            {
                { "OWNER", "public" }, { "TABLE_NAME", "t" }, { "COLUMN_NAME", "key_1" }, { "TYPE_LABEL", "character varying(80)" }, { "NULLABLE", "N" }, { "COLUMN_ID", (short)3 }, { "IS_PK", 1 }
            }));
            Assert.Equal("character varying(80)", column.TypeLabel);
            Assert.False(column.Nullable);
            Assert.True(column.PrimaryKey);
            Assert.Equal(3, column.Position);

            var hit = Pg.ReadColumnHit(new FakeRecord(new Dictionary<string, object>
            {
                { "OWNER", "public" }, { "OBJECT_TYPE", "TABLE" }, { "OBJECT_NAME", "t" }, { "COLUMN_NAME", "c" }, { "TYPE_LABEL", "numeric" }
            }));
            Assert.Equal("c", hit.ColumnName);
            Assert.Equal("numeric", hit.ColumnType);
        }

        /// <summary>이름으로 값을 돌려주는 최소 IDataRecord(읽기 함수 시험용). 첫 열은 GetValue(0)으로도 읽힌다.</summary>
        private sealed class FakeRecord : System.Data.IDataRecord
        {
            private readonly Dictionary<string, object> _values;
            private readonly List<string> _names;

            public FakeRecord(Dictionary<string, object> values)
            {
                _values = values;
                _names = values.Keys.ToList();
            }

            public object this[string name] { get { return _values[name]; } }
            public object this[int i] { get { return _values[_names[i]]; } }
            public int FieldCount { get { return _names.Count; } }
            public object GetValue(int i) { return this[i]; }
            public bool IsDBNull(int i) { return this[i] == null || this[i] is DBNull; }
            public string GetName(int i) { return _names[i]; }
            public int GetOrdinal(string name) { return _names.IndexOf(name); }
            public string GetString(int i) { return (string)this[i]; }
            public bool GetBoolean(int i) { throw new NotSupportedException(); }
            public byte GetByte(int i) { throw new NotSupportedException(); }
            public long GetBytes(int i, long fieldOffset, byte[] buffer, int bufferoffset, int length) { throw new NotSupportedException(); }
            public char GetChar(int i) { throw new NotSupportedException(); }
            public long GetChars(int i, long fieldoffset, char[] buffer, int bufferoffset, int length) { throw new NotSupportedException(); }
            public System.Data.IDataReader GetData(int i) { throw new NotSupportedException(); }
            public string GetDataTypeName(int i) { throw new NotSupportedException(); }
            public DateTime GetDateTime(int i) { throw new NotSupportedException(); }
            public decimal GetDecimal(int i) { throw new NotSupportedException(); }
            public double GetDouble(int i) { throw new NotSupportedException(); }
            public Type GetFieldType(int i) { throw new NotSupportedException(); }
            public float GetFloat(int i) { throw new NotSupportedException(); }
            public Guid GetGuid(int i) { throw new NotSupportedException(); }
            public short GetInt16(int i) { throw new NotSupportedException(); }
            public int GetInt32(int i) { throw new NotSupportedException(); }
            public long GetInt64(int i) { throw new NotSupportedException(); }
            public int GetValues(object[] values) { throw new NotSupportedException(); }
        }
    }
}
