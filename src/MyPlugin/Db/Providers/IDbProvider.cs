using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;

namespace MyPlugin
{
    /// <summary>
    /// DBMS 공급자: 접속(연결 문자열·연결 객체·프로세스 설정), 방언, 데이터 사전, 오류 판정을 한 곳에 모은다.
    /// 화면과 세션은 중립 모델(OracleConnectionProfile·SchemaInfo·DbObjectInfo·ColumnInfo·QueryTable …)만 알고, DBMS 고유 코드는 공급자 안에만 둔다.
    /// 새 DBMS는 이 인터페이스를 구현해 <see cref="DbProviders"/>에 등록하면 접속 관리·트리·SQL 실행·GCM 정의서가 그대로 동작한다.
    /// (테이블 정보 F4·테이블 정의서는 <see cref="IMetadataProvider.SupportsTableInfo"/>가 true인 공급자만 — 아직 Oracle뿐.)
    /// </summary>
    internal interface IDbProvider
    {
        /// <summary>저장 값(접속 프로필의 provider): "oracle", "postgres" …. 소문자.</summary>
        string Kind { get; }

        /// <summary>표시 이름: "Oracle", "PostgreSQL".</summary>
        string DisplayName { get; }

        int DefaultPort { get; }

        /// <summary>접속 프로필의 ServiceName 칸 이름: Oracle "서비스명", PostgreSQL "데이터베이스".</summary>
        string ServiceLabel { get; }

        ISqlDialect Dialect { get; }

        IMetadataProvider Metadata { get; }

        /// <summary>공급자별 필수 항목 검사(호스트·포트·서비스/데이터베이스·사용자). 문제가 없으면 빈 목록. label은 "'이름'" 꼴 접두.</summary>
        List<string> Validate(OracleConnectionProfile profile, string label);

        /// <summary>연결 문자열. 값은 공급자 빌더가 따옴표 처리한다. 풀링은 끈다.</summary>
        string BuildConnectionString(OracleConnectionProfile profile, string password, int timeoutSeconds);

        /// <summary>연결 객체(아직 안 열림).</summary>
        DbConnection CreateConnection(string connectionString);

        /// <summary>연결을 열기 전에 한 번 해 둘 프로세스 전역 설정(Oracle: in-band break). 없으면 아무것도 안 함.</summary>
        void ConfigureProcess();

        /// <summary>열린 연결의 서버 버전 글(접속 테스트).</summary>
        string ServerVersionOf(DbConnection connection);

        /// <summary>접속 표시 user@host:port/service.</summary>
        string Address(OracleConnectionProfile profile);

        /// <summary>
        /// 한 연결에서 리더(결과 커서)를 열어 둔 채 다른 명령·커밋·트랜잭션 시작을 할 수 있는지.
        /// Oracle·SQL Server(MARS)·SQLite는 true. PostgreSQL(Npgsql)은 false — "A command is already in progress"가 나므로
        /// 세션이 새 명령 전에 열린 커서를 닫는다([다음 행 가져오기]는 그 결과에서 더 못 한다).
        /// </summary>
        bool SupportsMultipleOpenReaders { get; }

        /// <summary>취소로 끝난 실행의 예외인지(공급자 고유 오류 번호). 모르면 false.</summary>
        bool IsCancellation(Exception ex);

        /// <summary>연결이 끊긴 오류인지(공급자 고유 오류 번호). 모르면 false.</summary>
        bool IsBroken(Exception ex);
    }

    /// <summary>
    /// 데이터 사전 조회(트리·검색·버전). 조회 SQL(<see cref="SqlQuery"/>)과 행 읽기 함수의 쌍으로 두어 <see cref="DbSession.QueryAsync{T}"/>가 중립으로 남는다.
    /// 결과 모델(SchemaInfo·DbObjectInfo·ColumnInfo·SearchHit)은 공급자와 무관하다.
    /// </summary>
    internal interface IMetadataProvider
    {
        /// <summary>
        /// Oracle 11g처럼 "내장 스키마 표시 열(ORACLE_MAINTAINED)이 없을 수 있어" 실패하면 다른 SQL로 다시 묻는 공급자면 true.
        /// false면 hasMaintainedFlag 인자는 무시된다.
        /// </summary>
        bool HasMaintainedFlagFallback { get; }

        /// <summary>테이블 정보(F4)·테이블 정의서(ALL_* 기반 일괄 조회)를 지원하는지.</summary>
        bool SupportsTableInfo { get; }

        /// <summary>현재 스키마 한 값(첫 열).</summary>
        SqlQuery CurrentSchema();

        SqlQuery Schemas(bool hasMaintainedFlag);
        SchemaInfo ReadSchema(IDataRecord record, bool hasMaintainedFlag);

        /// <summary>스키마 하나의 객체 종류별 개수: 열 OBJECT_TYPE, CNT(TreeLoaderLogic.ReadGroupCount로 읽는다).</summary>
        SqlQuery GroupCounts(string owner);

        /// <summary>묶음(TreeGroups)의 객체. limit+1행까지(0은 제한 없음).</summary>
        SqlQuery Objects(string owner, string group, int limit);
        DbObjectInfo ReadObject(IDataRecord record);

        SqlQuery Columns(string owner, string objectName);
        ColumnInfo ReadColumn(IDataRecord record);

        SqlQuery SearchSchemas(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit);
        SqlQuery SearchObjects(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit);
        SearchHit ReadObjectHit(IDataRecord record);
        SqlQuery SearchColumns(string pattern, bool includeSystem, bool hasMaintainedFlag, int limit);
        SearchHit ReadColumnHit(IDataRecord record);

        /// <summary>DBMS 제품·버전(정의서 표지). 읽는 쪽은 첫 행만 쓴다.</summary>
        SqlQuery ServerVersion();
        string ReadServerVersion(IDataRecord record);
    }
}
