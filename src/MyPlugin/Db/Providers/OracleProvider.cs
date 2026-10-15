using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Oracle.ManagedDataAccess.Client;

namespace MyPlugin
{
    /// <summary>
    /// Oracle 공급자. 데이터 사전은 기존 <see cref="OracleMetadata"/>·<see cref="TableSpecQueries"/>에 위임한다(SQL 문자열 테스트가 그대로 산다).
    /// 세션의 Oracle 분기(OracleCommand 설정·ORA 오류 번호)는 아직 <see cref="DbSession"/> 안에 있다(3단계에서 옮긴다).
    /// </summary>
    internal sealed class OracleProvider : IDbProvider
    {
        public const string KindName = "oracle";

        private readonly IMetadataProvider _metadata = new OracleMetadataProvider();

        public string Kind { get { return KindName; } }
        public string DisplayName { get { return "Oracle"; } }
        public int DefaultPort { get { return 1521; } }
        public string ServiceLabel { get { return "서비스명"; } }
        public ISqlDialect Dialect { get { return SqlDialects.Oracle; } }
        public IMetadataProvider Metadata { get { return _metadata; } }

        public List<string> Validate(OracleConnectionProfile profile, string label)
        {
            return ProviderRules.ValidateHostPortServiceUser(profile, label, ServiceLabel);
        }

        public string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds)
        {
            var builder = new OracleConnectionStringBuilder
            {
                DataSource = profile.Host.Trim() + ":" + profile.Port + "/" + profile.ServiceName.Trim(),
                UserID = profile.UserId.Trim(),
                Password = password,
                ConnectionTimeout = timeoutSeconds,
                Pooling = false
            };
            return builder.ConnectionString;
        }

        public DbConnection CreateConnection(string connectionString)
        {
            return new OracleConnection(connectionString);
        }

        /// <summary>첫 연결 전에 취소 방식을 in-band break로(연결을 연 뒤에는 바꿀 수 없다 — ORA-50099).</summary>
        public void ConfigureProcess()
        {
            DbSession.UseInBandBreak();
        }

        public string ServerVersionOf(DbConnection connection)
        {
            return connection.ServerVersion;
        }

        public string Address(OracleConnectionProfile profile)
        {
            return ProviderRules.Address(profile);
        }

        public bool SupportsMultipleOpenReaders { get { return true; } }

        public bool IsCancellation(Exception ex)
        {
            var oracle = ex as OracleException;
            return oracle != null && oracle.Number == 1013;
        }

        /// <summary>끊김 판정은 아직 DbSession의 ORA 번호 표가 맡는다(3단계에서 여기로).</summary>
        public bool IsBroken(Exception ex)
        {
            return false;
        }

        /// <summary>기존 정적 OracleMetadata에 위임.</summary>
        private sealed class OracleMetadataProvider : IMetadataProvider
        {
            public bool HasMaintainedFlagFallback { get { return true; } }
            public bool SupportsTableInfo { get { return true; } }

            public SqlQuery CurrentSchema() { return OracleMetadata.CurrentSchema(); }
            public SqlQuery Schemas(bool hasMaintainedFlag) { return OracleMetadata.Schemas(hasMaintainedFlag); }
            public SchemaInfo ReadSchema(IDataRecord record, bool hasMaintainedFlag) { return OracleMetadata.ReadSchema(record, hasMaintainedFlag); }
            public SqlQuery GroupCounts(string owner) { return OracleMetadata.GroupCounts(owner); }
            public SqlQuery Objects(string owner, string group, int limit) { return OracleMetadata.Objects(owner, group, limit); }
            public DbObjectInfo ReadObject(IDataRecord record) { return OracleMetadata.ReadObject(record); }
            public SqlQuery Columns(string owner, string objectName) { return OracleMetadata.Columns(owner, objectName); }
            public ColumnInfo ReadColumn(IDataRecord record) { return OracleMetadata.ReadColumn(record); }
            public SqlQuery UniqueKeys(string owner, string objectName) { return OracleMetadata.UniqueKeys(owner, objectName); }
            public UniqueKeyColumn ReadUniqueKey(IDataRecord record) { return OracleMetadata.ReadUniqueKey(record); }
            public SqlQuery RowIdTable(string owner, string objectName) { return OracleMetadata.RowIdTable(owner, objectName); }
            public SqlQuery SearchSchemas(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit) { return OracleMetadata.SearchSchemas(pattern, includeSystem, hasMaintainedFlag, limit); }
            public SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit) { return OracleMetadata.SearchObjects(pattern, includeSystem, hasMaintainedFlag, limit); }
            public SearchHit ReadObjectHit(IDataRecord record) { return OracleMetadata.ReadObjectHit(record); }
            public SqlQuery SearchColumns(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit) { return OracleMetadata.SearchColumns(pattern, includeSystem, hasMaintainedFlag, limit); }
            public SearchHit ReadColumnHit(IDataRecord record) { return OracleMetadata.ReadColumnHit(record); }
            public SqlQuery ServerVersion() { return TableSpecQueries.ServerVersion(); }
            public string ReadServerVersion(IDataRecord record) { return TableSpecQueries.ReadServerVersion(record); }
            public SqlQuery Source(string owner, string objectType, string name) { return OracleMetadata.Source(owner, objectType, name); }
            public string BuildSource(string owner, string objectType, string name, IList<SourcePart> parts) { return OracleMetadata.BuildSource(owner, objectType, name, parts); }
        }
    }

    /// <summary>공급자들이 함께 쓰는 검사·표시 규칙.</summary>
    internal static class ProviderRules
    {
        /// <summary>호스트·포트(1~65535)·서비스/데이터베이스·사용자 필수.</summary>
        public static List<string> ValidateHostPortServiceUser(OracleConnectionProfile p, string label, string serviceLabel)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(p.Host))
                errors.Add(label + ": 호스트를 입력하세요.");
            if (p.Port < 1 || p.Port > 65535)
                errors.Add(label + ": 포트는 1~65535여야 합니다.");
            if (string.IsNullOrWhiteSpace(p.ServiceName))
                errors.Add(label + ": " + serviceLabel + "을(를) 입력하세요.");
            if (string.IsNullOrWhiteSpace(p.UserId))
                errors.Add(label + ": 사용자를 입력하세요.");
            return errors;
        }

        /// <summary>user@host:port/service.</summary>
        public static string Address(OracleConnectionProfile profile)
        {
            if (profile == null)
                return "";
            return (profile.UserId ?? "").Trim() + "@" + (profile.Host ?? "").Trim() + ":"
                + profile.Port.ToString(CultureInfo.InvariantCulture) + "/" + (profile.ServiceName ?? "").Trim();
        }
    }

    /// <summary>시험(SQLite)·모르는 연결: 접속 관리에는 보이지 않는다. 데이터 사전은 없다(트리에 쓰면 NotSupportedException).</summary>
    internal sealed class GenericProvider : IDbProvider
    {
        public string Kind { get { return "generic"; } }
        public string DisplayName { get { return "Generic"; } }
        public int DefaultPort { get { return 0; } }
        public string ServiceLabel { get { return "데이터베이스"; } }
        public ISqlDialect Dialect { get { return SqlDialects.Generic; } }
        public IMetadataProvider Metadata { get { throw new NotSupportedException("이 연결에는 데이터 사전 조회가 없습니다."); } }
        public List<string> Validate(OracleConnectionProfile profile, string label) { return new List<string>(); }
        public string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds) { throw new NotSupportedException(); }
        public DbConnection CreateConnection(string connectionString) { throw new NotSupportedException(); }
        public void ConfigureProcess() { }
        public string ServerVersionOf(DbConnection connection) { return connection.ServerVersion; }
        public string Address(OracleConnectionProfile profile) { return ProviderRules.Address(profile); }
        public bool SupportsMultipleOpenReaders { get { return true; } }
        public bool IsCancellation(Exception ex) { return false; }
        public bool IsBroken(Exception ex) { return false; }
    }
}
